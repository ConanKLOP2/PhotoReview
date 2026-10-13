using System.ComponentModel;
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
/// WP-35 GĐ4 - test lấp các đột biến SỐNG sau đợt đột biến thủ công của cửa sổ Recovery Win32 (view + phần Recovery của cổng):
/// cuộn danh sách giữ nguyên, màu trạng thái đích theo tone của nó, hộp xác nhận bị đóng bằng X không phải là "Yes", Clear khi không
/// chọn gì, Retry đúng hàng chọn, con trỏ chờ đặt NGAY, danh sách lọc lại khi kiểm tra xong, chi tiết cập nhật giữa chừng, focus
/// chuyển khi nút bị khoá/bộ lọc rỗng, Explorer lỗi InvalidOperation, đường dẫn rỗng, cửa sổ đóng giữa lúc hỏi/thông báo, và đường
/// Retry/Clear/xoá vĩnh viễn của CỔNG với dịch vụ thật trên thư mục tạm. AN TOÀN DỮ LIỆU: thư mục tạm riêng, không IRecycleBin,
/// hộp thoại/clipboard/Explorer giả.
/// </summary>
[Collection("OverlayLocalization")]
[Trait("Category", "UI")]
public sealed class RecoveryDialogGapTests
{
    private static RecoveryRetryResult Succeeded() => new(Succeeded: true, Message: "retried", Entry: null);

    private static RecoveryRetryResult Rejected() => new(Succeeded: false, Message: "refused: changed", Entry: null);

    private static DismissOutcome Dismissed(IReadOnlyList<JournalEntry> entries) => new(entries, []);

    /// <summary>Ba mục: CanRetry (e1), AlreadyDone (e2: đích có, nguồn mất), Lost (e3).</summary>
    private static List<JournalEntry> ThreeEntries(RecoveryRig rig)
    {
        var retry = rig.Write(@"src\retry-me.jpg", "aaaa");
        var done = rig.Write(@"dst\already.jpg", "bbbbbb");
        var lost = Path.Combine(rig.Root, "src", "lost.jpg");
        return
        [
            rig.Entry("e1", retry, Path.Combine(rig.Root, "dst", "retry-me.jpg")),
            rig.Entry("e2", Path.Combine(rig.Root, "src", "already.jpg"), done),
            rig.Entry("e3", lost, Path.Combine(rig.Root, "dst", "lost.jpg"), size: 5),
        ];
    }

    private static string Text(RecoveryDialog d, int id) => TextOf(d, id);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetCursor();

    // ---- danh sách -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Refresh_KeepsTheListScrolledWhereTheUserLeftIt()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var entries = Enumerable.Range(0, 24)
            .Select(i => rig.Entry("t" + i, Path.Combine(rig.Root, "src", $"f{i:00}.jpg"), Path.Combine(rig.Root, "dst", $"f{i:00}.jpg")))
            .ToList();
        var d = await rig.OpenAsync(entries, autoCheck: false);
        var scrolled = await rig.OnUi(() =>
        {
            var list = Item(d, RecoveryDialog.ListId);
            User32.SendMessage(list, ControlConstants.LbSetTopIndex, 9, 0);
            return (int)User32.SendMessage(list, ControlConstants.LbGetTopIndex, 0, 0);
        });
        Assert.True(scrolled > 0, "the list must be scrollable for this test to mean anything");

        await rig.FilterAsync(d, 0); // same filter again = rebuild of the list

        var after = await rig.OnUi(() => (int)User32.SendMessage(Item(d, RecoveryDialog.ListId), ControlConstants.LbGetTopIndex, 0, 0));
        await rig.OnUi(() => d.Close());
        Assert.Equal(scrolled, after);
    }

    [Fact]
    public async Task WhenTheCheckCompletes_TheListIsFilteredAgainByTheNewVerdicts()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var d = await rig.OpenAsync(ThreeEntries(rig), autoCheck: false);
        await rig.FilterAsync(d, 1); // "Can retry": unchecked rows have no verdict yet

        await rig.OnUi(() => d.StartChecks());
        await rig.CheckDoneAsync(d);

        await rig.OnUi(() =>
        {
            Assert.Equal(["e1"], d.VisibleRows.Select(r => r.Entry.Id).ToArray());
            Assert.False(IsVisible(d, RecoveryDialog.FilterEmptyId));
            Assert.True(IsVisible(d, RecoveryDialog.ListId));
            d.Close();
        });
    }

    [Fact]
    public async Task TheSelectedRowsDetails_UpdateAsSoonAsItsResultArrives_NotOnlyWhenTheWholeCheckEnds()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var entries = ThreeEntries(rig);
        using var reached = new ManualResetEventSlim(false);
        using var gate = new ManualResetEventSlim(false);
        var d = await rig.OpenAsync(entries, autoCheck: false, fileSystem: new HookFileSystem(new PhysicalFileSystem(), path =>
        {
            if (path.EndsWith("already.jpg", StringComparison.OrdinalIgnoreCase))
            {
                reached.Set();
                gate.Wait(RecoveryRig.Bound);
            }
        }));
        await rig.PickAsync(d, 0);
        await rig.OnUi(() => d.StartChecks());
        Assert.True(reached.Wait(RecoveryRig.Bound), "the check never reached the second entry");

        var seen = string.Empty;
        for (var i = 0; i < 50 && seen != RecoveryPresentation.VerdictText(RecoveryVerdict.CanRetry); i++)
        {
            await rig.OnUi(() => { }); // let the posted result of e1 reach the UI thread
            seen = await rig.OnUi(() => Text(d, RecoveryDialog.VerdictBadgeId));
        }

        gate.Set();
        await rig.CheckDoneAsync(d);
        await rig.OnUi(() => d.Close());
        Assert.Equal(RecoveryPresentation.VerdictText(RecoveryVerdict.CanRetry), seen);
    }

    [Fact]
    public async Task DestinationStatus_IsColouredByTheDestinationsOwnTone_NotTheSources()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var d = await rig.OpenAsync(ThreeEntries(rig));
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 1); // already done: source missing, destination there

        await rig.OnUi(() =>
        {
            var details = RecoveryWindowPresenter.Details(d.Presenter.Rows[1]);
            var source = details.Source!.StatusTone;
            var destination = details.Destination!.StatusTone;
            Assert.NotEqual(source, destination);
            Assert.Equal(RecoveryDialog.ToColorRef(source), d.TextColorOf(RecoveryDialog.SourceStatusId));
            Assert.Equal(RecoveryDialog.ToColorRef(destination), d.TextColorOf(RecoveryDialog.DestinationStatusId));
            d.Close();
        });
    }

    [Fact]
    public void ActionLine_IsLeftOutWhenTheVerdictHasNoActionText()
    {
        var details = new RecoveryDetails(string.Empty, false, "verdict", RecoveryTone.Neutral, "explanation", string.Empty,
            null, false, null, false, false, null, null);

        var text = RecoveryDialog.BuildDetailsText(details);

        Assert.Equal("explanation", text);
    }

    // ---- xác nhận --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AConfirmationClosedWithoutAnswering_IsNotAYes_ForRetryOrClear()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var retried = 0;
        var cleared = 0;
        rig.Messages.ConfirmationResult = NativeDialog.IdCancel; // the TaskDialog closed with X / Esc
        var d = await rig.OpenAsync(ThreeEntries(rig), retry: _ =>
        {
            retried++;
            return Task.FromResult(Succeeded());
        }, dismiss: entries =>
        {
            cleared++;
            return Dismissed(entries);
        });
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);

        await rig.ClickAsync(d, RecoveryDialog.RetryId);
        await rig.ClickAsync(d, RecoveryDialog.ClearSelectedId);
        await rig.ClickAsync(d, RecoveryDialog.ClearAllId);

        Assert.Equal(0, retried);
        Assert.Equal(0, cleared);
        Assert.Equal(3, rig.Messages.Calls.Count(call => call.Kind == TaskDialogKind.Confirmation));
        Assert.Equal(3, await rig.OnUi(() => d.VisibleRows.Count));
        await rig.OnUi(() => d.Close());
    }

    [Fact]
    public async Task ClearSelected_WithNothingSelected_AsksNothing_AndDismissesNothing()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var cleared = 0;
        var d = await rig.OpenAsync(ThreeEntries(rig), dismiss: entries =>
        {
            cleared++;
            return Dismissed(entries);
        });
        await rig.CheckDoneAsync(d);

        await rig.ClickAsync(d, RecoveryDialog.ClearSelectedId);

        Assert.Equal(0, cleared);
        Assert.Empty(rig.Messages.Calls);
        Assert.Equal(3, await rig.OnUi(() => d.VisibleRows.Count));
        await rig.OnUi(() => d.Close());
    }

    [Fact]
    public async Task Retry_RunsTheSelectedRow_NotTheFirstOne()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var a = rig.Write(@"src\a.jpg", "aaaa");
        var b = rig.Write(@"src\b.jpg", "bbbb");
        var entries = new List<JournalEntry>
        {
            rig.Entry("r1", a, Path.Combine(rig.Root, "dst", "a.jpg")),
            rig.Entry("r2", b, Path.Combine(rig.Root, "dst", "b.jpg")),
        };
        var seen = new List<string>();
        var d = await rig.OpenAsync(entries, retry: entry =>
        {
            seen.Add(entry.Id);
            return Task.FromResult(Rejected());
        });
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 1);

        await rig.ClickAsync(d, RecoveryDialog.RetryId);
        await rig.RetryDoneAsync(d);
        await rig.CheckDoneAsync(d);

        Assert.Equal(["r2"], seen);
        var question = rig.Messages.Calls.First(call => call.Kind == TaskDialogKind.Confirmation);
        Assert.Equal(Tr.DialogConfirmRetryMessage(RecoveryPresentation.OperationText(FileOperationType.Move), "b.jpg"), question.Message);
        await rig.OnUi(() => d.Close());
    }

    [Fact]
    public async Task ClearSelected_WhenTheWindowCloseDuringTheQuestion_StaysQuietAboutStaleEntries()
    {
        await using var rig = await RecoveryRig.StartAsync();
        RecoveryDialog? dialog = null;
        rig.Messages.OnShow = message =>
        {
            if (message.Kind == TaskDialogKind.Confirmation)
            {
                dialog!.Close(); // shell exit while the question is up
            }
        };
        var d = await rig.OpenAsync(ThreeEntries(rig), dismiss: entries => new DismissOutcome([], entries));
        dialog = d;
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);

        await rig.ClickAsync(d, RecoveryDialog.ClearSelectedId);

        var only = Assert.Single(rig.Messages.Calls);
        Assert.Equal(TaskDialogKind.Confirmation, only.Kind);
        Assert.True(await rig.OnUi(() => d.IsClosed));
    }

    [Fact]
    public async Task Retry_WhenTheWindowCloseWhileTheNoticeIsUp_DoesNotCheckAgainOnADeadWindow()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var stats = 0;
        RecoveryDialog? dialog = null;
        rig.Messages.OnShow = message =>
        {
            if (message.Kind != TaskDialogKind.Confirmation)
            {
                dialog!.Close(); // the rejected-retry notice is up: shell exit
            }
        };
        var d = await rig.OpenAsync(ThreeEntries(rig), retry: _ => Task.FromResult(Rejected()),
            fileSystem: new HookFileSystem(new PhysicalFileSystem(), _ => Interlocked.Increment(ref stats)));
        dialog = d;
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);
        var before = Volatile.Read(ref stats);

        await rig.ClickAsync(d, RecoveryDialog.RetryId);
        await rig.RetryDoneAsync(d);
        await rig.CheckDoneAsync(d);

        Assert.True(await rig.OnUi(() => d.IsClosed));
        Assert.Equal(before, Volatile.Read(ref stats));
    }

    // ---- con trỏ, focus ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheWaitCursorIsSetTheMomentTheRetryStarts_AndTheArrowTheMomentItEnds()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var gate = new TaskCompletionSource<RecoveryRetryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var d = await rig.OpenAsync(ThreeEntries(rig), retry: _ => gate.Task);
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);
        var arrow = await rig.OnUi(() => User32.LoadCursor(0, 32512));
        var wait = await rig.OnUi(() => User32.LoadCursor(0, 32514));
        await rig.OnUi(() => User32.SetCursor(arrow));

        await rig.ClickAsync(d, RecoveryDialog.RetryId);
        var during = await rig.OnUi(GetCursor); // no WM_SETCURSOR in between: the view sets it itself
        gate.SetResult(Rejected());
        await rig.RetryDoneAsync(d);
        var after = await rig.OnUi(GetCursor);

        Assert.Equal(wait, during);
        Assert.Equal(arrow, after);
        await rig.CheckDoneAsync(d);
        await rig.OnUi(() => d.Close());
    }

    [Fact]
    public async Task WhenTheFocusedButtonGetsLocked_TheFocusMovesToTheList()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var d = await rig.OpenAsync(ThreeEntries(rig), retry: _ => Task.FromResult(Succeeded()), dismiss: Dismissed);
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);
        var onRetry = await rig.OnUi(() =>
        {
            User32.SetFocus(Item(d, RecoveryDialog.RetryId));
            return User32.GetFocus() == Item(d, RecoveryDialog.RetryId);
        });
        Assert.True(onRetry, "the focus could not be put on the Retry button in this session");

        await rig.PickAsync(d, 1); // already done: Retry gets disabled

        await rig.OnUi(() =>
        {
            Assert.False(d.IsEnabled(RecoveryDialog.RetryId));
            Assert.Equal(Item(d, RecoveryDialog.ListId), User32.GetFocus());
            d.Close();
        });
    }

    [Fact]
    public async Task WhenTheFilterEmptiesTheList_TheFocusMovesToTheFilter()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var a = rig.Write(@"src\a.jpg", "aaaa");
        var d = await rig.OpenAsync([rig.Entry("r1", a, Path.Combine(rig.Root, "dst", "a.jpg"))]);
        await rig.CheckDoneAsync(d);
        var onList = await rig.OnUi(() =>
        {
            User32.SetFocus(Item(d, RecoveryDialog.ListId));
            return User32.GetFocus() == Item(d, RecoveryDialog.ListId);
        });
        Assert.True(onList, "the focus could not be put on the list in this session");

        await rig.FilterAsync(d, 3); // "Problems": nothing matches

        await rig.OnUi(() =>
        {
            Assert.True(IsVisible(d, RecoveryDialog.FilterEmptyId));
            Assert.Equal(Item(d, RecoveryDialog.FilterId), User32.GetFocus());
            d.Close();
        });
    }

    // ---- Explorer / Copy ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ShowInExplorer_WhenExplorerReportsAnInvalidOperation_ShowsTheMessage_AndKeepsTheWindow()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var entries = ThreeEntries(rig);
        rig.ExplorerError = new InvalidOperationException("no shell verb");
        var d = await rig.OpenAsync([entries[0]]);
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);

        await rig.ClickAsync(d, RecoveryDialog.ShowSourceId);

        var call = Assert.Single(rig.Messages.Calls);
        Assert.Equal(Tr.RecoveryExplorerFailedTitle, call.Title);
        Assert.Contains("no shell verb", call.Message, StringComparison.Ordinal);
        Assert.False(await rig.OnUi(() => d.IsClosed));
        await rig.OnUi(() => d.Close());
    }

    [Fact]
    public async Task CopyAndShowInExplorer_WithNoPathShown_DoNothing()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var d = await rig.OpenAsync(ThreeEntries(rig));
        await rig.CheckDoneAsync(d); // nothing selected: no path panel

        await rig.ClickAsync(d, RecoveryDialog.CopySourceId);
        await rig.ClickAsync(d, RecoveryDialog.CopyDestinationId);
        await rig.ClickAsync(d, RecoveryDialog.ShowSourceId);
        await rig.ClickAsync(d, RecoveryDialog.ShowDestinationId);

        Assert.Empty(rig.Copied);
        Assert.Empty(rig.ExplorerArgs);
        Assert.Empty(rig.Messages.Calls);
        await rig.OnUi(() => d.Close());
    }

    // ---- cổng (dịch vụ thật trên thư mục tạm) -----------------------------------------------------------------------------------------

    private static List<JournalEntry> Seed(RecoveryRig rig, params JournalEntry[] entries)
    {
        foreach (var entry in entries)
        {
            rig.Journal.Append(entry);
        }

        return [.. rig.Journal.ReadPendingAndFailedOperations()];
    }

    private static ServiceProvider Services(RecoveryRig rig, SettingsStore? settings = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ReviewMetrics());
        services.AddSingleton(rig.Journal);
        services.AddSingleton(rig.Service);
        services.AddSingleton<IFileSystem>(new PhysicalFileSystem());
        services.AddSingleton(settings ?? new SettingsStore(new AppPaths(rig.Root), new PhysicalFileSystem(), NullLog.Instance));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Port_Retry_ConfirmsAUserCancelledEntryExplicitly_SoTheServiceFinishesIt()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var source = rig.Write(@"work\a.jpg", "content-A");
        var destination = Path.Combine(rig.Root, "work", "out", "a.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Seed(rig, rig.Entry("cx", source, destination, errorCode: JournalErrors.CancelledByUser, error: null));

        await rig.OnUi(() =>
        {
            using var provider = Services(rig);
            var port = new Win32SecondaryWindowPort(provider, null, d =>
            {
                var dialog = (RecoveryDialog)d;
                dialog.SelectRows(0);
                Command(d, RecoveryDialog.RetryId);
                // A refused retry keeps the window open: close once the retry task is over, whatever its outcome.
                _ = dialog.LastRetryTask!.ContinueWith(_ => rig.Ui.InvokeAsync(() => d.Close()), TaskScheduler.Default);
            }, loop: rig.Ui.Loop, recovery: new RecoveryDialogSeams(rig.Messages, AutoCheck: false));
            port.ShowRecovery(rig.Owner);
        });

        Assert.False(File.Exists(source));
        Assert.Equal("content-A", File.ReadAllText(destination));
        Assert.Empty(rig.Journal.ReadPendingAndFailedOperations());
    }

    [Fact]
    public async Task Port_Clear_WritesTheDismissalToTheRealJournal()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var source = rig.Write(@"work\a.jpg", "content-A");
        var treeBefore = rig.Tree();
        Seed(rig, rig.Entry("m1", source, Path.Combine(rig.Root, "work", "out", "a.jpg")));
        Assert.Single(rig.Journal.ReadPendingAndFailedOperations());

        await rig.OnUi(() =>
        {
            using var provider = Services(rig);
            var port = new Win32SecondaryWindowPort(provider, null, d =>
            {
                var dialog = (RecoveryDialog)d;
                dialog.SelectRows(0);
                Command(d, RecoveryDialog.ClearSelectedId);
                d.Close();
            }, loop: rig.Ui.Loop, recovery: new RecoveryDialogSeams(rig.Messages, AutoCheck: false));
            port.ShowRecovery(rig.Owner);
        });

        Assert.Empty(rig.Journal.ReadPendingAndFailedOperations());
        Assert.Equal(treeBefore, rig.Tree());
    }

    [Fact]
    public async Task Port_RetryQuestion_WarnsAboutPermanentDeletion_OnlyWhenTheSettingAllowsIt()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var a = rig.Write(@"work\a.jpg", "A");
        var b = rig.Write(@"work\b.jpg", "B");
        var members = new List<JournalGroupMember>
        {
            new(a, null, 1, new FileInfo(a).LastWriteTimeUtc, Permanent: true),
            new(b, null, 1, new FileInfo(b).LastWriteTimeUtc, Permanent: true),
        };
        Seed(rig, rig.Entry("grp", a, null, FileOperationType.Recycle, members: members));
        rig.Messages.Answer = (_, _) => false; // only the question matters
        var settings = new SettingsStore(new AppPaths(rig.Root), new PhysicalFileSystem(), NullLog.Instance);

        async Task<string> AskAsync()
        {
            var before = rig.Messages.Calls.Count;
            await rig.OnUi(() =>
            {
                using var provider = Services(rig, settings);
                var port = new Win32SecondaryWindowPort(provider, null, d =>
                {
                    var dialog = (RecoveryDialog)d;
                    dialog.SelectRows(0);
                    Command(d, RecoveryDialog.RetryId);
                    d.Close();
                }, loop: rig.Ui.Loop, recovery: new RecoveryDialogSeams(rig.Messages, AutoCheck: false));
                port.ShowRecovery(rig.Owner);
            });
            return rig.Messages.Calls.Skip(before).Single(call => call.Kind == TaskDialogKind.Confirmation).Message;
        }

        var recycle = RecoveryPresentation.OperationText(FileOperationType.Recycle);
        var plain = await AskAsync();
        settings.Save(new AppSettings { AllowPermanentDeleteWithoutRecycleBin = true });
        var permanent = await AskAsync();

        Assert.Equal(Tr.RecoveryGroupRetryConfirm(recycle, 2), plain);
        Assert.Equal(Tr.RecoveryGroupRetryConfirmPermanent(recycle, 2, 2), permanent);
    }
}
