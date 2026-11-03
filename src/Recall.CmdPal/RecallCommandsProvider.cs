using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Recall.CmdPal;

public sealed partial class RecallCommandsProvider : CommandProvider
{
    public const string SearchId = "RecallSearch.Search";

    private readonly ICommandItem[] _commands;

    public RecallCommandsProvider()
    {
        Id = "RecallSearch";
        DisplayName = "Recall Search";
        Icon = Icons.Logo;
        _commands =
        [
            new CommandItem(new SearchPage(new SearchSession()) { Id = SearchId })
            {
                Title = "Recall Search",
                Icon = Icons.Logo,
                Subtitle = "Find text in documents and images",
            },
        ];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;
}
