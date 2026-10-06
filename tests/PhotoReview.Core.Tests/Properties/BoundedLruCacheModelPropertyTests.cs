using PhotoReview.Core.Caching;

namespace PhotoReview.Core.Tests.Properties;

/// <summary>
/// Model-based property: random Set/TryGet/Remove/RemoveWhere/Clear sequences against a deliberately naive list model
/// (most-recent first). Size bound, eviction order and "no live entry lost" must hold after every single step.
/// Seeds: see <see cref="PropertyRunner"/> (fixed in CI, <c>PHOTOREVIEW_PROP_SEED</c> to replay or randomise).
/// </summary>
[Trait("Category", "HotPath")]
public sealed class BoundedLruCacheModelPropertyTests
{
    private sealed class Model(long capacity)
    {
        public readonly List<(int Key, int Value, long Size)> Items = []; // index 0 = most recently used

        public long Size => Items.Sum(i => i.Size);

        public void Set(int key, int value, long size)
        {
            size = Math.Max(1, size);
            Items.RemoveAll(i => i.Key == key);
            if (size > capacity) return;
            while (Size + size > capacity && Items.Count > 0) Items.RemoveAt(Items.Count - 1);
            Items.Insert(0, (key, value, size));
        }

        public bool TryGet(int key, out int value)
        {
            var index = Items.FindIndex(i => i.Key == key);
            if (index < 0) { value = 0; return false; }
            var item = Items[index];
            Items.RemoveAt(index);
            Items.Insert(0, item);
            value = item.Value;
            return true;
        }
    }

    [Fact(DisplayName = "Random operation sequences keep BoundedLruCache identical to a naive LRU model (size bound, eviction order, no lost entries)")]
    public void RandomOperations_MatchNaiveModel()
    {
        PropertyRunner.Check("BoundedLruCache model", iterations: 100, (rng, _) =>
        {
            var capacity = rng.Next(1, 60);
            var cache = new BoundedLruCache<int, (int Id, long Size)>(capacity, v => v.Size);
            var model = new Model(capacity);
            var nextValue = 0;

            for (var step = 0; step < 120; step++)
            {
                var key = rng.Next(0, 12);
                switch (rng.Next(10))
                {
                    case <= 3:
                        {
                            var size = rng.Next(5) == 0 ? rng.Next(-3, capacity * 2) : rng.Next(1, Math.Max(2, capacity / 3));
                            var value = ++nextValue;
                            cache.Set(key, (value, size));
                            model.Set(key, value, size);
                            // Just stored (when it fits) => it is the most recent live entry and must be there.
                            if (Math.Max(1, size) <= capacity) Assert.True(cache.TryGet(key, out var got) && got.Id == value, "fresh entry lost");
                            if (Math.Max(1, size) <= capacity) model.TryGet(key, out _);
                            break;
                        }
                    case <= 6:
                        {
                            var hit = cache.TryGet(key, out var got);
                            var modelHit = model.TryGet(key, out var expected);
                            Assert.Equal(modelHit, hit);
                            if (hit) Assert.Equal(expected, got.Id);
                            break;
                        }
                    case 7:
                        Assert.Equal(model.Items.RemoveAll(i => i.Key == key) > 0, cache.Remove(key));
                        break;
                    case 8:
                        {
                            var parity = rng.Next(2);
                            var removed = cache.RemoveWhere(k => k % 2 == parity);
                            Assert.Equal(model.Items.RemoveAll(i => i.Key % 2 == parity), removed);
                            break;
                        }
                    default:
                        if (rng.Next(8) == 0) { cache.Clear(); model.Items.Clear(); }
                        break;
                }

                Assert.True(cache.CurrentSize <= capacity, $"size {cache.CurrentSize} exceeds capacity {capacity}");
                Assert.Equal(model.Size, cache.CurrentSize);
                Assert.Equal(model.Items.Count, cache.Count);
            }

            // Final: exactly the model's keys are live, and probing in LRU order (oldest first) keeps the order consistent.
            foreach (var item in model.Items.AsEnumerable().Reverse().ToArray())
                Assert.True(cache.TryGet(item.Key, out var got) && got.Id == item.Value, $"live key {item.Key} lost");
        });
    }
}
