using System.ComponentModel;
using OpenTone;
using Spectre.Console;
using Spectre.Console.Cli;

public class FirmwareSettings : CommandSettings, IDeviceSettings
    {
        [CommandArgument(0, "<device>")]
        [Description("Target device")]
        public required string Device { get; init; }
    }

    public class FirmwareCommand : AmpCommand<FirmwareSettings>
    {
        protected override void Run(FenderAmp amp, FirmwareSettings s) =>
            AnsiConsole.WriteLine(AmpActions.GetFirmware(amp));
    }
