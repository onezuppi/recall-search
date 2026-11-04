using Microsoft.Data.Sqlite;
using Recall.Core.Index;

namespace Recall.Core.Search;

/// <summary>SQLite implementation of <see cref="ISearchRepository"/>: all search SQL lives here.</summary>
public sealed class SqliteSearchRepository(string dbPath) : ISearchRepository
{
    private readonly VectorCache _vectors = new();

    public ISearchSession? Open()
    {
        if (!File.Exists(dbPath)) return null;
        var db = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite");
        db.Open();
        return new Session(db, _vectors);
    }

    private sealed class Session : ISearchSession
    {
        private const int ScanTextMax = 200000, ContextTextMax = 300000;
        private readonly SqliteConnection _db;
        private readonly VectorCache _vectors;

        public Session(SqliteConnection db, VectorCache vectors)
        {
            (_db, _vectors) = (db, vectors);
            RegisterFunctions(db);
        }

        public IReadOnlyList<SegmentRow> MatchSegments(ParsedQuery filter, string matchExpression, IReadOnlyList<string> shortTerms, int limit)
        {
            var (where, args) = Filters(filter);
            var extra = string.Concat(shortTerms.Select((_, i) => $" AND rs_contains(s.text, $s{i})"));
            var restricted = filter.In != null || filter.Type != null || filter.After != null || filter.Before != null || shortTerms.Count > 0;
            using var cmd = _db.CreateCommand();
            // The snippet column is empty: the engine replaces it with a window of the segment text for every hit it returns.
            // Without restricting filters the best segments are picked from the FTS table alone, so the joins run for a few rows
            // instead of for every segment that contains a common word. With filters the join must come first, or the pool could be
            // left empty by them.
            cmd.CommandText = restricted
                ? $"""
                    SELECT f.path, c.grp, s.locator, '', f.mtime, f.size, bm25(segments_fts), s.id
                    FROM segments_fts
                    JOIN segments s ON s.id = segments_fts.rowid
                    JOIN contents c ON c.id = s.content_id
                    JOIN files f ON f.content_id = c.id
                    WHERE segments_fts MATCH $m AND {where}{extra}
                    ORDER BY bm25(segments_fts)
                    LIMIT $lim
                    """
                : $"""
                    WITH m AS MATERIALIZED (
                        SELECT rowid AS id, bm25(segments_fts) AS rank FROM segments_fts
                        WHERE segments_fts MATCH $m ORDER BY rank LIMIT $pool)
                    SELECT f.path, c.grp, s.locator, '', f.mtime, f.size, m.rank, s.id
                    FROM m
                    JOIN segments s ON s.id = m.id
                    JOIN contents c ON c.id = s.content_id
                    JOIN files f ON f.content_id = c.id
                    WHERE {where}
                    ORDER BY m.rank
                    LIMIT $lim
                    """;
            cmd.Parameters.AddWithValue("$m", matchExpression);
            cmd.Parameters.AddWithValue("$pool", Math.Max(limit, 1) * PoolFactor);
            Bind(cmd, args, limit, shortTerms, "$s");
            return ReadSegments(cmd, rankColumn: 6, idColumn: 7);
        }

        /// <summary>Segments taken from the FTS table per wanted row: files sharing one content and files that are not ok use up pool entries.</summary>
        private const int PoolFactor = 4;

        public IReadOnlyList<SegmentRow> ScanSegments(ParsedQuery filter, IReadOnlyList<string> shortTerms, int limit)
        {
            var (where, args) = Filters(filter);
            // Case variants let SQLite's own instr reject most rows before the slower managed rs_contains runs.
            var conds = string.Concat(shortTerms.Select((_, i) => $"{CaseFilter(shortTerms[i], i)} AND rs_contains(s.text, $s{i})").Select(c => " AND " + c));
            using var cmd = _db.CreateCommand();
            // CROSS JOIN fixes the order: newest files first through the mtime index, so a common term stops after `limit` rows.
            cmd.CommandText = $"""
                SELECT f.path, c.grp, s.locator, substr(s.text, 1, {ScanTextMax}), f.mtime, f.size, 0.0, s.id
                FROM files f
                CROSS JOIN contents c ON c.id = f.content_id
                CROSS JOIN segments s ON s.content_id = c.id
                WHERE {where}{conds}
                ORDER BY f.mtime DESC
                LIMIT $lim
                """;
            Bind(cmd, args, limit, shortTerms, "$s");
            for (var i = 0; i < shortTerms.Count; i++)
                foreach (var (v, j) in CaseVariants(shortTerms[i]).Select((v, j) => (v, j)))
                    cmd.Parameters.AddWithValue($"$s{i}v{j}", System.Text.Encoding.UTF8.GetBytes(v));
            return ReadSegments(cmd, rankColumn: 6, idColumn: 7);
        }

        private const int MaxVariantLength = 2;

        /// <summary>Every lower/upper combination of a term of up to two characters; longer terms get none (the filter is skipped).</summary>
        private static IReadOnlyList<string> CaseVariants(string term)
        {
            if (term.Length is 0 or > MaxVariantLength) return [];
            var variants = new List<string> { "" };
            foreach (var ch in term)
            {
                var forms = new[] { char.ToLowerInvariant(ch), char.ToUpperInvariant(ch) }.Distinct().ToArray();
                variants = [.. variants.SelectMany(v => forms.Select(f => v + f))];
            }
            return variants;
        }

        private static string CaseFilter(string term, int index)
        {
            var n = CaseVariants(term).Count;
            return n == 0 ? "1" : "(" + string.Join(" OR ", Enumerable.Range(0, n).Select(j => $"instr(CAST(s.text AS BLOB), $s{index}v{j}) > 0")) + ")";
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

        public void ScanVectors(string space, int dimensions, VectorVisitor visit) =>
            _vectors.Scan(space, dimensions, v => StreamVectors(space, dimensions, v), visit);

        private void StreamVectors(string space, int dimensions, VectorVisitor visit)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT segment_id, vec FROM embeddings WHERE space = $space AND chunk >= 0";
            cmd.Parameters.AddWithValue("$space", space);
            var size = dimensions * sizeof(float);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var bytes = r.GetFieldValue<byte[]>(1);
                if (bytes.Length != size) continue;
                visit(r.GetInt64(0), System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes));
            }
        }

        public IReadOnlySet<long>? FilteredSegments(ParsedQuery filter)
        {
            if (filter.In == null && filter.Type == null && filter.After == null && filter.Before == null) return null;
            var (where, args) = Filters(filter);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT s.id FROM segments s
                JOIN contents c ON c.id = s.content_id
                JOIN files f ON f.content_id = c.id
                WHERE {where}
                """;
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
            var ids = new HashSet<long>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
            return ids;
        }

        public IReadOnlyList<VectorRow> VectorRows(ParsedQuery filter, IReadOnlyCollection<long> segmentIds)
        {
            if (segmentIds.Count == 0) return [];
            var (where, args) = Filters(filter);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"""
                SELECT f.path, c.grp, s.locator, f.mtime, f.size, s.id
                FROM segments s
                JOIN contents c ON c.id = s.content_id
                JOIN files f ON f.content_id = c.id
                WHERE s.id IN (SELECT value FROM json_each($ids)) AND {where}
                """;
            cmd.Parameters.AddWithValue("$ids", "[" + string.Join(",", segmentIds) + "]");
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
            var rows = new List<VectorRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new VectorRow(r.GetString(0), r.GetString(1), r.GetString(2), new DateTime(r.GetInt64(3), DateTimeKind.Utc), r.GetInt64(4), r.GetInt64(5)));
            return rows;
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
