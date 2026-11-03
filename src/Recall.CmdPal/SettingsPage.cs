using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Recall.Core.Ipc;

namespace Recall.CmdPal;

/// <summary>Lists the indexed folders and exclude patterns. Text typed in the search box can be added as either.</summary>
internal sealed partial class SettingsPage : DynamicListPage
{
    private const string RemoveIcon = "";
    private const string AddIcon = "";
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(1);

    private IpcSettings? _settings;
    private string? _error;
    private DateTime _loadedAt = DateTime.MinValue;
    private int _loading;
    private IListItem[] _items = [];

    public SettingsPage()
    {
        Icon = Icons.Logo;
        Title = "Recall Search settings";
        Name = "Open";
        PlaceholderText = "Type a folder path or an exclude pattern (*.log, node_modules/, secret*.txt) to add it";
        Rebuild();
    }

    public override IListItem[] GetItems()
    {
        if (DateTime.UtcNow - _loadedAt > Fresh) _ = ReloadAsync();
        return _items;
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        Rebuild();
        RaiseItemsChanged(_items.Length);
    }

    private async Task ReloadAsync()
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        IsLoading = true;
        try
        {
            var result = await SettingsClient.LoadAsync();
            _settings = result.Settings ?? _settings;
            _error = result.Error;
        }
        finally
        {
            _loadedAt = DateTime.UtcNow;
            IsLoading = false;
            Interlocked.Exchange(ref _loading, 0);
        }
        Rebuild();
        RaiseItemsChanged(_items.Length);
    }

    private void Changed()
    {
        _loadedAt = DateTime.MinValue;
        SearchText = "";
        _ = ReloadAsync();
    }

    private void Rebuild()
    {
        var items = new List<IListItem>();
        var text = SearchText?.Trim() ?? "";
        if (text.Length > 0)
        {
            items.Add(Add($"Add folder: {text}", "Index this folder and watch it for changes", "Folder added", () => SettingsClient.AddFolderAsync(text)));
            items.Add(Add($"Add exclusion: {text}", "Skip files and folders matching this pattern everywhere", "Exclusion added", () => SettingsClient.AddExcludeAsync(text)));
        }
        if (_error != null)
            items.Add(new ListItem(new NoOpCommand()) { Title = _error, Subtitle = "Settings are changed by the agent, so it must be running." });
        foreach (var root in _settings?.Roots ?? [])
            items.Add(Folder(root));
        foreach (var pattern in _settings?.Excludes ?? [])
            items.Add(Exclusion(pattern));
        if (_settings is { Roots.Length: 0, Excludes.Length: 0 } && text.Length == 0)
            items.Add(new ListItem(new NoOpCommand()) { Title = "No folders yet", Subtitle = "Type a folder path above to add one." });
        _items = [.. items];
    }

    private ListItem Add(string title, string subtitle, string done, Func<Task<SettingsResult>> call) =>
        new(new SettingsChangeCommand("Add", AddIcon, done, call, Changed)) { Title = title, Subtitle = subtitle };

    private ListItem Folder(IpcRoot root) => new(new NoOpCommand())
    {
        Title = root.Path,
        Subtitle = root.Watched ? "Folder - watched for changes" : "Folder - scanned at start",
        Section = "Folders",
        MoreCommands = [Remove(() => SettingsClient.RemoveFolderAsync(root.Path), "Folder removed from the index")],
    };

    private ListItem Exclusion(string pattern) => new(new NoOpCommand())
    {
        Title = pattern,
        Subtitle = "Exclusion pattern",
        Section = "Exclusions",
        MoreCommands = [Remove(() => SettingsClient.RemoveExcludeAsync(pattern), "Exclusion removed")],
    };

    private CommandContextItem Remove(Func<Task<SettingsResult>> call, string done) =>
        new(new SettingsChangeCommand("Remove", RemoveIcon, done, call, Changed)) { IsCritical = true };
}
