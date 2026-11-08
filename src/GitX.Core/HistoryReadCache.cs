namespace GitX.Core;

// Completed immutable reads only. The owner supplies a conservative retained-byte
// estimate including the key; no repository, native handle or live task is stored.
internal sealed class HistoryReadCache<TKey, TValue>(int maxEntries, long maxBytes, bool frequencyAdmission = false) : IDisposable where TKey : notnull
{
    private sealed record Entry(TKey Key, TValue Value, long Bytes);
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _index = [];
    private readonly LinkedList<Entry> _lru = new();
    private RecentFrequency? _frequency;
    private long _bytes;
    private bool _closed;

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (!_closed) _frequency?.Observe(key.GetHashCode());
            if (!_closed && _index.TryGetValue(key, out var node))
            {
                _lru.Remove(node); _lru.AddFirst(node); value = node.Value.Value; return true;
            }
            value = default!; return false;
        }
    }

    public void Put(TKey key, TValue value, long bytes)
    {
        if (bytes < 0 || bytes > maxBytes) return;
        lock (_gate)
        {
            if (_closed) return;
            // A scan larger than the cache must not evict every reusable result
            // on each pass. Admission considers every victim of a large entry;
            // ties keep the existing value. Estimates affect admission only,
            // never key equality or the data returned to a reader.
            if (frequencyAdmission && !_index.ContainsKey(key) && (_index.Count >= maxEntries || _bytes + bytes > maxBytes))
            {
                // Allocate the sketch only when capacity is first reached.
                // Cold readers and small working sets pay no sketch allocation.
                if (_frequency is null)
                {
                    _frequency = new(Math.Max(64, maxEntries * 4));
                    foreach (var entry in _lru) _frequency.Observe(entry.Key.GetHashCode());
                    _frequency.Observe(key.GetHashCode());
                }
                var count = _index.Count; var retained = _bytes;
                var frequency = _frequency.Estimate(key.GetHashCode());
                for (var victim = _lru.Last; victim is not null && (count >= maxEntries || retained + bytes > maxBytes); victim = victim.Previous)
                {
                    if (_frequency.Estimate(victim.Value.Key.GetHashCode()) >= frequency) return;
                    --count; retained -= victim.Value.Bytes;
                }
            }
            if (_index.Remove(key, out var previous)) { _bytes -= previous.Value.Bytes; _lru.Remove(previous); }
            _index.Add(key, _lru.AddFirst(new Entry(key, value, bytes))); _bytes += bytes;
            while (_index.Count > maxEntries || _bytes > maxBytes)
            {
                var last = _lru.Last!;
                _index.Remove(last.Value.Key); _bytes -= last.Value.Bytes; _lru.RemoveLast();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate) { _closed = true; _index.Clear(); _lru.Clear(); _frequency = null; _bytes = 0; }
    }

    // Four rows of byte counters, capped at 15. Fixed 1 KiB; no keys or paths.
    // Aging counts every request (including saturated hits), so a previously
    // popular working set cannot retain its score forever after navigation.
    private sealed class RecentFrequency(int period)
    {
        private readonly byte[] _counts = new byte[4 * 256];
        private int _requests;

        public void Observe(int hash)
        {
            if (++_requests >= period)
            {
                for (var i = 0; i < _counts.Length; i++) _counts[i] >>= 1;
                _requests = 0;
            }
            var bits = Spread(hash);
            for (var row = 0; row < 4; row++, bits >>= 8)
            {
                var index = row * 256 + (int)(bits & 255);
                if (_counts[index] < 15) ++_counts[index];
            }
        }

        public int Estimate(int hash)
        {
            var bits = Spread(hash); var result = 15;
            for (var row = 0; row < 4; row++, bits >>= 8)
                result = Math.Min(result, _counts[row * 256 + (int)(bits & 255)]);
            return result;
        }

        private static uint Spread(int hash)
        {
            var bits = unchecked((uint)hash * 0x9e3779b9u);
            return bits ^ (bits >> 16);
        }
    }
}
