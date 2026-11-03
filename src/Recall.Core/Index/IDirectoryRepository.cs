namespace Recall.Core.Index;

/// <summary>Remembered directory times and small key-value state that let a later pass skip unchanged directories.</summary>
public interface IDirectoryRepository
{
    IReadOnlyDictionary<string, long> DirectoryTimes(string root);

    /// <summary>Stores new directory times and forgets the removed directories, in one transaction.</summary>
    void ApplyDirectories(string root, IReadOnlyDictionary<string, long> upserts, IEnumerable<string> removals);

    string? GetMeta(string key);

    void SetMeta(string key, string value);
}
