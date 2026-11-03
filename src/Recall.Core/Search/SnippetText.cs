using System.Text.RegularExpressions;

namespace Recall.Core.Search;

/// <summary>Cuts display text out of a stored segment.</summary>
internal static partial class SnippetText
{
    public const int SnippetMax = 160;
    private const int ContextBefore = 150;
    private const int ContextAfter = 450;
    private const string Ellipsis = "…";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Single-line text, at most <see cref="SnippetMax"/> characters.</summary>
    public static string Clean(string s)
    {
        s = Whitespace().Replace(s, " ").Trim();
        return s.Length <= SnippetMax ? s : s[..SnippetMax];
    }

    /// <summary>Short text around the first occurrence of <paramref name="term"/> (used by the fallback scan).</summary>
    public static string Around(string text, string term)
    {
        var i = Math.Max(0, text.IndexOf(term, StringComparison.OrdinalIgnoreCase));
        var start = Math.Max(0, i - 60);
        return Clean(text.Substring(start, Math.Min(SnippetMax, text.Length - start)));
    }

    /// <summary>
    /// Longer text around the first match of any term: about 150 characters before and 450 after, cut at word boundaries,
    /// with an ellipsis on each side that was cut. Without a match the text starts at the beginning.
    /// </summary>
    public static string Context(string text, IReadOnlyList<string> terms)
    {
        text = Whitespace().Replace(text, " ").Trim();
        var first = terms.Select(t => text.IndexOf(t, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, first - ContextBefore);
        var end = Math.Min(text.Length, first + ContextAfter);
        if (start > 0) start = NextWordStart(text, start, first);
        if (end < text.Length) end = PreviousWordEnd(text, end, first);
        var body = text[start..end].Trim();
        return (start > 0 ? Ellipsis + " " : "") + body + (end < text.Length ? " " + Ellipsis : "");
    }

    private static int NextWordStart(string text, int start, int limit)
    {
        var i = text.IndexOf(' ', start, Math.Min(30, limit - start));
        return i < 0 ? start : i + 1;
    }

    private static int PreviousWordEnd(string text, int end, int limit)
    {
        var i = text.LastIndexOf(' ', end - 1, Math.Min(30, end - limit));
        return i < 0 ? end : i;
    }
}
