using System.Globalization;
using Recall.Core.Ignore;
using Recall.Core.Ipc;

namespace Recall.CmdPal;

/// <summary>The cards of the settings page: fast folders, slow folders, exclusions, extra file types and the semantic threshold.</summary>
internal static class SettingsSpecs
{
    private const string DefaultGroup = "text";

    public static IReadOnlyList<ListSpec> All { get; } =
    [
        Folders("Fast folders",
            "One folder per line. Watched live: a new or changed file is searchable within seconds. Use it for folders you work in every day.",
            watched: true),
        Folders("Slow folders",
            "One folder per line. Scanned when the agent starts and by its periodic pass, not watched. Cheaper for big or rarely changing folders. "
            + "To move a folder between the lists, delete its line in one box and save, then add it to the other box and save. "
            + "A folder already in the other list is moved by adding it here. A fast folder may lie inside a slow one (and the other way round): "
            + "the inner list owns that subfolder.",
            watched: false),
        new ListSpec(
            "Exclusions",
            "One pattern per line. Files and folders matching a pattern are never indexed. Glob: *.log, node_modules/, secret*.txt. "
            + "Regular expression: start with re: and it is matched case-insensitively against the full path with forward slashes, "
            + "for example re:/backup(s)?/ or re:\\.tmp$. Lookaround and backreferences are not supported.",
            "*.log\nnode_modules/\nre:/backup(s)?/",
            s => s.Excludes,
            c => Edit(c, SettingsClient.AddExcludeAsync, SettingsClient.RemoveExcludeAsync),
            Comparer: StringComparer.Ordinal,
            Builtin: "Folders never entered: " + string.Join(", ", BuiltInRules.DescribeSkippedFolders()) + ".\n\n"
                + "Files that may hold secrets: " + string.Join(", ", BuiltInRules.DescribeSecrets()) + ".\n\n"
                + "System places: Windows, Program Files, ProgramData, AppData, Temp, the Recycle Bin and the profiles of other users.\n\n"
                + "Everything that the .gitignore and .recallignore files inside a folder list. Links and cloud-only files are skipped too."),
        new ListSpec(
            "File types",
            "One type per line as \"extension group\". Files with these extensions are indexed in addition to the built-in types "
            + "(txt, md, pdf, docx, xlsx, pptx and common images). Groups: text (plain text), images (OCR), office, pdf. "
            + "Without a group the file is read as text. The group must be enabled in the agent configuration.",
            "log text\nsql text\ncsv",
            s => (s.FileTypes ?? []).Select(t => $"{t.Extension} {t.Group}"),
            ApplyTypes,
            Normalize: FileType,
            Builtin: string.Join("\n\n", BuiltInRules.DescribeTypes())),
        new ListSpec(
            "Semantic search threshold",
            "Files whose meaning is less similar to the query than this number are hidden. Raise it for fewer, surer results, "
            + "lower it for more. A cosine similarity from 0 to 1 that depends on the model. Empty while semantic search is off: "
            + "set \"embeddingModel\" in config.json to turn it on.",
            "0.84",
            s => s.MinSimilarity is { } v ? [v.ToString("0.00", CultureInfo.InvariantCulture)] : [],
            c => c.Wanted.Count > 0 ? SettingsClient.SetSimilarityAsync(c.Wanted[0]) : Task.FromResult(new SettingsResult(null, "Enter a number between 0 and 1")),
            Multiline: false),
    ];

    private static ListSpec Folders(string title, string help, bool watched) => new(
        title, help, "D:\\Work\nC:\\Users\\me\\Documents",
        s => s.Roots.Where(r => r.Watched == watched).Select(r => r.Path),
        c => ApplyFolders(c, watched),
        Normalize: line => Path.TrimEndingDirectorySeparator(line.Trim('"', ' ')));

    /// <summary>A folder already in the other list is moved; any other new folder is added. Removals go first.</summary>
    private static async Task<SettingsResult> ApplyFolders(ListChange c, bool watched)
    {
        var list = watched ? IpcOps.FastList : IpcOps.SlowList;
        var other = new HashSet<string>(
            c.Current.Roots.Where(r => r.Watched != watched).Select(r => Path.TrimEndingDirectorySeparator(r.Path)), StringComparer.OrdinalIgnoreCase);
        var calls = c.Removed.Select<string, Func<Task<SettingsResult>>>(p => () => SettingsClient.RemoveFolderAsync(p))
            .Concat(c.Added.Select<string, Func<Task<SettingsResult>>>(p =>
                other.Contains(p) ? () => SettingsClient.MoveFolderAsync(p, list) : () => SettingsClient.AddFolderAsync(p, list)));
        return await SettingsBatch.RunAsync(calls) ?? await SettingsClient.LoadAsync();
    }

    private static async Task<SettingsResult> Edit(ListChange c, Func<string, Task<SettingsResult>> add, Func<string, Task<SettingsResult>> remove)
    {
        var calls = c.Removed.Select<string, Func<Task<SettingsResult>>>(p => () => remove(p))
            .Concat(c.Added.Select<string, Func<Task<SettingsResult>>>(p => () => add(p)));
        return await SettingsBatch.RunAsync(calls) ?? await SettingsClient.LoadAsync();
    }

    /// <summary>A changed group is one new line for the same extension; only extensions that left the list are removed.</summary>
    private static async Task<SettingsResult> ApplyTypes(ListChange c)
    {
        var kept = new HashSet<string>(c.Wanted.Select(Extension), StringComparer.Ordinal);
        var calls = c.Removed.Where(l => !kept.Contains(Extension(l))).Select<string, Func<Task<SettingsResult>>>(l => () => SettingsClient.RemoveTypeAsync(Extension(l)))
            .Concat(c.Added.Select<string, Func<Task<SettingsResult>>>(l => () => SettingsClient.AddTypeAsync(Extension(l), Group(l))));
        return await SettingsBatch.RunAsync(calls) ?? await SettingsClient.LoadAsync();
    }

    private static string Extension(string line) => line.Split(' ', 2)[0];

    private static string Group(string line) => line.Split(' ', 2) is { Length: 2 } p ? p[1] : DefaultGroup;

    /// <summary>"log", ".LOG text" and "log   text" all become ".log text".</summary>
    private static string FileType(string line)
    {
        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "";
        var ext = "." + parts[0].TrimStart('.').ToLowerInvariant();
        return $"{ext} {(parts.Length > 1 ? parts[1].ToLowerInvariant() : DefaultGroup)}";
    }
}
