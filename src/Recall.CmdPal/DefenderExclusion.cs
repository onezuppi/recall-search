using System.ComponentModel;
using System.Diagnostics;
using Recall.Core.Ipc;

namespace Recall.CmdPal;

/// <summary>
/// Adds or removes the agent process in the Microsoft Defender exclusions. Defender then skips real-time scanning of the files the agent
/// opens to hash them. Only the process is excluded, no folders. Windows asks the user to confirm (UAC) before anything changes.
/// </summary>
internal static class DefenderExclusion
{
    private const int CancelledByUser = 1223;

    /// <returns>A message for the user and whether the change was made.</returns>
    public static async Task<(string Message, bool Done)> ApplyAsync(bool add)
    {
        var agent = AgentPaths.ExpectedExe;
        if (add && !File.Exists(agent)) return ("The agent is not installed at " + agent, false);
        try
        {
            using var process = Process.Start(Elevated(Command(agent, add)))!;
            await process.WaitForExitAsync();
            return process.ExitCode == 0
                ? (add ? "Done. Defender skips scanning files opened by the agent." : "Done. The exclusion is removed.", true)
                : ("Windows Defender refused the change (exit code " + process.ExitCode + ").", false);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == CancelledByUser) { return ("Cancelled: Windows did not get the administrator confirmation.", false); }
        catch (Win32Exception e) { return ("Could not start PowerShell: " + e.Message, false); }
    }

    /// <summary>The PowerShell command that changes the exclusion. The path sits in single quotes, so a quote inside it is doubled.</summary>
    public static string Command(string agentPath, bool add) =>
        $"{(add ? "Add-MpPreference" : "Remove-MpPreference")} -ExclusionProcess '{agentPath.Replace("'", "''")}'";

    private static ProcessStartInfo Elevated(string command) => new("powershell.exe")
    {
        ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", command },
        UseShellExecute = true,
        Verb = "runas",
        WindowStyle = ProcessWindowStyle.Hidden,
    };
}
