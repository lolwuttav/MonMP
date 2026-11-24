#if MONMP_PERFORMANCE_CAPTURE
using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace MonMP.Plugin.Runtime
{
    // An opt-in capture control, not a player setting. OnDemandRendering changes
    // draw cadence independently of the game's update/simulation cadence. This
    // helper never touches targetFrameRate, vSync, cameras, networking or physics.
    internal sealed class CaptureClientRendering : IDisposable
    {
        private const int SparseInterval = 8;
        private const float RetrySeconds = 1f;
        private const string NormalStatus = "Client render cadence: normal (Ctrl+F10)";
        private bool ownsInterval, restorePending, disposed;
        private int originalInterval;
        private float retryAt;
        internal bool Sparse { get; private set; }
        internal string Experiment => restorePending ? "client-render-restore-pending" :
            Sparse ? "client-render-sparse" : "baseline";
        internal string Status { get; private set; } = NormalStatus;
        internal int ActualInterval => OnDemandRendering.renderFrameInterval;
        internal bool ScheduledRender => OnDemandRendering.willCurrentFrameRender;

        internal void Toggle()
        {
            if (disposed) return;
            if (ownsInterval) { Reset(); return; }
            int previous;
            try { previous = OnDemandRendering.renderFrameInterval; }
            catch (Exception)
            {
                Status = "Client render experiment unavailable: interval could not be read";
                return;
            }
            originalInterval = previous;
            // Retain ownership before the setter: a native setter can change its
            // value and then fail, in which case the saved original is still due.
            ownsInterval = true;
            try
            {
                OnDemandRendering.renderFrameInterval = SparseInterval;
                int actual = OnDemandRendering.renderFrameInterval;
                if (actual != SparseInterval)
                {
                    Release("Client render experiment did not apply; current interval=" + actual);
                    return;
                }
                Sparse = true; restorePending = false;
                Status = "EXPERIMENT: client draws every 8 updates; prior interval=" + previous + " (Ctrl+F10 restores)";
            }
            catch (Exception)
            {
                Reset();
                Status = "Client render experiment did not start; " + Status;
            }
        }

        // Root calls this even when capture/eligibility is gone, so cleanup is
        // independent of recorder lifetime. An ordinary baseline does no native
        // reads, formatting or allocation. Pending cleanup remains retryable even
        // after Dispose if the owner still has a later cleanup tick available.
        internal void Tick(bool eligible = true)
        {
            if (restorePending)
            {
                if (Time.unscaledTime >= retryAt) Reset();
                return;
            }
            if (disposed || !ownsInterval) return;
            if (!eligible) { Reset(); return; }
            try
            {
                int actual = OnDemandRendering.renderFrameInterval;
                if (actual != SparseInterval)
                    Release("Client render experiment ended: external interval=" + actual);
            }
            catch (Exception) { Pending("Client render interval read failed; original restore pending (retry within 1s)"); }
        }

        internal void Reset()
        {
            if (!ownsInterval)
            {
                Sparse = restorePending = false;
                Status = NormalStatus;
                return;
            }
            try
            {
                int actual = OnDemandRendering.renderFrameInterval;
                if (actual != SparseInterval)
                {
                    // Respect a newer owner. This also handles a setter which
                    // applied the saved original before throwing on a prior try.
                    Release(actual == originalInterval ? NormalStatus :
                        "Client render experiment ended: external interval=" + actual);
                    return;
                }
                if (actual != originalInterval)
                {
                    OnDemandRendering.renderFrameInterval = originalInterval;
                    actual = OnDemandRendering.renderFrameInterval;
                }
                if (actual == originalInterval) { Release(NormalStatus); return; }
                if (actual != SparseInterval)
                {
                    Release("Client render experiment ended: external interval=" + actual);
                    return;
                }
                Pending("Client render interval restore not accepted; retry within 1s");
            }
            catch (Exception) { Pending("Client render interval restore failed; retry within 1s"); }
        }

        private void Pending(string status)
        {
            Sparse = false; restorePending = true;
            retryAt = Time.unscaledTime + RetrySeconds;
            Status = status;
        }

        private void Release(string status)
        {
            ownsInterval = Sparse = restorePending = false;
            originalInterval = 0; retryAt = 0;
            Status = status;
        }

        public void Dispose()
        {
            if (!disposed) { Reset(); disposed = true; }
        }
    }
}
#endif
