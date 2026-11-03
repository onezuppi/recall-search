using System.Net;
using System.Security.Cryptography;
using Recall.Core.Embed;
using Recall.Core.Tests.Support;

namespace Recall.Core.Tests;

public class ModelDownloaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rs-dl-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _payload = Enumerable.Range(0, 10_000).Select(i => (byte)(i * 7)).ToArray();

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private ModelSpec Spec()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(_payload));
        return new("t", "o/t", "rev", 4, 16, [new("a/model.onnx", hash, _payload.Length), new("a/tok.model", hash, _payload.Length)]);
    }

    private sealed class Handler(byte[] body, bool honorRange = true) : HttpMessageHandler
    {
        public int Calls;
        public bool Answer416 { get; init; }
        public List<string?> Ranges { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var from = request.Headers.Range?.Ranges.First().From;
            Ranges.Add(from?.ToString());
            if (from != null && Answer416) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            if (from is { } f && honorRange)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body[(int)f..]) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private string DirOf(ModelSpec s) => ModelDownloader.DirectoryOf(_root, s);

    [Fact]
    public async Task Downloads_and_verifies()
    {
        var spec = Spec();
        var dir = await new ModelDownloader(new HttpClient(new Handler(_payload))).EnsureAsync(spec, _root, null, default);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(Path.Combine(dir, "model.onnx")));
        Assert.True(ModelDownloader.IsDownloaded(_root, spec));
        Assert.False(File.Exists(Path.Combine(dir, "model.onnx.part")));
    }

    [Fact]
    public async Task Hash_mismatch_deletes_part_and_throws()
    {
        var spec = Spec();
        var wrong = new byte[_payload.Length];
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ModelDownloader(new HttpClient(new Handler(wrong))).EnsureAsync(spec, _root, null, default));
        Assert.False(File.Exists(Path.Combine(DirOf(spec), "model.onnx.part")));
        Assert.False(ModelDownloader.IsDownloaded(_root, spec));
    }

    [Fact]
    public async Task Resumes_from_part_file()
    {
        var spec = Spec();
        Directory.CreateDirectory(DirOf(spec));
        await File.WriteAllBytesAsync(Path.Combine(DirOf(spec), "model.onnx.part"), _payload[..4096]);
        var handler = new Handler(_payload);
        await new ModelDownloader(new HttpClient(handler)).EnsureAsync(spec, _root, null, default);
        Assert.Equal("4096", handler.Ranges[0]);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(Path.Combine(DirOf(spec), "model.onnx")));
    }

    [Fact]
    public async Task Complete_part_file_is_refetched_after_416()
    {
        var spec = Spec();
        Directory.CreateDirectory(DirOf(spec));
        await File.WriteAllBytesAsync(Path.Combine(DirOf(spec), "model.onnx.part"), new byte[_payload.Length + 5]);
        await new ModelDownloader(new HttpClient(new Handler(_payload) { Answer416 = true })).EnsureAsync(spec, _root, null, default);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(Path.Combine(DirOf(spec), "model.onnx")));
    }

    [Fact]
    public async Task Existing_valid_file_is_not_requested_again()
    {
        var spec = Spec();
        await new ModelDownloader(new HttpClient(new Handler(_payload))).EnsureAsync(spec, _root, null, default);
        var second = new Handler(_payload);
        await new ModelDownloader(new HttpClient(second)).EnsureAsync(spec, _root, null, default);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task Server_ignoring_range_restarts()
    {
        var spec = Spec();
        Directory.CreateDirectory(DirOf(spec));
        await File.WriteAllBytesAsync(Path.Combine(DirOf(spec), "model.onnx.part"), _payload[..4096]);
        await new ModelDownloader(new HttpClient(new Handler(_payload, honorRange: false))).EnsureAsync(spec, _root, null, default);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(Path.Combine(DirOf(spec), "model.onnx")));
    }

    [Fact]
    public async Task Progress_reaches_one()
    {
        var last = 0.0;
        await new ModelDownloader(new HttpClient(new Handler(_payload))).EnsureAsync(Spec(), _root, new SyncProgress<double>(p => last = p), default);
        Assert.Equal(1.0, last, 3);
    }
}
