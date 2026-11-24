using System;
using System.IO;

namespace MonMP.Core.World
{
    public sealed class ClockState
    {
        public const ushort Adapter = 6;
        public static readonly ulong Entity = StaticId.Hash("shared-world-clock/v1");
        public readonly float DayFraction;
        public readonly int Year, Month, Day, Weekday, ElapsedDays, TotalTicks;
        public readonly bool Flowing;
        public readonly int ComputerTicks;
        public readonly uint ComputerUser;
        public readonly int SleepTicks, RefillAt, RefillComputerTicks, RefillSleepTicks;
        public readonly int FastForwardStart;
        public readonly ushort FastForwardTarget;
        public readonly bool FastForwardRunning;
        public ClockState(float dayFraction, int year, int month, int day, int weekday, int elapsedDays, int totalTicks, bool flowing,
            int computerTicks = 0, uint computerUser = ItemLease.Unowned, int sleepTicks = 0, int refillAt = 0,
            int refillComputerTicks = 0, int refillSleepTicks = 0, int fastForwardStart = 0, ushort fastForwardTarget = 0, bool fastForwardRunning = false)
        { DayFraction = dayFraction; Year = year; Month = month; Day = day; Weekday = weekday; ElapsedDays = elapsedDays; TotalTicks = totalTicks; Flowing = flowing; ComputerTicks = computerTicks; ComputerUser = computerUser; SleepTicks = sleepTicks; RefillAt = refillAt; RefillComputerTicks = refillComputerTicks; RefillSleepTicks = refillSleepTicks; FastForwardStart = fastForwardStart; FastForwardTarget = fastForwardTarget; FastForwardRunning = fastForwardRunning; }
        public int MinuteOfDay => (int)(DayFraction * 24f * 60f);
        private void Validate()
        {
            if (!ItemPose.Finite(DayFraction, 1) || DayFraction < 0 || DayFraction >= 1 || Year < 1 || Year > 9999 ||
                Month < 1 || Month > 12 || Day < 1 || Day > DateTime.DaysInMonth(Year, Month) || Weekday < 0 || Weekday > 6 ||
                ElapsedDays < 0 || TotalTicks < 0 || ComputerTicks < 0 || ComputerTicks > TotalTicks ||
                ComputerTicks > 0 && ComputerUser == ItemLease.Unowned || SleepTicks < 0 || (long)SleepTicks + ComputerTicks > TotalTicks ||
                RefillAt < 0 || RefillAt > TotalTicks || RefillComputerTicks < 0 || RefillComputerTicks > ComputerTicks ||
                RefillSleepTicks < 0 || RefillSleepTicks > SleepTicks || (long)RefillComputerTicks + RefillSleepTicks > RefillAt ||
                FastForwardStart < 0 || FastForwardStart > TotalTicks ||
                (FastForwardTarget == 0 ? FastForwardStart != 0 || FastForwardRunning : FastForwardTarget != 60 && FastForwardTarget != 1440))
                throw new InvalidDataException("Invalid shared clock state.");
        }
        public byte[] Encode()
        {
            Validate(); using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write(DayFraction); w.Write((ushort)Year); w.Write((byte)Month); w.Write((byte)Day); w.Write((byte)Weekday);
            w.Write(ElapsedDays); w.Write(TotalTicks); w.Write(Flowing); w.Write(ComputerTicks); w.Write(ComputerUser);
            w.Write(SleepTicks); w.Write(RefillAt); w.Write(RefillComputerTicks); w.Write(RefillSleepTicks);
            w.Write(FastForwardStart); w.Write(FastForwardTarget); w.Write(FastForwardRunning); return stream.ToArray();
        }
        public static ClockState Decode(byte[] payload)
        {
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream);
            float fraction = r.ReadSingle(); int year = r.ReadUInt16(), month = r.ReadByte(), day = r.ReadByte(), weekday = r.ReadByte();
            int days = r.ReadInt32(), ticks = r.ReadInt32(); byte flowing = r.ReadByte();
            int computerTicks = r.ReadInt32(); uint computerUser = r.ReadUInt32();
            int sleepTicks = r.ReadInt32(), refillAt = r.ReadInt32(), refillComputerTicks = r.ReadInt32(), refillSleepTicks = r.ReadInt32();
            int fastForwardStart = r.ReadInt32(); ushort fastForwardTarget = r.ReadUInt16();
            byte fastForwardRunning = r.ReadByte();
            if (flowing > 1 || fastForwardRunning > 1 || stream.Position != stream.Length) throw new InvalidDataException("Invalid shared clock payload.");
            var state = new ClockState(fraction, year, month, day, weekday, days, ticks, flowing == 1, computerTicks, computerUser,
                sleepTicks, refillAt, refillComputerTicks, refillSleepTicks, fastForwardStart, fastForwardTarget, fastForwardRunning == 1); state.Validate(); return state;
        }
    }

    // Initial snapshots establish a baseline. Subsequent accepted ticks advance
    // local needs exactly once, including across duplicate and repair snapshots.
    public sealed class PersonalTickCursor
    {
        private int? highWater;
        public long Pending { get; private set; }
        public void Observe(int totalTicks)
        {
            if (totalTicks < 0) throw new InvalidDataException("Negative clock counter.");
            if (!highWater.HasValue) { highWater = totalTicks; return; }
            if (totalTicks <= highWater.Value) return;
            Pending += (long)totalTicks - highWater.Value; highWater = totalTicks;
        }
        // The host fast-forwarded (cheat-menu hour/day skip), which refills needs natively instead
        // of decaying them; the skipped ticks are not personal time.
        public void Discard() => Pending = 0;
        public int Take(int budget)
        {
            if (budget < 1 || budget > 256) throw new ArgumentOutOfRangeException(nameof(budget));
            int count = (int)Math.Min(Pending, budget); Pending -= count; return count;
        }
    }
}
