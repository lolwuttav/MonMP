using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum PurchaseStatus : byte
    {
        Held = 1, Placed, Unavailable, Closed, PriceChanged, TooFar, HandsBusy, Funds,
        NativeRejected, RetryRejected, Capacity, Uncertain
    }
    public sealed class PurchaseResult
    {
        public Guid Operation { get; }
        public PurchaseStatus Status { get; }
        public ulong Item { get; }
        public PurchaseResult(Guid operation, PurchaseStatus status, ulong item = 0)
        {
            bool success = status == PurchaseStatus.Held || status == PurchaseStatus.Placed;
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(PurchaseStatus), status) ||
                (success ? item <= WorldRecord.DynamicIdStart : item != 0)) throw new InvalidDataException("Invalid purchase result.");
            Operation = operation; Status = status; Item = item;
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Operation.ToByteArray()); writer.Write((byte)Status); writer.Write(Item); return stream.ToArray();
        }
        public static PurchaseResult Decode(byte[] payload)
        {
            if (payload.Length != 25) throw new InvalidDataException("Invalid purchase result size.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            return new PurchaseResult(new Guid(reader.ReadBytes(16)), (PurchaseStatus)reader.ReadByte(), reader.ReadUInt64());
        }
    }
}
