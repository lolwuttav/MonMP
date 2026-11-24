using System;
using System.IO;

namespace MonMP.Core.World
{
    public readonly struct VehicleHoodState : IEquatable<VehicleHoodState>
    {
        public const ushort Adapter = 18;
        public readonly bool Released;
        public readonly bool Open;
        public readonly float Angle;
        public VehicleHoodState(bool released, bool open, float angle)
        { Released = released || open; Open = open; Angle = angle; Validate(); }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write((byte)((Released ? 1 : 0) | (Open ? 2 : 0))); w.Write(Angle); return stream.ToArray();
        }
        public static VehicleHoodState FromLocalEulerAngles(bool released, bool open, float baselineEulerX, float currentEulerX)
        {
            if (!open) return new VehicleHoodState(released, false, 0);
            if (!Finite(baselineEulerX) || !Finite(currentEulerX)) throw new InvalidDataException("Invalid native hood angle.");
            float angle = (currentEulerX - baselineEulerX) % 360f;
            if (!Finite(angle)) throw new InvalidDataException("Invalid native hood angle.");
            if (angle > 180f) angle -= 360f;
            else if (angle < -180f) angle += 360f;
            return new VehicleHoodState(true, true, angle);
        }
        public static VehicleHoodState Decode(byte[] payload)
        {
            if (payload.Length != 5 || payload[0] > 3) throw new InvalidDataException("Invalid hood payload.");
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream);
            byte flags = r.ReadByte(); var state = new VehicleHoodState((flags & 1) != 0, (flags & 2) != 0, r.ReadSingle());
            if ((flags & 3) != ((state.Released ? 1 : 0) | (state.Open ? 2 : 0))) throw new InvalidDataException("Invalid hood flags.");
            return state;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private void Validate()
        {
            if (!ItemPose.Finite(Angle, 90) || (!Open && Math.Abs(Angle) > 0.01f)) throw new InvalidDataException("Invalid hood angle.");
        }
        public bool Equals(VehicleHoodState other) => Released == other.Released && Open == other.Open && Math.Abs(Angle - other.Angle) < 0.01f;
    }
}
