using Recall.Core.Config;
using Recall.Core.Embed;
using Recall.Core.Tests.Support;

namespace Recall.Core.Tests;

public class EmbedHostTests
{
    private static (EmbedderHost Host, Func<int> Loads) Make(TimeSpan delay)
    {
        var loads = 0;
        var host = new EmbedderHost("fake@1", () => { Interlocked.Increment(ref loads); return new FakeEmbedder(); }, delay);
        return (host, () => loads);
    }

    [Fact]
    public async Task Loads_once_for_concurrent_acquires()
    {
        var (host, loads) = Make(TimeSpan.FromMinutes(1));
        var leases = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(host.Acquire)));
        Assert.Equal(1, loads());
        Assert.All(leases, l => l.Dispose());
    }

    [Fact]
    public void Unloads_after_last_lease_with_zero_delay()
    {
        var (host, loads) = Make(TimeSpan.Zero);
        var a = host.Acquire();
        var b = host.Acquire();
        a.Dispose();
        Assert.True(host.IsLoaded);
        b.Dispose();
        b.Dispose(); // a second dispose must not release another lease
        Assert.False(host.IsLoaded);
        host.Acquire().Dispose();
        Assert.Equal(2, loads());
    }

    [Fact]
    public async Task Stays_loaded_during_delay_and_reacquire_cancels_unload()
    {
        var (host, loads) = Make(TimeSpan.FromMilliseconds(200));
        host.Acquire().Dispose();
        Assert.True(host.IsLoaded);
        var again = host.Acquire();
        await Task.Delay(400);
        Assert.True(host.IsLoaded);
        Assert.Equal(1, loads());
        again.Dispose();
        await Task.Delay(500);
        Assert.False(host.IsLoaded);
    }

    private static RecallConfig Config() => new([], new HashSet<string>(), []);

    [Fact]
    public void Create_returns_null_when_off() => Assert.Null(EmbedderHost.Create(Config(), "unused"));

    [Fact]
    public void Create_throws_not_supported_for_gpu()
    {
        var cfg = Config() with { EmbeddingModel = "multilingual-e5-small", EmbeddingDevice = EmbeddingDevice.Gpu };
        Assert.Throws<NotSupportedException>(() => EmbedderHost.Create(cfg, "unused")!.Acquire());
    }

    [Fact]
    public void Unknown_model_is_unavailable()
    {
        var cfg = Config() with { EmbeddingModel = "nope" };
        Assert.Throws<EmbeddingUnavailableException>(() => EmbedderHost.Create(cfg, "unused")!.Acquire());
    }

    private sealed class FakeProvider(FakeEmbedder embedder) : IEmbedderProvider
    {
        public bool Supports(ModelSpec spec, EmbeddingDevice device) => device == EmbeddingDevice.Npu;
        public IEmbedder Create(ModelSpec spec, string modelDir, EmbeddingDevice device) => embedder;
    }

    [Fact]
    public void A_new_provider_serves_a_device_without_other_changes()
    {
        var fake = new FakeEmbedder();
        var cfg = Config() with { EmbeddingModel = "multilingual-e5-small", EmbeddingDevice = EmbeddingDevice.Npu };
        using var lease = EmbedderHost.Create(cfg, "unused", providers: [new FakeProvider(fake)])!.Acquire();
        Assert.Same(fake, lease.Embedder);
    }

    [Fact]
    public void Release_drops_the_model_after_the_running_lease_ends()
    {
        var (host, _) = Make(TimeSpan.FromMinutes(5));
        var lease = host.Acquire();
        host.Release();
        Assert.True(host.IsLoaded);
        lease.Dispose();
        Assert.False(host.IsLoaded);
    }
}
