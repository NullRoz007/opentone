using Haukcode.PcapngUtils;
using Haukcode.PcapngUtils.Common;
using LibUsbDotNet.LibUsb;
using Nito.HashAlgorithms;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using OpenTone.Capture;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spectre.Console;
using System.Text.Encodings.Web;


namespace OpenTone
{
    public enum UpdateControl : byte
    {
        Begin = 0x02,
        Data = 0x03,
        End = 0x04,
    };

    /// <summary>
    /// Thrown when a flash fails after Begin was acknowledged. The amp has erased its application
    /// and is left in update mode, so the flash should be run again.
    /// </summary>
    public sealed class FirmwareUpdateInterruptedException : Exception
    {
        public int FrameIndex { get; }
        public int FrameCount { get; }

        public FirmwareUpdateInterruptedException(int frameIndex, int frameCount, Exception inner)
            : base($"Flash interrupted at frame {frameIndex} of {frameCount}: {inner.Message}", inner)
        {
            FrameIndex = frameIndex;
            FrameCount = frameCount;
        }
    }

    internal sealed class FirmwareTransport : UsbTransport
    {
        public FirmwareTransport(IUsbDevice opened) : base(opened) { }
        public UpdateControl ReadFirmwareStatus(int timeout = DefaultTimeoutMs) => (UpdateControl)(Read(timeout) ?? throw new TimeoutException())[2];
        private const int  FrameDataLength = 32;

        private void BeginUpdate(int timeout = DefaultTimeoutMs)
        {
            byte[] report = new byte[reportSize];
            report[0] = (byte)UpdateControl.Begin;
            report[1] = 0x3; // from the capture, not sure what it means
            Write(report, timeout);

            UpdateControl status = ReadFirmwareStatus(timeout);
            if(status != UpdateControl.Begin) throw new Exception($"Invalid Update Control Status. Expected {((int)UpdateControl.Begin).ToString("X")}, found {status.ToString("X")}");
        }

        public struct SendFirmwareCallback
        {
            public int frameIndex;
            public int frameCount;
            public byte[] report;
        }
        public void SendFirmware(byte[] firmware, Action<SendFirmwareCallback>? onProgress = null, int timeout = 30000, int updateRate = 512)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(updateRate);
            if (reportSize != 64) throw new InvalidOperationException("reportSize != 64");
            if (!FenderFirmware.ValidateFirmware(firmware, out uint expected, out uint calculated))
                throw new InvalidDataException($"Image CRC mismatch: calculated {calculated:X8}, expected {expected:X8}");
            if (firmware.Length == 0 || firmware.Length % FrameDataLength != 0)
                throw new InvalidDataException($"Image length {firmware.Length} is not a multiple of {FrameDataLength}");
            
            BeginUpdate(timeout);

            int frameCount = firmware.Length / FrameDataLength;
            int frameIndex = 0;
            try
            {
                SendFrames(firmware, frameCount, ref frameIndex, onProgress, timeout, updateRate);
            }
            catch (Exception e)
            {
                throw new FirmwareUpdateInterruptedException(frameIndex, frameCount, e);
            }
        }

        private void SendFrames(byte[] firmware, int frameCount, ref int frameIndex, Action<SendFirmwareCallback>? onProgress, int timeout, int updateRate)
        {
            byte[] report = new byte[reportSize];
            void Progress(int i, int c, byte[] r) {
                try {
                    onProgress?.Invoke(new SendFirmwareCallback
                    {
                        frameIndex = i,
                        frameCount = c,
                        report = r
                    });
                } catch { /** Specifically supress errors throw when invoking the callback. We don't want to abort a flash in progress. */ }
            }
            
            for(int i = 0; i < frameCount; i++)
            {
                frameIndex = i;
                Array.Clear(report);
                report[0] = (byte)UpdateControl.Data;
                report[1] = 0x03;
                report[2] = (byte)(i & 0xFF);
                report[3] = FrameDataLength;
                Buffer.BlockCopy(firmware, i * FrameDataLength, report, 4, FrameDataLength);
                Write(report, timeout);
                UpdateControl _status = ReadFirmwareStatus(timeout);
                if(_status != UpdateControl.Data) throw new Exception($"Invalid Update Control Status. Expected {((int)UpdateControl.Data).ToString("X")}, found {_status.ToString("X")}");
                if (i % updateRate == 0) Progress(i, frameCount, report);
            }

            frameIndex = frameCount;
            Array.Clear(report);
            report[0] = (byte)UpdateControl.End;
            report[1] = 0x03;
            Write(report, timeout);

            UpdateControl status = ReadFirmwareStatus(timeout);
            if(status != UpdateControl.End) throw new Exception($"Invalid Update Control Status. Expected {((int)UpdateControl.End).ToString("X")}, found {status.ToString("X")}");
            Progress(frameCount, frameCount, report);
        }
    }

    public class FenderFirmware
    {
        // Minimum size of a packet that is known to be part of a firmware upgrade transaction
        private static readonly int MIN_FIRMWARE_PACKET_SIZE = 4;
        // Minimum packet header size
        private static readonly int  MIN_HEADER_LENGTH = 27;

        private static PcapInfo ParseInfo(byte info) => (PcapInfo)(info & 1);
        private static PcapTransfer ParseTransfer(byte transfer) => (PcapTransfer)(transfer);
        private static PcapEndpoint ParseEndpoint(byte endpoint) => new PcapEndpoint {
            direction = (endpoint & 0x80) != 0 ? PcapDirection.In : PcapDirection.Out,
            endpoint = (byte)(endpoint & 0x7F)
        };

        private static PcapPacket? ParsePacket(IPacket packet)
        {
            if(packet.Data.Length <  MIN_HEADER_LENGTH) return null;
            var data = packet.Data.AsSpan();
            var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(data);
            if (headerLength < MIN_HEADER_LENGTH || headerLength > packet.Data.Length) return null;

            var transfer = ParseTransfer(data[22]);
            return new PcapPacket
            {
                headerLength    = headerLength,
                bus             = BinaryPrimitives.ReadUInt16LittleEndian(data[17..]),
                device          = BinaryPrimitives.ReadUInt16LittleEndian(data[19..]),
                function        = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]),
                
                irpId           = BinaryPrimitives.ReadUInt64LittleEndian(data[2..]),
                dataLength      = BinaryPrimitives.ReadUInt32LittleEndian(data[23..]),
                status          = BinaryPrimitives.ReadUInt32LittleEndian(data[10..]),
            
                info            = ParseInfo(data[16]),
                endpoint        = ParseEndpoint(data[21]),
                transfer        = transfer,
                stage           = (transfer == PcapTransfer.Control && headerLength >= 28) ? data[27] : null,
                payload         = data[headerLength..].ToArray()
            };
        }


        private static PcapDeviceDescriptor? ParseDeviceDescriptor(PcapPacket p)
        {
            if (p.transfer != PcapTransfer.Control) return null;
            if (p.info != PcapInfo.Completion) return null;

            byte[] d = p.payload;
            if (d.Length != 18 || d[0] != 18 || d[1] != 1) return null;   // bLength=18, type=DEVICE

            return new PcapDeviceDescriptor {
                VID = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(8)),
                PID = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(10))
            };
        }

        private static byte[] ExtractFirmware(IEnumerable<PcapPacket> packets)
        {
            var stream = new MemoryStream();
            bool begun = false, ended = false;
            int nextSequence = -1;
            
            foreach(var packet in packets)
            {
                if (packet.transfer != PcapTransfer.Interrupt)          continue; 
                if (packet.endpoint.direction != PcapDirection.Out)     continue;
                if (packet.payload.Length < MIN_FIRMWARE_PACKET_SIZE)   continue;

                byte command    = packet.payload[0];
                byte sequence   = packet.payload[2];
                byte length     = packet.payload[3];

                switch(command)
                {
                    case (byte)UpdateControl.Begin:
                        AnsiConsole.WriteLine("Got UpdateControl.Begin");
                        begun = true;
                        break;
                    case (byte)UpdateControl.Data:
                        if(!begun) 
                            throw new Exception("Got \"Data\" Command Before \"Begin\"");
                        if(length > packet.payload.Length - MIN_FIRMWARE_PACKET_SIZE) 
                            throw new Exception("Malformed frame. Payload length larger than expected.");
                        if(nextSequence != -1 && nextSequence != sequence) 
                            throw new Exception("A frame was dropped during capture, or the capture contains duplicate frames.");
                        
                        nextSequence = (sequence + 1) & 0xFF;
                        stream.Write(packet.payload, 4, length);
                        break;
                    case (byte)UpdateControl.End:
                        if(begun) ended = true;
                        else throw new Exception("Got \"End\" Command Before \"Begin\"");

                        AnsiConsole.WriteLine("Got UpdateControl.End");
                        break;
                    default:
                        break;
                }
            }

            if(!begun || !ended) throw new Exception("Capture does not contain a valid Fender LT Update");
            return stream.ToArray();
        }

        public enum ValidateFirmwareStatus
        {
            Reading,
            Hashing,
            Complete
        }

        public struct ValidateFirmwareCallback {
            public ValidateFirmwareStatus status;
            public int readIndex;
            public int readLength;
        }

        public static bool ValidateFirmware(byte[] firmware, out uint expected, out uint crc, Action<ValidateFirmwareCallback>? onProgress = null)
        {
            var StmCrc32 = new CRC32(CRC32.Definition.Mpeg2);

            int length = firmware.Length;
            var body = firmware.AsSpan(0, length - 4).ToArray();

            expected = BinaryPrimitives.ReadUInt32LittleEndian(firmware.AsSpan(length - 4));

            // StmCrc32 calculates on reversed 4 byte groups (32-bit words)
            // MemoryMarshal.Cast allows us to view the memory of 'body' as 32-bit words (uint's) 
            // we can reverse the 4 byte groups easily by reversing the endianness of the 32-bit words
            // then when we compute the hash, the 4 byte groups in body are reversed

            Span<uint> words = MemoryMarshal.Cast<byte, uint>(body.AsSpan());
            for (int i = 0; i < words.Length; i++) {
                words[i] = BinaryPrimitives.ReverseEndianness(words[i]);
                onProgress?.Invoke(new ValidateFirmwareCallback {
                    status = ValidateFirmwareStatus.Reading,
                    readIndex = i,
                    readLength = words.Length
                });
            }

            onProgress?.Invoke(new ValidateFirmwareCallback {
                status = ValidateFirmwareStatus.Hashing
            });

            byte[] hash = StmCrc32.ComputeHash(body);
            crc = BinaryPrimitives.ReadUInt32LittleEndian(hash);

            onProgress?.Invoke(new ValidateFirmwareCallback {
                status = ValidateFirmwareStatus.Complete
            });

            return crc == expected;
        }

        public record JsonString(int Offset, int Length, string Value);

        public static List<JsonString> FindJsonStrings(
            byte[] data,
            int minLength = 4)
        {
            var results = new List<JsonString>();
            var current = new StringBuilder();
            int startOffset = 0;

            void TryParse()
            {
                if (current.Length < minLength)
                    return;

                string value = current.ToString();

                try
                {
                    using var document = JsonDocument.Parse(value);

                    if (document.RootElement.ValueKind is
                        JsonValueKind.Object or JsonValueKind.Array)
                    {
                        results.Add(new JsonString(
                            startOffset,
                            value.Length,
                            value
                        ));
                    }
                }
                catch (JsonException)
                {
                    // Not valid JSON.
                }
            }

            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];

                if (b >= 0x20 && b <= 0x7E)
                {
                    if (current.Length == 0)
                        startOffset = i;

                    current.Append((char)b);
                }
                else
                {
                    TryParse();
                    current.Clear();
                }
            }

            TryParse();

            return results;
        }

        public static FenderFirmware CreateFromCapture(FenderAmp amp, string path) => CreateFromCapture(path);

        /// <summary>Extracts the firmware image from a capture of an official update. No amp needs to be connected.</summary>
        public static FenderFirmware CreateFromCapture(string path)
        {
            ushort vendorId = FenderDevices.Known[FenderDevices.LT25].Vid;
            var addresses = new HashSet<(ushort bus, ushort device)>();
            var pids = FenderDevices.Known.Values.Where(id => id.Vid == vendorId).Select(id => id.Pid).ToHashSet();
            List<PcapPacket> packets = new();
            using var reader = IReaderFactory.GetReader(path); // <-- here

            while(reader.MoreAvailable)
            {   
                IPacket p = reader.ReadNextPacket();
                if(p == null) break;
                
                var pkt = ParsePacket(p);
                if(pkt == null) continue;
                PcapPacket packet = pkt.Value;

                var address = (packet.bus, packet.device);
                var pcd = ParseDeviceDescriptor(packet);
                if(pcd != null) { // track the device address as we read packets
                    PcapDeviceDescriptor pcapDevice = pcd.Value;
                    if(pcapDevice.VID == vendorId && pids.Contains(pcapDevice.PID)) addresses.Add(address);
                    else addresses.Remove(address);
                }

                if (!addresses.Contains(address)) continue;                 // not the amp
                if (packet.transfer != PcapTransfer.Interrupt) continue;    // HID reports only
                if (packet.payload.Length == 0) continue;                   // empty submit/completion
                if(packet.endpoint.direction == PcapDirection.Out) packets.Add(packet);
            }
            
            var firmware = ExtractFirmware(packets);
            return new FenderFirmware(firmware);
        }

        // instance members
        public uint CheckSumCalculated;
        public uint CheckSumExpected;
        public int Size;
        public byte[] Payload;
        public FenderFirmware(byte[] payload)
        {   
            uint crcCalculated = 0;
            uint crcExpected = 0;

            var valid = ValidateFirmware(payload, out crcExpected, out crcCalculated);
            if(!valid) throw new Exception($"Invalid firmware. Calculated CRC ({crcCalculated}) != Expected CRC ({crcExpected})");

            CheckSumCalculated = crcCalculated;
            CheckSumExpected = crcExpected;
            Size = payload.Length;
            Payload = payload;
        }

        public void Save(string path)
        {
            File.WriteAllBytes(path, Payload);
        }

        // ---- Guarded patching. All edits go through FirmwarePatcher, which enforces the firmware's
        // constraints (16-character preset names, 5-character version, in-place JSON edits) and
        // re-validates the whole image before returning it.

        /// <summary>Replaces the version string. Both versions must be exactly 5 characters.</summary>
        public byte[] PatchVersion(string oldString, string newString)
        {
            var patcher = new FirmwarePatcher(Payload);
            if (patcher.Version != oldString)
                throw new ArgumentException($"Current version is '{patcher.Version}', not '{oldString}'.");
            patcher.SetVersion(newString);
            return patcher.Build();
        }

        /// <summary>
        /// Changes one value of a preset or model found by <see cref="FindJsonStrings"/>.
        /// <paramref name="target"/> is "info" (only displayName is supported) or a node id
        /// (stomp, mod, amp, delay, reverb); for a model it is ignored and the default is changed.
        /// </summary>
        public byte[] PatchParam(JsonString preset, string target, string paramId, string value)
        {
            var patcher = new FirmwarePatcher(Payload);
            var entry = patcher.GetEntryAt(preset.Offset);

            if (entry.Kind == FirmwareJsonKind.Model)
                patcher.SetModelDefault(entry, paramId, value);
            else if (target == "info" && paramId == "displayName")
                patcher.SetPresetName(entry, value);
            else if (target == "info")
                throw new NotSupportedException($"Only info.displayName can be changed, not info.{paramId}.");
            else
                patcher.SetPresetParam(entry, target, paramId, value);

            return patcher.Build();
        }

        /// <summary>Renames a preset. The name must be exactly 16 characters of A-Z, 0-9 and space.</summary>
        public byte[] PatchDisplayName(JsonString preset, string newDisplayName)
        {
            var patcher = new FirmwarePatcher(Payload);
            patcher.SetPresetName(patcher.GetEntryAt(preset.Offset), newDisplayName);
            return patcher.Build();
        }

        public byte[] ReplaceString(string oldString, string newString)
        {
            byte[] oldBytes = Encoding.ASCII.GetBytes(oldString);
            byte[] newBytes = Encoding.ASCII.GetBytes(newString);

            if (newBytes.Length > oldBytes.Length)
                throw new ArgumentException("New string must not be longer than the old one");

            var firmware = Payload.AsSpan(0, Size - 4);
            int index = firmware.IndexOf(oldBytes);
            if(index == -1 || index != firmware.LastIndexOf(oldBytes)) throw new Exception("Target string must occur exactly once");

            byte[] patched = (byte[])Payload.Clone();
            Array.Clear(patched, index, oldBytes.Length); //NUL pad incase the new string is shorter
            newBytes.CopyTo(patched, index);
            
            ValidateFirmware(patched, out _, out uint crc); //checksum stored in patched is stale so this fails, but we need the value of crc
            BinaryPrimitives.WriteUInt32LittleEndian(patched.AsSpan(patched.Length - 4), crc);

            if (!ValidateFirmware(patched, out _, out _))
                throw new InvalidOperationException("Patched image failed CRC validation");
            return patched;
        }
    }
}