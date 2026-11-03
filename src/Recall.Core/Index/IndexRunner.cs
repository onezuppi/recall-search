using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Hashing;
using System.Threading.Channels;
using Recall.Core.Config;
using Recall.Core.Extract;
using Recall.Core.Ignore;
using Recall.Core.Ocr;
using Recall.Core.Scan;

namespace Recall.Core.Index;

public sealed record RunProgress(string Root, long Done, long Total);

/// <summary>
/// Counts and stage times of one pass. Stage times are summed busy time across workers.
/// <see cref="Ocr"/> covers images embedded in documents; OCR of standalone image files is part of <see cref="Extract"/>.
/// </summary>
public sealed record RunReport(
    long Added, long Changed, long Removed, long Hashed, long Extracted, long Deduped, long Failed, long Skipped, long MissingRoots,
    TimeSpan Scan, TimeSpan Hash, TimeSpan Extract, TimeSpan Ocr, TimeSpan Elapsed,
    long DirsListed = 0, long DirsReused = 0);

/// <param name="workers">Files processed at the same time; null selects <see cref="DefaultWorkers"/>. Benchmarks pass 1 for single-thread figures.</param>
/// <param name="fullScanInterval">Longest time a root may go without a full walk in <see cref="ScanMode.Auto"/>; null selects 7 days.</param>
/// <param name="scanThreads">Directories listed at the same time during a scan; null selects <see cref="DefaultScanThreads"/>.</param>
public sealed class IndexRunner(
    IndexStore store, IgnorePolicy policy, IReadOnlyList<IExtractor> extractors, IOcr ocr, int? workers = null,
    TimeSpan? fullScanInterval = null, int? scanThreads = null)
{
    private readonly int _workers = Math.Max(1, workers ?? DefaultWorkers);
    private readonly RootScanPlanner planner = new(store, policy, fullScanInterval, scanThreads ?? DefaultScanThreads);

    private sealed class Counters
    {
        public long Added, Changed, Removed, Hashed, Extracted, Deduped, Failed, Skipped, MissingRoots;
        public long ScanTicks, HashTicks, ExtractTicks, OcrTicks, DirsListed, DirsReused;
        /// <summary>Directories with a file skipped for now; their remembered time is dropped so the next fast pass lists them.</summary>
        public readonly ConcurrentDictionary<string, byte> RetryDirectories = new(StringComparer.OrdinalIgnoreCase);
        public readonly ConcurrentDictionary<UInt128, TaskCompletionSource<long>> Inflight = new();
    }

    /// <summary>Directory listing is mostly waiting for the disk, so more threads than cores help. Measured: 1 thread 4 min 39 s, 8 threads 1 min 18 s, 16 threads 45 s on 200k folders.</summary>
    public static int DefaultScanThreads { get; } = Math.Clamp(Environment.ProcessorCount, 4, 16);

    /// <summary>Number of files processed at the same time. OCR pools should be at least this large.</summary>
    public static int DefaultWorkers { get; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <param name="hints">What the change journal said about each root; null when there is no journal.</param>
    /// <param name="mode">Full (default) walks every directory. Auto skips directories unchanged since the last pass; see <see cref="RootScanPlanner"/>.</param>
    public async Task<RunReport> RunAsync(
        IReadOnlyList<RootFolder> roots, IProgress<RunProgress>? progress, CancellationToken ct, ScanMode mode = ScanMode.Full,
        RootHints? hints = null)
    {
        var total = Stopwatch.StartNew();
        var c = new Counters();
        foreach (var root in roots.OrderByDescending(r => r.Watched))
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var rootPath = Path.GetFullPath(root.Path).TrimEnd('\\');
            // An unplugged drive or an unavailable cloud folder looks like "everything was deleted": keep the index as it is.
            if (!Directory.Exists(rootPath)) { c.MissingRoots++; continue; }
            var walk = planner.Begin(rootPath, mode, hints);
            var scanned = await Task.Run(() => walk.Scan(root, ct), ct);
            var changes = ChangeSet.Diff(scanned, store.Snapshot(rootPath), walk.UnchangedDirectories);
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
        return Report(c, total.Elapsed);
    }

    /// <summary>Indexes specific files (watcher events). The caller has already applied the ignore policy. Missing files are removed from the index.</summary>
    public async Task<RunReport> IndexFilesAsync(RootFolder root, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var c = new Counters();
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
        return Report(c, total.Elapsed);
    }

    private static RunReport Report(Counters c, TimeSpan elapsed) => new(
        c.Added, c.Changed, c.Removed, c.Hashed, c.Extracted, c.Deduped, c.Failed, c.Skipped, c.MissingRoots,
        TimeSpan.FromTicks(c.ScanTicks), TimeSpan.FromTicks(c.HashTicks), TimeSpan.FromTicks(c.ExtractTicks),
        TimeSpan.FromTicks(c.OcrTicks), elapsed, c.DirsListed, c.DirsReused);

    private async Task ProcessAsync(List<FileRecord> work, string root, Counters c, IProgress<RunProgress>? progress, CancellationToken ct)
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

    private async Task ProcessFileAsync(FileRecord f, string root, Counters c, CancellationToken ct)
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
        catch (ExtractException e) when (e.Transient) { Retry(f, c); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Retry(f, c); }
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

    private static void Retry(FileRecord f, Counters c)
    {
        Interlocked.Increment(ref c.Skipped);
        c.RetryDirectories.TryAdd(Path.GetDirectoryName(f.Path)!, 0);
    }

    private async Task<long> ExtractAndStoreAsync(FileRecord f, UInt128 hash, Counters c, CancellationToken ct)
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
