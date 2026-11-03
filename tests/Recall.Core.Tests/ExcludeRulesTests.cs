using Recall.Core.Config;
using Recall.Core.Ignore;

namespace Recall.Core.Tests;

public class ExcludeRulesTests
{
    private static IgnorePolicy PolicyWith(params string[] patterns) =>
        new(new RecallConfig([], new HashSet<string>(["text"]), patterns), _ => false);

    [Fact]
    public void Regex_pattern_excludes_files_below_a_matching_folder()
    {
        var policy = PolicyWith("re:/backup(s)?/");
        Assert.Equal(FileDecision.SkipIgnoreFile, policy.IndexFile(@"C:\data\Backups\a.txt", 10, FileAttributes.Normal, []));
        Assert.Equal(FileDecision.Index, policy.IndexFile(@"C:\data\keep\a.txt", 10, FileAttributes.Normal, []));
    }

    [Fact]
    public void Regex_pattern_prunes_the_matching_directory_itself()
    {
        var policy = PolicyWith("re:/backup(s)?/");
        Assert.Equal(DirectoryDecision.SkipIgnoreFile, policy.EnterDirectory(@"C:\data\backups", FileAttributes.Directory, []));
        Assert.Equal(DirectoryDecision.Enter, policy.EnterDirectory(@"C:\data\other", FileAttributes.Directory, []));
    }

    [Fact]
    public void Regex_is_case_insensitive_and_sees_forward_slashes()
    {
        var policy = PolicyWith(@"re:^c:/data/.*\.tmp\.txt$");
        Assert.Equal(FileDecision.SkipIgnoreFile, policy.IndexFile(@"C:\Data\X\note.TMP.txt", 10, FileAttributes.Normal, []));
    }

    [Fact]
    public void Glob_and_regex_patterns_work_together()
    {
        var policy = PolicyWith("*.skip.txt", @"re:~\d+\.txt$");
        Assert.Equal(FileDecision.SkipIgnoreFile, policy.IndexFile(@"C:\d\a.skip.txt", 10, FileAttributes.Normal, []));
        Assert.Equal(FileDecision.SkipIgnoreFile, policy.IndexFile(@"C:\d\b~12.txt", 10, FileAttributes.Normal, []));
        Assert.Equal(FileDecision.Index, policy.IndexFile(@"C:\d\c.txt", 10, FileAttributes.Normal, []));
    }

    [Fact]
    public void Invalid_regex_in_a_hand_edited_config_is_ignored()
    {
        var policy = PolicyWith("re:(", "*.skip.txt");
        Assert.Equal(FileDecision.Index, policy.IndexFile(@"C:\d\a.txt", 10, FileAttributes.Normal, []));
        Assert.Equal(FileDecision.SkipIgnoreFile, policy.IndexFile(@"C:\d\a.skip.txt", 10, FileAttributes.Normal, []));
    }

    [Theory]
    [InlineData("*.log")]
    [InlineData("node_modules/")]
    [InlineData("re:/backup(s)?/")]
    [InlineData(@"re:\d{4}-\d{2}")]
    public void Valid_patterns_pass_validation(string pattern) => Assert.Null(ExcludeRules.Validate(pattern));

    [Fact]
    public void Empty_regex_is_rejected() => Assert.Contains("empty", ExcludeRules.Validate("re:"));

    [Fact]
    public void Malformed_regex_gets_a_clear_message()
    {
        var error = ExcludeRules.Validate("re:(unclosed");
        Assert.StartsWith("Invalid regular expression", error);
    }

    [Fact]
    public void Regex_features_that_need_backtracking_are_rejected()
    {
        var error = ExcludeRules.Validate("re:(?=a)b");
        Assert.StartsWith("Unsupported regular expression", error);
    }

    [Fact]
    public void Fingerprint_follows_regex_patterns()
    {
        var policy = PolicyWith();
        var before = policy.Fingerprint;
        policy.SetGlobalPatterns(["re:/x/"]);
        Assert.NotEqual(before, policy.Fingerprint);
    }
}
