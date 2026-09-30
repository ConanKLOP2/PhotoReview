namespace PhotoReview.Imaging.Preload;

/// <summary>
/// Backoff for paths whose preload ended <c>Busy</c> (the RAW decoder's bounded queue refused the background decode). Without it every
/// order rebuild (navigation, shape change, calibration) retried such a path at once, costing a header parse, a thumbnail try, a log line
/// and a perf event each time while a viewer decode held the gate. Time is counted in order passes, not wall clock, so it is deterministic:
/// a path that was busy is skipped for the next <see cref="CooldownPasses"/> passes, and after <see cref="MaxRetries"/> retries (so
/// <c>MaxRetries + 1</c> busy outcomes) it is left alone until the file itself changes (its <see cref="ImageCacheKey"/> differs).
/// </summary>
internal sealed class PreloadBusyBackoff
{
    internal const int CooldownPasses = 3;
    internal const int MaxRetries = 5;

    private readonly object _gate = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private int _pass;

    private readonly record struct State(ImageCacheKey Key, int LastPass, int BusyCount);

    /// <summary>Marks the start of an order pass (the order was rebuilt).</summary>
    internal void BeginPass()
    {
        lock (_gate) _pass++;
    }

    /// <summary>Records a busy outcome; returns how many times this path (as this exact file) has now been busy.</summary>
    internal int NoteBusy(string path, ImageCacheKey key)
    {
        lock (_gate)
        {
            var count = _states.TryGetValue(path, out var state) && state.Key.Equals(key) ? state.BusyCount + 1 : 1;
            _states[path] = new State(key, _pass, count);
            return count;
        }
    }

    /// <summary>A preload of the path succeeded: its busy history no longer applies (the retry cap must not outlive a good decode).</summary>
    internal void RecordSuccess(string path)
    {
        lock (_gate) _states.Remove(path);
    }

    /// <summary>Forgets every path (folder reload / cache clear): the entries would otherwise outlive the catalog they describe.</summary>
    internal void Clear()
    {
        lock (_gate) _states.Clear();
    }

    /// <summary>True while the path is cooling down after a busy outcome, or has used up its retries for this exact file.</summary>
    internal bool ShouldSkip(string path, ImageCacheKey key)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(path, out var state)) return false;
            if (!state.Key.Equals(key)) { _states.Remove(path); return false; } // the file changed: a fresh start
            return state.BusyCount > MaxRetries || _pass - state.LastPass <= CooldownPasses;
        }
    }
}
