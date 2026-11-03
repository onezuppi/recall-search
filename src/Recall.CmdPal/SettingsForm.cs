using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Recall.Core.Ipc;

namespace Recall.CmdPal;

/// <summary>An Adaptive Card for one list: the entries in a text box, one per line, and a Save button that sends what changed.</summary>
internal sealed partial class SettingsForm : FormContent
{
    private const string SaveAction = "save", TextField = "lines";
    private readonly SettingsState _state;
    private readonly ListSpec _spec;
    private string? _draft;

    public SettingsForm(SettingsState state, ListSpec spec)
    {
        _state = state;
        _spec = spec;
        TemplateJson = Build();
        state.Changed += () => TemplateJson = Build();
    }

    public override ICommandResult SubmitForm(string inputs)
    {
        if (_state.Current is not { } current) return CommandResult.KeepOpen();
        using var doc = JsonDocument.Parse(inputs);
        var typed = doc.RootElement.TryGetProperty(TextField, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";
        var wanted = ListDiff.ParseLines(typed, _spec.Clean, _spec.Equality);
        var have = ListDiff.ParseLines(string.Join('\n', _spec.Lines(current)), _spec.Clean, _spec.Equality);
        var (added, removed) = ListDiff.Compute(have, wanted, _spec.Equality);
        var result = Task.Run(() => _spec.Apply(new ListChange(wanted, added, removed, current))).GetAwaiter().GetResult();
        _draft = result.Ok ? null : typed; // a refused save keeps what the user typed
        _state.Apply(result);
        return CommandResult.KeepOpen();
    }

    private string Build()
    {
        var text = _draft ?? (_state.Current is { } s ? string.Join('\n', _spec.Lines(s)) : "");
        var body = new JsonArray { Card.Text(_spec.Title, bold: true, size: "Medium"), Card.Text(_spec.Help, subtle: true) };
        if (_spec.Builtin != null) body.Add(Card.Text("Built in, always on and cannot be removed here:\n\n" + _spec.Builtin, subtle: true));
        if (_state.Error != null) body.Add(Card.Text(_state.Error, color: "Attention"));
        body.Add(Card.Input(TextField, _spec.Example, text, _spec.Multiline));
        return Card.Wrap(body, new JsonArray { Card.Submit("Save", SaveAction) });
    }
}
