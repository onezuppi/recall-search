using Microsoft.Data.Sqlite;
using Recall.Core.Index;
using Recall.Core.Scan;

namespace Recall.Core.Tests;

public class IndexStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-idx-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "index.db");

    public IndexStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static FileRecord Rec(string path, long size = 10, long ticks = 100) =>
        new(path, size, new DateTime(ticks, DateTimeKind.Utc), "text");

    private long Scalar(string sql)
    {
        using var c = new SqliteConnection($"Data Source={Db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void Same_content_two_paths_stored_once()
    {
        using var s = IndexStore.Open(Db);
        var id = s.AddContent(1, "text", [new Segment("", "hello")]);
        s.UpsertFile(Rec(@"C:\a\one.txt"), @"C:\a", id, "ok", null);
        s.UpsertFile(Rec(@"C:\a\two.txt"), @"C:\a", id, "ok", null);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM contents"));
        Assert.Equal(2, Scalar("SELECT COUNT(*) FROM files"));
        Assert.Equal(id, s.FindContent(1));
        Assert.Null(s.FindContent(2));
    }

    [Fact]
    public void Trigram_finds_fragment_inside_word()
    {
        using var s = IndexStore.Open(Db);
        s.AddContent(7, "text", [new Segment("", "Пароль от wifi: молоко123")]);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM segments_fts WHERE segments_fts MATCH '\"оло\"'"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM segments_fts WHERE segments_fts MATCH '\"WIFI\"'"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM segments_fts WHERE segments_fts MATCH '\"xyz\"'"));
    }

    [Fact]
    public void Removing_last_path_deletes_orphan_content()
    {
        using var s = IndexStore.Open(Db);
        var id = s.AddContent(1, "text", [new Segment("", "some text here")]);
        s.UpsertFile(Rec(@"C:\a\one.txt"), @"C:\a", id, "ok", null);
        s.UpsertFile(Rec(@"C:\a\two.txt"), @"C:\a", id, "ok", null);
        s.RemoveFiles([@"C:\a\one.txt"]);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM contents"));
        s.RemoveFiles([@"C:\a\two.txt"]);
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM contents"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM segments"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM segments_fts WHERE segments_fts MATCH '\"some\"'"));
    }

    [Fact]
    public void Replacing_content_of_a_path_drops_the_old_orphan()
    {
        using var s = IndexStore.Open(Db);
        var a = s.AddContent(1, "text", [new Segment("", "old words")]);
        s.UpsertFile(Rec(@"C:\a\one.txt"), @"C:\a", a, "ok", null);
        var b = s.AddContent(2, "text", [new Segment("", "new words")]);
        s.UpsertFile(Rec(@"C:\a\one.txt", ticks: 200), @"C:\a", b, "ok", null);
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM contents"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM segments_fts WHERE segments_fts MATCH '\"old\"'"));
    }

    [Fact]
    public void Snapshot_returns_size_and_mtime_per_path_for_root()
    {
        using var s = IndexStore.Open(Db);
        s.UpsertFile(Rec(@"C:\a\one.txt", 5, 111), @"C:\a", null, "failed", "boom");
        s.UpsertFile(Rec(@"C:\b\two.txt", 6, 222), @"C:\b", null, "ok", null);
        var snap = s.Snapshot(@"C:\a");
        var e = Assert.Single(snap);
        Assert.Equal(@"C:\a\one.txt", e.Key, ignoreCase: true);
        Assert.Equal((5L, 111L), e.Value);
        // lookups are case-insensitive like NTFS
        Assert.True(snap.ContainsKey(@"c:\A\ONE.txt"));
    }
}
