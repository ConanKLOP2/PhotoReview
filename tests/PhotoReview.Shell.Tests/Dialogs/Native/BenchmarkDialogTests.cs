using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App;
using PhotoReview.App.Presenters;
using PhotoReview.Benchmarking;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Localization;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Tests.Hosting;
using PhotoReview.Shell.Win32.Dialogs;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.Shell.Win32.Hosting;
using PhotoReview.TestSupport;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.Native;

/// <summary>
/// WP-35 GĐ2: cửa sổ Benchmark MODELESS trên UI thread thật của shell (<see cref="UiThread"/>: dispatcher + vòng lặp + bối cảnh đồng
/// bộ như production) với phiên đo giả. Presenter đã có test riêng (<c>BenchmarkPresenterTests</c>); ở đây kiểm VIEW: không chặn,
/// chép giá trị vào điều khiển, khoá điều khiển khi chạy, Esc/Close/X giữa chừng, sắp xếp + tô hàng, Tab qua vòng lặp shell, DPI,
/// ngôn ngữ, rò rỉ. Không Sleep/Delay, không so ManagedThreadId.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "UI")]
public sealed class BenchmarkDialogTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private sealed class FakeSession(BenchmarkProfile profile, Func<FakeSession, CancellationToken, Task<BenchmarkReport>> run)
        : IBenchmarkProfileSession
    {
        public BenchmarkProfile Profile { get; } = profile;

        public IProgress<BenchmarkProgress>? Progress { get; private set; }

        public CancellationToken Token { get; private set; }

        public bool Disposed { get; private set; }

        public Task<BenchmarkReport> RunAsync(string folder, IProgress<BenchmarkProgress> progress, CancellationToken cancellationToken)
        {
            Progress = progress;
            Token = cancellationToken;
            return run(this, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return default;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<string> _tempFiles = [];

        public UiThread Ui { get; private set; } = null!;

        public nint Owner { get; private set; }

        public BenchmarkDialog? Dialog { get; private set; }

        public List<FakeSession> Sessions { get; } = [];

        public List<string> Explorer { get; } = [];

        public List<string> Saved { get; } = [];

        public string[] Files { get; set; } = ["a.jpg", "b.jpg"];

        public Dictionary<string, Func<FakeSession, CancellationToken, Task<BenchmarkReport>>> Behaviors { get; } = [];

        public Func<nint, string, string?, string?> Pick { get; set; } = (_, _, _) => null;

        public List<(nint Owner, string Title, string? Initial)> PickCalls { get; } = [];

        public string SummaryPath { get; private set; } = string.Empty;

        public static async Task<Rig> StartAsync()
        {
            var rig = new Rig { Ui = await UiThread.StartAsync() };
            rig.Owner = await rig.Ui.InvokeAsync(() => CreateOwner());
            var dir = Path.Combine(Path.GetTempPath(), "wp35g2-bench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            rig.SummaryPath = Path.Combine(dir, "summary.json");
            File.WriteAllText(rig.SummaryPath, "{}");
            rig._tempFiles.Add(dir);
            return rig;
        }

        public Task<BenchmarkDialog> OpenAsync(string? folder = @"C:\bench", INativeDialogChrome? chrome = null) => Ui.InvokeAsync(() =>
        {
            var dialog = new BenchmarkDialog(folder, Ui.Dispatcher,
                (profile, files) =>
                {
                    var behavior = Behaviors.TryGetValue(profile.Id, out var b) ? b : (_, _) => Task.FromResult(Report(profile.Id, [10, 20]));
                    var session = new FakeSession(profile, behavior);
                    lock (Sessions)
                    {
                        Sessions.Add(session);
                    }

                    return session;
                },
                (owner, title, initial) =>
                {
                    PickCalls.Add((owner, title, initial));
                    return Pick(owner, title, initial);
                },
                Explorer.Add,
                (_, _, _) => Files,
                reports =>
                {
                    Saved.Add(string.Join(",", reports.Select(r => r.Phases[0].ProfileId)));
                    return Task.FromResult(SummaryPath);
                },
                chrome);
            Dialog = dialog;
            dialog.ShowModeless(Owner, Ui.Loop);
            return dialog;
        });

        public Task<T> OnUi<T>(Func<T> func) => Ui.InvokeAsync(func);

        public Task OnUi(Action action) => Ui.InvokeAsync(action);

        /// <summary>Hết mọi việc dispatcher đã xếp hàng trước lời gọi này (tiến độ post từ luồng nền).</summary>
        public Task FlushAsync() => Ui.InvokeAsync(() => { });

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Ui.InvokeAsync(() =>
                {
                    Dialog?.Close();
                    DestroyOwner(Owner);
                });
            }
            finally
            {
                await Ui.DisposeAsync();
                foreach (var path in _tempFiles)
                {
                    try
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
    }

    private static BenchmarkReport Report(string profileId, double[] samples, BenchmarkResultStatus status = BenchmarkResultStatus.Pass,
        BenchmarkWorkload workload = BenchmarkWorkload.Sequential)
        => new("run-" + profileId, DateTimeOffset.UnixEpoch, "C:\\bench", [new BenchmarkPhaseResult(profileId, workload, samples, status)]);

    private static Task<BenchmarkReport> UntilCancelled(CancellationToken token)
    {
        var tcs = new TaskCompletionSource<BenchmarkReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => tcs.TrySetCanceled(token));
        return tcs.Task;
    }

    private static void SelectOnly(BenchmarkDialog d, params string[] profileIds)
    {
        var list = Item(d, BenchmarkDialog.ProfilesId);
        ListBoxControl.SelectAll(list, false);
        for (var i = 0; i < d.Presenter.ProfileItems.Count; i++)
        {
            if (profileIds.Contains(d.Presenter.ProfileItems[i].Profile.Id))
            {
                ListBoxControl.SetSelected(list, i, true);
            }
        }
    }

    private static bool Enabled(BenchmarkDialog d, int id) => User32.IsWindowEnabled(Item(d, id));

    // ---- mở: modeless, giá trị đầu ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Opens_WithoutBlocking_AndWithTheInitialValues()
    {
        await using var rig = await Rig.StartAsync();

        var d = await rig.OpenAsync(@"C:\bench");

        await rig.OnUi(() =>
        {
            Assert.False(d.IsClosed);
            Assert.True(User32.IsWindowVisible(d.Hwnd));
            Assert.True(User32.IsWindowEnabled(rig.Owner), "modeless: the owner is not disabled");
            Assert.Equal(Tr.BenchmarkWindowTitle, TextOf(d.Hwnd));
            Assert.Equal(@"C:\bench", TextOf(d, BenchmarkDialog.FolderId));
            Assert.Equal("64", TextOf(d, BenchmarkDialog.LimitId));
            var list = Item(d, BenchmarkDialog.ProfilesId);
            Assert.Equal(BenchmarkProfiles.All.Count, ListBoxControl.Count(list));
            var selected = ListBoxControl.SelectedIndices(list);
            Assert.Equal([d.Presenter.DefaultSelection.Profile.Id], selected.Select(i => d.Presenter.ProfileItems[i].Profile.Id));
            Assert.True(Enabled(d, BenchmarkDialog.RunId));
            Assert.True(Enabled(d, BenchmarkDialog.QuickCheckId));
            Assert.False(Enabled(d, BenchmarkDialog.CancelRunId));
            Assert.Equal(BenchmarkDialog.ColumnCount, (int)User32.SendMessage(
                ListViewControl.Header(Item(d, BenchmarkDialog.ResultsId)), 0x1200, 0, 0));
            // The default button (Enter) is Run; DM_GETDEFID: HIWORD = DC_HASDEFID (0x534B), LOWORD = id.
            var def = (uint)User32.SendMessage(d.Hwnd, DialogConstants.DmGetDefId, 0, 0);
            Assert.Equal(0x534Bu, def >> 16);
            Assert.Equal(BenchmarkDialog.RunId, (int)(def & 0xFFFF));
        });
    }

    [Fact]
    public async Task WithoutAFolder_TheFolderBoxStartsAtPictures()
    {
        await using var rig = await Rig.StartAsync();

        var d = await rig.OpenAsync(folder: null);

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            await rig.OnUi(() => TextOf(d, BenchmarkDialog.FolderId)));
    }

    [Fact]
    public async Task EveryProfileHasATooltipWithItsDescription_AndABadgeWithItsLoadingMode()
    {
        await using var rig = await Rig.StartAsync();
        var d = await rig.OpenAsync();

        await rig.OnUi(() =>
        {
            for (var i = 0; i < d.Presenter.ProfileItems.Count; i++)
            {
                Assert.Equal(d.Presenter.ProfileItems[i].Description, d.ItemTooltipText(BenchmarkDialog.ProfilesId, i));
            }

            Assert.Equal(BenchmarkText.LoadingModeText(d.Presenter.ProfileItems[0].Profile.LoadingMode), d.ProfileBadge(0));
        });
    }

    // ---- chạy ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RunButton_RunsTheSelectedProfiles_ShowsProgress_LocksTheControls_ThenListsRows()
    {
        await using var rig = await Rig.StartAsync();
        var reported = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        rig.Behaviors["preview-light"] = async (s, _) =>
        {
            s.Progress!.Report(new BenchmarkProgress("preview-light", BenchmarkWorkload.Sequential, 3, 4, BenchmarkEngine.ProgressOk));
            reported.SetResult();
            await release.Task;
            return Report("preview-light", [10, 20, 30]);
        };
        var d = await rig.OpenAsync();
        await rig.OnUi(() =>
        {
            SelectOnly(d, "preview-light");
            Command(d, BenchmarkDialog.RunId);
        });
        await reported.Task.WaitAsync(Bound);
        await rig.FlushAsync(); // the progress was posted to the dispatcher before 'reported' was set

        await rig.OnUi(() =>
        {
            Assert.Equal(75, (int)Math.Round(ProgressControl.Fraction(Item(d, BenchmarkDialog.ProgressId)) * 100));
            Assert.Equal(Tr.BenchmarkStatusProgress(1, 1, "preview-light", 3, 4, Tr.BenchmarkProgressOk), TextOf(d, BenchmarkDialog.StatusId));
            Assert.False(Enabled(d, BenchmarkDialog.RunId));
            Assert.False(Enabled(d, BenchmarkDialog.QuickCheckId));
            Assert.False(Enabled(d, BenchmarkDialog.BrowseId));
            Assert.False(Enabled(d, BenchmarkDialog.ProfilesId));
            Assert.False(Enabled(d, BenchmarkDialog.LimitId));
            Assert.False(Enabled(d, BenchmarkDialog.FolderId));
            Assert.True(Enabled(d, BenchmarkDialog.CancelRunId));
            Assert.Equal(Item(d, BenchmarkDialog.CancelRunId), User32.GetFocus()); // focus never sits on a disabled control
        });

        release.SetResult();
        await (await rig.OnUi(() => d.LastRunTask!)).WaitAsync(Bound);
        await rig.FlushAsync();

        await rig.OnUi(() =>
        {
            var results = Item(d, BenchmarkDialog.ResultsId);
            Assert.Equal(1, ListViewControl.Count(results));
            Assert.Equal("Preview Light", ListViewControl.CellText(results, 0, 1));
            Assert.Equal(BenchmarkResultRow.BestSymbol, ListViewControl.CellText(results, 0, 0)); // the only row is the best one
            Assert.Equal(d.Presenter.Rows[0].P95Text, ListViewControl.CellText(results, 0, 6));
            Assert.Equal(Tr.BenchmarkStatusDone(1, 1, rig.SummaryPath), TextOf(d, BenchmarkDialog.StatusId));
            Assert.True(Enabled(d, BenchmarkDialog.RunId));
            Assert.False(Enabled(d, BenchmarkDialog.CancelRunId));
            Assert.True(Enabled(d, BenchmarkDialog.ProfilesId));
        });
        Assert.Equal(["preview-light"], rig.Sessions.Select(s => s.Profile.Id));
        Assert.Equal(["preview-light"], rig.Saved);
    }

    [Fact]
    public async Task ASecondRun_ClearsTheOldRows_BeforeItsFirstResult()
    {
        await using var rig = await Rig.StartAsync();
        var d = await rig.OpenAsync();
        await RunTwoProfilesAsync(rig, d);
        var firstRows = await rig.OnUi(() => ListViewControl.Count(Item(d, BenchmarkDialog.ResultsId)));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource();
        rig.Behaviors["preview-light"] = async (_, _) =>
        {
            started.SetResult();
            await release.Task;
            return Report("preview-light", [1]);
        };

        await rig.OnUi(() =>
        {
            SelectOnly(d, "preview-light");
            Command(d, BenchmarkDialog.RunId);
        });
        await started.Task.WaitAsync(Bound);
        var during = await rig.OnUi(() => ListViewControl.Count(Item(d, BenchmarkDialog.ResultsId)));
        release.SetResult();
        await (await rig.OnUi(() => d.LastRunTask!)).WaitAsync(Bound);
        await rig.FlushAsync();

        Assert.Equal(3, firstRows);
        Assert.Equal(0, during);
        Assert.Equal(1, await rig.OnUi(() => ListViewControl.Count(Item(d, BenchmarkDialog.ResultsId))));
    }

    [Fact]
    public async Task QuickCheck_RunsTheFixedProfile_WhateverIsSelected()
    {
        await using var rig = await Rig.StartAsync();
        var d = await rig.OpenAsync();
        await rig.OnUi(() =>
        {
            SelectOnly(d, "preview-light", "fast-balanced");
            Command(d, BenchmarkDialog.QuickCheckId);
        });

        await (await rig.OnUi(() => d.LastRunTask!)).WaitAsync(Bound);

        Assert.Equal([BenchmarkPresenter.QuickCheckProfileId], rig.Sessions.Select(s => s.Profile.Id));
        Assert.Equal(BenchmarkPresenter.QuickCheckIterations, rig.Sessions[0].Profile.Iterations);
    }

    [Fact]
    public async Task Run_WithNothingSelected_SaysSo_AndStartsNothing()
    {
        await using var rig = await Rig.StartAsync();
        var d = await rig.OpenAsync();

        await rig.OnUi(() =>
        {
            SelectOnly(d);
            Command(d, BenchmarkDialog.RunId);
        });
        await (await rig.OnUi(() => d.LastRunTask!)).WaitAsync(Bound);

        Assert.Equal(Tr.BenchmarkStatusNoProfileSelected, await rig.OnUi(() => TextOf(d, BenchmarkDialog.StatusId)));
        Assert.Empty(rig.Sessions);
    }

    [Fact]
    public async Task SelectAllAndSelectNone_ToggleEveryProfile()
    {
        await using var rig = await Rig.StartAsync();
        var d = await rig.OpenAsync();

        var (all, none) = await rig.OnUi(() =>
        {
            var list = Item(d, BenchmarkDialog.ProfilesId);
            Command(d, BenchmarkDialog.SelectAllId);
            var selectedAll = ListBoxControl.SelectedIndices(list).Count;
            Command(d, BenchmarkDialog.SelectNoneId);
            return (selectedAll, ListBoxControl.SelectedIndices(list).Count);
        });

        Assert.Equal(BenchmarkProfiles.All.Count, all);
        Assert.Equal(0, none);
    }

    // ---- huỷ / đóng giữa chừng ----------------------------------------------------------------------------------------------

    private static async Task<(BenchmarkDialog Dialog, FakeSession Session)> StartLongRunAsync(Rig rig)
    {
        var started = new TaskCompletionSource<FakeSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Behaviors["preview-light"] = (s, ct) =>
        {
            started.TrySetResult(s);
            return UntilCancelled(ct);
        };
        var d = await rig.OpenAsync();
        await rig.OnUi(() =>
        {
            SelectOnly(d, "preview-light");
            Command(d, BenchmarkDialog.RunId);
        });
        var session = await started.Task.WaitAsync(Bound);
        return (d, session);
    }

    [Fact]
    public async Task CancelButton_StopsTheRun_AndTheWindowStaysOpen()
    {
        await using var rig = await Rig.StartAsync();
        var (d, session) = await StartLongRunAsync(rig);

        await rig.OnUi(() => Command(d, BenchmarkDialog.CancelRunId));
        await (await rig.OnUi(() => d.LastRunTask!)).WaitAsync(Bound);
        await rig.FlushAsync();

        await rig.OnUi(() =>
        {
            Assert.False(d.IsClosed);
            Assert.Equal(Tr.BenchmarkStatusCanceled, TextOf(d, BenchmarkDialog.StatusId));
            Assert.True(Enabled(d, BenchmarkDialog.RunId));
            Assert.False(Enabled(d, BenchmarkDialog.CancelRunId));
        });
        Assert.True(session.Token.IsCancellationRequested);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task Escape_WhileRunning_CancelsTheRun_ButDoesNotCloseTheWindow()
    {
        await using var rig = await Rig.StartAsync();
        var (d, session) = await StartLongRunAsync(rig);

        await rig.OnUi(() =>
        {
            Key(d, Item(d, BenchmarkDialog.FolderId), VkEscape);
            Drain(d);
        });
        await (await rig.OnUi(() => d.LastRunTask!)).WaitAsync(Bound);

        Assert.True(session.Token.IsCancellationRequested);
        Assert.False(await rig.OnUi(() => d.IsClosed));
    }

    [Fact]
    public async Task Escape_WhenIdle_ClosesTheWindow()
    {
        await using var rig = await Rig.StartAsync();
        var d = await rig.OpenAsync();

        await rig.OnUi(() =>
        {
            Key(d, Item(d, BenchmarkDialog.FolderId), VkEscape);
            Drain(d);
        });

        Assert.True(await rig.OnUi(() => d.IsClosed));
        Assert.False(await rig.OnUi(() => User32.IsWindow(d.Hwnd)));
    }

    [Theory]
    [InlineData("button")]
    [InlineData("wmclose")]
    [InlineData("api")]
    public async Task Closing_MidRun_CancelsTheRun_DestroysTheWindow_AndLateProgressIsIgnored(string how)
    {
        await using var rig = await Rig.StartAsync();
        var (d, session) = await StartLongRunAsync(rig);
        var hwnd = d.Hwnd;
        var presenter = d.Presenter;

        await rig.OnUi(() =>
        {
            switch (how)
            {
                case "button":
                    Command(d, BenchmarkDialog.CloseId);
                    break;
                case "wmclose":
                    User32.SendMessage(d.Hwnd, WindowMessages.WmClose, 0, 0);
                    break;
                default:
                    d.Close();
                    break;
            }
        });
        // Progress that arrives after the window is gone must be dropped, not applied to a dead window.
        session.Progress!.Report(new BenchmarkProgress("preview-light", BenchmarkWorkload.Sequential, 1, 2, "late"));
        await (await rig.OnUi(() => d.LastRunTask!)).WaitAsync(Bound);
        await rig.FlushAsync();

        Assert.True(await rig.OnUi(() => d.IsClosed));
        Assert.False(await rig.OnUi(() => User32.IsWindow(hwnd)));
        Assert.True(session.Token.IsCancellationRequested);
        Assert.True(session.Disposed);
        Assert.False(presenter.IsRunning);
        Assert.Empty(presenter.Rows); // cancelled, never a bogus Fail row
    }

    // ---- Tab / phím qua vòng lặp shell -----------------------------------------------------------------------------------------

    private sealed class MarkerFilter(TaskCompletionSource seen) : IMessageFilter
    {
        public bool PreFilterMessage(in WindowMessage message)
        {
            if (message.Msg == WindowMessages.WmApp + 0x21)
            {
                seen.TrySetResult();
            }

            return false;
        }
    }

    [Fact]
    public async Task TabKey_PostedToTheWindow_IsHandledByTheDialogManagerInsideTheShellLoop()
    {
        await using var rig = await Rig.StartAsync();
        var d = await rig.OpenAsync();
        var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await rig.OnUi(() =>
        {
            rig.Ui.Loop.AddMessageFilter(new MarkerFilter(marker));
            var folder = Item(d, BenchmarkDialog.FolderId);
            User32.SetFocus(folder);
            // Queued like real input: key first, marker after. The loop pumps them in order, so when the marker is seen the key was
            // already given to the modeless dialog's IsDialogMessage filter (it is NOT dispatched to the window procedure).
            User32.PostMessage(folder, WindowMessages.WmKeyDown, (nint)VkTab, 1);
            User32.PostMessage(rig.Owner, WindowMessages.WmApp + 0x21, 0, 0);
        });
        await marker.Task.WaitAsync(Bound);

        var focus = await rig.OnUi(() => User32.GetFocus());
        Assert.Equal(Item(d, BenchmarkDialog.BrowseId), focus);
    }

    // ---- bảng: sắp xếp + tô hàng ----------------------------------------------------------------------------------------------

    private static async Task RunTwoProfilesAsync(Rig rig, BenchmarkDialog d)
    {
        rig.Behaviors["fast-sequential"] = (_, _) => Task.FromResult(Report("fast-sequential", [30, 40, 50]));
        rig.Behaviors["preview-light"] = (_, _) => Task.FromResult(Report("preview-light", [5, 6]));
        rig.Behaviors["original-correctness"] = (_, _) => Task.FromResult(
            Report("original-correctness", [], BenchmarkResultStatus.Fail, BenchmarkWorkload.Correctness));
        await rig.OnUi(() =>
        {
            SelectOnly(d, "fast-sequential", "preview-light", "original-correctness");
            Command(d, BenchmarkDialog.RunId);
        });
        await (await rig.OnUi(() => d.LastRunTask!)).WaitAsync(Bound);
        await rig.FlushAsync();
    }

    private static string[] Column(BenchmarkDialog d, int column)
    {
        var results = Item(d, BenchmarkDialog.ResultsId);
        return Enumerable.Range(0, ListViewControl.Count(results)).Select(i => ListViewControl.CellText(results, i, column)).ToArray();
    }

    [Fact]
    public async Task Rows_AreListedInProfileOrder_AndTheBestOfASharedWorkloadIsMarked()
    {
        await using var rig = await Rig.StartAsync();
        var d = await rig.OpenAsync();

        await RunTwoProfilesAsync(rig, d);

        await rig.OnUi(() =>
        {
            // The profile list order is the catalog order (fast-sequential, preview-light, original-correctness).
            Assert.Equal(3, ListViewControl.Count(Item(d, BenchmarkDialog.ResultsId)));
            var profiles = Column(d, 1);
            Assert.Equal(d.Presenter.Rows.Select(r => r.ProfileName), profiles);
            var markers = Column(d, 0);
            var best = d.Presenter.Rows.Select(r => r.IsBest ? BenchmarkResultRow.BestSymbol : string.Empty).ToArray();
            Assert.Equal(best, markers);
            Assert.Contains(BenchmarkResultRow.BestSymbol, markers);
        });
    }

    [Fact]
    public async Task ClickingAColumnHeader_SortsAscendingThenDescending_NumericColumnsByValue()
    {
        await using var rig = await Rig.StartAsync();
        var d = await rig.OpenAsync();
        await RunTwoProfilesAsync(rig, d);

        var (asc, desc, third) = await rig.OnUi(() =>
        {
            var results = Item(d, BenchmarkDialog.ResultsId);
            void Click(int column)
            {
                var nm = new byte[ControlConstants.NmListViewSize];
                unsafe
                {
                    fixed (byte* p = nm)
                    {
                        *(nint*)p = results;
                        *(nint*)(p + 8) = BenchmarkDialog.ResultsId;
                        *(int*)(p + 16) = ControlConstants.LvnColumnClick;
                        *(int*)(p + 28) = column;
                        User32.SendMessage(d.Hwnd, ControlConstants.WmNotify, BenchmarkDialog.ResultsId, (nint)p);
                    }
                }
            }

            Click(6); // P95
            var first = Column(d, 6).Select(Parse).ToArray();
            Click(6);
            var second = Column(d, 6).Select(Parse).ToArray();
            Click(1);
            var byProfile = Column(d, 1);
            Assert.Equal((1, 1), d.Sort);
            return (first, second, byProfile);
        });

        Assert.Equal(asc.OrderBy(v => v), asc);
        Assert.Equal(desc.OrderByDescending(v => v), desc);
        Assert.Equal(third.OrderBy(v => v, StringComparer.CurrentCulture), third);
    }

    private static double Parse(string text)
    {
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? -1 : double.Parse(digits, CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task SortingKeepsTheSelectedRow()
    {
        await using var rig = await Rig.StartAsync();
        var d = await rig.OpenAsync();
        await RunTwoProfilesAsync(rig, d);

        var (before, after) = await rig.OnUi(() =>
        {
            var results = Item(d, BenchmarkDialog.ResultsId);
            ListViewControl.SelectRow(results, 0);
            var name = ListViewControl.CellText(results, 0, 1);
            d.SortBy(1);
            d.SortBy(1); // descending: the first row moves
            var row = ListViewControl.SelectedRow(results);
            return (name, ListViewControl.CellText(results, row, 1));
        });

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task RowColours_BestIsGreen_FailIsRed_OthersAlternate()
    {
        await using var rig = await Rig.StartAsync();
        var d = await rig.OpenAsync();
        await RunTwoProfilesAsync(rig, d);

        await rig.OnUi(() =>
        {
            for (var source = 0; source < d.Presenter.Rows.Count; source++)
            {
                var row = d.Presenter.Rows[source];
                var color = CustomDrawBackColor(d, displayedRow: source, param: source);
                var expected = row.IsBest ? "Dark.SuccessBackground"
                    : row.Status == BenchmarkResultStatus.Fail ? "Dark.ErrorBackground"
                    : source % 2 == 0 ? "Dark.Panel" : "Dark.RowAlt";
                Assert.Equal(DialogColors.FromKey(expected), color);
            }
        });
    }

    private static unsafe uint CustomDrawBackColor(BenchmarkDialog d, int displayedRow, int param)
    {
        var buffer = new byte[ControlConstants.NmLvCustomDrawSize];
        fixed (byte* p = buffer)
        {
            *(nint*)p = Item(d, BenchmarkDialog.ResultsId);
            *(nint*)(p + 8) = BenchmarkDialog.ResultsId;
            *(int*)(p + 16) = ControlConstants.NmCustomDraw;
            *(uint*)(p + 24) = ControlConstants.CddsItemPrePaint;
            *(nint*)(p + 56) = displayedRow;
            *(nint*)(p + 72) = param;
            User32.SendMessage(d.Hwnd, ControlConstants.WmNotify, BenchmarkDialog.ResultsId, (nint)p);
            return *(uint*)(p + 84);
        }
    }

    // ---- Browse / Explorer -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Browse_AsksTheWindowsOwnPicker_AndFillsTheFolderBox()
    {
        await using var rig = await Rig.StartAsync();
        rig.Pick = (_, _, _) => @"D:\photos\chosen";
        var d = await rig.OpenAsync();

        var text = await rig.OnUi(() =>
        {
            Command(d, BenchmarkDialog.BrowseId);
            return TextOf(d, BenchmarkDialog.FolderId);
        });

        Assert.Equal(@"D:\photos\chosen", text);
        var call = Assert.Single(rig.PickCalls);
        Assert.Equal(d.Hwnd, call.Owner);
        Assert.Equal(Tr.BenchmarkPickFolderTitle, call.Title);
    }

    [Fact]
    public async Task Browse_Cancelled_LeavesTheFolderBoxAlone()
    {
        await using var rig = await Rig.StartAsync();
        rig.Pick = (_, _, _) => null;
        var d = await rig.OpenAsync(@"C:\keep");

        var text = await rig.OnUi(() =>
        {
            Command(d, BenchmarkDialog.BrowseId);
            return TextOf(d, BenchmarkDialog.FolderId);
        });

        Assert.Equal(@"C:\keep", text);
    }

    [Fact]
    public async Task OpenReportAndOpenFolder_UseTheLastSummary()
    {
        await using var rig = await Rig.StartAsync();
        var d = await rig.OpenAsync();
        await RunTwoProfilesAsync(rig, d);

        await rig.OnUi(() =>
        {
            Command(d, BenchmarkDialog.OpenReportId);
            Command(d, BenchmarkDialog.OpenFolderId);
        });

        Assert.Equal([$"/select,\"{rig.SummaryPath}\"", Path.GetDirectoryName(rig.SummaryPath)!], rig.Explorer);
    }

    // ---- ngôn ngữ ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SwitchingLanguage_WhileOpen_RetranslatesTheWindow()
    {
        await using var rig = await Rig.StartAsync();
        var d = await rig.OpenAsync();
        using var english = TestLocalization.Use(TestLocalization.English);
        await rig.FlushAsync();
        var before = await rig.OnUi(() => TextOf(d, BenchmarkDialog.RunId));

        using (TestLocalization.Use(TestLocalization.Vietnamese))
        {
            await rig.FlushAsync(); // the change handler posted its refresh to the UI thread
            var (after, title) = await rig.OnUi(() => (TextOf(d, BenchmarkDialog.RunId), TextOf(d.Hwnd)));

            Assert.NotEqual(before, after);
            Assert.Equal(Tr.BenchmarkRun, after);
            Assert.Equal(Tr.BenchmarkWindowTitle, title);
        }
    }

    // ---- bố cục + DPI + rò rỉ ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Layout_AtTwoScales_NothingOverlapsOrLeavesTheClient_EvenWithALongStatusLine()
    {
        await using var rig = await Rig.StartAsync();
        rig.Behaviors["preview-light"] = (s, _) =>
        {
            s.Progress!.Report(new BenchmarkProgress("preview-light", BenchmarkWorkload.Sequential, 1, 4,
                string.Join(' ', Enumerable.Repeat("a long progress message", 12))));
            return Task.FromResult(Report("preview-light", [10]));
        };
        var d = await rig.OpenAsync();
        await rig.OnUi(() =>
        {
            SelectOnly(d, "preview-light");
            Command(d, BenchmarkDialog.RunId);
        });
        await (await rig.OnUi(() => d.LastRunTask!)).WaitAsync(Bound);
        await rig.FlushAsync();

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

    private static IEnumerable<string> Problems(BenchmarkDialog d, string label)
    {
        int[] ids =
        [
            BenchmarkDialog.HeaderId, BenchmarkDialog.DescriptionId, BenchmarkDialog.FolderId, BenchmarkDialog.BrowseId,
            BenchmarkDialog.LimitLabelId, BenchmarkDialog.LimitId, BenchmarkDialog.ProfilesLabelId, BenchmarkDialog.SelectAllId,
            BenchmarkDialog.SelectNoneId, BenchmarkDialog.ProfilesId, BenchmarkDialog.RunId, BenchmarkDialog.QuickCheckId,
            BenchmarkDialog.CancelRunId, BenchmarkDialog.ProgressId, BenchmarkDialog.StatusId, BenchmarkDialog.ResultsId,
            BenchmarkDialog.OpenReportId, BenchmarkDialog.OpenFolderId, BenchmarkDialog.CloseId,
        ];
        var client = ClientRect(d.Hwnd);
        var rects = ids.Select(id => (id, rect: ChildRect(d, id))).ToList();
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
    public async Task OpeningAndClosingRepeatedly_DoesNotLeakGdiOrUserObjects()
    {
        await using var rig = await Rig.StartAsync();
        var first = await rig.OpenAsync();
        await rig.OnUi(() => first.Close()); // warm-up: class registration, comctl32 init
        var before = await rig.OnUi(() => (GdiObjects(), UserObjects()));

        for (var i = 0; i < 6; i++)
        {
            var d = await rig.OpenAsync();
            await rig.OnUi(() => d.Close());
        }

        var after = await rig.OnUi(() => (GdiObjects(), UserObjects()));
        Assert.True((int)after.Item1 - (int)before.Item1 <= 2, $"GDI grew {before.Item1} -> {after.Item1}");
        Assert.True((int)after.Item2 - (int)before.Item2 <= 2, $"USER grew {before.Item2} -> {after.Item2}");
    }

    // ---- cổng Win32: một cửa sổ duy nhất, đóng cùng shell ------------------------------------------------------------------------------

    private static ServiceProvider Services(UiThread ui)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IUiScheduler>(ui.Dispatcher);
        services.AddSingleton(new ReviewMetrics());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Port_OpensOneBenchmarkWindow_SecondOpenBringsItForward_ReopensAfterClose()
    {
        await using var ui = await UiThread.StartAsync();
        using var services = Services(ui);
        var owner = await ui.InvokeAsync(() => CreateOwner());
        var port = await ui.InvokeAsync(() => new Win32SecondaryWindowPort(services, null, null, ui.Loop));

        var (opened, again, same, reopened) = await ui.InvokeAsync(() =>
        {
            port.ShowBenchmark(owner, @"C:\bench");
            var firstOpen = port.IsBenchmarkOpen;
            var windows = CountWindows();
            port.ShowBenchmark(owner, @"C:\other");
            var sameCount = CountWindows() == windows;
            // Close it (as the user would) and open again.
            var hwnd = FindBenchmarkWindow();
            User32.SendMessage(hwnd, WindowMessages.WmClose, 0, 0);
            var closed = !port.IsBenchmarkOpen;
            port.ShowBenchmark(owner, null);
            return (firstOpen, closed, sameCount, port.IsBenchmarkOpen);
        });

        Assert.True(opened);
        Assert.True(again);
        Assert.True(same, "a second open must not create a second window");
        Assert.True(reopened);
        await ui.InvokeAsync(() =>
        {
            port.Dispose();
            DestroyOwner(owner);
        });
    }

    private static int CountWindows() => EnumerateBenchmarkWindows().Count;

    private static nint FindBenchmarkWindow() => EnumerateBenchmarkWindows().Single();

    private static List<nint> EnumerateBenchmarkWindows()
    {
        var title = Tr.BenchmarkWindowTitle;
        var found = new List<nint>();
        var thread = GetCurrentThreadId();
        EnumThreadWindows(thread, (hwnd, _) =>
        {
            if (User32.IsWindowVisible(hwnd) && TextOf(hwnd) == title)
            {
                found.Add(hwnd);
            }

            return true;
        }, 0);
        return found;
    }

    private delegate bool EnumProc(nint hwnd, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumThreadWindows(uint threadId, EnumProc callback, nint lParam);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [Fact]
    public async Task Port_Dispose_WhileARunIsActive_ClosesTheWindow_CancelsTheRun_AndDoesNotHang()
    {
        await using var ui = await UiThread.StartAsync();
        using var services = Services(ui);
        var started = new TaskCompletionSource<FakeSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seams = new BenchmarkDialogSeams(
            Sessions: (profile, _) => new FakeSession(profile, (s, ct) =>
            {
                started.TrySetResult(s);
                return UntilCancelled(ct);
            }),
            Scan: (_, _, _) => ["a.jpg"],
            Save: _ => Task.FromResult("summary.json"));
        var owner = await ui.InvokeAsync(() => CreateOwner());
        var port = await ui.InvokeAsync(() => new Win32SecondaryWindowPort(services, null, null, ui.Loop, benchmark: seams));
        await ui.InvokeAsync(() =>
        {
            port.ShowBenchmark(owner, @"C:\bench");
            var hwnd = FindBenchmarkWindow();
            User32.SendMessage(hwnd, WindowMessages.WmCommand, BenchmarkDialog.RunId, 0);
        });
        var session = await started.Task.WaitAsync(Bound);

        await ui.InvokeAsync(() => port.Dispose());

        Assert.True(session.Token.IsCancellationRequested);
        Assert.False(await ui.InvokeAsync(() => port.IsBenchmarkOpen));
        Assert.Equal(0, await ui.InvokeAsync(() => CountWindows()));
        await ui.InvokeAsync(() => DestroyOwner(owner));
    }

    [Fact]
    public async Task Port_ActionProfiles_ReturnsTheEditedList_OnApply_AndNullOnCancel()
    {
        await using var ui = await UiThread.StartAsync();
        using var services = Services(ui);
        var owner = await ui.InvokeAsync(() => CreateOwner());
        var actions = new List<PhotoReview.Core.Settings.ReviewAction>
        {
            new() { Name = "Keep", Shortcut = "F6", Operation = PhotoReview.Core.Model.FileOperationType.Move, Destination = "Keep" },
        };
        IReadOnlyList<PhotoReview.Core.Settings.ReviewAction>? applied = null;
        IReadOnlyList<PhotoReview.Core.Settings.ReviewAction>? cancelled = [];
        var applyPort = await ui.InvokeAsync(() => new Win32SecondaryWindowPort(services, null, d =>
        {
            SetText(d, ActionProfilesDialog.NameId, "Renamed");
            Command(d, NativeDialog.IdOk);
        }, ui.Loop));
        applied = await ui.InvokeAsync(() => applyPort.ShowActionProfiles(owner, actions));
        var cancelPort = await ui.InvokeAsync(() => new Win32SecondaryWindowPort(services, null, d => d.Close(), ui.Loop));
        cancelled = await ui.InvokeAsync(() => cancelPort.ShowActionProfiles(owner, actions));

        Assert.Equal(["Renamed"], applied!.Select(a => a.Name));
        Assert.Equal("Keep", actions[0].Name); // the input list is never edited in place
        Assert.Null(cancelled);
        Assert.True(applyPort.Supports(SecondaryWindowKind.ActionProfiles));
        Assert.True(applyPort.Supports(SecondaryWindowKind.Benchmark));
        await ui.InvokeAsync(() => DestroyOwner(owner));
    }
}
