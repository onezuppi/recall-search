using Recall.Core.Config;

namespace Recall.Core.Embed;

/// <summary>
/// EmbeddingGemma 2 on the AMD NPU through the long-lived <c>windows_embed_server</c> process. Disposing the embedder ends the
/// process, which frees the model and the NPU contexts; <see cref="EmbedderHost"/> does that after a pass, on a pause and when idle.
/// </summary>
public sealed class NpuEmbedder(ModelSpec spec, ILineChannel channel) : IEmbedder, IImageEmbedder
{
    private readonly object _gate = new();

    public string Space => spec.Space;
    public int Dimensions => spec.Dimensions;

    public float[][] Embed(IReadOnlyList<string> texts, EmbedKind kind)
    {
        lock (_gate)
        {
            var result = new float[texts.Count][];
            for (var i = 0; i < texts.Count; i++)
            {
                channel.WriteLine(NpuProtocol.Request(kind, texts[i]));
                result[i] = NpuProtocol.ParseVector(channel.ReadLine(), spec.Dimensions);
            }
            return result;
        }
    }

    public float[]? EmbedImage(string path)
    {
        if (!NpuProtocol.IsImageFormat(path)) return null;
        lock (_gate)
        {
            channel.WriteLine(NpuProtocol.ImageRequest(path));
            return NpuProtocol.ParseVector(channel.ReadLine(), spec.Dimensions);
        }
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
