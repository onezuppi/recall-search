using System.Diagnostics;
using Recall.Core.Embed;

namespace Recall.Core.Index;

public sealed record EmbedStats(long Segments, long Chunks, TimeSpan Busy);

/// <summary>
/// Fills the vectors of stored segments that have none in the host's embedding space. Work goes in pages; a page is stored
/// in one transaction, so a cancelled run keeps every finished page. The model is loaded only when a page is pending.
/// </summary>
public sealed class EmbeddingStage(IndexStore store, EmbedderHost host, int pageSize = 64)
{
    private const int BatchChunks = 16;

    public Task<EmbedStats> RunAsync(CancellationToken ct) => Task.Run(() => Run(ct), ct);

    private EmbedStats Run(CancellationToken ct)
    {
        long segments = 0, chunks = 0;
        var busy = new Stopwatch();
        EmbedderHost.Lease? lease = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = store.PendingSegments(host.Space, pageSize);
                if (page.Count == 0) break;
                lease ??= host.Acquire();
                busy.Start();
                var rows = EmbedPage(lease.Embedder, page, ct);
                busy.Stop();
                store.AddEmbeddings(host.Space, rows);
                segments += page.Count;
                chunks += rows.Count(r => r.Chunk != IndexStore.BlankChunk);
            }
        }
        finally
        {
            busy.Stop();
            lease?.Dispose();
        }
        return new EmbedStats(segments, chunks, busy.Elapsed);
    }

    private static List<(long SegmentId, int Chunk, float[] Vector)> EmbedPage(IEmbedder embedder, IReadOnlyList<PendingSegment> page, CancellationToken ct)
    {
        var rows = new List<(long, int, float[])>();
        var items = new List<(long SegmentId, int Chunk, string Text)>();
        foreach (var p in page)
        {
            var parts = EmbedChunker.Split(p.Text);
            if (parts.Count == 0) rows.Add((p.SegmentId, IndexStore.BlankChunk, []));
            for (var i = 0; i < parts.Count; i++) items.Add((p.SegmentId, i, parts[i]));
        }
        // Similar lengths in one batch keep the padding small.
        foreach (var batch in items.OrderBy(i => i.Text.Length).Chunk(BatchChunks))
        {
            ct.ThrowIfCancellationRequested();
            var vectors = embedder.Embed(batch.Select(b => b.Text).ToArray(), EmbedKind.Passage);
            for (var i = 0; i < batch.Length; i++) rows.Add((batch[i].SegmentId, batch[i].Chunk, vectors[i]));
        }
        return rows;
    }
}
