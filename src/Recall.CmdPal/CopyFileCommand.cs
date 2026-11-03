using System.Diagnostics;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Recall.CmdPal;

/// <summary>
/// Puts the file itself on the clipboard, so it can be pasted into Explorer or a messenger.
/// The extension runs in the background without a window, so the clipboard is filled by a short PowerShell process.
/// The path travels in an environment variable and never becomes part of the command text.
/// </summary>
internal sealed partial class CopyFileCommand : InvokableCommand
{
    private const string PathVariable = "RECALL_COPY_PATH";
    private const string Script = "Set-Clipboard -LiteralPath $env:" + PathVariable;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _path;

    public CopyFileCommand(string path)
    {
        _path = path;
        Name = "Copy file";
        Icon = new IconInfo("");
    }

    public override ICommandResult Invoke()
    {
        if (!Path.IsPathRooted(_path) || !File.Exists(_path)) return CommandResult.ShowToast("The file no longer exists.");
        try
        {
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-STA", "-Command", Script }) start.ArgumentList.Add(arg);
            start.Environment[PathVariable] = _path;
            using var process = Process.Start(start);
            if (process is null || !process.WaitForExit(Timeout) || process.ExitCode != 0)
                return CommandResult.ShowToast("Could not copy the file.");
            return CommandResult.ShowToast("File copied to the clipboard");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return CommandResult.ShowToast("Could not copy the file: " + e.Message);
        }
    }
}
