using System.Diagnostics;
using System.Globalization;
using System.Numerics.Tensors;
using System.Text.Json;
using Recall.Core.Embed;

namespace Recall.EmbedEval;

/// <summary>
/// Text queries against picture vectors. Screenshots are named "app_random.jpg" (ShareX), so the app is the ground truth:
/// a query about "a terminal window" is right for a picture of an app that is a terminal. No picture is opened by the tool or shown.
/// </summary>
internal static class ImageEval
{
    private sealed record Query(string Q, string[]? Apps);

    public static void Run(IEmbedder embedder, string folder, int perApp)
    {
        if (embedder is not IImageEmbedder images) throw new InvalidOperationException("this model does not embed pictures");
        var queries = JsonSerializer.Deserialize<Query[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "image-queries.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var apps = queries.SelectMany(q => q.Apps ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = Sample(folder, perApp, apps);
        Console.WriteLine($"{files.Count} pictures from {files.Select(f => f.App).Distinct().Count()} apps");

        var sw = Stopwatch.StartNew();
        var vectors = files.Select(f => images.EmbedImage(f.Path)).ToList();
        Console.WriteLine($"picture embedding: {sw.Elapsed.TotalSeconds:0} s, {sw.Elapsed.TotalMilliseconds / files.Count:0} ms per picture");

        double mrr = 0;
        int top1 = 0, top3 = 0, known = 0;
        var right = new List<float>();
        var wrong = new List<float>();
        var nonsense = new List<float>();
        Console.WriteLine("\n| Query | Rank of the first right picture | Best right score | Best wrong score |\n|---|---|---|---|");
        foreach (var q in queries)
        {
            var qv = embedder.Embed([q.Q], EmbedKind.Query)[0];
            var scored = files.Select((f, i) => (f.App, Score: vectors[i] == null ? float.MinValue : TensorPrimitives.Dot<float>(qv, vectors[i]!)))
                .OrderByDescending(s => s.Score).ToList();
            if (q.Apps == null) { nonsense.Add(scored[0].Score); Console.WriteLine($"| {q.Q} (nonsense) | - | - | {F(scored[0].Score)} |"); continue; }
            var rank = scored.FindIndex(s => q.Apps.Contains(s.App, StringComparer.OrdinalIgnoreCase)) + 1;
            known++;
            if (rank == 1) top1++;
            if (rank is >= 1 and <= 3) top3++;
            if (rank > 0) mrr += 1.0 / rank;
            right.AddRange(scored.Where(s => q.Apps.Contains(s.App, StringComparer.OrdinalIgnoreCase)).Select(s => s.Score));
            wrong.AddRange(scored.Where(s => !q.Apps.Contains(s.App, StringComparer.OrdinalIgnoreCase)).Select(s => s.Score));
            var bestRight = scored.Where(s => q.Apps.Contains(s.App, StringComparer.OrdinalIgnoreCase)).Max(s => s.Score);
            var bestWrong = scored.Where(s => !q.Apps.Contains(s.App, StringComparer.OrdinalIgnoreCase)).Max(s => s.Score);
            Console.WriteLine($"| {q.Q} | {rank} | {F(bestRight)} | {F(bestWrong)} |");
        }
        Console.WriteLine($"\nrank 1: {top1} of {known}; top 3: {top3}; MRR {mrr / known:0.000}");
        Console.WriteLine($"score of right pictures: median {F(right.Order().ElementAt(right.Count / 2))}, 90th percentile {F(right.Order().ElementAt(right.Count * 9 / 10))}");
        Console.WriteLine($"score of wrong pictures: median {F(wrong.Order().ElementAt(wrong.Count / 2))}, 90th percentile {F(wrong.Order().ElementAt(wrong.Count * 9 / 10))}");
        Console.WriteLine($"nonsense queries, top score: {string.Join(", ", nonsense.Select(F))}");
    }

    private sealed record Picture(string Path, string App);

    /// <summary>Up to <paramref name="perApp"/> pictures of every known app and of a few other apps as distractors, spread over the folder.</summary>
    private static List<Picture> Sample(string folder, int perApp, HashSet<string> known)
    {
        var all = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(NpuProtocol.IsImageFormat)
            .Where(p => new FileInfo(p).Length < 3 * 1024 * 1024)
            .Select(p => new Picture(p, System.IO.Path.GetFileNameWithoutExtension(p).Split('_')[0..^1] is { Length: > 0 } parts ? string.Join('_', parts) : ""))
            .Where(p => p.App.Length > 0)
            .GroupBy(p => p.App, StringComparer.OrdinalIgnoreCase)
            .Where(g => known.Contains(g.Key) || g.Key is "chrome" or "browser" or "msedge")
            .SelectMany(g => Spread(g.OrderBy(p => p.Path).ToList(), perApp));
        return [.. all];
    }

    private static IEnumerable<Picture> Spread(List<Picture> items, int count) =>
        items.Count <= count ? items : Enumerable.Range(0, count).Select(i => items[i * items.Count / count]);

    private static string F(float v) => v.ToString("0.000", CultureInfo.InvariantCulture);
}
