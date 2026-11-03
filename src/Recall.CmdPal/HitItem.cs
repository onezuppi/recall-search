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
        item.Tags = Tags(hit, terms);
        return item;
    }

    /// <summary>The hero slot draws icon-sized pictures, so a picture goes into the markdown body as a pane-sized thumbnail.</summary>
    private static string Body(IpcHit hit, IReadOnlyList<string> terms, string? thumbnail)
    {
        var text = MarkdownHighlight.Apply(hit.Context.Length > 0 ? hit.Context : hit.Snippet, terms);
        return thumbnail is null ? text : $"![]({new Uri(thumbnail).AbsoluteUri})\n\n{text}";
    }

    /// <summary>A colored badge with the searched word sits at the row's right edge, which a narrow row never cuts off.</summary>
    private static ITag[] Tags(IpcHit hit, IReadOnlyList<string> terms)
    {
        var tags = new List<ITag>();
        var term = terms.FirstOrDefault(t => t.Length > 0 && hit.Snippet.Contains(t, StringComparison.OrdinalIgnoreCase));
        if (term is not null)
            tags.Add(new Tag(term) { Background = ColorHelpers.FromRgb(255, 214, 10), Foreground = ColorHelpers.FromRgb(20, 20, 20) });
        if (hit.Locator.Length > 0) tags.Add(new Tag(hit.Locator));
        return tags.ToArray();
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
