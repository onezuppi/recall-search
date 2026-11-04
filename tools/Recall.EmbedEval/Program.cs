using System.Diagnostics;
using System.Globalization;
using System.Numerics.Tensors;
using System.Text.Json;
using Recall.Core.Config;
using Recall.Core.Embed;

namespace Recall.EmbedEval;

/// <summary>
/// Ranks the files of a folder of Markdown documents for a list of queries with one embedding model and prints a report.
/// Run it once per model and compare. Usage: --model &lt;id from ModelCatalog&gt; --corpus &lt;dir&gt;... [--models-root dir] [--server exe --bundle dir]
/// </summary>
internal static class Program
{
    private const int Batch = 16;

    private sealed record Query(string Q, string? File);

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var opt = Options.Parse(args);
        if (opt == null) { Console.Error.WriteLine("usage: --model <id> --corpus <dir> [--corpus <dir>] [--models-root <dir>] [--server <exe> --bundle <dir>]"); return 1; }
        var spec = ModelCatalog.Find(opt.Model) ?? throw new InvalidOperationException($"unknown model {opt.Model}");
        var queries = JsonSerializer.Deserialize<Query[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "queries.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var chunks = Corpus.Load(opt.Corpus);
        Console.WriteLine($"model {spec.Id}, {chunks.Select(c => c.File).Distinct().Count()} files, {chunks.Count} chunks");

        using var embedder = Create(spec, opt);
        var sw = Stopwatch.StartNew();
        var vectors = new List<float[]>();
        for (var i = 0; i < chunks.Count; i += Batch)
            vectors.AddRange(embedder.Embed([.. chunks.Skip(i).Take(Batch).Select(c => c.Text)], EmbedKind.Passage));
        var perChunk = sw.Elapsed.TotalMilliseconds / chunks.Count;
        Console.WriteLine($"indexing: {sw.Elapsed.TotalSeconds:0.0} s, {perChunk:0} ms per chunk, {1000 / perChunk:0.0} chunks per second");

        Report(queries, embedder, chunks, vectors);
        return 0;
    }

    private static IEmbedder Create(ModelSpec spec, Options opt)
    {
        if (spec.Kind == ModelSpec.KindTaconiteNpu)
            return new TaconiteNpuProvider(opt.Server, opt.Bundle).Create(spec, "", EmbeddingDevice.Npu);
        if (!ModelDownloader.IsDownloaded(opt.ModelsRoot, spec))
        {
            Console.WriteLine($"downloading {spec.Id} ({spec.Files.Sum(f => f.Size) / (1024 * 1024)} MB, hashes are checked)");
            using var http = new HttpClient();
            new ModelDownloader(http).EnsureAsync(spec, opt.ModelsRoot, new Progress<double>(), default).GetAwaiter().GetResult();
        }
        return new OnnxE5Provider().Create(spec, ModelDownloader.DirectoryOf(opt.ModelsRoot, spec), EmbeddingDevice.Cpu);
    }

    private static void Report(Query[] queries, IEmbedder embedder, IReadOnlyList<Chunk> chunks, List<float[]> vectors)
    {
        double mrr = 0;
        int top1 = 0, top3 = 0, known = 0;
        var wrongTops = new List<float>();
        var rightBest = new List<float>();
        var qWatch = Stopwatch.StartNew();
        Console.WriteLine("\n| Query | Rank | Best score of the right file | Best score of any other file |\n|---|---|---|---|");
        foreach (var q in queries)
        {
            var qv = embedder.Embed([q.Q], EmbedKind.Query)[0];
            var best = new Dictionary<string, float>();
            for (var i = 0; i < chunks.Count; i++)
            {
                var s = TensorPrimitives.Dot<float>(qv, vectors[i]);
                if (!best.TryGetValue(chunks[i].File, out var old) || s > old) best[chunks[i].File] = s;
            }
            var ranked = best.OrderByDescending(kv => kv.Value).ToList();
            if (q.File == null)
            {
                wrongTops.Add(ranked[0].Value);
                Console.WriteLine($"| {q.Q} (nonsense) | - | - | {F(ranked[0].Value)} |");
                continue;
            }
            known++;
            var rank = ranked.FindIndex(kv => kv.Key.EndsWith(q.File.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)
                                              || kv.Key.EndsWith(q.File, StringComparison.OrdinalIgnoreCase)) + 1;
            var right = rank > 0 ? ranked[rank - 1].Value : float.NaN;
            var other = ranked.First(kv => ranked.IndexOf(kv) != rank - 1).Value;
            if (rank == 1) top1++;
            if (rank is >= 1 and <= 3) top3++;
            if (rank > 0) mrr += 1.0 / rank;
            rightBest.Add(right);
            Console.WriteLine($"| {q.Q} | {rank} | {F(right)} | {F(other)} |");
        }
        Console.WriteLine($"\nqueries with a known file: {known}; rank 1: {top1}; top 3: {top3}; MRR {mrr / known:0.000}");
        Console.WriteLine($"right file score: min {F(rightBest.Min())}, median {F(rightBest.Order().ElementAt(rightBest.Count / 2))}");
        Console.WriteLine($"nonsense queries, top score: {string.Join(", ", wrongTops.Select(F))}");
        Console.WriteLine($"query embedding + ranking: {qWatch.Elapsed.TotalMilliseconds / queries.Length:0} ms per query");
    }

    private static string F(float v) => v.ToString("0.000", CultureInfo.InvariantCulture);
}
