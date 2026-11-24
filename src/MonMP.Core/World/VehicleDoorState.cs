using System;
using System.IO;

namespace MonMP.Core.World
{
    public readonly struct VehicleDoorState
    {
        public const ushort Adapter = 5;
        public readonly bool Open;
        public readonly float Angle;
        public VehicleDoorState(bool open, float angle) { Open = open; Angle = angle; }
        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write((byte)(Open ? 1 : 0)); w.Write(Angle); return stream.ToArray();
        }
        public static VehicleDoorState Decode(byte[] payload)
        {
            if (payload.Length != 5 || payload[0] > 1) throw new InvalidDataException("Invalid door payload.");
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream);
            var state = new VehicleDoorState(r.ReadByte() == 1, r.ReadSingle()); state.Validate(); return state;
        }
        private void Validate()
        {
            if (!ItemPose.Finite(Angle, 90) || (!Open && Math.Abs(Angle) > 0.01f)) throw new InvalidDataException("Invalid door angle.");
        }
    }
}
