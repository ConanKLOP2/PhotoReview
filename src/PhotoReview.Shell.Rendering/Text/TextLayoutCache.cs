using PhotoReview.App.Input;
using PhotoReview.Shell.Interop;
using PhotoReview.Shell.Interop.Graphics;

namespace PhotoReview.Shell.Rendering;

/// <summary>Khoá cache layout: mọi thứ làm đổi kết quả DirectWrite (C-11: text, style, maxWidth, trimming) + locale.</summary>
internal readonly record struct TextLayoutKey(
    string Text, string FontFamily, float FontSizeDip, bool Bold, float MaxWidth, TextTrimming Trimming);

/// <summary>
/// WP-17: một layout DirectWrite đã dựng + số đo. Sống trong <see cref="TextLayoutCache"/>; số ref đếm các
/// <see cref="DWriteTextLayoutLease"/> đang giữ nó. COM chỉ được trả khi vừa bị loại khỏi cache vừa hết lease.
/// </summary>
internal sealed class TextLayoutEntry : IDisposable
{
    private IDWriteTextLayout? _native;

    public TextLayoutEntry(TextLayoutKey key, string text, IDWriteTextLayout native, SizeD size, int lineCount, bool isTrimmed)
    {
        Key = key;
        Text = text;
        _native = native;
        Size = size;
        LineCount = lineCount;
        IsTrimmed = isTrimmed;
    }

    public TextLayoutKey Key { get; }

    public string Text { get; }

    public SizeD Size { get; }

    public int LineCount { get; }

    public bool IsTrimmed { get; }

    public int RefCount { get; set; }

    public bool Evicted { get; set; }

    public bool IsReleased => _native is null;

    public IDWriteTextLayout Native => _native ?? throw new ObjectDisposedException(nameof(TextLayoutEntry));

    public void Dispose()
    {
        ComInterop.Release(_native);
        _native = null;
    }
}

/// <summary>
/// WP-17: cache LRU của layout theo <see cref="TextLayoutKey"/>. Layout là đối tượng độc lập thiết bị (DirectWrite) nên
/// mất thiết bị D2D KHÔNG làm vô hiệu cache. Đối tượng trả cho người gọi là lease (đếm ref): loại khỏi cache một layout
/// đang được overlay giữ là an toàn (COM trả khi lease cuối được Dispose).
/// Không thread-safe ngoài khoá nội bộ; dùng trên luồng UI.
/// </summary>
internal sealed class TextLayoutCache : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<TextLayoutKey, LinkedListNode<TextLayoutEntry>> _map = new();
    private readonly LinkedList<TextLayoutEntry> _lru = new();
    private readonly int _capacity;
    private bool _disposed;

    public TextLayoutCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>Số layout đang trong cache.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>Số lần tìm thấy sẵn / phải dựng mới (cho test và đo hiệu năng).</summary>
    public long Hits { get; private set; }

    public long Misses { get; private set; }

    /// <summary>Số entry đã loại khỏi cache nhưng còn lease giữ (chưa trả COM).</summary>
    public int PendingRelease
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    private int _pending;

    /// <summary>Lấy (hoặc dựng bằng <paramref name="factory"/>) entry và tăng ref; người gọi phải <see cref="Release"/>.</summary>
    public TextLayoutEntry Acquire(TextLayoutKey key, Func<TextLayoutKey, TextLayoutEntry> factory)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_map.TryGetValue(key, out LinkedListNode<TextLayoutEntry>? node))
            {
                Hits++;
                _lru.Remove(node);
                _lru.AddFirst(node);
                node.Value.RefCount++;
                return node.Value;
            }

            Misses++;
            TextLayoutEntry entry = factory(key);
            entry.RefCount = 1;
            _map[key] = _lru.AddFirst(entry);
            while (_map.Count > _capacity)
            {
                Evict(_lru.Last!);
            }

            return entry;
        }
    }

    /// <summary>Trả một ref; entry đã bị loại và hết ref thì trả COM.</summary>
    public void Release(TextLayoutEntry entry)
    {
        lock (_gate)
        {
            if (entry.RefCount <= 0)
            {
                return;
            }

            entry.RefCount--;
            if (entry.Evicted && entry.RefCount == 0 && !entry.IsReleased)
            {
                entry.Dispose();
                _pending--;
            }
        }
    }

    /// <summary>Loại hết cache (layout đang được giữ sống tới khi lease cuối Dispose).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            while (_lru.Last is { } last)
            {
                Evict(last);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            while (_lru.Last is { } last)
            {
                Evict(last);
            }

            _disposed = true;
        }
    }

    private void Evict(LinkedListNode<TextLayoutEntry> node)
    {
        TextLayoutEntry entry = node.Value;
        _lru.Remove(node);
        _map.Remove(entry.Key);
        entry.Evicted = true;
        if (entry.RefCount == 0)
        {
            entry.Dispose();
        }
        else
        {
            _pending++;
        }
    }
}
