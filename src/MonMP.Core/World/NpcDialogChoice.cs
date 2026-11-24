using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MonMP.Core.World
{
    public enum NpcDialogStatus : byte { Offered = 1, Applied, NotEnoughCash, Unavailable, OutOfRange }

    // A choice is authorized by a short-lived host offer from the actor's native
    // DialogStarted path. Actor identity alone never authorizes a progression action.
    public readonly struct NpcDialogChoiceRequest
    {
        public const byte Marker = 0xBB;
        public const int Size = 41;
        public Guid Operation { get; }
        public ulong Actor { get; }
        public Guid Offer { get; }
        public NpcDialogChoiceRequest(Guid operation, ulong actor, Guid offer)
        {
            if (operation == Guid.Empty || actor == 0 || offer == Guid.Empty) throw new InvalidDataException("Invalid NPC dialog choice.");
            Operation = operation; Actor = actor; Offer = offer;
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Operation.ToByteArray()); writer.Write(Actor); writer.Write(Offer.ToByteArray());
            return stream.ToArray();
        }
        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static NpcDialogChoiceRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid NPC dialog choice payload.");
            using var reader = new BinaryReader(new MemoryStream(payload)); reader.ReadByte();
            return new NpcDialogChoiceRequest(new Guid(reader.ReadBytes(16)), reader.ReadUInt64(), new Guid(reader.ReadBytes(16)));
        }
    }

    public readonly struct NpcDialogReply
    {
        public const byte Marker = 0xBC;
        public const int Size = 51;
        public Guid Operation { get; }
        public ulong Actor { get; }
        public Guid Offer { get; }
        public ulong GreetingSequence { get; }
        public NpcDialogStatus Status { get; }
        public bool FullFriendship { get; }
        public NpcDialogReply(Guid operation, ulong actor, Guid offer, ulong greetingSequence, NpcDialogStatus status, bool fullFriendship = false)
        {
            if (operation == Guid.Empty || actor == 0 || offer == Guid.Empty || !Enum.IsDefined(typeof(NpcDialogStatus), status))
                throw new InvalidDataException("Invalid NPC dialog reply.");
            Operation = operation; Actor = actor; Offer = offer; GreetingSequence = greetingSequence; Status = status; FullFriendship = fullFriendship;
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Operation.ToByteArray()); writer.Write(Actor); writer.Write(Offer.ToByteArray());
            writer.Write(GreetingSequence); writer.Write((byte)Status); writer.Write(FullFriendship); return stream.ToArray();
        }
        public static bool Is(byte[] payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static NpcDialogReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid NPC dialog reply payload.");
            using var reader = new BinaryReader(new MemoryStream(payload)); reader.ReadByte();
            var operation = new Guid(reader.ReadBytes(16)); ulong actor = reader.ReadUInt64(); var offer = new Guid(reader.ReadBytes(16));
            ulong sequence = reader.ReadUInt64(); var status = (NpcDialogStatus)reader.ReadByte(); byte fullFriendship = reader.ReadByte();
            if (fullFriendship > 1) throw new InvalidDataException("Invalid NPC dialog achievement state.");
            return new NpcDialogReply(operation, actor, offer, sequence, status, fullFriendship != 0);
        }
    }

    public sealed class NpcDialogChoiceAuthority
    {
        private readonly Dictionary<uint, (ulong Actor, Guid Token, double Expires)> offers = new Dictionary<uint, (ulong, Guid, double)>();
        private readonly Dictionary<(uint Peer, Guid Operation), (ulong Actor, Guid Offer, NpcDialogStatus Status)> results =
            new Dictionary<(uint, Guid), (ulong, Guid, NpcDialogStatus)>();
        public Guid Issue(uint peer, ulong actor, double now)
        {
            if (peer == 0 || actor == 0 || double.IsNaN(now) || double.IsInfinity(now) || results.Count >= 8192) return Guid.Empty;
            var token = Guid.NewGuid(); offers[peer] = (actor, token, now + 30); return token;
        }
        public NpcDialogStatus Execute(uint peer, NpcDialogChoiceRequest request, double now,
            Func<NpcDialogStatus> validate, Func<NpcDialogStatus> apply)
        {
            var key = (Peer: peer, Operation: request.Operation);
            if (results.TryGetValue(key, out var previous))
                return previous.Actor == request.Actor && previous.Offer == request.Offer ? previous.Status : NpcDialogStatus.Unavailable;
            if (peer == 0 || results.Count >= 8192 || !offers.TryGetValue(peer, out var offer) ||
                offer.Actor != request.Actor || offer.Token != request.Offer || double.IsNaN(now) || double.IsInfinity(now) ||
                now < offer.Expires - 30 || now > offer.Expires) return NpcDialogStatus.Unavailable;
            offers.Remove(peer);
            // Record a consumed outcome before callbacks. An exception after a native
            // debit or progression mutation must not make the token reusable.
            results.Add(key, (request.Actor, request.Offer, NpcDialogStatus.Unavailable));
            var status = validate();
            if (status == NpcDialogStatus.Applied) status = apply();
            results[key] = (request.Actor, request.Offer, status);
            return status;
        }
        public void RemovePeer(uint peer) => offers.Remove(peer);
        public void Clear() { offers.Clear(); results.Clear(); }
    }

    public readonly struct NpcDialogGreetingPart
    {
        public const byte Marker = 0xBD;
        public const int MaxPartBytes = 108;
        public Guid Operation { get; }
        public byte Index { get; }
        public byte Count { get; }
        public byte[] Bytes { get; }
        public NpcDialogGreetingPart(Guid operation, byte index, byte count, byte[] bytes)
        {
            if (operation == Guid.Empty || count == 0 || count > 2 || index >= count || bytes == null ||
                bytes.Length == 0 || bytes.Length > MaxPartBytes) throw new InvalidDataException("Invalid NPC greeting part.");
            Operation = operation; Index = index; Count = count; Bytes = (byte[])bytes.Clone();
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Operation.ToByteArray()); writer.Write(Index); writer.Write(Count); writer.Write(Bytes);
            return stream.ToArray();
        }
        public static bool Is(byte[] payload) => payload != null && payload.Length > 19 && payload.Length <= 127 && payload[0] == Marker;
        public static NpcDialogGreetingPart Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid NPC greeting part payload.");
            using var reader = new BinaryReader(new MemoryStream(payload)); reader.ReadByte();
            return new NpcDialogGreetingPart(new Guid(reader.ReadBytes(16)), reader.ReadByte(), reader.ReadByte(), reader.ReadBytes(payload.Length - 19));
        }
        public static NpcDialogGreetingPart[] Split(Guid operation, string greeting)
        {
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(greeting);
            if (bytes.Length == 0 || bytes.Length > NpcInteractionState.MaxGreetingBytes) throw new InvalidDataException("Invalid NPC offered greeting.");
            byte count = (byte)((bytes.Length + MaxPartBytes - 1) / MaxPartBytes);
            return Enumerable.Range(0, count).Select(index => new NpcDialogGreetingPart(operation, (byte)index, count,
                bytes.Skip(index * MaxPartBytes).Take(MaxPartBytes).ToArray())).ToArray();
        }
    }

    public sealed class NpcDialogGreetingAssembler
    {
        private readonly Guid operation;
        private readonly byte[][] parts;
        public NpcDialogGreetingAssembler(NpcDialogGreetingPart first) { operation = first.Operation; parts = new byte[first.Count][]; Add(first); }
        public void Add(NpcDialogGreetingPart part)
        {
            if (part.Operation != operation || part.Count != parts.Length ||
                (parts[part.Index] != null && !parts[part.Index].SequenceEqual(part.Bytes))) throw new InvalidDataException("NPC offered greeting parts disagree.");
            parts[part.Index] = part.Bytes;
            if (parts.Where(partBytes => partBytes != null).Sum(partBytes => partBytes.Length) > NpcInteractionState.MaxGreetingBytes)
                throw new InvalidDataException("NPC offered greeting exceeds its bound.");
        }
        public bool TryRead(out string greeting)
        {
            greeting = "";
            if (parts.Any(part => part == null)) return false;
            try { greeting = new UTF8Encoding(false, true).GetString(parts.SelectMany(part => part).ToArray()); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("NPC offered greeting is not UTF8.", ex); }
            return true;
        }
    }
}
