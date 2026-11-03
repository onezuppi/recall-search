namespace Recall.Core.Extract;

/// <summary>Lets an extractor also handle the file types the user assigned to its group.</summary>
public sealed class GroupExtractor(IExtractor inner, string group, Func<string, string?> groupOf) : IExtractor
{
    public bool CanHandle(string extension) => inner.CanHandle(extension) || groupOf(extension) == group;

    public Task<ExtractResult> ExtractAsync(string path, CancellationToken ct) => inner.ExtractAsync(path, ct);
}
