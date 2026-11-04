using Microsoft.Data.Sqlite;
using Recall.Agent;
using Recall.Core.Config;
using Recall.Core.Embed;
using Recall.Core.Index;
using Recall.Core.Ipc;
using Recall.Core.Scan;
using Recall.Core.Search;

namespace Recall.Core.Tests;

public class DeviceAndProgressTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-dev-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "index.db");
    private string Cfg => Path.Combine(_dir, "config.json");

    public DeviceAndProgressTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static RecallConfig Config() => new([], new HashSet<string>(), []);

    private sealed class NoEffects : ISettingsEffects
    {
        public void RootAdded(RootFolder root) { }
        public void RootRemoved(RootFolder root) { }
        public void RootWatchChanged(RootFolder root) { }
        public void ExcludesChanged(IReadOnlyList<string> patterns) { }
        public void FileTypesChanged(IReadOnlyDictionary<string, string> types) { }
        public void SimilarityChanged(float minSimilarity) { }
    }

    [Fact]
    public void Cpu_and_off_need_nothing_and_name_their_model()
    {
        Assert.Null(DeviceChoice.Apply(Config(), " CPU ", out var cpu));
        Assert.Equal(("multilingual-e5-small", EmbeddingDevice.Cpu), (cpu.EmbeddingModel, cpu.EmbeddingDevice));
        Assert.Null(DeviceChoice.Apply(cpu, "off", out var off));
        Assert.Null(off.EmbeddingModel);
        Assert.Equal("off", DeviceChoice.Of(off));
        Assert.Equal("cpu", DeviceChoice.Of(cpu));
    }

    [Fact]
    public void Npu_is_refused_until_the_server_and_the_bundle_exist()
    {
        Assert.Contains("windows_embed_server", DeviceChoice.Apply(Config(), "npu", out _));
        var server = Path.Combine(_dir, "windows_embed_server.exe");
        File.WriteAllText(server, "");
        Assert.Contains("bundle", DeviceChoice.Apply(Config() with { NpuServerPath = server }, "npu", out _));
        Assert.Null(DeviceChoice.Apply(Config() with { NpuServerPath = server, NpuBundlePath = _dir }, "npu", out var npu));
        Assert.Equal(("embeddinggemma2-npu", EmbeddingDevice.Npu), (npu.EmbeddingModel, npu.EmbeddingDevice));
    }

    [Theory]
    [InlineData("gpu")]
    [InlineData("tpu")]
    [InlineData("")]
    public void Gpu_and_unknown_words_are_refused_and_change_nothing(string value)
    {
        Assert.NotNull(DeviceChoice.Apply(Config(), value, out var next));
        Assert.Null(next.EmbeddingModel);
    }

    [Fact]
    public void Controller_saves_the_device_and_reports_that_a_restart_is_pending()
    {
        var controller = new SettingsController(Config(), Cfg, null, new NoEffects());
        Assert.False(controller.Snapshot().RestartPending);
        var set = controller.Handle(new IpcRequest(IpcOps.SetDevice, Value: "cpu"));
        Assert.True(set.Ok);
        Assert.Equal("cpu", set.Settings!.Device);
        Assert.True(set.Settings.RestartPending);
        Assert.Equal("multilingual-e5-small", RecallConfig.Load(Cfg).EmbeddingModel);
        Assert.False(controller.Handle(new IpcRequest(IpcOps.SetDevice, Value: "gpu")).Ok);
    }

    [Theory]
    [InlineData("20", 20)]
    [InlineData(" 0 ", 0)]
    [InlineData("1440", 1440)]
    public void Embed_interval_is_saved_in_whole_minutes(string typed, int expected)
    {
        var controller = new SettingsController(Config(), Cfg, null, new NoEffects());
        Assert.Equal(20, controller.Snapshot().EmbedIntervalMinutes);
        var set = controller.Handle(new IpcRequest(IpcOps.SetEmbedInterval, Value: typed));
        Assert.True(set.Ok);
        Assert.Equal(expected, set.Settings!.EmbedIntervalMinutes);
        Assert.Equal(expected, RecallConfig.Load(Cfg).EmbedIntervalMinutes);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("soon")]
    [InlineData("1441")]
    [InlineData("")]
    public void Embed_interval_refuses_anything_else(string typed)
    {
        var controller = new SettingsController(Config(), Cfg, null, new NoEffects());
        Assert.False(controller.Handle(new IpcRequest(IpcOps.SetEmbedInterval, Value: typed)).Ok);
        Assert.Equal(20, controller.Snapshot().EmbedIntervalMinutes);
    }

    [Fact]
    public void Tracker_reports_the_speed_since_the_first_reading_and_ignores_a_growing_backlog()
    {
        var now = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);
        var tracker = new ProgressTracker(() => now);
        Assert.Equal(0, tracker.Observe(1000));
        now = now.AddSeconds(30);
        Assert.Equal(0, tracker.Observe(990)); // under a minute: not measurable yet
        now = now.AddMinutes(9).AddSeconds(30);
        Assert.Equal(40, tracker.Observe(600), 3);
        Assert.Equal(0, tracker.Observe(1500)); // new files arrived
    }

    [Fact]
    public async Task Pipe_handler_routes_the_scan_time_and_device_and_attaches_progress()
    {
        using var store = SqliteIndexRepository.Open(Db);
        var id = store.AddContent((UInt128)1, "text", [new Segment("", "one"), new Segment("", "two")]);
        store.UpsertFile(new FileRecord(@"C:\a.txt", 3, DateTime.UtcNow, "text"), @"C:\", id, FileStatus.Ok, null);
        using var host = new EmbedderHost("m@1", () => throw new InvalidOperationException("not needed"), TimeSpan.Zero);
        using var queue = new ChangeQueue(TimeSpan.FromSeconds(1), (_, _) => Task.CompletedTask, new NullLog());
        var controller = new SettingsController(Config(), Cfg, null, new NoEffects());
        var handler = new AgentRequestHandler(SearchEngine.ForDatabase(Db), store, queue, controller, () => null, host);

        var slow = await handler.HandleAsync(new IpcRequest(IpcOps.SetSlowScan, Value: "04:15"), default);
        Assert.True(slow.Ok);
        Assert.Equal("04:15", slow.Settings!.SlowScanTime);
        var progress = slow.Settings.Progress!;
        Assert.Equal((1L, 2L, 2L, "m@1"), (progress.FilesIndexed, progress.TextTotal, progress.TextPending, progress.Model));

        store.AddEmbeddings("m@1", [new EmbeddingRow(1, 0, [1f])]);
        var after = (await handler.HandleAsync(new IpcRequest(IpcOps.Settings), default)).Settings!.Progress!;
        Assert.Equal(1, after.TextPending);
        // Routed to the controller (which refuses it here because semantic search is off), not answered as an unknown operation.
        var similarity = await handler.HandleAsync(new IpcRequest(IpcOps.SetSimilarity, Value: "0.8"), default);
        Assert.Contains("Semantic search is off", similarity.Error);
    }
}
