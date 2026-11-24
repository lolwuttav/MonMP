using System;

namespace MonMP.Plugin.Replication
{
    // Small scalar-only pieces of the stock-body pose solver are kept here so the exact production
    // calculations can be exercised without loading Unity or the game's avatar assets.
    internal readonly struct GestureVector
    {
        internal readonly float X, Y, Z;
        internal GestureVector(float x, float y, float z) { X = x; Y = y; Z = z; }
        internal float SqrMagnitude => X * X + Y * Y + Z * Z;
        internal GestureVector Normalized
        {
            get
            {
                float length = MathF.Sqrt(SqrMagnitude);
                return length > 1e-6f ? new GestureVector(X / length, Y / length, Z / length) : default;
            }
        }
        internal static float Dot(GestureVector a, GestureVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        internal static GestureVector Cross(GestureVector a, GestureVector b) =>
            new GestureVector(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public static GestureVector operator +(GestureVector a, GestureVector b) => new GestureVector(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static GestureVector operator -(GestureVector a, GestureVector b) => new GestureVector(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static GestureVector operator *(GestureVector a, float value) => new GestureVector(a.X * value, a.Y * value, a.Z * value);
    }

    internal static class StockGesturePoseMath
    {
        // The stock body lets the head turn up to 80 degrees and the hand-held aim track up to 70.
        internal static float ClampHeadYaw(float value) => Math.Clamp(value, -80f, 80f);
        internal static float ClampHeadPitch(float value) => Math.Clamp(value, -80f, 80f);
        internal static float ClampAimPitch(float value) => Math.Clamp(value, -70f, 70f);
        internal static bool DriverReleasesRightHand(bool isDriver, bool gestureActive, float sorryWeight) =>
            isDriver && (gestureActive || sorryWeight > 0.001f);
        internal static float SeatedHeadYaw(float seatYaw, float cameraYaw) => ClampHeadYaw(DeltaAngle(seatYaw, cameraYaw));

        private static float DeltaAngle(float from, float to)
        {
            float delta = (to - from) % 360f;
            if (delta > 180f) delta -= 360f;
            if (delta < -180f) delta += 360f;
            return delta;
        }

        internal static GestureVector PointDirection(bool seated, GestureVector aimForward, GestureVector bodyUp, GestureVector bodyForward)
        {
            var direction = seated
                ? aimForward
                : aimForward - bodyUp * GestureVector.Dot(aimForward, bodyUp);
            if (direction.SqrMagnitude < 1e-4f) direction = bodyForward;
            return direction.Normalized;
        }

        // Seated stock poses put the shoulder about 0.18 m to the right and 0.18 m below
        // the head. Move the seated wave wrist away from the face while leaving the walking arc alone.
        // Components are in body right/up/forward order and scale with the cloned avatar.
        internal static GestureVector WaveOffset(bool seated, float phase, float scale)
        {
            float side = seated ? 0.37f + 0.02f * MathF.Sin(phase) : 0.24f + 0.035f * MathF.Sin(phase);
            float height = seated ? 0.12f : 0.08f;
            float forward = seated ? 0.04f + 0.02f * MathF.Cos(phase) : 0.08f + 0.1f * MathF.Cos(phase);
            return new GestureVector(side * scale, height * scale, forward * scale);
        }

        internal static GestureVector WaveTarget(GestureVector head, GestureVector bodyRight, GestureVector bodyUp,
            GestureVector bodyForward, float phase, float scale, bool seated)
        {
            var offset = WaveOffset(seated, phase, scale);
            return head + bodyRight * offset.X + bodyUp * offset.Y + bodyForward * offset.Z;
        }

        // The native right-hand hierarchy has a combined four-finger chain along +Y and a separate
        // thumb base offset from that chain. The projection gives the measured thumb-side direction
        // in the Hand.R local frame. This supplies the local joint axis; the palm normal is
        // independently checked as cross(finger, thumb-side) in the fixture.
        internal static GestureVector FingerCurlAxis(GestureVector fingerDirection, GestureVector thumbOffset)
        {
            var finger = fingerDirection.Normalized;
            var thumbSide = thumbOffset - finger * GestureVector.Dot(thumbOffset, finger);
            return thumbSide.Normalized;
        }

        internal const string ProximalFingerBone = "Fingers01.R";
        internal const string DistalFingerBone = "Fingers02.R";
        internal const string ThumbBaseBone = "Thumb01.R";
        internal const string ThumbDistalBone = "Thumb02.R";
        internal const string ThumbEndBone = "Thumb02.R_end";
        internal static bool HasThumbUpFingerChain(bool hasFingerBase, bool hasThumbBase) => hasFingerBase && hasThumbBase;
        internal static float FingerCurlDegrees(float weight, bool distal)
        {
            float bounded = Math.Clamp(weight, 0f, 1f);
            return -(distal ? 62f : 46f) * bounded;
        }

        internal static GestureVector ExtendDirection(GestureVector current, GestureVector bodyUp, float weight)
        {
            var direction = current.Normalized;
            var up = bodyUp.Normalized;
            if (direction.SqrMagnitude < 1e-6f || up.SqrMagnitude < 1e-6f) return direction;
            float bounded = Math.Clamp(weight, 0f, 1f);
            if (bounded <= 0f) return direction;
            if (bounded >= 1f) return up;
            float dot = Math.Clamp(GestureVector.Dot(direction, up), -1f, 1f);
            float angle = MathF.Acos(dot);
            if (angle < 1e-4f) return direction;
            if (MathF.PI - angle < 1e-4f)
            {
                // Pick the cardinal axis least aligned with the input, then construct a stable
                // orthogonal halfway direction for the exactly-opposite case.
                var cardinal = MathF.Abs(direction.X) < 0.8f
                    ? new GestureVector(1f, 0f, 0f) : new GestureVector(0f, 1f, 0f);
                var perpendicular = GestureVector.Cross(direction, cardinal).Normalized;
                return (direction * MathF.Cos(MathF.PI * bounded) + perpendicular * MathF.Sin(MathF.PI * bounded)).Normalized;
            }
            float sine = MathF.Sin(angle);
            float fromWeight = MathF.Sin((1f - bounded) * angle) / sine;
            float toWeight = MathF.Sin(bounded * angle) / sine;
            return (direction * fromWeight + up * toWeight).Normalized;
        }
    }
}
