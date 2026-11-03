using System.Globalization;
using System.Text.RegularExpressions;

namespace Recall.Core.Search;

public sealed record ParsedQuery(
    IReadOnlyList<string> Fragments,
    IReadOnlyList<string> Phrases,
    string? In,
    string? Type,
    DateTime? After,
    DateTime? Before)
{
    public bool IsEmpty => Fragments.Count == 0 && Phrases.Count == 0;
}

public static partial class QueryParser
{
    // "quoted phrase" | key:value | key:"quoted value" | bare word
    [GeneratedRegex("""(?<kv>\b(?<k>in|type|after|before):(?<v>"[^"]*"|\S+))|(?<q>"[^"]*")|(?<w>\S+)""", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    public static ParsedQuery Parse(string text)
    {
        var fragments = new List<string>();
        var phrases = new List<string>();
        string? inValue = null, type = null;
        DateTime? after = null, before = null;

        foreach (Match m in TokenRegex().Matches(text ?? ""))
        {
            if (m.Groups["kv"].Success)
            {
                var v = Unquote(m.Groups["v"].Value);
                if (v.Length == 0) continue;
                switch (m.Groups["k"].Value.ToLowerInvariant())
                {
                    case "in": inValue = v; break;
                    case "type": type = v.TrimStart('.').ToLowerInvariant(); break;
                    case "after": after = ParseDate(v) ?? after; break;
                    case "before": before = ParseDate(v) ?? before; break;
                }
            }
            else if (m.Groups["q"].Success)
            {
                var p = Unquote(m.Groups["q"].Value).Trim();
                if (p.Length > 0) phrases.Add(p);
            }
            else fragments.Add(m.Groups["w"].Value);
        }
        return new ParsedQuery(fragments, phrases, inValue, type, after, before);
    }

    private static string Unquote(string s) => s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    /// <summary>Accepts 2026, 2026-05 and 2026-05-14; returns the start of that period (local time).</summary>
    private static DateTime? ParseDate(string s)
    {
        string[] formats = ["yyyy", "yyyy-MM", "yyyy-M", "yyyy-MM-dd", "yyyy-M-d"];
        return DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}
