using System.IO;

namespace MonMP.Core.World
{
    // Actual local poses preserve interrupted travel and manual crank rotation.
    public readonly struct VehicleWindowState
    {
        public const ushort Adapter = 14;
        public readonly bool TargetDown, Closed, MotorPlaying;
        public readonly ItemPose Window, Lever;
        public VehicleWindowState(bool targetDown, bool closed, ItemPose window, ItemPose lever, bool motorPlaying = false)
        { TargetDown = targetDown; Closed = closed; Window = window; Lever = lever; MotorPlaying = motorPlaying; }
        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(TargetDown); writer.Write(Closed); Window.Write(writer); Lever.Write(writer); writer.Write(MotorPlaying); return stream.ToArray();
        }
        public static VehicleWindowState Decode(byte[] payload)
        {
            if (payload.Length != 59 || payload[0] > 1 || payload[1] > 1 || payload[58] > 1) throw new InvalidDataException("Invalid window payload.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            var value = new VehicleWindowState(reader.ReadBoolean(), reader.ReadBoolean(), ItemPose.Read(reader), ItemPose.Read(reader), reader.ReadBoolean());
            value.Validate(); return value;
        }
        private void Validate()
        {
            Window.Validate(); Lever.Validate();
            if (TargetDown && Closed) throw new InvalidDataException("A closed window cannot target down.");
            if (!Local(Window) || !Local(Lever)) throw new InvalidDataException("Invalid local window pose.");
        }
        private static bool Local(ItemPose p) => ItemPose.Finite(p.X, 20) && ItemPose.Finite(p.Y, 20) && ItemPose.Finite(p.Z, 20);
    }
}
