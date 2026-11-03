using Microsoft.Data.Sqlite;
using Recall.Core.Embed;
using Recall.Core.Index;
using Recall.Core.Scan;
using Recall.Core.Search;
using Recall.Core.Tests.Support;

namespace Recall.Core.Tests;

public class HybridSearchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-hyb-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "index.db");
    private readonly SqliteIndexRepository _store;
    private readonly FakeEmbedder _fake = new();
    private long _hash;

    public HybridSearchTests()
    {
        Directory.CreateDirectory(_dir);
        _store = SqliteIndexRepository.Open(Db);
        _fake.Aliases["bill"] = "invoice";
        _fake.Aliases["payment"] = "invoice";
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void Add(string path, string text, string group = "text", long? sameHash = null)
    {
        var hash = (UInt128)(sameHash ?? ++_hash);
        var id = _store.FindContent(hash) ?? _store.AddContent(hash, group, [new Segment("", text)]);
        _store.UpsertFile(new FileRecord(path, 10, DateTime.UtcNow, group), _dir, id, "ok", null);
    }

    private async Task<SearchEngine> Engine(FakeEmbedder? embedder = null, string? space = null)
    {
        var e = embedder ?? _fake;
        var host = new EmbedderHost(space ?? e.Space, () => e, TimeSpan.Zero);
        await new EmbeddingStage(_store, new EmbedderHost(e.Space, () => e, TimeSpan.Zero)).RunAsync(default);
        return SearchEngine.ForDatabase(Db, host);
    }

    [Fact]
    public async Task Semantic_hit_without_shared_words_is_found()
    {
        Add(@"C:\d\a.txt", "bill payment due friday");
        Add(@"C:\d\b.txt", "borscht recipe with sour cream");
        var hits = (await Engine()).Search("invoice", 5);
        Assert.Equal(@"C:\d\a.txt", hits[0].Path);
        Assert.NotEmpty(hits[0].Snippet);
    }

    [Fact]
    public async Task Exact_hit_ranks_before_semantic_only_hit()
    {
        Add(@"C:\d\exact.txt", "invoice number 42");
        Add(@"C:\d\semantic.txt", "bill payment");
        var hits = (await Engine()).Search("invoice", 5);
        Assert.Equal([@"C:\d\exact.txt", @"C:\d\semantic.txt"], hits.Select(h => h.Path).Take(2));
    }

    [Fact]
    public async Task Filters_apply_to_vector_hits()
    {
        Add(@"C:\d\a.pdf", "bill payment", group: "pdf");
        Add(@"C:\d\b.txt", "bill payment");
        var hits = (await Engine()).Search("type:pdf invoice", 5);
        Assert.Equal([@"C:\d\a.pdf"], hits.Select(h => h.Path));
    }

    [Fact]
    public async Task Same_content_two_paths_both_listed()
    {
        Add(@"C:\d\a.txt", "bill payment", sameHash: 99);
        Add(@"C:\d\copy\a.txt", "bill payment", sameHash: 99);
        var hits = (await Engine()).Search("invoice", 5);
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task Old_space_vectors_are_ignored()
    {
        Add(@"C:\d\a.txt", "bill payment");
        var old = new FakeEmbedder("fake@1") { Aliases = { ["bill"] = "invoice", ["payment"] = "invoice" } };
        await Engine(old);
        var other = new FakeEmbedder("fake@2");
        var engine = SearchEngine.ForDatabase(Db, new EmbedderHost("fake@2", () => other, TimeSpan.Zero));
        Assert.Empty(engine.Search("invoice", 5));
    }

    [Fact]
    public async Task Filter_only_query_does_not_embed()
    {
        Add(@"C:\d\a.txt", "bill payment");
        var engine = await Engine();
        var calls = _fake.Calls;
        Assert.Empty(engine.Search("type:pdf", 5));
        Assert.Equal(calls, _fake.Calls);
        engine.Search("invoice", 5);
        Assert.Equal(calls + 1, _fake.Calls);
    }

    [Fact]
    public void Unavailable_model_falls_back_to_text_search()
    {
        Add(@"C:\d\a.txt", "needle in a haystack");
        var host = new EmbedderHost("m@1", () => throw new EmbeddingUnavailableException("not downloaded"), TimeSpan.Zero);
        var hits = SearchEngine.ForDatabase(Db, host).Search("needle", 5);
        Assert.Equal([@"C:\d\a.txt"], hits.Select(h => h.Path));
    }

    [Fact]
    public async Task Model_is_unloaded_after_a_query()
    {
        Add(@"C:\d\a.txt", "bill payment");
        var host = new EmbedderHost(_fake.Space, () => _fake, TimeSpan.Zero);
        await new EmbeddingStage(_store, host).RunAsync(default);
        SearchEngine.ForDatabase(Db, host).Search("invoice", 5);
        Assert.False(host.IsLoaded);
    }
}
