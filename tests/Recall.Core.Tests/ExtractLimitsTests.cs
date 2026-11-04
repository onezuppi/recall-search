using System.IO.Compression;
using Recall.Core.Extract;

namespace Recall.Core.Tests;

public class ExtractLimitsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-lim-" + Guid.NewGuid().ToString("N"));

    public ExtractLimitsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Images_past_the_count_budget_are_dropped_but_the_first_ones_are_kept()
    {
        var budget = new ImageBudget();
        for (var i = 0; i < ExtractLimits.MaxImages; i++) Assert.True(budget.TryAdd("i" + i, [1]));

        Assert.True(budget.IsFull);
        Assert.False(budget.TryAdd("late", [1]));
        Assert.Equal(ExtractLimits.MaxImages, budget.Images.Count);
    }

    [Fact]
    public void One_oversized_image_is_dropped_without_blocking_the_next_one()
    {
        var budget = new ImageBudget();

        Assert.False(budget.TryAdd("huge", new byte[ExtractLimits.MaxImageBytes + 1]));
        Assert.True(budget.TryAdd("small", [1, 2, 3]));
        Assert.Single(budget.Images);
    }

    [Fact]
    public void An_archive_that_unpacks_past_the_limit_is_refused_before_it_is_read()
    {
        var path = Path.Combine(_dir, "bomb.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var w = zip.CreateEntry("word/document.xml", CompressionLevel.SmallestSize).Open())
        {
            var block = new byte[1 << 20];
            for (var i = 0; i < 1100; i++) w.Write(block);   // 1.1 GB of zeros, a few MB on disk
        }

        var e = Assert.Throws<ExtractException>(() => ExtractLimits.CheckArchive(path));

        Assert.False(e.Transient);
        Assert.Contains("limit", e.Message);
    }

    [Fact]
    public void A_normal_archive_passes_the_check()
    {
        var path = Path.Combine(_dir, "ok.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("a.xml").Open())) w.Write("<a/>");

        ExtractLimits.CheckArchive(path);
    }
}

public class RootPathTests
{
    [Fact]
    public void A_drive_root_keeps_its_separator_because_a_bare_drive_letter_means_the_current_folder()
    {
        Assert.Equal(@"C:\", Recall.Core.Scan.RootPath.Normalize(@"C:\"));
        Assert.Equal(@"C:\", Recall.Core.Scan.RootPath.Normalize("C:/"));
    }

    [Fact]
    public void Other_roots_lose_the_trailing_separator()
    {
        Assert.Equal(@"C:\Users\x", Recall.Core.Scan.RootPath.Normalize(@"C:\Users\x\"));
        Assert.Equal(@"C:\Users\x", Recall.Core.Scan.RootPath.Normalize(@"C:\Users\x"));
    }
}
