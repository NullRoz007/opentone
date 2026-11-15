using Google.Protobuf;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace OpenTone
{
    /// <summary>
    /// Talks to HID interface 0 of an already-opened device: sends 64-byte reports,
    /// reassembles multi-report messages, and pulls values out of Protobuf replies.
    /// </summary>
    internal sealed class FenderTransport
    {
        const int DefaultTimeoutMs = 1000;

        readonly IUsbDevice device;
        readonly UsbEndpointReader reader;
        readonly UsbEndpointWriter writer;

        /// <summary>How many messages ReadFirstField will inspect before giving up.</summary>
        public int MaxReadAttempts { get; set; } = 100;

        public FenderTransport(IUsbDevice opened)
        {
            device = opened;
            if (!device.ClaimInterface(0)) throw new InvalidOperationException("Error claiming interface: 0");

            writer = device.OpenEndpointWriter(WriteEndpointID.Ep01, EndpointType.Interrupt);
            reader = device.OpenEndpointReader(ReadEndpointID.Ep01, FenderProtocol.ReportSize, EndpointType.Interrupt);
        }

        public void ReleaseInterface() => device.ReleaseInterface(0);

        // ---- raw reports ----

        readonly object writeLock = new();
        readonly object readLock = new();
        readonly object transactionLock = new();
        
        /// <summary>
        /// Thread safe wrapper around Read/Write operations
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="transaction"></param>
        /// <returns></returns>
        public T ExecuteTransaction<T>(Func<T> transaction)
        {
            lock (transactionLock)
            {
                return transaction();
            }
        }

        public void ExecuteTransaction(Action transaction)
        {
            lock (transactionLock)
            {
                transaction();
            }
        }
        public void Write(byte[] buffer64)
        {
            lock (writeLock)
            {
                    Error err = writer.Write(buffer64, DefaultTimeoutMs, out _);
                    if (err != Error.Success) throw new IOException("write: " + err);
            }
        }

        /// <summary>Reads one 64-byte report; returns null on timeout.</summary>
        public byte[]? Read(int timeout = DefaultTimeoutMs)
        {
            byte[] buffer = new byte[FenderProtocol.ReportSize];
            lock(readLock) {
                Error err = reader.Read(buffer, timeout, out _);
                if (err == Error.Timeout) return null;
                if (err != Error.Success) throw new IOException("read: " + err);
                return buffer;
            }
        }

        // ---- messages ----

        /// <summary>Reads one complete message; throws TimeoutException if a report doesn't arrive.</summary>
        public byte[] ReadMessage() => ReadMessage(() => Read() ?? throw new TimeoutException());

        // 00 | type | len | payload (len bytes) | junk padding
        // The report source is a delegate so the deframer can be fed from a capture in tests.
        internal static byte[] ReadMessage(Func<byte[]> readReport)
        {
            var ms = new MemoryStream();

            while (true)
            {
                byte[] report = readReport();

                int idx = 0;
                while (report[idx] == 0) idx++; // 00

                byte type = report[idx];       // type
                int len = report[idx + 1];     // len

                if (type == FenderProtocol.FrameFirst) ms.SetLength(0); // new message, drop any stale data
                ms.Write(report, idx + 2, len); // payload

                if (type == FenderProtocol.FrameLast) return ms.ToArray(); // message done, return data
            }
        }

        public byte[]? ReadFirstSubmessage(FieldTag replyTag, Func<byte[], bool>? accept = null)
        {
            for(int i = 0; i < MaxReadAttempts; i++)
            {
                byte[] message = ReadMessage();
                CodedInputStream inputStream = new CodedInputStream(message);
                
                try {
                    uint tag;
                    while ((tag = inputStream.ReadTag()) != 0)
                    {
                        int field = WireFormat.GetTagFieldNumber(tag);
                        var wire  = WireFormat.GetTagWireType(tag);

                        if (field == (int)replyTag && wire == WireFormat.WireType.LengthDelimited)
                        {
                            byte[] submessage = inputStream.ReadBytes().ToByteArray();
                            bool ok = accept?.Invoke(submessage) ?? true;
                            if (ok) return submessage;
                        }
                        else inputStream.SkipLastField();
                    }
                } catch (InvalidProtocolBufferException)
                {

                } // ignore malformed messages
            }

            return null;
        }

        /// <summary>
        /// Reads messages until one contains <paramref name="replyTag"/>, then returns its value:
        /// a top-level varint directly, or field 1 of a length-delimited submessage.
        /// </summary>
        public T? ReadFirstField<T>(FieldTag replyTag, Func<CodedInputStream, T> readValue)
        {
            for (int i = 0; i < MaxReadAttempts; i++)
            {
                byte[] message = ReadMessage();
                CodedInputStream inputStream = new CodedInputStream(message);

                uint tag;
                while ((tag = inputStream.ReadTag()) != 0)
                {
                    if (WireFormat.GetTagFieldNumber(tag) != (int)replyTag)
                    {
                        inputStream.SkipLastField();
                        continue;
                    }

                    var wireType = WireFormat.GetTagWireType(tag);
                    if (wireType == WireFormat.WireType.Varint)
                    {
                        return readValue(inputStream);
                    }

                    else if (wireType == WireFormat.WireType.LengthDelimited)
                    {
                        CodedInputStream innerStream =
                            new CodedInputStream(inputStream.ReadBytes().ToByteArray());

                        while ((tag = innerStream.ReadTag()) != 0)
                        {
                            if (WireFormat.GetTagFieldNumber(tag) == 1)
                                return readValue(innerStream);

                            innerStream.SkipLastField();
                        }
                    }
                    else
                    {
                        inputStream.SkipLastField();
                    }
                }
            }

            return default;
        }
    }
}
