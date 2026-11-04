using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using Recall.Core.Extract;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Recall.Core.Ocr;

/// <summary>
/// Windows.Media.Ocr with one engine pool per installed language; texts are merged and duplicate lines removed.
/// A single OcrEngine rejects overlapping RecognizeAsync calls, so each call borrows an engine from the pool.
/// </summary>
public sealed class WindowsOcr : IOcr
{
    private readonly List<(string Tag, Channel<OcrEngine> Pool)> _pools = [];

    /// <param name="concurrency">Engines per language, i.e. how many images can be recognized at the same time.</param>
    public WindowsOcr(IReadOnlyList<string> languages, int concurrency = 1)
    {
        concurrency = Math.Max(1, concurrency);
        foreach (var tag in languages)
        {
            try
            {
                var lang = new Language(tag);
                if (OcrEngine.IsLanguageSupported(lang)) AddPool(tag, () => OcrEngine.TryCreateFromLanguage(lang), concurrency);
            }
            catch (Exception) { /* invalid tag or language pack missing */ }
        }
        if (_pools.Count == 0)
        {
            var probe = OcrEngine.TryCreateFromUserProfileLanguages();
            if (probe != null)
                AddPool(probe.RecognizerLanguage.LanguageTag, OcrEngine.TryCreateFromUserProfileLanguages, concurrency);
        }
    }

    /// <summary>Configured languages that have an OCR engine on this machine.</summary>
    public IReadOnlyList<string> AvailableLanguages => _pools.Select(p => p.Tag).ToList();

    private void AddPool(string tag, Func<OcrEngine?> create, int size)
    {
        var pool = Channel.CreateBounded<OcrEngine>(size);
        for (var i = 0; i < size; i++)
        {
            var engine = create();
            if (engine == null) return;
            pool.Writer.TryWrite(engine);
        }
        _pools.Add((tag, pool));
    }

    public async Task<string> RecognizeAsync(byte[] image, CancellationToken ct)
    {
        if (_pools.Count == 0) throw new ExtractException("no OCR language is installed", transient: true);
        SoftwareBitmap bitmap;
        try { bitmap = await DecodeAsync(image, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { throw new ExtractException("unsupported or corrupt image: " + e.Message, e); }

        using (bitmap)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();
            foreach (var (_, pool) in _pools)
            {
                var engine = await pool.Reader.ReadAsync(ct);
                try
                {
                    var result = await engine.RecognizeAsync(bitmap).AsTask(ct);
                    foreach (var line in result.Lines)
                    {
                        var t = line.Text.Trim();
                        if (t.Length > 0 && seen.Add(t)) lines.Add(t);
                    }
                }
                finally { pool.Writer.TryWrite(engine); }
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
