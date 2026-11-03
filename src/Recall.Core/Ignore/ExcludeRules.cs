using System.Text.RegularExpressions;

namespace Recall.Core.Ignore;

/// <summary>
/// The user's global exclude patterns. A pattern is a gitignore-style glob, or a regular expression when it starts with "re:".
/// A regular expression is matched case-insensitively against the full path with forward slashes (a directory path ends with a slash)
/// and runs without backtracking, so no pattern can stall a scan.
/// </summary>
public sealed class ExcludeRules
{
    public const string RegexPrefix = "re:";
    private const int MaxPatternLength = 300;
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    private readonly Regex[] _regexes;

    private ExcludeRules(GitIgnoreMatcher globs, Regex[] regexes)
    {
        Globs = globs;
        _regexes = regexes;
    }

    public GitIgnoreMatcher Globs { get; }

    /// <summary>Patterns that cannot be compiled (a hand-edited config) are left out instead of failing the scan.</summary>
    public static ExcludeRules Parse(IEnumerable<string> patterns)
    {
        var globs = new List<string>();
        var regexes = new List<Regex>();
        foreach (var p in patterns)
        {
            if (!IsRegex(p)) { globs.Add(p); continue; }
            var regex = Compile(p[RegexPrefix.Length..], out _);
            if (regex != null) regexes.Add(regex);
        }
        return new ExcludeRules(GitIgnoreMatcher.Parse("", globs), [.. regexes]);
    }

    public bool MatchesRegex(string fullPath, bool isDirectory)
    {
        if (_regexes.Length == 0) return false;
        var path = fullPath.Replace('\\', '/');
        if (isDirectory) path += "/";
        foreach (var r in _regexes)
            if (r.IsMatch(path)) return true;
        return false;
    }

    /// <summary>Null when the pattern can be added, otherwise a message for the user.</summary>
    public static string? Validate(string pattern)
    {
        if (pattern.Length == 0) return "Pattern is empty";
        if (pattern.Length > MaxPatternLength) return "Pattern is too long";
        if (pattern.Any(char.IsControl)) return "Pattern contains control characters";
        if (IsRegex(pattern))
        {
            var body = pattern[RegexPrefix.Length..];
            if (body.Length == 0) return "Regular expression is empty";
            Compile(body, out var error);
            return error;
        }
        return pattern[0] == '#' ? "Pattern cannot start with #" : null;
    }

    private static bool IsRegex(string pattern) => pattern.StartsWith(RegexPrefix, StringComparison.Ordinal);

    private static Regex? Compile(string body, out string? error)
    {
        error = null;
        try { return new Regex(body, Options); }
        catch (NotSupportedException e) { error = "Unsupported regular expression: " + e.Message; }
        catch (ArgumentException e) { error = "Invalid regular expression: " + e.Message; }
        return null;
    }
}
