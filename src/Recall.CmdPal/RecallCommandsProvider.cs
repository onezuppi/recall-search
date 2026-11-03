using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Recall.CmdPal;

public sealed partial class RecallCommandsProvider : CommandProvider
{
    public const string SearchId = "RecallSearch.Search";
    public const string FallbackId = "RecallSearch.Search.Fallback";

    private readonly ICommandItem[] _commands;
    private readonly IFallbackCommandItem[] _fallbacks;
    private readonly ExtensionSettings _settings = new();

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
                Subtitle = "Find text in documents and images",
            },
        ];
        _fallbacks = [new SearchFallbackItem(FallbackId)];
    }

    public override ICommandSettings? Settings => _settings;

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override IFallbackCommandItem[]? FallbackCommands() => _fallbacks;
}
