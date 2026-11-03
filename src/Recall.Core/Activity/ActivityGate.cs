namespace Recall.Core.Activity;

/// <summary>
/// Polls an <see cref="IActivityMonitor"/>. Busy pauses immediately; the pause ends only after the monitor
/// has been idle for <c>resumeDelay</c> without a break, so a short flicker does not restart the work.
/// </summary>
public sealed class ActivityGate : IPauseGate, IDisposable
{
    private readonly IActivityMonitor _monitor;
    private readonly TimeSpan _resumeDelay;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private TaskCompletionSource _resumed = Completed();
    private DateTime? _idleSince;
    private bool _paused;

    /// <summary>Raised on every transition: (paused, reason). The reason is null when resuming. Runs on a timer thread.</summary>
    public event Action<bool, string?>? Changed;

    public ActivityGate(IActivityMonitor monitor, TimeSpan pollInterval, TimeSpan resumeDelay)
    {
        _monitor = monitor;
        _resumeDelay = resumeDelay;
        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, pollInterval);
    }

    public bool IsPaused
    {
        get { lock (_gate) return _paused; }
    }

    public Task WaitWhilePausedAsync(CancellationToken ct)
    {
        Task wait;
        lock (_gate) wait = _resumed.Task;
        return wait.IsCompleted ? Task.CompletedTask : wait.WaitAsync(ct);
    }

    private void Poll()
    {
        string? reason;
        try { reason = _monitor.BusyReason(); }
        catch (Exception) { return; } // an unreadable state must not change anything

        bool? transition = null;
        lock (_gate)
        {
            if (reason != null)
            {
                _idleSince = null;
                if (!_paused)
                {
                    _paused = true;
                    _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    transition = true;
                }
            }
            else if (_paused)
            {
                _idleSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - _idleSince >= _resumeDelay)
                {
                    _paused = false;
                    _idleSince = null;
                    _resumed.TrySetResult();
                    transition = false;
                }
            }
        }
        if (transition is { } p) Changed?.Invoke(p, reason);
    }

    private static TaskCompletionSource Completed()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate) _resumed.TrySetResult(); // never leave a waiter hanging
    }
}
