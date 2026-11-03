using Microsoft.Data.Sqlite;
using Recall.Core.Ignore;

namespace Recall.Core.Search;

/// <summary>SQL of the text search: FTS5 trigram match, short-fragment scan and file-name match, with the shared query filters.</summary>
internal static class SearchSql
{
    internal static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    internal static void RegisterFunctions(SqliteConnection db)
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

    internal static (string Where, List<(string, object)> Args) Filters(ParsedQuery q)
    {
        var where = new List<string> { "f.status = 'ok'" };
        var args = new List<(string, object)>();
        if (q.In != null) { where.Add("rs_in(f.path, $in)"); args.Add(("$in", q.In)); }
        if (q.Type != null) { where.Add("(c.grp = $type OR rs_ext(f.path, $type))"); args.Add(("$type", q.Type)); }
        if (q.After is { } a) { where.Add("f.mtime >= $after"); args.Add(("$after", ToUtc(a).Ticks)); }
        if (q.Before is { } b) { where.Add("f.mtime < $before"); args.Add(("$before", ToUtc(b).Ticks)); }
        return (string.Join(" AND ", where), args);
    }

    internal static DateTime ToUtc(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();

    internal static List<SearchHit> RunFts(SqliteConnection db, ParsedQuery q, string matchExpr, List<string> shortTerms, int limit, int tier)
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

    internal static List<SearchHit> RunScan(SqliteConnection db, ParsedQuery q, List<string> shortTerms, int limit)
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

    internal static List<SearchHit> RunNames(SqliteConnection db, ParsedQuery q, List<string> terms, int limit)
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
