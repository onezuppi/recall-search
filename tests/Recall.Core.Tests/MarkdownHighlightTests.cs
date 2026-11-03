using Recall.Core.Search;

namespace Recall.Core.Tests;

public class MarkdownHighlightTests
{
    [Fact]
    public void Matches_are_bold_ignoring_case()
    {
        Assert.Equal("see **Contract** terms", MarkdownHighlight.Apply("see Contract terms", ["contract"]));
    }

    [Fact]
    public void Markdown_characters_in_the_text_are_escaped()
    {
        Assert.Equal(@"a\_b \*c\* \[x\](y) **hit**", MarkdownHighlight.Apply("a_b *c* [x](y) hit", ["hit"]));
    }

    [Fact]
    public void Several_terms_and_overlaps_are_merged()
    {
        Assert.Equal("**network map** here", MarkdownHighlight.Apply("network map here", ["network", "network map"]));
    }

    [Fact]
    public void No_terms_returns_escaped_text()
    {
        Assert.Equal(@"plain \# text", MarkdownHighlight.Apply("plain # text", []));
    }
}
