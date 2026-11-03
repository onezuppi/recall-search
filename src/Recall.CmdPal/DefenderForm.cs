using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Recall.CmdPal;

/// <summary>An Adaptive Card with two buttons that add or remove the agent in the Defender process exclusions.</summary>
internal sealed partial class DefenderForm : FormContent
{
    private const string AddAction = "add", RemoveAction = "remove";
    private string? _message;
    private bool _failed;

    public DefenderForm() => TemplateJson = Build();

    public override ICommandResult SubmitForm(string inputs)
    {
        var add = inputs.Contains($"\"{AddAction}\"", StringComparison.Ordinal);
        (_message, var done) = Task.Run(() => DefenderExclusion.ApplyAsync(add)).GetAwaiter().GetResult();
        _failed = !done;
        TemplateJson = Build();
        return CommandResult.KeepOpen();
    }

    private string Build()
    {
        var body = new JsonArray
        {
            Card.Text("Windows Defender", bold: true, size: "Medium"),
            Card.Text("The agent opens every new or changed file to read it, and Defender scans each of those reads. "
                + "Excluding the agent process keeps indexing light on the processor. Only the process Recall.Agent.exe is excluded, no folders. "
                + "Windows asks for administrator confirmation before it changes anything.", subtle: true),
        };
        if (_message != null) body.Add(Card.Text(_message, color: _failed ? "Attention" : "Good"));
        return Card.Wrap(body, new JsonArray { Card.Submit("Add the agent to Defender exclusions", AddAction), Card.Submit("Remove the exclusion", RemoveAction) });
    }
}
