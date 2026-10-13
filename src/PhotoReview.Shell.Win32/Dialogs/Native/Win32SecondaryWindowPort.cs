using Microsoft.Extensions.DependencyInjection;
using PhotoReview.App.Localization;
using PhotoReview.App.Presenters;
using PhotoReview.App.Services;
using PhotoReview.Core.Abstractions;
using PhotoReview.Core.Catalog;
using PhotoReview.Core.Diagnostics;
using PhotoReview.Core.Settings;
using PhotoReview.Core.Updates;
using PhotoReview.Imaging.Decoding;
using PhotoReview.Imaging.Decoding.Wic;
using PhotoReview.Shell.Win32.Dialogs.SettingsUi;
using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.Win32.Dialogs.Native;

/// <summary>Phần phụ thuộc có thể thay của cửa sổ Benchmark (test); null = thật.</summary>
internal sealed record BenchmarkDialogSeams(
    BenchmarkSessionFactory? Sessions = null,
    BenchmarkFileScan? Scan = null,
    BenchmarkSummarySave? Save = null,
    Func<nint, string, string?, string?>? PickFolder = null,
    Action<string>? OpenInExplorer = null);

/// <summary>
/// WP-35 GĐ1: cổng cửa sổ phụ KHÔNG WPF (hộp thoại Win32 gốc, AOT-an toàn). Làm được: Diagnostics, SkippedFiles, BatchReview,
/// ZoomPrompt (<see cref="Supports"/>); Settings/Recovery/Benchmark chưa có (trả false/no-op) nên <see cref="SecondaryWindowRouter"/>
/// chuyển sang cầu WPF hoặc cổng tạm. Mọi hàm gọi trên UI thread của shell; <see cref="Dispose"/> đóng các hộp thoại còn mở
/// (shell thoát giữa chừng) - sau đó mọi lệnh hiện là no-op trả về như Huỷ.
/// </summary>
internal sealed class Win32SecondaryWindowPort : ISecondaryWindowPort, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly INativeDialogChrome? _chrome;
    private readonly Action<NativeDialog>? _ready;
    private readonly MessageLoop? _loop;
    private readonly List<NativeDialog> _open = [];
    private readonly IDialogWarningSink? _warnings;
    private readonly BenchmarkDialogSeams? _benchmarkSeams;
    private readonly SettingsDialogOptions? _settingsOptions;
    private readonly Func<bool>? _settingsEnabled;
    private BenchmarkDialog? _benchmark;
    private readonly int _uiThreadId = Environment.CurrentManagedThreadId;
    private bool _disposed;

    /// <param name="services">DI của shell (ReviewMetrics, LatestExplorerSnapshot, IClipboardService).</param>
    /// <param name="chrome">Giao diện tối (null = <see cref="DarkDialogChrome"/>); test thay bản ghi lại.</param>
    /// <param name="ready">Test: gọi sau khi hộp thoại đã hiện, trước vòng lặp modal (mô phỏng người dùng).</param>
    /// <param name="loop">Vòng lặp của shell: vòng modal chạy lồng trong nó (khung hình, việc nền, phím WPF modeless); null = đơn giản.</param>
    public Win32SecondaryWindowPort(IServiceProvider services, INativeDialogChrome? chrome = null, Action<NativeDialog>? ready = null,
        MessageLoop? loop = null, IDialogWarningSink? warnings = null, BenchmarkDialogSeams? benchmark = null,
        SettingsDialogOptions? settings = null, Func<bool>? settingsEnabled = null)
    {
        _settingsOptions = settings;
        _settingsEnabled = settingsEnabled;
        _warnings = warnings;
        _benchmarkSeams = benchmark;
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _chrome = chrome;
        _ready = ready;
        _loop = loop;
    }

    /// <summary>Số hộp thoại đang mở (kể cả lồng nhau).</summary>
    internal int OpenCount => _open.Count;

    /// <summary>Biến môi trường (ẩn) bật cửa sổ Settings Win32 khi chưa đủ trang: <c>PHOTOREVIEW_SETTINGS_WIN32=1</c>.</summary>
    internal const string SettingsEnvironmentVariable = "PHOTOREVIEW_SETTINGS_WIN32";

    /// <summary>
    /// WP-35 GĐ3a: cổng Win32 nhận Settings khi ĐỦ MỌI trang (<see cref="SettingsPageCatalog.IsComplete"/>) hoặc khi bật cờ ẩn; chưa đủ
    /// thì cầu WPF (bản hybrid) hoặc cổng tạm (Native AOT: mở config.json) làm mặc định.
    /// </summary>
    internal bool SettingsEnabled
        => _settingsEnabled?.Invoke()
            ?? (SettingsPageCatalog.IsComplete
                || string.Equals(Environment.GetEnvironmentVariable(SettingsEnvironmentVariable), "1", StringComparison.Ordinal));

    public bool Supports(SecondaryWindowKind kind)
        => kind is SecondaryWindowKind.Diagnostics or SecondaryWindowKind.SkippedFiles or SecondaryWindowKind.BatchReview
            or SecondaryWindowKind.ZoomPrompt or SecondaryWindowKind.Benchmark or SecondaryWindowKind.ActionProfiles
            || (kind == SecondaryWindowKind.Settings && SettingsEnabled);

    /// <summary>
    /// WP-35 GĐ3a: Settings MODAL (<see cref="SettingsDialog"/>); true khi đã lưu (Save hoặc Apply) để người gọi nạp lại cấu hình.
    /// </summary>
    public bool ShowSettings(nint ownerHwnd, SettingsTarget target)
    {
        if (_disposed)
        {
            return false;
        }

        var store = _services.GetService<SettingsStore>();
        if (store is null)
        {
            return false;
        }

        var baseOptions = _settingsOptions ?? new SettingsDialogOptions();
        var options = baseOptions with
        {
            Target = target,
            Files = baseOptions.Files ?? _services.GetService<IShellFileDialog>(),
            Warnings = baseOptions.Warnings ?? _warnings,
            ShowActionProfiles = baseOptions.ShowActionProfiles ?? ShowActionProfiles,
        };
        var dialog = new SettingsDialog(store.Current, store, _services.GetService<IImageDecoderFactory>(),
            _services.GetService<LocalizationService>(), _services.GetService<IUpdateChecker>(), options, _chrome);
        Run(dialog, ownerHwnd);
        return dialog.Saved;
    }

    public void ShowRecovery(nint ownerHwnd)
    {
    }

    /// <summary>
    /// WP-35 GĐ2: Benchmark MODELESS (không chặn shell), một cửa sổ duy nhất - mở lần hai chỉ đưa cửa sổ đang có lên trước.
    /// Chạy lượt đo bằng codec/decoder không WPF của shell (<see cref="PixelBufferImageCodec"/> + WIC trực tiếp: mặc định của executor).
    /// </summary>
    public void ShowBenchmark(nint ownerHwnd, string? folder)
    {
        if (_disposed)
        {
            return;
        }

        VerifyUiThread();
        if (_benchmark is { IsClosed: false } existing)
        {
            existing.Activate();
            return;
        }

        var folders = _services.GetService<IShellFolderDialog>();
        var seams = _benchmarkSeams;
        var dialog = new BenchmarkDialog(
            folder,
            _services.GetRequiredService<IUiScheduler>(),
            seams?.Sessions
                ?? BenchmarkProfileSession.Factory(PixelBufferImageCodec.Instance, () => new WicDirectDecoder(PixelBufferImageCodec.Instance)),
            pickFolder: seams?.PickFolder ?? (folders is null
                ? null
                : (owner, title, initial) => folders.Show(owner, title, FolderPicker.UsableInitialFolder(initial))),
            openInExplorer: seams?.OpenInExplorer,
            scan: seams?.Scan,
            save: seams?.Save,
            chrome: _chrome);
        _benchmark = dialog;
        _open.Add(dialog);
        try
        {
            dialog.ShowModeless(ownerHwnd, _loop, _ready, () =>
            {
                _open.Remove(dialog);
                if (ReferenceEquals(_benchmark, dialog))
                {
                    _benchmark = null;
                }
            });
        }
        catch
        {
            _open.Remove(dialog);
            _benchmark = null;
            throw;
        }
    }

    /// <summary>Benchmark đang mở (null khi chưa mở/đã đóng): test và chẩn đoán.</summary>
    internal bool IsBenchmarkOpen => _benchmark is { IsClosed: false };

    /// <summary>WP-35 GĐ2: Action profiles MODAL (owner bị khoá); danh sách đã sửa khi Apply, null khi huỷ.</summary>
    public IReadOnlyList<ReviewAction>? ShowActionProfiles(nint ownerHwnd, IReadOnlyList<ReviewAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var dialog = new ActionProfilesDialog(actions, _services.GetService<IShellFileDialog>(), _warnings, _chrome);
        return Run(dialog, ownerHwnd) == NativeDialog.IdOk ? dialog.Actions : null;
    }

    public void ShowDiagnostics(nint ownerHwnd)
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = _services.GetService<ReviewMetrics>()?.Snapshot() ?? new ReviewMetrics().Snapshot();
        var display = DiagnosticsPresenter.Build(snapshot, _services.GetService<LatestExplorerSnapshot>()?.Current);
        var clipboard = _services.GetService<IClipboardService>();
        Run(new DiagnosticsDialog(display, text => clipboard?.TrySetText(text) ?? false, _chrome), ownerHwnd);
    }

    public void ShowSkippedFiles(nint ownerHwnd, IReadOnlyList<SkippedEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Run(new ListDialog(ListDialogContent.ForSkippedFiles(entries), _chrome), ownerHwnd);
    }

    public bool ShowBatchReview(nint ownerHwnd, IReadOnlyList<BatchReviewItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return Run(new ListDialog(ListDialogContent.ForBatchReview(items), _chrome), ownerHwnd) == NativeDialog.IdOk;
    }

    public int? PromptCustomZoom(nint ownerHwnd, int currentPercent)
    {
        var dialog = new ZoomPromptDialog(currentPercent, _chrome);
        return Run(dialog, ownerHwnd) == NativeDialog.IdOk ? dialog.Value : null;
    }

    private int Run(NativeDialog dialog, nint owner)
    {
        if (_disposed)
        {
            return NativeDialog.IdCancel;
        }

        VerifyUiThread();

        _open.Add(dialog);
        try
        {
            return dialog.ShowModal(owner, _ready, _loop);
        }
        finally
        {
            _open.Remove(dialog);
        }
    }

    private void VerifyUiThread()
    {
        if (Environment.CurrentManagedThreadId != _uiThreadId)
        {
            throw new InvalidOperationException("Native dialogs must be shown on the shell UI thread.");
        }
    }

    /// <summary>Đóng mọi hộp thoại còn mở (lồng nhau: cái trong cùng trước; modeless cuối cùng). Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var snapshot = _open.ToArray(); // modeless tự rút khỏi _open khi đóng
        for (var i = snapshot.Length - 1; i >= 0; i--)
        {
            snapshot[i].Close();
        }
    }
}

/// <summary>
/// WP-35 GĐ1: định tuyến theo TỪNG cửa sổ - cổng Win32 cho cửa sổ nó hỗ trợ; cửa sổ chưa có thì rơi về cầu WPF (nạp lười, chỉ khi
/// cần); cầu vắng (bản AOT) thì rơi về <see cref="SecondaryWindowFallback"/>. Chủ sở hữu của hai cổng: <see cref="Dispose"/>
/// đóng cả hai (cầu chỉ khi đã nạp: không nạp WPF để dọn).
/// </summary>
internal sealed class SecondaryWindowRouter : ISecondaryWindowPort, IDisposable
{
    private readonly ISecondaryWindowPort _native;
    private readonly Lazy<ISecondaryWindowPort?> _bridge;
    private readonly SecondaryWindowFallback? _fallback;

    public SecondaryWindowRouter(ISecondaryWindowPort native, Lazy<ISecondaryWindowPort?> bridge, SecondaryWindowFallback? fallback)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _fallback = fallback;
    }

    public bool Supports(SecondaryWindowKind kind) => true;

    public bool ShowSettings(nint ownerHwnd, SettingsTarget target)
        => Pick(SecondaryWindowKind.Settings) is { } port ? port.ShowSettings(ownerHwnd, target) : _fallback?.OpenSettings() ?? false;

    public void ShowRecovery(nint ownerHwnd)
    {
        if (Pick(SecondaryWindowKind.Recovery) is { } port) port.ShowRecovery(ownerHwnd);
        else _fallback?.Recovery();
    }

    public void ShowDiagnostics(nint ownerHwnd)
    {
        if (Pick(SecondaryWindowKind.Diagnostics) is { } port) port.ShowDiagnostics(ownerHwnd);
        else _fallback?.Diagnostics();
    }

    public void ShowBenchmark(nint ownerHwnd, string? folder)
    {
        if (Pick(SecondaryWindowKind.Benchmark) is { } port) port.ShowBenchmark(ownerHwnd, folder);
        else _fallback?.Benchmark();
    }

    public void ShowSkippedFiles(nint ownerHwnd, IReadOnlyList<SkippedEntry> entries)
    {
        if (Pick(SecondaryWindowKind.SkippedFiles) is { } port) port.ShowSkippedFiles(ownerHwnd, entries);
        else _fallback?.SkippedFiles();
    }

    public bool ShowBatchReview(nint ownerHwnd, IReadOnlyList<BatchReviewItem> items)
        => Pick(SecondaryWindowKind.BatchReview) is { } port ? port.ShowBatchReview(ownerHwnd, items) : _fallback?.BatchReview() ?? false;

    public int? PromptCustomZoom(nint ownerHwnd, int currentPercent)
        => Pick(SecondaryWindowKind.ZoomPrompt) is { } port ? port.PromptCustomZoom(ownerHwnd, currentPercent) : null;

    public IReadOnlyList<ReviewAction>? ShowActionProfiles(nint ownerHwnd, IReadOnlyList<ReviewAction> actions)
        => Pick(SecondaryWindowKind.ActionProfiles) is { } port ? port.ShowActionProfiles(ownerHwnd, actions) : null;

    /// <summary>Cổng Win32 nếu nó làm được; ngược lại cầu WPF (nạp lười), hoặc null khi cầu vắng/lỗi nạp.</summary>
    private ISecondaryWindowPort? Pick(SecondaryWindowKind kind)
    {
        if (_native.Supports(kind))
        {
            return _native;
        }

        try
        {
            var bridge = _bridge.Value;
            return bridge is not null && bridge.Supports(kind) ? bridge : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        (_native as IDisposable)?.Dispose();
        if (_bridge.IsValueCreated && _bridge.Value is IDisposable bridge)
        {
            bridge.Dispose();
        }
    }
}
