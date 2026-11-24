using System;
using System.IO;

namespace MonMP.Core.World
{
    // Guest use of cottage contents, sent to the cottage purchase entity. The wheelbarrow is driven
    // by one player at a time: Grab claims it, Move carries the driver's barrow pose, Release lets go.
    // Percolator brews from a nearby coffee basket or pours a mug into the guest's hand.
    public enum CottageUseAction : byte { WheelbarrowGrab = 1, WheelbarrowMove, WheelbarrowRelease, Percolator, CoffeeSeeds, Fertilizer }
    public enum CottageUseStatus : byte
    {
        Accepted = 1, Busy, TooFar, NotReady, NotOwned,
        Poured, Brewing, HandsBusy, TooCold, NoBasket, BasketEmpty, NotCoffee, NotEnoughCoffee,
        SeedsCollected, SupplyEmpty, FertilizerStarted, NotEnoughFertilizer
    }

    public readonly struct CottageUseRequest
    {
        public const byte Marker = 0x5C;
        public const int Size = 46;
        public readonly Guid Operation;
        public readonly CottageUseAction Action;
        public readonly ItemPose Pose;

        public CottageUseRequest(Guid operation, CottageUseAction action, ItemPose pose)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(CottageUseAction), action))
                throw new InvalidDataException("Invalid cottage use request.");
            Operation = operation; Action = action; Pose = pose;
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Operation.ToByteArray()); writer.Write((byte)Action); Pose.Write(writer);
            return stream.ToArray();
        }

        public static CottageUseRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid cottage use request size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            reader.ReadByte();
            var operation = new Guid(reader.ReadBytes(16));
            var action = (CottageUseAction)reader.ReadByte();
            return new CottageUseRequest(operation, action, ItemPose.Read(reader));
        }
    }

    public readonly struct CottageUseReply
    {
        public const byte Marker = CottageUseRequest.Marker;
        public const int Size = 19;
        public readonly Guid Operation;
        public readonly CottageUseAction Action;
        public readonly CottageUseStatus Status;

        public CottageUseReply(Guid operation, CottageUseAction action, CottageUseStatus status)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(CottageUseAction), action) || !Enum.IsDefined(typeof(CottageUseStatus), status))
                throw new InvalidDataException("Invalid cottage use reply.");
            Operation = operation; Action = action; Status = status;
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Operation.ToByteArray()); writer.Write((byte)Action); writer.Write((byte)Status);
            return stream.ToArray();
        }

        public static CottageUseReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid cottage use reply size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            reader.ReadByte();
            var operation = new Guid(reader.ReadBytes(16));
            var action = (CottageUseAction)reader.ReadByte();
            return new CottageUseReply(operation, action, (CottageUseStatus)reader.ReadByte());
        }
    }
}
