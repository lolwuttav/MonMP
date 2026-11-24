using System;
using System.IO;

namespace MonMP.Core.World
{
    [Flags]
    public enum GardenFlags : byte { None = 0, Infested = 1, Fertilized = 2, FullyGrown = 4, Ready = 8, Dead = 16 }

    // One dynamic record per ground dirt patch in the host's world: its pose and the plant growing in
    // it (native SeedsType 0-3, grow percent, watering tick, pests, fertilizer, harvest amount).
    // Exists=false is the tombstone left after a patch is shovelled away. The host alone runs the
    // native minute tick; guests display the replicated plant.
    public readonly struct GardenState
    {
        public const ushort Adapter = 24;
        // Patch ids sit in their own quarter of the dynamic range, apart from items and the forest.
        public const ulong IdBase = WorldRecord.DynamicIdStart | 0x2000000000000000;
        public const int Size = 29;
        public readonly bool Exists;
        public readonly byte Seed;
        public readonly GardenFlags Flags;
        public readonly float Grow;
        public readonly int WateredTick;
        public readonly short Amount;
        public readonly float X, Y, Z, Yaw;
        public GardenState(bool exists, byte seed, GardenFlags flags, float grow, int wateredTick, short amount, float x, float y, float z, float yaw)
        {
            Exists = exists; Seed = seed; Flags = flags; Grow = grow; WateredTick = wateredTick; Amount = amount; X = x; Y = y; Z = z; Yaw = yaw; Validate();
        }
        public bool Has(GardenFlags flag) => (Flags & flag) != 0;
        public bool Watered => WateredTick != -1;
        public GardenState Removed() => new GardenState(false, 0, GardenFlags.None, 0, -1, 0, X, Y, Z, Yaw);
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(Exists); w.Write(Seed); w.Write((byte)Flags); w.Write(Grow); w.Write(WateredTick); w.Write(Amount);
            w.Write(X); w.Write(Y); w.Write(Z); w.Write(Yaw);
            return stream.ToArray();
        }
        public static GardenState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size || payload[0] > 1) throw new InvalidDataException("Invalid garden payload.");
            return new GardenState(payload[0] == 1, payload[1], (GardenFlags)payload[2], BitConverter.ToSingle(payload, 3), BitConverter.ToInt32(payload, 7), BitConverter.ToInt16(payload, 11),
                BitConverter.ToSingle(payload, 13), BitConverter.ToSingle(payload, 17), BitConverter.ToSingle(payload, 21), BitConverter.ToSingle(payload, 25));
        }
        private void Validate()
        {
            bool ok = Seed <= 3 && ((byte)Flags & ~0x1F) == 0 && ItemPose.Finite(Grow, 1) && Grow >= 0 && WateredTick >= -1 && WateredTick <= 1000000 && Amount >= 0 && Amount <= 1000 &&
                ItemPose.Finite(X, ForestState.MaxCoordinate) && ItemPose.Finite(Y, ForestState.MaxCoordinate) && ItemPose.Finite(Z, ForestState.MaxCoordinate) && ItemPose.Finite(Yaw, 720);
            if (!ok) throw new InvalidDataException("Invalid garden state.");
        }
    }

    public enum GardenOp : byte { Dig = 1, Remove = 2, Clear = 3, Plant = 4, Water = 5, Fertilize = 6, Desinfect = 7, Harvest = 8, Spray = 9 }

    // A guest's native garden action. Dig targets the static garden root record and carries the new
    // patch's pose; Plant targets a patch and identifies the exact leased seed pack.
    public readonly struct GardenCommand
    {
        public const byte Marker = 0xD5;
        public const ushort RootAdapter = 25;
        public const int Size = 19;
        public const int PlantSize = 15;
        public static readonly ulong Root = StaticId.Hash("shared-garden-root/v1") & ~WorldRecord.DynamicIdStart;
        public readonly GardenOp Op;
        public readonly byte Seed;
        public readonly float X, Y, Z, Yaw;
        public readonly ulong SeedPack;
        public readonly uint SeedGeneration;
        public GardenCommand(GardenOp op, byte seed = 0, float x = 0, float y = 0, float z = 0, float yaw = 0,
            ulong seedPack = 0, uint seedGeneration = 0)
        { Op = op; Seed = seed; X = x; Y = y; Z = z; Yaw = yaw; SeedPack = seedPack; SeedGeneration = seedGeneration; Validate(); }
        public static WorldRecord RootRecord() => new WorldRecord(Root, 1, RootAdapter, new byte[] { 1 });
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(Marker); w.Write((byte)Op); w.Write(Seed);
            if (Op == GardenOp.Plant) { w.Write(SeedPack); w.Write(SeedGeneration); }
            else { w.Write(X); w.Write(Y); w.Write(Z); w.Write(Yaw); }
            return stream.ToArray();
        }
        public static GardenCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 3 || payload[0] != Marker) throw new InvalidDataException("Invalid garden command.");
            if ((GardenOp)payload[1] == GardenOp.Plant)
            {
                if (payload.Length != PlantSize) throw new InvalidDataException("Invalid garden plant command.");
                return new GardenCommand(GardenOp.Plant, payload[2], seedPack: BitConverter.ToUInt64(payload, 3),
                    seedGeneration: BitConverter.ToUInt32(payload, 11));
            }
            if (payload.Length != Size) throw new InvalidDataException("Invalid garden command.");
            return new GardenCommand((GardenOp)payload[1], payload[2], BitConverter.ToSingle(payload, 3), BitConverter.ToSingle(payload, 7), BitConverter.ToSingle(payload, 11), BitConverter.ToSingle(payload, 15));
        }
        private void Validate()
        {
            bool ok = Op >= GardenOp.Dig && Op <= GardenOp.Spray && Seed <= 3 &&
                (Op == GardenOp.Plant ? Seed >= 1 && SeedPack > WorldRecord.DynamicIdStart && SeedGeneration != 0 &&
                    X == 0 && Y == 0 && Z == 0 && Yaw == 0 : SeedPack == 0 && SeedGeneration == 0) &&
                ItemPose.Finite(X, ForestState.MaxCoordinate) && ItemPose.Finite(Y, ForestState.MaxCoordinate) && ItemPose.Finite(Z, ForestState.MaxCoordinate) && ItemPose.Finite(Yaw, 720);
            if (!ok) throw new InvalidDataException("Invalid garden command.");
        }
    }
}
