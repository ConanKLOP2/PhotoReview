using System.Text.Json;
using System.IO;

namespace PhotoReview.App;

public sealed class AppSettings
{
    public const int CurrentConfigVersion = 2;
    public int ConfigVersion { get; set; } = CurrentConfigVersion;
    public string Folder2Name { get; set; } = "Loai-2";
    public string InitialViewMode { get; set; } = "Fit";
    public string LoadingMode { get; set; } = "Preview";
    public string ImageSortMode { get; set; } = "PortraitFirst";
    public bool CompareHashEnabled { get; set; } = true;
    public bool CompareSizeEnabled { get; set; } = true;
    public List<ReviewAction> Actions { get; set; } = ReviewAction.Defaults();
    public ShortcutMappings Shortcuts { get; set; } = ShortcutMappings.Default();
    public static string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoReview", "config.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath)) ?? new();
                Migrate(loaded);
                loaded.Shortcuts ??= ShortcutMappings.Default();
                loaded.Actions ??= ReviewAction.Defaults();
                loaded.LoadingMode = NormalizeLoadingMode(loaded.LoadingMode);
                loaded.ImageSortMode = NormalizeImageSortMode(loaded.ImageSortMode);
                if (ValidateShortcuts(loaded) is not null)
                {
                    loaded.Shortcuts = ShortcutMappings.Default();
                    loaded.Actions = ReviewAction.Defaults();
                }
                return loaded;
            }
        }
        catch
        {
            try { File.Copy(ConfigPath, ConfigPath + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), overwrite: false); } catch { }
        }
        var settings = new AppSettings();
        Save(settings);
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        settings.ConfigVersion = CurrentConfigVersion;
        var temp = ConfigPath + ".tmp";
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new StreamWriter(stream)) { writer.Write(json); writer.Flush(); stream.Flush(flushToDisk: true); }
        File.Move(temp, ConfigPath, true);
    }

    private static void Migrate(AppSettings settings)
    {
        if (settings.ConfigVersion < 2)
        {
            settings.Actions ??= ReviewAction.Defaults();
            settings.ImageSortMode = NormalizeImageSortMode(settings.ImageSortMode);
            settings.ConfigVersion = CurrentConfigVersion;
        }
        settings.Actions ??= ReviewAction.Defaults();
        settings.Shortcuts ??= ShortcutMappings.Default();
    }

    public static string NormalizeLoadingMode(string? value) =>
        value?.ToUpperInvariant() switch
        {
            "ORIGINAL" => "Original",
            "PREVIEW" => "Preview",
            _ => "Fast"
        };

    public static bool IsValidLoadingMode(string? value) =>
        string.Equals(value, "Fast", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Preview", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Original", StringComparison.OrdinalIgnoreCase);

    public static string NormalizeImageSortMode(string? value) =>
        value?.ToUpperInvariant() switch
        {
            "NAME" => "Name",
            "SIZE" or "SIZEDESCENDING" => "SizeDescending",
            "SIZEASCENDING" => "SizeAscending",
            _ => "PortraitFirst"
        };

    public static bool IsValidImageSortMode(string? value) =>
        string.Equals(value, "Name", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "Size", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "SizeAscending", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "SizeDescending", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "PortraitFirst", StringComparison.OrdinalIgnoreCase);

    public static string? ValidateShortcuts(AppSettings settings)
    {
        var bindings = new List<(string Name, string Value)>();
        foreach (var property in typeof(ShortcutMappings).GetProperties())
        {
            if (property.Name == nameof(ShortcutMappings.MoveToFolder2)) continue; // Legacy alias; Enter is owned by ReviewAction.
            var value = property.GetValue(settings.Shortcuts)?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(value) || !Enum.TryParse<System.Windows.Input.Key>(value, true, out _))
                return $"Shortcut {property.Name} không hợp lệ.";
            bindings.Add((property.Name, value));
        }
        foreach (var action in settings.Actions ?? [])
        {
            if (string.IsNullOrWhiteSpace(action.Name) || !Enum.TryParse<System.Windows.Input.Key>(action.Shortcut, true, out _))
                return "Action phải có tên và phím tắt hợp lệ.";
            bindings.Add(($"Action: {action.Name}", action.Shortcut.Trim()));
        }
        var duplicate = bindings.GroupBy(item => item.Value, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        return duplicate is null ? null : $"Phím {duplicate.Key} bị dùng trùng bởi: {string.Join(", ", duplicate.Select(item => item.Name))}.";
    }
}

public sealed class ShortcutMappings
{
    public string Next { get; set; } = "Right";
    public string Previous { get; set; } = "Left";
    public string MoveToFolder2 { get; set; } = "Enter";
    public string SendToRecycleBin { get; set; } = "Delete";
    public string Compare { get; set; } = "C";
    public string NextFolder { get; set; } = "PageDown";
    public string PreviousFolder { get; set; } = "PageUp";
    public string FirstImage { get; set; } = "Home";
    public string ZoomIn { get; set; } = "Add";
    public string ZoomOut { get; set; } = "Subtract";
    public string ToggleFit { get; set; } = "F";
    public string Skip { get; set; } = "Space";
    public string Undo { get; set; } = "Z";
    public string Fullscreen { get; set; } = "F11";

    public static ShortcutMappings Default() => new();
}

public sealed class ReviewAction
{
    public string Name { get; set; } = "Loại 2";
    public string Shortcut { get; set; } = "Enter";
    public string Operation { get; set; } = "Move";
    public string Destination { get; set; } = "Loai-2";
    public bool Confirm { get; set; }

    public static List<ReviewAction> Defaults() =>
    [
        new() { Name = "Loại 2", Shortcut = "Enter", Operation = "Move", Destination = "Loai-2" },
        new() { Name = "Loại 3", Shortcut = "F3", Operation = "Move", Destination = "Loai-3" },
        new() { Name = "Loại 4", Shortcut = "F4", Operation = "Move", Destination = "Loai-4" },
        new() { Name = "Backup", Shortcut = "F5", Operation = "Copy", Destination = "Backup" }
    ];
}
