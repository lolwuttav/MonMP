using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    // Guest-side native intent queue. Ordinary whole-state revision checking stays
    // in WorldChannel; an explicit reply distinguishes our acceptance from another
    // actor publishing the same value. A stale reply rebases one native step.
    public sealed class MountedPartEditQueue
    {
        private const byte Marker = 0xDD;
        public const byte Rejected = 0, Accepted = 1, Stale = 2;
        private readonly Queue<Step> steps = new Queue<Step>();
        private uint nextNonce, inFlight, replyRevision;
        private byte replyStatus;
        private bool replied;
        private readonly struct Step
        {
            public readonly ushort Prefab;
            public readonly bool Tightened;
            public readonly MountedVehiclePartKind Kind;
            public readonly int Field, Direction;
            public Step(VehiclePartState before, MountedVehiclePartKind kind, int field, int direction)
            { Prefab = before.Prefab; Tightened = before.FullyTightened; Kind = kind; Field = field; Direction = direction; }
        }
        public int PendingCount => steps.Count;
        public bool RejectedByHost => replied && replyStatus == Rejected;
        public void Clear() { steps.Clear(); inFlight = 0; replied = false; }
        public bool Enqueue(VehiclePartState before, VehiclePartState after, MountedVehiclePartKind kind, int optionCount = 0)
        {
            if (!MountedVehiclePartValues.IsOneNativeStep(before, after, kind, optionCount)) return false;
            if (steps.Count >= 128) throw new InvalidOperationException("Too many mounted edits are waiting for the host.");
            int field = kind == MountedVehiclePartKind.RearSeat ? 4 :
                kind == MountedVehiclePartKind.RpmGaugeShiftThreshold ? 5 :
                kind == MountedVehiclePartKind.SpeedGaugeUnits ? 6 :
                kind == MountedVehiclePartKind.TurboWastegateSound ? 7 :
                kind == MountedVehiclePartKind.StakePickupHistory || kind == MountedVehiclePartKind.SpoilerPickupHistory ? 8 :
                before.Value1 != after.Value1 ? 0 :
                before.Value2 != after.Value2 ? 1 : before.IntValue1 != after.IntValue1 ? 2 : 3;
            int direction = field == 0 ? Math.Sign(after.Value1 - before.Value1) : field == 1 ? Math.Sign(after.Value2 - before.Value2) :
                field == 5 ? Math.Sign(after.IntValue1 - before.IntValue1) : field == 6 ? Math.Sign(after.IntValue1 - before.IntValue1) :
                field == 7 ? before.IntValue1 == 0 && after.IntValue1 == optionCount - 1 ? -1 :
                    after.IntValue1 == 0 && before.IntValue1 == optionCount - 1 ? 1 : Math.Sign(after.IntValue1 - before.IntValue1) :
                field == 2 ? Math.Sign(after.IntValue1 - before.IntValue1) : field == 3 ? Math.Sign(after.IntValue2 - before.IntValue2) : 1;
            steps.Enqueue(new Step(before, kind, field, direction)); return true;
        }
        public bool Prepare(VehiclePartState authoritative, uint revision, MountedVehiclePartKind kind, out byte[] payload, int optionCount = 0)
        {
            payload = Array.Empty<byte>();
            if (inFlight != 0)
            {
                if (!replied || revision < replyRevision) return false;
                if (replyStatus == Accepted) steps.Dequeue();
                else if (replyStatus == Rejected) { Clear(); return false; }
                inFlight = 0; replied = false;
            }
            while (steps.Count != 0)
            {
                var step = steps.Peek();
                if (!authoritative.Installed || authoritative.Prefab != step.Prefab || authoritative.FullyTightened != step.Tightened || kind != step.Kind)
                { Clear(); return false; } // Installation/removal/replacement wins over tuning.
                float Clamp(float value) => Math.Max(0, Math.Min(50, value));
                var next = new VehiclePartState(true, authoritative.Prefab, authoritative.FullyTightened,
                    step.Field == 0 ? Clamp(authoritative.Value1 + step.Direction) : step.Field == 4 ? 1 - authoritative.Value1 : authoritative.Value1,
                    step.Field == 1 ? Clamp(authoritative.Value2 + step.Direction) : authoritative.Value2,
                    step.Field == 5 ? Math.Max(2000, Math.Min(10000, authoritative.IntValue1 + step.Direction * 100)) :
                        step.Field == 6 ? 1 - authoritative.IntValue1 :
                        step.Field == 7 ? Advance(authoritative.IntValue1, step.Direction, optionCount) :
                        step.Field == 2 ? (int)Clamp(authoritative.IntValue1 + step.Direction) : authoritative.IntValue1,
                    step.Field == 8 ? 1 : step.Field == 3 ? (int)Clamp(authoritative.IntValue2 + step.Direction) : authoritative.IntValue2);
                if (next.Equals(authoritative)) { steps.Dequeue(); continue; } // Native endpoint clamp.
                if (!MountedVehiclePartValues.IsOneNativeStep(authoritative, next, kind, optionCount))
                    throw new InvalidOperationException("Queued mounted edit is not one native step.");
                do { nextNonce++; } while (nextNonce == 0);
                inFlight = nextNonce;
                using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
                writer.Write(Marker); writer.Write(inFlight); writer.Write(next.Encode());
                payload = stream.ToArray(); return true;
            }
            return false;
        }
        public void RequestFailed() { inFlight = 0; replied = false; }
        public bool Reply(byte[] payload)
        {
            if (payload.Length != 10 || payload[0] != Marker) return false;
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            reader.ReadByte(); uint nonce = reader.ReadUInt32(); byte status = reader.ReadByte(); uint revision = reader.ReadUInt32();
            if (nonce != inFlight || inFlight == 0) return false;
            if (status > Stale || revision == 0) throw new InvalidDataException("Invalid mounted edit reply.");
            replyStatus = status; replyRevision = revision; replied = true; return true;
        }
        public static bool DecodeRequest(byte[] payload, out uint nonce, out VehiclePartState state)
        {
            nonce = 0; state = VehiclePartState.Empty;
            if (payload.Length != 24 || payload[0] != Marker) return false;
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            reader.ReadByte(); nonce = reader.ReadUInt32();
            if (nonce == 0) throw new InvalidDataException("Invalid mounted edit nonce.");
            state = VehiclePartState.Decode(reader.ReadBytes(VehiclePartState.EncodedSize)); return true;
        }
        private static int Advance(int value, int direction, int optionCount) =>
            optionCount < 1 ? value : (value + direction + optionCount) % optionCount;
        public static byte[] EncodeReply(uint nonce, byte status, uint revision)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(nonce); writer.Write(status); writer.Write(revision); return stream.ToArray();
        }
    }
}
