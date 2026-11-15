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

            foreach (var usbDevice in devices)
            {
                string deviceName = FenderDevices.Known
                    .FirstOrDefault(d => d.Value.Pid == usbDevice.Info.ProductId)
                    .Key;

                AnsiConsole.MarkupLine(
                    $"{Markup.Escape(deviceName ?? "Unknown")} " +
                    $"[grey]{usbDevice.Info.VendorId}:{usbDevice.Info.ProductId}[/]"
                );
            }

            return 0;
        }
    }