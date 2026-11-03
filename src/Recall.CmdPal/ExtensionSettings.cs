using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Recall.CmdPal;

/// <summary>The "Extension settings" section on the extension's page in Command Palette settings: indexed folders and exclusions.</summary>
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
        _forms =
        [
            new SettingsForm(_state, new ListSpec(
                "Indexed folders", "No folders yet. Add one below.", "Full path, for example D:\\Documents", "Add folder",
                s => s.Roots.Select(r => (r.Path, $"{r.Path} ({(r.Watched ? "watched for changes" : "scanned at start")})")),
                SettingsClient.AddFolderAsync, SettingsClient.RemoveFolderAsync)),
            new SettingsForm(_state, new ListSpec(
                "Exclusions", "No exclusions yet.", "Pattern, for example *.log, node_modules/, secret*.txt", "Add exclusion",
                s => s.Excludes.Select(p => (p, p)),
                SettingsClient.AddExcludeAsync, SettingsClient.RemoveExcludeAsync)),
        ];
        _ = _state.ReloadAsync();
    }

    public override IContent[] GetContent() => _forms;
}
