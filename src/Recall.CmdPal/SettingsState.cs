using Recall.Core.Ipc;

namespace Recall.CmdPal;

/// <summary>The agent's settings as last seen by the extension, shared by the forms of the settings page.</summary>
internal sealed class SettingsState
{
    public IpcSettings? Current { get; private set; }

    public string? Error { get; private set; }

    public event Action? Changed;

    public async Task ReloadAsync() => Apply(await SettingsClient.LoadAsync());

    public void Apply(SettingsResult result)
    {
        Current = result.Settings ?? Current;
        Error = result.Error;
        Changed?.Invoke();
    }
}
