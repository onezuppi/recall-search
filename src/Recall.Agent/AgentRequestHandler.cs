using Recall.Core.Index;
using Recall.Core.Ipc;
using Recall.Core.Search;

namespace Recall.Agent;

/// <summary>Maps pipe requests to the search engine, index counters and settings.</summary>
public sealed class AgentRequestHandler(SearchEngine search, IIndexRepository store, ChangeQueue queue, SettingsController settings, Func<DateTime?> lastRunUtc)
{
    private const int MaxLimit = 200;

    private static readonly HashSet<string> SettingsOps =
    [
        IpcOps.Settings, IpcOps.AddRoot, IpcOps.RemoveRoot, IpcOps.SetWatched,
        IpcOps.AddExclude, IpcOps.RemoveExclude, IpcOps.AddType, IpcOps.RemoveType,
    ];

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct) => Task.FromResult(request.Op switch
    {
        IpcOps.Ping => new IpcResponse(true),
        IpcOps.Status => Status(),
        IpcOps.Search => Search(request),
        _ when SettingsOps.Contains(request.Op) => settings.Handle(request),
        _ => new IpcResponse(false, "unknown operation"),
    });

    private IpcResponse Status()
    {
        var (indexed, failed) = store.Counts();
        return new IpcResponse(true, Status: new IpcStatus(indexed, queue.Pending, failed, lastRunUtc()));
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
