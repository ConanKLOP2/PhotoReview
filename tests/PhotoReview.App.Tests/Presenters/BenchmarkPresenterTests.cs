using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using PhotoReview.App.Presenters;
using PhotoReview.Benchmarking;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.TestSupport;

namespace PhotoReview.App.Tests.Presenters;

/// <summary>
/// WP-35 G2a: the Benchmark window's run / cancel / close behaviour without a window. A fake view records every call,
/// a queued scheduler stands in for the UI thread (progress is "posted from a background thread" and pumped by the test),
/// and gated fake sessions stand in for the image executor -- no sleeps, no thread-id checks.
/// </summary>
[Collection("GlobalState")]
public sealed class BenchmarkPresenterTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    // ---- Fakes ----

    private sealed class FakeView : IBenchmarkView
    {
        public string? Status;
        public double Progress;
        public bool Running;
        public int Refreshes;
        public readonly List<bool> RunningHistory = [];
        public readonly List<string> Statuses = [];
        public readonly List<string> Explorer = [];

        public void SetStatus(string text) { Status = text; Statuses.Add(text); }
        public void SetProgress(double fraction) => Progress = fraction;
        public void SetRunning(bool running) { Running = running; RunningHistory.Add(running); }
        public void RefreshRows() => Refreshes++;
        public void OpenInExplorer(string arguments) => Explorer.Add(arguments);
    }

    /// <summary>The UI thread: posted actions wait in a queue until the test pumps them.</summary>
    private sealed class QueueUi : IUiScheduler
    {
        private readonly Queue<Action> _queue = new();
        public void Post(Action action) { lock (_queue) _queue.Enqueue(action); }
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public ValueTask YieldAsync(CancellationToken cancellationToken = default) => default;

        public void Pump()
        {
            while (true)
            {
                Action? next;
                lock (_queue) next = _queue.Count > 0 ? _queue.Dequeue() : null;
                if (next is null) return;
                next();
            }
        }
    }

    private sealed class FakeSession(BenchmarkProfile profile, Func<FakeSession, CancellationToken, Task<BenchmarkReport>> run) : IBenchmarkProfileSession
    {
        public BenchmarkProfile Profile { get; } = profile;
        public IProgress<BenchmarkProgress>? Progress { get; private set; }
        public bool Disposed { get; private set; }
        public CancellationToken Token { get; private set; }

        public Task<BenchmarkReport> RunAsync(string folder, IProgress<BenchmarkProgress> progress, CancellationToken cancellationToken)
        {
            Progress = progress;
            Token = cancellationToken;
            return run(this, cancellationToken);
        }

        public ValueTask DisposeAsync() { Disposed = true; return default; }
    }

    private static BenchmarkReport Report(string profileId, double[] samples, BenchmarkWorkload workload = BenchmarkWorkload.Sequential,
        BenchmarkResultStatus status = BenchmarkResultStatus.Pass) =>
        new("run-" + profileId, DateTimeOffset.UnixEpoch, "C:\\bench", [new BenchmarkPhaseResult(profileId, workload, samples, status)]);

    private static Task<BenchmarkReport> Ok(string id, double[] samples, BenchmarkWorkload workload = BenchmarkWorkload.Sequential) =>
        Task.FromResult(Report(id, samples, workload));

    private static Task<BenchmarkReport> UntilCancelled(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<BenchmarkReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    private sealed class Harness
    {
        public FakeView View { get; } = new();
        public QueueUi Ui { get; } = new();
        public List<FakeSession> Sessions { get; } = [];
        public List<IReadOnlyList<BenchmarkReport>> Saved { get; } = [];
        public Dictionary<string, Func<FakeSession, CancellationToken, Task<BenchmarkReport>>> Behaviors { get; } = [];
        public Func<string, int, CancellationToken, string[]> Scan { get; set; } = (_, _, _) => ["a.jpg", "b.jpg"];
        public Func<BenchmarkProfile, IReadOnlyList<string>, IBenchmarkProfileSession>? FactoryOverride { get; set; }
        public string SummaryPath { get; } = Path.Combine(Path.GetTempPath(), "bench-fake-summary", "summary.json");
        public BenchmarkPresenter Presenter { get; }

        public Harness()
        {
            Presenter = new BenchmarkPresenter(View, Ui,
                (profile, files) =>
                {
                    if (FactoryOverride is not null) return FactoryOverride(profile, files);
                    var behavior = Behaviors.TryGetValue(profile.Id, out var b) ? b : (_, _) => Ok(profile.Id, [10, 20, 30]);
                    var session = new FakeSession(profile, behavior);
                    Sessions.Add(session);
                    return session;
                },
                (folder, limit, ct) => Scan(folder, limit, ct),
                reports =>
                {
                    Saved.Add(reports);
                    return Task.FromResult(SummaryPath);
                });
        }

        public static BenchmarkProfile P(string id) => BenchmarkProfiles.Find(id)!;
    }

    private static async Task Await(Task task) => await task.WaitAsync(Bound);

    // ---- Normal run ----

    [Fact(DisplayName = "A normal two-profile run reports progress, lists rows in profile order, saves once and ends idle with 'done'")]
    public async Task NormalRun_ListsRowsInOrder_SavesOnce_EndsIdle()
    {
        var h = new Harness();
        h.Behaviors["fast-sequential"] = (s, _) =>
        {
            s.Progress!.Report(new BenchmarkProgress("fast-sequential", BenchmarkWorkload.Sequential, 1, 4, BenchmarkEngine.ProgressOk));
            return Ok("fast-sequential", [10, 20]);
        };
        h.Behaviors["preview-light"] = (_, _) => Ok("preview-light", [5, 6, 7], BenchmarkWorkload.Sequential);

        await Await(h.Presenter.RunSelectedAsync([Harness.P("fast-sequential"), Harness.P("preview-light")], "C:\\bench", "5"));
        h.Ui.Pump();

        Assert.Equal([true, false], h.View.RunningHistory);
        Assert.False(h.Presenter.IsRunning);
        Assert.Equal(["fast-sequential", "preview-light"], h.Presenter.Rows.Select(r => r.Profile.Id));
        var saved = Assert.Single(h.Saved);
        Assert.Equal(2, saved.Count);
        Assert.Equal(h.SummaryPath, h.Presenter.LastReportPath);
        Assert.Equal(Tr.BenchmarkStatusDone(2, 2, h.SummaryPath), h.View.Status);
        Assert.All(h.Sessions, s => Assert.True(s.Disposed));
    }

    [Fact(DisplayName = "Progress is posted to the UI scheduler (not applied on the caller) and shown as fraction + translated status")]
    public async Task Progress_IsPostedThroughScheduler_AndFormatted()
    {
        var h = new Harness();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        h.Behaviors["fast-balanced"] = async (s, _) =>
        {
            s.Progress!.Report(new BenchmarkProgress("fast-balanced", BenchmarkWorkload.WarmNext, 3, 4, BenchmarkEngine.ProgressOk));
            entered.SetResult();
            await release.Task;
            return Report("fast-balanced", [1]);
        };

        var run = h.Presenter.RunSelectedAsync([Harness.P("fast-balanced")], "C:\\bench", "");
        await Await(entered.Task);
        Assert.Equal(0, h.View.Progress); // nothing applied until the UI thread runs the posted action
        h.Ui.Pump();

        Assert.Equal(0.75, h.View.Progress);
        Assert.Equal(Tr.BenchmarkStatusProgress(1, 1, "fast-balanced", 3, 4, Tr.BenchmarkProgressOk), h.View.Status);
        release.SetResult();
        await Await(run);
    }

    [Fact(DisplayName = "Progress with Total 0 shows 0, and the profile index counts up across profiles")]
    public async Task Progress_ZeroTotal_AndProfileIndex()
    {
        var h = new Harness();
        var firstBar = -1.0;
        var secondBar = -1.0;
        string? secondStatus = null;
        h.Behaviors["fast-sequential"] = (s, _) =>
        {
            s.Progress!.Report(new BenchmarkProgress("fast-sequential", BenchmarkWorkload.Sequential, 0, 0, "x"));
            h.Ui.Pump();
            firstBar = h.View.Progress;
            return Ok("fast-sequential", [1]);
        };
        h.Behaviors["preview-light"] = (s, _) =>
        {
            s.Progress!.Report(new BenchmarkProgress("preview-light", BenchmarkWorkload.Sequential, 1, 2, "x"));
            h.Ui.Pump();
            secondBar = h.View.Progress;
            secondStatus = h.View.Status;
            return Ok("preview-light", [1]);
        };

        await Await(h.Presenter.RunSelectedAsync([Harness.P("fast-sequential"), Harness.P("preview-light")], "C:\bench", null));

        // Captured inside the sessions and asserted here: an assertion thrown inside a session would be swallowed as a Fail row.
        Assert.Equal(0, firstBar);
        Assert.Equal(0.5, secondBar);
        Assert.Equal(Tr.BenchmarkStatusProgress(2, 2, "preview-light", 1, 2, "x"), secondStatus);
    }

    [Fact(DisplayName = "The library's fixed English progress messages are shown as catalog text, others pass through")]
    public async Task Progress_MessageIsTranslated()
    {
        using var _ = TestLocalization.Use(TestLocalization.Vietnamese);
        var h = new Harness();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        h.Behaviors["fast-sequential"] = async (s, _) =>
        {
            s.Progress!.Report(new BenchmarkProgress("fast-sequential", BenchmarkWorkload.Sequential, 1, 2, BenchmarkEngine.ProgressCorrectnessFailed));
            entered.SetResult();
            await release.Task;
            return Report("fast-sequential", [1]);
        };

        var run = h.Presenter.RunSelectedAsync([Harness.P("fast-sequential")], "C:\bench", "");
        await Await(entered.Task);
        h.Ui.Pump();

        Assert.NotEqual(BenchmarkEngine.ProgressCorrectnessFailed, Tr.BenchmarkProgressCorrectnessFailed);
        Assert.Equal(Tr.BenchmarkStatusProgress(1, 1, "fast-sequential", 1, 2, Tr.BenchmarkProgressCorrectnessFailed), h.View.Status);
        release.SetResult();
        await Await(run);
    }

    // ---- Cancel ----

    [Fact(DisplayName = "Cancel during a profile ends as 'canceled': no row, no save, controls idle, session disposed")]
    public async Task Cancel_MidProfile_EndsCancelled()
    {
        var h = new Harness();
        var entered = new TaskCompletionSource();
        h.Behaviors["fast-sequential"] = (_, ct) => { entered.SetResult(); return UntilCancelled(ct); };

        var run = h.Presenter.RunSelectedAsync([Harness.P("fast-sequential")], "C:\\bench", "");
        await Await(entered.Task);
        Assert.True(h.Presenter.IsRunning);
        h.Presenter.Cancel();
        await Await(run);

        Assert.Equal(Tr.BenchmarkStatusCanceled, h.View.Status);
        Assert.Empty(h.Presenter.Rows);
        Assert.Empty(h.Saved);
        Assert.Equal([true, false], h.View.RunningHistory);
        Assert.True(h.Sessions[0].Disposed);
        Assert.True(h.Sessions[0].Token.IsCancellationRequested);
    }

    [Fact(DisplayName = "Cancel during the folder scan interrupts it: canceled, and no profile session is ever created")]
    public async Task Cancel_DuringScan_NoSessionCreated()
    {
        var h = new Harness();
        var scanning = new ManualResetEventSlim();
        h.Scan = (_, _, ct) =>
        {
            scanning.Set();
            SpinWait.SpinUntil(() => ct.IsCancellationRequested, Bound); // IsCancellationRequested stays valid after the run disposed its source (WaitHandle would not)
            ct.ThrowIfCancellationRequested();
            return ["a.jpg"];
        };

        var run = h.Presenter.RunSelectedAsync([Harness.P("fast-sequential")], "C:\\bench", "");
        Assert.True(scanning.Wait(Bound));
        h.Presenter.Cancel();
        await Await(run);

        Assert.Equal(Tr.BenchmarkStatusCanceled, h.View.Status);
        Assert.Empty(h.Sessions);
        Assert.Equal([true, false], h.View.RunningHistory);
    }

    [Fact(DisplayName = "Cancel while idle does nothing")]
    public void Cancel_WhenIdle_IsNoOp()
    {
        var h = new Harness();
        h.Presenter.Cancel();
        h.Presenter.Dispose();
        h.Presenter.Dispose();

        Assert.Empty(h.View.RunningHistory);
        Assert.Null(h.View.Status);
    }

    // ---- Close (Dispose) ----

    [Fact(DisplayName = "Closing between two profiles ends as 'canceled' with only the finished row -- never a bogus Fail row")]
    public async Task Close_BetweenProfiles_EndsCancelled_NoFailRow()
    {
        var h = new Harness();
        var closed = false;
        h.Presenter.Rows.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add || closed) return;
            closed = true;
            h.Presenter.Dispose();
        };
        h.Behaviors["preview-light"] = (_, ct) => { ct.ThrowIfCancellationRequested(); return Ok("preview-light", [1]); };

        await Await(h.Presenter.RunSelectedAsync([Harness.P("fast-sequential"), Harness.P("preview-light")], "C:\\bench", ""));

        Assert.True(closed);
        Assert.Single(h.Presenter.Rows);
        Assert.Equal(Tr.BenchmarkStatusCanceled, h.View.Status);
        Assert.Empty(h.Saved);
        Assert.Equal([true, false], h.View.RunningHistory);
        Assert.All(h.Sessions, s => Assert.True(s.Disposed));
    }

    [Fact(DisplayName = "Closing during a profile cancels its token and the run ends through the cancel path")]
    public async Task Close_DuringProfile_CancelsToken()
    {
        var h = new Harness();
        var entered = new TaskCompletionSource();
        h.Behaviors["fast-sequential"] = (_, ct) => { entered.SetResult(); return UntilCancelled(ct); };

        var run = h.Presenter.RunSelectedAsync([Harness.P("fast-sequential")], "C:\\bench", "");
        await Await(entered.Task);
        h.Presenter.Dispose();
        await Await(run);

        Assert.True(h.Sessions[0].Token.IsCancellationRequested);
        Assert.Equal(Tr.BenchmarkStatusCanceled, h.View.Status);
        Assert.False(h.Presenter.IsRunning);
        Assert.True(h.Sessions[0].Disposed);
    }

    [Fact(DisplayName = "Closing during the folder scan cancels it and no session is created")]
    public async Task Close_DuringScan_NoSession()
    {
        var h = new Harness();
        var scanning = new ManualResetEventSlim();
        h.Scan = (_, _, ct) =>
        {
            scanning.Set();
            SpinWait.SpinUntil(() => ct.IsCancellationRequested, Bound); // IsCancellationRequested stays valid after the run disposed its source (WaitHandle would not)
            ct.ThrowIfCancellationRequested();
            return ["a.jpg"];
        };

        var run = h.Presenter.RunSelectedAsync([Harness.P("fast-sequential")], "C:\\bench", "");
        Assert.True(scanning.Wait(Bound));
        h.Presenter.Dispose();
        await Await(run);

        Assert.Empty(h.Sessions);
        Assert.Equal(Tr.BenchmarkStatusCanceled, h.View.Status);
    }

    // ---- One profile fails ----

    [Fact(DisplayName = "A failing profile becomes a Fail row with its error and the next profile still runs")]
    public async Task FailingProfile_BecomesFailRow_NextStillRuns()
    {
        var h = new Harness();
        h.Behaviors["fast-sequential"] = (_, _) => throw new InvalidOperationException("boom");

        await Await(h.Presenter.RunSelectedAsync([Harness.P("fast-sequential"), Harness.P("preview-light")], "C:\\bench", ""));

        Assert.Equal(2, h.Presenter.Rows.Count);
        var fail = h.Presenter.Rows[0];
        Assert.Equal(BenchmarkResultStatus.Fail, fail.Status);
        Assert.Equal(0, fail.Count);
        Assert.Equal(Tr.BenchNoValue, fail.P95Text);
        Assert.Equal(Tr.BenchResultFailedDetail(BenchmarkText.Describe(fail.Error!)), fail.StatusText);
        Assert.Equal(BenchmarkResultStatus.Pass, h.Presenter.Rows[1].Status);
        Assert.Contains(Tr.BenchmarkStatusProfileFailed(BenchmarkText.ProfileName(Harness.P("fast-sequential")), BenchmarkText.Describe(fail.Error!)), h.View.Statuses);
        Assert.Equal(Tr.BenchmarkStatusDone(1, 2, h.SummaryPath), h.View.Status);
        Assert.Equal(2, h.Sessions.Count);
        Assert.All(h.Sessions, s => Assert.True(s.Disposed));
    }

    [Fact(DisplayName = "When every profile fails nothing is saved and the status stays the last failure")]
    public async Task AllProfilesFail_NothingSaved()
    {
        var h = new Harness();
        h.Behaviors["fast-sequential"] = (_, _) => throw new InvalidOperationException("boom");

        await Await(h.Presenter.RunSelectedAsync([Harness.P("fast-sequential")], "C:\\bench", ""));

        Assert.Empty(h.Saved);
        Assert.Null(h.Presenter.LastReportPath);
        var row = Assert.Single(h.Presenter.Rows);
        Assert.Equal(BenchmarkResultStatus.Fail, row.Status);
        Assert.Equal(Tr.BenchmarkStatusProfileFailed(BenchmarkText.ProfileName(Harness.P("fast-sequential")), BenchmarkText.Describe(row.Error!)), h.View.Status);
    }

    [Fact(DisplayName = "A session that cannot be created ends the whole run as 'failed' (the factory is outside the profile's failure handling)")]
    public async Task FactoryThrows_EndsRunFailed()
    {
        var h = new Harness();
        h.FactoryOverride = (_, _) => throw new BenchmarkProfileException(BenchmarkProfileProblem.NotImplemented, "cache-recovery", "nope");

        await Await(h.Presenter.RunSelectedAsync([Harness.P("cache-recovery"), Harness.P("fast-sequential")], "C:\\bench", ""));

        Assert.Equal(Tr.BenchStatusFailed(BenchmarkText.Describe(new BenchmarkProfileException(BenchmarkProfileProblem.NotImplemented, "cache-recovery", "nope"))), h.View.Status);
        Assert.Empty(h.Presenter.Rows);
        Assert.Equal([true, false], h.View.RunningHistory);
    }

    // ---- Two Starts in a row ----

    [Fact(DisplayName = "A second Start (Run or Quick check) while a run is active is ignored; a new Start works after it ended")]
    public async Task SecondStart_WhileRunning_IsIgnored()
    {
        var h = new Harness();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        h.Behaviors["fast-sequential"] = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return Report("fast-sequential", [1, 2]);
        };

        var first = h.Presenter.RunSelectedAsync([Harness.P("fast-sequential")], "C:\\bench", "");
        await Await(entered.Task);
        var second = h.Presenter.RunSelectedAsync([Harness.P("preview-light")], "C:\\other", "1");
        var quick = h.Presenter.QuickCheckAsync("C:\\other", "1");
        Assert.True(second.IsCompletedSuccessfully);
        Assert.True(quick.IsCompletedSuccessfully);
        var empty = h.Presenter.RunSelectedAsync([], "C:\\bench", "");
        Assert.True(empty.IsCompletedSuccessfully);

        Assert.Single(h.Sessions);
        Assert.Equal([true], h.View.RunningHistory);
        release.SetResult();
        await Await(first);
        Assert.Equal([true, false], h.View.RunningHistory);
        Assert.Single(h.Presenter.Rows);
        Assert.Equal("fast-sequential", h.Presenter.Rows[0].Profile.Id);

        await Await(h.Presenter.RunSelectedAsync([Harness.P("preview-light")], "C:\\bench", ""));
        Assert.Equal([true, false, true, false], h.View.RunningHistory);
        Assert.Equal("preview-light", Assert.Single(h.Presenter.Rows).Profile.Id); // rows cleared at the start
    }

    // ---- Late results ----

    [Fact(DisplayName = "Progress that arrives after a cancelled run ended cannot overwrite the final status or the bar")]
    public async Task LateProgress_AfterCancel_IsDropped()
    {
        var h = new Harness();
        var entered = new TaskCompletionSource();
        h.Behaviors["fast-sequential"] = (_, ct) => { entered.SetResult(); return UntilCancelled(ct); };

        var run = h.Presenter.RunSelectedAsync([Harness.P("fast-sequential")], "C:\\bench", "");
        await Await(entered.Task);
        h.Presenter.Cancel();
        await Await(run);
        var statusAfter = h.View.Status;

        h.Sessions[0].Progress!.Report(new BenchmarkProgress("fast-sequential", BenchmarkWorkload.Sequential, 3, 4, "late"));
        h.Ui.Pump();

        Assert.Equal(Tr.BenchmarkStatusCanceled, statusAfter);
        Assert.Equal(statusAfter, h.View.Status);
        Assert.Equal(0, h.View.Progress);
    }

    [Fact(DisplayName = "Progress from an earlier run is dropped when a newer run is active")]
    public async Task LateProgress_FromEarlierRun_IsDroppedDuringNewRun()
    {
        var h = new Harness();
        var firstEntered = new TaskCompletionSource();
        h.Behaviors["fast-sequential"] = (_, ct) => { firstEntered.SetResult(); return UntilCancelled(ct); };
        var first = h.Presenter.RunSelectedAsync([Harness.P("fast-sequential")], "C:\\bench", "");
        await Await(firstEntered.Task);
        h.Presenter.Cancel();
        await Await(first);
        var stale = h.Sessions[0].Progress!;

        var secondEntered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        h.Behaviors["preview-light"] = async (_, _) =>
        {
            secondEntered.SetResult();
            await release.Task;
            return Report("preview-light", [1]);
        };
        var second = h.Presenter.RunSelectedAsync([Harness.P("preview-light")], "C:\\bench", "");
        await Await(secondEntered.Task);
        var before = h.View.Status;
        stale.Report(new BenchmarkProgress("fast-sequential", BenchmarkWorkload.Sequential, 1, 4, "stale"));
        h.Ui.Pump();

        Assert.Equal(before, h.View.Status);
        Assert.Equal(0, h.View.Progress);
        release.SetResult();
        await Await(second);
    }

    [Fact(DisplayName = "A report that finishes although Cancel was requested is kept (only a throwing run counts as cancelled)")]
    public async Task ReportCompletingAfterCancel_IsKeptAsRow()
    {
        var h = new Harness();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        h.Behaviors["fast-sequential"] = async (_, _) =>
        {
            entered.SetResult();
            await release.Task; // ignores the token and finishes anyway
            return Report("fast-sequential", [4, 5]);
        };

        var run = h.Presenter.RunSelectedAsync([Harness.P("fast-sequential")], "C:\\bench", "");
        await Await(entered.Task);
        h.Presenter.Cancel();
        release.SetResult();
        await Await(run);

        Assert.Single(h.Presenter.Rows);
        Assert.Equal([true, false], h.View.RunningHistory);
        Assert.False(h.Presenter.IsRunning);
    }

    // ---- Start guards and folder problems ----

    [Fact(DisplayName = "Run with nothing selected reports the status and never goes busy")]
    public async Task NoSelection_ReportsStatus()
    {
        var h = new Harness();

        await Await(h.Presenter.RunSelectedAsync([], "C:\\bench", ""));

        Assert.Equal(Tr.BenchmarkStatusNoProfileSelected, h.View.Status);
        Assert.Empty(h.View.RunningHistory);
        Assert.Empty(h.Sessions);
    }

    [Fact(DisplayName = "Folder problems map to their own status and always restore the idle controls")]
    public async Task FolderProblems_StatusAndIdle()
    {
        var h = new Harness();

        h.Scan = (f, _, _) => throw new DirectoryNotFoundException(f);
        await Await(h.Presenter.RunSelectedAsync([Harness.P("fast-sequential")], "C:\\gone", ""));
        Assert.Equal(Tr.BenchmarkStatusFolderMissing, h.View.Status);

        h.Scan = (_, _, _) => throw new IOException("share offline");
        await Await(h.Presenter.RunSelectedAsync([Harness.P("fast-sequential")], "C:\\bench", ""));
        Assert.Equal(Tr.BenchmarkStatusCannotReadFolder("share offline"), h.View.Status);

        h.Scan = (_, _, _) => throw new UnauthorizedAccessException("denied");
        await Await(h.Presenter.RunSelectedAsync([Harness.P("fast-sequential")], "C:\\bench", ""));
        Assert.Equal(Tr.BenchmarkStatusCannotReadFolder("denied"), h.View.Status);

        h.Scan = (_, _, _) => [];
        await Await(h.Presenter.RunSelectedAsync([Harness.P("fast-sequential")], "C:\\bench", ""));
        Assert.Equal(Tr.BenchmarkStatusNoImages, h.View.Status);

        Assert.Equal([true, false, true, false, true, false, true, false], h.View.RunningHistory);
        Assert.Empty(h.Sessions);
        Assert.Empty(h.Saved);
    }

    [Fact(DisplayName = "An unexpected scan error ends the run as 'failed' with the translated description")]
    public async Task UnexpectedScanError_Failed()
    {
        var h = new Harness();
        h.Scan = (_, _, _) => throw new InvalidOperationException("weird");

        await Await(h.Presenter.RunSelectedAsync([Harness.P("fast-sequential")], "C:\\bench", ""));

        Assert.Equal(Tr.BenchStatusFailed(Tr.BenchErrorUnexpected("weird")), h.View.Status);
        Assert.False(h.View.Running);
    }

    [Fact(DisplayName = "The image limit text is parsed per run and passed to the scan (blank / invalid / negative mean all)")]
    public async Task ImageLimit_IsPassedToScan()
    {
        var h = new Harness();
        var limits = new List<int>();
        h.Scan = (_, limit, _) => { limits.Add(limit); return ["a.jpg"]; };

        foreach (var text in new[] { "5", "", "abc", "-3", "0", null })
            await Await(h.Presenter.RunSelectedAsync([Harness.P("fast-sequential")], "C:\\bench", text));

        Assert.Equal([5, 0, 0, 0, 0, 0], limits);
    }

    [Fact(DisplayName = "Quick check always runs fast-sequential with 10 iterations whatever else is selected, and the scan receives the folder")]
    public async Task QuickCheck_RunsFastSequentialWithTenIterations()
    {
        var h = new Harness();
        BenchmarkProfile? seen = null;
        string? folder = null;
        h.Scan = (f, _, _) => { folder = f; return ["a.jpg"]; };
        h.FactoryOverride = (p, _) =>
        {
            seen = p;
            var s = new FakeSession(p, (_, _) => Ok(p.Id, [1]));
            h.Sessions.Add(s);
            return s;
        };

        await Await(h.Presenter.QuickCheckAsync("C:\\pics", "7"));

        Assert.Equal("C:\\pics", folder);
        Assert.Equal("fast-sequential", seen!.Id);
        Assert.Equal(10, seen.Iterations);
        Assert.Equal(BenchmarkProfiles.Find("fast-sequential")!.WarmupCount, seen.WarmupCount);
        Assert.Single(h.Presenter.Rows);
    }

    // ---- Ranking / rows ----

    [Fact(DisplayName = "The lowest-P95 Pass row of each workload carries the best marker; other workloads and Fail rows are ranked apart")]
    public async Task BestMarker_PerWorkloadGroup()
    {
        var h = new Harness();
        h.Behaviors["fast-sequential"] = (_, _) => Ok("fast-sequential", [10, 12], BenchmarkWorkload.Sequential);
        h.Behaviors["preview-light"] = (_, _) => Ok("preview-light", [3, 4], BenchmarkWorkload.Sequential);
        h.Behaviors["instant-review"] = (_, _) => Ok("instant-review", [50, 60], BenchmarkWorkload.FirstFrame);

        await Await(h.Presenter.RunSelectedAsync([Harness.P("fast-sequential"), Harness.P("preview-light"), Harness.P("instant-review")], "C:\\bench", ""));

        var rows = h.Presenter.Rows;
        Assert.Equal(["", BenchmarkResultRow.BestSymbol, BenchmarkResultRow.BestSymbol], rows.Select(r => r.BestMarker));
        Assert.Equal([false, true, true], rows.Select(r => r.IsBest));
        Assert.True(h.View.Refreshes >= 3); // the grid is repainted after every recompute
    }

    [Fact(DisplayName = "A slower row later in the run does not steal the marker, and an earlier best loses it when beaten")]
    public async Task BestMarker_MovesWhenBeaten()
    {
        var h = new Harness();
        h.Behaviors["fast-sequential"] = (_, _) => Ok("fast-sequential", [10]);
        h.Behaviors["preview-light"] = (_, _) => Ok("preview-light", [20]);
        h.Behaviors["no-preload-baseline"] = (_, _) => Ok("no-preload-baseline", [1]);

        await Await(h.Presenter.RunSelectedAsync([Harness.P("fast-sequential"), Harness.P("preview-light"), Harness.P("no-preload-baseline")], "C:\\bench", ""));

        Assert.Equal([false, false, true], h.Presenter.Rows.Select(r => r.IsBest));
    }

    [Fact(DisplayName = "The ten cells show marker, profile, mode, workload, samples and P50/P95/P99/Max in ms; no samples show the placeholder")]
    public void Row_CellTexts_TenColumnsInOrder()
    {
        var profile = BenchmarkProfiles.Find("fast-sequential")!;
        var row = new BenchmarkResultRow(profile, new BenchmarkPhaseResult(profile.Id, BenchmarkWorkload.Sequential, [10, 20, 30, 40], BenchmarkResultStatus.Pass)) { IsBest = true };

        var cells = row.CellTexts;

        Assert.Equal(10, cells.Count);
        Assert.Equal(BenchmarkResultRow.BestSymbol, cells[0]);
        Assert.Equal(BenchmarkText.ProfileName(profile), cells[1]);
        Assert.Equal(BenchmarkText.LoadingModeText(profile.LoadingMode), cells[2]);
        Assert.Equal(BenchmarkText.WorkloadText(BenchmarkWorkload.Sequential), cells[3]);
        Assert.Equal("4", cells[4]);
        Assert.Equal(Tr.UnitMilliseconds(row.Phase.P50.ToString("F0", CultureInfo.CurrentCulture)), cells[5]);
        Assert.Equal(Tr.UnitMilliseconds(row.Phase.P95.ToString("F0", CultureInfo.CurrentCulture)), cells[6]);
        Assert.Equal(Tr.UnitMilliseconds(row.Phase.P99.ToString("F0", CultureInfo.CurrentCulture)), cells[7]);
        Assert.Equal(Tr.UnitMilliseconds("40"), cells[8]);
        Assert.Equal(Tr.EnumBenchmarkResultStatusPass, cells[9]);

        var empty = new BenchmarkResultRow(profile, new BenchmarkPhaseResult(profile.Id, BenchmarkWorkload.Sequential, [], BenchmarkResultStatus.InsufficientData));
        Assert.Equal([Tr.BenchNoValue, Tr.BenchNoValue, Tr.BenchNoValue, Tr.BenchNoValue], empty.CellTexts.Skip(5).Take(4));
        Assert.Equal("", empty.CellTexts[0]);
        Assert.Equal(Tr.EnumBenchmarkResultStatusInsufficientData, empty.CellTexts[9]);
    }

    [Fact(DisplayName = "Status text: Warn, Fail without message, the correctness failure and a detailed failure each have their own text")]
    public void Row_StatusText_Branches()
    {
        var profile = BenchmarkProfiles.Find("fast-sequential")!;
        BenchmarkResultRow Row(BenchmarkResultStatus status, string? message) =>
            new(profile, new BenchmarkPhaseResult(profile.Id, BenchmarkWorkload.Sequential, [1], status, message));

        Assert.Equal(Tr.EnumBenchmarkResultStatusWarn, Row(BenchmarkResultStatus.Warn, null).StatusText);
        Assert.Equal(Tr.EnumBenchmarkResultStatusFail, Row(BenchmarkResultStatus.Fail, null).StatusText);
        Assert.Equal(Tr.BenchmarkResultCorrectnessFailed, Row(BenchmarkResultStatus.Fail, BenchmarkEngine.ResultCorrectnessFailed).StatusText);
        Assert.Equal(Tr.BenchResultFailedDetail("disk"), Row(BenchmarkResultStatus.Fail, "disk").StatusText);
    }

    // ---- Export ----

    [Fact(DisplayName = "CSV export has a header and one quoted-as-needed line per row, in grid order")]
    public async Task Csv_HeaderAndRows()
    {
        var h = new Harness();
        h.Behaviors["fast-sequential"] = (_, _) => Ok("fast-sequential", [10, 20]);
        h.Behaviors["preview-light"] = (_, _) => throw new InvalidOperationException("a,\"b\"");
        await Await(h.Presenter.RunSelectedAsync([Harness.P("fast-sequential"), Harness.P("preview-light")], "C:\\bench", ""));

        var lines = h.Presenter.ResultsToCsv().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.StartsWith(BenchmarkResultRow.BestSymbol + ",", lines[0], StringComparison.Ordinal);
        Assert.Contains(",P50,P95,P99,Max,", lines[0], StringComparison.Ordinal);
        Assert.Contains(BenchmarkText.ProfileName(Harness.P("fast-sequential")), lines[1], StringComparison.Ordinal);
        Assert.Contains("\"\"b\"\"", lines[2], StringComparison.Ordinal); // quotes doubled inside a quoted cell
    }

    // ---- Reports / Explorer ----

    [Fact(DisplayName = "Open report selects the last summary only when it exists; Open folder falls back to the temp folder before any run")]
    public async Task OpenReport_AndFolder()
    {
        var h = new Harness();
        h.Presenter.OpenReport();
        h.Presenter.OpenResultFolder();
        Assert.Equal([Path.GetTempPath()], h.View.Explorer);

        var dir = Path.Combine(Path.GetTempPath(), "bench-presenter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var summary = Path.Combine(dir, "summary.json");
            var h2 = new Harness();
            var presenter = new BenchmarkPresenter(h2.View, h2.Ui, (p, _) => new FakeSession(p, (_, _) => Ok(p.Id, [1])),
                (_, _, _) => ["a.jpg"], _ => Task.FromResult(summary));
            await Await(presenter.RunSelectedAsync([Harness.P("fast-sequential")], "C:\\bench", ""));

            presenter.OpenReport(); // the file does not exist yet
            Assert.Empty(h2.View.Explorer);
            File.WriteAllText(summary, "[]");
            presenter.OpenReport();
            presenter.OpenResultFolder();

            Assert.Equal([$"/select,\"{summary}\"", dir], h2.View.Explorer);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact(DisplayName = "The report store writes one file per profile plus summary.json under a timestamped folder; an empty report is named 'report-<run>'")]
    public async Task ReportStore_WritesFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "bench-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var empty = new BenchmarkReport("r0", DateTimeOffset.UnixEpoch, "C:\\f", []);
            var reports = new[] { Report("fast-sequential", [1, 2]), empty };

            var summary = await BenchmarkReportStore.SaveSessionAsync(reports, root, new DateTime(2026, 10, 13, 8, 9, 10, DateTimeKind.Utc));

            var dir = Path.Combine(root, "20261013-080910");
            Assert.Equal(Path.Combine(dir, "summary.json"), summary);
            Assert.True(File.Exists(Path.Combine(dir, "fast-sequential-run-fast-sequential.json")));
            Assert.True(File.Exists(Path.Combine(dir, "report-r0.json")));
            using var doc = JsonDocument.Parse(File.ReadAllText(summary));
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            Assert.Equal(2, doc.RootElement.GetArrayLength());
            Assert.Contains("\n", File.ReadAllText(summary), StringComparison.Ordinal); // indented
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    // ---- Language / defaults ----

    [Fact(DisplayName = "A language switch raises change notification on every profile item and repaints the grid")]
    public void LanguageChange_NotifiesItemsAndRepaints()
    {
        var h = new Harness();
        var raised = 0;
        foreach (var item in h.Presenter.ProfileItems)
            ((INotifyPropertyChanged)item).PropertyChanged += (_, _) => raised++;

        h.Presenter.NotifyLanguageChanged();

        Assert.Equal(BenchmarkProfiles.All.Count, raised);
        Assert.Equal(1, h.View.Refreshes);
    }

    [Fact(DisplayName = "Defaults: every registry profile is listed, fast-sequential is preselected, limit 64, folder falls back to My Pictures")]
    public void Defaults()
    {
        var h = new Harness();

        Assert.Equal(BenchmarkProfiles.All.Select(p => p.Id), h.Presenter.ProfileItems.Select(i => i.Profile.Id));
        Assert.Equal("fast-sequential", h.Presenter.DefaultSelection.Profile.Id);
        Assert.Equal("64", BenchmarkPresenter.DefaultImageLimitText);
        Assert.Equal("D:\\x", BenchmarkPresenter.InitialFolder("D:\\x"));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), BenchmarkPresenter.InitialFolder(null));
    }

    [Theory(DisplayName = "ParseImageLimit: positive integers are the cap, everything else means all")]
    [InlineData("64", 64)]
    [InlineData(" 12", 12)]
    [InlineData("0", 0)]
    [InlineData("-1", 0)]
    [InlineData("x", 0)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void ParseImageLimit_Table(string? text, int expected) => Assert.Equal(expected, BenchmarkPresenter.ParseImageLimit(text));
}
