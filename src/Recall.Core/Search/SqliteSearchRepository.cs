using Microsoft.Data.Sqlite;
using Recall.Core.Index;

namespace Recall.Core.Search;

/// <summary>SQLite implementation of <see cref="ISearchRepository"/>: all search SQL lives here.</summary>
public sealed class SqliteSearchRepository(string dbPath) : ISearchRepository
{
    public ISearchSession? Open()
    {
        if (!File.Exists(dbPath)) return null;
        var db = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite");
        db.Open();
        return new Session(db);
    }

    private sealed class Session : ISearchSession
    {
        private const int ScanTextMax = 200000, ContextTextMax = 300000;
        private readonly SqliteConnection _db;

        public Session(SqliteConnection db)
        {
            _db = db;
            RegisterFunctions(db);
        }

        public IReadOnlyList<SegmentRow> MatchSegments(ParsedQuery filter, string matchExpression, IReadOnlyList<string> shortTerms, int limit)
        {
            var (where, args) = Filters(filter);
            var extra = string.Concat(shortTerms.Select((_, i) => $" AND rs_contains(s.text, $s{i})"));
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT f.path, c.grp, s.locator, snippet(segments_fts, 0, '', '', ' … ', 64), f.mtime, f.size, bm25(segments_fts), s.id
                FROM segments_fts
                JOIN segments s ON s.id = segments_fts.rowid
                JOIN contents c ON c.id = s.content_id
                JOIN files f ON f.content_id = c.id
                WHERE segments_fts MATCH $m AND {where}{extra}
                ORDER BY bm25(segments_fts)
                LIMIT $lim
                """;
            cmd.Parameters.AddWithValue("$m", matchExpression);
            Bind(cmd, args, limit, shortTerms, "$s");
            return ReadSegments(cmd, rankColumn: 6, idColumn: 7);
        }

        public IReadOnlyList<SegmentRow> ScanSegments(ParsedQuery filter, IReadOnlyList<string> shortTerms, int limit)
        {
            var (where, args) = Filters(filter);
            var conds = string.Concat(shortTerms.Select((_, i) => $" AND rs_contains(s.text, $s{i})"));
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT f.path, c.grp, s.locator, substr(s.text, 1, {ScanTextMax}), f.mtime, f.size, 0.0, s.id
                FROM segments s
                JOIN contents c ON c.id = s.content_id
                JOIN files f ON f.content_id = c.id
                WHERE {where}{conds}
                ORDER BY f.mtime DESC
                LIMIT $lim
                """;
            Bind(cmd, args, limit, shortTerms, "$s");
            return ReadSegments(cmd, rankColumn: 6, idColumn: 7);
        }

        public IReadOnlyList<FileRow> MatchFileNames(ParsedQuery filter, IReadOnlyList<string> terms, int limit)
        {
            var (where, args) = Filters(filter);
            var conds = string.Concat(terms.Select((_, i) => $" AND rs_contains(f.path, $n{i})"));
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT f.path, COALESCE(c.grp, ''), f.mtime, f.size
                FROM files f LEFT JOIN contents c ON c.id = f.content_id
                WHERE {where}{conds}
                ORDER BY f.mtime DESC
                LIMIT $lim
                """;
            Bind(cmd, args, limit, terms, "$n");
            var rows = new List<FileRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(new FileRow(r.GetString(0), r.GetString(1), new DateTime(r.GetInt64(2), DateTimeKind.Utc), r.GetInt64(3)));
            return rows;
        }

        public void ScanVectors(ParsedQuery filter, string space, int dimensions, VectorVisitor visit)
        {
            var (where, args) = Filters(filter);
            using var cmd = _db.CreateCommand();
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
            var buffer = new byte[dimensions * sizeof(float)];
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.GetBytes(6, 0, null, 0, 0) != buffer.Length) continue;
                r.GetBytes(6, 0, buffer, 0, buffer.Length);
                var row = new VectorRow(r.GetString(0), r.GetString(1), r.GetString(2), new DateTime(r.GetInt64(3), DateTimeKind.Utc), r.GetInt64(4), r.GetInt64(5));
                visit(row, System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer));
            }
        }

        public string? SegmentText(long segmentId)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT substr(text, 1, {ContextTextMax}) FROM segments WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", segmentId);
            return cmd.ExecuteScalar() as string;
        }

        public void Dispose() => _db.Dispose();

        private static List<SegmentRow> ReadSegments(SqliteCommand cmd, int rankColumn, int idColumn)
        {
            var rows = new List<SegmentRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new SegmentRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    new DateTime(r.GetInt64(4), DateTimeKind.Utc), r.GetInt64(5), r.GetDouble(rankColumn), r.GetInt64(idColumn)));
            return rows;
        }

        private static void Bind(SqliteCommand cmd, List<(string Name, object Value)> args, int limit, IReadOnlyList<string> terms, string prefix)
        {
            cmd.Parameters.AddWithValue("$lim", limit);
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
            for (var i = 0; i < terms.Count; i++) cmd.Parameters.AddWithValue($"{prefix}{i}", terms[i]);
        }

        private static (string Where, List<(string, object)> Args) Filters(ParsedQuery q)
        {
            var where = new List<string> { $"f.status = '{FileStatus.Ok}'" };
            var args = new List<(string, object)>();
            if (q.In != null) { where.Add("rs_in(f.path, $in)"); args.Add(("$in", q.In)); }
            if (q.Type != null) { where.Add("(c.grp = $type OR rs_ext(f.path, $type))"); args.Add(("$type", q.Type)); }
            if (q.After is { } a) { where.Add("f.mtime >= $after"); args.Add(("$after", ToUtc(a).Ticks)); }
            if (q.Before is { } b) { where.Add("f.mtime < $before"); args.Add(("$before", ToUtc(b).Ticks)); }
            return (string.Join(" AND ", where), args);
        }

        private static DateTime ToUtc(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();

        private static void RegisterFunctions(SqliteConnection db)
        {
            db.CreateFunction("rs_contains", (string? text, string? frag) =>
                text != null && frag != null && text.Contains(frag, StringComparison.OrdinalIgnoreCase));
            db.CreateFunction("rs_in", (string? path, string? term) =>
            {
                if (path == null || term == null) return false;
                var parts = path.Split('\\');
                for (var i = 0; i < parts.Length - 1; i++)
                    if (parts[i].StartsWith(term, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            });
            db.CreateFunction("rs_ext", (string? path, string? ext) =>
                path != null && ext != null && path.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase));
        }
    }
}
