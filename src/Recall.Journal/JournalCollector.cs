namespace Recall.Journal;

/// <summary>Turns journal records into the list of files whose content may have changed, limited to the roots the caller may see.</summary>
public sealed class JournalCollector(IJournalSource source, int maxFiles = 200_000)
{
    public JournalResponse Collect(JournalRequest request, IReadOnlyList<string> allowedRoots)
    {
        JournalState state;
        try { state = source.Query(request.Volume); }
        catch (JournalUnavailableException e) { return new JournalResponse(JournalStatus.Unavailable, Error: e.Message); }

        if (request.JournalId == 0) return new JournalResponse(JournalStatus.NoCursor, state.JournalId, state.NextUsn, Roots: allowedRoots);
        if (request.JournalId != state.JournalId || request.StartUsn < state.FirstUsn || request.StartUsn > state.NextUsn)
            return new JournalResponse(JournalStatus.Gap, state.JournalId, state.NextUsn, Roots: allowedRoots);

        var roots = allowedRoots.Select(r => r.TrimEnd('\\') + "\\").ToList();
        var extensions = new HashSet<string>(request.Extensions, StringComparer.OrdinalIgnoreCase);
        var parents = new Dictionary<ulong, string?>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var from = request.StartUsn;

        while (from < state.NextUsn)
        {
            var (next, records) = source.Read(request.Volume, state.JournalId, from);
            if (records.Count == 0 || next <= from) break;
            foreach (var r in records)
            {
                if (r.Usn >= state.NextUsn) break;
                if (r.IsDirectory || (r.Reason & UsnRecord.ContentReasons) == 0) continue;
                if (extensions.Count > 0 && !extensions.Contains(Path.GetExtension(r.Name))) continue;
                if (!parents.TryGetValue(r.ParentId, out var parent))
                    parents[r.ParentId] = parent = source.DirectoryPath(request.Volume, r.ParentId);
                if (parent == null) continue;
                var path = Path.Combine(parent, r.Name);
                if (!roots.Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase))) continue;
                files.Add(path);
                if (files.Count > maxFiles) return new JournalResponse(JournalStatus.Gap, state.JournalId, state.NextUsn, Roots: allowedRoots);
            }
            from = next;
        }
        return new JournalResponse(JournalStatus.Ok, state.JournalId, state.NextUsn, files.ToList(), allowedRoots);
    }
}
