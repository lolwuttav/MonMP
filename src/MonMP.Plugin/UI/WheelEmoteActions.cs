using System;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Input;
using MonMP.Core.Protocol;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace MonMP.Plugin.UI
{
    // Executes one emote wheel selection on the local player. The three cosmetic gestures enter
    // the replicated pose queue and pose the existing first-person arm while it is free; Sorry,
    // Spit and Slur call the game's own PlayerVisualHands
    // handlers so native voice, NPC interaction, cooldown and delayed spit behavior stay intact.
    internal sealed class WheelEmoteActions : IDisposable
    {
        private const float SorryHoldSeconds = 1.4f;
        private static readonly MethodInfo? SorryDown = AccessTools.Method(typeof(PlayerVisualHands), "SorryDown", Type.EmptyTypes);
        private static readonly MethodInfo? SorryUp = AccessTools.Method(typeof(PlayerVisualHands), "SorryUp", Type.EmptyTypes);
        private static readonly MethodInfo? SpitDown = AccessTools.Method(typeof(PlayerVisualHands), "SpitDown", Type.EmptyTypes);
        private static readonly MethodInfo? SlurDown = AccessTools.Method(typeof(PlayerVisualHands), "SlurDown", Type.EmptyTypes);
        private static readonly FieldInfo? AnimatorField = AccessTools.Field(typeof(PlayerVisualHands), "animator");
        private static readonly FieldInfo? InputsField = AccessTools.Field(typeof(PlayerVisualHands), "inputs");
        private static int nativeHandlerInvocationDepth;

        private readonly MonMPPlugin plugin;
        private readonly FirstPersonEmoteGesture firstPerson = new FirstPersonEmoteGesture();
        private Gameplay? gestureGameplay;
        private PlayerVisualHands? gestureHands;
        private Camera? gestureCamera;
        private PlayerState gestureState;
        private int gestureSceneHandle;
        private PlayerVisualHands? sorryHands;
        private Gameplay? sorryGameplay;
        private int sorrySceneHandle;
        private float sorryReleaseAt;
        private bool ownsSorry, disposed;

        internal static bool InvokingNativeHandler => nativeHandlerInvocationDepth > 0;

        internal static bool PhysicalSorryTControlActive(PlayerVisualHands hands)
        {
            try
            {
                var inputs = InputsField?.GetValue(hands) as PlayerInputActions;
                var control = inputs?.Player.Sorry.activeControl;
                // activeControl.path is a runtime path (for example /Keyboard/t or
                // /Keyboard1/t), while <Keyboard>/t is the binding-path pattern.
                return control != null && IsKeyboardTPath(control.path);
            }
            catch { return false; }
        }

        private static bool IsKeyboardTPath(string path)
        {
            if (string.IsNullOrEmpty(path) || path[0] != '/') return false;
            int deviceEnd = path.IndexOf('/', 1);
            if (deviceEnd <= 1 || !string.Equals(path.Substring(deviceEnd + 1), "t", StringComparison.OrdinalIgnoreCase))
                return false;
            string device = path.Substring(1, deviceEnd - 1);
            if (!device.StartsWith("Keyboard", StringComparison.OrdinalIgnoreCase)) return false;
            for (int i = "Keyboard".Length; i < device.Length; i++)
                if (!char.IsDigit(device[i])) return false;
            return true;
        }

        internal WheelEmoteActions(MonMPPlugin plugin) => this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

        internal bool TryPerform(EmoteWheelSlot slot)
        {
            if (disposed || !CanPerform(slot, out var gameplay, out var hands))
            {
                CancelOwnedSorry();
                CancelFirstPersonGesture();
                return false;
            }

            // An active hold has one owner until Tick, Reset or Dispose releases it. Do not let a
            // second wheel action steal the same first-person hand state.
            if (ownsSorry)
            {
                if (!OwnsCurrentHands(gameplay, hands)) CancelOwnedSorry();
                else return false;
            }

            switch (slot)
            {
                case EmoteWheelSlot.Wave:
                    if (GetAnimator(hands)?.GetBool("Sorry") == true) return false;
                    if (!Patches.LocalEmotes.RecordGesture(PlayerEmoteKind.Wave)) return false;
                    BeginFirstPersonGesture(gameplay, hands, PlayerEmoteKind.Wave);
                    return true;
                case EmoteWheelSlot.Point:
                    if (GetAnimator(hands)?.GetBool("Sorry") == true) return false;
                    if (!Patches.LocalEmotes.RecordGesture(PlayerEmoteKind.Point)) return false;
                    BeginFirstPersonGesture(gameplay, hands, PlayerEmoteKind.Point);
                    return true;
                case EmoteWheelSlot.ThumbsUp:
                    if (GetAnimator(hands)?.GetBool("Sorry") == true) return false;
                    if (!Patches.LocalEmotes.RecordGesture(PlayerEmoteKind.ThumbsUp)) return false;
                    BeginFirstPersonGesture(gameplay, hands, PlayerEmoteKind.ThumbsUp);
                    return true;
                case EmoteWheelSlot.Sorry:
                    CancelFirstPersonGesture();
                    return BeginSorry(gameplay, hands);
                case EmoteWheelSlot.Spit:
                    CancelFirstPersonGesture();
                    // SpitDown owns its native 5 s cooldown and schedules SpitSpawn after 1.85 s.
                    // Its sound patch records exactly one successful spit; never enqueue one here.
                    return Invoke(SpitDown, hands);
                case EmoteWheelSlot.Slur:
                    CancelFirstPersonGesture();
                    // SlurDown plays the native Sacre voice line; the voice patch records it once.
                    return Invoke(SlurDown, hands);
                default:
                    return false;
            }
        }

        internal void Tick()
        {
            // Update runs before native Animator evaluation; idle clips may leave finger joints
            // unkeyed, so remove last frame's overlay even when an animation will overwrite arms.
            firstPerson.RestoreBeforeAnimation();
            if (firstPerson.IsActive && !CanContinueFirstPersonGesture()) CancelFirstPersonGesture();
            Patches.SocialInputPatches.Tick(plugin);
            if (!ownsSorry) return;
            if (disposed || !CanPerform(EmoteWheelSlot.Sorry, out var gameplay, out var hands) || !OwnsCurrentHands(gameplay, hands))
            {
                CancelOwnedSorry();
                return;
            }

            // Native canceled/Sleep_Start can lower the same hand before our timer expires.
            // Drop the wheel claim immediately so it cannot block a later selected gesture.
            if (GetAnimator(hands)?.GetBool("Sorry") != true)
            {
                ClearOwnedSorry();
                return;
            }

            if (Time.unscaledTime < sorryReleaseAt) return;
            ReleaseOwnedSorry(allowNativeTransfer: true);
        }

        internal void LateTick()
        {
            if (!firstPerson.IsActive) return;
            if (!CanContinueFirstPersonGesture()) { CancelFirstPersonGesture(); return; }
            firstPerson.Apply();
            if (!firstPerson.IsActive) CancelFirstPersonGesture();
        }

        internal void Reset()
        {
            CancelOwnedSorry();
            CancelFirstPersonGesture();
        }

        public void Dispose()
        {
            if (disposed) return;
            CancelOwnedSorry();
            CancelFirstPersonGesture();
            disposed = true;
        }

        private void BeginFirstPersonGesture(Gameplay gameplay, PlayerVisualHands hands, PlayerEmoteKind kind)
        {
            CancelFirstPersonGesture();
            if (!CanPoseFirstPerson(gameplay, hands)) return;
            var camera = FirstPersonCamera(gameplay);
            if (camera == null || !camera.isActiveAndEnabled || !firstPerson.Begin(hands, camera, kind)) return;
            gestureGameplay = gameplay;
            gestureHands = hands;
            gestureCamera = camera;
            gestureState = gameplay.Get_PlayerState;
            gestureSceneHandle = SceneManager.GetActiveScene().handle;
        }

        private bool CanContinueFirstPersonGesture()
        {
            if (disposed || !CanPerform(EmoteWheelSlot.Wave, out var gameplay, out var hands) ||
                gameplay != gestureGameplay || hands != gestureHands || gameplay.Get_PlayerState != gestureState ||
                SceneManager.GetActiveScene().handle != gestureSceneHandle || !CanPoseFirstPerson(gameplay, hands)) return false;
            var camera = FirstPersonCamera(gameplay);
            return camera != null && camera == gestureCamera && camera.isActiveAndEnabled;
        }

        private static Camera? FirstPersonCamera(Gameplay gameplay) =>
            gameplay.PlayerWalking?.playerCameraController?.cam3DInFrontOfEverything ?? gameplay.Player.mainCamera;

        private static bool CanPoseFirstPerson(Gameplay gameplay, PlayerVisualHands hands)
        {
            // Cosmetic replication is also allowed while carrying and in the creeper/wheelbarrow.
            // Their native first-person poses keep ownership. Do not hide, disable or replace any
            // tool, held item, light, joint, map, console or interaction to make a gesture fit.
            if (gameplay.Player.Get_HoldingItem != null || gameplay.Player.Get_ActiveTool != null ||
                gameplay.Get_PlayerState == PlayerState.InCreeper || gameplay.Get_PlayerState == PlayerState.InWheelbarrow)
                return false;
            try
            {
                var animator = GetAnimator(hands);
                return hands.Get_CanSmoke && animator != null && animator.isActiveAndEnabled &&
                    !animator.GetBool("Sorry") && !animator.GetBool("JointInHand") && !animator.GetBool("SmokingJoint") &&
                    !animator.GetBool("Map") && !animator.GetBool("PortableConsole") && !animator.GetBool("FlashLight") &&
                    !animator.GetBool("Interaction") && !animator.GetBool("PetLeo");
            }
            catch { return false; }
        }

        private void CancelFirstPersonGesture()
        {
            firstPerson.Cancel();
            gestureGameplay = null; gestureHands = null; gestureCamera = null;
            gestureSceneHandle = 0;
        }

        private bool BeginSorry(Gameplay gameplay, PlayerVisualHands hands)
        {
            var animator = GetAnimator(hands);
            if (SorryDown == null || SorryUp == null || animator == null || !animator.isActiveAndEnabled ||
                animator.GetBool("Sorry") || PhysicalSorryHeld(hands)) return false;

            // Claim before invoking: SorryDown sets the animator before playing voice and may call
            // MouseDown on a naturally targeted Actor. If the native handler throws partway through,
            // release its bool on this same hands object before returning.
            ownsSorry = true;
            sorryHands = hands;
            sorryGameplay = gameplay;
            sorrySceneHandle = SceneManager.GetActiveScene().handle;
            sorryReleaseAt = Time.unscaledTime + SorryHoldSeconds;
            if (Invoke(SorryDown, hands)) return true;
            ReleaseOwnedSorry();
            return false;
        }

        private bool CanPerform(EmoteWheelSlot slot, out Gameplay gameplay, out PlayerVisualHands hands)
        {
            gameplay = null!;
            hands = null!;
            var session = plugin.Session;
            var world = plugin.World;
            if (session?.IsRunning != true || !session.IsReady || world == null ||
                world.SaveGate.Active || plugin.GuestWorld?.HoldPresentation == true ||
                NativeSorryInputBlocked(plugin))
                return false;

            try { if (!world.Channel.FullySynchronized) return false; }
            catch (InvalidOperationException) { return false; }

            gameplay = plugin.Gameplay!;
            if (gameplay == null || gameplay.Get_isGamePaused || gameplay.Get_IsDead || gameplay.Get_IsSleeping ||
                gameplay.Player == null || !EmoteGameplayPolicy.CanPerformSelectedAction(slot, gameplay.Get_PlayerState,
                    gameplay.Player.Get_HoldingItem == null && gameplay.Player.Get_ActiveTool == null))
                return false;

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return false;
            hands = gameplay.Player.Get_VisualHands;
            return hands != null && hands.isActiveAndEnabled && hands.gameObject.scene.handle == scene.handle &&
                !hands.Get_IsMapOpen && !hands.Get_isGameConsoleOpen;
        }

        private static bool TypingInField()
        {
            var events = EventSystem.current;
            if (events == null) return false;
            var selected = events.currentSelectedGameObject;
            return selected != null && selected.GetComponent<TMP_InputField>() != null;
        }

        internal static bool NativeSorryInputBlocked(MonMPPlugin plugin) =>
            plugin.SocialUiBlocked || plugin.ChatBlocksInput || MonMPPlugin.CleanFrame ||
            !Application.isFocused || Cursor.lockState != CursorLockMode.Locked ||
            UnityEngine.Object.FindObjectOfType<UI_GenericWindowManager>()?.Get_isOpen == true || TypingInField();

        private bool OwnsCurrentHands(Gameplay gameplay, PlayerVisualHands hands)
        {
            if (!ownsSorry || sorryHands == null || sorryGameplay == null || hands == null ||
                !sorryHands.isActiveAndEnabled || gameplay != sorryGameplay || hands != sorryHands)
                return false;
            var scene = SceneManager.GetActiveScene();
            return scene.IsValid() && scene.isLoaded && scene.handle == sorrySceneHandle &&
                sorryHands.gameObject.scene.handle == sorrySceneHandle;
        }

        private void CancelOwnedSorry() => ReleaseOwnedSorry();

        private void ReleaseOwnedSorry(bool allowNativeTransfer = false)
        {
            if (!ownsSorry) return;
            var hands = sorryHands;
            ClearOwnedSorry();

            // An action value alone does not prove ownership: T still reads held when its native
            // started callback was suppressed for the wheel. Only an accepted native press can
            // take over at normal expiry. Cancellation always releases the wheel's captured hands.
            if (hands != null && !(allowNativeTransfer && Patches.SocialInputPatches.NativeSorryHeld(hands)))
                ReleaseNativeSorry(hands);
        }

        private void ClearOwnedSorry()
        {
            ownsSorry = false;
            sorryHands = null;
            sorryGameplay = null;
            sorrySceneHandle = 0;
            sorryReleaseAt = 0;
        }

        private static Animator? GetAnimator(PlayerVisualHands hands)
        {
            try { return AnimatorField?.GetValue(hands) as Animator; }
            catch { return null; }
        }

        internal static bool PhysicalSorryHeld(PlayerVisualHands hands)
        {
            try
            {
                var inputs = InputsField?.GetValue(hands) as PlayerInputActions;
                var action = inputs?.Player.Sorry;
                if (action == null || !action.enabled || action.ReadValue<float>() <= 0f) return false;
                // The raw button also catches a released key whose canceled callback/action value
                // was lost during a focus, input-map or controller transition.
                return !(action.activeControl is UnityEngine.InputSystem.Controls.ButtonControl button) || button.isPressed;
            }
            catch { return false; }
        }

        internal static void ReleaseNativeSorry(PlayerVisualHands hands)
        {
            if (hands == null) return;
            Patches.SocialInputPatches.ForgetNativeSorry(hands);
            nativeHandlerInvocationDepth++;
            try
            {
                // SorryUp only clears the animator bool. A disabled but surviving hands component
                // still needs release before it can be enabled again after a menu/seat transition.
                if (SorryUp != null) SorryUp.Invoke(hands, null);
                else ClearSorryAnimator(hands);
            }
            catch { ClearSorryAnimator(hands); }
            finally { nativeHandlerInvocationDepth--; }
        }

        private static void ClearSorryAnimator(PlayerVisualHands hands)
        {
            try
            {
                var animator = GetAnimator(hands);
                if (animator != null) animator.SetBool("Sorry", false);
            }
            catch { }
        }

        private static bool Invoke(MethodInfo? method, PlayerVisualHands hands)
        {
            if (method == null || hands == null || !hands.isActiveAndEnabled) return false;
            nativeHandlerInvocationDepth++;
            try { method.Invoke(hands, null); return true; }
            catch { return false; }
            finally { nativeHandlerInvocationDepth--; }
        }
    }
}
