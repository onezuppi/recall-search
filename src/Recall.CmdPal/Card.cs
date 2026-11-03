using System.Text.Json.Nodes;

namespace Recall.CmdPal;

/// <summary>Small builders for the Adaptive Card elements the settings forms use.</summary>
internal static class Card
{
    public static JsonNode Text(string text, bool bold = false, string? size = null, string? color = null, bool subtle = false)
    {
        var block = new JsonObject { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = true };
        if (bold) block["weight"] = "Bolder";
        if (size != null) block["size"] = size;
        if (color != null) block["color"] = color;
        if (subtle) block["isSubtle"] = true;
        return block;
    }

    public static JsonNode Input(string id, string placeholder) =>
        new JsonObject { ["type"] = "Input.Text", ["id"] = id, ["placeholder"] = placeholder };

    /// <param name="selected">The preselected value, or null to start with nothing chosen.</param>
    public static JsonNode Choices(string id, string label, IEnumerable<(string Title, string Value)> choices, string? selected)
    {
        var set = new JsonObject
        {
            ["type"] = "Input.ChoiceSet",
            ["id"] = id,
            ["style"] = "compact",
            ["placeholder"] = label,
            ["choices"] = new JsonArray(choices.Select(c => (JsonNode)new JsonObject { ["title"] = c.Title, ["value"] = c.Value }).ToArray()),
        };
        if (label.Length > 0) set["label"] = label;
        if (selected != null) set["value"] = selected;
        return set;
    }

    public static JsonNode Submit(string title, string action) =>
        new JsonObject { ["type"] = "Action.Submit", ["title"] = title, ["data"] = new JsonObject { ["action"] = action } };

    public static string Wrap(JsonArray body, JsonArray actions) => new JsonObject
    {
        ["type"] = "AdaptiveCard",
        ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
        ["version"] = "1.5",
        ["body"] = body,
        ["actions"] = actions,
    }.ToJsonString();
}
