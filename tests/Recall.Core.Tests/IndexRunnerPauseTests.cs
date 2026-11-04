using Recall.Core.Activity;
using Recall.Core.Index;
using Recall.Core.Tests.Support;

namespace Recall.Core.Tests;

public class IndexRunnerPauseTests : RunnerTestBase
{
    private sealed class ManualGate : IPauseGate
    {
        private TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Waits;
        public bool IsPaused => !_open.Task.IsCompleted;
        public ManualGate(bool paused) { if (!paused) _open.SetResult(); }
        public void Pause() { if (!IsPaused) _open = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public void Resume() => _open.TrySetResult();
        public Task WaitWhilePausedAsync(CancellationToken ct)
        {
            if (!IsPaused) return Task.CompletedTask;
            Interlocked.Increment(ref Waits);
            return _open.Task.WaitAsync(ct);
        }
    }

    private IndexRunner Runner(Setup s, ManualGate gate, int? workers = null) =>
        new(s.Store, new Recall.Core.Ignore.IgnorePolicy(s.Config, _ => false), [s.Text], new FakeOcr("x"), workers, gate);

    [Fact]
    public async Task Paused_run_takes_no_files_until_resumed()
    {
        using var s = Make();
        Write("a.txt", "alpha document");
        Write("b.txt", "bravo document");
        var gate = new ManualGate(paused: true);
        var run = Runner(s, gate).RunAsync([R()], null, default);

        await Task.Delay(300);
        Assert.False(run.IsCompleted);
        Assert.Equal(0, s.Text.Calls);
        Assert.Equal((0L, 0L), s.Store.Counts());

        gate.Resume();
        var report = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, report.Extracted);
    }

    [Fact]
    public async Task Pause_in_the_middle_leaves_remaining_files_for_later()
    {
        using var s = Make();
        for (var i = 0; i < 6; i++) Write($"f{i}.txt", $"unique content number {i}");
        var gate = new ManualGate(paused: false);
        var seen = 0;
        var progress = new SyncProgress<RunProgress>(_ => { if (Interlocked.Increment(ref seen) == 2) gate.Pause(); });
        var run = Runner(s, gate, workers: 1).RunAsync([R()], progress, default);

        // Wait for the second file instead of a fixed time: a cold CI machine needs more than half a second for it.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Volatile.Read(ref seen) < 2 && DateTime.UtcNow < deadline) await Task.Delay(20);
        await Task.Delay(300);   // time for a third file to start, if the pause did not hold
        Assert.False(run.IsCompleted);
        Assert.Equal(2, s.Store.Counts().Indexed); // the file in flight finishes, nothing new starts

        gate.Resume();
        var report = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(6, s.Store.Counts().Indexed);
        Assert.Equal(6, report.Added);
    }
}
