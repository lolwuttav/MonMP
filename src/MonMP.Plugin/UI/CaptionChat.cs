using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Core.Protocol;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;

namespace MonMP.Plugin.UI
{
    internal sealed class CaptionChat : IDisposable
    {
        private readonly MonMPPlugin plugin;
        private readonly List<(string Text, float Until)> feed = new List<(string, float)>();
        private readonly TimeSkipInputLease inputLease = new TimeSkipInputLease();
        private Func<bool>? inputOwnerCurrent, inputRestoreAllowed;
        private RectTransform? root, column;
        private TMP_InputField? input;
        private TextMeshProUGUI[] lines = Array.Empty<TextMeshProUGUI>();
        private bool open, restorePending, focusPending, cursorVisible, cursorRestorePending;
        private CursorLockMode cursorLock;
        private int closedFrame, openedFrame;
        private float captionWidth;
        internal bool BlocksInput => open || restorePending;
        internal bool Open => open;
        internal string[] Captions => feed.Select(x => x.Text).ToArray();

        internal CaptionChat(MonMPPlugin plugin)
        {
            this.plugin = plugin;
            InputSystem.onEvent += OnInput;
            InputSystem.onBeforeUpdate += BeforeInput;
        }

        private bool CanOpen => !plugin.SocialUiBlocked && !plugin.HoldPositionEditorOpen && !MonMPPlugin.CleanFrame && plugin.Session?.IsReady == true &&
            plugin.World?.Channel.FullySynchronized == true && plugin.GuestWorld?.HoldPresentation != true &&
            plugin.World.SaveGate.Active != true && plugin.Gameplay?.Get_isGamePaused == false &&
            plugin.Gameplay?.Get_IsDead == false && plugin.Gameplay?.Get_IsSleeping == false &&
            UnityEngine.Object.FindObjectOfType<UI_GenericWindowManager>()?.Get_isOpen != true &&
            !TypingInField();

        // Unity-null checks: `?.` doesn't see a destroyed selection, whose GetComponent then threw on
        // every key press after the selected menu object went away.
        private static bool TypingInField()
        {
            var events = EventSystem.current;
            if (events == null) return false;
            var selected = events.currentSelectedGameObject;
            return selected != null && selected.GetComponent<TMP_InputField>() != null;
        }

        private void OnInput(InputEventPtr evt, InputDevice device)
        {
            if (device is not Keyboard keyboard || !evt.IsA<StateEvent>() && !evt.IsA<DeltaStateEvent>()) return;
            // T drives native Sorry and the boat anchor; leave it to those actions (#142).
            if (!BlocksInput && (Pressed(keyboard.enterKey, evt) || Pressed(keyboard.numpadEnterKey, evt)) &&
                CanOpen) Begin();
        }

        private static bool Pressed(UnityEngine.InputSystem.Controls.KeyControl key, InputEventPtr evt) =>
            !key.isPressed && key.ReadValueFromEvent(evt, out float value) && value > 0.5f;

        private void EnsureUI()
        {
            if (root != null) return;
            root = MenuKit.Canvas("MonMP Chat", 460);
            column = MenuKit.Rect("Captions", root);
            column.anchorMin = column.anchorMax = column.pivot = Vector2.zero;
            column.sizeDelta = new Vector2(850, 310); column.anchoredPosition = new Vector2(28, 28);
            input = MenuKit.Input(column, "Message (Enter to send, Esc to cancel)", ChatMessage.MaxCharacters, 23);
            var fieldRect = (RectTransform)input.transform;
            fieldRect.anchorMin = fieldRect.anchorMax = fieldRect.pivot = Vector2.zero;
            fieldRect.sizeDelta = new Vector2(850, 46);
            input.onSubmit.AddListener(_ => Submit());
            input.gameObject.SetActive(false);
            lines = new TextMeshProUGUI[5];
            for (int i = 0; i < lines.Length; i++)
            {
                var line = MenuKit.Label(column, "", 23);
                line.enableWordWrapping = true; line.overflowMode = TextOverflowModes.Overflow;
                line.rectTransform.anchorMin = line.rectTransform.anchorMax = line.rectTransform.pivot = Vector2.zero;
                line.rectTransform.sizeDelta = new Vector2(850, 48);
                line.rectTransform.anchoredPosition = new Vector2(0, 56 + (4 - i) * 48);
                lines[i] = line;
            }
        }

        internal void Begin()
        {
            if (BlocksInput || !CanOpen) return;
            plugin.ReleaseSocialInput();
            var gameAssets = UnityEngine.Object.FindObjectOfType<GameAssets>();
            var gameplay = plugin.Gameplay;
            if (gameAssets == null || gameplay == null || gameplay.Player == null) return;
            EnsureUI();
            var player = gameplay.Player;
            var hands = player.Get_VisualHands;
            if (hands == null) return;
            var playerActions = gameAssets.inputs.asset;
            var vehicleActions = gameAssets.vehicleInputs.asset;
            int scene = SceneManager.GetActiveScene().handle;
            var assets = new List<InputActionAsset> { playerActions, vehicleActions };
            var ships = new List<(NWH.DWP2.ShipController.InputSystemShipInputProvider Provider, InputActionAsset Actions)>();
            foreach (var ship in UnityEngine.Object.FindObjectsOfType<NWH.DWP2.ShipController.InputSystemShipInputProvider>())
                if (ship.shipInputActions != null)
                {
                    assets.Add(ship.shipInputActions.asset);
                    ships.Add((ship, ship.shipInputActions.asset));
                }
            Func<bool> ownerCurrent = () => gameplay != null && player != null && gameAssets != null &&
                plugin.Gameplay == gameplay && gameplay.Player == player && GameAssets.i == gameAssets &&
                hands != null && player.Get_VisualHands == hands &&
                gameAssets.inputs.asset == playerActions && gameAssets.vehicleInputs.asset == vehicleActions &&
                SceneManager.GetActiveScene().handle == scene;
            Func<bool> restoreAllowed = () => plugin.GuestWorld?.HoldPresentation != true && !MonMPPlugin.CleanFrame &&
                plugin.World?.SaveGate.Active != true && !plugin.SpectatorUiBlocked && !plugin.Spectating &&
                !plugin.PauseMenuOpen && !gameplay.Get_isGamePaused && !gameplay.Get_IsDead && !gameplay.Get_IsSleeping;
            var consoleMap = gameAssets.inputs.GameConsole.Get();
            var escapeMap = gameAssets.inputs.EscapeMenu.Get();
            inputOwnerCurrent = ownerCurrent; inputRestoreAllowed = restoreAllowed;
            inputLease.Begin(assets, action => restoreAllowed() &&
                (action.actionMap == escapeMap || (action.actionMap == consoleMap
                    ? hands != null && hands.Get_isGameConsoleOpen
                    : hands != null && !hands.Get_isGameConsoleOpen &&
                      UnityEngine.Object.FindObjectOfType<UI_GenericWindowManager>()?.Get_isOpen != true)), ownerCurrent,
                action => playerActions.Contains(action) || vehicleActions.Contains(action) ||
                    ships.Exists(ship => ship.Provider != null && ship.Provider.shipInputActions?.asset == ship.Actions && ship.Actions.Contains(action)));
            cursorLock = Cursor.lockState; cursorVisible = Cursor.visible;
            cursorRestorePending = true;
            open = true; openedFrame = Time.frameCount; focusPending = true;
            input!.text = ""; input.gameObject.SetActive(true); root!.gameObject.SetActive(true);
            HoldInputs();
        }

        internal void HoldInputs()
        {
            if (!BlocksInput) return;
            inputLease.Hold();
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }

        private void BeforeInput()
        {
            if (restorePending && Time.frameCount > closedFrame && Keyboard.current?.enterKey.isPressed != true &&
                Keyboard.current?.numpadEnterKey.isPressed != true && Keyboard.current?.escapeKey.isPressed != true)
                Restore();
            inputLease.TryRestorePending();
            RestoreCursor();
            HoldInputs();
        }

        internal void Submit()
        {
            if (!open || Time.frameCount <= openedFrame) return;
            string text = input!.text.Trim();
            if (text.Length > 0)
            {
                try
                {
                    if (plugin.Session?.SendChat(text) != true)
                    { plugin.ReportItemUseUnavailable("Please wait a moment before sending another message."); input.ActivateInputField(); return; }
                }
                catch (Exception ex) when (ex is System.IO.IOException || ex is ArgumentException)
                { plugin.ReportItemUseUnavailable(ex.Message); input.ActivateInputField(); return; }
            }
            Close();
        }

        internal void Close()
        {
            if (!open) return;
            open = false; focusPending = false; restorePending = true; closedFrame = Time.frameCount;
            input?.DeactivateInputField(); input?.gameObject.SetActive(false);
            EventSystem.current?.SetSelectedGameObject(null);
        }

        internal void CloseForTimeSkip()
        {
            Close();
            if (restorePending) Restore();
        }

        private void Restore()
        {
            restorePending = false;
            inputLease.End();
            RestoreCursor();
        }

        private void RestoreCursor()
        {
            if (!cursorRestorePending || BlocksInput) return;
            if (inputOwnerCurrent?.Invoke() != true) { cursorRestorePending = false; return; }
            if (inputRestoreAllowed?.Invoke() != true ||
                UnityEngine.Object.FindObjectOfType<UI_GenericWindowManager>()?.Get_isOpen == true) return;
            Cursor.lockState = cursorLock; Cursor.visible = cursorVisible;
            cursorRestorePending = false;
        }

        internal void Receive(ChatMessage message)
        {
            if (plugin.Session?.Peers.TryGetValue(message.PeerId, out var peer) != true) return;
            feed.Add((peer.DisplayName + ": " + message.Text, Time.unscaledTime + ChatMessage.CaptionSeconds));
            if (feed.Count > 5) feed.RemoveAt(0);
        }

        internal void Tick()
        {
            if (plugin.Session?.IsRunning != true) { Reset(); return; }
            if (open && inputOwnerCurrent?.Invoke() != true) Close();
            if (MonMPPlugin.CleanFrame || plugin.Gameplay?.Get_isGamePaused == true || plugin.Gameplay?.Get_IsDead == true ||
                plugin.Gameplay?.Get_IsSleeping == true || plugin.GuestWorld?.HoldPresentation == true) Close();
            if (open && Time.frameCount > openedFrame && Keyboard.current?.escapeKey.wasPressedThisFrame == true) Close();
            if (open && Time.frameCount > openedFrame && (Keyboard.current?.enterKey.wasPressedThisFrame == true || Keyboard.current?.numpadEnterKey.wasPressedThisFrame == true)) Submit();
            EnsureUI(); root!.gameObject.SetActive(!MonMPPlugin.CleanFrame);
            if (focusPending) { focusPending = false; input!.ActivateInputField(); input.Select(); }
            feed.RemoveAll(x => Time.unscaledTime >= x.Until);
            float width = Mathf.Min(850, Mathf.Max(200, root.rect.width - 56));
            bool reflow = !Mathf.Approximately(captionWidth, width);
            captionWidth = width;
            for (int i = 0; i < lines.Length; i++)
            {
                string text = i < feed.Count ? feed[i].Text : "";
                if (lines[i].text != text) { lines[i].text = text; reflow = true; }
                lines[i].gameObject.SetActive(i < feed.Count);
                lines[i].alpha = i < feed.Count ? Mathf.Clamp01((feed[i].Until - Time.unscaledTime) / 2) : 0;
            }
            if (reflow) ReflowCaptions(width);
            HoldInputs();
        }

        private void ReflowCaptions(float width)
        {
            float bottom = 56;
            for (int i = feed.Count - 1; i >= 0; i--)
            {
                var line = lines[i];
                float height = Mathf.Max(32, line.GetPreferredValues(line.text, width, 0).y + 4);
                line.rectTransform.sizeDelta = new Vector2(width, height);
                line.rectTransform.anchoredPosition = new Vector2(0, bottom);
                bottom += height;
            }
            column!.sizeDelta = new Vector2(width, bottom);
            var field = (RectTransform)input!.transform;
            field.sizeDelta = new Vector2(width, field.sizeDelta.y);
        }

        internal void Reset()
        {
            Close(); if (restorePending) Restore(); inputLease.TryRestorePending(); RestoreCursor(); feed.Clear();
            if (root != null) root.gameObject.SetActive(false);
        }
        public void Dispose()
        {
            Reset(); InputSystem.onEvent -= OnInput; InputSystem.onBeforeUpdate -= BeforeInput;
            inputLease.Dispose();
            if (root != null) UnityEngine.Object.Destroy(root.gameObject);
        }

#if MONMP_DEVELOPMENT
        internal void TestText(string text) { if (input != null) input.text = text; }
#endif

    }
}
