using System.Numerics.Tensors;

namespace Recall.Core.Search;

/// <summary>Brute-force cosine search over the stored chunk vectors of one embedding space. Vectors are normalized, so the dot product is the cosine.</summary>
internal static class VectorSearch
{
    /// <summary>The best chunk of each file, best files first. Query filters apply as in the text search.</summary>
    public static List<SearchHit> Find(ISearchSession session, ParsedQuery q, float[] query, string space, int limit, float minSimilarity)
    {
        var best = new Dictionary<string, (float Score, VectorRow Row)>(StringComparer.OrdinalIgnoreCase);
        session.ScanVectors(q, space, query.Length, (row, vector) =>
        {
            var score = TensorPrimitives.Dot(query, vector);
            if (score < minSimilarity) return;
            if (best.TryGetValue(row.Path, out var old) && old.Score >= score) return;
            best[row.Path] = (score, row);
        });
        return best.Values.OrderByDescending(b => b.Score).Take(limit)
            .Select(b => new SearchHit(b.Row.Path, b.Row.Group, b.Row.Locator, "", b.Row.Modified, b.Row.Size, b.Score) { SegmentId = b.Row.SegmentId })
            .ToList();
    }
}
