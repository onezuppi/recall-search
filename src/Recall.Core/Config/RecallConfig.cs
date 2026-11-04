using System.Text.Json;
using System.Text.Json.Serialization;

namespace Recall.Core.Config;

public enum EmbeddingDevice { Cpu, Gpu, Npu }

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
    /// <summary>Id from <c>ModelCatalog</c>; null turns embeddings off.</summary>
    public string? EmbeddingModel { get; init; }
    public EmbeddingDevice EmbeddingDevice { get; init; } = EmbeddingDevice.Cpu;

    /// <summary>Full path of <c>windows_embed_server.exe</c> (tools/npu-embed-server). Used when the model runs on the NPU.</summary>
    public string? NpuServerPath { get; init; }

    /// <summary>Folder of the EmbeddingGemma 2 bundle that the NPU server loads.</summary>
    public string? NpuBundlePath { get; init; }

    /// <summary>
    /// Cosine similarity under which a semantic hit is not shown. Null keeps the default of the chosen model.
    /// Raise it to see fewer, surer semantic hits; lower it to see more.
    /// </summary>
    public float? EmbeddingMinSimilarity { get; init; }

    /// <summary>Also embed picture files when the model can (EmbeddingGemma 2 on the NPU). About a second of NPU time per picture.</summary>
    public bool EmbedImages { get; init; } = true;

    /// <summary>The floor for picture hits. Null keeps the default of the chosen model.</summary>
    public float? EmbeddingImageMinSimilarity { get; init; }
    public IReadOnlyList<string> OcrLanguages { get; init; } = ["ru", "en-US"];

    /// <summary>Extra file types: extension with the leading dot (lower case) to group name. Built-in types cannot be overridden.</summary>
    public IReadOnlyDictionary<string, string> CustomFileTypes { get; init; } = new Dictionary<string, string>();

    /// <summary>Local time ("HH:mm") of the daily scan of the slow folders. Empty turns the scheduled scan off; they are then scanned at start only.</summary>
    public string SlowScanTime { get; init; } = Scan.SlowScanSchedule.Default;

    /// <summary>
    /// Minutes between two runs of the vector stage once the backlog is done. New files wait for the next run, so the model is loaded
    /// (about 2 GB for the NPU one) at most this often. 0 makes vectors right after each change. The first backlog is never delayed.
    /// </summary>
    public int EmbedIntervalMinutes { get; init; } = 20;

    /// <summary>Pause indexing while a full screen game or a presentation is running.</summary>
    public bool PauseWhenBusy { get; init; } = true;

    /// <summary>Extra process names (with or without ".exe") that also pause indexing, for games that run windowed.</summary>
    public IReadOnlyList<string> PauseProcesses { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new GroupSetConverter(), new JsonStringEnumConverter() },
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

    /// <summary>
    /// Reads the file, or returns the defaults when it is missing. A file that cannot be read (a save cut off by a crash, a typo in a hand edit)
    /// is copied to <c>config.json.bad</c> and the defaults are returned, so the agent still starts and the user can repair the copy.
    /// </summary>
    /// <param name="onProblem">Told what went wrong; the agent writes it to its log.</param>
    public static RecallConfig Load(string path, Action<string>? onProblem = null)
    {
        if (!File.Exists(path)) return CreateDefault();
        try
        {
            var cfg = JsonSerializer.Deserialize<RecallConfig>(File.ReadAllText(path), Json);
            return cfg == null ? CreateDefault() : cfg.FillMissing();
        }
        catch (JsonException e)
        {
            var bad = path + ".bad";
            try { File.Copy(path, bad, overwrite: true); } catch (IOException) { bad = "(the copy failed)"; }
            onProblem?.Invoke($"{path} cannot be read ({e.Message}); using the defaults. The file was kept as {bad}.");
            return CreateDefault();
        }
    }

    /// <summary>A hand-edited file may leave out whole lists; the code downstream expects them to exist.</summary>
    private RecallConfig FillMissing() => this with
    {
        Roots = (IReadOnlyList<RootFolder>?)Roots ?? [],
        EnabledGroups = (IReadOnlySet<string>?)EnabledGroups is { Count: > 0 } groups ? groups : CreateDefault().EnabledGroups,
        GlobalIgnorePatterns = (IReadOnlyList<string>?)GlobalIgnorePatterns ?? [],
    };

    /// <summary>Writes a temporary file and swaps it in, so a crash or power loss mid-write cannot leave half a file behind.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        if (File.Exists(path)) File.Replace(temp, path, destinationBackupFileName: null);
        else File.Move(temp, path);
    }
}

internal sealed class GroupSetConverter : JsonConverter<IReadOnlySet<string>>
{
    public override IReadOnlySet<string> Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o) =>
        new HashSet<string>(JsonSerializer.Deserialize<string[]>(ref reader, o) ?? [], StringComparer.OrdinalIgnoreCase);

    public override void Write(Utf8JsonWriter w, IReadOnlySet<string> v, JsonSerializerOptions o) =>
        JsonSerializer.Serialize(w, v.Order().ToArray(), o);
}
