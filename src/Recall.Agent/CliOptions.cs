namespace Recall.Agent;

/// <summary>
/// <c>--once</c> runs one pass and exits. <c>--root PATH</c> (repeatable) replaces the configured roots.
/// <c>--fast</c> skips directories unchanged since the last pass (see <c>ScanMode.Auto</c>).
/// <c>--embedding-model ID</c> turns embeddings on for this run (benchmarks), whatever the config says.
/// <c>--scan-threads N</c> lists N directories at the same time during a scan.
/// <c>--workers N</c> sets how many files are processed at once (1 gives single-thread figures).
/// <c>--db PATH</c> uses another index file and skips the single-instance check, which is how benchmarks avoid the live index.
/// </summary>
public sealed record CliOptions(
    bool Once, IReadOnlyList<string> Roots, string? DbPath, int? Workers = null, bool Fast = false, int? ScanThreads = null, string? EmbeddingModel = null)
{
    public static CliOptions Parse(string[] args)
    {
        var once = false;
        var fast = false;
        int? scanThreads = null;
        string? db = null;
        string? model = null;
        int? workers = null;
        var roots = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--once": once = true; break;
                case "--fast": fast = true; break;
                case "--scan-threads" when i + 1 < args.Length && int.TryParse(args[i + 1], out var t) && t > 0: scanThreads = t; i++; break;
                case "--root" when i + 1 < args.Length: roots.Add(args[++i]); break;
                case "--db" when i + 1 < args.Length: db = args[++i]; break;
                case "--workers" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n) && n > 0: workers = n; i++; break;
                case "--embedding-model" when i + 1 < args.Length: model = args[++i]; break;
                default: throw new ArgumentException($"unknown or incomplete argument: {args[i]}");
            }
        }
        return new CliOptions(once, roots, db, workers, fast, scanThreads, model);
    }
}
