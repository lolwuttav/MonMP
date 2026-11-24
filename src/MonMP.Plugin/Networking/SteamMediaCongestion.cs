using System;
using MonMP.Core.Session;

namespace MonMP.Plugin.Networking
{
    // Per-link optional media only. Never changes world admission or considers
    // ordinary sent-but-unacknowledged data a flood. Native samples are local.
    internal sealed class SteamMediaCongestion
    {
        internal const int ProbeFloorBytesPerSecond = 32 * 1024;
        internal int BytesPerSecond { get; private set; } = CdMediaPacing.PeerBytesPerSecond;
        private double previous = -1, nextSample, nextReduction, nextRecovery, healthySince = -1, pausedUntil, scale = 1;
        internal void Observe(in SteamLinkMetrics metrics, double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || now < 0) { BytesPerSecond = 0; return; }
            if (previous >= 0 && now < previous)
            { nextSample = nextReduction = nextRecovery = pausedUntil = 0; healthySince = -1; scale = 1; }
            if (now < nextSample) return;
            previous = now; nextSample = now + 0.25;
            // Unavailable status is not evidence of recovery from a prior loss.
            double localLoss = double.IsNaN(metrics.Loss) || double.IsInfinity(metrics.Loss) ? 0 : Math.Max(0, metrics.Loss);
            if (metrics.Ping <= 0 && metrics.SendRateBytesPerSecond == 0 && metrics.QueueTimeMs < 0 && metrics.RemoteLoss < 0 && localLoss == 0) return;
            // The native rate is a configured constant, not an estimate (see
            // SteamTransport.SendRateBytesPerSecond). Media may fill it; world traffic
            // keeps lane priority and the queue/loss backoff below still applies.
            int ceiling = metrics.SendRateBytesPerSecond == 0 ? CdMediaPacing.PeerBytesPerSecond :
                (int)Math.Min(CdMediaPacing.PeerBytesPerSecond, Math.Max(ProbeFloorBytesPerSecond, metrics.SendRateBytesPerSecond * 1.25));
            double loss = Math.Max(localLoss, metrics.RemoteLoss);
            double softQueue = Math.Max(100, Math.Min(500, metrics.Ping * 0.5));
            bool severe = loss >= 20 || metrics.QueueTimeMs >= Math.Max(1000, metrics.Ping * 2.0);
            bool congested = severe || loss >= 3 || metrics.QueueTimeMs >= softQueue;
            if (congested)
            {
                healthySince = -1;
                if (now >= nextReduction) { scale = Math.Max(1.0 / 16, scale * 0.5); nextReduction = now + 1; }
                if (severe) pausedUntil = now + 1;
            }
            else
            {
                if (healthySince < 0) healthySince = now;
                if (scale < 1 && now - healthySince >= 2 && now >= nextRecovery)
                { scale = Math.Min(1, scale + 0.125); nextRecovery = now + 2; }
            }
            BytesPerSecond = now < pausedUntil ? 0 : Math.Max(1, (int)(ceiling * scale));
        }
    }
}
