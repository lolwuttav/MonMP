using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MonMP.Core.World
{
    // A bucket is still a normal shared item. This bounded sidecar records the ordered
    // identities of its fish without putting Unity parent references on the wire.
    public sealed class FishBucketState
    {
        public const ushort Adapter = 44;
        public const int Capacity = 20;
        public const byte Version = 1;
        // Traffic owns 0x9000... plus its fixed pool slot. Keep fish sidecars in
        // 0x9800... through 0x9FFF..., below the garden's 0xA000... range.
        public const ulong IdBase = WorldRecord.DynamicIdStart | 0x1800000000000000;
        private const ulong SerialLimit = 0x0800000000000000;
        private readonly ulong[] fish;
        private readonly IReadOnlyList<ulong> fishView;
        public ulong Bucket { get; }
        public bool Exists { get; }
        public IReadOnlyList<ulong> Fish => fishView;
        public FishBucketState(ulong bucket, IEnumerable<ulong> fish, bool exists = true)
        {
            Bucket = bucket; Exists = exists; this.fish = (fish ?? throw new ArgumentNullException(nameof(fish))).ToArray();
            Validate();
            fishView = Array.AsReadOnly(this.fish);
        }
        public static ulong RecordId(ulong bucket)
        {
            if (bucket <= WorldRecord.DynamicIdStart) throw new InvalidDataException("Invalid fish bucket entity.");
            ulong serial = bucket - WorldRecord.DynamicIdStart;
            if (serial == 0 || serial >= SerialLimit) throw new InvalidDataException("Fish bucket entity is outside the adapter range.");
            return IdBase | serial;
        }
        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Version); writer.Write(Bucket); writer.Write(Exists); writer.Write((byte)fish.Length);
            foreach (ulong item in fish) writer.Write(item);
            return stream.ToArray();
        }
        public static FishBucketState Decode(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            byte version = reader.ReadByte(); if (version != Version) throw new InvalidDataException("Unsupported fish bucket state.");
            ulong bucket = reader.ReadUInt64(); byte exists = reader.ReadByte(); byte count = reader.ReadByte();
            if (exists > 1 || count > Capacity || stream.Length - stream.Position != count * sizeof(ulong)) throw new InvalidDataException("Invalid fish bucket contents length.");
            var fish = new ulong[count]; for (int i = 0; i < fish.Length; i++) fish[i] = reader.ReadUInt64();
            return new FishBucketState(bucket, fish, exists == 1);
        }
        private void Validate()
        {
            _ = RecordId(Bucket);
            if (fish.Length > Capacity || (!Exists && fish.Length != 0) || fish.Any(id => id <= WorldRecord.DynamicIdStart) ||
                fish.Distinct().Count() != fish.Length)
                throw new InvalidDataException("Invalid fish bucket contents.");
        }
    }

    public enum FishBucketAction : byte { Add = 1, Remove = 2 }

    // Remove carries the expected first fish, so two near-simultaneous right clicks
    // cannot remove successive contents from one stale view of the bucket.
    public readonly struct FishBucketCommand
    {
        public const byte Marker = 0xD4;
        public readonly uint Nonce, BucketGeneration, FishGeneration;
        public readonly ulong Fish;
        public readonly FishBucketAction Action;
        public FishBucketCommand(uint nonce, FishBucketAction action, uint bucketGeneration, ulong fish, uint fishGeneration)
        { Nonce = nonce; Action = action; BucketGeneration = bucketGeneration; Fish = fish; FishGeneration = fishGeneration; }
        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write(Nonce); writer.Write((byte)Action); writer.Write(BucketGeneration); writer.Write(Fish); writer.Write(FishGeneration);
            return stream.ToArray();
        }
        public static FishBucketCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 22) throw new InvalidDataException("Invalid fish bucket command length.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            if (reader.ReadByte() != Marker) throw new InvalidDataException("Invalid fish bucket command marker.");
            var command = new FishBucketCommand(reader.ReadUInt32(), (FishBucketAction)reader.ReadByte(), reader.ReadUInt32(), reader.ReadUInt64(), reader.ReadUInt32());
            command.Validate(); return command;
        }
        private void Validate()
        {
            if (Nonce == 0 || !Enum.IsDefined(typeof(FishBucketAction), Action) || BucketGeneration == 0 ||
                Fish <= WorldRecord.DynamicIdStart || FishGeneration == 0)
                throw new InvalidDataException("Invalid fish bucket command.");
        }
    }

    public readonly struct FishBucketReply
    {
        public readonly uint Nonce;
        public readonly bool Accepted;
        public FishBucketReply(uint nonce, bool accepted) { Nonce = nonce; Accepted = accepted; }
        public byte[] Encode()
        {
            if (Nonce == 0) throw new InvalidDataException("Invalid fish bucket reply.");
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Nonce); writer.Write(Accepted); return stream.ToArray();
        }
        public static FishBucketReply Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 5) throw new InvalidDataException("Invalid fish bucket reply.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            uint nonce = reader.ReadUInt32(); byte accepted = reader.ReadByte();
            if (nonce == 0 || accepted > 1) throw new InvalidDataException("Invalid fish bucket reply.");
            return new FishBucketReply(nonce, accepted == 1);
        }
    }
}
