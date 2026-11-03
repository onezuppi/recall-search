using Microsoft.Data.Sqlite;
using Recall.Core.Index;
using Recall.Core.Scan;
using Recall.Core.Search;

namespace Recall.Core.Tests;

public class SearchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-srch-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "index.db");
    private readonly IndexStore _store;
    private long _hash;

    public SearchTests()
    {
        Directory.CreateDirectory(_dir);
        _store = IndexStore.Open(Db);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void Add(string path, string group, DateTime modified, params (string Loc, string Text)[] segs)
    {
        var id = _store.AddContent((UInt128)(++_hash), group, segs.Select(s => new Segment(s.Loc, s.Text)).ToList());
        _store.UpsertFile(new FileRecord(path, 100, modified.ToUniversalTime(), group), Path.GetDirectoryName(path)!, id, "ok", null);
    }

    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void Parses_filters_and_phrases()
    {
        var q = QueryParser.Parse("\"network map\" moca in:notes type:pdf after:2026-05");
        Assert.Equal(["network map"], q.Phrases);
        Assert.Equal(["moca"], q.Fragments);
        Assert.Equal("notes", q.In);
        Assert.Equal("pdf", q.Type);
        Assert.Equal(new DateTime(2026, 5, 1), q.After);
        Assert.Null(q.Before);
        Assert.Equal(new DateTime(2026, 1, 1), QueryParser.Parse("after:2026").After);
        Assert.Equal(new DateTime(2026, 5, 14), QueryParser.Parse("after:2026-05-14").After);
        Assert.Equal(new DateTime(2026, 3, 1), QueryParser.Parse("before:2026-03").Before);
        Assert.Equal(["http://x"], QueryParser.Parse("http://x").Fragments);
    }

    [Fact]
    public void Fragment_matches_inside_words()
    {
        Add(@"C:\n\a.txt", "text", Now, ("", "Пароль от wifi: молоко123"));
        var hit = Assert.Single(new SearchEngine(Db).Search("оло", 10));
        Assert.Equal(@"C:\n\a.txt", hit.Path);
        Assert.Contains("молоко", hit.Snippet);
    }

    [Fact]
    public void Phrase_beats_scattered_words()
    {
        Add(@"C:\n\scattered.txt", "text", Now, ("", "the map of the whole network"));
        Add(@"C:\n\adjacent.txt", "text", Now, ("", "our network map is attached"));
        var hits = new SearchEngine(Db).Search("network map", 10);
        Assert.Equal(2, hits.Count);
        Assert.EndsWith("adjacent.txt", hits[0].Path);
    }

    [Fact]
    public void Quoted_phrase_requires_adjacency()
    {
        Add(@"C:\n\scattered.txt", "text", Now, ("", "the map of the whole network"));
        Add(@"C:\n\adjacent.txt", "text", Now, ("", "our network map is attached"));
        var hits = new SearchEngine(Db).Search("\"network map\"", 10);
        Assert.Equal(@"C:\n\adjacent.txt", Assert.Single(hits).Path);
    }

    [Fact]
    public void Type_and_date_filters_apply()
    {
        Add(@"C:\notes\old.pdf", "pdf", new DateTime(2025, 1, 10, 0, 0, 0, DateTimeKind.Local), ("page:1", "budget plan"));
        Add(@"C:\notes\new.pdf", "pdf", new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Local), ("page:1", "budget plan"));
        Add(@"C:\other\new.txt", "text", new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Local), ("", "budget plan"));
        var e = new SearchEngine(Db);
        Assert.Equal(3, e.Search("budget", 10).Count);
        Assert.Equal(2, e.Search("budget type:pdf", 10).Count);
        Assert.Equal(2, e.Search("budget after:2026-05", 10).Count);
        Assert.Single(e.Search("budget before:2026", 10));
        Assert.Equal(@"C:\notes\new.pdf", Assert.Single(e.Search("budget type:pdf after:2026", 10)).Path);
        Assert.Equal(2, e.Search("budget in:notes", 10).Count);
        Assert.Equal(2, e.Search("budget type:.pdf", 10).Count);
    }

    [Fact]
    public void Two_char_query_uses_fallback()
    {
        Add(@"C:\n\a.txt", "text", Now, ("", "Мой код ab12"));
        var e = new SearchEngine(Db);
        Assert.Single(e.Search("ab", 10));
        Assert.Single(e.Search("мо", 10));
        Assert.Single(e.Search("код ab", 10));
        Assert.Empty(e.Search("zz", 10));
    }

    [Fact]
    public void One_hit_per_file_with_best_snippet()
    {
        Add(@"C:\n\doc.pdf", "pdf", Now,
            ("page:1", "intro with a single invoice mention and lots of other filler words around it to dilute"),
            ("page:2", "invoice invoice invoice"));
        var hits = new SearchEngine(Db).Search("invoice", 10);
        var hit = Assert.Single(hits);
        Assert.Equal("page:2", hit.Locator);
        Assert.Contains("invoice", hit.Snippet);
        Assert.True(hit.Snippet.Length <= 160);
    }

    [Fact]
    public void File_name_hits_rank_below_text_hits()
    {
        Add(@"C:\n\contract-draft.txt", "text", Now, ("", "unrelated words"));
        Add(@"C:\n\other.txt", "text", Now, ("", "see the contract terms"));
        var hits = new SearchEngine(Db).Search("contract", 10);
        Assert.Equal(2, hits.Count);
        Assert.EndsWith("other.txt", hits[0].Path);
        Assert.EndsWith("contract-draft.txt", hits[1].Path);
        Assert.Equal("contract-draft.txt", hits[1].Snippet);
    }

    [Fact]
    public void Empty_query_and_missing_db_return_nothing()
    {
        Assert.Empty(new SearchEngine(Db).Search("   ", 10));
        Assert.Empty(new SearchEngine(Path.Combine(_dir, "none.db")).Search("abc", 10));
    }

    [Fact]
    public void Hit_context_is_longer_than_the_snippet_and_centered_on_the_match()
    {
        var filler = string.Concat(Enumerable.Repeat("слово ", 120));
        Add(@"C:\n\long.txt", "text", Now, ("", filler + "ключевая фраза найдена здесь " + filler));
        var hit = Assert.Single(new SearchEngine(Db).Search("ключевая", 5));
        Assert.Contains("ключевая фраза найдена здесь", hit.Context);
        Assert.True(hit.Context.Length > hit.Snippet.Length);
        Assert.InRange(hit.Context.Length, 200, 800);
        Assert.StartsWith("…", hit.Context);
        Assert.EndsWith("…", hit.Context);
    }

    [Fact]
    public void Hit_context_has_no_leading_ellipsis_when_the_match_is_at_the_start()
    {
        Add(@"C:\n\short.txt", "text", Now, ("", "Шаумяна 20, офис продаж"));
        var hit = Assert.Single(new SearchEngine(Db).Search("Шаумяна", 5));
        Assert.Equal("Шаумяна 20, офис продаж", hit.Context);
    }

    [Fact]
    public void File_name_only_hit_has_no_context()
    {
        Add(@"C:\n\contract-draft.txt", "text", Now, ("", "unrelated words"));
        var hit = Assert.Single(new SearchEngine(Db).Search("contract", 5));
        Assert.Equal("", hit.Context);
    }
}
