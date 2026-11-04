using Recall.Core.Ipc;

namespace Recall.CmdPal;

internal enum SearchState { Results, AgentNotRunning, UntrustedServer, Failed }

internal sealed record SearchOutcome(SearchState State, IReadOnlyList<IpcHit> Hits, string? Message = null);

/// <summary>Debounced search against the agent. The page cancels the previous call on every keystroke.</summary>
internal sealed class SearchSession
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(150);
    // A query that arrives before the model is loaded waits for the load (about two seconds on the NPU).
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(6);

    private static long _lastWarmTicks;
    private static readonly TimeSpan WarmEvery = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Tells the agent to load the model. Called when the page opens and on typing, at most every 20 seconds (the agent keeps a loaded model
    /// for a minute), so the load of about two seconds runs while the user types and the first query finds the model ready.
    /// </summary>
    public static void WarmUp()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastWarmTicks);
        if (now - last < WarmEvery.TotalMilliseconds || Interlocked.CompareExchange(ref _lastWarmTicks, now, last) != last) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (PipeClient.IsListening()) await PipeClient.SendAsync(new IpcRequest(IpcOps.Warm), RequestTimeout, default);
            }
            catch (Exception e) when (e is UntrustedServerException or UnauthorizedAccessException or TimeoutException or IOException) { }
        });
    }

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
