using Recall.Core.Config;

namespace Recall.Core.Embed;

/// <summary>
/// The one-word choice shown in the settings: where semantic search runs. Each working device has one model:
/// cpu is multilingual-e5-small, npu is EmbeddingGemma 2. "gpu" is accepted but refused until a model can run on it.
/// </summary>
public static class DeviceChoice
{
    public const string Off = "off", Cpu = "cpu", Gpu = "gpu", Npu = "npu";

    public const string CpuModel = "multilingual-e5-small", NpuModel = "embeddinggemma2-npu";

    public static string Of(RecallConfig c) => c.EmbeddingModel == null ? Off : c.EmbeddingDevice switch
    {
        EmbeddingDevice.Npu => Npu,
        EmbeddingDevice.Gpu => Gpu,
        _ => Cpu,
    };

    /// <summary>The configuration for <paramref name="value"/>, or the reason it cannot be used.</summary>
    public static string? Apply(RecallConfig c, string? value, out RecallConfig next)
    {
        next = c;
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case Off:
                next = c with { EmbeddingModel = null };
                return null;
            case Cpu:
                next = c with { EmbeddingModel = CpuModel, EmbeddingDevice = EmbeddingDevice.Cpu };
                return null;
            case Npu:
                if (string.IsNullOrWhiteSpace(c.NpuServerPath) || !File.Exists(c.NpuServerPath))
                    return "The NPU server is not set up: put the path of windows_embed_server.exe into npuServerPath in config.json";
                if (string.IsNullOrWhiteSpace(c.NpuBundlePath) || !Directory.Exists(c.NpuBundlePath))
                    return "The model bundle is not set up: put the folder of the EmbeddingGemma 2 bundle into npuBundlePath in config.json";
                next = c with { EmbeddingModel = NpuModel, EmbeddingDevice = EmbeddingDevice.Npu };
                return null;
            case Gpu:
                return "No model runs on the GPU yet. Use cpu or npu";
            default:
                return "Enter off, cpu or npu";
        }
    }
}
