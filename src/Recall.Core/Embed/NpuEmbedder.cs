using Recall.Core.Config;

namespace Recall.Core.Embed;

/// <summary>
/// EmbeddingGemma 2 on the AMD NPU through the long-lived <c>windows_embed_server</c> process. Disposing the embedder ends the
/// process, which frees the model and the NPU contexts; <see cref="EmbedderHost"/> does that after a pass, on a pause and when idle.
/// </summary>
public sealed class NpuEmbedder(ModelSpec spec, ILineChannel channel) : IEmbedder, IImageEmbedder, IEmbedderHealth
{
    private readonly object _gate = new();
    private volatile bool _broken;

    public bool IsBroken => _broken;

    /// <summary>
    /// The channel can no longer be trusted: a reply timed out (its read is still pending, so the stale reply would answer the next
    /// request and shift every later vector by one text) or the server closed its output. Ending the process drops the pending read.
    /// </summary>
    private void Break()
    {
        _broken = true;
        try { channel.Dispose(); } catch (Exception e) when (e is IOException or InvalidOperationException) { /* already gone */ }
    }

    private void EnsureUsable()
    {
        if (_broken) throw new EmbeddingUnavailableException("the embedding server stopped; it starts again on the next use");
    }

    private void Send(string line)
    {
        try { channel.WriteLine(line); }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException) { Break(); throw; }
    }

    private string? Receive()
    {
        string? line;
        try { line = channel.ReadLine(); }
        catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException or ObjectDisposedException) { Break(); throw; }
        if (line == null) Break();
        return line;
    }

    public string Space => spec.Space;
    public int Dimensions => spec.Dimensions;

    public float[][] Embed(IReadOnlyList<string> texts, EmbedKind kind)
    {
        lock (_gate)
        {
            EnsureUsable();
            var result = new float[texts.Count][];
            for (var i = 0; i < texts.Count; i++)
            {
                Send(NpuProtocol.Request(kind, texts[i]));
                result[i] = NpuProtocol.ParseVector(Receive(), spec.Dimensions);
            }
            return result;
        }
    }

    public float[]? EmbedImage(string path)
    {
        if (!NpuProtocol.IsImageFormat(path)) return null;
        lock (_gate)
        {
            EnsureUsable();
            Send(NpuProtocol.ImageRequest(path));
            return NpuProtocol.ParseVector(Receive(), spec.Dimensions);
        }
    }

    /// <summary>One request for the whole page: the server decodes the pictures in parallel and runs them through the NPU in groups of three, about 20% faster per picture.</summary>
    public float[]?[] EmbedImages(IReadOnlyList<string> paths)
    {
        var vectors = new float[]?[paths.Count];
        var wanted = Enumerable.Range(0, paths.Count).Where(i => NpuProtocol.IsImageFormat(paths[i])).ToList();
        if (wanted.Count == 0) return vectors;
        lock (_gate)
        {
            EnsureUsable();
            Send(NpuProtocol.ImagesRequest(wanted.Select(i => paths[i])));
            // Every reply line is read before any is parsed, so one bad picture cannot leave the others unread on the pipe.
            var lines = wanted.Select(_ => Receive()).ToList();
            for (var k = 0; k < wanted.Count; k++)
            {
                try { vectors[wanted[k]] = NpuProtocol.ParseVector(lines[k], spec.Dimensions); }
                catch (InvalidDataException) when (lines[k] != null) { }
            }
        }
        return vectors;
    }

    public void Dispose() => channel.Dispose();
}

/// <summary>Runs <see cref="ModelSpec.KindTaconiteNpu"/> models on <see cref="EmbeddingDevice.Npu"/>. Both paths come from the config.</summary>
public sealed class TaconiteNpuProvider(string? serverExe, string? bundleDir) : IEmbedderProvider
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2), ReplyTimeout = TimeSpan.FromMinutes(2);

    public bool Supports(ModelSpec spec, EmbeddingDevice device) => spec.Kind == ModelSpec.KindTaconiteNpu && device == EmbeddingDevice.Npu;

    public IEmbedder Create(ModelSpec spec, string modelDir, EmbeddingDevice device)
    {
        if (string.IsNullOrWhiteSpace(serverExe) || !File.Exists(serverExe))
            throw new EmbeddingUnavailableException("npuServerPath in config.json does not point to windows_embed_server.exe");
        if (string.IsNullOrWhiteSpace(bundleDir) || !Directory.Exists(bundleDir))
            throw new EmbeddingUnavailableException("npuBundlePath in config.json does not point to the model bundle folder");
        var channel = ProcessLineChannel.Start(serverExe, [bundleDir], "READY", StartTimeout, ReplyTimeout);
        return new NpuEmbedder(spec, channel);
    }
}
