using System;
using System.Collections.Generic;

namespace MonMP.Plugin.Runtime
{
    // Per-frame handlers that keep failing must not write a full stack trace every frame or
    // physics step; on slow machines that logging alone collapses the frame rate. The first
    // occurrence of each distinct error is logged in full, repeats are counted and summarized
    // at most once per window, and an error that stops for a second flushes its remaining count.
    internal sealed class RepeatingErrorLog
    {
        internal const float Window = 10f;
        private const int MaximumKeys = 64;

        private sealed class Entry
        {
            public string Summary = "";
            public float WindowStart, LastSeen;
            public int Repeats;
        }

        private readonly Action<string> write;
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private int pending;
        private float nextFlush = float.NegativeInfinity;

        internal RepeatingErrorLog(Action<string> write) => this.write = write;

        internal int Pending => pending;

        internal void Report(string site, Exception error, float now)
        {
            string message = FirstLine(error.Message);
            string key = site + "|" + error.GetType().FullName + "|" + error.TargetSite + "|" + message;
            if (!entries.TryGetValue(key, out var entry))
            {
                if (entries.Count >= MaximumKeys) Flush(float.PositiveInfinity, true);
                entries.Add(key, new Entry { Summary = site + ": " + error.GetType().Name + ": " + message, WindowStart = now });
                write("MonMP " + site + " failed: " + error);
                return;
            }
            if (entry.Repeats == 0) { entry.WindowStart = now; pending++; }
            entry.Repeats++; entry.LastSeen = now;
            if (now - entry.WindowStart >= Window) Emit(entry, now);
        }

        // Cheap when nothing is pending; call once per frame.
        internal void Flush(float now)
        {
            if (pending == 0 || now < nextFlush) return;
            nextFlush = now + 1f;
            Flush(now, false);
        }

        private void Flush(float now, bool clear)
        {
            foreach (var entry in entries.Values)
                if (entry.Repeats > 0 && (clear || now - entry.WindowStart >= Window || now - entry.LastSeen >= 1f)) Emit(entry, now);
            if (clear) entries.Clear();
        }

        private void Emit(Entry entry, float now)
        {
            float seconds = float.IsInfinity(now) ? Window : Math.Max(0f, now - entry.WindowStart);
            write("MonMP " + entry.Summary + " (repeated " + entry.Repeats + " more times in " + seconds.ToString("0") + " s)");
            entry.Repeats = 0; entry.WindowStart = now; pending--;
        }

        private static string FirstLine(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            int end = text!.IndexOfAny(new[] { '\r', '\n' });
            string line = end < 0 ? text : text.Substring(0, end);
            return line.Length > 200 ? line.Substring(0, 200) : line;
        }
    }
}
