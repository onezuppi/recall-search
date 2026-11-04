namespace Recall.Agent;

/// <summary>Measures how fast the text backlog shrinks, from the counts seen since the agent started.</summary>
public sealed class ProgressTracker(Func<DateTime>? clock = null)
{
    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly object _gate = new();
    private (DateTime At, long Pending)? _first;

    /// <summary>Records the current backlog. Returns segments per minute since the first record, or 0 while that is unknown.</summary>
    public double Observe(long textPending)
    {
        lock (_gate)
        {
            var now = _now();
            _first ??= (now, textPending);
            var (at, pending) = _first.Value;
            var minutes = (now - at).TotalMinutes;
            // Newly stored files add to the backlog, so a rise is not a negative speed.
            return minutes < 1 || pending <= textPending ? 0 : (pending - textPending) / minutes;
        }
    }
}
