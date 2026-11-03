using System.Globalization;
using Recall.Core.Ipc;

namespace Recall.CmdPal;

/// <summary>The cards of the settings page: fast folders, slow folders, exclusions and extra file types.</summary>
internal static class SettingsSpecs
{
    private static readonly Choice[] FolderLists =
    [
        new("Fast list (watched live)", IpcOps.FastList),
        new("Slow list (scanned only)", IpcOps.SlowList),
    ];

    private static readonly Choice[] Groups =
    [
        new("Text (read as plain text)", "text"),
        new("Images (read with OCR)", "images"),
        new("Office documents", "office"),
        new("PDF", "pdf"),
    ];

    public static IReadOnlyList<ListSpec> All { get; } =
    [
        Folders(
            "Fast folders",
            "Watched live: a new or changed file is searchable within seconds. Use it for folders you work in every day.",
            "No fast folders yet.", watched: true, moveButton: "Move to slow list", moveTo: IpcOps.SlowList,
            add: new AddSpec("Full folder path, for example D:\\Work", "Add folder", (path, list) => SettingsClient.AddFolderAsync(path, list),
                "Add to", FolderLists)),
        Folders(
            "Slow folders",
            "Scanned when the agent starts and by its periodic pass, not watched. Cheaper for big or rarely changing folders. Add folders in the card above.",
            "No slow folders yet.", watched: false, moveButton: "Move to fast list", moveTo: IpcOps.FastList, add: null),
        new ListSpec(
            "Exclusions",
            "Files and folders matching a pattern are never indexed. Glob examples: *.log, node_modules/, secret*.txt. "
            + "Regular expression: start with re: and it is matched case-insensitively against the full path with forward slashes, "
            + "for example re:/backup(s)?/ or re:\\.tmp$. Lookaround and backreferences are not supported.",
            "No exclusions yet.",
            s => s.Excludes.Select(p => new ListEntry(p, p.StartsWith("re:", StringComparison.Ordinal) ? $"Regex  {p[3..]}" : $"Glob  {p}")),
            new AddSpec("Pattern, for example *.log or re:/backup(s)?/", "Add exclusion", (p, _) => SettingsClient.AddExcludeAsync(p)),
            SettingsClient.RemoveExcludeAsync),
        new ListSpec(
            "File types",
            "Files with these extensions are indexed in addition to the built-in types (txt, md, pdf, docx, xlsx, pptx and common images). "
            + "Examples: log, sql, csv as Text. The group decides how the file is read and must be enabled in the agent configuration.",
            "No extra file types yet.",
            s => (s.FileTypes ?? []).Select(t => new ListEntry(t.Extension, $"{t.Extension}  as {t.Group}")),
            new AddSpec("Extension, for example log or .sql", "Add file type", (ext, group) => SettingsClient.AddTypeAsync(ext, group),
                "File group", Groups),
            SettingsClient.RemoveTypeAsync),
        new ListSpec(
            "Semantic search threshold",
            "Files whose meaning is less similar to the query than this number are hidden. Raise it for fewer, surer results, "
            + "lower it for more. The value is a cosine similarity from 0 to 1 and depends on the model.",
            "Semantic search is off. Set \"embeddingModel\" in config.json to turn it on.",
            s => s.MinSimilarity is { } v ? [new ListEntry("", $"Current threshold  {v.ToString("0.00", CultureInfo.InvariantCulture)}")] : [],
            new AddSpec("New threshold, for example 0.84", "Save threshold", (value, _) => SettingsClient.SetSimilarityAsync(value)),
            null),
    ];

    private static ListSpec Folders(string title, string help, string empty, bool watched, string moveButton, string moveTo, AddSpec? add) => new(
        title, help, empty,
        s => s.Roots.Where(r => r.Watched == watched).Select(r => new ListEntry(r.Path, r.Path)),
        add,
        SettingsClient.RemoveFolderAsync,
        [new RowAction(moveButton, path => SettingsClient.MoveFolderAsync(path, moveTo))]);
}
