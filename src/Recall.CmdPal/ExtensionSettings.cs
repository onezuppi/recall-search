using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Recall.CmdPal;

/// <summary>The "Extension settings" section on the extension's page in Command Palette settings: fast and slow folders, exclusions and file types.</summary>
internal sealed partial class ExtensionSettings : ICommandSettings
{
    public IContentPage SettingsPage { get; } = new SettingsContentPage();
}

internal sealed partial class SettingsContentPage : ContentPage
{
    private readonly SettingsState _state = new();
    private readonly IContent[] _forms;

    public SettingsContentPage()
    {
        Id = "RecallSearch.Settings";
        Title = "Recall Search settings";
        Icon = Icons.Logo;
        _forms = [.. SettingsSpecs.All.Select(spec => (IContent)new SettingsForm(_state, spec)), new DefenderForm()];
        _ = _state.ReloadAsync();
    }

    public override IContent[] GetContent() => _forms;
}
