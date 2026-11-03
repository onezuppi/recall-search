using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Recall.Core.Embed;

/// <summary>
/// Downloads the files of a pinned model revision from Hugging Face. A file is written to <c>name.part</c>, resumed with a
/// Range request, checked against its SHA-256 and then renamed. A hash mismatch deletes the partial file and throws.
/// </summary>
public sealed class ModelDownloader(HttpClient http)
{
    public static string DirectoryOf(string modelsRoot, ModelSpec spec) => Path.Combine(modelsRoot, spec.Id, spec.Revision);

    public static bool IsDownloaded(string modelsRoot, ModelSpec spec) =>
        spec.Files.All(f => File.Exists(Path.Combine(DirectoryOf(modelsRoot, spec), f.FileName)));

    public async Task<string> EnsureAsync(ModelSpec spec, string modelsRoot, IProgress<double>? progress, CancellationToken ct)
    {
        var dir = DirectoryOf(modelsRoot, spec);
        Directory.CreateDirectory(dir);
        var total = spec.Files.Sum(f => f.Size);
        long before = 0;
        foreach (var file in spec.Files)
        {
            var done = before;
            await EnsureFileAsync(spec, file, dir, n => progress?.Report((double)(done + n) / total), ct);
            before += file.Size;
        }
        return dir;
    }

    private async Task EnsureFileAsync(ModelSpec spec, ModelFile file, string dir, Action<long> progress, CancellationToken ct)
    {
        var target = Path.Combine(dir, file.FileName);
        if (File.Exists(target) && await HashOfAsync(target, ct) == file.Sha256) { progress(file.Size); return; }
        var part = target + ".part";
        await FetchAsync($"https://huggingface.co/{spec.Repo}/resolve/{spec.Revision}/{file.RemotePath}", part, progress, ct);
        if (await HashOfAsync(part, ct) != file.Sha256)
        {
            File.Delete(part);
            throw new InvalidDataException($"hash mismatch for {file.RemotePath}; the partial file was deleted");
        }
        File.Move(part, target, overwrite: true);
    }

    private async Task FetchAsync(string url, string part, Action<long> progress, CancellationToken ct)
    {
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var append = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append) have = 0;
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[256 * 1024];
        int n;
        while ((n = await body.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            have += n;
            progress(have);
        }
    }

    private static async Task<string> HashOfAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
    }
}
