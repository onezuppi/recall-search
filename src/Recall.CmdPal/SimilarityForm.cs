using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Recall.CmdPal;

/// <summary>An Adaptive Card form with one number: how similar in meaning a file must be to the query to be listed.</summary>
internal sealed partial class SimilarityForm : FormContent
{
    private const string ValueField = "similarity";
    private readonly SettingsState _state;

    public SimilarityForm(SettingsState state)
    {
        _state = state;
        TemplateJson = Build();
        state.Changed += () => TemplateJson = Build();
    }

    public override ICommandResult SubmitForm(string inputs)
    {
        using var doc = JsonDocument.Parse(inputs);
        var text = doc.RootElement.TryGetProperty(ValueField, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        if (text.Length > 0) _state.Apply(Task.Run(() => SettingsClient.SetSimilarityAsync(text)).GetAwaiter().GetResult());
        return CommandResult.KeepOpen();
    }

    private string Build()
    {
        var body = new JsonArray { Block("Semantic search threshold", bold: true) };
        if (_state.Error != null) body.Add(Block(_state.Error, color: "Attention"));
        var current = _state.Current?.MinSimilarity;
        var actions = new JsonArray();
        if (current is { } value)
        {
            body.Add(Block("Files whose meaning is less similar to the query than this number are hidden. Raise it for fewer, surer "
                           + "results, lower it for more. The value is a cosine similarity from 0 to 1 and depends on the model."));
            body.Add((JsonNode)new JsonObject
            {
                ["type"] = "Input.Text",
                ["id"] = ValueField,
                ["value"] = value.ToString("0.00", CultureInfo.InvariantCulture),
                ["placeholder"] = "For example 0.84",
            });
            actions.Add((JsonNode)new JsonObject { ["type"] = "Action.Submit", ["title"] = "Save threshold" });
        }
        else
        {
            body.Add(Block("Semantic search is off. Set \"embeddingModel\" in config.json to turn it on.", color: "Accent"));
        }
        return new JsonObject
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.5",
            ["body"] = body,
            ["actions"] = actions,
        }.ToJsonString();
    }

    private static JsonNode Block(string text, bool bold = false, string? color = null)
    {
        var block = new JsonObject { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = true };
        if (bold) block["weight"] = "Bolder";
        if (color != null) block["color"] = color;
        return block;
    }
}
