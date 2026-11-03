using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Recall.Core.Extract;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using A = DocumentFormat.OpenXml.Drawing;

namespace Recall.Core.Tests;

public class ExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-ext-" + Guid.NewGuid().ToString("N"));

    public ExtractorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string P(string name) => Path.Combine(_dir, name);

    private static byte[] NoisePng(int w, int h)
    {
        var rnd = new Random(1);
        using var bmp = new Bitmap(w, h);
        for (var x = 0; x < w; x++)
            for (var y = 0; y < h; y++)
                bmp.SetPixel(x, y, System.Drawing.Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)));
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private string MakeDocx(string name, string text, params byte[][] images)
    {
        var path = P(name);
        using (var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text(text)))));
            foreach (var img in images)
            {
                var part = main.AddImagePart(ImagePartType.Png);
                using var ms = new MemoryStream(img);
                part.FeedData(ms);
            }
        }
        return path;
    }

    [Fact]
    public async Task Docx_text_and_embedded_png_are_extracted()
    {
        var path = MakeDocx("a.docx", "Quarterly report", NoisePng(120, 120));
        var r = await new OpenXmlExtractor().ExtractAsync(path, default);
        Assert.Contains("Quarterly report", r.Segments.Single(s => s.Locator == "").Text);
        var img = Assert.Single(r.Images);
        Assert.Equal("image:1", img.Locator);
    }

    [Fact]
    public async Task Tiny_images_are_dropped()
    {
        var path = MakeDocx("t.docx", "x", NoisePng(10, 10), NoisePng(120, 120));
        var r = await new OpenXmlExtractor().ExtractAsync(path, default);
        Assert.Single(r.Images);
    }

    [Fact]
    public async Task Pptx_text_has_slide_locators()
    {
        var path = P("a.pptx");
        using (var doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation))
        {
            var pres = doc.AddPresentationPart();
            pres.Presentation = new P.Presentation(new P.SlideIdList());
            uint id = 256;
            foreach (var t in new[] { "First slide words", "Second slide words" })
            {
                var sp = pres.AddNewPart<SlidePart>();
                sp.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(new P.Shape(new P.TextBody(
                    new A.BodyProperties(), new A.Paragraph(new A.Run(new A.Text(t))))))));
                pres.Presentation.SlideIdList!.Append(new P.SlideId { Id = id++, RelationshipId = pres.GetIdOfPart(sp) });
            }
        }
        var r = await new OpenXmlExtractor().ExtractAsync(path, default);
        Assert.Contains("First", r.Segments.Single(s => s.Locator == "slide:1").Text);
        Assert.Contains("Second", r.Segments.Single(s => s.Locator == "slide:2").Text);
    }

    [Fact]
    public async Task Xlsx_shared_strings_are_extracted()
    {
        var path = P("a.xlsx");
        using (var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
        {
            var wb = doc.AddWorkbookPart();
            wb.Workbook = new S.Workbook();
            var sst = wb.AddNewPart<SharedStringTablePart>();
            sst.SharedStringTable = new S.SharedStringTable(
                new S.SharedStringItem(new S.Text("Budget Moscow")));
            var ws = wb.AddNewPart<WorksheetPart>();
            ws.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(
                new S.Cell { CellReference = "A1", DataType = S.CellValues.SharedString, CellValue = new S.CellValue("0") })));
            wb.Workbook.AppendChild(new S.Sheets(new S.Sheet { Name = "Data", SheetId = 1, Id = wb.GetIdOfPart(ws) }));
        }
        var r = await new OpenXmlExtractor().ExtractAsync(path, default);
        Assert.Contains("Budget Moscow", r.Segments.Single(s => s.Locator == "sheet:Data").Text);
    }

    [Fact]
    public async Task Pdf_text_has_page_locators()
    {
        var path = P("a.pdf");
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        b.AddPage(595, 842).AddText("Hello first page", 14, new PdfPoint(50, 700), font);
        b.AddPage(595, 842).AddText("Second page here", 14, new PdfPoint(50, 700), font);
        File.WriteAllBytes(path, b.Build());
        var r = await new PdfExtractor().ExtractAsync(path, default);
        Assert.Contains("Hello", r.Segments.Single(s => s.Locator == "page:1").Text);
        Assert.Contains("Second", r.Segments.Single(s => s.Locator == "page:2").Text);
    }

    [Fact]
    public async Task Pdf_page_image_is_passed_for_ocr()
    {
        var path = P("scan.pdf");
        var b = new PdfDocumentBuilder();
        var page = b.AddPage(595, 842);
        page.AddPng(NoisePng(200, 200), new PdfRectangle(0, 0, 595, 842));
        File.WriteAllBytes(path, b.Build());
        var r = await new PdfExtractor().ExtractAsync(path, default);
        var img = Assert.Single(r.Images);
        Assert.Equal("image:page:1:1", img.Locator);
    }

    [Fact]
    public async Task Txt_in_cp1251_is_decoded()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var path = P("old.txt");
        File.WriteAllBytes(path, Encoding.GetEncoding(1251).GetBytes("Привет, мир"));
        var r = await new TextExtractor().ExtractAsync(path, default);
        Assert.Equal("Привет, мир", r.Segments.Single().Text);
    }

    [Fact]
    public async Task Txt_utf8_with_bom_is_decoded()
    {
        var path = P("u.md");
        File.WriteAllBytes(path, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("Привет")]);
        var r = await new TextExtractor().ExtractAsync(path, default);
        Assert.Equal("Привет", r.Segments.Single().Text);
    }

    [Theory]
    [InlineData("bad.docx")]
    [InlineData("bad.pdf")]
    public async Task Corrupt_file_throws_ExtractException(string name)
    {
        var path = P(name);
        File.WriteAllBytes(path, new byte[2000].Select((_, i) => (byte)(i * 31)).ToArray());
        IExtractor ex = name.EndsWith("pdf") ? new PdfExtractor() : new OpenXmlExtractor();
        await Assert.ThrowsAsync<ExtractException>(() => ex.ExtractAsync(path, default));
    }
}
