namespace OpenTone
{
    namespace Capture
    {
        public enum PcapInfo : byte
        {
            Request = 0,
            Completion = 1
        }

        public enum PcapDirection : byte
        {
            In = 1,
            Out = 0,
        }

        public enum PcapTransfer : byte
        {
            Isochronous,
            Interrupt,
            Control,
            Bulk
        }

        public struct PcapEndpoint
        {
            public PcapDirection direction;
            public byte endpoint;  
        }

        public struct PcapPacket
        {
            public ushort headerLength;    
            public ushort bus;              
            public ushort device;         
            public ushort function;
            
            public ulong irpId;
            public uint dataLength;
            public uint status;
            
            public PcapInfo info;
            public PcapEndpoint endpoint;
            public PcapTransfer transfer;

            public byte? stage;
            public byte[] payload; 
        }

        public struct PcapDeviceDescriptor
        {
            public ushort VID;
            public ushort PID;
        }
    }
}
