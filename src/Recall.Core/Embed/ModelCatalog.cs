namespace Recall.Core.Embed;

public sealed record ModelFile(string RemotePath, string Sha256, long Size)
{
    public string FileName => System.IO.Path.GetFileName(RemotePath);
}

/// <param name="MinSimilarity">Cosine similarity under which a vector hit is noise. Each model has its own score range.</param>
/// <param name="Kind">Selects the <see cref="IEmbedderProvider"/> that can run the model.</param>
/// <param name="Files">Empty for a local model. Otherwise index 0 is the ONNX model, index 1 is the SentencePiece tokenizer model.</param>
public sealed record ModelSpec(string Id, string Repo, string Revision, int Dimensions, int MaxTokens, IReadOnlyList<ModelFile> Files, float MinSimilarity = 0f, string Kind = ModelSpec.KindE5Onnx)
{
    public const string KindE5Onnx = "e5-onnx", KindTaconiteNpu = "taconite-npu";

    /// <summary>Vectors of different models or revisions are not comparable, so they are stored under this id.</summary>
    public string Space => $"{Id}@{Revision}";

    public ModelFile Onnx => Files[0];
    public ModelFile Tokenizer => Files[1];
}

/// <summary>Models that can be downloaded from Hugging Face, pinned to an exact revision and file hashes.</summary>
public static class ModelCatalog
{
    public static IReadOnlyList<ModelSpec> All { get; } =
    [
        new("multilingual-e5-small", "intfloat/multilingual-e5-small", "614241f622f53c4eeff9890bdc4f31cfecc418b3", 384, 512,
        [
            new("onnx/model_qint8_avx512_vnni.onnx", "dd476dd0c2514e9b9be83aeb3853fac0763e0bdf4a71645407587d77c48a2d88", 118346824),
            new("onnx/sentencepiece.bpe.model", "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865", 5069051),
        ], MinSimilarity: 0.84f),
        // A local bundle, not downloaded. Runs only on the NPU through the provider that the config points at.
        // Floor 0.65 separates nonsense queries (up to 0.62) from the right file (from 0.68) on 18 queries; see docs/benchmarks/2026-11-gemma-vs-e5.md.
        new("embeddinggemma2-npu", "", "taconite-0.2.0", 768, 2048, [], MinSimilarity: 0.65f, Kind: ModelSpec.KindTaconiteNpu),
    ];

    public static ModelSpec? Find(string id) => All.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
