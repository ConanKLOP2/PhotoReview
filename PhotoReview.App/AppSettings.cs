using System.Text.Json;
using System.IO;

namespace PhotoReview.App;

public sealed class AppSettings
{
    public string Folder1Name { get; set; } = "Loai-1";
    public string Folder2Name { get; set; } = "Loai-2";
    public bool MoveImmediately { get; set; } = true;
    public static string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoReview", "config.json");

    public static AppSettings Load()
    {
        try { if (File.Exists(ConfigPath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath)) ?? new(); }
        catch { }
        var settings = new AppSettings();
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        return settings;
    }
}
