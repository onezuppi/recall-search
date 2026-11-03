using System.Collections.Concurrent;

namespace Recall.Core.Index;

internal sealed class RunCounters
{
    public long Added, Changed, Removed, Hashed, Extracted, Deduped, Failed, Skipped, MissingRoots;
    public long ScanTicks, HashTicks, ExtractTicks, OcrTicks, DirsListed, DirsReused, EmbedTicks, EmbeddedChunks;
    public string? EmbedError;
    /// <summary>Directories with a file skipped for now; their remembered time is dropped so the next fast pass lists them.</summary>
    public readonly ConcurrentDictionary<string, byte> RetryDirectories = new(StringComparer.OrdinalIgnoreCase);
    public readonly ConcurrentDictionary<UInt128, TaskCompletionSource<long>> Inflight = new();
}
