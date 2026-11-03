using Recall.Core.Ipc;
using Recall.Core.Tests.Support;

namespace Recall.Core.Tests;

public class SettingsTests : SettingsTestBase
{
    [Fact]
    public async Task Settings_lists_roots_and_excludes()
    {
        var r = await Send(IpcOps.Settings);
        Assert.True(r.Ok);
        Assert.Equal(Root, Assert.Single(r.Settings!.Roots).Path);
        Assert.Empty(r.Settings.Excludes);
    }

    [Fact]
    public async Task Added_root_is_saved_indexed_and_watched()
    {
        var extra = Folder("extra", "a.txt", "quokkaword inside");
        var r = await Send(IpcOps.AddRoot, extra);
        Assert.True(r.Ok, r.Error);
        Assert.Contains(r.Settings!.Roots, x => x.Path == extra);
        Assert.Contains(extra.Replace("\\", "\\\\"), File.ReadAllText(ConfigFile));
        Assert.True(await WaitFor(() => Found("quokkaword")));

        File.WriteAllText(Path.Combine(extra, "b.txt"), "wombatword later");
        Assert.True(await WaitFor(() => Found("wombatword")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative\\folder")]
    [InlineData("C:\\")]
    public async Task Invalid_root_is_rejected(string path)
    {
        var r = await Send(IpcOps.AddRoot, path);
        Assert.False(r.Ok);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task Missing_system_duplicate_and_nested_roots_are_rejected()
    {
        Assert.Contains("does not exist", (await Send(IpcOps.AddRoot, Path.Combine(Dir, "nope"))).Error);
        Assert.Contains("System", (await Send(IpcOps.AddRoot, Folder("system"))).Error);
        Assert.Contains("already", (await Send(IpcOps.AddRoot, Root.ToUpperInvariant())).Error);
        Assert.Contains("covered", (await Send(IpcOps.AddRoot, Folder(Path.Combine("watched", "inner")))).Error);
        Assert.Contains("contains", (await Send(IpcOps.AddRoot, Dir)).Error);
        Assert.Single((await Send(IpcOps.Settings)).Settings!.Roots);
    }

    [Fact]
    public async Task Removed_root_files_leave_the_index()
    {
        File.WriteAllText(Path.Combine(Root, "x.txt"), "narwhalword text");
        Assert.True(await WaitFor(() => Found("narwhalword")));
        var r = await Send(IpcOps.RemoveRoot, Root);
        Assert.True(r.Ok, r.Error);
        Assert.Empty(r.Settings!.Roots);
        Assert.True(await WaitFor(() => !Found("narwhalword")));
        Assert.DoesNotContain(Root.Replace("\\", "\\\\"), File.ReadAllText(ConfigFile));
    }

    [Fact]
    public async Task Removing_an_unknown_root_fails()
    {
        Assert.False((await Send(IpcOps.RemoveRoot, Path.Combine(Dir, "other"))).Ok);
    }

    [Fact]
    public async Task Exclude_pattern_drops_matching_files_and_removal_brings_them_back()
    {
        // One file at a time: concurrent first-time indexing of several files is a separate, known race in IndexStore.
        File.WriteAllText(Path.Combine(Root, "keep.txt"), "pandaword here");
        Assert.True(await WaitFor(() => Found("pandaword")), "keep");
        File.WriteAllText(Path.Combine(Root, "drop.skipme.txt"), "lemurword here");
        Assert.True(await WaitFor(() => Found("lemurword")), "drop");

        var added = await Send(IpcOps.AddExclude, "  *.skipme.txt ");
        Assert.True(added.Ok, added.Error);
        Assert.Equal("*.skipme.txt", Assert.Single(added.Settings!.Excludes));
        Assert.True(await WaitFor(() => !Found("lemurword")));
        Assert.True(Found("pandaword"));

        File.WriteAllText(Path.Combine(Root, "new.skipme.txt"), "otterword here");
        await Task.Delay(1500);
        Assert.False(Found("otterword"));

        var removed = await Send(IpcOps.RemoveExclude, "*.skipme.txt");
        Assert.True(removed.Ok, removed.Error);
        Assert.Empty(removed.Settings!.Excludes);
        Assert.True(await WaitFor(() => Found("lemurword")), "lemurword");
        Assert.True(await WaitFor(() => Found("otterword")), "otterword");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# comment")]
    [InlineData("bad\npattern")]
    public async Task Invalid_exclude_pattern_is_rejected(string pattern)
    {
        Assert.False((await Send(IpcOps.AddExclude, pattern)).Ok);
    }

    [Fact]
    public async Task Duplicate_exclude_and_unknown_removal_fail()
    {
        Assert.True((await Send(IpcOps.AddExclude, "*.tmp")).Ok);
        Assert.False((await Send(IpcOps.AddExclude, "*.tmp")).Ok);
        Assert.False((await Send(IpcOps.RemoveExclude, "*.other")).Ok);
    }

    [Fact]
    public async Task Failed_save_keeps_the_old_settings()
    {
        Directory.CreateDirectory(ConfigFile); // a directory where the file should be makes Save throw
        var r = await Send(IpcOps.AddExclude, "*.tmp");
        Assert.False(r.Ok);
        Assert.Empty((await Send(IpcOps.Settings)).Settings!.Excludes);
    }
}
