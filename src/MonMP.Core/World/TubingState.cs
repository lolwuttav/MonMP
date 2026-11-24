using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum TubingKind : byte { Post = 1, Spout = 2, Tube = 3 }

    // Maple tubing: a placed tubing post or tree spout (ground pose and yaw), or a tube between two
    // connection points (post record ids and point indexes). The shack's vacuum pump post is the fixed
    // endpoint Pump. Removal keeps the record as a tombstone.
    public readonly struct TubingState
    {
        public const ushort Adapter = 28;
        public const ulong IdBase = WorldRecord.DynamicIdStart | 0x0800000000000000;
        public static readonly ulong Pump = StaticId.Hash("tubing-vacuum-pump/v1") & ~WorldRecord.DynamicIdStart;
        public const float MaxCoordinate = 100000;
        public const int MaxPoint = 15;
        public readonly TubingKind Kind;
        public readonly bool Exists;
        public readonly float X, Y, Z, Yaw;
        public readonly ulong Origin, Target;
        public readonly byte OriginPoint, TargetPoint;
        private TubingState(TubingKind kind, bool exists, float x, float y, float z, float yaw, ulong origin, byte originPoint, ulong target, byte targetPoint)
        {
            Kind = kind; Exists = exists; X = x; Y = y; Z = z; Yaw = yaw; Origin = origin; OriginPoint = originPoint; Target = target; TargetPoint = targetPoint; Validate();
        }
        public static TubingState Placed(TubingKind kind, float x, float y, float z, float yaw) => new TubingState(kind, true, x, y, z, yaw, 0, 0, 0, 0);
        public static TubingState Tube(ulong origin, byte originPoint, ulong target, byte targetPoint) => new TubingState(TubingKind.Tube, true, 0, 0, 0, 0, origin, originPoint, target, targetPoint);
        public TubingState Removed() => new TubingState(Kind, false, X, Y, Z, Yaw, Origin, OriginPoint, Target, TargetPoint);
        public static bool Endpoint(ulong id) => id == Pump || id > IdBase;
        // The same tube, whichever end was clicked first.
        public bool Joins(ulong a, int pa, ulong b, int pb) => Kind == TubingKind.Tube &&
            ((Origin == a && OriginPoint == pa && Target == b && TargetPoint == pb) || (Origin == b && OriginPoint == pb && Target == a && TargetPoint == pa));
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write((byte)Kind); w.Write(Exists);
            if (Kind == TubingKind.Tube) { w.Write(Origin); w.Write(OriginPoint); w.Write(Target); w.Write(TargetPoint); }
            else { w.Write(X); w.Write(Y); w.Write(Z); w.Write(Yaw); }
            return stream.ToArray();
        }
        public static TubingState Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 2 || payload[1] > 1) throw new InvalidDataException("Invalid tubing payload.");
            var kind = (TubingKind)payload[0]; bool exists = payload[1] == 1;
            if (kind == TubingKind.Tube)
            {
                if (payload.Length != 20) throw new InvalidDataException("Invalid tubing payload.");
                return new TubingState(kind, exists, 0, 0, 0, 0, BitConverter.ToUInt64(payload, 2), payload[10], BitConverter.ToUInt64(payload, 11), payload[19]);
            }
            if (payload.Length != 18) throw new InvalidDataException("Invalid tubing payload.");
            return new TubingState(kind, exists, BitConverter.ToSingle(payload, 2), BitConverter.ToSingle(payload, 6), BitConverter.ToSingle(payload, 10), BitConverter.ToSingle(payload, 14), 0, 0, 0, 0);
        }
        private void Validate()
        {
            bool ok = Kind switch
            {
                TubingKind.Post or TubingKind.Spout => ItemPose.Finite(X, MaxCoordinate) && ItemPose.Finite(Y, MaxCoordinate) && ItemPose.Finite(Z, MaxCoordinate) && ItemPose.Finite(Yaw, 720) &&
                    Origin == 0 && Target == 0 && OriginPoint == 0 && TargetPoint == 0,
                TubingKind.Tube => Endpoint(Origin) && Endpoint(Target) && (Origin != Target || OriginPoint != TargetPoint) && OriginPoint <= MaxPoint && TargetPoint <= MaxPoint &&
                    X == 0 && Y == 0 && Z == 0 && Yaw == 0,
                _ => false
            };
            if (!ok) throw new InvalidDataException("Invalid tubing state.");
        }
    }

    public enum TubingOp : byte { Place = 1, Connect = 2, Remove = 3 }

    // A guest's tubing action. Place (a post or spout from the held tool item, sent to the root record),
    // Connect (a tube between two connection points, sent to the root record) and Remove (sent to the
    // post or tube record).
    public readonly struct TubingCommand
    {
        public const byte Marker = 0xC3;
        public const ushort RootAdapter = 29;
        public const int Size = 49;
        public static readonly ulong Root = StaticId.Hash("shared-tubing-root/v1") & ~WorldRecord.DynamicIdStart;
        public readonly TubingOp Op;
        public readonly TubingKind Kind;
        public readonly float X, Y, Z, Yaw;
        public readonly ulong Item, Origin, Target;
        public readonly uint LeaseGeneration;
        public readonly byte OriginPoint, TargetPoint;
        private TubingCommand(TubingOp op, TubingKind kind, float x, float y, float z, float yaw, ulong item, ulong origin, byte originPoint, ulong target, byte targetPoint, uint leaseGeneration = 0)
        {
            LeaseGeneration = leaseGeneration; Op = op; Kind = kind; X = x; Y = y; Z = z; Yaw = yaw; Item = item; Origin = origin; OriginPoint = originPoint; Target = target; TargetPoint = targetPoint; Validate();
        }
        public static TubingCommand Place(TubingKind kind, float x, float y, float z, float yaw, ulong item) => new TubingCommand(TubingOp.Place, kind, x, y, z, yaw, item, 0, 0, 0, 0);
        public static TubingCommand Connect(ulong origin, byte originPoint, ulong target, byte targetPoint, ulong item, uint leaseGeneration) => new TubingCommand(TubingOp.Connect, TubingKind.Tube, 0, 0, 0, 0, item, origin, originPoint, target, targetPoint, leaseGeneration);
        public static TubingCommand Remove(ulong item = 0, uint leaseGeneration = 0) => new TubingCommand(TubingOp.Remove, 0, 0, 0, 0, 0, item, 0, 0, 0, 0, leaseGeneration);
        public static WorldRecord RootRecord() => new WorldRecord(Root, 1, RootAdapter, new byte[] { 1 });
        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(Marker); w.Write((byte)Op); w.Write((byte)Kind); w.Write(X); w.Write(Y); w.Write(Z); w.Write(Yaw);
            w.Write(Item); w.Write(Origin); w.Write(OriginPoint); w.Write(Target); w.Write(TargetPoint); w.Write(LeaseGeneration);
            return stream.ToArray();
        }
        public static TubingCommand Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid tubing command.");
            return new TubingCommand((TubingOp)payload[1], (TubingKind)payload[2], BitConverter.ToSingle(payload, 3), BitConverter.ToSingle(payload, 7), BitConverter.ToSingle(payload, 11),
                BitConverter.ToSingle(payload, 15), BitConverter.ToUInt64(payload, 19), BitConverter.ToUInt64(payload, 27), payload[35], BitConverter.ToUInt64(payload, 36), payload[44], BitConverter.ToUInt32(payload, 45));
        }
        private void Validate()
        {
            bool ok = Op switch
            {
                TubingOp.Place => (Kind == TubingKind.Post || Kind == TubingKind.Spout) && ItemPose.Finite(X, TubingState.MaxCoordinate) && ItemPose.Finite(Y, TubingState.MaxCoordinate) &&
                    ItemPose.Finite(Z, TubingState.MaxCoordinate) && ItemPose.Finite(Yaw, 720) && Item != 0 && LeaseGeneration == 0 && Origin == 0 && Target == 0,
                TubingOp.Connect => Kind == TubingKind.Tube && TubingState.Endpoint(Origin) && TubingState.Endpoint(Target) && (Origin != Target || OriginPoint != TargetPoint) &&
                    OriginPoint <= TubingState.MaxPoint && TargetPoint <= TubingState.MaxPoint && Item != 0 && LeaseGeneration != 0 && X == 0 && Y == 0 && Z == 0 && Yaw == 0,
                TubingOp.Remove => Kind == 0 && ((Item == 0 && LeaseGeneration == 0) || (Item != 0 && LeaseGeneration != 0)) && Origin == 0 && Target == 0 && X == 0 && Y == 0 && Z == 0 && Yaw == 0,
                _ => false
            };
            if (!ok) throw new InvalidDataException("Invalid tubing command.");
        }
    }
}
