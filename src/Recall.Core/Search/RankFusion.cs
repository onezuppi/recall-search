namespace Recall.Core.Search;

/// <summary>Weighted reciprocal rank fusion of text hits and vector hits, matched by file path.</summary>
public static class RankFusion
{
    public const double TextWeight = 2.0;
    public const double VectorWeight = 1.0;
    public const int K = 60;

    /// <summary>
    /// Score of a path is the sum over the lists it appears in of weight / (K + rank). The text weight is higher, so an exact
    /// text hit stays ahead of a purely semantic one. Equal scores keep text order first.
    /// </summary>
    public static IReadOnlyList<(string Key, double Score)> Fuse(IReadOnlyList<string> text, IReadOnlyList<string> vector)
    {
        var scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        void Add(IReadOnlyList<string> list, double weight)
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                if (!scores.ContainsKey(list[rank])) { scores[list[rank]] = 0; order.Add(list[rank]); }
                scores[list[rank]] += weight / (K + rank + 1);
            }
        }
        Add(text, TextWeight);
        Add(vector, VectorWeight);
        return order.Select((k, i) => (Key: k, Score: scores[k], I: i))
            .OrderByDescending(x => x.Score).ThenBy(x => x.I)
            .Select(x => (x.Key, x.Score)).ToList();
    }

    /// <summary>Merges two hit lists with <see cref="Fuse"/>; a hit found by both keeps its text version. Score becomes the fused score.</summary>
    public static List<SearchHit> Merge(IReadOnlyList<SearchHit> text, IReadOnlyList<SearchHit> vector)
    {
        var byPath = new Dictionary<string, SearchHit>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in vector) byPath[h.Path] = h;
        foreach (var h in text) byPath[h.Path] = h;
        return Fuse(text.Select(h => h.Path).ToList(), vector.Select(h => h.Path).ToList())
            .Select(f => byPath[f.Key] with { Score = f.Score }).ToList();
    }
}
