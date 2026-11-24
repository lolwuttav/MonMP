using System;
using System.IO;

namespace MonMP.Core.World
{
    public readonly struct SceneAirEquipmentState
    {
        public const ushort Adapter = 110;
        public readonly uint Owner, Generation;
        public SceneAirEquipmentState(uint owner, uint generation)
        { if (generation == 0) throw new ArgumentOutOfRangeException(nameof(generation)); Owner = owner; Generation = generation; }
        public byte[] Encode()
        {
            if (Generation == 0) throw new InvalidDataException("Invalid scene-air holder generation.");
            using var stream = new MemoryStream(8); using var writer = new BinaryWriter(stream);
            writer.Write(Owner); writer.Write(Generation); return stream.ToArray();
        }
        public static SceneAirEquipmentState Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length != 8) throw new InvalidDataException("Invalid scene-air holder state.");
            using var reader = new BinaryReader(new MemoryStream(bytes, false));
            uint owner = reader.ReadUInt32(), generation = reader.ReadUInt32();
            if (generation == 0) throw new InvalidDataException("Invalid scene-air holder generation.");
            return new SceneAirEquipmentState(owner, generation);
        }
    }

    public readonly struct SceneAirEquipmentCommand
    {
        public readonly bool Equipped;
        public readonly uint Generation;
        public SceneAirEquipmentCommand(bool equipped, uint generation)
        { if (generation == 0) throw new ArgumentOutOfRangeException(nameof(generation)); Equipped = equipped; Generation = generation; }
        public byte[] Encode()
        {
            if (Generation == 0) throw new InvalidDataException("Invalid scene-air equip generation.");
            using var stream = new MemoryStream(5); using var writer = new BinaryWriter(stream);
            writer.Write((byte)(Equipped ? 1 : 0)); writer.Write(Generation); return stream.ToArray();
        }
        public static SceneAirEquipmentCommand Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length != 5 || bytes[0] > 1) throw new InvalidDataException("Invalid scene-air equip command.");
            using var reader = new BinaryReader(new MemoryStream(bytes, false));
            bool equipped = reader.ReadByte() == 1; uint generation = reader.ReadUInt32();
            if (generation == 0) throw new InvalidDataException("Invalid scene-air equip generation.");
            return new SceneAirEquipmentCommand(equipped, generation);
        }
    }

    // One host-owned slot per exact scene gun. Heartbeats retain the generation;
    // ownership transitions advance it so delayed releases cannot cancel a new holder.
    public sealed class SceneAirEquipmentLease
    {
        public SceneAirEquipmentState State { get; private set; } = new SceneAirEquipmentState(ItemLease.Unowned, 1);
        public bool Apply(uint actor, SceneAirEquipmentCommand command, bool freshToolPose, bool withinReach)
        {
            if (actor == ItemLease.Unowned || command.Generation != State.Generation) return false;
            if (!command.Equipped)
            {
                if (State.Owner != actor) return false;
                Revoke(); return true;
            }
            if (!freshToolPose || !withinReach || State.Owner != ItemLease.Unowned && State.Owner != actor) return false;
            if (State.Owner == ItemLease.Unowned) State = new SceneAirEquipmentState(actor, Next(State.Generation));
            return true;
        }
        public void Revoke()
        { if (State.Owner != ItemLease.Unowned) State = new SceneAirEquipmentState(ItemLease.Unowned, Next(State.Generation)); }
        private static uint Next(uint value) => value == uint.MaxValue ? 1 : value + 1;
    }
}
