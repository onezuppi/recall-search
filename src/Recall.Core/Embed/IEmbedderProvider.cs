using Recall.Core.Config;

namespace Recall.Core.Embed;

/// <summary>
/// Builds the embedder of a catalog model on a device. A new model family or device (EmbeddingGemma 2 on the NPU, DirectML)
/// is added by one provider and one <see cref="ModelCatalog"/> entry; nothing else in the pipeline changes.
/// </summary>
public interface IEmbedderProvider
{
    bool Supports(ModelSpec spec, EmbeddingDevice device);

    IEmbedder Create(ModelSpec spec, string modelDir, EmbeddingDevice device);
}

/// <summary>E5 models (<see cref="ModelSpec.KindE5Onnx"/>) on the ONNX Runtime CPU provider.</summary>
public sealed class OnnxE5Provider : IEmbedderProvider
{
    public bool Supports(ModelSpec spec, EmbeddingDevice device) => spec.Kind == ModelSpec.KindE5Onnx && device == EmbeddingDevice.Cpu;

    public IEmbedder Create(ModelSpec spec, string modelDir, EmbeddingDevice device) =>
        new OnnxEmbedder(spec, modelDir, Math.Max(1, Environment.ProcessorCount / 2));
}
