using Microsoft.Data.Sqlite;
using Recall.Agent;
using Recall.Core.Config;
using Recall.Core.Ipc;
using Recall.Core.Search;

namespace Recall.Core.Tests.Support;

/// <summary>A running agent host with one watched root, a config file and the pipe, for tests that change settings through the pipe.</summary>
[Collection("AgentPaths")]
public abstract class SettingsTestBase : IDisposable
{
    protected readonly string Dir = Path.Combine(Path.GetTempPath(), "rs-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string _pipe = "RecallSearchTest." + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _cts = new();
    private readonly string _savedExe = AgentPaths.ExpectedExe;
    private readonly Task? _run;

    protected string Root => Path.Combine(Dir, "watched");
    protected string Db => Path.Combine(Dir, "index.db");
    protected string ConfigFile => Path.Combine(Dir, "config.json");

    protected SettingsTestBase()
    {
        Directory.CreateDirectory(Root);
        AgentPaths.ExpectedExe = Environment.ProcessPath!;
        var groups = new HashSet<string>(["text"], StringComparer.OrdinalIgnoreCase);
        var config = new RecallConfig([new RootFolder(Root, true)], groups, []);
        var options = new AgentOptions(Db, _pipe, TimeSpan.FromMilliseconds(200), p => p.EndsWith("system", StringComparison.OrdinalIgnoreCase),
            ConfigPath: ConfigFile);
        var host = new AgentHost(config, options);
        _run = Task.Run(() => host.RunAsync(_cts.Token));
        Assert.True(host.Watching.Wait(TimeSpan.FromSeconds(10)), "watchers did not start");
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _run?.Wait(TimeSpan.FromSeconds(10)); } catch { }
        AgentPaths.ExpectedExe = _savedExe;
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch { }
    }

    protected Task<IpcResponse> Send(string op, string? value = null, string? target = null) =>
        PipeClient.SendAsync(new IpcRequest(op, Value: value, Target: target), TimeSpan.FromSeconds(5), default, _pipe);

    protected string Folder(string name, string? fileName = null, string? text = null)
    {
        var path = Path.Combine(Dir, name);
        Directory.CreateDirectory(path);
        if (fileName != null) File.WriteAllText(Path.Combine(path, fileName), text);
        return path;
    }

    protected bool Found(string word) => SearchEngine.ForDatabase(Db).Search(word, 5).Count > 0;

    protected static async Task<bool> WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }
}
