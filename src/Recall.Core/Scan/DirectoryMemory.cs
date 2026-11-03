namespace Recall.Core.Scan;

/// <summary>Directory last-write times (UTC ticks) stored by the previous scan of one root, with a children lookup.</summary>
public sealed class DirectoryMemory
{
    private readonly Dictionary<string, long> _mtimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _children = new(StringComparer.OrdinalIgnoreCase);

    public DirectoryMemory(IReadOnlyDictionary<string, long> mtimes)
    {
        foreach (var (dir, mtime) in mtimes)
        {
            _mtimes[dir] = mtime;
            var parent = Path.GetDirectoryName(dir);
            if (parent == null) continue;
            if (!_children.TryGetValue(parent, out var list)) _children[parent] = list = [];
            list.Add(dir);
        }
    }

    public int Count => _mtimes.Count;

    public IReadOnlyCollection<string> Directories => _mtimes.Keys;

    public bool IsUnchanged(string dir, long mtimeTicks) => _mtimes.TryGetValue(dir, out var old) && old == mtimeTicks;

    public IReadOnlyList<string> ChildrenOf(string dir) => _children.TryGetValue(dir, out var list) ? list : [];
}
