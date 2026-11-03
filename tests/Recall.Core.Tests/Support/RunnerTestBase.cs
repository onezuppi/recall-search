using Microsoft.Data.Sqlite;
using Recall.Core.Config;
using Recall.Core.Extract;
using Recall.Core.Ignore;
using Recall.Core.Index;
using Recall.Core.Ocr;

namespace Recall.Core.Tests.Support;

/// <summary>Temp folder, index database and runner wiring shared by the IndexRunner test classes.</summary>
public abstract class RunnerTestBase : IDisposable
{
    protected readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-run-" + Guid.NewGuid().ToString("N"));
    protected string Root => Path.Combine(_dir, "docs");
    protected string Db => Path.Combine(_dir, "index.db");

    protected RunnerTestBase() => Directory.CreateDirectory(Root);

    public virtual void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    protected sealed record Setup(IIndexRepository Store, IndexRunner Runner, CountingExtractor Text, CountingExtractor Office, RecallConfig Config) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }

    protected Setup Make(string ocrText = "ocr text")
    {
        var cfg = new RecallConfig([], new HashSet<string>(["images", "pdf", "office", "text"], StringComparer.OrdinalIgnoreCase), []);
        var store = SqliteIndexRepository.Open(Db);
        var text = new CountingExtractor(new TextExtractor());
        var office = new CountingExtractor(new OpenXmlExtractor());
        var runner = new IndexRunner(store, new IgnorePolicy(cfg, _ => false), [text, office], new FakeOcr(ocrText));
        return new Setup(store, runner, text, office, cfg);
    }

    protected RootFolder R(bool watched = false, string? path = null) => new(path ?? Root, watched);

    protected string Write(string name, string content, string? dir = null)
    {
        var p = Path.Combine(dir ?? Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    protected long Hits(string phrase)
    {
        using var c = new SqliteConnection($"Data Source={Db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM segments_fts WHERE segments_fts MATCH $q";
        cmd.Parameters.AddWithValue("$q", "\"" + phrase + "\"");
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}

public sealed class CountingExtractor(IExtractor inner) : IExtractor
{
    public int Calls;
    public bool CanHandle(string extension) => inner.CanHandle(extension);
    public Task<ExtractResult> ExtractAsync(string path, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        return inner.ExtractAsync(path, ct);
    }
}

public sealed class FakeOcr(string text) : IOcr
{
    public Task<string> RecognizeAsync(byte[] image, CancellationToken ct) => Task.FromResult(text);
}

public sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
{
    public void Report(T value) => onReport(value);
}
