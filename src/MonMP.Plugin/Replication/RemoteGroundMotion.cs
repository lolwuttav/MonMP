using System;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Network samples keep their sender clock through relays. A short history absorbs
    // packet jitter and several poses dispatched by one Unity frame without changing gait.
    internal sealed class RemoteGroundMotion
    {
        internal const int Capacity = 16;
        internal const float BufferSeconds = 0.075f;
        internal const float PredictionSeconds = 0.05f;
        private const float StaleSeconds = 0.25f;
        private const double MinimumSampleSeconds = 0.000001;
        private const double OffsetCatchUpPerSecond = 0.02, OffsetDriftPerSecond = 0.002;
        private struct Sample { internal Vector3 Position; internal double Time, Offset; }
        private readonly Sample[] samples = new Sample[Capacity];
        private int first, count, cursor;
        private float receivedAt, evaluatedAt = float.NaN;
        private double clockOffset, presentationTime;
        private bool timed, clockUnpresented;
        private Vector3 cachedPosition;
        private float cachedSpeed;

        internal int SampleCount => count;
        internal double PresentationTime => presentationTime;

        internal void Clear() { count = 0; evaluatedAt = float.NaN; cachedSpeed = 0f; }

        // Local self-view/shadows use the current native feet immediately. They need no
        // network buffer or extrapolation; their two local samples still supply gait.
        internal void Accept(Vector3 position, float now, bool reset) => Accept(position, now, now, reset, false);

        internal void AcceptTimed(Vector3 position, float now, double sampledAt, bool reset) => Accept(position, now, sampledAt, reset, true);

        private void Accept(Vector3 position, float now, double sampledAt, bool reset, bool useBuffer)
        {
            if (double.IsNaN(sampledAt) || double.IsInfinity(sampledAt)) return;
            int newest = (first + count - 1 + Capacity) % Capacity;
            double elapsed = count == 0 ? 0 : sampledAt - samples[newest].Time;
            var movement = count == 0 ? Vector3.zero : position - samples[newest].Position;
            if (reset || count == 0 || timed != useBuffer || now < receivedAt || now - receivedAt > StaleSeconds ||
                elapsed < 0 || elapsed > StaleSeconds || elapsed <= MinimumSampleSeconds && movement.sqrMagnitude > 0.000001f)
            {
                first = cursor = 0; count = 1; timed = useBuffer;
                samples[0] = new Sample { Position = position, Time = sampledAt, Offset = now - sampledAt };
                clockOffset = now - sampledAt;
                clockUnpresented = true;
                presentationTime = sampledAt - (useBuffer ? BufferSeconds : 0f);
                receivedAt = now; evaluatedAt = float.NaN; cachedPosition = position; cachedSpeed = 0;
                return;
            }
            if (elapsed <= MinimumSampleSeconds)
            {
                // A repeated clock tick has no measurable duration. Coalesce it instead
                // of inventing a high speed or growing an all-equal-time history.
                samples[newest].Position = position;
            }
            else
            {
                int next = (first + count) % Capacity;
                if (count == Capacity)
                {
                    if (cursor == first) cursor = (cursor + 1) % Capacity;
                    first = (first + 1) % Capacity;
                }
                else count++;
                samples[next] = new Sample { Position = position, Time = sampledAt, Offset = now - sampledAt };
                if (useBuffer)
                {
                    // The least delayed receipt in the bounded recent window estimates
                    // clock offset. A late packet cannot move the playback clock backwards.
                    double minimum = samples[first].Offset;
                    for (int i = 1; i < count; i++) minimum = Math.Min(minimum, samples[(first + i) % Capacity].Offset);
                    // Before a new baseline is rendered, an entire queued burst can
                    // establish its offset together instead of replaying old backlog.
                    clockOffset = clockUnpresented ? minimum : minimum >= clockOffset
                        ? Math.Min(minimum, clockOffset + elapsed * OffsetDriftPerSecond)
                        : Math.Max(minimum, clockOffset - elapsed * OffsetCatchUpPerSecond);
                }
            }
            receivedAt = now; evaluatedAt = float.NaN;
        }

        internal Vector3 Position(float now) { Evaluate(now); return cachedPosition; }
        internal float Speed(float now) { Evaluate(now); return cachedSpeed; }

        private void Evaluate(float now)
        {
            if (count == 0 || now == evaluatedAt) return;
            clockUnpresented = false;
            int newest = (first + count - 1) % Capacity;
            if (!timed)
            {
                cachedPosition = samples[newest].Position;
                cachedSpeed = now - receivedAt <= PredictionSeconds * 2f && count > 1
                    ? HorizontalSpeed(Velocity((newest - 1 + Capacity) % Capacity, newest)) : 0f;
                evaluatedAt = now;
                return;
            }
            double targetTime = Math.Max(presentationTime, now - clockOffset - BufferSeconds);
            bool advancing = targetTime > presentationTime;
            presentationTime = targetTime;
            cachedSpeed = 0;
            if (targetTime <= samples[first].Time || count == 1)
                cachedPosition = samples[first].Position;
            else
            {
                // Playback only moves forward, so each accepted sample is crossed once.
                // This cursor avoids rescanning the history on every rendered frame.
                while (cursor != newest && samples[(cursor + 1) % Capacity].Time <= targetTime)
                    cursor = (cursor + 1) % Capacity;
                if (cursor != newest)
                {
                    int next = (cursor + 1) % Capacity;
                    float blend = (float)((targetTime - samples[cursor].Time) / (samples[next].Time - samples[cursor].Time));
                    cachedPosition = Vector3.Lerp(samples[cursor].Position, samples[next].Position, blend);
                    if (advancing) cachedSpeed = HorizontalSpeed(Velocity(cursor, next));
                }
                else
                {
                    var velocity = Velocity((newest - 1 + Capacity) % Capacity, newest);
                    double age = targetTime - samples[newest].Time;
                    cachedPosition = samples[newest].Position + velocity * (float)Math.Min(age, PredictionSeconds);
                    if (age < PredictionSeconds && advancing) cachedSpeed = HorizontalSpeed(velocity);
                }
            }
            evaluatedAt = now;
        }

        private Vector3 Velocity(int from, int to)
        {
            double elapsed = samples[to].Time - samples[from].Time;
            var velocity = elapsed > MinimumSampleSeconds ? (samples[to].Position - samples[from].Position) / (float)elapsed : Vector3.zero;
            // GodSpeed walking legitimately exceeds this speed. Keep those accepted
            // positions for interpolation; only gait/prediction retain the old bound.
            return velocity.sqrMagnitude > 400f ? velocity.normalized * 20f : velocity;
        }

        private static float HorizontalSpeed(Vector3 velocity) => new Vector2(velocity.x, velocity.z).magnitude;
    }
}
