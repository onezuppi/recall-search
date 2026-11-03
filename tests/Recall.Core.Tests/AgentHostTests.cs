using Microsoft.Data.Sqlite;
using Recall.Agent;
using Recall.Core.Tests.Support;
using Recall.Core.Config;
using Recall.Core.Ipc;
using Recall.Core.Search;

namespace Recall.Core.Tests;

[Collection("AgentPaths")]
public class AgentHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-agent-" + Guid.NewGuid().ToString("N"));
    private readonly string _pipe = "RecallSearchTest." + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _cts = new();
    private readonly string _savedExe = AgentPaths.ExpectedExe;
    private readonly ListLog _log = new();
    private Task? _run;

    private const int Patience = 30;

    private string Root => Path.Combine(_dir, "watched");
    private string Db => Path.Combine(_dir, "index.db");

    public AgentHostTests()
    {
        Directory.CreateDirectory(Root);
        AgentPaths.ExpectedExe = Environment.ProcessPath!;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _run?.Wait(TimeSpan.FromSeconds(10)); } catch { }
        AgentPaths.ExpectedExe = _savedExe;
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AgentHost Start(TimeSpan debounce, PauseOptions? activity = null)
    {
        var groups = new HashSet<string>(["text"], StringComparer.OrdinalIgnoreCase);
        var config = new RecallConfig([new RootFolder(Root, true)], groups, []);
        var host = new AgentHost(config, new AgentOptions(Db, _pipe, debounce, _ => false, Log: _log, Activity: activity));
        _run = Task.Run(() => host.RunAsync(_cts.Token));
        Assert.True(host.Watching.Wait(TimeSpan.FromSeconds(10)), "watchers did not start");
        return host;
    }

    /// <summary>Polls until the condition holds. Timeouts are generous because the whole suite runs in parallel on a busy machine; the agent log is attached to a failure.</summary>
    private async Task WaitFor(Func<bool> condition, string what, int seconds = Patience)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        if (!condition()) Assert.Fail($"Not reached within {seconds} s: {what}{Environment.NewLine}Agent log:{Environment.NewLine}{_log}");
    }

    private bool Found(string word) => SearchEngine.ForDatabase(Db).Search(word, 5).Count > 0;

    [Fact]
    public async Task New_file_in_watched_root_becomes_searchable_soon()
    {
        Start(TimeSpan.FromMilliseconds(200));
        File.WriteAllText(Path.Combine(Root, "note.txt"), "zebra crossing schedule");
        await WaitFor(() => Found("zebra"), "word zebra");
    }

    [Fact]
    public async Task File_in_ignored_subfolder_of_watched_root_is_not_indexed()
    {
        Start(TimeSpan.FromMilliseconds(200));
        Directory.CreateDirectory(Path.Combine(Root, "node_modules"));
        File.WriteAllText(Path.Combine(Root, "node_modules", "x.md"), "ignoredword here");
        File.WriteAllText(Path.Combine(Root, "ok.txt"), "visibleword here");
        await WaitFor(() => Found("visibleword"), "word visibleword");
        Assert.False(Found("ignoredword"));
    }

    [Fact]
    public async Task Watcher_overflow_triggers_rescan()
    {
        // A long debounce keeps the normal event path from indexing the file, so only the rescan can.
        var host = Start(TimeSpan.FromMinutes(5));
        File.WriteAllText(Path.Combine(Root, "missed.txt"), "overflowword inside");
        host.SimulateOverflow(Root);
        await WaitFor(() => Found("overflowword"), "word overflowword");
    }

    [Fact]
    public async Task Deleted_file_disappears_from_search()
    {
        Start(TimeSpan.FromMilliseconds(200));
        var path = Path.Combine(Root, "gone.txt");
        File.WriteAllText(path, "temporaryword text");
        await WaitFor(() => Found("temporaryword"), "word temporaryword");
        File.Delete(path);
        await WaitFor(() => !Found("temporaryword"), "word gone: temporaryword");
    }

    [Fact]
    public async Task Search_works_over_the_pipe()
    {
        Start(TimeSpan.FromMilliseconds(200));
        File.WriteAllText(Path.Combine(Root, "pipe.txt"), "pipeword content");
        await WaitFor(() => Found("pipeword"), "word pipeword");
        var r = await PipeClient.SendAsync(new IpcRequest(IpcOps.Search, "pipeword"), TimeSpan.FromSeconds(5), default, _pipe);
        Assert.EndsWith("pipe.txt", Assert.Single(r.Hits!).Path);
        var status = await PipeClient.SendAsync(new IpcRequest(IpcOps.Status), TimeSpan.FromSeconds(5), default, _pipe);
        Assert.True(status.Status!.FilesIndexed >= 1);
    }

    [Fact]
    public async Task Busy_user_pauses_indexing_but_not_search_and_work_resumes_when_idle()
    {
        File.WriteAllText(Path.Combine(Root, "before.txt"), "beforeword content");
        var monitor = new FakeActivityMonitor("test game");
        Start(TimeSpan.FromMilliseconds(100), new PauseOptions(monitor, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(150)));
        await Task.Delay(500);
        Assert.False(Found("beforeword")); // the initial pass is held back

        File.WriteAllText(Path.Combine(Root, "during.txt"), "duringword content");
        await Task.Delay(600);
        Assert.False(Found("duringword")); // watcher events stay queued

        var ping = await PipeClient.SendAsync(new IpcRequest(IpcOps.Ping), TimeSpan.FromSeconds(5), default, _pipe);
        Assert.True(ping.Ok); // the pipe answers during the pause

        monitor.SetIdle();
        await WaitFor(() => Found("beforeword") && Found("duringword"), "both files searchable after the pause ends");
    }
}

/// <summary>Collects agent log lines so a failing wait can show what the agent was doing.</summary>
internal sealed class ListLog : IAgentLog
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

    public void Info(string message) => _lines.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {message}");

    public void Error(string message, Exception? error = null) => _lines.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} ERROR {message} {error}");

    public override string ToString() => string.Join(Environment.NewLine, _lines);
}
