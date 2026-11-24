using System;
using System.Threading;
using UnityEngine;

namespace MonMP.Plugin.Integration
{
    // Typed form of the provider callbacks. Optional: Register also has an overload that needs
    // only BCL and Unity types, so an external mod can bind by reflection without referencing MonMP.
    public interface ILocalVrProvider
    {
        string OwnerId { get; }
        bool Active { get; }
        bool UiBlocksGameplay { get; }
        bool ControllerToolAim { get; }
        Ray? SelectionRay(Camera camera);
        Vector2? HeadAngles();
    }

    // The supported local extension point for a separate VR mod. MonMP references no XR API and
    // no VR mod: one external owner registers guarded callbacks; with no live, fresh, active owner
    // every caller below takes the original desktop path. Main thread only.
    //
    // This file holds ownership, freshness and sample validation and depends only on Unity value
    // types and Camera. Session/world context lives in LocalVrBridge.Context.cs.
    public static partial class LocalVrBridge
    {
        public const int ApiVersion = 1;

        internal const int FreshFrames = 3;
        internal const float FreshSeconds = 0.5f;
        internal const float MaxRayOriginDistance = 2.5f;
        internal const float MaxCoordinate = 100000f;
        internal const int MaxActiveFailures = 30;
        internal const int MaxOwnerIdLength = 64;

        private sealed class Registration : IDisposable, ILocalInteractionProvider
        {
            internal readonly string Owner;
            internal readonly Func<bool> Active, Ui;
            internal readonly Func<bool>? Tool;
            internal readonly Func<Camera, Ray?> Selection;
            internal readonly Func<Vector2?> Head;
            internal Func<int, int>? PickupIntent;
            internal Func<int, IDisposable?>? BeginItemGrant;
            internal int HeartbeatFrame;
            internal float HeartbeatTime;
            internal bool HasHeartbeat, Disposed;
            internal int ActiveFailures, Warned;

            internal Registration(string owner, Func<bool> active, Func<bool> ui, Func<Camera, Ray?> selection,
                Func<Vector2?> head, Func<bool>? tool)
            { Owner = owner; Active = active; Ui = ui; Selection = selection; Head = head; Tool = tool; }

            bool ILocalInteractionProvider.OwnsView => XrOwnsLocalView;
            bool ILocalInteractionProvider.BlocksInput => VrUiBlocksGameplay;
            bool ILocalInteractionProvider.IndependentAim => IndependentToolAimActive;
            // Item selection belongs to the live item-input capability, including a
            // transition in which the native camera has already been restored.
            bool ILocalInteractionProvider.SelectsOperatedItem => OnMainThread && PickupIntent != null &&
                OwnsRegistration(this) && Fresh(this, Frame());
            bool ILocalInteractionProvider.TryRay(Camera camera, out Ray ray) => TryControllerRay(camera, out ray);
            bool ILocalInteractionProvider.TryHead(out float yaw, out float pitch) => TryHeadAngles(out yaw, out pitch);
            int ILocalInteractionProvider.PickupIntent(int instance) => ItemHandIntent(instance);
            IDisposable? ILocalInteractionProvider.OpenPickup(int instance) => BeginItemHandGrant(instance);

            public void Dispose() { if (OnMainThread) Release(this, "released by its owner"); }
        }

        private static Registration? current;
        private static Registration? sampledOwner;
        private static int sampledFrame = int.MinValue;
        private static bool activeSample, uiSample, toolSample;
        private static int mainThread;
        private static bool bound;

        // Replaced by fixtures; production reads Unity's frame counter and unscaled clock.
        internal static Func<int> Frame = () => Time.frameCount;
        internal static Func<float> Clock = () => Time.unscaledTime;
        internal static Action<string> Warn = message => Debug.LogWarning(message);
        internal static Action<string> Info = message => Debug.Log(message);
        // Session-side capability for controller tool aim; bound with the plugin context.
        internal static Func<bool> ToolAimSupported = () => false;

        public static int LifecycleEpoch { get; private set; }
        public static event Action? LifecycleReset;

        public static bool ProviderRegistered => current != null;
        // A live owner whose heartbeat is fresh and whose active() is true this frame. While true
        // MonMP writes no local camera transform, matrix, culling or foreground-camera state.
        public static bool XrOwnsLocalView { get { if (!OnMainThread) return false; Sample(); return activeSample; } }
        public static bool VrUiBlocksGameplay { get { if (!OnMainThread) return false; Sample(); return uiSample; } }
        internal static bool ControllerToolAimRequested { get { if (!OnMainThread) return false; Sample(); return toolSample; } }

        // Called from the plugin's Awake on Unity's main thread.
        internal static void Bind()
        {
            mainThread = Thread.CurrentThread.ManagedThreadId;
            bound = true;
        }

        internal static void Unbind()
        {
            // Close registration before notifying subscribers: unload callbacks cannot acquire
            // another provider while the old registration is being released.
            bound = false;
            if (current != null) Release(current, "MonMP unloaded");
        }

        private static bool OnMainThread => bound && Thread.CurrentThread.ManagedThreadId == mainThread;
        private static bool OwnsRegistration(Registration registration) => !registration.Disposed && ReferenceEquals(current, registration);

        public static IDisposable? Register(ILocalVrProvider provider)
        {
            if (!OnMainThread || provider == null) return null;
            string owner;
            try { owner = provider.OwnerId; }
            catch { return null; }
            return Register(owner, () => provider.Active, () => provider.UiBlocksGameplay,
                camera => provider.SelectionRay(camera), () => provider.HeadAngles(), () => provider.ControllerToolAim);
        }

        public static IDisposable? Register(string ownerId, Func<bool> active, Func<bool> uiBlocksGameplay,
            Func<Camera, Ray?> selectionRay, Func<Vector2?> headAngles, Func<bool>? controllerToolAim)
        {
            if (!OnMainThread || current != null || string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > MaxOwnerIdLength ||
                active == null || uiBlocksGameplay == null || selectionRay == null || headAngles == null) return null;
            current = new Registration(ownerId, active, uiBlocksGameplay, selectionRay, headAngles, controllerToolAim);
            LocalInteraction.Provider = current;
            Invalidate();
            Info("MonMP local VR bridge: registered provider '" + ownerId + "' (API " + ApiVersion + ").");
            AdvanceLifecycle();
            return current;
        }

        // Optional local extension. The ID is a Unity GameObject instance ID on
        // this process, never a wire identity or a request to acquire an item.
        public static bool RegisterItemHands(IDisposable registration, Func<int, int> intent, Func<int, IDisposable?> beginGrant)
        {
            if (!OnMainThread || !(registration is Registration owner) || !OwnsRegistration(owner) ||
                intent == null || beginGrant == null || owner.PickupIntent != null) return false;
            owner.PickupIntent = intent;
            owner.BeginItemGrant = beginGrant;
            return true;
        }

        internal static int ItemHandIntent(int instance)
        {
            var owner = current;
            if (!OnMainThread || owner?.PickupIntent == null || !XrOwnsLocalView || !OwnsRegistration(owner)) return 0;
            try
            {
                int result = owner.PickupIntent(instance);
                if (!OwnsRegistration(owner) || result == 0) return 0;
                return result == 1 && !VrUiBlocksGameplay ? 1 : -1;
            }
            catch (Exception ex) { WarnOnce(owner, 32, "item pickup intent", ex); return -1; }
        }

        internal static IDisposable? BeginItemHandGrant(int instance)
        {
            var owner = current;
            if (ItemHandIntent(instance) != 1 || owner?.BeginItemGrant == null || !OwnsRegistration(owner)) return null;
            try
            {
                var slot = owner.BeginItemGrant(instance);
                if (slot == null) return null;
                var guarded = new ItemGrantScope(owner, slot);
                if (OwnsRegistration(owner)) return guarded;
                guarded.Dispose();
                return null;
            }
            catch (Exception ex) { WarnOnce(owner, 64, "item grant scope", ex); return null; }
        }

        private sealed class ItemGrantScope : IDisposable
        {
            private readonly Registration owner;
            private IDisposable? slot;
            internal ItemGrantScope(Registration owner, IDisposable slot) { this.owner = owner; this.slot = slot; }
            public void Dispose()
            {
                var restore = slot;
                slot = null;
                try { restore?.Dispose(); }
                catch (Exception ex) { WarnOnce(owner, 128, "item grant restoration", ex); }
            }
        }

        // Provider liveness, not sensor-pose freshness: call once per rendered frame for as long as
        // the provider's XR display owns the local view, including while tracking or focus is lost
        // (then return null head/ray samples and report the UI as blocking). Without it the
        // provider goes stale and MonMP resumes its own flat camera writes until the next call.
        public static void Heartbeat(IDisposable registration)
        {
            if (!OnMainThread || !(registration is Registration live) || live.Disposed || !ReferenceEquals(live, current)) return;
            live.HeartbeatFrame = Frame(); live.HeartbeatTime = Clock(); live.HasHeartbeat = true;
            // A heartbeat that arrives after this frame's sample must not wait a frame to count.
            if (!activeSample) Invalidate();
        }

        private static void Release(Registration registration, string why)
        {
            if (registration.Disposed) return;
            registration.Disposed = true;
            if (!ReferenceEquals(current, registration)) return;
            LocalInteraction.Provider = null;
            current = null;
            Invalidate();
            Info("MonMP local VR bridge: provider '" + registration.Owner + "' " + why + ".");
            AdvanceLifecycle();
        }

        internal static void AdvanceLifecycle()
        {
            unchecked { LifecycleEpoch++; }
            var handlers = LifecycleReset;
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList())
            {
                try { handler(); }
                catch (Exception ex) { Warn("MonMP local VR bridge: a lifecycle subscriber failed: " + ex.Message); }
            }
        }

        private static void Invalidate() { sampledFrame = int.MinValue; sampledOwner = null; activeSample = uiSample = toolSample = false; }

        private static void WarnOnce(Registration registration, int bit, string callback, Exception ex, string outcome = "ignored")
        {
            if ((registration.Warned & bit) != 0) return;
            registration.Warned |= bit;
            Warn("MonMP local VR bridge: provider '" + registration.Owner + "' " + callback + " failed and is " + outcome + ": " + ex.Message);
        }

        private static bool Fresh(Registration registration, int frame)
        {
            if (!registration.HasHeartbeat) return false;
            int frames = unchecked(frame - registration.HeartbeatFrame);
            float seconds = Clock() - registration.HeartbeatTime;
            return frames >= 0 && frames <= FreshFrames && seconds >= 0 && seconds <= FreshSeconds;
        }

        // active/uiBlocksGameplay/controllerToolAim are read at most once per frame.
        private static void Sample()
        {
            // Refuse before reading Unity clocks or changing the shared frame cache.
            if (!OnMainThread) return;
            var registration = current;
            if (registration == null) { if (sampledOwner != null || activeSample) Invalidate(); return; }
            int frame = Frame();
            if (frame == sampledFrame && ReferenceEquals(sampledOwner, registration)) return;
            sampledFrame = frame; sampledOwner = registration;
            activeSample = uiSample = toolSample = false;
            // A stale provider is not called at all.
            if (!OnMainThread || !Fresh(registration, frame)) return;
            bool active;
            try { active = registration.Active(); registration.ActiveFailures = 0; }
            catch (Exception ex)
            {
                WarnOnce(registration, 1, "active", ex);
                if (++registration.ActiveFailures >= MaxActiveFailures) Release(registration, "dropped after repeated failures");
                return;
            }
            if (!OwnsRegistration(registration)) { Invalidate(); return; }
            if (!active) return;
            activeSample = true;
            // The provider owns the view but cannot say whether its modal UI is open: fail closed for
            // this frame so MonMP gameplay/social input stays gated. Release and cancel are never gated.
            try { uiSample = registration.Ui(); }
            catch (Exception ex) { uiSample = true; WarnOnce(registration, 2, "uiBlocksGameplay", ex, "treated as blocking"); }
            if (!OwnsRegistration(registration)) { Invalidate(); return; }
            if (registration.Tool != null)
            {
                try { toolSample = registration.Tool(); }
                catch (Exception ex) { WarnOnce(registration, 4, "controllerToolAim", ex); }
                if (!OwnsRegistration(registration)) { Invalidate(); return; }
            }
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value, float bound) => Finite(value.x) && Finite(value.y) && Finite(value.z) &&
            Mathf.Abs(value.x) <= bound && Mathf.Abs(value.y) <= bound && Mathf.Abs(value.z) <= bound;

        internal static bool ValidRay(Ray ray, Vector3 eye, out Ray normalized)
        {
            normalized = default;
            Vector3 origin = ray.origin, direction = ray.direction;
            if (!Finite(origin, MaxCoordinate) || !Finite(direction, 2f) || !Finite(eye, float.MaxValue)) return false;
            float length = direction.magnitude;
            if (!Finite(length) || Mathf.Abs(length - 1f) > 0.001f) return false;
            if ((origin - eye).sqrMagnitude > MaxRayOriginDistance * MaxRayOriginDistance) return false;
            normalized = new Ray(origin, direction / length);
            return true;
        }

        internal static bool ValidHeadAngles(Vector2 angles, out float yaw, out float pitch)
        {
            yaw = pitch = 0;
            if (!Finite(angles.x) || !Finite(angles.y) || Mathf.Abs(angles.x) > MaxCoordinate || angles.y < -90f || angles.y > 90f) return false;
            yaw = Mathf.Repeat(angles.x, 360f); pitch = angles.y;
            if (yaw >= 360f) yaw = 0;
            return true;
        }

        // The tracked controller ray for this native camera, when one is admitted right now.
        internal static bool TryControllerRay(Camera camera, out Ray ray)
        {
            ray = default;
            if (!OnMainThread || camera == null) return false;
            Sample();
            var registration = current;
            if (!activeSample || registration == null) return false;
            Ray? sample;
            try { sample = registration.Selection(camera); }
            catch (Exception ex) { WarnOnce(registration, 8, "selectionRay", ex); return false; }
            return OwnsRegistration(registration) && sample.HasValue && ValidRay(sample.Value, camera.transform.position, out ray);
        }

        // HMD world yaw/pitch for legacy pose publication. False means "use the native camera".
        internal static bool TryHeadAngles(out float yaw, out float pitch)
        {
            yaw = pitch = 0;
            if (!OnMainThread) return false;
            Sample();
            var registration = current;
            if (!activeSample || registration == null) return false;
            Vector2? sample;
            try { sample = registration.Head(); }
            catch (Exception ex) { WarnOnce(registration, 16, "headAngles", ex); return false; }
            return OwnsRegistration(registration) && sample.HasValue && ValidHeadAngles(sample.Value, out yaw, out pitch);
        }

        public static Ray SelectionRay(Camera camera) => OnMainThread ? LocalInteraction.SelectionRay(camera) : default;

        public static bool IndependentToolAimActive
        {
            get
            {
                if (!OnMainThread) return false;
                Sample();
                if (!activeSample || !toolSample) return false;
                try { return ToolAimSupported(); }
                catch { return false; }
            }
        }

        // ToolAimPose returns the mode of the sampled ray for transmission with the action.
        public static Ray ToolAimRay(Camera camera) => OnMainThread ? LocalInteraction.SampleTool(camera).Ray : default;

        internal static bool ToolAimPose(Camera camera, out Vector3 origin, out float yaw, out float pitch)
        {
            var aim = OnMainThread ? LocalInteraction.SampleTool(camera) : default;
            origin = aim.Origin; yaw = aim.Yaw; pitch = aim.Pitch;
            return aim.Independent;
        }

#if MONMP_DEVELOPMENT
        internal static void ResetForTest()
        {
            if (current != null) current.Disposed = true;
            LocalInteraction.Provider = null;
            current = null; Invalidate(); LifecycleEpoch = 0; LifecycleReset = null;
        }
#endif
    }
}
