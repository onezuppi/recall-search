using System.Text;

namespace Recall.Core.Search;

// Linked into the Command Palette extension: keep this file free of other Recall.Core types.

/// <summary>Escapes text for a markdown pane and makes every occurrence of the search terms bold.</summary>
public static class MarkdownHighlight
{
    private const string Special = "\\`*_[]#";

    public static string Apply(string text, IReadOnlyList<string> terms)
    {
        var sb = new StringBuilder(text.Length + 16);
        var pos = 0;
        foreach (var (start, end) in MatchRanges(text, terms))
        {
            Escape(sb, text.AsSpan(pos, start - pos));
            sb.Append("**");
            Escape(sb, text.AsSpan(start, end - start));
            sb.Append("**");
            pos = end;
        }
        Escape(sb, text.AsSpan(pos));
        return sb.ToString();
    }

    /// <summary>Sorted, non-overlapping character ranges where any term occurs (case-insensitive).</summary>
    private static List<(int Start, int End)> MatchRanges(string text, IReadOnlyList<string> terms)
    {
        var raw = new List<(int Start, int End)>();
        foreach (var term in terms.Where(t => t.Length > 0))
        {
            var i = 0;
            while ((i = text.IndexOf(term, i, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                raw.Add((i, i + term.Length));
                i += term.Length;
            }
        }
        raw.Sort();
        var merged = new List<(int Start, int End)>();
        foreach (var r in raw)
        {
            if (merged.Count > 0 && r.Start <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, r.End));
            else merged.Add(r);
        }
        return merged;
    }

    private static void Escape(StringBuilder sb, ReadOnlySpan<char> s)
    {
        foreach (var c in s)
        {
            if (Special.Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
    }
}
