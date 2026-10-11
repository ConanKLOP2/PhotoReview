using PhotoReview.Imaging.Pixels;
using PhotoReview.Shell.Rendering;

namespace PhotoReview.Shell.Win32.Viewing;

/// <summary>Những gì <see cref="PresentationPrefetcher"/> cần từ host (cửa sổ thật hay giả trong test).</summary>
internal interface IPrefetchHost
{
    /// <summary>UI thread: đường dẫn của mục thứ <paramref name="index"/> trong danh mục (null khi ngoài phạm vi).</summary>
    string? PathAt(int index);

    /// <summary>
    /// Có thể chạy trên thread nền (stat file để dựng khoá cache): buffer preview đã decode của từng đường dẫn, null nếu chưa có.
    /// </summary>
    PixelBuffer?[] LookupCached(string[] paths);

    /// <summary>Chạy <paramref name="work"/> ngoài UI thread.</summary>
    void RunBackground(Action work);

    /// <summary>Chạy <paramref name="action"/> trên UI thread (kể cả khi gọi từ thread nền).</summary>
    void PostToUi(Action action);

    /// <summary>Chạy <paramref name="action"/> trên UI thread sau <paramref name="delay"/>.</summary>
    void ScheduleOnUi(TimeSpan delay, Action action);
}

/// <summary>
/// WP-21: <c>GpuImageCache.Prefetch</c> các ảnh kế tiếp THEO HƯỚNG duyệt. Preview của hàng xóm do <c>PreloadController</c> decode
/// vào cache RAM (không đồng bộ với việc điều hướng), nên mỗi lần điều hướng chạy một lượt ngay rồi vài lượt thử lại tăng dần
/// (<see cref="RetryDelaysMs"/>) cho tới khi có buffer; lượt cũ bị bỏ khi đã điều hướng tiếp. Việc tra cache (stat file) ở thread
/// nền; chỉ <c>Prefetch</c> (UI affine) về UI thread, và bản thân nó chạy từng dải 2 MB ở <c>UiPriority.Background</c> (WP-15) nên
/// không chặn UI. Cung cấp id để cửa sổ <c>Retain</c> (LRU không đuổi chúng).
/// </summary>
internal sealed class PresentationPrefetcher
{
    /// <summary>Số ảnh phía trước (theo hướng đi) và phía sau.</summary>
    internal const int Ahead = 2;

    internal const int Behind = 1;

    /// <summary>Các lượt thử lại sau lần điều hướng (ms): preload hàng xóm xong dần.</summary>
    internal static readonly int[] RetryDelaysMs = [120, 400, 1000];

    private readonly GpuImageCache _cache;
    private readonly IPrefetchHost _host;
    private readonly HashSet<long> _planned = [];
    private int _lastIndex = -1;
    private int _direction = 1;
    private long _generation;

    internal PresentationPrefetcher(GpuImageCache cache, IPrefetchHost host)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(host);
        _cache = cache;
        _host = host;
    }

    /// <summary>Hướng duyệt hiện tại: +1 tiến, -1 lùi (giữ hướng cũ khi vẫn đứng yên).</summary>
    internal int Direction => _direction;

    /// <summary>Các id buffer đã được yêu cầu prefetch trong lượt gần nhất.</summary>
    internal IReadOnlyCollection<long> PlannedIds => _planned;

    /// <summary>Số lần <c>Prefetch</c> đã gọi (chẩn đoán/test).</summary>
    internal int PrefetchCalls { get; private set; }

    /// <summary>Raised sau mỗi lượt để cửa sổ cập nhật <c>Retain</c>.</summary>
    internal event EventHandler? PlanChanged;

    /// <summary>
    /// Chỉ số ứng viên theo thứ tự ưu tiên: <paramref name="ahead"/> ảnh theo <paramref name="direction"/> rồi
    /// <paramref name="behind"/> ảnh ngược lại; bỏ ngoài [0, count) (không vòng tròn: đầu/cuối danh sách là hết).
    /// </summary>
    internal static IReadOnlyList<int> Plan(int count, int index, int direction, int ahead = Ahead, int behind = Behind)
    {
        var step = direction < 0 ? -1 : 1;
        var result = new List<int>(ahead + behind);
        for (var i = 1; i <= ahead; i++)
        {
            Add(result, count, index + (step * i));
        }

        for (var i = 1; i <= behind; i++)
        {
            Add(result, count, index - (step * i));
        }

        return result;
    }

    /// <summary>Gọi (UI thread) khi ảnh hiện tại (chỉ số trong danh mục) đổi. Hướng = dấu của (index - lần trước).</summary>
    internal void Navigated(int index, int count)
    {
        if (index < 0 || count <= 0)
        {
            _planned.Clear();
            _lastIndex = -1;
            _generation++;
            return;
        }

        if (_lastIndex >= 0 && index != _lastIndex)
        {
            _direction = index > _lastIndex ? 1 : -1;
        }

        _lastIndex = index;
        var generation = ++_generation;
        Pass(index, count, generation);
        foreach (var delay in RetryDelaysMs)
        {
            _host.ScheduleOnUi(TimeSpan.FromMilliseconds(delay), () =>
            {
                if (generation == _generation)
                {
                    Pass(index, count, generation);
                }
            });
        }
    }

    private void Pass(int index, int count, long generation)
    {
        var paths = new List<string>(Ahead + Behind);
        foreach (var candidate in Plan(count, index, _direction))
        {
            if (_host.PathAt(candidate) is { } path)
            {
                paths.Add(path);
            }
        }

        if (paths.Count == 0)
        {
            return;
        }

        var array = paths.ToArray();
        _host.RunBackground(() =>
        {
            var found = _host.LookupCached(array);
            _host.PostToUi(() =>
            {
                if (generation == _generation)
                {
                    Apply(found);
                }
            });
        });
    }

    private void Apply(PixelBuffer?[] found)
    {
        _planned.Clear();
        foreach (var pixels in found)
        {
            if (pixels is not { IsDisposed: false })
            {
                continue;
            }

            _planned.Add(pixels.Id);
            if (!_cache.Contains(pixels.Id))
            {
                _cache.Prefetch(pixels);
                PrefetchCalls++;
            }
        }

        PlanChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void Add(List<int> result, int count, int index)
    {
        if (index >= 0 && index < count)
        {
            result.Add(index);
        }
    }
}
