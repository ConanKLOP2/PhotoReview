namespace PhotoReview.Core.Caching;

/// <summary>Thread-safe, byte-bounded LRU cache.</summary>
public sealed class BoundedLruCache<TKey, TValue> where TKey : notnull
{
    private sealed record Entry(TKey Key, TValue Value, long Size);
    private readonly long _capacity;
    private readonly Func<TValue, long> _sizeOf;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _items;
    private readonly LinkedList<Entry> _lru = [];
    private readonly object _gate = new();
    private long _size;

    public BoundedLruCache(long capacity, Func<TValue, long> sizeOf, IEqualityComparer<TKey>? comparer = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _sizeOf = sizeOf ?? throw new ArgumentNullException(nameof(sizeOf));
        _items = new Dictionary<TKey, LinkedListNode<Entry>>(comparer);
    }

    public int Count { get { lock (_gate) return _items.Count; } }
    public long CurrentSize { get { lock (_gate) return _size; } }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        var size = Math.Max(1, _sizeOf(value));
        if (size > _capacity) return;
        lock (_gate)
        {
            RemoveCore(key);
            while (_size + size > _capacity && _lru.Last is not null)
            {
                var oldest = _lru.Last!;
                _lru.RemoveLast();
                _items.Remove(oldest.Value.Key);
                _size -= oldest.Value.Size;
            }
            var node = _lru.AddFirst(new Entry(key, value, size));
            _items[key] = node;
            _size += size;
        }
    }

    public bool Remove(TKey key) { lock (_gate) return RemoveCore(key); }
    public int RemoveWhere(Func<TKey, bool> predicate)
    {
        lock (_gate)
        {
            var keys = _items.Keys.Where(predicate).ToArray();
            foreach (var key in keys) RemoveCore(key);
            return keys.Length;
        }
    }
    public void Clear() { lock (_gate) { _items.Clear(); _lru.Clear(); _size = 0; } }

    private bool RemoveCore(TKey key)
    {
        if (!_items.Remove(key, out var node)) return false;
        _lru.Remove(node);
        _size -= node.Value.Size;
        return true;
    }
}
