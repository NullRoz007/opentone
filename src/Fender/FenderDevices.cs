using LibUsbDotNet;
using LibUsbDotNet.Info;
using LibUsbDotNet.LibUsb;

namespace OpenTone
{
    public readonly record struct UsbId(ushort Vid, ushort Pid);

    /// <summary>Registry of supported amps and the lookup that finds one on the bus.</summary>
    public static class FenderDevices
    {
        public const string LT25 = "mustang-lt25";
        public const string LT_DFU = "lt-dfu";
        public const string LT_UPDATER_DEVICE_NAME = "Mustang LT Updater v1.0.0";

        /// <summary>Fender's USB vendor ID.</summary>
        public const ushort FenderVid = 0x1ED8;

        public static IReadOnlyDictionary<string, UsbId> Known { get; } =
            new Dictionary<string, UsbId>
            {
                [LT25] = new UsbId(FenderVid, 0x0037),
                [LT_DFU] = new UsbId(FenderVid, 0x0033),
            };

        /// <summary>
        /// Names of other Fender products by PID, only for labelling unsupported devices in `list`. From the
        /// community table at https://github.com/offa/plug/blob/master/doc/USB.md, not checked against
        /// hardware, and nothing says these speak the LT25 protocol, so they are not in <see cref="Known"/>.
        /// The Mustang LT50 is not in that table; its PID is unknown.
        /// </summary>
        public static IReadOnlyDictionary<ushort, string> OtherFenderProducts { get; } =
            new Dictionary<ushort, string>
            {
                [0x0004] = "Mustang I/II (v1)",
                [0x0005] = "Mustang III/IV/V (v1)",
                [0x000a] = "Mustang Bronco",
                [0x0010] = "Mustang Mini",
                [0x0012] = "Mustang Floor",
                [0x0014] = "Mustang I/II (v2)",
                [0x0016] = "Mustang III/IV/V (v2)",
                [0x0032] = "Mustang GT 40",
                [0x0038] = "Rumble LT 25",
                [0x003a] = "Mustang Micro Plus",
                [0x0046] = "Mustang LT 40S",
            };

        /// <summary>
        /// Finds and opens the first matching device. Returns null if it isn't plugged in
        /// or can't be opened (e.g. interface 0 isn't bound to WinUSB).
        /// The caller must keep <paramref name="context"/> alive for as long as the device is used.
        /// </summary>
        internal static IUsbDevice? Find(UsbContext context, string deviceName)
        {
            if (!Known.TryGetValue(deviceName, out UsbId id))
                throw new ArgumentException(
                    $"Invalid deviceName: {deviceName}. Known: {string.Join(", ", Known.Keys)}",
                    nameof(deviceName));

            // The list itself isn't disposed (that would dispose the chosen device too), but every device we
            // don't keep is released now, while the context is alive. Leaving them to the finalizer crashes
            // (access violation in UnrefDevice) once the context has been disposed.
            var devices = context.List();
            var device = devices.FirstOrDefault(d => d.Info.VendorId == id.Vid && d.Info.ProductId == id.Pid);
            bool opened = device != null && device.TryOpen();

            foreach (var other in devices)
                if (!opened || !ReferenceEquals(other, device)) other.Dispose();

            return opened ? device : null;
        }

        /// <summary>
        /// Every device with Fender's vendor ID, including ones not in <see cref="Known"/>, so `list` can
        /// show the PID of models OpenTone doesn't support yet.
        /// </summary>
        internal static IUsbDevice[]? FindAll(UsbContext context)
        {
            // Devices that aren't returned are released now, while the context is alive (see Find).
            // The caller must dispose the returned devices before disposing the context.
            var devices = context.List();
            var known = devices.Where(d => d.Info.VendorId == FenderVid).ToArray();
            foreach (var other in devices)
                if (!known.Contains(other)) other.Dispose();
            return known;
        }

        public readonly record struct HardwareInfo(ushort Vid, ushort Pid, string? Serial, LocationId Location);
        internal static HardwareInfo? GetHardwareInfo(IUsbDevice device)
        {
            // Info reads the strings through libusb_get_string_descriptor_ascii. GetDescriptor/GetString in
            // LibUsbDotNet 3 throw "Field passed in is not a marshaled member of the type UsbDescriptor".
            string? serial = device.Info.SerialNumber;

            return new(
                device.VendorId,
                device.ProductId,
                string.IsNullOrEmpty(serial) ? null : serial,
                device.LocationId
            );
        }
    }
}
