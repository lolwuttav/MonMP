using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    public enum ElectricPercolatorAction : byte { BrewOrServe = 1, Power = 2 }
    public enum ElectricPercolatorStatus : byte
    {
        Accepted = 1, Brewing, Poured, Busy, TooFar, HandsBusy, NoPower, TooCold,
        NoBasket, BasketEmpty, NotCoffee, NotEnoughCoffee, NotReady
    }

    public readonly struct ElectricPercolatorRequest
    {
        public const byte Marker = 0xF2;
        public const int Size = 58;
        public readonly Guid Operation;
        public readonly ElectricPercolatorAction Action;
        public readonly ItemPose Pose;
        public readonly ulong Basket;
        public readonly uint BasketGeneration;

        public ElectricPercolatorRequest(Guid operation, ElectricPercolatorAction action, ItemPose pose,
            ulong basket = 0, uint basketGeneration = 0)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(ElectricPercolatorAction), action))
                throw new InvalidDataException("Invalid electric percolator request.");
            if ((basket == 0) != (basketGeneration == 0) || (action == ElectricPercolatorAction.Power && basket != 0))
                throw new InvalidDataException("Invalid electric percolator basket reference.");
            pose.Validate(); Operation = operation; Action = action; Pose = pose; Basket = basket; BasketGeneration = basketGeneration;
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Operation.ToByteArray()); writer.Write((byte)Action); Pose.Write(writer); writer.Write(Basket); writer.Write(BasketGeneration);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static ElectricPercolatorRequest Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid electric percolator request payload.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            reader.ReadByte();
            var request = new ElectricPercolatorRequest(new Guid(reader.ReadBytes(16)), (ElectricPercolatorAction)reader.ReadByte(),
                ItemPose.Read(reader), reader.ReadUInt64(), reader.ReadUInt32());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing electric percolator request data.");
            return request;
        }
    }

    public readonly struct ElectricPercolatorReply
    {
        public const int Size = 19;
        public readonly Guid Operation;
        public readonly ElectricPercolatorAction Action;
        public readonly ElectricPercolatorStatus Status;
        public ElectricPercolatorReply(Guid operation, ElectricPercolatorAction action, ElectricPercolatorStatus status)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(ElectricPercolatorAction), action) ||
                !Enum.IsDefined(typeof(ElectricPercolatorStatus), status)) throw new InvalidDataException("Invalid electric percolator reply.");
            Operation = operation; Action = action; Status = status;
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(ElectricPercolatorRequest.Marker); writer.Write(Operation.ToByteArray()); writer.Write((byte)Action); writer.Write((byte)Status);
            return stream.ToArray();
        }
        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == ElectricPercolatorRequest.Marker;
        public static ElectricPercolatorReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid electric percolator reply payload.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            reader.ReadByte();
            var reply = new ElectricPercolatorReply(new Guid(reader.ReadBytes(16)), (ElectricPercolatorAction)reader.ReadByte(), (ElectricPercolatorStatus)reader.ReadByte());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing electric percolator reply data.");
            return reply;
        }
    }

    public enum ElectricPercolatorClaim : byte { Claimed, Duplicate, Capacity }

    public sealed class ElectricPercolatorClaims
    {
        public const int Capacity = 512;
        private readonly HashSet<(uint Peer, Guid Operation)> claims = new HashSet<(uint, Guid)>();
        public int Count => claims.Count;
        public ElectricPercolatorClaim Claim(uint peer, Guid operation)
        {
            if (peer == 0 || operation == Guid.Empty) throw new InvalidDataException("Invalid electric percolator operation identity.");
            var key = (peer, operation);
            if (claims.Contains(key)) return ElectricPercolatorClaim.Duplicate;
            if (claims.Count >= Capacity) return ElectricPercolatorClaim.Capacity;
            claims.Add(key); return ElectricPercolatorClaim.Claimed;
        }
        public void Clear() => claims.Clear();
    }
}
