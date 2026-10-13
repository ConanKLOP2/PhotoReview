using System.ComponentModel;
using System.IO;
using PhotoReview.App;
using PhotoReview.App.Presenters;
using PhotoReview.Core.FileActions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.TestSupport;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// WP-35 GĐ4: cửa sổ Recovery Win32 trên HWND thật + UI thread thật của shell. Presenter (<see cref="RecoveryWindowPresenter"/>,
/// 55+28 ca ở App.Tests) đã có test riêng; ở đây kiểm VIEW: danh sách + huy hiệu theo tone, bộ lọc, chọn nhiều, chi tiết/đường dẫn,
/// Copy path/Show in Explorer, kiểm tra nền (trạng thái, Re-check, huỷ khi đóng), Retry (xác nhận, thành công/từ chối/cũ/ném lỗi,
/// khoá + từ chối đóng khi đang chạy), Clear (xác nhận có/không, cũ, lỗi), modal, DPI, tiếng Việt, rò rỉ. KHÔNG chạm Thùng rác
/// thật: thư mục tạm riêng, hộp xác nhận giả, delegate giả hoặc RecoveryRetryService thật trên đĩa tạm. Không Sleep/Delay.
/// </summary>
[Collection("OverlayLocalization")]
[Trait("Category", "UI")]
public sealed class RecoveryDialogTests
{
    private static readonly TimeSpan Bound = RecoveryRig.Bound;

    private static RecoveryRetryResult Succeeded() => new(Succeeded: true, Message: "retried", Entry: null);

    private static RecoveryRetryResult Rejected() => new(Succeeded: false, Message: "refused: changed", Entry: null);

    private static DismissOutcome Dismissed(IReadOnlyList<JournalEntry> entries) => new(entries, []);

    /// <summary>Ba mục có phán quyết khác nhau: CanRetry, AlreadyDone (cả hai trong thư mục tạm) và Lost.</summary>
    private static List<JournalEntry> ThreeEntries(RecoveryRig rig)
    {
        var retry = rig.Write(@"src\retry-me.jpg", "aaaa");
        var done = rig.Write(@"dst\already.jpg", "bbbbbb"); // đích có, nguồn đã mất
        var lost = Path.Combine(rig.Root, "src", "lost.jpg");
        return
        [
            rig.Entry("e1", retry, Path.Combine(rig.Root, "dst", "retry-me.jpg")),
            rig.Entry("e2", Path.Combine(rig.Root, "src", "already.jpg"), done),
            rig.Entry("e3", lost, Path.Combine(rig.Root, "dst", "lost.jpg"), size: 5),
        ];
    }

    private static string Text(RecoveryDialog d, int id) => TextOf(d, id);

    // ---- mở: hàng, huy hiệu, tóm tắt, nút -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Opens_WithTheRowsBeforeTheChecks_AsCheckingNeutralBadges()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var entries = ThreeEntries(rig);

        var d = await rig.OpenAsync(entries, autoCheck: false);

        await rig.OnUi(() =>
        {
            Assert.False(d.IsClosed);
            Assert.Equal(Tr.RecoveryTitle, TextOf(d.Hwnd));
            Assert.Equal(Tr.RecoverySummaryCount(3), Text(d, RecoveryDialog.SummaryId));
            Assert.Equal(3, d.VisibleRows.Count);
            Assert.Equal(4, ComboBoxControl.Count(Item(d, RecoveryDialog.FilterId)));
            Assert.Equal(Tr.RecoveryFilterAll, ComboBoxControl.TextAt(Item(d, RecoveryDialog.FilterId), 0));
            for (var i = 0; i < 3; i++)
            {
                var row = d.RowView(i);
                Assert.Equal(Path.GetFileName(entries[i].Source), row.Text);
                Assert.Equal(Tr.RecoveryRowChecking, row.Badge);
                Assert.Equal(Tr.RecoveryRowSubtitle(RecoveryPresentation.StateText(JournalState.Failed),
                    RecoveryPresentation.OperationText(FileOperationType.Move)), row.Subtitle);
                Assert.Equal(RecoveryDialog.ToColorRef(RecoveryTone.Neutral), row.Color);
            }

            // Chưa chọn gì: chi tiết trống, Retry/Clear selected tắt; Clear all cần delegate dismiss nên tắt.
            Assert.True(IsVisible(d, RecoveryDialog.DetailsEmptyId));
            Assert.False(IsVisible(d, RecoveryDialog.DetailsId));
            Assert.False(d.IsEnabled(RecoveryDialog.RetryId));
            Assert.False(d.IsEnabled(RecoveryDialog.ClearSelectedId));
            Assert.False(d.IsEnabled(RecoveryDialog.ClearAllId));
            d.Close();
        });
    }

    [Fact]
    public async Task EmptyJournal_ShowsTheEmptySummary_AndNoRows()
    {
        await using var rig = await RecoveryRig.StartAsync();

        var d = await rig.OpenAsync([], dismiss: Dismissed);
        await rig.CheckDoneAsync(d);

        await rig.OnUi(() =>
        {
            Assert.Equal(Tr.RecoverySummaryEmpty, Text(d, RecoveryDialog.SummaryId));
            Assert.Empty(d.VisibleRows);
            Assert.Equal(string.Empty, Text(d, RecoveryDialog.CheckStatusId));
            Assert.True(d.IsEnabled(RecoveryDialog.RecheckId));
            Assert.False(d.IsEnabled(RecoveryDialog.ClearAllId), "nothing to clear");
            Assert.False(IsVisible(d, RecoveryDialog.FilterEmptyId), "an empty journal is not an empty FILTER");
            d.Close();
        });
    }

    [Fact]
    public async Task AfterTheCheck_BadgesCarryTheVerdictTextAndToneColour()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var d = await rig.OpenAsync(ThreeEntries(rig));

        await rig.CheckDoneAsync(d);

        await rig.OnUi(() =>
        {
            var retry = d.RowView(0);
            Assert.Equal(RecoveryPresentation.VerdictText(RecoveryVerdict.CanRetry), retry.Badge);
            Assert.Equal(RecoveryDialog.ToColorRef(RecoveryTone.Good), retry.Color);
            var done = d.RowView(1);
            Assert.Equal(RecoveryPresentation.VerdictText(RecoveryVerdict.AlreadyDone), done.Badge);
            Assert.Equal(RecoveryDialog.ToColorRef(RecoveryTone.Info), done.Color);
            var lost = d.RowView(2);
            Assert.Equal(RecoveryPresentation.VerdictText(RecoveryVerdict.Lost), lost.Badge);
            Assert.Equal(RecoveryDialog.ToColorRef(RecoveryTone.Bad), lost.Color);
            // Màu lấy ĐÚNG từ bảng tone của presenter (đúng 0xRRGGBB -> COLORREF 0xBBGGRR).
            Assert.Equal(0x7ACB6Cu, RecoveryDialog.ToColorRef(RecoveryTone.Good));
            Assert.Equal(0x6B6BFFu, RecoveryDialog.ToColorRef(RecoveryTone.Bad));
            Assert.Equal(0xFFB16Fu, RecoveryDialog.ToColorRef(RecoveryTone.Info));
            Assert.Equal(0x40B0F0u, RecoveryDialog.ToColorRef(RecoveryTone.Warn));
            d.Close();
        });
    }

    [Fact]
    public async Task TheCheckStatus_ShowsDone_AndReCheckIsEnabledAgain()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var d = await rig.OpenAsync(ThreeEntries(rig));

        await rig.CheckDoneAsync(d);

        await rig.OnUi(() =>
        {
            var prefix = Tr.RecoveryCheckDone(3, "|").Split('|')[0];
            Assert.StartsWith(prefix, Text(d, RecoveryDialog.CheckStatusId), StringComparison.Ordinal);
            Assert.True(d.IsEnabled(RecoveryDialog.RecheckId));
            d.Close();
        });
    }

    // ---- bộ lọc -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Filter_ShowsOnlyTheMatchingVerdict_AndFlagsAnEmptyResult()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var d = await rig.OpenAsync(ThreeEntries(rig));
        await rig.CheckDoneAsync(d);

        await rig.FilterAsync(d, 1);
        var canRetry = await rig.OnUi(() => d.VisibleRows.Select(r => r.Entry.Id).ToArray());
        await rig.FilterAsync(d, 2);
        var alreadyDone = await rig.OnUi(() => d.VisibleRows.Select(r => r.Entry.Id).ToArray());
        await rig.FilterAsync(d, 3);
        var (problems, listVisible) = await rig.OnUi(() => (d.VisibleRows.Select(r => r.Entry.Id).ToArray(), IsVisible(d, RecoveryDialog.ListId)));
        await rig.FilterAsync(d, 0);
        var all = await rig.OnUi(() => d.VisibleRows.Count);

        Assert.Equal(["e1"], canRetry);
        Assert.Equal(["e2"], alreadyDone);
        Assert.Equal(["e3"], problems);
        Assert.True(listVisible);
        Assert.Equal(3, all);
    }

    [Fact]
    public async Task Filter_WithNoMatch_ShowsTheEmptyNotice_InPlaceOfTheList()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var only = rig.Write(@"src\a.jpg");
        var d = await rig.OpenAsync([rig.Entry("e1", only, Path.Combine(rig.Root, "dst", "a.jpg"))]); // CanRetry
        await rig.CheckDoneAsync(d);

        await rig.FilterAsync(d, 3); // Problems: nothing matches

        await rig.OnUi(() =>
        {
            Assert.Empty(d.VisibleRows);
            Assert.True(IsVisible(d, RecoveryDialog.FilterEmptyId));
            Assert.Equal(Tr.RecoveryFilterEmpty, Text(d, RecoveryDialog.FilterEmptyId));
            Assert.False(IsVisible(d, RecoveryDialog.ListId));
            Assert.Equal(Tr.RecoverySummaryCount(1), Text(d, RecoveryDialog.SummaryId));
        });

        await rig.FilterAsync(d, 0);
        await rig.OnUi(() =>
        {
            Assert.False(IsVisible(d, RecoveryDialog.FilterEmptyId));
            Assert.True(IsVisible(d, RecoveryDialog.ListId));
            Assert.Single(d.VisibleRows);
        });
    }

    [Fact]
    public async Task Filter_KeepsTheSelectionOfRowsThatStayVisible()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var d = await rig.OpenAsync(ThreeEntries(rig));
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0, 2); // e1 + e3

        await rig.FilterAsync(d, 1); // only e1 stays

        await rig.OnUi(() =>
        {
            var selected = d.SelectedRows();
            Assert.Equal("e1", Assert.Single(selected).Entry.Id);
            d.Close();
        });
    }

    // ---- chọn nhiều + nút -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task MultiSelect_EnablesClearSelected_ButNotRetry_AndShowsNoDetails()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var d = await rig.OpenAsync(ThreeEntries(rig), retry: _ => Task.FromResult(Succeeded()), dismiss: Dismissed);
        await rig.CheckDoneAsync(d);

        await rig.PickAsync(d, 0, 1);

        await rig.OnUi(() =>
        {
            Assert.Equal(2, d.SelectedRows().Count);
            Assert.True(d.IsEnabled(RecoveryDialog.ClearSelectedId));
            Assert.True(d.IsEnabled(RecoveryDialog.ClearAllId));
            Assert.False(d.IsEnabled(RecoveryDialog.RetryId), "Retry needs exactly one selected row");
            Assert.True(IsVisible(d, RecoveryDialog.DetailsEmptyId));
            d.Close();
        });
    }

    [Fact]
    public async Task Retry_IsEnabledOnlyForASingleRetryableRow()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var d = await rig.OpenAsync(ThreeEntries(rig), retry: _ => Task.FromResult(Succeeded()), dismiss: Dismissed);
        await rig.CheckDoneAsync(d);

        await rig.PickAsync(d, 0);
        var canRetry = await rig.OnUi(() => d.IsEnabled(RecoveryDialog.RetryId));
        await rig.PickAsync(d, 1); // AlreadyDone: dismiss, not retry
        var alreadyDone = await rig.OnUi(() => d.IsEnabled(RecoveryDialog.RetryId));
        await rig.PickAsync(d, 2); // Lost
        var lost = await rig.OnUi(() => d.IsEnabled(RecoveryDialog.RetryId));

        Assert.True(canRetry);
        Assert.False(alreadyDone);
        Assert.False(lost);
    }

    [Fact]
    public async Task WithoutDelegates_RetryAndClearStayDisabled()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var d = await rig.OpenAsync(ThreeEntries(rig));
        await rig.CheckDoneAsync(d);

        await rig.PickAsync(d, 0);

        await rig.OnUi(() =>
        {
            Assert.False(d.IsEnabled(RecoveryDialog.RetryId));
            Assert.False(d.IsEnabled(RecoveryDialog.ClearSelectedId));
            Assert.False(d.IsEnabled(RecoveryDialog.ClearAllId));
            d.Close();
        });
    }

    // ---- chi tiết ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Details_ShowVerdictPathsAndStatus_OfTheSelectedRow()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var entries = ThreeEntries(rig);
        var d = await rig.OpenAsync(entries);
        await rig.CheckDoneAsync(d);

        await rig.PickAsync(d, 0);

        await rig.OnUi(() =>
        {
            Assert.True(IsVisible(d, RecoveryDialog.VerdictBadgeId));
            Assert.False(IsVisible(d, RecoveryDialog.DetailsEmptyId));
            Assert.Equal(RecoveryPresentation.VerdictText(RecoveryVerdict.CanRetry), Text(d, RecoveryDialog.VerdictBadgeId));
            Assert.Equal(RecoveryDialog.ToColorRef(RecoveryTone.Good), d.TextColorOf(RecoveryDialog.VerdictBadgeId));
            Assert.Equal(entries[0].Source, Text(d, RecoveryDialog.SourcePathId));
            Assert.Equal(entries[0].Destination, Text(d, RecoveryDialog.DestinationPathId));
            Assert.Equal(Tr.RecoveryDetailSource, Text(d, RecoveryDialog.SourceTitleId));
            Assert.Equal(Tr.RecoveryDetailDestination, Text(d, RecoveryDialog.DestinationTitleId));
            Assert.Equal(Tr.RecoveryDetailStatus(RecoveryPresentation.PathStatusText(RecoveryPathStatus.Exists)),
                Text(d, RecoveryDialog.SourceStatusId));
            Assert.Equal(RecoveryDialog.ToColorRef(RecoveryTone.Good), d.TextColorOf(RecoveryDialog.SourceStatusId));
            Assert.Equal(Tr.RecoveryDetailStatus(RecoveryPresentation.PathStatusText(RecoveryPathStatus.Missing)),
                Text(d, RecoveryDialog.DestinationStatusId));
            var body = Text(d, RecoveryDialog.DetailsId);
            Assert.Contains(RecoveryPresentation.ExplainText(RecoveryVerdict.CanRetry), body, StringComparison.Ordinal);
            Assert.Contains(RecoveryPresentation.ActionText(RecoveryVerdict.CanRetry), body, StringComparison.Ordinal);
            Assert.True(IsVisible(d, RecoveryDialog.CopySourceId));
            Assert.True(IsVisible(d, RecoveryDialog.ShowDestinationId));
            Assert.False(IsVisible(d, RecoveryDialog.NoDestinationId));
            d.Close();
        });
    }

    [Fact]
    public async Task Details_OfARowNotYetChecked_ShowTheJournalDataAndChecking()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var entries = ThreeEntries(rig);
        var d = await rig.OpenAsync(entries, autoCheck: false);

        await rig.PickAsync(d, 1);

        await rig.OnUi(() =>
        {
            Assert.Equal(Tr.RecoveryRowChecking, Text(d, RecoveryDialog.VerdictBadgeId));
            Assert.Equal(entries[1].Source, Text(d, RecoveryDialog.SourcePathId));
            Assert.Contains(Tr.RecoveryDetailChecking, Text(d, RecoveryDialog.DetailsId), StringComparison.Ordinal);
            Assert.Contains(RecoveryPresentation.JournalText(entries[1]), Text(d, RecoveryDialog.DetailsId), StringComparison.Ordinal);
            d.Close();
        });
    }

    [Fact]
    public async Task Details_UpdateWhenTheSelectedRowsCheckResultArrives()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var entries = ThreeEntries(rig);
        var d = await rig.OpenAsync(entries, autoCheck: false);
        await rig.PickAsync(d, 0);
        var before = await rig.OnUi(() => Text(d, RecoveryDialog.VerdictBadgeId));

        await rig.OnUi(() => d.StartChecks());
        await rig.CheckDoneAsync(d);

        var after = await rig.OnUi(() => Text(d, RecoveryDialog.VerdictBadgeId));
        var enabledRetryable = await rig.OnUi(() => d.TextColorOf(RecoveryDialog.VerdictBadgeId));
        Assert.Equal(Tr.RecoveryRowChecking, before);
        Assert.Equal(RecoveryPresentation.VerdictText(RecoveryVerdict.CanRetry), after);
        Assert.Equal(RecoveryDialog.ToColorRef(RecoveryTone.Good), enabledRetryable);
    }

    [Fact]
    public async Task Details_ShowTheJournalError_InTheErrorColour()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var source = rig.Write(@"src\err.jpg");
        var entry = rig.Entry("err", source, Path.Combine(rig.Root, "dst", "err.jpg"), error: "The device is not ready");
        var plain = rig.Entry("ok", source, Path.Combine(rig.Root, "dst", "ok.jpg"), error: null);
        var d = await rig.OpenAsync([entry, plain]);
        await rig.CheckDoneAsync(d);

        await rig.PickAsync(d, 0);
        var shown = await rig.OnUi(() => (IsVisible(d, RecoveryDialog.ErrorId), Text(d, RecoveryDialog.ErrorId),
            d.TextColorOf(RecoveryDialog.ErrorId)));
        await rig.PickAsync(d, 1);
        var hidden = await rig.OnUi(() => IsVisible(d, RecoveryDialog.ErrorId));

        Assert.True(shown.Item1);
        Assert.Equal(Tr.RecoveryDetailError(JournalErrors.Describe(entry)!), shown.Item2);
        Assert.Contains("The device is not ready", shown.Item2, StringComparison.Ordinal);
        Assert.Equal(DialogColors.FromKey("Dark.ErrorText"), shown.Item3);
        Assert.False(hidden);
    }

    [Fact]
    public async Task Details_OfACaptureGroup_ListTheMembers_AndHideTheSinglePathPanels()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var a = rig.Write(@"src\g1.jpg", "1111");
        var b = rig.Write(@"src\g1.raw", "22222");
        var members = new List<JournalGroupMember>
        {
            new(a, Path.Combine(rig.Root, "dst", "g1.jpg"), 4, new FileInfo(a).LastWriteTimeUtc),
            new(b, Path.Combine(rig.Root, "dst", "g1.raw"), 5, new FileInfo(b).LastWriteTimeUtc),
        };
        var group = rig.Entry("grp", a, null, members: members);
        var d = await rig.OpenAsync([group], retry: _ => Task.FromResult(Succeeded()), dismiss: Dismissed);
        await rig.CheckDoneAsync(d);

        await rig.PickAsync(d, 0);

        await rig.OnUi(() =>
        {
            Assert.Equal(Tr.RecoveryGroupTitle("g1.jpg", 2), d.RowView(0).Text);
            Assert.False(IsVisible(d, RecoveryDialog.SourcePathId));
            Assert.False(IsVisible(d, RecoveryDialog.DestinationPathId));
            Assert.False(IsVisible(d, RecoveryDialog.NoDestinationId));
            var body = Text(d, RecoveryDialog.DetailsId);
            Assert.Contains(Tr.RecoveryGroupMembers(2), body, StringComparison.Ordinal);
            Assert.Contains(a, body, StringComparison.Ordinal);
            Assert.Contains(members[1].Destination!, body, StringComparison.Ordinal);
            Assert.True(d.IsEnabled(RecoveryDialog.RetryId), "a retryable capture group can be retried");
            d.Close();
        });
    }

    [Fact]
    public async Task Details_OfAnEntryWithoutDestination_SayThereIsNone_AndRetryStaysOff()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var source = rig.Write(@"src\trash.jpg"); // Delete that failed: the file is still on disk
        var d = await rig.OpenAsync([rig.Entry("del", source, null, FileOperationType.Recycle)],
            retry: _ => Task.FromResult(Succeeded()), dismiss: Dismissed);
        await rig.CheckDoneAsync(d);

        await rig.PickAsync(d, 0);

        await rig.OnUi(() =>
        {
            Assert.Equal(RecoveryPresentation.VerdictText(RecoveryVerdict.NotRecycled), Text(d, RecoveryDialog.VerdictBadgeId));
            Assert.Equal(RecoveryDialog.ToColorRef(RecoveryTone.Warn), d.TextColorOf(RecoveryDialog.VerdictBadgeId));
            Assert.True(IsVisible(d, RecoveryDialog.NoDestinationId));
            Assert.False(IsVisible(d, RecoveryDialog.DestinationPathId));
            Assert.False(d.IsEnabled(RecoveryDialog.RetryId));
            Assert.True(d.IsEnabled(RecoveryDialog.ClearSelectedId));
            d.Close();
        });
    }

    // ---- Copy path / Show in Explorer ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task CopyPath_SendsTheShownPathOfThatPanelToTheClipboard()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var entries = ThreeEntries(rig);
        var d = await rig.OpenAsync(entries);
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);

        await rig.ClickAsync(d, RecoveryDialog.CopySourceId);
        await rig.ClickAsync(d, RecoveryDialog.CopyDestinationId);

        Assert.Equal([entries[0].Source, entries[0].Destination!], rig.Copied);
    }

    [Fact]
    public async Task ShowInExplorer_SelectsAnExistingFile_OtherwiseOpensTheNearestExistingFolder_ReadOnly()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var entries = ThreeEntries(rig);
        var deep = Path.Combine(rig.Root, "gone", "deeper", "x.jpg");
        var entry = rig.Entry("deep", entries[0].Source, deep);
        var d = await rig.OpenAsync([entry]);
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);
        var before = rig.Tree();

        await rig.ClickAsync(d, RecoveryDialog.ShowSourceId);
        await rig.ClickAsync(d, RecoveryDialog.ShowDestinationId);

        Assert.Equal([$"/select,\"{entry.Source}\"", $"\"{rig.Root}\""], rig.ExplorerArgs);
        Assert.Equal(before, rig.Tree());
    }

    [Fact]
    public async Task ShowInExplorer_WhenExplorerCannotStart_ShowsTheOsMessage_AndKeepsTheWindow()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var entries = ThreeEntries(rig);
        rig.ExplorerError = new Win32Exception("explorer.exe is blocked");
        var d = await rig.OpenAsync([entries[0]]);
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);

        await rig.ClickAsync(d, RecoveryDialog.ShowSourceId);

        var call = Assert.Single(rig.Messages.Calls);
        Assert.Equal(Tr.RecoveryExplorerFailedTitle, call.Title);
        Assert.Contains("explorer.exe is blocked", call.Message, StringComparison.Ordinal);
        Assert.Equal(TaskDialogKind.Error, call.Kind);
        Assert.False(await rig.OnUi(() => d.IsClosed));
    }

    // ---- kiểm tra nền -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReCheck_IsDisabledWhileRunning_AndRunsAgainOnClick()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var entries = ThreeEntries(rig);
        using var gate = new ManualResetEventSlim(false);
        var d = await rig.OpenAsync(entries,
            fileSystem: new HookFileSystem(new PhotoReview.Core.IO.PhysicalFileSystem(), _ => gate.Wait(Bound)));

        // Lượt tự chạy lúc mở đang bị chặn ở lần stat đầu tiên: nút Re-check tắt, trạng thái hiện tiến độ 0/3.
        var running = await rig.OnUi(() => (d.IsEnabled(RecoveryDialog.RecheckId), Text(d, RecoveryDialog.CheckStatusId)));
        gate.Set();
        await rig.CheckDoneAsync(d);
        var first = await rig.OnUi(() => d.LastCheckTask);
        await rig.ClickAsync(d, RecoveryDialog.RecheckId);
        await rig.CheckDoneAsync(d);
        var second = await rig.OnUi(() => d.LastCheckTask);

        Assert.False(running.Item1);
        Assert.Equal(Tr.RecoveryCheckProgress(0, 3), running.Item2);
        Assert.NotSame(first, second);
        Assert.True(await rig.OnUi(() => d.IsEnabled(RecoveryDialog.RecheckId)));
    }

    [Fact]
    public async Task ClosingDuringTheCheck_CancelsIt_AndWritesNothingToTheRows()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var entries = ThreeEntries(rig);
        using var reached = new ManualResetEventSlim(false);
        using var gate = new ManualResetEventSlim(false);
        var d = await rig.OpenAsync(entries, fileSystem: new HookFileSystem(new PhotoReview.Core.IO.PhysicalFileSystem(), _ =>
        {
            reached.Set();
            gate.Wait(Bound);
        }));
        Assert.True(reached.Wait(Bound), "the background check never started");

        await rig.OnUi(() => d.Close()); // window closes mid-check
        gate.Set();
        var task = await rig.OnUi(() => d.LastCheckTask!);
        await task.WaitAsync(Bound);

        var rows = await rig.OnUi(() => d.Presenter.Rows.Select(r => r.Check).ToArray());
        Assert.All(rows, check => Assert.Null(check));
        Assert.True(await rig.OnUi(() => d.IsClosed));
    }

    [Fact]
    public async Task ACheckerFailure_DoesNotLeaveReCheckDisabled()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var entries = ThreeEntries(rig);
        var d = await rig.OpenAsync(entries,
            fileSystem: new HookFileSystem(new PhotoReview.Core.IO.PhysicalFileSystem(),
                _ => throw new InvalidOperationException("share went away")));

        await rig.CheckDoneAsync(d);

        await rig.OnUi(() =>
        {
            Assert.True(d.IsEnabled(RecoveryDialog.RecheckId));
            Assert.Equal(string.Empty, Text(d, RecoveryDialog.CheckStatusId));
            Assert.False(d.IsClosed);
            d.Close();
        });
    }

    // ---- Retry ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Retry_DeclinedAtTheConfirmation_NeverCallsTheDelegate()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var calls = 0;
        rig.Messages.Answer = (_, _) => false;
        var d = await rig.OpenAsync(ThreeEntries(rig), retry: _ =>
        {
            calls++;
            return Task.FromResult(Succeeded());
        });
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);
        var hwnd = await rig.OnUi(() => d.Hwnd);

        await rig.ClickAsync(d, RecoveryDialog.RetryId);
        await rig.RetryDoneAsync(d);

        Assert.Equal(0, calls);
        var question = Assert.Single(rig.Messages.Calls);
        Assert.Equal(Tr.DialogConfirmRetryTitle, question.Title);
        Assert.Equal(TaskDialogKind.Confirmation, question.Kind);
        Assert.Equal(Tr.DialogConfirmRetryMessage(RecoveryPresentation.OperationText(FileOperationType.Move), "retry-me.jpg"), question.Message);
        Assert.Equal(hwnd, question.Owner); // the confirmation is modal over the Recovery window itself
        Assert.False(await rig.OnUi(() => d.IsClosed));
    }

    [Fact]
    public async Task Retry_Confirmed_CallsTheDelegateOnceForTheSelectedEntry_ThenReportsAndCloses()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var entries = ThreeEntries(rig);
        var seen = new List<string>();
        var questionBeforeDelegate = false;
        rig.Messages.OnShow = message =>
        {
            if (message.Kind == TaskDialogKind.Confirmation)
            {
                questionBeforeDelegate = seen.Count == 0;
            }
        };
        var d = await rig.OpenAsync(entries, retry: entry =>
        {
            seen.Add(entry.Id);
            return Task.FromResult(Succeeded());
        });
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);

        await rig.ClickAsync(d, RecoveryDialog.RetryId);
        await rig.RetryDoneAsync(d);

        Assert.Equal(["e1"], seen);
        Assert.True(questionBeforeDelegate, "the confirmation is asked BEFORE the delegate runs");
        var notice = rig.Messages.Calls[^1];
        Assert.Equal(Tr.DialogRetrySucceededTitle, notice.Title);
        Assert.Equal("retried", notice.Message);
        Assert.Equal(TaskDialogKind.Information, notice.Kind);
        Assert.True(await rig.OnUi(() => d.IsClosed), "a successful retry closes the window");
    }

    [Fact]
    public async Task Retry_Rejected_ShowsAWarning_KeepsTheWindow_AndChecksAgain()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var d = await rig.OpenAsync(ThreeEntries(rig), retry: _ => Task.FromResult(Rejected()));
        await rig.CheckDoneAsync(d);
        var firstCheck = await rig.OnUi(() => d.LastCheckTask);
        await rig.PickAsync(d, 0);

        await rig.ClickAsync(d, RecoveryDialog.RetryId);
        await rig.RetryDoneAsync(d);
        await rig.CheckDoneAsync(d);

        var notice = rig.Messages.Calls[^1];
        Assert.Equal(Tr.DialogRetryRejectedTitle, notice.Title);
        Assert.Equal(TaskDialogKind.Error, notice.Kind);
        Assert.Equal("refused: changed", notice.Message);
        Assert.False(await rig.OnUi(() => d.IsClosed));
        Assert.NotSame(firstCheck, await rig.OnUi(() => d.LastCheckTask));
        Assert.False(await rig.OnUi(() => d.IsBusy));
    }

    [Fact]
    public async Task Retry_Superseded_DropsTheRow_RefreshesTheList_AndOnlyInforms()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var superseded = new RecoveryRetryResult(Succeeded: false, Message: "handled elsewhere", Entry: null, Superseded: true);
        var d = await rig.OpenAsync(ThreeEntries(rig), retry: _ => Task.FromResult(superseded), dismiss: Dismissed);
        await rig.CheckDoneAsync(d);
        var check = await rig.OnUi(() => d.LastCheckTask);
        await rig.PickAsync(d, 0);

        await rig.ClickAsync(d, RecoveryDialog.RetryId);
        await rig.RetryDoneAsync(d);

        await rig.OnUi(() =>
        {
            Assert.Equal(["e2", "e3"], d.VisibleRows.Select(r => r.Entry.Id).ToArray());
            Assert.Equal(Tr.RecoverySummaryCount(2), Text(d, RecoveryDialog.SummaryId));
            Assert.False(d.IsClosed);
            Assert.Same(check, d.LastCheckTask); // informational: no re-check
            d.Close();
        });
        var notice = rig.Messages.Calls[^1];
        Assert.Equal(Tr.RecoveryRetryStaleTitle, notice.Title);
        Assert.Equal(TaskDialogKind.Information, notice.Kind);
        Assert.Equal("handled elsewhere", notice.Message);
    }

    [Fact]
    public async Task Retry_WhenTheDelegateThrows_ShowsTheMessage_AndReleasesTheBusyState()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var d = await rig.OpenAsync(ThreeEntries(rig), retry: _ => throw new IOException("disk full"), dismiss: Dismissed);
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);

        await rig.ClickAsync(d, RecoveryDialog.RetryId);
        await rig.RetryDoneAsync(d);

        var notice = rig.Messages.Calls[^1];
        Assert.Equal(Tr.DialogRetryRejectedTitle, notice.Title);
        Assert.Equal("disk full", notice.Message);
        Assert.Equal(TaskDialogKind.Error, notice.Kind);
        await rig.OnUi(() =>
        {
            Assert.False(d.IsBusy);
            Assert.False(d.Presenter.IsRetrying);
            Assert.True(d.IsEnabled(RecoveryDialog.RetryId));
            Assert.True(d.IsEnabled(RecoveryDialog.ClearAllId));
            Assert.False(d.IsClosed);
            d.Close();
        });
    }

    [Fact]
    public async Task WhileARetryRuns_RetryAndClearAreLocked_AndTheWindowRefusesEveryWayOfClosing()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var gate = new TaskCompletionSource<RecoveryRetryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var d = await rig.OpenAsync(ThreeEntries(rig), retry: _ => gate.Task, dismiss: Dismissed);
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);

        await rig.ClickAsync(d, RecoveryDialog.RetryId);

        await rig.OnUi(() =>
        {
            Assert.True(d.IsBusy);
            Assert.True(d.Presenter.IsRetrying);
            Assert.False(d.IsEnabled(RecoveryDialog.RetryId));
            Assert.False(d.IsEnabled(RecoveryDialog.ClearSelectedId));
            Assert.False(d.IsEnabled(RecoveryDialog.ClearAllId));

            User32.SendMessage(d.Hwnd, WindowMessages.WmClose, 0, 0); // X / Alt+F4
            Assert.False(d.IsClosed);
            Command(d, NativeDialog.IdCancel); // Close button
            Assert.False(d.IsClosed);
            Assert.True(Key(d, Item(d, RecoveryDialog.ListId), VkEscape)); // Esc through the dialog manager
            Drain(d);
            Assert.False(d.IsClosed);
        });

        gate.SetResult(Succeeded());
        await rig.RetryDoneAsync(d);

        Assert.True(await rig.OnUi(() => d.IsClosed));
        Assert.Equal(Tr.DialogRetrySucceededTitle, rig.Messages.Calls[^1].Title);
    }

    [Fact]
    public async Task ClosingTheShellMidRetry_ClosesTheWindowWithoutHanging_AndTheLateResultTouchesNoView()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var gate = new TaskCompletionSource<RecoveryRetryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var d = await rig.OpenAsync(ThreeEntries(rig), retry: _ => gate.Task);
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);
        await rig.ClickAsync(d, RecoveryDialog.RetryId);
        var calls = rig.Messages.Calls.Count;

        await rig.OnUi(() => d.Close()); // shell exit: an external close always wins (no hung nested loop)
        Assert.True(await rig.OnUi(() => d.IsClosed));
        gate.SetResult(Succeeded());
        await rig.RetryDoneAsync(d);

        Assert.Equal(calls, rig.Messages.Calls.Count); // no notice for a window that is gone
        Assert.Equal(0, await rig.OnUi(() => d.Hwnd));
    }

    [Fact]
    public async Task WhileARetryRuns_TheWaitCursorIsShownOverTheDialog_AndOnlyThen()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var gate = new TaskCompletionSource<RecoveryRetryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var d = await rig.OpenAsync(ThreeEntries(rig), retry: _ => gate.Task);
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);
        var arrow = await rig.OnUi(() => User32.LoadCursor(0, 32512));
        var wait = await rig.OnUi(() => User32.LoadCursor(0, 32514));
        var hit = (nint)((0x0200 << 16) | 1); // WM_MOUSEMOVE over HTCLIENT

        await rig.ClickAsync(d, RecoveryDialog.RetryId);
        var whileBusy = await rig.OnUi(() =>
        {
            User32.SetCursor(arrow);
            var handled = User32.SendMessage(d.Hwnd, WindowMessages.WmSetCursor, Item(d, RecoveryDialog.ListId), hit);
            return (handled, GetCursor());
        });
        gate.SetResult(Rejected());
        await rig.RetryDoneAsync(d);
        await rig.CheckDoneAsync(d);
        var afterwards = await rig.OnUi(() =>
        {
            User32.SetCursor(arrow);
            User32.SendMessage(d.Hwnd, WindowMessages.WmSetCursor, Item(d, RecoveryDialog.ListId), hit);
            return GetCursor();
        });

        Assert.Equal(1, whileBusy.handled);
        Assert.Equal(wait, whileBusy.Item2);
        Assert.Equal(arrow, afterwards);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetCursor();

    [Fact]
    public async Task ReadOnlyBoxes_ArePaintedLikeInputFields_WhileLabelsKeepTheWindowColour()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var d = await rig.OpenAsync(ThreeEntries(rig), autoCheck: false);
        await rig.PickAsync(d, 0);

        var (details, path, label) = await rig.OnUi(() =>
        {
            const uint WmCtlColorStatic = 0x0138;
            var hdc = User32.GetDC(d.Hwnd);
            try
            {
                uint BackgroundFor(int id)
                {
                    User32.SendMessage(d.Hwnd, WmCtlColorStatic, hdc, Item(d, id));
                    return Gdi32.GetBkColor(hdc);
                }

                return (BackgroundFor(RecoveryDialog.DetailsId), BackgroundFor(RecoveryDialog.SourcePathId),
                    BackgroundFor(RecoveryDialog.SummaryId));
            }
            finally
            {
                _ = User32.ReleaseDC(d.Hwnd, hdc);
            }
        });

        Assert.Equal(DialogColors.Input, details);
        Assert.Equal(DialogColors.Input, path);
        Assert.Equal(DialogColors.Window, label);
    }

    // ---- Clear (dismiss) -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ClearSelected_Declined_CallsNothing_AndKeepsTheRows()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var calls = 0;
        rig.Messages.Answer = (_, _) => false;
        var d = await rig.OpenAsync(ThreeEntries(rig), dismiss: entries =>
        {
            calls++;
            return Dismissed(entries);
        });
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0, 1);

        await rig.ClickAsync(d, RecoveryDialog.ClearSelectedId);

        Assert.Equal(0, calls);
        var question = Assert.Single(rig.Messages.Calls);
        Assert.Equal(Tr.RecoveryDismissConfirmTitle, question.Title);
        Assert.Equal(Tr.RecoveryDismissConfirmMessage(2), question.Message);
        Assert.Equal(3, await rig.OnUi(() => d.VisibleRows.Count));
    }

    [Fact]
    public async Task ClearSelected_Confirmed_DismissesExactlyTheSelectedEntries_AndRemovesTheirRows()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var dismissed = new List<string>();
        var d = await rig.OpenAsync(ThreeEntries(rig), dismiss: entries =>
        {
            dismissed.AddRange(entries.Select(e => e.Id));
            return Dismissed(entries);
        });
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0, 2);

        await rig.ClickAsync(d, RecoveryDialog.ClearSelectedId);

        Assert.Equal(["e1", "e3"], dismissed);
        await rig.OnUi(() =>
        {
            Assert.Equal(["e2"], d.VisibleRows.Select(r => r.Entry.Id).ToArray());
            Assert.Equal(Tr.RecoverySummaryCount(1), Text(d, RecoveryDialog.SummaryId));
            Assert.Empty(d.SelectedRows());
            d.Close();
        });
        Assert.Single(rig.Messages.Calls); // the confirmation only: no stale notice
    }

    [Fact]
    public async Task ClearAll_Confirmed_DismissesEveryRow_EvenOnesHiddenByTheFilter()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var dismissed = new List<string>();
        var d = await rig.OpenAsync(ThreeEntries(rig), dismiss: entries =>
        {
            dismissed.AddRange(entries.Select(e => e.Id));
            return Dismissed(entries);
        });
        await rig.CheckDoneAsync(d);
        await rig.FilterAsync(d, 1); // only e1 visible

        await rig.ClickAsync(d, RecoveryDialog.ClearAllId);

        Assert.Equal(["e1", "e2", "e3"], dismissed);
        Assert.Equal(Tr.RecoveryDismissConfirmMessage(3), rig.Messages.Calls[0].Message);
        await rig.OnUi(() =>
        {
            Assert.Empty(d.VisibleRows);
            Assert.Equal(Tr.RecoverySummaryEmpty, Text(d, RecoveryDialog.SummaryId));
            Assert.False(d.IsEnabled(RecoveryDialog.ClearAllId));
            d.Close();
        });
    }

    [Fact]
    public async Task Clear_WhenSomeEntriesWereAlreadyResolved_RemovesThemToo_AndSaysSo()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var d = await rig.OpenAsync(ThreeEntries(rig), dismiss: entries => new DismissOutcome([entries[0]], [entries[1]]));
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0, 1);

        await rig.ClickAsync(d, RecoveryDialog.ClearSelectedId);

        var notice = rig.Messages.Calls[^1];
        Assert.Equal(Tr.RecoveryDismissStaleTitle, notice.Title);
        Assert.Equal(Tr.RecoveryDismissStaleMessage(1), notice.Message);
        Assert.Equal(TaskDialogKind.Information, notice.Kind);
        Assert.Equal(["e3"], await rig.OnUi(() => d.VisibleRows.Select(r => r.Entry.Id).ToArray()));
    }

    [Fact]
    public async Task Clear_WhenTheJournalThrows_ShowsTheError_AndRemovesNothing()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var d = await rig.OpenAsync(ThreeEntries(rig), dismiss: _ => throw new IOException("journal is locked"));
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);

        await rig.ClickAsync(d, RecoveryDialog.ClearSelectedId);

        var notice = rig.Messages.Calls[^1];
        Assert.Equal(Tr.RecoveryDismissFailedTitle, notice.Title);
        Assert.Equal("journal is locked", notice.Message);
        Assert.Equal(TaskDialogKind.Error, notice.Kind);
        Assert.Equal(3, await rig.OnUi(() => d.VisibleRows.Count));
    }

    // ---- modal, phím, đóng ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Modal_DisablesTheOwner_AndEscAndTheCloseButtonClose()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var observed = new List<(bool OwnerEnabled, bool Owned)>();

        foreach (var path in new[] { "esc", "close", "x" })
        {
            await rig.Ui.InvokeAsync(() =>
            {
                var dialog = new RecoveryDialog(ThreeEntries(rig), seams: new RecoveryDialogSeams(rig.Messages, AutoCheck: false));
                var result = dialog.ShowModal(rig.Owner, ready: d =>
                {
                    observed.Add((User32.IsWindowEnabled(rig.Owner), User32.GetWindow(d.Hwnd, 4 /* GW_OWNER */) == rig.Owner));
                    switch (path)
                    {
                        case "esc":
                            Assert.True(Key(d, Item(d, RecoveryDialog.ListId), VkEscape));
                            Drain(d);
                            break;
                        case "close":
                            Command(d, NativeDialog.IdCancel);
                            break;
                        default:
                            User32.SendMessage(d.Hwnd, WindowMessages.WmClose, 0, 0);
                            break;
                    }
                }, loop: rig.Ui.Loop);
                Assert.Equal(NativeDialog.IdCancel, result);
                Assert.True(User32.IsWindowEnabled(rig.Owner), "the owner is enabled again after the dialog closes");
            });
        }

        Assert.All(observed, item =>
        {
            Assert.False(item.OwnerEnabled);
            Assert.True(item.Owned);
        });
    }

    [Fact]
    public async Task InTheModalLoop_EscapeIsStillRefusedWhileARetryRuns()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var gate = new TaskCompletionSource<RecoveryRetryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        RecoveryDialog? shown = null;
        var stillOpenAfterEsc = false;
        var ui = rig.Ui.InvokeAsync(() =>
        {
            var dialog = new RecoveryDialog(ThreeEntries(rig), _ => gate.Task, Dismissed,
                seams: new RecoveryDialogSeams(rig.Messages, AutoCheck: true));
            dialog.ShowModal(rig.Owner, ready: d =>
            {
                shown = dialog;
                _ = d;
            }, loop: rig.Ui.Loop);
        });
        Assert.True(SpinWait.SpinUntil(() => shown is not null, Bound));
        var d1 = shown!;
        await rig.CheckDoneAsync(d1);
        await rig.PickAsync(d1, 0);
        await rig.ClickAsync(d1, RecoveryDialog.RetryId);

        await rig.OnUi(() =>
        {
            Assert.True(Key(d1, Item(d1, RecoveryDialog.ListId), VkEscape));
            stillOpenAfterEsc = !d1.IsClosed;
        });
        gate.SetResult(Succeeded());
        await ui.WaitAsync(Bound); // the modal loop ends after the success notice closes the window

        Assert.True(stillOpenAfterEsc);
    }

    // ---- bố cục, DPI, ngôn ngữ, rò rỉ -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en")]
    [InlineData("vi")]
    public async Task Layout_AtTwoScales_NothingVisibleOverlapsOrLeavesTheClient(string language)
    {
        using var scope = TestLocalization.Use(language == "vi" ? TestLocalization.Vietnamese : TestLocalization.English);
        await using var rig = await RecoveryRig.StartAsync();
        var long1 = rig.Write(@"src\" + new string('a', 40) + ".jpg");
        var longDest = Path.Combine(rig.Root, "dst", string.Join(@"\", Enumerable.Repeat("a-quite-long-folder-name", 5)), "x.jpg");
        var entries = new List<JournalEntry>
        {
            rig.Entry("e1", long1, longDest, error: "The network path was not found because the share went offline"),
            rig.Entry("e2", Path.Combine(rig.Root, "src", "b.jpg"), Path.Combine(rig.Root, "dst", "b.jpg")),
        };
        var d = await rig.OpenAsync(entries, retry: _ => Task.FromResult(Succeeded()), dismiss: Dismissed);
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);

        var problems = await rig.OnUi(() =>
        {
            var list = new List<string>();
            list.AddRange(Problems(d, "96 dpi"));
            ChangeDpi(d, 144);
            list.AddRange(Problems(d, "144 dpi"));
            return list;
        });

        Assert.Empty(problems);
    }

    private static IEnumerable<string> Problems(RecoveryDialog d, string label)
    {
        int[] ids =
        [
            RecoveryDialog.HeaderId, RecoveryDialog.SummaryId, RecoveryDialog.FilterLabelId, RecoveryDialog.FilterId, RecoveryDialog.RecheckId,
            RecoveryDialog.CheckStatusId, RecoveryDialog.ListId, RecoveryDialog.FilterEmptyId, RecoveryDialog.DetailsEmptyId,
            RecoveryDialog.VerdictLabelId, RecoveryDialog.VerdictBadgeId, RecoveryDialog.ErrorId, RecoveryDialog.SourceTitleId,
            RecoveryDialog.SourceStatusId, RecoveryDialog.SourcePathId, RecoveryDialog.CopySourceId, RecoveryDialog.ShowSourceId,
            RecoveryDialog.DestinationTitleId, RecoveryDialog.DestinationStatusId, RecoveryDialog.DestinationPathId,
            RecoveryDialog.CopyDestinationId, RecoveryDialog.ShowDestinationId, RecoveryDialog.NoDestinationId, RecoveryDialog.DetailsId,
            RecoveryDialog.ClearSelectedId, RecoveryDialog.ClearAllId, RecoveryDialog.RetryId, RecoveryDialog.CloseId,
        ];
        var client = ClientRect(d.Hwnd);
        var rects = ids.Where(id => IsVisible(d, id)).Select(id => (id, rect: ChildRect(d, id))).ToList();
        foreach (var (id, rect) in rects)
        {
            if (rect.Left < 0 || rect.Top < 0 || rect.Right > client.Right || rect.Bottom > client.Bottom + 1)
            {
                yield return $"{label}: control {id} leaves the client area ({rect.Left},{rect.Top},{rect.Right},{rect.Bottom} in {client.Right}x{client.Bottom})";
            }
        }

        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++)
            {
                var a = rects[i].rect;
                var b = rects[j].rect;
                if (Width(a) > 0 && Height(a) > 0 && Width(b) > 0 && Height(b) > 0
                    && a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom)
                {
                    yield return $"{label}: controls {rects[i].id} and {rects[j].id} overlap";
                }
            }
        }
    }

    private static unsafe void ChangeDpi(NativeDialog dialog, uint dpi)
    {
        var current = WindowRect(dialog.Hwnd);
        var factor = dpi / (double)dialog.Dpi;
        var suggested = new Rect
        {
            Left = current.Left,
            Top = current.Top,
            Right = current.Left + (int)(Width(current) * factor),
            Bottom = current.Top + (int)(Height(current) * factor),
        };
        User32.SendMessage(dialog.Hwnd, WindowMessages.WmDpiChanged, (nint)((dpi << 16) | dpi), (nint)(&suggested));
    }

    [Fact]
    public async Task TheLayout_FollowsTheWindowSize_AndTheListGrowsWithIt()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var d = await rig.OpenAsync(ThreeEntries(rig), autoCheck: false);

        var (before, after) = await rig.OnUi(() =>
        {
            var small = Height(ChildRect(d, RecoveryDialog.ListId));
            var frame = WindowRect(d.Hwnd);
            User32.SetWindowPos(d.Hwnd, 0, frame.Left, frame.Top, Width(frame) + 100, Height(frame) + 200,
                WindowMessages.SwpNoMove | WindowMessages.SwpNoZOrder | WindowMessages.SwpNoActivate);
            return (small, Height(ChildRect(d, RecoveryDialog.ListId)));
        });

        Assert.True(after > before, $"the list should grow with the window ({before} -> {after})");
    }

    [Fact]
    public async Task TheDialogIsDark_ThemeIsAppliedOnceToTheListComboAndButtons()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var chrome = new RecordingChrome();

        var d = await rig.OpenAsync(ThreeEntries(rig), autoCheck: false, chrome: chrome);

        await rig.OnUi(() => d.Close());
        var call = Assert.Single(chrome.Calls);
        Assert.Contains(call.Controls, c => c.Class == DialogControlClass.ComboBox);
        Assert.Contains(call.Controls, c => c.Class == DialogControlClass.ListBox);
        Assert.Contains(call.Controls, c => c.Class == DialogControlClass.Button);
    }

    [Fact]
    public async Task OpeningAndClosingRepeatedly_DoesNotLeakGdiOrUserObjects()
    {
        await using var rig = await RecoveryRig.StartAsync();
        var entries = ThreeEntries(rig);
        var first = await rig.OpenAsync(entries);
        await rig.CheckDoneAsync(first);
        await rig.PickAsync(first, 0); // warm-up also paints the selected state (one-time GDI caches of the hand-drawn chrome)
        await rig.OnUi(() => first.Close()); // warm-up: class registration, comctl32 init
        var before = await rig.OnUi(() => (GdiObjects(), UserObjects()));

        for (var i = 0; i < 6; i++)
        {
            var d = await rig.OpenAsync(entries);
            await rig.CheckDoneAsync(d);
            await rig.PickAsync(d, 0);
            await rig.OnUi(() => d.Close());
        }

        var after = await rig.OnUi(() => (GdiObjects(), UserObjects()));
        Assert.True((int)after.Item1 - (int)before.Item1 <= 2, $"GDI grew {before.Item1} -> {after.Item1}");
        Assert.True((int)after.Item2 - (int)before.Item2 <= 2, $"USER grew {before.Item2} -> {after.Item2}");
    }

    [Fact]
    public async Task EverythingTheViewTells_ComesFromTheLocalizer()
    {
        using var scope = TestLocalization.Use(TestLocalization.Vietnamese);
        await using var rig = await RecoveryRig.StartAsync();
        var d = await rig.OpenAsync(ThreeEntries(rig), retry: _ => Task.FromResult(Succeeded()), dismiss: Dismissed);
        await rig.CheckDoneAsync(d);
        await rig.PickAsync(d, 0);

        await rig.OnUi(() =>
        {
            Assert.Equal(Tr.RecoveryTitle, TextOf(d.Hwnd));
            Assert.Equal(Tr.RecoveryHeader, Text(d, RecoveryDialog.HeaderId));
            Assert.Equal(Tr.RecoveryRetry, Text(d, RecoveryDialog.RetryId));
            Assert.Equal(Tr.RecoveryClearAll, Text(d, RecoveryDialog.ClearAllId));
            Assert.Equal(Tr.RecoveryDetailCopyPath, Text(d, RecoveryDialog.CopySourceId));
            Assert.Equal(Tr.CommonClose, Text(d, RecoveryDialog.CloseId));
            d.Close();
        });
        Assert.NotEqual("Retry Move/Copy", Tr.RecoveryRetry); // the Vietnamese catalog is really in use
    }
}
