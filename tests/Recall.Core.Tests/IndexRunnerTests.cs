using Recall.Core.Tests.Support;

namespace Recall.Core.Tests;

public class IndexRunnerTests : RunnerTestBase
{
    [Fact]
    public async Task First_run_indexes_all_then_second_run_extracts_nothing()
    {
        using var s = Make();
        Write("a.txt", "alpha document");
        Write("b.md", "bravo document");
        var r1 = await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(2, r1.Added);
        Assert.Equal(2, r1.Extracted);
        var calls = s.Text.Calls;
        Assert.Equal(2, calls);

        var r2 = await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(0, r2.Extracted);
        Assert.Equal(0, r2.Hashed);
        Assert.Equal(0, r2.Added + r2.Changed + r2.Removed);
        Assert.Equal(calls, s.Text.Calls);
        Assert.Equal((2L, 0L), s.Store.Counts());
    }

    [Fact]
    public async Task Modified_file_is_reextracted_and_old_text_is_gone()
    {
        using var s = Make();
        var p = Write("a.txt", "oldword here");
        await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(1, Hits("oldword"));
        File.WriteAllText(p, "newword here, longer");
        var r = await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(1, r.Changed);
        Assert.Equal(0, Hits("oldword"));
        Assert.Equal(1, Hits("newword"));
    }

    [Fact]
    public async Task Deleted_file_is_removed_from_search()
    {
        using var s = Make();
        var p = Write("a.txt", "vanishing words");
        await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(1, Hits("vanishing"));
        File.Delete(p);
        var r = await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(1, r.Removed);
        Assert.Equal(0, Hits("vanishing"));
    }

    [Fact]
    public async Task Copy_of_a_file_is_deduped()
    {
        using var s = Make();
        Write("a.txt", "same bytes in both");
        Write("copy of a.txt", "same bytes in both");
        File.Move(Path.Combine(Root, "copy of a.txt"), Path.Combine(Root, "b.txt"));
        var r = await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(1, r.Deduped);
        Assert.Equal(1, r.Extracted);
        Assert.Equal(1, s.Text.Calls);
        Assert.Equal((2L, 0L), s.Store.Counts());
    }

    [Fact]
    public async Task Corrupt_file_is_recorded_failed_and_not_retried_until_changed()
    {
        using var s = Make();
        var bad = Path.Combine(Root, "bad.docx");
        File.WriteAllBytes(bad, new byte[3000].Select((_, i) => (byte)(i * 7)).ToArray());
        Write("good.txt", "fine file");
        var r1 = await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(1, r1.Failed);
        Assert.Equal(1, s.Office.Calls);
        Assert.Equal((1L, 1L), s.Store.Counts());

        await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(1, s.Office.Calls);

        File.WriteAllBytes(bad, new byte[3500].Select((_, i) => (byte)(i * 11)).ToArray());
        var r3 = await s.Runner.RunAsync([R()], null, default);
        Assert.Equal(1, s.Office.Calls - 1);
        Assert.Equal(1, r3.Failed);
    }
}
