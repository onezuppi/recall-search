using System.Text;

namespace Recall.Core.Search;

// Linked into the Command Palette extension: keep this file free of other Recall.Core types.

/// <summary>Escapes text for a markdown pane and marks every occurrence of the search terms as a code span.</summary>
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
            CodeSpan(sb, text.AsSpan(start, end - start));
            pos = end;
        }
        Escape(sb, text.AsSpan(pos));
        return sb.ToString();
    }

    /// <summary>
    /// A heading with a short window around the first match, so the pane draws the found words large and the eye lands on them first.
    /// <paramref name="complete"/> is true when the window is the whole text. Empty when nothing matches.
    /// </summary>
    public static string Headline(string text, IReadOnlyList<string> terms, out bool complete, int radius = 40)
    {
        complete = false;
        var ranges = MatchRanges(text, terms);
        if (ranges.Count == 0) return "";
        var (matchStart, matchEnd) = ranges[0];
        var from = Math.Max(0, matchStart - radius);
        var to = Math.Min(text.Length, matchEnd + radius);
        if (from > 0 && text.IndexOf(' ', from, matchStart - from) is var s and >= 0) from = s + 1;
        if (to < text.Length && text.LastIndexOf(' ', to - 1, to - matchEnd) is var e and >= 0) to = e;
        complete = from == 0 && to == text.Length;
        return "### " + (from > 0 ? "… " : "") + Apply(text[from..to], terms) + (to < text.Length ? " …" : "");
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

    /// <summary>A code span is the only markdown the pane draws on a background, so it serves as the highlight.</summary>
    private static void CodeSpan(StringBuilder sb, ReadOnlySpan<char> s)
    {
        var fence = new string('`', LongestBacktickRun(s) + 1);
        var pad = s[0] == '`' || s[^1] == '`' ? " " : "";
        sb.Append(fence).Append(pad).Append(s).Append(pad).Append(fence);
    }

    private static int LongestBacktickRun(ReadOnlySpan<char> s)
    {
        int best = 0, run = 0;
        foreach (var c in s)
        {
            run = c == '`' ? run + 1 : 0;
            best = Math.Max(best, run);
        }
        return best;
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
