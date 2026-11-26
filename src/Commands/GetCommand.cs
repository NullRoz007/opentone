
using OpenTone;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;


public class GetSettings : CommandSettings, IDeviceSettings
{
    [CommandArgument(0, "<type>")]
    [Description("What to get: param, preset, node, mem (needs the debug hook firmware)")]
    public required string Type { get; init; }

    [CommandArgument(1, "<arg1>")]
    [Description("Node ID, preset slot or memory address")]
    public required string Arg1 { get; init; }

    [CommandArgument(2, "[arg2]")]
    [Description("Parameter ID or number of bytes")]
    public string? Arg2 { get; init; }

    [CommandOption("-d|--device")]
    [Description("Target device")]
    public required string Device { get; init; }
}

public class GetCommand : AmpCommand<GetSettings>
{
    protected override void Run(FenderAmp amp, GetSettings s)
    {
        string? result = s.Type.ToLowerInvariant() switch
        {
            "param" when s.Arg2 != null => AmpActions.GetParam(amp, s.Arg1, s.Arg2),
            "preset" => AmpActions.GetPreset(amp, int.Parse(s.Arg1)),
            "node" => AmpActions.ViewNode(amp, s.Arg1),
            "mem" => AmpActions.Peek(amp, s.Arg1, s.Arg2 ?? "16"),
            _ => throw new ArgumentException("Unknown type: " + s.Type)
        };

        // Presets and nodes are printed as plain JSON (no panel, no colour) so they can be piped or saved.
        if (IsJsonContainer(result))
        {
            Console.WriteLine(result);
            return;
        }

        string title = s.Type.ToLowerInvariant() switch
        {
            "param" => $"{s.Arg1}.{s.Arg2}",
            "preset" => $"Preset {s.Arg1}",
            "mem" => $"Memory at {s.Arg1}",
            _ => $"Node {s.Arg1}",
        };
        
        Ui.Result(title, result);
    }

    private static bool IsJsonContainer(string? text)
    {
        try { return text != null && System.Text.Json.Nodes.JsonNode.Parse(text) is System.Text.Json.Nodes.JsonObject or System.Text.Json.Nodes.JsonArray; }
        catch (System.Text.Json.JsonException) { return false; }
    }
}