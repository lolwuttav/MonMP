using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    public enum FenceAction : byte { Install = 1, BeginRemoval, CancelRemoval, ChangeStyle }
    public enum FenceActionClaim : byte { Claimed, Duplicate, Capacity }
    public enum FenceActionStatus : byte { Accepted = 1, Busy, TooFar, NotInstalled, NotOnHomeLand, NotReady, Capacity }

    public readonly struct FenceActionCommand
    {
        public const byte Marker = 0xF3;
        public const int Size = 50;
        public readonly Guid Operation;
        public readonly FenceAction Action;
        public readonly uint Generation;
        public readonly ItemPose Pose;
        public FenceActionCommand(Guid operation, FenceAction action, uint generation, ItemPose pose)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(FenceAction), action) ||
                (action == FenceAction.Install ? generation == 0 : generation != 0))
                throw new InvalidDataException("Invalid fence action command.");
            pose.Validate(); Operation = operation; Action = action; Generation = generation; Pose = pose;
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Operation.ToByteArray()); writer.Write((byte)Action); writer.Write(Generation); Pose.Write(writer);
            return stream.ToArray();
        }
        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;
        public static FenceActionCommand Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid fence action command payload.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            reader.ReadByte();
            var command = new FenceActionCommand(new Guid(reader.ReadBytes(16)), (FenceAction)reader.ReadByte(), reader.ReadUInt32(), ItemPose.Read(reader));
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing fence action command data.");
            return command;
        }
    }

    public readonly struct FenceActionReply
    {
        public const int Size = 19;
        public readonly Guid Operation;
        public readonly FenceAction Action;
        public readonly FenceActionStatus Status;
        public FenceActionReply(Guid operation, FenceAction action, FenceActionStatus status)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(FenceAction), action) || !Enum.IsDefined(typeof(FenceActionStatus), status))
                throw new InvalidDataException("Invalid fence action reply.");
            Operation = operation; Action = action; Status = status;
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(FenceActionCommand.Marker); writer.Write(Operation.ToByteArray()); writer.Write((byte)Action); writer.Write((byte)Status);
            return stream.ToArray();
        }
        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == FenceActionCommand.Marker;
        public static FenceActionReply Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid fence action reply payload.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            reader.ReadByte();
            var reply = new FenceActionReply(new Guid(reader.ReadBytes(16)), (FenceAction)reader.ReadByte(), (FenceActionStatus)reader.ReadByte());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing fence action reply data.");
            return reply;
        }
    }

    public sealed class FenceActionClaims
    {
        public const int Capacity = 512;
        private readonly HashSet<(uint Peer, Guid Operation)> claims = new HashSet<(uint, Guid)>();
        public int Count => claims.Count;
        public FenceActionClaim Claim(uint peer, Guid operation)
        {
            if (peer == 0 || operation == Guid.Empty) throw new InvalidDataException("Invalid fence operation identity.");
            var key = (peer, operation);
            if (claims.Contains(key)) return FenceActionClaim.Duplicate;
            if (claims.Count >= Capacity) return FenceActionClaim.Capacity;
            claims.Add(key); return FenceActionClaim.Claimed;
        }
        public void Clear() => claims.Clear();
    }
}
