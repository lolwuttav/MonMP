using System;
using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    // SOUND_Manager advances its CD cursor with Time.deltaTime. Snapshot age must use
    // the same Unity clock, while speaker playback keeps native's half-second start lag.
    internal static class CdPlaybackContinuity
    {
        internal const float NativeStartDelay = 0.5f;
        internal const float DriftTolerance = 1.5f;

        internal static float SnapshotTime(float time, float receivedAt, float now) =>
            CdPlaybackSyncPolicy.PredictGuestTime(time, receivedAt, now);

        internal static float AtSnapshot(float currentTime, float receivedAt, float now) =>
            currentTime - Math.Max(0f, now - receivedAt);

        internal static float SeekTime(float logicalTime, float length, bool alreadyPlaying)
        {
            if (!Finite(logicalTime) || !Finite(length) || length <= 0f) return 0f;
            return Math.Max(0f, Math.Min(length - Math.Min(0.01f, length),
                logicalTime - (alreadyPlaying ? NativeStartDelay : 0f)));
        }

        internal static bool NeedsSeek(float sourceTime, float target) =>
            !Finite(sourceTime) || Math.Abs(sourceTime - target) > DriftTolerance;

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
