using System.Diagnostics.Tracing;
using System.IO.Pipelines;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.VisualBasic;

namespace OpenTone
{
    /// <summary>
    /// Protobuf field numbers of the message-type field inside the envelope,
    /// as observed in the Fender Tone captures.
    /// </summary>
    public enum FieldTag
    {
        Hello = 113,

        FirmwareRequest = 102,
        FirmwareReply = 103,

        PresetGetRequest = 105,
        PresetGetReply = 31,

        CurrentPresetRequest = 104,
        CurrentPresetReply = 32,

        PresetSetRequest = 33,
        PresetSetReply = 37,

        ParamSetRequest = 34,
        ParamSetReply = 35,

        Heartbeat = 201,

        ModelSetRequest   = 39,
        ModelSetReply = 40,
    }

    /// <summary>Wire-level constants and builders. No device access in here.</summary>
    public static class FenderProtocol
    {
        public const int ReportSize = 64;

        // Frame type byte (second byte of an IN report, first byte of an OUT report)
        public const byte FrameFirst = 0x33;   // first report of a multi-report message
        public const byte FrameMiddle = 0x34;  // continuation report
        public const byte FrameLast = 0x35;    // last report (or a message that fits in one)

        /// <summary>Builds a single 64-byte OUT report: envelope + tag + length + inner bytes.</summary>
        public static byte[] Request(FieldTag field, params byte[] inner)
        {
            var payload = new List<byte> { 0x08, 0x00 };
            payload.AddRange(Varint((uint)((int)field << 3) | 2));
            payload.Add((byte)inner.Length);
            payload.AddRange(inner);
            return BuildReport(payload.ToArray());
        }

        public static byte[] BuildReport(params byte[] payload)
        {
            var report = new byte[ReportSize];
            report[0] = FrameLast;               // frame type: last/only report
            report[1] = (byte)payload.Length;
            payload.CopyTo(report, 2);
            return report;
        }

        public readonly record struct ParamChange(string? Node, string? Param, object Value);

        public static byte[] ParamBodyFloat(string node, string param, float value)
        {
            MemoryStream memoryStream = new MemoryStream();
            CodedOutputStream outputStream = new CodedOutputStream(memoryStream);
            outputStream.WriteTag(1, WireFormat.WireType.LengthDelimited); outputStream.WriteString(node);
            outputStream.WriteTag(2, WireFormat.WireType.LengthDelimited); outputStream.WriteString(param);
            outputStream.WriteTag(3, WireFormat.WireType.Fixed32); outputStream.WriteFloat(value);
            outputStream.Flush();

            return memoryStream.ToArray();
        }

        public static byte[] ParamBodyBool(string node, string param, float value)
        {
            MemoryStream memoryStream = new MemoryStream();
            CodedOutputStream outputStream = new CodedOutputStream(memoryStream);
            outputStream.WriteTag(1, WireFormat.WireType.LengthDelimited); outputStream.WriteString(node);
            outputStream.WriteTag(2, WireFormat.WireType.LengthDelimited); outputStream.WriteString(param);
            outputStream.WriteTag(3, WireFormat.WireType.Fixed32); outputStream.WriteFloat(value);
            outputStream.Flush();

            return memoryStream.ToArray();
        }

        public static byte[] ParamBodyBool(string node, string param, bool value)
        {
            MemoryStream memoryStream = new MemoryStream();
            CodedOutputStream outputStream = new CodedOutputStream(memoryStream);
            outputStream.WriteTag(1, WireFormat.WireType.LengthDelimited); outputStream.WriteString(node);
            outputStream.WriteTag(2, WireFormat.WireType.LengthDelimited); outputStream.WriteString(param);
            outputStream.WriteTag(6, WireFormat.WireType.Varint); 
            outputStream.WriteBool(value);
            outputStream.Flush();

            return memoryStream.ToArray();
        }

        public static ParamChange? ParseParamChange(byte[] body)
        {
            var inputStream = new CodedInputStream(body);
            string? node = null, param = null;
            object? value = null;

            uint tag;
            while ((tag = inputStream.ReadTag()) != 0)
            {
                int fieldNumber = WireFormat.GetTagFieldNumber(tag);
                var wireType = WireFormat.GetTagWireType(tag);

                if (fieldNumber == 1 && wireType == WireFormat.WireType.LengthDelimited)
                    node = inputStream.ReadString();
                else if (fieldNumber == 2 && wireType == WireFormat.WireType.LengthDelimited)
                    param = inputStream.ReadString();
                else if (fieldNumber == 3 && wireType == WireFormat.WireType.Fixed32)
                    value = inputStream.ReadFloat();
                else if (fieldNumber == 6 && wireType == WireFormat.WireType.Varint)
                    value = inputStream.ReadUInt64() != 0;
                else
                    inputStream.SkipLastField();
            }

            return node != null && param != null && value != null
                ? new ParamChange(node, param, value)
                : null;
        }

        public readonly record struct ModelChange(string? NodeId, string? FenderId);

        public static byte[] ModelChangeBody(string nodeId, string fenderId)
        {
            MemoryStream memoryStream = new MemoryStream();
            CodedOutputStream outputStream = new CodedOutputStream(memoryStream);
            outputStream.WriteTag(1, WireFormat.WireType.LengthDelimited); outputStream.WriteString(nodeId);
            outputStream.WriteTag(2, WireFormat.WireType.LengthDelimited); outputStream.WriteString(fenderId);
            outputStream.Flush();

            return memoryStream.ToArray();
        }

        public static ModelChange? ParseModelChange(byte[] body)
        {
            var inputStream = new CodedInputStream(body);
            string? nodeId = null, fenderId = null;

            uint tag;
            while ((tag = inputStream.ReadTag()) != 0)
            {
                int fieldNumber = WireFormat.GetTagFieldNumber(tag);
                var wireType = WireFormat.GetTagWireType(tag);

                if (fieldNumber == 1 && wireType == WireFormat.WireType.LengthDelimited)
                    nodeId = inputStream.ReadString();
                else if (fieldNumber == 2 && wireType == WireFormat.WireType.LengthDelimited)
                    fenderId = inputStream.ReadString();
                else
                    inputStream.SkipLastField();
            }

            return nodeId != null && fenderId != null
                ? new ModelChange(nodeId, fenderId)
                : null;
        }

        public static IEnumerable<byte> Varint(uint v)
        {
            while (v >= 0x80)
            {
                yield return (byte)(v | 0x80);   // low 7 bits, continuation bit set
                v >>= 7;
            }
            yield return (byte)v;                // final byte, top bit clear
        }
    }
}
