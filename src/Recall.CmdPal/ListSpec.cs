using Recall.Core.Ipc;

namespace Recall.CmdPal;

/// <summary>What the user changed in a list: the lines now in the box, those added and those removed since the agent's list.</summary>
internal sealed record ListChange(IReadOnlyList<string> Wanted, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IpcSettings Current);

/// <summary>
/// One card of the settings page: a text box with one entry per line and a Save button.
/// <see cref="Lines"/> reads the agent's list, <see cref="Normalize"/> cleans one typed line, <see cref="Apply"/> sends the difference.
/// </summary>
internal sealed record ListSpec(
    string Title,
    string Help,
    string Example,
    Func<IpcSettings, IEnumerable<string>> Lines,
    Func<ListChange, Task<SettingsResult>> Apply,
    Func<string, string>? Normalize = null,
    StringComparer? Comparer = null,
    bool Multiline = true)
{
    public StringComparer Equality => Comparer ?? StringComparer.OrdinalIgnoreCase;

    public string Clean(string line) => Normalize?.Invoke(line) ?? line;
}
