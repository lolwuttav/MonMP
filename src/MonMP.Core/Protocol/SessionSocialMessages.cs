using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MonMP.Core.Protocol
{
    public sealed class SessionCheatState
    {
        private readonly uint[] allowed;
        public bool Enabled { get; }
        public float GravityY { get; }
        public IReadOnlyList<uint> Allowed => Array.AsReadOnly(allowed);
        public SessionCheatState(bool enabled, float gravityY = -9.81f, IEnumerable<uint>? allowed = null)
        {
            if (!Finite(gravityY) || gravityY < -100 || gravityY > 0) throw new InvalidDataException("Invalid cheat gravity.");
            this.allowed = (allowed ?? Array.Empty<uint>()).OrderBy(id => id).ToArray();
            if (this.allowed.Length > 15 || this.allowed.Any(id => id == 0 || id == uint.MaxValue) || this.allowed.Distinct().Count() != this.allowed.Length)
                throw new InvalidDataException("Invalid cheat permissions.");
            Enabled = enabled; GravityY = gravityY;
        }
        public bool Permitted(uint peer) => Enabled && (peer == 0 || Array.IndexOf(allowed, peer) >= 0);
        public bool Granted(uint peer) => Array.IndexOf(allowed, peer) >= 0;
        public void Write(BinaryWriter writer)
        {
            writer.Write(Enabled); writer.Write(GravityY); writer.Write((byte)allowed.Length);
            foreach (uint peer in allowed) writer.Write(peer);
        }
        public static SessionCheatState Read(BinaryReader reader)
        {
            byte enabled = reader.ReadByte(); float gravity = reader.ReadSingle(); byte count = reader.ReadByte();
            if (enabled > 1 || count > 15) throw new InvalidDataException("Invalid cheat state.");
            var peers = new uint[count]; for (int i = 0; i < count; i++) peers[i] = reader.ReadUInt32();
            return new SessionCheatState(enabled == 1, gravity, peers);
        }
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    // Personal needs, speed and player teleport run locally after permission checks.
    // Shared actions carry a bounded native selection to the host; they cannot enable cheats.
    public enum SharedCheatAction : byte { Cash = 1, Hour, Day, WaterPlants, DisinfectPlants, Refuel, Weather, Gravity, Spawn, TeleportVehicle }
    public readonly struct CheatActionRequest
    {
        public SharedCheatAction Action { get; }
        public ushort Selection { get; }
        public CheatActionRequest(SharedCheatAction action, ushort selection = 0)
        {
            if (action < SharedCheatAction.Cash || action > SharedCheatAction.TeleportVehicle || selection > 512 ||
                (action <= SharedCheatAction.Refuel && selection != 0)) throw new InvalidDataException("Invalid cheat action.");
            Action = action; Selection = selection;
        }
        public void Write(BinaryWriter writer) { writer.Write((byte)Action); writer.Write(Selection); }
        public static CheatActionRequest Read(BinaryReader reader) => new CheatActionRequest((SharedCheatAction)reader.ReadByte(), reader.ReadUInt16());
    }

    // Only the authoritative host sends action results, directly to the requester.
    public readonly struct CheatFeedback
    {
        public const int MaxTextBytes = 900;
        public SharedCheatAction Action { get; }
        public bool Error { get; }
        public string Text { get; }
        public CheatFeedback(SharedCheatAction action, bool error, string text)
        {
            _ = new CheatActionRequest(action);
            if (string.IsNullOrWhiteSpace(text) || System.Text.Encoding.UTF8.GetByteCount(text) > MaxTextBytes)
                throw new InvalidDataException("Invalid cheat feedback.");
            Action = action; Error = error; Text = text;
        }
        public void Write(BinaryWriter writer)
        {
            writer.Write((byte)Action); writer.Write(Error); Wire.WriteText(writer, Text, MaxTextBytes);
        }
        public static CheatFeedback Read(BinaryReader reader)
        {
            var action = (SharedCheatAction)reader.ReadByte(); byte error = reader.ReadByte();
            if (error > 1) throw new InvalidDataException("Invalid cheat feedback flag.");
            return new CheatFeedback(action, error == 1, Wire.ReadText(reader, MaxTextBytes));
        }
    }

    public readonly struct SocialPing
    {
        public const float Seconds = 8, MaxDistance = 200;
        public uint PeerId { get; }
        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public bool Point { get; }
        public SocialPing(uint peer, float x, float y, float z, bool point = true)
        {
            if (!SessionCheatState.Finite(x) || !SessionCheatState.Finite(y) || !SessionCheatState.Finite(z) ||
                Math.Abs(x) > 100000 || Math.Abs(y) > 100000 || Math.Abs(z) > 100000) throw new InvalidDataException("Invalid ping target.");
            PeerId = peer; X = x; Y = y; Z = z; Point = point;
        }
        public void Write(BinaryWriter writer) { writer.Write(PeerId); writer.Write(X); writer.Write(Y); writer.Write(Z); writer.Write(Point); }
        public static SocialPing Read(BinaryReader reader)
        {
            uint peer = reader.ReadUInt32(); float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle(); byte point = reader.ReadByte();
            if (point > 1) throw new InvalidDataException("Invalid ping pointing flag.");
            return new SocialPing(peer, x, y, z, point == 1);
        }
    }
}
