using System;
using System.Globalization;

namespace MonMP.Plugin.Replication
{
    // Local CD payload throughput, not all game traffic or queued file sizes.
    // One half-second bucket; no timers, scene searches or per-packet formatting.
    internal sealed class CdTransferRates
    {
        private long sent, received;
        private double sampledAt;
        internal string? Text { get; private set; }
        internal void Sent(int bytes) { if (bytes > 0) sent += bytes; }
        internal void Received(int bytes) { if (bytes > 0) received += bytes; }
        internal bool Sample(double now, bool busy, int uploadsLeft = 0, int downloadsLeft = 0)
        {
            if (double.IsNaN(now) || double.IsInfinity(now)) return false;
            if (now < sampledAt) { Reset(); sampledAt = now; return true; }
            double elapsed = now - sampledAt;
            if (elapsed < 0.5) return false;
            string? next = busy || sent != 0 || received != 0 || uploadsLeft > 0 || downloadsLeft > 0
                ? "UP " + (sent / elapsed / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " [" + Math.Max(0, uploadsLeft) + "]" +
                  "  DL " + (received / elapsed / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " [" + Math.Max(0, downloadsLeft) + "] KB/s" : null;
            sent = received = 0; sampledAt = now;
            bool changed = next != Text; Text = next;
            return changed;
        }
        internal void Reset() { sent = received = 0; sampledAt = 0; Text = null; }
    }
}
