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
/// <param name="foregroundProcess">Overrides the name of the process that owns the active window (tests).</param>
public sealed class WindowsActivityMonitor(
    IEnumerable<string> processNames, Func<int?>? queryState = null, Func<IEnumerable<string>>? runningProcesses = null,
    Func<string?>? foregroundProcess = null, Func<bool?>? foregroundCoversMonitor = null, Func<bool?>? foregroundMaximized = null) : IActivityMonitor
{
    private readonly Func<bool?> _coversMonitor = foregroundCoversMonitor ?? ForegroundCoversMonitor;
    private readonly Func<bool?> _maximized = foregroundMaximized ?? ForegroundMaximized;
    private readonly HashSet<string> _names = new(processNames.Select(Normalize), StringComparer.OrdinalIgnoreCase);
    private readonly Func<int?> _queryState = queryState ?? QueryShell;
    private readonly Func<IEnumerable<string>> _running = runningProcesses ?? RunningProcessNames;
    private readonly Func<string?> _foreground = foregroundProcess ?? ForegroundProcessName;

    /// <summary>
    /// Windows reports "busy" (state 2) while these are the active window although nothing is full screen: measured with Yandex Browser
    /// (process "browser") in a normal window, which paused indexing for minutes at a time. They are never games or presentations.
    /// </summary>
    private static readonly HashSet<string> BusyFalsePositives = new(StringComparer.OrdinalIgnoreCase)
    {
        "browser", "chrome", "msedge", "firefox", "opera", "brave", "vivaldi",
        // Windows shell windows: measured "busy" with the desktop, the file manager and the search box in front.
        "ShareX", // its region capture overlay covers the screen
        "explorer", "SearchHost", "SearchApp", "StartMenuExperienceHost", "ShellExperienceHost", "ApplicationFrameHost",
    };

    public string? BusyReason()
    {
        var state = _queryState();
        var active = state is null or 5 ? null : _foreground();
        // "Busy" is also reported for an ordinary maximized window (measured: browser, explorer, ChatGPT). A real full screen
        // window covers the whole monitor, taskbar included; one that does not is not a game or a video in full screen.
        var falseBusy = (state is QunsBusy or QunsApp && active != null && BusyFalsePositives.Contains(Normalize(active)))
            // A maximized window is the user's ordinary work, also when it covers the monitor (taskbar hidden, frameless apps such as ChatGPT).
            || (state == QunsBusy && (_maximized() == true || _coversMonitor() == false));
        var reason = falseBusy ? null : Classify(state);
        // The name of the window owner goes into the log: it tells which app Windows treats as full screen.
        if (reason != null) return active == null ? reason : $"{reason}, active window: {active}";
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

    private const int QunsBusy = 2, QunsApp = 7;

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

    /// <summary>Whether the active window fills its whole monitor. Null when it cannot be told.</summary>
    private static bool? ForegroundCoversMonitor()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !GetWindowRect(window, out var w)) return null;
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
        var m = info.Monitor;
        return w.Left <= m.Left && w.Top <= m.Top && w.Right >= m.Right && w.Bottom >= m.Bottom;
    }

    private static bool? ForegroundMaximized()
    {
        var window = GetForegroundWindow();
        return window == IntPtr.Zero ? null : IsZoomed(window);
    }

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr window);

    private const int MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public int Flags; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
