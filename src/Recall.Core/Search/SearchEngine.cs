using Microsoft.Data.Sqlite;
using Recall.Core.Embed;

namespace Recall.Core.Search;

public sealed record SearchHit(string Path, string Group, string Locator, string Snippet, DateTime Modified, long Size, double Score)
{
    /// <summary>About 600 characters around the match for a details pane. Empty for file-name-only hits.</summary>
    public string Context { get; init; } = "";

    internal long SegmentId { get; init; }
}

/// <summary>
/// Fragment search over the trigram index. Ranking: phrase hits, then all-fragments hits by bm25, then file-name hits.
/// With an <paramref name="embedder"/> the content hits are merged with vector-similarity hits (<see cref="RankFusion"/>);
/// when the model is not available the search is text-only.
/// Fragments under 3 characters cannot use the trigram index and fall back to a scan with a case-insensitive contains.
/// </summary>
public sealed class SearchEngine(string dbPath, EmbedderHost? embedder = null)
{
    private const int MinTrigram = 3;
    private const int HybridPoolFactor = 3;


    public IReadOnlyList<SearchHit> Search(string text, int limit)
    {
        var q = QueryParser.Parse(text);
        if (q.IsEmpty || !File.Exists(dbPath)) return [];

        using var db = new SqliteConnection($"Data Source={dbPath};Mode=ReadWrite");
        db.Open();
        SearchSql.RegisterFunctions(db);

        var terms = q.Fragments.Concat(q.Phrases).ToList();
        var longTerms = terms.Where(t => t.Length >= MinTrigram).ToList();
        var shortTerms = terms.Where(t => t.Length < MinTrigram).ToList();
        var perQuery = Math.Max(limit, 1) * 10;
        var hits = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var cap = embedder == null ? limit : limit * HybridPoolFactor;

        void Add(IEnumerable<SearchHit> found)
        {
            foreach (var h in found)
                if (hits.Count < cap && seen.Add(h.Path)) hits.Add(h);
        }

        if (longTerms.Count > 0)
        {
            // 1) the unquoted words as one adjacent phrase, 2) all terms anywhere in the same segment
            if (q.Fragments.Count > 1 && q.Fragments.All(f => f.Length >= MinTrigram) && shortTerms.Count == 0)
            {
                var joined = string.Join(" ", q.Fragments);
                var expr = string.Join(" AND ", q.Phrases.Select(SearchSql.Quote).Append(SearchSql.Quote(joined)));
                Add(SearchSql.RunFts(db, q, expr, shortTerms, perQuery, tier: 2));
            }
            var all = string.Join(" AND ", longTerms.Select(SearchSql.Quote));
            Add(SearchSql.RunFts(db, q, all, shortTerms, perQuery, tier: 1));
        }
        else
        {
            Add(SearchSql.RunScan(db, q, shortTerms, perQuery));
        }

        var vectorHits = VectorHits(db, q, terms, cap);
        if (vectorHits.Count > 0) hits = RankFusion.Merge(hits, vectorHits);
        hits = hits.Take(limit).ToList();
        seen.Clear();
        foreach (var h in hits) seen.Add(h.Path);
        cap = limit;
        Add(SearchSql.RunNames(db, q, terms, perQuery));
        return AttachContext(db, hits, terms);
    }

    private List<SearchHit> VectorHits(SqliteConnection db, ParsedQuery q, List<string> terms, int limit)
    {
        if (embedder == null || terms.Count == 0) return [];
        try
        {
            using var lease = embedder.Acquire();
            var vector = lease.Embedder.Embed([string.Join(" ", terms)], EmbedKind.Query)[0];
            return VectorSearch.Find(db, q, vector, embedder.Space, limit, embedder.MinSimilarity);
        }
        catch (EmbeddingUnavailableException) { return []; }
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
            if (cmd.ExecuteScalar() is not string text) return h;
            var snippet = h.Snippet.Length > 0 ? h.Snippet : SnippetText.Clean(text);
            return h with { Snippet = snippet, Context = SnippetText.Context(text, terms) };
        }).ToList();
    }
}
