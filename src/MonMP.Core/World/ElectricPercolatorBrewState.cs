using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum ElectricPercolatorPhase : byte { Idle, Filling, Filled, Closing, Heating, SteamGrowing, Brewing, Finishing }

    // Current host-owned presentation, separate from the item values that own ingredients/output.
    // Elapsed is scaled native time at capture; observers advance only its visual timeline.
    public readonly struct ElectricPercolatorBrewState
    {
        public const ushort Adapter = 140;
        public const int Size = 21;
        public readonly ulong Item;
        public readonly uint Sequence;
        public readonly ElectricPercolatorPhase Phase;
        public readonly float Elapsed, Duration;
        public bool Active => Phase != ElectricPercolatorPhase.Idle;

        public ElectricPercolatorBrewState(ulong item, uint sequence, bool active, float elapsed = 0, float duration = 0)
        {
            if (item <= WorldRecord.DynamicIdStart || !ItemPose.FiniteValue(elapsed, 3600) || elapsed < 0 ||
                !ItemPose.FiniteValue(duration, 3600) || (active && (sequence == 0 || duration < 20 || elapsed > duration)) ||
                (!active && (elapsed != 0 || duration != 0))) throw new InvalidDataException("Invalid electric brew presentation.");
            Item = item; Sequence = sequence; Elapsed = elapsed; Duration = duration;
            Phase = active ? PhaseAt(elapsed, duration) : ElectricPercolatorPhase.Idle;
        }

        public static ulong RecordId(ulong item) => WorldRecord.DynamicIdStart |
            Math.Max(1UL, StaticId.Hash(item.ToString("X16") + "/electric-brew-v1") & 0x7FFFFFFFFFFFFFFFUL);

        public static ElectricPercolatorPhase PhaseAt(float elapsed, float duration) => elapsed < 4 ? ElectricPercolatorPhase.Filling :
            elapsed < 4.5f ? ElectricPercolatorPhase.Filled : elapsed < 5.5f ? ElectricPercolatorPhase.Closing :
            elapsed < 8 ? ElectricPercolatorPhase.Heating : elapsed < 19 ? ElectricPercolatorPhase.SteamGrowing :
            elapsed < duration - 1 ? ElectricPercolatorPhase.Brewing : ElectricPercolatorPhase.Finishing;

        // A stalled/repaired observer remains busy until the host sends Idle, including at the end.
        public ElectricPercolatorBrewState Advance(float seconds) => !Active ? this :
            new ElectricPercolatorBrewState(Item, Sequence, true, Math.Min(Duration, Elapsed + Math.Max(0, seconds)), Duration);

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Item); writer.Write(Sequence); writer.Write((byte)Phase); writer.Write(Elapsed); writer.Write(Duration);
            return stream.ToArray();
        }

        public static ElectricPercolatorBrewState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid electric brew payload.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            ulong item = reader.ReadUInt64(); uint sequence = reader.ReadUInt32(); var phase = (ElectricPercolatorPhase)reader.ReadByte();
            var state = new ElectricPercolatorBrewState(item, sequence, phase != ElectricPercolatorPhase.Idle, reader.ReadSingle(), reader.ReadSingle());
            if (state.Phase != phase) throw new InvalidDataException("Electric brew phase disagrees with its timing.");
            return state;
        }
    }
}
