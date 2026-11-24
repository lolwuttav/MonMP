using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MonMP.Core.World
{
    public sealed class ProgressState
    {
        public const ushort Adapter = 10;
        public static readonly ulong Entity = StaticId.Hash("shared-todo/v1");
        public IReadOnlyList<bool> Completed { get; }
        public IReadOnlyList<bool> VisibleGroups { get; }
        public ProgressState(IEnumerable<bool> completed, IEnumerable<bool> visibleGroups)
        {
            Completed = Array.AsReadOnly(completed.ToArray()); VisibleGroups = Array.AsReadOnly(visibleGroups.ToArray());
            if (Completed.Count < 1 || Completed.Count > 256 || VisibleGroups.Count < 1 || VisibleGroups.Count > 32) throw new InvalidDataException("Invalid progression layout.");
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write((ushort)Completed.Count); writer.Write((byte)VisibleGroups.Count);
            foreach (bool value in Completed.Concat(VisibleGroups)) writer.Write(value); return stream.ToArray();
        }
        public static ProgressState Decode(byte[] payload)
        {
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            int lines = reader.ReadUInt16(), groups = reader.ReadByte();
            if (lines < 1 || lines > 256 || groups < 1 || groups > 32 || payload.Length != lines + groups + 3) throw new InvalidDataException("Invalid progression payload.");
            bool[] Read(int count)
            { var values = new bool[count]; for (int i = 0; i < count; i++) { byte value = reader.ReadByte(); if (value > 1) throw new InvalidDataException("Invalid progression flag."); values[i] = value == 1; } return values; }
            return new ProgressState(Read(lines), Read(groups));
        }
    }
}
