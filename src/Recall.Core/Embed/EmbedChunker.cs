using System.Text.RegularExpressions;

namespace Recall.Core.Embed;

/// <summary>Cuts a segment into overlapping windows of about 1000 characters. Only the first 16 windows are used.</summary>
public static partial class EmbedChunker
{
    public const int ChunkChars = 1000;
    public const int Overlap = 100;
    public const int MaxChunks = 16;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static IReadOnlyList<string> Split(string text)
    {
        var s = Whitespace().Replace(text, " ").Trim();
        var chunks = new List<string>();
        var start = 0;
        while (start < s.Length && chunks.Count < MaxChunks)
        {
            var end = Math.Min(start + ChunkChars, s.Length);
            if (end < s.Length)
            {
                var space = s.LastIndexOf(' ', end - 1, Overlap);
                if (space > start) end = space;
                else if (char.IsHighSurrogate(s[end - 1])) end--;
            }
            chunks.Add(s[start..end].Trim());
            if (end >= s.Length) break;
            start = Math.Max(end - Overlap, start + 1);
            if (char.IsLowSurrogate(s[start])) start++;
        }
        return chunks;
    }
}
