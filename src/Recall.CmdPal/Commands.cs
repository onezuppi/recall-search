using System.Diagnostics;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Recall.Core.Ipc;

namespace Recall.CmdPal;

internal static class Icons
{
    public static IconInfo Logo => IconHelpers.FromRelativePath("Assets\\RecallIcon.png");
}

/// <summary>Opens a result with its default app. Paths come from the verified agent but are still checked before the shell sees them.</summary>
internal sealed partial class OpenFileCommand : InvokableCommand
{
    private readonly string _path;

    public OpenFileCommand(string path)
    {
        _path = path;
        Name = "Open";
        Icon = new IconInfo("");
    }

    public override ICommandResult Invoke() => ShellActions.Run(_path, () =>
        Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true }));
}

internal sealed partial class ShowInFolderCommand : InvokableCommand
{
    private readonly string _path;

    public ShowInFolderCommand(string path)
    {
        _path = path;
        Name = "Show in folder";
        Icon = new IconInfo("");
    }

    public override ICommandResult Invoke() => ShellActions.Run(_path, () =>
        Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{_path}\"" }));
}

internal sealed partial class StartAgentCommand : InvokableCommand
{
    public StartAgentCommand()
    {
        Name = "Start Recall Search agent";
        Icon = new IconInfo("");
    }

    public override ICommandResult Invoke()
    {
        var exe = AgentPaths.ExpectedExe;
        if (!File.Exists(exe)) return CommandResult.ShowToast($"Agent not found at {exe}");
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true });
            return CommandResult.ShowToast("Recall Search agent started");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return CommandResult.ShowToast("Could not start the agent: " + e.Message);
        }
    }
}

internal static class ShellActions
{
    public static ICommandResult Run(string path, Func<Process?> action)
    {
        if (!Path.IsPathRooted(path) || !File.Exists(path)) return CommandResult.ShowToast("The file no longer exists.");
        try
        {
            action()?.Dispose();
            return CommandResult.Dismiss();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return CommandResult.ShowToast("Could not open the file: " + e.Message);
        }
    }
}
