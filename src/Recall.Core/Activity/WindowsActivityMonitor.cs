using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Recall.Core.Activity;

/// <summary>
/// Busy while Windows says a Direct3D game runs in full screen or the presentation settings are on, or while one of the configured processes runs.
/// The other "busy" answers are ignored on purpose: <c>QUNS_BUSY</c> (2) and <c>QUNS_APP</c> (7) also come for ordinary maximized windows,
/// the desktop, the search box and screenshot overlays (measured: the agent was paused about 50 minutes a day by them). The agent runs at a lower
/// priority instead, so a game or the user's work gets the processor first anyway.
/// </summary>
/// <param name="processNames">Process names with or without ".exe", compared case-insensitively.</param>
/// <param name="queryState">Overrides the shell query (tests). Returns a QUERY_USER_NOTIFICATION_STATE value or null when unknown.</param>
/// <param name="runningProcesses">Overrides process enumeration (tests).</param>
/// <param name="foregroundProcess">Overrides the name of the process that owns the active window (tests).</param>
public sealed class WindowsActivityMonitor(
    IEnumerable<string> processNames, Func<int?>? queryState = null, Func<IEnumerable<string>>? runningProcesses = null,
    Func<string?>? foregroundProcess = null) : IActivityMonitor
{
    private readonly HashSet<string> _names = new(processNames.Select(Normalize), StringComparer.OrdinalIgnoreCase);
    private readonly Func<int?> _queryState = queryState ?? QueryShell;
    private readonly Func<IEnumerable<string>> _running = runningProcesses ?? RunningProcessNames;
    private readonly Func<string?> _foreground = foregroundProcess ?? ForegroundProcessName;

    public string? BusyReason()
    {
        var state = _queryState();
        var reason = Classify(state);
        // The name of the window owner goes into the log: it tells which game Windows saw.
        if (reason != null) return _foreground() is { } active ? $"{reason}, active window: {active}" : reason;
        if (_names.Count == 0) return null;
        var hit = _running().Select(Normalize).FirstOrDefault(_names.Contains);
        return hit == null ? null : $"process {hit}";
    }

    /// <summary>Maps a QUERY_USER_NOTIFICATION_STATE value to a busy reason; null means the agent may work.</summary>
    public static string? Classify(int? state) => state switch
    {
        3 => "full screen game",         // QUNS_RUNNING_D3D_FULL_SCREEN
        4 => "presentation mode",        // QUNS_PRESENTATION_MODE
        _ => null,                       // 2 busy and 7 app: unreliable, see the class comment; 1 not present, 5 accepts notifications, 6 quiet time
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

    private static string? ForegroundProcessName()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var pid) == 0) return null;
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch (Exception) { return null; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
