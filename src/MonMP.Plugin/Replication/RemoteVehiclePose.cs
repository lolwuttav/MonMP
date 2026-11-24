using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Render-driven replicas follow the accepted trajectory between packets. A new sample
    // corrects that continuous trajectory, rather than restarting from last frame's body.
    internal sealed class RemoteVehiclePose
    {
        internal const float CorrectionSeconds = 0.08f;
        internal const float PredictionSeconds = 0.25f;
        private bool known;
        private float receivedAt;
        private Vector3 targetPosition, velocity, angularVelocity, correction;
        private Quaternion targetRotation = Quaternion.identity, rotationCorrection = Quaternion.identity;

        internal void Clear() => known = false;

        internal void Accept(Vector3 position, Quaternion rotation, Vector3 linear, Vector3 angular, float now, bool snap)
        {
            var from = position; var fromRotation = rotation;
            if (known && !snap && now >= receivedAt) Evaluate(now, out from, out fromRotation);
            targetPosition = position; targetRotation = rotation;
            velocity = linear; angularVelocity = angular;
            correction = from - position;
            rotationCorrection = fromRotation * Quaternion.Inverse(rotation);
            receivedAt = now; known = true;
        }

        internal void Evaluate(float now, out Vector3 position, out Quaternion rotation)
        {
            float age = Mathf.Max(0f, now - receivedAt);
            float prediction = Mathf.Min(age, PredictionSeconds);
            float blend = Mathf.Clamp01(age / CorrectionSeconds);
            position = targetPosition + velocity * prediction + correction * (1f - blend);
            float spin = angularVelocity.magnitude;
            var predictedRotation = spin > .0001f
                ? Quaternion.AngleAxis(spin * prediction * Mathf.Rad2Deg, angularVelocity / spin) * targetRotation
                : targetRotation;
            rotation = Quaternion.Slerp(rotationCorrection, Quaternion.identity, blend) * predictedRotation;
        }

        // Update-driven copies must not also be rewound by Unity's physics interpolation.
        // Host remote physics still uses MovePosition and needs native interpolation.
        internal static void ConfigureInterpolation(Rigidbody body, bool local, bool host, RigidbodyInterpolation original)
        {
            var desired = local ? original : host ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;
            if (body.interpolation != desired) body.interpolation = desired;
        }
    }
}
