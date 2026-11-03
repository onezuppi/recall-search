namespace Recall.Agent;

/// <summary>
/// <c>--once</c> runs one pass and exits. <c>--root PATH</c> (repeatable) replaces the configured roots.
/// <c>--db PATH</c> uses another index file and skips the single-instance check, which is how benchmarks avoid the live index.
/// </summary>
public sealed record CliOptions(bool Once, IReadOnlyList<string> Roots, string? DbPath)
{
    public static CliOptions Parse(string[] args)
    {
        var once = false;
        string? db = null;
        var roots = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--once": once = true; break;
                case "--root" when i + 1 < args.Length: roots.Add(args[++i]); break;
                case "--db" when i + 1 < args.Length: db = args[++i]; break;
                default: throw new ArgumentException($"unknown or incomplete argument: {args[i]}");
            }
        }
        return new CliOptions(once, roots, db);
    }
}
