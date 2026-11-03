using Microsoft.Data.Sqlite;
using Recall.Core.Ignore;

namespace Recall.Core.Search;

public sealed record SearchHit(string Path, string Group, string Locator, string Snippet, DateTime Modified, long Size, double Score)
{
    /// <summary>About 600 characters around the match for a details pane. Empty for file-name-only hits.</summary>
    public string Context { get; init; } = "";

    internal long SegmentId { get; init; }
}

/// <summary>
/// Fragment search over the trigram index. Ranking: phrase hits, then all-fragments hits by bm25, then file-name hits.
/// Fragments under 3 characters cannot use the trigram index and fall back to a scan with a case-insensitive contains.
/// </summary>
public sealed class SearchEngine(string dbPath)
{
    private const int MinTrigram = 3;

    public IReadOnlyList<SearchHit> Search(string text, int limit)
    {
        var q = QueryParser.Parse(text);
        if (q.IsEmpty || !File.Exists(dbPath)) return [];

        using var db = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite");
        db.Open();
        RegisterFunctions(db);

        var terms = q.Fragments.Concat(q.Phrases).ToList();
        var longTerms = terms.Where(t => t.Length >= MinTrigram).ToList();
        var shortTerms = terms.Where(t => t.Length < MinTrigram).ToList();
        var perQuery = Math.Max(limit, 1) * 10;
        var hits = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(IEnumerable<SearchHit> found)
        {
            foreach (var h in found)
                if (hits.Count < limit && seen.Add(h.Path)) hits.Add(h);
        }

        if (longTerms.Count > 0)
        {
            // 1) the unquoted words as one adjacent phrase, 2) all terms anywhere in the same segment
            if (q.Fragments.Count > 1 && q.Fragments.All(f => f.Length >= MinTrigram) && shortTerms.Count == 0)
            {
                var joined = string.Join(" ", q.Fragments);
                var expr = string.Join(" AND ", q.Phrases.Select(Quote).Append(Quote(joined)));
                Add(RunFts(db, q, expr, shortTerms, perQuery, tier: 2));
            }
            var all = string.Join(" AND ", longTerms.Select(Quote));
            Add(RunFts(db, q, all, shortTerms, perQuery, tier: 1));
        }
        else
        {
            Add(RunScan(db, q, shortTerms, perQuery));
        }

        Add(RunNames(db, q, terms, perQuery));
        return AttachContext(db, hits, terms);
    }

    private static List<SearchHit> AttachContext(SqliteConnection db, List<SearchHit> hits, IReadOnlyList<string> terms)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT substr(text, 1, 300000) FROM segments WHERE id = $id";
        var id = cmd.Parameters.Add("$id", SqliteType.Integer);
        return hits.Select(h =>
        {
            if (h.SegmentId == 0) return h;
            id.Value = h.SegmentId;
            return cmd.ExecuteScalar() is string text
                ? h with { Context = SnippetText.Context(text, terms), Snippet = SnippetText.Lead(text, terms) }
                : h;
        }).ToList();
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

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

    private static (string Where, List<(string, object)> Args) Filters(ParsedQuery q)
    {
        var where = new List<string> { "f.status = 'ok'" };
        var args = new List<(string, object)>();
        if (q.In != null) { where.Add("rs_in(f.path, $in)"); args.Add(("$in", q.In)); }
        if (q.Type != null) { where.Add("(c.grp = $type OR rs_ext(f.path, $type))"); args.Add(("$type", q.Type)); }
        if (q.After is { } a) { where.Add("f.mtime >= $after"); args.Add(("$after", ToUtc(a).Ticks)); }
        if (q.Before is { } b) { where.Add("f.mtime < $before"); args.Add(("$before", ToUtc(b).Ticks)); }
        return (string.Join(" AND ", where), args);
    }

    private static DateTime ToUtc(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();

    private static List<SearchHit> RunFts(SqliteConnection db, ParsedQuery q, string matchExpr, List<string> shortTerms, int limit, int tier)
    {
        var (where, args) = Filters(q);
        var extra = string.Concat(shortTerms.Select((_, i) => $" AND rs_contains(s.text, $s{i})"));
        using var cmd = db.CreateCommand();
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
        cmd.Parameters.AddWithValue("$m", matchExpr);
        cmd.Parameters.AddWithValue("$lim", limit);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        for (var i = 0; i < shortTerms.Count; i++) cmd.Parameters.AddWithValue($"$s{i}", shortTerms[i]);
        var list = new List<SearchHit>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SearchHit(r.GetString(0), r.GetString(1), r.GetString(2), SnippetText.Clean(r.GetString(3)),
                new DateTime(r.GetInt64(4), DateTimeKind.Utc), r.GetInt64(5), tier * 1000 - r.GetDouble(6)) { SegmentId = r.GetInt64(7) });
        return list;
    }

    private static List<SearchHit> RunScan(SqliteConnection db, ParsedQuery q, List<string> shortTerms, int limit)
    {
        var (where, args) = Filters(q);
        var conds = string.Concat(shortTerms.Select((_, i) => $" AND rs_contains(s.text, $s{i})"));
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT f.path, c.grp, s.locator, substr(s.text, 1, 200000), f.mtime, f.size, s.id
            FROM segments s
            JOIN contents c ON c.id = s.content_id
            JOIN files f ON f.content_id = c.id
            WHERE {where}{conds}
            ORDER BY f.mtime DESC
            LIMIT $lim
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        for (var i = 0; i < shortTerms.Count; i++) cmd.Parameters.AddWithValue($"$s{i}", shortTerms[i]);
        var list = new List<SearchHit>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SearchHit(r.GetString(0), r.GetString(1), r.GetString(2), SnippetText.Around(r.GetString(3), shortTerms[0]),
                new DateTime(r.GetInt64(4), DateTimeKind.Utc), r.GetInt64(5), 1000) { SegmentId = r.GetInt64(6) });
        return list;
    }

    private static List<SearchHit> RunNames(SqliteConnection db, ParsedQuery q, List<string> terms, int limit)
    {
        var (where, args) = Filters(q);
        var conds = string.Concat(terms.Select((_, i) => $" AND rs_contains(f.path, $n{i})"));
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT f.path, COALESCE(c.grp, ''), f.mtime, f.size
            FROM files f LEFT JOIN contents c ON c.id = f.content_id
            WHERE {where}{conds}
            ORDER BY f.mtime DESC
            LIMIT $lim
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        for (var i = 0; i < terms.Count; i++) cmd.Parameters.AddWithValue($"$n{i}", terms[i]);
        var list = new List<SearchHit>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var path = r.GetString(0);
            var group = r.GetString(1);
            if (group.Length == 0) group = BuiltInRules.GroupOf(System.IO.Path.GetExtension(path)) ?? "";
            list.Add(new SearchHit(path, group, "", System.IO.Path.GetFileName(path),
                new DateTime(r.GetInt64(2), DateTimeKind.Utc), r.GetInt64(3), 0));
        }
        return list;
    }
}
