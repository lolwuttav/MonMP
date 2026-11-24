using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    // A delayed computer tick retains its user after the computer has finished saving.
    public sealed class PersonalClockCursor
    {
        private int? highWater;
        private int computerHighWater;
        private int sleepHighWater, refillHighWater;
        private readonly Queue<Batch> pending = new Queue<Batch>();
        private Batch active;
        private struct Batch
        {
            internal int Count, SleepTick;
            internal uint User;
            internal bool Sleeping, Dead, Saving, Historical, Refill;
        }
        private readonly Queue<(int Start, int End)> sleeps = new Queue<(int, int)>();
        private int? sleepingSince;
        public void BeginSleep(int sleepTicks) => sleepingSince = sleepTicks;
        public void EndSleep(int sleepTicks)
        {
            if (sleepingSince.HasValue && sleepTicks > sleepingSince.Value) sleeps.Enqueue((sleepingSince.Value, sleepTicks));
            sleepingSince = null;
        }
        public bool HasPending => active.Count > 0 || pending.Count > 0;
        public void MarkDead()
        {
            active.Dead = true;
            int count = pending.Count;
            for (int i = 0; i < count; i++)
            { var batch = pending.Dequeue(); batch.Dead = true; pending.Enqueue(batch); }
        }
        public int Observe(ClockState state, bool sleeping = false, bool dead = false, bool saving = false, bool repair = false)
        {
            if (!highWater.HasValue) { highWater = state.TotalTicks; computerHighWater = state.ComputerTicks; sleepHighWater = state.SleepTicks; refillHighWater = state.RefillAt; return 0; }
            if (state.TotalTicks < highWater.Value) return 0;
            if (state.TotalTicks == highWater.Value && state.RefillAt <= refillHighWater) return 0;
            int start = highWater.Value, computerStart = computerHighWater, sleepStart = sleepHighWater;
            int jump = state.TotalTicks - start, computer = state.ComputerTicks - computerStart, sleep = state.SleepTicks - sleepStart;
            ValidateInterval(jump, computer, sleep);
            if (state.RefillAt < refillHighWater) throw new InvalidDataException("Personal clock refill moved backwards.");
            // Normal deltas are a FIFO of real minute endpoints. A mixed interval
            // has lost its chronology and must never be guessed ordinary-first.
            // A repair can legitimately recover from a failed adapter callback;
            // its unknown need history is replayed without irreversible effects.
            bool mixed = Mixed(jump, computer, sleep);
            if (mixed && !repair) throw new InvalidDataException("Personal clock minute chronology was not retained.");
            bool uncertain = mixed || repair && computer > 1;
            if (state.RefillAt > refillHighWater)
            {
                if (state.RefillAt < start) throw new InvalidDataException("A new needs refill predates accepted world time.");
                ValidateInterval(state.RefillAt - start, state.RefillComputerTicks - computerStart, state.RefillSleepTicks - sleepStart);
                ValidateInterval(state.TotalTicks - state.RefillAt, state.ComputerTicks - state.RefillComputerTicks, state.SleepTicks - state.RefillSleepTicks);
                // Fill_Everything resets four needs, not drug effects, elapsed consumption
                // timers or debuffs. Keep their minute work AND every reset in order.
                if (!active.Refill) active.Historical = true;
                int count = pending.Count;
                for (int i = 0; i < count; i++)
                {
                    var batch = pending.Dequeue();
                    if (!batch.Refill) batch.Historical = true;
                    pending.Enqueue(batch);
                }
                Enqueue(state.RefillAt - start, state.RefillComputerTicks - computerStart,
                    state.RefillSleepTicks - sleepStart, state.ComputerUser, sleepStart, sleeping, dead, saving, true);
                pending.Enqueue(new Batch { Count = 1, User = ItemLease.Unowned, Refill = true });
                start = state.RefillAt; computerStart = state.RefillComputerTicks; sleepStart = state.RefillSleepTicks;
            }
            jump = state.TotalTicks - start; computer = state.ComputerTicks - computerStart; sleep = state.SleepTicks - sleepStart;
            highWater = state.TotalTicks; computerHighWater = state.ComputerTicks;
            sleepHighWater = state.SleepTicks; refillHighWater = state.RefillAt;
            int ordinary = jump - computer - sleep;
            Enqueue(jump, computer, sleep, state.ComputerUser, sleepStart, sleeping, dead, saving, uncertain);
            return ordinary;
        }
        private static bool Mixed(int jump, int computer, int sleep) =>
            (computer > 0 ? 1 : 0) + (sleep > 0 ? 1 : 0) + (jump > computer + sleep ? 1 : 0) > 1;
        private static void ValidateInterval(int jump, int computer, int sleep)
        {
            if (jump < 0 || computer < 0 || sleep < 0 || (long)computer + sleep > jump)
                throw new InvalidDataException("Personal clock boundary counters moved independently of world time.");
        }
        private void Enqueue(int jump, int computer, int sleep, uint user, int sleepStart, bool sleeping, bool dead, bool saving, bool historical)
        {
            ValidateInterval(jump, computer, sleep);
            int ordinary = jump - computer - sleep;
            if (ordinary > 0) pending.Enqueue(new Batch { Count = ordinary, User = ItemLease.Unowned, Sleeping = sleeping, Dead = dead, Saving = saving, Historical = historical });
            if (computer > 0) pending.Enqueue(new Batch { Count = computer, User = user, Sleeping = sleeping, Dead = dead, Saving = saving, Historical = historical });
            if (sleep > 0) pending.Enqueue(new Batch { Count = sleep, User = ItemLease.Unowned, SleepTick = sleepStart + 1, Dead = dead, Saving = saving, Historical = historical });
        }
        public bool TryTake(out uint computerUser) => TryTake(out computerUser, out _, out _, out _, out _, out _);
        public bool TryTake(out uint computerUser, out bool sleeping, out bool dead, out bool saving, out bool historical, out bool refill)
        {
            sleeping = dead = false;
            saving = historical = refill = false;
            if (active.Count == 0)
            {
                if (pending.Count == 0) { computerUser = ItemLease.Unowned; return false; }
                active = pending.Dequeue();
            }
            computerUser = active.User; dead = active.Dead; sleeping = active.Sleeping; saving = active.Saving; historical = active.Historical; refill = active.Refill;
            if (active.SleepTick != 0)
            {
                while (sleeps.Count > 0 && sleeps.Peek().End < active.SleepTick) sleeps.Dequeue();
                sleeping = sleepingSince.HasValue && active.SleepTick > sleepingSince.Value ||
                    sleeps.Count > 0 && active.SleepTick > sleeps.Peek().Start && active.SleepTick <= sleeps.Peek().End;
                active.SleepTick++;
            }
            active.Count--; return true;
        }
        public void DiscardOrdinary()
        {
            if (active.User == ItemLease.Unowned) active = default;
            int count = pending.Count;
            for (int i = 0; i < count; i++) { var batch = pending.Dequeue(); if (batch.User != ItemLease.Unowned) pending.Enqueue(batch); }
        }
    }
    public static class ComputerNeedsPolicy
    {
        // Starting above the cap does not turn computer use into a free refill.
        public static float Limit(float before, float after) => Math.Max(0f, Math.Min(after, Math.Max(0.7f, before)));
        // Native BasicNeeds_Manager.Tick's saving-minute anxiety reduction applies
        // to everyone sharing that skipped time, independently of the save actor.
        public static float AnxietyAfterMinute(float before) => Math.Max(0f, Math.Min(1f, before - .0029166667f));
    }
}
