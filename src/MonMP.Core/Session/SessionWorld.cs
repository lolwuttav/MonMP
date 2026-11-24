using System;
using System.IO;
using System.Linq;
using MonMP.Core.Protocol;

namespace MonMP.Core.Session
{
    // Immutable host configuration sent before a guest loads the master scene.
    public sealed class SessionWorld
    {
        public string Scene { get; }
        public string Catalog { get; }
        public bool HardMode { get; }
        public bool Permadeath { get; }
        public PlayerPose Spawn { get; }
        // Hard-mode world shape the native save loader built on the host (HardModeLayout flags).
        public byte HardModeLayout { get; }
        // Starter vehicles (bit = native Vehicle_Type) the host has no active copy of: stored in a
        // storage garage or removed by hard mode. A guest loading a fresh world hides its copies.
        public uint HiddenVehicles { get; }
        // The subset of HiddenVehicles parked in a storage garage: both sides bind them while stored.
        public uint StoredVehicles { get; }
        public const uint MaxHiddenVehicles = (1u << 24) - 1;
        public SessionWorld(string scene, string catalog, bool hardMode, bool permadeath, PlayerPose spawn, byte hardModeLayout = 0, uint hiddenVehicles = 0, uint storedVehicles = 0)
        {
            if (string.IsNullOrWhiteSpace(scene) || (catalog.Length != 0 && (catalog.Length != 64 || catalog.Any(c => !Uri.IsHexDigit(c)))))
                throw new InvalidDataException("Invalid host world configuration.");
            if (!hardMode && hardModeLayout != 0) throw new InvalidDataException("Invalid hard-mode world layout.");
            if (hiddenVehicles > MaxHiddenVehicles || (storedVehicles & ~hiddenVehicles) != 0) throw new InvalidDataException("Invalid hidden vehicle set.");
            HardModeLayout = hardModeLayout; HiddenVehicles = hiddenVehicles; StoredVehicles = storedVehicles;
            Scene = scene; Catalog = catalog; HardMode = hardMode; Permadeath = permadeath; Spawn = spawn;
        }
        internal void Write(BinaryWriter writer)
        {
            Wire.WriteText(writer, Scene, 200); Wire.WriteText(writer, Catalog, 64);
            writer.Write((byte)((HardMode ? 1 : 0) | (Permadeath ? 2 : 0))); writer.Write(HardModeLayout); writer.Write(HiddenVehicles); writer.Write(StoredVehicles); Spawn.Write(writer);
        }
        internal static SessionWorld Read(BinaryReader reader)
        {
            string scene = Wire.ReadText(reader, 200), catalog = Wire.ReadText(reader, 64); byte flags = reader.ReadByte();
            if (flags > 3) throw new InvalidDataException("Invalid world flags.");
            byte layout = reader.ReadByte(); uint hidden = reader.ReadUInt32(), stored = reader.ReadUInt32();
            return new SessionWorld(scene, catalog, (flags & 1) != 0, (flags & 2) != 0, PlayerPose.Read(reader), layout, hidden, stored);
        }
    }
}
