namespace Recall.Agent;

/// <summary>
/// Watches one root recursively. File events go to <c>onPath</c>. Anything the watcher cannot describe precisely
/// (buffer overflow, a folder created or renamed, watcher failure) raises <c>onRescan</c>, because the incremental scan is cheap and exact.
/// </summary>
public sealed class RootWatcher : IDisposable
{
    private readonly string _root;
    private readonly Action<string> _onPath;
    private readonly Action _onRescan;
    private readonly FileSystemWatcher _watcher;

    public RootWatcher(string root, Action<string> onPath, Action onRescan, IAgentLog log)
    {
        _root = root;
        _onPath = onPath;
        _onRescan = onRescan;
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, e) => Handle(e.FullPath);
        _watcher.Changed += (_, e) => Handle(e.FullPath);
        _watcher.Deleted += (_, e) => _onPath(e.FullPath);
        _watcher.Renamed += (_, e) => { _onPath(e.OldFullPath); Handle(e.FullPath); };
        _watcher.Error += (_, e) =>
        {
            log.Error($"watcher for {root} failed", e.GetException());
            NotifyOverflow();
        };
    }

    public void Start() => _watcher.EnableRaisingEvents = true;

    /// <summary>Called for the watcher's Error event; also the test seam for simulating a buffer overflow.</summary>
    public void NotifyOverflow() => _onRescan();

    private void Handle(string fullPath)
    {
        if (Directory.Exists(fullPath)) _onRescan();
        else _onPath(fullPath);
    }

    public void Dispose() => _watcher.Dispose();

    public override string ToString() => _root;
}
