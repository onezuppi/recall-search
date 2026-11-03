using Recall.Core.Config;
using Recall.Core.Ignore;

namespace Recall.Core.Scan;

public sealed class ScanStats
{
    public long DirectoriesEntered { get; internal set; }
    public Dictionary<DirectoryDecision, long> DirectoriesSkipped { get; } = [];
    public long FilesListed { get; internal set; }
    public Dictionary<FileDecision, long> FilesSkipped { get; } = [];

    internal readonly Dictionary<string, long> DirectFiles = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<string> Roots = [];

    /// <summary>The 50 directories with the most listed files beneath them (all depths). Feeds the heavy-folder hint.</summary>
    public IReadOnlyList<(string Path, long Files)> HeavyDirectories
    {
        get
        {
            var total = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var (dir, n) in DirectFiles)
            {
                var d = dir;
                while (!string.IsNullOrEmpty(d))
                {
                    total[d] = total.GetValueOrDefault(d) + n;
                    if (Roots.Any(r => string.Equals(r, d, StringComparison.OrdinalIgnoreCase))) break;
                    d = Path.GetDirectoryName(d);
                }
            }
            return total
                .Where(kv => !Roots.Any(r => string.Equals(r, kv.Key, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(kv => kv.Value).Take(50)
                .Select(kv => (kv.Key, kv.Value)).ToList();
        }
    }

    internal void Skip(DirectoryDecision d) => DirectoriesSkipped[d] = DirectoriesSkipped.GetValueOrDefault(d) + 1;
    internal void Skip(FileDecision d) => FilesSkipped[d] = FilesSkipped.GetValueOrDefault(d) + 1;
}

/// <summary>Metadata walk. Excluded directories are decided before they are entered, so their children are never listed.</summary>
public sealed class Scanner(IgnorePolicy policy)
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly Dictionary<string, long> _listed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reused = new(StringComparer.OrdinalIgnoreCase);

    public ScanStats Stats { get; } = new();

    /// <summary>Directories whose files were enumerated, with their last-write ticks read before enumeration.</summary>
    public IReadOnlyDictionary<string, long> ListedDirectories => _listed;

    /// <summary>Directories whose stored last-write time still matched, so their files were not enumerated.</summary>
    public IReadOnlySet<string> ReusedDirectories => _reused;

    /// <summary>
    /// Walks one root. With <paramref name="memory"/>, a directory whose last-write time is unchanged is not listed:
    /// its remembered subdirectories are checked instead. Without it every directory is listed.
    /// </summary>
    public IEnumerable<FileRecord> Scan(RootFolder root, CancellationToken ct, DirectoryMemory? memory = null)
    {
        var rootPath = Path.GetFullPath(root.Path).TrimEnd('\\');
        Stats.Roots.Add(rootPath);
        var stack = new Stack<(string Dir, ScopeNode Scope)>();
        stack.Push((rootPath, new ScopeNode(rootPath, null, policy)));
        Stats.DirectoriesEntered++;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            // The directory's own timestamp: the copy in its parent's listing can be stale on NTFS.
            var (dir, node) = stack.Pop();
            var mtime = Directory.GetLastWriteTimeUtc(dir).Ticks;
            if (memory != null && memory.IsUnchanged(dir, mtime))
            {
                _reused.Add(dir);
                foreach (var child in memory.ChildrenOf(dir))
                {
                    Stats.DirectoriesEntered++;
                    stack.Push((child, new ScopeNode(child, node, policy)));
                }
                continue;
            }

            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Options).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            _listed[dir] = mtime;
            var scope = node.Scope;

            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.Directory) != 0)
                {
                    var decision = policy.EnterDirectory(e.FullName, e.Attributes, scope);
                    if (decision != DirectoryDecision.Enter) { Stats.Skip(decision); continue; }
                    Stats.DirectoriesEntered++;
                    stack.Push((e.FullName, new ScopeNode(e.FullName, node, policy)));
                }
                else
                {
                    var fi = (FileInfo)e;
                    long size;
                    try { size = fi.Length; } catch (IOException) { continue; }
                    var decision = policy.IndexFile(e.FullName, size, e.Attributes, scope);
                    if (decision != FileDecision.Index) { Stats.Skip(decision); continue; }
                    Stats.FilesListed++;
                    Stats.DirectFiles[dir] = Stats.DirectFiles.GetValueOrDefault(dir) + 1;
                    yield return new FileRecord(e.FullName, size, e.LastWriteTimeUtc,
                        policy.GroupOf(Path.GetExtension(e.Name))!);
                }
            }
        }
    }
}
