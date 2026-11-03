using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using Recall.Core.Extract;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Recall.Core.Tests;

/// <summary>Some generators write xml:space="" (invalid per the XML spec). System.Xml rejects it, Excel and Word apparently do not.</summary>
public class OfficeXmlRepairTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-rep-" + Guid.NewGuid().ToString("N"));

    public OfficeXmlRepairTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string MakeXlsx(string text)
    {
        var path = Path.Combine(_dir, "a.xlsx");
        using var doc = SpreadsheetDocument.Create(path, DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
        var wb = doc.AddWorkbookPart();
        wb.Workbook = new S.Workbook();
        var ws = wb.AddNewPart<WorksheetPart>();
        ws.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(
            new S.Cell { CellReference = "A1", DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(text)) })));
        wb.Workbook.AppendChild(new S.Sheets(new S.Sheet { Name = "Data", SheetId = 1, Id = wb.GetIdOfPart(ws) }));
        return path;
    }

    private string MakeDocx(string text)
    {
        var path = Path.Combine(_dir, "a.docx");
        using var doc = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        doc.AddMainDocumentPart().Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text(text)))));
        return path;
    }

    /// <summary>Rewrites every text element of every XML part as <c>&lt;t xml:space=""&gt;</c>.</summary>
    private static void InjectEmptyXmlSpace(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml")).ToList())
        {
            string xml;
            using (var r = new StreamReader(entry.Open(), Encoding.UTF8)) xml = r.ReadToEnd();
            var patched = Regex.Replace(xml, @"<((?:\w+:)?t)>", "<$1 xml:space=\"\">");
            if (patched == xml) continue;
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.BaseStream.SetLength(0);
            w.Write(patched);
        }
    }

    [Fact]
    public async Task Xlsx_with_empty_xml_space_is_read()
    {
        var path = MakeXlsx("Цифровой рубль spec");
        InjectEmptyXmlSpace(path);
        var r = await new OpenXmlExtractor().ExtractAsync(path, default);
        Assert.Contains("Цифровой рубль", r.Segments.Single(s => s.Locator == "sheet:Data").Text);
    }

    [Fact]
    public async Task Docx_with_empty_xml_space_is_read()
    {
        var path = MakeDocx("Договор поставки");
        InjectEmptyXmlSpace(path);
        var r = await new OpenXmlExtractor().ExtractAsync(path, default);
        Assert.Contains("Договор поставки", r.Segments.Single(s => s.Locator == "").Text);
    }

    [Fact]
    public async Task The_original_file_is_not_modified_by_reading()
    {
        var path = MakeXlsx("keep me");
        InjectEmptyXmlSpace(path);
        var before = File.ReadAllBytes(path);
        await new OpenXmlExtractor().ExtractAsync(path, default);
        Assert.Equal(before, File.ReadAllBytes(path));
    }
}
