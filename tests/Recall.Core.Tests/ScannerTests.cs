using System.Diagnostics;
using Recall.Core.Config;
using Recall.Core.Ignore;
using Recall.Core.Scan;

namespace Recall.Core.Tests;

public class ScannerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "rs-scan-" + Guid.NewGuid().ToString("N"));

    public ScannerTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private static RecallConfig Cfg() =>
        new([], new HashSet<string>(["images", "pdf", "office", "text"], StringComparer.OrdinalIgnoreCase), []);

    private sealed class CountingPolicy() : IgnorePolicy(Cfg(), _ => false)
    {
        public List<string> Asked { get; } = [];
        public override DirectoryDecision EnterDirectory(string fullPath, FileAttributes attrs, IReadOnlyList<GitIgnoreMatcher> scope)
        {
            Asked.Add(Path.GetFileName(fullPath));
            return base.EnterDirectory(fullPath, attrs, scope);
        }
    }

    private string Touch(string rel, string content = "x")
    {
        var p = Path.Combine(_tmp, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    private static List<FileRecord> Run(Scanner s, string root) =>
        s.Scan(new RootFolder(root, false), CancellationToken.None).ToList();

    [Fact]
    public void Pruned_folder_children_are_never_enumerated()
    {
        Touch(@"proj\node_modules\pkg\a.md");
        Touch(@"proj\notes.md");
        var policy = new CountingPolicy();
        var files = Run(new Scanner(policy), _tmp);
        Assert.Equal(["notes.md"], files.Select(f => Path.GetFileName(f.Path)));
        Assert.Single(policy.Asked, "node_modules");
        Assert.DoesNotContain("pkg", policy.Asked);
    }

    [Fact]
    public void Junction_is_not_followed()
    {
        Touch(@"realdir\a.md");
        var link = Path.Combine(_tmp, "link");
        var psi = new ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{Path.Combine(_tmp, "realdir")}\"")
        { CreateNoWindow = true, RedirectStandardOutput = true };
        Process.Start(psi)!.WaitForExit();
        Assert.True(Directory.Exists(link));
        var s = new Scanner(new IgnorePolicy(Cfg(), _ => false));
        var files = Run(s, _tmp);
        Assert.Single(files);
        Assert.Equal(1, s.Stats.DirectoriesSkipped[DirectoryDecision.SkipReparse]);
    }

    [Fact]
    public void Offline_file_is_not_listed_and_not_opened()
    {
        var p = Touch("a.txt");
        File.SetAttributes(p, FileAttributes.Offline);
        var s = new Scanner(new IgnorePolicy(Cfg(), _ => false));
        Assert.Empty(Run(s, _tmp));
        Assert.Equal(1, s.Stats.FilesSkipped[FileDecision.SkipCloudOnly]);
    }

    [Fact]
    public void Lists_allowed_files_with_metadata()
    {
        var p = Touch("a.md", "hello");
        var info = new FileInfo(p);
        var rec = Assert.Single(Run(new Scanner(new IgnorePolicy(Cfg(), _ => false)), _tmp));
        Assert.Equal(info.Length, rec.Size);
        Assert.Equal(info.LastWriteTimeUtc, rec.LastWriteUtc);
        Assert.Equal("text", rec.Group);
    }

    [Fact]
    public void Heavy_directories_are_ranked_by_files_beneath()
    {
        for (var i = 0; i < 5; i++) Touch($@"big\sub\f{i}.md");
        Touch(@"small\x.md");
        var s = new Scanner(new IgnorePolicy(Cfg(), _ => false));
        Run(s, _tmp);
        var top = s.Stats.HeavyDirectories;
        Assert.Equal(Path.Combine(_tmp, "big"), top.First(d => d.Path.EndsWith("big")).Path);
        Assert.Equal(5, top.First(d => d.Path.EndsWith("big")).Files);
        Assert.True(top.Count <= 50);
    }

    [Fact]
    public void Gitignore_in_a_folder_prunes_its_subtree()
    {
        Touch(@"p\.gitignore", "gen/\n");
        Touch(@"p\gen\a.md");
        Touch(@"p\keep.md");
        var files = Run(new Scanner(new IgnorePolicy(Cfg(), _ => false)), _tmp);
        Assert.Equal(["keep.md"], files.Select(f => Path.GetFileName(f.Path)));
    }
}
