using OpenTone;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

public class SetParamSettings : CommandSettings, IDeviceSettings
{
        [CommandArgument(0, "<nodeid>")]
        [Description("The node ID of the preset node, string")]
        public required string Node { get; init; }

        [CommandArgument(1, "<paramid>")]
        [Description("The param ID of the preset node, string")]
        public required string Param{ get; init; }

        [CommandArgument(2, "<value>")]
        [Description("A float, int, string or bool")]
        public required string Value{ get; init; }

        [CommandArgument(3, "<device>")]
        [Description("Target device")]
        public required string Device { get; init; }
}

public class GetParamSettings : CommandSettings, IDeviceSettings
{
        [CommandArgument(0, "<nodeid>")]
        [Description("The node ID of the preset node, string")]
        public required string Node { get; init; }

        [CommandArgument(1, "<paramid>")]
        [Description("The param ID of the preset node, string")]
        public required string Param{ get; init; }

        [CommandArgument(2, "<device>")]
        [Description("Target device")]
        public required string Device { get; init; }
}

public class SetParamCommand : AmpCommand<SetParamSettings>
{
    protected override void Run(FenderAmp amp, SetParamSettings s) => 
        AnsiConsole.WriteLine(AmpActions.SetParamAuto(amp, s.Node, s.Param, s.Value));
}

public class GetParamCommand : AmpCommand<GetParamSettings>
{
    protected override void Run(FenderAmp amp, GetParamSettings s) => 
        AnsiConsole.WriteLine(AmpActions.GetParam(amp, s.Node, s.Param));
}