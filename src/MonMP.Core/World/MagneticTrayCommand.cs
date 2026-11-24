using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum MagneticTrayAction : byte { AttachBolt = 1, RemoveAll = 2 }

    // Guest requests for mutations involving both a leased tray and a separate loose-bolt entity.
    // The host resolves every id against its registered item table and checks both generations.
    public readonly struct MagneticTrayCommand
    {
        public const byte Marker = 0xD3;
        public const int Size = 18;
        public readonly MagneticTrayAction Action;
        public readonly uint TrayGeneration;
        public readonly ulong BoltEntity;
        public readonly uint BoltGeneration;

        public MagneticTrayCommand(MagneticTrayAction action, uint trayGeneration, ulong boltEntity = 0, uint boltGeneration = 0)
        { Action = action; TrayGeneration = trayGeneration; BoltEntity = boltEntity; BoltGeneration = boltGeneration; }

        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(Marker); w.Write((byte)Action); w.Write(TrayGeneration); w.Write(BoltEntity); w.Write(BoltGeneration);
            return stream.ToArray();
        }

        public static MagneticTrayCommand Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid magnetic tray command.");
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream);
            if (r.ReadByte() != Marker) throw new InvalidDataException("Invalid magnetic tray command.");
            var command = new MagneticTrayCommand((MagneticTrayAction)r.ReadByte(), r.ReadUInt32(), r.ReadUInt64(), r.ReadUInt32());
            if (stream.Position != stream.Length) throw new InvalidDataException("Invalid magnetic tray command.");
            command.Validate(); return command;
        }

        private void Validate()
        {
            if (!Enum.IsDefined(typeof(MagneticTrayAction), Action) || TrayGeneration == 0)
                throw new InvalidDataException("Invalid magnetic tray command.");
            if (Action == MagneticTrayAction.AttachBolt)
            {
                if (BoltEntity <= WorldRecord.DynamicIdStart || BoltGeneration == 0)
                    throw new InvalidDataException("Invalid magnetic tray attach command.");
            }
            else if (BoltEntity != 0 || BoltGeneration != 0)
                throw new InvalidDataException("Invalid magnetic tray removal command.");
        }
    }
}
