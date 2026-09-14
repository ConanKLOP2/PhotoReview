using System.Text.Json;
using System.IO;

namespace PhotoReview.App;

public sealed class AppSettings
{
    public string Folder2Name { get; set; } = "Loai-2";
    public string InitialViewMode { get; set; } = "Fit";
    public string LoadingMode { get; set; } = "Preview";
    public ShortcutMappings Shortcuts { get; set; } = ShortcutMappings.Default();
    public static string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoReview", "config.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath)) ?? new();
                loaded.Shortcuts ??= ShortcutMappings.Default();
                loaded.LoadingMode = NormalizeLoadingMode(loaded.LoadingMode);
                return loaded;
            }
        }
        catch { }
        var settings = new AppSettings();
        Save(settings);
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, ConfigPath, true);
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

    public static ShortcutMappings Default() => new();
}
