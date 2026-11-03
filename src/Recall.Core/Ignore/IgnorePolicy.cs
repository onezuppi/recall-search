using Recall.Core.Config;

namespace Recall.Core.Ignore;

public enum DirectoryDecision { Enter, SkipSystem, SkipBuiltIn, SkipIgnoreFile, SkipReparse }

public enum FileDecision { Index, SkipSecret, SkipType, SkipSize, SkipCloudOnly, SkipIgnoreFile }

public class IgnorePolicy
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    private readonly RecallConfig _config;
    private readonly Func<string, bool> _isSystemPath;
    private readonly GitIgnoreMatcher _global;

    public IgnorePolicy(RecallConfig config, Func<string, bool>? isSystemPath = null)
    {
        _config = config;
        _isSystemPath = isSystemPath ?? BuiltInRules.IsSystemPath;
        _global = GitIgnoreMatcher.Parse("", config.GlobalIgnorePatterns);
    }

    /// <summary>Virtual so tests can count which directories the scanner asks about.</summary>
    public virtual DirectoryDecision EnterDirectory(string fullPath, FileAttributes attrs, IReadOnlyList<GitIgnoreMatcher> scope)
    {
        if ((attrs & FileAttributes.ReparsePoint) != 0) return DirectoryDecision.SkipReparse;
        if (_isSystemPath(fullPath)) return DirectoryDecision.SkipSystem;
        if (BuiltInRules.SkipFolderNames.Contains(Path.GetFileName(fullPath.TrimEnd('\\', '/')))) return DirectoryDecision.SkipBuiltIn;
        if (Ignored(fullPath, true, scope)) return DirectoryDecision.SkipIgnoreFile;
        return DirectoryDecision.Enter;
    }

    public FileDecision IndexFile(string fullPath, long size, FileAttributes attrs, IReadOnlyList<GitIgnoreMatcher> scope)
    {
        var name = Path.GetFileName(fullPath);
        if (BuiltInRules.IsSecret(name)) return FileDecision.SkipSecret;
        if ((attrs & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0) return FileDecision.SkipCloudOnly;
        var group = BuiltInRules.GroupOf(Path.GetExtension(name));
        if (group == null || !_config.EnabledGroups.Contains(group)) return FileDecision.SkipType;
        if (Ignored(fullPath, false, scope)) return FileDecision.SkipIgnoreFile;
        var limit = group == BuiltInRules.Text ? _config.MaxTextBytes : _config.MaxImageBytes;
        if (size > limit) return FileDecision.SkipSize;
        return FileDecision.Index;
    }

    /// <summary>
    /// True when a single existing file passes every rule, including those of each ancestor folder below the root.
    /// Used for watcher events, where no scan has built the ignore scope.
    /// </summary>
    public bool Allows(string rootPath, string filePath, long size, FileAttributes attrs)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd('\\');
        var chain = new Stack<string>();
        for (var d = Path.GetDirectoryName(filePath);
             d != null && d.Length > root.Length && d.StartsWith(root, StringComparison.OrdinalIgnoreCase);
             d = Path.GetDirectoryName(d))
            chain.Push(d);
        if (!filePath.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return false;

        var scope = ScopeFor(root, []);
        try
        {
            foreach (var dir in chain)
            {
                if (EnterDirectory(dir, File.GetAttributes(dir), scope) != DirectoryDecision.Enter) return false;
                scope = ScopeFor(dir, scope);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        return IndexFile(filePath, size, attrs, scope) == FileDecision.Index;
    }

    public IReadOnlyList<GitIgnoreMatcher> ScopeFor(string dir, IReadOnlyList<GitIgnoreMatcher> parentScope)
    {
        List<GitIgnoreMatcher>? added = null;
        foreach (var name in new[] { ".gitignore", ".recallignore" })
        {
            var f = Path.Combine(dir, name);
            try
            {
                if (!File.Exists(f)) continue;
                var m = GitIgnoreMatcher.Parse(dir, File.ReadAllLines(f));
                if (!m.IsEmpty) (added ??= []).Add(m);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return added == null ? parentScope : [.. parentScope, .. added];
    }

    private bool Ignored(string fullPath, bool isDir, IReadOnlyList<GitIgnoreMatcher> scope)
    {
        var result = _global.Match(fullPath, isDir);
        foreach (var m in scope)
        {
            var r = m.Match(fullPath, isDir);
            if (r != MatchResult.None) result = r;
        }
        return result == MatchResult.Ignored;
    }
}
