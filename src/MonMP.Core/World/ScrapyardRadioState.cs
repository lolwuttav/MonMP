using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MonMP.Core.World
{
    // Canonical scrapyard radio slots. The host owns removals and NewDay respawns;
    // each slot stays in the native save through PlayerData.RadioSlotIsRemoved.
    public sealed class ScrapyardRadioState
    {
        public const ushort Adapter = 58;
        public const byte Version = 1;
        public const int MaxSlots = 64;
        public static readonly ulong Entity = StaticId.Hash("scrapyard/radio-slots/v1");

        private readonly bool[] removed;
        public IReadOnlyList<bool> Removed => Array.AsReadOnly(removed);
        public int Count => removed.Length;

        public ScrapyardRadioState(IEnumerable<bool> removed)
        {
            this.removed = (removed ?? throw new ArgumentNullException(nameof(removed))).ToArray();
            Validate();
        }

        public bool IsRemoved(int slot) => (uint)slot < (uint)removed.Length
            ? removed[slot]
            : throw new ArgumentOutOfRangeException(nameof(slot));

        public ScrapyardRadioState WithRemoved(int slot, bool value)
        {
            if ((uint)slot >= (uint)removed.Length) throw new ArgumentOutOfRangeException(nameof(slot));
            var copy = (bool[])removed.Clone();
            copy[slot] = value;
            return new ScrapyardRadioState(copy);
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(2 + (removed.Length + 7) / 8);
            using var writer = new BinaryWriter(stream);
            writer.Write(Version);
            writer.Write((byte)removed.Length);
            for (int start = 0; start < removed.Length; start += 8)
            {
                byte flags = 0;
                for (int bit = 0; bit < 8 && start + bit < removed.Length; bit++)
                    if (removed[start + bit]) flags |= (byte)(1 << bit);
                writer.Write(flags);
            }
            return stream.ToArray();
        }

        public static ScrapyardRadioState Decode(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 2 || reader.ReadByte() != Version)
                throw new InvalidDataException("Unsupported scrapyard radio state.");
            int count = reader.ReadByte();
            int packedBytes = (count + 7) / 8;
            if (count < 1 || count > MaxSlots || stream.Length - stream.Position != packedBytes)
                throw new InvalidDataException("Invalid scrapyard radio slot count or payload length.");
            var removed = new bool[count];
            for (int start = 0; start < count; start += 8)
            {
                byte flags = reader.ReadByte();
                int bits = Math.Min(8, count - start);
                if ((flags >> bits) != 0) throw new InvalidDataException("Scrapyard radio state has unused slot bits set.");
                for (int bit = 0; bit < bits; bit++) removed[start + bit] = (flags & (1 << bit)) != 0;
            }
            return new ScrapyardRadioState(removed);
        }

        private void Validate()
        {
            if (removed.Length < 1 || removed.Length > MaxSlots)
                throw new InvalidDataException("Invalid scrapyard radio slot count.");
        }

        public bool Equals(ScrapyardRadioState? other) => other != null && removed.SequenceEqual(other.removed);
        public override bool Equals(object? obj) => obj is ScrapyardRadioState other && Equals(other);
        public override int GetHashCode()
        {
            int hash = removed.Length;
            foreach (bool value in removed) hash = unchecked((hash * 31) + (value ? 1 : 0));
            return hash;
        }
    }

    public readonly struct ScrapyardRadioCommand
    {
        public const byte Marker = 0xEB;
        public const int Size = 8;
        public readonly uint Nonce;
        public readonly ushort Slot;

        public ScrapyardRadioCommand(uint nonce, int slot)
        {
            if (slot < 0 || slot >= ScrapyardRadioState.MaxSlots)
                throw new InvalidDataException("Invalid scrapyard radio slot.");
            Nonce = nonce;
            Slot = (ushort)slot;
            Validate();
        }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write((byte)1); writer.Write(Slot); writer.Write(Nonce);
            return stream.ToArray();
        }

        public static bool Is(byte[]? payload) => payload != null && payload.Length == Size && payload[0] == Marker;

        public static ScrapyardRadioCommand Decode(byte[] payload)
        {
            if (!Is(payload)) throw new InvalidDataException("Invalid scrapyard radio command size or marker.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            _ = reader.ReadByte();
            byte action = reader.ReadByte();
            if (action != 1) throw new InvalidDataException("Unknown scrapyard radio command.");
            ushort slot = reader.ReadUInt16();
            uint nonce = reader.ReadUInt32();
            return new ScrapyardRadioCommand(nonce, slot);
        }

        private void Validate()
        {
            if (Nonce == 0 || Slot >= ScrapyardRadioState.MaxSlots)
                throw new InvalidDataException("Invalid scrapyard radio command.");
        }
    }

    public readonly struct ScrapyardRadioReply
    {
        public const int Size = 5;
        public readonly uint Nonce;
        public readonly bool Accepted;

        public ScrapyardRadioReply(uint nonce, bool accepted) { Nonce = nonce; Accepted = accepted; }

        public byte[] Encode()
        {
            if (Nonce == 0) throw new InvalidDataException("Invalid scrapyard radio reply.");
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Nonce); writer.Write(Accepted);
            return stream.ToArray();
        }

        public static ScrapyardRadioReply Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid scrapyard radio reply size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            uint nonce = reader.ReadUInt32();
            byte accepted = reader.ReadByte();
            if (nonce == 0 || accepted > 1) throw new InvalidDataException("Invalid scrapyard radio reply.");
            return new ScrapyardRadioReply(nonce, accepted == 1);
        }
    }
}
