using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Recall.CmdPal;

/// <summary>Home-page item "Search files for ..." shown once the typed text has at least three characters.</summary>
internal sealed partial class SearchFallbackItem : FallbackCommandItem
{
    private const int MinQueryLength = 3;
    private readonly SearchPage _page = new(new SearchSession());

    public SearchFallbackItem(string id) : base(new SearchPage(new SearchSession()), "Search files", id)
    {
        Icon = Icons.Logo;
    }

    public override void UpdateQuery(string query)
    {
        if (query.Trim().Length < MinQueryLength)
        {
            Title = string.Empty;
            Subtitle = string.Empty;
            return;
        }
        _page.Seed(query);
        Title = $"Search files for \"{query}\"";
        Subtitle = "Recall Search";
        Command = _page;
    }
}
