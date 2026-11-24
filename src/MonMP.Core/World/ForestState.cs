using System;
using System.IO;
using System.Text;

namespace MonMP.Core.World
{
    public enum ForestKind : byte { Tree = 1, Trunk = 2, Sapling = 3 }

    [System.Flags]
    public enum ForestFlags : byte { None = 0, Cuttings = 1, Fertilized = 2 }

    // One dynamic record per standing tree, felled trunk or growing sapling in the host's world: its
    // resource prefab name, pose and scale (a sapling's scale is its final grown size), plus a tree's
    // cuttings flag and a sapling's tree type, fertilizer and growth ticks. Exists=false is the tombstone left after a tree is felled or a trunk is
    // split into logs (the logs themselves become shared items).
    public readonly struct ForestState
    {
        public const ushort Adapter = 23;
        // Forest ids live in the upper half of the dynamic range so they never meet item ids.
        public const ulong IdBase = WorldRecord.DynamicIdStart | 0x4000000000000000;
        public const int MaxName = 96;
        public const float MaxCoordinate = 100000;
        public readonly ForestKind Kind;
        public readonly bool Exists;
        public readonly string Prefab;
        public readonly float X, Y, Z, QX, QY, QZ, QW, SX, SY, SZ;
        public readonly ForestFlags Flags;
        public readonly byte TreeType;
        public readonly int Ticks;
        public const int MaxTicks = 1000000;
        public ForestState(ForestKind kind, bool exists, string prefab, float x, float y, float z, float qx, float qy, float qz, float qw, float sx, float sy, float sz, ForestFlags flags = ForestFlags.None, byte treeType = 0, int ticks = 0)
        {
            Kind = kind; Exists = exists; Prefab = prefab; X = x; Y = y; Z = z; QX = qx; QY = qy; QZ = qz; QW = qw; SX = sx; SY = sy; SZ = sz; Flags = flags; TreeType = treeType; Ticks = ticks; Validate();
        }
        public bool Has(ForestFlags flag) => (Flags & flag) != 0;
        public ForestState Removed() => new ForestState(Kind, false, Prefab, X, Y, Z, QX, QY, QZ, QW, SX, SY, SZ, Flags, TreeType, Ticks);
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            var name = Encoding.ASCII.GetBytes(Prefab);
            w.Write((byte)Kind); w.Write(Exists); w.Write((byte)name.Length); w.Write(name);
            foreach (float value in new[] { X, Y, Z, QX, QY, QZ, QW, SX, SY, SZ }) w.Write(value);
            w.Write((byte)Flags); w.Write(TreeType); w.Write(Ticks);
            return stream.ToArray();
        }
        public static ForestState Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 3) throw new InvalidDataException("Invalid forest payload.");
            int length = payload[2];
            if (payload.Length != 3 + length + 46 || payload[1] > 1) throw new InvalidDataException("Invalid forest payload.");
            var f = new float[10];
            for (int i = 0; i < 10; i++) f[i] = BitConverter.ToSingle(payload, 3 + length + i * 4);
            int tail = 3 + length + 40;
            return new ForestState((ForestKind)payload[0], payload[1] == 1, Encoding.ASCII.GetString(payload, 3, length), f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9],
                (ForestFlags)payload[tail], payload[tail + 1], BitConverter.ToInt32(payload, tail + 2));
        }
        private void Validate()
        {
            bool ok = Kind >= ForestKind.Tree && Kind <= ForestKind.Sapling && ((byte)Flags & ~3) == 0 && TreeType <= 8 && Ticks >= 0 && Ticks <= MaxTicks && Prefab != null && Prefab.Length > 0 && Prefab.Length <= MaxName &&
                ItemPose.Finite(X, MaxCoordinate) && ItemPose.Finite(Y, MaxCoordinate) && ItemPose.Finite(Z, MaxCoordinate) &&
                ItemPose.Finite(QX, 1.01f) && ItemPose.Finite(QY, 1.01f) && ItemPose.Finite(QZ, 1.01f) && ItemPose.Finite(QW, 1.01f) &&
                Scale(SX) && Scale(SY) && Scale(SZ);
            if (ok) foreach (char c in Prefab!) if (c < 0x20 || c > 0x7E) { ok = false; break; }
            if (!ok) throw new InvalidDataException("Invalid forest state.");
        }
        private static bool Scale(float value) => ItemPose.Finite(value, 20) && value >= 0.01f;
    }

    public enum ForestOp : byte { Prune = 1, Fertilize = 2, CutSapling = 3, Plant = 4 }

    // A guest's native forestry action besides felling and splitting: pruning loppers taking a tree's
    // cuttings, fertilizing or cutting down a sapling, or planting a cutting (sent to the static
    // forest root record with the tree type, ground pose and exact leased cutting payment).
    // The host echoes an accepted plant request to settle only that player's native feedback.
    public readonly struct ForestAction
    {
        public const byte Marker = 0xC8;
        public const ushort RootAdapter = 26;
        public const int Size = 35;
        public static readonly ulong Root = StaticId.Hash("shared-forest-root/v1") & ~WorldRecord.DynamicIdStart;
        public readonly ForestOp Op;
        public readonly byte TreeType;
        public readonly float X, Y, Z, Yaw;
        public readonly ulong Item;
        public readonly uint LeaseGeneration;
        public readonly uint Request;
        public ForestAction(ForestOp op, byte treeType = 0, float x = 0, float y = 0, float z = 0, float yaw = 0, ulong item = 0, uint leaseGeneration = 0, uint request = 0)
        { Op = op; TreeType = treeType; X = x; Y = y; Z = z; Yaw = yaw; Item = item; LeaseGeneration = leaseGeneration; Request = request; Validate(); }
        public static WorldRecord RootRecord() => new WorldRecord(Root, 1, RootAdapter, new byte[] { 1 });
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(Marker); w.Write((byte)Op); w.Write(TreeType); w.Write(X); w.Write(Y); w.Write(Z); w.Write(Yaw); w.Write(Item); w.Write(LeaseGeneration); w.Write(Request); return stream.ToArray();
        }
        public static ForestAction Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size || payload[0] != Marker) throw new InvalidDataException("Invalid forest action.");
            return new ForestAction((ForestOp)payload[1], payload[2], BitConverter.ToSingle(payload, 3), BitConverter.ToSingle(payload, 7), BitConverter.ToSingle(payload, 11), BitConverter.ToSingle(payload, 15),
                BitConverter.ToUInt64(payload, 19), BitConverter.ToUInt32(payload, 27), BitConverter.ToUInt32(payload, 31));
        }
        private void Validate()
        {
            if (Op < ForestOp.Prune || Op > ForestOp.Plant || TreeType > 8 || !ItemPose.Finite(X, ForestState.MaxCoordinate) || !ItemPose.Finite(Y, ForestState.MaxCoordinate) ||
                !ItemPose.Finite(Z, ForestState.MaxCoordinate) || !ItemPose.Finite(Yaw, 720) ||
                (Op == ForestOp.Plant ? Item <= WorldRecord.DynamicIdStart || LeaseGeneration == 0 || Request == 0 : Item != 0 || LeaseGeneration != 0 || Request != 0)) throw new InvalidDataException("Invalid forest action.");
        }
    }

    // A guest's native chainsaw cut: fell a standing tree (with the player's push direction) or split
    // a felled trunk into logs. The host re-runs the native cut after its own range and land checks.
    public readonly struct ForestCut
    {
        public const byte Marker = 0xC7;
        public readonly ForestKind Kind;
        public readonly float PushX, PushY, PushZ;
        public ForestCut(ForestKind kind, float x, float y, float z) { Kind = kind; PushX = x; PushY = y; PushZ = z; Validate(); }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(Marker); w.Write((byte)Kind); w.Write(PushX); w.Write(PushY); w.Write(PushZ); return stream.ToArray();
        }
        public static ForestCut Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 14 || payload[0] != Marker) throw new InvalidDataException("Invalid forest cut.");
            return new ForestCut((ForestKind)payload[1], BitConverter.ToSingle(payload, 2), BitConverter.ToSingle(payload, 6), BitConverter.ToSingle(payload, 10));
        }
        private void Validate()
        {
            if ((Kind != ForestKind.Tree && Kind != ForestKind.Trunk) || !ItemPose.Finite(PushX, 1.01f) || !ItemPose.Finite(PushY, 1.01f) || !ItemPose.Finite(PushZ, 1.01f))
                throw new InvalidDataException("Invalid forest cut.");
        }
    }
}
