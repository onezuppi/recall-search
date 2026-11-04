using System.IO.Compression;

namespace Recall.Core.Extract;

/// <summary>
/// Memory guards for one file. A document is held in memory with all its embedded images until the images are recognized,
/// so a zip bomb or a PDF with thousands of scans must not be able to grow without a bound.
/// </summary>
internal static class ExtractLimits
{
    public const long MaxArchiveBytes = 1L << 30;
    public const long MaxImageBytes = 64L << 20;
    public const long MaxImagesBytes = 256L << 20;
    public const int MaxImages = 500;

    /// <summary>Refuses an Office archive whose entries claim more than <see cref="MaxArchiveBytes"/> once unpacked.</summary>
    public static void CheckArchive(string path)
    {
        long total = 0;
        using var zip = ZipFile.OpenRead(path);
        foreach (var entry in zip.Entries) total += entry.Length;
        if (total > MaxArchiveBytes)
            throw new ExtractException($"archive unpacks to {total >> 20} MB, more than the {MaxArchiveBytes >> 20} MB limit");
    }
}

/// <summary>Collects the embedded images of one file and drops those past the count or size budget; the document text is kept anyway.</summary>
internal sealed class ImageBudget
{
    private long _bytes;

    public List<EmbeddedImage> Images { get; } = [];

    /// <summary>True when no further image can be kept, so the caller may stop decoding them.</summary>
    public bool IsFull => Images.Count >= ExtractLimits.MaxImages || _bytes >= ExtractLimits.MaxImagesBytes;

    /// <returns>False when the image was over budget and was not kept.</returns>
    public bool TryAdd(string locator, byte[] bytes)
    {
        if (bytes.Length > ExtractLimits.MaxImageBytes
            || Images.Count >= ExtractLimits.MaxImages
            || _bytes + bytes.Length > ExtractLimits.MaxImagesBytes) return false;
        _bytes += bytes.Length;
        Images.Add(new EmbeddedImage(locator, bytes));
        return true;
    }
}
