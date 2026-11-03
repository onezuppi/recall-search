namespace Recall.Agent;

/// <summary>
/// Collects changed paths per root and hands them to <paramref name="flush"/> once no new event has arrived for the debounce time.
/// Paths are deduplicated, so a file written in many chunks is reported once.
/// </summary>
public sealed class ChangeQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _debounce;
    private readonly Func<string, IReadOnlyList<string>, Task> _flush;
    private readonly IAgentLog _log;
    private readonly Timer _timer;

    public ChangeQueue(TimeSpan debounce, Func<string, IReadOnlyList<string>, Task> flush, IAgentLog log)
    {
        _debounce = debounce;
        _flush = flush;
        _log = log;
        _timer = new Timer(_ => _ = DrainAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public int Pending
    {
        get { lock (_gate) return _pending.Values.Sum(s => s.Count); }
    }

    public void Add(string root, string path)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(root, out var set)) _pending[root] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(path);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task DrainAsync()
    {
        Dictionary<string, List<string>> batch;
        lock (_gate)
        {
            batch = _pending.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
            _pending.Clear();
        }
        foreach (var (root, paths) in batch)
        {
            try { await _flush(root, paths); }
            catch (Exception e) { _log.Error($"indexing {paths.Count} changed file(s) under {root} failed", e); }
        }
    }

    public void Dispose() => _timer.Dispose();
}
