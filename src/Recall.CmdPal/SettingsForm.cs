using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Recall.CmdPal;

/// <summary>An Adaptive Card for one list: help text, the current entries, a field to add one and a selector to remove or move one.</summary>
internal sealed partial class SettingsForm : FormContent
{
    private const string AddAction = "add", RemoveAction = "remove", RowPrefix = "row:";
    private const string ValueField = "value", ChoiceField = "choice", SelectedField = "selected";
    private readonly SettingsState _state;
    private readonly ListSpec _spec;

    public SettingsForm(SettingsState state, ListSpec spec)
    {
        _state = state;
        _spec = spec;
        TemplateJson = Build();
        state.Changed += () => TemplateJson = Build();
    }

    public override ICommandResult SubmitForm(string inputs)
    {
        using var doc = JsonDocument.Parse(inputs);
        var root = doc.RootElement;
        var selected = Text(root, SelectedField);
        var result = Text(root, "action") switch
        {
            AddAction when _spec.Add is { } add && Text(root, ValueField).Length > 0 =>
                Run(() => add.Call(Text(root, ValueField), Text(root, ChoiceField))),
            RemoveAction when _spec.Remove is { } remove && selected.Length > 0 => Run(() => remove(selected)),
            var a when a.StartsWith(RowPrefix, StringComparison.Ordinal) && selected.Length > 0 && RowAt(a) is { } row =>
                Run(() => row.Call(selected)),
            _ => null,
        };
        if (result != null) _state.Apply(result);
        return CommandResult.KeepOpen();
    }

    private RowAction? RowAt(string action) =>
        int.TryParse(action.AsSpan(RowPrefix.Length), out var i) && _spec.RowActions is { } rows && i >= 0 && i < rows.Count ? rows[i] : null;

    private static SettingsResult Run(Func<Task<SettingsResult>> call) => Task.Run(call).GetAwaiter().GetResult();

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    private string Build()
    {
        var entries = _state.Current is { } s ? _spec.Entries(s).ToList() : [];
        var body = new JsonArray { Card.Text(_spec.Title, bold: true, size: "Medium") };
        if (_spec.Help.Length > 0) body.Add(Card.Text(_spec.Help, subtle: true));
        if (_state.Error != null) body.Add(Card.Text(_state.Error, color: "Attention"));
        if (entries.Count == 0) body.Add(Card.Text(_spec.Empty, color: "Accent"));
        foreach (var e in entries) body.Add(Card.Text("- " + e.Label));
        if (_spec.Add is { } add)
        {
            body.Add(Card.Input(ValueField, add.Placeholder));
            if (add.Choices is { Count: > 0 } choices)
                body.Add(Card.Choices(ChoiceField, add.ChoiceLabel ?? "", choices.Select(c => (c.Title, c.Value)), choices[0].Value));
        }
        var canPick = entries.Count > 0 && (_spec.Remove != null || _spec.RowActions is { Count: > 0 });
        if (canPick)
            body.Add(Card.Choices(SelectedField, "Entry to change", entries.Select(e => (e.Label, e.Value)), null));

        var actions = new JsonArray();
        if (_spec.Add is { } a) actions.Add(Card.Submit(a.Button, AddAction));
        if (canPick)
        {
            for (var i = 0; i < (_spec.RowActions?.Count ?? 0); i++) actions.Add(Card.Submit(_spec.RowActions![i].Button, RowPrefix + i));
            if (_spec.Remove != null) actions.Add(Card.Submit("Remove selected", RemoveAction));
        }
        return Card.Wrap(body, actions);
    }
}
