namespace Recall.Core.Scan;

/// <summary>Difference between a finished scan of one root and what the index stores for it. A file counts as changed when size or mtime differs.</summary>
public sealed record ChangeSet(IReadOnlyList<FileRecord> Added, IReadOnlyList<FileRecord> Changed, IReadOnlyList<string> Removed)
{
    public static ChangeSet Diff(IEnumerable<FileRecord> scanned, IReadOnlyDictionary<string, (long Size, long Mtime)> snapshot,
        IReadOnlySet<string>? unchangedDirs = null, IReadOnlySet<string>? failedDirs = null)
    {
        var added = new List<FileRecord>();
        var changed = new List<FileRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in scanned)
        {
            seen.Add(f.Path);
            if (!snapshot.TryGetValue(f.Path, out var old)) added.Add(f);
            else if (old.Size != f.Size || old.Mtime != f.LastWriteUtc.Ticks) changed.Add(f);
        }
        var removed = snapshot.Keys.Where(p => !seen.Contains(p) && !InUnchanged(p, unchangedDirs) && !UnderFailed(p, failedDirs)).ToList();
        return new ChangeSet(added, changed, removed);
    }

    /// <summary>
    /// A directory that could not be listed (a share that dropped, a disk error) hides its files, but does not delete them.
    /// The same holds for everything below it, because its subdirectories were never reached.
    /// </summary>
    private static bool UnderFailed(string path, IReadOnlySet<string>? failedDirs)
    {
        if (failedDirs is not { Count: > 0 }) return false;
        for (var dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            if (failedDirs.Contains(dir)) return true;
        return false;
    }

    /// <summary>Files of a directory that was not listed are neither seen nor removed: the previous scan still stands for them.</summary>
    private static bool InUnchanged(string path, IReadOnlySet<string>? unchangedDirs) =>
        unchangedDirs is { Count: > 0 } && Path.GetDirectoryName(path) is { } dir && unchangedDirs.Contains(dir);
}
