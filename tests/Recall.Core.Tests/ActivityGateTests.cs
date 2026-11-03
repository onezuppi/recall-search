using Recall.Core.Activity;
using Recall.Core.Tests.Support;

namespace Recall.Core.Tests;

public class ActivityGateTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(200);

    private static async Task<bool> WaitFor(Func<bool> condition, int ms = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (condition()) return true; await Task.Delay(10); }
        return condition();
    }

    [Fact]
    public async Task Busy_monitor_pauses_the_gate_and_idle_resumes_it_after_the_delay()
    {
        var monitor = new FakeActivityMonitor();
        using var gate = new ActivityGate(monitor, Poll, Delay);
        Assert.False(gate.IsPaused);

        monitor.SetBusy();
        Assert.True(await WaitFor(() => gate.IsPaused));

        var cleared = DateTime.UtcNow;
        monitor.SetIdle();
        Assert.True(await WaitFor(() => !gate.IsPaused));
        Assert.True(DateTime.UtcNow - cleared >= Delay - TimeSpan.FromMilliseconds(40));
    }

    [Fact]
    public async Task Short_idle_flicker_does_not_resume()
    {
        var monitor = new FakeActivityMonitor("game");
        using var gate = new ActivityGate(monitor, Poll, TimeSpan.FromMilliseconds(300));
        Assert.True(await WaitFor(() => gate.IsPaused));

        monitor.SetIdle();
        await Task.Delay(100);
        monitor.SetBusy();
        await Task.Delay(400);
        Assert.True(gate.IsPaused);
    }

    [Fact]
    public async Task Waiter_blocks_while_paused_and_is_released_on_resume()
    {
        var monitor = new FakeActivityMonitor("game");
        using var gate = new ActivityGate(monitor, Poll, Delay);
        Assert.True(await WaitFor(() => gate.IsPaused));

        var wait = gate.WaitWhilePausedAsync(default);
        await Task.Delay(100);
        Assert.False(wait.IsCompleted);

        monitor.SetIdle();
        await wait.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Waiter_honors_cancellation()
    {
        using var gate = new ActivityGate(new FakeActivityMonitor("game"), Poll, Delay);
        Assert.True(await WaitFor(() => gate.IsPaused));
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.WaitWhilePausedAsync(cts.Token));
    }

    [Fact]
    public async Task Changed_reports_each_transition_once_with_the_reason()
    {
        var monitor = new FakeActivityMonitor();
        var events = new List<(bool, string?)>();
        using var gate = new ActivityGate(monitor, Poll, Delay);
        gate.Changed += (p, r) => { lock (events) events.Add((p, r)); };

        monitor.SetBusy("full screen game");
        Assert.True(await WaitFor(() => { lock (events) return events.Count == 1; }));
        monitor.SetIdle();
        Assert.True(await WaitFor(() => { lock (events) return events.Count == 2; }));
        await Task.Delay(100);
        lock (events) Assert.Equal([(true, "full screen game"), (false, (string?)null)], events);
    }
}
