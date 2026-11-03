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

    public ScanStats Stats { get; } = new();

    public IEnumerable<FileRecord> Scan(RootFolder root, CancellationToken ct)
    {
        var rootPath = Path.GetFullPath(root.Path).TrimEnd('\\');
        Stats.Roots.Add(rootPath);
        var stack = new Stack<(string Dir, IReadOnlyList<GitIgnoreMatcher> Scope)>();
        stack.Push((rootPath, policy.ScopeFor(rootPath, [])));
        Stats.DirectoriesEntered++;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, scope) = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Options).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.Directory) != 0)
                {
                    var decision = policy.EnterDirectory(e.FullName, e.Attributes, scope);
                    if (decision != DirectoryDecision.Enter) { Stats.Skip(decision); continue; }
                    Stats.DirectoriesEntered++;
                    stack.Push((e.FullName, policy.ScopeFor(e.FullName, scope)));
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
                        BuiltInRules.GroupOf(Path.GetExtension(e.Name))!);
                }
            }
        }
    }
}
