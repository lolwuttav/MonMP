using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum PaintMixingStatus : byte
    {
        Accepted = 1,
        NotReady,
        SaveBlocked,
        Closed,
        TooFar,
        InvalidColor,
        NoFunds,
        NoCan,
        HandsBusy,
        Unsupported,
        Capacity,
        Uncertain,
        RetryRejected,
        NativeRejected
    }

    // Adapter 71 carries the host's refill-can display mask for each static paint mixer.
    public readonly struct PaintMixingState
    {
        public const ushort Adapter = 71;
        public const byte Version = 1;
        public const int MaxSlots = 64;
        public const int Size = 10;
        public int SlotCount { get; }
        public ulong ActiveMask { get; }

        public PaintMixingState(int slotCount, ulong activeMask)
        {
            if (slotCount < 0 || slotCount > MaxSlots || (slotCount < MaxSlots && (activeMask >> slotCount) != 0))
                throw new InvalidDataException("Invalid paint-mixing stock state.");
            SlotCount = slotCount;
            ActiveMask = activeMask;
        }

        public bool IsActive(int slot) => slot >= 0 && slot < SlotCount && (ActiveMask & (1UL << slot)) != 0;

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Version);
            writer.Write((byte)SlotCount);
            writer.Write(ActiveMask);
            return stream.ToArray();
        }

        public static PaintMixingState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid paint-mixing state size.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            if (reader.ReadByte() != Version) throw new InvalidDataException("Unsupported paint-mixing state version.");
            return new PaintMixingState(reader.ReadByte(), reader.ReadUInt64());
        }
    }

    public readonly struct PaintMixingRequest
    {
        public const byte Marker = 0xF8;
        public const int Size = 21;
        public Guid Operation { get; }
        public int ColorCode { get; }

        public PaintMixingRequest(Guid operation, int colorCode)
        {
            if (operation == Guid.Empty || !PaintMixingPolicy.IsValidColorCode(colorCode))
                throw new InvalidDataException("Invalid paint-mixing request.");
            Operation = operation;
            ColorCode = colorCode;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write(ColorCode);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public static PaintMixingRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid paint-mixing request size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var request = new PaintMixingRequest(new Guid(reader.ReadBytes(16)), reader.ReadInt32());
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing paint-mixing request data.");
            return request;
        }
    }

    public readonly struct PaintMixingReply
    {
        public const byte Marker = 0xF9;
        public const int Size = 26;
        public Guid Operation { get; }
        public PaintMixingStatus Status { get; }
        public ulong Item { get; }

        public PaintMixingReply(Guid operation, PaintMixingStatus status, ulong item = 0)
        {
            bool success = status == PaintMixingStatus.Accepted;
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(PaintMixingStatus), status) ||
                (success ? item <= WorldRecord.DynamicIdStart : item != 0))
                throw new InvalidDataException("Invalid paint-mixing reply.");
            Operation = operation;
            Status = status;
            Item = item;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write((byte)Status);
            writer.Write(Item);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public static PaintMixingReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid paint-mixing reply size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var operation = new Guid(reader.ReadBytes(16));
            var status = (PaintMixingStatus)reader.ReadByte();
            var item = reader.ReadUInt64();
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Trailing paint-mixing reply data.");
            return new PaintMixingReply(operation, status, item);
        }
    }

    public static class PaintMixingPolicy
    {
        public const float Price = 30f;
        public const int MaxOperations = 16384;

        // Native UI serializes a leading 1 followed by three zero-padded RGB bytes.
        // Each channel occupies three decimal digits, so a range check alone admits values
        // such as 1000256000 (green = 256) that cannot represent an RGB color.
        public static bool IsValidColorCode(int colorCode)
        {
            if (colorCode < 1000000000 || colorCode > 1255255255) return false;
            int rgb = colorCode - 1000000000;
            return rgb / 1000000 <= 255 && (rgb / 1000) % 1000 <= 255 && rgb % 1000 <= 255;
        }

        public static bool TryDecodeRgb(int colorCode, out byte red, out byte green, out byte blue)
        {
            red = green = blue = 0;
            if (!IsValidColorCode(colorCode)) return false;
            int rgb = colorCode - 1000000000;
            int redValue = rgb / 1000000;
            int greenValue = (rgb / 1000) % 1000;
            int blueValue = rgb % 1000;
            if (redValue > 255 || greenValue > 255 || blueValue > 255) return false;
            red = (byte)redValue;
            green = (byte)greenValue;
            blue = (byte)blueValue;
            return true;
        }

        public static PaintMixingStatus Validate(bool worldReady, bool actionsBlocked, bool requestBudget,
            bool exactStation, bool storeOpen, bool freshWalkingPoseAndInRange, int colorCode,
            bool canAfford, bool canAvailable, bool handsFree)
        {
            if (!worldReady) return PaintMixingStatus.NotReady;
            if (actionsBlocked) return PaintMixingStatus.SaveBlocked;
            if (!requestBudget) return PaintMixingStatus.Capacity;
            if (!exactStation) return PaintMixingStatus.Unsupported;
            if (!IsValidColorCode(colorCode)) return PaintMixingStatus.InvalidColor;
            if (!storeOpen) return PaintMixingStatus.Closed;
            if (!freshWalkingPoseAndInRange) return PaintMixingStatus.TooFar;
            if (!handsFree) return PaintMixingStatus.HandsBusy;
            if (!canAfford) return PaintMixingStatus.NoFunds;
            return canAvailable ? PaintMixingStatus.Accepted : PaintMixingStatus.NoCan;
        }
    }
}
