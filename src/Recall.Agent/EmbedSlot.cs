namespace Recall.Agent;

/// <summary>When the background vector stage may run again. The interval is read at every look, so a change in the settings counts at once.</summary>
public static class EmbedSlot
{
    /// <summary>The longest single sleep while waiting for a slot.</summary>
    public static readonly TimeSpan Slice = TimeSpan.FromSeconds(15);

    /// <summary>Time left until the next run; zero before the first run (the start-up backlog is never delayed) and when the slot is open.</summary>
    public static TimeSpan Remaining(DateTime? lastRunEndedUtc, int intervalMinutes, DateTime nowUtc)
    {
        if (lastRunEndedUtc is not { } last) return TimeSpan.Zero;
        var wait = last + TimeSpan.FromMinutes(intervalMinutes) - nowUtc;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    public static TimeSpan NextSleep(TimeSpan remaining) => remaining < Slice ? remaining : Slice;
}
