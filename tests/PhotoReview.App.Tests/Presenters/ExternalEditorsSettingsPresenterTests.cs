using System.Text.Json;
using PhotoReview.App.Presenters;

namespace PhotoReview.App.Tests.Presenters;

/// <summary>
/// FEAT-EXTERNAL-EDITORS: the editors list, the default, the RAW switch and the default-editor shortcut through
/// <see cref="SettingsPresenter"/> / <c>SettingsFieldMap</c> (Load -&gt; Collect -&gt; Save), with no window.
/// </summary>
[Collection("GlobalState")]
public sealed class ExternalEditorsSettingsPresenterTests
{
    private const long PhysicalMemory = 32L * 1024 * 1024 * 1024;

    private static SettingsPresenter NewPresenter(AppSettings settings) => new(settings, physicalMemoryBytes: PhysicalMemory);

    private static AppSettings WithTwoEditors() => new()
    {
        ExternalEditors =
        [
            new ExternalEditor { Id = "lr", Name = "Lightroom", Path = @"C:\LR\lightroom.exe" },
            new ExternalEditor { Id = "tp", Name = "Topaz AI", Path = @"C:\TP\topaz.exe", Arguments = "-o {file}" },
        ],
        DefaultExternalEditorId = "tp",
        ExternalEditorPath = @"C:\TP\topaz.exe",
    };

    [Fact(DisplayName = "Load shows the list, the default, the RAW switch and the default editor's path")]
    public void Load_CarriesTheEditorsIntoTheForm()
    {
        var presenter = NewPresenter(WithTwoEditors());

        var state = presenter.LoadFields();

        Assert.Equal(["Lightroom", "Topaz AI"], state.ExternalEditors.Select(e => e.Name));
        Assert.Equal("tp", state.DefaultExternalEditorId);
        Assert.Equal(@"C:\TP\topaz.exe", state.ExternalEditorPath);
        Assert.True(state.ExternalEditorSendsRawOriginal);
    }

    [Fact(DisplayName = "An untouched form round-trips the list exactly and Save is a fixed point")]
    public void Save_Untouched_KeepsTheListAndIsStable()
    {
        var presenter = NewPresenter(WithTwoEditors());

        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(presenter.LoadFields()).Outcome);
        var first = JsonSerializer.Serialize(presenter.Settings, AppSettingsJsonContext.Default.AppSettings);
        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(presenter.LoadFields()).Outcome);

        Assert.Equal(first, JsonSerializer.Serialize(presenter.Settings, AppSettingsJsonContext.Default.AppSettings));
        Assert.Equal(["lr", "tp"], presenter.Settings.ExternalEditors.Select(e => e.Id));
        Assert.Equal("-o {file}", presenter.Settings.ExternalEditors[1].Arguments);
        Assert.Equal("tp", presenter.Settings.DefaultExternalEditorId);
    }

    [Fact(DisplayName = "Save writes an edited list, the new default, the RAW switch off and the mirrored path")]
    public void Save_EditedForm_IsWrittenAndThePathFollowsTheDefault()
    {
        var presenter = NewPresenter(WithTwoEditors());
        var state = presenter.LoadFields();
        state.ExternalEditors.Add(new ExternalEditor { Id = "dxo", Name = "DxO", Path = @"C:\DxO\dxo.exe" });
        state.DefaultExternalEditorId = "dxo";
        state.ExternalEditorPath = @"C:\DxO\dxo.exe"; // what the Files page does after the editors dialog
        state.ExternalEditorSendsRawOriginal = false;

        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(state).Outcome);

        Assert.Equal(["lr", "tp", "dxo"], presenter.Settings.ExternalEditors.Select(e => e.Id));
        Assert.Equal("dxo", presenter.Settings.DefaultExternalEditorId);
        Assert.Equal(@"C:\DxO\dxo.exe", presenter.Settings.ExternalEditorPath);
        Assert.False(presenter.Settings.ExternalEditorSendsRawOriginal);
    }

    [Fact(DisplayName = "A path typed into the single editor-path box lands on the default editor (the WPF window's only field)")]
    public void Save_TypedPath_MovesTheDefaultEditor()
    {
        var presenter = NewPresenter(WithTwoEditors());
        var state = presenter.LoadFields();
        state.ExternalEditorPath = @"  C:\New\topaz2.exe  ";

        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(state).Outcome);

        Assert.Equal(2, presenter.Settings.ExternalEditors.Count);
        Assert.Equal(@"C:\New\topaz2.exe", presenter.Settings.ExternalEditors[1].Path);
        Assert.Equal(@"C:\New\topaz2.exe", presenter.Settings.ExternalEditorPath);
        Assert.Equal(@"C:\LR\lightroom.exe", presenter.Settings.ExternalEditors[0].Path);
    }

    [Fact(DisplayName = "Clearing the path box removes the default editor and the next editor becomes the default")]
    public void Save_ClearedPath_RemovesTheDefaultEditor()
    {
        var presenter = NewPresenter(WithTwoEditors());
        var state = presenter.LoadFields();
        state.ExternalEditorPath = string.Empty;

        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(state).Outcome);

        Assert.Equal(["lr"], presenter.Settings.ExternalEditors.Select(e => e.Id));
        Assert.Equal("lr", presenter.Settings.DefaultExternalEditorId);
    }

    [Fact(DisplayName = "A path typed with no list yet creates the first (default) editor, named after the program")]
    public void Save_TypedPathWithoutList_CreatesAnEditor()
    {
        var presenter = NewPresenter(new AppSettings());
        var state = presenter.LoadFields();
        state.ExternalEditorPath = @"C:\Tools\Capture One\CaptureOne.exe";

        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(state).Outcome);

        var editor = Assert.Single(presenter.Settings.ExternalEditors);
        Assert.Equal("CaptureOne", editor.Name);
        Assert.Equal(editor.Id, presenter.Settings.DefaultExternalEditorId);
    }

    [Fact(DisplayName = "Defaults empties the list and the default and turns the RAW switch back on")]
    public void Defaults_ClearsTheList()
    {
        var settings = WithTwoEditors();
        settings.ExternalEditorSendsRawOriginal = false;
        var presenter = NewPresenter(settings);

        var state = presenter.ApplyDefaults();

        Assert.Empty(presenter.Settings.ExternalEditors);
        Assert.Equal(string.Empty, presenter.Settings.DefaultExternalEditorId);
        Assert.True(presenter.Settings.ExternalEditorSendsRawOriginal);
        Assert.Empty(state.ExternalEditors);
        Assert.Equal(string.Empty, state.ExternalEditorPath);
    }

    [Fact(DisplayName = "The default-editor shortcut is empty by default, round-trips, and a key already used by an action is refused")]
    public void Shortcut_RoundTripsAndConflictsAreRefused()
    {
        var presenter = NewPresenter(new AppSettings());
        var state = presenter.LoadFields();
        Assert.Equal(string.Empty, SettingsFieldMap.ShortcutText(state, nameof(ShortcutMappings.OpenInDefaultEditor)));

        state.ShortcutTexts[nameof(ShortcutMappings.OpenInDefaultEditor)] = "F9";
        Assert.Equal(SettingsSaveOutcome.Saved, presenter.TrySave(state).Outcome);
        Assert.Equal("F9", presenter.Settings.Shortcuts.OpenInDefaultEditor);

        var clash = presenter.LoadFields();
        clash.ShortcutTexts[nameof(ShortcutMappings.OpenInDefaultEditor)] = presenter.Settings.Actions[0].Shortcut;
        Assert.NotEqual(SettingsSaveOutcome.Saved, presenter.TrySave(clash).Outcome);
    }
}
