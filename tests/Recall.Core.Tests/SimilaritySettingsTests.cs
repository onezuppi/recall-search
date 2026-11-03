using Recall.Agent;
using Recall.Core.Config;
using Recall.Core.Ipc;

namespace Recall.Core.Tests;

public class SimilaritySettingsTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "rs-sim-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose() { try { File.Delete(_file); } catch { } }

    private sealed class Effects : ISettingsEffects
    {
        public float? Similarity;
        public void RootAdded(RootFolder root) { }
        public void RootRemoved(RootFolder root) { }
        public void ExcludesChanged(IReadOnlyList<string> patterns) { }
        public void SimilarityChanged(float minSimilarity) => Similarity = minSimilarity;
    }

    private SettingsController Make(Effects effects, string? model = "multilingual-e5-small") =>
        new(new RecallConfig([], new HashSet<string>(), []) { EmbeddingModel = model }, _file, null, effects);

    private static IpcRequest Set(string value) => new(IpcOps.SetSimilarity, Value: value);

    [Fact]
    public void Snapshot_shows_the_model_default_until_the_user_sets_a_value()
    {
        Assert.Equal(0.84, Make(new Effects()).Snapshot().MinSimilarity!.Value, 3);
    }

    [Theory]
    [InlineData("0.9")]
    [InlineData("0,9")]
    [InlineData(" 0.9 ")]
    public void Valid_value_is_saved_applied_and_shown(string typed)
    {
        var effects = new Effects();
        var controller = Make(effects);
        var response = controller.Handle(Set(typed));
        Assert.True(response.Ok);
        Assert.Equal(0.9f, effects.Similarity);
        Assert.Equal(0.9, response.Settings!.MinSimilarity!.Value, 3);
        Assert.Equal(0.9f, RecallConfig.Load(_file).EmbeddingMinSimilarity);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1.5")]
    [InlineData("-0.1")]
    [InlineData("NaN")]
    public void Invalid_value_is_rejected_and_nothing_changes(string typed)
    {
        var effects = new Effects();
        var response = Make(effects).Handle(Set(typed));
        Assert.False(response.Ok);
        Assert.Null(effects.Similarity);
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public void Semantic_search_off_has_no_threshold_and_refuses_to_set_one()
    {
        var effects = new Effects();
        var controller = Make(effects, model: null);
        Assert.Null(controller.Snapshot().MinSimilarity);
        Assert.False(controller.Handle(Set("0.5")).Ok);
        Assert.Null(effects.Similarity);
    }
}
