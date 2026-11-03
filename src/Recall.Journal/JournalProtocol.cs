using System.Text.Json.Serialization;

namespace Recall.Journal;

/// <summary>Asks for the files changed on one volume since a cursor. A JournalId of 0 means "no cursor yet".</summary>
public sealed record JournalRequest(
    string Volume, ulong JournalId, long StartUsn, IReadOnlyList<string> Roots, IReadOnlyList<string> Extensions);

public static class JournalStatus
{
    /// <summary>Changes since the cursor are complete: <see cref="JournalResponse.Files"/> lists every content change.</summary>
    public const string Ok = "ok";
    /// <summary>The request had no cursor. Only the current end of the journal is returned.</summary>
    public const string NoCursor = "no-cursor";
    /// <summary>The journal was recreated, wrapped past the cursor, or too much changed. The caller must scan fully.</summary>
    public const string Gap = "gap";
    /// <summary>The volume has no readable change journal.</summary>
    public const string Unavailable = "unavailable";
}

/// <param name="Files">Full paths of files created, renamed in or written to under <paramref name="Roots"/>. Deleted files and folder changes are not listed.</param>
/// <param name="Roots">The requested roots the caller may list. Only these are covered by <paramref name="Files"/>.</param>
public sealed record JournalResponse(
    string Status, ulong JournalId = 0, long NextUsn = 0, IReadOnlyList<string>? Files = null,
    IReadOnlyList<string>? Roots = null, string? Error = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JournalRequest))]
[JsonSerializable(typeof(JournalResponse))]
public sealed partial class JournalJson : JsonSerializerContext;

public static class JournalPipe
{
    public const string Name = "RecallSearch.Journal";
    public const int MaxRequestBytes = 256 * 1024;
    public const int MaxResponseBytes = 64 * 1024 * 1024;

    /// <summary>Reads bytes up to the first newline. Null when the line is longer than <paramref name="max"/> or the stream ends first.</summary>
    public static async Task<byte[]?> ReadLineAsync(Stream s, int max, CancellationToken ct)
    {
        var ms = new MemoryStream();
        var buf = new byte[8192];
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
}
