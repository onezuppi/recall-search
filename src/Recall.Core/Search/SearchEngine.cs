using Recall.Core.Ignore;

namespace Recall.Core.Search;

public sealed record SearchHit(string Path, string Group, string Locator, string Snippet, DateTime Modified, long Size, double Score)
{
    /// <summary>About 350 characters around the match for a details pane. Empty for file-name-only hits.</summary>
    public string Context { get; init; } = "";

    internal long SegmentId { get; init; }
}

/// <summary>
/// Fragment search over the trigram index. Ranking: phrase hits, then all-fragments hits by bm25, then file-name hits.
/// Fragments under 3 characters cannot use the trigram index and fall back to a scan with a case-insensitive contains.
/// </summary>
public sealed class SearchEngine(ISearchRepository repository)
{
    private const int MinTrigram = 3;
    private const int TierPhrase = 2, TierAllTerms = 1;
    private const double TierWeight = 1000, ScanScore = 1000;

    public static SearchEngine ForDatabase(string dbPath) => new(new SqliteSearchRepository(dbPath));

    public IReadOnlyList<SearchHit> Search(string text, int limit)
    {
        var q = QueryParser.Parse(text);
        if (q.IsEmpty) return [];
        using var session = repository.Open();
        if (session is null) return [];

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
                Add(Matched(session.MatchSegments(q, expr, shortTerms, perQuery), TierPhrase));
            }
            var all = string.Join(" AND ", longTerms.Select(Quote));
            Add(Matched(session.MatchSegments(q, all, shortTerms, perQuery), TierAllTerms));
        }
        else
        {
            Add(session.ScanSegments(q, shortTerms, perQuery).Select(r => Hit(r, SnippetText.Around(r.Text, shortTerms[0]), ScanScore)));
        }

        Add(session.MatchFileNames(q, terms, perQuery).Select(NameHit));
        return hits.Select(h => WithContext(session, h, terms)).ToList();
    }

    private static IEnumerable<SearchHit> Matched(IReadOnlyList<SegmentRow> rows, int tier) =>
        rows.Select(r => Hit(r, SnippetText.Clean(r.Text), tier * TierWeight - r.Rank));

    private static SearchHit Hit(SegmentRow r, string snippet, double score) =>
        new(r.Path, r.Group, r.Locator, snippet, r.Modified, r.Size, score) { SegmentId = r.SegmentId };

    private static SearchHit NameHit(FileRow f)
    {
        var group = f.Group.Length > 0 ? f.Group : BuiltInRules.GroupOf(System.IO.Path.GetExtension(f.Path)) ?? "";
        return new SearchHit(f.Path, group, "", System.IO.Path.GetFileName(f.Path), f.Modified, f.Size, 0);
    }

    private static SearchHit WithContext(ISearchSession session, SearchHit h, IReadOnlyList<string> terms)
    {
        if (h.SegmentId == 0 || session.SegmentText(h.SegmentId) is not { } text) return h;
        return h with { Context = SnippetText.Context(text, terms), Snippet = SnippetText.Lead(text, terms) };
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}
