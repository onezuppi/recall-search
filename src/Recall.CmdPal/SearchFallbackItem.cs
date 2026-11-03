using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Recall.CmdPal;

/// <summary>Home-page item "Search files for ..." shown once the typed text has at least three characters.</summary>
internal sealed partial class SearchFallbackItem : FallbackCommandItem
{
    private const int MinQueryLength = 3;
    private readonly SearchPage _page = new(new SearchSession());

    public SearchFallbackItem(string id) : base(new SearchPage(new SearchSession()), "Search files by content", id)
    {
        Icon = Icons.Logo;
    }

    public override void UpdateQuery(string query)
    {
        if (query.Trim().Length < MinQueryLength)
        {
            Title = "Search files by content";
            Subtitle = "Recall Search: type at least 3 characters";
            return;
        }
        Title = $"Search files for \"{query}\"";
        Subtitle = "Recall Search";
        Command = _page;
        try { _page.Seed(query); }
        catch (Exception e) { Subtitle = "Recall Search: " + e.GetType().Name; }
    }
}
