using System.Drawing;
using Recall.Core.Index;

namespace Recall.Core.Extract;

public sealed record EmbeddedImage(string Locator, byte[] Bytes);

public sealed record ExtractResult(IReadOnlyList<Segment> Segments, IReadOnlyList<EmbeddedImage> Images);

public interface IExtractor
{
    bool CanHandle(string extension);
    Task<ExtractResult> ExtractAsync(string path, CancellationToken ct);
}

/// <summary>The file cannot be read as its type. <see cref="Transient"/> marks I/O failures (a locked file) that may succeed later.</summary>
public sealed class ExtractException(string reason, Exception? inner = null) : Exception(reason, inner)
{
    public bool Transient => InnerException is IOException && InnerException is not FileNotFoundException;
}

internal static class ImageFilter
{
    private const int MinBytes = 4 * 1024;
    private const int MinSide = 64;

    /// <summary>Icons, bullets and spacers: under 4 KB or under 64x64 px. Formats System.Drawing cannot decode are kept.</summary>
    public static bool IsTiny(byte[] bytes)
    {
        if (bytes.Length < MinBytes) return true;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
            return img.Width < MinSide || img.Height < MinSide;
        }
        catch (Exception) { return false; }
    }
}
