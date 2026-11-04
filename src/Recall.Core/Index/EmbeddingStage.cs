using System.Diagnostics;
using Recall.Core.Activity;
using Recall.Core.Embed;

namespace Recall.Core.Index;

public sealed record EmbedStats(long Segments, long Chunks, TimeSpan Busy, long Images = 0);

/// <summary>
/// Fills the vectors of stored segments that have none in the host's embedding space, then the picture vectors of image files when
/// the model can make them. Work goes in pages; a page is stored in one transaction, so a cancelled run keeps every finished page.
/// The model is loaded only when a page is pending.
/// </summary>
/// <param name="pause">Checked before every page. While paused the model is dropped, and it is loaded again on resume. Pending segments stay in the index.</param>
public sealed class EmbeddingStage(
    IEmbeddingRepository repository, EmbedderHost host, int pageSize = 64, IPauseGate? pause = null)
{
    private const int BatchChunks = 16, ImagePageSize = 8;

    private readonly Stopwatch _busy = new();
    private EmbedderHost.Lease? _lease;

    public async Task<EmbedStats> RunAsync(CancellationToken ct)
    {
        long segments = 0, chunks = 0, images = 0;
        try
        {
            while (await NextAsync(ct, () => repository.PendingSegments(host.Space, pageSize), (e, page) => EmbedPage(e, page, ct),
                       rows => repository.AddEmbeddings(host.Space, rows)) is { } done)
            {
                segments += done.Count;
                chunks += done.Rows.Count(r => r.Chunk != EmbeddingRow.BlankChunk);
            }
            if (host.ImageSpace is { } imageSpace)
                while (await NextAsync(ct, () => repository.PendingImages(imageSpace, ImagePageSize), EmbedImages,
                           rows => repository.AddEmbeddings(imageSpace, rows)) is { } done)
                {
                    images += done.Rows.Count(r => r.Chunk != EmbeddingRow.BlankChunk);
                }
        }
        finally
        {
            _busy.Stop();
            _lease?.Dispose();
            _lease = null;
        }
        return new EmbedStats(segments, chunks, _busy.Elapsed, images);
    }

    private sealed record Page(int Count, List<EmbeddingRow> Rows);

    /// <summary>Takes the next pending page, embeds it and stores it. Null when nothing is pending.</summary>
    private async Task<Page?> NextAsync<T>(
        CancellationToken ct, Func<IReadOnlyList<T>> pending, Func<IEmbedder, IReadOnlyList<T>, List<EmbeddingRow>> embed, Action<List<EmbeddingRow>> store)
    {
        ct.ThrowIfCancellationRequested();
        if (pause is { IsPaused: true })
        {
            _lease?.Dispose();
            _lease = null;
            host.Release();
            await pause.WaitWhilePausedAsync(ct);
        }
        var page = pending();
        if (page.Count == 0) return null;
        _lease ??= host.Acquire();
        var embedder = _lease.Embedder;
        _busy.Start();
        var rows = await Task.Run(() => embed(embedder, page), ct);
        _busy.Stop();
        store(rows);
        return new Page(page.Count, rows);
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

    /// <summary>A picture that cannot be read or has an unsupported format gets a blank row, so it is not tried again.</summary>
    private static List<EmbeddingRow> EmbedImages(IEmbedder embedder, IReadOnlyList<PendingImage> page)
    {
        var rows = new List<EmbeddingRow>();
        foreach (var p in page)
        {
            float[]? vector = null;
            if (embedder is IImageEmbedder images)
            {
                try { vector = images.EmbedImage(p.Path); }
                catch (InvalidDataException) { }
            }
            rows.Add(vector == null ? EmbeddingRow.Blank(p.SegmentId) : new EmbeddingRow(p.SegmentId, 0, vector));
        }
        return rows;
    }
}
