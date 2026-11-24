using System;
using System.IO;

namespace MonMP.Core.World
{
    public enum PortableConsoleSaveAction : byte { Begin = 1, Tick = 2, Complete = 3, Cancel = 4, Approved = 5, Rejected = 6 }

    public readonly struct PortableConsoleSaveRequest
    {
        public const byte Marker = 0xF6;
        public const int Size = 20;
        public readonly Guid Operation;
        public readonly PortableConsoleSaveAction Action;
        public readonly ushort Ticks;
        public PortableConsoleSaveRequest(Guid operation, PortableConsoleSaveAction action, int ticks = 0)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(PortableConsoleSaveAction), action) ||
                ticks < 0 || ticks > 179 || (action != PortableConsoleSaveAction.Tick && action != PortableConsoleSaveAction.Complete && ticks != 0))
                throw new InvalidDataException("Invalid handheld save request.");
            Operation = operation; Action = action; Ticks = (ushort)ticks;
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Marker); writer.Write((byte)Action); writer.Write(Operation.ToByteArray()); writer.Write(Ticks);
            return stream.ToArray();
        }
        public static bool Is(byte[] data) => data != null && data.Length == Size && data[0] == Marker;
        public static PortableConsoleSaveRequest Decode(byte[] data)
        {
            if (!Is(data)) throw new InvalidDataException("Invalid handheld save request size.");
            using var reader = new BinaryReader(new MemoryStream(data, false));
            reader.ReadByte(); var action = (PortableConsoleSaveAction)reader.ReadByte();
            return new PortableConsoleSaveRequest(new Guid(reader.ReadBytes(16)), action, reader.ReadUInt16());
        }
    }

    // One physical console, one save sequence. Completed/cancelled operations cannot be replayed.
    public sealed class PortableConsoleSaveBook
    {
        private readonly System.Collections.Generic.HashSet<(uint, Guid)> finished = new System.Collections.Generic.HashSet<(uint, Guid)>();
        private readonly System.Collections.Generic.Queue<(uint, Guid)> finishOrder = new System.Collections.Generic.Queue<(uint, Guid)>();
        public uint Owner { get; private set; } = ItemLease.Unowned;
        public Guid Operation { get; private set; }
        public int Ticks { get; private set; }
        private float started, seen;
        public bool Active => Owner != ItemLease.Unowned;
        public bool Matches(uint peer, Guid operation) => Active && Owner == peer && Operation == operation;
        public bool Begin(uint peer, Guid operation, float now)
        {
            if (peer == ItemLease.Unowned || operation == Guid.Empty || !ItemPose.Finite(now, float.MaxValue) ||
                finished.Contains((peer, operation))) return false;
            Expire(now);
            if (Active) return Matches(peer, operation);
            Owner = peer; Operation = operation; Ticks = 0; started = seen = now; return true;
        }
        public int Advance(uint peer, Guid operation, int ticks, float now)
        {
            if (!Matches(peer, operation) || !ItemPose.Finite(now, float.MaxValue) || now < started || now - seen > 8f || now - started > 60f ||
                ticks <= Ticks || ticks > 179 || ticks > (now - started) * 40f + 2f) return 0;
            int delta = ticks - Ticks; Ticks = ticks; seen = now; return delta;
        }
        public bool CanComplete(uint peer, Guid operation, int ticks, float now) =>
            Matches(peer, operation) && ticks == Ticks && ticks >= 120 && ticks <= 179 &&
            ItemPose.Finite(now, float.MaxValue) && now - started >= 5.5f && now - started <= 60f && now - seen <= 8f;
        public void End(uint peer, Guid operation)
        {
            if (!Matches(peer, operation)) return;
            finished.Add((peer, operation)); finishOrder.Enqueue((peer, operation));
            while (finishOrder.Count > 1024) finished.Remove(finishOrder.Dequeue());
            Owner = ItemLease.Unowned; Operation = Guid.Empty; Ticks = 0;
        }
        public void Expire(float now)
        { if (Active && (now < seen || now - seen > 8f || now - started > 60f)) End(Owner, Operation); }
    }
}
