using Recall.Core.Embed;

namespace Recall.EmbedEval;

internal sealed record Chunk(string File, string Text);

internal sealed record Options(string Model, IReadOnlyList<string> Corpus, string ModelsRoot, string? Server, string? Bundle)
{
    public static Options? Parse(string[] args)
    {
        string? model = null, root = null, server = null, bundle = null;
        var corpus = new List<string>();
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--model": model = args[i + 1]; break;
                case "--corpus": corpus.Add(args[i + 1]); break;
                case "--models-root": root = args[i + 1]; break;
                case "--server": server = args[i + 1]; break;
                case "--bundle": bundle = args[i + 1]; break;
                default: return null;
            }
        }
        return model == null || corpus.Count == 0 ? null : new Options(model, corpus, root ?? EmbeddingSetup.ModelsRoot, server, bundle);
    }
}

/// <summary>Every Markdown file below the given folders, cut into the same chunks the agent embeds.</summary>
internal static class Corpus
{
    private static readonly string[] Skipped = [".claude", "node_modules", ".git", "bin", "obj"];

    public static IReadOnlyList<Chunk> Load(IEnumerable<string> roots) =>
    [
        .. roots.SelectMany(r => Directory.EnumerateFiles(r, "*.md", SearchOption.AllDirectories))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => Skipped.Contains(p, StringComparer.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(f => EmbedChunker.Split(File.ReadAllText(f)).Select(t => new Chunk(f, t))),
    ];
}
