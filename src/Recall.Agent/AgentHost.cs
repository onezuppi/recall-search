using Recall.Core.Config;
using Recall.Core.Extract;
using Recall.Core.Ignore;
using Recall.Core.Index;
using Recall.Core.Ipc;
using Recall.Core.Ocr;

namespace Recall.Agent;

/// <param name="DbPath">Index database file.</param>
/// <param name="PipeName">Null selects the per-user pipe name.</param>
/// <param name="Debounce">Quiet time before watcher events are indexed. Null selects 2 seconds.</param>
/// <param name="IsSystemPath">Overrides the built-in system path rule (tests run inside %TEMP%).</param>
public sealed record AgentOptions(
    string DbPath, string? PipeName = null, TimeSpan? Debounce = null, Func<string, bool>? IsSystemPath = null, IAgentLog? Log = null);

/// <summary>Indexes the configured roots, keeps watched roots current, and answers searches on the per-user pipe.</summary>
public sealed class AgentHost(RecallConfig config, AgentOptions options)
{
    private readonly IAgentLog _log = options.Log ?? new NullLog();
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private readonly TaskCompletionSource _watching = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, RootWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private IndexRunner? _runner;
    private ChangeQueue? _queue;
    private IgnorePolicy? _policy;
    private DateTime? _lastRunUtc;

    /// <summary>Completes once every watched root is being observed (the initial pass may still be running).</summary>
    public Task Watching => _watching.Task;

    public RunReport? LastReport { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        using var store = IndexStore.Open(options.DbPath);
        _policy = new IgnorePolicy(config, options.IsSystemPath);
        var ocr = new WindowsOcr(config.OcrLanguages);
        IExtractor[] extractors = [new TextExtractor(), new OpenXmlExtractor(), new PdfExtractor(), new ImageExtractor(ocr)];
        _runner = new IndexRunner(store, _policy, extractors, ocr);
        using var queue = _queue = new ChangeQueue(options.Debounce ?? TimeSpan.FromSeconds(2), FlushAsync, _log);

        var handler = new AgentRequestHandler(options.DbPath, store, queue, () => _lastRunUtc);
        var pipe = new PipeServer(handler.HandleAsync, options.PipeName).RunAsync(ct);
        if (pipe.IsFaulted) await pipe; // the pipe name is already owned by another process

        // Watchers start before the first pass so nothing changed during it is missed.
        StartWatchers();
        _watching.TrySetResult();

        await IndexAsync(config.Roots, ct);
        await pipe;
        foreach (var w in _watchers.Values) w.Dispose();
    }

    /// <summary>Test seam and overflow entry point: re-scan one watched root.</summary>
    public void SimulateOverflow(string rootPath)
    {
        if (_watchers.TryGetValue(rootPath, out var w)) w.NotifyOverflow();
    }

    private void StartWatchers()
    {
        foreach (var root in config.Roots.Where(r => r.Watched && Directory.Exists(r.Path)))
        {
            var watcher = new RootWatcher(root.Path, p => _queue!.Add(root.Path, p), () => RescanSoon(root), _log);
            _watchers[root.Path] = watcher;
            watcher.Start();
            _log.Info($"watching {root.Path}");
        }
    }

    private void RescanSoon(RootFolder root)
    {
        _ = Task.Run(async () =>
        {
            try { await IndexAsync([root], CancellationToken.None); }
            catch (Exception e) { _log.Error($"rescan of {root.Path} failed", e); }
        });
    }

    private async Task IndexAsync(IReadOnlyList<RootFolder> roots, CancellationToken ct)
    {
        await _indexGate.WaitAsync(ct);
        try
        {
            _log.Info($"indexing {roots.Count} root(s)");
            LastReport = await _runner!.RunAsync(roots, null, ct);
            _lastRunUtc = DateTime.UtcNow;
            _log.Info($"pass done: {LastReport}");
        }
        finally { _indexGate.Release(); }
    }

    private async Task FlushAsync(string rootPath, IReadOnlyList<string> paths)
    {
        var root = config.Roots.FirstOrDefault(r => string.Equals(r.Path, rootPath, StringComparison.OrdinalIgnoreCase));
        if (root == null) return;
        var allowed = paths.Where(p => IsIndexable(root, p)).ToList();
        if (allowed.Count == 0) return;
        await _indexGate.WaitAsync();
        try { await _runner!.IndexFilesAsync(root, allowed, CancellationToken.None); }
        finally { _indexGate.Release(); }
    }

    /// <summary>Deleted files pass through (removal is harmless); existing files must satisfy the full ignore policy.</summary>
    private bool IsIndexable(RootFolder root, string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return true;
        return _policy!.Allows(root.Path, path, info.Length, info.Attributes);
    }
}
