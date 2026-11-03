using Recall.Core.Config;
using Recall.Core.Ignore;
using Recall.Core.Index;

namespace Recall.Core.Scan;

/// <summary>Full walks every directory. Auto skips directories whose last-write time is unchanged, unless a full walk is due.</summary>
public enum ScanMode { Full, Auto }

/// <summary>
/// Decides per root whether a pass may reuse the stored directory times. A full walk is used when the mode asks for it,
/// when the root was never fully walked, when the ignore configuration changed, or when the last full walk is older than
/// the interval. A fast walk cannot see a file overwritten in place, which is what the interval bounds.
/// </summary>
public sealed class RootScanPlanner(IDirectoryRepository store, IgnorePolicy policy, TimeSpan? fullScanInterval = null)
{
    public static readonly TimeSpan DefaultFullScanInterval = TimeSpan.FromDays(7);

    private readonly TimeSpan _interval = fullScanInterval ?? DefaultFullScanInterval;

    internal static string StampKey(string rootPath) => "full:" + rootPath.ToLowerInvariant();

    public RootScan Begin(string rootPath, ScanMode mode)
    {
        var stored = store.DirectoryTimes(rootPath);
        var fast = mode == ScanMode.Auto && stored.Count > 0 && StampIsFresh(rootPath);
        return new RootScan(store, policy, rootPath, stored, fast);
    }

    private bool StampIsFresh(string rootPath)
    {
        var parts = store.GetMeta(StampKey(rootPath))?.Split('|', 2);
        return parts is { Length: 2 } && long.TryParse(parts[0], out var ticks)
            && parts[1] == policy.Fingerprint
            && DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < _interval;
    }
}

/// <summary>One walk of one root. <see cref="Commit"/> stores the directory state once the walk's files were processed.</summary>
public sealed class RootScan
{
    private const long NeverMatches = -1;

    private readonly IDirectoryRepository _store;
    private readonly IgnorePolicy _policy;
    private readonly string _root;
    private readonly IReadOnlyDictionary<string, long> _stored;
    private readonly Scanner _scanner;

    internal RootScan(IDirectoryRepository store, IgnorePolicy policy, string root, IReadOnlyDictionary<string, long> stored, bool fast)
    {
        (_store, _policy, _root, _stored, IsFast) = (store, policy, root, stored, fast);
        _scanner = new Scanner(policy);
    }

    public bool IsFast { get; }

    /// <summary>Directories that were listed; the others were checked by last-write time only.</summary>
    public int DirectoriesListed => _scanner.ListedDirectories.Count;

    public int DirectoriesReused => _scanner.ReusedDirectories.Count;

    /// <summary>Directories not listed in this walk: their files in the index stay as they are.</summary>
    public IReadOnlySet<string> UnchangedDirectories => _scanner.ReusedDirectories;

    public List<FileRecord> Scan(RootFolder root, CancellationToken ct) =>
        _scanner.Scan(root, ct, IsFast ? new DirectoryMemory(_stored) : null).ToList();

    /// <param name="retryDirectories">Directories holding a file that was skipped for now (locked, IO error); they are listed again next time.</param>
    public void Commit(IEnumerable<string> retryDirectories)
    {
        var retry = new HashSet<string>(retryDirectories, StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(_scanner.ListedDirectories.Keys, StringComparer.OrdinalIgnoreCase);
        visited.UnionWith(_scanner.ReusedDirectories);
        // A retry directory stays known (its parent still reaches it) but with a time that never matches, so it is listed again.
        var upserts = _scanner.ListedDirectories
            .Select(kv => retry.Contains(kv.Key) ? new KeyValuePair<string, long>(kv.Key, NeverMatches) : kv)
            .Where(kv => !_stored.TryGetValue(kv.Key, out var old) || old != kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var removals = _stored.Keys.Where(d => !visited.Contains(d));
        _store.ApplyDirectories(_root, upserts, removals);
        if (!IsFast) _store.SetMeta(RootScanPlanner.StampKey(_root), $"{DateTime.UtcNow.Ticks}|{_policy.Fingerprint}");
    }
}
