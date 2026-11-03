using Recall.Core.Config;
using Recall.Core.Ignore;
using Recall.Core.Index;
using Recall.Core.Scan;
using Recall.Journal;

namespace Recall.Core.Journal;

/// <summary>What the change journal told the agent at startup, per root.</summary>
public sealed class JournalPlan
{
    internal Dictionary<string, (ulong JournalId, long Usn)> Cursors { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, List<string>> Changed { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Roots whose changes since the stored cursor are known in full, including files overwritten in place.</summary>
    public HashSet<string> Covered { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Roots with no usable cursor: they get one full walk, and the cursor taken before it covers everything after.</summary>
    public HashSet<string> NeedsFull { get; } = new(StringComparer.OrdinalIgnoreCase);

    public RootHints Hints => new(Covered, NeedsFull);
}

/// <summary>
/// Startup sync with the NTFS change journal. Per root it stores a cursor (journal id and next USN) in the index.
/// Roots without a valid cursor are scanned fully once; roots with one need only the listed files plus a directory pass.
/// </summary>
public sealed class JournalCatchUp(IDirectoryRepository store, IChangeJournal journal, Func<IReadOnlyCollection<string>>? extensions = null)
{
    private static string Key(string root) => "usn:" + root.ToLowerInvariant();

    public async Task<JournalPlan> PrepareAsync(IReadOnlyList<RootFolder> roots, CancellationToken ct)
    {
        var plan = new JournalPlan();
        var known = (extensions?.Invoke() ?? BuiltInRules.KnownExtensions).ToList();
        foreach (var root in roots)
        {
            var path = Path.GetFullPath(root.Path).TrimEnd('\\');
            if (path.Length < 2 || path[1] != ':' || !Directory.Exists(path)) continue;
            var (id, usn) = ReadCursor(path);
            var response = await journal.QueryAsync(new JournalRequest(path[..2].ToUpperInvariant(), id, usn, [path], known), ct);
            if (response == null) continue;

            switch (response.Status)
            {
                case JournalStatus.Ok when response.Roots?.Contains(path, StringComparer.OrdinalIgnoreCase) == true:
                    plan.Covered.Add(path);
                    plan.Changed[path] = response.Files?.ToList() ?? [];
                    plan.Cursors[path] = (response.JournalId, response.NextUsn);
                    break;
                case JournalStatus.NoCursor or JournalStatus.Gap when response.Roots?.Contains(path, StringComparer.OrdinalIgnoreCase) == true:
                    plan.NeedsFull.Add(path);
                    plan.Cursors[path] = (response.JournalId, response.NextUsn);
                    break;
            }
        }
        return plan;
    }

    /// <summary>Indexes the files the journal reported for covered roots. <paramref name="flush"/> applies the ignore policy.</summary>
    public async Task ApplyAsync(JournalPlan plan, Func<string, IReadOnlyList<string>, Task> flush)
    {
        foreach (var (root, files) in plan.Changed)
            if (files.Count > 0) await flush(root, files);
    }

    /// <summary>Stores the new cursors. Call after the files and the directory pass of this startup have been processed.</summary>
    public void Commit(JournalPlan plan)
    {
        foreach (var (root, cursor) in plan.Cursors) store.SetMeta(Key(root), $"{cursor.JournalId}|{cursor.Usn}");
    }

    private (ulong Id, long Usn) ReadCursor(string root)
    {
        var parts = store.GetMeta(Key(root))?.Split('|', 2);
        return parts is { Length: 2 } && ulong.TryParse(parts[0], out var id) && long.TryParse(parts[1], out var usn) ? (id, usn) : (0, 0);
    }
}
