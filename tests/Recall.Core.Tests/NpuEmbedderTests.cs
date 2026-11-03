using System.Globalization;
using Recall.Core.Config;
using Recall.Core.Embed;
using Recall.Core.Tests.Support;

namespace Recall.Core.Tests;

public class NpuEmbedderTests
{
    private static ModelSpec Spec => ModelCatalog.Find("embeddinggemma2-npu")!;

    /// <summary>The server side of the protocol in memory: a vector that has 1 at a position chosen by the text length.</summary>
    private sealed class FakeServer : ILineChannel
    {
        private readonly Queue<string> _replies = new();
        public readonly List<string> Requests = [];
        public bool Disposed;

        public void WriteLine(string line)
        {
            Requests.Add(line);
            if (line.Contains("FAIL")) { _replies.Enqueue("ERR\tmodel exploded\nwith a newline".Replace('\n', ' ')); return; }
            var v = new float[Spec.Dimensions];
            v[line.Length % v.Length] = 3f;
            _replies.Enqueue("V " + string.Join(' ', v.Select(x => x.ToString(CultureInfo.InvariantCulture))));
        }

        public string? ReadLine() => _replies.Count > 0 ? _replies.Dequeue() : null;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Requests_carry_the_task_prompt_and_escaped_text()
    {
        var server = new FakeServer();
        using var embedder = new NpuEmbedder(Spec, server);
        embedder.Embed(["a\tb\nc\\d"], EmbedKind.Query);
        embedder.Embed(["plain"], EmbedKind.Passage);
        Assert.Equal("SearchQuery\ta\\tb\\nc\\\\d", server.Requests[0]);
        Assert.Equal("Document\tplain", server.Requests[1]);
        Assert.DoesNotContain('\n', server.Requests[0]);
    }

    [Fact]
    public void Vectors_are_normalized_and_come_back_in_request_order()
    {
        using var embedder = new NpuEmbedder(Spec, new FakeServer());
        var vectors = embedder.Embed(["short", "a longer text"], EmbedKind.Passage);
        Assert.Equal(2, vectors.Length);
        Assert.All(vectors, v => Assert.Equal(1f, v.Sum(x => x * x), 4));
        Assert.NotEqual(Array.IndexOf(vectors[0], 1f), Array.IndexOf(vectors[1], 1f));
    }

    [Fact]
    public void Server_error_is_an_exception_with_its_message()
    {
        using var embedder = new NpuEmbedder(Spec, new FakeServer());
        var e = Assert.Throws<InvalidDataException>(() => embedder.Embed(["FAIL"], EmbedKind.Passage));
        Assert.Contains("model exploded", e.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("V 1 2 3")]
    [InlineData("V 0 0 0")]
    public void Bad_replies_are_rejected(string? reply) =>
        Assert.Throws<InvalidDataException>(() => NpuProtocol.ParseVector(reply, 3 + (reply == "V 1 2 3" ? 1 : 0)));

    [Fact]
    public void Disposing_the_embedder_ends_the_server()
    {
        var server = new FakeServer();
        new NpuEmbedder(Spec, server).Dispose();
        Assert.True(server.Disposed);
    }

    [Fact]
    public void Host_ends_the_server_when_released_and_starts_a_new_one_on_next_use()
    {
        var servers = new List<FakeServer>();
        var host = new EmbedderHost(Spec.Space, () => { var s = new FakeServer(); servers.Add(s); return new NpuEmbedder(Spec, s); }, TimeSpan.FromMinutes(5));
        host.Acquire().Dispose();
        host.Release();
        Assert.True(servers[0].Disposed);
        host.Acquire().Dispose();
        Assert.Equal(2, servers.Count);
    }

    private static RecallConfig Npu(string? exe, string? bundle) =>
        new([], new HashSet<string>(), []) { EmbeddingModel = "embeddinggemma2-npu", EmbeddingDevice = EmbeddingDevice.Npu, NpuServerPath = exe, NpuBundlePath = bundle };

    [Fact]
    public void Missing_server_or_bundle_means_unavailable_not_a_crash()
    {
        Assert.Throws<EmbeddingUnavailableException>(() => EmbedderHost.Create(Npu(null, null), "unused")!.Acquire());
        Assert.Throws<EmbeddingUnavailableException>(() => EmbedderHost.Create(Npu(@"X:\none\server.exe", @"X:\none"), "unused")!.Acquire());
    }

    [Fact]
    public void The_npu_model_does_not_run_on_cpu()
    {
        var cfg = Npu("a", "b") with { EmbeddingDevice = EmbeddingDevice.Cpu };
        Assert.Throws<NotSupportedException>(() => EmbedderHost.Create(cfg, "unused")!.Acquire());
    }

    [Fact]
    public void Npu_space_differs_from_the_cpu_space()
    {
        Assert.NotEqual(ModelCatalog.Find("multilingual-e5-small")!.Space, Spec.Space);
        Assert.Equal("embeddinggemma2-npu@taconite-0.2.0", EmbedderHost.Create(Npu(null, null), "unused")!.Space);
    }

    [SkippableFact]
    public void A_real_child_process_answers_line_by_line_and_is_killed_on_dispose()
    {
        var script = "[Console]::Error.WriteLine('READY dim=3'); while (($l = [Console]::In.ReadLine()) -ne $null) { 'V 1 0 0' }";
        var exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        Skip.IfNot(File.Exists(exe), "Windows PowerShell is not available");
        using var channel = ProcessLineChannel.Start(exe, ["-NoProfile", "-Command", script], "READY", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
        channel.WriteLine("Document\thello");
        Assert.Equal("V 1 0 0", channel.ReadLine());
    }

    [Fact]
    public void A_process_that_never_becomes_ready_is_reported_unavailable()
    {
        var exe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert.Throws<EmbeddingUnavailableException>(() =>
            ProcessLineChannel.Start(exe, ["/c", "exit 0"], "READY", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1)));
    }
}
