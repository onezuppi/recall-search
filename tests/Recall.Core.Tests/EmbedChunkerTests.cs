using Recall.Core.Embed;

namespace Recall.Core.Tests;

public class EmbedChunkerTests
{
    private static string Words(int count) => string.Join(' ', Enumerable.Range(0, count).Select(i => "слово" + i));

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void Blank_text_gives_no_chunks(string text) => Assert.Empty(EmbedChunker.Split(text));

    [Fact]
    public void Emoji_only_text_gives_one_chunk() => Assert.Single(EmbedChunker.Split("😀😀"));

    [Fact]
    public void Short_text_is_one_chunk() => Assert.Equal(["Привет мир"], EmbedChunker.Split("Привет\n мир"));

    [Fact]
    public void Long_text_is_capped_at_16_chunks() =>
        Assert.Equal(EmbedChunker.MaxChunks, EmbedChunker.Split(new string('a', 100_000)).Count);

    [Fact]
    public void Chunks_overlap()
    {
        var chunks = EmbedChunker.Split(Words(400));
        Assert.True(chunks.Count > 1);
        var lastWord = chunks[0].Split(' ')[^1];
        Assert.Contains(lastWord, chunks[1]);
    }

    [Fact]
    public void No_chunk_exceeds_the_limit()
    {
        Assert.All(EmbedChunker.Split(Words(2000)), c => Assert.True(c.Length <= EmbedChunker.ChunkChars));
        Assert.All(EmbedChunker.Split(string.Concat(Enumerable.Repeat("😀", 3000))), c => Assert.True(c.Length <= EmbedChunker.ChunkChars));
    }

    [Fact]
    public void Surrogate_pairs_are_never_split()
    {
        var chunks = EmbedChunker.Split(string.Concat(Enumerable.Repeat("😀", 3000)));
        Assert.All(chunks, c => Assert.False(char.IsLowSurrogate(c[0]) || char.IsHighSurrogate(c[^1])));
    }
}
