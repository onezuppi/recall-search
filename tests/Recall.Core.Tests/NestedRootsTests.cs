using Recall.Core.Ipc;
using Recall.Core.Tests.Support;

namespace Recall.Core.Tests;

public class NestedRootsTests : SettingsTestBase
{
    [Fact]
    public async Task Slow_folder_inside_a_fast_one_is_accepted()
    {
        var inner = Folder(Path.Combine("watched", "archive"), "old.txt", "narwhalword archived");
        var r = await Send(IpcOps.AddRoot, inner, "slow");
        Assert.True(r.Ok, r.Error);
        Assert.True(await WaitFor(() => Found("narwhalword")));
    }

    [Fact]
    public async Task Fast_folder_inside_a_slow_one_is_watched_and_the_slow_one_is_not()
    {
        var outer = Folder("outer", "top.txt", "tapirword top");
        var inner = Folder(Path.Combine("outer", "live"), "in.txt", "ocelotword inner");
        Assert.True((await Send(IpcOps.AddRoot, outer, "slow")).Ok);
        var r = await Send(IpcOps.AddRoot, inner, "fast");
        Assert.True(r.Ok, r.Error);
        Assert.True(await WaitFor(() => Found("ocelotword") && Found("tapirword")));

        File.WriteAllText(Path.Combine(inner, "new.txt"), "okapiword live");
        Assert.True(await WaitFor(() => Found("okapiword")));
        File.WriteAllText(Path.Combine(outer, "late.txt"), "gnuword not watched");
        await Task.Delay(1500);
        Assert.False(Found("gnuword"));
    }

    [Fact]
    public async Task Removing_the_inner_folder_returns_its_files_to_the_outer_one()
    {
        var outer = Folder("outer2");
        var inner = Folder(Path.Combine("outer2", "live"), "in.txt", "yakword inner");
        Assert.True((await Send(IpcOps.AddRoot, outer, "slow")).Ok);
        Assert.True((await Send(IpcOps.AddRoot, inner, "fast")).Ok);
        Assert.True(await WaitFor(() => Found("yakword")));

        Assert.True((await Send(IpcOps.RemoveRoot, inner)).Ok);
        await Task.Delay(1500);
        Assert.True(await WaitFor(() => Found("yakword")));
    }

    [Fact]
    public async Task Same_kind_of_folders_still_cannot_nest()
    {
        var outer = Folder("outer3");
        var inner = Folder(Path.Combine("outer3", "in"));
        Assert.True((await Send(IpcOps.AddRoot, outer, "slow")).Ok);
        Assert.Contains("covered", (await Send(IpcOps.AddRoot, inner, "slow")).Error);
    }

    [Fact]
    public async Task Moving_a_folder_into_the_kind_of_its_container_is_refused()
    {
        var outer = Folder("outer4");
        var inner = Folder(Path.Combine("outer4", "in"));
        Assert.True((await Send(IpcOps.AddRoot, outer, "slow")).Ok);
        Assert.True((await Send(IpcOps.AddRoot, inner, "fast")).Ok);
        Assert.Contains("covered", (await Send(IpcOps.SetWatched, inner, "slow")).Error);
    }
}
