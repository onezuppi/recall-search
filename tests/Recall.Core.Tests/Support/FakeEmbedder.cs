using Recall.Core.Embed;

namespace Recall.Core.Tests.Support;

/// <summary>Hashing bag-of-words vectors: equal words give equal directions. <see cref="Aliases"/> maps a word to another one before hashing.</summary>
public sealed class FakeEmbedder(string space = "fake@1") : IEmbedder
{
    public const int Dim = 32;
    public int Calls, Texts;
    public Action? OnEmbed;
    public Dictionary<string, string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Space { get; } = space;
    public int Dimensions => Dim;

    public float[][] Embed(IReadOnlyList<string> texts, EmbedKind kind)
    {
        Interlocked.Increment(ref Calls);
        Interlocked.Add(ref Texts, texts.Count);
        OnEmbed?.Invoke();
        return texts.Select(Vector).ToArray();
    }

    private float[] Vector(string text)
    {
        var v = new float[Dim];
        foreach (var raw in text.Split([' ', ',', '.', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var w = Aliases.GetValueOrDefault(raw, raw).ToLowerInvariant();
            v[(int)((uint)StableHash(w) % Dim)] += 1;
        }
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0) for (var i = 0; i < Dim; i++) v[i] /= norm;
        return v;
    }

    private static int StableHash(string s) => s.Aggregate(17, (h, c) => unchecked(h * 31 + c));

    public void Dispose() { }
}
