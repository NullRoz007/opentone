using System.ComponentModel;
using OpenTone;
using Spectre.Console;
using Spectre.Console.Cli;
using static OpenTone.FenderFirmware;

public class FirmwareSettings : CommandSettings, IDeviceSettings
{
    [CommandOption("-d|--device")]
    [Description("Target device")]
    public required string Device { get; init; }
}

public class FirmwareCommand : AmpCommand<FirmwareSettings>
{
    protected override void Run(FenderAmp amp, FirmwareSettings s) =>
        AnsiConsole.Write(Ui.KeyValues()
            .Row("Device", $"[bold]{Markup.Escape(amp.GetDeviceName() ?? "Unknown device")}[/]")
            .Row("Firmware version", $"[bold]{Markup.Escape(AmpActions.GetFirmware(amp))}[/]"));
}

public class FirmwareUpdateSettings : CommandSettings, IDeviceSettings
{
    [CommandArgument(0, "<path>")]
    [Description("Firmware file path")]
    public required string Path { get; init; }
    
    [CommandOption("-d|--device")]
    [Description("Target device")]
    public required string Device { get; init; }

    [CommandOption("-y|--yes")]
    [Description("Flash without asking for confirmation. Required when not run in an interactive terminal.")]
    public bool Yes { get; init; }
}

public class FirmwareDumpSettings : CommandSettings
{
    [CommandArgument(0, "<input>")]
    [Description("Path of a WireShark PcapNG capture taken during a firmware update.")]
    public required string Input { get; init; }

    [CommandArgument(1, "<output>")]
    [Description("Path to save the dumped firmware.")]
    public required string Output { get; init; }

    [CommandOption("-d|--device")]
    [Description("Not needed any more; accepted for compatibility.")]
    public string? Device { get; init; }
}

public class UpdateCommand : AmpCommand<FirmwareUpdateSettings>
{
    protected override void Run(FenderAmp amp, FirmwareUpdateSettings settings)
    {
        bool interactive = AnsiConsole.Profile.Capabilities.Interactive;
        if (!interactive && !settings.Yes)
        {
            Ui.Message("Not flashed", "Not an interactive terminal. Pass --yes to flash without the confirmation prompt.", Ui.Level.Error);
            return;
        }

        if (interactive)
        {
            AnsiConsole.Cursor.Hide();
            AnsiConsole.Clear();
        }

        try
        {
            Flash(amp, settings, interactive);
        }
        catch (FirmwareUpdateInterruptedException)
        {
            // The error itself is shown by the app's error handler.
            Ui.Message("Amp state", "The amp has started the update and is still in update mode. Run flash again with a valid image.", Ui.Level.Warning);
            throw;
        }
        finally
        {
            if (interactive) AnsiConsole.Cursor.Show();
        }
    }

    private static void Flash(FenderAmp amp, FirmwareUpdateSettings settings, bool interactive)
    {
        byte[] raw = File.ReadAllBytes(settings.Path);
        var usbDevice = amp.GetUsbDevice();
        FirmwareTransport fwTransport = new(usbDevice);
        FenderFirmware firmware = new(raw); //throws when firmware is invalid

        var warning = new Panel(
            "[bold yellow]Warning:[/] this writes new firmware to your amp and cannot be undone.\n\n" +
            "Do [bold]not[/] unplug the USB cable, power off the amp or let the computer sleep until it finishes.\n" +
            "The update takes about 3 minutes. The progress bar may sit at 0% for up to 15 seconds at the start.\n" +
            "Close Fender Tone and any other program that uses the amp first.\n" +
            "If the update is interrupted, the amp may stop working normally. This is unofficial software, so use it at your own risk.")
            .Header("[yellow]Read before flashing[/]")
            .BorderColor(Color.Yellow)
            .Border(BoxBorder.Rounded)
            .Padding(1, 0);

        AnsiConsole.Write(warning);
        Ui.FirmwareSummary(settings.Path, firmware, amp);

        bool confirmed = settings.Yes || AnsiConsole.Confirm("Valid firmware found, proceed with flashing?", false);
        if(confirmed) {
            if (interactive && !settings.Yes) AnsiConsole.Write(new ControlCode("\u001b[1A\u001b[2K"));
            AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .Start(ctx =>
            {
                var flash = ctx.AddTask("Waiting for device...", maxValue: 100);
                fwTransport.SendFirmware(firmware.Payload, p =>
                {
                    flash.Description = $"Flashing firmware";
                    flash.Value = 100.0 * (p.frameIndex + 1) / p.frameCount;
                });

                flash.Value = 100;
                flash.Description = "[green]Complete[/]";
            });
        }
    }
}

/// <summary>Extracts the firmware image from a capture of an official update. Use "patch" to inspect or edit it.</summary>
public class DumpCommand : Command<FirmwareDumpSettings>
{
    public override int Execute(CommandContext context, FirmwareDumpSettings settings, CancellationToken cancellation)
    {
        if (!PatchCommand.IsCapture(settings.Input))
            throw new ArgumentException($"{settings.Input} is not a PcapNG capture. To inspect or edit a firmware image, use 'patch'.");

        var firmware = CreateFromCapture(settings.Input);   // throws if the CRC is invalid
        firmware.Save(settings.Output);

        AnsiConsole.Write(Ui.KeyValues()
            .Row("Capture", $"[bold]{Markup.Escape(settings.Input)}[/]")
            .Row("Size", $"{firmware.Info.Size} bytes ({firmware.Info.Size / 32} update frames)")
            .Row("CRC-32", $"0x{firmware.Info.CrcCalculated:X8} [green]valid[/] [grey](expected 0x{firmware.Info.CrcExpected:X8})[/]")
            .Row("Written to", $"[bold]{Markup.Escape(settings.Output)}[/]"));
        return 0;
    }
}
