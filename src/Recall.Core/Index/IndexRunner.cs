using System.Diagnostics;
using System.IO.Hashing;
using System.Threading.Channels;
using Recall.Core.Config;
using Recall.Core.Embed;
using Recall.Core.Extract;
using Recall.Core.Ignore;
using Recall.Core.Ocr;
using Recall.Core.Scan;

namespace Recall.Core.Index;

/// <param name="workers">Files processed at the same time; null selects <see cref="DefaultWorkers"/>. Benchmarks pass 1 for single-thread figures.</param>
/// <param name="embedder">Null keeps embeddings off. Vectors are made once per pass, after the files are stored.</param>
public sealed class IndexRunner(
    IndexStore store, IgnorePolicy policy, IReadOnlyList<IExtractor> extractors, IOcr ocr, int? workers = null, EmbedderHost? embedder = null)
{
    private readonly int _workers = Math.Max(1, workers ?? DefaultWorkers);

    /// <summary>Number of files processed at the same time. OCR pools should be at least this large.</summary>
    public static int DefaultWorkers { get; } = Math.Max(1, Environment.ProcessorCount / 2);

    public async Task<RunReport> RunAsync(IReadOnlyList<RootFolder> roots, IProgress<RunProgress>? progress, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var c = new RunCounters();
        foreach (var root in roots.OrderByDescending(r => r.Watched))
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var rootPath = Path.GetFullPath(root.Path).TrimEnd('\\');
            // An unplugged drive or an unavailable cloud folder looks like "everything was deleted": keep the index as it is.
            if (!Directory.Exists(rootPath)) { c.MissingRoots++; continue; }
            var scanner = new Scanner(policy);
            var scanned = await Task.Run(() => scanner.Scan(root, ct).ToList(), ct);
            var changes = ChangeSet.Diff(scanned, store.Snapshot(rootPath));
            Interlocked.Add(ref c.ScanTicks, sw.ElapsedTicks);

            if (changes.Removed.Count > 0) store.RemoveFiles(changes.Removed);
            c.Removed += changes.Removed.Count;
            c.Added += changes.Added.Count;
            c.Changed += changes.Changed.Count;

            var work = changes.Added.Concat(changes.Changed).OrderByDescending(f => f.LastWriteUtc).ToList();
            await ProcessAsync(work, rootPath, c, progress, ct);
        }
        await EmbedAsync(c, ct);
        return Report(c, total.Elapsed);
    }

    /// <summary>Indexes specific files (watcher events). The caller has already applied the ignore policy. Missing files are removed from the index.</summary>
    public async Task<RunReport> IndexFilesAsync(RootFolder root, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var c = new RunCounters();
        var rootPath = Path.GetFullPath(root.Path).TrimEnd('\\');
        var work = new List<FileRecord>();
        var gone = new List<string>();
        foreach (var p in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fi = new FileInfo(p);
            if (!fi.Exists) { gone.Add(p); continue; }
            var group = BuiltInRules.GroupOf(fi.Extension);
            if (group == null) continue;
            var rec = new FileRecord(fi.FullName, fi.Length, fi.LastWriteTimeUtc, group);
            var state = store.FileState(fi.FullName);
            if (state is { } s && s.Size == rec.Size && s.Mtime == rec.LastWriteUtc.Ticks) continue;
            work.Add(rec);
        }
        if (gone.Count > 0) { store.RemoveFiles(gone); c.Removed += gone.Count; }
        c.Added += work.Count;
        await ProcessAsync(work, rootPath, c, null, ct);
        await EmbedAsync(c, ct);
        return Report(c, total.Elapsed);
    }

    private async Task EmbedAsync(RunCounters c, CancellationToken ct)
    {
        if (embedder == null) return;
        try
        {
            var stats = await new EmbeddingStage(store, embedder).RunAsync(ct);
            c.EmbeddedChunks += stats.Chunks;
            c.EmbedTicks += stats.Busy.Ticks;
        }
        catch (EmbeddingUnavailableException) { /* the model is not downloaded; text search still works and the next pass retries */ }
    }

    private static RunReport Report(RunCounters c, TimeSpan elapsed) => new(
        c.Added, c.Changed, c.Removed, c.Hashed, c.Extracted, c.Deduped, c.Failed, c.Skipped, c.MissingRoots,
        TimeSpan.FromTicks(c.ScanTicks), TimeSpan.FromTicks(c.HashTicks), TimeSpan.FromTicks(c.ExtractTicks),
        TimeSpan.FromTicks(c.OcrTicks), elapsed, c.EmbeddedChunks, TimeSpan.FromTicks(c.EmbedTicks));

    private async Task ProcessAsync(List<FileRecord> work, string root, RunCounters c, IProgress<RunProgress>? progress, CancellationToken ct)
    {
        if (work.Count == 0) return;
        var channel = Channel.CreateBounded<FileRecord>(64);
        long done = 0;
        var workers = Enumerable.Range(0, _workers).Select(_ => Task.Run(async () =>
        {
            await foreach (var f in channel.Reader.ReadAllAsync(ct))
            {
                await ProcessFileAsync(f, root, c, ct);
                progress?.Report(new RunProgress(root, Interlocked.Increment(ref done), work.Count));
            }
        }, ct)).ToList();
        try
        {
            foreach (var f in work) await channel.Writer.WriteAsync(f, ct);
        }
        finally { channel.Writer.Complete(); }
        await Task.WhenAll(workers);
    }

    private async Task ProcessFileAsync(FileRecord f, string root, RunCounters c, CancellationToken ct)
    {
        try
        {
            UInt128 hash;
            var sw = Stopwatch.StartNew();
            try { hash = await HashAsync(f.Path, ct); }
            finally { Interlocked.Add(ref c.HashTicks, sw.ElapsedTicks); }
            Interlocked.Increment(ref c.Hashed);

            var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            long contentId;
            if (c.Inflight.TryAdd(hash, tcs))
            {
                try
                {
                    var existing = store.FindContent(hash);
                    if (existing != null) { contentId = existing.Value; Interlocked.Increment(ref c.Deduped); }
                    else { contentId = await ExtractAndStoreAsync(f, hash, c, ct); Interlocked.Increment(ref c.Extracted); }
                    tcs.SetResult(contentId);
                }
                catch (Exception e)
                {
                    tcs.SetException(e);
                    throw;
                }
            }
            else
            {
                contentId = await c.Inflight[hash].Task.WaitAsync(ct);
                Interlocked.Increment(ref c.Deduped);
            }
            store.UpsertFile(f, root, contentId, "ok", null);
        }
        catch (OperationCanceledException) { throw; }
        catch (ExtractException e) when (e.Transient) { Interlocked.Increment(ref c.Skipped); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Interlocked.Increment(ref c.Skipped); }
        catch (ExtractException e)
        {
            store.UpsertFile(f, root, null, "failed", e.Message);
            Interlocked.Increment(ref c.Failed);
        }
        catch (Exception e)
        {
            store.UpsertFile(f, root, null, "failed", $"{e.GetType().Name}: {e.Message}");
            Interlocked.Increment(ref c.Failed);
        }
    }

    private async Task<long> ExtractAndStoreAsync(FileRecord f, UInt128 hash, RunCounters c, CancellationToken ct)
    {
        var ext = Path.GetExtension(f.Path);
        var extractor = extractors.FirstOrDefault(e => e.CanHandle(ext)) ?? throw new ExtractException("no extractor for " + ext);
        var sw = Stopwatch.StartNew();
        ExtractResult result;
        try { result = await extractor.ExtractAsync(f.Path, ct); }
        finally { Interlocked.Add(ref c.ExtractTicks, sw.ElapsedTicks); }

        var segments = result.Segments.ToList();
        foreach (var img in result.Images)
        {
            var o = Stopwatch.StartNew();
            try
            {
                var text = await ocr.RecognizeAsync(img.Bytes, ct);
                if (!string.IsNullOrWhiteSpace(text)) segments.Add(new Segment(img.Locator, text));
            }
            catch (ExtractException) { /* one unreadable embedded image does not fail the document */ }
            finally { Interlocked.Add(ref c.OcrTicks, o.ElapsedTicks); }
        }
        return store.AddContent(hash, f.Group, segments);
    }

    private static async Task<UInt128> HashAsync(string path, CancellationToken ct)
    {
        var h = new XxHash128();
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var buf = new byte[81920];
        int n;
        while ((n = await fs.ReadAsync(buf, ct)) > 0) h.Append(buf.AsSpan(0, n));
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt128LittleEndian(h.GetCurrentHash());
    }
}
