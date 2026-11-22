
using OpenTone;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;


public class GetSettings : CommandSettings, IDeviceSettings
{
    [CommandArgument(0, "<type>")]
    [Description("What to get: param, preset, node")]
    public required string Type { get; init; }

    [CommandArgument(1, "<arg1>")]
    [Description("Node ID or preset slot")]
    public required string Arg1 { get; init; }

    [CommandArgument(2, "[arg2]")]
    [Description("Parameter ID")]
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
            _ => throw new ArgumentException("Unknown type: " + s.Type)
        };

        AnsiConsole.WriteLine(result);
    }
}