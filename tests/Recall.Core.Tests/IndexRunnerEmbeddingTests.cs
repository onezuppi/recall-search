using Microsoft.Data.Sqlite;
using Recall.Core.Activity;
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
    private (SqliteIndexRepository Store, IndexRunner Runner) Build(EmbedderHost? host)
    {
        var cfg = new RecallConfig([], new HashSet<string>(["text"], StringComparer.OrdinalIgnoreCase), []);
        var store = SqliteIndexRepository.Open(Db);
        var runner = new IndexRunner(store, new IgnorePolicy(cfg, _ => false), [new TextExtractor()], new FakeOcr(""), embeddings: store, embedder: host);
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
        var report = await RunUntilIndexed(runner, 2);
        Assert.True(Count("SELECT COUNT(*) FROM embeddings WHERE space = 'fake@1'") == 2,
            $"{report} segments={Count("SELECT COUNT(*) FROM segments")} files={Count("SELECT COUNT(*) FROM files WHERE status = 'ok'")}");
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
        using var store = SqliteIndexRepository.Open(Db);
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
        using var store = SqliteIndexRepository.Open(Db);
        var id = store.AddContent(1, "text", [new Segment("", string.Join(' ', Enumerable.Repeat("слово", 600))), new Segment("p2", "   ")]);
        store.UpsertFile(new FileRecord(@"C:\d.txt", 1, DateTime.UtcNow, "text"), @"C:\d", id, "ok", null);
        var stats = await new EmbeddingStage(store, HostOf(new FakeEmbedder())).RunAsync(default);
        Assert.Equal(1, stats.Segments);
        Assert.InRange(stats.Chunks, 3, EmbedChunker.MaxChunks);
        Assert.Empty(store.PendingSegments("fake@1", 10));
    }

    [Fact]
    public async Task Crashing_model_is_reported_and_does_not_fail_the_pass()
    {
        Write("a.txt", "needle in a haystack");
        var host = new EmbedderHost("m@1", () => throw new InvalidOperationException("boom"), TimeSpan.Zero);
        var (store, runner) = Build(host);
        using var _ = store;
        var report = await RunUntilIndexed(runner, 1);
        Assert.Equal(0, report.Failed);
        Assert.Contains("boom", report.EmbedError);
        Assert.Equal(1, Hits("needle"));
    }

    private sealed class FakePause : IPauseGate
    {
        private TaskCompletionSource _resumed = new();
        public bool IsPaused { get; private set; }
        public void Pause() { IsPaused = true; _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public void Resume() { IsPaused = false; _resumed.SetResult(); }
        public Task WaitWhilePausedAsync(CancellationToken ct) => IsPaused ? _resumed.Task.WaitAsync(ct) : Task.CompletedTask;
    }

    private static void AddSegments(SqliteIndexRepository store, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var id = store.AddContent((UInt128)(i + 1), "text", [new Segment("", "document number " + i)]);
            store.UpsertFile(new FileRecord($@"C:\d{i}.txt", 1, DateTime.UtcNow, "text"), @"C:\d", id, "ok", null);
        }
    }

    private static async Task<bool> WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        return condition();
    }

    [Fact]
    public async Task Pause_drops_the_model_keeps_pending_segments_and_resume_loads_it_again()
    {
        using var store = SqliteIndexRepository.Open(Db);
        AddSegments(store, 4);
        var pause = new FakePause();
        var loads = 0;
        var fake = new FakeEmbedder();
        var host = new EmbedderHost(fake.Space, () => { loads++; return fake; }, TimeSpan.FromMinutes(5));
        fake.OnEmbed = () => { if (fake.Calls == 1) pause.Pause(); };
        var run = new EmbeddingStage(store, host, pageSize: 2, pause: pause).RunAsync(default);

        Assert.True(await WaitFor(() => pause.IsPaused && !host.IsLoaded), "the model stayed loaded while paused");
        Assert.Equal(2, store.PendingSegments(fake.Space, 10).Count);
        Assert.False(run.IsCompleted);

        pause.Resume();
        await run;
        Assert.Empty(store.PendingSegments(fake.Space, 10));
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task Pending_segments_resume_after_a_restart()
    {
        using (var first = SqliteIndexRepository.Open(Db))
        {
            AddSegments(first, 4);
            var cts = new CancellationTokenSource();
            var fake = new FakeEmbedder { OnEmbed = cts.Cancel };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EmbeddingStage(first, HostOf(fake), pageSize: 2).RunAsync(cts.Token));
        }
        SqliteConnection.ClearAllPools();
        using var second = SqliteIndexRepository.Open(Db);
        Assert.Equal(2, second.PendingSegments("fake@1", 10).Count);
        var again = new FakeEmbedder();
        await new EmbeddingStage(second, HostOf(again), pageSize: 2).RunAsync(default);
        Assert.Equal(2, again.Texts);
        Assert.Empty(second.PendingSegments("fake@1", 10));
    }

    [Fact]
    public async Task Identical_files_share_one_vector()
    {
        Write("a.txt", "same text in two files");
        Write("b.txt", "same text in two files");
        var fake = new FakeEmbedder();
        var (store, runner) = Build(HostOf(fake));
        using var _ = store;
        await RunUntilIndexed(runner, 2);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM embeddings"));
        Assert.Equal(1, fake.Texts);
    }

    [Fact]
    public async Task Vectors_disappear_with_the_file()
    {
        var path = Write("a.txt", "doomed text");
        var (store, runner) = Build(HostOf(new FakeEmbedder()));
        using var _ = store;
        await RunUntilIndexed(runner, 1);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM embeddings"));
        File.Delete(path);
        await runner.RunAsync([R()], null, default);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM embeddings"));
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
