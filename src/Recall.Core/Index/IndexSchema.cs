using Microsoft.Data.Sqlite;

namespace Recall.Core.Index;

/// <summary>DDL of the index database. Segments are mirrored into an external-content FTS5 table with the trigram tokenizer. Vectors of segments live in <c>embeddings</c>, one row per chunk and embedding space.</summary>
internal static class IndexSchema
{
    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS files(
          id INTEGER PRIMARY KEY,
          path TEXT NOT NULL UNIQUE COLLATE NOCASE,
          root TEXT NOT NULL COLLATE NOCASE,
          size INTEGER NOT NULL,
          mtime INTEGER NOT NULL,
          content_id INTEGER NULL,
          status TEXT NOT NULL,
          error TEXT NULL);
        CREATE INDEX IF NOT EXISTS files_root ON files(root);
        CREATE INDEX IF NOT EXISTS files_content ON files(content_id);
        CREATE TABLE IF NOT EXISTS contents(
          id INTEGER PRIMARY KEY,
          hash BLOB NOT NULL UNIQUE,
          grp TEXT NOT NULL,
          extracted_at INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS segments(
          id INTEGER PRIMARY KEY,
          content_id INTEGER NOT NULL,
          locator TEXT NOT NULL,
          text TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS segments_content ON segments(content_id);
        CREATE VIRTUAL TABLE IF NOT EXISTS segments_fts USING fts5(
          text, content='segments', content_rowid='id', tokenize='trigram');
        CREATE TRIGGER IF NOT EXISTS segments_ai AFTER INSERT ON segments BEGIN
          INSERT INTO segments_fts(rowid, text) VALUES (new.id, new.text);
        END;
        CREATE TRIGGER IF NOT EXISTS segments_ad AFTER DELETE ON segments BEGIN
          INSERT INTO segments_fts(segments_fts, rowid, text) VALUES ('delete', old.id, old.text);
        END;
        CREATE TABLE IF NOT EXISTS embeddings(
          segment_id INTEGER NOT NULL,
          space TEXT NOT NULL,
          chunk INTEGER NOT NULL,
          vec BLOB NOT NULL,
          PRIMARY KEY(segment_id, space, chunk)) WITHOUT ROWID;
        CREATE TRIGGER IF NOT EXISTS segments_ad_emb AFTER DELETE ON segments BEGIN
          DELETE FROM embeddings WHERE segment_id = old.id;
        END;
        """;

    public static void Create(SqliteConnection db)
    {
        Execute(db, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        try { Execute(db, Ddl); }
        catch (SqliteException e) when (e.Message.Contains("tokenizer", StringComparison.OrdinalIgnoreCase)
                                        || e.Message.Contains("fts5", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "This SQLite build has no FTS5 trigram tokenizer (SQLite 3.34 or newer with FTS5 is required).", e);
        }
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
