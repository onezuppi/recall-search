using Recall.Core.Config;
using Recall.Core.Ignore;

namespace Recall.Core.Tests;

public class IgnoreTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "rs-ign-" + Guid.NewGuid().ToString("N"));
    private static readonly IReadOnlyList<GitIgnoreMatcher> NoScope = [];

    public IgnoreTests() => Directory.CreateDirectory(_tmp);
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private static RecallConfig Cfg(string[]? global = null, string[]? groups = null) =>
        new([], new HashSet<string>(groups ?? ["images", "pdf", "office", "text"], StringComparer.OrdinalIgnoreCase),
            global ?? []);

    // The temp folder sits under %LOCALAPPDATA%, a system path, so tests turn that rule off
    // except where they test it.
    private IgnorePolicy Policy(RecallConfig? c = null) => new(c ?? Cfg(), _ => false);

    [Theory]
    [InlineData("node_modules")] [InlineData(".git")] [InlineData(".pnpm")] [InlineData(".angular")]
    [InlineData(".godot")] [InlineData("__pycache__")] [InlineData("site-packages")]
    [InlineData("bin")] [InlineData("obj")] [InlineData("Pods")]
    [InlineData(".claude")] [InlineData(".codex")] [InlineData(".cursor")] [InlineData(".superpowers")]
    public void Built_in_folders_are_not_entered(string name)
    {
        var d = Path.Combine(_tmp, name);
        Assert.Equal(DirectoryDecision.SkipBuiltIn, Policy().EnterDirectory(d, FileAttributes.Directory, NoScope));
    }

    [Fact]
    public void Ordinary_folder_is_entered() =>
        Assert.Equal(DirectoryDecision.Enter,
            Policy().EnterDirectory(Path.Combine(_tmp, "notes"), FileAttributes.Directory, NoScope));

    [Fact]
    public void System_paths_are_not_entered_even_inside_a_root()
    {
        foreach (var p in new[]
        {
            Environment.GetEnvironmentVariable("SystemRoot")!,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath().TrimEnd('\\'),
            Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32"),
        })
            Assert.Equal(DirectoryDecision.SkipSystem, new IgnorePolicy(Cfg()).EnterDirectory(p, FileAttributes.Directory, NoScope));
    }

    [Fact]
    public void Reparse_point_directory_is_not_entered() =>
        Assert.Equal(DirectoryDecision.SkipReparse,
            Policy().EnterDirectory(Path.Combine(_tmp, "link"), FileAttributes.Directory | FileAttributes.ReparsePoint, NoScope));

    [Theory]
    [InlineData("a.pem")] [InlineData("x.mafile")] [InlineData(".env")] [InlineData(".env.local")]
    [InlineData("id_rsa")] [InlineData("id_rsa.pub")] [InlineData("k.ppk")] [InlineData("a.kdbx")]
    public void Secrets_are_skipped(string name) =>
        Assert.Equal(FileDecision.SkipSecret, Policy().IndexFile(Path.Combine(_tmp, name), 10, FileAttributes.Normal, NoScope));

    [Fact]
    public void Environment_md_is_not_a_secret() =>
        Assert.Equal(FileDecision.Index, Policy().IndexFile(Path.Combine(_tmp, "environment.md"), 10, FileAttributes.Normal, NoScope));

    [Fact]
    public void Cloud_only_file_is_skipped()
    {
        Assert.Equal(FileDecision.SkipCloudOnly, Policy().IndexFile(Path.Combine(_tmp, "a.txt"), 10, (FileAttributes)0x400000, NoScope));
        Assert.Equal(FileDecision.SkipCloudOnly, Policy().IndexFile(Path.Combine(_tmp, "a.txt"), 10, FileAttributes.Offline, NoScope));
        Assert.Equal(FileDecision.SkipCloudOnly, Policy().IndexFile(Path.Combine(_tmp, "a.txt"), 10, (FileAttributes)0x40000, NoScope));
    }

    [Fact]
    public void Type_allow_list()
    {
        Assert.Equal(FileDecision.Index, Policy().IndexFile(Path.Combine(_tmp, "a.JPEG"), 10, FileAttributes.Normal, NoScope));
        foreach (var n in new[] { "a.js", "a.json", "a.map", "a.dll" })
            Assert.Equal(FileDecision.SkipType, Policy().IndexFile(Path.Combine(_tmp, n), 10, FileAttributes.Normal, NoScope));
        var noImages = Policy(Cfg(groups: ["pdf", "office", "text"]));
        Assert.Equal(FileDecision.SkipType, noImages.IndexFile(Path.Combine(_tmp, "a.png"), 10, FileAttributes.Normal, NoScope));
    }

    [Fact]
    public void Size_limits()
    {
        Assert.Equal(FileDecision.SkipSize, Policy().IndexFile(Path.Combine(_tmp, "a.txt"), 2 * 1024 * 1024 + 1, FileAttributes.Normal, NoScope));
        Assert.Equal(FileDecision.SkipSize, Policy().IndexFile(Path.Combine(_tmp, "a.png"), 50L * 1024 * 1024 + 1, FileAttributes.Normal, NoScope));
        Assert.Equal(FileDecision.Index, Policy().IndexFile(Path.Combine(_tmp, "a.png"), 50L * 1024 * 1024, FileAttributes.Normal, NoScope));
    }

    private IReadOnlyList<GitIgnoreMatcher> Scope(string dir, params (string file, string[] lines)[] files)
    {
        Directory.CreateDirectory(dir);
        foreach (var (f, l) in files) File.WriteAllLines(Path.Combine(dir, f), l);
        return Policy().ScopeFor(dir, NoScope);
    }

    [Fact]
    public void A_malformed_glob_is_skipped_and_the_other_rules_still_apply()
    {
        // "[z-a]" is a reversed range: it turns into an invalid regex. One bad line in someone's .gitignore must not abort a scan.
        var matcher = GitIgnoreMatcher.Parse(@"C:\repo", ["[z-a]", "*.log", "[!]"]);
        Assert.Equal(MatchResult.Ignored, matcher.Match(@"C:\repo\build.log", isDirectory: false));
        Assert.Equal(MatchResult.None, matcher.Match(@"C:\repo\readme.md", isDirectory: false));
    }

    [Fact]
    public void Gitignore_star_pattern_ignores_nested_files()
    {
        var scope = Scope(_tmp, (".gitignore", ["*.txt"]));
        Assert.Equal(FileDecision.SkipIgnoreFile,
            Policy().IndexFile(Path.Combine(_tmp, "a", "b", "x.txt"), 1, FileAttributes.Normal, scope));
    }

    [Fact]
    public void Trailing_slash_ignores_directory_only()
    {
        var scope = Scope(_tmp, (".gitignore", ["out2/"]));
        Assert.Equal(DirectoryDecision.SkipIgnoreFile,
            Policy().EnterDirectory(Path.Combine(_tmp, "out2"), FileAttributes.Directory, scope));
        // a FILE named out2.txt is not matched by "out2/"
        Assert.Equal(FileDecision.Index, Policy().IndexFile(Path.Combine(_tmp, "out2.txt"), 1, FileAttributes.Normal, scope));
    }

    [Fact]
    public void Negation_reincludes()
    {
        var scope = Scope(_tmp, (".gitignore", ["*.txt", "!keep.txt"]));
        Assert.Equal(FileDecision.Index, Policy().IndexFile(Path.Combine(_tmp, "keep.txt"), 1, FileAttributes.Normal, scope));
        Assert.Equal(FileDecision.SkipIgnoreFile, Policy().IndexFile(Path.Combine(_tmp, "other.txt"), 1, FileAttributes.Normal, scope));
    }

    [Fact]
    public void Leading_slash_anchors_to_base()
    {
        var scope = Scope(_tmp, (".gitignore", ["/root-only.txt"]));
        Assert.Equal(FileDecision.SkipIgnoreFile, Policy().IndexFile(Path.Combine(_tmp, "root-only.txt"), 1, FileAttributes.Normal, scope));
        Assert.Equal(FileDecision.Index, Policy().IndexFile(Path.Combine(_tmp, "sub", "root-only.txt"), 1, FileAttributes.Normal, scope));
    }

    [Fact]
    public void Nested_gitignore_adds_rules_for_its_subtree_only()
    {
        var root = Scope(_tmp);
        var sub = Path.Combine(_tmp, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllLines(Path.Combine(sub, ".gitignore"), ["*.md"]);
        var chained = Policy().ScopeFor(sub, root);
        Assert.Equal(FileDecision.SkipIgnoreFile, Policy().IndexFile(Path.Combine(sub, "n.md"), 1, FileAttributes.Normal, chained));
        Assert.Equal(FileDecision.Index, Policy().IndexFile(Path.Combine(_tmp, "n.md"), 1, FileAttributes.Normal, root));
    }

    [Fact]
    public void Recallignore_behaves_like_gitignore()
    {
        var scope = Scope(_tmp, (".recallignore", ["private/"]));
        Assert.Equal(DirectoryDecision.SkipIgnoreFile,
            Policy().EnterDirectory(Path.Combine(_tmp, "private"), FileAttributes.Directory, scope));
    }

    [Fact]
    public void Global_patterns_apply_everywhere()
    {
        var p = Policy(Cfg(global: ["drafts/"]));
        Assert.Equal(DirectoryDecision.SkipIgnoreFile,
            p.EnterDirectory(Path.Combine(_tmp, "x", "drafts"), FileAttributes.Directory, NoScope));
    }

    [Fact]
    public void Global_pattern_with_a_full_path_excludes_that_folder_only()
    {
        var p = Policy(Cfg(global: ["C:/projects/self/hgame/", "service-autotest-2.0*/"]));
        Assert.Equal(DirectoryDecision.SkipIgnoreFile,
            p.EnterDirectory(@"C:\projects\self\hgame", FileAttributes.Directory, NoScope));
        Assert.Equal(FileDecision.SkipIgnoreFile,
            p.IndexFile(@"C:\projects\self\hgame\docs.md", 1, FileAttributes.Normal, NoScope));
        Assert.Equal(DirectoryDecision.Enter,
            p.EnterDirectory(@"C:\projects\self\other", FileAttributes.Directory, NoScope));
        Assert.Equal(DirectoryDecision.Enter,
            p.EnterDirectory(@"C:\projects\hgame", FileAttributes.Directory, NoScope));
        Assert.Equal(DirectoryDecision.SkipIgnoreFile,
            p.EnterDirectory(@"C:\projects\work\service-autotest-2.0.wt", FileAttributes.Directory, NoScope));
    }
}
