using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MonMP.Core.World
{
    public enum NewspaperCardboxStatus : byte
    {
        Accepted = 0,
        NotReady = 1,
        ActionsBlocked = 2,
        TargetUnavailable = 3,
        OutOfRange = 4,
        HandsBusy = 5,
        CapReached = 6,
        SpawnFailed = 7
    }

    public readonly struct NewspaperJournalState : IEquatable<NewspaperJournalState>
    {
        public ulong BoxId { get; }
        public int JournalIndex { get; }
        public int JournalCount { get; }

        public NewspaperJournalState(ulong boxId, int journalIndex, int journalCount)
        {
            if (boxId == 0 || journalCount < 1 || journalCount > NewspaperCardboxState.MaxJournals ||
                journalIndex < 0 || journalIndex >= journalCount)
                throw new InvalidDataException("Invalid newspaper box journal state.");
            BoxId = boxId; JournalIndex = journalIndex; JournalCount = journalCount;
        }

        public bool Equals(NewspaperJournalState other) => BoxId == other.BoxId &&
            JournalIndex == other.JournalIndex && JournalCount == other.JournalCount;
        public override bool Equals(object? obj) => obj is NewspaperJournalState other && Equals(other);
        public override int GetHashCode() => unchecked(((BoxId.GetHashCode() * 31) + JournalIndex) * 31 + JournalCount);
    }

    // One world record carries every active native box's journal position and the shared item count.
    // The count follows the native >10 guard, while box IDs bind requests to an exact interaction.
    public readonly struct NewspaperCardboxState
    {
        public const ushort Adapter = 61;
        public const byte Version = 2;
        public const int MaxBoxes = 16;
        public const int MaxJournals = 64;
        public const int MaxObservedNewspapers = 4096;
        public const int NativeLimit = 10;
        public static readonly ulong Entity = StaticId.Hash("newspaper/cardbox/main/v1");

        private readonly NewspaperJournalState[] boxes;
        public IReadOnlyList<NewspaperJournalState> Boxes => Array.AsReadOnly(boxes ?? Array.Empty<NewspaperJournalState>());
        public int NewspaperCount { get; }

        public NewspaperCardboxState(IEnumerable<NewspaperJournalState> boxes, int newspaperCount)
        {
            this.boxes = (boxes ?? throw new ArgumentNullException(nameof(boxes))).OrderBy(box => box.BoxId).ToArray();
            if (this.boxes.Length < 1 || this.boxes.Length > MaxBoxes ||
                this.boxes.Select(box => box.BoxId).Distinct().Count() != this.boxes.Length ||
                newspaperCount < 0 || newspaperCount > MaxObservedNewspapers)
                throw new InvalidDataException("Invalid newspaper cardbox state.");
            NewspaperCount = newspaperCount;
        }

        public bool TryGetBox(ulong boxId, out NewspaperJournalState journal)
        {
            foreach (var box in boxes ?? Array.Empty<NewspaperJournalState>())
                if (box.BoxId == boxId) { journal = box; return true; }
            journal = default;
            return false;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(4 + (boxes.Length * 10));
            using var writer = new BinaryWriter(stream);
            writer.Write(Version);
            writer.Write((byte)boxes.Length);
            writer.Write((ushort)NewspaperCount);
            foreach (var box in boxes)
            {
                writer.Write(box.BoxId);
                writer.Write((byte)box.JournalCount);
                writer.Write((byte)box.JournalIndex);
            }
            return stream.ToArray();
        }

        public static NewspaperCardboxState Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 14) throw new InvalidDataException("Invalid newspaper cardbox state size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadByte() != Version) throw new InvalidDataException("Unsupported newspaper cardbox state version.");
            int boxCount = reader.ReadByte();
            int newspaperCount = reader.ReadUInt16();
            if (boxCount < 1 || boxCount > MaxBoxes || stream.Length != 4 + (boxCount * 10))
                throw new InvalidDataException("Invalid newspaper box count or state size.");
            var boxes = new NewspaperJournalState[boxCount];
            for (int i = 0; i < boxes.Length; i++)
            {
                ulong boxId = reader.ReadUInt64();
                int journalCount = reader.ReadByte();
                int journalIndex = reader.ReadByte();
                boxes[i] = new NewspaperJournalState(boxId, journalIndex, journalCount);
            }
            return new NewspaperCardboxState(boxes, newspaperCount);
        }

        public bool Equals(NewspaperCardboxState other) => NewspaperCount == other.NewspaperCount &&
            (boxes ?? Array.Empty<NewspaperJournalState>()).SequenceEqual(other.boxes ?? Array.Empty<NewspaperJournalState>());
        public override bool Equals(object? obj) => obj is NewspaperCardboxState other && Equals(other);
        public override int GetHashCode()
        {
            int hash = NewspaperCount;
            foreach (var box in boxes ?? Array.Empty<NewspaperJournalState>()) hash = unchecked((hash * 31) + box.GetHashCode());
            return hash;
        }
    }

    public readonly struct NewspaperCardboxRequest
    {
        public const byte Marker = 0xEE;
        public const int Size = 25;
        public Guid Operation { get; }
        public ulong BoxId { get; }

        public NewspaperCardboxRequest(Guid operation, ulong boxId)
        {
            if (operation == Guid.Empty || boxId == 0) throw new InvalidDataException("Invalid newspaper cardbox operation or target.");
            Operation = operation; BoxId = boxId;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write(BoxId);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public static NewspaperCardboxRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid newspaper cardbox request size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            var operation = new Guid(reader.ReadBytes(16));
            return new NewspaperCardboxRequest(operation, reader.ReadUInt64());
        }
    }

    public readonly struct NewspaperCardboxReply
    {
        public const byte Marker = 0xEF;
        public const int Size = 18;
        public Guid Operation { get; }
        public NewspaperCardboxStatus Status { get; }

        public NewspaperCardboxReply(Guid operation, NewspaperCardboxStatus status)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(NewspaperCardboxStatus), status))
                throw new InvalidDataException("Invalid newspaper cardbox reply.");
            Operation = operation;
            Status = status;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker);
            writer.Write(Operation.ToByteArray());
            writer.Write((byte)Status);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public static NewspaperCardboxReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid newspaper cardbox reply size or marker.");
            using var reader = new BinaryReader(new MemoryStream(payload, false));
            _ = reader.ReadByte();
            return new NewspaperCardboxReply(new Guid(reader.ReadBytes(16)), (NewspaperCardboxStatus)reader.ReadByte());
        }
    }

    public static class NewspaperCardboxPolicy
    {
        // Native MouseDown refuses when the pre-spawn count is greater than ten, so a
        // count of exactly ten is allowed to become eleven and the following request fails.
        public static NewspaperCardboxStatus Validate(bool worldReady, bool actionsBlocked, bool requestBudget,
            bool exactRegisteredBox, bool activeBox, bool freshWalkingPose, bool inRange, bool handsFree,
            int observedNewspaperCount)
        {
            if (!worldReady) return NewspaperCardboxStatus.NotReady;
            if (actionsBlocked || !requestBudget) return NewspaperCardboxStatus.ActionsBlocked;
            if (!exactRegisteredBox || !activeBox) return NewspaperCardboxStatus.TargetUnavailable;
            if (!freshWalkingPose || !inRange) return NewspaperCardboxStatus.OutOfRange;
            if (!handsFree) return NewspaperCardboxStatus.HandsBusy;
            if (observedNewspaperCount < 0 || observedNewspaperCount > NewspaperCardboxState.MaxObservedNewspapers ||
                observedNewspaperCount > NewspaperCardboxState.NativeLimit) return NewspaperCardboxStatus.CapReached;
            return NewspaperCardboxStatus.Accepted;
        }
    }
}
