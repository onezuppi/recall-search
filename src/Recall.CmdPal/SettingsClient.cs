using Recall.Core.Ipc;

namespace Recall.CmdPal;

/// <param name="Settings">The agent's settings after the call. Null when the call failed.</param>
internal sealed record SettingsResult(IpcSettings? Settings, string? Error)
{
    public bool Ok => Error == null;
}

/// <summary>Reads and changes the watched folders and exclude patterns through the agent. The agent validates and saves them.</summary>
internal static class SettingsClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public static Task<SettingsResult> LoadAsync() => CallAsync(IpcOps.Settings, null);

    public static Task<SettingsResult> AddFolderAsync(string path) => CallAsync(IpcOps.AddRoot, path.Trim().Trim('"'));

    public static Task<SettingsResult> RemoveFolderAsync(string path) => CallAsync(IpcOps.RemoveRoot, path);

    public static Task<SettingsResult> AddExcludeAsync(string pattern) => CallAsync(IpcOps.AddExclude, pattern);

    public static Task<SettingsResult> RemoveExcludeAsync(string pattern) => CallAsync(IpcOps.RemoveExclude, pattern);

    private static async Task<SettingsResult> CallAsync(string op, string? value)
    {
        if (!PipeClient.IsListening()) return new SettingsResult(null, "The Recall Search agent is not running.");
        try
        {
            var response = await PipeClient.SendAsync(new IpcRequest(op, Value: value), RequestTimeout, default);
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
