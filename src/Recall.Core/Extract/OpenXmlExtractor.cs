using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Recall.Core.Index;
using A = DocumentFormat.OpenXml.Drawing;

namespace Recall.Core.Extract;

public sealed class OpenXmlExtractor : IExtractor
{
    public bool CanHandle(string extension) =>
        extension.Equals(".docx", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase);

    public Task<ExtractResult> ExtractAsync(string path, CancellationToken ct)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var segments = new List<Segment>();
            var images = new List<EmbeddedImage>();
            switch (ext)
            {
                case ".docx": Docx(path, segments, images); break;
                case ".pptx": Pptx(path, segments, images, ct); break;
                default: Xlsx(path, segments, images, ct); break;
            }
            return Task.FromResult(new ExtractResult(segments, images));
        }
        catch (OperationCanceledException) { throw; }
        catch (ExtractException) { throw; }
        catch (Exception e) { throw new ExtractException("cannot read Office file: " + e.Message, e); }
    }

    private static void AddImages(IEnumerable<ImagePart> parts, string prefix, List<EmbeddedImage> images)
    {
        var k = 0;
        foreach (var part in parts)
        {
            using var s = part.GetStream();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            if (ImageFilter.IsTiny(bytes)) continue;
            k++;
            images.Add(new EmbeddedImage($"{prefix}{k}", bytes));
        }
    }

    private static void Docx(string path, List<Segment> segments, List<EmbeddedImage> images)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var main = doc.MainDocumentPart ?? throw new ExtractException("no main document part");
        var text = string.Join("\n", main.Document?.Body?.Descendants<Paragraph>().Select(p => p.InnerText).Where(t => t.Length > 0) ?? []);
        segments.Add(new Segment("", text));
        AddImages(main.ImageParts, "image:", images);
    }

    private static void Pptx(string path, List<Segment> segments, List<EmbeddedImage> images, CancellationToken ct)
    {
        using var doc = PresentationDocument.Open(path, false);
        var pres = doc.PresentationPart ?? throw new ExtractException("no presentation part");
        var ids = pres.Presentation?.SlideIdList?.Elements<SlideId>().ToList() ?? [];
        var n = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            n++;
            if (id.RelationshipId?.Value is not { } rid || pres.GetPartById(rid) is not SlidePart slide) continue;
            var text = string.Join("\n", (slide.Slide?.Descendants<A.Paragraph>().Select(p => p.InnerText).Where(t => t.Length > 0) ?? []));
            segments.Add(new Segment($"slide:{n}", text));
            AddImages(slide.ImageParts, $"image:slide:{n}:", images);
        }
    }

    private static void Xlsx(string path, List<Segment> segments, List<EmbeddedImage> images, CancellationToken ct)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart ?? throw new ExtractException("no workbook part");
        var shared = wb.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(i => i.InnerText).ToList() ?? [];
        foreach (var sheet in wb.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            ct.ThrowIfCancellationRequested();
            if (sheet.Id?.Value is not { } rid || wb.GetPartById(rid) is not WorksheetPart ws) continue;
            var lines = new List<string>();
            foreach (var row in ws.Worksheet?.Descendants<Row>() ?? [])
            {
                var cells = new List<string>();
                foreach (var c in row.Elements<Cell>())
                {
                    string? v = null;
                    if (c.DataType?.Value == CellValues.SharedString && int.TryParse(c.CellValue?.Text, out var idx) && idx >= 0 && idx < shared.Count)
                        v = shared[idx];
                    else if (c.DataType?.Value == CellValues.InlineString) v = c.InlineString?.InnerText;
                    else if (c.DataType?.Value == CellValues.String) v = c.CellValue?.Text;
                    if (!string.IsNullOrEmpty(v)) cells.Add(v);
                }
                if (cells.Count > 0) lines.Add(string.Join(" ", cells));
            }
            var name = sheet.Name?.Value ?? "sheet";
            segments.Add(new Segment($"sheet:{name}", string.Join("\n", lines)));
            if (ws.DrawingsPart != null) AddImages(ws.DrawingsPart.ImageParts, $"image:sheet:{name}:", images);
        }
    }
}
