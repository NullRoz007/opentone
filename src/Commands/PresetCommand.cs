using OpenTone;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;

public class PresetSettings : CommandSettings, IDeviceSettings
{
    [CommandArgument(0, "<slot>")]
    [Description("Preset slot, 1 to 60")]
    public required int Slot { get; init; }

    [CommandArgument(1, "<device>")]
    [Description("Target device")]
    public required string Device { get; init; }
}

public class GetPresetCommand : AmpCommand<PresetSettings>
{
    protected override void Run(FenderAmp amp, PresetSettings s) =>
        AnsiConsole.WriteLine(AmpActions.GetPreset(amp, s.Slot));
}

public class SetPresetCommand : AmpCommand<PresetSettings>
{
    protected override void Run(FenderAmp amp, PresetSettings s) =>
        AnsiConsole.WriteLine(AmpActions.SetPreset(amp, s.Slot));
}

public class ViewNodeSettings : CommandSettings, IDeviceSettings
{
        [CommandArgument(0, "<nodeid>")]
        [Description("The node ID of the preset node, string")]
        public required string Node { get; init; }

        [CommandArgument(1, "<device>")]
        [Description("Target device")]
        public required string Device { get; init; }
}

public class ViewNodeCommand : AmpCommand<ViewNodeSettings>
{
    protected override void Run(FenderAmp amp, ViewNodeSettings s) => 
        AnsiConsole.WriteLine(AmpActions.ViewNode(amp, s.Node));
}

public class SetModelSettings : CommandSettings, IDeviceSettings
{
        [CommandArgument(0, "<nodeid>")]
        [Description("The node ID of the preset node, string")]
        public required string NodeId { get; init; }

        [CommandArgument(1, "<fenderid>")]
        [Description("The fenderID of the model, string")]
        public required string FenderId{ get; init; }

        [CommandArgument(2, "<device>")]
        [Description("Target device")]
        public required string Device { get; init; }
}

public class SetModelCommand : AmpCommand<SetModelSettings>
{
    protected override void Run(FenderAmp amp, SetModelSettings s)
    {
        AnsiConsole.WriteLine(AmpActions.SetModel(amp, s.NodeId, s.FenderId));
    }
}