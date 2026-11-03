using Recall.Agent;
using Recall.Core.Config;
using Recall.Core.Extract;
using Recall.Core.Ignore;
using Recall.Core.Index;
using Recall.Core.Ocr;
using Recall.Core.Scan;

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

try
{
    if (cli.Once)
    {
        using var store = IndexStore.Open(dbPath);
        var ocr = new WindowsOcr(config.OcrLanguages, cli.Workers ?? IndexRunner.DefaultWorkers);
        IExtractor[] extractors = [new TextExtractor(), new OpenXmlExtractor(), new PdfExtractor(), new ImageExtractor(ocr)];
        var policy = new IgnorePolicy(config);
        var runner = new IndexRunner(store, policy, extractors, ocr, cli.Workers, scanThreads: cli.ScanThreads);
        var report = await runner.RunAsync(config.Roots, null, shutdown.Token, cli.Fast ? ScanMode.Auto : ScanMode.Full);
        log.Info($"one-shot pass done: {report}");
        return 0;
    }

    await new AgentHost(config, new AgentOptions(dbPath, Log: log)).RunAsync(shutdown.Token);
    return 0;
}
catch (OperationCanceledException) { return 0; }
catch (Exception e)
{
    log.Error("agent stopped", e);
    return 1;
}
