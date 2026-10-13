using PhotoReview.App.Input;
using PhotoReview.App.Menus;
using PhotoReview.Core.Settings;
using PhotoReview.Shell.Rendering;
using PhotoReview.Shell.Win32;
using PhotoReview.Shell.Win32.Menus;
using PhotoReview.Shell.Win32.Overlay;

namespace PhotoReview.Shell.Tests.Overlay;

/// <summary>Xe tải lệnh toolbar: đếm lời gọi, lệnh async trả task chọn trước.</summary>
internal sealed class FakeOverlayCommands : IOverlayCommands
{
    public List<string> Calls { get; } = [];

    public Func<Task> OpenFolder { get; set; } = () => Task.CompletedTask;

    public Func<Task> Fit { get; set; } = () => Task.CompletedTask;

    public Task OpenFolderAsync()
    {
        Calls.Add("OpenFolder");
        return OpenFolder();
    }

    public void ShowSettings() => Calls.Add("Settings");

    public Task FitAsync()
    {
        Calls.Add("Fit");
        return Fit();
    }

    public void ShowSkippedFiles() => Calls.Add("Skipped");

    public void ShowRecovery() => Calls.Add("Recovery");

    public bool ThrowOnDiagnostics { get; set; }

    public void ShowDiagnostics()
    {
        Calls.Add("Diagnostics");
        if (ThrowOnDiagnostics)
        {
            throw new InvalidOperationException("diagnostics boom");
        }
    }

    public void ShowBenchmark() => Calls.Add("Benchmark");

    public Task ClearCacheAsync()
    {
        Calls.Add("ClearCache");
        return Task.CompletedTask;
    }

    public Task RemoveDuplicatesAsync(bool numbered)
    {
        Calls.Add(numbered ? "RemoveNumbered" : "RemoveOriginal");
        return Task.CompletedTask;
    }
}

internal sealed class FakeTooltips : IOverlayTooltips
{
    public IReadOnlyList<OverlayTooltip> Tools { get; private set; } = [];

    public double Dpi { get; private set; }

    public int SyncCount { get; private set; }

    public List<uint> Relayed { get; } = [];

    public bool Disposed { get; private set; }

    public void Sync(IReadOnlyList<OverlayTooltip> tools, double dpiScale)
    {
        Tools = [.. tools];
        Dpi = dpiScale;
        SyncCount++;
    }

    public void RelayMouse(uint message, nint wParam, nint lParam) => Relayed.Add(message);

    public void Dispose() => Disposed = true;
}

internal sealed class FakeMenuHost : IPopupMenuHost
{
    public string? Choose { get; set; }

    public int ShowCount { get; private set; }

    public IReadOnlyList<MenuItemModel>? LastItems { get; private set; }

    public PointD LastPoint { get; private set; }

    /// <summary>Chạy trong lúc menu "đang mở" (kiểm tra trạng thái modal).</summary>
    public Action? WhileOpen { get; set; }

    public string? Show(IReadOnlyList<MenuItemModel> items, PointD screenPointDip)
    {
        ShowCount++;
        LastItems = items;
        LastPoint = screenPointDip;
        WhileOpen?.Invoke();
        return Choose;
    }
}

/// <summary>
/// Bộ dựng <see cref="ShellOverlay"/> với đồng hồ khung + đồng hồ Stopwatch giả (1 tick = 1 ms): test chỉ gọi
/// <see cref="Advance"/>; không phụ thuộc kích thước màn hình hay thời gian thật.
/// </summary>
internal sealed class OverlayRig : IDisposable
{
    private readonly bool _ownsText;

    public OverlayRig(ITextRenderer? text = null, AppSettings? settings = null, bool menus = true, bool tooltips = true)
    {
        Settings = settings ?? new AppSettings
        {
            ToolbarAutoHide = true,
            ToolbarAutoHideDelayMs = 1500,
            InfoOverlayAutoHide = true,
            InfoOverlayAutoHideDelayMs = 3000,
            ToolbarOpacityPercent = 100,
        };
        _ownsText = text is null;
        Text = text ?? new FakeTextRenderer();
        Clock = new FakeFrameClock();
        Commands = new FakeOverlayCommands();
        Tooltips = tooltips ? new FakeTooltips() : null;
        Menus = menus ? new FakeMenuHost() : null;
        Overlay = new ShellOverlay(new ShellOverlayOptions
        {
            Text = Text,
            Clock = Clock,
            Settings = () => Settings,
            Commands = Commands,
            RequestRender = () => RenderRequests++,
            IsWindowActive = () => WindowActive,
            Menus = Menus,
            Tooltips = Tooltips,
            ClientOriginOnScreenDip = () => ClientOrigin,
            OnCommandFailed = (name, ex) => Failures.Add((name, ex)),
            Timestamp = () => Now,
            TicksPerSecond = 1000,
            AnimatorTicksPerSecond = 1000,
        });
        Overlay.Arrange(new SizeD(1000, 700), 1.0);
    }

    public AppSettings Settings { get; }

    public ITextRenderer Text { get; }

    public FakeFrameClock Clock { get; }

    public FakeOverlayCommands Commands { get; }

    public FakeTooltips? Tooltips { get; }

    public FakeMenuHost? Menus { get; }

    public ShellOverlay Overlay { get; }

    public long Now { get; private set; } = 10_000;

    public bool WindowActive { get; set; } = true;

    public PointD ClientOrigin { get; set; } = new(100, 50);

    public int RenderRequests { get; private set; }

    public List<(string Name, Exception Exception)> Failures { get; } = [];

    /// <summary>Hộp bao của toolbar sau Arrange (8, 8, w, h).</summary>
    public RectD ToolbarBounds => Overlay.Toolbar.Bounds;

    /// <summary>Điểm giữa hộp phần tử.</summary>
    public static PointD Center(OverlayElement element) =>
        new(element.Bounds.X + (element.Bounds.Width / 2), element.Bounds.Y + (element.Bounds.Height / 2));

    /// <summary>Tiến đồng hồ <paramref name="ms"/> ms, phát khung mỗi 1 ms (độ phân giải đủ cho mọi ngưỡng).</summary>
    public void Advance(int ms)
    {
        for (int i = 0; i < ms; i++)
        {
            Now++;
            Clock.Tick(Now);
        }
    }

    /// <summary>Sửa model hiện hành rồi đẩy vào overlay (như view model báo thay đổi).</summary>
    public void Update(Func<OverlayModel, OverlayModel> edit) => Overlay.Update(edit(Overlay.Model));

    public void MoveTo(PointD point) => Overlay.PointerMoved(point);

    /// <summary>Chuột ở xa toolbar (ngoài hot zone) - ảnh ở giữa cửa sổ.</summary>
    public static PointD FarAway => new(600, 400);

    public void Dispose()
    {
        Overlay.Dispose();
        if (_ownsText && Text is IDisposable d)
        {
            d.Dispose();
        }
    }
}
