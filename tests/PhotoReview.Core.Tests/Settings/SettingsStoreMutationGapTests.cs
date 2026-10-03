using System.Text.RegularExpressions;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Tests.Fakes;

namespace PhotoReview.Core.Tests.Settings;

/// <summary>Mutation-testing gaps in <see cref="SettingsStore"/>: logging contracts, backup-name bound, directory creation, explicit-path load.</summary>
public sealed partial class SettingsStoreMutationGapTests
{
    private const string MistypedValueConfig = """
        {"ConfigVersion":3,"ClickZoomPercent":"abc"}
        """;

    private readonly InMemoryFileSystem _fs = new();
    private readonly AppPaths _paths = new(@"C:\Users\test\AppData\Local");
    private readonly MutationRecordingLog _log = new();
    private readonly List<string> _startupErrors = [];

    private SettingsStore NewStore(IFileSystem? fs = null, IKeyNameValidator? validator = null) =>
        new(_paths, fs ?? _fs, _log, (message, _) => _startupErrors.Add(message), validator);

    private sealed class RejectAllKeyNames : IKeyNameValidator
    {
        public bool IsValidKeyName(string keyName) => false;
    }

    [Fact]
    public void Constructor_CustomKeyNameValidator_IsExposedAndUsedForShortcutValidation()
    {
        var validator = new RejectAllKeyNames();
        var store = NewStore(validator: validator);

        Assert.Same(validator, store.KeyNames);
        Assert.NotNull(store.ValidateShortcuts(new AppSettings())); // the default shortcuts are unknown key names to this validator
    }

    [Fact]
    public void Constructor_NoValidator_FallsBackToAnyNonBlankName()
    {
        var store = NewStore();

        Assert.True(store.KeyNames.IsValidKeyName("F13"));
        Assert.False(store.KeyNames.IsValidKeyName("  "));
    }

    [Fact]
    public void Load_TransientReadFailure_IsRetriedAndLogged()
    {
        _fs.AddFile(_paths.ConfigFile, """{"ConfigVersion":3}""");
        var failures = 1;
        _fs.OpenReadHook = _ => failures-- > 0 ? new IOException("locked by antivirus") : null;
        var store = NewStore();

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Empty(_startupErrors);
        var info = Assert.Single(_log.Infos, m => m.Contains("retrying", StringComparison.Ordinal));
        Assert.Contains("IOException", info, StringComparison.Ordinal);
        Assert.Contains("locked by antivirus", info, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_CleanFile_ReportsNoRepairsAndLogsNothingAboutRepairsOrShortcuts()
    {
        var store = NewStore();
        store.Save(new AppSettings());

        store.Load();

        Assert.Empty(store.LastLoadRepairs);
        Assert.Empty(_log.Warnings);
        Assert.DoesNotContain(_log.Infos, m => m.Contains("Optional shortcuts", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_UnusableValue_LogsAWarningNamingTheResetSetting()
    {
        _fs.AddFile(_paths.ConfigFile, MistypedValueConfig);
        var store = NewStore();

        store.Load();

        Assert.Contains("ClickZoomPercent", store.LastLoadRepairs);
        var warning = Assert.Single(_log.Warnings);
        Assert.Contains("ClickZoomPercent", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_RepairThatCannotBePersisted_ReportsTheWriteFailureAndKeepsTheRepairInMemory()
    {
        _fs.AddFile(_paths.ConfigFile, MistypedValueConfig);
        _fs.WriteHook = _ => new IOException("read-only");
        var store = NewStore();
        var changed = 0;
        store.Changed += (_, _) => changed++;

        var loaded = store.Load();

        Assert.Contains(_startupErrors, m => m.Contains("repaired config.json", StringComparison.Ordinal));
        Assert.Same(loaded, store.Current);
        Assert.Contains("ClickZoomPercent", store.LastLoadRepairs);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Load_ExplicitPathThatDoesNotExist_ReturnsDefaultsMakesThemCurrentAndRaisesChanged()
    {
        var store = NewStore();
        var custom = new AppSettings { ClickZoomPercent = 321 };
        store.Save(custom);
        var changed = new List<AppSettings>();
        store.Changed += (_, s) => changed.Add(s);

        var loaded = store.Load(@"C:\elsewhere\missing.json");

        Assert.NotSame(custom, loaded);
        Assert.Same(loaded, store.Current);
        Assert.Equal(new AppSettings().ClickZoomPercent, loaded.ClickZoomPercent);
        Assert.Same(loaded, Assert.Single(changed));
        Assert.False(_fs.FileExists(@"C:\elsewhere\missing.json")); // an explicit path is never written
    }

    [Fact]
    public void Save_CreatesTheConfigDirectoryBeforeWriting()
    {
        var fs = MutationFileSystemProxy.Create(_fs, out var proxy);
        var store = NewStore(fs);

        store.Save(new AppSettings());

        var calls = proxy.Calls;
        var create = calls.ToList().IndexOf("CreateDirectory");
        var write = calls.ToList().IndexOf("WriteAllTextAtomic");
        Assert.True(create >= 0, "CreateDirectory was not called");
        Assert.True(write > create, "the directory must exist before the atomic write");
    }

    [GeneratedRegex(@"\.corrupt-\d{14}(?:-(?<n>\d+))?$")]
    private static partial Regex BackupName();

    [Fact]
    public void Load_CorruptConfigWithEveryBackupNameTaken_UsesTheLastNumberedNameBelowOneHundred()
    {
        _fs.AddFile(_paths.ConfigFile, "{ not json");
        var fs = MutationFileSystemProxy.Create(_fs, out var proxy);
        string? copiedTo = null;
        proxy.Intercept = (name, args) =>
        {
            var path = (string)args[0]!;
            if (name == "FileExists" && BackupName().Match(path) is { Success: true } m)
            {
                // taken: the plain name and -2 .. -99; free: -100 and up
                var taken = !m.Groups["n"].Success || int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) < 100;
                return new(true, taken);
            }
            if (name == "Copy")
            {
                copiedTo = (string)args[1]!;
                return new(true, null);
            }
            return default;
        };
        var store = NewStore(fs);

        store.Load();

        Assert.NotNull(copiedTo);
        Assert.EndsWith("-99", copiedTo, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_CorruptConfigWithFirstBackupNameTaken_UsesTheSecondName()
    {
        _fs.AddFile(_paths.ConfigFile, "{ not json");
        var fs = MutationFileSystemProxy.Create(_fs, out var proxy);
        string? copiedTo = null;
        proxy.Intercept = (name, args) =>
        {
            var path = (string)args[0]!;
            if (name == "FileExists" && BackupName().Match(path) is { Success: true } m)
                return new(true, !m.Groups["n"].Success); // only the plain name is taken
            if (name == "Copy")
            {
                copiedTo = (string)args[1]!;
                return new(true, null);
            }
            return default;
        };
        var store = NewStore(fs);

        store.Load();

        Assert.NotNull(copiedTo);
        Assert.EndsWith("-2", copiedTo, StringComparison.Ordinal);
    }
}