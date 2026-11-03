using Recall.Core.Ipc;

namespace Recall.CmdPal;

internal enum SearchState { Results, AgentNotRunning, UntrustedServer, Failed }

internal sealed record SearchOutcome(SearchState State, IReadOnlyList<IpcHit> Hits, string? Message = null);

/// <summary>Debounced search against the agent. The page cancels the previous call on every keystroke.</summary>
internal sealed class SearchSession
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    public async Task<SearchOutcome> SearchAsync(string text, CancellationToken ct)
    {
        await Task.Delay(Debounce, ct);
        if (!PipeClient.IsListening()) return new SearchOutcome(SearchState.AgentNotRunning, []);
        try
        {
            var response = await PipeClient.SendAsync(new IpcRequest(IpcOps.Search, text), RequestTimeout, ct);
            return response.Ok
                ? new SearchOutcome(SearchState.Results, response.Hits ?? [])
                : new SearchOutcome(SearchState.Failed, [], response.Error);
        }
        catch (UntrustedServerException e)
        {
            return new SearchOutcome(SearchState.UntrustedServer, [], e.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return new SearchOutcome(SearchState.UntrustedServer, [], "The pipe belongs to another account.");
        }
        catch (TimeoutException)
        {
            return new SearchOutcome(SearchState.AgentNotRunning, []);
        }
        catch (IOException e)
        {
            return new SearchOutcome(SearchState.Failed, [], e.Message);
        }
    }
}
