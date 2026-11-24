using System.Collections.Generic;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Recent poses on a smoothed arrival timeline, played back a little behind real time. A late or
    // bunched packet then still has a neighbour to blend towards, so the object keeps moving at an
    // even pace instead of stopping short and jumping to catch up.
    internal sealed class PoseTrail
    {
        internal struct Sample
        {
            public float Time, Pitch;
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private readonly List<Sample> samples = new List<Sample>();
        private readonly float delay, minSpacing;

        internal PoseTrail(float delay, float minSpacing) { this.delay = delay; this.minSpacing = minSpacing; }

        internal int Count => samples.Count;
        internal void Clear() => samples.Clear();

        internal void Add(Vector3 position, Quaternion rotation, float pitch = 0)
        {
            float now = Time.unscaledTime;
            // Bunched packets get spread out; the timeline never runs far ahead of arrival.
            float time = samples.Count == 0 ? now : Mathf.Min(Mathf.Max(now, samples[samples.Count - 1].Time + minSpacing), now + delay);
            samples.Add(new Sample { Time = time, Position = position, Rotation = rotation, Pitch = pitch });
            while (samples.Count > 2 && samples[1].Time < now - delay - 0.5f) samples.RemoveAt(0);
            if (samples.Count > 32) samples.RemoveAt(0);
        }

        internal Sample Playback()
        {
            float at = Time.unscaledTime - delay;
            if (at <= samples[0].Time) return samples[0];
            for (int i = 1; i < samples.Count; i++)
            {
                if (at > samples[i].Time) continue;
                var a = samples[i - 1]; var b = samples[i];
                float u = Mathf.Clamp01((at - a.Time) / Mathf.Max(b.Time - a.Time, 0.0001f));
                return new Sample { Time = at, Position = Vector3.Lerp(a.Position, b.Position, u), Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, u), Pitch = Mathf.Lerp(a.Pitch, b.Pitch, u) };
            }
            return samples[samples.Count - 1];
        }
    }
}
