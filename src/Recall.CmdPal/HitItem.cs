using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Recall.Core.Ipc;
using Recall.Core.Search;

namespace Recall.CmdPal;

/// <summary>One search hit as a list row plus a details pane: file name, highlighted context, and the picture itself for images.</summary>
internal static class HitItem
{
    public static IListItem Create(IpcHit hit, IReadOnlyList<string> terms, string? thumbnail)
    {
        var folder = Path.GetDirectoryName(hit.Path) ?? "";
        var modified = hit.Modified.ToLocalTime().ToString("g");
        var item = new ListItem(new OpenFileCommand(hit.Path))
        {
            Title = Path.GetFileName(hit.Path),
            Subtitle = hit.Snippet,
            Icon = new IconInfo(hit.Path),
            Details = new Details
            {
                Title = Path.GetFileName(hit.Path),
                Body = Body(hit, terms, thumbnail),
                Metadata =
                [
                    Meta("Folder", folder),
                    Meta("Size", FormatSize(hit.Size)),
                    Meta("Modified", modified),
                    Meta("Where", hit.Locator.Length > 0 ? hit.Locator : "whole file"),
                ],
            },
            MoreCommands =
            [
                new CommandContextItem(new ShowInFolderCommand(hit.Path)),
                new CommandContextItem(new CopyTextCommand(hit.Path) { Name = "Copy path" }),
                new CommandContextItem(new CopyTextCommand(hit.Context.Length > 0 ? hit.Context : hit.Snippet) { Name = "Copy text" }),
            ],
        };
        if (hit.Locator.Length > 0) item.Tags = [new Tag(hit.Locator)];
        return item;
    }

    /// <summary>The hero slot draws icon-sized pictures, so a picture goes into the markdown body as a pane-sized thumbnail.</summary>
    private static string Body(IpcHit hit, IReadOnlyList<string> terms, string? thumbnail)
    {
        var source = hit.Context.Length > 0 ? hit.Context : hit.Snippet;
        var parts = new List<string>();
        if (thumbnail is not null) parts.Add($"![]({new Uri(thumbnail).AbsoluteUri})");
        var headline = MarkdownHighlight.Headline(source, terms);
        if (headline.Length > 0) parts.Add(headline);
        parts.Add(MarkdownHighlight.Apply(source, terms));
        return string.Join("\n\n", parts);
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
