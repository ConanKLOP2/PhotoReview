using System.Runtime.InteropServices;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Localization;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Dialogs;

namespace PhotoReview.Shell.Tests;

/// <summary>
/// WP-19a (NO-WPF-EXEC-PLAN-WP): hộp thoại native + clipboard của shell. Quyết định (nút nào = có, thư mục khởi đầu,
/// uỷ cửa sổ phụ) test qua seam; không mở hộp thoại thật trong test mặc định (Category=Native mới chạm OS).
/// </summary>
public sealed class Win32DialogServiceTests
{
    private sealed class FakeTaskDialogs : ITaskDialogPort
    {
        public int Result = WindowMessages.IdYes;
        public List<(nint Owner, string Title, string Message, TaskDialogKind Kind)> Calls { get; } = [];

        public int Show(nint owner, string title, string message, TaskDialogKind kind)
        {
            Calls.Add((owner, title, message, kind));
            return Result;
        }
    }

    private sealed class FakeFolderDialog : IShellFolderDialog
    {
        public string? Answer;
        public (nint Owner, string Title, string? Initial)? Last;

        public string? Show(nint owner, string title, string? initialFolder)
        {
            Last = (owner, title, initialFolder);
            return Answer;
        }
    }

    private sealed class FakeSecondary : ISecondaryWindowPort
    {
        public List<string> Calls { get; } = [];
        public IReadOnlyList<BatchReviewItem>? Items;
        public bool Answer = true;

        public bool ShowSettings(nint ownerHwnd, SettingsTarget target) { Calls.Add($"settings:{ownerHwnd}:{target}"); return Answer; }
        public void ShowRecovery(nint ownerHwnd) => Calls.Add($"recovery:{ownerHwnd}");
        public void ShowDiagnostics(nint ownerHwnd) => Calls.Add($"diagnostics:{ownerHwnd}");
        public void ShowBenchmark(nint ownerHwnd, string? folder) => Calls.Add($"benchmark:{ownerHwnd}:{folder}");
        public void ShowSkippedFiles(nint ownerHwnd, IReadOnlyList<SkippedEntry> entries) => Calls.Add($"skipped:{ownerHwnd}:{entries.Count}");

        public bool ShowBatchReview(nint ownerHwnd, IReadOnlyList<BatchReviewItem> items)
        {
            Items = items;
            Calls.Add($"batch:{ownerHwnd}:{items.Count}");
            return Answer;
        }
    }

    private static readonly string[] TwoPaths = [@"C:\a.jpg", @"C:\b.jpg"];

    private static (Win32DialogService Service, FakeTaskDialogs Dialogs, FakeFolderDialog Folder) Create(
        ISecondaryWindowPort? secondary = null, Action? onSecondaryCreated = null)
    {
        var dialogs = new FakeTaskDialogs();
        var folder = new FakeFolderDialog();
        var lazy = new Lazy<ISecondaryWindowPort?>(() =>
        {
            onSecondaryCreated?.Invoke();
            return secondary;
        });
        var service = new Win32DialogService(() => 0x1234, dialogs, new FolderPicker(folder, () => 0x1234), lazy);
        return (service, dialogs, folder);
    }

    [Trait("Category", "HotPath")]
    [Theory]
    [InlineData(WindowMessages.IdYes, true)]
    [InlineData(WindowMessages.IdNo, false)]
    [InlineData(WindowMessages.IdCancel, false)]
    public void ShowConfirmation_OnlyYesIsTrue(int pressed, bool expected)
    {
        var (service, dialogs, _) = Create();
        dialogs.Result = pressed;

        Assert.Equal(expected, service.ShowConfirmation("Tiêu đề", "Xoá?"));

        var call = Assert.Single(dialogs.Calls);
        Assert.Equal((0x1234, "Tiêu đề", "Xoá?", TaskDialogKind.Confirmation), (call.Owner, call.Title, call.Message, call.Kind));
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void ShowMessageAndShowError_UseInformationAndErrorKinds()
    {
        var (service, dialogs, _) = Create();

        service.ShowMessage("T1", "M1");
        service.ShowError("T2", "M2");

        Assert.Equal([TaskDialogKind.Information, TaskDialogKind.Error], dialogs.Calls.Select(c => c.Kind));
        Assert.Equal(["T1", "T2"], dialogs.Calls.Select(c => c.Title));
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void PickFolder_UsesCatalogTitle_AndDropsMissingInitialFolder()
    {
        var (service, _, folder) = Create();
        folder.Answer = @"D:\Photos";

        var picked = service.PickFolder(@"Z:\does\not\exist");

        Assert.Equal(@"D:\Photos", picked);
        Assert.Equal((0x1234, Tr.DialogPickFolderTitle, (string?)null), folder.Last);
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void PickFolder_PassesExistingInitialFolder_AndNullWhenCancelled()
    {
        var (service, _, folder) = Create();
        var existing = Path.GetTempPath();
        folder.Answer = null;

        Assert.Null(service.PickFolder(existing));
        Assert.Equal(existing, folder.Last!.Value.Initial);
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void SecondaryWindows_AreDelegatedWithOwner_AndLoadedLazily()
    {
        var secondary = new FakeSecondary();
        var created = 0;
        var (service, dialogs, _) = Create(secondary, () => created++);

        service.ShowConfirmation("t", "m");
        Assert.Equal(0, created); // hộp thoại native không nạp cầu nối WPF

        Assert.True(service.ShowSettings(SettingsTarget.ExternalEditor));
        service.ShowRecovery();
        service.ShowDiagnostics();
        service.ShowBenchmark(@"C:\x");
        service.ShowSkippedFiles([new SkippedEntry("a", "r")]);

        Assert.Equal(1, created);
        Assert.Equal(
            ["settings:4660:ExternalEditor", "recovery:4660", "diagnostics:4660", "benchmark:4660:C:\\x", "skipped:4660:1"],
            secondary.Calls);
        Assert.Single(dialogs.Calls);
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void ShowBatchReview_PathOverload_MapsToItemsWithUnknownLength()
    {
        var secondary = new FakeSecondary();
        var (service, _, _) = Create(secondary);

        Assert.True(service.ShowBatchReview(TwoPaths));

        Assert.Equal([new BatchReviewItem(@"C:\a.jpg", null), new BatchReviewItem(@"C:\b.jpg", null)], secondary.Items);
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void SecondaryWindows_WhenBridgeUnavailable_ReturnFalseAndDoNotThrow()
    {
        var (service, _, _) = Create(secondary: null);

        Assert.False(service.ShowSettings());
        Assert.False(service.ShowBatchReview([new BatchReviewItem("a", 1)]));
        service.ShowRecovery();
        service.ShowDiagnostics();
        service.ShowBenchmark();
        service.ShowSkippedFiles([]);
    }

    // --- Clipboard (seam) ---

    private sealed class ClipboardBusyException : ExternalException;

    private sealed class FakeClipboard : IClipboardApi
    {
        public int OpenFailures;
        public int OpenCalls;
        public int CloseCalls;
        public List<string> Written { get; } = [];
        public bool SetResult = true;
        public Exception? SetThrows;

        public bool Open(nint owner)
        {
            OpenCalls++;
            return OpenCalls > OpenFailures;
        }

        public void Close() => CloseCalls++;

        public bool SetUnicodeText(string text)
        {
            if (SetThrows is not null) throw SetThrows;
            Written.Add(text);
            return SetResult;
        }
    }

    private static (Win32ClipboardService Service, List<int> Sleeps) CreateClipboard(FakeClipboard api, nint owner = 0x77)
    {
        var sleeps = new List<int>();
        return (new Win32ClipboardService(api, () => owner, sleeps.Add), sleeps);
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void TrySetText_RetriesWhileClipboardIsBusy_ThenSucceeds()
    {
        var api = new FakeClipboard { OpenFailures = 2 };
        var (service, sleeps) = CreateClipboard(api);

        Assert.True(service.TrySetText("abc"));

        Assert.Equal(["abc"], api.Written);
        Assert.Equal(3, api.OpenCalls);
        Assert.Equal([20, 20], sleeps);
        Assert.Equal(1, api.CloseCalls);
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void TrySetText_WhenAlwaysBusy_GivesUpAfterFiveAttemptsWithoutThrowing()
    {
        var api = new FakeClipboard { OpenFailures = int.MaxValue };
        var (service, sleeps) = CreateClipboard(api);

        Assert.False(service.TrySetText("abc"));

        Assert.Equal(5, api.OpenCalls);
        Assert.Equal(4, sleeps.Count);
        Assert.Equal(0, api.CloseCalls); // chưa mở được thì không Close
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void TrySetText_WhenSetFails_ClosesClipboardAndReportsFalse()
    {
        var api = new FakeClipboard { SetResult = false };
        var (service, _) = CreateClipboard(api);

        Assert.False(service.TrySetText("abc"));
        Assert.Equal(1, api.CloseCalls);
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void TrySetText_WhenSetThrowsExternal_ClosesClipboardAndReportsFalse()
    {
        var api = new FakeClipboard { SetThrows = new ClipboardBusyException() };
        var (service, _) = CreateClipboard(api);

        Assert.False(service.TrySetText("abc"));
        Assert.Equal(1, api.CloseCalls);
    }

    [Trait("Category", "HotPath")]
    [Fact]
    public void TrySetText_WithoutOwnerWindow_ReturnsFalseWithoutTouchingClipboard()
    {
        var api = new FakeClipboard();
        var (service, _) = CreateClipboard(api, owner: 0);

        Assert.False(service.TrySetText("abc"));
        Assert.Equal(0, api.OpenCalls);
    }

    // --- Native (chạm OS; loại khỏi lần chạy mặc định) ---

    [Trait("Category", "Native")]
    [Fact]
    public unsafe void NativeTaskDialog_ClosedWithWmClose_ReturnsCancel_AndConfirmationIsFalse()
    {
        // testhost không có manifest comctl32 v6 -> kích hoạt manifest nhúng của PhotoReview.dll cho luồng này.
        var dll = typeof(Win32DialogService).Assembly.Location;
        nuint cookie = 0;
        nint context;
        fixed (char* source = dll)
        {
            var actCtx = new ActivationContext.ActCtx
            {
                Size = (uint)sizeof(ActivationContext.ActCtx),
                Flags = ActivationContext.FlagResourceNameValid,
                Source = source,
                ResourceName = 1, // MAKEINTRESOURCE(1): manifest nhúng
            };
            context = ActivationContext.CreateActCtx(&actCtx);
        }

        Assert.NotEqual(-1, context);
        Assert.True(ActivationContext.ActivateActCtx(context, &cookie));
        try
        {
            var created = 0;
            var port = new NativeTaskDialogPort(hwnd =>
            {
                created++;
                User32.PostMessage(hwnd, WindowMessages.WmClose, 0, 0);
            });
            var service = new Win32DialogService(() => 0, port, new FolderPicker(UnwiredShellFolderDialog.Instance, () => 0),
                new Lazy<ISecondaryWindowPort?>(() => null));

            var confirmed = service.ShowConfirmation("PhotoReview", "Test WP-19a");

            Assert.True(created == 1, port.LastFailure); // thiếu comctl32 v6 thì hộp thoại không bao giờ được tạo
            Assert.False(confirmed);
        }
        finally
        {
            ActivationContext.DeactivateActCtx(0, cookie);
            ActivationContext.ReleaseActCtx(context);
        }
    }

    [Trait("Category", "Native")]
    [Fact]
    public void NativeClipboard_SetText_CanBeReadBackAndOldTextIsRestored()
    {
        var owner = User32.CreateWindowEx(0, "STATIC", "WP19a-clipboard-owner", 0, 0, 0, 0, 0, 0, 0, Kernel32.GetModuleHandle(0), 0);
        Assert.NotEqual(0, owner);
        var previous = ReadClipboardText(owner);
        try
        {
            var service = new Win32ClipboardService(() => owner);
            var text = "PhotoReview WP-19a " + Guid.NewGuid();

            Assert.True(service.TrySetText(text));
            Assert.Equal(text, ReadClipboardText(owner));
        }
        finally
        {
            if (previous is not null) new Win32ClipboardService(() => owner).TrySetText(previous);
            User32.DestroyWindow(owner);
        }
    }

    private static string? ReadClipboardText(nint owner)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (!User32.OpenClipboard(owner)) { Thread.Sleep(20); continue; }
            try
            {
                var handle = User32.GetClipboardData(WindowMessages.CfUnicodeText);
                if (handle == 0) return null;
                var locked = Kernel32.GlobalLock(handle);
                try
                {
                    return locked == 0 ? null : Marshal.PtrToStringUni(locked);
                }
                finally
                {
                    Kernel32.GlobalUnlock(handle);
                }
            }
            finally
            {
                User32.CloseClipboard();
            }
        }

        return null;
    }
}




