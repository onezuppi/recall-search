using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Recall.Core.Index;

public sealed partial class IndexStore
{
    /// <summary>Chunk number of the marker row for a segment that has no embeddable text.</summary>
    public const int BlankChunk = -1;

    private const int PendingTextChars = 20_000;

    /// <summary>
    /// Segments of contents that some file with status ok points to and that have no row for <paramref name="space"/>.
    /// Text is cut to the first 20 000 characters, which is more than the chunker uses.
    /// </summary>
    public IReadOnlyList<PendingSegment> PendingSegments(string space, int limit)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT s.id, substr(s.text, 1, {PendingTextChars}) FROM segments s
                WHERE EXISTS (SELECT 1 FROM files f WHERE f.content_id = s.content_id AND f.status = 'ok')
                  AND NOT EXISTS (SELECT 1 FROM embeddings e WHERE e.segment_id = s.id AND e.space = $space)
                ORDER BY s.id LIMIT $lim
                """;
            cmd.Parameters.AddWithValue("$space", space);
            cmd.Parameters.AddWithValue("$lim", limit);
            var list = new List<PendingSegment>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new PendingSegment(r.GetInt64(0), r.GetString(1)));
            return list;
        }
    }

    /// <summary>Stores chunk vectors in one transaction. A segment with <see cref="BlankChunk"/> and an empty vector is marked as done.</summary>
    public void AddEmbeddings(string space, IReadOnlyList<(long SegmentId, int Chunk, float[] Vector)> rows)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO embeddings(segment_id, space, chunk, vec) VALUES ($s,$sp,$c,$v)";
            var seg = cmd.Parameters.Add("$s", SqliteType.Integer);
            var chunk = cmd.Parameters.Add("$c", SqliteType.Integer);
            var vec = cmd.Parameters.Add("$v", SqliteType.Blob);
            cmd.Parameters.AddWithValue("$sp", space);
            foreach (var (segmentId, c, v) in rows)
            {
                seg.Value = segmentId;
                chunk.Value = c;
                vec.Value = MemoryMarshal.AsBytes(v.AsSpan()).ToArray();
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
}
