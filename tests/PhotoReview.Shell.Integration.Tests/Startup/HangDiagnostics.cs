using System.Diagnostics;
using System.Text;

namespace PhotoReview.Shell.Integration.Tests.Startup;

/// <summary>
/// Chẩn đoán treo cho test shell: khi một lần chạy không thoát/không dựng xong, thông báo lỗi kèm bảng thread của tiến trình
/// (trạng thái, lý do chờ, CPU) và, nếu có công cụ, stack managed của MỌI thread (<c>dotnet-stack report</c>; đường dẫn từ biến
/// môi trường <c>PHOTOREVIEW_DOTNET_STACK</c> hoặc PATH). Không đổi hành vi test; chỉ chạy trên đường thất bại.
/// </summary>
internal static class HangDiagnostics
{
    private const int MaxChars = 30_000;
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(40);

    public static string Capture()
    {
        var text = new StringBuilder();
        try
        {
            using var process = Process.GetCurrentProcess();
            text.Append("threads(").Append(process.Threads.Count).Append("): ");
            foreach (ProcessThread thread in process.Threads)
            {
                try
                {
                    text.Append(thread.Id).Append('=').Append(thread.ThreadState);
                    if (thread.ThreadState == System.Diagnostics.ThreadState.Wait)
                    {
                        text.Append('/').Append(thread.WaitReason);
                    }

                    text.Append(" cpu=").Append((int)thread.TotalProcessorTime.TotalMilliseconds).Append("ms; ");
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    text.Append("?; ");
                }
            }

            text.AppendLine();
            text.Append("handles=").Append(process.HandleCount).Append(" pid=").Append(process.Id).AppendLine();
            AppendStacks(text, process.Id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            text.Append("diagnostics failed: ").Append(ex.Message);
        }

        return text.Length > MaxChars ? text.ToString(0, MaxChars) + "...(cắt)" : text.ToString();
    }

    /// <summary>
    /// Giữ nguyên các thread có frame <c>PhotoReview.*</c> (trừ chính bộ chẩn đoán); các thread còn lại (testhost, pool, timer...)
    /// chỉ đếm, để báo cáo không bị cắt trước khi tới thread UI của shell.
    /// </summary>
    private static string Summarize(string report)
    {
        var kept = new StringBuilder();
        var skipped = 0;
        foreach (var block in System.Text.RegularExpressions.Regex.Split(report, @"\r?\n\s*\r?\n"))
        {
            if (block.Contains("PhotoReview.", StringComparison.Ordinal)
                && !block.Contains(nameof(HangDiagnostics), StringComparison.Ordinal)
                && !block.Contains("StopAndAssertClean", StringComparison.Ordinal))
            {
                kept.AppendLine(block.Trim()).AppendLine();
            }
            else
            {
                skipped++;
            }
        }

        return kept.Append("(").Append(skipped).Append(" thread khác không có frame PhotoReview)").ToString();
    }

    private static void AppendStacks(StringBuilder text, int pid)
    {
        var tool = Environment.GetEnvironmentVariable("PHOTOREVIEW_DOTNET_STACK");
        if (string.IsNullOrWhiteSpace(tool))
        {
            tool = "dotnet-stack";
        }

        try
        {
            var info = new ProcessStartInfo(tool, $"report -p {pid}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var child = Process.Start(info);
            if (child is null)
            {
                return;
            }

            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(ToolTimeout))
            {
                child.Kill(entireProcessTree: true);
                text.AppendLine("dotnet-stack: timeout");
                return;
            }

            text.AppendLine(Summarize(output.GetAwaiter().GetResult()));
            var err = error.GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(err))
            {
                text.AppendLine(err);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            text.Append("dotnet-stack unavailable: ").AppendLine(ex.Message);
        }
    }
}
