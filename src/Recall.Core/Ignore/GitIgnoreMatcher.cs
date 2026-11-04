using System.Text;
using System.Text.RegularExpressions;

namespace Recall.Core.Ignore;

public enum MatchResult { None, Ignored, Included }

/// <summary>Gitignore semantics for one ignore file. Matching is case-insensitive (NTFS).</summary>
public sealed class GitIgnoreMatcher
{
    private sealed record Rule(Regex Pattern, bool Negated, bool DirOnly);

    private readonly string _baseDir;
    private readonly List<Rule> _rules = [];

    private GitIgnoreMatcher(string baseDir) => _baseDir = baseDir.TrimEnd('\\', '/');

    public bool IsEmpty => _rules.Count == 0;

    /// <param name="baseDir">Folder that holds the ignore file; empty for global patterns, which match anywhere.</param>
    public static GitIgnoreMatcher Parse(string baseDir, IEnumerable<string> lines)
    {
        var m = new GitIgnoreMatcher(baseDir);
        foreach (var raw in lines)
        {
            var line = TrimUnescapedTrailingSpaces(raw.TrimEnd('\r', '\n'));
            if (line.Length == 0 || line[0] == '#') continue;
            var negated = false;
            if (line[0] == '!') { negated = true; line = line[1..]; }
            else if (line.StartsWith("\\#") || line.StartsWith("\\!")) line = line[1..];
            var dirOnly = line.EndsWith('/');
            if (dirOnly) line = line.TrimEnd('/');
            if (line.Length == 0) continue;
            var anchored = line.Contains('/');
            if (line[0] == '/') line = line[1..];
            Regex pattern;
            // Git itself ignores a line it cannot read, and the file may belong to any repository on the disk: skip the line, not the scan.
            try { pattern = new Regex(ToRegex(line, anchored), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
            catch (ArgumentException) { continue; }
            m._rules.Add(new Rule(pattern, negated, dirOnly));
        }
        return m;
    }

    public MatchResult Match(string fullPath, bool isDirectory)
    {
        var rel = Relative(fullPath);
        if (rel == null) return MatchResult.None;
        var result = MatchResult.None;
        foreach (var r in _rules)
        {
            var mt = r.Pattern.Match(rel);
            if (!mt.Success) continue;
            var beneath = mt.Groups["rest"].Success;
            if (r.DirOnly && !beneath && !isDirectory) continue;
            result = r.Negated ? MatchResult.Included : MatchResult.Ignored;
        }
        return result;
    }

    private string? Relative(string fullPath)
    {
        var p = fullPath.Replace('\\', '/');
        if (_baseDir.Length == 0) return p.TrimStart('/');
        var b = _baseDir.Replace('\\', '/');
        if (p.Length <= b.Length || !p.StartsWith(b, StringComparison.OrdinalIgnoreCase) || p[b.Length] != '/') return null;
        return p[(b.Length + 1)..];
    }

    private static string TrimUnescapedTrailingSpaces(string s)
    {
        var end = s.Length;
        while (end > 0 && s[end - 1] == ' ' && !(end > 1 && s[end - 2] == '\\')) end--;
        return s[..end];
    }

    private static string ToRegex(string glob, bool anchored)
    {
        var sb = new StringBuilder(anchored ? "^" : "(?:^|.*/)");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        var leading = i == 0 || glob[i - 1] == '/';
                        var trailing = i + 2 >= glob.Length || glob[i + 2] == '/';
                        if (leading && trailing && i + 2 < glob.Length) { sb.Append("(?:.*/)?"); i += 2; }
                        else { sb.Append(".*"); i++; }
                    }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                case '[':
                    var close = glob.IndexOf(']', Math.Min(i + 2, glob.Length - 1));
                    if (close < 0) { sb.Append(@"\["); break; }
                    var cls = glob[(i + 1)..close];
                    if (cls.StartsWith('!')) cls = "^" + cls[1..];
                    sb.Append('[').Append(cls.Replace("\\", "\\\\")).Append(']');
                    i = close;
                    break;
                case '\\':
                    if (i + 1 < glob.Length) { sb.Append(Regex.Escape(glob[i + 1].ToString())); i++; }
                    break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        sb.Append("(?<rest>/.*)?$");
        return sb.ToString();
    }
}
