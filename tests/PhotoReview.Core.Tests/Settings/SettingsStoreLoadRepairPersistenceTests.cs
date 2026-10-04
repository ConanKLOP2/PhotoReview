using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Localization;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>A load repair is written back once, so the start-up dialog does not repeat on every launch.</summary>
public sealed class SettingsStoreLoadRepairPersistenceTests
{
    private const string ConflictingConfig = """
        {"ConfigVersion":3,"Actions":[{"Name":"Mine","Shortcut":"K"}]}
        """; // ToggleKeepZoom's default "K" collides with the action

    private readonly InMemoryFileSystem _fs = new();
    private readonly AppPaths _paths = new(@"C:\Users\test\AppData\Local");

    private SettingsStore NewStore() => new(_paths, _fs, NullLog.Instance);

    [Fact]
    public void Load_DisabledShortcutConflict_IsPersistedSoTheSecondLoadReportsNothing()
    {
        _fs.AddFile(_paths.ConfigFile, ConflictingConfig);
        var first = NewStore();

        var loaded = first.Load();

        Assert.Contains("Shortcuts." + nameof(ShortcutMappings.ToggleKeepZoom), first.LastLoadRepairs);
        Assert.Equal("", loaded.Shortcuts.ToggleKeepZoom);
        var second = NewStore();
        var reloaded = second.Load();
        Assert.Empty(second.LastLoadRepairs);
        Assert.Equal("", reloaded.Shortcuts.ToggleKeepZoom);
        Assert.Equal("K", Assert.Single(reloaded.Actions).Shortcut); // the user's action binding is untouched
    }

    [Fact]
    public void Load_RepairThatCannotBePersisted_StillLoadsAndReportsTheRepair()
    {
        _fs.AddFile(_paths.ConfigFile, ConflictingConfig);
        _fs.WriteHook = _ => new IOException("read-only");
        var store = NewStore();
        var changed = 0;
        store.Changed += (_, _) => changed++;

        var loaded = store.Load();

        Assert.Contains("Shortcuts." + nameof(ShortcutMappings.ToggleKeepZoom), store.LastLoadRepairs);
        Assert.Equal("", loaded.Shortcuts.ToggleKeepZoom);
        Assert.Same(loaded, store.Current);
        Assert.Equal(1, changed);
    }

    [Fact(DisplayName = "R23: a Changed handler that throws IOException after a repaired config was written is called once, not misread as a failed write")]
    public void Load_RepairedConfig_ThrowingChangedHandlerIsInvokedOnce()
    {
        _fs.AddFile(_paths.ConfigFile, ConflictingConfig);
        var store = NewStore();
        var calls = 0;
        store.Changed += (_, _) => { calls++; throw new IOException("handler bug"); };

        Assert.Throws<IOException>(() => store.Load());

        Assert.Equal(1, calls);
        Assert.Equal("", NewStore().Load().Shortcuts.ToggleKeepZoom); // the repair was durably written
    }

    [Fact(DisplayName = "R23: a Changed handler that throws IOException after the first-run default config was written is called once")]
    public void Load_FirstRun_ThrowingChangedHandlerIsInvokedOnce()
    {
        var store = NewStore();
        var calls = 0;
        store.Changed += (_, _) => { calls++; throw new IOException("handler bug"); };

        Assert.Throws<IOException>(() => store.Load());

        Assert.Equal(1, calls);
        Assert.True(_fs.FileExists(_paths.ConfigFile));
    }

    [Fact]
    public void Load_RepairIsPersistedExactlyOnce_AndChangedIsRaisedOnce()
    {
        _fs.AddFile(_paths.ConfigFile, ConflictingConfig);
        var writes = 0;
        _fs.WriteHook = _ => { writes++; return null; };
        var store = NewStore();
        var changed = 0;
        store.Changed += (_, _) => changed++;

        store.Load();

        Assert.Equal(1, writes);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Load_ConfigFromNewerBuildWithRepair_IsNotRewrittenButRepairedInMemory()
    {
        var newer = """{"ConfigVersion":99,"FutureField":"keep me","Actions":[{"Name":"Mine","Shortcut":"K"}]}""";
        _fs.AddFile(_paths.ConfigFile, newer);
        var writes = 0;
        _fs.WriteHook = _ => { writes++; return null; };
        var store = NewStore();
        var changed = 0;
        store.Changed += (_, _) => changed++;

        var loaded = store.Load();

        Assert.Equal(0, writes);
        Assert.Equal(newer, _fs.ReadAllText(_paths.ConfigFile));
        Assert.Contains("Shortcuts." + nameof(ShortcutMappings.ToggleKeepZoom), store.LastLoadRepairs);
        Assert.Equal("", loaded.Shortcuts.ToggleKeepZoom);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Load_ValidConfigWithoutRepairs_DoesNotRewriteTheFile()
    {
        _fs.AddFile(_paths.ConfigFile, """{"ConfigVersion":3}""");
        var writes = 0;
        _fs.WriteHook = _ => { writes++; return null; };
        var store = NewStore();

        store.Load();

        Assert.Empty(store.LastLoadRepairs);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void Load_ExplicitPath_DoesNotWriteTheDefaultConfigFile()
    {
        var other = @"C:\elsewhere\config.json";
        _fs.AddFile(other, ConflictingConfig);
        var writes = 0;
        _fs.WriteHook = _ => { writes++; return null; };
        var store = NewStore();

        var loaded = store.Load(other);

        // Not vacuous: the explicit file really was read and repaired (only then is "no write" a statement about the default file).
        Assert.Contains("Shortcuts." + nameof(ShortcutMappings.ToggleKeepZoom), store.LastLoadRepairs);
        Assert.Equal("", loaded.Shortcuts.ToggleKeepZoom);
        Assert.Equal("K", Assert.Single(loaded.Actions).Shortcut);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void Build_ShortcutRepair_UsesTheTurnedOffTextNotTheResetText()
    {
        var text = SettingsLoadRepairText.Build(["Shortcuts.ToggleKeepZoom"]);

        Assert.Equal(Tr.SettingsLoadShortcutDisabled("ToggleKeepZoom"), text);
        Assert.DoesNotContain(Tr.SettingsLoadRepaired("ToggleKeepZoom"), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_OtherRepair_KeepsTheResetText()
    {
        Assert.Equal(Tr.SettingsLoadRepaired("PreloadWorkerCount, Actions"), SettingsLoadRepairText.Build(["PreloadWorkerCount", "Actions"]));
    }

    [Fact]
    public void Build_MixedRepairs_ListsTheResetTextThenOneLinePerDisabledShortcut()
    {
        var text = SettingsLoadRepairText.Build(["Shortcuts.ToggleKeepZoom", "Actions", "Shortcuts.CustomZoom", "Shortcuts"]);

        Assert.Equal(
            Tr.SettingsLoadRepaired("Actions, Shortcuts") + Environment.NewLine
            + Tr.SettingsLoadShortcutDisabled("ToggleKeepZoom") + Environment.NewLine
            + Tr.SettingsLoadShortcutDisabled("CustomZoom"),
            text);
    }

    [Fact]
    public void Build_NoRepairs_IsEmpty() => Assert.Equal(string.Empty, SettingsLoadRepairText.Build([]));
}
