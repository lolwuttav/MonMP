using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Core.Input;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace MonMP.Plugin.UI
{
    internal sealed class EmoteWheel : IDisposable
    {
        private const float InnerRadius = 102f;
        private const float OuterRadius = 236f;
        private const float LabelRadius = 166f;
        internal const float WheelRadiusDenominator = 520f;
        private const float WheelSize = WheelRadiusDenominator;
        private const float SectorStep = 360f / EmoteWheelSelection.SlotCount;

        private static readonly string[] Labels = { "Wave", "Point", "Thumbs up", "Sorry", "Spit", "Slur" };

        private readonly MonMPPlugin plugin;
        private readonly Func<KeyCode> getBinding;
        private readonly Action<EmoteWheelSlot> onSelected;
        private readonly EmoteGesturePreview? preview;
        private readonly HashSet<string> loggedCancelReasons = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<ButtonControl> suppressedMouseButtons = new HashSet<ButtonControl>();
        private RectTransform? root, wheelRect, virtualPointerRect;
        private Image? virtualPointerOuter, virtualPointerInner;
        private EmoteWheelWedgeGraphic[] wedges = Array.Empty<EmoteWheelWedgeGraphic>();
        private TextMeshProUGUI[] labels = Array.Empty<TextMeshProUGUI>();
        private TextMeshProUGUI? hint;
        private bool open, awaitingRelease, lookWasEnabled, ownsLookDisable;
        private bool holdPressLatched, holdReleaseLatched;
        private bool suppressEscapeUntilRelease;
        private int cancelReasonLogCount;
        private KeyControl? openingKey;
        private KeyControl? missedOpeningPressKey;
        private KeyControl? trackedOpeningKey;
        private KeyCode openingKeyCode;
        private KeyCode trackedOpeningKeyCode;
        private bool openingPressLatched, openingReleaseLatched;
        private bool trackedOpeningKeyPressed;
        private bool pendingLookRestore, pendingLookWasEnabled;
        private KeyControl? activeKey, waitingKey;
        private KeyCode activeKeyCode;
        private Gameplay? capturedGameplay;
        private GameAssets? capturedAssets;
        private InputAction? lookAction;
        private int sceneHandle = -1, openedFrame;
        private int pendingSceneHandle = -1;
        private Gameplay? pendingGameplay;
        private GameAssets? pendingAssets;
        private InputAction? pendingLookAction;
        private Vector2 virtualPointer;
        private int nativeLookupScene = -1;
        private float nativeLookupRetryAt;
        private GameAssets? nativeLookupAssets;
        private UI_GenericWindowManager? nativeLookupModal;
        private EmoteWheelSlot selected, previewed;

        internal bool Open => open;

        internal EmoteWheel(MonMPPlugin plugin, Func<KeyCode> getBinding, Action<EmoteWheelSlot> onSelected,
            EmoteGesturePreview? preview = null)
        {
            this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            this.getBinding = getBinding ?? throw new ArgumentNullException(nameof(getBinding));
            this.onSelected = onSelected ?? throw new ArgumentNullException(nameof(onSelected));
            this.preview = preview;
            InputSystem.onBeforeUpdate += BeforeInput;
            InputSystem.onEvent += OnInput;
        }

        private bool Eligible() => OpeningGateFailure() == null;

        private string? OpeningGateFailure()
        {
            RefreshNativeLookups();
            var gameplay = plugin.Gameplay;
            var player = gameplay?.Player;
            var hands = player?.Get_VisualHands;
            return OpenGateFailure(
                plugin.Session?.IsRunning == true,
                plugin.Session?.IsReady == true,
                plugin.World?.Channel.FullySynchronized == true,
                gameplay != null && player != null,
                gameplay != null && player != null && EmoteGameplayPolicy.IsWheelDisplayState(gameplay.Get_PlayerState),
                gameplay?.Get_isGamePaused == true,
                gameplay?.Get_IsDead == true,
                gameplay?.Get_IsSleeping == true,
                plugin.SocialUiBlocked || plugin.ChatBlocksInput,
                IsNativeModalOpen(hands),
                TypingInField(),
                MonMPPlugin.CleanFrame,
                CurrentSceneLoaded(),
                Application.isFocused,
                Cursor.lockState == CursorLockMode.Locked,
                nativeLookupAssets != null && nativeLookupAssets.inputs != null,
                nativeLookupAssets != null && nativeLookupAssets.inputs != null && nativeLookupAssets.inputs.Player.enabled);
        }

        private bool IsNativeModalOpen(PlayerVisualHands? hands) =>
            nativeLookupModal != null && nativeLookupModal.Get_isOpen ||
            hands == null || hands.Get_IsMapOpen || hands.Get_isGameConsoleOpen || plugin.LocalSorryActive ||
            plugin.GuestWorld?.HoldPresentation == true || plugin.World?.SaveGate.Active == true;

        private static bool CurrentSceneLoaded()
        {
            var scene = SceneManager.GetActiveScene();
            return scene.IsValid() && scene.isLoaded;
        }

        private static string? OpenGateFailure(bool sessionRunning, bool sessionReady, bool worldSynchronized,
            bool gameplayAvailable, bool emotePlayerStateSupported, bool paused, bool dead, bool sleeping, bool pluginUiBlocked,
            bool nativeModalOpen, bool textEntryActive, bool cleanFrame, bool sceneLoaded, bool applicationFocused,
            bool cursorLocked, bool nativeAssetsAvailable, bool nativePlayerActionsEnabled)
        {
            if (!sessionRunning) return "session-not-running";
            if (!sessionReady) return "session-not-ready";
            if (!worldSynchronized) return "world-not-synchronized";
            if (!gameplayAvailable) return "gameplay-unavailable";
            if (!emotePlayerStateSupported) return "player-state-unsupported";
            if (paused) return "game-paused";
            if (dead) return "player-dead";
            if (sleeping) return "player-sleeping";
            if (pluginUiBlocked) return "plugin-ui-blocked";
            if (nativeModalOpen) return "native-modal-open";
            if (textEntryActive) return "text-entry-active";
            if (cleanFrame) return "clean-frame-active";
            if (!sceneLoaded) return "scene-not-loaded";
            if (!applicationFocused) return "application-unfocused";
            if (!cursorLocked) return "cursor-not-locked";
            if (!nativeAssetsAvailable) return "native-input-assets-unavailable";
            if (!nativePlayerActionsEnabled) return "native-player-map-disabled";
            return null;
        }

        private static bool TypingInField()
        {
            var events = EventSystem.current;
            if (events == null) return false;
            var selected = events.currentSelectedGameObject;
            return selected != null && selected.GetComponent<TMP_InputField>() != null;
        }

        private void RefreshNativeLookups()
        {
            // These searches walk every component of the type in a very large scene, and an open
            // wheel asks several times per frame: both found objects stay valid for the scene, and
            // a missing one is searched for again at most once a second (or on a new key press).
            int currentScene = SceneManager.GetActiveScene().handle;
            if (nativeLookupScene == currentScene)
            {
                if (nativeLookupAssets != null && nativeLookupAssets.isActiveAndEnabled &&
                    nativeLookupModal != null && nativeLookupModal.isActiveAndEnabled) return;
                if (Time.unscaledTime < nativeLookupRetryAt) return;
            }
            nativeLookupRetryAt = Time.unscaledTime + 1f;
            nativeLookupScene = currentScene;
            nativeLookupAssets = UnityEngine.Object.FindObjectOfType<GameAssets>();
            nativeLookupModal = UnityEngine.Object.FindObjectOfType<UI_GenericWindowManager>();
        }

        internal void Tick()
        {
            if (open)
            {
                if (!Eligible()) { Close(cancelled: true, "eligibility-lost"); return; }
                if (!CanRestoreOwners()) { Close(cancelled: true, "owner-replaced"); return; }
                if (getBinding() != activeKeyCode) { Close(cancelled: true, "binding-changed"); return; }
                if (activeKey == null || !activeKey.device.added) { Close(cancelled: true, "hold-device-removed"); return; }

                EnforceWheelCapture();
                UpdateHover();

                if (activeKey != null && EmoteWheelSelection.IsHoldReleased(
                    activeKey.wasReleasedThisFrame, activeKey.isPressed, holdReleaseLatched))
                {
                    EmoteWheelSlot released = EmoteWheelSelection.Release(open, Time.frameCount <= openedFrame,
                        Eligible() && SameScene(), selected);
                    if (released == EmoteWheelSlot.None)
                        LogWheelEvent(Time.frameCount <= openedFrame ? "key-released-in-opening-frame" : "key-released-outside-wedge");
                    Close(cancelled: false);
                    if (released != EmoteWheelSlot.None)
                    {
                        try { onSelected(released); }
                        catch (Exception ex) { Debug.LogException(ex); }
                    }
                    return;
                }
                // Track an unpressed sample for diagnosis, but only the matching key-up transition
                // (or this frame's release flag) ends the hold. Focus/device/owner guards remain
                // fail-closed when native input can no longer be trusted.
                if (activeKey?.isPressed == false && !holdReleaseLatched && Time.frameCount > openedFrame)
                    LogWheelEvent("hold-control-unpressed-without-release-edge");
                TickPreview();
                return;
            }

            TryRestorePendingOwners();
            if (awaitingRelease)
            {
                if (waitingKey?.isPressed == true) return;
                awaitingRelease = false; waitingKey = null;
            }
            KeyCode binding = getBinding();
            KeyControl? key = ResolveKey(binding);
            if (!TryConsumeOpeningPress(binding, key, out KeyControl? pressedKey, out string? inputRejection))
            {
                if (inputRejection != null) LogWheelEvent("open-rejected:" + inputRejection, binding);
                else if (OpeningPressEdgeMissing(key, binding, trackedOpeningKey, trackedOpeningKeyCode,
                             trackedOpeningKeyPressed))
                {
                    LogWheelEvent("open-rejected:held-key-down-edge-not-observed", binding, key);
                    ShouldReportUnlatchedHold(key, ref missedOpeningPressKey);
                }
                else if (key != null)
                {
                    ShouldReportUnlatchedHold(key, ref missedOpeningPressKey);
                }
                return;
            }
            key = pressedKey;
            missedOpeningPressKey = null;

            nativeLookupRetryAt = 0f;
            string? gateFailure = OpeningGateFailure();
            if (gateFailure != null)
            {
                LogWheelEvent("open-rejected:" + gateFailure, binding, key);
                return;
            }
            string? bindingConflict = NativeBindingConflictReason(key!);
            if (bindingConflict != null)
            {
                LogWheelEvent("open-rejected:" + bindingConflict, binding, key);
                return;
            }
            string? beginFailure = Begin(key!, binding);
            if (beginFailure != null)
            {
                LogWheelEvent("open-rejected:" + beginFailure, binding, key);
            }
        }

        private static bool ShouldReportUnlatchedHold(KeyControl? key, ref KeyControl? previouslyReported)
        {
            if (key == null) return false;
            if (!key.isPressed)
            {
                previouslyReported = null;
                return false;
            }
            if (previouslyReported == key) return false;
            previouslyReported = key;
            return true;
        }

        private static bool OpeningPressEdgeMissing(KeyControl? key, KeyCode binding,
            KeyControl? trackedKey, KeyCode trackedBinding, bool trackedPressed)
        {
            if (key?.isPressed != true) return false;
            return trackedKey != key || trackedBinding != binding || !trackedPressed;
        }

        private string? Begin(KeyControl key, KeyCode binding)
        {
            try
            {
                EnsureUI();
                var gameplay = plugin.Gameplay;
                RefreshNativeLookups();
                var assets = nativeLookupAssets;
                if (gameplay == null || assets == null || wheelRect == null) return "open-owner-or-ui-unavailable";
                if (Cursor.lockState != CursorLockMode.Locked) return "cursor-not-locked-at-begin";
                if (!Application.isFocused) return "application-unfocused-at-begin";
                if (NativeMouseButtonHeld()) return "mouse-button-held-at-begin";

                capturedGameplay = gameplay;
                capturedAssets = assets;
                var scene = SceneManager.GetActiveScene();
                sceneHandle = scene.handle;
                virtualPointer = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                activeKey = key; activeKeyCode = binding; openedFrame = Time.frameCount;
                holdPressLatched = true; holdReleaseLatched = false;
                selected = previewed = EmoteWheelSlot.None;
                open = true;
                lookAction = assets.inputs.Player.LookArround;
                lookWasEnabled = lookAction.enabled;
                ownsLookDisable = lookWasEnabled;
                if (ownsLookDisable) lookAction.Disable();
                root!.gameObject.SetActive(true);
                EnforceWheelCapture();
                UpdateHover();
                TickPreview();
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Close(cancelled: true, "open-exception");
                return "open-exception-" + ex.GetType().Name;
            }
        }

        internal void ObserveOpeningKeyEvent(Keyboard keyboard, InputEventPtr evt)
        {
            if (open) return;
            KeyCode binding = getBinding();
            if (binding == KeyCode.None) return;
            KeyControl? key = ResolveKey(keyboard, binding);
            if (key == null || !key.ReadValueFromEvent(evt, out float value)) return;
            UpdateOpeningPressLatch(key, binding, value);
        }

        private void UpdateOpeningPressLatch(KeyControl key, KeyCode binding, float value)
        {
            if (trackedOpeningKey != key || trackedOpeningKeyCode != binding)
            {
                trackedOpeningKey = key;
                trackedOpeningKeyCode = binding;
                // InputSystem.onEvent runs before this event changes device state. Seed from the prior
                // state so a held key echoed by later full Keyboard StateEvents is not a fresh press.
                trackedOpeningKeyPressed = key.isPressed;
            }
            if (value > 0.5f)
            {
                if (!trackedOpeningKeyPressed)
                {
                    openingKey = key;
                    openingKeyCode = binding;
                    openingPressLatched = true;
                    openingReleaseLatched = false;
                }
                trackedOpeningKeyPressed = true;
            }
            else
            {
                bool wasTrackedPressed = trackedOpeningKeyPressed;
                trackedOpeningKeyPressed = false;
                if (wasTrackedPressed && openingPressLatched && openingKey == key && openingKeyCode == binding)
                {
                    openingPressLatched = false;
                    openingReleaseLatched = true;
                }
            }
        }

        internal bool TryConsumeOpeningPress(KeyCode binding, KeyControl? resolvedKey,
            out KeyControl? key, out string? rejection)
        {
            key = null;
            rejection = null;
            if (openingReleaseLatched)
            {
                ClearOpeningPressLatch();
                rejection = "key-released-before-tick";
                return false;
            }
            if (openingPressLatched)
            {
                KeyControl? pending = openingKey;
                KeyCode pendingBinding = openingKeyCode;
                ClearOpeningPressLatch();
                if (pendingBinding != binding)
                {
                    rejection = "binding-changed-before-tick";
                    return false;
                }
                if (pending == null || !pending.device.added)
                {
                    rejection = "input-device-removed-before-tick";
                    return false;
                }
                if (!pending.isPressed)
                {
                    rejection = "key-not-held-at-tick";
                    return false;
                }
                key = pending;
                return true;
            }
            if (resolvedKey == null || !resolvedKey.wasPressedThisFrame) return false;
            if (!resolvedKey.device.added)
            {
                rejection = "input-device-removed-before-tick";
                return false;
            }
            if (!resolvedKey.isPressed)
            {
                rejection = "press-not-held-at-tick";
                return false;
            }
            key = resolvedKey;
            return true;
        }

        internal void ClearOpeningPressLatch()
        {
            openingKey = null;
            missedOpeningPressKey = null;
            openingKeyCode = KeyCode.None;
            openingPressLatched = openingReleaseLatched = false;
        }

        private void EnsureUI()
        {
            if (root != null) return;
            root = MenuKit.Canvas("MonMP Emote Wheel", 475);

            var backdrop = MenuKit.Rect("Dim", root).Stretch();
            var dim = backdrop.gameObject.AddComponent<Image>();
            dim.color = new Color(0, 0, 0, 0.34f); dim.raycastTarget = false;

            wheelRect = MenuKit.Rect("Wheel", root).Center(WheelSize, WheelSize);
            wedges = new EmoteWheelWedgeGraphic[EmoteWheelSelection.SlotCount];
            labels = new TextMeshProUGUI[EmoteWheelSelection.SlotCount];
            for (int i = 0; i < wedges.Length; i++)
            {
                var rect = MenuKit.Rect("Wedge " + Labels[i], wheelRect).Center(WheelSize, WheelSize);
                var wedge = rect.gameObject.AddComponent<EmoteWheelWedgeGraphic>();
                float centerAngle = 90f - SectorStep * i;
                wedge.Configure(centerAngle - SectorStep * 0.5f, centerAngle + SectorStep * 0.5f,
                    InnerRadius, OuterRadius, false);
                wedges[i] = wedge;

                var label = MenuKit.Label(wheelRect, Labels[i], 28, MenuKit.Semibold, Color.white, TextAlignmentOptions.Center);
                label.fontStyle = FontStyles.Bold;
                label.rectTransform.anchorMin = label.rectTransform.anchorMax = label.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                label.rectTransform.sizeDelta = new Vector2(150, 44);
                float radians = centerAngle * Mathf.Deg2Rad;
                label.rectTransform.anchoredPosition = new Vector2(Mathf.Cos(radians), Mathf.Sin(radians)) * LabelRadius;
                labels[i] = label;
            }

            hint = MenuKit.Label(root, "", 21, MenuKit.Semibold, new Color(1, 1, 1, 0.82f), TextAlignmentOptions.Center);
            hint.rectTransform.anchorMin = hint.rectTransform.anchorMax = hint.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            hint.rectTransform.sizeDelta = new Vector2(900, 38);
            hint.rectTransform.anchoredPosition = new Vector2(0, -275);

            virtualPointerRect = MenuKit.Rect("Virtual Pointer", root);
            virtualPointerRect.anchorMin = virtualPointerRect.anchorMax = virtualPointerRect.pivot = new Vector2(0.5f, 0.5f);
            virtualPointerRect.sizeDelta = new Vector2(18, 18);
            virtualPointerOuter = virtualPointerRect.gameObject.AddComponent<Image>();
            virtualPointerOuter.color = new Color(0.05f, 0.06f, 0.09f, 0.98f);
            virtualPointerOuter.raycastTarget = false;
            var inner = MenuKit.Rect("Pointer Center", virtualPointerRect).Center(8, 8);
            virtualPointerInner = inner.gameObject.AddComponent<Image>();
            virtualPointerInner.color = Color.white;
            virtualPointerInner.raycastTarget = false;
        }

        private void UpdateHover()
        {
            float x = virtualPointer.x, y = virtualPointer.y;
            EmoteWheelSelection.MovePointer(ref x, ref y, 0f, 0f, Screen.width, Screen.height);
            virtualPointer.Set(x, y);
            EmoteWheelSlot pointed = PointerSelection();
            bool pointedAvailable = IsSelectionAvailable(pointed);
            selected = pointedAvailable ? pointed : EmoteWheelSlot.None;
            for (int i = 0; i < wedges.Length; i++)
            {
                var slot = (EmoteWheelSlot)(i + 1);
                bool available = IsSelectionAvailable(slot);
                wedges[i].SetHighlighted(pointed == slot && available, available);
                if (i < labels.Length && labels[i] != null)
                    labels[i].color = available ? Color.white : new Color(0.65f, 0.67f, 0.72f, 0.65f);
            }

            if (hint != null)
            {
                string key = activeKeyCode.ToString();
                hint.text = pointed != EmoteWheelSlot.None && !pointedAvailable
                    ? !HandsAreFree() ? "Put away the held item or tool to emote" : Labels[(int)pointed - 1] + " is unavailable in this pose"
                    : selected == EmoteWheelSlot.None
                        ? "Hold " + key + " | release on a wedge to emote | center or outside cancels"
                        : "Release to choose " + Labels[(int)selected - 1] + " | move to another wedge to change";
            }
            UpdateVirtualPointerVisual();
        }

        private EmoteWheelSlot PointerSelection()
        {
            if (wheelRect == null ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(wheelRect, virtualPointer, null, out var local))
                return EmoteWheelSlot.None;
            return EmoteWheelSelection.Select(local.x, local.y, InnerRadius, OuterRadius);
        }

        private bool IsSelectionAvailable(EmoteWheelSlot slot)
        {
            if (slot == EmoteWheelSlot.None) return false;
            var gameplay = plugin.Gameplay;
            return gameplay != null && gameplay.Player != null &&
                EmoteGameplayPolicy.CanPerformSelectedAction(slot, gameplay.Get_PlayerState, HandsAreFree());
        }

        private bool HandsAreFree()
        {
            var player = plugin.Gameplay?.Player;
            return player != null && player.Get_HoldingItem == null && player.Get_ActiveTool == null;
        }

        private void UpdateVirtualPointer(Vector2 delta)
        {
            float x = virtualPointer.x, y = virtualPointer.y;
            EmoteWheelSelection.MovePointer(ref x, ref y, delta.x, delta.y, Screen.width, Screen.height);
            virtualPointer.Set(x, y);
            UpdateVirtualPointerVisual();
        }

        private static bool NativeMouseButtonHeld()
        {
            var mouse = Mouse.current;
            return mouse != null && mouse.allControls.OfType<ButtonControl>().Any(button => button.isPressed);
        }

        private void UpdateVirtualPointerVisual()
        {
            if (root == null || virtualPointerRect == null) return;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(root, virtualPointer, null, out var local))
                virtualPointerRect.anchoredPosition = local;
        }

        private void TickPreview()
        {
            if (preview == null) return;
            if (selected == EmoteWheelSlot.None)
            {
                if (previewed != EmoteWheelSlot.None) preview.Hide();
                previewed = EmoteWheelSlot.None;
                return;
            }
            Color shirt = Color.gray;
            try
            {
                var hands = plugin.Gameplay?.Player != null ? plugin.Gameplay.Player.Get_VisualHands : null;
                if (hands != null) shirt = ShirtColor(hands.Get_ShirtColor);
            }
            catch { }
            preview.Tick(selected, shirt, Time.unscaledDeltaTime);
            previewed = selected;
        }

        private static Color ShirtColor(int shirt)
        {
            switch (shirt)
            {
                case 1: return new Color(0.09803923f, 0.4170849f, 0.8862745f);
                case 2: return new Color(0.09803923f, 0.8862745f, 0.4617344f);
                case 3: return new Color(0.6320754f, 0.6320754f, 0.6320754f);
                case 4: return new Color(1f, 0.5571218f, 0.08176094f);
                case 5: return new Color(1f, 0.5345911f, 0.9706057f);
                case 6: return new Color(0.9756485f, 0.9811321f, 0.4319449f);
                case 7: return new Color(0.47f, 0.31f, 0.66f);
                case 8: return Color.white;
                default: return new Color(0.8862745f, 0.09803923f, 0.2392157f);
            }
        }

        internal void Draw()
        {
            if (!open || selected == EmoteWheelSlot.None || preview == null || root == null) return;
            // The avatar preview sits inside the wheel's empty center, where the pointer cancels.
            float scale = root.GetComponent<Canvas>()?.scaleFactor ?? 1f;
            float width = 116f * scale, height = 156f * scale;
            preview.Draw(new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height));
        }

        private void BeforeInput()
        {
            if (!open) { TryRestorePendingOwners(); return; }
            if (!Eligible()) { Close(cancelled: true, "eligibility-lost-before-input"); return; }
            if (!CanRestoreOwners()) { Close(cancelled: true, "owner-replaced-before-input"); return; }
            EnforceWheelCapture();
        }

        private void OnInput(InputEventPtr evt, InputDevice device)
        {
            if (!evt.IsA<StateEvent>() && !evt.IsA<DeltaStateEvent>()) return;

            if (device is Mouse eventMouse)
            {
                MaskTrackedMouseButtons(eventMouse, evt);
                if (!open) return;
            }
            else if (device is Keyboard eventKeyboard)
            {
                MaskTrackedEscape(eventKeyboard, evt);
                if (!open)
                {
                    ObserveOpeningKeyEvent(eventKeyboard, evt);
                    return;
                }
            }
            else if (!open) return;

            if (!Eligible() || !CanRestoreOwners())
            {
                Close(cancelled: true, "input-eligibility-or-owner-lost");
                return;
            }
            if (capturedAssets == null)
            {
                Close(cancelled: true, "captured-assets-lost");
                return;
            }

            var player = capturedAssets.inputs.Player;
            if (device is Mouse mouse)
            {
                if (Pressed(mouse.rightButton, evt))
                {
                    SuppressMouseButton(mouse.rightButton, evt);
                    Close(cancelled: true, "right-click-cancel");
                    return;
                }

                string? nativeMousePath = FindMouseNonMovementAction(player, mouse, evt);
                if (nativeMousePath != null)
                {
                    // Keep unrelated mouse input live, but report which native binding cancelled the hold.
                    Close(cancelled: true, "native-mouse-action:" + nativeMousePath);
                    return;
                }

                if (mouse.delta.ReadValueFromEvent(evt, out Vector2 delta))
                {
                    UpdateVirtualPointer(delta);
                    mouse.delta.WriteValueIntoEvent(Vector2.zero, evt);
                }
                // Tick resolves the hover once per frame, before it reads the release.
                return;
            }

            if (device is Keyboard keyboard)
            {
                if (Pressed(keyboard.escapeKey, evt))
                {
                    suppressEscapeUntilRelease = true;
                    keyboard.escapeKey.WriteValueIntoEvent(0f, evt);
                    Close(cancelled: true, "escape-cancel");
                    return;
                }

                if (activeKey != null && activeKey.device == keyboard &&
                    activeKey.ReadValueFromEvent(evt, out float holdValue))
                {
                    if (holdValue > 0.5f)
                    {
                        holdPressLatched = true;
                        holdReleaseLatched = false;
                    }
                    else if (holdPressLatched)
                    {
                        holdReleaseLatched = true;
                    }
                }

                foreach (var key in keyboard.allKeys)
                {
                    bool wheelOwnsNativeSorryBinding = key == activeKey && ShouldIgnoreNativeSorryBinding(key);
                    if (Pressed(key, evt) && NonMovementActionUsesControl(player, key, wheelOwnsNativeSorryBinding))
                    {
                        // Close before Unity processes an interaction or modal key.
                        Close(cancelled: true, "native-key-action:" + key.path);
                        return;
                    }
                }
                return;
            }

            if (NonMovementDeviceActionPressed(player, device, evt))
                Close(cancelled: true, "native-device-action:" + device.name);
        }

        private static bool Pressed(ButtonControl key, InputEventPtr evt) =>
            !key.isPressed && key.ReadValueFromEvent(evt, out float value) && value > 0.5f;

        private string? FindMouseNonMovementAction(PlayerInputActions.PlayerActions player, Mouse mouse, InputEventPtr evt)
        {
            foreach (var control in mouse.allControls)
                if (control is ButtonControl button && Pressed(button, evt) && NonMovementActionUsesControl(player, button))
                    return button.path;

            if (mouse.scroll.ReadValueFromEvent(evt, out Vector2 scroll) && scroll.sqrMagnitude > 0.001f &&
                NonMovementActionUsesControl(player, mouse.scroll))
                return mouse.scroll.path;
            return null;
        }

        private void SuppressMouseButton(ButtonControl button, InputEventPtr evt)
        {
            if (!button.ReadValueFromEvent(evt, out float value)) return;
            button.WriteValueIntoEvent(0f, evt);
            if (value > 0.5f) suppressedMouseButtons.Add(button);
            else suppressedMouseButtons.Remove(button);
        }

        private void MaskTrackedMouseButtons(Mouse mouse, InputEventPtr evt)
        {
            foreach (var button in suppressedMouseButtons.ToArray())
            {
                if (button.device != mouse || !button.ReadValueFromEvent(evt, out float value)) continue;
                button.WriteValueIntoEvent(0f, evt);
                if (value <= 0.5f) suppressedMouseButtons.Remove(button);
            }
        }

        private void MaskTrackedEscape(Keyboard keyboard, InputEventPtr evt)
        {
            if (!suppressEscapeUntilRelease || !keyboard.escapeKey.ReadValueFromEvent(evt, out float value)) return;
            keyboard.escapeKey.WriteValueIntoEvent(0f, evt);
            if (value <= 0.5f) suppressEscapeUntilRelease = false;
        }

        private static bool NonMovementDeviceActionPressed(PlayerInputActions.PlayerActions player, InputDevice device, InputEventPtr evt)
        {
            foreach (var control in device.allControls)
            {
                if (control is ButtonControl button && Pressed(button, evt) && NonMovementActionUsesControl(player, button))
                    return true;
                if (control is AxisControl axis && Mathf.Abs(axis.ReadValue()) <= 0.5f &&
                    axis.ReadValueFromEvent(evt, out float value) && Mathf.Abs(value) > 0.5f &&
                    NonMovementActionUsesControl(player, axis))
                    return true;
            }
            return false;
        }

        private static bool NonMovementActionUsesControl(PlayerInputActions.PlayerActions player, InputControl control,
            bool ignoreNativeSorry = false)
        {
            var actions = new[]
            {
                player.LookArround, player.LeftClick, player.RightClick, player.Scroll, player.Use, player.Zoom,
                player.Escape, player.Smoke, player.Spit, player.Slur, player.Map,
                player.GameConsole, player.FlashLight, player.HideUI
            };
            foreach (var action in actions)
                if (ActionUsesControl(action, control)) return true;
            if (ignoreNativeSorry
                ? ActionUsesControlExceptPath(player.Sorry, control, "<Keyboard>/t")
                : ActionUsesControl(player.Sorry, control)) return true;
            return false;
        }

        private static bool ActionUsesControlExceptPath(InputAction action, InputControl control, string ignoredPath)
        {
            foreach (var binding in action.bindings)
            {
                if (binding.isComposite || string.IsNullOrEmpty(binding.effectivePath) ||
                    string.Equals(binding.effectivePath, ignoredPath, StringComparison.OrdinalIgnoreCase)) continue;
                try { if (NativeControlPathMatches(binding.effectivePath, control)) return true; }
                catch { }
            }
            return false;
        }

        private bool ShouldIgnoreNativeSorryBinding(KeyControl key) => plugin.OverrideNativeSorry &&
            key != null && string.Equals(key.name, "t", StringComparison.OrdinalIgnoreCase);

        private static bool ActionUsesControl(InputAction action, InputControl control)
        {
            foreach (var binding in action.bindings)
            {
                if (binding.isComposite || string.IsNullOrEmpty(binding.effectivePath)) continue;
                try { if (NativeControlPathMatches(binding.effectivePath, control)) return true; }
                catch { }
            }
            return false;
        }

        private void EnforceWheelCapture()
        {
            if (!open) return;
            if (ownsLookDisable && lookAction != null && lookAction.enabled) lookAction.Disable();
        }

        private void Close(bool cancelled, string? reason = null)
        {
            if (!open) return;
            if (cancelled) LogWheelEvent(reason ?? "external-cancel");
            bool sameOwner = CanRestoreOwners();
            bool nativeOwnerAvailable = capturedGameplay != null &&
                NativeOwnerAvailable(capturedGameplay, sameOwner, capturedAssets, sceneHandle);
            open = false;
            if (root != null) root.gameObject.SetActive(false);
            if (cancelled && activeKey?.isPressed == true)
            {
                waitingKey = activeKey;
                awaitingRelease = true;
            }
            else if (!cancelled)
            {
                awaitingRelease = false; waitingKey = null;
            }
            if (activeKey != null && trackedOpeningKey == activeKey && !activeKey.isPressed)
                trackedOpeningKeyPressed = false;
            activeKey = null;
            selected = EmoteWheelSlot.None;
            previewed = EmoteWheelSlot.None;
            preview?.Hide();

            if (nativeOwnerAvailable && ownsLookDisable && lookWasEnabled && lookAction != null && !lookAction.enabled)
                lookAction.Enable();
            else if (sameOwner && capturedGameplay != null && capturedAssets != null && ownsLookDisable && lookWasEnabled)
                StorePendingLookRestore(capturedGameplay, capturedAssets, sceneHandle, lookAction!, lookWasEnabled);
            ownsLookDisable = false; lookWasEnabled = false; lookAction = null;
            capturedGameplay = null; capturedAssets = null; sceneHandle = -1;
        }

        private void LogWheelEvent(string reason, KeyCode? requestedKey = null, KeyControl? requestedControl = null)
        {
            if (cancelReasonLogCount >= 8 || !loggedCancelReasons.Add(reason)) return;
            cancelReasonLogCount++;
            var control = activeKey ?? requestedControl;
            KeyCode keyCode = open ? activeKeyCode : requestedKey ?? activeKeyCode;
            Debug.LogWarning("[MonMP] Emote wheel event: " + reason + " (frame=" + Time.frameCount +
                ", key=" + keyCode + ", control=" + (control?.path ?? "unresolved") +
                ", keyPressed=" + (control?.isPressed == true) +
                ", keyReleasedThisFrame=" + (control?.wasReleasedThisFrame == true) + ").");
        }

        private bool CanRestoreOwners()
        {
            return capturedGameplay != null && plugin.Gameplay == capturedGameplay &&
                HasCurrentInputOwner(capturedAssets, sceneHandle);
        }

        private bool HasCurrentInputOwner(GameAssets? ownerAssets, int ownerScene)
        {
            RefreshNativeLookups();
            var scene = SceneManager.GetActiveScene();
            return ownerAssets != null && scene.IsValid() && scene.isLoaded && scene.handle == ownerScene &&
                nativeLookupScene == ownerScene && nativeLookupAssets == ownerAssets;
        }

        private bool NativeOwnerAvailable(Gameplay gameplay, bool sameOwner, GameAssets? ownerAssets, int ownerScene)
        {
            if (gameplay == null || gameplay.Player == null || !Application.isFocused ||
                Cursor.lockState != CursorLockMode.Locked) return false;
            RefreshNativeLookups();
            var hands = gameplay.Player.Get_VisualHands;
            bool nativeModal = hands == null || hands.Get_IsMapOpen || hands.Get_isGameConsoleOpen ||
                plugin.GuestWorld?.HoldPresentation == true ||
                nativeLookupModal != null && nativeLookupModal.Get_isOpen ||
                ownerAssets == null || !ownerAssets.inputs.Player.enabled;
            return EmoteWheelSelection.CanRestoreNativeInput(
                sameOwner && plugin.Gameplay == gameplay && ownerAssets != null &&
                    nativeLookupAssets == ownerAssets &&
                    SceneManager.GetActiveScene().handle == ownerScene,
                EmoteGameplayPolicy.IsWheelDisplayState(gameplay.Get_PlayerState),
                gameplay.Get_isGamePaused || plugin.PauseMenuOpen,
                gameplay.Get_IsDead,
                gameplay.Get_IsSleeping,
                plugin.SocialUiBlocked || plugin.ChatBlocksInput || plugin.HoldPositionEditorOpen,
                nativeModal,
                TypingInField(),
                MonMPPlugin.CleanFrame,
                plugin.World?.SaveGate.Active == true);
        }

        private void StorePendingLookRestore(Gameplay ownerGameplay, GameAssets ownerAssets, int ownerScene,
            InputAction ownerLookAction, bool ownerLookWasEnabled)
        {
            pendingGameplay = ownerGameplay;
            pendingAssets = ownerAssets;
            pendingLookAction = ownerLookAction;
            pendingSceneHandle = ownerScene;
            pendingLookRestore = true;
            pendingLookWasEnabled = ownerLookWasEnabled;
        }

        private void TryRestorePendingOwners()
        {
            if (!pendingLookRestore) return;
            bool sameOwner = pendingGameplay != null && plugin.Gameplay == pendingGameplay &&
                HasCurrentInputOwner(pendingAssets, pendingSceneHandle);
            if (!sameOwner)
            {
                ClearPendingRestore();
                return;
            }
            if (!NativeOwnerAvailable(pendingGameplay!, sameOwner, pendingAssets, pendingSceneHandle)) return;

            if (pendingLookRestore && pendingLookWasEnabled && pendingLookAction != null && !pendingLookAction.enabled)
                pendingLookAction.Enable();
            ClearPendingRestore();
        }

        private void ClearPendingRestore()
        {
            pendingLookRestore = pendingLookWasEnabled = false;
            pendingLookAction = null; pendingGameplay = null; pendingAssets = null; pendingSceneHandle = -1;
        }

        // UI owners call this before they snapshot or take native input and cursor state.
        internal void CancelForUi()
        {
            if (open) Close(cancelled: true, "plugin-ui-opened");
            TryRestorePendingOwners();
        }

        private bool SameScene()
        {
            var scene = SceneManager.GetActiveScene();
            return capturedAssets != null && scene.IsValid() && scene.isLoaded && scene.handle == sceneHandle;
        }

        private string? NativeBindingConflictReason(KeyControl key)
        {
            var assets = NativeInputAssets();
            if (assets == null || assets.Count == 0) return "native-binding-assets-unavailable";
            var gameAssets = UnityEngine.Object.FindObjectOfType<GameAssets>();
            if (gameAssets == null || gameAssets.inputs == null) return "native-player-input-asset-unavailable";
            var playerAsset = gameAssets.inputs.asset;
            var sorryAction = gameAssets.inputs.Player.Sorry;
            PlayerState playerState = plugin.Gameplay?.Get_PlayerState ?? PlayerState.InCreeper;
            bool nativeSorryOverride = ShouldIgnoreNativeSorryBinding(key) &&
                EmoteGameplayPolicy.IsWheelDisplayState(playerState);
            foreach (var asset in assets)
            {
                bool isPlayerAsset = ReferenceEquals(asset, playerAsset);
                // Driver vehicle/ship maps can own controls while seated. Other supported states
                // only override the Player Sorry/T callback and keep unrelated maps fail-closed.
                if (IsInactiveAssetForSupportedPlayerState(nativeSorryOverride, isPlayerAsset,
                    EmoteGameplayPolicy.VehicleControlsMayBeActive(playerState))) continue;
                foreach (var binding in asset.bindings)
                {
                    if (binding.isComposite) continue;
                    string path = binding.effectivePath;
                    if (string.IsNullOrEmpty(path)) continue;
                    bool? bindingActionEnabled = null;
                    if (nativeSorryOverride && !isPlayerAsset &&
                        EmoteGameplayPolicy.VehicleControlsMayBeActive(playerState))
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(binding.action))
                                bindingActionEnabled = asset.FindAction(binding.action, false)?.enabled;
                        }
                        catch
                        {
                            // An unresolvable action remains fail-closed below.
                        }
                    }
                    if (IsInactiveBindingForSupportedPlayerState(nativeSorryOverride, isPlayerAsset,
                        EmoteGameplayPolicy.VehicleControlsMayBeActive(playerState), bindingActionEnabled)) continue;
                    if (IsOverriddenNativeSorryBinding(nativeSorryOverride, isPlayerAsset,
                        binding.action, path, sorryAction.id)) continue;
                    try
                    {
                        if (NativePathMatches(path, key))
                            return "native-binding-conflict:" + (string.IsNullOrEmpty(binding.action) ? "unknown-action" : binding.action) + ":" + path;
                    }
                    catch
                    {
                        // An unfamiliar active binding is treated as occupied so it cannot swallow the hold key.
                        return "native-binding-check-failed:" + (string.IsNullOrEmpty(binding.action) ? path : binding.action);
                    }
                }
            }
            return null;
        }

        private static bool IsOverriddenNativeSorryBinding(bool nativeSorryOverride, bool isPlayerAsset,
            string actionReference, string effectivePath, Guid sorryActionId)
        {
            if (!nativeSorryOverride || !isPlayerAsset ||
                !string.Equals(effectivePath, "<Keyboard>/t", StringComparison.OrdinalIgnoreCase)) return false;
            return string.Equals(actionReference, "Sorry", StringComparison.OrdinalIgnoreCase) ||
                Guid.TryParse(actionReference, out Guid actionId) && actionId == sorryActionId;
        }

        private static bool IsInactiveAssetForSupportedPlayerState(bool nativeSorryOverride, bool isPlayerAsset,
            bool vehicleControlsMayBeActive)
        {
            return nativeSorryOverride && !isPlayerAsset && !vehicleControlsMayBeActive;
        }

        private static bool IsInactiveBindingForSupportedPlayerState(bool nativeSorryOverride, bool isPlayerAsset,
            bool vehicleControlsMayBeActive, bool? bindingActionEnabled)
        {
            return nativeSorryOverride && !isPlayerAsset && vehicleControlsMayBeActive &&
                bindingActionEnabled == false;
        }

        private static bool NativePathMatches(string path, KeyControl key)
        {
            return KeyboardBindingMatchesKeyName(path, key.name);
        }

        private static bool NativeControlPathMatches(string path, InputControl control)
        {
            if (control is KeyControl key) return KeyboardBindingMatchesKeyName(path, key.name);
            return InputControlPath.Matches(path, control);
        }

        private static bool KeyboardBindingMatchesKeyName(string bindingPath, string keyName)
        {
            if (string.Equals(bindingPath, "<Keyboard>", StringComparison.OrdinalIgnoreCase)) return true;
            const string keyboardPrefix = "<Keyboard>/";
            if (string.IsNullOrEmpty(bindingPath) || !bindingPath.StartsWith(keyboardPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            string keyPattern = bindingPath.Substring(keyboardPrefix.Length);
            if (string.Equals(keyPattern, "anyKey", StringComparison.OrdinalIgnoreCase)) return true;
            if (keyPattern.Length == 0 || keyPattern.IndexOf('/') >= 0) return false;
            return KeyboardKeyNameMatches(keyPattern, keyName);
        }

        private static bool KeyboardKeyNameMatches(string pattern, string keyName)
        {
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(keyName)) return false;
            int patternIndex = 0, keyIndex = 0, lastStar = -1, starKeyIndex = -1;
            while (keyIndex < keyName.Length)
            {
                if (patternIndex < pattern.Length &&
                    (pattern[patternIndex] == '?' || char.ToUpperInvariant(pattern[patternIndex]) == char.ToUpperInvariant(keyName[keyIndex])))
                {
                    patternIndex++;
                    keyIndex++;
                }
                else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                {
                    lastStar = patternIndex++;
                    starKeyIndex = keyIndex;
                }
                else if (lastStar >= 0)
                {
                    patternIndex = lastStar + 1;
                    keyIndex = ++starKeyIndex;
                }
                else return false;
            }

            while (patternIndex < pattern.Length && pattern[patternIndex] == '*') patternIndex++;
            return patternIndex == pattern.Length;
        }

        private static List<InputActionAsset>? NativeInputAssets()
        {
            var gameAssets = UnityEngine.Object.FindObjectOfType<GameAssets>();
            if (gameAssets == null || gameAssets.inputs == null || gameAssets.vehicleInputs == null) return null;
            var assets = new List<InputActionAsset> { gameAssets.inputs.asset, gameAssets.vehicleInputs.asset };
            foreach (var ship in UnityEngine.Object.FindObjectsOfType<NWH.DWP2.ShipController.InputSystemShipInputProvider>(true))
                if (ship != null && ship.shipInputActions != null) assets.Add(ship.shipInputActions.asset);
            return assets.Where(a => a != null).Distinct().ToList();
        }

        private static KeyControl? ResolveKey(KeyCode code)
        {
            var keyboard = Keyboard.current;
            return keyboard == null ? null : ResolveKey(keyboard, code);
        }

        private static KeyControl? ResolveKey(Keyboard keyboard, KeyCode code)
        {
            if (keyboard == null || code == KeyCode.None) return null;
            string? name = InputSystemKeyName(code);
            if (name == null) return null;
            foreach (var key in keyboard.allKeys)
                if (string.Equals(key.name, name, StringComparison.OrdinalIgnoreCase)) return key;
            return null;
        }

        private static string? InputSystemKeyName(KeyCode key)
        {
            string name = key.ToString();
            if (name.StartsWith("Alpha", StringComparison.Ordinal)) return name.Substring(5);
            if (name.StartsWith("Keypad", StringComparison.Ordinal)) return "numpad" + name.Substring(6).ToLowerInvariant();
            switch (name)
            {
                case "Return": return "enter";
                case "KeypadEnter": return "numpadEnter";
                case "LeftControl": return "leftCtrl";
                case "RightControl": return "rightCtrl";
                case "LeftShift": return "leftShift";
                case "RightShift": return "rightShift";
                case "LeftAlt": return "leftAlt";
                case "RightAlt": return "rightAlt";
                case "LeftWindows": return "leftMeta";
                case "RightWindows": return "rightMeta";
                case "UpArrow": return "upArrow";
                case "DownArrow": return "downArrow";
                case "LeftArrow": return "leftArrow";
                case "RightArrow": return "rightArrow";
                case "PageUp": return "pageUp";
                case "PageDown": return "pageDown";
                case "BackQuote": return "backquote";
                case "LeftBracket": return "leftBracket";
                case "RightBracket": return "rightBracket";
                default: return key == KeyCode.None ? null : name.ToLowerInvariant();
            }
        }

        internal void Reset()
        {
            Close(cancelled: true, "reset");
            ClearOpeningPressLatch();
            awaitingRelease = false; waitingKey = null;
            preview?.Clear();
            if (root != null) root.gameObject.SetActive(false);
        }

        public void Dispose()
        {
            Reset();
            InputSystem.onBeforeUpdate -= BeforeInput;
            InputSystem.onEvent -= OnInput;
            if (root != null) UnityEngine.Object.Destroy(root.gameObject);
            root = null; wheelRect = null;
        }
    }

    internal sealed class EmoteWheelWedgeGraphic : MaskableGraphic
    {
        private float startAngle, endAngle, innerRadius, outerRadius;

        internal void Configure(float startAngle, float endAngle, float innerRadius, float outerRadius, bool highlighted)
        {
            this.startAngle = startAngle * Mathf.Deg2Rad;
            this.endAngle = endAngle * Mathf.Deg2Rad;
            this.innerRadius = innerRadius;
            this.outerRadius = outerRadius;
            raycastTarget = false;
            SetHighlighted(highlighted, available: true);
        }

        internal void SetHighlighted(bool highlighted, bool available)
        {
            color = !available
                ? new Color(0.28f, 0.30f, 0.34f, 0.48f)
                : highlighted
                    ? new Color(MenuKit.Accent.r, MenuKit.Accent.g, MenuKit.Accent.b, 0.96f)
                    : new Color(MenuKit.Accent.r, MenuKit.Accent.g, MenuKit.Accent.b, 0.70f);
        }

        protected override void OnPopulateMesh(VertexHelper vertexHelper)
        {
            vertexHelper.Clear();
            var rect = rectTransform.rect;
            float scale = Mathf.Min(rect.width, rect.height) / EmoteWheel.WheelRadiusDenominator;
            float inner = innerRadius * scale;
            float outer = outerRadius * scale;
            const int slices = 24;
            Color32 tint = color;
            for (int i = 0; i <= slices; i++)
            {
                float angle = Mathf.Lerp(startAngle, endAngle, i / (float)slices);
                var direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                vertexHelper.AddVert(direction * inner, tint, Vector2.zero);
                vertexHelper.AddVert(direction * outer, tint, Vector2.zero);
            }
            for (int i = 0; i < slices; i++)
            {
                int inner0 = i * 2, outer0 = inner0 + 1;
                int inner1 = inner0 + 2, outer1 = inner0 + 3;
                vertexHelper.AddTriangle(inner0, outer0, outer1);
                vertexHelper.AddTriangle(inner0, outer1, inner1);
            }
        }
    }
}
