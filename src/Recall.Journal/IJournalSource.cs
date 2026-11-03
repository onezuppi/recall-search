namespace Recall.Journal;

public readonly record struct JournalState(ulong JournalId, long FirstUsn, long NextUsn);

/// <summary>The NTFS change journal of one volume. The real implementation needs administrator rights; tests use a fake.</summary>
public interface IJournalSource
{
    /// <summary>Throws <see cref="JournalUnavailableException"/> when the volume has no active journal.</summary>
    JournalState Query(string volume);

    /// <summary>One batch of close records starting at <paramref name="startUsn"/>. An empty batch means the end was reached.</summary>
    (long NextUsn, IReadOnlyList<UsnRecord> Records) Read(string volume, ulong journalId, long startUsn);

    /// <summary>Current full path of a directory by file id, or null when it no longer exists.</summary>
    string? DirectoryPath(string volume, ulong fileId);
}

public sealed class JournalUnavailableException(string message) : Exception(message);
