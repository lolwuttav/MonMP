using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace MonMP.Plugin.UI
{
    // A walking or forced-permadeath camera lease. The native walker stays at its real position; only its
    // detached camera follows a remote avatar, and only a separate spectator-head point is sent.
    internal sealed class SpectatorCamera
    {
        private static readonly System.Reflection.FieldInfo? FlashlightField =
            AccessTools.Field(typeof(PlayerVisualHands), "isFlashLightActive");
        private static readonly System.Reflection.FieldInfo? HandsAnimatorField =
            AccessTools.Field(typeof(PlayerVisualHands), "animator");

        private readonly MonMPPlugin plugin;
        private readonly SpectatorHint hint = new SpectatorHint();
        private readonly SpectatorCameraCollision collision = new SpectatorCameraCollision();
        private readonly List<InputAction> restoreActions = new List<InputAction>();
        private readonly List<InputActionAsset> capturedAssets = new List<InputActionAsset>();
        private Gameplay? capturedGameplay;
        private GameAssets? capturedGameAssets;
        private UI_GenericWindowManager? capturedWindowManager;
        private InputActionAsset? capturedPlayerActions, capturedVehicleActions;
        private object? capturedPlayer;
        private Camera? camera;
        private Transform? originalParent;
        private Vector3 originalLocalPosition, originalLocalScale, originalWorldPosition;
        private Quaternion originalLocalRotation, originalWorldRotation;
        private CursorLockMode originalCursorLock;
        private bool originalCursorVisible, ownsCursor, cameraDetached, active, restorePending, waitForEscapeRelease;
        private int sceneHandle = -1;
        private float nextWindowManagerLookup;
        private bool hasCapturedWindowManager;
        private uint? queuedTarget, target;
        private bool forced;
        private float yaw, pitch, distance = 5.5f, queuedUntil;

        internal SpectatorCamera(MonMPPlugin plugin) { this.plugin = plugin; }
        internal bool Active => active;
        internal uint? CurrentTarget => active ? target : null;
        internal uint? QueuedTarget => queuedTarget;
        internal Camera? LeasedCamera => camera;
        internal string Status { get; private set; } = "";

        internal bool CanQueueTarget(uint peer) =>
            queuedTarget == null && !restorePending && !cameraDetached && peer != plugin.Session?.LocalPeerId && TargetAvailable(peer) &&
            CanEnter(playbackUiAllowed: plugin.PauseMenuOpen && plugin.NativeHostScreenOpen, requireNativeInput: false);
        internal bool StartTarget(uint peer) => TryStart(peer);

        internal bool QueueTarget(uint peer)
        {
            if (!CanQueueTarget(peer))
            {
                Status = "Spectating needs a synchronized walking player with empty hands, no held item or tool, and no save or gameplay window.";
                return false;
            }
            queuedTarget = peer;
            queuedUntil = Time.unscaledTime + 30f;
            Status = "Spectate queued. Close the multiplayer and pause menus to begin.";
            return true;
        }

        internal bool CancelQueuedTarget(uint peer)
        {
            if (queuedTarget != peer) return false;
            queuedTarget = null;
            Status = "Spectate request canceled.";
            return true;
        }

        internal void ForcePermadeath()
        {
            forced = true; queuedTarget = null;
            // Dead players never regain their previously enabled gameplay actions.
            restorePending = false; restoreActions.Clear();
        }

        internal void Tick()
        {
            if (forced && plugin.World?.Permadeath?.LocalDead == true)
            {
                if (!active) TryStart(NextLivingTarget() ?? uint.MaxValue);
                if (active && !TargetAvailable(target ?? uint.MaxValue)) target = NextLivingTarget();
                if (!active && Application.isFocused && (Keyboard.current?.escapeKey.wasPressedThisFrame == true ||
                    Gamepad.current?.startButton.wasPressedThisFrame == true)) { plugin.MenuDisconnect(); return; }
            }
            // A previous stop may have deferred restoration until the native walking state,
            // cursor, and menus are stable. Do this before considering a new queued request.
            if (!active) TryRestorePending();
            if (active)
            {
                if (!InputOwnerIsCurrent() || !forced && (!CanEnter(playbackUiAllowed: false, requireNativeInput: false,
                    useCapturedGameAssets: true, useCapturedWindowManager: true, requireFocus: false) || !TargetAvailable(target ?? 0)))
                { End(waitForEscape: false); return; }

                DisableCapturedAssets();
                // Keep following the target through Alt-Tab in LateTick, but leave keys,
                // mouse motion and cursor ownership alone until the game has focus again.
                if (!Application.isFocused) return;
                RestoreLeasedCursor();
                if (Keyboard.current?.escapeKey.wasPressedThisFrame == true || forced && Gamepad.current?.startButton.wasPressedThisFrame == true)
                { if (forced) plugin.MenuDisconnect(); else End(waitForEscape: true); return; }
                if (Keyboard.current?.f6Key.wasPressedThisFrame == true || forced && Gamepad.current?.rightShoulder.wasPressedThisFrame == true) CycleTarget();
                ReadOrbitInput();
            }
            else if (queuedTarget is uint peer)
            {
                if (Time.unscaledTime >= queuedUntil)
                { queuedTarget = null; Status = "Spectate request expired. Choose a player again from Multiplayer."; }
                else if (!TargetAvailable(peer))
                { queuedTarget = null; Status = "The selected player is no longer available."; }
                else
                {
                    // The pause-menu row only queues a target. The native menu remains the owner
                    // of input and cursor until it has closed and the walking player has resumed.
                    if (WaitingForTransientStartGate()) { }
                    else if (!CanEnter(playbackUiAllowed: false, requireNativeInput: false))
                    { queuedTarget = null; Status = "Spectating could not start because the player state changed."; }
                    else if (NativeInputReady())
                    {
                        queuedTarget = null;
                        if (!TryStart(peer)) Status = "Spectating could not start; resume synchronized walking with your hands empty.";
                    }
                }
            }
        }

        internal void LateTick()
        {
            if (!active) return;
            if (!InputOwnerIsCurrent()) { End(waitForEscape: false); return; }
            DisableCapturedAssets();
            SyncViewOwnership();
            // Following is presentation, not input. Background frames must track the latest
            // interpolated head without consuming mouse/keyboard or reclaiming the cursor.
            if (camera != null && camera.transform != null) PlaceCamera();
        }

        internal bool Forced => forced;

        // A VR provider that owns the local view keeps the native eye: the lease, forced policy,
        // disabled input and published spectator state stay, but the camera is neither detached
        // nor moved. Hand the eye back if XR acquires mid-lease and resume the flat orbit if it
        // releases (the provider restores the native local camera pose before reporting inactive).
        private void SyncViewOwnership()
        {
            bool externalView = plugin.ExternalViewOwned;
            if (externalView && cameraDetached) RestoreCamera();
            else if (!externalView && !cameraDetached && active && camera != null && CameraOwnerIsCurrent())
            {
                originalParent = camera.transform.parent;
                originalLocalPosition = camera.transform.localPosition;
                originalLocalRotation = camera.transform.localRotation;
                originalLocalScale = camera.transform.localScale;
                originalWorldPosition = camera.transform.position;
                originalWorldRotation = camera.transform.rotation;
                camera.transform.SetParent(null, true);
                cameraDetached = true;
            }
        }

        // The collision-checked orbit pose MonMP writes to the flat camera, for a VR provider
        // that presents spectating itself.
        internal bool SuggestedView(out Vector3 position, out Quaternion rotation, out Vector3 focus)
        {
            position = focus = default; rotation = Quaternion.identity;
            if (!active || target == null) return false;
            var targetHead = plugin.RemoteAvatarHeadPosition(target.Value);
            if (!targetHead.HasValue) return false;
            focus = targetHead.Value;
            Quaternion orbit = Quaternion.Euler(pitch, yaw, 0);
            Vector3 offset = orbit * Vector3.back * distance;
            float wanted = offset.magnitude;
            Vector3 direction = offset / wanted;
            position = focus + direction * collision.CameraDistance(focus, direction, wanted);
            rotation = Quaternion.LookRotation(focus - position, Vector3.up);
            return true;
        }

        private void DisableCapturedAssets()
        {
            foreach (var asset in capturedAssets) if (asset != null && asset.enabled) asset.Disable();
        }

        internal void Draw()
        {
            if (forced && active && !MonMPPlugin.CleanFrame)
            {
                string name = target.HasValue && plugin.Session?.Peers.TryGetValue(target.Value, out var living) == true
                    ? "Permadeath: spectating " + living.DisplayName : "Permadeath: waiting for a living player";
                hint.Show(name, true); return;
            }
            if (MonMPPlugin.CleanFrame || !active || target == null ||
                plugin.Session?.Peers.TryGetValue(target.Value, out var peer) != true)
            { hint.Hide(); return; }
            hint.Show(peer.DisplayName);
        }


#if MONMP_DEVELOPMENT
        internal bool TryStartForTest(uint peer) => TryStart(peer);


        internal void StopForTest() => End(waitForEscape: false);
#endif
        internal void Stop() => End(waitForEscape: false);


#if MONMP_DEVELOPMENT
        internal bool TestSetOrbitForProbe(float yawDelta)
        {
            if (!active || float.IsNaN(yawDelta) || float.IsInfinity(yawDelta)) return false;
            yaw += yawDelta;
            PlaceCamera();
            return true;
        }
#endif


        private bool TryStart(uint peer)
        {
            if (active || restorePending || cameraDetached || queuedTarget != null || !forced && (!TargetAvailable(peer) ||
                !CanEnter(playbackUiAllowed: false, requireNativeInput: true))) return false;
            var gameplay = plugin.Gameplay;
            var player = gameplay?.Player;
            var gameAssets = UnityEngine.Object.FindObjectOfType<GameAssets>();
            var nativeCamera = player?.mainCamera;
            if (gameplay == null || player == null || gameAssets?.inputs == null || gameAssets.vehicleInputs == null || nativeCamera == null ||
                !forced && (Cursor.lockState != CursorLockMode.Locked || !Application.isFocused)) return false;

            capturedGameplay = gameplay;
            capturedPlayer = player;
            capturedGameAssets = gameAssets;
            capturedWindowManager = UnityEngine.Object.FindObjectOfType<UI_GenericWindowManager>();
            hasCapturedWindowManager = capturedWindowManager != null;
            nextWindowManagerLookup = hasCapturedWindowManager ? Time.unscaledTime + 1f : Time.unscaledTime;
            capturedPlayerActions = gameAssets.inputs.asset;
            capturedVehicleActions = gameAssets.vehicleInputs.asset;
            camera = nativeCamera;
            sceneHandle = SceneManager.GetActiveScene().handle;
            originalParent = camera.transform.parent;
            originalLocalPosition = camera.transform.localPosition;
            originalLocalRotation = camera.transform.localRotation;
            originalLocalScale = camera.transform.localScale;
            originalWorldPosition = camera.transform.position;
            originalWorldRotation = camera.transform.rotation;
            originalCursorLock = Cursor.lockState;
            originalCursorVisible = Cursor.visible;
            ownsCursor = true;

            restoreActions.Clear(); capturedAssets.Clear();
            try
            {
                var assets = new List<InputActionAsset> { gameAssets.inputs.asset, gameAssets.vehicleInputs.asset };
                foreach (var ship in UnityEngine.Object.FindObjectsOfType<NWH.DWP2.ShipController.InputSystemShipInputProvider>())
                    if (ship.shipInputActions != null) assets.Add(ship.shipInputActions.asset);
                foreach (var asset in assets.Distinct())
                {
                    capturedAssets.Add(asset);
                    restoreActions.AddRange(asset.Where(action => action.enabled));
                    asset.Disable();
                }

                var euler = camera.transform.eulerAngles;
                yaw = euler.y;
                pitch = Mathf.Clamp(-Mathf.DeltaAngle(0, euler.x), -20f, 65f);
                distance = 5.5f;
                if (!plugin.ExternalViewOwned)
                {
                    camera.transform.SetParent(null, true);
                    cameraDetached = true;
                }
                active = true;
                target = forced && peer == uint.MaxValue ? null : peer;
                waitForEscapeRelease = false;
                PlaceCamera();
                Status = "";
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("MonMP could not start spectating: " + ex.Message);
                active = false; target = null;
                RestoreCamera();
                restorePending = true;
                TryRestorePending();
                return false;
            }
        }

        private bool CanEnter(bool playbackUiAllowed, bool requireNativeInput, bool useCapturedGameAssets = false,
            bool useCapturedWindowManager = false, bool requireFocus = true)
        {
            var gameplay = plugin.Gameplay;
            var session = plugin.Session;
            var player = gameplay?.Player;
            var visualHands = player?.Get_VisualHands;
            var gameAssets = useCapturedGameAssets ? capturedGameAssets : UnityEngine.Object.FindObjectOfType<GameAssets>();
            if (gameplay == null || player == null || visualHands == null || gameAssets?.inputs == null || gameAssets.vehicleInputs == null)
                return false;

            bool hostScreenException = playbackUiAllowed && plugin.PauseMenuOpen && plugin.NativeHostScreenOpen;
            bool uiBlocked = plugin.SpectatorUiBlocked || NativeModalOpen(useCapturedWindowManager) || MonMPPlugin.CleanFrame ||
                plugin.PauseMenuOpen && !hostScreenException || gameplay.Get_isGamePaused && !hostScreenException;
            bool heldItemOrTool = player.Get_HoldingItem != null || player.Get_ActiveTool != null || player.IsHoldingAnObject;
            PlayerHands hands = CurrentHands(visualHands);
            if (visualHands.Get_IsMapOpen) hands |= PlayerHands.Map;
            if (visualHands.Get_isGameConsoleOpen) hands |= PlayerHands.Console;
            bool sorry = plugin.LocalSorryActive;
            bool flashlight = FlashlightField?.GetValue(visualHands) is true;
            byte state = (byte)gameplay.Get_PlayerState;
            var rest = gameplay.Get_IsDead ? PlayerRest.Down : gameplay.Get_IsSleeping ? PlayerRest.Sleeping : PlayerRest.None;
            bool canStart = SpectatorAdmissionPolicy.CanStart(session?.IsReady == true,
                plugin.World?.Channel.FullySynchronized == true, state, heldItemOrTool, hands, rest, sorry,
                flashlight, gameplay.Get_IsDead, gameplay.Get_IsSleeping, uiBlocked,
                plugin.World?.SaveGate.Active == true, plugin.GuestWorld?.HoldPresentation == true,
                player.mainCamera != null && gameplay.PlayerWalking != null && (!requireFocus || Application.isFocused));
            if (!canStart) return false;
            return !requireNativeInput || gameAssets.inputs.Player.enabled;
        }

        private static PlayerHands CurrentHands(PlayerVisualHands visualHands)
        {
            if (HandsAnimatorField?.GetValue(visualHands) is not Animator animator || !animator.isActiveAndEnabled)
                return PlayerHands.None;
            var hands = PlayerHands.None;
            if (animator.GetBool("Map")) hands |= PlayerHands.Map;
            if (animator.GetBool("PortableConsole")) hands |= PlayerHands.Console;
            if (animator.GetBool("JointInHand")) hands |= PlayerHands.Joint;
            if (animator.GetBool("SmokingJoint")) hands |= PlayerHands.Smoking;
            return hands;
        }

        private bool NativeInputReady()
        {
            var gameAssets = UnityEngine.Object.FindObjectOfType<GameAssets>();
            return gameAssets?.inputs?.Player.enabled == true && Cursor.lockState == CursorLockMode.Locked && Application.isFocused;
        }

        private bool WaitingForTransientStartGate() => plugin.PauseMenuOpen || plugin.SpectatorUiBlocked ||
            plugin.Gameplay?.Get_isGamePaused == true || NativeModalOpen() || MonMPPlugin.CleanFrame ||
            plugin.World?.SaveGate.Active == true || plugin.GuestWorld?.HoldPresentation == true || !Application.isFocused;

        private bool NativeModalOpen(bool useCapturedManager = false)
        {
            if (!useCapturedManager) return UnityEngine.Object.FindObjectOfType<UI_GenericWindowManager>()?.Get_isOpen == true;
            if (capturedWindowManager != null) return capturedWindowManager.Get_isOpen;
            if (!hasCapturedWindowManager && Time.unscaledTime < nextWindowManagerLookup) return false;
            // A captured manager was destroyed or no manager existed at admission. Reacquire
            // now, then limit further scene searches to once per second while it remains absent.
            nextWindowManagerLookup = Time.unscaledTime + 1f;
            capturedWindowManager = UnityEngine.Object.FindObjectOfType<UI_GenericWindowManager>();
            hasCapturedWindowManager = capturedWindowManager != null;
            return capturedWindowManager?.Get_isOpen == true;
        }

        private bool TargetAvailable(uint peer) => peer != plugin.Session?.LocalPeerId &&
            plugin.Session?.IsReady == true && plugin.Session.Peers.ContainsKey(peer) && plugin.RemoteAvatar(peer) != null &&
            plugin.RemoteAvatarHeadPosition(peer).HasValue;

        private uint? NextLivingTarget() => plugin.Session?.Peers.Keys
            .Where(TargetAvailable).OrderBy(id => id).Select(id => (uint?)id).FirstOrDefault();

        private void CycleTarget()
        {
            var peers = plugin.Session?.Peers.Keys.Where(id => id != plugin.Session.LocalPeerId && TargetAvailable(id)).OrderBy(id => id).ToArray();
            if (peers == null || peers.Length == 0) { if (forced) target = null; else End(waitForEscape: false); return; }
            int current = Array.IndexOf(peers, target ?? 0);
            target = peers[(current + 1 + peers.Length) % peers.Length];
        }

        private void ReadOrbitInput()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;
            Vector2 delta = mouse.delta.ReadValue();
            yaw += delta.x * 0.12f;
            pitch = Mathf.Clamp(pitch - delta.y * 0.12f, -20f, 65f);
            float wheel = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(wheel) > 0.01f) distance = Mathf.Clamp(distance - wheel * 0.004f, 1.5f, 10f);
        }

        private void PlaceCamera()
        {
            if (!active || camera == null || target == null) return;
            // Never write the eye while a VR provider owns the local view or it is not detached.
            if (!cameraDetached || plugin.ExternalViewOwned) return;
            if (!SuggestedView(out var position, out var rotation, out _)) return;
            camera.transform.SetPositionAndRotation(position, rotation);
        }

        private void End(bool waitForEscape)
        {
            if (!active && !cameraDetached && !restorePending) return;
            active = false; target = null;
            hint.Hide();
            waitForEscapeRelease = waitForEscape;
            RestoreCamera();
            restorePending = restoreActions.Count > 0 || ownsCursor;
            queuedTarget = null;
            TryRestorePending();
        }

        private void RestoreCamera()
        {
            if (!cameraDetached) return;
            if (CameraOwnerIsCurrent() && camera != null)
            {
                camera.transform.SetParent(originalParent, false);
                camera.transform.localPosition = originalLocalPosition;
                camera.transform.localRotation = originalLocalRotation;
                camera.transform.localScale = originalLocalScale;
                // A detached camera may have been adjusted by Unity while spectator input was held.
                if (originalParent == null) camera.transform.SetPositionAndRotation(originalWorldPosition, originalWorldRotation);
            }
            cameraDetached = false;
        }

        private void TryRestorePending()
        {
            if (!restorePending) return;
            if (!CameraOwnerIsCurrent() || !InputOwnerIsCurrent()) { ClearOwner(); return; }
            if (!CanRestoreNativeInput() || waitForEscapeRelease && Keyboard.current?.escapeKey.isPressed == true) return;
            var failed = new List<InputAction>();
            foreach (var action in restoreActions)
            {
                if (action == null || action.enabled) continue;
                try { action.Enable(); }
                catch (Exception ex)
                {
                    failed.Add(action);
                    Debug.LogWarning("MonMP could not restore a native input action yet: " + ex.Message);
                }
            }
            if (failed.Count > 0)
            {
                restoreActions.Clear(); restoreActions.AddRange(failed);
                return;
            }
            if (ownsCursor)
            {
                Cursor.lockState = originalCursorLock;
                Cursor.visible = originalCursorVisible;
            }
            restorePending = false; waitForEscapeRelease = false;
            ClearOwner();
        }

        private bool CanRestoreNativeInput()
        {
            if (!Application.isFocused) return false;
            var gameplay = plugin.Gameplay;
            var player = gameplay?.Player;
            return gameplay != null && player != null && ReferenceEquals(player, capturedPlayer) &&
                CapturedInputsAreCurrent() &&
                gameplay.Get_PlayerState == PlayerState.Walking && gameplay.PlayerWalking != null &&
                !gameplay.Get_IsDead && !gameplay.Get_IsSleeping && !gameplay.Get_isGamePaused && !plugin.PauseMenuOpen &&
                !plugin.SpectatorUiBlocked && !NativeModalOpen(useCapturedManager: true) && !MonMPPlugin.CleanFrame &&
                plugin.World?.SaveGate.Active != true && plugin.GuestWorld?.HoldPresentation != true && Application.isFocused;
        }

        private bool CameraOwnerIsCurrent()
        {
            var scene = SceneManager.GetActiveScene();
            return capturedGameplay != null && plugin.Gameplay == capturedGameplay &&
                capturedGameplay.Player != null && ReferenceEquals(capturedPlayer, capturedGameplay.Player) &&
                camera != null && capturedGameplay.Player.mainCamera == camera &&
                scene.IsValid() && scene.isLoaded && scene.handle == sceneHandle;
        }

        private bool InputOwnerIsCurrent()
        {
            if (!CameraOwnerIsCurrent()) return false;
            var scene = SceneManager.GetActiveScene();
            // This object was captured when the lease began. Verifying its identity and input
            // asset identities avoids repeated scene-wide FindObjectOfType calls while spectating.
            return CapturedInputsAreCurrent() &&
                scene.IsValid() && scene.isLoaded && scene.handle == sceneHandle;
        }

        private bool CapturedInputsAreCurrent()
        {
            var gameAssets = capturedGameAssets;
            return gameAssets != null && GameAssets.i == gameAssets && gameAssets.inputs != null && gameAssets.vehicleInputs != null &&
                gameAssets.inputs.asset == capturedPlayerActions && gameAssets.vehicleInputs.asset == capturedVehicleActions;
        }

        private void RestoreLeasedCursor()
        {
            if (!ownsCursor) return;
            var wantedLock = forced ? CursorLockMode.Locked : originalCursorLock;
            bool wantedVisible = !forced && originalCursorVisible;
            if (Cursor.lockState != wantedLock) Cursor.lockState = wantedLock;
            if (Cursor.visible != wantedVisible) Cursor.visible = wantedVisible;
        }

        private void ClearOwner()
        {
            restoreActions.Clear(); capturedAssets.Clear();
            capturedGameplay = null; capturedGameAssets = null; capturedPlayerActions = null; capturedVehicleActions = null;
            capturedWindowManager = null; nextWindowManagerLookup = 0f; hasCapturedWindowManager = false;
            capturedPlayer = null; camera = null; originalParent = null;
            sceneHandle = -1; ownsCursor = false; restorePending = false; waitForEscapeRelease = false;
        }

        internal void Reset()
        {
            forced = false;
            queuedTarget = null;
            if (active || cameraDetached) End(waitForEscape: false);
            else TryRestorePending();
            hint.Destroy();
            Status = "";
        }
    }
}
