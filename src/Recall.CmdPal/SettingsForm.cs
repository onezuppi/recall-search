using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Recall.Core.Ipc;

namespace Recall.CmdPal;

/// <summary>What one list on the settings page shows and which agent calls add and remove its entries.</summary>
internal sealed record ListSpec(
    string Title,
    string Hint,
    string AddPlaceholder,
    string AddLabel,
    Func<IpcSettings, IEnumerable<(string Value, string Label)>> Items,
    Func<string, Task<SettingsResult>> Add,
    Func<string, Task<SettingsResult>> Remove);

/// <summary>An Adaptive Card form: the current entries, a field to add one and a list to remove one.</summary>
internal sealed partial class SettingsForm : FormContent
{
    private const string AddAction = "add", RemoveAction = "remove", ValueField = "value", RemoveField = "remove";
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
        var action = Text(root, "action");
        var result = action switch
        {
            AddAction when Text(root, ValueField).Length > 0 => Run(_spec.Add, Text(root, ValueField)),
            RemoveAction when Text(root, RemoveField).Length > 0 => Run(_spec.Remove, Text(root, RemoveField)),
            _ => null,
        };
        if (result != null) _state.Apply(result);
        return CommandResult.KeepOpen();
    }

    private static SettingsResult Run(Func<string, Task<SettingsResult>> call, string value) =>
        Task.Run(() => call(value)).GetAwaiter().GetResult();

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    private string Build()
    {
        var items = _state.Current is { } s ? _spec.Items(s).ToList() : [];
        var body = new JsonArray { Block(_spec.Title, bold: true, size: "Medium") };
        if (_state.Error != null) body.Add(Block(_state.Error, color: "Attention"));
        if (items.Count == 0) body.Add(Block(_spec.Hint, color: "Accent"));
        foreach (var (_, label) in items) body.Add(Block(label));
        body.Add((JsonNode)new JsonObject { ["type"] = "Input.Text", ["id"] = ValueField, ["placeholder"] = _spec.AddPlaceholder });
        if (items.Count > 0)
            body.Add((JsonNode)new JsonObject
            {
                ["type"] = "Input.ChoiceSet",
                ["id"] = RemoveField,
                ["placeholder"] = "Choose an entry to remove",
                ["choices"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject { ["title"] = i.Label, ["value"] = i.Value }).ToArray()),
            });
        var actions = new JsonArray { Submit(_spec.AddLabel, AddAction) };
        if (items.Count > 0) actions.Add(Submit("Remove selected", RemoveAction));
        return new JsonObject
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.5",
            ["body"] = body,
            ["actions"] = actions,
        }.ToJsonString();
    }

    private static JsonNode Block(string text, bool bold = false, string? size = null, string? color = null)
    {
        var block = new JsonObject { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = true };
        if (bold) block["weight"] = "Bolder";
        if (size != null) block["size"] = size;
        if (color != null) block["color"] = color;
        return block;
    }

    private static JsonNode Submit(string title, string action) =>
        new JsonObject { ["type"] = "Action.Submit", ["title"] = title, ["data"] = new JsonObject { ["action"] = action } };
}
