using System.Collections.Concurrent;
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

/// <summary>
/// Metadata walk. Excluded directories are decided before they are entered, so their children are never listed.
/// With <paramref name="threads"/> above 1, directories are listed concurrently; the order of the returned files is then unspecified.
/// </summary>
public sealed class Scanner(IgnorePolicy policy, int threads = 1)
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly object _sync = new();
    private readonly Dictionary<string, long> _listed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reused = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _skip;

    public ScanStats Stats { get; } = new();

    /// <summary>Directories that could not be listed. Their files were not seen, which does not mean they were deleted.</summary>
    public IReadOnlySet<string> FailedDirectories => _failed;

    /// <summary>Directories whose files were enumerated, with their last-write ticks read before enumeration.</summary>
    public IReadOnlyDictionary<string, long> ListedDirectories => _listed;

    /// <summary>Directories whose stored last-write time still matched, so their files were not enumerated.</summary>
    public IReadOnlySet<string> ReusedDirectories => _reused;

    /// <summary>
    /// Walks one root. With <paramref name="memory"/>, a directory whose last-write time is unchanged is not listed:
    /// its remembered subdirectories are checked instead. Without it every directory is listed.
    /// </summary>
    /// <param name="skip">Folders not to enter: other roots inside this one, which are walked on their own.</param>
    public IEnumerable<FileRecord> Scan(RootFolder root, CancellationToken ct, DirectoryMemory? memory = null, IReadOnlyCollection<string>? skip = null)
    {
        _skip = skip is { Count: > 0 } ? new HashSet<string>(skip.Select(s => s.TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase) : null;
        var rootPath = RootPath.Normalize(root.Path);
        Stats.Roots.Add(rootPath);
        Stats.DirectoriesEntered++;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var dirs = new BlockingCollection<(string Dir, ScopeNode Scope)>();
        using var files = new BlockingCollection<FileRecord>();
        var pending = 1; // directories queued or being listed; the walk ends when it reaches zero
        dirs.Add((rootPath, new ScopeNode(rootPath, null, policy)));

        void Push(string dir, ScopeNode node)
        {
            Interlocked.Increment(ref pending);
            dirs.Add((dir, node));
        }

        var workers = Enumerable.Range(0, Math.Max(1, threads)).Select(_ => Task.Factory.StartNew(() =>
        {
            try
            {
                foreach (var (dir, node) in dirs.GetConsumingEnumerable(stop.Token))
                {
                    stop.Token.ThrowIfCancellationRequested();
                    ListDirectory(dir, node, memory, Push, files);
                    if (Interlocked.Decrement(ref pending) == 0) dirs.CompleteAdding();
                }
            }
            catch { stop.Cancel(); throw; }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        var finished = Task.WhenAll(workers).ContinueWith(_ => files.CompleteAdding(), TaskScheduler.Default);

        try
        {
            using var next = files.GetConsumingEnumerable(stop.Token).GetEnumerator();
            while (true)
            {
                bool has;
                try { has = next.MoveNext(); }
                catch (OperationCanceledException) { break; } // caller cancelled, or a worker failed and is rethrown below
                if (!has) break;
                yield return next.Current;
            }
        }
        finally
        {
            stop.Cancel(); // also reached when the caller stops early
            try { finished.Wait(); } catch (AggregateException) { }
        }
        ct.ThrowIfCancellationRequested();
        foreach (var w in workers) if (w.IsFaulted && w.Exception!.GetBaseException() is not OperationCanceledException) throw w.Exception.GetBaseException();
    }

    private void ListDirectory(
        string dir, ScopeNode node, DirectoryMemory? memory, Action<string, ScopeNode> push, BlockingCollection<FileRecord> files)
    {
        // The directory's own timestamp: the copy in its parent's listing can be stale on NTFS.
        var mtime = Directory.GetLastWriteTimeUtc(dir).Ticks;
        if (memory != null && memory.IsUnchanged(dir, mtime))
        {
            lock (_sync) { _reused.Add(dir); Stats.DirectoriesEntered += memory.ChildrenOf(dir).Count; }
            foreach (var child in memory.ChildrenOf(dir).Where(c => _skip?.Contains(c) != true)) push(child, new ScopeNode(child, node, policy));
            return;
        }

        FileSystemInfo[] entries;
        try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Options).ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            lock (_sync) _failed.Add(dir);
            return;
        }
        var scope = node.Scope;
        var found = new List<FileRecord>();
        var subdirs = new List<string>();
        var skippedDirs = new List<DirectoryDecision>();
        var skippedFiles = new List<FileDecision>();

        foreach (var e in entries)
        {
            if ((e.Attributes & FileAttributes.Directory) != 0)
            {
                var decision = policy.EnterDirectory(e.FullName, e.Attributes, scope);
                if (decision != DirectoryDecision.Enter) skippedDirs.Add(decision);
                else if (_skip?.Contains(e.FullName) != true) subdirs.Add(e.FullName);
                continue;
            }
            long size;
            try { size = ((FileInfo)e).Length; } catch (IOException) { continue; }
            var fileDecision = policy.IndexFile(e.FullName, size, e.Attributes, scope);
            if (fileDecision != FileDecision.Index) { skippedFiles.Add(fileDecision); continue; }
            found.Add(new FileRecord(e.FullName, size, e.LastWriteTimeUtc, policy.GroupOf(Path.GetExtension(e.Name))!));
        }

        lock (_sync)
        {
            _listed[dir] = mtime;
            foreach (var d in skippedDirs) Stats.Skip(d);
            foreach (var d in skippedFiles) Stats.Skip(d);
            Stats.DirectoriesEntered += subdirs.Count;
            Stats.FilesListed += found.Count;
            if (found.Count > 0) Stats.DirectFiles[dir] = Stats.DirectFiles.GetValueOrDefault(dir) + found.Count;
        }
        foreach (var sub in subdirs) push(sub, new ScopeNode(sub, node, policy));
        foreach (var f in found) files.Add(f);
    }
}
