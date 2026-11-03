using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Recall.Core.Index;

public sealed partial class SqliteIndexRepository : IEmbeddingRepository
{
    private const int PendingTextChars = 20_000;

    public IReadOnlyList<PendingSegment> PendingSegments(string space, int limit)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT s.id, substr(s.text, 1, {PendingTextChars}) FROM segments s
                WHERE EXISTS (SELECT 1 FROM files f WHERE f.content_id = s.content_id AND f.status = '{FileStatus.Ok}')
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

    public void AddEmbeddings(string space, IReadOnlyList<EmbeddingRow> rows)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            // A segment can be deleted while its page is being embedded, and SQLite may reuse its id for new text.
            cmd.CommandText = """
                INSERT OR REPLACE INTO embeddings(segment_id, space, chunk, vec)
                SELECT $s, $sp, $c, $v WHERE EXISTS (SELECT 1 FROM segments WHERE id = $s)
                """;
            var seg = cmd.Parameters.Add("$s", SqliteType.Integer);
            var chunk = cmd.Parameters.Add("$c", SqliteType.Integer);
            var vec = cmd.Parameters.Add("$v", SqliteType.Blob);
            cmd.Parameters.AddWithValue("$sp", space);
            foreach (var row in rows)
            {
                seg.Value = row.SegmentId;
                chunk.Value = row.Chunk;
                vec.Value = MemoryMarshal.AsBytes(row.Vector.AsSpan()).ToArray();
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void DeleteSpace(string space)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM embeddings WHERE space = $space";
            cmd.Parameters.AddWithValue("$space", space);
            cmd.ExecuteNonQuery();
        }
    }
}
