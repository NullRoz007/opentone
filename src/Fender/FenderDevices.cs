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

            var devices = context.List();   // not disposed: disposing the list disposes the devices
            var device = devices.FirstOrDefault(d => d.Info.VendorId == id.Vid && d.Info.ProductId == id.Pid);

            return device != null && device.TryOpen() ? device : null;
        }

        internal static IUsbDevice[]? FindAll(UsbContext context)
        {
            var devices = context.List();   // not disposed: disposing the list disposes the devices
            return devices.Where(d => Known.Values.Any(id => d.Info.VendorId == id.Vid && d.Info.ProductId == id.Pid)).ToArray();
        }
    }
}
