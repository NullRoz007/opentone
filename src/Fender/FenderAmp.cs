using System.Buffers;
using System.Net.Security;
using System.Text.Json.Nodes;
using Google.Protobuf;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;

namespace OpenTone
{
    /// <summary>
    /// A connected Fender amp. Create with <see cref="Connect"/>, dispose when done.
    /// Owns the USB context, the opened device and the interface claim.
    /// </summary>
    public sealed class FenderAmp : IDisposable
    {
        /// <summary>Preset slots observed in the capture: 1..60 (1-based).</summary>
        public int PresetSlotCount = 60;

        // 35 09 | 08 00 8A 07 04 08 00 10 00 : both directions of the handshake
        static readonly byte[] HelloRequest =
            FenderProtocol.Request(FieldTag.Hello, 0x08, 0x00, 0x10, 0x00);
        
        static readonly byte[] SyncDoneRequest =
            FenderProtocol.Request(FieldTag.Hello, 0x08, 0x01, 0x10, 0x00);

        // What the amp echoes back, as returned by ReadMessage (no frame header, envelope field = 2)
        static readonly byte[] HelloResponse = { 0x08, 0x02, 0x8a, 0x07, 0x04, 0x08, 0x00, 0x10, 0x00 };
        static readonly byte[] SyncResponse = {0x08, 0x02, 0x8A, 0x07, 0x04, 0x08, 0x01, 0x10, 0x00};

        readonly UsbContext context;
        readonly IUsbDevice device;
        readonly FenderTransport transport;
        bool disposed;

        FenderAmp(UsbContext context, IUsbDevice device)
        {
            this.context = context;
            this.device = device;
            transport = new FenderTransport(device);
        }

        /// <summary>Finds, opens and claims the amp. Throws IOException if it can't be found or opened.</summary>
        public static FenderAmp Connect(string deviceName = FenderDevices.LT25)
        {
            var context = new UsbContext();
            IUsbDevice? device = null;
            FenderAmp? amp = null;
            try
            {
                device = FenderDevices.Find(context, deviceName)
                    ?? throw new IOException(
                        $"Failed to open device: {deviceName}");
                amp = new FenderAmp(context, device);
                
                return amp;
            }
            catch
            {
                device?.Close();
                context.Dispose();
                throw;
            }
        }

        public IUsbContext GetUsbContext() => context;
        public IUsbDevice GetUsbDevice() => device;

        /// <summary>How many incoming messages a request will inspect before giving up on its reply.</summary>
        public int MaxReadAttempts
        {
            get => transport.MaxReadAttempts;
            set => transport.MaxReadAttempts = value;
        }

        // ---- session ----

        /// <summary>Sends the hello handshake and waits for the echo. Throws TimeoutException if the amp is silent.</summary>
        public string? Hello()
        {
            return transport.ExecuteTransaction(() =>
            {
                transport.Write(HelloRequest);
                for (int i = 0; i < transport.MaxReadAttempts; i++)
                {
                    byte[] message = transport.ReadMessage();
                    if (message.AsSpan().SequenceEqual(HelloResponse)) return Convert.ToHexString(message);
                }
                
                return null;
            });
        }

        public string? Sync()
        {
            return transport.ExecuteTransaction(() =>
            {
                            
                transport.Write(SyncDoneRequest);
                
                for (int i = 0; i < transport.MaxReadAttempts; i++)
                {
                    byte[] message = transport.ReadMessage();
                    if (message.AsSpan().SequenceEqual(SyncResponse)) return Convert.ToHexString(message);
                }

                return null;
            });
        }

        static readonly byte[] HeartbeatRequest = FenderProtocol.Request(FieldTag.Heartbeat, 0x08, 0x01);
        Timer? heartbeat;

        public void StartHeartbeat() {
            heartbeat ??= new Timer(_ =>
            {
                try { 
                    transport.ExecuteTransaction(() =>
                    {
                        transport.Write(HeartbeatRequest); 
                    });
                }
                catch { /* the session is ending or the device is gone; the next real command reports it */ }
            }, null, 1000, 1000);
        }

        public string? GetFirmwareVersion()
        {
            return transport.ExecuteTransaction(() =>
            {
                transport.Write(FenderProtocol.Request(FieldTag.FirmwareRequest, 0x08, 0x01));
                return transport.ReadFirstField(FieldTag.FirmwareReply, s => s.ReadString());
            });
        }

        public string? GetDeviceName()
        {
            return device.Info.Product;
        }

        // ---- presets ----

        public JsonNode? GetCurrentPreset()
        {
            return transport.ExecuteTransaction(() =>
            {
                transport.Write(FenderProtocol.Request(FieldTag.CurrentPresetRequest, 0x08, 0x01));
                return ReadPresetJson(FieldTag.CurrentPresetReply);
            });   
        }

        /// <summary>Reads the stored preset in <paramref name="slot"/> (1..60).</summary>
        public JsonNode? ReadPreset(int slot)
        {
            if (slot < 1 || slot > PresetSlotCount)
                throw new ArgumentOutOfRangeException(nameof(slot), $"Slot must be 1..{PresetSlotCount}");
            return transport.ExecuteTransaction(() =>
            {
                transport.Write(FenderProtocol.Request(FieldTag.PresetGetRequest, 0x08, (byte)slot));
                return ReadPresetJson(FieldTag.PresetGetReply);
            });
        }


        /// <summary>
        /// Selects a preset on the amp. Returns true if the amp acknowledged the same number.
        /// Starts at 1.
        /// </summary>
        public FenderPreset? SetPreset(int slot)
        {
            if (slot < 0 || slot > byte.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(slot), "Slot must fit in one byte");
            return transport.ExecuteTransaction(() =>
            {
                transport.Write(FenderProtocol.Request(FieldTag.PresetSetRequest, 0x08, (byte)slot));
                uint? result = transport.ReadFirstField(FieldTag.PresetSetReply, s => s.ReadUInt32());
                var current = GetCurrentPreset();
                if(current == null) return null;
                return FenderPreset.FromNode(current);
            });
        }

        public FenderProtocol.ModelChange? SetModel(string nodeId, string fenderId)
        {
            return transport.ExecuteTransaction(() =>
            {
                transport.Write(FenderProtocol.Request(FieldTag.ModelSetRequest, FenderProtocol.ModelChangeBody(nodeId, fenderId)));
                byte[]? reply = transport.ReadFirstSubmessage(FieldTag.ModelSetReply, body =>
                FenderProtocol.ParseModelChange(body) is { } c && c.NodeId == nodeId && c.FenderId == fenderId);

                return reply == null ? null : FenderProtocol.ParseModelChange(reply);
            });
        }

        // ---- Debug hook (field 12), only answered by a firmware built with the OpenTone hook ----
        // Request bytes: [op][0][0][0][u32 address][args...]. Reply bytes: [op][status][0][0][payload].

        public const byte DebugPeek = 1, DebugPoke = 2, DebugCall = 3, DebugContext = 4,
            DebugAlloc = 5, DebugFree = 6, DebugSlotWrite = 7, DebugSlotRead = 8;
        public const int DebugMaxPeek = 496;    // 500-byte reply minus the 4-byte header
        public const int DebugMaxPoke = 41;     // a request must fit in one 61-byte OUT frame
        public const int DebugReplyTimeoutMs = 10000;

        /// <summary>
        /// Sends one debug request and returns the reply payload (after the 4-byte header). Throws if the
        /// firmware has no hook (stock answers with the "unsupported" status message) or reports an error.
        /// Other messages that arrive first (for example ones a called function sends) are listed in
        /// <paramref name="others"/> as field number and value length.
        /// </summary>
        public byte[] DebugRequest(byte op, uint address, byte[] args, List<(int Field, int Bytes)>? others = null)
        {
            // Bytes 2-3 carry a request ID that debug.c echoes, so a late reply to an earlier request (one
            // that timed out here) is not taken for this one. Hooks built before the ID echo leave them 0.
            ushort id = (ushort)Random.Shared.Next(1, 0x10000);
            var value = new byte[8 + args.Length];
            value[0] = op;
            BitConverter.TryWriteBytes(value.AsSpan(2), id);
            BitConverter.TryWriteBytes(value.AsSpan(4), address);
            args.CopyTo(value, 8);

            // Sub-message {1: bytes}: tag 0x0a, length, value.
            byte[] inner = [0x0a, (byte)value.Length, .. value];
            if (inner.Length > 57) throw new ArgumentException("Debug request too long for one frame", nameof(args));

            return transport.ExecuteTransaction(() =>
            {
                transport.Write(FenderProtocol.Request(FieldTag.Debug, inner));
                for (int i = 0; i < transport.MaxReadAttempts; i++)
                {
                    // A CALL can run a slow firmware function, so wait well beyond the usual 1 s per report.
                    var input = new CodedInputStream(transport.ReadMessage(DebugReplyTimeoutMs));
                    uint tag;
                    while ((tag = input.ReadTag()) != 0)
                    {
                        int field = WireFormat.GetTagFieldNumber(tag);
                        if (field == (int)FieldTag.Status)
                            throw new InvalidOperationException(
                                "The amp answered \"unsupported message\": this firmware has no debug hook. Flash an image built with it first.");
                        if (field != (int)FieldTag.Debug || WireFormat.GetTagWireType(tag) != WireFormat.WireType.LengthDelimited)
                        {
                            if (others != null && field != 1 && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
                                others.Add((field, input.ReadBytes().Length));
                            else
                                input.SkipLastField();
                            continue;
                        }

                        var sub = new CodedInputStream(input.ReadBytes().ToByteArray());
                        byte[] reply = sub.ReadTag() == 0x0a ? sub.ReadBytes().ToByteArray() : [];
                        if (reply.Length < 4 || reply[0] != op) continue;
                        ushort echoed = BitConverter.ToUInt16(reply, 2);
                        if (echoed != 0 && echoed != id) continue;          // stale reply to an earlier request
                        if (reply[1] != 0)
                            throw new InvalidOperationException(reply[1] switch
                            {
                                1 => "The hook rejected the request length.",
                                2 => "The hook does not know this operation. Flash a debug hook built from the current hooks/debug.c.",
                                3 => "The operation failed on the amp.",
                                _ => $"The hook returned status {reply[1]}.",
                            });
                        return reply[4..];
                    }
                }
                throw new TimeoutException("No reply to the debug request.");
            });
        }

        /// <summary>Reads <paramref name="length"/> bytes at <paramref name="address"/>. Word accesses when both are 4-aligned.</summary>
        public byte[] Peek(uint address, int length)
        {
            var result = new List<byte>(length);
            while (result.Count < length)
            {
                int n = Math.Min(DebugMaxPeek, length - result.Count);
                result.AddRange(DebugRequest(DebugPeek, address + (uint)result.Count, BitConverter.GetBytes((ushort)n).Concat(new byte[2]).ToArray()));
            }
            return result.ToArray();
        }

        /// <summary>Writes <paramref name="data"/> at <paramref name="address"/>, in chunks that fit one frame.</summary>
        public void Poke(uint address, byte[] data)
        {
            for (int done = 0; done < data.Length; done += DebugMaxPoke)
            {
                var chunk = data.AsSpan(done, Math.Min(DebugMaxPoke, data.Length - done));
                DebugRequest(DebugPoke, address + (uint)done, [.. BitConverter.GetBytes((ushort)chunk.Length), 0, 0, .. chunk]);
            }
        }

        /// <summary>
        /// Calls the firmware function at <paramref name="function"/> with up to 8 arguments (r0-r3, then the
        /// stack). Returns r0 and r1. More than 4 needs a debug hook built with 8-argument CALL.
        /// </summary>
        /// <remarks>Messages the function sends to the host before returning are added to <paramref name="others"/>.</remarks>
        public (uint R0, uint R1) Call(uint function, uint[] args, List<(int Field, int Bytes)>? others = null)
        {
            if (args.Length > 8) throw new ArgumentException("At most 8 arguments", nameof(args));
            var regs = new byte[args.Length > 4 ? 32 : 16];
            for (int i = 0; i < args.Length; i++) BitConverter.TryWriteBytes(regs.AsSpan(4 * i), args[i]);
            byte[] reply = DebugRequest(DebugCall, function, regs, others);
            return (BitConverter.ToUInt32(reply, 0), BitConverter.ToUInt32(reply, 4));
        }

        /// <summary>The MessageCommManager and application object addresses (hooks/firmware.h works on them).</summary>
        public (uint Manager, uint App) Context()
        {
            byte[] reply = DebugRequest(DebugContext, 0, []);
            return (BitConverter.ToUInt32(reply, 0), BitConverter.ToUInt32(reply, 4));
        }

        // ---- Data-flash assets (see hooks/firmware.h, "Assets in the data flash") ----
        // Slots 120-399. Each slot holds one COBS-encoded chunk (the firmware's slot functions are
        // NUL-terminated). The stream is a 16-byte header ("OTA1", u32 length, u32 CRC-32, u32 0) then the data.

        public const int AssetFirstSlot = 120, AssetLastSlot = 399, AssetChunk = 4064;
        const int SlotCapacity = 4096;

        /// <summary>Allocates on the amp's heap (malloc). Throws if it is out of memory.</summary>
        public uint Alloc(uint size)
        {
            uint p = BitConverter.ToUInt32(DebugRequest(DebugAlloc, size, []), 0);
            return p != 0 ? p : throw new InvalidOperationException($"The amp could not allocate {size} bytes.");
        }

        public void Free(uint pointer) => DebugRequest(DebugFree, pointer, []);

        /// <summary>Writes the NUL-free bytes at <paramref name="address"/> on the amp to a data-flash slot.</summary>
        public void SlotWrite(uint slot, uint address, int length) =>
            DebugRequest(DebugSlotWrite, address, [.. BitConverter.GetBytes(slot), .. BitConverter.GetBytes(length)]);

        /// <summary>Reads a data-flash slot into amp RAM at <paramref name="address"/>; returns its length.</summary>
        public int SlotRead(uint slot, uint address, int capacity) =>
            (int)BitConverter.ToUInt32(DebugRequest(DebugSlotRead, address, [.. BitConverter.GetBytes(slot), .. BitConverter.GetBytes(capacity)]), 0);

        /// <summary>The number of slots an asset of <paramref name="length"/> bytes takes.</summary>
        public static int AssetSlots(int length) => (16 + length + AssetChunk - 1) / AssetChunk;

        /// <summary>Stores <paramref name="data"/> as an asset in consecutive slots from <paramref name="slot"/>.</summary>
        public void WriteAsset(int slot, byte[] data, Action<int, int>? progress = null)
        {
            int slots = AssetSlots(data.Length);
            if (slot < AssetFirstSlot || slot + slots - 1 > AssetLastSlot)
                throw new ArgumentOutOfRangeException(nameof(slot), $"An asset of {data.Length} bytes needs {slots} slots; assets use slots {AssetFirstSlot}-{AssetLastSlot}.");

            byte[] stream = [.. "OTA1"u8, .. BitConverter.GetBytes(data.Length), .. BitConverter.GetBytes(Crc32(data)), 0, 0, 0, 0, .. data];
            uint buffer = Alloc(SlotCapacity);
            try
            {
                for (int i = 0; i < slots; i++)
                {
                    byte[] encoded = CobsEncode(stream.AsSpan(i * AssetChunk, Math.Min(AssetChunk, stream.Length - i * AssetChunk)));
                    Poke(buffer, [.. encoded, 0]);
                    SlotWrite((uint)(slot + i), buffer, encoded.Length);
                    progress?.Invoke(i + 1, slots);
                }
            }
            finally { Free(buffer); }
        }

        /// <summary>Reads the asset starting at <paramref name="slot"/>. Throws if it is missing or corrupt.</summary>
        public byte[] ReadAsset(int slot)
        {
            if (slot < AssetFirstSlot || slot > AssetLastSlot)
                throw new ArgumentOutOfRangeException(nameof(slot), $"Assets use slots {AssetFirstSlot}-{AssetLastSlot}.");
            uint buffer = Alloc(SlotCapacity);
            try
            {
                var stream = new List<byte>();
                int total = -1;
                for (int s = slot; s <= AssetLastSlot && (total < 0 || stream.Count < 16 + total); s++)
                {
                    int n = SlotRead((uint)s, buffer, SlotCapacity);
                    if (n == 0) throw new InvalidDataException($"Slot {s} is blank: no asset at {slot}, or it is incomplete.");
                    stream.AddRange(CobsDecode(Peek(buffer, n)));
                    if (total < 0)
                    {
                        if (stream.Count < 16 || !stream.Take(4).SequenceEqual("OTA1"u8.ToArray()))
                            throw new InvalidDataException($"Slot {slot} does not start an asset.");
                        total = BitConverter.ToInt32(stream.ToArray(), 4);
                    }
                }
                byte[] all = stream.ToArray();
                if (all.Length < 16 + total) throw new InvalidDataException("The asset is incomplete.");
                byte[] data = all[16..(16 + total)];
                if (Crc32(data) != BitConverter.ToUInt32(all, 8)) throw new InvalidDataException("The asset's CRC does not match.");
                return data;
            }
            finally { Free(buffer); }
        }

        static byte[] CobsEncode(ReadOnlySpan<byte> data)
        {
            var output = new List<byte>(data.Length + data.Length / 254 + 2);
            int codeAt = output.Count; output.Add(0);
            byte code = 1;
            foreach (byte b in data)
            {
                if (b != 0) { output.Add(b); code++; }
                if (b == 0 || code == 0xff)
                {
                    output[codeAt] = code;
                    codeAt = output.Count; output.Add(0);
                    code = 1;
                }
            }
            output[codeAt] = code;
            return output.ToArray();
        }

        static byte[] CobsDecode(byte[] data)
        {
            var output = new List<byte>(data.Length);
            for (int i = 0; i < data.Length;)
            {
                byte code = data[i++];
                for (int k = 1; k < code && i < data.Length; k++) output.Add(data[i++]);
                if (code != 0xff && i < data.Length) output.Add(0);
            }
            return output.ToArray();
        }

        static uint Crc32(byte[] data)
        {
            uint crc = 0xffffffff;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xedb88320 & (uint)-(int)(crc & 1));
            }
            return ~crc;
        }

        JsonNode? ReadPresetJson(FieldTag replyTag)
        {
            string? json = transport.ReadFirstField(replyTag, s => s.ReadString());
            return json == null ? null : JsonNode.Parse(json);
        }

        /// <summary>
        /// Sets a float parameter, e.g. SetParam("amp", "gain", 0.4f). Returns the value the amp
        /// echoed back, or null if no matching echo arrived.
        /// </summary>
        public FenderProtocol.ParamChange? SetFloat(string nodeId, string paramId, float value) {
            
            return transport.ExecuteTransaction(() =>
            {
                var body = FenderProtocol.ParamBodyFloat(nodeId, paramId, value);
                transport.Write(FenderProtocol.Request(FieldTag.ParamSetRequest, body));

                byte[]? reply = transport.ReadFirstSubmessage(FieldTag.ParamSetReply, body =>
                FenderProtocol.ParseParamChange(body) is { } c && c.Node == nodeId && c.Param == paramId);

                return reply == null ? null : FenderProtocol.ParseParamChange(reply);
            });
        }

         public FenderProtocol.ParamChange? SetBool(string nodeId, string paramId, bool value) {
            return transport.ExecuteTransaction(() =>
            {                              
                var body = FenderProtocol.ParamBodyBool(nodeId, paramId, value);
                transport.Write(FenderProtocol.Request(FieldTag.ParamSetRequest, body));

                byte[]? reply = transport.ReadFirstSubmessage(FieldTag.ParamSetReply, body =>
                    FenderProtocol.ParseParamChange(body) is { } c && c.Node == nodeId && c.Param == paramId);

                return reply == null ? null : FenderProtocol.ParseParamChange(reply);
            });
        }

        /// <summary>Reads a parameter from the amp's current preset. Null if the node or parameter doesn't exist.</summary>
        public JsonNode? GetParam(string nodeId, string paramId)
        {
            JsonNode? root = GetCurrentPreset();
            if (root == null) return null;

            return FenderPreset.FromNode(root).FindNode(nodeId)?.Parameters.GetValueOrDefault(paramId);
        }

        // ---- cleanup ----

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            transport.ReleaseInterface();
            device.Close();
            device.Dispose();    // release the device handle now; a later finalizer would touch the freed context
            context.Dispose();   // must outlive the device, so it goes last
        }
    }
}
