using System.ComponentModel;
using OpenTone;
using Spectre.Console;
using Spectre.Console.Cli;

public class SetSettings : CommandSettings, IDeviceSettings
{
    [CommandArgument(0, "<type>")]
    [Description("What to set: param, preset, model")]
    public required string Type { get; init; }

    [CommandArgument(1, "<arg1>")]
    [Description("Node ID or preset slot")]
    public required string Arg1 { get; init; }

    [CommandArgument(2, "[arg2]")]
    [Description("Parameter ID or model ID")]
    public string? Arg2 { get; init; }

    [CommandArgument(3, "[arg3]")]
    [Description("Parameter value")]
    public string? Arg3 { get; init; }

    [CommandOption("-d|--device")]
    [Description("Target device")]
    public required string Device { get; init; }
}

public class SetCommand : AmpCommand<SetSettings>
{
    protected override void Run(FenderAmp amp, SetSettings s)
    {
        string? result = s.Type.ToLowerInvariant() switch
        {
            "param" => AmpActions.SetParamAuto(amp, s.Arg1, s.Arg2 ?? "", s.Arg3 ?? ""),
            "preset" => AmpActions.SetPreset(amp, int.Parse(s.Arg1)),
            "model" => AmpActions.SetModel(amp, s.Arg1, s.Arg2 ?? ""),
            _ => throw new ArgumentException("Unknown type: " + s.Type)
        };

        string title = s.Type.ToLowerInvariant() switch
        {
            "param" => $"Node {s.Arg1} after setting {s.Arg2}",
            "preset" => $"Preset {s.Arg1} selected",
            "model" => $"Node {s.Arg1} after setting model",
            _ => "Result",
        };
        Ui.Result(title, result);
    }
}
