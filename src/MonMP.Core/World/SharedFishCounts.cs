using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    // Native Tool_FishingRod counts each catch in GameManager.Fishing_Amount (one int per fish type)
    // and grants Catch 1/50/250 Fish from that player's own total. Only the host's save keeps the
    // array and guests start each session at zero, so a group's catches never reached 250 for anyone.
    // The host keeps one shared count; guests report their own catches as a cumulative per-type total,
    // so a repeated or reordered report can never be counted twice.
    public static class SharedFishCounts
    {
        public const int MaximumTypes = 64;
        public const int MaximumCount = 1_000_000;
        // A guest catches one fish at a time; a larger jump in one report is not a catch.
        public const int MaximumReportStep = 1000;
        public const byte ReportMarker = 0x55;
        public static readonly int[] AwardThresholds = { 1, 50, 250 };

        public static byte[] Encode(IReadOnlyList<int> counts)
        {
            Validate(counts);
            var payload = new byte[1 + counts.Count * 4];
            payload[0] = (byte)counts.Count;
            for (int i = 0; i < counts.Count; i++) WriteInt(payload, 1 + i * 4, counts[i]);
            return payload;
        }

        public static int[] Decode(byte[] payload)
        {
            if (payload == null || payload.Length == 0 || payload[0] == 0 || payload[0] > MaximumTypes || payload.Length != 1 + payload[0] * 4)
                throw new InvalidDataException("Invalid fish counts payload.");
            var counts = new int[payload[0]];
            for (int i = 0; i < counts.Length; i++)
            {
                counts[i] = ReadInt(payload, 1 + i * 4);
                if (counts[i] < 0 || counts[i] > MaximumCount) throw new InvalidDataException("Invalid fish count.");
            }
            return counts;
        }

        public static byte[] EncodeReport(IReadOnlyList<int> caught)
        {
            Validate(caught);
            var payload = new byte[2 + caught.Count * 4];
            payload[0] = ReportMarker; payload[1] = (byte)caught.Count;
            for (int i = 0; i < caught.Count; i++) WriteInt(payload, 2 + i * 4, caught[i]);
            return payload;
        }

        public static bool IsReport(byte[] payload) => payload != null && payload.Length >= 2 && payload[0] == ReportMarker;

        public static int[] DecodeReport(byte[] payload)
        {
            if (!IsReport(payload) || payload[1] == 0 || payload[1] > MaximumTypes || payload.Length != 2 + payload[1] * 4)
                throw new InvalidDataException("Invalid fish catch report.");
            var caught = new int[payload[1]];
            for (int i = 0; i < caught.Length; i++)
            {
                caught[i] = ReadInt(payload, 2 + i * 4);
                if (caught[i] < 0 || caught[i] > MaximumCount) throw new InvalidDataException("Invalid fish catch report.");
            }
            return caught;
        }

        public static long Total(IReadOnlyList<int>? counts)
        {
            long total = 0;
            if (counts != null) for (int i = 0; i < counts.Count; i++) total += Math.Max(0, counts[i]);
            return total;
        }

        private static void Validate(IReadOnlyList<int> counts)
        {
            if (counts == null || counts.Count == 0 || counts.Count > MaximumTypes) throw new InvalidDataException("Invalid fish type count.");
            for (int i = 0; i < counts.Count; i++)
                if (counts[i] < 0 || counts[i] > MaximumCount) throw new InvalidDataException("Invalid fish count.");
        }
        private static void WriteInt(byte[] payload, int at, int value)
        { payload[at] = (byte)value; payload[at + 1] = (byte)(value >> 8); payload[at + 2] = (byte)(value >> 16); payload[at + 3] = (byte)(value >> 24); }
        private static int ReadInt(byte[] payload, int at) =>
            payload[at] | payload[at + 1] << 8 | payload[at + 2] << 16 | payload[at + 3] << 24;

        // Host: adds each guest's new catches to the shared native array exactly once.
        public sealed class HostLedger
        {
            private readonly Dictionary<uint, int[]> reported = new Dictionary<uint, int[]>();
            public bool Apply(uint peer, int[] cumulative, int[] counts)
            {
                if (cumulative == null || counts == null || cumulative.Length != counts.Length) return false;
                if (!reported.TryGetValue(peer, out var prior)) reported.Add(peer, prior = new int[counts.Length]);
                bool changed = false;
                for (int i = 0; i < counts.Length; i++)
                {
                    int value = cumulative[i];
                    if (value <= prior[i]) continue;
                    int step = Math.Min(MaximumReportStep, value - prior[i]);
                    prior[i] = value;
                    int next = (int)Math.Min(MaximumCount, (long)Math.Max(0, counts[i]) + step);
                    if (next != counts[i]) { counts[i] = next; changed = true; }
                }
                return changed;
            }
            public void Remove(uint peer) => reported.Remove(peer);
        }

        // Guest: keeps the native array at the shared count plus this player's own catches the host
        // has not counted yet, and accumulates those catches for the host.
        public sealed class GuestTracker
        {
            private int[] baseline = Array.Empty<int>();
            private int[] caught = Array.Empty<int>();
            public bool Received { get; private set; }
            public bool Dirty { get; private set; }
            public int[] Caught => (int[])caught.Clone();

            // The first host count replaces whatever an earlier single-player save left in the static
            // array; later counts never lower a value that already includes an uncounted local catch.
            public void Receive(int[] shared, int[] local)
            {
                if (shared == null || local == null || shared.Length != local.Length) return;
                if (!Received || caught.Length != local.Length)
                {
                    Array.Copy(shared, local, local.Length);
                    caught = new int[local.Length]; Received = true; Dirty = false;
                }
                else
                {
                    Observe(local);
                    for (int i = 0; i < local.Length; i++) if (shared[i] > local[i]) local[i] = shared[i];
                }
                baseline = (int[])local.Clone();
            }

            // Counts native increments since the last write as this player's catches.
            public bool Observe(int[] local)
            {
                if (!Received || local == null || local.Length != baseline.Length) return false;
                for (int i = 0; i < local.Length; i++)
                {
                    int step = local[i] - baseline[i];
                    if (step > 0 && step <= MaximumReportStep)
                    { caught[i] = (int)Math.Min(MaximumCount, (long)caught[i] + step); Dirty = true; }
                    baseline[i] = local[i];
                }
                return Dirty;
            }
            public void Sent() => Dirty = false;
        }
    }
}
