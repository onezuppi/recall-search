using Recall.Core.Embed;
using Recall.Core.Index;
using Recall.Core.Ipc;
using Recall.Core.Search;

namespace Recall.Agent;

/// <summary>Maps pipe requests to the search engine, index counters and settings.</summary>
public sealed class AgentRequestHandler(
    SearchEngine search, IIndexRepository store, ChangeQueue queue, SettingsController settings, Func<DateTime?> lastRunUtc,
    EmbedderHost? embedder = null, ProgressTracker? tracker = null)
{
    private const int MaxLimit = 200;

    private readonly ProgressTracker _tracker = tracker ?? new ProgressTracker();

    private static readonly HashSet<string> SettingsOps =
    [
        IpcOps.Settings, IpcOps.AddRoot, IpcOps.RemoveRoot, IpcOps.SetWatched,
        IpcOps.AddExclude, IpcOps.RemoveExclude, IpcOps.AddType, IpcOps.RemoveType,
        IpcOps.SetSimilarity, IpcOps.SetSlowScan, IpcOps.SetDevice, IpcOps.SetEmbedInterval,
    ];

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct) => Task.FromResult(request.Op switch
    {
        IpcOps.Ping => new IpcResponse(true),
        IpcOps.Status => Status(),
        IpcOps.Search => Search(request),
        IpcOps.Warm => Warm(),
        _ when SettingsOps.Contains(request.Op) => WithProgress(settings.Handle(request)),
        _ => new IpcResponse(false, "unknown operation"),
    });

    private IpcResponse Status()
    {
        var (indexed, failed) = store.Counts();
        return new IpcResponse(true, Status: new IpcStatus(indexed, queue.Pending, failed, lastRunUtc()));
    }

    /// <summary>Takes the first reading, so the speed is measured from the start of the agent. Failures are not worth stopping the agent for.</summary>
    public void StartMeasuring()
    {
        try { Progress(); }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException) { }
    }

    private IpcResponse WithProgress(IpcResponse r)
    {
        if (r.Settings == null) return r;
        try { return r with { Settings = r.Settings with { Progress = Progress() } }; }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException) { return r; }
    }

    private IpcProgress Progress()
    {
        var (indexed, _) = store.Counts();
        var backlog = embedder != null && store is IEmbeddingRepository repo
            ? repo.Backlog(embedder.Space, embedder.ImageSpace)
            : new EmbeddingBacklog(0, 0, 0, 0);
        var speed = embedder == null ? 0 : _tracker.Observe(backlog.TextPending);
        return new IpcProgress(indexed, queue.Pending, backlog.TextTotal, backlog.TextPending, backlog.ImagesTotal, backlog.ImagesPending, speed, embedder?.Space ?? "");
    }

    /// <summary>Loads the model in the background and lets the host unload it after its idle time, as after a query.</summary>
    private IpcResponse Warm()
    {
        if (embedder is { IsLoaded: false })
            _ = Task.Run(() =>
            {
                try { embedder.Acquire().Dispose(); }
                catch (Exception) { /* the model is missing or broken: search falls back to text and says nothing */ }
            });
        return new IpcResponse(true);
    }

    private IpcResponse Search(IpcRequest request)
    {
        var limit = Math.Clamp(request.Limit, 1, MaxLimit);
        var hits = search.Search(request.Query ?? "", limit)
            .Select(h => new IpcHit(h.Path, h.Group, h.Locator, h.Snippet, h.Modified, h.Size, h.Context))
            .ToArray();
        return new IpcResponse(true, Hits: hits);
    }
}
