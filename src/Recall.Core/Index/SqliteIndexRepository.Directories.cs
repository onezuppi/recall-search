using Microsoft.Data.Sqlite;

namespace Recall.Core.Index;

/// <summary>Remembered directory times and small key-value state that let a later pass skip unchanged directories.</summary>
public sealed partial class SqliteIndexRepository
{
    public IReadOnlyDictionary<string, long> DirectoryTimes(string root)
    {
        lock (_gate)
        {
            var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT path, mtime FROM dirs WHERE root = $r";
            cmd.Parameters.AddWithValue("$r", root);
            using var r = cmd.ExecuteReader();
            while (r.Read()) d[r.GetString(0)] = r.GetInt64(1);
            return d;
        }
    }

    /// <summary>Stores new directory times and forgets the removed directories, in one transaction.</summary>
    public void ApplyDirectories(string root, IReadOnlyDictionary<string, long> upserts, IEnumerable<string> removals)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var p in removals) Run(tx, "DELETE FROM dirs WHERE path = $p", ("$p", p));
            foreach (var (p, m) in upserts)
                Run(tx, "INSERT INTO dirs(path, root, mtime) VALUES ($p,$r,$m) ON CONFLICT(path) DO UPDATE SET root=$r, mtime=$m",
                    ("$p", p), ("$r", root), ("$m", m));
            tx.Commit();
        }
    }

    public string? GetMeta(string key)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string value)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO meta(key, value) VALUES ($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    private void Run(SqliteTransaction tx, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }
}
