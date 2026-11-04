using System.Numerics.Tensors;

namespace Recall.Core.Search;

/// <summary>One embedding space to search and the cosine similarity under which its hits are dropped.</summary>
internal readonly record struct VectorSpace(string Space, float MinSimilarity);

/// <summary>
/// Brute-force cosine search over the stored chunk vectors of the given embedding spaces. Vectors are normalized, so the dot product is the cosine.
/// The scan reads only the vector table. Files are looked up afterwards for the best segments alone.
/// </summary>
internal static class VectorSearch
{
    /// <summary>Best segments kept per wanted result, so that files dropped by the filters or sharing a segment do not leave the list short.</summary>
    private const int PoolFactor = 10;

    /// <summary>The best chunk of each file, best files first. Query filters apply as in the text search.</summary>
    public static List<SearchHit> Find(ISearchSession session, ParsedQuery q, float[] query, IReadOnlyList<VectorSpace> spaces, int limit)
    {
        var allowed = session.FilteredSegments(q);
        var best = new Dictionary<long, float>();
        foreach (var (space, minSimilarity) in spaces)
        {
            session.ScanVectors(space, query.Length, (segmentId, vector) =>
            {
                if (allowed != null && !allowed.Contains(segmentId)) return;
                var score = TensorPrimitives.Dot(query, vector);
                if (score < minSimilarity) return;
                if (!best.TryGetValue(segmentId, out var old) || score > old) best[segmentId] = score;
            });
        }
        var pool = best.OrderByDescending(b => b.Value).Take(Math.Max(limit, 1) * PoolFactor).Select(b => b.Key).ToList();

        var byPath = new Dictionary<string, (float Score, VectorRow Row)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in session.VectorRows(q, pool))
        {
            var score = best[row.SegmentId];
            if (!byPath.TryGetValue(row.Path, out var old) || score > old.Score) byPath[row.Path] = (score, row);
        }
        return byPath.Values.OrderByDescending(b => b.Score).Take(limit)
            .Select(b => new SearchHit(b.Row.Path, b.Row.Group, b.Row.Locator, "", b.Row.Modified, b.Row.Size, b.Score) { SegmentId = b.Row.SegmentId })
            .ToList();
    }
}
