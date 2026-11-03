using System.Numerics.Tensors;
using Recall.Core.Embed;
using Recall.Core.Tests.Support;

namespace Recall.Core.Tests;

public class EmbedOnnxTests
{
    private static readonly Lazy<OnnxEmbedder> Shared = new(() => new OnnxEmbedder(ModelFixture.Spec, ModelFixture.Dir, 4));

    private static float Cos(float[] a, float[] b) => TensorPrimitives.Dot(a, b);

    [SkippableFact]
    public void Vectors_are_normalized_with_384_dimensions()
    {
        ModelFixture.RequireModel();
        var v = Shared.Value.Embed(["Квитанция за свет"], EmbedKind.Passage)[0];
        Assert.Equal(384, v.Length);
        Assert.Equal(1f, TensorPrimitives.Norm(v), 3);
    }

    [SkippableFact]
    public void Russian_query_is_closer_to_matching_passage()
    {
        ModelFixture.RequireModel();
        var e = Shared.Value;
        var q = e.Embed(["как оплатить счёт за электричество"], EmbedKind.Query)[0];
        var p = e.Embed([
            "Квитанция за свет: оплата до 10 числа",
            "Рецепт борща со сметаной",
            "Electricity bill payment is due on the 10th",
            "Recipe for borscht with sour cream"], EmbedKind.Passage);
        Assert.True(Cos(q, p[0]) > Cos(q, p[1]));
        Assert.True(Cos(q, p[2]) > Cos(q, p[3]));
    }

    [SkippableFact]
    // Dynamic int8 quantization scales activations over the whole padded batch, so batch and single vectors differ slightly (measured 0.9966).
    public void Batch_equals_single()
    {
        ModelFixture.RequireModel();
        var e = Shared.Value;
        string[] texts = ["короткий текст", "a much longer passage about electricity bills and how to pay them on time every month"];
        var batch = e.Embed(texts, EmbedKind.Passage);
        for (var i = 0; i < texts.Length; i++)
            { var c = Cos(batch[i], e.Embed([texts[i]], EmbedKind.Passage)[0]); Assert.True(c > 0.99f, $"cos={c}"); }
    }

    [Fact]
    public void Missing_files_throw_unavailable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rs-nomodel-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<EmbeddingUnavailableException>(() => new OnnxEmbedder(ModelFixture.Spec, dir, 1));
    }
}
