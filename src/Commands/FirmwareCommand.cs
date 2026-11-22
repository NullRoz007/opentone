using System.ComponentModel;
using System.Text.Json;
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
        AnsiConsole.WriteLine(AmpActions.GetFirmware(amp));
}

public class FirmwareUpdateSettings : CommandSettings, IDeviceSettings
{
    [CommandArgument(0, "<path>")]
    [Description("Firmware file path")]
    public required string Path { get; init; }
    
    [CommandOption("-d|--device")]
    [Description("Target device")]
    public required string Device { get; init; }
}

public class FirmwareDumpSettings : CommandSettings, IDeviceSettings
{
    [CommandArgument(0, "<input>")]
    [Description("Path of a WireShark PcapNG capture taken during a firmware update.")]
    public required string Input { get; init; }
    
    [CommandArgument(0, "<output>")]
    [Description("Path to save the dumped firmware.")]
    public required string Output { get; init; }
    
    [CommandOption("-d|--device")]
    [Description("Target device")]
    public required string Device { get; init; }
}

public class UpdateCommand : AmpCommand<FirmwareUpdateSettings>
{
    protected override void Run(FenderAmp amp, FirmwareUpdateSettings settings)
    {
        AnsiConsole.Cursor.Hide();
        AnsiConsole.Clear();

        byte[] raw = File.ReadAllBytes(settings.Path);
        var usbDevice = amp.GetUsbDevice();
        FirmwareTransport fwTransport = new(usbDevice);
        
        uint expected = 0, calculated = 0;
        string deviceName = amp.GetDeviceName() ?? "Unknown";
        bool isValid = ValidateFirmware(raw, out expected, out calculated);

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

        var table = new Table()
            .Border(TableBorder.Rounded)
            .HideHeaders()
            .AddColumn(new TableColumn(""))
            .AddColumn(new TableColumn(""));

        table.AddRow("Firmware CRC", isValid ? "[green]Valid[/]" : "[red]Invalid[/]");
        table.AddRow("[grey]calculated[/]", $"[grey]0x{firmware.CheckSumCalculated:X8}[/]");
        table.AddRow("[grey]expected[/]",   $"[grey]0x{firmware.CheckSumExpected:X8}[/]");
        table.AddEmptyRow();
        table.AddRow("Device", "[green]Connected[/]");
        table.AddRow("[grey]name[/]", $"[grey]{Markup.Escape(deviceName)}[/]");
        table.AddRow("[grey]vid[/]", $"[grey]0x{usbDevice.VendorId:X4}[/]");
        table.AddRow("[grey]pid[/]", $"[grey]0x{usbDevice.ProductId:X4}[/]");

        AnsiConsole.Write(table);

        bool confirmed = AnsiConsole.Confirm("Valid firmware found, proceed with flashing?", false);
        if(confirmed) {
            AnsiConsole.Write(new ControlCode("\u001b[1A\u001b[2K"));
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
                    int percentage = p.frameIndex / p.frameCount * 100;
                    flash.Value = 100.0 * (p.frameIndex + 1) / p.frameCount;
                });

                flash.Value = 100;
                flash.Description = "[green]Complete[/]";
            });
        }
    }
}

public class DumpCommand : AmpCommand<FirmwareDumpSettings>
{
    protected override void Run(FenderAmp amp, FirmwareDumpSettings settings)
    {
        AnsiConsole.WriteLine($"Dumping {amp.GetDeviceName()} firmware from {settings.Input}");
        var firmware = CreateFromCapture(amp, settings.Input);
        var stringRefs = FindJsonStrings(firmware.Payload);
        foreach(var str in stringRefs)
        {
            FenderPreset preset = FenderPreset.Parse(str.Value);
            AnsiConsole.WriteLine($"0x{str.Offset.ToString("X")} = {preset.Info.DisplayName}");
        }

        AnsiConsole.WriteLine($"crcCalculated \t= 0x{firmware.CheckSumCalculated.ToString("X")}");
        AnsiConsole.WriteLine($"crcExpected \t= 0x{firmware.CheckSumExpected.ToString("X")}");
        
        firmware.Save(settings.Output);
        AnsiConsole.WriteLine($"wrote {firmware.Size} bytes to {settings.Output}");
    }
}
