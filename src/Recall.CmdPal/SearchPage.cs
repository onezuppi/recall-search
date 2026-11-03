using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Recall.CmdPal;

internal sealed partial class SearchPage : DynamicListPage
{
    private readonly SearchSession _session;
    private CancellationTokenSource? _current;
    private IListItem[] _items = [ResultItems.Hint()];

    public SearchPage(SearchSession session)
    {
        _session = session;
        Icon = Icons.Logo;
        Title = "Recall Search";
        Name = "Search";
        PlaceholderText = "Type text you remember from a document or an image";
        ShowDetails = true;
    }

    public override IListItem[] GetItems() => _items;

    public override void UpdateSearchText(string oldSearch, string newSearch) => Start(newSearch);

    /// <summary>Opens the page with a query already running (used by the fallback item on the home page).</summary>
    public void Seed(string query)
    {
        SearchText = query;
        Start(query);
    }

    private void Start(string query)
    {
        _current?.Cancel();
        if (string.IsNullOrWhiteSpace(query))
        {
            _current = null;
            IsLoading = false;
            Publish([ResultItems.Hint()]);
            return;
        }
        var cts = _current = new CancellationTokenSource();
        IsLoading = true;
        _ = RunAsync(query, cts);
    }

    private async Task RunAsync(string query, CancellationTokenSource cts)
    {
        try
        {
            var outcome = await _session.SearchAsync(query, cts.Token);
            if (cts.IsCancellationRequested) return;
            Publish(ResultItems.From(outcome, query));
        }
        catch (OperationCanceledException) { return; }
        finally
        {
            if (ReferenceEquals(_current, cts)) IsLoading = false;
        }
    }

    private void Publish(IListItem[] items)
    {
        _items = items;
        RaiseItemsChanged(items.Length);
    }
}
