using System.Collections.Concurrent;

namespace PcTidy.Safety.Paths;

/// <summary>
/// Thread-safe, size-capped cache with a time limit per entry, keyed by path (case-insensitive).
/// Two generations approximate LRU at O(1) cost: new entries go to the young generation; when it fills up it becomes
/// the old one and the previous old generation is dropped. A hit in the old generation is copied back to the young one,
/// so entries in use survive. Holds at most about twice <c>generationSize</c> entries.
/// </summary>
internal sealed class BoundedCache<TValue>
{
    private readonly int _generationSize;
    private readonly long _lifetimeMs;
    private readonly object _swapLock = new();
    private Generation _young = new();
    private Generation _old = new();

    private readonly record struct Entry(long StoredMs, TValue Value);

    /// <summary>One generation. <see cref="Added"/> is kept by hand: ConcurrentDictionary.Count takes every lock.</summary>
    private sealed class Generation
    {
        public readonly ConcurrentDictionary<string, Entry> Map = new(StringComparer.OrdinalIgnoreCase);
        public int Added;
    }

    public BoundedCache(int generationSize, TimeSpan lifetime)
    {
        if (generationSize < 1) throw new ArgumentOutOfRangeException(nameof(generationSize));
        _generationSize = generationSize;
        _lifetimeMs = (long)lifetime.TotalMilliseconds;
    }

    /// <summary>Entries currently held (approximate under concurrency; for tests and diagnostics).</summary>
    public int Count => Volatile.Read(ref _young).Map.Count + Volatile.Read(ref _old).Map.Count;

    public bool TryGet(string key, out TValue value)
    {
        var now = Environment.TickCount64;
        if (Volatile.Read(ref _young).Map.TryGetValue(key, out var e) && now - e.StoredMs < _lifetimeMs)
        {
            value = e.Value;
            return true;
        }
        if (Volatile.Read(ref _old).Map.TryGetValue(key, out e) && now - e.StoredMs < _lifetimeMs)
        {
            Store(key, e); // still in use: keep it through the next swap
            value = e.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Set(string key, TValue value) => Store(key, new Entry(Environment.TickCount64, value));

    public TValue GetOrAdd(string key, Func<string, TValue> create)
    {
        if (TryGet(key, out var hit)) return hit;
        var value = create(key);
        Set(key, value);
        return value;
    }

    public void Clear()
    {
        lock (_swapLock)
        {
            Volatile.Write(ref _old, new Generation());
            Volatile.Write(ref _young, new Generation());
        }
    }

    private void Store(string key, Entry entry)
    {
        var young = Volatile.Read(ref _young);
        if (!young.Map.TryAdd(key, entry)) { young.Map[key] = entry; return; }
        if (Interlocked.Increment(ref young.Added) < _generationSize) return;
        lock (_swapLock)
        {
            if (!ReferenceEquals(young, _young)) return; // another thread already swapped
            Volatile.Write(ref _old, young);
            Volatile.Write(ref _young, new Generation());
        }
    }
}
