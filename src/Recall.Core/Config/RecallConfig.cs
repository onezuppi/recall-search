using System.Text.Json;
using System.Text.Json.Serialization;

namespace Recall.Core.Config;

public sealed record RootFolder(string Path, bool Watched, string? Name = null);

public static class RecallPaths
{
    public static string DataDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecallSearch");
}

public sealed record RecallConfig(
    IReadOnlyList<RootFolder> Roots,
    IReadOnlySet<string> EnabledGroups,
    IReadOnlyList<string> GlobalIgnorePatterns)
{
    public long MaxTextBytes { get; init; } = 2 * 1024 * 1024;
    public long MaxImageBytes { get; init; } = 50 * 1024 * 1024;
    public IReadOnlyList<string> OcrLanguages { get; init; } = ["ru", "en-US"];

    /// <summary>Pause indexing while a full screen game or a presentation is running.</summary>
    public bool PauseWhenBusy { get; init; } = true;

    /// <summary>Extra process names (with or without ".exe") that also pause indexing, for games that run windowed.</summary>
    public IReadOnlyList<string> PauseProcesses { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new GroupSetConverter() },
    };

    public static RecallConfig CreateDefault()
    {
        var roots = new List<RootFolder>();
        void Add(string? path, bool watched, string? name = null)
        {
            if (path == null || !Directory.Exists(path)) return;
            if (roots.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            roots.Add(new RootFolder(path, watched, name));
        }
        Add(KnownFolders.Get(KnownFolderIds.Desktop), true);
        Add(KnownFolders.Get(KnownFolderIds.Downloads), true);
        Add(KnownFolders.Get(KnownFolderIds.Screenshots), true);
        Add(KnownFolders.Get(KnownFolderIds.Documents), false);
        Add(KnownFolders.Get(KnownFolderIds.Pictures), false);
        Add(KnownFolders.Get(KnownFolderIds.CameraRoll), false);
        var videos = KnownFolders.Get(KnownFolderIds.Videos);
        Add(videos == null ? null : System.IO.Path.Combine(videos, "Captures"), false);
        foreach (var r in KnownFolders.DetectAppFolders()) Add(r.Path, r.Watched, r.Name);
        return new RecallConfig(
            roots,
            new HashSet<string>(["images", "pdf", "office", "text"], StringComparer.OrdinalIgnoreCase),
            []);
    }

    public static RecallConfig Load(string path)
    {
        if (!File.Exists(path)) return CreateDefault();
        var cfg = JsonSerializer.Deserialize<RecallConfig>(File.ReadAllText(path), Json);
        return cfg ?? CreateDefault();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}

internal sealed class GroupSetConverter : JsonConverter<IReadOnlySet<string>>
{
    public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
        new HashSet<string>(JsonSerializer.Deserialize<string[]>(ref reader, o) ?? [], StringComparer.OrdinalIgnoreCase);

    public override void Write(Utf8JsonWriter w, IReadOnlySet<string> v, JsonSerializerOptions o) =>
        JsonSerializer.Serialize(w, v.Order().ToArray(), o);
}
