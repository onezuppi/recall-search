using Recall.Core.Config;
using Recall.Core.Ignore;
using Recall.Core.Scan;

namespace Recall.Core.Tests;

public class IncrementalScanTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "rs-incr-" + Guid.NewGuid().ToString("N"));

    public IncrementalScanTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private static IgnorePolicy Policy() =>
        new(new RecallConfig([], new HashSet<string>(["text"], StringComparer.OrdinalIgnoreCase), []), _ => false);

    private string P(string rel) => Path.Combine(_tmp, rel);

    private string Touch(string rel, string content = "x")
    {
        var p = P(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    private (Scanner Scanner, List<FileRecord> Files) Scan(DirectoryMemory? memory = null)
    {
        var s = new Scanner(Policy());
        var files = s.Scan(new RootFolder(_tmp, false), CancellationToken.None, memory).ToList();
        return (s, files);
    }

    private DirectoryMemory Remember()
    {
        var full = Scan().Scanner;
        return new DirectoryMemory(full.ListedDirectories);
    }

    private static string[] Names(IEnumerable<FileRecord> files) => files.Select(f => Path.GetFileName(f.Path)).Order().ToArray();

    [Fact]
    public void Full_scan_lists_every_directory_and_reuses_none()
    {
        Touch(@"a\b\1.md");
        var (s, _) = Scan();
        Assert.Equal(3, s.ListedDirectories.Count);
        Assert.Empty(s.ReusedDirectories);
    }

    [Fact]
    public void Unchanged_tree_lists_no_directory_and_returns_no_files()
    {
        Touch(@"a\b\1.md");
        Touch(@"a\2.md");
        var memory = Remember();
        var (s, files) = Scan(memory);
        Assert.Empty(files);
        Assert.Empty(s.ListedDirectories);
        Assert.Equal(3, s.ReusedDirectories.Count);
        Assert.Contains(P(@"a\b"), s.ReusedDirectories);
    }

    [Fact]
    public void New_file_relists_only_its_own_directory()
    {
        Touch(@"a\b\1.md");
        Touch(@"top.md");
        var memory = Remember();
        Touch(@"a\b\new.md");
        var (s, files) = Scan(memory);
        Assert.Equal(["1.md", "new.md"], Names(files));
        Assert.Equal([P(@"a\b")], s.ListedDirectories.Keys);
        Assert.Contains(P("a"), s.ReusedDirectories);
    }

    [Fact]
    public void New_subdirectory_is_discovered_through_its_changed_parent()
    {
        Touch(@"a\1.md");
        var memory = Remember();
        Touch(@"a\c\deep\n.md");
        var (s, files) = Scan(memory);
        Assert.Equal(["1.md", "n.md"], Names(files));
        Assert.Contains(P(@"a\c\deep"), s.ListedDirectories.Keys);
    }

    [Fact]
    public void Deleted_directory_is_not_visited()
    {
        Touch(@"a\b\1.md");
        Touch(@"a\2.md");
        var memory = Remember();
        Directory.Delete(P(@"a\b"), true);
        var (s, _) = Scan(memory);
        Assert.DoesNotContain(P(@"a\b"), s.ListedDirectories.Keys);
        Assert.DoesNotContain(P(@"a\b"), s.ReusedDirectories);
    }

    [Fact]
    public void In_place_overwrite_is_invisible_to_a_fast_scan_but_seen_by_a_full_one()
    {
        var p = Touch(@"a\1.md", "short");
        var memory = Remember();
        File.WriteAllText(p, "much longer content");
        Assert.Empty(Scan(memory).Files);
        Assert.Single(Scan().Files);
    }

    [Fact]
    public void Diff_does_not_remove_files_of_unchanged_directories()
    {
        var snapshot = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase)
        {
            [P(@"a\keep.md")] = (1, 1), [P(@"b\gone.md")] = (1, 1), [P(@"c\gone.md")] = (1, 1),
        };
        var reused = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { P("a") };
        var changes = ChangeSet.Diff([], snapshot, reused);
        Assert.Equal([P(@"b\gone.md"), P(@"c\gone.md")], changes.Removed.Order());
    }

    [Fact]
    public void Diff_keeps_the_files_of_a_directory_that_failed_to_list_and_of_everything_below_it()
    {
        // A share that dropped mid-scan: its directory could not be listed, so its files were not seen. That is not a deletion.
        var snapshot = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase)
        {
            [P(@"share\a.md")] = (1, 1), [P(@"share\deep\b.md")] = (1, 1), [P(@"sharepoint\c.md")] = (1, 1), [P(@"other\gone.md")] = (1, 1),
        };
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { P("share") };
        var changes = ChangeSet.Diff([], snapshot, null, failed);
        // "sharepoint" only shares a name prefix with "share": it is a different directory and its file is really gone.
        Assert.Equal([P(@"other\gone.md"), P(@"sharepoint\c.md")], changes.Removed.Order());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void Concurrent_walk_returns_the_same_files_and_statistics(int threads)
    {
        for (var d = 0; d < 20; d++)
            for (var f = 0; f < 5; f++) Touch($"d{d}/sub{d % 3}/f{f}.md");
        var s = new Scanner(Policy(), threads);
        var files = s.Scan(new RootFolder(_tmp, false), CancellationToken.None).ToList();
        Assert.Equal(100, files.Count);
        Assert.Equal(100, s.Stats.FilesListed);
        Assert.Equal(1 + 20 + 20, s.Stats.DirectoriesEntered);
        Assert.Equal(41, s.ListedDirectories.Count);
    }

    [Fact]
    public void Concurrent_walk_stops_when_cancelled()
    {
        for (var d = 0; d < 20; d++) Touch($"d{d}/f.md");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var s = new Scanner(Policy(), 4);
        Assert.Throws<OperationCanceledException>(() => s.Scan(new RootFolder(_tmp, false), cts.Token).ToList());
    }
}
