using Recall.Core.Embed;
using Recall.Core.Tests.Support;

namespace Recall.Core.Tests;

public class EmbedTokenizerTests
{
    private static E5Tokenizer Make(int max = 512) =>
        new(Path.Combine(ModelFixture.Dir, ModelFixture.Spec.Tokenizer.FileName), max);

    [SkippableTheory]
    [InlineData("query: Привет, мир", new[] { 0, 41, 1294, 12, 1813, 18454, 4, 11373, 2 })]
    [InlineData("passage: Hello world", new[] { 0, 46692, 12, 35378, 8999, 2 })]
    [InlineData("passage: Счёт № 42 от 03.11.2026 😀", new[] { 0, 46692, 12, 589, 96732, 438, 4828, 183, 5098, 39618, 4046, 21119, 2 })]
    public void Ids_match_reference_tokenizer(string text, int[] expected)
    {
        ModelFixture.RequireModel();
        Assert.Equal(expected, Make().Encode(text));
    }

    [SkippableFact]
    public void Truncates_to_max_tokens()
    {
        ModelFixture.RequireModel();
        var ids = Make(512).Encode(string.Join(' ', Enumerable.Repeat("слово", 3000)));
        Assert.Equal(512, ids.Length);
        Assert.Equal(2, ids[^1]);
    }
}
