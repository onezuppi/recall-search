using Recall.Agent;

namespace Recall.Core.Tests;

public class RescanQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-watch-" + Guid.NewGuid().ToString("N"));

    public RescanQueueTests() => Directory.CreateDirectory(Path.Combine(_dir, "sub"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void A_second_request_for_a_waiting_root_is_dropped_until_its_pass_starts()
    {
        var queue = new RescanQueue();
        Assert.True(queue.TryQueue(@"C:\Root"));
        Assert.False(queue.TryQueue(@"c:\root")); // the pass has not started: it will see everything this request would
        Assert.True(queue.TryQueue(@"D:\Other"));

        queue.Started(@"C:\Root");
        Assert.True(queue.TryQueue(@"C:\Root")); // events after the pass began may be missed by it: one more is wanted
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(100);
        Assert.True(condition(), what);
    }

    [Fact]
    public async Task Writing_a_file_does_not_rescan_but_a_new_folder_does()
    {
        var paths = new System.Collections.Concurrent.ConcurrentBag<string>();
        var rescans = 0;
        using var watcher = new RootWatcher(_dir, paths.Add, () => Interlocked.Increment(ref rescans), new NullLog());
        watcher.Start();

        // Windows also reports a change of the parent folder for every file written in it. That is no reason to walk the root again.
        var file = Path.Combine(_dir, "sub", "note.txt");
        File.WriteAllText(file, "hello");
        await WaitFor(() => paths.Contains(file), "the file event");
        await Task.Delay(500);
        Assert.Equal(0, Volatile.Read(ref rescans));

        // A new folder may already hold files that no event will describe: that one does need a pass.
        Directory.CreateDirectory(Path.Combine(_dir, "fresh"));
        await WaitFor(() => Volatile.Read(ref rescans) > 0, "a rescan for the new folder");
    }
}
