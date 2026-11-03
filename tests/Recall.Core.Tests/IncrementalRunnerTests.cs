using Recall.Core.Config;
using Recall.Core.Ignore;
using Recall.Core.Index;
using Recall.Core.Scan;
using Recall.Core.Tests.Support;

namespace Recall.Core.Tests;

public class IncrementalRunnerTests : RunnerTestBase
{
    private static Task<RunReport> Auto(Setup s, RootFolder root) => s.Runner.RunAsync([root], null, default, ScanMode.Auto);

    [Fact]
    public async Task First_auto_run_is_a_full_scan()
    {
        using var s = Make();
        Write(@"a\1.txt", "alpha");
        var r = await Auto(s, R());
        Assert.Equal(1, r.Added);
        Assert.Equal(0, r.DirsReused);
        Assert.Equal(2, r.DirsListed);
    }

    [Fact]
    public async Task Second_auto_run_with_no_changes_lists_nothing_and_removes_nothing()
    {
        using var s = Make();
        Write(@"a\1.txt", "alpha");
        Write(@"b\2.txt", "bravo");
        await Auto(s, R());
        var r = await Auto(s, R());
        Assert.Equal(0, r.DirsListed);
        Assert.Equal(3, r.DirsReused);
        Assert.Equal(0, r.Added + r.Changed + r.Removed);
        Assert.Equal((2L, 0L), s.Store.Counts());
    }

    [Fact]
    public async Task New_and_deleted_files_are_found_by_a_fast_run()
    {
        using var s = Make();
        Write(@"a\1.txt", "alpha");
        var doomed = Write(@"b\2.txt", "bravo");
        await Auto(s, R());
        Write(@"a\3.txt", "charlie");
        File.Delete(doomed);
        var r = await Auto(s, R());
        Assert.Equal(1, r.Added);
        Assert.Equal(1, r.Removed);
        Assert.Equal(2, r.DirsListed);
        Assert.Equal(1, Hits("charlie"));
        Assert.Equal(0, Hits("bravo"));
    }

    [Fact]
    public async Task In_place_edit_waits_for_a_full_run()
    {
        using var s = Make();
        var p = Write(@"a\1.txt", "oldword");
        await Auto(s, R());
        File.WriteAllText(p, "newword and more");
        Assert.Equal(0, (await Auto(s, R())).Changed);
        var full = await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(1, full.Changed);
        Assert.Equal(1, Hits("newword"));
    }

    [Fact]
    public async Task Changed_ignore_configuration_forces_a_full_scan()
    {
        using (var s = Make()) { Write(@"a\1.txt", "alpha"); await Auto(s, R()); }
        var cfg = new RecallConfig([], new HashSet<string>(["text"], StringComparer.OrdinalIgnoreCase), []);
        using var store = IndexStore.Open(Db);
        var runner = new IndexRunner(store, new IgnorePolicy(cfg, _ => false), [], new FakeOcr(""));
        var r = await runner.RunAsync([R()], null, default, ScanMode.Auto);
        Assert.Equal(0, r.DirsReused);
        Assert.Equal(2, r.DirsListed);
    }

    [Fact]
    public async Task Expired_full_scan_interval_forces_a_full_scan()
    {
        using var store = IndexStore.Open(Db);
        var cfg = new RecallConfig([], new HashSet<string>(["text"], StringComparer.OrdinalIgnoreCase), []);
        var runner = new IndexRunner(store, new IgnorePolicy(cfg, _ => false), [], new FakeOcr(""), fullScanInterval: TimeSpan.Zero);
        Write(@"a\1.bin", "x");
        await runner.RunAsync([R()], null, default, ScanMode.Auto);
        var r = await runner.RunAsync([R()], null, default, ScanMode.Auto);
        Assert.Equal(0, r.DirsReused);
    }

    [Fact]
    public async Task File_skipped_while_locked_is_picked_up_by_the_next_fast_run()
    {
        using var s = Make();
        var p = Write(@"a\1.txt", "lockedword");
        await Auto(s, R()); // directory state now stored
        var q = Write(@"a\2.txt", "otherword");
        long skipped;
        await using (new FileStream(q, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            skipped = (await Auto(s, R())).Skipped;
        Assert.Equal(1, skipped);
        var r = await Auto(s, R());
        Assert.Equal(1, r.Added);
        Assert.Equal(1, Hits("otherword"));
        Assert.True(File.Exists(p));
    }
}
