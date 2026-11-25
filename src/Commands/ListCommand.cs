using OpenTone;
using LibUsbDotNet.LibUsb;
using Spectre.Console;
using Spectre.Console.Cli;

public class ListSettings : CommandSettings { }

public class ListCommand : Command<ListSettings>
{
    public override int Execute(
        CommandContext context,
        ListSettings settings,
        CancellationToken cancellation)
    {
        using var usb = new UsbContext();
        var devices = FenderDevices.FindAll(usb) ?? [];

        if (devices.Length == 0)
        {
            Ui.Message("Devices", "No Fender devices found.", Ui.Level.Warning);
            return 0;
        }

        var table = Ui.Table("Devices", "Name (-d)", "VID:PID", "Mode");
        foreach (var usbDevice in devices)
        {
            string? deviceName = FenderDevices.Known
                .FirstOrDefault(d => d.Value.Pid == usbDevice.Info.ProductId)
                .Key;
            string mode = deviceName switch
            {
                FenderDevices.LT25 => "normal",
                FenderDevices.LT_DFU => "[yellow]update[/]",
                _ => "[grey]unknown[/]",
            };

            table.AddRow(
                $"[bold]{Markup.Escape(deviceName ?? "Unknown")}[/]",
                $"{usbDevice.Info.VendorId:X4}:{usbDevice.Info.ProductId:X4}",
                mode);
        }
        AnsiConsole.Write(table);

        foreach (var d in devices) d.Dispose();     // before the context is disposed
        return 0;
    }
}