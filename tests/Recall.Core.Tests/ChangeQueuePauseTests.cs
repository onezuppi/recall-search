using Recall.Agent;

namespace Recall.Core.Tests;

public class ChangeQueuePauseTests
{
    [Fact]
    public async Task Changes_stay_queued_while_paused_and_flush_after_resume()
    {
        var paused = true;
        var flushed = new List<string>();
        using var queue = new ChangeQueue(TimeSpan.FromMilliseconds(30), (_, paths) =>
        {
            lock (flushed) flushed.AddRange(paths);
            return Task.CompletedTask;
        }, new NullLog(), () => paused);

        queue.Add("root", "a.txt");
        queue.Add("root", "b.txt");
        await Task.Delay(300);
        lock (flushed) Assert.Empty(flushed);
        Assert.Equal(2, queue.Pending);

        paused = false;
        queue.Resume();
        var end = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < end) { lock (flushed) if (flushed.Count == 2) break; await Task.Delay(20); }
        lock (flushed) Assert.Equal(2, flushed.Count);
        Assert.Equal(0, queue.Pending);
    }
}
