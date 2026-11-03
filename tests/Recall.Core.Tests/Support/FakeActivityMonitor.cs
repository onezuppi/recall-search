using Recall.Core.Activity;

namespace Recall.Core.Tests.Support;

public sealed class FakeActivityMonitor : IActivityMonitor
{
    private volatile string? _reason;

    public FakeActivityMonitor(string? reason = null) => _reason = reason;

    public void SetBusy(string reason = "test game") => _reason = reason;
    public void SetIdle() => _reason = null;
    public string? BusyReason() => _reason;
}
