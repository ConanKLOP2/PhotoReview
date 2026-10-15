using System.IO;
using System.Text.Json;
using PhotoReview.App.Localization;
using PhotoReview.App.Presenters;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Model;
using PhotoReview.Core.Updates;

namespace PhotoReview.App.Tests.Presenters;

/// <summary>
/// WP-35 G0: the behaviour that used to live in <c>SettingsWindow</c>'s code-behind, driven through
/// <see cref="SettingsPresenter"/> with no window: validation order and focus, shortcut rules, defaults, import/export,
/// RAM slider arithmetic, update check, Explorer integration and the language picker.
/// </summary>
[Collection("GlobalState")]
public sealed class SettingsPresenterTests
{
    private const long PhysicalMemory = 32L * 1024 * 1024 * 1024;

    private static SettingsPresenter NewPresenter(AppSettings? settings = null, IUpdateChecker? checker = null) =>
        new(settings ?? new AppSettings(), updateChecker: checker, physicalMemoryBytes: PhysicalMemory);

    // ---- Save: validation and order ----

    [Fact(DisplayName = "Save of an untouched form succeeds, and saving the result again changes nothing")]
    public void Save_UntouchedForm_Succeeds_AndIsStable()
    {
        var presenter = NewPresenter();
        var first = presenter.TrySave(presenter.LoadFields());
        Assert.Equal(SettingsSaveOutcome.Saved, first.Outcome);
        Assert.Null(first.Message);

        // The first Save materializes the legacy-derived context-menu list; from then on the form is a fixed point.
        var afterFirst = JsonSerializer.Serialize(presenter.Settings, AppSettingsJsonContext.Default.AppSettings);
        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(presenter.LoadFields()).Outcome);
        Assert.Equal(afterFirst, JsonSerializer.Serialize(presenter.Settings, AppSettingsJsonContext.Default.AppSettings));
    }

    public static TheoryData<string, string> NumericRefusals() => new()
    {
        { nameof(SettingsFormState.ClickZoomPercentText), nameof(SettingsFocusField.None) },
        { nameof(SettingsFormState.ArrowPanStepText), nameof(SettingsFocusField.ArrowPanStep) },
        { nameof(SettingsFormState.KeyboardZoomStepPercentText), nameof(SettingsFocusField.KeyboardZoomStep) },
        { nameof(SettingsFormState.ImageTransitionMsText), nameof(SettingsFocusField.ImageTransitionMs) },
        { nameof(SettingsFormState.TouchpadSwipeDistanceText), nameof(SettingsFocusField.TouchpadSwipeDistance) },
        { nameof(SettingsFormState.PreloadForwardText), nameof(SettingsFocusField.PreloadForward) },
        { nameof(SettingsFormState.PreloadBackwardText), nameof(SettingsFocusField.PreloadBackward) },
        { nameof(SettingsFormState.ToolbarAutoHideDelayText), nameof(SettingsFocusField.None) },
        { nameof(SettingsFormState.InfoOverlayAutoHideDelayText), nameof(SettingsFocusField.InfoOverlayAutoHideDelay) },
        { nameof(SettingsFormState.InfoOverlayFontSizeText), nameof(SettingsFocusField.None) },
    };

    [Theory(DisplayName = "Save refuses an unparsable number, names the range and says which box to focus")]
    [MemberData(nameof(NumericRefusals))]
    public void Save_BadNumber_IsRefusedWithFocus(string field, string focusName)
    {
        var presenter = NewPresenter();
        var state = presenter.LoadFields();
        typeof(SettingsFormState).GetProperty(field)!.SetValue(state, "not-a-number");

        var result = presenter.TrySave(state);

        Assert.Equal(SettingsSaveOutcome.Invalid, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Equal(Enum.Parse<SettingsFocusField>(focusName), result.Focus);
    }

    [Theory(DisplayName = "Save refuses a number one past each end of its range")]
    [InlineData(nameof(SettingsFormState.ClickZoomPercentText), 9)]
    [InlineData(nameof(SettingsFormState.ClickZoomPercentText), 801)]
    [InlineData(nameof(SettingsFormState.PreloadForwardText), -1)]
    public void Save_NumberOutsideRange_IsRefused(string field, int value)
    {
        var presenter = NewPresenter();
        var state = presenter.LoadFields();
        typeof(SettingsFormState).GetProperty(field)!.SetValue(state, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(SettingsSaveOutcome.Invalid, presenter.TrySave(state).Outcome);
    }

    [Fact(DisplayName = "Save accepts both ends of the click-zoom range")]
    public void Save_ClickZoomRangeEnds_AreAccepted()
    {
        var presenter = NewPresenter();
        var state = presenter.LoadFields();
        foreach (var value in new[] { AppSettings.MinClickZoomPercent, AppSettings.MaxClickZoomPercent })
        {
            state.ClickZoomPercentText = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(state).Outcome);
            Assert.Equal(value, presenter.Settings.ClickZoomPercent);
        }
    }

    [Fact(DisplayName = "Validation runs in the old Save order: the first refusal wins and earlier rows are already written")]
    public void Save_FirstRefusalWins_AndEarlierRowsAreWritten()
    {
        var presenter = NewPresenter();
        var state = presenter.LoadFields();
        state.ViewModeIndex = 4;                       // written before the numbers are validated
        state.ClickZoomPercentText = "x";              // first refusal
        state.ArrowPanStepText = "y";                  // would also be refused, but later
        state.PreloadForwardText = "z";

        var result = presenter.TrySave(state);

        Assert.Equal(SettingsSaveOutcome.Invalid, result.Outcome);
        Assert.Equal(Tr.DialogSettingsInvalidClickZoomPercent(AppSettings.MinClickZoomPercent, AppSettings.MaxClickZoomPercent), result.Message);
        Assert.Equal(SettingsFocusField.None, result.Focus);
        Assert.Equal(InitialViewMode.Percent100, presenter.Settings.InitialViewMode);
    }

    [Fact(DisplayName = "Both preload counts are validated before either is written")]
    public void Save_PreloadCounts_AreAllOrNothing()
    {
        var presenter = NewPresenter();
        var original = presenter.Settings.PreloadForwardCount;
        var state = presenter.LoadFields();
        state.PreloadForwardText = PerformanceOptions.MaxPreloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        state.PreloadBackwardText = "bad";

        var result = presenter.TrySave(state);

        Assert.Equal(SettingsSaveOutcome.Invalid, result.Outcome);
        Assert.Equal(SettingsFocusField.PreloadBackward, result.Focus);
        Assert.Equal(original, presenter.Settings.PreloadForwardCount);
    }

    [Fact(DisplayName = "Auto-hide delays and the font size are validated before the toolbar/overlay values are written")]
    public void Save_AutoHideGroup_IsAllOrNothing()
    {
        var presenter = NewPresenter();
        var originalDelay = presenter.Settings.ToolbarAutoHideDelayMs;
        var state = presenter.LoadFields();
        state.ToolbarAutoHide = true;
        state.ToolbarAutoHideDelayText = AppSettings.MaxToolbarAutoHideDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        state.InfoOverlayFontSizeText = "NaN";

        Assert.Equal(SettingsSaveOutcome.Invalid, presenter.TrySave(state).Outcome);
        Assert.False(presenter.Settings.ToolbarAutoHide);
        Assert.Equal(originalDelay, presenter.Settings.ToolbarAutoHideDelayMs);
    }

    [Fact(DisplayName = "The font size is read with the invariant culture and an infinite value is refused")]
    public void Save_FontSize_UsesInvariantCultureAndRefusesInfinity()
    {
        var presenter = NewPresenter();
        var state = presenter.LoadFields();
        state.InfoOverlayFontSizeText = "Infinity";
        Assert.Equal(SettingsSaveOutcome.Invalid, presenter.TrySave(state).Outcome);

        state.InfoOverlayFontSizeText = "14.5";
        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(state).Outcome);
        Assert.Equal(14.5, presenter.Settings.InfoOverlayFontSize);
    }

    [Fact(DisplayName = "Out-of-range combo indexes fall back to the documented defaults instead of failing")]
    public void Save_UnknownComboIndexes_FallBack()
    {
        var presenter = NewPresenter(new AppSettings { ImageSortMode = ImageSortMode.SizeAscending });
        var state = presenter.LoadFields();
        state.ViewModeIndex = -1;
        state.FitWidthAnchorIndex = 99;
        state.MiddleClickActionIndex = 99;
        state.SortModeTag = null;
        state.DecoderBackendIndex = 7;
        state.RawPairModeIndex = 7;

        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(state).Outcome);
        Assert.Equal(InitialViewMode.Fit, presenter.Settings.InitialViewMode);
        Assert.Equal(FitWidthAnchor.Centre, presenter.Settings.FitWidthAnchor);
        Assert.Equal(MiddleClickAction.ActualSize, presenter.Settings.MiddleClickAction);
        Assert.Equal(ImageSortMode.Name, presenter.Settings.ImageSortMode);
        Assert.Equal(DecoderBackend.Wpf, presenter.Settings.DecoderBackend);
        Assert.Equal(RawPairMode.Separate, presenter.Settings.RawPairMode);
    }

    [Fact(DisplayName = "A stray Percent400 loads on the 200 % item and the external editor path is trimmed on Save")]
    public void Load_Percent400_LandsOn200_AndPathIsTrimmed()
    {
        var presenter = NewPresenter(new AppSettings { InitialViewMode = InitialViewMode.Percent400 });
        var state = presenter.LoadFields();
        Assert.Equal(5, state.ViewModeIndex);

        state.ExternalEditorPath = "  C:\\Tools\\editor.exe  ";
        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(state).Outcome);
        Assert.Equal(InitialViewMode.Percent200, presenter.Settings.InitialViewMode);
        Assert.Equal("C:\\Tools\\editor.exe", presenter.Settings.ExternalEditorPath);
    }

    // ---- Shortcuts ----

    [Fact(DisplayName = "Two mandatory shortcuts that canonicalize to the same key are refused")]
    public void Save_DuplicateMandatoryShortcut_IsRefused()
    {
        var presenter = NewPresenter();
        var state = presenter.LoadFields();
        state.ShortcutTexts[nameof(ShortcutMappings.Next)] = "Right";
        state.ShortcutTexts[nameof(ShortcutMappings.Previous)] = "right";

        var result = presenter.TrySave(state);

        Assert.Equal(SettingsSaveOutcome.Invalid, result.Outcome);
        Assert.Equal(Tr.DialogSettingsInvalidShortcuts, result.Message);
    }

    [Fact(DisplayName = "A mandatory shortcut that is not a key name is refused; an empty optional one is accepted")]
    public void Save_MandatoryNeedsAKey_OptionalMayBeEmpty()
    {
        var presenter = NewPresenter();
        var state = presenter.LoadFields();
        state.ShortcutTexts[nameof(ShortcutMappings.Next)] = "NotAKey";
        Assert.Equal(Tr.DialogSettingsInvalidShortcuts, presenter.TrySave(state).Message);

        state.ShortcutTexts[nameof(ShortcutMappings.Next)] = "Right";
        state.ShortcutTexts[nameof(ShortcutMappings.LastImage)] = "";
        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(state).Outcome);
        Assert.Equal("", presenter.Settings.Shortcuts.LastImage);
    }

    [Fact(DisplayName = "A non-empty optional shortcut must still be a real key name")]
    public void Save_OptionalShortcutWithJunk_IsRefused()
    {
        var presenter = NewPresenter();
        var state = presenter.LoadFields();
        state.ShortcutTexts[nameof(ShortcutMappings.ClickZoom)] = "Nope";

        var result = presenter.TrySave(state);

        Assert.Equal(SettingsSaveOutcome.Invalid, result.Outcome);
        Assert.Equal(Tr.DialogSettingsInvalidShortcuts, result.Message);
    }

    [Fact(DisplayName = "An optional shortcut equal to a mandatory one is refused by the shared validator with its own message")]
    public void Save_OptionalConflictingWithMandatory_IsRefusedByValidator()
    {
        var presenter = NewPresenter();
        var state = presenter.LoadFields();
        state.ShortcutTexts[nameof(ShortcutMappings.MoveToFolder)] = state.ShortcutTexts[nameof(ShortcutMappings.Next)];

        var result = presenter.TrySave(state);

        Assert.Equal(SettingsSaveOutcome.Invalid, result.Outcome);
        Assert.NotEqual(Tr.DialogSettingsInvalidShortcuts, result.Message);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact(DisplayName = "Shortcuts are saved canonicalized (aliases collapse to one name)")]
    public void Save_ShortcutsAreCanonicalized()
    {
        var presenter = NewPresenter();
        var state = presenter.LoadFields();
        state.ShortcutTexts[nameof(ShortcutMappings.Next)] = " right ";

        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(state).Outcome);
        Assert.Equal(ShortcutKeyCanonical.Canonicalize(" right "), presenter.Settings.Shortcuts.Next);
    }

    [Fact(DisplayName = "The live duplicate warning follows the typed boxes and never throws on broken actions JSON")]
    public void DuplicateWarning_FollowsTypedText()
    {
        var presenter = NewPresenter();
        var state = presenter.LoadFields();
        Assert.Null(presenter.DuplicateShortcutWarning(state.ShortcutTexts, state.ActionsJson));

        var typed = new Dictionary<string, string>(state.ShortcutTexts)
        {
            [nameof(ShortcutMappings.Next)] = "Right",
            [nameof(ShortcutMappings.Previous)] = "Right",
        };
        Assert.False(string.IsNullOrWhiteSpace(presenter.DuplicateShortcutWarning(typed, state.ActionsJson)));
        Assert.False(string.IsNullOrWhiteSpace(presenter.DuplicateShortcutWarning(typed, "{ not json")));
    }

    [Fact(DisplayName = "An action shortcut equal to a typed shortcut shows up in the live warning (actions JSON is parsed once per text)")]
    public void DuplicateWarning_SeesActionShortcuts()
    {
        var presenter = NewPresenter(new AppSettings { Actions = [new ReviewAction { Name = "A", Shortcut = "F8", Destination = "Out" }] });
        var state = presenter.LoadFields();
        var typed = new Dictionary<string, string>(state.ShortcutTexts) { [nameof(ShortcutMappings.Next)] = "F8" };

        Assert.False(string.IsNullOrWhiteSpace(presenter.DuplicateShortcutWarning(typed, state.ActionsJson)));
        Assert.False(string.IsNullOrWhiteSpace(presenter.DuplicateShortcutWarning(typed, state.ActionsJson))); // cached parse, same answer
        Assert.Null(presenter.DuplicateShortcutWarning(state.ShortcutTexts, state.ActionsJson));
    }

    [Fact(DisplayName = "Shortcut capture lets Tab/Escape/modifiers through and canonicalizes real keys")]
    public void CaptureShortcut_ReservedKeysPassThrough()
    {
        Assert.Null(SettingsPresenter.CaptureShortcut(PhotoReview.App.Input.KeyId.Tab, "Tab"));
        Assert.Null(SettingsPresenter.CaptureShortcut(PhotoReview.App.Input.KeyId.Escape, "Escape"));
        Assert.Null(SettingsPresenter.CaptureShortcut(PhotoReview.App.Input.KeyId.LeftCtrl, "LeftCtrl"));
        Assert.Equal(ShortcutKeyCanonical.Canonicalize("Return"), SettingsPresenter.CaptureShortcut(PhotoReview.App.Input.KeyId.Return, "Return"));
    }

    [Fact(DisplayName = "The zoom summary lists the eight zoom shortcuts as typed and marks an empty one")]
    public void ZoomSummary_ListsTypedKeys()
    {
        var texts = new Dictionary<string, string>
        {
            [nameof(ShortcutMappings.ClickZoom)] = "  ",
            [nameof(ShortcutMappings.ToggleFit)] = " F ",
        };
        var summary = SettingsPresenter.ZoomShortcutSummary(texts);
        var lines = summary.Split(Environment.NewLine);
        Assert.Equal(8, lines.Length);
        Assert.Equal($"{Tr.SettingsShortcutClickZoom}: {Tr.SettingsZoomShortcutsNone}", lines[0]);
        Assert.Equal($"{Tr.SettingsShortcutToggleFit}: F", lines[1]);
    }

    // ---- Actions JSON box ----

    [Fact(DisplayName = "Actions typed as JSON are validated: bad JSON, a nameless action and a duplicate key are refused")]
    public void Save_ActionsJson_IsValidated()
    {
        var presenter = NewPresenter();
        var state = presenter.LoadFields();
        var refused = Tr.DialogSettingsInvalidActionsJson;

        state.ActionsJson = "{ not json";
        Assert.Equal(refused, presenter.TrySave(state).Message);

        state.ActionsJson = """[{"Name":"","Shortcut":"F7","Operation":0,"Destination":"Out"}]""";
        Assert.Equal(refused, presenter.TrySave(state).Message);

        state.ActionsJson = """[{"Name":"A","Shortcut":"F7","Operation":0,"Destination":"Out"},{"Name":"B","Shortcut":"f7","Operation":0,"Destination":"Out2"}]""";
        Assert.Equal(refused, presenter.TrySave(state).Message);

        state.ActionsJson = """[{"Name":"A","Shortcut":"F7","Operation":0,"Destination":"Out"}]""";
        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(state).Outcome);
        Assert.Equal("A", Assert.Single(presenter.Settings.Actions).Name);
    }

    [Fact(DisplayName = "An action destination that escapes the source folder is refused with the destination message")]
    public void Save_ActionDestinationProblem_IsRefused()
    {
        var presenter = NewPresenter();
        var state = presenter.LoadFields();
        state.ActionsJson = """[{"Name":"Esc","Shortcut":"F7","Operation":0,"Destination":"..\\Elsewhere"}]""";

        var result = presenter.TrySave(state);

        Assert.Equal(SettingsSaveOutcome.Invalid, result.Outcome);
        Assert.Equal(Tr.ActionProfilesErrorDestinationEscapes("Esc"), result.Message);
    }

    [Fact(DisplayName = "Vietnamese action names stay readable in the JSON box (relaxed escaping) and round-trip")]
    public void ActionsText_KeepsNonAsciiReadable()
    {
        var presenter = NewPresenter(new AppSettings { Actions = [new ReviewAction { Name = "Tuyệt vời", Shortcut = "F7", Destination = "Out" }] });
        var state = presenter.LoadFields();
        Assert.Contains("Tuyệt vời", state.ActionsJson, StringComparison.Ordinal);
        Assert.Equal("Tuyệt vời", Assert.Single(SettingsPresenter.ActionsForEditor(state.ActionsJson)).Name);
        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(state).Outcome);
    }

    [Fact(DisplayName = "The editor gets the built-in defaults for a literal null and the real list otherwise")]
    public void ActionsForEditor_NullGivesDefaults()
    {
        Assert.Equal(ReviewAction.Defaults().Count, SettingsPresenter.ActionsForEditor("null").Count);
        Assert.Throws<JsonException>(() => SettingsPresenter.ActionsForEditor("{ nope"));
    }

    // ---- Defaults ----

    [Fact(DisplayName = "Defaults resets the listed settings, keeps the language and actions, and returns the form for them")]
    public void Defaults_ResetsListedSettings_AndKeepsTheRest()
    {
        var custom = new AppSettings
        {
            ClickZoomPercent = 300, LoggingEnabled = true, InitialViewMode = InitialViewMode.Percent100,
            UiLanguage = "vi", Actions = [new ReviewAction { Name = "Keep", Shortcut = "F7", Destination = "Out" }],
            PreloadForwardCount = PerformanceOptions.MaxPreloadCount, ShowFolderInfo = true,
            Shortcuts = new ShortcutMappings { Next = "N" },
        };
        var presenter = NewPresenter(custom);

        var state = presenter.ApplyDefaults();

        Assert.Equal(AppSettings.DefaultClickZoomPercent, presenter.Settings.ClickZoomPercent);
        Assert.False(presenter.Settings.LoggingEnabled);
        Assert.Equal(InitialViewMode.Fit, presenter.Settings.InitialViewMode);
        Assert.Equal(PerformanceOptions.PreloadForwardCount, presenter.Settings.PreloadForwardCount);
        Assert.Equal(ShortcutMappings.Default().Next, presenter.Settings.Shortcuts.Next);
        Assert.Equal("vi", presenter.Settings.UiLanguage);
        Assert.Equal("Keep", Assert.Single(presenter.Settings.Actions).Name);
        Assert.Equal(AppSettings.DefaultClickZoomPercent.ToString(System.Globalization.CultureInfo.InvariantCulture), state.ClickZoomPercentText);
        Assert.False(state.ShowFolderInfo);
    }

    [Fact(DisplayName = "Defaults also clears the zoom indicator and the external editor path")]
    public void Defaults_ClearsIndicatorAndEditorPath()
    {
        var presenter = NewPresenter(new AppSettings { ShowZoomIndicator = true, ExternalEditorPath = "C:\\Tools\\editor.exe" });

        var state = presenter.ApplyDefaults();

        Assert.False(presenter.Settings.ShowZoomIndicator);
        Assert.Equal(string.Empty, presenter.Settings.ExternalEditorPath);
        Assert.False(state.ShowZoomIndicator);
        Assert.Equal(string.Empty, state.ExternalEditorPath);
    }

    // ---- Import / export ----

    [Fact(DisplayName = "Import keeps the valid part of a half-valid file, names the reset value and reloads the view once")]
    public void Import_HalfValidFile_KeepsValidPart()
    {
        var presenter = NewPresenter();
        var refreshed = 0;

        var ok = presenter.TryApplyImportedJson(
            """{"ConfigVersion":3,"UiLanguage":"en","ClickZoomPercent":"abc","KeyboardZoomStepPercent":40,"Shortcuts":{"Next":"N"}}""",
            () => refreshed++, out var message);

        Assert.True(ok);
        Assert.Contains(nameof(AppSettings.ClickZoomPercent), message);
        Assert.Equal(new AppSettings().ClickZoomPercent, presenter.Settings.ClickZoomPercent);
        Assert.Equal(40, presenter.Settings.KeyboardZoomStepPercent);
        Assert.Equal("N", presenter.Settings.Shortcuts.Next);
        Assert.Equal(1, refreshed);
    }

    [Fact(DisplayName = "Import of a file that cannot be applied restores the previous settings and reloads the view back")]
    public void Import_Unusable_RestoresPrevious()
    {
        var presenter = NewPresenter(new AppSettings { ClickZoomPercent = 250 });
        var before = presenter.Settings;
        var refreshed = 0;

        var ok = presenter.TryApplyImportedJson("this is not json", () => refreshed++, out var message);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Same(before, presenter.Settings);
        Assert.Equal(250, presenter.Settings.ClickZoomPercent);
        Assert.Equal(1, refreshed); // only the restore reload: the failure happened before the first one
    }

    [Fact(DisplayName = "Import when reloading the view throws still restores the previous settings and reports the failure")]
    public void Import_ViewReloadThrows_RestoresPrevious()
    {
        var presenter = NewPresenter(new AppSettings { ClickZoomPercent = 250 });
        var calls = 0;

        var ok = presenter.TryApplyImportedJson("""{"ConfigVersion":3,"ClickZoomPercent":400}""", () =>
        {
            if (calls++ == 0) throw new InvalidOperationException("view broke");
        }, out var message);

        Assert.False(ok);
        Assert.Contains("view broke", message, StringComparison.Ordinal);
        Assert.Equal(250, presenter.Settings.ClickZoomPercent);
        Assert.Equal(2, calls);
    }

    [Fact(DisplayName = "Export writes the config.json contract and Import of that file gives the same settings back")]
    public void ExportThenImport_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wp35g0-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            var source = NewPresenter(new AppSettings { ClickZoomPercent = 333, KeyboardZoomStepPercent = 45, LoggingEnabled = true });
            Assert.Null(source.ExportSettingsTo(path));

            var target = NewPresenter();
            var result = target.ImportSettingsFrom(path, () => { });

            Assert.Null(result.Error);
            Assert.Equal(333, target.Settings.ClickZoomPercent);
            Assert.Equal(45, target.Settings.KeyboardZoomStepPercent);
            Assert.True(target.Settings.LoggingEnabled);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact(DisplayName = "Export to a path that cannot be written and Import of a missing file return the localized reason")]
    public void ExportAndImport_FailuresAreReported()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wp35g0-missing-" + Guid.NewGuid().ToString("N"));
        var presenter = NewPresenter();

        Assert.NotNull(presenter.ExportSettingsTo(Path.Combine(dir, "nope", "settings.json")));
        var result = presenter.ImportSettingsFrom(Path.Combine(dir, "missing.json"), () => { });
        Assert.NotNull(result.Error);
        Assert.Null(result.RepairsNotice);
    }

    // ---- RAM slider arithmetic ----

    [Fact(DisplayName = "The RAM slider range grows with the preload window being typed and falls back to the saved value on bad text")]
    public void RamRange_FollowsTypedPreloadWindow()
    {
        // A small machine, so that a bigger window visibly raises the floor (on 32 GB the floors round to nearly the same percent).
        var presenter = new SettingsPresenter(new AppSettings(), physicalMemoryBytes: 8L * 1024 * 1024 * 1024);
        var max = System.Globalization.CultureInfo.InvariantCulture;
        var small = presenter.RamCacheRange("1", "1");
        var large = presenter.RamCacheRange(PerformanceOptions.MaxPreloadCount.ToString(max), PerformanceOptions.MaxPreloadCount.ToString(max));
        var saved = presenter.RamCacheRange(presenter.Settings.PreloadForwardCount.ToString(max), presenter.Settings.PreloadBackwardCount.ToString(max));

        Assert.Equal(PerformanceOptions.MaxImageCacheRamPercent, small.Max);
        Assert.True(large.Min > small.Min, $"large window floor {large.Min} should exceed small window floor {small.Min}");
        Assert.Equal(saved, presenter.RamCacheRange("garbage", null));
        Assert.Equal(saved, presenter.RamCacheRange("-5", "99999"));
        Assert.NotEqual(saved, large);

        // Each box counts on its own: changing only the forward box (backward = saved) moves the floor too.
        var savedBackward = presenter.Settings.PreloadBackwardCount.ToString(max);
        var forwardOnly = presenter.RamCacheRange(PerformanceOptions.MaxPreloadCount.ToString(max), savedBackward);
        var forwardSmall = presenter.RamCacheRange("1", savedBackward);
        Assert.True(forwardOnly.Min > forwardSmall.Min, $"forward-only {forwardOnly.Min} vs {forwardSmall.Min}");
        var backwardOnly = presenter.RamCacheRange(presenter.Settings.PreloadForwardCount.ToString(max), PerformanceOptions.MaxPreloadCount.ToString(max));
        var backwardSmall = presenter.RamCacheRange(presenter.Settings.PreloadForwardCount.ToString(max), "0");
        Assert.True(backwardOnly.Min > backwardSmall.Min, $"backward-only {backwardOnly.Min} vs {backwardSmall.Min}");
    }

    [Fact(DisplayName = "A saved RAM percent below the window minimum is clamped up on load; the value text shows percent and GB")]
    public void RamLoad_ClampsIntoRange()
    {
        var presenter = NewPresenter(new AppSettings { ImageCacheRamPercent = 1 });
        var state = presenter.LoadFields();
        var (min, max) = presenter.RamCacheRange(state.PreloadForwardText, state.PreloadBackwardText);

        Assert.Equal(min, (int)state.RamCachePercent);
        Assert.InRange((int)state.RamCachePercent, min, max);
        Assert.Contains("32.0", presenter.RamCacheValueText(100), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "The RAM slider position is saved rounded to a whole percent")]
    public void RamSave_RoundsTheSliderValue()
    {
        var presenter = NewPresenter();
        var state = presenter.LoadFields();
        state.RamCachePercent = 41.6;
        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(state).Outcome);
        Assert.Equal(42, presenter.Settings.ImageCacheRamPercent);
    }

    [Fact(DisplayName = "Toolbar opacity is clamped into its range on Save")]
    public void OpacitySave_IsClamped()
    {
        var presenter = NewPresenter();
        var state = presenter.LoadFields();
        state.ToolbarOpacityValue = 5000;
        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(state).Outcome);
        Assert.Equal(AppSettings.MaxToolbarOpacityPercent, presenter.Settings.ToolbarOpacityPercent);
    }

    // ---- Pages ----

    [Fact(DisplayName = "Page cycling wraps in both directions")]
    public void NextPageIndex_Wraps()
    {
        Assert.Equal(1, SettingsPresenter.NextPageIndex(0, 7, forward: true));
        Assert.Equal(0, SettingsPresenter.NextPageIndex(6, 7, forward: true));
        Assert.Equal(6, SettingsPresenter.NextPageIndex(0, 7, forward: false));
        Assert.Equal(7, SettingsPresenter.Pages.Count);
    }

    [Fact(DisplayName = "Every page has a non-empty localized title and an unknown key has none")]
    public void PageTitles_AreLocalized()
    {
        Assert.All(SettingsPresenter.Pages, page => Assert.False(string.IsNullOrWhiteSpace(SettingsPresenter.PageTitle(page.Key))));
        Assert.Equal(string.Empty, SettingsPresenter.PageTitle("NoSuchPage"));
    }

    [Fact(DisplayName = "The remembered page is used only when it is a known key")]
    public void InitialPage_UsesRememberedKnownKey()
    {
        var original = SettingsPresenter.RememberedPageKey;
        try
        {
            SettingsPresenter.RememberPage("Files");
            Assert.Equal("Files", SettingsPresenter.InitialPageKey);
            SettingsPresenter.RememberPage("Gone");
            Assert.Equal("General", SettingsPresenter.InitialPageKey);
        }
        finally { SettingsPresenter.RememberPage(original); }
    }

    // ---- Context menu rows ----

    [Fact(DisplayName = "There is one tick-box row per menu item; locked items carry the tooltip and children are indented")]
    public void ContextMenuRows_MatchTheItemList()
    {
        var rows = SettingsPresenter.ContextMenuRows();
        // FEAT-RENAME: the WPF window (default) lists no Win32-only item; the Win32 page asks for them too.
        Assert.Equal(ContextMenuItems.Items.Count(i => !i.ShellOnly), rows.Count);
        Assert.DoesNotContain(rows, r => r.Id == ContextMenuItemId.RenameFile);
        var win32Rows = SettingsPresenter.ContextMenuRows(includeShellOnly: true);
        Assert.Equal(ContextMenuItems.Items.Count, win32Rows.Count);
        Assert.Contains(win32Rows, r => r.Id == ContextMenuItemId.RenameFile);
        Assert.Equal(ContextMenuItems.Items.Select(i => i.Id), win32Rows.Select(r => r.Id)); // menu order
        foreach (var row in win32Rows)
        {
            var info = ContextMenuItems.Info(row.Id);
            Assert.Equal(info.Locked, row.Locked);
            Assert.Equal(info.Parent is not null, row.IsChild);
            Assert.Equal(info.Locked, row.LockedTooltip is not null);
            Assert.False(string.IsNullOrWhiteSpace(row.Label));
        }
    }

    // ---- Update check ----

    private sealed class FakeChecker(Func<CancellationToken, Task<UpdateCheckResult>> check) : IUpdateChecker
    {
        public Task<UpdateCheckResult> CheckAsync(string? currentVersion, CancellationToken cancellationToken) => check(cancellationToken);
    }

    [Fact(DisplayName = "Up-to-date shows its status and no download page")]
    public async Task UpdateCheck_UpToDate()
    {
        var presenter = NewPresenter(checker: new FakeChecker(_ => Task.FromResult(UpdateCheckResult.UpToDate("2.0.93"))));
        var display = await presenter.CheckForUpdateAsync("2.0.93");
        Assert.NotNull(display);
        Assert.Equal(Tr.UpdateStatusUpToDate("2.0.93"), display.StatusText);
        Assert.False(display.ShowOpenPage);
    }

    [Fact(DisplayName = "An available update offers only the validated release URL and opens exactly that")]
    public async Task UpdateCheck_Available_OffersValidatedUrl()
    {
        const string url = "https://github.com/ConanKLOP2/PhotoReview/releases/tag/v2.0.94";
        var presenter = NewPresenter(checker: new FakeChecker(_ => Task.FromResult(UpdateCheckResult.UpdateAvailable("2.0.94", url))));
        var display = await presenter.CheckForUpdateAsync("2.0.93");
        Assert.NotNull(display);
        Assert.True(display.ShowOpenPage);
        Assert.Equal(url, display.DownloadUrl);

        var opened = new List<string>();
        Assert.Null(presenter.OpenUpdatePage(opened.Add));
        Assert.Equal([url], opened);
    }

    [Fact(DisplayName = "A foreign download URL is never offered or opened, even if it was stored")]
    public async Task UpdateCheck_ForeignUrl_IsNeverOpened()
    {
        var presenter = NewPresenter(checker: new FakeChecker(_ => Task.FromResult(UpdateCheckResult.UpdateAvailable("2.0.94", "https://evil.example/x.exe"))));
        var display = await presenter.CheckForUpdateAsync("2.0.93");
        Assert.NotNull(display);
        Assert.False(display.ShowOpenPage);

        presenter.UpdateUrl = "https://evil.example/x.exe";
        var opened = new List<string>();
        Assert.Null(presenter.OpenUpdatePage(opened.Add));
        Assert.Empty(opened);
    }

    [Theory(DisplayName = "Each failure code maps to its own status text")]
    [InlineData(UpdateFailure.Offline)]
    [InlineData(UpdateFailure.Timeout)]
    [InlineData(UpdateFailure.RateLimited)]
    [InlineData(UpdateFailure.InvalidVersion)]
    [InlineData(UpdateFailure.BadResponse)]
    public async Task UpdateCheck_Failures_HaveTheirOwnText(UpdateFailure failure)
    {
        var presenter = NewPresenter(checker: new FakeChecker(_ => Task.FromResult(UpdateCheckResult.Failed(failure))));
        var display = await presenter.CheckForUpdateAsync("2.0.93");
        Assert.NotNull(display);
        Assert.Equal(failure switch
        {
            UpdateFailure.Offline => Tr.UpdateStatusFailedOffline,
            UpdateFailure.Timeout => Tr.UpdateStatusFailedTimeout,
            UpdateFailure.RateLimited => Tr.UpdateStatusFailedRateLimited,
            UpdateFailure.InvalidVersion => Tr.UpdateStatusFailedInvalidVersion,
            _ => Tr.UpdateStatusFailedBadResponse,
        }, display.StatusText);
        Assert.False(display.ShowOpenPage);
    }

    [Fact(DisplayName = "A checker that throws is shown as a bad response, not as a crash")]
    public async Task UpdateCheck_CheckerThrows_IsABadResponse()
    {
        var presenter = NewPresenter(checker: new FakeChecker(_ => throw new InvalidOperationException("boom")));
        var display = await presenter.CheckForUpdateAsync("2.0.93");
        Assert.Equal(Tr.UpdateStatusFailedBadResponse, display!.StatusText);
    }

    [Fact(DisplayName = "Cancelling a running check (dialog closed) yields no display")]
    public async Task UpdateCheck_Cancelled_YieldsNothing()
    {
        var started = new TaskCompletionSource();
        var presenter = NewPresenter(checker: new FakeChecker(async token =>
        {
            started.SetResult();
            await new TaskCompletionSource().Task.WaitAsync(token); // never completes: only the token ends it
            return UpdateCheckResult.UpToDate("x");
        }));

        var pending = presenter.CheckForUpdateAsync("2.0.93");
        await started.Task;
        presenter.CancelUpdateCheck();

        Assert.Null(await pending);
    }

    [Fact(DisplayName = "An open-page failure is reported as a status text")]
    public async Task OpenUpdatePage_Failure_IsReported()
    {
        const string url = "https://github.com/ConanKLOP2/PhotoReview/releases/tag/v2.0.94";
        var presenter = NewPresenter(checker: new FakeChecker(_ => Task.FromResult(UpdateCheckResult.UpdateAvailable("2.0.94", url))));
        await presenter.CheckForUpdateAsync("2.0.93");

        var status = presenter.OpenUpdatePage(_ => throw new InvalidOperationException("no browser"));

        Assert.Equal(Tr.UpdateOpenPageFailed, status);
    }

    // ---- Explorer integration ----

    private sealed class FakeShell : IShellIntegration
    {
        public ShellMenuState State { get; set; } = ShellMenuState.NotRegistered;
        public Exception? Throws { get; set; }
        public List<string> Calls { get; } = [];

        public ShellMenuState GetState(string executablePath) => Throws is null ? State : throw Throws;

        public void Register(string executablePath)
        {
            Calls.Add("register " + executablePath);
            if (Throws is not null) throw Throws;
            State = ShellMenuState.Registered;
        }

        public void Unregister()
        {
            Calls.Add("unregister");
            State = ShellMenuState.NotRegistered;
        }
    }

    private static string ExistingExecutable() => Path.Combine(Environment.SystemDirectory, "notepad.exe");

    [Fact(DisplayName = "Explorer integration states map to text and button availability")]
    public void ShellStatus_MapsStates()
    {
        var shell = new FakeShell();
        var presenter = NewPresenter();
        presenter.ShellIntegration = shell;
        presenter.ExecutablePath = ExistingExecutable();

        var notRegistered = presenter.RefreshShellStatus();
        Assert.Equal(Tr.SettingsShellStatusNotRegistered, notRegistered.Text);
        Assert.True(notRegistered.AddEnabled);
        Assert.False(notRegistered.RemoveEnabled);
        Assert.Equal(Tr.SettingsShellAdd, notRegistered.AddLabel);

        shell.State = ShellMenuState.Registered;
        var registered = presenter.RefreshShellStatus();
        Assert.Equal(Tr.SettingsShellStatusRegistered, registered.Text);
        Assert.False(registered.AddEnabled);
        Assert.True(registered.RemoveEnabled);

        shell.State = ShellMenuState.RegisteredElsewhere;
        var elsewhere = presenter.RefreshShellStatus();
        Assert.Equal(Tr.SettingsShellStatusElsewhere, elsewhere.Text);
        Assert.True(elsewhere.AddEnabled);
        Assert.True(elsewhere.RemoveEnabled);
        Assert.Equal(Tr.SettingsShellUpdate, elsewhere.AddLabel);
    }

    [Fact(DisplayName = "Without a usable executable the integration is unavailable and both buttons are off")]
    public void ShellStatus_NoExecutable_IsUnavailable()
    {
        var presenter = NewPresenter();
        presenter.ShellIntegration = new FakeShell();
        presenter.ExecutablePath = null;

        var status = presenter.RefreshShellStatus();

        Assert.Equal(Tr.SettingsShellStatusUnavailable, status.Text);
        Assert.False(status.AddEnabled);
        Assert.False(status.RemoveEnabled);
        Assert.Null(status.AddLabel);
    }

    [Fact(DisplayName = "The dotnet host is never registered as the Explorer command")]
    public void ShellStatus_DotnetHost_IsUnavailable()
    {
        var presenter = NewPresenter();
        presenter.ShellIntegration = new FakeShell();
        presenter.ExecutablePath = Path.Combine(Path.GetTempPath(), "dotnet.exe");
        Assert.Equal(Tr.SettingsShellStatusUnavailable, presenter.RefreshShellStatus().Text);
    }

    [Fact(DisplayName = "Registering and removing go through the seam and report the new state; a failure replaces the text")]
    public void ShellChange_RegistersRemovesAndReportsFailure()
    {
        var shell = new FakeShell();
        var presenter = NewPresenter();
        presenter.ShellIntegration = shell;
        presenter.ExecutablePath = ExistingExecutable();

        var added = presenter.ChangeShellRegistration(register: true);
        Assert.Equal(["register " + ExistingExecutable()], shell.Calls);
        Assert.Equal(Tr.SettingsShellStatusRegistered, added.Text);

        var removed = presenter.ChangeShellRegistration(register: false);
        Assert.Equal(Tr.SettingsShellStatusNotRegistered, removed.Text);

        shell.Throws = new UnauthorizedAccessException("denied");
        var failed = presenter.ChangeShellRegistration(register: true);
        Assert.Equal(Tr.SettingsShellStatusError("denied"), failed.Text);
    }

    // ---- Language picker ----

    [Fact(DisplayName = "Without a localization service there is no language group")]
    public void Language_WithoutService_HasNoGroup()
    {
        var presenter = NewPresenter();
        Assert.False(presenter.HasLocalization);
        Assert.Null(presenter.LoadLanguages(null));
        Assert.Null(presenter.ReloadTranslations());
        Assert.Equal(new SettingsExplorerRequest(null, null), presenter.PrepareLanguagesFolder());
        Assert.Equal((null, null), presenter.ExportTranslation(null as LanguageOption));
    }

    [Fact(DisplayName = "The Win32/WPF-free log-location request names the log file")]
    public void LogLocation_NamesTheLogFile()
    {
        var request = SettingsPresenter.PrepareLogLocation();
        Assert.Null(request.ErrorMessage);
        Assert.Contains(AppLog.FilePath, request.Arguments, StringComparison.Ordinal);
        Assert.StartsWith("/select,", request.Arguments, StringComparison.Ordinal);
    }

    // ---- Misc ----

    [Fact(DisplayName = "The TurboJPEG option is greyed out only when a factory says the backend is not registered")]
    public void TurboJpegAvailability()
    {
        Assert.True(SettingsPresenter.IsTurboJpegAvailable(null));
    }
}
