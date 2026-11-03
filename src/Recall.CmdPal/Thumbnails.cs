using System.Security.Cryptography;
using System.Text;
using Recall.Core.Ipc;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Recall.CmdPal;

/// <summary>
/// Pane-sized copies (fitted into a box, so tall phone screenshots do not scroll the pane) of found images. The markdown pane draws a picture at its pixel size, so a screenshot
/// has to be scaled down before it is shown. Copies live in the package's real local folder, which Command Palette can read.
/// </summary>
internal static class Thumbnails
{
    private const double MaxWidth = 220, MaxHeight = 220;
    private const int Parallelism = 4;

    private static readonly Lazy<string> Folder = new(() =>
    {
        var dir = Path.Combine(ApplicationData.Current.LocalFolder.Path, "thumbs");
        Directory.CreateDirectory(dir);
        return dir;
    });

    /// <summary>Thumbnail file per image hit path. Images that cannot be decoded are left out.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> ForAsync(IEnumerable<IpcHit> hits, CancellationToken ct)
    {
        var result = new Dictionary<string, string>();
        using var gate = new SemaphoreSlim(Parallelism);
        var tasks = hits.Where(h => h.Group == "images").Select(async h =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var file = await TryCreateAsync(h);
                if (file is not null) lock (result) result[h.Path] = file;
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        return result;
    }

    private static async Task<string?> TryCreateAsync(IpcHit hit)
    {
        try
        {
            var target = Path.Combine(Folder.Value, Key(hit) + ".jpg");
            if (File.Exists(target)) return target;
            var bytes = await ScaleAsync(hit.Path);
            var temp = target + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, target, overwrite: true);
            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ScaleAsync(string path)
    {
        await using var input = File.OpenRead(path);
        var decoder = await BitmapDecoder.CreateAsync(input.AsRandomAccessStream());
        var scale = Math.Min(1.0, Math.Min(MaxWidth / decoder.PixelWidth, MaxHeight / decoder.PixelHeight));
        var width = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
        var height = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
        var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant };
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);

        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync();
        var bytes = new byte[output.Size];
        output.Seek(0);
        await output.AsStream().ReadExactlyAsync(bytes);
        return bytes;
    }

    private static string Key(IpcHit hit) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{hit.Path}|{hit.Modified.Ticks}|{hit.Size}|{MaxWidth}x{MaxHeight}")));
}
