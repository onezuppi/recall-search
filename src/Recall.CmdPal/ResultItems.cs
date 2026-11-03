using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Recall.Core.Ipc;

namespace Recall.CmdPal;

/// <summary>Builds list items for hits and for the states that replace them.</summary>
internal static class ResultItems
{
    public static IListItem Hint() => new ListItem(new NoOpCommand())
    {
        Title = "Type a few characters you remember",
        Subtitle = "Filters: in:folder  type:pdf  after:2026-05  before:2026  \"exact phrase\"",
    };

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
        _ => outcome.Hits.Select(Hit).ToArray(),
    };

    private static IListItem Hit(IpcHit hit)
    {
        var folder = Path.GetDirectoryName(hit.Path) ?? "";
        var parts = new List<string> { folder, hit.Modified.ToLocalTime().ToString("g") };
        if (hit.Locator.Length > 0) parts.Add(hit.Locator);
        return new ListItem(new OpenFileCommand(hit.Path))
        {
            Title = hit.Snippet,
            Subtitle = string.Join(" · ", parts),
            Details = new Details
            {
                Title = Path.GetFileName(hit.Path),
                Body = hit.Snippet,
                Metadata =
                [
                    Meta("Folder", folder),
                    Meta("Size", FormatSize(hit.Size)),
                    Meta("Modified", hit.Modified.ToLocalTime().ToString("g")),
                    Meta("Where", hit.Locator.Length > 0 ? hit.Locator : "whole file"),
                ],
            },
            MoreCommands =
            [
                new CommandContextItem(new ShowInFolderCommand(hit.Path)),
                new CommandContextItem(new CopyTextCommand(hit.Path) { Name = "Copy path" }),
                new CommandContextItem(new CopyTextCommand(hit.Snippet) { Name = "Copy text" }),
            ],
        };
    }

    private static DetailsElement Meta(string key, string value) =>
        new() { Key = key, Data = new DetailsLink { Text = value } };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1048576.0:0.#} MB",
    };
}
