using Recall.Core.Search;

namespace Recall.Core.Tests;

public class RankFusionTests
{
    [Fact]
    public void Both_lists_beat_single_list()
    {
        var fused = RankFusion.Fuse(["a", "b"], ["c", "b"]);
        Assert.Equal("b", fused[0].Key);
    }

    [Fact]
    public void Text_rank_one_beats_vector_rank_one()
    {
        var fused = RankFusion.Fuse(["a"], ["v"]);
        Assert.Equal(["a", "v"], fused.Select(f => f.Key));
    }

    [Fact]
    public void Text_hit_at_rank_ten_still_beats_vector_rank_one()
    {
        var text = Enumerable.Range(0, 10).Select(i => "t" + i).ToList();
        var fused = RankFusion.Fuse(text, ["v"]);
        Assert.Equal("v", fused[^1].Key);
    }

    [Fact]
    public void Empty_vector_list_keeps_text_order() =>
        Assert.Equal(["a", "b", "c"], RankFusion.Fuse(["a", "b", "c"], []).Select(f => f.Key));

    [Fact]
    public void Paths_match_case_insensitively() =>
        Assert.Single(RankFusion.Fuse([@"C:\A.txt"], [@"c:\a.TXT"]));
}
