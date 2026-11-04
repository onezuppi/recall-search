using Recall.Core.Index;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Recall.Core.Extract;

/// <summary>PDF text layer per page, plus every non-tiny page image. Scanned pages (almost no text, one big image) are covered by the image pass.</summary>
public sealed class PdfExtractor : IExtractor
{
    public bool CanHandle(string extension) => extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public Task<ExtractResult> ExtractAsync(string path, CancellationToken ct)
    {
        try
        {
            var segments = new List<Segment>();
            var images = new ImageBudget();
            using var pdf = PdfDocument.Open(path);
            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                segments.Add(new Segment($"page:{page.Number}", ContentOrderTextExtractor.GetText(page)));
                var k = 0;
                foreach (var img in page.GetImages())
                {
                    if (images.IsFull) break;
                    byte[]? bytes = img.TryGetPng(out var png) ? png : img.RawBytes.ToArray();
                    if (bytes == null || ImageFilter.IsTiny(bytes)) continue;
                    k++;
                    images.TryAdd($"image:page:{page.Number}:{k}", bytes);
                }
            }
            return Task.FromResult(new ExtractResult(segments, images.Images));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { throw new ExtractException("cannot read PDF: " + e.Message, e); }
    }
}
