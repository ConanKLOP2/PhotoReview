using PhotoReview.App.Windowing;
using PhotoReview.Core.Settings;

namespace PhotoReview.Shell.Win32.Startup;

/// <summary>C-16 phần shell (NO-WPF-EXEC-PLAN mục 5): đầu vào của startup shell. Dùng từ WP-20.</summary>
public sealed record ShellStartupContext(IReadOnlyList<string> Arguments, string? InitialPath, string? LaunchFolder,
    Task<AppSettings> Settings, Task<WindowPlacementData?> Placement, long ProcessStartTimestamp);
