using System.Diagnostics;
using PhotoReview.App.Input;

namespace PhotoReview.Shell.Win32.Overlay;

/// <summary>Phần tử có opacity chỉnh được (animator chỉ cần chừng này).</summary>
public interface IOpacityTarget
{
    float Opacity { get; set; }
}

/// <summary>
/// WP-17 (C-11): tween opacity theo <see cref="IFrameClock"/> - đồng hồ khung do người gọi tiêm (test dùng đồng hồ giả, tick
/// với <c>Timestamp</c> tự chọn; <c>ticksPerSecond</c> mặc định = <see cref="Stopwatch.Frequency"/>).
/// <para>
/// Ngữ nghĩa (như <c>FadeTargetGate</c> + <c>BeginAnimation</c> của WPF): <see cref="FadeTo"/> cùng đích với tween đang chạy
/// thì giữ nguyên (không khởi động lại); khác đích thì HUỶ tween cũ và chạy tween mới từ opacity hiện tại. Mốc t = 0 là
/// <c>Timestamp</c> của khung ĐẦU TIÊN sau <see cref="FadeTo"/> (opacity khung đó = giá trị đầu). Đồng hồ chỉ được đăng ký
/// (armed) khi có tween - hết tween thì ngừng nghe để vòng lặp khung nghỉ.
/// </para>
/// Dùng trên luồng UI.
/// </summary>
public sealed class Animator : IAnimator, IDisposable
{
    private readonly IFrameClock _clock;
    private readonly Func<string, IOpacityTarget?> _resolve;
    private readonly double _ticksPerSecond;
    private readonly Dictionary<string, Tween> _tweens = new(StringComparer.Ordinal);
    private readonly List<string> _finished = [];
    private bool _subscribed;
    private bool _disposed;

    public Animator(IFrameClock clock, Func<string, IOpacityTarget?> resolve)
        : this(clock, resolve, Stopwatch.Frequency)
    {
    }

    public Animator(IFrameClock clock, Func<string, IOpacityTarget?> resolve, long ticksPerSecond)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ticksPerSecond, 0);
        _clock = clock;
        _resolve = resolve;
        _ticksPerSecond = ticksPerSecond;
    }

    /// <summary>Sau mỗi khung có opacity đổi (host vẽ lại).</summary>
    public event EventHandler? Changed;

    public bool IsAnimating => _tweens.Count > 0;

    /// <summary>Số tween đang chạy.</summary>
    public int ActiveCount => _tweens.Count;

    public void FadeTo(string elementId, float target, TimeSpan duration, Easing easing)
    {
        ArgumentNullException.ThrowIfNull(elementId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(easing))
        {
            throw new ArgumentOutOfRangeException(nameof(easing), easing, null);
        }

        target = float.IsNaN(target) ? 0f : Math.Clamp(target, 0f, 1f);
        IOpacityTarget? element = _resolve(elementId);
        if (element is null)
        {
            _tweens.Remove(elementId);
            StopListeningWhenIdle();
            return;
        }

        if (_tweens.TryGetValue(elementId, out Tween? running) && running.Target == target)
        {
            return;
        }

        _tweens.Remove(elementId);
        if (duration <= TimeSpan.Zero || element.Opacity == target)
        {
            bool changed = element.Opacity != target;
            element.Opacity = target;
            StopListeningWhenIdle();
            if (changed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        _tweens[elementId] = new Tween(element.Opacity, target, duration.TotalSeconds, easing);
        if (!_subscribed)
        {
            _subscribed = true;
            _clock.Frame += OnFrame;
        }

        _clock.RequestFrame();
    }

    /// <summary>Huỷ tween của phần tử (opacity giữ giá trị hiện tại).</summary>
    public void Cancel(string elementId)
    {
        ArgumentNullException.ThrowIfNull(elementId);
        if (_tweens.Remove(elementId))
        {
            StopListeningWhenIdle();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tweens.Clear();
        StopListeningWhenIdle();
    }

    /// <summary>Hàm làm mượt: 0..1 -&gt; 0..1.</summary>
    internal static double Ease(Easing easing, double t) => easing switch
    {
        Easing.Linear => t,
        Easing.QuadraticOut => 1 - ((1 - t) * (1 - t)),
        _ => throw new ArgumentOutOfRangeException(nameof(easing), easing, null),
    };

    private void OnFrame(object? sender, FrameTickEventArgs e)
    {
        long now = e.Tick.Timestamp;
        bool changed = false;
        _finished.Clear();
        foreach ((string id, Tween tween) in _tweens)
        {
            IOpacityTarget? element = _resolve(id);
            if (element is null)
            {
                _finished.Add(id);
                continue;
            }

            tween.Start ??= now;
            double elapsed = Math.Max(0, (now - tween.Start.Value) / _ticksPerSecond);
            double progress = Math.Min(1, elapsed / tween.DurationSeconds);
            float value = progress >= 1
                ? tween.Target
                : (float)(tween.From + ((tween.Target - tween.From) * Ease(tween.Easing, progress)));
            if (element.Opacity != value)
            {
                element.Opacity = value;
                changed = true;
            }

            if (progress >= 1)
            {
                _finished.Add(id);
            }
        }

        foreach (string id in _finished)
        {
            _tweens.Remove(id);
        }

        StopListeningWhenIdle();
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void StopListeningWhenIdle()
    {
        if (_subscribed && _tweens.Count == 0)
        {
            _subscribed = false;
            _clock.Frame -= OnFrame;
        }
    }

    private sealed class Tween(float from, float target, double durationSeconds, Easing easing)
    {
        public float From { get; } = from;

        public float Target { get; } = target;

        public double DurationSeconds { get; } = durationSeconds;

        public Easing Easing { get; } = easing;

        public long? Start { get; set; }
    }
}
