using System.Collections.Concurrent;

namespace Recall.Core.Index;

internal sealed class RunCounters
{
    public long Added, Changed, Removed, Hashed, Extracted, Deduped, Failed, Skipped, MissingRoots;
    public long ScanTicks, HashTicks, ExtractTicks, OcrTicks, EmbedTicks, EmbeddedChunks;
    public readonly ConcurrentDictionary<UInt128, TaskCompletionSource<long>> Inflight = new();
}
