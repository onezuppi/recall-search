using Recall.Agent;
using Recall.Core.Activity;
using Recall.Core.Config;
using Recall.Core.Extract;
using Recall.Core.Ignore;
using Recall.Core.Index;
using Recall.Core.Ocr;

CliOptions cli;
try { cli = CliOptions.Parse(args); }
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    return 64;
}

var dataDir = RecallPaths.DataDir;
var configPath = Path.Combine(dataDir, "config.json");
var dbPath = cli.DbPath ?? Path.Combine(dataDir, "index.db");
var log = new RollingLog(Path.Combine(dataDir, "recall.log"), echoToConsole: cli.Once);

// Runs against another index (--db) are side runs such as benchmarks: they must not block or be blocked by the real agent.
Mutex? instance = null;
if (cli.DbPath == null)
{
    instance = new Mutex(true, @"Local\RecallSearch.Agent", out var isFirst);
    if (!isFirst)
    {
        Console.Error.WriteLine("Recall agent is already running for this user.");
        return 2;
    }
}
using var instanceGuard = instance;

var config = RecallConfig.Load(configPath);
if (cli.DbPath == null && !File.Exists(configPath)) config.Save(configPath);
if (cli.Roots.Count > 0)
    config = config with { Roots = cli.Roots.Select(r => new RootFolder(Path.GetFullPath(r), false)).ToList() };

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

PauseOptions? pause = config.PauseWhenBusy ? new PauseOptions(new WindowsActivityMonitor(config.PauseProcesses)) : null;

try
{
    if (cli.Once)
    {
        using var store = SqliteIndexRepository.Open(dbPath);
        var runner = IndexRunnerFactory.Create(config, store, new IgnorePolicy(config), cli.Workers);
        var report = await runner.RunAsync(config.Roots, null, shutdown.Token);
        log.Info($"one-shot pass done: {report}");
        return 0;
    }

    var configForSave = cli.DbPath == null && cli.Roots.Count == 0 ? configPath : null;
    await new AgentHost(config, new AgentOptions(dbPath, Log: log, Activity: pause, ConfigPath: configForSave)).RunAsync(shutdown.Token);
    return 0;
}
catch (OperationCanceledException) { return 0; }
catch (Exception e)
{
    log.Error("agent stopped", e);
    return 1;
}
