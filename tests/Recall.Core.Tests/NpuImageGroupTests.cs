using System.Globalization;
using Recall.Core.Embed;

namespace Recall.Core.Tests;

public class NpuImageGroupTests
{
    private static ModelSpec Spec => ModelCatalog.Find("embeddinggemma2-npu")!;

    /// <summary>Answers an "@images" line with one reply per path: an error for a path with "bad" in it, else a vector chosen by the path length.</summary>
    private sealed class GroupServer : ILineChannel
    {
        private readonly Queue<string> _replies = new();
        public readonly List<string> Requests = [];

        public void WriteLine(string line)
        {
            Requests.Add(line);
            foreach (var path in line.Split('\t').Skip(1))
            {
                if (path.Contains("bad")) { _replies.Enqueue("ERR\tcannot read the image"); continue; }
                var v = new float[Spec.Dimensions];
                v[path.Length % v.Length] = 1f;
                _replies.Enqueue("V " + string.Join(' ', v.Select(x => x.ToString(CultureInfo.InvariantCulture))));
            }
        }

        public string? ReadLine() => _replies.Count > 0 ? _replies.Dequeue() : null;
        public void Dispose() { }
    }

    [Fact]
    public void A_page_of_pictures_is_one_request_with_escaped_paths_separated_by_tabs()
    {
        var server = new GroupServer();
        using var embedder = new NpuEmbedder(Spec, server);
        embedder.EmbedImages([@"C:\a b\1.png", @"C:\a b\22.jpg"]);
        Assert.Equal("@images\tC:\\\\a b\\\\1.png\tC:\\\\a b\\\\22.jpg", Assert.Single(server.Requests));
    }

    [Fact]
    public void Vectors_come_back_in_order_and_a_bad_or_unsupported_picture_is_null_without_losing_sync()
    {
        var server = new GroupServer();
        using var embedder = new NpuEmbedder(Spec, server);
        var vectors = embedder.EmbedImages([@"C:\p\one.png", @"C:\p\bad.png", @"C:\p\old.webp", @"C:\p\three.jpg"]);

        Assert.NotNull(vectors[0]);
        Assert.Null(vectors[1]); // the server could not read it
        Assert.Null(vectors[2]); // never sent: the format is not supported
        Assert.NotNull(vectors[3]);
        Assert.NotEqual(Array.IndexOf(vectors[0]!, 1f), Array.IndexOf(vectors[3]!, 1f));
        Assert.DoesNotContain("old.webp", Assert.Single(server.Requests));
        // The next request still gets its own reply, so no line was left on the pipe.
        Assert.NotNull(embedder.EmbedImages([@"C:\p\next.png"])[0]);
    }

    [Fact]
    public void Only_unsupported_pictures_send_nothing()
    {
        var server = new GroupServer();
        using var embedder = new NpuEmbedder(Spec, server);
        Assert.All(embedder.EmbedImages([@"C:\p\a.webp", @"C:\p\b.heic"]), Assert.Null);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void A_closed_server_is_an_error_not_a_blank_picture()
    {
        var server = new GroupServer();
        using var embedder = new NpuEmbedder(Spec, new DeadServer());
        Assert.Throws<InvalidDataException>(() => embedder.EmbedImages([@"C:\p\a.png"]));
    }

    private sealed class DeadServer : ILineChannel
    {
        public void WriteLine(string line) { }
        public string? ReadLine() => null;
        public void Dispose() { }
    }
}
