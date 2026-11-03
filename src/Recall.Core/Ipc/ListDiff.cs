namespace Recall.Core.Ipc;

// Linked into the Command Palette extension: keep this file free of other Recall.Core types.

/// <summary>What a user changed in a text box that holds one entry per line.</summary>
public static class ListDiff
{
    /// <summary>Trimmed, non-empty lines in order, each normalized, without repeats under <paramref name="comparer"/>.</summary>
    public static List<string> ParseLines(string? text, Func<string, string> normalize, StringComparer comparer)
    {
        var seen = new HashSet<string>(comparer);
        var lines = new List<string>();
        foreach (var raw in (text ?? "").Split(['\r', '\n']))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            line = normalize(line);
            if (line.Length > 0 && seen.Add(line)) lines.Add(line);
        }
        return lines;
    }

    /// <summary>Entries only in <paramref name="wanted"/> (in its order) and entries only in <paramref name="current"/> (in its order).</summary>
    public static (List<string> Added, List<string> Removed) Compute(IEnumerable<string> current, IEnumerable<string> wanted, StringComparer comparer)
    {
        var have = current.ToList();
        var want = wanted.ToList();
        var haveSet = new HashSet<string>(have, comparer);
        var wantSet = new HashSet<string>(want, comparer);
        return ([.. want.Where(w => !haveSet.Contains(w))], [.. have.Where(h => !wantSet.Contains(h))]);
    }
}
