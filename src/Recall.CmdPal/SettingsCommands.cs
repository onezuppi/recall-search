using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Recall.CmdPal;

/// <summary>Sends one settings change to the agent, shows its verdict and asks the page to reload.</summary>
internal sealed partial class SettingsChangeCommand : InvokableCommand
{
    private readonly Func<Task<SettingsResult>> _call;
    private readonly string _done;
    private readonly Action _changed;

    public SettingsChangeCommand(string name, string icon, string done, Func<Task<SettingsResult>> call, Action changed)
    {
        Name = name;
        Icon = new IconInfo(icon);
        _done = done;
        _call = call;
        _changed = changed;
    }

    public override ICommandResult Invoke()
    {
        SettingsResult result;
        try { result = _call().GetAwaiter().GetResult(); }
        catch (Exception e) when (e is not OperationCanceledException) { result = new SettingsResult(null, e.Message); }
        if (result.Ok) _changed();
        return CommandResult.ShowToast(result.Error ?? _done);
    }
}
