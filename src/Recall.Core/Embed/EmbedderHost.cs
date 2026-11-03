using Recall.Core.Config;

namespace Recall.Core.Embed;

/// <summary>
/// Loads the embedding model on first use and unloads it <paramref name="unloadDelay"/> after the last lease ends, so the
/// model is in memory only while indexing or answering a query. <see cref="Space"/> is known without loading.
/// </summary>
public sealed class EmbedderHost(string space, Func<IEmbedder> factory, TimeSpan unloadDelay, float minSimilarity = 0f) : IDisposable
{
    private readonly object _gate = new();
    private IEmbedder? _embedder;
    private int _leases;
    private CancellationTokenSource? _pendingUnload;

    public string Space => space;

    /// <summary>Vector hits scoring under this cosine similarity are not shown.</summary>
    public float MinSimilarity => minSimilarity;

    public bool IsLoaded { get { lock (_gate) return _embedder != null; } }

    public sealed class Lease : IDisposable
    {
        private readonly EmbedderHost _host;
        private int _disposed;

        internal Lease(EmbedderHost host, IEmbedder embedder)
        {
            _host = host;
            Embedder = embedder;
        }

        public IEmbedder Embedder { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _host.Release();
        }
    }

    /// <summary>The configured host, or null when embeddings are off. A device other than CPU fails when the model is first needed.</summary>
    public static EmbedderHost? Create(RecallConfig config, string modelsRoot, TimeSpan? unloadDelay = null)
    {
        if (config.EmbeddingModel == null) return null;
        var spec = ModelCatalog.Find(config.EmbeddingModel);
        var space = spec?.Space ?? config.EmbeddingModel;
        return new EmbedderHost(space, () =>
        {
            if (spec == null) throw new EmbeddingUnavailableException($"unknown embedding model '{config.EmbeddingModel}'");
            if (config.EmbeddingDevice != EmbeddingDevice.Cpu)
                throw new NotSupportedException($"embedding device {config.EmbeddingDevice} is not supported yet");
            return new OnnxEmbedder(spec, ModelDownloader.DirectoryOf(modelsRoot, spec), Math.Max(1, Environment.ProcessorCount / 2));
        }, unloadDelay ?? TimeSpan.FromSeconds(60), spec?.MinSimilarity ?? 0f);
    }

    public Lease Acquire()
    {
        lock (_gate)
        {
            _pendingUnload?.Cancel();
            _pendingUnload = null;
            _embedder ??= factory();
            _leases++;
            return new Lease(this, _embedder);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_leases > 0) return;
            if (unloadDelay <= TimeSpan.Zero) { Unload(); return; }
            var cts = _pendingUnload = new CancellationTokenSource();
            _ = Task.Delay(unloadDelay, cts.Token).ContinueWith(t =>
            {
                lock (_gate)
                {
                    if (!t.IsCanceled && ReferenceEquals(_pendingUnload, cts) && _leases == 0) Unload();
                }
            }, TaskScheduler.Default);
        }
    }

    private void Unload()
    {
        _embedder?.Dispose();
        _embedder = null;
        _pendingUnload = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _pendingUnload?.Cancel();
            Unload();
        }
    }
}
