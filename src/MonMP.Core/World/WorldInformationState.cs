using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MonMP.Core.World
{
    public readonly struct PoliceTicketDisplay
    {
        public readonly byte Type;
        public readonly float PlayerSpeed, SpeedLimit, Price;
        public PoliceTicketDisplay(byte type, float playerSpeed, float speedLimit, float price)
        {
            if (type > 4 || !Valid(playerSpeed, 10000) || !Valid(speedLimit, 10000) || !Valid(price, 10000000))
                throw new InvalidDataException("Invalid police ticket display.");
            Type = type; PlayerSpeed = playerSpeed; SpeedLimit = speedLimit; Price = price;
        }
        private static bool Valid(float value, float maximum) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0 && value <= maximum;
    }

    // Host quotes only. These values never create tickets or perform transactions on a guest.
    public sealed class WorldInformationState
    {
        public const ushort Adapter = 145;
        public const byte Version = 1;
        public const int MaximumTickets = 10, HeaderSize = 23, TicketSize = 13;
        public static readonly ulong Entity = StaticId.Hash("shared-world-information/v1");
        public readonly float SuperPrice, OrdinaryPrice, DockSuperPrice, DockOrdinaryPrice, PoliceTotal;
        public readonly bool PoliceAvailable;
        public IReadOnlyList<PoliceTicketDisplay> Tickets { get; }

        public WorldInformationState(float superPrice, float ordinaryPrice, float dockSuperPrice, float dockOrdinaryPrice,
            bool policeAvailable, float policeTotal, IEnumerable<PoliceTicketDisplay> tickets)
        {
            if (!Valid(superPrice, 1000) || !Valid(ordinaryPrice, 1000) || !Valid(dockSuperPrice, 1000) ||
                !Valid(dockOrdinaryPrice, 1000) || !Valid(policeTotal, 1000000000))
                throw new InvalidDataException("Invalid world information quote.");
            var selected = tickets.Take(MaximumTickets + 1).ToArray();
            if (selected.Length > MaximumTickets || (!policeAvailable && (policeTotal != 0 || selected.Length != 0)))
                throw new InvalidDataException("Invalid police information layout.");
            // Default structs and externally supplied collections must satisfy the same bounds as decoding.
            foreach (var ticket in selected) _ = new PoliceTicketDisplay(ticket.Type, ticket.PlayerSpeed, ticket.SpeedLimit, ticket.Price);
            SuperPrice = superPrice; OrdinaryPrice = ordinaryPrice; DockSuperPrice = dockSuperPrice; DockOrdinaryPrice = dockOrdinaryPrice;
            PoliceAvailable = policeAvailable; PoliceTotal = policeTotal; Tickets = Array.AsReadOnly(selected);
        }

        private static bool Valid(float value, float maximum) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0 && value <= maximum;

        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Version);
            writer.Write(SuperPrice); writer.Write(OrdinaryPrice); writer.Write(DockSuperPrice); writer.Write(DockOrdinaryPrice);
            writer.Write(PoliceAvailable); writer.Write(PoliceTotal); writer.Write((byte)Tickets.Count);
            foreach (var ticket in Tickets)
            { writer.Write(ticket.Type); writer.Write(ticket.PlayerSpeed); writer.Write(ticket.SpeedLimit); writer.Write(ticket.Price); }
            return stream.ToArray();
        }

        public static WorldInformationState Decode(byte[] payload)
        {
            if (payload == null || payload.Length < HeaderSize || payload.Length > HeaderSize + MaximumTickets * TicketSize || payload[0] != Version)
                throw new InvalidDataException("Invalid world information payload.");
            using var stream = new MemoryStream(payload, false); using var reader = new BinaryReader(stream);
            reader.ReadByte();
            float super = reader.ReadSingle(), ordinary = reader.ReadSingle(), dockSuper = reader.ReadSingle(), dockOrdinary = reader.ReadSingle();
            byte available = reader.ReadByte(); float total = reader.ReadSingle(); int count = reader.ReadByte();
            if (available > 1 || count > MaximumTickets || payload.Length != HeaderSize + count * TicketSize)
                throw new InvalidDataException("Invalid police information payload.");
            var tickets = new PoliceTicketDisplay[count];
            for (int i = 0; i < count; i++) tickets[i] = new PoliceTicketDisplay(reader.ReadByte(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            return new WorldInformationState(super, ordinary, dockSuper, dockOrdinary, available == 1, total, tickets);
        }
    }
}
