namespace Recall.CmdPal;

/// <summary>Runs agent calls one after another and stops at the first refusal. The agent validates and saves each call.</summary>
internal static class SettingsBatch
{
    /// <returns>The result of the last call, or the first failed one. Null when there was nothing to send.</returns>
    public static async Task<SettingsResult?> RunAsync(IEnumerable<Func<Task<SettingsResult>>> calls)
    {
        SettingsResult? last = null;
        foreach (var call in calls)
        {
            last = await call();
            if (!last.Ok) return last;
        }
        return last;
    }
}
