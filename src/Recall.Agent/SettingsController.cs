using Recall.Core.Config;
using Recall.Core.Ignore;
using Recall.Core.Ipc;

namespace Recall.Agent;

/// <summary>What the agent must do after a settings change was validated and saved.</summary>
public interface ISettingsEffects
{
    void RootAdded(RootFolder root);
    void RootRemoved(RootFolder root);
    void ExcludesChanged(IReadOnlyList<string> patterns);
}

/// <summary>
/// Validates folder and exclude-pattern changes, saves them to config.json and tells the agent to apply them.
/// The agent owns the file: the Command Palette extension runs in an MSIX package and must not write it.
/// </summary>
public sealed class SettingsController(
    RecallConfig initial, string? configPath, Func<string, bool>? isSystemPath, ISettingsEffects effects)
{
    private const int MaxPatternLength = 300;

    private readonly object _gate = new();
    private readonly Func<string, bool> _isSystemPath = isSystemPath ?? BuiltInRules.IsSystemPath;
    private RecallConfig _config = initial;

    public RecallConfig Config { get { lock (_gate) return _config; } }

    public bool IsConfigured(string rootPath) => Config.Roots.Any(r => SamePath(r.Path, rootPath));

    public IpcSettings Snapshot()
    {
        var c = Config;
        return new IpcSettings([.. c.Roots.Select(r => new IpcRoot(r.Path, r.Watched))], [.. c.GlobalIgnorePatterns]);
    }

    public IpcResponse Handle(IpcRequest request) => request.Op switch
    {
        IpcOps.Settings => Ok(),
        IpcOps.AddRoot => AddRoot(request.Value),
        IpcOps.RemoveRoot => RemoveRoot(request.Value),
        IpcOps.AddExclude => AddExclude(request.Value),
        IpcOps.RemoveExclude => RemoveExclude(request.Value),
        _ => new IpcResponse(false, "unknown operation"),
    };

    private IpcResponse Ok() => new(true, Settings: Snapshot());

    private IpcResponse AddRoot(string? value)
    {
        RootFolder root;
        lock (_gate)
        {
            var error = ValidateRoot(value, out var full);
            if (error != null) return new IpcResponse(false, error);
            root = new RootFolder(full, true);
            var failure = Commit(_config with { Roots = [.. _config.Roots, root] });
            if (failure != null) return failure;
        }
        effects.RootAdded(root);
        return Ok();
    }

    private IpcResponse RemoveRoot(string? value)
    {
        RootFolder? root;
        lock (_gate)
        {
            root = _config.Roots.FirstOrDefault(r => SamePath(r.Path, value));
            if (root == null) return new IpcResponse(false, "folder is not in the list");
            var failure = Commit(_config with { Roots = [.. _config.Roots.Where(r => r != root)] });
            if (failure != null) return failure;
        }
        effects.RootRemoved(root);
        return Ok();
    }

    private IpcResponse AddExclude(string? value)
    {
        var pattern = value?.Trim() ?? "";
        IReadOnlyList<string> patterns;
        lock (_gate)
        {
            var error = ValidatePattern(pattern);
            if (error != null) return new IpcResponse(false, error);
            patterns = [.. _config.GlobalIgnorePatterns, pattern];
            var failure = Commit(_config with { GlobalIgnorePatterns = patterns });
            if (failure != null) return failure;
        }
        effects.ExcludesChanged(patterns);
        return Ok();
    }

    private IpcResponse RemoveExclude(string? value)
    {
        IReadOnlyList<string> patterns;
        lock (_gate)
        {
            if (!_config.GlobalIgnorePatterns.Contains(value ?? "")) return new IpcResponse(false, "pattern is not in the list");
            patterns = [.. _config.GlobalIgnorePatterns.Where(p => p != value)];
            var failure = Commit(_config with { GlobalIgnorePatterns = patterns });
            if (failure != null) return failure;
        }
        effects.ExcludesChanged(patterns);
        return Ok();
    }

    /// <summary>Saves first, so a failed write leaves memory and file in agreement.</summary>
    private IpcResponse? Commit(RecallConfig next)
    {
        try { if (configPath != null) next.Save(configPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new IpcResponse(false, "cannot save the settings: " + e.Message);
        }
        _config = next;
        return null;
    }

    private string? ValidateRoot(string? value, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(value)) return "folder path is empty";
        if (!Path.IsPathFullyQualified(value)) return "folder path must be absolute";
        string absolute;
        try { absolute = Path.GetFullPath(value); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "folder path is invalid";
        }
        if (string.Equals(Path.GetPathRoot(absolute), absolute, StringComparison.OrdinalIgnoreCase))
            return "a whole drive cannot be added";
        full = absolute.TrimEnd('\\', '/');
        if (!Directory.Exists(full)) return "folder does not exist";
        if (_isSystemPath(full)) return "system folders cannot be indexed";
        foreach (var r in _config.Roots)
        {
            if (SamePath(r.Path, full)) return "folder is already in the list";
            if (IsUnder(full, r.Path)) return $"folder is already covered by {r.Path}";
            if (IsUnder(r.Path, full)) return $"folder contains {r.Path}, remove that one first";
        }
        return null;
    }

    private string? ValidatePattern(string pattern)
    {
        if (pattern.Length == 0) return "pattern is empty";
        if (pattern.Length > MaxPatternLength) return "pattern is too long";
        if (pattern[0] == '#') return "pattern cannot start with #";
        if (pattern.Any(char.IsControl)) return "pattern contains control characters";
        if (_config.GlobalIgnorePatterns.Contains(pattern)) return "pattern is already in the list";
        return null;
    }

    private static bool SamePath(string a, string? b) =>
        b != null && string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string parent) =>
        path.StartsWith(parent.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase);
}
