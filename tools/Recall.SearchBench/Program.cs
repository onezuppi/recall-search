using System.Diagnostics;
using Recall.Core.Index;
using Recall.Core.Search;

namespace Recall.SearchBench;

/// <summary>
/// Builds a synthetic index (one file and one text segment per file, optional random vectors) and times the search paths on it.
/// Usage: --db &lt;new file&gt; --files &lt;n&gt; [--dim 384]   or   --db &lt;existing file&gt; --run
/// </summary>
internal static class Program
{
    private const string Space = "bench@1";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault() ?? "";
        var db = Arg("--db");
        if (db.Length == 0) { Console.Error.WriteLine("usage: --db <file> [--files <n> [--dim 384]] [--run]"); return 1; }
        if (int.TryParse(Arg("--files"), out var files) && files > 0)
            Generator.Build(db, files, int.TryParse(Arg("--dim"), out var dim) ? dim : 0, Space);
        if (args.Contains("--run")) Bench.Run(db, int.TryParse(Arg("--dim"), out var d) ? d : 384, Space);
        return 0;
    }
}

internal static class Generator
{
    private const int VocabularySize = 20000, WordsPerFile = 180, FilesPerFolder = 200;

    public static void Build(string dbPath, int files, int dim, string space)
    {
        var rng = new Random(7);
        var vocabulary = Enumerable.Range(0, VocabularySize).Select(i => Word(rng, i)).ToArray();
        var sw = Stopwatch.StartNew();
        using (var store = SqliteIndexRepository.Open(dbPath))
        {
            for (var i = 0; i < files; i++)
            {
                var text = string.Join(' ', Enumerable.Range(0, WordsPerFile).Select(_ => vocabulary[(int)(VocabularySize * Math.Pow(rng.NextDouble(), 3))]));
                var hash = new UInt128((ulong)i + 1, 99);
                var content = store.AddContent(hash, "text", [new Segment("", text)]);
                var path = $@"C:\bench\d{i / FilesPerFolder}\file{i}.txt";
                store.UpsertFile(new Recall.Core.Scan.FileRecord(path, text.Length, DateTime.UtcNow.AddMinutes(-i), "text"), @"C:\bench", content, FileStatus.Ok, null);
            }
            Console.WriteLine($"{files} files in {sw.Elapsed.TotalSeconds:0} s");
            if (dim > 0) AddVectors(store, space, dim, rng);
        }
    }

    private static void AddVectors(SqliteIndexRepository store, string space, int dim, Random rng)
    {
        while (store.PendingSegments(space, 2000) is { Count: > 0 } page)
            store.AddEmbeddings(space, [.. page.Select(p => new EmbeddingRow(p.SegmentId, 0, Normalized(rng, dim)))]);
        Console.WriteLine("vectors added");
    }

    private static float[] Normalized(Random rng, int dim)
    {
        var v = Enumerable.Range(0, dim).Select(_ => (float)(rng.NextDouble() - 0.5)).ToArray();
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return [.. v.Select(x => x / norm)];
    }

    private static string Word(Random rng, int i)
    {
        const string letters = "abcdefghijklmnopqrstuvwxyzабвгдежзиклмнопрстуфх";
        return string.Concat(Enumerable.Range(0, 4 + i % 7).Select(_ => letters[rng.Next(letters.Length)])) + i;
    }
}

internal static class Bench
{
    /// <summary>The most frequent word of the generated texts (index 0 of the vocabulary, seed 7).</summary>
    private const string CommonWord = "sпеc0";

    public static void Run(string dbPath, int dim, string space)
    {
        var repo = new SqliteSearchRepository(dbPath);
        var engine = new SearchEngine(repo);
        Time("common word (many matches)", () => engine.Search("аб", 48)); // short term, scan path
        Time("very common word (FTS)", () => engine.Search(CommonWord, 48));
        Time("two rare words", () => engine.Search("word1234 word99", 48));
        Time("one word, 3+ letters", () => engine.Search("ааа", 48));
        Time("phrase of two words", () => engine.Search("\"абв где\"", 48));
        Time("with in: filter", () => engine.Search("ааа in:d5", 48));
        Time("with type: filter", () => engine.Search("ааа type:txt", 48));
        Time("file name only", () => engine.Search("file12345", 48));
        Time("  ScanSegments only", () =>
        {
            using var s = repo.Open()!;
            s.ScanSegments(QueryParser.Parse("аб"), ["аб"], 480);
        });
        Time("  MatchFileNames only", () =>
        {
            using var s = repo.Open()!;
            s.MatchFileNames(QueryParser.Parse("аб"), ["аб"], 480);
        });
        var query = new float[dim];
        query[0] = 1;
        Time("vector scan, all vectors", () =>
        {
            using var s = repo.Open()!;
            var n = 0;
            s.ScanVectors(space, dim, (_, v) => n += v.Length > 0 ? 1 : 0);
        });
    }

    private static void Time(string name, Action run)
    {
        run();
        var times = Enumerable.Range(0, 3).Select(_ => { var sw = Stopwatch.StartNew(); run(); return sw.Elapsed.TotalMilliseconds; }).Order().ToArray();
        Console.WriteLine($"| {name} | {times[1]:0} ms |");
    }
}
