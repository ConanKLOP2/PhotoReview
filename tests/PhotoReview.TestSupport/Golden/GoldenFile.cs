using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PhotoReview.TestSupport.Golden;

/// <summary>
/// Đọc/ghi file golden <c>tests/Fixtures/golden/*.v1.json</c> (WP-10). Ghi tất định: mỗi phần tử một dòng, LF, UTF-8 không BOM,
/// dòng cuối có newline, để ghi lại trên cùng bản WPF cho ra cùng byte (diff trong PR đọc được).
/// </summary>
public static class GoldenFile
{
    public const string ViewportLayout = "viewport-layout.v1.json";
    public const string InputScripts = "input-scripts.v1.json";
    public const string KeyNames = "key-names.v1.json";
    public const string ContextMenu = "context-menu.v1.json";
    public const string OverlayBoxes = "overlay-boxes.v1.json";

    /// <summary>Biến môi trường mở khoá việc GHI file golden (mặc định chỉ đọc/so sánh).</summary>
    public const string RecordEnvironmentVariable = "PHOTOREVIEW_GOLDEN_RECORD";

    /// <summary>Thư mục golden của checkout đang chạy (đi lên từ thư mục build tới PhotoReview.slnx).</summary>
    public static string Directory { get; } = Path.Combine(FindRoot(), "tests", "Fixtures", "golden");

    public static string PathOf(string fileName) => Path.Combine(Directory, fileName);

    public static bool IsRecordingEnabled => Environment.GetEnvironmentVariable(RecordEnvironmentVariable) == "1";

    public static GoldenDocument<T> Read<T>(string fileName, JsonTypeInfo<GoldenDocument<T>> typeInfo)
    {
        var path = PathOf(fileName);
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, typeInfo)
            ?? throw new InvalidDataException($"Golden file {path} is empty.");
    }

    /// <summary>Chuỗi file golden sẽ có cho <paramref name="document"/> (dùng cho so sánh và cho <see cref="Write{T}"/>).</summary>
    public static string Render<T>(GoldenDocument<T> document, JsonTypeInfo<T> itemInfo)
    {
        var text = new StringBuilder();
        text.Append("{\"schema\":").Append(JsonSerializer.Serialize(document.Schema, GoldenJsonContext.Default.String))
            .Append(",\"version\":").Append(document.Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(",\"recorder\":").Append(JsonSerializer.Serialize(document.Recorder, GoldenJsonContext.Default.String))
            .Append(",\"notes\":").Append(JsonSerializer.Serialize(document.Notes, GoldenJsonContext.Default.String))
            .Append(",\"items\":[\n");
        for (var i = 0; i < document.Items.Count; i++)
        {
            text.Append(JsonSerializer.Serialize(document.Items[i], itemInfo));
            text.Append(i + 1 < document.Items.Count ? ",\n" : "\n");
        }
        text.Append("]}\n");
        return text.ToString();
    }

    /// <summary>Ghi file golden; chỉ khi <see cref="IsRecordingEnabled"/> (chống ghi nhầm khi chạy test thường).</summary>
    public static void Write<T>(string fileName, GoldenDocument<T> document, JsonTypeInfo<T> itemInfo)
    {
        if (!IsRecordingEnabled)
            throw new InvalidOperationException($"Writing golden files needs {RecordEnvironmentVariable}=1 (tools/diag/record-golden.ps1).");
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(PathOf(fileName), Render(document, itemInfo), new UTF8Encoding(false));
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PhotoReview.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("PhotoReview.slnx not found above " + AppContext.BaseDirectory);
    }
}
