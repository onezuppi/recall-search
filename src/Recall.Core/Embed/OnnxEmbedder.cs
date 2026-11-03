using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Recall.Core.Embed;

/// <summary>E5 model on the ONNX Runtime CPU provider: prefix, tokenize, run, mean pool over the mask, L2 normalize.</summary>
public sealed class OnnxEmbedder : IEmbedder
{
    private readonly ModelSpec _spec;
    private readonly E5Tokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly bool _needsTokenTypes;

    public OnnxEmbedder(ModelSpec spec, string modelDir, int threads)
    {
        _spec = spec;
        var onnx = Path.Combine(modelDir, spec.Onnx.FileName);
        var spm = Path.Combine(modelDir, spec.Tokenizer.FileName);
        if (!File.Exists(onnx) || !File.Exists(spm))
            throw new EmbeddingUnavailableException($"model {spec.Id} is not downloaded to {modelDir}");
        _tokenizer = new E5Tokenizer(spm, spec.MaxTokens);
        using var options = new SessionOptions
        {
            IntraOpNumThreads = Math.Max(1, threads),
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(onnx, options);
        _needsTokenTypes = _session.InputMetadata.ContainsKey("token_type_ids");
    }

    public string Space => _spec.Space;
    public int Dimensions => _spec.Dimensions;

    public float[][] Embed(IReadOnlyList<string> texts, EmbedKind kind)
    {
        if (texts.Count == 0) return [];
        var prefix = kind == EmbedKind.Query ? "query: " : "passage: ";
        var ids = texts.Select(t => _tokenizer.Encode(prefix + t)).ToArray();
        var width = ids.Max(i => i.Length);
        var shape = new[] { ids.Length, width };
        var inputIds = new DenseTensor<long>(shape);
        var mask = new DenseTensor<long>(shape);
        for (var b = 0; b < ids.Length; b++)
            for (var t = 0; t < width; t++)
            {
                var real = t < ids[b].Length;
                inputIds[b, t] = real ? ids[b][t] : E5Tokenizer.Pad;
                mask[b, t] = real ? 1 : 0;
            }
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };
        if (_needsTokenTypes) inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(shape)));
        using var results = _session.Run(inputs);
        return Pool(results.First().AsTensor<float>(), ids);
    }

    private float[][] Pool(Tensor<float> hidden, int[][] ids)
    {
        var dims = _spec.Dimensions;
        var output = new float[ids.Length][];
        for (var b = 0; b < ids.Length; b++)
        {
            var v = new float[dims];
            for (var t = 0; t < ids[b].Length; t++)
                for (var d = 0; d < dims; d++) v[d] += hidden[b, t, d];
            var norm = MathF.Sqrt(v.Sum(x => x * x));
            for (var d = 0; d < dims; d++) v[d] /= norm > 0 ? norm : 1; // mean pooling is a constant factor away from the sum, so normalizing the sum is enough
            output[b] = v;
        }
        return output;
    }

    public void Dispose() => _session.Dispose();
}
