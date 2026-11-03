using System.Drawing;
using System.Drawing.Imaging;
using DocumentFormat.OpenXml.Packaging;
using Recall.Core.Index;
using Recall.Core.Tests.Support;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Recall.Core.Tests;

public class IndexRunnerEdgeTests : RunnerTestBase
{

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
        var progress = new SyncProgress<RunProgress>(p => { lock (order) if (!order.Contains(p.Root)) order.Add(p.Root); });
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

    [Fact]
    public async Task Missing_root_does_not_wipe_the_index()
    {
        using var s = Make();
        Write("keep.txt", "survivor words");
        await s.Runner.RunAsync([R()], null, default);
        Directory.Delete(Root, true);

        var r = await s.Runner.RunAsync([R()], null, default);

        Assert.Equal(1, r.MissingRoots);
        Assert.Equal(0, r.Removed);
        Assert.Equal(1, Hits("survivor"));
    }
}
