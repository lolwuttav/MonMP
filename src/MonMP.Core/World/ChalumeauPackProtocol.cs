using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum ChalumeauPackStatus : byte
    {
        Accepted = 1,
        Unavailable,
        InvalidTarget,
        Stale,
        SaveBlocked,
        TooFar,
        NotWalking,
        HandsBusy,
        Empty,
        UnsupportedFactory,
        SpawnFailed,
        Uncertain
    }

    // One controlled unpack from an ItemState-backed Pack_Chalumeau. The resulting tool and pack
    // stock continue to use SharedItems; this command carries only operation and exact lease data.
    public readonly struct ChalumeauPackRequest
    {
        public const byte Marker = 0xFC;
        public const int Size = 29;
        public readonly Guid Operation;
        public readonly ulong PackEntity;
        public readonly uint Generation;

        public ChalumeauPackRequest(Guid operation, ulong packEntity, uint generation)
        {
            if (operation == Guid.Empty || packEntity <= WorldRecord.DynamicIdStart || generation == 0)
                throw new InvalidDataException("Invalid chalumeau pack request.");
            Operation = operation;
            PackEntity = packEntity;
            Generation = generation;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write(PackEntity);
            writer.Write(Generation);
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public static ChalumeauPackRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid chalumeau pack request size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            return new ChalumeauPackRequest(new Guid(reader.ReadBytes(16)), reader.ReadUInt64(), reader.ReadUInt32());
        }
    }

    public readonly struct ChalumeauPackReply
    {
        public const byte Marker = 0xFD;
        public const int Size = 27;
        public readonly Guid Operation;
        public readonly ChalumeauPackStatus Status;
        public readonly bool Committed;
        public readonly ulong ToolEntity;

        public ChalumeauPackReply(Guid operation, ChalumeauPackStatus status, bool committed = false, ulong toolEntity = 0)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(ChalumeauPackStatus), status) ||
                (committed && (status != ChalumeauPackStatus.Accepted && status != ChalumeauPackStatus.Uncertain)) ||
                (status == ChalumeauPackStatus.Accepted && (!committed || toolEntity <= WorldRecord.DynamicIdStart)) ||
                (!committed && toolEntity != 0))
                throw new InvalidDataException("Invalid chalumeau pack reply.");
            Operation = operation;
            Status = status;
            Committed = committed;
            ToolEntity = toolEntity;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write((byte)Status);
            writer.Write((byte)(Committed ? 1 : 0));
            writer.Write(ToolEntity);
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public static ChalumeauPackReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid chalumeau pack reply size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var operation = new Guid(reader.ReadBytes(16));
            var status = reader.ReadByte();
            var committed = reader.ReadByte();
            if (!Enum.IsDefined(typeof(ChalumeauPackStatus), status) || committed > 1)
                throw new InvalidDataException("Invalid chalumeau pack reply fields.");
            return new ChalumeauPackReply(operation, (ChalumeauPackStatus)status, committed != 0, reader.ReadUInt64());
        }
    }

    public static class ChalumeauPackPolicy
    {
        public const ushort Adapter = 77;
        public static readonly ulong Root = StaticId.Hash("monmp/chalumeau-pack-root/v1") & ~WorldRecord.DynamicIdStart;

        public static WorldRecord RootRecord() => new WorldRecord(Root, 1, Adapter, new byte[] { 1 });

        public static ChalumeauPackStatus Validate(bool synchronized, bool actionsBlocked, bool requestCapacity,
            bool exactTarget, bool exactPack, bool generationMatches, bool ownershipAllowed,
            bool freshPose, bool walking, bool inRange, bool noOtherHeldItem, bool stockAvailable,
            bool exactToolFactory)
        {
            if (!synchronized || !exactTarget || !exactPack) return ChalumeauPackStatus.Unavailable;
            if (!generationMatches) return ChalumeauPackStatus.Stale;
            if (!ownershipAllowed) return ChalumeauPackStatus.InvalidTarget;
            if (actionsBlocked) return ChalumeauPackStatus.SaveBlocked;
            if (!requestCapacity) return ChalumeauPackStatus.Unavailable;
            if (!freshPose || !walking) return ChalumeauPackStatus.NotWalking;
            if (!inRange) return ChalumeauPackStatus.TooFar;
            if (!noOtherHeldItem) return ChalumeauPackStatus.HandsBusy;
            if (!stockAvailable) return ChalumeauPackStatus.Empty;
            if (!exactToolFactory) return ChalumeauPackStatus.UnsupportedFactory;
            return ChalumeauPackStatus.Accepted;
        }
    }
}
