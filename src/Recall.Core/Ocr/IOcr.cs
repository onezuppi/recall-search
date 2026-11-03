namespace Recall.Core.Ocr;

public interface IOcr
{
    /// <summary>Recognized text of an encoded image (png, jpeg, ...). Throws <see cref="Extract.ExtractException"/> when the bytes are not a readable image.</summary>
    Task<string> RecognizeAsync(byte[] image, CancellationToken ct);
}
