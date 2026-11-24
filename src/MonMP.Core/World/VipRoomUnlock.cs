using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    public enum VipRoomUnlockStatus : byte
    {
        Unlocked = 1, AlreadyUnlocked, Funds, DoorLocked, PriceChanged, Unavailable,
        TooFar, Busy, Uncertain, Capacity, Stale, RetryRejected
    }

    // The room is a progression purchase, with no held-item input or lease to consume.
    // It shares adapter 15 with its door, but has distinct command/reply markers.
    public readonly struct VipRoomUnlockRequest : IEquatable<VipRoomUnlockRequest>
    {
        public const byte Marker = 0xD0;
        public const int Size = 23;
        public readonly Guid Operation;
        public readonly byte Handle;
        public readonly bool ObservedOpen;
        public readonly float Price;

        public VipRoomUnlockRequest(Guid operation, byte handle, bool observedOpen, float price)
        {
            if (operation == Guid.Empty || float.IsNaN(price) || float.IsInfinity(price) || price < 0 || price > 10_000_000)
                throw new InvalidDataException("Invalid VIP room unlock request.");
            Operation = operation; Handle = handle; ObservedOpen = observedOpen; Price = price;
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Operation.ToByteArray()); writer.Write(Handle);
            writer.Write((byte)(ObservedOpen ? 1 : 0)); writer.Write(Price); return stream.ToArray();
        }
        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static VipRoomUnlockRequest Decode(byte[] payload)
        {
            if (!Is(payload) || payload[18] > 1) throw new InvalidDataException("Invalid VIP room unlock request payload.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            reader.ReadByte(); var operation = new Guid(reader.ReadBytes(16)); byte handle = reader.ReadByte();
            bool open = reader.ReadByte() == 1;
            return new VipRoomUnlockRequest(operation, handle, open, reader.ReadSingle());
        }
        public bool Equals(VipRoomUnlockRequest other) => Operation == other.Operation && Handle == other.Handle &&
            ObservedOpen == other.ObservedOpen && Price.Equals(other.Price);
        public override bool Equals(object? obj) => obj is VipRoomUnlockRequest other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Operation, Handle, ObservedOpen, Price);
    }

    public readonly struct VipRoomUnlockReply
    {
        public const byte Marker = 0xD1;
        public const int Size = 22;
        public readonly Guid Operation;
        public readonly VipRoomUnlockStatus Status;
        public readonly int SpentCents;
        public VipRoomUnlockReply(Guid operation, VipRoomUnlockStatus status, int spentCents = 0)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(VipRoomUnlockStatus), status) || spentCents < 0 || spentCents > 1_000_000_000 ||
                (spentCents != 0 && status != VipRoomUnlockStatus.Unlocked && status != VipRoomUnlockStatus.Uncertain))
                throw new InvalidDataException("Invalid VIP room unlock reply.");
            Operation = operation; Status = status; SpentCents = spentCents;
        }
        public byte[] Encode()
        {
            var result = new byte[Size]; result[0] = Marker;
            Operation.ToByteArray().CopyTo(result, 1); result[17] = (byte)Status;
            BitConverter.GetBytes(SpentCents).CopyTo(result, 18); return result;
        }
        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static VipRoomUnlockReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid VIP room unlock reply payload.");
            var operation = new byte[16]; Array.Copy(payload, 1, operation, 0, operation.Length);
            return new VipRoomUnlockReply(new Guid(operation), (VipRoomUnlockStatus)payload[17], BitConverter.ToInt32(payload, 18));
        }
    }

    public static class VipRoomUnlockPolicy
    {
        public static VipRoomUnlockStatus Validate(float quotedPrice, float currentPrice, bool blocked,
            bool visible, bool alreadyUnlocked, bool inRange, bool locked, bool observedMatches, bool canAfford)
        {
            if (quotedPrice != currentPrice) return VipRoomUnlockStatus.PriceChanged;
            if (blocked) return VipRoomUnlockStatus.Busy;
            if (!visible) return VipRoomUnlockStatus.Unavailable;
            if (!inRange) return VipRoomUnlockStatus.TooFar;
            // A second buyer's still-unlocked presentation must neither debit nor toggle again.
            if (alreadyUnlocked) return VipRoomUnlockStatus.AlreadyUnlocked;
            if (!observedMatches) return VipRoomUnlockStatus.Stale;
            if (locked) return VipRoomUnlockStatus.DoorLocked;
            if (!canAfford) return VipRoomUnlockStatus.Funds;
            return VipRoomUnlockStatus.Unlocked;
        }
    }

    // Main-thread native execution, claimed before any side effect. Results survive peer disconnects
    // for the entire world lifetime, including refusals and partially completed native transactions.
    public sealed class VipRoomUnlockLedger
    {
        public const int Capacity = 16384;
        private sealed class Entry
        {
            public readonly ulong Entity;
            public readonly VipRoomUnlockRequest Request;
            public VipRoomUnlockStatus Status = VipRoomUnlockStatus.Busy;
            public int SpentCents;
            public Entry(ulong entity, VipRoomUnlockRequest request) { Entity = entity; Request = request; }
        }
        private readonly Dictionary<(Guid Profile, Guid Operation), Entry> outcomes = new Dictionary<(Guid, Guid), Entry>();
        private bool executing;
        public bool Executing => executing;

        public VipRoomUnlockReply Execute(Guid profile, ulong entity, VipRoomUnlockRequest request, Func<(VipRoomUnlockStatus Status, int SpentCents)> action)
        {
            if (profile == Guid.Empty || request.Operation == Guid.Empty || entity == 0 || action == null) throw new InvalidDataException("Invalid VIP room unlock identity.");
            var key = (profile, request.Operation);
            if (outcomes.TryGetValue(key, out var previous))
                return previous.Entity == entity && previous.Request.Equals(request)
                    ? new VipRoomUnlockReply(request.Operation, previous.Status, previous.SpentCents)
                    : new VipRoomUnlockReply(request.Operation, VipRoomUnlockStatus.RetryRejected);
            if (outcomes.Count >= Capacity) return new VipRoomUnlockReply(request.Operation, VipRoomUnlockStatus.Capacity);
            var entry = new Entry(entity, request); outcomes.Add(key, entry);
            if (executing) return new VipRoomUnlockReply(request.Operation, entry.Status);
            executing = true;
            try
            {
                var result = action();
                // Validate before retaining a receipt supplied by the native execution boundary.
                var reply = new VipRoomUnlockReply(request.Operation, result.Status, result.SpentCents);
                entry.Status = reply.Status; entry.SpentCents = reply.SpentCents;
            }
            catch { entry.Status = VipRoomUnlockStatus.Uncertain; throw; }
            finally { executing = false; }
            return new VipRoomUnlockReply(request.Operation, entry.Status, entry.SpentCents);
        }
    }
}
