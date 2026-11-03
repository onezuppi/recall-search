using System.Text;
using Recall.Core.Index;

namespace Recall.Core.Extract;

public sealed class TextExtractor : IExtractor
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    static TextExtractor() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public bool CanHandle(string extension) =>
        extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) || extension.Equals(".md", StringComparison.OrdinalIgnoreCase);

    public Task<ExtractResult> ExtractAsync(string path, CancellationToken ct)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ExtractException("cannot read file", e); }
        return Task.FromResult(new ExtractResult([new Segment("", Decode(bytes))], []));
    }

    internal static string Decode(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        try { return StrictUtf8.GetString(b); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1251).GetString(b); }
    }
}
