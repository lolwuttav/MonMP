using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    // Session-only visuals for valid native Fishing_Trash array entries that are not eligible
    // for SharedItems' catalogued-item path. The host owns the six-second lifetime.
    public readonly struct FishingTrashCosmeticState
    {
        public const int Capacity = 32, HeaderSize = 1, EntrySize = 52;

        public readonly ulong Rod;
        public readonly uint Owner, Generation, Ticket;
        public readonly ushort Template, RemainingMilliseconds;
        public readonly ItemPose Pose;

        public FishingTrashCosmeticState(ulong rod, uint owner, uint generation, uint ticket, ushort template,
            ItemPose pose, ushort remainingMilliseconds)
        { Rod = rod; Owner = owner; Generation = generation; Ticket = ticket; Template = template; Pose = pose; RemainingMilliseconds = remainingMilliseconds; }

        private void Validate()
        {
            if (Rod == 0 || Owner == ItemLease.Unowned || Generation == 0 || Ticket == 0 ||
                Template == FishingPresentationState.NoTrashTemplate || RemainingMilliseconds == 0 || RemainingMilliseconds > 6000)
                throw new InvalidDataException("Invalid transient fishing trash cosmetic.");
            Pose.Validate();
        }

        public static byte[] Encode(IReadOnlyList<FishingTrashCosmeticState> states)
        {
            if (states == null || states.Count > Capacity) throw new InvalidDataException("Too many fishing trash cosmetics.");
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write((byte)states.Count);
            var identities = new HashSet<(ulong Rod, uint Generation, uint Ticket)>();
            foreach (var state in states)
            {
                state.Validate();
                if (!identities.Add((state.Rod, state.Generation, state.Ticket))) throw new InvalidDataException("Duplicate fishing trash cosmetic.");
                writer.Write(state.Rod); writer.Write(state.Owner); writer.Write(state.Generation); writer.Write(state.Ticket);
                writer.Write(state.Template); state.Pose.Write(writer); writer.Write(state.RemainingMilliseconds);
            }
            return stream.ToArray();
        }

        public static FishingTrashCosmeticState[] Decode(byte[] payload)
        {
            if (payload == null || payload.Length < HeaderSize || payload[0] > Capacity ||
                payload.Length != HeaderSize + payload[0] * EntrySize)
                throw new InvalidDataException("Invalid fishing trash cosmetic snapshot.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            var count = reader.ReadByte(); var states = new FishingTrashCosmeticState[count];
            var identities = new HashSet<(ulong Rod, uint Generation, uint Ticket)>();
            for (int i = 0; i < states.Length; i++)
            {
                var state = new FishingTrashCosmeticState(reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(),
                    reader.ReadUInt16(), ItemPose.Read(reader), reader.ReadUInt16());
                state.Validate();
                if (!identities.Add((state.Rod, state.Generation, state.Ticket))) throw new InvalidDataException("Duplicate fishing trash cosmetic.");
                states[i] = state;
            }
            return states;
        }
    }
}
