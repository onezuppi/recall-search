using Recall.Core.Activity;
using Recall.Core.Config;
using Recall.Core.Extract;
using Recall.Core.Ignore;
using Recall.Core.Index;
using Recall.Core.Search;
using Recall.Core.Ipc;
using Recall.Core.Ocr;

namespace Recall.Agent;

/// <param name="DbPath">Index database file.</param>
/// <param name="PipeName">Null selects the per-user pipe name.</param>
/// <param name="Debounce">Quiet time before watcher events are indexed. Null selects 2 seconds.</param>
/// <param name="IsSystemPath">Overrides the built-in system path rule (tests run inside %TEMP%).</param>
/// <param name="Activity">Pauses indexing while its monitor reports busy. Null never pauses.</param>
/// <param name="ConfigPath">Where settings changes are saved. Null keeps them in memory only.</param>
public sealed record AgentOptions(
    string DbPath, string? PipeName = null, TimeSpan? Debounce = null, Func<string, bool>? IsSystemPath = null, IAgentLog? Log = null,
    PauseOptions? Activity = null, string? ConfigPath = null);

/// <param name="Poll">How often the monitor is asked. Null selects 3 seconds.</param>
/// <param name="ResumeDelay">Continuous idle time before work resumes. Null selects 30 seconds.</param>
public sealed record PauseOptions(IActivityMonitor Monitor, TimeSpan? Poll = null, TimeSpan? ResumeDelay = null);

/// <summary>Indexes the configured roots, keeps watched roots current, and answers searches on the per-user pipe.</summary>
public sealed class AgentHost(RecallConfig config, AgentOptions options) : ISettingsEffects
{
    private readonly IAgentLog _log = options.Log ?? new NullLog();
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private readonly TaskCompletionSource _watching = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, RootWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private SettingsController? _settings;
    private IIndexRepository? _store;
    private IndexRunner? _runner;
    private ChangeQueue? _queue;
    private IgnorePolicy? _policy;
    private DateTime? _lastRunUtc;

    /// <summary>Completes once every watched root is being observed (the initial pass may still be running).</summary>
    public Task Watching => _watching.Task;

    public RunReport? LastReport { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        using var store = _store = SqliteIndexRepository.Open(options.DbPath);
        _settings = new SettingsController(config, options.ConfigPath, options.IsSystemPath, this);
        _policy = new IgnorePolicy(config, options.IsSystemPath);
        var ocr = new ReleasableOcr(() => IndexRunnerFactory.CreateOcr(config));
        using var gate = StartPauseGate(ocr);
        _runner = IndexRunnerFactory.Create(config, store, _policy, ocr: ocr, pause: gate);
        using var queue = _queue = new ChangeQueue(options.Debounce ?? TimeSpan.FromSeconds(2), FlushAsync, _log, () => gate?.IsPaused ?? false);
        if (gate != null) gate.Changed += (paused, _) => { if (!paused) queue.Resume(); };

        var handler = new AgentRequestHandler(SearchEngine.ForDatabase(options.DbPath), store, queue, _settings, () => _lastRunUtc);
        var pipe = new PipeServer(handler.HandleAsync, options.PipeName).RunAsync(ct);
        if (pipe.IsFaulted) await pipe; // the pipe name is already owned by another process

        // Watchers start before the first pass so nothing changed during it is missed.
        StartWatchers();
        _watching.TrySetResult();

        await IndexAsync(_settings.Config.Roots, ct);
        await pipe;
        lock (_watchers) foreach (var w in _watchers.Values) w.Dispose();
    }

    /// <summary>On pause the OCR engines are dropped; the log records both transitions.</summary>
    private ActivityGate? StartPauseGate(ReleasableOcr ocr)
    {
        if (options.Activity == null) return null;
        var pause = options.Activity;
        var gate = new ActivityGate(pause.Monitor, pause.Poll ?? TimeSpan.FromSeconds(3), pause.ResumeDelay ?? TimeSpan.FromSeconds(30));
        if (gate.IsPaused) _log.Info("indexing paused at start");
        gate.Changed += (paused, reason) =>
        {
            if (paused) ocr.Release();
            _log.Info(paused ? $"indexing paused: {reason}" : "indexing resumed");
        };
        return gate;
    }

    /// <summary>Test seam and overflow entry point: re-scan one watched root.</summary>
    public void SimulateOverflow(string rootPath)
    {
        RootWatcher? w;
        lock (_watchers) _watchers.TryGetValue(rootPath, out w);
        w?.NotifyOverflow();
    }

    private void StartWatchers()
    {
        foreach (var root in _settings!.Config.Roots) StartWatcher(root);
    }

    private void StartWatcher(RootFolder root)
    {
        if (!root.Watched || !Directory.Exists(root.Path)) return;
        var watcher = new RootWatcher(root.Path, p => _queue!.Add(root.Path, p), () => RescanSoon(root), _log);
        lock (_watchers) _watchers[root.Path] = watcher;
        watcher.Start();
        _log.Info($"watching {root.Path}");
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
            // A root removed while this pass waited for the gate must not come back.
            var current = roots.Where(r => _settings!.IsConfigured(r.Path)).ToList();
            _log.Info($"indexing {current.Count} root(s)");
            LastReport = await _runner!.RunAsync(current, null, ct);
            _lastRunUtc = DateTime.UtcNow;
            _log.Info($"pass done: {LastReport}");
        }
        finally { _indexGate.Release(); }
    }

    private async Task FlushAsync(string rootPath, IReadOnlyList<string> paths)
    {
        var root = _settings!.Config.Roots.FirstOrDefault(r => string.Equals(r.Path, rootPath, StringComparison.OrdinalIgnoreCase));
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

    void ISettingsEffects.RootAdded(RootFolder root)
    {
        StartWatcher(root);
        Background($"indexing of {root.Path}", () => IndexAsync([root], CancellationToken.None));
    }

    void ISettingsEffects.RootRemoved(RootFolder root)
    {
        RootWatcher? w;
        lock (_watchers) { _watchers.Remove(root.Path, out w); }
        w?.Dispose();
        Background($"removal of {root.Path}", () => RemoveRootFilesAsync(root));
    }

    void ISettingsEffects.ExcludesChanged(IReadOnlyList<string> patterns)
    {
        _policy!.SetGlobalPatterns(patterns);
        // A full pass drops files the new patterns exclude and brings back those a removed pattern had hidden.
        Background("rescan after exclude change", () => IndexAsync(_settings!.Config.Roots, CancellationToken.None));
    }

    private async Task RemoveRootFilesAsync(RootFolder root)
    {
        await _indexGate.WaitAsync();
        try
        {
            if (_settings!.IsConfigured(root.Path)) return; // added back while this waited
            var rootPath = Path.GetFullPath(root.Path).TrimEnd('\\');
            var paths = _store!.Snapshot(rootPath).Keys.ToList();
            _store.RemoveFiles(paths);
            _log.Info($"removed {paths.Count} file(s) of {rootPath} from the index");
        }
        finally { _indexGate.Release(); }
    }

    private void Background(string what, Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try { await work(); }
            catch (Exception e) { _log.Error(what + " failed", e); }
        });
    }
}
