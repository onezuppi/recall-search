using Recall.Core.Ipc;

namespace Recall.CmdPal;

/// <param name="Settings">The agent's settings after the call. Null when the call failed.</param>
internal sealed record SettingsResult(IpcSettings? Settings, string? Error)
{
    public bool Ok => Error == null;
}

/// <summary>Reads and changes folders, exclusions and file types through the agent. The agent validates and saves them.</summary>
internal static class SettingsClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public static Task<SettingsResult> LoadAsync() => CallAsync(IpcOps.Settings, null);

    /// <param name="list"><see cref="IpcOps.FastList"/> or <see cref="IpcOps.SlowList"/>.</param>
    public static Task<SettingsResult> AddFolderAsync(string path, string list) => CallAsync(IpcOps.AddRoot, path.Trim().Trim('"'), list);

    public static Task<SettingsResult> RemoveFolderAsync(string path) => CallAsync(IpcOps.RemoveRoot, path);

    public static Task<SettingsResult> MoveFolderAsync(string path, string list) => CallAsync(IpcOps.SetWatched, path, list);

    public static Task<SettingsResult> AddExcludeAsync(string pattern) => CallAsync(IpcOps.AddExclude, pattern);

    public static Task<SettingsResult> RemoveExcludeAsync(string pattern) => CallAsync(IpcOps.RemoveExclude, pattern);

    public static Task<SettingsResult> SetSimilarityAsync(string value) => CallAsync(IpcOps.SetSimilarity, value);

    public static Task<SettingsResult> SetSlowScanAsync(string time) => CallAsync(IpcOps.SetSlowScan, time);

    public static Task<SettingsResult> SetEmbedIntervalAsync(string minutes) => CallAsync(IpcOps.SetEmbedInterval, minutes);

    public static Task<SettingsResult> SetDeviceAsync(string device) => CallAsync(IpcOps.SetDevice, device);

    public static Task<SettingsResult> AddTypeAsync(string extension, string group) => CallAsync(IpcOps.AddType, extension, group);

    public static Task<SettingsResult> RemoveTypeAsync(string extension) => CallAsync(IpcOps.RemoveType, extension);

    private static async Task<SettingsResult> CallAsync(string op, string? value, string? target = null)
    {
        if (!PipeClient.IsListening()) return new SettingsResult(null, "The Recall Search agent is not running.");
        try
        {
            var response = await PipeClient.SendAsync(new IpcRequest(op, Value: value, Target: target), RequestTimeout, default);
            return response.Ok && response.Settings != null
                ? new SettingsResult(response.Settings, null)
                : new SettingsResult(null, Capitalize(response.Error ?? "The agent returned an error."));
        }
        catch (UntrustedServerException e) { return new SettingsResult(null, "Refusing to talk to an unverified process: " + e.Message); }
        catch (UnauthorizedAccessException) { return new SettingsResult(null, "The pipe belongs to another account."); }
        catch (TimeoutException) { return new SettingsResult(null, "The Recall Search agent is not running."); }
        catch (IOException e) { return new SettingsResult(null, e.Message); }
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
