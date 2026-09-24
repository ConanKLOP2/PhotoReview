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
    private AppSettings _current;

    public AppSettings Current => _current;
    public event EventHandler<AppSettings>? Changed;

    public SettingsStore(
        IAppPaths appPaths,
        IFileSystem fileSystem,
        ILog log,
        Action<string, Exception>? onStartupError = null)
    {
        _appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _onStartupError = onStartupError;
        _current = new AppSettings();
    }

    public AppSettings Load(string? path = null)
    {
        var filePath = path ?? _appPaths.ConfigFile;
        try
        {
            if (_fileSystem.FileExists(filePath))
            {
                var json = _fileSystem.ReadAllText(filePath);
                var loaded = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings) ?? new();
                Migrate(loaded);
                loaded.Shortcuts ??= ShortcutMappings.Default();
                loaded.Actions ??= ReviewAction.Defaults();
                _current = loaded;
                Changed?.Invoke(this, _current);
                return _current;
            }
        }
        catch (JsonException ex)
        {
            LogStartupError("Corrupt config.json detected, resetting to defaults", ex);
            try
            {
                _fileSystem.Copy(filePath, filePath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
            }
            catch
            {
                // Bỏ qua lỗi copy file hỏng
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStartupError("Config.json inaccessible, using in-memory defaults for this session", ex);
            _current = new AppSettings();
            Changed?.Invoke(this, _current);
            return _current;
        }

        var settings = new AppSettings();
        if (path is null)
        {
            Save(settings);
        }
        else
        {
            _current = settings;
            Changed?.Invoke(this, _current);
        }
        return _current;
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var filePath = _appPaths.ConfigFile;
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            _fileSystem.CreateDirectory(dir);
        }

        settings.ConfigVersion = AppSettings.CurrentConfigVersion;
        var json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        _fileSystem.WriteAllTextAtomic(filePath, json, durable: true);

        _current = settings;
        Changed?.Invoke(this, _current);
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
