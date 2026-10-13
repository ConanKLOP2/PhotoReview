using System.IO;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App;
using PhotoReview.App.Presenters;
using PhotoReview.App.Services;
using PhotoReview.Core;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.IO;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs;
using PhotoReview.Shell.Win32.Dialogs.Native;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// WP-35 GĐ4 - "kiểm trên bản sao journal": cửa sổ Recovery Win32 chạy cả đường Retry/Dismiss/Clear THẬT (RecoveryRetryService +
/// OperationJournal thật) trên một thư mục tạm riêng, qua đúng các nút của view, rồi kiểm tệp nguồn/đích/journal sau MỖI hành động.
/// AN TOÀN DỮ LIỆU: mọi đường dẫn nằm trong thư mục tạm <c>wp35g4-recovery-*</c>; không truyền <c>IRecycleBin</c> (không có Thùng
/// rác, thật hay giả, để chạm vào); mục Recycle trong test chỉ dùng cho Clear (ghi journal) hoặc delegate giả.
/// </summary>
[Collection("OverlayLocalization")]
[Trait("Category", "UI")]
public sealed class RecoveryDialogDataSafetyTests
{
    private static readonly TimeSpan Bound = RecoveryRig.Bound;

    private static Func<JournalEntry, Task<RecoveryRetryResult>> RealRetry(RecoveryRig rig, List<(string Id, bool Confirmed)>? calls = null)
        => entry =>
        {
            // Giống ShowRecovery: cửa sổ tự xác nhận mục bị người dùng huỷ trước, nên delegate truyền confirmedFinishCancelled: true.
            calls?.Add((entry.Id, true));
            return rig.Service.RetryMoveOrCopyAsync(entry, confirmedFinishCancelled: true);
        };

    private static Func<IReadOnlyList<JournalEntry>, DismissOutcome> RealDismiss(RecoveryRig rig)
        => entries => rig.Journal.Dismiss(entries);

    private static List<JournalEntry> Pending(RecoveryRig rig) => [.. rig.Journal.ReadPendingAndFailedOperations()];

    /// <summary>Nạp journal thật bằng các mục cho trước rồi trả về đúng những gì cửa sổ sẽ đọc (như SecondaryWindowFactory).</summary>
    private static List<JournalEntry> Seed(RecoveryRig rig, params JournalEntry[] entries)
    {
        foreach (var entry in entries)
        {
            rig.Journal.Append(entry);
        }

        return Pending(rig);
    }

    // ---- Retry thật -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Retry_Move_Succeeds_MovesTheFile_CommitsTheJournal_AndClosesTheWindow()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var source = rig.Write(@"work\a.jpg", "content-A");
        var destination = Path.Combine(rig.Root, "work", "out", "a.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var entries = Seed(rig, rig.Entry("m1", source, destination));
        var calls = new List<(string, bool)>();
        var d = await rig.OpenAsync(entries, RealRetry(rig, calls), RealDismiss(rig));
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);
        Assert.True(await rig.OnUi(() => d.IsEnabled(RecoveryDialog.RetryId)));

        await rig.ClickAsync(d, RecoveryDialog.RetryId);
        await rig.RetryDoneAsync(d);

        Assert.False(File.Exists(source));
        Assert.Equal("content-A", File.ReadAllText(destination));
        Assert.Empty(Pending(rig)); // the entry is Committed now, nothing is pending or failed any more
        Assert.Equal([("m1", true)], calls);
        Assert.Equal(Tr.DialogRetrySucceededTitle, rig.Messages.Calls[^1].Title);
        Assert.True(await rig.OnUi(() => d.IsClosed));
    }

    [Fact]
    public async Task Retry_Copy_KeepsTheSource_AndCreatesTheDestination()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var source = rig.Write(@"work\c.jpg", "copy-me");
        var destination = Path.Combine(rig.Root, "work", "copies", "c.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var entries = Seed(rig, rig.Entry("c1", source, destination, FileOperationType.Copy));
        var d = await rig.OpenAsync(entries, RealRetry(rig), RealDismiss(rig));
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);

        await rig.ClickAsync(d, RecoveryDialog.RetryId);
        await rig.RetryDoneAsync(d);

        Assert.Equal("copy-me", File.ReadAllText(source));
        Assert.Equal("copy-me", File.ReadAllText(destination));
        Assert.Empty(Pending(rig));
    }

    [Fact]
    public async Task Retry_Declined_ChangesNothing_NeitherFilesNorJournal()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var source = rig.Write(@"work\a.jpg", "content-A");
        var destination = Path.Combine(rig.Root, "work", "out", "a.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var entries = Seed(rig, rig.Entry("m1", source, destination));
        var journalBefore = File.ReadAllText(rig.JournalFile);
        var treeBefore = rig.Tree();
        rig.Messages.Answer = (_, _) => false;
        var d = await rig.OpenAsync(entries, RealRetry(rig), RealDismiss(rig));
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);

        await rig.ClickAsync(d, RecoveryDialog.RetryId);
        await rig.RetryDoneAsync(d);

        Assert.Equal(treeBefore, rig.Tree());
        Assert.Equal(journalBefore, File.ReadAllText(rig.JournalFile));
        Assert.Single(Pending(rig));
    }

    [Fact]
    public async Task Retry_WhenTheSourceChangedAfterTheCheck_IsRefusedByTheService_AndNothingMoves()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var source = rig.Write(@"work\a.jpg", "content-A");
        var destination = Path.Combine(rig.Root, "work", "out", "a.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var entries = Seed(rig, rig.Entry("m1", source, destination));
        var d = await rig.OpenAsync(entries, RealRetry(rig), RealDismiss(rig));
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);
        File.WriteAllText(source, "content-A but edited meanwhile in another program"); // the file changes AFTER the window's check
        var treeBefore = rig.Tree();

        await rig.ClickAsync(d, RecoveryDialog.RetryId);
        await rig.RetryDoneAsync(d);

        Assert.Equal(treeBefore, rig.Tree()); // source untouched, destination not created
        Assert.False(File.Exists(destination));
        var notice = rig.Messages.Calls[^1];
        Assert.Equal(Tr.DialogRetryRejectedTitle, notice.Title);
        Assert.Equal(Tr.CoreRecoverySourceChanged, notice.Message);
        Assert.Single(Pending(rig)); // still failed: the user can investigate or dismiss
        Assert.False(await rig.OnUi(() => d.IsClosed));
    }

    [Fact]
    public async Task Retry_OfAnEntryAnotherWindowAlreadyDismissed_IsSuperseded_AndTouchesNoFile()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var source = rig.Write(@"work\a.jpg", "content-A");
        var destination = Path.Combine(rig.Root, "work", "out", "a.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var entries = Seed(rig, rig.Entry("m1", source, destination));
        var d = await rig.OpenAsync(entries, RealRetry(rig), RealDismiss(rig));
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);
        rig.Journal.Dismiss(entries); // another window / process resolved the entry meanwhile
        var treeBefore = rig.Tree();

        await rig.ClickAsync(d, RecoveryDialog.RetryId);
        await rig.RetryDoneAsync(d);

        Assert.Equal(treeBefore, rig.Tree());
        Assert.Equal(Tr.RecoveryRetryStaleTitle, rig.Messages.Calls[^1].Title);
        await rig.OnUi(() =>
        {
            Assert.Empty(d.VisibleRows); // the row is gone: it no longer describes that Id
            Assert.False(d.IsClosed);
            d.Close();
        });
    }

    [Fact]
    public async Task Retry_OfAnEntryTheUserCancelled_NeedsThePointedConfirmation_AndFinishesItOnlyOnYes()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var source = rig.Write(@"work\a.jpg", "content-A");
        var destination = Path.Combine(rig.Root, "work", "out", "a.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var entry = rig.Entry("cx", source, destination, errorCode: JournalErrors.CancelledByUser, error: null);
        var entries = Seed(rig, entry);
        var asked = new List<string>();
        rig.Messages.Answer = (_, message) =>
        {
            asked.Add(message);
            return asked.Count > 1; // refuse the first time, accept the second
        };
        var d = await rig.OpenAsync(entries, RealRetry(rig), RealDismiss(rig));
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);

        await rig.ClickAsync(d, RecoveryDialog.RetryId);
        await rig.RetryDoneAsync(d);
        Assert.True(File.Exists(source), "declined: nothing finished");
        Assert.False(File.Exists(destination));

        await rig.ClickAsync(d, RecoveryDialog.RetryId);
        await rig.RetryDoneAsync(d);

        var expected = Tr.RecoveryRetryConfirmCancelled(RecoveryPresentation.OperationText(FileOperationType.Move), 1);
        Assert.Equal([expected, expected], asked);
        Assert.False(File.Exists(source));
        Assert.Equal("content-A", File.ReadAllText(destination));
        Assert.Empty(Pending(rig));
    }

    // ---- Clear thật ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ClearSelected_WritesOnlyJournalLines_AcrossFailedPreparedAndDeleteFailedEntries()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var a = rig.Write(@"work\a.jpg", "A");
        var b = rig.Write(@"work\b.jpg", "BB");
        var c = rig.Write(@"work\c.jpg", "CCC"); // a Delete that failed: the file is still here
        var entries = Seed(rig,
            rig.Entry("failed", a, Path.Combine(rig.Root, "work", "out", "a.jpg")),
            rig.Entry("prepared", b, Path.Combine(rig.Root, "work", "out", "b.jpg"), state: JournalState.Prepared, error: null),
            rig.Entry("delete", c, null, FileOperationType.Recycle));
        var treeBefore = rig.Tree();
        var d = await rig.OpenAsync(entries, RealRetry(rig), RealDismiss(rig));
        await rig.CheckDoneAsync(d);
        // The journal lists Prepared entries first, then Failed ones: [prepared, failed, delete].
        Assert.Equal(["prepared", "failed", "delete"], entries.Select(e => e.Id).ToArray());
        await rig.PickAsync(d, 1, 2); // failed + delete

        await rig.ClickAsync(d, RecoveryDialog.ClearSelectedId);

        Assert.Equal(treeBefore, rig.Tree()); // not a single user file was created, moved or deleted
        var left = Pending(rig);
        Assert.Equal("prepared", Assert.Single(left).Id);
        await rig.OnUi(() =>
        {
            Assert.Equal(["prepared"], d.VisibleRows.Select(r => r.Entry.Id).ToArray());
            d.Close();
        });
    }

    [Fact]
    public async Task ClearAll_DismissesEverything_AndLeavesEveryFileExactlyAsItWas()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var a = rig.Write(@"work\a.jpg", "A");
        var done = rig.Write(@"work\out\done.jpg", "DONE"); // AlreadyDone: destination present, source gone
        var entries = Seed(rig,
            rig.Entry("one", a, Path.Combine(rig.Root, "work", "out", "a.jpg")),
            rig.Entry("two", Path.Combine(rig.Root, "work", "done.jpg"), done),
            rig.Entry("three", Path.Combine(rig.Root, "work", "gone.jpg"), Path.Combine(rig.Root, "work", "out", "gone.jpg"), size: 7));
        var treeBefore = rig.Tree();
        var d = await rig.OpenAsync(entries, RealRetry(rig), RealDismiss(rig));
        await rig.CheckDoneAsync(d);

        await rig.ClickAsync(d, RecoveryDialog.ClearAllId);

        Assert.Equal(treeBefore, rig.Tree());
        Assert.Empty(Pending(rig));
        Assert.Equal(Tr.RecoveryDismissConfirmMessage(3), rig.Messages.Calls[0].Message);
    }

    [Fact]
    public async Task ClearAll_Declined_LeavesTheJournalAndTheFilesAlone()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var a = rig.Write(@"work\a.jpg", "A");
        var entries = Seed(rig, rig.Entry("one", a, Path.Combine(rig.Root, "work", "out", "a.jpg")));
        var journalBefore = File.ReadAllText(rig.JournalFile);
        var treeBefore = rig.Tree();
        rig.Messages.Answer = (_, _) => false;
        var d = await rig.OpenAsync(entries, RealRetry(rig), RealDismiss(rig));
        await rig.CheckDoneAsync(d);

        await rig.ClickAsync(d, RecoveryDialog.ClearAllId);

        Assert.Equal(treeBefore, rig.Tree());
        Assert.Equal(journalBefore, File.ReadAllText(rig.JournalFile));
        Assert.Equal(1, await rig.OnUi(() => d.VisibleRows.Count));
    }

    [Fact]
    public async Task Clear_OfAnEntryResolvedElsewhere_IsReportedAsStale_NotClaimedAsCleared()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var a = rig.Write(@"work\a.jpg", "A");
        var b = rig.Write(@"work\b.jpg", "BB");
        var entries = Seed(rig,
            rig.Entry("one", a, Path.Combine(rig.Root, "work", "out", "a.jpg")),
            rig.Entry("two", b, Path.Combine(rig.Root, "work", "out", "b.jpg")));
        var d = await rig.OpenAsync(entries, RealRetry(rig), RealDismiss(rig));
        await rig.CheckDoneAsync(d);
        rig.Journal.Dismiss([entries[0]]); // another window already dismissed the first one
        await rig.PickAsync(d, 0, 1);

        await rig.ClickAsync(d, RecoveryDialog.ClearSelectedId);

        var notice = rig.Messages.Calls[^1];
        Assert.Equal(Tr.RecoveryDismissStaleTitle, notice.Title);
        Assert.Equal(Tr.RecoveryDismissStaleMessage(1), notice.Message);
        Assert.Empty(Pending(rig));
        Assert.Empty(await rig.OnUi(() => d.VisibleRows.ToArray()));
    }

    // ---- hộp xác nhận xoá vĩnh viễn (nhóm Delete) ----------------------------------------------------------------------------------

    [Fact]
    public async Task GroupDeleteRetry_WarnsAboutPermanentDeletion_UsingTheSettingReadWhenAsked()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var a = rig.Write(@"cap\g.jpg", "1");
        var b = rig.Write(@"cap\g.raw", "22");
        var members = new List<JournalGroupMember>
        {
            new(a, null, 1, new FileInfo(a).LastWriteTimeUtc, Permanent: true),
            new(b, null, 2, new FileInfo(b).LastWriteTimeUtc, Permanent: false),
        };
        var group = rig.Entry("del", a, null, FileOperationType.Recycle, members: members);
        var allow = false;
        var retried = 0;
        rig.Messages.Answer = (_, _) => false; // never actually run a Delete here
        var d = await rig.OpenAsync([group], retry: _ =>
        {
            retried++;
            return Task.FromResult(new RecoveryRetryResult(Succeeded: true, Message: "x", Entry: null));
        }, dismiss: _ => new DismissOutcome([], []), allowPermanentDelete: () => allow);
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);
        Assert.True(await rig.OnUi(() => d.IsEnabled(RecoveryDialog.RetryId)), "a Delete group still on disk is retryable");
        var treeBefore = rig.Tree();

        await rig.ClickAsync(d, RecoveryDialog.RetryId); // setting off
        allow = true; // the user flipped the setting while the window stays open
        await rig.ClickAsync(d, RecoveryDialog.RetryId);

        var operation = RecoveryPresentation.OperationText(FileOperationType.Recycle);
        Assert.Equal([Tr.RecoveryGroupRetryConfirm(operation, 2), Tr.RecoveryGroupRetryConfirmPermanent(operation, 2, 1)],
            rig.Messages.Calls.Select(call => call.Message).ToArray());
        Assert.Equal(0, retried); // declined both times
        Assert.Equal(treeBefore, rig.Tree());
    }

    // ---- cổng Win32: nguồn dữ liệu giống bản WPF ------------------------------------------------------------------------------------

    private static ServiceProvider Services(RecoveryRig rig, bool withJournal = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ReviewMetrics());
        if (withJournal)
        {
            services.AddSingleton(rig.Journal);
            services.AddSingleton(rig.Service);
            services.AddSingleton<IFileSystem>(new PhysicalFileSystem());
            var paths = new AppPaths(rig.Root);
            services.AddSingleton(new SettingsStore(paths, new PhysicalFileSystem(), NullLog.Instance));
        }

        return services.BuildServiceProvider();
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public List<string> Texts { get; } = [];

        public bool TrySetText(string text)
        {
            Texts.Add(text);
            return true;
        }
    }

    private sealed class Warnings : IDialogWarningSink
    {
        public List<(string Title, string Message)> Shown { get; } = [];

        public void ShowWarning(nint owner, string title, string message) => Shown.Add((title, message));
    }

    [Fact]
    public async Task Port_ShowRecovery_OpensTheJournalsEntries_OwnedAndModal_AndClosesCleanly()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var source = rig.Write(@"work\a.jpg", "content-A");
        Seed(rig, rig.Entry("m1", source, Path.Combine(rig.Root, "work", "out", "a.jpg")));
        var treeBefore = rig.Tree();

        var (rows, ownerEnabled, owned, title) = await rig.OnUi(() =>
        {
            using var provider = Services(rig);
            var observed = (Rows: -1, OwnerEnabled: true, Owned: false, Title: string.Empty);
            var port = new Win32SecondaryWindowPort(provider, null, d =>
            {
                var dialog = Assert.IsType<RecoveryDialog>(d);
                observed = (dialog.Presenter.Rows.Count, User32.IsWindowEnabled(rig.Owner),
                    User32.GetWindow(d.Hwnd, 4 /* GW_OWNER */) == rig.Owner, TextOf(d.Hwnd));
                d.Close();
            }, loop: rig.Ui.Loop, recovery: new RecoveryDialogSeams(rig.Messages, AutoCheck: false));
            port.ShowRecovery(rig.Owner);
            return observed;
        });

        Assert.Equal(1, rows);
        Assert.False(ownerEnabled);
        Assert.True(owned);
        Assert.Equal(Tr.RecoveryTitle, title);
        Assert.Equal(treeBefore, rig.Tree());
    }

    [Fact]
    public async Task Port_ShowRecovery_WithoutAJournal_OpensNothing()
    {
        await using var rig = await RecoveryRig.StartAsync();

        var opened = await rig.OnUi(() =>
        {
            using var provider = Services(rig, withJournal: false);
            var count = 0;
            var port = new Win32SecondaryWindowPort(provider, null, d =>
            {
                count++;
                d.Close();
            });
            port.ShowRecovery(rig.Owner);
            return count;
        });

        Assert.Equal(0, opened);
    }

    [Fact]
    public async Task Port_ShowRecovery_WithAnUnreadableJournal_TellsTheUser_AndOpensNothing()
    {
        await using var rig = await RecoveryRig.StartAsync();
        // The journal is held open exclusively by someone else: reading it throws IOException (RV-A14).
        Seed(rig, rig.Entry("m1", rig.Write(@"work\a.jpg"), Path.Combine(rig.Root, "work", "out", "a.jpg")));
        using var holder = new FileStream(rig.JournalFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var warnings = new Warnings();

        var opened = await rig.OnUi(() =>
        {
            using var provider = Services(rig);
            var count = 0;
            var port = new Win32SecondaryWindowPort(provider, null, d =>
            {
                count++;
                d.Close();
            }, warnings: warnings);
            port.ShowRecovery(rig.Owner);
            return count;
        });

        Assert.Equal(0, opened);
        var shown = Assert.Single(warnings.Shown);
        Assert.Equal(Tr.RecoveryTitle, shown.Title);
        Assert.StartsWith(Tr.RecoveryReadFailed("|").Split('|')[0], shown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingTheJournal_ReportsIoAndAccessErrors_ThroughTheCallback()
    {
        var shown = new List<(string Title, string Message)>();

        var io = Win32SecondaryWindowPort.TryReadRecoveryEntries(() => throw new IOException("locked"),
            (title, message) => shown.Add((title, message)));
        var access = Win32SecondaryWindowPort.TryReadRecoveryEntries(() => throw new UnauthorizedAccessException("denied"),
            (title, message) => shown.Add((title, message)));
        var ok = Win32SecondaryWindowPort.TryReadRecoveryEntries(() => [], (_, _) => throw new InvalidOperationException("no error expected"));

        Assert.Null(io);
        Assert.Null(access);
        Assert.NotNull(ok);
        Assert.Equal([(Tr.RecoveryTitle, Tr.RecoveryReadFailed("locked")), (Tr.RecoveryTitle, Tr.RecoveryReadFailed("denied"))], shown);
    }

    [Fact]
    public async Task Port_Dispose_ClosesAnOpenRecovery_AndLaterCallsDoNothing()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var source = rig.Write(@"work\a.jpg", "content-A");
        Seed(rig, rig.Entry("m1", source, Path.Combine(rig.Root, "work", "out", "a.jpg")));

        var (closed, later) = await rig.OnUi(() =>
        {
            using var provider = Services(rig);
            Win32SecondaryWindowPort? port = null;
            var wasClosed = false;
            var opened = 0;
            port = new Win32SecondaryWindowPort(provider, null, d =>
            {
                opened++;
                port!.Dispose(); // as ShellStartup does when the shell closes with Recovery open
                wasClosed = d.IsClosed;
            }, loop: rig.Ui.Loop, recovery: new RecoveryDialogSeams(rig.Messages, AutoCheck: false));
            port.ShowRecovery(rig.Owner);
            var afterFirst = opened;
            port.ShowRecovery(rig.Owner); // disposed: no window, no throw
            return (wasClosed, opened - afterFirst);
        });

        Assert.True(closed);
        Assert.Equal(0, later);
    }

    [Fact]
    public async Task Port_Copy_UsesTheShellsClipboardService()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var source = rig.Write(@"work\a.jpg", "content-A");
        Seed(rig, rig.Entry("m1", source, Path.Combine(rig.Root, "work", "out", "a.jpg")));
        var clipboard = new FakeClipboard();

        await rig.OnUi(() =>
        {
            var services = new ServiceCollection();
            services.AddSingleton(rig.Journal);
            services.AddSingleton<IClipboardService>(clipboard);
            using var provider = services.BuildServiceProvider();
            var port = new Win32SecondaryWindowPort(provider, null, d =>
            {
                var dialog = (RecoveryDialog)d;
                dialog.SelectRows(0);
                Command(d, RecoveryDialog.CopySourceId);
                d.Close();
            }, loop: rig.Ui.Loop, recovery: new RecoveryDialogSeams(Messages: rig.Messages, AutoCheck: false));
            port.ShowRecovery(rig.Owner);
        });

        Assert.Equal([source], clipboard.Texts);
    }
}
