using Recall.Core.Search;

namespace Recall.Core.Tests;

public class MarkdownHighlightTests
{
    [Fact]
    public void Matches_are_code_spans_ignoring_case()
    {
        Assert.Equal("see `Contract` terms", MarkdownHighlight.Apply("see Contract terms", ["contract"]));
    }

    [Fact]
    public void Markdown_characters_in_the_text_are_escaped_but_not_inside_a_match()
    {
        Assert.Equal(@"a\_b \*c\* \[x\](y) `hit`", MarkdownHighlight.Apply("a_b *c* [x](y) hit", ["hit"]));
        Assert.Equal("`a_b`", MarkdownHighlight.Apply("a_b", ["a_b"]));
    }

    [Fact]
    public void Several_terms_and_overlaps_are_merged()
    {
        Assert.Equal("`network map` here", MarkdownHighlight.Apply("network map here", ["network", "network map"]));
    }

    [Fact]
    public void A_match_with_backticks_gets_a_longer_fence()
    {
        Assert.Equal("``a`b``", MarkdownHighlight.Apply("a`b", ["a`b"]));
        Assert.Equal("`` `x ``", MarkdownHighlight.Apply("`x", ["`x"]));
    }

    [Fact]
    public void No_terms_returns_escaped_text()
    {
        Assert.Equal(@"plain \# text", MarkdownHighlight.Apply("plain # text", []));
    }
}
