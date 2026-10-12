using System.IO;
using System.Globalization;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;

namespace PhotoReview.App.Tests.Presenters;

/// <summary>WP-35 G0: Action Profiles, Click zoom custom, Diagnostics, Batch review and Skipped files without a window.</summary>
[Collection("GlobalState")]
public sealed class SecondaryDialogPresenterTests
{
    private static ReviewAction Action(string name, string shortcut, FileOperationType operation = FileOperationType.Move, string destination = "Out") =>
        new() { Name = name, Shortcut = shortcut, Operation = operation, Destination = destination };

    // ---- Action Profiles ----

    [Fact(DisplayName = "The presenter edits a copy: changing the original list or a row afterwards does not leak in")]
    public void ActionProfiles_EditsACopy()
    {
        var original = new List<ReviewAction> { Action("A", "F6") };
        var presenter = new ActionProfilesPresenter(original);
        original[0].Name = "Changed";
        original.Add(Action("B", "F7"));

        Assert.Equal("A", Assert.Single(presenter.Actions).Name);
    }

    [Fact(DisplayName = "Opening a row returns its fields (operation as combo index); nothing selected returns nothing")]
    public void ActionProfiles_OpenReturnsFields()
    {
        var presenter = new ActionProfilesPresenter([Action("Copy it", "F6", FileOperationType.Copy, "Backup"), Action("Bin", "F7", FileOperationType.Recycle)]);

        var copy = presenter.Open(presenter.Actions[0]);
        Assert.Equal(new ActionProfileForm("Copy it", "F6", 1, "Backup", false), copy);
        Assert.Equal(2, presenter.Open(presenter.Actions[1])!.OperationIndex);
        Assert.Null(presenter.Open(null));
    }

    [Fact(DisplayName = "SaveCurrent trims, canonicalizes the key and maps the operation index; no open row means nothing is written")]
    public void ActionProfiles_SaveCurrentWritesTheOpenRow()
    {
        var presenter = new ActionProfilesPresenter([Action("A", "F6")]);
        Assert.False(presenter.SaveCurrent(ActionProfileForm.Empty));

        presenter.Open(presenter.Actions[0]);
        Assert.True(presenter.SaveCurrent(new ActionProfileForm("  New name ", " f8 ", 2, "  Dest ", true)));

        var action = presenter.Actions[0];
        Assert.Equal("New name", action.Name);
        Assert.Equal(ShortcutKeyCanonical.Canonicalize(" f8 "), action.Shortcut);
        Assert.Equal(FileOperationType.Recycle, action.Operation);
        Assert.Equal("Dest", action.Destination);
        Assert.True(action.Confirm);
    }

    [Fact(DisplayName = "A removed row that is still 'open' is never written back")]
    public void ActionProfiles_RemovedRowIsNotWrittenBack()
    {
        var presenter = new ActionProfilesPresenter([Action("A", "F6"), Action("B", "F7")]);
        var first = presenter.Actions[0];
        presenter.Open(first);

        var next = presenter.Remove(first, selectedIndex: 0);

        Assert.Equal(0, next);
        Assert.False(presenter.SaveCurrent(new ActionProfileForm("ghost", "F9", 0, "x", false)));
        Assert.Equal("B", Assert.Single(presenter.Actions).Name);
        Assert.Equal("A", first.Name); // the detached row kept its own values
    }

    [Fact(DisplayName = "Remove picks the next index, clamps at the end and signals an empty list with -1")]
    public void ActionProfiles_RemoveSelectionRules()
    {
        var presenter = new ActionProfilesPresenter([Action("A", "F6"), Action("B", "F7"), Action("C", "F8")]);
        Assert.Equal(1, presenter.Remove(presenter.Actions[1], 1));
        Assert.Equal(0, presenter.Remove(presenter.Actions[1], 1)); // list is A, C: removing the last one clamps to the new last index
        Assert.Equal(-1, presenter.Remove(presenter.Actions[0], 0));
        Assert.Empty(presenter.Actions);
        Assert.Null(presenter.Loaded);
    }

    [Fact(DisplayName = "Add appends a Move action with the first free F6..F12 key and the localized default name")]
    public void ActionProfiles_AddUsesNextFreeKey()
    {
        var presenter = new ActionProfilesPresenter([Action("A", "F6"), Action("B", "Return")]);
        var added = presenter.Add();

        Assert.Equal("F7", added.Shortcut);
        Assert.Equal(Tr.ActionProfilesNewActionName, added.Name);
        Assert.Equal(FileOperationType.Move, added.Operation);
        Assert.Equal("Output", added.Destination);
        Assert.Equal(3, presenter.Actions.Count);
    }

    [Fact(DisplayName = "NextFreeShortcut skips keys used under another spelling and falls back to F6 when all are taken")]
    public void ActionProfiles_NextFreeShortcut()
    {
        Assert.Equal("F8", ActionProfilesPresenter.NextFreeShortcut([Action("A", "f6"), Action("B", "F7")]));
        Assert.Equal("F12", ActionProfilesPresenter.NextFreeShortcut(Enumerable.Range(6, 6).Select(n => Action("k" + n, "F" + n)))); // F6..F11 taken: the last candidate is still offered
        var all = Enumerable.Range(6, 7).Select(n => Action("k" + n, "F" + n)).ToList();
        Assert.Equal("F6", ActionProfilesPresenter.NextFreeShortcut(all));
    }

    [Fact(DisplayName = "Apply refuses an empty list, a blank name, a bad key and two aliases of one key; accepts a valid list")]
    public void ActionProfiles_ApplyChecks()
    {
        Assert.True(ActionProfilesPresenter.HasInvalidActions([]));
        Assert.True(ActionProfilesPresenter.HasInvalidActions([Action(" ", "F6")]));
        Assert.True(ActionProfilesPresenter.HasInvalidActions([Action("A", "Nope")]));
        Assert.True(ActionProfilesPresenter.HasInvalidActions([Action("A", "Return"), Action("B", "Enter")]));
        Assert.True(ActionProfilesPresenter.HasInvalidActions([new ReviewAction { Name = "A", Shortcut = "F6", Operation = (FileOperationType)99 }]));
        Assert.False(ActionProfilesPresenter.HasInvalidActions([Action("A", "F6"), Action("B", "F7")]));

        var bad = new ActionProfilesPresenter([Action("A", "F6", destination: "..\\up")]);
        var result = bad.Apply();
        Assert.False(result.Accepted);
        Assert.Equal(Tr.ActionProfilesErrorDestinationEscapes("A"), result.Message);

        var invalid = new ActionProfilesPresenter([Action("", "F6")]);
        Assert.Equal(Tr.DialogActionProfilesInvalidMessage, invalid.Apply().Message);

        var good = new ActionProfilesPresenter([Action("A", "F6")]);
        Assert.True(good.Apply().Accepted);
    }

    [Fact(DisplayName = "A Recycle action's destination is not checked; a Copy action's is")]
    public void ActionProfiles_RecycleIgnoresDestination()
    {
        Assert.Null(ActionProfilesPresenter.FindDestinationProblem([Action("Bin", "F6", FileOperationType.Recycle, destination: "..\\up")]));
        Assert.NotNull(ActionProfilesPresenter.FindDestinationProblem([Action("Cp", "F6", FileOperationType.Copy, destination: "..\\up")]));
    }

    [Fact(DisplayName = "Import/export round-trips a list; a non-list, an empty list and a missing file give three different reasons")]
    public void ActionProfiles_ImportExport()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wp35g0-actions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "actions.json");
            Assert.Null(ActionProfilesPresenter.TryExport(path, [Action("Vi\u1ec7t", "F6", FileOperationType.Copy, "Backup")]));
            var imported = ActionProfilesPresenter.TryImport(path, out var error);
            Assert.NotNull(imported);
            Assert.Equal("", error);
            var row = Assert.Single(imported);
            Assert.Equal(("Vi\u1ec7t", "F6", FileOperationType.Copy, "Backup"), (row.Name, row.Shortcut, row.Operation, row.Destination));

            File.WriteAllText(path, "{\"not\":\"a list\"}");
            Assert.Null(ActionProfilesPresenter.TryImport(path, out var notList));
            Assert.Equal(Tr.DialogImportActionsInvalidMessage, notList);

            File.WriteAllText(path, "[]");
            Assert.Null(ActionProfilesPresenter.TryImport(path, out var empty));
            Assert.Equal(Tr.DialogImportActionsInvalidMessage, empty);

            Assert.Null(ActionProfilesPresenter.TryImport(Path.Combine(dir, "missing.json"), out var missing));
            Assert.NotEqual(Tr.DialogImportActionsInvalidMessage, missing);
            Assert.False(string.IsNullOrWhiteSpace(missing));

            Assert.NotNull(ActionProfilesPresenter.TryExport(Path.Combine(dir, "nope", "x.json"), [Action("A", "F6")]));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact(DisplayName = "ReplaceAll swaps the whole list")]
    public void ActionProfiles_ReplaceAll()
    {
        var presenter = new ActionProfilesPresenter([Action("A", "F6")]);
        presenter.ReplaceAll([Action("X", "F7"), Action("Y", "F8")]);
        Assert.Equal(["X", "Y"], presenter.Actions.Select(a => a.Name).ToArray());
    }

    // ---- Click zoom custom ----

    [Fact(DisplayName = "The custom click-zoom box starts with the current value and accepts the whole range, refusing one past each end")]
    public void ClickZoomCustom_AcceptsRangeOnly()
    {
        Assert.Equal("150", new ClickZoomCustomPresenter(150).InitialText);

        var presenter = new ClickZoomCustomPresenter(100);
        Assert.Null(presenter.TryAccept(AppSettings.MinClickZoomPercent.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(AppSettings.MinClickZoomPercent, presenter.Value);
        Assert.Null(presenter.TryAccept(AppSettings.MaxClickZoomPercent.ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(AppSettings.MaxClickZoomPercent, presenter.Value);

        var message = Tr.DialogSettingsInvalidClickZoomPercent(AppSettings.MinClickZoomPercent, AppSettings.MaxClickZoomPercent);
        Assert.Equal(message, presenter.TryAccept((AppSettings.MinClickZoomPercent - 1).ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(message, presenter.TryAccept((AppSettings.MaxClickZoomPercent + 1).ToString(CultureInfo.InvariantCulture)));
        Assert.Equal(message, presenter.TryAccept("12x"));
        Assert.Equal(message, presenter.TryAccept(null));
        Assert.Equal(AppSettings.MaxClickZoomPercent, presenter.Value); // a refusal keeps the last accepted value
    }

    // ---- Diagnostics ----

    private static T WithCulture<T>(string name, Func<T> body)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        try { return body(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact(DisplayName = "An empty snapshot shows 'not available' for the rates and zero for the counters")]
    public void Diagnostics_EmptySnapshot()
    {
        var display = DiagnosticsPresenter.Build(new ReviewMetricsSnapshot(0, 0, 0, 0, 0, 0, 0));

        Assert.Equal(Tr.DiagnosticsNotAvailable, display.HitRate);
        Assert.Equal(Tr.DiagnosticsNotAvailable, display.PresentLatency);
        Assert.Equal(Tr.DiagnosticsNotAvailable, display.PresentHistogram);
        Assert.Equal(Tr.DiagnosticsNotAvailable, display.ExplorerMetadata);
        Assert.Equal(Tr.DiagnosticsExplorerOrderNotQueried, display.ExplorerOrder);
        Assert.Equal("0", display.SourceOpens);
        Assert.Equal("0", display.CacheHits);
    }

    [Fact(DisplayName = "Counters use the current culture's grouping and the hit rate is hits / (hits + misses)")]
    public void Diagnostics_FormatsWithCurrentCulture()
    {
        var snapshot = new ReviewMetricsSnapshot(CacheHits: 3000, CacheMisses: 1000, SourceBytesRead: 1234567, SourceReads: 2, DecodeMilliseconds: 4500,
            PresentedImages: 4, PresentMilliseconds: 80)
        {
            StatCount = 7, SessionWriteCount = 8, CrossThreadPresentCount = 9,
            SourceOpenCount = 2, TopSourceOpens = [new SourceOpenEntry("C:\\photos\\a.jpg", 2)],
            PresentHistogram = [new PresentLatencyBucket("<16ms", 3)],
        };

        var display = WithCulture("en-US", () => DiagnosticsPresenter.Build(snapshot));

        Assert.Equal("3,000", display.CacheHits);
        Assert.Equal("1,000", display.CacheMisses);
        Assert.Equal(string.Format(CultureInfo.GetCultureInfo("en-US"), "{0:P1}", 0.75), display.HitRate);
        Assert.Equal(Tr.DiagnosticsValueBytes("1,234,567"), display.SourceBytes);
        Assert.Equal(Tr.UnitMilliseconds("4,500"), display.DecodeTime);
        Assert.Equal(Tr.DiagnosticsValuePresentLatency("20", "4"), display.PresentLatency);
        Assert.Equal("7", display.StatCount);
        Assert.Equal("8", display.SessionWrites);
        Assert.Equal("9", display.CrossThreadPresents);
        Assert.Contains("a.jpg", display.SourceOpens, StringComparison.Ordinal);
        Assert.DoesNotContain("photos", display.SourceOpens, StringComparison.Ordinal);
        Assert.Contains("<16ms", display.PresentHistogram, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "An Explorer snapshot shows its translated status and either the reason or the item count")]
    public void Diagnostics_ExplorerSnapshot()
    {
        var snapshot = new ReviewMetricsSnapshot(0, 0, 0, 0, 0, 0, 0);
        var ok = new ExplorerViewSnapshot("C:\\p", ["a", "b", "c"], [], ExplorerGroupState.Active, ExplorerOrderStatus.Available, null, DateTime.UtcNow);
        var failed = ok with { Status = ExplorerOrderStatus.TimedOut, Reason = ExplorerReason.Timeout };

        var okDisplay = DiagnosticsPresenter.Build(snapshot, ok);
        var failedDisplay = DiagnosticsPresenter.Build(snapshot, failed);

        Assert.Equal(Tr.DiagnosticsExplorerOrderStatus(Tr.EnumExplorerOrderStatusAvailable, Tr.DiagnosticsExplorerOrderItemCount(3)), okDisplay.ExplorerOrder);
        Assert.Equal(Tr.DiagnosticsExplorerOrderStatus(Tr.EnumExplorerOrderStatusTimedOut, Tr.DiagExplorerReasonTimeout), failedDisplay.ExplorerOrder);
        Assert.Equal(Tr.DiagnosticsExplorerMetadata(0, ExplorerGroupState.Active), okDisplay.ExplorerMetadata);
    }

    [Fact(DisplayName = "A reason without a known code is shown verbatim inside the generic sentence")]
    public void Diagnostics_UnknownReason()
    {
        Assert.Equal(Tr.DiagExplorerReasonUnknown("weird thing"), DiagnosticsPresenter.ReasonText("weird thing"));
        Assert.Equal(((ExplorerOrderStatus)99).ToString(), DiagnosticsPresenter.StatusText((ExplorerOrderStatus)99));
    }

    // ---- Batch review / skipped files ----

    [Fact(DisplayName = "Batch lines show name, grouped size and path; an unknown size shows the 'missing' line")]
    public void BatchReview_Lines()
    {
        var lines = WithCulture("en-US", () => BatchReviewPresenter.BuildLines(
            [new BatchReviewItem("C:\\a\\one.jpg", 1234567), new BatchReviewItem("C:\\a\\gone.jpg", null)]));

        Assert.Equal(Tr.BatchReviewItem("one.jpg", "1,234,567", "C:\\a\\one.jpg"), lines[0]);
        Assert.Equal(Tr.BatchReviewItemMissing("gone.jpg", "C:\\a\\gone.jpg"), lines[1]);
        Assert.Equal(Tr.BatchReviewSummary(2), BatchReviewPresenter.Summary(2));
    }

    [Fact(DisplayName = "The path-only form has no sizes, so every line is the 'missing' line")]
    public void BatchReview_PathOnlyForm()
    {
        var items = BatchReviewPresenter.FromPaths(["C:\\x\\a.jpg"]);
        var item = Assert.Single(items);
        Assert.Null(item.Length);
        Assert.Equal(Tr.BatchReviewItemMissing("a.jpg", "C:\\x\\a.jpg"), Assert.Single(BatchReviewPresenter.BuildLines(items)));
    }

    [Fact(DisplayName = "Skipped lines distinguish an interrupted listing from an unreadable file and keep the OS message verbatim")]
    public void SkippedFiles_Lines()
    {
        var lines = SkippedFilesPresenter.BuildLines(
        [
            new SkippedEntry("C:\\a\\bad.jpg", "Access to the path is denied"),
            new SkippedEntry("C:\\a", "The network path was not found", SkippedKind.ListingInterrupted),
        ]);

        Assert.Equal(Tr.SkippedEntry("C:\\a\\bad.jpg", Tr.SkippedReasonFileUnreadable("Access to the path is denied")), lines[0]);
        Assert.Equal(Tr.SkippedEntry("C:\\a", Tr.SkippedReasonListingInterrupted("The network path was not found")), lines[1]);
        Assert.False(string.IsNullOrWhiteSpace(SkippedFilesPresenter.Title));
        Assert.False(string.IsNullOrWhiteSpace(SkippedFilesPresenter.Intro));
        Assert.False(string.IsNullOrWhiteSpace(SkippedFilesPresenter.CloseText));
    }
}
