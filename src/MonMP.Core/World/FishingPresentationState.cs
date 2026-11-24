using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    public enum FishingPresentationPhase : byte { Cast = 0, End = 1, Caught = 2, Equip = 3, Unequip = 4 }
    public enum FishingCatchOutcome : byte { None = 0, Fish = 1, Trash = 2, Unique = 3, Unknown = 4 }

    // Current rod presentation plus the accepted result category. Strength controls, spot/species
    // selection, rewards and personal fishing UI remain local to the angler.
    public readonly struct FishingPresentationState
    {
        public const ushort Adapter = 138;
        public const int WireSize = 36, Capacity = 16;
        public const ushort NoTrashTemplate = ushort.MaxValue;
        public readonly ulong Rod;
        public readonly uint Owner, Generation;
        public readonly FishingPresentationPhase Phase;
        public readonly float X, Y, Z;
        public readonly FishingCatchOutcome Outcome;
        // Assigned only by the host when a new caught phase is accepted. Zero is permitted on
        // client reports and is replaced before the state is included in a current snapshot.
        public readonly uint CatchTicket;
        // Index into GameAssets.Fishing_Trash. Other outcomes use NoTrashTemplate.
        public readonly ushort TrashTemplate;
        public FishingPresentationState(ulong rod, uint owner, uint generation, FishingPresentationPhase phase, float x, float y, float z)
            : this(rod, owner, generation, phase, x, y, z, FishingCatchOutcome.None, 0, NoTrashTemplate) { }
        public FishingPresentationState(ulong rod, uint owner, uint generation, FishingPresentationPhase phase, float x, float y, float z,
            FishingCatchOutcome outcome, uint catchTicket, ushort trashTemplate)
        { Rod = rod; Owner = owner; Generation = generation; Phase = phase; X = x; Y = y; Z = z; Outcome = outcome; CatchTicket = catchTicket; TrashTemplate = trashTemplate; }
        public FishingPresentationState WithOwner(uint owner) => new FishingPresentationState(Rod, owner, Generation, Phase, X, Y, Z, Outcome, CatchTicket, TrashTemplate);
        public FishingPresentationState WithCatchTicket(uint ticket) => new FishingPresentationState(Rod, Owner, Generation, Phase, X, Y, Z, Outcome, ticket, TrashTemplate);
        private void Validate(bool current = false)
        {
            if (Rod == 0 || Owner == ItemLease.Unowned || Generation == 0 || Phase > FishingPresentationPhase.Unequip ||
                !ItemPose.FiniteValue(X, 100000) || !ItemPose.FiniteValue(Y, 100000) || !ItemPose.FiniteValue(Z, 100000) ||
                !Enum.IsDefined(typeof(FishingCatchOutcome), Outcome))
                throw new InvalidDataException("Invalid fishing presentation state.");
            if (Phase != FishingPresentationPhase.Caught)
            {
                if (Outcome != FishingCatchOutcome.None || CatchTicket != 0 || TrashTemplate != NoTrashTemplate)
                    throw new InvalidDataException("Fishing result metadata requires a caught phase.");
            }
            else
            {
                if (Outcome == FishingCatchOutcome.None || (current && CatchTicket == 0) ||
                    (Outcome == FishingCatchOutcome.Trash ? TrashTemplate == NoTrashTemplate : TrashTemplate != NoTrashTemplate))
                    throw new InvalidDataException("Invalid fishing catch metadata.");
            }
        }
        private void Write(BinaryWriter w, bool current = false)
        { Validate(current); w.Write(Rod); w.Write(Owner); w.Write(Generation); w.Write((byte)Phase); w.Write(X); w.Write(Y); w.Write(Z); w.Write((byte)Outcome); w.Write(CatchTicket); w.Write(TrashTemplate); }
        private static FishingPresentationState Read(BinaryReader r, bool current = false)
        {
            var state = new FishingPresentationState(r.ReadUInt64(), r.ReadUInt32(), r.ReadUInt32(), (FishingPresentationPhase)r.ReadByte(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
                (FishingCatchOutcome)r.ReadByte(), r.ReadUInt32(), r.ReadUInt16());
            state.Validate(current); return state;
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream); Write(w); return stream.ToArray();
        }
        public static FishingPresentationState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != WireSize) throw new InvalidDataException("Invalid fishing presentation report.");
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream); return Read(r);
        }
        public static byte[] EncodeCurrent(IReadOnlyList<FishingPresentationState> states)
        {
            if (states.Count > Capacity) throw new InvalidDataException("Too many fishing presentations.");
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write((byte)states.Count); var rods = new HashSet<ulong>(); var owners = new HashSet<uint>();
            foreach (var state in states)
            {
                if (state.Phase == FishingPresentationPhase.Unequip || !rods.Add(state.Rod) || !owners.Add(state.Owner))
                    throw new InvalidDataException("Duplicate or inactive fishing presentation.");
                state.Write(w, current: true);
            }
            return stream.ToArray();
        }
        public static FishingPresentationState[] DecodeCurrent(byte[] payload)
        {
            if (payload == null || payload.Length < 1 || payload[0] > Capacity || payload.Length != 1 + payload[0] * WireSize)
                throw new InvalidDataException("Invalid current fishing presentations.");
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream);
            var result = new FishingPresentationState[r.ReadByte()]; var rods = new HashSet<ulong>(); var owners = new HashSet<uint>();
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = Read(r, current: true);
                if (result[i].Phase == FishingPresentationPhase.Unequip || !rods.Add(result[i].Rod) || !owners.Add(result[i].Owner))
                    throw new InvalidDataException("Duplicate or inactive fishing presentation.");
            }
            return result;
        }
    }
}
