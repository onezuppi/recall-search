using Microsoft.Data.Sqlite;
using Recall.Core.Config;
using Recall.Core.Embed;
using Recall.Core.Extract;
using Recall.Core.Ignore;
using Recall.Core.Index;
using Recall.Core.Scan;
using Recall.Core.Tests.Support;

namespace Recall.Core.Tests;

public class IndexRunnerEmbeddingTests : RunnerTestBase
{
    private (IndexStore Store, IndexRunner Runner) Build(EmbedderHost? host)
    {
        var cfg = new RecallConfig([], new HashSet<string>(["text"], StringComparer.OrdinalIgnoreCase), []);
        var store = IndexStore.Open(Db);
        var runner = new IndexRunner(store, new IgnorePolicy(cfg, _ => false), [new TextExtractor()], new FakeOcr(""), embedder: host);
        return (store, runner);
    }

    private static EmbedderHost HostOf(FakeEmbedder e) => new(e.Space, () => e, TimeSpan.Zero);

    private long Count(string sql)
    {
        using var c = new SqliteConnection($"Data Source={Db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>A just-written file can be locked for a moment (scanners); the runner skips it and the next pass picks it up.</summary>
    private async Task<RunReport> RunUntilIndexed(IndexRunner runner, int files)
    {
        var report = await runner.RunAsync([R()], null, default);
        for (var attempt = 0; attempt < 20 && Count("SELECT COUNT(*) FROM files WHERE status = 'ok'") < files; attempt++)
        {
            await Task.Delay(250);
            report = await runner.RunAsync([R()], null, default);
        }
        return report;
    }

    [Fact]
    public async Task Indexing_creates_vectors_for_new_segments()
    {
        Write("a.txt", "первый документ про счета");
        Write("b.txt", "second document about bills");
        var (store, runner) = Build(HostOf(new FakeEmbedder()));
        using var _ = store;
        await RunUntilIndexed(runner, 2);
        Assert.Equal(2, Count("SELECT COUNT(*) FROM embeddings WHERE space = 'fake@1'"));
    }

    [Fact]
    public async Task Unchanged_second_run_does_not_call_embedder()
    {
        Write("a.txt", "alpha text");
        var fake = new FakeEmbedder();
        var (store, runner) = Build(HostOf(fake));
        using var _ = store;
        await RunUntilIndexed(runner, 1);
        var calls = fake.Calls;
        await runner.RunAsync([R()], null, default);
        Assert.Equal(calls, fake.Calls);
    }

    [Fact]
    public async Task Model_is_not_loaded_when_nothing_is_pending()
    {
        var loads = 0;
        var host = new EmbedderHost("fake@1", () => { loads++; return new FakeEmbedder(); }, TimeSpan.Zero);
        var (store, runner) = Build(host);
        using var _ = store;
        await runner.RunAsync([R()], null, default);
        Assert.Equal(0, loads);
    }

    [Fact]
    public async Task Changed_space_reembeds_and_keeps_old_vectors()
    {
        Write("a.txt", "alpha text");
        var (store, runner) = Build(HostOf(new FakeEmbedder("fake@1")));
        await runner.RunAsync([R()], null, default);
        store.Dispose();
        var (store2, runner2) = Build(HostOf(new FakeEmbedder("fake@2")));
        using var _ = store2;
        await runner2.RunAsync([R()], null, default);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM embeddings WHERE space = 'fake@1'"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM embeddings WHERE space = 'fake@2'"));
    }

    [Fact]
    public async Task Unavailable_model_does_not_fail_the_pass()
    {
        Write("a.txt", "needle in a haystack");
        var host = new EmbedderHost("m@1", () => throw new EmbeddingUnavailableException("not downloaded"), TimeSpan.Zero);
        var (store, runner) = Build(host);
        using var _ = store;
        var report = await runner.RunAsync([R()], null, default);
        Assert.Equal(0, report.Failed);
        Assert.Equal(1, Hits("needle"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM embeddings"));
    }

    [Fact]
    public async Task Cancellation_keeps_finished_pages()
    {
        using var store = IndexStore.Open(Db);
        for (var i = 0; i < 6; i++)
        {
            var id = store.AddContent((UInt128)(i + 1), "text", [new Segment("", "document number " + i)]);
            store.UpsertFile(new FileRecord($@"C:\d{i}.txt", 1, DateTime.UtcNow, "text"), @"C:\d", id, "ok", null);
        }
        var cts = new CancellationTokenSource();
        var batches = 0;
        var fake = new FakeEmbedder { OnEmbed = () => { if (++batches == 2) cts.Cancel(); } };
        var stage = new EmbeddingStage(store, HostOf(fake), pageSize: 2);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stage.RunAsync(cts.Token));
        Assert.Equal(4, Count("SELECT COUNT(DISTINCT segment_id) FROM embeddings"));
        Assert.Equal(2, store.PendingSegments("fake@1", 100).Count);
    }

    [Fact]
    public async Task Stage_reports_segments_and_chunks()
    {
        using var store = IndexStore.Open(Db);
        var id = store.AddContent(1, "text", [new Segment("", string.Join(' ', Enumerable.Repeat("слово", 600))), new Segment("p2", "   ")]);
        store.UpsertFile(new FileRecord(@"C:\d.txt", 1, DateTime.UtcNow, "text"), @"C:\d", id, "ok", null);
        var stats = await new EmbeddingStage(store, HostOf(new FakeEmbedder())).RunAsync(default);
        Assert.Equal(1, stats.Segments);
        Assert.InRange(stats.Chunks, 3, EmbedChunker.MaxChunks);
        Assert.Empty(store.PendingSegments("fake@1", 10));
    }

    [Fact]
    public async Task Model_is_unloaded_after_the_pass()
    {
        Write("a.txt", "alpha text");
        var host = HostOf(new FakeEmbedder());
        var (store, runner) = Build(host);
        using var _ = store;
        await runner.RunAsync([R()], null, default);
        Assert.False(host.IsLoaded);
    }
}
