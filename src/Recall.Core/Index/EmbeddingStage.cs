using System.Diagnostics;
using Recall.Core.Activity;
using Recall.Core.Embed;

namespace Recall.Core.Index;

public sealed record EmbedStats(long Segments, long Chunks, TimeSpan Busy);

/// <summary>
/// Fills the vectors of stored segments that have none in the host's embedding space. Work goes in pages; a page is stored
/// in one transaction, so a cancelled run keeps every finished page. The model is loaded only when a page is pending.
/// </summary>
/// <param name="pause">Checked before every page. While paused the model is dropped, and it is loaded again on resume. Pending segments stay in the index.</param>
public sealed class EmbeddingStage(
    IEmbeddingRepository repository, EmbedderHost host, int pageSize = 64, IPauseGate? pause = null)
{
    private const int BatchChunks = 16;

    public async Task<EmbedStats> RunAsync(CancellationToken ct)
    {
        long segments = 0, chunks = 0;
        var busy = new Stopwatch();
        EmbedderHost.Lease? lease = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (pause is { IsPaused: true })
                {
                    lease?.Dispose();
                    lease = null;
                    host.Release();
                    await pause.WaitWhilePausedAsync(ct);
                }
                var page = repository.PendingSegments(host.Space, pageSize);
                if (page.Count == 0) break;
                lease ??= host.Acquire();
                var embedder = lease.Embedder;
                busy.Start();
                var rows = await Task.Run(() => EmbedPage(embedder, page, ct), ct);
                busy.Stop();
                repository.AddEmbeddings(host.Space, rows);
                segments += page.Count;
                chunks += rows.Count(r => r.Chunk != EmbeddingRow.BlankChunk);
            }
        }
        finally
        {
            busy.Stop();
            lease?.Dispose();
        }
        return new EmbedStats(segments, chunks, busy.Elapsed);
    }

    private static List<EmbeddingRow> EmbedPage(IEmbedder embedder, IReadOnlyList<PendingSegment> page, CancellationToken ct)
    {
        var rows = new List<EmbeddingRow>();
        var items = new List<(long SegmentId, int Chunk, string Text)>();
        foreach (var p in page)
        {
            var parts = EmbedChunker.Split(p.Text);
            if (parts.Count == 0) rows.Add(EmbeddingRow.Blank(p.SegmentId));
            for (var i = 0; i < parts.Count; i++) items.Add((p.SegmentId, i, parts[i]));
        }
        // Similar lengths in one batch keep the padding small.
        foreach (var batch in items.OrderBy(i => i.Text.Length).Chunk(BatchChunks))
        {
            ct.ThrowIfCancellationRequested();
            var vectors = embedder.Embed(batch.Select(b => b.Text).ToArray(), EmbedKind.Passage);
            for (var i = 0; i < batch.Length; i++) rows.Add(new EmbeddingRow(batch[i].SegmentId, batch[i].Chunk, vectors[i]));
        }
        return rows;
    }
}
