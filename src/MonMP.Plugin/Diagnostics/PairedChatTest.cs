using System;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Persistence;
using MonMP.Core.World;
using MonMP.Plugin.Testing;
using MonMP.Plugin.UI;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedChatTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Keyboard keyboard;
        private readonly Keyboard? previousKeyboard;
        private readonly InputSettings.BackgroundBehavior background;
        private readonly InputAction nativeSorry;
        private readonly int blockedMenuCallbacks;
        private int menuExceptions;
        private int phase = -2;
        private float next;
        private Vector3 position;
        private bool cleaned;
        private bool sorryStarted;
        private InputAction[] heldActions = Array.Empty<InputAction>();
        private bool[] enabledActions = Array.Empty<bool>();
        private CursorLockMode cursorLock;
        private bool cursorVisible, saveObserved, saveRequested, deathStarted;
        private readonly bool originalHard;
        private readonly WorldChannel channel;
        private int acknowledgment;
        private Guid saveRound;
        private int saveCompletedFrame = -1;
        private const string SaveDraft = "Draft survives native coordinated save";
        private string? leaseFailure;
        private Vector3 deathPosition;
        private Quaternion deathRotation;
        internal string Status => "chat phase=" + phase + "; captions=" + string.Join(" | ", plugin.Chat!.Captions);

        internal PairedChatTest(MonMPPlugin plugin)
        {
            if (!LocalTestIsolation.Installed || !plugin.TestOptions.Enabled)
                throw new InvalidOperationException("Chat transition fixture requires an isolated local test copy.");
            this.plugin = plugin;
            originalHard = plugin.Gameplay!.Get_isHardMode; channel = plugin.World!.Channel;
            previousKeyboard = Keyboard.current;
            background = InputSystem.settings.backgroundBehavior;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            keyboard = InputSystem.AddDevice<Keyboard>(); keyboard.MakeCurrent();
            nativeSorry = UnityEngine.Object.FindObjectOfType<GameAssets>().inputs.Player.Sorry;
            nativeSorry.started += OnSorry;
            blockedMenuCallbacks = Patches.ChatMenuInputPatch.TestBlockedCallbacks;
            Application.logMessageReceived += OnLog;
            InputSystem.onBeforeUpdate += ObserveSaveLease;
            if (plugin.TestOptions.IsHost) channel.CommandRequested += Acknowledge;
        }

        private void Acknowledge(uint peer, ulong entity, byte[] payload)
        { if (peer != 0 && entity == ClockState.Entity && payload.Length == 2 && payload[0] == 0xca) acknowledgment = Math.Max(acknowledgment, payload[1]); }
        private void Signal(byte value) => channel.Request(ClockState.Entity, new[] { (byte)0xca, value });
        private string Draft => ((TMP_InputField)AccessTools.Field(typeof(CaptionChat), "input").GetValue(plugin.Chat)).text;
        private void CaptureActions()
        {
            var assets = UnityEngine.Object.FindObjectOfType<GameAssets>();
            heldActions = assets.inputs.asset.Concat(assets.vehicleInputs.asset)
                .Concat(UnityEngine.Object.FindObjectsOfType<NWH.DWP2.ShipController.InputSystemShipInputProvider>()
                    .Where(ship => ship.shipInputActions != null).SelectMany(ship => ship.shipInputActions.asset)).Distinct().ToArray();
            enabledActions = heldActions.Select(action => action.enabled).ToArray();
            cursorLock = Cursor.lockState; cursorVisible = Cursor.visible;
        }
        private void RequireActionsRestored(string reason)
        {
            Require(!plugin.Chat!.BlocksInput && heldActions.Select(action => action.enabled).SequenceEqual(enabledActions), reason);
            Require(Cursor.lockState == cursorLock && Cursor.visible == cursorVisible, "Chat did not restore the captured cursor owner.");
        }
        private void ObserveSaveLease()
        {
            if ((phase != 10 && phase != 11) || plugin.World?.SaveGate.Active != true) return;
            saveObserved = true;
            if (!plugin.Chat!.Open || Draft != SaveDraft || heldActions.Any(action => action.enabled))
                leaseFailure = "A real coordinated save discarded the active draft or released its native actions.";
        }

        private void OnSorry(InputAction.CallbackContext _) => sorryStarted = true;
        private void OnLog(string condition, string stack, LogType type)
        {
            if ((type == LogType.Exception || type == LogType.Error) && stack.Contains("MainMenu_Manager.CloseOpenWindows"))
                menuExceptions++;
        }

        internal bool Tick()
        {
            var chat = plugin.Chat!;
            var session = plugin.Session!;
            bool host = plugin.TestOptions.IsHost;
            if (leaseFailure != null) throw new InvalidOperationException(leaseFailure);
            if (Time.unscaledTime < next) return false;
            if (phase == -2)
            {
                // Load/activation can still hold native input when the host profile becomes ready.
                if (!nativeSorry.enabled || !keyboard.enabled || session.Peers.Count < 2 ||
                    plugin.World?.Channel.FullySynchronized != true) return false;
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.T));
                phase = -1; next = Time.unscaledTime + 0.3f;
            }
            else if (phase == -1)
            {
                Require(!chat.BlocksInput, "Chat captured the native Sorry key (#142).");
                Require(sorryStarted, "Enabled native Sorry did not receive T (#142): action=" + nativeSorry.enabled + "; keyboard=" + keyboard.enabled);
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                phase = 0; next = Time.unscaledTime + 0.2f;
            }
            else if (phase == 0)
            {
                CaptureActions();
                position = plugin.Gameplay!.PlayerWalking.transform.position;
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Enter));
                phase = 1; next = Time.unscaledTime + 0.3f;
            }
            else if (phase == 1)
            {
                Require(chat.Open, "Enter did not open chat.");
                var assets = UnityEngine.Object.FindObjectOfType<GameAssets>();
                Require(!assets.inputs.Player.enabled && !assets.vehicleInputs.VehicleControls.enabled, "Chat left gameplay actions enabled.");
                Require(heldActions.All(action => !action.enabled), "Chat left a native player, vehicle or ship action enabled.");
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W, Key.E, Key.Space));
                phase = 2; next = Time.unscaledTime + 0.6f;
            }
            else if (phase == 2)
            {
                Require(Vector3.Distance(position, plugin.Gameplay!.PlayerWalking.transform.position) < 0.25f, "Typing moved the player.");
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Escape));
                phase = 3; next = Time.unscaledTime + 0.2f;
            }
            else if (phase == 3)
            {
                Require(!chat.Open && !plugin.Gameplay!.Get_isGamePaused, "Esc did not cancel chat without opening pause.");
                Require(Patches.ChatMenuInputPatch.TestBlockedCallbacks > blockedMenuCallbacks && menuExceptions == 0,
                    "Chat Escape leaked into the native menu callback (#161): blocked=" +
                    (Patches.ChatMenuInputPatch.TestBlockedCallbacks - blockedMenuCallbacks) + "; exceptions=" + menuExceptions);
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                phase = 4; next = Time.unscaledTime + 0.2f;
            }
            else if (phase == 4)
            {
                Require(!chat.BlocksInput && UnityEngine.Object.FindObjectOfType<GameAssets>().inputs.Player.enabled, "Chat did not restore native input.");
                RequireActionsRestored("Ordinary chat close did not restore the exact native action set.");
                if (!host) InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Enter));
                phase = 5; next = Time.unscaledTime + 0.2f;
            }
            else if (phase == 5 && !host)
            {
                Require(chat.Open, "Enter did not open chat.");
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                chat.TestText("Guest caption Québec");
                phase = 6; next = Time.unscaledTime + 0.2f;
            }
            else if (phase == 6 && !host)
            {
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.Enter));
                phase = 7; next = Time.unscaledTime + 0.2f;
            }
            else if (host && phase == 5)
            {
                var guest = session.Peers.Values.FirstOrDefault(p => p.Id != 0);
                if (guest == null || !chat.Captions.Contains(guest.DisplayName + ": Guest caption Québec")) return false;
                Require(plugin.RemoteAvatarVoiceText(guest.Id) == "Guest caption Québec", "Guest overhead caption differs on host.");
                Require(session.SendChat("Host caption received"), "Host chat rejected.");
                phase = 8;
            }
            else if (!host && phase == 7)
            {
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                if (!chat.Captions.Contains(session.Peers[session.LocalPeerId].DisplayName + ": Guest caption Québec") ||
                    !chat.Captions.Contains(session.Peers[0].DisplayName + ": Host caption received")) return false;
                Require(plugin.RemoteAvatarVoiceText(0) == "Host caption received", "Host overhead caption differs on guest.");
                Require(!chat.Open, "Sending did not close chat.");
                Require(session.SendChat("Chat verified"), "Guest acknowledgement rejected.");
                phase = 9; next = Time.unscaledTime + 1;
            }
            else if (host && phase == 8)
            {
                if (!chat.Captions.Any(x => x.EndsWith(": Chat verified", StringComparison.Ordinal))) return false;
                phase = 9; next = Time.unscaledTime + 1;
            }
            else if (phase == 9)
            {
                CaptureActions(); chat.Begin(); chat.TestText(SaveDraft);
                Require(chat.Open && Draft == SaveDraft, "Could not prepare the native save overlap draft.");
                saveRound = plugin.World!.SaveRounds!.Round;
                if (!host) Signal(1);
                phase = 10;
            }
            else if (phase == 10)
            {
                if (host && acknowledgment >= 1 && !saveRequested)
                {
                    saveRequested = true;
                    SaveSystem.Save(House_Location.SpecialMenu);
                    Require(plugin.World!.Saves!.Pending, "Native SaveSystem.Save bypassed coordinated collection.");
                }
                ObserveSaveLease();
                if (saveObserved) phase = 11;
            }
            else if (phase == 11)
            {
                ObserveSaveLease();
                var world = plugin.World!;
                Require(world.SaveRounds!.Phase != SavePhase.Failed, "Native overlapping save failed: " + world.SaveRounds.LocalFailure);
                if (world.SaveGate.Active || world.SaveRounds.Round == saveRound || world.SaveRounds.Phase != SavePhase.Complete) return false;
                // Save release runs in World.Update; chat enforces its remaining lease in
                // LateUpdate before the next native input update. Observe that full frame.
                if (saveCompletedFrame < 0) { saveCompletedFrame = Time.frameCount; return false; }
                if (Time.frameCount <= saveCompletedFrame) return false;
                Require(chat.Open && Draft == SaveDraft && heldActions.All(action => !action.enabled),
                    "Native save completion lost the draft or enabled controls underneath chat.");
                chat.Close(); phase = 12; next = Time.unscaledTime + 0.3f;
            }
            else if (phase == 12)
            {
                RequireActionsRestored("Closing chat after a real coordinated save stranded native controls.");
                if (!host) { Signal(2); phase = 13; }
                else if (acknowledgment >= 2) phase = 13;
            }
            else if (phase == 13)
            {
                CaptureActions(); chat.Begin();
                Require(chat.Open, "Could not open chat before native death.");
                var game = plugin.Gameplay!;
                deathPosition = game.PlayerWalking.transform.position; deathRotation = game.PlayerWalking.transform.rotation;
                // Use the real reversible death coroutine in the isolated copy. No dead
                // flags, save gates, relief functions or input maps are forced by the test.
                game.Get_isHardMode = false; deathStarted = true;
                game.InitiateDeath("MonMP isolated chat input regression");
                Require(game.Get_IsDead && !UnityEngine.Object.FindObjectOfType<GameAssets>().inputs.Player.enabled,
                    "Native death did not claim Player input.");
                phase = 14; next = Time.unscaledTime + 0.3f;
            }
            else if (phase == 14)
            {
                if (plugin.Gameplay!.Get_IsDead)
                {
                    Require(!chat.Open && !chat.BlocksInput && heldActions.All(action => !action.enabled),
                        "Chat close restored native actions during the real death coroutine (LA-01).");
                    return false;
                }
                phase = 15; next = Time.unscaledTime + 0.3f;
            }
            else if (phase == 15)
            {
                RequireActionsRestored("Native death completion stranded deferred Player, vehicle or ship actions.");
                Require(!plugin.Gameplay!.GetComponent<BasicNeeds_Manager>().Dead, "Native death recovery did not finish its personal lifecycle.");
                Cleanup(); return true;
            }
            return false;
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        internal void Cleanup()
        {
            if (cleaned) return; cleaned = true;
            nativeSorry.started -= OnSorry;
            Application.logMessageReceived -= OnLog;
            InputSystem.onBeforeUpdate -= ObserveSaveLease;
            channel.CommandRequested -= Acknowledge;
            plugin.Chat?.Close();
            if (plugin.Gameplay != null)
            {
                plugin.Gameplay.Get_isHardMode = originalHard;
                if (deathStarted && !plugin.Gameplay.Get_IsDead)
                {
                    plugin.Gameplay.PlayerWalking.transform.SetPositionAndRotation(deathPosition, deathRotation);
                    plugin.Gameplay.Get_Player_Rigidbody.position = deathPosition;
                    plugin.Gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                }
            }
            InputSystem.RemoveDevice(keyboard); previousKeyboard?.MakeCurrent();
            InputSystem.settings.backgroundBehavior = background;
        }
    }
}
