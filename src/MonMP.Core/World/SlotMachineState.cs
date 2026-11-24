using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    public sealed class SlotMachineState
    {
        public const ushort Adapter = 37;
        public const int Size = 20;
        // Reel stop positions 0..13, or UnknownReel while that reel is spinning or not yet known.
        public const byte UnknownReel = 255, ReelStops = 14;
        public int Bet { get; }
        public int Credit { get; }
        public int WinnerPaid { get; }
        public int LossAmount { get; }
        public bool Spinning { get; }
        public byte Reel1 { get; }
        public byte Reel2 { get; }
        public byte Reel3 { get; }

        public SlotMachineState(int bet, int credit, int winnerPaid, int lossAmount, bool spinning = false,
            byte reel1 = UnknownReel, byte reel2 = UnknownReel, byte reel3 = UnknownReel)
        {
            if (bet < 10 || bet > 1000 || credit < 0 || winnerPaid < 0 || lossAmount < 0 || !Reel(reel1) || !Reel(reel2) || !Reel(reel3))
                throw new InvalidDataException("Invalid slot machine state.");
            Bet = bet; Credit = credit; WinnerPaid = winnerPaid; LossAmount = lossAmount; Spinning = spinning;
            Reel1 = reel1; Reel2 = reel2; Reel3 = reel3;
        }
        private static bool Reel(byte value) => value < ReelStops || value == UnknownReel;
        public byte ReelAt(int index) => index == 0 ? Reel1 : index == 1 ? Reel2 : Reel3;

        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Bet); writer.Write(Credit); writer.Write(WinnerPaid); writer.Write(LossAmount); writer.Write((byte)(Spinning ? 1 : 0));
            writer.Write(Reel1); writer.Write(Reel2); writer.Write(Reel3);
            return stream.ToArray();
        }

        public static SlotMachineState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid slot machine state size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            int bet = reader.ReadInt32(), credit = reader.ReadInt32(), paid = reader.ReadInt32(), loss = reader.ReadInt32();
            byte spinning = reader.ReadByte();
            if (spinning > 1) throw new InvalidDataException("Invalid slot machine running flag.");
            byte r1 = reader.ReadByte(), r2 = reader.ReadByte(), r3 = reader.ReadByte();
            return new SlotMachineState(bet, credit, paid, loss, spinning != 0, r1, r2, r3);
        }
    }

    public enum SlotMachineAction : byte
    {
        CashOut = 0,
        BetMin = 1,
        Bet10 = 2,
        Bet100 = 3,
        BetMax = 4,
        CashIn10 = 5,
        CashIn100 = 6,
        CashIn1000 = 7,
        Spin = 8
    }

    public sealed class SlotMachineCommand
    {
        public const int Size = 17;
        public Guid Operation { get; }
        public SlotMachineAction Action { get; }
        public SlotMachineCommand(Guid operation, SlotMachineAction action)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(SlotMachineAction), action))
                throw new InvalidDataException("Invalid slot machine command.");
            Operation = operation; Action = action;
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Operation.ToByteArray()); writer.Write((byte)Action);
            return stream.ToArray();
        }
        public static SlotMachineCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid slot machine command size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            return new SlotMachineCommand(new Guid(reader.ReadBytes(16)), (SlotMachineAction)reader.ReadByte());
        }
    }

    public enum SlotMachineReplyStatus : byte
    {
        Applied = 0,
        Rejected = 1,
        Duplicate = 2,
        Capacity = 3,
        Uncertain = 4
    }

    public sealed class SlotMachineReply
    {
        public const int Size = 17;
        public Guid Operation { get; }
        public SlotMachineReplyStatus Status { get; }
        public SlotMachineReply(Guid operation, SlotMachineReplyStatus status)
        {
            if (operation == Guid.Empty || !Enum.IsDefined(typeof(SlotMachineReplyStatus), status))
                throw new InvalidDataException("Invalid slot machine reply.");
            Operation = operation; Status = status;
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(Size);
            using var writer = new BinaryWriter(stream);
            writer.Write(Operation.ToByteArray()); writer.Write((byte)Status);
            return stream.ToArray();
        }
        public static SlotMachineReply Decode(byte[] payload)
        {
            if (payload == null || payload.Length != Size) throw new InvalidDataException("Invalid slot machine reply size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            return new SlotMachineReply(new Guid(reader.ReadBytes(16)), (SlotMachineReplyStatus)reader.ReadByte());
        }
    }

    public enum SlotMachineClaimResult : byte { Claimed, Duplicate, Capacity }

    // Claims remain for this host world lifetime and never evict an accepted or rejected operation.
    public sealed class SlotMachineClaims
    {
        private readonly HashSet<(Guid Profile, Guid Operation)> claims = new HashSet<(Guid, Guid)>();
        public const int Capacity = 16384;
        public SlotMachineClaimResult Claim(Guid profile, Guid operation)
        {
            if (profile == Guid.Empty || operation == Guid.Empty) throw new InvalidDataException("Missing slot machine operation identity.");
            if (claims.Contains((profile, operation))) return SlotMachineClaimResult.Duplicate;
            if (claims.Count >= Capacity) return SlotMachineClaimResult.Capacity;
            claims.Add((profile, operation));
            return SlotMachineClaimResult.Claimed;
        }
        public int Count => claims.Count;
    }
}
