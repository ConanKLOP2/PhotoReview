using PhotoReview.App.Presenters;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.IO;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs;
using PhotoReview.Shell.Win32.Dialogs.Native;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi;
using static PhotoReview.Shell.Tests.Dialogs.Native.NativeDialogTestKit;

namespace PhotoReview.Shell.Tests.Dialogs.SettingsUi;

/// <summary>WP-35 GĐ3a: bộ dụng cụ dùng chung của test Settings Win32 (mở hộp thoại trên thread STA, mô phỏng người dùng bằng thông điệp).</summary>
internal static unsafe class SettingsDialogKit
{
    public sealed class FakeWarnings : IDialogWarningSink
    {
        public List<(string Title, string Message)> Shown { get; } = [];

        public void ShowWarning(nint owner, string title, string message) => Shown.Add((title, message));
    }

    public sealed class FakeFiles : IShellFileDialog
    {
        public string? OpenResult { get; set; }

        public string? SaveResult { get; set; }

        public List<string> Calls { get; } = [];

        public string? PickOpenFile(nint owner, string title, IReadOnlyList<FileDialogFilter> filters, string? initialFolder)
        {
            Calls.Add("open:" + title);
            return OpenResult;
        }

        public string? PickSaveFile(nint owner, string title, IReadOnlyList<FileDialogFilter> filters, string? defaultExtension,
            string? suggestedFileName, string? initialFolder)
        {
            Calls.Add($"save:{title}:{defaultExtension}:{suggestedFileName}");
            return SaveResult;
        }
    }

    public sealed class FakeShell : IShellIntegration
    {
        public ShellMenuState State { get; set; } = ShellMenuState.NotRegistered;

        public List<string> Calls { get; } = [];

        public ShellMenuState GetState(string executablePath) => State;

        public void Register(string executablePath)
        {
            Calls.Add("register:" + executablePath);
            State = ShellMenuState.Registered;
        }

        public void Unregister()
        {
            Calls.Add("unregister");
            State = ShellMenuState.NotRegistered;
        }
    }

    public sealed class FakeUpdates(Func<PhotoReview.Core.Updates.UpdateCheckResult> next) : PhotoReview.Core.Updates.IUpdateChecker
    {
        public int Calls { get; private set; }

        public string? LastVersion { get; private set; }

        public Task<PhotoReview.Core.Updates.UpdateCheckResult> CheckAsync(string? currentVersion, CancellationToken cancellationToken)
        {
            Calls++;
            LastVersion = currentVersion;
            return Task.FromResult(next());
        }
    }

    private sealed class PathsStub(string configFile) : IAppPaths
    {
        public string ConfigFile => configFile;

        public string JournalFile => throw new NotSupportedException();

        public string SessionsDir => throw new NotSupportedException();

        public string LogFile => throw new NotSupportedException();

        public string PreviewCacheDir => throw new NotSupportedException();

        public string ThumbnailCacheDir => throw new NotSupportedException();

        public string WindowPlacementFile => throw new NotSupportedException();
    }

    public static SettingsStore NewStore(string directory)
        => new(new PathsStub(Path.Combine(directory, "config.json")), new PhysicalFileSystem(), NullLog.Instance);

    /// <summary>Tuỳ chọn mặc định của test: cảnh báo/file giả, không Explorer thật.</summary>
    public static SettingsDialogOptions Options(FakeWarnings? warnings = null, FakeFiles? files = null, List<string>? explorer = null,
        IReadOnlyList<ISettingsPageView>? pages = null, SettingsTarget target = SettingsTarget.Default, Func<bool>? control = null)
        => new()
        {
            Warnings = warnings,
            Files = files,
            Pages = pages,
            Target = target,
            ControlKeyDown = control,
            Version = "2.0.1+test",
            StartExplorer = args =>
            {
                explorer?.Add(args);
                return null;
            },
            OpenUrl = _ => { },
        };

    /// <summary>Mở trên cửa sổ chủ mới; <paramref name="ready"/> chạy ngay trước vòng lặp modal. Trả (kết quả, hộp thoại).</summary>
    public static (int Result, SettingsDialog Dialog) Show(AppSettings settings, Action<SettingsDialog, nint> ready,
        SettingsDialogOptions? options = null, SettingsStore? store = null, Action<SettingsDialog>? prepare = null,
        INativeDialogChrome? chrome = null, PhotoReview.Core.Updates.IUpdateChecker? updates = null, bool realizeAll = true)
    {
        return OnSta(() =>
        {
            var dialog = new SettingsDialog(settings, store, null, null, updates, options ?? Options(new FakeWarnings(), new FakeFiles()), chrome);
            // Chưa có registry thật: mặc định giả "không đăng ký".
            dialog.Presenter.ShellIntegration = new FakeShell();
            dialog.Presenter.ExecutablePath = @"C:\Apps\PhotoReview\PhotoReview.exe";
            prepare?.Invoke(dialog);
            var result = ShowOnOwner(dialog, (d, owner) =>
            {
                // Pages create their controls when first opened; most tests want every page's controls to exist.
                if (realizeAll)
                {
                    ((SettingsDialog)d).EnsureAllPagesCreated();
                }

                ready((SettingsDialog)d, owner);
            });
            return (result, dialog);
        });
    }

    public static nint H(SettingsDialog dialog, int id) => dialog.ControlHandle(id);

    public static string Txt(SettingsDialog dialog, int id) => TextOf(H(dialog, id));

    public static void SetText(SettingsDialog dialog, int id, string text) => User32.SetWindowText(H(dialog, id), text);

    public static bool Checked(SettingsDialog dialog, int id) => ButtonStateControl.IsChecked(H(dialog, id));

    /// <summary>Đặt tick rồi báo BN_CLICKED như người dùng bấm.</summary>
    public static void Tick(SettingsDialog dialog, int id, bool value)
    {
        ButtonStateControl.SetChecked(H(dialog, id), value);
        Command(dialog, id, 0);
    }

    public static int ComboIndex(SettingsDialog dialog, int id) => ComboBoxControl.SelectedIndex(H(dialog, id));

    public static void Choose(SettingsDialog dialog, int id, int index)
    {
        ComboBoxControl.Select(H(dialog, id), index);
        Command(dialog, id, ControlConstants.CbnSelChange);
    }

    public static string ComboText(SettingsDialog dialog, int id, int index) => ComboBoxControl.TextAt(H(dialog, id), index);

    public static int ComboCount(SettingsDialog dialog, int id) => ComboBoxControl.Count(H(dialog, id));

    /// <summary>Chọn trang trong danh sách điều hướng như người dùng.</summary>
    public static void GoTo(SettingsDialog dialog, int pageIndex)
    {
        ListBoxControl.SelectSingle(H(dialog, SettingsDialog.NavId), pageIndex);
        Command(dialog, SettingsDialog.NavId, ControlConstants.LbnSelChange);
    }

    public static string NavText(SettingsDialog dialog, int index) => ListBoxControl.TextAt(H(dialog, SettingsDialog.NavId), index);

    public static bool Enabled(SettingsDialog dialog, int id) => User32.IsWindowEnabled(H(dialog, id));

    public static bool Visible(SettingsDialog dialog, int id) => User32.IsWindowVisible(H(dialog, id));

    /// <summary>Hình chữ nhật của điều khiển trang theo toạ độ client của cửa sổ chứa trang đang hiện.</summary>
    public static Rect RectInViewport(SettingsDialog dialog, int id)
    {
        var viewport = H(dialog, SettingsDialog.ViewportId(dialog.ActivePageIndex));
        var rect = WindowRect(H(dialog, id));
        var topLeft = new Point { X = rect.Left, Y = rect.Top };
        var bottomRight = new Point { X = rect.Right, Y = rect.Bottom };
        User32.ScreenToClient(viewport, &topLeft);
        User32.ScreenToClient(viewport, &bottomRight);
        return new Rect { Left = topLeft.X, Top = topLeft.Y, Right = bottomRight.X, Bottom = bottomRight.Y };
    }

    public static bool IsGroupBox(nint hwnd)
        => ((uint)User32.GetWindowLongPtr(hwnd, WindowMessages.GwlStyle) & 0xF) == ControlConstants.BsGroupBox;

    public static void ChangeDpi(NativeDialog dialog, uint dpi)
    {
        var current = WindowRect(dialog.Hwnd);
        var factor = dpi / (double)dialog.Dpi;
        var suggested = new Rect
        {
            Left = current.Left,
            Top = current.Top,
            Right = current.Left + (int)(Width(current) * factor),
            Bottom = current.Top + (int)(Height(current) * factor),
        };
        User32.SendMessage(dialog.Hwnd, WindowMessages.WmDpiChanged, (nint)((dpi << 16) | dpi), (nint)(&suggested));
    }
}
