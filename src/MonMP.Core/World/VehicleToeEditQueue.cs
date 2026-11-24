using System;
using System.Collections.Generic;

namespace MonMP.Core.World
{
    /// <summary>Serializes guest rear-toe steps against acknowledged host state.</summary>
    public sealed class VehicleToeEditQueue
    {
        private const byte ReplyMarker = 0xDE;
        public const byte Rejected = 0, Accepted = 1, Stale = 2;
        public const int Capacity = 64;
        public const double TimeoutSeconds = 5;

        private readonly Queue<Step> steps = new Queue<Step>();
        private VehicleToeCommand request;
        private uint nextOperation, requestRevision, replyRevision;
        private byte replyStatus;
        private bool inFlight, replied, started;
        private double startedAt;

        private readonly struct Step
        {
            public readonly VehicleToeSide Side;
            public readonly int Direction;
            public Step(VehicleToeSide side, int direction) { Side = side; Direction = direction; }
        }

        public bool HasPending => steps.Count != 0;
        public int Count => steps.Count;

        public bool Enqueue(VehicleToeSide side, int direction)
        {
            // Validate even when full so malformed callers cannot silently enqueue a different operation.
            _ = new VehicleToeCommand(side, direction, 0);
            if (steps.Count >= Capacity) return false;
            steps.Enqueue(new Step(side, direction));
            return true;
        }

        public void Clear()
        {
            steps.Clear(); inFlight = false; replied = false; started = false;
            requestRevision = 0; replyRevision = 0;
            // Keep operation numbers across cancellation so late replies cannot match a later click.
        }

        /// <summary>Clears stalled edits; a stale retry shares the original step's deadline.</summary>
        public bool Expire(double now)
        {
            ValidateTime(now);
            if (!started || (now >= startedAt && now - startedAt < TimeoutSeconds)) return false;
            Clear();
            return true;
        }

        public bool Prepare(VehicleUiState authoritative, uint revision, double now, out VehicleToeCommand command)
        {
            command = default;
            if (Expire(now)) return false;
            if (!authoritative.HasRearToe || revision == 0) { Clear(); return false; }

            if (inFlight)
            {
                if (!replied || revision < replyRevision) return false;
                if (replyStatus == Accepted) { steps.Dequeue(); started = false; }
                // Stale preserves the native step, then rebases it on the freshly published state.
                inFlight = false; replied = false;
            }

            while (steps.Count != 0)
            {
                var step = steps.Peek();
                int value = step.Side == VehicleToeSide.RearLeft ? authoritative.RearLeft : authoritative.RearRight;
                int next = value + step.Direction;
                if (next < 0 || next > 100)
                {
                    // Native endpoint clicks do nothing; they must not block later clicks on either side.
                    steps.Dequeue(); started = false; continue;
                }
                if (!started) { startedAt = now; started = true; }
                do { nextOperation++; } while (nextOperation == 0);
                request = new VehicleToeCommand(step.Side, step.Direction, value, nextOperation);
                requestRevision = revision; inFlight = true;
                command = request;
                return true;
            }
            return false;
        }

        /// <summary>Retries a request that the channel did not enqueue, without extending its deadline.</summary>
        public void RequestFailed() { inFlight = false; replied = false; }

        public bool Reply(byte[]? payload)
        {
            if (!inFlight || replied || !TryDecodeReply(payload, out var command, out byte status, out uint revision) ||
                command.Operation != request.Operation || command.Side != request.Side ||
                command.Direction != request.Direction || command.ExpectedValue != request.ExpectedValue ||
                revision < requestRevision || (status == Accepted && revision <= requestRevision)) return false;
            if (status == Rejected) { Clear(); return true; }
            replyStatus = status; replyRevision = revision; replied = true;
            return true;
        }

        public static byte[] EncodeReply(VehicleToeCommand command, byte status, uint revision)
        {
            _ = new VehicleToeCommand(command.Side, command.Direction, command.ExpectedValue, command.Operation);
            if (status > Stale || revision == 0) throw new ArgumentException("Invalid rear toe adjustment reply.");
            var bytes = new byte[command.Operation == 0 ? 9 : 13];
            bytes[0] = ReplyMarker; bytes[1] = (byte)command.Side;
            bytes[2] = unchecked((byte)command.Direction); bytes[3] = command.ExpectedValue;
            int statusOffset = command.Operation == 0 ? 4 : 8;
            if (command.Operation != 0) BitConverter.GetBytes(command.Operation).CopyTo(bytes, 4);
            bytes[statusOffset] = status;
            BitConverter.GetBytes(revision).CopyTo(bytes, statusOffset + 1);
            return bytes;
        }

        public static bool TryDecodeReply(byte[]? payload, out VehicleToeCommand command, out byte status, out uint revision)
        {
            command = default; status = Rejected; revision = 0;
            if (payload == null || (payload.Length != 9 && payload.Length != 13) || payload[0] != ReplyMarker) return false;
            int statusOffset = payload.Length == 9 ? 4 : 8;
            uint operation = payload.Length == 13 ? BitConverter.ToUInt32(payload, 4) : 0;
            uint decodedRevision = BitConverter.ToUInt32(payload, statusOffset + 1);
            if (payload[statusOffset] > Stale || decodedRevision == 0 || (payload.Length == 13 && operation == 0)) return false;
            try { command = new VehicleToeCommand((VehicleToeSide)payload[1], unchecked((sbyte)payload[2]), payload[3], operation); }
            catch (ArgumentException) { return false; }
            status = payload[statusOffset]; revision = decodedRevision;
            return true;
        }

        private static void ValidateTime(double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now)) throw new ArgumentOutOfRangeException(nameof(now));
        }
    }
}
