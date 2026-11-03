using System.Globalization;
using System.Text;

namespace Recall.Core.Embed;

/// <summary>
/// Line protocol of <c>windows_embed_server</c> (see tools/npu-embed-server). Request: <c>prompt TAB text</c> with
/// backslash escapes for backslash, newline, tab and CR. Response: <c>V f32 f32 ...</c> or <c>ERR TAB message</c>.
/// </summary>
public static class NpuProtocol
{
    public const string QueryPrompt = "SearchQuery", PassagePrompt = "Document";

    public static string PromptFor(EmbedKind kind) => kind == EmbedKind.Query ? QueryPrompt : PassagePrompt;

    public static string Request(EmbedKind kind, string text) => PromptFor(kind) + "\t" + Escape(text);

    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>The vector of a response line, L2-normalized. Throws <see cref="InvalidDataException"/> for an error or a wrong size.</summary>
    public static float[] ParseVector(string? line, int dimensions)
    {
        if (line == null) throw new InvalidDataException("the embedding server closed its output");
        if (line.StartsWith("ERR", StringComparison.Ordinal)) throw new InvalidDataException("embedding server: " + line.Replace('\t', ' '));
        if (!line.StartsWith("V ", StringComparison.Ordinal)) throw new InvalidDataException("unexpected reply from the embedding server");
        var vector = new float[dimensions];
        var count = 0;
        foreach (var part in line.AsSpan(2).ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (count == dimensions) throw new InvalidDataException($"the embedding server returned more than {dimensions} values");
            vector[count++] = float.Parse(part, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        if (count != dimensions) throw new InvalidDataException($"expected {dimensions} values, got {count}");
        var norm = MathF.Sqrt(vector.Sum(x => x * x));
        if (!(norm > 0)) throw new InvalidDataException("the embedding server returned a zero vector");
        for (var i = 0; i < dimensions; i++) vector[i] /= norm;
        return vector;
    }
}
