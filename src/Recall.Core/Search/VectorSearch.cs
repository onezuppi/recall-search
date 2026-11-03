using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Recall.Core.Search;

/// <summary>Brute-force cosine search over the stored chunk vectors of one embedding space. Vectors are normalized, so the dot product is the cosine.</summary>
internal static class VectorSearch
{
    /// <summary>The best chunk of each file, best files first. Query filters apply as in the text search.</summary>
    public static List<SearchHit> Find(SqliteConnection db, ParsedQuery q, float[] query, string space, int limit, float minSimilarity)
    {
        var (where, args) = SearchSql.Filters(q);
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT f.path, c.grp, s.locator, f.mtime, f.size, s.id, e.vec
            FROM embeddings e
            JOIN segments s ON s.id = e.segment_id
            JOIN contents c ON c.id = s.content_id
            JOIN files f ON f.content_id = c.id
            WHERE e.space = $space AND e.chunk >= 0 AND {where}
            """;
        cmd.Parameters.AddWithValue("$space", space);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);

        var best = new Dictionary<string, (float Score, SearchHit Hit)>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[query.Length * sizeof(float)];
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.GetBytes(6, 0, buffer, 0, buffer.Length) != buffer.Length) continue;
            var score = TensorPrimitives.Dot(query, MemoryMarshal.Cast<byte, float>(buffer));
            if (score < minSimilarity) continue;
            var path = r.GetString(0);
            if (best.TryGetValue(path, out var old) && old.Score >= score) continue;
            best[path] = (score, new SearchHit(path, r.GetString(1), r.GetString(2), "",
                new DateTime(r.GetInt64(3), DateTimeKind.Utc), r.GetInt64(4), score) { SegmentId = r.GetInt64(5) });
        }
        return best.Values.OrderByDescending(b => b.Score).Take(limit).Select(b => b.Hit).ToList();
    }
}
