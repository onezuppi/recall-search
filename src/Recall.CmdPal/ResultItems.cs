using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Recall.Core.Search;

namespace Recall.CmdPal;

/// <summary>Builds list items for hits and for the states that replace them.</summary>
internal static class ResultItems
{
    public static IListItem Hint() => new ListItem(new NoOpCommand())
    {
        Title = "Type a few characters you remember",
        Subtitle = "Filters: in:folder  type:pdf  after:2026-05  before:2026  \"exact phrase\"",
    };

    private static IListItem[] Hits(SearchOutcome outcome, string query)
    {
        var parsed = QueryParser.Parse(query);
        var terms = parsed.Fragments.Concat(parsed.Phrases).ToList();
        return outcome.Hits.Select(h => HitItem.Create(h, terms)).ToArray();
    }

    public static IListItem[] From(SearchOutcome outcome, string query) => outcome.State switch
    {
        SearchState.AgentNotRunning => [new ListItem(new StartAgentCommand())
        {
            Title = "Recall Search agent is not running",
            Subtitle = "Press Enter to start it",
        }],
        SearchState.UntrustedServer => [new ListItem(new NoOpCommand())
        {
            Title = "Refusing to talk to an unverified process",
            Subtitle = outcome.Message ?? "The pipe is not owned by the Recall Search agent.",
        }],
        SearchState.Failed => [new ListItem(new NoOpCommand())
        {
            Title = "Search failed",
            Subtitle = outcome.Message ?? "The agent returned an error.",
        }],
        _ when outcome.Hits.Count == 0 => [new ListItem(new NoOpCommand())
        {
            Title = $"Nothing found for \"{query}\"",
            Subtitle = "Files indexed so far are searched. A new file can take a few seconds to appear.",
        }],
        _ => Hits(outcome, query),
    };
}
