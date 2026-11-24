using System;
using System.IO;

namespace MonMP.Core.World
{
    [Flags]
    public enum TrafficLightFlags : byte
    {
        None = 0,
        Front = 1,
        Rear = 2,
        Reverse = 4,
        Brake = 8,
        LeftIndicator = 16,
        RightIndicator = 32
    }

    // A pool slot is shared by its stable index. The host chooses when it is
    // active and supplies the presentation pose; guests never run Gley's AI.
    public sealed class TrafficSlotState
    {
        public const ushort Adapter = 43;
        public const int MaxSlots = 4096;
        // Reserve an adapter-specific dynamic entity range. Generic spawned item
        // IDs advance from DynamicIdStart, while trees, planters, and tubing use
        // their own published ranges.
        private const ulong IdBase = WorldRecord.DynamicIdStart | 0x1000000000000000UL;
        public readonly int Slot;
        public readonly ulong PrefabFingerprint;
        public readonly bool Active;
        public readonly ItemPose Pose;
        public readonly float VelocityX, VelocityY, VelocityZ;
        public readonly TrafficLightFlags Lights;
        public readonly float ColorR, ColorG, ColorB, ColorA;

        public TrafficSlotState(int slot, ulong prefabFingerprint, bool active, ItemPose pose,
            float velocityX, float velocityY, float velocityZ, TrafficLightFlags lights,
            float colorR, float colorG, float colorB, float colorA)
        {
            Slot = slot; PrefabFingerprint = prefabFingerprint; Active = active; Pose = pose;
            VelocityX = velocityX; VelocityY = velocityY; VelocityZ = velocityZ; Lights = lights;
            ColorR = colorR; ColorG = colorG; ColorB = colorB; ColorA = colorA;
        }

        public static ulong Entity(int slot)
        {
            ValidateSlot(slot);
            return IdBase | (uint)slot;
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Slot); writer.Write(PrefabFingerprint); writer.Write(Active); Pose.Write(writer);
            writer.Write(VelocityX); writer.Write(VelocityY); writer.Write(VelocityZ);
            writer.Write((byte)Lights); writer.Write(ColorR); writer.Write(ColorG); writer.Write(ColorB); writer.Write(ColorA);
            return stream.ToArray();
        }

        public static TrafficSlotState Decode(byte[] payload)
        {
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            int slot = reader.ReadInt32(); ulong fingerprint = reader.ReadUInt64(); byte active = reader.ReadByte();
            var pose = ItemPose.Read(reader);
            float vx = reader.ReadSingle(), vy = reader.ReadSingle(), vz = reader.ReadSingle();
            var lights = (TrafficLightFlags)reader.ReadByte();
            float red = reader.ReadSingle(), green = reader.ReadSingle(), blue = reader.ReadSingle(), alpha = reader.ReadSingle();
            if (active > 1 || stream.Position != stream.Length) throw new InvalidDataException("Invalid traffic slot payload.");
            var state = new TrafficSlotState(slot, fingerprint, active == 1, pose, vx, vy, vz, lights, red, green, blue, alpha);
            state.Validate();
            return state;
        }

        private void Validate()
        {
            ValidateSlot(Slot); Pose.Validate();
            if (PrefabFingerprint == 0 || !ItemPose.Finite(VelocityX, 1000) || !ItemPose.Finite(VelocityY, 1000) ||
                !ItemPose.Finite(VelocityZ, 1000) || ((byte)Lights & ~63) != 0 ||
                !Color(ColorR) || !Color(ColorG) || !Color(ColorB) || !Color(ColorA))
                throw new InvalidDataException("Invalid traffic slot state.");
        }

        private static bool Color(float value) => ItemPose.Finite(value, 1) && value >= 0;
        private static void ValidateSlot(int slot)
        { if (slot < 0 || slot >= MaxSlots) throw new InvalidDataException("Invalid traffic slot index."); }
    }
}
