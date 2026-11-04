using Recall.Core.Search;

namespace Recall.Core.Tests;

public class VectorCacheTests
{
    private const string Space = "m@1";

    private sealed class Source(int count, int dimensions)
    {
        public int Streams;

        public void Stream(VectorVisitor visit)
        {
            Streams++;
            for (var i = 0; i < count; i++)
            {
                var v = new float[dimensions];
                v[i % dimensions] = 1;
                visit(100 + i, v);
            }
        }
    }

    private static List<long> Collect(VectorCache cache, Source source, int dimensions)
    {
        var ids = new List<long>();
        cache.Scan(Space, dimensions, source.Stream, (id, _) => ids.Add(id));
        return ids;
    }

    [Fact]
    public void Second_scan_is_served_from_memory()
    {
        var cache = new VectorCache();
        var source = new Source(5, 4);
        var first = Collect(cache, source, 4);
        var second = Collect(cache, source, 4);
        Assert.Equal(first, second);
        Assert.Equal(5, second.Count);
        Assert.Equal(1, source.Streams);
        Assert.True(cache.IsLoaded);
    }

    [Fact]
    public void Memory_copy_holds_the_same_vectors_as_the_stream()
    {
        var cache = new VectorCache();
        var source = new Source(6, 3);
        var streamed = new List<(long, float[])>();
        cache.Scan(Space, 3, source.Stream, (id, v) => streamed.Add((id, v.ToArray())));
        var cached = new List<(long, float[])>();
        cache.Scan(Space, 3, source.Stream, (id, v) => cached.Add((id, v.ToArray())));
        Assert.Equal(streamed.Select(s => s.Item1), cached.Select(s => s.Item1));
        for (var i = 0; i < streamed.Count; i++) Assert.Equal(streamed[i].Item2, cached[i].Item2);
    }

    [Fact]
    public void A_change_to_the_vectors_makes_the_next_scan_read_them_again()
    {
        var cache = new VectorCache();
        var source = new Source(3, 4);
        Collect(cache, source, 4);
        VectorVersion.Bump();
        Collect(cache, source, 4);
        Assert.Equal(2, source.Streams);
    }

    [Fact]
    public void Another_space_or_size_is_not_taken_from_the_copy()
    {
        var cache = new VectorCache();
        var source = new Source(3, 4);
        Collect(cache, source, 4);
        cache.Scan("other@1", 4, source.Stream, (_, _) => { });
        Assert.Equal(2, source.Streams);
    }

    [Fact]
    public void Index_too_big_for_the_limit_is_streamed_every_time_and_still_complete()
    {
        var cache = new VectorCache(maxBytes: 4 * sizeof(float) * 2); // room for two vectors of four numbers
        var source = new Source(5, 4);
        Assert.Equal(5, Collect(cache, source, 4).Count);
        Assert.Equal(5, Collect(cache, source, 4).Count);
        Assert.Equal(2, source.Streams);
        Assert.False(cache.IsLoaded);
    }

    [Fact]
    public async Task Copy_is_dropped_after_a_quiet_period()
    {
        var cache = new VectorCache(idle: TimeSpan.FromMilliseconds(100));
        Collect(cache, new Source(2, 4), 4);
        for (var i = 0; i < 60 && cache.IsLoaded; i++) await Task.Delay(100);
        Assert.False(cache.IsLoaded);
    }
}
