using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MonMP.Core.World
{
    public enum SleepPhase : byte { Idle = 0, Advancing = 1 }

    public sealed class SleepState
    {
        public const ushort Adapter = 16;
        public const int MaxTicks = 1440;
        public static readonly ulong Entity = StaticId.Hash("shared-sleep/v1");
        public static readonly SleepState Initial = new SleepState(SleepPhase.Idle, 0, 0, 0, 0, 0, 0);
        public readonly SleepPhase Phase;
        public readonly uint Round, Completed;
        public readonly ushort Ready, Required, Target, Advanced;
        public readonly int TotalTicks, RoundStartTicks;
        public SleepState(SleepPhase phase, uint round, uint completed, ushort ready, ushort required, ushort target, ushort advanced,
            int totalTicks = 0, int roundStartTicks = 0)
        {
            Phase = phase; Round = round; Completed = completed; Ready = ready; Required = required; Target = target; Advanced = advanced;
            TotalTicks = totalTicks; RoundStartTicks = roundStartTicks;
            Validate();
        }
        // A player who went to bed while Round was r wakes once that round (or a later one) completes.
        public bool Releases(uint requestRound) => Completed > requestRound;
        private void Validate()
        {
            bool advancing = Phase == SleepPhase.Advancing;
            if ((byte)Phase > 1 || Ready > Required || Target > MaxTicks || Advanced > Target || TotalTicks < 0 ||
                RoundStartTicks < 0 || RoundStartTicks > TotalTicks || advancing && TotalTicks - RoundStartTicks != Advanced ||
                (advancing ? Completed + 1 != Round || Target == 0 || Advanced >= Target : Completed != Round || Target != 0 || Advanced != 0))
                throw new InvalidDataException("Invalid shared sleep state.");
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
            w.Write((byte)Phase); w.Write(Round); w.Write(Completed); w.Write(Ready); w.Write(Required); w.Write(Target); w.Write(Advanced);
            w.Write(TotalTicks); w.Write(RoundStartTicks);
            return stream.ToArray();
        }
        public static SleepState Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 25) throw new InvalidDataException("Invalid shared sleep payload.");
            using var stream = new MemoryStream(payload, false); using var r = new BinaryReader(stream);
            byte phase = r.ReadByte();
            if (phase > 1) throw new InvalidDataException("Invalid shared sleep phase.");
            return new SleepState((SleepPhase)phase, r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16(), r.ReadInt32(), r.ReadInt32());
        }
        public bool SameAs(SleepState other) => Enumerable.SequenceEqual(Encode(), other.Encode());
    }

    public readonly struct SleepCommand
    {
        public readonly bool Ready;
        public readonly ushort Ticks;
        // The round the sender saw when it went to bed. Readiness is a per-player intent sent
        // without the aggregate record revision, so a request from before a later round began is stale.
        public readonly uint Round;
        public SleepCommand(bool ready, ushort ticks, uint round = 0)
        {
            if (ready ? ticks < 1 || ticks > SleepState.MaxTicks : ticks != 0) throw new InvalidDataException("Invalid sleep request.");
            Ready = ready; Ticks = ticks; Round = round;
        }
        public byte[] Encode() => new[] { (byte)(Ready ? 1 : 0), (byte)Ticks, (byte)(Ticks >> 8), (byte)Round, (byte)(Round >> 8), (byte)(Round >> 16), (byte)(Round >> 24) };
        public static SleepCommand Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 7 || payload[0] > 1) throw new InvalidDataException("Invalid sleep request.");
            return new SleepCommand(payload[0] == 1, (ushort)(payload[1] | payload[2] << 8), (uint)(payload[3] | payload[4] << 8 | payload[5] << 16 | payload[6] << 24));
        }
    }

    // Host policy: every connected player must be in bed. The world then advances once by the
    // longest requested sleep; requests made while a round runs wait for the next round.
    public sealed class SleepCoordinator
    {
        private readonly Dictionary<uint, ushort> ready = new Dictionary<uint, ushort>();
        private readonly List<uint> departed = new List<uint>();
        public SleepState State { get; private set; } = SleepState.Initial;
        public bool IsReady(uint peer) => ready.ContainsKey(peer);
        public void Request(uint peer, SleepCommand command)
        {
            // Requests are idempotent and may be resent; a ready request from before a later round
            // started would outlive its sleeper, who that round already released.
            if (!command.Ready) ready.Remove(peer);
            else if (command.Round >= State.Round) ready[peer] = command.Ticks;
        }
        public void Remove(uint peer) => ready.Remove(peer);
        public SleepState Update(IReadOnlyCollection<uint> peers, bool blocked)
        {
            if (peers.Count > ushort.MaxValue) throw new InvalidOperationException("Too many sleep participants.");
            departed.Clear();
            foreach (uint peer in ready.Keys) if (!peers.Contains(peer)) departed.Add(peer);
            foreach (uint peer in departed) ready.Remove(peer);
            var s = State;
            ushort count = 0, target = 0;
            // The game supplies a reusable list. Index lists/arrays without boxing
            // their enumerator; retain the public collection contract for other callers.
            if (peers is IReadOnlyList<uint> indexed)
            {
                for (int i = 0; i < indexed.Count; i++)
                    if (ready.TryGetValue(indexed[i], out ushort ticks)) { count++; target = Math.Max(target, ticks); }
            }
            else
                foreach (uint peer in peers)
                    if (ready.TryGetValue(peer, out ushort ticks)) { count++; target = Math.Max(target, ticks); }
            if (s.Phase == SleepPhase.Idle && !blocked && peers.Count > 0 && count == peers.Count)
            {
                foreach (uint peer in peers) ready.Remove(peer);
                State = new SleepState(SleepPhase.Advancing, s.Round + 1, s.Completed, 0, (ushort)peers.Count, target, 0, s.TotalTicks, s.TotalTicks);
            }
            else if (s.Ready != count || s.Required != peers.Count)
                State = new SleepState(s.Phase, s.Round, s.Completed, count, (ushort)peers.Count, s.Target, s.Advanced, s.TotalTicks, s.RoundStartTicks);
            return State;
        }
        // Returns how many of the offered ticks the host should run now.
        public int Advance(int offered)
        {
            var s = State;
            if (s.Phase != SleepPhase.Advancing || offered <= 0) return 0;
            int run = Math.Min(offered, s.Target - s.Advanced);
            int advanced = s.Advanced + run;
            State = advanced >= s.Target
                ? new SleepState(SleepPhase.Idle, s.Round, s.Round, s.Ready, s.Required, 0, 0, checked(s.TotalTicks + run), s.RoundStartTicks)
                : new SleepState(SleepPhase.Advancing, s.Round, s.Completed, s.Ready, s.Required, s.Target, (ushort)advanced, checked(s.TotalTicks + run), s.RoundStartTicks);
            return run;
        }
    }
}
