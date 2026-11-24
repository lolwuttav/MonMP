namespace MonMP.Plugin.Replication
{
    // Keeps the latest authoritative window value until the native button and sound
    // components have started and the control is active again.
    internal sealed class DeferredVehicleWindowState<T>
    {
        private T pending = default!;
        private bool hasPending;
        private bool effects;
        private bool snap;

        internal bool HasPending => hasPending;

        internal void Stage(T state, bool playEffects, bool snapPose)
        {
            pending = state;
            effects = playEffects;
            snap = snapPose;
            hasPending = true;
        }

        internal bool TryTake(bool buttonStarted, bool soundStarted, bool enabled, bool activeInHierarchy,
            out T state, out bool playEffects, out bool snapPose)
        {
            if (!CanUseNative(buttonStarted, soundStarted, enabled, activeInHierarchy) || !hasPending)
            {
                state = default!;
                playEffects = false;
                snapPose = false;
                return false;
            }

            state = pending;
            playEffects = effects;
            snapPose = snap;
            pending = default!;
            effects = false;
            snap = false;
            hasPending = false;
            return true;
        }

        internal void Clear()
        {
            pending = default!;
            effects = false;
            snap = false;
            hasPending = false;
        }

        internal static bool HasCapturedButtonPose(float rotationNormSquared) =>
            rotationNormSquared > 0.5f && !float.IsNaN(rotationNormSquared) && !float.IsInfinity(rotationNormSquared);

        internal static bool CanUseNative(bool buttonStarted, bool soundStarted, bool enabled, bool activeInHierarchy) =>
            buttonStarted && soundStarted && enabled && activeInHierarchy;
    }
}
