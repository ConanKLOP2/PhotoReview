using System.Diagnostics;

namespace PhotoReview.Shell.Win32.Dialogs.Native;

/// <summary>FEAT-EXTERNAL-EDITORS: tên gợi ý cho một chương trình = ProductName (rồi FileDescription) trong thông tin phiên bản của .exe.</summary>
internal static class ExternalEditorProductName
{
    /// <summary>Tên sản phẩm của <paramref name="exePath"/>, hoặc null (không đọc được / để trống): người gọi dùng tên tệp.</summary>
    public static string? Suggest(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            foreach (var candidate in new[] { info.ProductName, info.FileDescription })
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate.Trim();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // unreadable: fall back to the file name
        }

        return null;
    }
}
