using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Recall.Core.Ipc;

// This file and PipeClient.cs are linked into the Command Palette extension: keep them free of other Recall.Core types.

/// <param name="Value">Folder path, exclude pattern or file extension for the settings operations.</param>
/// <param name="Target">Folder list ("fast" or "slow") for add-root and set-watched, file group for add-type.</param>
public sealed record IpcRequest(string Op, string? Query = null, int Limit = 48, string? Value = null, string? Target = null);

public sealed record IpcHit(string Path, string Group, string Locator, string Snippet, DateTime Modified, long Size, string Context = "");

public sealed record IpcStatus(long FilesIndexed, long FilesPending, long FilesFailed, DateTime? LastRunUtc);

public sealed record IpcRoot(string Path, bool Watched);

public sealed record IpcFileType(string Extension, string Group);

/// <param name="MinSimilarity">Cosine similarity under which a semantic hit is hidden. Null when semantic search is off.</param>
/// <param name="SlowScanTime">Local "HH:mm" of the daily slow-folder scan; empty when the scheduled scan is off.</param>
/// <param name="Device">Where semantic search runs: off, cpu, gpu or npu.</param>
/// <param name="RestartPending">The device was changed after the agent started; the agent uses the new one after a restart.</param>
/// <param name="Progress">Indexing progress; null when the agent could not count it.</param>
public sealed record IpcSettings(
    IpcRoot[] Roots, string[] Excludes, IpcFileType[]? FileTypes = null, double? MinSimilarity = null, string SlowScanTime = "",
    string Device = "off", bool RestartPending = false, IpcProgress? Progress = null, int EmbedIntervalMinutes = 20);

/// <param name="TextPending">Stored text segments with no vector yet in the active model's space.</param>
/// <param name="ImagesPending">Pictures with no picture vector yet.</param>
/// <param name="TextPerMinute">Measured speed of the text vectors since the agent started; 0 when unknown.</param>
public sealed record IpcProgress(
    long FilesIndexed, long FilesPending, long TextTotal, long TextPending, long ImagesTotal, long ImagesPending,
    double TextPerMinute, string Model);

public sealed record IpcResponse(bool Ok, string? Error = null, IpcHit[]? Hits = null, IpcStatus? Status = null, IpcSettings? Settings = null);

public static class IpcOps
{
    public const string Search = "search", Status = "status", Ping = "ping";

    /// <summary>The extension was opened: load the embedding model now, so the first query does not wait for it. Answers at once.</summary>
    public const string Warm = "warm";
    public const string Settings = "settings", AddRoot = "add-root", RemoveRoot = "remove-root";
    public const string AddExclude = "add-exclude", RemoveExclude = "remove-exclude";
    public const string SetWatched = "set-watched", AddType = "add-type", RemoveType = "remove-type", SetSimilarity = "set-similarity", SetSlowScan = "set-slow-scan", SetDevice = "set-device", SetEmbedInterval = "set-embed-interval";
    public const string FastList = "fast", SlowList = "slow";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
public sealed partial class IpcJson : JsonSerializerContext;

public static class PipeNames
{
    public static string ForCurrentUser() => "RecallSearch." + WindowsIdentity.GetCurrent().User!.Value;
}

public static class AgentPaths
{
    /// <summary>Image path the client requires from the pipe server. Settable for tests.</summary>
    public static string ExpectedExe { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RecallSearch", "Recall.Agent.exe");
}

/// <summary>The process on the other end of the pipe is not the user's own Recall agent.</summary>
public sealed class UntrustedServerException(string reason) : Exception(reason);

internal static class Wire
{
    public const int MaxRequestBytes = 64 * 1024;
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>Reads bytes up to the first newline. Returns null when the line is longer than <paramref name="max"/> or the stream ends first.</summary>
    public static async Task<byte[]?> ReadLineAsync(Stream s, int max, CancellationToken ct)
    {
        var ms = new MemoryStream();
        var buf = new byte[4096];
        while (true)
        {
            var n = await s.ReadAsync(buf, ct);
            if (n == 0) return null;
            var nl = Array.IndexOf(buf, (byte)'\n', 0, n);
            var take = nl >= 0 ? nl : n;
            if (ms.Length + take > max) return null;
            ms.Write(buf, 0, take);
            if (nl >= 0) return ms.ToArray();
        }
    }

    public static async Task WriteLineAsync(Stream s, byte[] json, CancellationToken ct)
    {
        await s.WriteAsync(json, ct);
        await s.WriteAsync("\n"u8.ToArray(), ct);
        await s.FlushAsync(ct);
    }

    public static byte[] Serialize(IpcResponse r) => JsonSerializer.SerializeToUtf8Bytes(r, IpcJson.Default.IpcResponse);
}
