using System.Diagnostics;
using System.Globalization;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Win32.Hosting;

namespace PhotoReview.Shell.Win32.Viewing;

/// <summary>
/// WP21-STALL: cấu hình giữ GPU ấm. Mặc định BẬT (nguyên nhân + số đo: docs/refactoring/perf/2026-10-12-wp21-stall.md).
/// <list type="bullet">
/// <item><c>PHOTOREVIEW_GPU_KEEPWARM=0</c> (hoặc <c>off</c>/<c>false</c>/<c>no</c>): tắt hẳn (không tạo luồng, không thiết bị).</item>
/// <item><c>PHOTOREVIEW_GPU_KEEPWARM_MS</c>: giữ ấm bao lâu sau tương tác cuối (250-60000, mặc định 5000).</item>
/// <item><c>PHOTOREVIEW_GPU_KEEPWARM_INTERVAL_MS</c>: khoảng giữa hai lần nộp (5-100, mặc định 20; &gt; ~25 ms thì GPU kịp rỗi).</item>
/// </list>
/// </summary>
internal sealed record KeepWarmOptions(bool Enabled = true, int WindowMs = KeepWarmOptions.DefaultWindowMs,
    int IntervalMs = KeepWarmOptions.DefaultIntervalMs)
{
    internal const string EnabledVariable = "PHOTOREVIEW_GPU_KEEPWARM";
    internal const string WindowVariable = "PHOTOREVIEW_GPU_KEEPWARM_MS";
    internal const string IntervalVariable = "PHOTOREVIEW_GPU_KEEPWARM_INTERVAL_MS";
    internal const int DefaultWindowMs = 5000;
    internal const int DefaultIntervalMs = 20;
    internal const int MinWindowMs = 250;
    internal const int MaxWindowMs = 60_000;
    internal const int MinIntervalMs = 5;
    internal const int MaxIntervalMs = 100;

    internal static KeepWarmOptions FromEnvironment(Func<string, string?>? get = null)
    {
        get ??= Environment.GetEnvironmentVariable;
        var enabled = true;
        if (get(EnabledVariable) is { } raw)
        {
            var value = raw.Trim();
            enabled = !(value is "0" || value.Equals("off", StringComparison.OrdinalIgnoreCase)
                || value.Equals("false", StringComparison.OrdinalIgnoreCase) || value.Equals("no", StringComparison.OrdinalIgnoreCase));
        }

        return new KeepWarmOptions(enabled,
            ReadInt(get, WindowVariable, DefaultWindowMs, MinWindowMs, MaxWindowMs),
            ReadInt(get, IntervalVariable, DefaultIntervalMs, MinIntervalMs, MaxIntervalMs));
    }

    private static int ReadInt(Func<string, string?> get, string name, int fallback, int min, int max) =>
        int.TryParse(get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>
/// WP21-STALL: quyết định KHI NÀO giữ GPU ấm. Mỗi tương tác của người dùng (phím, chuột, cuộn, cảm ứng, kích hoạt cửa sổ) khi cửa
/// sổ ĐANG ACTIVE kéo dài khoảng giữ ấm thêm <see cref="KeepWarmOptions.WindowMs"/>; hết khoảng đó (hoặc cửa sổ mất active) thì thôi -
/// không có tương tác = không có gì chạy. Chỉ NGHE thông điệp, không bao giờ xử lý (luôn trả false: hành vi nhập liệu không đổi).
/// Gộp (throttle) các tương tác dày (chuột 1000 Hz) thành một lần kéo dài mỗi <see cref="ThrottleMs"/> ms. UI thread.
/// </summary>
internal sealed class KeepWarmController : IWindowMessageHandler
{
    /// <summary>Hai lần kéo dài cách nhau tối thiểu chừng này (ms): đủ nhỏ so với cửa sổ (>= 250 ms), đủ lớn để chuột 1 kHz rẻ.</summary>
    internal const int ThrottleMs = 100;

    private readonly KeepWarmOptions _options;
    private readonly Func<bool> _isWindowActive;
    private readonly Action<long> _activate;
    private readonly Func<long> _timestamp;
    private long _lastActivation;
    private bool _hasActivation;

    public KeepWarmController(KeepWarmOptions options, Func<bool> isWindowActive, Action<long> activate, Func<long>? timestamp = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _isWindowActive = isWindowActive ?? throw new ArgumentNullException(nameof(isWindowActive));
        _activate = activate ?? throw new ArgumentNullException(nameof(activate));
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
    }

    /// <summary>Số lần đã kéo dài khoảng giữ ấm (chẩn đoán/test).</summary>
    internal long Activations { get; private set; }

    /// <summary>Số lần giữ ấm lúc khởi động (<see cref="WarmAtLaunch"/>; chẩn đoán/test). Không tính vào <see cref="Activations"/>.</summary>
    internal long LaunchWarmups { get; private set; }

    /// <summary>Thông điệp nào là "người dùng đang tương tác" (kể cả kích hoạt cửa sổ: bấm vào cửa sổ để lấy focus).</summary>
    internal static bool IsInteraction(uint message) => message is
        WindowMessages.WmKeyDown or WindowMessages.WmSysKeyDown or WindowMessages.WmMouseMove or WindowMessages.WmLButtonDown
        or WindowMessages.WmRButtonDown or WindowMessages.WmMButtonDown or WindowMessages.WmXButtonDown or WindowMessages.WmMouseWheel
        or WindowMessages.WmMouseHWheel or WindowMessages.WmActivate or WmTouch or WmPointerUpdate or WmPointerDown;

    private const uint WmTouch = 0x0240;
    private const uint WmPointerUpdate = 0x0245;
    private const uint WmPointerDown = 0x0246;

    public bool TryHandle(in WindowMessage message, out nint result)
    {
        result = 0;
        if (IsInteraction(message.Msg))
        {
            NoteInteraction();
        }

        return false;
    }

    /// <summary>
    /// perf(gate 3): giữ ấm NGAY khi ảnh đầu đã Present, không chờ tương tác đầu tiên. Lần nộp việc đầu cho GPU sau khi nó rỗi bị
    /// chặn ~0,3-0,5 s; trước đây phím điều hướng ĐẦU TIÊN (thường vài giây sau khi mở) chính là lần nộp đó nên bị chặn ở
    /// 25-50 % số lần, vì giữ ấm chỉ bắt đầu từ tương tác. Chỉ kéo dài thêm <see cref="KeepWarmOptions.WindowMs"/> một lần (cùng
    /// cơ chế hạn chót, không bao giờ rút ngắn); không đổi <see cref="Activations"/> hay throttle của tương tác.
    /// </summary>
    internal void WarmAtLaunch()
    {
        if (!_options.Enabled)
        {
            return;
        }

        LaunchWarmups++;
        _activate(_timestamp() + (_options.WindowMs * Stopwatch.Frequency / 1000));
    }

    /// <summary>Có tương tác: kéo dài khoảng giữ ấm nếu cửa sổ đang active và đã qua <see cref="ThrottleMs"/> kể từ lần trước.</summary>
    internal void NoteInteraction()
    {
        if (!_options.Enabled || !_isWindowActive())
        {
            return;
        }

        var now = _timestamp();
        if (_hasActivation && now - _lastActivation < ThrottleMs * Stopwatch.Frequency / 1000)
        {
            return;
        }

        _lastActivation = now;
        _hasActivation = true;
        Activations++;
        _activate(now + (_options.WindowMs * Stopwatch.Frequency / 1000));
    }
}
