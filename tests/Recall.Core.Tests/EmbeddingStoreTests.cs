using Microsoft.Data.Sqlite;
using Recall.Core.Index;
using Recall.Core.Scan;

namespace Recall.Core.Tests;

public class EmbeddingStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-emb-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "index.db");
    private readonly SqliteIndexRepository _store;
    private long _hash;

    public EmbeddingStoreTests()
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

    private void AddFile(string path, string text, long? sameHash = null)
    {
        var hash = (UInt128)(sameHash ?? ++_hash);
        var id = _store.FindContent(hash) ?? _store.AddContent(hash, "text", [new Segment("", text)]);
        _store.UpsertFile(new FileRecord(path, 1, DateTime.UtcNow, "text"), _dir, id, "ok", null);
    }

    private long Count(string sql)
    {
        using var c = new SqliteConnection($"Data Source={Db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void Pending_lists_segments_without_vectors()
    {
        AddFile(@"C:\a.txt", "alpha");
        AddFile(@"C:\b.txt", "beta");
        Assert.Equal(2, _store.PendingSegments("m@1", 10).Count);
        Assert.Single(_store.PendingSegments("m@1", 1));
    }

    [Fact]
    public void Added_vectors_are_not_pending_again()
    {
        AddFile(@"C:\a.txt", "alpha");
        AddFile(@"C:\b.txt", "beta");
        var p = _store.PendingSegments("m@1", 10);
        _store.AddEmbeddings("m@1", [new EmbeddingRow(p[0].SegmentId, 0, [1, 0]), EmbeddingRow.Blank(p[1].SegmentId)]);
        Assert.Empty(_store.PendingSegments("m@1", 10));
    }

    [Fact]
    public void Other_space_stays_pending()
    {
        AddFile(@"C:\a.txt", "alpha");
        var p = _store.PendingSegments("m@1", 10);
        _store.AddEmbeddings("m@1", [new EmbeddingRow(p[0].SegmentId, 0, [1, 0])]);
        Assert.Single(_store.PendingSegments("m@2", 10));
    }

    [Fact]
    public void Removing_the_file_deletes_vectors()
    {
        AddFile(@"C:\a.txt", "alpha");
        var p = _store.PendingSegments("m@1", 10);
        _store.AddEmbeddings("m@1", [new EmbeddingRow(p[0].SegmentId, 0, [1, 0])]);
        Assert.Equal(1, Count("SELECT COUNT(*) FROM embeddings"));
        _store.RemoveFiles([@"C:\a.txt"]);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM embeddings"));
    }

    [Fact]
    public void Shared_content_has_one_set_of_vectors()
    {
        AddFile(@"C:\a.txt", "alpha", sameHash: 7);
        AddFile(@"C:\copy\a.txt", "alpha", sameHash: 7);
        Assert.Single(_store.PendingSegments("m@1", 10));
    }

    [Fact]
    public void Vectors_of_a_deleted_segment_are_dropped()
    {
        AddFile(@"C:.txt", "alpha");
        _store.AddEmbeddings("m@1", [new EmbeddingRow(9999, 0, [1, 0])]);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM embeddings"));
    }

    [Fact]
    public void DeleteSpace_removes_only_that_space()
    {
        AddFile(@"C:.txt", "alpha");
        var id = _store.PendingSegments("x", 1)[0].SegmentId;
        _store.AddEmbeddings("m@1", [new EmbeddingRow(id, 0, [1, 0])]);
        _store.AddEmbeddings("m@2", [new EmbeddingRow(id, 0, [0, 1])]);
        _store.DeleteSpace("m@1");
        Assert.Equal(1, Count("SELECT COUNT(*) FROM embeddings WHERE space = 'm@2'"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM embeddings WHERE space = 'm@1'"));
    }
}
