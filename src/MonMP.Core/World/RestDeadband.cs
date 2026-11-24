using System;

namespace MonMP.Core.World
{
    /// <summary>
    /// Decides whether a host-simulated body that nobody drives is still resting at the sample
    /// already published for it.
    /// </summary>
    /// <remarks>
    /// Native vehicle bodies never sleep (their sleep threshold is zero), so a parked car or
    /// trailer changes by a few float bits every physics step and an exact comparison streamed
    /// it at the full motion rate indefinitely. The comparison is always against the published
    /// anchor, never the previous sample: slow creep leaves the distance bound and is published,
    /// so a peer's copy is never further from the host than these bounds.
    /// </remarks>
    public static class RestDeadband
    {
        /// <summary>Metres per second; both samples must be slower.</summary>
        public const float Speed = 0.05f;
        /// <summary>Radians per second; both samples must be slower.</summary>
        public const float Spin = 0.05f;
        /// <summary>Metres per axis from the published anchor.</summary>
        public const float Distance = 0.005f;
        /// <summary>Quaternion component difference from the anchor (about 0.11 degrees).</summary>
        public const float Rotation = 0.001f;

        public static bool Holds(ItemPose anchor, VehicleVector anchorVelocity, VehicleVector anchorSpin,
            ItemPose pose, VehicleVector velocity, VehicleVector spin)
        {
            // Every comparison is false for NaN, which then takes the exact path and its validation.
            if (!Slower(anchorVelocity, Speed) || !Slower(velocity, Speed) || !Slower(anchorSpin, Spin) || !Slower(spin, Spin)) return false;
            if (!Near(anchor.X, pose.X, Distance) || !Near(anchor.Y, pose.Y, Distance) || !Near(anchor.Z, pose.Z, Distance)) return false;
            // q and -q are the same rotation.
            float sign = anchor.Qx * pose.Qx + anchor.Qy * pose.Qy + anchor.Qz * pose.Qz + anchor.Qw * pose.Qw < 0 ? -1f : 1f;
            return Near(anchor.Qx, sign * pose.Qx, Rotation) && Near(anchor.Qy, sign * pose.Qy, Rotation) &&
                Near(anchor.Qz, sign * pose.Qz, Rotation) && Near(anchor.Qw, sign * pose.Qw, Rotation);
        }

        private static bool Slower(VehicleVector value, float limit) =>
            Math.Abs(value.X) < limit && Math.Abs(value.Y) < limit && Math.Abs(value.Z) < limit;
        private static bool Near(float left, float right, float limit) => Math.Abs(left - right) <= limit;
    }
}
