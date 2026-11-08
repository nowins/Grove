using GitX.Core;
using Xunit;

namespace GitX.Core.Tests;

public sealed class HistoryCacheAdmissionTests
{
    [Fact]
    public void Repeated_oversized_working_set_retains_useful_hits_without_expanding_capacity()
    {
        using var cache = new HistoryReadCache<int, object>(4, 16, frequencyAdmission: true);
        var hits = 0;
        for (var round = 0; round < 20; round++)
            for (var key = 0; key < 8; key++)
                if (cache.TryGet(key, out _)) ++hits;
                else cache.Put(key, new(), 4);
        // A four-entry LRU cycling over eight distinct results has no hits.
        Assert.True(hits >= 40, $"Only {hits} hits across 160 reads");
        Assert.InRange(Enumerable.Range(0, 8).Count(key => cache.TryGet(key, out _)), 1, 4);
    }

    [Fact]
    public void Popular_results_survive_a_one_off_scan_and_new_locality_can_replace_them()
    {
        using var cache = new HistoryReadCache<int, object>(4, 16, frequencyAdmission: true);
        void Read(int key) { if (!cache.TryGet(key, out _)) cache.Put(key, new(), 4); }
        for (var round = 0; round < 40; round++) for (var key = 0; key < 8; key++) Read(key);
        var hot = Enumerable.Range(0, 8).Where(key => cache.TryGet(key, out _)).ToArray();
        Assert.NotEmpty(hot);
        for (var key = 100; key < 116; key++) Read(key);
        Assert.Contains(hot, key => cache.TryGet(key, out _));
        // Navigation can move completely away from the old set. Old scores
        // must age out even after counters have saturated on previous hits.
        for (var round = 0; round < 64; round++) for (var key = 200; key < 204; key++) Read(key);
        Assert.All(Enumerable.Range(200, 4), key => Assert.True(cache.TryGet(key, out _)));
        Assert.All(hot, key => Assert.False(cache.TryGet(key, out _)));
    }

    [Fact]
    public void A_large_candidate_cannot_evict_a_hot_victim_hidden_behind_a_cold_one()
    {
        using var cache = new HistoryReadCache<int, object>(4, 12, frequencyAdmission: true);
        void Read(int key, long bytes) { if (!cache.TryGet(key, out _)) cache.Put(key, new(), bytes); }
        Read(1, 4); Read(2, 4); Read(3, 4); Read(4, 4); // activate admission
        for (var i = 0; i < 6; i++) Read(1, 4);
        Read(3, 4); // a cold entry is least recent, with the hot entry behind it
        Read(9, 10); Read(9, 10);
        Assert.True(cache.TryGet(1, out _)); Assert.True(cache.TryGet(2, out _)); Assert.False(cache.TryGet(9, out _));
        Read(10, 13); Assert.False(cache.TryGet(10, out _));
        Assert.InRange(Enumerable.Range(1, 10).Count(key => cache.TryGet(key, out _)), 1, 3);
    }

    [Fact]
    public async Task Colliding_estimates_concurrency_and_disposal_never_change_key_identity_or_bounds()
    {
        using var cache = new HistoryReadCache<CollisionKey, int>(4, 16, frequencyAdmission: true);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                var key = new CollisionKey((worker + i) % 16);
                if (cache.TryGet(key, out var value)) Assert.Equal(key.Value, value);
                else cache.Put(key, key.Value, 4);
            }
        })));
        Assert.InRange(Enumerable.Range(0, 16).Count(i => cache.TryGet(new(i), out _)), 1, 4);
        cache.Dispose(); cache.Put(new(99), 99, 4);
        Assert.False(cache.TryGet(new(99), out _));
    }

    private sealed record CollisionKey(int Value)
    {
        public override int GetHashCode() => 1;
    }
}
