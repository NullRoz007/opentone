using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace OpenTone;

public class ListenSettings : CommandSettings, IDeviceSettings
{
    [CommandArgument(0, "<playbackid>")]
    [Description("Playback device ID from `opentone list`.")]
    public required string PlaybackStr { get; init; }

    [CommandArgument(1, "[captureid]")]
    [Description("Capture device ID from `opentone list`. Default: the amp given with -d, matched by its USB VID, PID and serial.")]
    public string? CaptureStr { get; init; }

    [CommandOption("-d|--device")]
    [Description("The amp to listen to.")]
    public required string Device { get; init; }
}


public class ListenCommand : AmpCommand<ListenSettings>
{
    protected override void Run(FenderAmp amp, ListenSettings s)
    {
        RunAsync(amp, s).GetAwaiter().GetResult();
    }

    private async Task RunAsync(FenderAmp amp, ListenSettings s)
    {
        object captureId = s.CaptureStr ?? FindCaptureDevice(amp);

        using AudioCapture c = new(captureId, s.PlaybackStr);

        await AudioActions.Listen(c);
    }

    /// <summary>The capture device of the amp opened with -d, found by its USB identity.</summary>
    internal static object FindCaptureDevice(FenderAmp amp)
    {
        var hw = FenderDevices.GetHardwareInfo(amp.GetUsbDevice())
            ?? throw new InvalidOperationException("Could not read the amp's USB device descriptor. Pass the capture ID instead.");

        using var enumerator = new AudioCapture();
        return enumerator.FindCaptureDevice(hw);
    }
}

