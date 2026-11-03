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

    [Fact]
    public async Task Recognizes_rendered_english_digits()
    {
        var ocr = new WindowsOcr(["en-US", "ru"]);
        Assert.NotEmpty(ocr.AvailableLanguages);
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

    [Fact]
    public async Task ImageExtractor_returns_one_segment_for_a_png_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "rs-ocr-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            File.WriteAllBytes(path, Render("Receipt 9137"));
            var ex = new ImageExtractor(new WindowsOcr(["en-US"]));
            Assert.True(ex.CanHandle(".PNG"));
            Assert.False(ex.CanHandle(".pdf"));
            var r = await ex.ExtractAsync(path, default);
            Assert.Contains("9137", r.Segments.Single().Text);
        }
        finally { File.Delete(path); }
    }
}
