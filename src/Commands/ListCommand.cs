using OpenTone;
using LibUsbDotNet.LibUsb;
using Spectre.Console;
using Spectre.Console.Cli;
using System.Runtime.InteropServices;
using Hexa.NET.MiniAudio;

public class ListSettings : CommandSettings { }

public class ListCommand : Command<ListSettings>
{
    private const int NAME_COLUMN_WIDTH = 30;
    private const int DETAIL_COLUMN_WIDTH = 64;
    private const int MODE_COLUMN_WIDTH = 10;

    private static void AddCaptureDeviceRow(Table table, AudioCapture audioCapture, MaDeviceInfo device, string type)
    {
        unsafe {
                string? name = Marshal.PtrToStringUTF8((nint)(&device.Name_0));
                object id = audioCapture.ResolveDeviceId(device);
                table.AddRow(
                    $"[bold]{Markup.Escape(
                        name?.Length >= NAME_COLUMN_WIDTH ? 
                            name.Substring(0, NAME_COLUMN_WIDTH - 4) + "..." : 
                            (name ?? "Unknown")
                    )}[/]",
                    $"{id}",
                    $"[{(type == "capture" ? "blue" : "green")}]{type}[/]"
                );
            }
    }
    public override int Execute(
        CommandContext context,
        ListSettings settings,
        CancellationToken cancellation)
    {
        using var usb = new UsbContext();
        using var cap = new AudioCapture();

        var devices = FenderDevices.FindAll(usb) ?? [];
        var (playbackDevices, captureDevices) = cap.EnumerateDevices();

        if (devices.Length == 0)
        {
            Ui.Message("Devices", "No Fender devices found.", Ui.Level.Warning);
            return 0;
        }

        var table = Ui.Table("Name (-d)", "VID:PID", "Mode")
            .Title("Amplifiers");

        var captureTable = Ui.Table("Name", $"{cap.BackendName()} ID", "Type")
            .Title("Capture/Playback Devices");

        table.Columns[0].Width = NAME_COLUMN_WIDTH;
        table.Columns[1].Width = DETAIL_COLUMN_WIDTH;
        table.Columns[2].Width = MODE_COLUMN_WIDTH;

        captureTable.Columns[0].Width = NAME_COLUMN_WIDTH;
        captureTable.Columns[1].Width = DETAIL_COLUMN_WIDTH;
        captureTable.Columns[2].Width = MODE_COLUMN_WIDTH;

        foreach (var usbDevice in devices)
        {
            string? deviceName = FenderDevices.Known
                .FirstOrDefault(d => d.Value.Pid == usbDevice.Info.ProductId)
                .Key;
            string mode = deviceName switch
            {
                FenderDevices.LT25 => "[green]normal[/]",
                FenderDevices.LT_DFU => "[blue]update[/]",
                _ => "[grey]unknown[/]",
            };

            table.AddRow(
                $"[bold]{Markup.Escape(deviceName ?? "Unknown")}[/]",
                $"{usbDevice.Info.VendorId:X4}:{usbDevice.Info.ProductId:X4}",
                mode);
        }

        foreach (var device in captureDevices) AddCaptureDeviceRow(captureTable, cap, device, "capture");
        foreach (var device in playbackDevices) AddCaptureDeviceRow(captureTable, cap, device, "playback");

        AnsiConsole.Write(new Rows(
            table,
            captureTable
        ));

        foreach (var d in devices) d.Dispose();     // before the context is disposed
        return 0;
    }
}