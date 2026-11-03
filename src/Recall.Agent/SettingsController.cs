using System.Globalization;
using Recall.Core.Config;
using Recall.Core.Embed;
using Recall.Core.Ignore;
using Recall.Core.Ipc;

namespace Recall.Agent;

/// <summary>What the agent must do after a settings change was validated and saved.</summary>
public interface ISettingsEffects
{
    void RootAdded(RootFolder root);
    void RootRemoved(RootFolder root);

    /// <summary>A folder moved between the fast (watched) and the slow (scanned only) list.</summary>
    void RootWatchChanged(RootFolder root);

    void ExcludesChanged(IReadOnlyList<string> patterns);
    void FileTypesChanged(IReadOnlyDictionary<string, string> types);
    void SimilarityChanged(float minSimilarity);
}

/// <summary>
/// Validates changes to folders, exclusions and file types, saves them to config.json and tells the agent to apply them.
/// The agent owns the file: the Command Palette extension runs in an MSIX package and must not write it.
/// </summary>
public sealed class SettingsController(
    RecallConfig initial, string? configPath, Func<string, bool>? isSystemPath, ISettingsEffects effects)
{
    private readonly object _gate = new();
    private readonly SettingsValidator _validate = new(isSystemPath ?? BuiltInRules.IsSystemPath);
    private RecallConfig _config = initial;

    /// <summary>The outcome of an edit: the new configuration, or the reason it was refused.</summary>
    private readonly record struct Edit(RecallConfig? Next, string? Error = null, Action? After = null)
    {
        public static Edit Refuse(string error) => new(null, error);
    }

    public RecallConfig Config { get { lock (_gate) return _config; } }

    public bool IsConfigured(string rootPath) => Config.Roots.Any(r => SettingsValidator.SamePath(r.Path, rootPath));

    public IpcSettings Snapshot()
    {
        var c = Config;
        return new IpcSettings(
            [.. c.Roots.Select(r => new IpcRoot(r.Path, r.Watched))],
            [.. c.GlobalIgnorePatterns],
            [.. c.CustomFileTypes.OrderBy(t => t.Key).Select(t => new IpcFileType(t.Key, t.Value))],
            MinSimilarity(c));
    }

    public IpcResponse Handle(IpcRequest r) => r.Op switch
    {
        IpcOps.Settings => Ok(),
        IpcOps.AddRoot => Change(c => AddRoot(c, r.Value, r.Target)),
        IpcOps.RemoveRoot => Change(c => RemoveRoot(c, r.Value)),
        IpcOps.SetWatched => Change(c => SetWatched(c, r.Value, r.Target)),
        IpcOps.AddExclude => Change(c => AddExclude(c, r.Value?.Trim() ?? "")),
        IpcOps.RemoveExclude => Change(c => RemoveExclude(c, r.Value)),
        IpcOps.AddType => Change(c => AddType(c, r.Value, r.Target)),
        IpcOps.RemoveType => Change(c => RemoveType(c, r.Value)),
        IpcOps.SetSimilarity => Change(c => SetSimilarity(c, r.Value)),
        _ => new IpcResponse(false, "unknown operation"),
    };

    private IpcResponse Ok() => new(true, Settings: Snapshot());

    private Edit AddRoot(RecallConfig c, string? value, string? list)
    {
        if (ParseList(list ?? IpcOps.FastList) is not { } watched) return Edit.Refuse(ListError);
        var error = _validate.Root(c, value, out var full);
        if (error != null) return Edit.Refuse(error);
        var root = new RootFolder(full, watched);
        return new Edit(c with { Roots = [.. c.Roots, root] }, After: () => effects.RootAdded(root));
    }

    private Edit RemoveRoot(RecallConfig c, string? value)
    {
        var root = c.Roots.FirstOrDefault(r => SettingsValidator.SamePath(r.Path, value));
        if (root == null) return Edit.Refuse("Folder is not in the list");
        return new Edit(c with { Roots = [.. c.Roots.Where(r => r != root)] }, After: () => effects.RootRemoved(root));
    }

    private Edit SetWatched(RecallConfig c, string? value, string? list)
    {
        if (ParseList(list) is not { } watched) return Edit.Refuse(ListError);
        var root = c.Roots.FirstOrDefault(r => SettingsValidator.SamePath(r.Path, value));
        if (root == null) return Edit.Refuse("Folder is not in the list");
        if (root.Watched == watched) return new Edit(c);
        var moved = root with { Watched = watched };
        return new Edit(c with { Roots = [.. c.Roots.Select(r => r == root ? moved : r)] }, After: () => effects.RootWatchChanged(moved));
    }

    private Edit AddExclude(RecallConfig c, string pattern)
    {
        var error = SettingsValidator.Pattern(c, pattern);
        if (error != null) return Edit.Refuse(error);
        IReadOnlyList<string> patterns = [.. c.GlobalIgnorePatterns, pattern];
        return new Edit(c with { GlobalIgnorePatterns = patterns }, After: () => effects.ExcludesChanged(patterns));
    }

    private Edit RemoveExclude(RecallConfig c, string? value)
    {
        if (!c.GlobalIgnorePatterns.Contains(value ?? "")) return Edit.Refuse("Pattern is not in the list");
        IReadOnlyList<string> patterns = [.. c.GlobalIgnorePatterns.Where(p => p != value)];
        return new Edit(c with { GlobalIgnorePatterns = patterns }, After: () => effects.ExcludesChanged(patterns));
    }

    private Edit AddType(RecallConfig c, string? extension, string? group)
    {
        var error = SettingsValidator.FileType(c, extension, group, out var ext);
        if (error != null) return Edit.Refuse(error);
        var types = new Dictionary<string, string>(c.CustomFileTypes) { [ext] = group! };
        return new Edit(c with { CustomFileTypes = types }, After: () => effects.FileTypesChanged(types));
    }

    private Edit RemoveType(RecallConfig c, string? extension)
    {
        var ext = "." + (extension ?? "").Trim().TrimStart('.').ToLowerInvariant();
        if (!c.CustomFileTypes.ContainsKey(ext)) return Edit.Refuse("File type is not in the list");
        var types = c.CustomFileTypes.Where(t => t.Key != ext).ToDictionary(t => t.Key, t => t.Value);
        return new Edit(c with { CustomFileTypes = types }, After: () => effects.FileTypesChanged(types));
    }

    /// <summary>The effective floor: the user's value, else the default of the chosen model. Null while semantic search is off.</summary>
    private static double? MinSimilarity(RecallConfig c) =>
        c.EmbeddingModel == null ? null : c.EmbeddingMinSimilarity ?? ModelCatalog.Find(c.EmbeddingModel)?.MinSimilarity;

    private Edit SetSimilarity(RecallConfig c, string? value)
    {
        // The extension sends what the user typed: accept both "0.84" and "0,84".
        if (!float.TryParse(value?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || !float.IsFinite(number) || number is < 0f or > 1f)
            return Edit.Refuse("Similarity must be a number from 0 to 1");
        if (c.EmbeddingModel == null) return Edit.Refuse("Semantic search is off in config.json");
        return new Edit(c with { EmbeddingMinSimilarity = number }, After: () => effects.SimilarityChanged(number));
    }

    private const string ListError = "List must be fast or slow";

    private static bool? ParseList(string? list) => list switch
    {
        IpcOps.FastList => true,
        IpcOps.SlowList => false,
        _ => null,
    };

    /// <summary>Applies an edit under the lock and saves first, so a failed write leaves memory and file in agreement.</summary>
    private IpcResponse Change(Func<RecallConfig, Edit> edit)
    {
        Action? after;
        lock (_gate)
        {
            var result = edit(_config);
            if (result.Next == null) return new IpcResponse(false, result.Error);
            try { if (configPath != null) result.Next.Save(configPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new IpcResponse(false, "Cannot save the settings: " + e.Message);
            }
            _config = result.Next;
            after = result.After;
        }
        after?.Invoke();
        return Ok();
    }
}
