using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>"Move to… / Copy to…": shortcut defaults, validation (empty = disabled, duplicates) and settings round-trip.</summary>
[Trait("Category", "HotPath")]
public sealed class MoveCopyToSettingsTests
{
    private readonly InMemoryFileSystem _fileSystem = new();
    private readonly AppPaths _appPaths = new(@"C:\Users\test\AppData\Local");

    private SettingsStore NewStore() => new(_appPaths, _fileSystem, NullLog.Instance, (_, _) => { });

    /// <summary>Accepts any non-blank key name (the real WPF key check is covered by ShortcutKeyName tests).</summary>
    private sealed class AnyKeyValidator : IKeyNameValidator
    {
        public bool IsValidKeyName(string keyName) => !string.IsNullOrWhiteSpace(keyName);
    }

    private static string? Validate(AppSettings settings) => new SettingsValidator(new AnyKeyValidator()).ValidateShortcuts(settings);

    [Fact]
    public void Defaults_AreMAndY_NoLastFolders_ReuseOff()
    {
        var settings = new AppSettings();

        Assert.Equal("M", settings.Shortcuts.MoveToFolder);
        Assert.Equal("Y", settings.Shortcuts.CopyToFolder);
        Assert.Null(settings.LastMoveToFolder);
        Assert.Null(settings.LastCopyToFolder);
        Assert.False(settings.MoveCopyReuseLastFolder);
    }

    [Fact]
    public void DefaultKeys_DoNotClashWithAnyOtherDefaultBinding()
    {
        var settings = new AppSettings();
        var others = typeof(ShortcutMappings).GetProperties()
            .Where(p => p.Name is not (nameof(ShortcutMappings.MoveToFolder) or nameof(ShortcutMappings.CopyToFolder)))
            .Select(p => (string)p.GetValue(settings.Shortcuts)!)
            .Concat(settings.Actions.Select(a => a.Shortcut))
            // Keys other in-flight branches add as defaults (End, D1, I) must stay free too.
            .Concat(["End", "D1", "I"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain(settings.Shortcuts.MoveToFolder, others);
        Assert.DoesNotContain(settings.Shortcuts.CopyToFolder, others);
        Assert.Null(Validate(settings));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void EmptyShortcut_MeansDisabled_AndIsValid(string value)
    {
        var settings = new AppSettings();
        settings.Shortcuts.MoveToFolder = value;
        settings.Shortcuts.CopyToFolder = value;

        Assert.Null(Validate(settings));
    }

    [Fact]
    public void EmptyOtherShortcut_IsStillInvalid()
    {
        var settings = new AppSettings();
        settings.Shortcuts.Skip = "";

        Assert.Equal(PhotoReview.Core.Localization.Tr.CoreSettingsShortcutInvalid(nameof(ShortcutMappings.Skip)), Validate(settings));
    }

    [Fact]
    public void MoveToKey_DuplicatingABuiltInShortcut_IsReported()
    {
        var settings = new AppSettings();
        settings.Shortcuts.MoveToFolder = settings.Shortcuts.Next;

        var error = Validate(settings);

        Assert.NotNull(error);
        Assert.Contains(nameof(ShortcutMappings.MoveToFolder), error, StringComparison.Ordinal);
        Assert.Contains(nameof(ShortcutMappings.Next), error, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyToKey_DuplicatingAnActionOrMoveTo_IsReported()
    {
        var settings = new AppSettings();
        settings.Actions[0].Shortcut = "Y";
        var error = Validate(settings);
        Assert.NotNull(error);
        Assert.Contains(nameof(ShortcutMappings.CopyToFolder), error, StringComparison.Ordinal);

        settings = new AppSettings();
        settings.Shortcuts.CopyToFolder = "m";
        error = Validate(settings);
        Assert.NotNull(error);
        Assert.Contains(nameof(ShortcutMappings.MoveToFolder), error, StringComparison.Ordinal);
        Assert.Contains(nameof(ShortcutMappings.CopyToFolder), error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_OldConfigWithoutTheNewMembers_UsesDefaults()
    {
        _fileSystem.WriteAllTextAtomic(_appPaths.ConfigFile, """
        { "ConfigVersion": 3, "Shortcuts": { "Next": "Right", "Previous": "Left" } }
        """);

        var loaded = NewStore().Load();

        Assert.Equal("M", loaded.Shortcuts.MoveToFolder);
        Assert.Equal("Y", loaded.Shortcuts.CopyToFolder);
        Assert.Null(loaded.LastMoveToFolder);
        Assert.Null(loaded.LastCopyToFolder);
        Assert.False(loaded.MoveCopyReuseLastFolder);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllMembers()
    {
        var settings = new AppSettings
        {
            LastMoveToFolder = @"D:\Keep",
            LastCopyToFolder = @"E:\Backup",
            MoveCopyReuseLastFolder = true,
        };
        settings.Shortcuts.MoveToFolder = "F7";
        settings.Shortcuts.CopyToFolder = "";

        NewStore().Save(settings);
        var reloaded = NewStore().Load();

        Assert.Equal(@"D:\Keep", reloaded.LastMoveToFolder);
        Assert.Equal(@"E:\Backup", reloaded.LastCopyToFolder);
        Assert.True(reloaded.MoveCopyReuseLastFolder);
        Assert.Equal("F7", reloaded.Shortcuts.MoveToFolder);
        Assert.Equal("", reloaded.Shortcuts.CopyToFolder);
    }
}
