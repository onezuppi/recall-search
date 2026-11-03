using Recall.Core.Ignore;
using Recall.Core.Index;
using Recall.Core.Ocr;

namespace Recall.Core.Extract;

/// <summary>Standalone image files: the OCR text becomes one segment. Word boxes are not stored yet.</summary>
public sealed class ImageExtractor(IOcr ocr) : IExtractor
{
    public bool CanHandle(string extension) => BuiltInRules.GroupOf(extension) == BuiltInRules.Images;

    public async Task<ExtractResult> ExtractAsync(string path, CancellationToken ct)
    {
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(path, ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ExtractException("cannot read file", e); }
        var text = await ocr.RecognizeAsync(bytes, ct);
        return new ExtractResult([new Segment("", text)], []);
    }
}
