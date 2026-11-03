using System.Drawing;
using System.Drawing.Imaging;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Data.Sqlite;
using Recall.Core.Config;
using Recall.Core.Extract;
using Recall.Core.Ignore;
using Recall.Core.Index;
using Recall.Core.Ocr;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Recall.Core.Tests;

public class IndexRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-run-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_dir, "docs");
    private string Db => Path.Combine(_dir, "index.db");

    public IndexRunnerTests() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class CountingExtractor(IExtractor inner) : IExtractor
    {
        public int Calls;
        public bool CanHandle(string extension) => inner.CanHandle(extension);
        public Task<ExtractResult> ExtractAsync(string path, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return inner.ExtractAsync(path, ct);
        }
    }

    private sealed class FakeOcr(string text) : IOcr
    {
        public Task<string> RecognizeAsync(byte[] image, CancellationToken ct) => Task.FromResult(text);
    }

    private sealed record Setup(IndexStore Store, IndexRunner Runner, CountingExtractor Text, CountingExtractor Office, RecallConfig Config) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }

    private Setup Make(string ocrText = "ocr text")
    {
        var cfg = new RecallConfig([], new HashSet<string>(["images", "pdf", "office", "text"], StringComparer.OrdinalIgnoreCase), []);
        var store = IndexStore.Open(Db);
        var text = new CountingExtractor(new TextExtractor());
        var office = new CountingExtractor(new OpenXmlExtractor());
        var runner = new IndexRunner(store, new IgnorePolicy(cfg, _ => false), [text, office], new FakeOcr(ocrText));
        return new Setup(store, runner, text, office, cfg);
    }

    private RootFolder R(bool watched = false, string? path = null) => new(path ?? Root, watched);

    private string Write(string name, string content, string? dir = null)
    {
        var p = Path.Combine(dir ?? Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    private long Hits(string phrase)
    {
        using var c = new SqliteConnection($"Data Source={Db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM segments_fts WHERE segments_fts MATCH $q";
        cmd.Parameters.AddWithValue("$q", "\"" + phrase + "\"");
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task First_run_indexes_all_then_second_run_extracts_nothing()
    {
        using var s = Make();
        Write("a.txt", "alpha document");
        Write("b.md", "bravo document");
        var r1 = await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(2, r1.Added);
        Assert.Equal(2, r1.Extracted);
        var calls = s.Text.Calls;
        Assert.Equal(2, calls);

        var r2 = await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(0, r2.Extracted);
        Assert.Equal(0, r2.Hashed);
        Assert.Equal(0, r2.Added + r2.Changed + r2.Removed);
        Assert.Equal(calls, s.Text.Calls);
        Assert.Equal((2L, 0L), s.Store.Counts());
    }

    [Fact]
    public async Task Modified_file_is_reextracted_and_old_text_is_gone()
    {
        using var s = Make();
        var p = Write("a.txt", "oldword here");
        await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(1, Hits("oldword"));
        File.WriteAllText(p, "newword here, longer");
        var r = await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(1, r.Changed);
        Assert.Equal(0, Hits("oldword"));
        Assert.Equal(1, Hits("newword"));
    }

    [Fact]
    public async Task Deleted_file_is_removed_from_search()
    {
        using var s = Make();
        var p = Write("a.txt", "vanishing words");
        await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(1, Hits("vanishing"));
        File.Delete(p);
        var r = await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(1, r.Removed);
        Assert.Equal(0, Hits("vanishing"));
    }

    [Fact]
    public async Task Copy_of_a_file_is_deduped()
    {
        using var s = Make();
        Write("a.txt", "same bytes in both");
        Write("copy of a.txt", "same bytes in both");
        File.Move(Path.Combine(Root, "copy of a.txt"), Path.Combine(Root, "b.txt"));
        var r = await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(1, r.Deduped);
        Assert.Equal(1, r.Extracted);
        Assert.Equal(1, s.Text.Calls);
        Assert.Equal((2L, 0L), s.Store.Counts());
    }

    [Fact]
    public async Task Corrupt_file_is_recorded_failed_and_not_retried_until_changed()
    {
        using var s = Make();
        var bad = Path.Combine(Root, "bad.docx");
        File.WriteAllBytes(bad, new byte[3000].Select((_, i) => (byte)(i * 7)).ToArray());
        Write("good.txt", "fine file");
        var r1 = await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(1, r1.Failed);
        Assert.Equal(1, s.Office.Calls);
        Assert.Equal((1L, 1L), s.Store.Counts());

        await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(1, s.Office.Calls);

        File.WriteAllBytes(bad, new byte[3500].Select((_, i) => (byte)(i * 11)).ToArray());
        var r3 = await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(1, s.Office.Calls - 1);
        Assert.Equal(1, r3.Failed);
    }

    [Fact]
    public async Task Locked_file_is_skipped_and_picked_up_on_the_next_run()
    {
        using var s = Make();
        var p = Write("locked.txt", "secret words inside");
        using (new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var r = await s.Runner.RunAsync([R()], null, default);
            Assert.Equal(0, r.Failed);
            Assert.Equal(1, r.Skipped);
        }
        Assert.Equal((0L, 0L), s.Store.Counts());
        await s.Runner.RunAsync([R()], null, default);
        Assert.Equal((1L, 0L), s.Store.Counts());
    }

    [Fact]
    public async Task Docx_embedded_image_text_is_searchable()
    {
        using var s = Make("скрин внутри");
        var path = Path.Combine(Root, "with-image.docx");
        var rnd = new Random(3);
        using var bmp = new Bitmap(120, 120);
        for (var x = 0; x < 120; x++)
            for (var y = 0; y < 120; y++)
                bmp.SetPixel(x, y, System.Drawing.Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)));
        using var png = new MemoryStream();
        bmp.Save(png, ImageFormat.Png);
        png.Position = 0;
        using (var doc = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text("body text")))));
            main.AddImagePart(ImagePartType.Png).FeedData(png);
        }
        var r = await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(1, r.Extracted);
        Assert.Equal(1, Hits("крин вну"));
        Assert.True(r.Ocr > TimeSpan.Zero || r.Ocr == TimeSpan.Zero);
    }

    [Fact]
    public async Task Watched_roots_run_first()
    {
        using var s = Make();
        var other = Path.Combine(_dir, "other");
        Write("a.txt", "in the normal root");
        Write("b.txt", "in the watched root", other);
        var order = new List<string>();
        var progress = new SyncProgress(p => { lock (order) if (!order.Contains(p.Root)) order.Add(p.Root); });
        await s.Runner.RunAsync([R(false), R(true, other)], progress, default);
        Assert.Equal([other, Root], order);
    }

    [Fact]
    public async Task IndexFilesAsync_adds_changes_and_removes_missing_files()
    {
        using var s = Make();
        var p = Write("w.txt", "watcher word");
        var root = R(true);
        await s.Runner.IndexFilesAsync(root, [p], default);
        Assert.Equal(1, Hits("watcher"));
        var calls = s.Text.Calls;
        await s.Runner.IndexFilesAsync(root, [p], default);
        Assert.Equal(calls, s.Text.Calls);
        File.Delete(p);
        await s.Runner.IndexFilesAsync(root, [p], default);
        Assert.Equal(0, Hits("watcher"));
    }

    private sealed class SyncProgress(Action<RunProgress> a) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => a(value);
    }
}
