using Recall.Core.Ipc;

namespace Recall.Core.Tests;

public class ListDiffTests
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    [Fact]
    public void Lines_are_trimmed_and_blank_lines_dropped_for_any_newline_style()
    {
        var lines = ListDiff.ParseLines("  a \r\n\r\nb\n   \nc", s => s, Ci);
        Assert.Equal(["a", "b", "c"], lines);
    }

    [Fact]
    public void Repeated_lines_count_once_under_the_comparer()
    {
        Assert.Equal([@"C:\Work"], ListDiff.ParseLines("C:\\Work\nc:\\work", s => s, Ci));
    }

    [Fact]
    public void Normalizer_runs_before_the_repeat_check_and_can_drop_a_line()
    {
        var lines = ListDiff.ParseLines("\"D:\\x\\\"\nD:\\x\n\"\"", s => Path.TrimEndingDirectorySeparator(s.Trim('"')), Ci);
        Assert.Equal([@"D:\x"], lines);
    }

    [Fact]
    public void Compute_reports_added_and_removed_in_their_own_order()
    {
        var (added, removed) = ListDiff.Compute(["a", "b", "c"], ["d", "B", "e"], Ci);
        Assert.Equal(["d", "e"], added);
        Assert.Equal(["a", "c"], removed);
    }

    [Fact]
    public void Unchanged_text_changes_nothing()
    {
        var (added, removed) = ListDiff.Compute(["a", "b"], ["b", "a"], Ci);
        Assert.Empty(added);
        Assert.Empty(removed);
    }
}
