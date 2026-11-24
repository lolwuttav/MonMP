using System.IO;

namespace MonMP.Core.World
{
    // Fifteen native lamp groups, three toggle latches and two one-second blink phases.
    public readonly struct VehicleLightingState
    {
        public readonly ushort Lamps;
        public readonly byte Controls, LeftPhase, RightPhase, InteriorMode;
        public VehicleLightingState(ushort lamps, byte controls, byte leftPhase, byte rightPhase, byte interiorMode = 0)
        { Lamps = lamps; Controls = controls; LeftPhase = leftPhase; RightPhase = rightPhase; InteriorMode = interiorMode; }
        internal void Write(BinaryWriter writer, byte radiatorHeat = 0)
        {
            if ((Lamps & 0x8000) != 0 || Controls > 7 || InteriorMode > 2 || radiatorHeat > VehicleEffects.HeatSteps) throw new InvalidDataException("Invalid vehicle lighting flags.");
            writer.Write(Lamps); writer.Write((byte)(Controls | radiatorHeat << 3)); writer.Write(LeftPhase); writer.Write(RightPhase); writer.Write(InteriorMode);
        }
        internal static VehicleLightingState Read(BinaryReader reader, out byte radiatorHeat)
        {
            ushort lamps = reader.ReadUInt16();
            byte controls = reader.ReadByte();
            radiatorHeat = (byte)(controls >> 3);
            var result = new VehicleLightingState(lamps, (byte)(controls & 7), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
            if ((result.Lamps & 0x8000) != 0 || result.Controls > 7 || result.InteriorMode > 2) throw new InvalidDataException("Invalid vehicle lighting flags.");
            return result;
        }
    }
}
