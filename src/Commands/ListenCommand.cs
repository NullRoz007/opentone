using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenTone;

public class ListenSettings : CommandSettings, IDeviceSettings
{
   [CommandArgument(0, "[captureid]")]
    public string? CaptureStr { get; init; }

    [CommandArgument(1, "<playbackid>")]
    public required string PlaybackStr { get; init; }

    [CommandOption("-d|--device")]
    [Description("The amp to listen to.")]
    public required string Device { get; init; }
}


public class ListenCommand : AmpCommand<ListenSettings>
{
    protected override void Run(FenderAmp amp, ListenSettings s)
    {
        RunAsync(s).GetAwaiter().GetResult();
    }

    private async Task RunAsync(ListenSettings s)
    {
        AudioCapture c = new(
           s.CaptureStr ?? "", // resolve this per amp
           s.PlaybackStr
        );

        await AudioActions.Listen(c);
    }
}

