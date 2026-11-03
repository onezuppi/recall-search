using System.Runtime.InteropServices.WindowsRuntime;
using Recall.Core.Extract;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Recall.Core.Ocr;

/// <summary>Windows.Media.Ocr with one engine per installed language; texts are merged and duplicate lines removed.</summary>
public sealed class WindowsOcr : IOcr
{
    private readonly List<(string Tag, OcrEngine Engine)> _engines = [];

    public WindowsOcr(IReadOnlyList<string> languages)
    {
        foreach (var tag in languages)
        {
            try
            {
                var lang = new Language(tag);
                if (!OcrEngine.IsLanguageSupported(lang)) continue;
                var engine = OcrEngine.TryCreateFromLanguage(lang);
                if (engine != null) _engines.Add((tag, engine));
            }
            catch (Exception) { /* invalid tag or language pack missing */ }
        }
        if (_engines.Count == 0)
        {
            var fallback = OcrEngine.TryCreateFromUserProfileLanguages();
            if (fallback != null) _engines.Add((fallback.RecognizerLanguage.LanguageTag, fallback));
        }
    }

    /// <summary>Configured languages that have an OCR engine on this machine.</summary>
    public IReadOnlyList<string> AvailableLanguages => _engines.Select(e => e.Tag).ToList();

    public async Task<string> RecognizeAsync(byte[] image, CancellationToken ct)
    {
        if (_engines.Count == 0) throw new ExtractException("no OCR language is installed");
        SoftwareBitmap bitmap;
        try { bitmap = await DecodeAsync(image, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { throw new ExtractException("unsupported or corrupt image: " + e.Message, e); }

        using (bitmap)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();
            foreach (var (_, engine) in _engines)
            {
                ct.ThrowIfCancellationRequested();
                var result = await engine.RecognizeAsync(bitmap).AsTask(ct);
                foreach (var line in result.Lines)
                {
                    var t = line.Text.Trim();
                    if (t.Length > 0 && seen.Add(t)) lines.Add(t);
                }
            }
            return string.Join("\n", lines);
        }
    }

    private static async Task<SoftwareBitmap> DecodeAsync(byte[] bytes, CancellationToken ct)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer()).AsTask(ct);
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
        var max = (double)OcrEngine.MaxImageDimension;
        var w = decoder.PixelWidth;
        var h = decoder.PixelHeight;
        var longer = Math.Max(w, h);
        if (longer <= max)
            return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(ct);
        var scale = max / longer;
        var transform = new BitmapTransform
        {
            ScaledWidth = Math.Max(1, (uint)(w * scale)),
            ScaledHeight = Math.Max(1, (uint)(h * scale)),
        };
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);
    }
}
