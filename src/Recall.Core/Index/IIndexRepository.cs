using Recall.Core.Scan;

namespace Recall.Core.Index;

public sealed record Segment(string Locator, string Text);

/// <summary>Values of the <c>files.status</c> column.</summary>
public static class FileStatus
{
    public const string Ok = "ok", Failed = "failed";
}

/// <summary>Write side of the index: files point to deduplicated contents, contents own text segments.</summary>
public interface IIndexRepository : IDisposable
{
    /// <summary>Stored size and mtime ticks of every file under a root, keyed by path.</summary>
    IReadOnlyDictionary<string, (long Size, long Mtime)> Snapshot(string root);

    /// <summary>Stored size and mtime ticks of one path, or null when the path is not indexed.</summary>
    (long Size, long Mtime)? FileState(string path);

    long? FindContent(UInt128 hash);

    long AddContent(UInt128 hash, string group, IReadOnlyList<Segment> segments);

    void UpsertFile(FileRecord file, string root, long? contentId, string status, string? error);

    /// <summary>Removes files and then the contents and segments no file points to any more.</summary>
    void RemoveFiles(IEnumerable<string> paths);

    /// <summary>Counts for the status line: indexed (ok) and failed files.</summary>
    (long Indexed, long Failed) Counts();
}
