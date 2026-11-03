using Recall.Core.Ipc;

namespace Recall.CmdPal;

/// <summary>One entry of a list as shown on the card: the value sent to the agent and a readable label.</summary>
internal sealed record ListEntry(string Value, string Label);

/// <summary>An option of a drop-down next to the add field, for example the folder list or the file group.</summary>
internal sealed record Choice(string Title, string Value);

/// <summary>The field that adds an entry. <see cref="Choices"/> adds a drop-down whose first option is the default.</summary>
internal sealed record AddSpec(
    string Placeholder,
    string Button,
    Func<string, string, Task<SettingsResult>> Call,
    string? ChoiceLabel = null,
    IReadOnlyList<Choice>? Choices = null);

/// <summary>A button that acts on the entry picked in the remove selector, such as moving a folder to the other list.</summary>
internal sealed record RowAction(string Button, Func<string, Task<SettingsResult>> Call);

/// <summary>What one card on the settings page shows and which agent calls its buttons make.</summary>
internal sealed record ListSpec(
    string Title,
    string Help,
    string Empty,
    Func<IpcSettings, IEnumerable<ListEntry>> Entries,
    AddSpec? Add,
    Func<string, Task<SettingsResult>>? Remove,
    IReadOnlyList<RowAction>? RowActions = null);
