using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    // The host bunker fertilizer reservoir is native save data. This small record
    // carries its canonical value to guests; bowls remain ordinary SharedItems records.
    public readonly struct LeoSuppliesState
    {
        public const ushort Adapter = 45;
        public const int Size = 4;
        public static readonly ulong Entity = StaticId.Hash("home-bunker-fertilizer-reservoir/v1");
        public readonly float Reservoir;

        public LeoSuppliesState(float reservoir) { Reservoir = reservoir; Validate(); }

        public byte[] Encode()
        {
            Validate();
            using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Reservoir); return stream.ToArray();
        }

        public static LeoSuppliesState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid Leo supplies state length.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            var state = new LeoSuppliesState(reader.ReadSingle());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing Leo supplies state.");
            return state;
        }

        public bool Equals(LeoSuppliesState other) => Reservoir.Equals(other.Reservoir);
        public override bool Equals(object? obj) => obj is LeoSuppliesState other && Equals(other);
        public override int GetHashCode() => Reservoir.GetHashCode();

        private void Validate()
        {
            if (!ItemPose.Finite(Reservoir, 1) || Reservoir < 0 || Reservoir > 1)
                throw new InvalidDataException("Invalid Leo supplies reservoir amount.");
        }
    }

    public enum LeoSuppliesAction : byte { FillDryBowl = 1, UseWateringCan = 2, FillBunkerReservoir = 3 }

    // A request binds a native effect to a live shared-tool lease. Target ids are
    // identities from the host's catalog; the payload never carries native paths.
    public readonly struct LeoSuppliesCommand
    {
        public const byte Marker = 0xD9;
        public const int Size = 31;
        public const float MaxWateringDuration = 0.25f;
        public readonly LeoSuppliesAction Action;
        public readonly ulong Tool;
        public readonly uint Generation, Sequence;
        public readonly ulong Target;
        public readonly byte BowlIndex;
        public readonly float Duration;

        public LeoSuppliesCommand(LeoSuppliesAction action, ulong tool, uint generation, uint sequence,
            ulong target = 0, byte bowlIndex = 0, float duration = 0)
        {
            Action = action; Tool = tool; Generation = generation; Sequence = sequence;
            Target = target; BowlIndex = bowlIndex; Duration = duration; Validate();
        }

        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write((byte)Action); writer.Write(Tool); writer.Write(Generation); writer.Write(Sequence);
            writer.Write(Target); writer.Write(BowlIndex); writer.Write(Duration); return stream.ToArray();
        }

        public static LeoSuppliesCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid Leo supplies command length.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            if (reader.ReadByte() != Marker) throw new InvalidDataException("Invalid Leo supplies command marker.");
            var command = new LeoSuppliesCommand((LeoSuppliesAction)reader.ReadByte(), reader.ReadUInt64(), reader.ReadUInt32(),
                reader.ReadUInt32(), reader.ReadUInt64(), reader.ReadByte(), reader.ReadSingle());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing Leo supplies command.");
            return command;
        }

        private void Validate()
        {
            if (!Enum.IsDefined(typeof(LeoSuppliesAction), Action) || Tool <= WorldRecord.DynamicIdStart || Generation == 0 || Sequence == 0)
                throw new InvalidDataException("Invalid Leo supplies command identity.");
            switch (Action)
            {
                case LeoSuppliesAction.FillDryBowl:
                    if (Target <= WorldRecord.DynamicIdStart || BowlIndex > 1 || Duration != 0)
                        throw new InvalidDataException("Invalid Leo dry-food target.");
                    break;
                case LeoSuppliesAction.UseWateringCan:
                    if ((Target == 0 && BowlIndex != 0) || (Target != 0 && (Target <= WorldRecord.DynamicIdStart || BowlIndex > 1)) ||
                        !ItemPose.Finite(Duration, MaxWateringDuration) || Duration <= 0)
                        throw new InvalidDataException("Invalid Leo watering request.");
                    break;
                case LeoSuppliesAction.FillBunkerReservoir:
                    if (Target != 0 || BowlIndex != 0 || Duration != 0)
                        throw new InvalidDataException("Invalid Leo bunker-fertilizer target.");
                    break;
                default:
                    throw new InvalidDataException("Unknown Leo supplies action.");
            }
        }
    }

    public enum LeoSuppliesSequenceResult : byte { Accepted, Duplicate, OutOfOrder, Capacity }

    // Replay protection is one sequence per source item lease. A new generation
    // resets its sequence; old generations can never replay into the new owner.
    public sealed class LeoSuppliesSequences
    {
        public const int Capacity = 2048;
        private readonly Dictionary<ulong, (uint Peer, uint Generation, uint Sequence)> latest =
            new Dictionary<ulong, (uint, uint, uint)>();
        public int Count => latest.Count;

        public LeoSuppliesSequenceResult Claim(uint peer, LeoSuppliesCommand command)
        {
            if (peer == 0 || command.Tool <= WorldRecord.DynamicIdStart || command.Generation == 0 || command.Sequence == 0)
                throw new InvalidDataException("Invalid Leo supplies sequence identity.");
            if (!latest.TryGetValue(command.Tool, out var current))
            {
                if (latest.Count >= Capacity) return LeoSuppliesSequenceResult.Capacity;
                if (command.Sequence != 1) return LeoSuppliesSequenceResult.OutOfOrder;
                latest.Add(command.Tool, (peer, command.Generation, command.Sequence));
                return LeoSuppliesSequenceResult.Accepted;
            }
            if (current.Peer != peer || current.Generation != command.Generation)
            {
                if (command.Sequence != 1) return LeoSuppliesSequenceResult.OutOfOrder;
                latest[command.Tool] = (peer, command.Generation, command.Sequence);
                return LeoSuppliesSequenceResult.Accepted;
            }
            if (command.Sequence <= current.Sequence) return LeoSuppliesSequenceResult.Duplicate;
            if (command.Sequence != current.Sequence + 1) return LeoSuppliesSequenceResult.OutOfOrder;
            latest[command.Tool] = (peer, command.Generation, command.Sequence);
            return LeoSuppliesSequenceResult.Accepted;
        }

        public void RemovePeer(uint peer)
        {
            foreach (var tool in new List<ulong>(latest.Keys))
                if (latest[tool].Peer == peer) latest.Remove(tool);
        }

        public void Clear() => latest.Clear();
    }

    public enum LeoSuppliesReplyStatus : byte { Applied = 1, Rejected = 2, Duplicate = 3, Capacity = 4 }

    public readonly struct LeoSuppliesReply
    {
        public const int Size = 17;
        public readonly ulong Tool;
        public readonly uint Generation, Sequence;
        public readonly LeoSuppliesReplyStatus Status;
        public LeoSuppliesReply(ulong tool, uint generation, uint sequence, LeoSuppliesReplyStatus status)
        { Tool = tool; Generation = generation; Sequence = sequence; Status = status; Validate(); }
        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(Size); using var writer = new BinaryWriter(stream);
            writer.Write(Tool); writer.Write(Generation); writer.Write(Sequence); writer.Write((byte)Status); return stream.ToArray();
        }
        public static LeoSuppliesReply Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid Leo supplies reply length.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            var reply = new LeoSuppliesReply(reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadUInt32(), (LeoSuppliesReplyStatus)reader.ReadByte());
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing Leo supplies reply.");
            return reply;
        }
        private void Validate()
        {
            if (Tool <= WorldRecord.DynamicIdStart || Generation == 0 || Sequence == 0 ||
                !Enum.IsDefined(typeof(LeoSuppliesReplyStatus), Status))
                throw new InvalidDataException("Invalid Leo supplies reply.");
        }
    }
}
