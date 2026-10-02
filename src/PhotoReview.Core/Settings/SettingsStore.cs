using System.IO;
using System.Globalization;
using System.Text.Json;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;

namespace PhotoReview.Core.Settings;

public sealed class SettingsStore
{
    private readonly IAppPaths _appPaths;
    private readonly IFileSystem _fileSystem;
    private readonly ILog _log;
    private readonly Action<string, Exception>? _onStartupError;
    private readonly SettingsValidator _settingsValidator;
    private AppSettings _current;
    private bool _keepCorruptFile; // the corrupt config.json could not be backed up: never overwrite the only copy this session

    public AppSettings Current => _current;

    /// <summary>
    /// The key-name validator this store's <see cref="ValidateShortcuts"/> uses (AR11a: replaces the old static
    /// <c>AppSettings.Validator</c>). Defaults to a permissive fallback (any non-blank name) when the caller does not
    /// have a platform-specific one (e.g. <c>WpfKeyNameValidator</c>, wired in <c>App.ConfigureServices</c>).
    /// </summary>
    public IKeyNameValidator KeyNames { get; }

    /// <summary>
    /// Names of the settings the last <see cref="Load"/> had to reset because <c>config.json</c> held unusable values
    /// (R2-F-04); empty when the file was valid. The app shows a one-time warning for them.
    /// </summary>
    public IReadOnlyList<string> LastLoadRepairs { get; private set; } = [];
    public event EventHandler<AppSettings>? Changed;

    public SettingsStore(
        IAppPaths appPaths,
        IFileSystem fileSystem,
        ILog log,
        Action<string, Exception>? onStartupError = null,
        IKeyNameValidator? keyNameValidator = null)
    {
        _appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _onStartupError = onStartupError;
        KeyNames = keyNameValidator ?? new SimpleKeyNameValidator();
        _settingsValidator = new SettingsValidator(KeyNames);
        _current = new AppSettings();
    }

    /// <summary>
    /// AR11a: replaces the old static <c>AppSettings.ValidateShortcuts</c> -- uses this store's <see cref="KeyNames"/>
    /// instead of a process-wide mutable static.
    /// </summary>
    public string? ValidateShortcuts(AppSettings settings) => _settingsValidator.ValidateShortcuts(settings);

    /// <summary>Permissive fallback used when a caller does not wire a platform-specific <see cref="IKeyNameValidator"/>.</summary>
    private sealed class SimpleKeyNameValidator : IKeyNameValidator
    {
        public bool IsValidKeyName(string keyName) => !string.IsNullOrWhiteSpace(keyName);
    }

    // A transient lock (antivirus scan, a sibling instance mid-rename) must not demote the whole session to defaults: retry the
    // read a few times with a short back-off, then let the caller degrade exactly as before.
    private static readonly int[] ReadRetryDelaysMs = [50, 100, 200, 400];

    private string ReadAllTextWithRetry(string filePath)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return _fileSystem.ReadAllText(filePath);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < ReadRetryDelaysMs.Length)
            {
                _log.Info(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"config.json read failed ({ex.GetType().Name}), retrying: {ex.Message}"));
                Thread.Sleep(ReadRetryDelaysMs[attempt]);
            }
        }
    }

    public AppSettings Load(string? path = null)
    {
        var filePath = path ?? _appPaths.ConfigFile;
        LastLoadRepairs = [];
        _keepCorruptFile = false;
        AppSettings? ready = null;
        try
        {
            if (_fileSystem.FileExists(filePath))
            {
                var json = ReadAllTextWithRetry(filePath);
                AppSettings loaded;
                IReadOnlyList<string> salvagedFrom = [];
                try
                {
                    loaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings) ?? new();
                }
                catch (JsonException ex) when (TrySalvage(json, out var salvaged, out var unusable))
                {
                    // One mistyped value (e.g. "ClickZoomPercent": "abc") must not throw away the user's actions and shortcuts:
                    // keep every property that reads fine, reset only the unusable ones, and keep the original as a backup.
                    loaded = salvaged;
                    salvagedFrom = unusable;
                    LogStartupError("config.json has unreadable values (" + string.Join(", ", unusable) + "); the rest was kept", ex);
                    try
                    {
                        _fileSystem.Copy(filePath, UniqueBackupPath(filePath));
                    }
                    catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
                    {
                        _keepCorruptFile = path is null; // never overwrite the only copy of the unreadable values
                        LogStartupError("Could not back up config.json with unreadable values", copyEx);
                    }
                }
                FinishDeserialize(loaded, json);
                LastLoadRepairs = [.. salvagedFrom, .. SettingsNormalizer.Normalize(loaded).Except(salvagedFrom, StringComparer.Ordinal)];
                if (LastLoadRepairs.Count > 0)
                    _log.Warn("config.json had invalid values, reset to defaults: " + string.Join(", ", LastLoadRepairs));
                var disabledShortcuts = SettingsNormalizer.DisableConflictingOptionalShortcuts(loaded);
                if (disabledShortcuts.Count > 0)
                {
                    _log.Info("Optional shortcuts disabled because their key is already bound: " + string.Join(", ", disabledShortcuts));
                    // Surface it too (the startup dialog lists LastLoadRepairs): the user must not silently lose a shortcut.
                    LastLoadRepairs = [.. LastLoadRepairs, .. disabledShortcuts.Select(name => "Shortcuts." + name)];
                }
                _current = loaded;
                // Write the repaired settings back once so the start-up dialog does not repeat on every launch (the repairs
                // are recomputed from the file on each Load). Save raises Changed itself; only the default file is rewritten.
                // A file from a NEWER build is never rewritten: Save would drop its unknown fields and stamp the older version.
                if (LastLoadRepairs.Count > 0 && path is null && loaded.ConfigVersion <= AppSettings.CurrentConfigVersion
                    && TryPersistRepairs(loaded)) return _current;
                ready = _current; // RV-S04: Changed is raised after the try, so a throwing handler cannot be mistaken for an IO failure
            }
        }
        catch (JsonException ex)
        {
            LogStartupError("Corrupt config.json detected, resetting to defaults", ex);
            try
            {
                _fileSystem.Copy(filePath, UniqueBackupPath(filePath));
            }
            catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
            {
                // The corrupt file could not be preserved: keep defaults in memory and do not overwrite the only copy.
                LogStartupError("Could not back up corrupt config.json, using in-memory defaults for this session", copyEx);
                _keepCorruptFile = path is null;
                _current = new AppSettings();
                Changed?.Invoke(this, _current);
                return _current;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStartupError("Config.json inaccessible, using in-memory defaults for this session", ex);
            _keepCorruptFile = path is null; // a briefly locked but valid config must not be replaced by defaults on the next Save
            _current = new AppSettings();
            Changed?.Invoke(this, _current);
            return _current;
        }

        if (ready is not null)
        {
            Changed?.Invoke(this, ready);
            return ready;
        }

        var settings = new AppSettings();
        if (path is null)
        {
            try
            {
                Save(settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // First run / corrupt config: a failed default write (disk full, locked or read-only folder) must not fail startup.
                LogStartupError("Could not write default config.json, using in-memory defaults for this session", ex);
                _current = settings;
                Changed?.Invoke(this, _current);
            }
        }
        else
        {
            _current = settings;
            Changed?.Invoke(this, _current);
        }
        return _current;
    }

    /// <summary>Saves the repaired settings; false (nothing thrown) when the file cannot be written, so the repairs stay in memory only.</summary>
    private bool TryPersistRepairs(AppSettings repaired)
    {
        try
        {
            Save(repaired);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStartupError("Could not write the repaired config.json; the repairs apply to this session only", ex);
            return false;
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_keepCorruptFile)
        {
            _log.Warn("Settings kept in memory only: config.json is preserved untouched because it could not be read or backed up");
            _current = settings;
            Changed?.Invoke(this, _current);
            return;
        }
        var filePath = _appPaths.ConfigFile;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            _fileSystem.CreateDirectory(dir);
        }

        ShortcutKeyCanonical.CanonicalizeAll(settings); // Q-R25: always saved canonical
        settings.ConfigVersion = AppSettings.CurrentConfigVersion;
        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        _fileSystem.WriteAllTextAtomic(filePath, json, durable: true);

        _current = settings;
        Changed?.Invoke(this, _current);
    }

    /// <summary>
    /// Reads <paramref name="json"/> property by property so that unusable values are skipped instead of failing the
    /// whole file. False when the text is not a JSON object at all (then the caller treats the file as corrupt).
    /// </summary>
    private static bool TrySalvage(string json, out AppSettings settings, out IReadOnlyList<string> unusable)
    {
        settings = new AppSettings();
        var bad = new List<string>();
        unusable = bad;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var typeInfo = AppSettingsJsonContext.Default.AppSettings;
            foreach (var element in doc.RootElement.EnumerateObject())
            {
                var info = typeInfo.Properties.FirstOrDefault(p => string.Equals(p.Name, element.Name, StringComparison.Ordinal));
                if (info?.Set is null) continue; // unknown property
                try
                {
                    var value = JsonSerializer.Deserialize(element.Value, info.PropertyType, AppSettingsJsonContext.Default);
                    if (value is null && info.PropertyType.IsValueType) throw new JsonException("null for a value type");
                    info.Set(settings, value);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    bad.Add(element.Name);
                }
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Second resolution is not unique when the app crash-loops on a bad file: never overwrite (or fail on) an earlier backup.
    private string UniqueBackupPath(string filePath)
    {
        var stem = filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var candidate = stem;
        for (var i = 2; i < 100 && _fileSystem.FileExists(candidate); i++) candidate = stem + "-" + i.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    private void LogStartupError(string message, Exception ex)
    {
        if (_onStartupError is not null)
        {
            _onStartupError(message, ex);
        }
        else
        {
            _log.Error(message, ex);
        }
    }

    /// <summary>
    /// AR11a: deserializes a config.json-shaped string and applies the same post-processing <see cref="Load"/>
    /// does (migrate + fill in missing <see cref="AppSettings.Shortcuts"/>/<see cref="AppSettings.Actions"/>), without
    /// touching disk. Internal test seam (<c>InternalsVisibleTo PhotoReview.Core.Tests</c>) for fixture-driven
    /// migration tests that used to go through the now-removed <c>AppSettings.Load(path)</c>.
    /// </summary>
    internal static AppSettings Parse(string json)
    {
        var loaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        FinishDeserialize(loaded, json);
        return loaded;
    }

    private static void FinishDeserialize(AppSettings loaded, string json)
    {
        MarkUnversionedAsLegacy(loaded, json);
        Migrate(loaded);
        loaded.Shortcuts ??= ShortcutMappings.Default();
        loaded.Actions ??= ReviewAction.Defaults();
    }

    /// <summary>
    /// <see cref="AppSettings.ConfigVersion"/> defaults to the current version, so a pre-versioned config (no key) would
    /// deserialize as current and skip every migration (Q-L1: such users must keep the Vietnamese UI). Peek at the raw JSON.
    /// </summary>
    private static void MarkUnversionedAsLegacy(AppSettings loaded, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind == JsonValueKind.Object && !doc.RootElement.TryGetProperty("ConfigVersion", out _))
                loaded.ConfigVersion = 1;
        }
        catch (JsonException)
        {
            // Not parseable here: leave the version as deserialized.
        }
    }

    public static void Migrate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.ConfigVersion < 2)
        {
            settings.Actions ??= ReviewAction.Defaults();
        }
        if (settings.ConfigVersion < 3)
        {
            // Q-L1 (ADR 0006): the UI was Vietnamese-only before version 3; existing users keep it.
            settings.UiLanguage = "vi";
        }
        if (settings.ConfigVersion < AppSettings.CurrentConfigVersion)
        {
            settings.ConfigVersion = AppSettings.CurrentConfigVersion;
        }
        if (string.IsNullOrWhiteSpace(settings.UiLanguage)) settings.UiLanguage = LanguageLoader.AutoCode;
        settings.Actions ??= ReviewAction.Defaults();
        settings.Shortcuts ??= ShortcutMappings.Default();
    }
}
