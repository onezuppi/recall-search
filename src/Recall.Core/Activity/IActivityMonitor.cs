namespace Recall.Core.Activity;

/// <summary>Tells whether the user is busy with something that must not be slowed down (a game, a presentation).</summary>
public interface IActivityMonitor
{
    /// <summary>Short reason while the user is busy, null otherwise. Cheap enough to poll every few seconds.</summary>
    string? BusyReason();
}

/// <summary>Lets long-running work wait while the user is busy.</summary>
public interface IPauseGate
{
    bool IsPaused { get; }

    /// <summary>Completes at once when not paused, otherwise when the pause ends.</summary>
    Task WaitWhilePausedAsync(CancellationToken ct);
}
