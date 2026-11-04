using System.Drawing;
using System.Drawing.Imaging;
using Recall.Core.Extract;
using Recall.Core.Ocr;

namespace Recall.Core.Tests;

public class OcrTests
{
    private static byte[] Render(string text, int size = 36)
    {
        using var bmp = new Bitmap(900, 120);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new Font("Arial", size, GraphicsUnit.Pixel);
            g.DrawString(text, font, Brushes.Black, 10, 30);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    /// <summary>An engine for English, or a skipped test when the machine (a clean CI runner, for one) has no English OCR pack.</summary>
    private static WindowsOcr RequireEnglish(int concurrency = 1)
    {
        var ocr = new WindowsOcr(["en-US"], concurrency: concurrency);
        Skip.IfNot(ocr.AvailableLanguages.Any(l => l.StartsWith("en", StringComparison.OrdinalIgnoreCase)), "English OCR language pack is not installed");
        return ocr;
    }

    [SkippableFact]
    public async Task Recognizes_rendered_english_digits()
    {
        var ocr = RequireEnglish();
        var text = await ocr.RecognizeAsync(Render("Invoice 4821 total"), default);
        Assert.Contains("4821", text);
    }

    [SkippableFact]
    public async Task Recognizes_rendered_russian_text()
    {
        var ocr = new WindowsOcr(["ru", "en-US"]);
        Skip.IfNot(ocr.AvailableLanguages.Contains("ru"), "Russian OCR language pack is not installed");
        var text = await ocr.RecognizeAsync(Render("Счёт оплачен"), default);
        Assert.Contains("оплачен", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unsupported_bytes_throw_ExtractException()
    {
        var ocr = new WindowsOcr(["en-US"]);
        await Assert.ThrowsAsync<ExtractException>(() => ocr.RecognizeAsync([1, 2, 3, 4, 5], default));
    }

    [SkippableFact]
    public async Task ImageExtractor_returns_one_segment_for_a_png_file()
    {
        var ocr = RequireEnglish();
        var path = Path.Combine(Path.GetTempPath(), "rs-ocr-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            File.WriteAllBytes(path, Render("Receipt 9137"));
            var ex = new ImageExtractor(ocr);
            Assert.True(ex.CanHandle(".PNG"));
            Assert.False(ex.CanHandle(".pdf"));
            var r = await ex.ExtractAsync(path, default);
            Assert.Contains("9137", r.Segments.Single().Text);
        }
        finally { File.Delete(path); }
    }

    [SkippableFact]
    public async Task Concurrent_calls_on_one_instance_all_succeed()
    {
        var ocr = RequireEnglish(concurrency: 3);
        var image = Render("Order 7305 shipped");
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => ocr.RecognizeAsync(image, default))));
        Assert.All(results, t => Assert.Contains("7305", t));
    }
}
