using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    // A guest adjusted one native suspension control on a loose coilover or tuned control arm.
    // The host checks the lease, prior values and exact one-step transition before applying it.
    public readonly struct LooseSuspensionEditCommand
    {
        public const byte Marker = 0xC2;
        public const int Size = 1 + 4 + 1 + 4 + 16 + 16;
        public readonly uint Nonce;
        public readonly MountedVehiclePartKind Kind;
        public readonly uint Generation;
        public readonly MountedPartTuning Before, After;

        public LooseSuspensionEditCommand(uint nonce, MountedVehiclePartKind kind, uint generation,
            MountedPartTuning before, MountedPartTuning after)
        { Nonce = nonce; Kind = kind; Generation = generation; Before = before; After = after; }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Nonce); writer.Write((byte)Kind); writer.Write(Generation);
            Write(writer, Before); Write(writer, After);
            return stream.ToArray();
        }

        public static bool Is(byte[] payload) => payload != null && payload.Length != 0 && payload[0] == Marker;

        public static LooseSuspensionEditCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid loose suspension edit length.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            if (reader.ReadByte() != Marker) throw new InvalidDataException("Invalid loose suspension edit marker.");
            var command = new LooseSuspensionEditCommand(reader.ReadUInt32(), (MountedVehiclePartKind)reader.ReadByte(),
                reader.ReadUInt32(), Read(reader), Read(reader));
            command.Validate();
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing loose suspension edit data.");
            return command;
        }

        public static bool TryGetNonce(byte[] payload, out uint nonce)
        {
            nonce = 0;
            if (!Is(payload) || payload.Length != Size) return false;
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            reader.ReadByte(); nonce = reader.ReadUInt32();
            return nonce != 0;
        }

        public static bool CanApply(uint peer, uint leaseOwner, uint leaseGeneration, bool transfersBlocked,
            bool itemExists, bool looseSuspensionPart, bool inRange, MountedPartTuning authoritative,
            LooseSuspensionEditCommand command) =>
            peer != 0 && peer != ItemLease.Unowned && command.Nonce != 0 && command.Generation != 0 &&
            (leaseOwner == ItemLease.Unowned || leaseOwner == peer) && leaseGeneration == command.Generation &&
            !transfersBlocked && itemExists && looseSuspensionPart && inRange &&
            authoritative.Equals(command.Before) && MountedVehiclePartValues.IsOneNativeStep(command.Before, command.After, command.Kind);

        private void Validate()
        {
            if (Nonce == 0 || Generation == 0 ||
                (Kind != MountedVehiclePartKind.Coilover && Kind != MountedVehiclePartKind.TunedControlArm) ||
                !MountedVehiclePartValues.IsOneNativeStep(Before, After, Kind))
                throw new InvalidDataException("Invalid loose suspension edit transition.");
        }

        private static void Write(BinaryWriter writer, MountedPartTuning values)
        {
            writer.Write(values.SpringForce); writer.Write(values.Rebound); writer.Write(values.Height); writer.Write(values.Damper);
        }

        private static MountedPartTuning Read(BinaryReader reader) =>
            new MountedPartTuning(reader.ReadSingle(), reader.ReadSingle(), reader.ReadInt32(), reader.ReadInt32());
    }

    public enum LooseSuspensionEditProgress : byte
    {
        Waiting,
        RequestReady,
        Complete,
        Rejected,
        TimedOut,
        LeaseChanged
    }

    // Serializes native knob intents for one loose item. Only one exact Before/After command
    // is in flight; later clicks are rebased onto each accepted host state.
    public sealed class LooseSuspensionEditQueue
    {
        public const byte ReplyMarker = 0xC3;
        public const byte RejectedReply = 0, AcceptedReply = 1;
        public const int ReplySize = 1 + 4 + 1 + 4;
        private const int Capacity = 128;

        private readonly Queue<Step> steps = new Queue<Step>();
        private uint nextNonce, inFlight, replyRevision;
        private byte replyStatus;
        private bool replied, hasLease;
        private uint leaseOwner, leaseGeneration;
        private double deadline;
        public MountedPartTuning? Display { get; private set; }

        private readonly struct Step
        {
            public readonly MountedVehiclePartKind Kind;
            public readonly int Field, Direction;
            public Step(MountedVehiclePartKind kind, int field, int direction)
            { Kind = kind; Field = field; Direction = direction; }
        }

        public int PendingCount => steps.Count;
        public bool HasPending => steps.Count != 0;
        public bool MatchesLease(uint owner, uint generation) => hasLease && leaseOwner == owner && leaseGeneration == generation;

        public bool Enqueue(uint owner, uint generation, MountedPartTuning before, MountedPartTuning after,
            MountedVehiclePartKind kind)
        {
            if (generation == 0 || owner == 0 ||
                !MountedVehiclePartValues.IsOneNativeStep(before, after, kind)) return false;
            if (steps.Count == 0)
            { leaseOwner = owner; leaseGeneration = generation; hasLease = true; }
            else if (!MatchesLease(owner, generation)) return false;
            if (steps.Count >= Capacity) return false;

            int field = before.SpringForce != after.SpringForce ? 0 : before.Rebound != after.Rebound ? 1 :
                before.Height != after.Height ? 2 : 3;
            int direction = field == 0 ? Math.Sign(after.SpringForce - before.SpringForce) :
                field == 1 ? Math.Sign(after.Rebound - before.Rebound) :
                field == 2 ? Math.Sign(after.Height - before.Height) : Math.Sign(after.Damper - before.Damper);
            steps.Enqueue(new Step(kind, field, direction));
            Display = after;
            return true;
        }

        public LooseSuspensionEditProgress Prepare(MountedPartTuning authoritative, uint owner, uint generation,
            uint revision, double now, double timeoutSeconds, out LooseSuspensionEditCommand command)
        {
            command = default;
            if (steps.Count == 0) return LooseSuspensionEditProgress.Complete;
            if (!MatchesLease(owner, generation))
            { CancelPending(); return LooseSuspensionEditProgress.LeaseChanged; }
            if (inFlight != 0)
            {
                if (now >= deadline) { CancelPending(); return LooseSuspensionEditProgress.TimedOut; }
                if (!replied || revision < replyRevision) return LooseSuspensionEditProgress.Waiting;
                if (replyStatus == RejectedReply) { CancelPending(); return LooseSuspensionEditProgress.Rejected; }
                steps.Dequeue(); inFlight = 0; replied = false;
                if (steps.Count == 0) { Display = null; return LooseSuspensionEditProgress.Complete; }
            }

            while (steps.Count != 0)
            {
                var step = steps.Peek();
                if (step.Kind != MountedVehiclePartKind.Coilover && step.Kind != MountedVehiclePartKind.TunedControlArm)
                { CancelPending(); return LooseSuspensionEditProgress.LeaseChanged; }
                var next = StepFrom(authoritative, step);
                if (next.Equals(authoritative))
                { steps.Dequeue(); continue; } // Native endpoint clamp.
                if (!MountedVehiclePartValues.IsOneNativeStep(authoritative, next, step.Kind))
                { CancelPending(); return LooseSuspensionEditProgress.LeaseChanged; }
                do { nextNonce++; } while (nextNonce == 0);
                inFlight = nextNonce; deadline = now + Math.Max(0.1, timeoutSeconds);
                command = new LooseSuspensionEditCommand(inFlight, step.Kind, generation, authoritative, next);
                return LooseSuspensionEditProgress.RequestReady;
            }
            Display = null;
            return LooseSuspensionEditProgress.Complete;
        }

        public bool Reply(byte[] payload)
        {
            if (payload == null || payload.Length != ReplySize || payload[0] != ReplyMarker || inFlight == 0) return false;
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            reader.ReadByte(); uint nonce = reader.ReadUInt32(); byte status = reader.ReadByte(); uint revision = reader.ReadUInt32();
            if (nonce != inFlight) return false;
            if (status > AcceptedReply || revision == 0) throw new InvalidDataException("Invalid loose suspension edit reply.");
            replyStatus = status; replyRevision = revision; replied = true;
            return true;
        }

        public void RequestFailed() => CancelPending();

        public void Clear()
        {
            CancelPending(); hasLease = false; leaseOwner = 0; leaseGeneration = 0;
        }

        public static byte[] EncodeReply(uint nonce, byte status, uint revision)
        {
            if (nonce == 0 || status > AcceptedReply || revision == 0) throw new InvalidDataException("Invalid loose suspension edit reply.");
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(ReplyMarker); writer.Write(nonce); writer.Write(status); writer.Write(revision);
            return stream.ToArray();
        }

        private void CancelPending()
        { steps.Clear(); inFlight = 0; replyRevision = 0; replied = false; deadline = 0; Display = null; }

        private static MountedPartTuning StepFrom(MountedPartTuning current, Step step)
        {
            float Clamp(float value) => Math.Max(0, Math.Min(50, value));
            return new MountedPartTuning(
                step.Field == 0 ? Clamp(current.SpringForce + step.Direction) : current.SpringForce,
                step.Field == 1 ? Clamp(current.Rebound + step.Direction) : current.Rebound,
                step.Field == 2 ? (int)Clamp(current.Height + step.Direction) : current.Height,
                step.Field == 3 ? (int)Clamp(current.Damper + step.Direction) : current.Damper);
        }
    }
}
