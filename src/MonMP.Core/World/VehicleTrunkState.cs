using System;
using System.IO;

namespace MonMP.Core.World
{
    public readonly struct VehicleTrunkState : IEquatable<VehicleTrunkState>
    {
        public const ushort Adapter = 19;
        public readonly bool Open;
        public readonly float Angle;
        public VehicleTrunkState(bool open, float angle) { Open = open; Angle = angle; Validate(); }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write((byte)(Open ? 1 : 0)); w.Write(Angle); return stream.ToArray();
        }
        public static VehicleTrunkState FromLocalEulerAngles(bool open, float baselineEulerX, float currentEulerX)
        {
            if (!open) return new VehicleTrunkState(false, 0);
            if (!Finite(baselineEulerX) || !Finite(currentEulerX)) throw new InvalidDataException("Invalid native trunk angle.");
            float angle = (currentEulerX - baselineEulerX) % 360f;
            if (!Finite(angle)) throw new InvalidDataException("Invalid native trunk angle.");
            if (angle > 180f) angle -= 360f;
            else if (angle < -180f) angle += 360f;
            return new VehicleTrunkState(true, angle);
        }
        public static VehicleTrunkState Decode(byte[] payload)
        {
            if (payload.Length != 5 || payload[0] > 1) throw new InvalidDataException("Invalid trunk payload.");
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream);
            var state = new VehicleTrunkState(r.ReadByte() == 1, r.ReadSingle()); return state;
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private void Validate()
        {
            if (!ItemPose.Finite(Angle, 180) || (!Open && Math.Abs(Angle) > 0.01f)) throw new InvalidDataException("Invalid trunk angle.");
        }
        public bool Equals(VehicleTrunkState other) => Open == other.Open && Math.Abs(Angle - other.Angle) < 0.01f;
    }
}
