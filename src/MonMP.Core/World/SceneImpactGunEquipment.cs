using System;
using System.IO;

namespace MonMP.Core.World
{
    // This state is carried on the fixed impact-gun rows in the bolt adapter's world stream.
    // It is session-only equipment ownership and is not saved with bolt-hole state.
    public readonly struct SceneImpactGunEquipmentState
    {
        public readonly uint Owner, Generation;
        public SceneImpactGunEquipmentState(uint owner, uint generation)
        {
            if (generation == 0) throw new ArgumentOutOfRangeException(nameof(generation));
            Owner = owner; Generation = generation;
        }
        public byte[] Encode()
        {
            if (Generation == 0) throw new InvalidDataException("Invalid impact-gun equipment generation.");
            using var stream = new MemoryStream(8); using var writer = new BinaryWriter(stream);
            writer.Write(Owner); writer.Write(Generation); return stream.ToArray();
        }
        public static SceneImpactGunEquipmentState Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length != 8) throw new InvalidDataException("Invalid impact-gun equipment state.");
            using var reader = new BinaryReader(new MemoryStream(bytes, false));
            uint owner = reader.ReadUInt32(), generation = reader.ReadUInt32();
            if (generation == 0) throw new InvalidDataException("Invalid impact-gun equipment generation.");
            return new SceneImpactGunEquipmentState(owner, generation);
        }
    }

    public readonly struct SceneImpactGunEquipmentCommand
    {
        public const byte Marker = 0xC5;
        public const int Size = 6;
        public readonly bool Equipped;
        public readonly uint Generation;
        public SceneImpactGunEquipmentCommand(bool equipped, uint generation)
        {
            if (generation == 0) throw new ArgumentOutOfRangeException(nameof(generation));
            Equipped = equipped; Generation = generation;
        }
        public byte[] Encode()
        {
            if (Generation == 0) throw new InvalidDataException("Invalid impact-gun equipment generation.");
            using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write((byte)(Equipped ? 1 : 0)); writer.Write(Generation); return stream.ToArray();
        }
        public static bool Is(byte[]? bytes) => bytes != null && bytes.Length == Size && bytes[0] == Marker;
        public static bool HasMarker(byte[]? bytes) => bytes != null && bytes.Length > 0 && bytes[0] == Marker;
        public static SceneImpactGunEquipmentCommand Decode(byte[] bytes)
        {
            if (!Is(bytes) || bytes[1] > 1) throw new InvalidDataException("Invalid impact-gun equipment command.");
            using var reader = new BinaryReader(new MemoryStream(bytes, false));
            reader.ReadByte(); bool equipped = reader.ReadByte() == 1; uint generation = reader.ReadUInt32();
            if (generation == 0) throw new InvalidDataException("Invalid impact-gun equipment generation.");
            return new SceneImpactGunEquipmentCommand(equipped, generation);
        }
    }

    // Ownership changes advance the generation so a delayed release cannot clear a new holder.
    public sealed class SceneImpactGunEquipmentLease
    {
        public SceneImpactGunEquipmentState State { get; private set; } = new SceneImpactGunEquipmentState(ItemLease.Unowned, 1);

        public bool Apply(uint actor, SceneImpactGunEquipmentCommand command, bool actionsAvailable,
            bool freshToolPose, bool withinStartOrHolderReach)
        {
            if (actor == ItemLease.Unowned || command.Generation != State.Generation) return false;
            if (!command.Equipped)
            {
                if (State.Owner != actor) return false;
                Revoke(); return true;
            }
            if (!actionsAvailable || !freshToolPose || !withinStartOrHolderReach ||
                State.Owner != ItemLease.Unowned && State.Owner != actor) return false;
            if (State.Owner == ItemLease.Unowned) State = new SceneImpactGunEquipmentState(actor, Next(State.Generation));
            return true;
        }

        public void Restore(SceneImpactGunEquipmentState state)
        {
            if (state.Generation == 0) throw new InvalidDataException("Invalid impact-gun equipment generation.");
            State = state;
        }
        public void Revoke()
        {
            if (State.Owner != ItemLease.Unowned) State = new SceneImpactGunEquipmentState(ItemLease.Unowned, Next(State.Generation));
        }
        private static uint Next(uint value) => value == uint.MaxValue ? 1 : value + 1;
    }
}
