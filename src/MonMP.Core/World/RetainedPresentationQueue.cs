using System;
using System.Collections.Generic;
using System.IO;

namespace MonMP.Core.World
{
    // A rolling log can replace a queued batch only while it still contains every
    // new cue in that batch. Once those cues roll out, preserve the old immutable
    // record and begin a second batch instead of silently coalescing them away.
    internal sealed class RetainedPresentationQueue
    {
        private readonly LinkedList<(WorldRecord Record, uint FirstNew)> records = new LinkedList<(WorldRecord, uint)>();
        private uint lastSequence;
        public int Count => records.Count;

        public void RememberBaseline(WorldRecord record)
        {
            if (!TryLog(record, out var events)) return;
            lastSequence = events.Length == 0 ? 0 : events[events.Length - 1].Sequence;
        }

        public bool TryEnqueue(WorldRecord record, out bool added)
        {
            added = false;
            if (!TryLog(record, out var events) || events.Length == 0) return false;
            uint first = events[0].Sequence, last = events[events.Length - 1].Sequence;
            if (last <= lastSequence) return true;
            var tail = records.Last;
            if (tail != null && tail.Value.Record.Id == record.Id && first <= tail.Value.FirstNew)
                tail.Value = (record, tail.Value.FirstNew);
            else
            {
                uint firstNew = Math.Max(first, lastSequence + 1);
                records.AddLast((record, firstNew));
                added = true;
            }
            lastSequence = last;
            return true;
        }

        public WorldRecord Dequeue()
        {
            var first = records.First ?? throw new InvalidOperationException("No pending presentation records.");
            records.RemoveFirst();
            return first.Value.Record;
        }

        private static bool TryLog(WorldRecord record, out PresentationEvent[] events)
        {
            try { events = PresentationEvent.DecodeLog(record.Payload); return true; }
            catch (InvalidDataException) { events = Array.Empty<PresentationEvent>(); return false; }
        }
    }
}
