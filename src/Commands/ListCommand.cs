using OpenTone;
using LibUsbDotNet.LibUsb;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Rendering;
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

        var devices = FenderDevices.FindAll(usb) ?? [];

        if (devices.Length == 0)
        {
            Ui.Message("Devices", "No Fender devices found.", Ui.Level.Warning);
            return 0;
        }

        var table = Ui.Table("Name (-d)", "VID:PID", "Mode")
            .Title("Amplifiers");

        table.Columns[0].Width = NAME_COLUMN_WIDTH;
        table.Columns[1].Width = DETAIL_COLUMN_WIDTH;
        table.Columns[2].Width = MODE_COLUMN_WIDTH;

        bool anyUnsupported = false;
        foreach (var usbDevice in devices)
        {
            string? deviceName = FenderDevices.Known
                .FirstOrDefault(d => d.Value.Vid == usbDevice.Info.VendorId && d.Value.Pid == usbDevice.Info.ProductId)
                .Key;
            string mode = deviceName switch
            {
                FenderDevices.LT25 => "[green]normal[/]",
                FenderDevices.LT_DFU => "[blue]update[/]",
                _ => "[yellow]unsupported[/]",
            };

            // Not usable with -d: show what it probably is instead of a name.
            string name = deviceName != null
                ? $"[bold]{Markup.Escape(deviceName)}[/]"
                : $"[grey]{Markup.Escape(FenderDevices.OtherFenderProducts.GetValueOrDefault(usbDevice.Info.ProductId, "Unknown Fender device"))}[/]";
            anyUnsupported |= deviceName == null;

            table.AddRow(
                name,
                $"{usbDevice.Info.VendorId:X4}:{usbDevice.Info.ProductId:X4}",
                mode);
        }

        if (anyUnsupported)
            table.Caption("[grey]Unsupported devices can't be used with -d yet. Please report their VID:PID and model.[/]");

        AnsiConsole.Write(new Rows(
            table,
            AudioDevicesTable()
        ));

        foreach (var d in devices) d.Dispose();     // before the context is disposed
        return 0;
    }

    /// <summary>
    /// The audio devices for `listen`. Audio is optional here: if MiniAudio can't start or the backend
    /// isn't supported (only WASAPI IDs are resolved so far), the amps are still listed.
    /// </summary>
    private static IRenderable AudioDevicesTable()
    {
        try
        {
            using var cap = new AudioCapture();
            var (playbackDevices, captureDevices) = cap.EnumerateDevices();

            var captureTable = Ui.Table("Name", $"{cap.BackendName()} ID", "Type")
                .Title("Capture/Playback Devices");

            captureTable.Columns[0].Width = NAME_COLUMN_WIDTH;
            captureTable.Columns[1].Width = DETAIL_COLUMN_WIDTH;
            captureTable.Columns[2].Width = MODE_COLUMN_WIDTH;

            foreach (var device in captureDevices) AddCaptureDeviceRow(captureTable, cap, device, "capture");
            foreach (var device in playbackDevices) AddCaptureDeviceRow(captureTable, cap, device, "playback");

            return captureTable;
        }
        catch (Exception e)
        {
            return new Markup($"[yellow]Audio devices unavailable:[/] {Markup.Escape(e.Message)}");
        }
    }
}