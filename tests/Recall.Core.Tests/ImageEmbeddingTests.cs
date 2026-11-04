using Microsoft.Data.Sqlite;
using Recall.Core.Embed;
using Recall.Core.Index;
using Recall.Core.Scan;
using Recall.Core.Search;

namespace Recall.Core.Tests;

public class ImageEmbeddingTests : IDisposable
{
    private const string Space = "fake@1", ImageSpace = "fake@1+image";
    private const int Dim = 4;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-img-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "index.db");
    private readonly SqliteIndexRepository _store;

    public ImageEmbeddingTests()
    {
        Directory.CreateDirectory(_dir);
        _store = SqliteIndexRepository.Open(Db);
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>Text vectors are e0. A picture vector is e1 for "cat" in its name and e2 otherwise; a webp is not supported.</summary>
    private sealed class FakeImageEmbedder : IEmbedder, IImageEmbedder
    {
        public readonly List<string> Pictures = [];
        public string Space => ImageEmbeddingTests.Space;
        public int Dimensions => Dim;

        public float[][] Embed(IReadOnlyList<string> texts, EmbedKind kind) => [.. texts.Select(_ => Unit(0))];

        public float[]? EmbedImage(string path)
        {
            Pictures.Add(path);
            if (path.EndsWith(".webp")) return null;
            return Unit(path.Contains("cat") ? 1 : 2);
        }

        public void Dispose() { }
    }

    private static float[] Unit(int i)
    {
        var v = new float[Dim];
        v[i] = 1;
        return v;
    }

    private static EmbedderHost Host(IEmbedder e, float minImage = 0f) =>
        new(Space, () => e, TimeSpan.Zero, 0f, ImageSpace, minImage);

    private void AddPicture(string path, string ocrText = "")
    {
        var id = _store.AddContent((UInt128)Math.Abs(path.GetHashCode()) + 1, "images", [new Segment("", ocrText)]);
        _store.UpsertFile(new FileRecord(path, 10, DateTime.UtcNow, "images"), @"C:\pics", id, FileStatus.Ok, null);
    }

    private long Count(string space, string extra = "")
    {
        using var c = new SqliteConnection($"Data Source={Db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM embeddings WHERE space = $s {extra}";
        cmd.Parameters.AddWithValue("$s", space);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void Picture_request_carries_the_image_prompt_and_an_escaped_path()
    {
        Assert.Equal("@image\tC:\\\\a b\\\\x.png", NpuProtocol.ImageRequest(@"C:\a b\x.png"));
    }

    [Theory]
    [InlineData(@"C:\p\a.png", true)]
    [InlineData(@"C:\p\a.JPG", true)]
    [InlineData(@"C:\p\a.jpeg", true)]
    [InlineData(@"C:\p\a.webp", false)]
    [InlineData(@"C:\p\a.heic", false)]
    public void Only_formats_the_server_can_decode_are_sent(string path, bool supported) =>
        Assert.Equal(supported, NpuProtocol.IsImageFormat(path));

    [Fact]
    public async Task Stage_stores_one_picture_vector_per_picture_in_the_image_space()
    {
        AddPicture(@"C:\pics\cat1.png", "cat text");
        AddPicture(@"C:\pics\dog1.png");
        var fake = new FakeImageEmbedder();
        var stats = await new EmbeddingStage(_store, Host(fake)).RunAsync(default);

        Assert.Equal(2, stats.Images);
        Assert.Equal(2, Count(ImageSpace, "AND chunk = 0"));
        Assert.Equal(1, Count(Space, "AND chunk >= 0")); // the OCR text of the first picture; the second has none
        Assert.Equal(2, fake.Pictures.Count);
    }

    [Fact]
    public async Task Unsupported_picture_gets_a_blank_row_and_is_not_tried_again()
    {
        AddPicture(@"C:\pics\old.webp");
        var fake = new FakeImageEmbedder();
        var host = Host(fake);
        await new EmbeddingStage(_store, host).RunAsync(default);
        await new EmbeddingStage(_store, host).RunAsync(default);

        Assert.Equal(1, Count(ImageSpace, "AND chunk = -1"));
        Assert.Single(fake.Pictures);
    }

    [Fact]
    public async Task Without_an_image_space_pictures_are_left_alone()
    {
        AddPicture(@"C:\pics\cat1.png");
        var fake = new FakeImageEmbedder();
        await new EmbeddingStage(_store, new EmbedderHost(Space, () => fake, TimeSpan.Zero)).RunAsync(default);
        Assert.Empty(fake.Pictures);
    }

    [Fact]
    public async Task Search_finds_a_picture_through_its_picture_vector()
    {
        AddPicture(@"C:\pics\cat1.png");
        AddPicture(@"C:\pics\dog1.png");
        var fake = new FakeImageEmbedder();
        var host = Host(fake);
        await new EmbeddingStage(_store, host).RunAsync(default);

        // The query vector is e0 for every text, so only a floor of 0 lets the pictures (e1, e2) through.
        var hits = SearchEngine.ForDatabase(Db, host).Search("qqq", 5).Select(h => Path.GetFileName(h.Path));
        Assert.Contains("cat1.png", hits);
        Assert.Contains("dog1.png", hits);
    }

    [Fact]
    public async Task Picture_floor_hides_weak_picture_hits_and_leaves_text_hits_alone()
    {
        AddPicture(@"C:\pics\cat1.png");
        var fake = new FakeImageEmbedder();
        var host = Host(fake, minImage: 0.5f);
        await new EmbeddingStage(_store, host).RunAsync(default);
        Assert.Empty(SearchEngine.ForDatabase(Db, host).Search("qqq", 5));
    }
}
