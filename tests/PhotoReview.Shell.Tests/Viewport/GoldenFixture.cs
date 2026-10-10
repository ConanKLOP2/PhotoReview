using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoReview.Shell.Tests.Viewport;

/// <summary>
/// WP-16: tìm và đọc golden C-17 (<c>tests/Fixtures/golden/*.v1.json</c>, WP-10 ghi từ bản WPF thật). Tạm thời ở đây vì
/// <c>TestSupport/Golden/GoldenFile.cs</c> + <c>GoldenJsonContext.cs</c> thuộc vùng file của WP-10; khi WP-10 merge, đổi
/// <see cref="ReadCases{T}"/> sang API của nó (một chỗ). Đọc khoan dung: gốc là mảng, hoặc object có một thuộc tính mảng
/// (ví dụ <c>cases</c>); số NaN/Infinity ghi dạng chuỗi (<see cref="JsonNumberHandling.AllowNamedFloatingPointLiterals"/>).
/// </summary>
internal static class GoldenFixture
{
    public const string ViewportLayoutFile = "viewport-layout.v1.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals | JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Đường dẫn đầy đủ của golden, hoặc null khi chưa có (WP-10 chưa merge).</summary>
    public static string? Find(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Fixtures", "golden", fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static IReadOnlyList<T> ReadCases<T>(string json)
    {
        using var document = JsonDocument.Parse(json);
        var array = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement
            : document.RootElement.EnumerateObject().Select(p => p.Value).FirstOrDefault(v => v.ValueKind == JsonValueKind.Array);
        if (array.ValueKind != JsonValueKind.Array) throw new InvalidDataException("golden không có mảng ca nào");
        return array.EnumerateArray().Select(e => e.Deserialize<T>(Options) ?? throw new InvalidDataException("ca golden rỗng")).ToList();
    }
}

/// <summary>[Fact] tự bỏ qua (Skip) khi file golden chưa có, tự chạy khi WP-10 đã commit nó - không cần sửa test lúc merge.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class GoldenFactAttribute : FactAttribute
{
    public GoldenFactAttribute(string fileName)
    {
        FileName = fileName;
        if (GoldenFixture.Find(fileName) is null) Skip = $"Chờ WP-10: chưa có tests/Fixtures/golden/{fileName}";
    }

    public string FileName { get; }
}
