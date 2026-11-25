using LibUsbDotNet;
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

        public static IReadOnlyDictionary<string, UsbId> Known { get; } =
            new Dictionary<string, UsbId>
            {
                [LT25] = new UsbId(0x1ED8, 0x0037),
                [LT_DFU] = new UsbId(0x1ED8, 0x0033),
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

        internal static IUsbDevice[]? FindAll(UsbContext context)
        {
            // Devices that aren't returned are released now, while the context is alive (see Find).
            // The caller must dispose the returned devices before disposing the context.
            var devices = context.List();
            var known = devices.Where(d => Known.Values.Any(id => d.Info.VendorId == id.Vid && d.Info.ProductId == id.Pid)).ToArray();
            foreach (var other in devices)
                if (!known.Contains(other)) other.Dispose();
            return known;
        }
    }
}
