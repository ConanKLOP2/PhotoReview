using System.Globalization;
using System.Text;

namespace PhotoReview.Shell.Win32.Startup;

/// <summary>
/// Tệp lỗi nhỏ của shell (<c>shell-errors.log</c>, cạnh config.json): ghi lỗi dựng/hiện menu (và lỗi tương tự bị nuốt) KỂ CẢ khi
/// log chung đang tắt (INV-10). Chỉ ghi khi có lỗi (không I/O ở đường bình thường); cắt còn nửa cuối khi vượt <see cref="MaxBytes"/>.
/// Nội dung: thời điểm UTC, ngữ cảnh, kiểu ngoại lệ, HRESULT, thông điệp (cắt 300 ký tự) và tối đa 12 khung stack - không đường dẫn
/// ảnh/cấu hình. Không bao giờ ném.
/// </summary>
internal static class ShellErrorLog
{
    internal const long MaxBytes = 256 * 1024;
    internal const int MaxMessageChars = 300;
    internal const int MaxStackFrames = 12;
    internal const string FileName = "shell-errors.log";

    private static readonly object Gate = new();
    private static string? s_path;

    /// <summary>Tệp đang dùng (null = chưa cấu hình: <see cref="Report"/> không làm gì).</summary>
    internal static string? CurrentPath => s_path;

    internal static void Configure(string? path)
    {
        lock (Gate)
        {
            s_path = path;
        }
    }

    internal static void Report(string context, Exception exception)
    {
        string? path;
        lock (Gate)
        {
            path = s_path;
        }

        if (path is not null)
        {
            Append(path, context, exception);
        }
    }

    internal static string Format(DateTime utcNow, string context, Exception exception)
    {
        var builder = new StringBuilder();
        builder.Append(utcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))
            .Append(" [").Append(context).Append("] ")
            .Append(exception.GetType().FullName)
            .Append(" 0x").Append(exception.HResult.ToString("X8", CultureInfo.InvariantCulture))
            .Append(": ");
        var message = exception.Message.ReplaceLineEndings(" ");
        builder.Append(message.Length <= MaxMessageChars ? message : message[..MaxMessageChars]).AppendLine();
        var frames = 0;
        foreach (var line in (exception.StackTrace ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (frames++ >= MaxStackFrames)
            {
                break;
            }

            builder.Append("    ").AppendLine(line.Trim());
        }

        if (exception.InnerException is { } inner)
        {
            var innerMessage = inner.Message.ReplaceLineEndings(" ");
            builder.Append("  inner: ").Append(inner.GetType().FullName).Append(": ")
                .AppendLine(innerMessage[..Math.Min(MaxMessageChars, innerMessage.Length)]);
        }

        return builder.ToString();
    }

    internal static void Append(string path, string context, Exception exception, long maxBytes = MaxBytes)
    {
        try
        {
            lock (Gate)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(path) && new FileInfo(path).Length > maxBytes)
                {
                    Truncate(path, maxBytes);
                }

                File.AppendAllText(path, Format(DateTime.UtcNow, context, exception), new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Tệp chẩn đoán không được làm hỏng đường lỗi gốc.
        }
    }

    /// <summary>Giữ nửa cuối (theo byte, bắt đầu ở đầu một dòng) của tệp.</summary>
    private static void Truncate(string path, long maxBytes)
    {
        var all = File.ReadAllBytes(path);
        var keep = (int)Math.Min(all.Length, maxBytes / 2);
        var start = all.Length - keep;
        while (start < all.Length && all[start - 1] != (byte)'\n')
        {
            start++;
        }

        File.WriteAllBytes(path, all.AsSpan(start).ToArray());
    }
}
