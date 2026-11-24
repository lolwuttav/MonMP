using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    public enum TrashRecycleFeedbackTransition : byte { Delayed = 1, Expired = 2 }

    public readonly struct TrashRecycleFeedbackEvent
    {
        public readonly Guid Operation;
        public readonly TrashRecycleFeedbackTransition Transition;
        public TrashRecycleFeedbackEvent(Guid operation, TrashRecycleFeedbackTransition transition)
        { Operation = operation; Transition = transition; }
    }

    // Bin requests only acknowledge UI feedback. An unanswered request must not keep
    // player-profile submission or a coordinated save blocked for the whole session.
    public sealed class TrashRecycleFeedbackTracker
    {
        public const int Capacity = 8;
        public const double InitialReplySeconds = 10;
        public const double LateReplySeconds = 20;
        private sealed class Entry
        {
            public ulong Bin, Item;
            public double InitialDeadline, FinalDeadline;
            public bool Delayed;
        }
        private readonly Dictionary<Guid, Entry> entries = new Dictionary<Guid, Entry>();
        public int Count => entries.Count;

        public bool TryTrack(Guid operation, ulong bin, ulong item, double now)
        {
            Validate(operation, bin, item, now);
            if (entries.TryGetValue(operation, out var existing))
                return existing.Bin == bin && existing.Item == item && now < existing.FinalDeadline;
            if (entries.Count >= Capacity) return false;
            entries.Add(operation, new Entry { Bin = bin, Item = item,
                InitialDeadline = now + InitialReplySeconds,
                FinalDeadline = now + InitialReplySeconds + LateReplySeconds });
            return true;
        }

        // The bin and item are checked even for delayed replies. A mismatched or expired
        // receipt cannot settle a different operation or extend the save barrier.
        public bool TryResolve(Guid operation, ulong bin, ulong item, double now)
        {
            if (!ValidTime(now) || !entries.TryGetValue(operation, out var entry) ||
                entry.Bin != bin || entry.Item != item || now >= entry.FinalDeadline) return false;
            entries.Remove(operation);
            return true;
        }

        public TrashRecycleFeedbackEvent[] Advance(double now)
        {
            if (!ValidTime(now)) throw new InvalidDataException("Invalid trash/recycle receipt clock.");
            if (entries.Count == 0) return Array.Empty<TrashRecycleFeedbackEvent>();
            var events = new List<TrashRecycleFeedbackEvent>();
            foreach (var operation in new List<Guid>(entries.Keys))
            {
                var entry = entries[operation];
                if (now >= entry.FinalDeadline)
                {
                    entries.Remove(operation);
                    events.Add(new TrashRecycleFeedbackEvent(operation, TrashRecycleFeedbackTransition.Expired));
                }
                else if (!entry.Delayed && now >= entry.InitialDeadline)
                {
                    entry.Delayed = true;
                    events.Add(new TrashRecycleFeedbackEvent(operation, TrashRecycleFeedbackTransition.Delayed));
                }
            }
            return events.ToArray();
        }

        public void Clear() => entries.Clear();
        private static void Validate(Guid operation, ulong bin, ulong item, double now)
        {
            if (operation == Guid.Empty || bin == 0 || item <= WorldRecord.DynamicIdStart || !ValidTime(now))
                throw new InvalidDataException("Invalid trash/recycle receipt.");
        }
        private static bool ValidTime(double now) => !double.IsNaN(now) && !double.IsInfinity(now) && now >= 0;
    }
}
