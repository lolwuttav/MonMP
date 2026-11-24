using System;
using System.IO;

namespace MonMP.Core.World
{
    // A disconnect must acknowledge the current driver's exact anchor request. A
    // local Dropped snapshot alone can be stale while a weighed-anchor delta travels.
    public readonly struct BoatDisconnectAnchorCommand
    {
        public const byte Marker = 0xD5;
        public const int Size = 9, ReplySize = 10;
        public readonly uint Operation, DriverGeneration;
        public BoatDisconnectAnchorCommand(uint operation, uint driverGeneration)
        {
            if (operation == 0 || driverGeneration == 0) throw new InvalidDataException("Invalid boat disconnect anchor identity.");
            Operation = operation; DriverGeneration = driverGeneration;
        }
        public static bool HasMarker(byte[]? payload) => payload != null && payload.Length != 0 && payload[0] == Marker;
        public byte[] Encode()
        {
            var bytes = new byte[Size]; bytes[0] = Marker;
            Array.Copy(BitConverter.GetBytes(Operation), 0, bytes, 1, 4);
            Array.Copy(BitConverter.GetBytes(DriverGeneration), 0, bytes, 5, 4);
            return bytes;
        }
        public static BoatDisconnectAnchorCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size || !HasMarker(payload)) throw new InvalidDataException("Invalid boat disconnect anchor request.");
            return new BoatDisconnectAnchorCommand(BitConverter.ToUInt32(payload, 1), BitConverter.ToUInt32(payload, 5));
        }
        public byte[] Reply(bool dropped)
        {
            var bytes = new byte[ReplySize]; Array.Copy(Encode(), bytes, Size); bytes[Size] = dropped ? (byte)1 : (byte)0;
            return bytes;
        }
        public static BoatDisconnectAnchorCommand DecodeReply(byte[] payload, out bool dropped)
        {
            if (payload == null || payload.Length != ReplySize || payload[Size] > 1) throw new InvalidDataException("Invalid boat disconnect anchor reply.");
            var command = new byte[Size]; Array.Copy(payload, command, Size);
            dropped = payload[Size] != 0;
            return Decode(command);
        }
    }
}
