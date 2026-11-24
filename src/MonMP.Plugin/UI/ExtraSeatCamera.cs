using System;
using MonMP.Plugin.Replication;
using NWH.VehiclePhysics2;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MonMP.Plugin.UI
{
    // Native gameplay keeps its seated eye; a full render-scoped camera pose
    // keeps culling, camera-position shaders and native image effects consistent.
    internal sealed class ExtraSeatCamera
    {
        private readonly MonMPPlugin plugin;
        private Camera? camera;
        private Camera? foreground;
        private bool foregroundWasEnabled, foregroundHidden, viewReady, callbacksAttached;
        private VehicleController? vehicle;
        private InputAction? changeView;
        private InputAction? sourceAction;
        private RaycastHit[] hits = new RaycastHit[32];
        private Vector3 localFocus;
        private float distance;
        private bool thirdPerson;
        private bool changeRequested, warnedView;
        private PassengerRenderScope? renderScope;
        internal Vector3 ViewPosition { get; private set; }
        internal bool Active => thirdPerson && viewReady && camera != null;

        internal ExtraSeatCamera(MonMPPlugin plugin) { this.plugin = plugin; }

        internal void Tick()
        {
            renderScope?.Restore();
            // A VR provider owns the local view: restore and unbind the render scope, foreground
            // camera and input lease, then stay idle so nothing repositions the headset.
            if (plugin.ExternalViewOwned) { Reset(); return; }
            var gameplay = plugin.Gameplay;
            var seated = plugin.Session?.IsRunning == true && plugin.World?.Channel.FullySynchronized == true &&
                gameplay != null && !gameplay.Get_IsDead && !gameplay.Get_IsSleeping && !plugin.Spectating
                ? PassengerVehicle() : null;
            var nativeCamera = gameplay?.Player?.mainCamera;
            if (seated == null || nativeCamera == null) { Reset(); return; }
            if (vehicle != seated || camera != nativeCamera)
            {
                Reset(); vehicle = seated; camera = nativeCamera;
                foreground = gameplay!.PlayerWalking.playerCameraController?.cam3DInFrontOfEverything;
                Camera.onPreCull += BeforeRender;
                callbacksAttached = true;
                FindFraming(seated);
            }
            var action = Singleton<GameAssets>.i?.vehicleInputs?.VehicleControls.ChangeCamera;
            if (action != sourceAction)
            {
                DisposeInput(); sourceAction = action;
                changeView = action?.Clone();
                if (changeView != null && action != null)
                {
                    // Clone preserves authored bindings; carry user rebindings too.
                    for (int i = 0; i < action.bindings.Count; i++)
                        if (action.bindings[i].overridePath != null)
                            changeView.ApplyBindingOverride(i, action.bindings[i]);
                    changeView.performed += ChangePerformed;
                    changeView.Enable();
                }
            }
            bool requested = changeRequested;
            changeRequested = false;
            if (Application.isFocused && !plugin.SocialUiBlocked && !gameplay!.Get_isGamePaused &&
                plugin.World?.SaveGate.Active != true && plugin.GuestWorld?.HoldPresentation != true && requested)
            {
                thirdPerson = !thirdPerson;
                if (!thirdPerson) { renderScope?.Unbind(); RestoreForeground(); viewReady = false; }
                else
                {
                    renderScope = camera!.GetComponent<PassengerRenderScope>() ?? camera.gameObject.AddComponent<PassengerRenderScope>();
                    renderScope.Bind(camera);
                }
            }
        }

        private VehicleController? PassengerVehicle() => plugin.World?.ExtraSeats.CurrentLocalPassengerVehicle ??
            plugin.World?.Vehicles.CurrentLocalPassengerVehicle;

        private void ChangePerformed(InputAction.CallbackContext _) { changeRequested = true; }
        private void DisposeInput()
        {
            if (changeView != null) { changeView.performed -= ChangePerformed; changeView.Dispose(); }
            changeView = null; changeRequested = false;
        }
        private bool NativeView(Camera source) => ExtraSeatCameraView.IsNativeView(source.worldToCameraMatrix,
            source.transform.position, source.transform.rotation, source.transform.worldToLocalMatrix);
        private void RefuseView()
        {
            thirdPerson = false; renderScope?.Unbind(); RestoreForeground(); viewReady = false;
            if (!warnedView) { warnedView = true; Debug.LogWarning("MonMP added-seat camera: an unrecognized custom camera matrix prevented switching views."); }
        }

        private void FindFraming(VehicleController target)
        {
            var body = target.vehicleRigidbody;
            var bounds = new Bounds(target.transform.position + target.transform.up, Vector3.one * 2f);
            bool found = false;
            foreach (var collider in target.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null || !collider.enabled || collider.isTrigger || collider.attachedRigidbody != body) continue;
                if (!found) { bounds = collider.bounds; found = true; }
                else bounds.Encapsulate(collider.bounds);
            }
            localFocus = target.transform.InverseTransformPoint(bounds.center);
            distance = Mathf.Clamp(bounds.extents.magnitude + 1.5f, 3.5f, 11f);
        }

        internal void LateTick()
        {
            // XR can acquire the view after this frame's Update.
            if (plugin.ExternalViewOwned) { if (camera != null || callbacksAttached) Reset(); return; }
            if (!thirdPerson || camera == null || vehicle == null) return;
            // A seat can be revoked or exited after this component's Update.
            if (plugin.Spectating || PassengerVehicle() != vehicle)
            { Reset(); return; }
            var eye = camera.transform;
            if (!NativeView(camera)) { RefuseView(); return; }
            var focus = vehicle.transform.TransformPoint(localFocus) + vehicle.transform.up * .35f;
            Vector3 direction = -eye.forward;
            float wanted = distance;
            int count;
            while (true)
            {
                count = Physics.SphereCastNonAlloc(focus, .18f, direction, hits, wanted,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                if (count < hits.Length) break;
                if (hits.Length >= 256) { wanted = .2f; count = 0; break; }
                Array.Resize(ref hits, hits.Length * 2);
            }
            var walker = plugin.Gameplay?.PlayerWalking?.transform;
            for (int i = 0; i < count; i++)
            {
                var collider = hits[i].collider;
                if (collider == null || collider.attachedRigidbody == vehicle.vehicleRigidbody ||
                    collider.transform.IsChildOf(vehicle.transform) || walker != null && collider.transform.IsChildOf(walker) ||
                    LooseItem(collider)) continue;
                wanted = Mathf.Min(wanted, Mathf.Max(.2f, hits[i].distance - .2f));
            }
            ViewPosition = focus + direction * wanted;
            viewReady = true;
            if (!foregroundHidden && foreground != null && foreground != camera)
            { foregroundWasEnabled = foreground.enabled; foreground.enabled = false; foregroundHidden = true; }
        }

        private static bool LooseItem(Collider collider)
        {
            // Loose and frozen loads have independent bodies/hierarchies. They
            // must not pull the chase camera into the bed. No physics or item
            // collision settings change; installed parts remain world geometry.
            var item = collider.GetComponentInParent<InteractableItems>();
            return item != null && !(item is InteractableParts part && (part.isPlaced || part.TempPlaced)) &&
                !(item is Hardware_Bolt bolt && bolt.isPlaced);
        }

        private void BeforeRender(Camera rendering)
        {
            if (plugin.ExternalViewOwned) { renderScope?.Restore(); return; }
            if (!Active || rendering != camera) return;
            if (renderScope?.OwnsPose == true) return;
            if (!NativeView(rendering)) { RefuseView(); return; }
            renderScope?.Apply(ViewPosition);
        }

        // Nameplates/pings use this same render viewpoint. Scope the override to
        // projection so native ViewportPointToRay in Update keeps the real seat eye.
        internal Vector3 Project(Camera source, Vector3 world)
        {
            if (!Active || source != camera || renderScope?.OwnsPose == true || plugin.ExternalViewOwned) return source.WorldToScreenPoint(world);
            var previous = source.worldToCameraMatrix;
            bool native = NativeView(source);
            var displayed = previous * Matrix4x4.Translate(source.transform.position - ViewPosition);
            try { source.worldToCameraMatrix = displayed; return source.WorldToScreenPoint(world); }
            finally { if (native) source.ResetWorldToCameraMatrix(); else source.worldToCameraMatrix = previous; }
        }
        private void RestoreForeground()
        {
            if (foregroundHidden && foreground != null && !foreground.enabled) foreground.enabled = foregroundWasEnabled;
            foregroundHidden = false;
        }

        internal void Reset()
        {
            if (callbacksAttached)
            {
                Camera.onPreCull -= BeforeRender;
                callbacksAttached = false;
            }
            renderScope?.Unbind(); renderScope = null;
            RestoreForeground(); viewReady = false; thirdPerson = false; camera = null; foreground = null; vehicle = null;
            DisposeInput(); sourceAction = null; warnedView = false;
        }
    }
}
