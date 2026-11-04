using System.Diagnostics;
using System.IO.Hashing;
using System.Threading.Channels;
using Recall.Core.Activity;
using Recall.Core.Config;
using Recall.Core.Embed;
using Recall.Core.Extract;
using Recall.Core.Ignore;
using Recall.Core.Ocr;
using Recall.Core.Scan;

namespace Recall.Core.Index;

/// <param name="workers">Files processed at the same time; null selects <see cref="DefaultWorkers"/>. Benchmarks pass 1 for single-thread figures.</param>
/// <param name="pause">While paused no new root, file or embedded image is started; work in flight finishes.</param>
/// <param name="embeddings">With <paramref name="embedder"/>, vectors are made once per pass after the files are stored. Null keeps embeddings off.</param>
/// <param name="fullScanInterval">Longest time a root may go without a full walk in <see cref="ScanMode.Auto"/>; null selects 7 days.</param>
/// <param name="scanThreads">Directories listed at the same time during a scan; null selects <see cref="DefaultScanThreads"/>.</param>
public sealed class IndexRunner(
    IIndexRepository store, IgnorePolicy policy, IReadOnlyList<IExtractor> extractors, IOcr ocr, int? workers = null, IPauseGate? pause = null,
    TimeSpan? fullScanInterval = null, int? scanThreads = null, IEmbeddingRepository? embeddings = null, EmbedderHost? embedder = null,
    bool embedInline = true)
{
    private readonly int _workers = Math.Max(1, workers ?? DefaultWorkers);
    private readonly RootScanPlanner planner = new(store, policy, fullScanInterval, scanThreads ?? DefaultScanThreads);

    /// <summary>Directory listing is mostly waiting for the disk, so more threads than cores help. Measured: 1 thread 4 min 39 s, 8 threads 1 min 18 s, 16 threads 45 s on 200k folders.</summary>
    public static int DefaultScanThreads { get; } = Math.Clamp(Environment.ProcessorCount, 4, 16);

    /// <summary>Number of files processed at the same time. OCR pools should be at least this large.</summary>
    public static int DefaultWorkers { get; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <param name="hints">What the change journal said about each root; null when there is no journal.</param>
    /// <param name="mode">Full (default) walks every directory. Auto skips directories unchanged since the last pass; see <see cref="RootScanPlanner"/>.</param>
    public async Task<RunReport> RunAsync(
        IReadOnlyList<RootFolder> roots, IProgress<RunProgress>? progress, CancellationToken ct, ScanMode mode = ScanMode.Full,
        RootHints? hints = null, IReadOnlyCollection<string>? knownRoots = null)
    {
        var total = Stopwatch.StartNew();
        var c = new RunCounters();
        foreach (var root in roots.OrderByDescending(r => r.Watched))
        {
            await WaitIfPausedAsync(ct);
            var sw = Stopwatch.StartNew();
            var rootPath = RootPath.Normalize(root.Path);
            // An unplugged drive or an unavailable cloud folder looks like "everything was deleted": keep the index as it is.
            if (!Directory.Exists(rootPath)) { c.MissingRoots++; continue; }
            var walk = planner.Begin(rootPath, mode, hints);
            var nested = RootNesting.Inside(rootPath, (knownRoots ?? roots.Select(r => r.Path).ToList()).Select(RootPath.Normalize));
            var scanned = await Task.Run(() => walk.Scan(root, ct, nested), ct);
            var changes = ChangeSet.Diff(scanned, store.Snapshot(rootPath), walk.UnchangedDirectories, walk.FailedDirectories);
            // Files that now belong to a nested root are not deleted here: that root's own pass takes them over.
            changes = changes with { Removed = [.. changes.Removed.Where(p => !RootNesting.UnderAny(p, nested))] };
            c.DirsListed += walk.DirectoriesListed;
            c.DirsReused += walk.DirectoriesReused;
            Interlocked.Add(ref c.ScanTicks, sw.ElapsedTicks);

            if (changes.Removed.Count > 0) store.RemoveFiles(changes.Removed);
            c.Removed += changes.Removed.Count;
            c.Added += changes.Added.Count;
            c.Changed += changes.Changed.Count;

            var work = changes.Added.Concat(changes.Changed).OrderByDescending(f => f.LastWriteUtc).ToList();
            await ProcessAsync(work, rootPath, c, progress, ct);
            walk.Commit(c.RetryDirectories.Keys);
            c.RetryDirectories.Clear();
        }
        if (embedInline) await EmbedAsync(c, ct);
        return Report(c, total.Elapsed);
    }

    /// <summary>
    /// Makes the vectors of everything stored that has none. With <c>embedInline: false</c> the passes leave this to the caller, which can then run it
    /// beside them: a backlog of hours must not hold up new files.
    /// </summary>
    public async Task<RunReport> EmbedPendingAsync(CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var c = new RunCounters();
        await EmbedAsync(c, ct);
        return Report(c, total.Elapsed);
    }

    /// <summary>Indexes specific files (watcher events). The caller has already applied the ignore policy. Missing files are removed from the index.</summary>
    public async Task<RunReport> IndexFilesAsync(RootFolder root, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var c = new RunCounters();
        var rootPath = RootPath.Normalize(root.Path);
        var work = new List<FileRecord>();
        var gone = new List<string>();
        foreach (var p in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fi = new FileInfo(p);
            if (!fi.Exists) { gone.Add(p); continue; }
            var group = policy.GroupOf(fi.Extension);
            if (group == null) continue;
            var rec = new FileRecord(fi.FullName, fi.Length, fi.LastWriteTimeUtc, group);
            var state = store.FileState(fi.FullName);
            if (state is { } s && s.Size == rec.Size && s.Mtime == rec.LastWriteUtc.Ticks) continue;
            work.Add(rec);
        }
        if (gone.Count > 0)
        {
            // The watcher reports a deleted or moved folder as one path, without the files in it.
            gone.AddRange(FilesBelow(store.Snapshot(rootPath).Keys, gone));
            store.RemoveFiles(gone);
            c.Removed += gone.Count;
        }
        c.Added += work.Count;
        await ProcessAsync(work, rootPath, c, null, ct);
        if (embedInline) await EmbedAsync(c, ct);
        return Report(c, total.Elapsed);
    }

    /// <summary>Stored paths that lie below any of the <paramref name="folders"/>. Each path is checked against its own ancestors only.</summary>
    private static List<string> FilesBelow(IEnumerable<string> stored, IEnumerable<string> folders)
    {
        var set = new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase);
        var below = new List<string>();
        foreach (var path in stored)
        {
            for (var d = Path.GetDirectoryName(path); d != null; d = Path.GetDirectoryName(d))
                if (set.Contains(d)) { below.Add(path); break; }
        }
        return below;
    }

    private Task WaitIfPausedAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return pause?.WaitWhilePausedAsync(ct) ?? Task.CompletedTask;
    }

    /// <summary>A failing model never fails the pass: the files are already stored and searchable, the error goes to the report.</summary>
    private async Task EmbedAsync(RunCounters c, CancellationToken ct)
    {
        if (embeddings == null || embedder == null) return;
        try
        {
            var stats = await new EmbeddingStage(embeddings, embedder, pause: pause).RunAsync(ct);
            c.EmbeddedChunks += stats.Chunks;
            c.EmbedTicks += stats.Busy.Ticks;
        }
        catch (EmbeddingUnavailableException) { /* the model is not downloaded; the next pass retries */ }
        catch (Exception e) when (e is not OperationCanceledException) { c.EmbedError = $"{e.GetType().Name}: {e.Message}"; }
    }

    private static RunReport Report(RunCounters c, TimeSpan elapsed) => new(
        c.Added, c.Changed, c.Removed, c.Hashed, c.Extracted, c.Deduped, c.Failed, c.Skipped, c.MissingRoots,
        TimeSpan.FromTicks(c.ScanTicks), TimeSpan.FromTicks(c.HashTicks), TimeSpan.FromTicks(c.ExtractTicks),
        TimeSpan.FromTicks(c.OcrTicks), elapsed, c.DirsListed, c.DirsReused, c.EmbeddedChunks, TimeSpan.FromTicks(c.EmbedTicks), c.EmbedError);

    private async Task ProcessAsync(List<FileRecord> work, string root, RunCounters c, IProgress<RunProgress>? progress, CancellationToken ct)
    {
        if (work.Count == 0) return;
        var channel = Channel.CreateBounded<FileRecord>(64);
        long done = 0;
        // A worker that fails stops the others and the producer: with every worker gone nobody would read the bounded queue and the pass would hang.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = Enumerable.Range(0, _workers).Select(_ => Task.Run(async () =>
        {
            try
            {
                await foreach (var f in channel.Reader.ReadAllAsync(stop.Token))
                {
                    await WaitIfPausedAsync(stop.Token);
                    await ProcessFileAsync(f, root, c, stop.Token);
                    progress?.Report(new RunProgress(root, Interlocked.Increment(ref done), work.Count));
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                stop.Cancel();
                throw;
            }
        }, stop.Token)).ToList();
        try
        {
            foreach (var f in work) await channel.Writer.WriteAsync(f, stop.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* a worker failed; its error is thrown below */ }
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
            store.UpsertFile(f, root, contentId, FileStatus.Ok, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (ExtractException e) when (e.Transient) { Retry(f, c); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Retry(f, c); }
        catch (ExtractException e)
        {
            store.UpsertFile(f, root, null, FileStatus.Failed, e.Message);
            Interlocked.Increment(ref c.Failed);
        }
        catch (Exception e)
        {
            store.UpsertFile(f, root, null, FileStatus.Failed, $"{e.GetType().Name}: {e.Message}");
            Interlocked.Increment(ref c.Failed);
        }
    }

    private static void Retry(FileRecord f, RunCounters c)
    {
        Interlocked.Increment(ref c.Skipped);
        c.RetryDirectories.TryAdd(Path.GetDirectoryName(f.Path)!, 0);
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
            await WaitIfPausedAsync(ct);
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
