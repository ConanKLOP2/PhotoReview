namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>
/// WP-20: một JPEG thật 320x240 (gradient cam -> xanh, vòng tròn trắng, ô xanh lá góc trên trái), nhúng sẵn để test khởi động
/// shell không phụ thuộc thư mục ảnh của máy. Sinh một lần bằng System.Drawing (chất lượng 80); không phải dữ liệu người dùng.
/// </summary>
internal static class TestJpeg
{
    public const int Width = 320;
    public const int Height = 240;

    private const string Base64 =
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAcFBQYFBAcGBQYIBwcIChELCgkJChUPEAwRGBUaGRgVGBcbHichGx0lHRcYIi4iJSgp" +
        "KywrGiAvMy8qMicqKyr/2wBDAQcICAoJChQLCxQqHBgcKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioqKioq" +
        "KioqKioqKir/wAARCADwAUADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUF" +
        "BAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVW" +
        "V1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi" +
        "4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAEC" +
        "AxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVm" +
        "Z2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq" +
        "8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDoSaaTQTVWWV1lIB4+lfkVGjKrLlifcZrmtHK6KrVk2m7aWvezfVrsWCaaTVbzn/vfpR5r" +
        "+v6V2LBVF1R8z/rtl38k/uj/APJExNNJqLzG9aTe3rVrBz7oX+uuX/yT+6P/AMkSE0wmk3H1pM1aws+6J/10y/8Akn90f/kgJppN" +
        "KelMJpSpuDsz38szSjmdF1qKaSdtbdk+jfcQmmk0pNMJqkj0WBNNJoJppNapEMCaYTSk0wmtUiGwJphNKTTSa1SIbEJppNKTTSa1" +
        "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" +
        "1qkQ2ITTSaCaaTWiRDYE0wmlJppNapENn//Z"
;

    public static byte[] Bytes() => Convert.FromBase64String(Base64);

    /// <summary>Ghi JPEG vào <paramref name="path"/> (tạo thư mục) và trả lại đường dẫn đầy đủ.</summary>
    public static string WriteTo(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Bytes());
        return full;
    }
}
