using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Recall.Core.Ipc;

// This file and PipeClient.cs are linked into the Command Palette extension: keep them free of other Recall.Core types.

public sealed record IpcRequest(string Op, string? Query = null, int Limit = 48);

public sealed record IpcHit(string Path, string Group, string Locator, string Snippet, DateTime Modified, long Size);

public sealed record IpcStatus(long FilesIndexed, long FilesPending, long FilesFailed, DateTime? LastRunUtc);

public sealed record IpcResponse(bool Ok, string? Error = null, IpcHit[]? Hits = null, IpcStatus? Status = null);

public static class IpcOps
{
    public const string Search = "search", Status = "status", Ping = "ping";
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
