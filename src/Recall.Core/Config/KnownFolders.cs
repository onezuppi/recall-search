using System.Runtime.InteropServices;
using System.Text.Json;

namespace Recall.Core.Config;

public static class KnownFolderIds
{
    public static readonly Guid Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    public static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    public static readonly Guid Documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    public static readonly Guid Pictures = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
    public static readonly Guid Screenshots = new("B7BEDE81-DF94-4682-A7D8-57A52620B86F");
    public static readonly Guid CameraRoll = new("AB5FB87B-7CE2-4F83-915D-550846C9537B");
    public static readonly Guid Videos = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");
}

public static class KnownFolders
{
    private const uint KF_FLAG_DONT_VERIFY = 0x00004000;

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    public static string? Get(Guid folderId)
    {
        if (SHGetKnownFolderPath(folderId, KF_FLAG_DONT_VERIFY, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public static IEnumerable<RootFolder> DetectAppFolders()
    {
        var docs = Get(KnownFolderIds.Documents);
        var downloads = Get(KnownFolderIds.Downloads);
        if (docs != null)
        {
            var shareX = Path.Combine(docs, "ShareX");
            string? shots = null;
            try
            {
                var cfg = Path.Combine(shareX, "ApplicationConfig.json");
                if (File.Exists(cfg))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
                    if (doc.RootElement.TryGetProperty("CustomScreenshotsPath", out var v) &&
                        v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                        shots = v.GetString();
                }
            }
            catch (Exception) { /* unreadable config: use the default folder */ }
            shots ??= Path.Combine(shareX, "Screenshots");
            if (Directory.Exists(shots)) yield return new RootFolder(shots, true, "ShareX");
        }
        if (downloads != null)
        {
            var tg = Path.Combine(downloads, "Telegram Desktop");
            if (Directory.Exists(tg)) yield return new RootFolder(tg, true, "Telegram");
        }
    }
}
