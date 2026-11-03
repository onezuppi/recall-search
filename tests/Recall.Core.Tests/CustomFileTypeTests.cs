using Recall.Core.Config;
using Recall.Core.Extract;
using Recall.Core.Ignore;

namespace Recall.Core.Tests;

public class CustomFileTypeTests
{
    private static readonly Dictionary<string, string> Zz = new() { [".zzlog"] = "text" };

    private static IgnorePolicy Policy() =>
        new(new RecallConfig([], new HashSet<string>(["text"]), []), _ => false);

    [Fact]
    public void Unknown_extension_is_skipped_until_a_type_is_added()
    {
        var policy = Policy();
        Assert.Equal(FileDecision.SkipType, policy.IndexFile(@"C:\d\a.zzlog", 10, FileAttributes.Normal, []));
        policy.SetCustomTypes(Zz);
        Assert.Equal(FileDecision.Index, policy.IndexFile(@"C:\d\a.ZZLOG", 10, FileAttributes.Normal, []));
    }

    [Fact]
    public void Custom_type_in_a_disabled_group_is_still_skipped()
    {
        var policy = Policy();
        policy.SetCustomTypes(new Dictionary<string, string> { [".zzpic"] = "images" });
        Assert.Equal(FileDecision.SkipType, policy.IndexFile(@"C:\d\a.zzpic", 10, FileAttributes.Normal, []));
    }

    [Fact]
    public void GroupOf_prefers_built_in_types_and_falls_back_to_custom()
    {
        var policy = Policy();
        policy.SetCustomTypes(new Dictionary<string, string> { [".txt"] = "images", [".zzlog"] = "text" });
        Assert.Equal("text", policy.GroupOf(".txt"));
        Assert.Equal("text", policy.GroupOf("zzlog"));
        Assert.Null(policy.GroupOf(".other"));
    }

    [Fact]
    public void Fingerprint_follows_custom_types()
    {
        var policy = Policy();
        var before = policy.Fingerprint;
        policy.SetCustomTypes(Zz);
        var withType = policy.Fingerprint;
        Assert.NotEqual(before, withType);
        policy.SetCustomTypes(new Dictionary<string, string>());
        Assert.Equal(before, policy.Fingerprint);
    }

    [Fact]
    public void Config_reads_custom_types_from_json()
    {
        var path = Path.Combine(Path.GetTempPath(), "rs-types-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new RecallConfig([], new HashSet<string>(["text"]), []) { CustomFileTypes = Zz }.Save(path);
            Assert.Equal("text", RecallConfig.Load(path).CustomFileTypes[".zzlog"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Config_without_custom_types_loads_with_an_empty_map()
    {
        var path = Path.Combine(Path.GetTempPath(), "rs-types-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{ "roots": [], "enabledGroups": ["text"], "globalIgnorePatterns": [] }""");
            Assert.Empty(RecallConfig.Load(path).CustomFileTypes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Group_extractor_handles_built_in_and_custom_extensions()
    {
        var policy = Policy();
        policy.SetCustomTypes(Zz);
        var extractor = new GroupExtractor(new TextExtractor(), BuiltInRules.Text, policy.GroupOf);
        Assert.True(extractor.CanHandle(".txt"));
        Assert.True(extractor.CanHandle(".zzlog"));
        Assert.False(extractor.CanHandle(".pdf"));
    }
}
