using System;

namespace MonMP.Plugin.Diagnostics
{
    // Retained histogram of whole-frame times. Percentiles use bin upper bounds;
    // recording a frame never allocates or sorts samples on the Unity thread.
    internal sealed class FramePacingWindow
    {
        private const double BinMs = 0.25;
        private readonly int[] bins = new int[4097];
        private double worstMs;
        internal int Count { get; private set; }
        internal int Over16Ms { get; private set; }
        internal int Over33Ms { get; private set; }

        internal void Add(double seconds)
        {
            if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return;
            double ms = seconds * 1000;
            int bin = ms >= 1024 ? 4096 : Math.Max(0, (int)Math.Ceiling(ms / BinMs) - 1);
            bins[bin]++; Count++;
            if (ms > 1000.0 / 60) Over16Ms++;
            if (ms > 1000.0 / 30) Over33Ms++;
            if (ms > worstMs) worstMs = ms;
        }

        internal double Percentile(double fraction)
        {
            if (Count == 0) return 0;
            int rank = Math.Max(1, (int)Math.Ceiling(Count * fraction));
            int seen = 0;
            for (int i = 0; i < bins.Length; i++)
            {
                seen += bins[i];
                if (seen >= rank) return i == 4096 ? worstMs : (i + 1) * BinMs;
            }
            return worstMs;
        }

        internal void Reset()
        {
            Array.Clear(bins, 0, bins.Length);
            Count = Over16Ms = Over33Ms = 0;
            worstMs = 0;
        }
    }
}
