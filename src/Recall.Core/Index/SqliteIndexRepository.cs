using Microsoft.Data.Sqlite;
using Recall.Core.Scan;

namespace Recall.Core.Index;

/// <summary>SQLite implementation of <see cref="IIndexRepository"/>; segments are FTS5 trigram indexed (see <see cref="IndexSchema"/>).</summary>
public sealed class SqliteIndexRepository : IIndexRepository
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    private SqliteIndexRepository(SqliteConnection db) => _db = db;

    public static SqliteIndexRepository Open(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        var store = new SqliteIndexRepository(db);
        IndexSchema.Create(db);
        return store;
    }

    private static byte[] HashBytes(UInt128 hash)
    {
        var b = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt128LittleEndian(b, hash);
        return b;
    }

    public IReadOnlyDictionary<string, (long Size, long Mtime)> Snapshot(string root)
    {
        lock (_gate)
        {
            var d = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT path, size, mtime FROM files WHERE root = $r";
            cmd.Parameters.AddWithValue("$r", root);
            using var r = cmd.ExecuteReader();
            while (r.Read()) d[r.GetString(0)] = (r.GetInt64(1), r.GetInt64(2));
            return d;
        }
    }

    public (long Size, long Mtime)? FileState(string path)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT size, mtime FROM files WHERE path = $p";
            cmd.Parameters.AddWithValue("$p", path);
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetInt64(0), r.GetInt64(1)) : null;
        }
    }

    public long? FindContent(UInt128 hash)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id FROM contents WHERE hash = $h";
            cmd.Parameters.AddWithValue("$h", HashBytes(hash));
            var o = cmd.ExecuteScalar();
            return o == null ? null : Convert.ToInt64(o);
        }
    }

    public long AddContent(UInt128 hash, string group, IReadOnlyList<Segment> segments)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            long id;
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO contents(hash, grp, extracted_at) VALUES ($h,$g,$t) RETURNING id";
                cmd.Parameters.AddWithValue("$h", HashBytes(hash));
                cmd.Parameters.AddWithValue("$g", group);
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                id = Convert.ToInt64(cmd.ExecuteScalar());
            }
            foreach (var s in segments)
            {
                if (string.IsNullOrWhiteSpace(s.Text)) continue;
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO segments(content_id, locator, text) VALUES ($c,$l,$t)";
                cmd.Parameters.AddWithValue("$c", id);
                cmd.Parameters.AddWithValue("$l", s.Locator);
                cmd.Parameters.AddWithValue("$t", s.Text);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return id;
        }
    }

    public void UpsertFile(FileRecord f, string root, long? contentId, string status, string? error)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO files(path, root, size, mtime, content_id, status, error)
                    VALUES ($p,$r,$s,$m,$c,$st,$e)
                    ON CONFLICT(path) DO UPDATE SET root=$r, size=$s, mtime=$m, content_id=$c, status=$st, error=$e
                    """;
                cmd.Parameters.AddWithValue("$p", f.Path);
                cmd.Parameters.AddWithValue("$r", root);
                cmd.Parameters.AddWithValue("$s", f.Size);
                cmd.Parameters.AddWithValue("$m", f.LastWriteUtc.Ticks);
                cmd.Parameters.AddWithValue("$c", (object?)contentId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$st", status);
                cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            DeleteOrphans(tx);
            tx.Commit();
        }
    }

    public void RemoveFiles(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var p in paths)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM files WHERE path = $p";
                cmd.Parameters.AddWithValue("$p", p);
                cmd.ExecuteNonQuery();
            }
            DeleteOrphans(tx);
            tx.Commit();
        }
    }

    private void DeleteOrphans(SqliteTransaction tx)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM segments WHERE content_id IN
              (SELECT id FROM contents WHERE id NOT IN (SELECT content_id FROM files WHERE content_id IS NOT NULL));
            DELETE FROM contents WHERE id NOT IN (SELECT content_id FROM files WHERE content_id IS NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public (long Indexed, long Failed) Counts()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT COALESCE(SUM(status='{FileStatus.Ok}'),0), COALESCE(SUM(status='{FileStatus.Failed}'),0) FROM files";
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetInt64(0), r.GetInt64(1));
        }
    }

    public void Dispose() => _db.Dispose();
}
