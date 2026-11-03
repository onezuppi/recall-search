using Recall.Core.Index;
using Recall.Core.Ipc;
using Recall.Core.Search;

namespace Recall.Agent;

/// <summary>Maps pipe requests to the search engine, index counters and settings.</summary>
public sealed class AgentRequestHandler(string dbPath, IndexStore store, ChangeQueue queue, SettingsController settings, Func<DateTime?> lastRunUtc)
{
    private const int MaxLimit = 200;
    private readonly SearchEngine _search = new(dbPath);

    public Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct) => Task.FromResult(request.Op switch
    {
        IpcOps.Ping => new IpcResponse(true),
        IpcOps.Status => Status(),
        IpcOps.Search => Search(request),
        IpcOps.Settings or IpcOps.AddRoot or IpcOps.RemoveRoot or IpcOps.AddExclude or IpcOps.RemoveExclude => settings.Handle(request),
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
        var hits = _search.Search(request.Query ?? "", limit)
            .Select(h => new IpcHit(h.Path, h.Group, h.Locator, h.Snippet, h.Modified, h.Size, h.Context))
            .ToArray();
        return new IpcResponse(true, Hits: hits);
    }
}
