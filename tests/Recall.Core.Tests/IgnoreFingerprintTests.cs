using Recall.Core.Config;
using Recall.Core.Ignore;

namespace Recall.Core.Tests;

public class IgnoreFingerprintTests
{
    [Fact]
    public void Fingerprint_follows_exclude_patterns_changed_at_runtime()
    {
        var policy = new IgnorePolicy(RecallConfig.CreateDefault());
        var before = policy.Fingerprint;
        policy.SetGlobalPatterns(["secret-folder/"]);
        Assert.NotEqual(before, policy.Fingerprint);
        policy.SetGlobalPatterns([]);
        Assert.Equal(before, policy.Fingerprint);
    }
}
