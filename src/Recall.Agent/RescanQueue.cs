namespace Recall.Agent;

/// <summary>
/// Keeps at most one rescan waiting per root. A pass that has not started yet will see everything a later request would ask for,
/// so that request is dropped. Once the pass starts, a new request is accepted again: events from after the start may be missed by it.
/// </summary>
public sealed class RescanQueue
{
    private readonly object _sync = new();
    private readonly HashSet<string> _waiting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the caller should schedule a pass for this root; false when one is already waiting.</summary>
    public bool TryQueue(string root)
    {
        lock (_sync) return _waiting.Add(root);
    }

    /// <summary>The waiting pass has started; the next request schedules another.</summary>
    public void Started(string root)
    {
        lock (_sync) _waiting.Remove(root);
    }
}
