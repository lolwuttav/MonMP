using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    // Host-authored presentation for the native AI school bus and its Richard actor.
    // This is session-only pose data; it does not encode or mutate the saved bus route.
    public sealed class SchoolBusState : IEquatable<SchoolBusState>
    {
        public const ushort Adapter = 49;
        public const int FixedSize = 59;
        public const int MaxMasterVisuals = 8;
        private const byte VehicleEnabledFlag = 1;
        private const byte AnimatorEnabledFlag = 2;
        private const byte WalkingFlag = 4;
        private const byte DrivingFlag = 8;
        private const byte SittingFlag = 16;
        private const byte KnownFlags = VehicleEnabledFlag | AnimatorEnabledFlag | WalkingFlag | DrivingFlag | SittingFlag;
        private readonly SchoolBusVisualPose[] masterVisuals;

        public readonly byte BusState;
        public readonly bool VehicleEnabled;
        public readonly bool RichardAnimatorEnabled;
        public readonly bool RichardWalking;
        public readonly bool RichardDriving;
        public readonly bool RichardSitting;
        public readonly ItemPose VehiclePose;
        public readonly ItemPose RichardPose;
        // The bus's native lamps (NativeVehicleLighting's mask) and blinker latches: a guest's bus is a
        // Remote replica whose lights module never runs (#177).
        public readonly ushort Lamps;
        public readonly byte LampControls;
        private const int LampBytes = 3;
        public int MasterVisualCount => masterVisuals.Length;
        public SchoolBusVisualPose MasterVisual(int index) => masterVisuals[index];

        public SchoolBusState(byte busState, bool vehicleEnabled, bool richardAnimatorEnabled,
            bool richardWalking, bool richardDriving, bool richardSitting,
            ItemPose vehiclePose, ItemPose richardPose, IEnumerable<SchoolBusVisualPose> masterVisuals,
            ushort lamps = 0, byte lampControls = 0)
        {
            Lamps = lamps; LampControls = lampControls;
            BusState = busState; VehicleEnabled = vehicleEnabled; RichardAnimatorEnabled = richardAnimatorEnabled;
            RichardWalking = richardWalking; RichardDriving = richardDriving; RichardSitting = richardSitting;
            VehiclePose = vehiclePose; RichardPose = richardPose;
            this.masterVisuals = masterVisuals == null ? throw new ArgumentNullException(nameof(masterVisuals)) : new List<SchoolBusVisualPose>(masterVisuals).ToArray();
            Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size(MasterVisualCount));
            using var writer = new BinaryWriter(stream);
            writer.Write(BusState); writer.Write(Flags());
            VehiclePose.Write(writer); RichardPose.Write(writer);
            writer.Write((byte)masterVisuals.Length);
            foreach (var visual in masterVisuals) visual.Write(writer);
            writer.Write(Lamps); writer.Write(LampControls);
            return stream.ToArray();
        }

        public static SchoolBusState Decode(byte[] payload)
        {
            if (payload == null || payload.Length < FixedSize) throw new InvalidDataException("Invalid school bus state payload.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            byte busState = reader.ReadByte(), flags = reader.ReadByte();
            if ((flags & ~KnownFlags) != 0) throw new InvalidDataException("Invalid school bus state flags.");
            var vehicle = ItemPose.Read(reader); var richard = ItemPose.Read(reader);
            int count = reader.ReadByte();
            if (count > MaxMasterVisuals || payload.Length != Size(count)) throw new InvalidDataException("Invalid school bus visual count or payload length.");
            var visuals = new SchoolBusVisualPose[count];
            for (int i = 0; i < visuals.Length; i++) visuals[i] = SchoolBusVisualPose.Read(reader);
            ushort lamps = reader.ReadUInt16(); byte controls = reader.ReadByte();
            if (controls > 7) throw new InvalidDataException("Invalid school bus lamp controls.");
            return new SchoolBusState(busState, (flags & VehicleEnabledFlag) != 0, (flags & AnimatorEnabledFlag) != 0,
                (flags & WalkingFlag) != 0, (flags & DrivingFlag) != 0, (flags & SittingFlag) != 0,
                vehicle, richard, visuals, lamps, controls);
        }

        public static int Size(int visualCount)
        {
            if (visualCount < 0 || visualCount > MaxMasterVisuals) throw new ArgumentOutOfRangeException(nameof(visualCount));
            return FixedSize + visualCount * SchoolBusVisualPose.Size + LampBytes;
        }

        private byte Flags() => (byte)((VehicleEnabled ? VehicleEnabledFlag : 0) | (RichardAnimatorEnabled ? AnimatorEnabledFlag : 0) |
            (RichardWalking ? WalkingFlag : 0) | (RichardDriving ? DrivingFlag : 0) | (RichardSitting ? SittingFlag : 0));

        private void Validate()
        {
            if (BusState > 7 || masterVisuals.Length > MaxMasterVisuals || LampControls > 7)
                throw new InvalidDataException("Invalid school bus state.");
            VehiclePose.Validate(); RichardPose.Validate();
            foreach (var visual in masterVisuals) visual.Validate();
        }

        public bool Equals(SchoolBusState? other)
        {
            if (other == null || BusState != other.BusState || VehicleEnabled != other.VehicleEnabled ||
                RichardAnimatorEnabled != other.RichardAnimatorEnabled || RichardWalking != other.RichardWalking ||
                RichardDriving != other.RichardDriving || RichardSitting != other.RichardSitting ||
                !PoseEquals(VehiclePose, other.VehiclePose) || !PoseEquals(RichardPose, other.RichardPose) ||
                masterVisuals.Length != other.masterVisuals.Length || Lamps != other.Lamps || LampControls != other.LampControls) return false;
            for (int i = 0; i < masterVisuals.Length; i++) if (!masterVisuals[i].Equals(other.masterVisuals[i])) return false;
            return true;
        }
        public override bool Equals(object? obj) => obj is SchoolBusState other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(HashCode.Combine(BusState, VehicleEnabled, RichardAnimatorEnabled,
            RichardWalking, RichardDriving, RichardSitting, VehiclePose.X, RichardPose.X), masterVisuals.Length);
        private static bool PoseEquals(ItemPose left, ItemPose right) => left.X == right.X && left.Y == right.Y && left.Z == right.Z &&
            left.Qx == right.Qx && left.Qy == right.Qy && left.Qz == right.Qz && left.Qw == right.Qw;
    }

    public readonly struct SchoolBusVisualPose : IEquatable<SchoolBusVisualPose>
    {
        public const int Size = 12;
        public readonly float EulerX, EulerY, EulerZ;
        public SchoolBusVisualPose(float eulerX, float eulerY, float eulerZ)
        { EulerX = eulerX; EulerY = eulerY; EulerZ = eulerZ; Validate(); }
        // Unity's euler angles can come back as tiny negatives or just over 360 near the wrap.
        public static SchoolBusVisualPose Normalized(float eulerX, float eulerY, float eulerZ) =>
            new SchoolBusVisualPose(Wrap(eulerX), Wrap(eulerY), Wrap(eulerZ));
        private static float Wrap(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0;
            value %= 360f;
            if (value < 0) value += 360f;
            return value >= 360f ? 0 : value;
        }
        internal void Write(BinaryWriter writer) { Validate(); writer.Write(EulerX); writer.Write(EulerY); writer.Write(EulerZ); }
        internal static SchoolBusVisualPose Read(BinaryReader reader) => new SchoolBusVisualPose(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        internal void Validate()
        {
            if (!Angle(EulerX) || !Angle(EulerY) || !Angle(EulerZ)) throw new InvalidDataException("Invalid school bus visual pose.");
        }
        private static bool Angle(float value) => ItemPose.FiniteValue(value, 360) && value >= 0;
        public bool Equals(SchoolBusVisualPose other) => EulerX == other.EulerX && EulerY == other.EulerY && EulerZ == other.EulerZ;
        public override bool Equals(object? obj) => obj is SchoolBusVisualPose other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(EulerX, EulerY, EulerZ);
    }

    public static class SchoolBusPoseInterpolation
    {
        public static ItemPose Lerp(ItemPose from, ItemPose to, float amount)
        {
            from.Validate(); to.Validate();
            if (float.IsNaN(amount) || float.IsInfinity(amount) || amount < 0 || amount > 1)
                throw new ArgumentOutOfRangeException(nameof(amount));

            float dot = from.Qx * to.Qx + from.Qy * to.Qy + from.Qz * to.Qz + from.Qw * to.Qw;
            float tx = to.Qx, ty = to.Qy, tz = to.Qz, tw = to.Qw;
            if (dot < 0)
            { dot = -dot; tx = -tx; ty = -ty; tz = -tz; tw = -tw; }

            float left, right;
            if (dot > 0.9995f)
            { left = 1 - amount; right = amount; }
            else
            {
                double angle = Math.Acos(Math.Max(-1, Math.Min(1, dot)));
                double sine = Math.Sin(angle);
                left = (float)(Math.Sin((1 - amount) * angle) / sine);
                right = (float)(Math.Sin(amount * angle) / sine);
            }

            float qx = left * from.Qx + right * tx, qy = left * from.Qy + right * ty;
            float qz = left * from.Qz + right * tz, qw = left * from.Qw + right * tw;
            float length = (float)Math.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (length <= 0.000001f) throw new InvalidDataException("Cannot interpolate invalid school bus rotation.");
            return new ItemPose(from.X + (to.X - from.X) * amount, from.Y + (to.Y - from.Y) * amount,
                from.Z + (to.Z - from.Z) * amount, qx / length, qy / length, qz / length, qw / length);
        }

        public static float LerpAngle(float from, float to, float amount)
        {
            if (!Angle(from) || !Angle(to) || float.IsNaN(amount) || float.IsInfinity(amount) || amount < 0 || amount > 1)
                throw new ArgumentOutOfRangeException(nameof(amount));
            float delta = ((to - from + 540) % 360) - 180;
            float result = (from + delta * amount) % 360;
            return result < 0 ? result + 360 : result;
        }

        private static bool Angle(float value) => ItemPose.FiniteValue(value, 360) && value >= 0;
    }
}
