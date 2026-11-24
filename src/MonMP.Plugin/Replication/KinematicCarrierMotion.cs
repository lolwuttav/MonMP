using System.Collections.Generic;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The host moves vehicles and trailers driven by another peer as kinematic bodies through
    // MovePosition, and Unity reports zero velocity for them (#250). Record each physics step's
    // target so loads pinned to them follow the body's actual motion and leave at its speed.
    internal static class KinematicCarrierMotion
    {
        private readonly struct Step
        {
            internal readonly Vector3 Position, Velocity, Spin;
            internal readonly Quaternion Rotation;
            internal Step(Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 spin)
            { Position = position; Rotation = rotation; Velocity = velocity; Spin = spin; }
        }
        private static readonly Dictionary<Rigidbody, Step> steps = new Dictionary<Rigidbody, Step>();

        // Called before any carrier moves in a physics step. Entries stay readable until the
        // next step, so Update-time releases between steps still see the latest motion.
        internal static void BeginStep() => steps.Clear();

        internal static void Clear() => steps.Clear();

        internal static void Record(Rigidbody body, Vector3 position, Quaternion rotation)
        {
            float dt = Time.fixedDeltaTime;
            if (body == null || dt <= 0) return;
            (rotation * Quaternion.Inverse(body.rotation)).ToAngleAxis(out float angle, out var axis);
            if (angle > 180) angle -= 360;
            var spin = Mathf.Abs(angle) > 0.0001f && IsFinite(axis) ? axis.normalized * (angle * Mathf.Deg2Rad / dt) : Vector3.zero;
            steps[body] = new Step(position, rotation, (position - body.position) / dt, spin);
        }

        internal static bool TryTarget(Rigidbody body, out Vector3 position, out Quaternion rotation)
        {
            if (body != null && body.isKinematic && steps.TryGetValue(body, out var step))
            { position = step.Position; rotation = step.Rotation; return true; }
            position = default; rotation = Quaternion.identity; return false;
        }

        // MoveRotation turns the body about its own origin, not its centre of mass.
        internal static Vector3 PointVelocity(Rigidbody body, Vector3 point) =>
            body.isKinematic && steps.TryGetValue(body, out var step) ? step.Velocity + Vector3.Cross(step.Spin, point - body.position) : body.GetPointVelocity(point);

        internal static Vector3 Spin(Rigidbody body) =>
            body.isKinematic && steps.TryGetValue(body, out var step) ? step.Spin : body.angularVelocity;

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
