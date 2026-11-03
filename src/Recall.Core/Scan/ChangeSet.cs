namespace Recall.Core.Scan;

/// <summary>Difference between a finished scan of one root and what the index stores for it. A file counts as changed when size or mtime differs.</summary>
public sealed record ChangeSet(IReadOnlyList<FileRecord> Added, IReadOnlyList<FileRecord> Changed, IReadOnlyList<string> Removed)
{
    public static ChangeSet Diff(IEnumerable<FileRecord> scanned, IReadOnlyDictionary<string, (long Size, long Mtime)> snapshot,
        IReadOnlySet<string>? unchangedDirs = null)
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
        var removed = snapshot.Keys.Where(p => !seen.Contains(p) && !InUnchanged(p, unchangedDirs)).ToList();
        return new ChangeSet(added, changed, removed);
    }

    /// <summary>Files of a directory that was not listed are neither seen nor removed: the previous scan still stands for them.</summary>
    private static bool InUnchanged(string path, IReadOnlySet<string>? unchangedDirs) =>
        unchangedDirs is { Count: > 0 } && Path.GetDirectoryName(path) is { } dir && unchangedDirs.Contains(dir);
}
