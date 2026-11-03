using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Recall.Core.Activity;

/// <summary>
/// Busy when the shell reports a full screen app, a presentation or a Direct3D game,
/// or when one of the configured processes is running.
/// </summary>
/// <param name="processNames">Process names with or without ".exe", compared case-insensitively.</param>
/// <param name="queryState">Overrides the shell query (tests). Returns a QUERY_USER_NOTIFICATION_STATE value or null when unknown.</param>
/// <param name="runningProcesses">Overrides process enumeration (tests).</param>
public sealed class WindowsActivityMonitor(
    IEnumerable<string> processNames, Func<int?>? queryState = null, Func<IEnumerable<string>>? runningProcesses = null) : IActivityMonitor
{
    private readonly HashSet<string> _names = new(processNames.Select(Normalize), StringComparer.OrdinalIgnoreCase);
    private readonly Func<int?> _queryState = queryState ?? QueryShell;
    private readonly Func<IEnumerable<string>> _running = runningProcesses ?? RunningProcessNames;

    public string? BusyReason()
    {
        var reason = Classify(_queryState());
        if (reason != null) return reason;
        if (_names.Count == 0) return null;
        var hit = _running().Select(Normalize).FirstOrDefault(_names.Contains);
        return hit == null ? null : $"process {hit}";
    }

    /// <summary>Maps a QUERY_USER_NOTIFICATION_STATE value to a busy reason; null means the user can be disturbed.</summary>
    public static string? Classify(int? state) => state switch
    {
        2 => "full screen app",          // QUNS_BUSY
        3 => "full screen game",         // QUNS_RUNNING_D3D_FULL_SCREEN
        4 => "presentation mode",        // QUNS_PRESENTATION_MODE
        7 => "full screen store app",    // QUNS_APP
        _ => null,                       // not present, accepts notifications, quiet time, unknown
    };

    private static string Normalize(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static int? QueryShell() => SHQueryUserNotificationState(out var s) == 0 ? s : null;

    private static IEnumerable<string> RunningProcessNames()
    {
        foreach (var p in Process.GetProcesses())
        {
            string name;
            try { name = p.ProcessName; }
            catch (Exception) { continue; }
            finally { p.Dispose(); }
            yield return name;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
