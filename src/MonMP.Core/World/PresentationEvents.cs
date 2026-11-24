using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    // A one-shot sight or sound (a crash, a shovel dig, an NPC's greeting voice) that the native game
    // only plays on the machine that ran the action. Actor is the peer that already presented it and
    // skips it; Target names the object it plays on (an entity, an actor or 0), Variant and Value are
    // kind-specific (a clip index, an intensity).
    public readonly struct PresentationEvent
    {
        public const ushort Adapter = 133;
        public const int WireSize = 34;
        public const int RequestSize = WireSize - 4;
        public readonly uint Sequence;
        public readonly byte Kind, Variant;
        public readonly uint Actor;
        public readonly ulong Target;
        public readonly float X, Y, Z, Value;

        public PresentationEvent(uint sequence, byte kind, byte variant, uint actor, ulong target, float x, float y, float z, float value)
        {
            Sequence = sequence; Kind = kind; Variant = variant; Actor = actor; Target = target; X = x; Y = y; Z = z; Value = value;
        }

        public PresentationEvent WithSequence(uint sequence, uint actor) => new PresentationEvent(sequence, Kind, Variant, actor, Target, X, Y, Z, Value);

        internal void Validate()
        {
            if (Kind == 0 || !ItemPose.Finite(X, 100000) || !ItemPose.Finite(Y, 100000) || !ItemPose.Finite(Z, 100000) || !ItemPose.Finite(Value, 100000))
                throw new InvalidDataException("Invalid presentation event.");
        }

        private void WriteBody(BinaryWriter w)
        {
            w.Write(Kind); w.Write(Variant); w.Write(Actor); w.Write(Target); w.Write(X); w.Write(Y); w.Write(Z); w.Write(Value);
        }
        private static PresentationEvent ReadBody(BinaryReader r, uint sequence)
        {
            var e = new PresentationEvent(sequence, r.ReadByte(), r.ReadByte(), r.ReadUInt32(), r.ReadUInt64(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            e.Validate(); return e;
        }

        // A guest's report of its own action; the host stamps the sequence and actor.
        public byte[] EncodeRequest()
        {
            Validate(); using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            WriteBody(w); return stream.ToArray();
        }
        public static PresentationEvent DecodeRequest(byte[] payload)
        {
            if (payload == null || payload.Length != RequestSize) throw new InvalidDataException("Invalid presentation event request.");
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream);
            return ReadBody(r, 0);
        }

        // The host's record: the most recent events, oldest first, so a burst coalesced into one
        // delivery still reaches everyone.
        public const int LogCapacity = 14;
        public static byte[] EncodeLog(IReadOnlyList<PresentationEvent> events)
        {
            if (events.Count > LogCapacity) throw new InvalidDataException("Too many presentation events.");
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write((byte)events.Count);
            foreach (var e in events) { e.Validate(); w.Write(e.Sequence); e.WriteBody(w); }
            return stream.ToArray();
        }
        public static PresentationEvent[] DecodeLog(byte[] payload)
        {
            if (payload == null || payload.Length < 1) throw new InvalidDataException("Invalid presentation event log.");
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream);
            int count = r.ReadByte();
            if (count > LogCapacity || payload.Length != 1 + count * WireSize) throw new InvalidDataException("Invalid presentation event log.");
            var result = new PresentationEvent[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = ReadBody(r, r.ReadUInt32());
                if (i > 0 && result[i].Sequence <= result[i - 1].Sequence) throw new InvalidDataException("Unordered presentation event log.");
            }
            return result;
        }
    }
}
