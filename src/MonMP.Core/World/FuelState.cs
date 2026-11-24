using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum FuelKind : byte { Station = 0, Tank = 1, Cap = 2, Door = 3 }

    // One record per world fuel tank (level in liters), filler cap (screwed 0..1) or fuel door (0/1).
    // Canister tanks travel as item values; driven vehicles also carry their level in VehicleState.
    public readonly struct FuelState : IEquatable<FuelState>
    {
        public const ushort Adapter = 21;
        // Command endpoint for fuel transfers; its own value is unused.
        public static readonly ulong Station = StaticId.Hash("shared-fuel/station-v1");
        public const float MaxLevel = 10000;
        public readonly FuelKind Kind;
        public readonly float Value;
        public FuelState(FuelKind kind, float value) { Kind = kind; Value = value; Validate(); }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write((byte)Kind); w.Write(Value); return stream.ToArray();
        }
        public static FuelState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 5 || payload[0] > (byte)FuelKind.Door) throw new InvalidDataException("Invalid fuel payload.");
            return new FuelState((FuelKind)payload[0], BitConverter.ToSingle(payload, 1));
        }
        private void Validate()
        {
            bool ok = Kind switch
            {
                FuelKind.Station => Value == 0,
                FuelKind.Tank => ItemPose.Finite(Value, MaxLevel) && Value >= 0,
                FuelKind.Cap => ItemPose.Finite(Value, 1) && Value >= 0,
                FuelKind.Door => Value == 0 || Value == 1,
                _ => false
            };
            if (!ok) throw new InvalidDataException("Invalid fuel state.");
        }
        // Native cap topology and fueling eligibility change at exact 0 and 1. A small
        // travel tolerance must never suppress crossing either endpoint.
        public static bool CapTravelMatches(float before, float after, float tolerance) =>
            (before == 0) == (after == 0) && (before == 1) == (after == 1) && Math.Abs(before - after) < tolerance;
        public bool Equals(FuelState other) => Kind == other.Kind &&
            (Kind == FuelKind.Cap ? CapTravelMatches(Value, other.Value, 0.001f) : Math.Abs(Value - other.Value) < 0.001f);
    }

    public enum FuelTool : byte { Canister = 1, Pump = 2 }
    public enum FuelTarget : byte { Tank = 1, Item = 2 }

    // A guest poured fuel natively: Amount liters moved from its tool (a held canister entity or a
    // pump gun static id) into a tank record or a canister item. The host re-applies it against its
    // own source (canister level, storage tank or wallet) and clamps to what was really available.
    public readonly struct FuelTransfer
    {
        public const byte Marker = 0xF7;
        public const float MaxAmount = 25;
        public readonly FuelTool Tool;
        public readonly ulong ToolId;
        public readonly FuelTarget Target;
        public readonly ulong TargetId;
        public readonly float Amount;
        public FuelTransfer(FuelTool tool, ulong toolId, FuelTarget target, ulong targetId, float amount)
        { Tool = tool; ToolId = toolId; Target = target; TargetId = targetId; Amount = amount; Validate(); }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(Marker); w.Write((byte)Tool); w.Write(ToolId); w.Write((byte)Target); w.Write(TargetId); w.Write(Amount); return stream.ToArray();
        }
        public static FuelTransfer Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 23 || payload[0] != Marker) throw new InvalidDataException("Invalid fuel transfer.");
            return new FuelTransfer((FuelTool)payload[1], BitConverter.ToUInt64(payload, 2), (FuelTarget)payload[10], BitConverter.ToUInt64(payload, 11), BitConverter.ToSingle(payload, 19));
        }
        private void Validate()
        {
            if ((Tool != FuelTool.Canister && Tool != FuelTool.Pump) || (Target != FuelTarget.Tank && Target != FuelTarget.Item) ||
                ToolId == 0 || TargetId == 0 || ToolId == TargetId || !ItemPose.Finite(Amount, MaxAmount) || Amount <= 0)
                throw new InvalidDataException("Invalid fuel transfer.");
        }
    }
}
