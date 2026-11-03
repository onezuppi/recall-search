using Recall.Core.Ipc;
using Recall.Core.Tests.Support;

namespace Recall.Core.Tests;

public class SettingsListTests : SettingsTestBase
{
    [Fact]
    public async Task Folder_added_to_the_slow_list_is_indexed_but_not_watched()
    {
        var slow = Folder("slow", "a.txt", "quokkaword inside");
        var r = await Send(IpcOps.AddRoot, slow, "slow");
        Assert.True(r.Ok, r.Error);
        Assert.False(r.Settings!.Roots.Single(x => x.Path == slow).Watched);
        Assert.True(await WaitFor(() => Found("quokkaword")));

        File.WriteAllText(Path.Combine(slow, "b.txt"), "wombatword later");
        await Task.Delay(1500);
        Assert.False(Found("wombatword"));
    }

    [Fact]
    public async Task Folder_added_without_a_target_goes_to_the_fast_list()
    {
        var folder = Folder("dflt");
        var r = await Send(IpcOps.AddRoot, folder);
        Assert.True(r.Settings!.Roots.Single(x => x.Path == folder).Watched);
    }

    [Fact]
    public async Task Moving_a_folder_to_fast_starts_watching_it()
    {
        var folder = Folder("moving");
        Assert.True((await Send(IpcOps.AddRoot, folder, "slow")).Ok);
        var moved = await Send(IpcOps.SetWatched, folder, "fast");
        Assert.True(moved.Ok, moved.Error);
        Assert.True(moved.Settings!.Roots.Single(x => x.Path == folder).Watched);

        File.WriteAllText(Path.Combine(folder, "live.txt"), "lemurword live");
        Assert.True(await WaitFor(() => Found("lemurword")));
        Assert.Contains("\"watched\": true", File.ReadAllText(ConfigFile));
    }

    [Fact]
    public async Task Moving_a_folder_to_slow_stops_watching_it()
    {
        var moved = await Send(IpcOps.SetWatched, Root, "slow");
        Assert.True(moved.Ok, moved.Error);
        Assert.False(moved.Settings!.Roots.Single().Watched);

        File.WriteAllText(Path.Combine(Root, "late.txt"), "otterword late");
        await Task.Delay(1500);
        Assert.False(Found("otterword"));
    }

    [Fact]
    public async Task Moving_to_the_list_a_folder_is_already_in_is_harmless()
    {
        Assert.True((await Send(IpcOps.SetWatched, Root, "fast")).Ok);
    }

    [Fact]
    public async Task Moving_needs_a_known_folder_and_a_valid_list()
    {
        Assert.False((await Send(IpcOps.SetWatched, Path.Combine(Dir, "nope"), "fast")).Ok);
        Assert.Contains("fast or slow", (await Send(IpcOps.SetWatched, Root, "sideways")).Error);
        Assert.Contains("fast or slow", (await Send(IpcOps.AddRoot, Folder("x"), "sideways")).Error);
    }

    [Fact]
    public async Task Added_file_type_indexes_files_and_removal_drops_them()
    {
        File.WriteAllText(Path.Combine(Root, "run.zzlog"), "pandaword log line");
        await Task.Delay(1200);
        Assert.False(Found("pandaword"));

        var added = await Send(IpcOps.AddType, " .ZZLog ", "text");
        Assert.True(added.Ok, added.Error);
        var type = Assert.Single(added.Settings!.FileTypes!);
        Assert.Equal((".zzlog", "text"), (type.Extension, type.Group));
        Assert.True(await WaitFor(() => Found("pandaword")));
        Assert.Contains(".zzlog", File.ReadAllText(ConfigFile));

        File.WriteAllText(Path.Combine(Root, "live.zzlog"), "koalaword live");
        Assert.True(await WaitFor(() => Found("koalaword")));

        var removed = await Send(IpcOps.RemoveType, ".zzlog");
        Assert.True(removed.Ok, removed.Error);
        Assert.Empty(removed.Settings!.FileTypes!);
        Assert.True(await WaitFor(() => !Found("pandaword") && !Found("koalaword")));
    }

    [Theory]
    [InlineData("", "text")]
    [InlineData(".", "text")]
    [InlineData("a b", "text")]
    [InlineData("a/b", "text")]
    [InlineData("txt", "text")]
    [InlineData("zzlog", "videos")]
    [InlineData("zzlog", "")]
    public async Task Invalid_file_type_is_rejected(string extension, string group)
    {
        var r = await Send(IpcOps.AddType, extension, group);
        Assert.False(r.Ok);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Duplicate_type_and_unknown_removal_fail()
    {
        Assert.True((await Send(IpcOps.AddType, "zzlog", "text")).Ok);
        Assert.Contains("already", (await Send(IpcOps.AddType, ".zzlog", "text")).Error);
        Assert.False((await Send(IpcOps.RemoveType, ".other")).Ok);
    }

    [Fact]
    public async Task Regex_exclusion_drops_matching_files()
    {
        var dir = Path.Combine(Root, "Backups");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "old.txt"), "dingoword archived");
        Assert.True(await WaitFor(() => Found("dingoword")));

        var added = await Send(IpcOps.AddExclude, "re:/backup(s)?/");
        Assert.True(added.Ok, added.Error);
        Assert.True(await WaitFor(() => !Found("dingoword")));
    }

    [Fact]
    public async Task Invalid_regex_exclusion_is_rejected_with_a_message()
    {
        Assert.StartsWith("Invalid regular expression", (await Send(IpcOps.AddExclude, "re:(")).Error);
        Assert.StartsWith("Unsupported regular expression", (await Send(IpcOps.AddExclude, "re:(?=a)b")).Error);
        Assert.Empty((await Send(IpcOps.Settings)).Settings!.Excludes);
    }
}
