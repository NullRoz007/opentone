using System.ComponentModel;
using OpenTone;
using Spectre.Console.Cli;

public class CallSettings : CommandSettings, IDeviceSettings
{
    [CommandArgument(0, "<address>")]
    [Description("Function address, e.g. 0x80ecd6c (the Thumb bit is added automatically)")]
    public required string Address { get; init; }

    [CommandArgument(1, "[args]")]
    [Description("Up to 8 arguments (r0-r3, then the stack), hex with 0x or decimal")]
    public string[] Args { get; init; } = [];

    [CommandOption("-d|--device")]
    [Description("Target device")]
    public required string Device { get; init; }
}

/// <summary>Calls a firmware function through the debug hook. Only works on a firmware built with the hook.</summary>
public class CallCommand : AmpCommand<CallSettings>
{
    protected override void Run(FenderAmp amp, CallSettings s) =>
        Ui.Result($"Call {s.Address}", AmpActions.Call(amp, s.Address, s.Args));
}
