namespace Recall.CmdPal;

/// <summary>Runs agent calls one after another and stops at the first refusal. The agent validates and saves each call.</summary>
internal static class SettingsBatch
{
    /// <returns>
    /// The result of the last call, or the first failed one. Null when there was nothing to send.
    /// When a call fails after earlier ones were already saved, the failed result carries the settings as they are now,
    /// so the page shows what the agent really holds instead of the list from before the batch.
    /// </returns>
    public static async Task<SettingsResult?> RunAsync(IEnumerable<Func<Task<SettingsResult>>> calls)
    {
        SettingsResult? last = null;
        var saved = 0;
        foreach (var call in calls)
        {
            last = await call();
            if (!last.Ok) return saved > 0 ? await WithCurrentSettings(last) : last;
            saved++;
        }
        return last;
    }

    private static async Task<SettingsResult> WithCurrentSettings(SettingsResult failed)
    {
        var fresh = await SettingsClient.LoadAsync();
        return fresh.Settings == null ? failed : failed with { Settings = fresh.Settings };
    }
}
