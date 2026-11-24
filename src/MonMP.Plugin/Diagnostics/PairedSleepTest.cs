using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Persistence;
using MonMP.Core.World;
using MonMP.Plugin.Testing;
using NWH;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedSleepTest
    {
        private static readonly System.Reflection.FieldInfo RearSeatField = AccessTools.Field(typeof(Furniture_Bed_BackSeat), "rearSeat");
        private static readonly System.Reflection.FieldInfo AttachedVehicleField = AccessTools.Field(typeof(Furniture_Bed), "VehicleAttachedto");
        private static readonly System.Reflection.MethodInfo UpdateRearSeatVisual = AccessTools.Method(typeof(Part_Seat_Rear), "UpdateFoldVisual");

        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly BasicNeeds_Manager needs;
        private readonly TickSystem ticks;
        private readonly Furniture_Bed bed;
        private readonly Furniture_Bed_BackSeat backSeatBed;
        private readonly Part_Seat_Rear rearSeat;
        private readonly Furniture_Bed_FishingBoat fishingBoatBed;
        private readonly ToDoList_LineDetails fishingBoatPurchasedLine;
        private readonly BasicNeeds_Manager? originalBackSeatNeeds, originalFishingBoatNeeds;
        private readonly float originalEnergy, originalMapleSyrup, originalRearSeatValue, originalAudioVolume;
        private readonly Vector3 originalPosition, originalVelocity;
        private readonly Quaternion originalRotation;
        private readonly bool originalFishingBoatPurchased;
        private int phase, startTicks, callbackPhase, callbackTicks;
        private float next, callbackNext, thirst;
        private uint guest;
        private bool callbackProbesComplete, cleaned;
        private bool separatedSleepers;
        private static PairedSleepTest? recoveryProbe;
        private int personalRecoveries;
        private InputAction[] chatActions = Array.Empty<InputAction>();
        private bool[] chatEnabled = Array.Empty<bool>();

        internal string Status => "sleep callbacks=" + (callbackProbesComplete ? "passed" : callbackPhase) +
            "; phase=" + phase + "; state=" + plugin.World?.Sleep.State.Phase + " " + plugin.World?.Sleep.State.Ready + "/" + plugin.World?.Sleep.State.Required +
            "; completed=" + plugin.World?.Sleep.State.Completed + "; sleeping=" + gameplay.Get_IsSleeping + "; ticks=" + ticks.Get_totalTickSinceGameStart;

        internal PairedSleepTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            if (!LocalTestIsolation.Installed || !plugin.TestOptions.Enabled)
                throw new InvalidOperationException("Sleep fixture requires an isolated local test copy.");
            if (plugin.World?.Sleep == null || plugin.Session?.IsRunning != true || plugin.World.Sleep.State.Phase != SleepPhase.Idle)
                throw new InvalidOperationException("Sleep fixture requires an idle paired session.");

            this.plugin = plugin; this.gameplay = gameplay;
            needs = UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>() ?? throw new InvalidOperationException("Sleep fixture needs manager is missing.");
            ticks = UnityEngine.Object.FindObjectOfType<TickSystem>() ?? throw new InvalidOperationException("Sleep fixture tick system is missing.");
            var todo = UnityEngine.Object.FindObjectOfType<ToDoList_Manager>() ?? throw new InvalidOperationException("Sleep fixture progression manager is missing.");
            if (gameplay.PlayerWalking == null || gameplay.Get_Player_Rigidbody == null || gameplay.Get_PlayerState != PlayerState.Walking || gameplay.Get_IsSleeping)
                throw new InvalidOperationException("Sleep fixture requires an awake walking player with a rigidbody.");
            if (RearSeatField == null || AttachedVehicleField == null || UpdateRearSeatVisual == null)
                throw new InvalidOperationException("Sleep fixture could not resolve the native bed fields and fold visual callback.");

            bed = UnityEngine.Object.FindObjectsOfType<Furniture_Bed>(true)
                .Where(b => b.GetType() == typeof(Furniture_Bed) && b.gameObject.scene.IsValid() && b.gameObject.activeInHierarchy && b.SleepPosition != null)
                .OrderBy(b => Vector3.Distance(b.transform.position, gameplay.PlayerWalking.transform.position)).FirstOrDefault()
                ?? throw new InvalidOperationException("Sleep fixture has no scene-backed plain bed.");
            var backSeat = UnityEngine.Object.FindObjectsOfType<Furniture_Bed_BackSeat>(true)
                .Where(b => b.gameObject.scene.IsValid() && b.SleepPosition != null)
                .Select(b => new { Bed = b, Seat = RearSeatField.GetValue(b) as Part_Seat_Rear })
                .Where(x => x.Seat != null)
                .OrderBy(x => Vector3.Distance(x.Bed.transform.position, gameplay.PlayerWalking.transform.position)).FirstOrDefault()
                ?? throw new InvalidOperationException("Sleep fixture has no scene-backed back-seat bed with its native rear seat.");
            backSeatBed = backSeat.Bed; rearSeat = backSeat.Seat!;
            fishingBoatBed = UnityEngine.Object.FindObjectsOfType<Furniture_Bed_FishingBoat>(true)
                .Where(b => b.gameObject.scene.IsValid() && b.SleepPosition != null)
                .Where(b => AttachedVehicleField.GetValue(b) is not Vehicle vehicle || vehicle.Speed <= 0.5f)
                .OrderBy(b => Vector3.Distance(b.transform.position, gameplay.PlayerWalking.transform.position)).FirstOrDefault()
                ?? throw new InvalidOperationException("Sleep fixture has no stationary scene-backed fishing-boat bed.");
            if (!todo.Get_Lines.TryGetValue(ToDoList_Line.Boat_Fishing_Bought, out var purchasedLine))
                throw new InvalidOperationException("Sleep fixture cannot temporarily stage the fishing-boat native purchase gate.");
            var purchaseTextField = AccessTools.Field(typeof(ToDoList_LineDetails), "UI_text");
            if (purchaseTextField == null || purchaseTextField.GetValue(purchasedLine) == null)
                throw new InvalidOperationException("Sleep fixture fishing-boat purchase presentation is not initialized.");
            fishingBoatPurchasedLine = purchasedLine;

            originalBackSeatNeeds = backSeatBed.basicNeeds_Manager; originalFishingBoatNeeds = fishingBoatBed.basicNeeds_Manager;
            originalEnergy = needs.GetEnergy; originalMapleSyrup = needs.GetMappleSyrup;
            originalRearSeatValue = rearSeat.Value1; originalFishingBoatPurchased = fishingBoatPurchasedLine.value;
            originalAudioVolume = AudioListener.volume;
            originalPosition = gameplay.PlayerWalking.transform.position; originalRotation = gameplay.PlayerWalking.transform.rotation;
            originalVelocity = gameplay.Get_Player_Rigidbody.velocity;

            needs.GetEnergy = plugin.TestOptions.IsHost ? 0.3f : 0.6f;
            needs.GetMappleSyrup = 0.2f;
            recoveryProbe = this;
        }

        internal bool Tick()
        {
            try
            {
                if (!callbackProbesComplete && !TickBedCallbackProbes()) return false;
                bool done = plugin.TestOptions.IsHost ? Host() : Guest();
                if (done) Cleanup();
                return done;
            }
            catch
            {
                Cleanup();
                throw;
            }
        }

        private bool TickBedCallbackProbes()
        {
            var sleep = plugin.World!.Sleep;
            if (Time.unscaledTime < callbackNext) return false;
            if (plugin.TestOptions.IsHost)
            {
                if (callbackPhase == 0)
                {
                    if (plugin.Session!.Peers.Count < 2) return false;
                    guest = plugin.Session.Peers.Keys.Single(p => p != 0);
                    if (!sleep.TestIsReady(guest)) return false;
                    callbackTicks = ticks.Get_totalTickSinceGameStart; callbackNext = Time.unscaledTime + 2; callbackPhase = 1;
                }
                else if (callbackPhase == 1)
                {
                    Require(sleep.State.Phase == SleepPhase.Idle && ticks.Get_totalTickSinceGameStart - callbackTicks < 30,
                        "Guest back-seat wait advanced the shared clock: " + Status);
                    callbackPhase = 2;
                }
                else if (callbackPhase == 2 && !sleep.TestIsReady(guest))
                {
                    Require(ticks.Get_totalTickSinceGameStart - callbackTicks < 30, "Guest back-seat cancellation advanced the shared clock.");
                    callbackTicks = ticks.Get_totalTickSinceGameStart;
                    StartFishingBoatSleep();
                    Require(gameplay.Get_IsSleeping, "Fishing-boat MouseDown did not start native sleep on the host.");
                    callbackNext = Time.unscaledTime + 7; callbackPhase = 3;
                }
                else if (callbackPhase == 3)
                {
                    Require(sleep.LocalWaiting && sleep.State.Phase == SleepPhase.Idle && sleep.State.Ready == 1 && sleep.State.Required == 2,
                        "Host fishing-boat bed did not wait for the guest: " + Status);
                    Require(ticks.Get_totalTickSinceGameStart - callbackTicks < 30, "Host fishing-boat wait advanced the shared clock.");
                    RequireChatHeldForSleep();
                    sleep.RequestGetUp(); callbackNext = Time.unscaledTime + 1.5f; callbackPhase = 4;
                }
                else if (callbackPhase == 4)
                {
                    Require(!gameplay.Get_IsSleeping && !sleep.LocalWaiting && sleep.State.Phase == SleepPhase.Idle && sleep.State.Ready == 0,
                        "Host fishing-boat cancellation did not clear its sleep request: " + Status);
                    Require(ticks.Get_totalTickSinceGameStart - callbackTicks < 30, "Host fishing-boat cancellation advanced the shared clock.");
                    RequireChatRestoredAfterSleep();
                    Require(personalRecoveries == 0, "Cancelled host sleep applied native recovery.");
                    callbackProbesComplete = true;
                }
            }
            else
            {
                if (callbackPhase == 0)
                {
                    callbackTicks = ticks.Get_totalTickSinceGameStart;
                    StartBackSeatSleep();
                    Require(gameplay.Get_IsSleeping, "Back-seat Use did not start native sleep on the guest.");
                    callbackNext = Time.unscaledTime + 5; callbackPhase = 1;
                }
                else if (callbackPhase == 1)
                {
                    Require(sleep.LocalWaiting && sleep.State.Phase == SleepPhase.Idle && sleep.State.Ready == 1 && sleep.State.Required == 2,
                        "Guest back-seat Use did not enter the coordinated wait: " + Status);
                    Require(ticks.Get_totalTickSinceGameStart - callbackTicks < 30, "Guest back-seat wait advanced the shared clock.");
                    RequireChatHeldForSleep();
                    sleep.RequestGetUp(); callbackNext = Time.unscaledTime + 1.5f; callbackPhase = 2;
                }
                else if (callbackPhase == 2)
                {
                    Require(!gameplay.Get_IsSleeping && !sleep.LocalWaiting && gameplay.Get_PlayerState == PlayerState.Walking,
                        "Guest back-seat cancellation did not restore walking control.");
                    Require(ticks.Get_totalTickSinceGameStart - callbackTicks < 30, "Guest back-seat cancellation advanced the shared clock.");
                    RequireChatRestoredAfterSleep();
                    Require(personalRecoveries == 0, "Cancelled guest sleep applied native recovery.");
                    callbackPhase = 3;
                }
                else if (callbackPhase == 3 && sleep.State.Ready == 1 && sleep.State.Required == 2)
                {
                    callbackTicks = ticks.Get_totalTickSinceGameStart;
                    callbackNext = Time.unscaledTime + 1.5f; callbackPhase = 4;
                }
                else if (callbackPhase == 4)
                {
                    Require(sleep.State.Ready == 1 && sleep.State.Required == 2 && sleep.State.Phase == SleepPhase.Idle,
                        "Host did not remain waiting in its fishing-boat bed long enough for guest observation: " + Status);
                    Require(ticks.Get_totalTickSinceGameStart - callbackTicks < 30, "Host fishing-boat wait advanced the shared clock.");
                    callbackPhase = 5;
                }
                else if (callbackPhase == 5 && sleep.State.Ready == 0)
                {
                    Require(sleep.State.Phase == SleepPhase.Idle && ticks.Get_totalTickSinceGameStart - callbackTicks < 30,
                        "Host fishing-boat cancellation changed the shared clock or failed to return to idle.");
                    callbackProbesComplete = true;
                }
            }
            return callbackProbesComplete;
        }

        private void StartBackSeatSleep()
        {
            float foldValue = rearSeat.Value1;
            var oldNeeds = backSeatBed.basicNeeds_Manager;
            try
            {
                if (!rearSeat.CanSleep) SetRearSeatFoldValue(0f);
                Require(rearSeat.CanSleep, "Back-seat fixture cannot unfold the rear seat for its native Use callback.");
                backSeatBed.basicNeeds_Manager = needs;
                Move(backSeatBed.transform.position + Vector3.up * 0.2f);
                BeginChatInputProbe();
                backSeatBed.Use();
            }
            finally
            {
                backSeatBed.basicNeeds_Manager = oldNeeds;
                SetRearSeatFoldValue(foldValue);
            }
        }

        private void StartFishingBoatSleep()
        {
            bool purchased = fishingBoatPurchasedLine.value;
            var oldNeeds = fishingBoatBed.basicNeeds_Manager;
            try
            {
                // MouseDown reads this native gate synchronously. Keep the temporary unlock inside
                // the isolated test process and restore it before any coordinated/native save.
                fishingBoatPurchasedLine.value = true; fishingBoatPurchasedLine.UpdateStrike();
                fishingBoatBed.basicNeeds_Manager = needs;
                Move(fishingBoatBed.transform.position + Vector3.up * 0.2f);
                BeginChatInputProbe();
                fishingBoatBed.MouseDown();
            }
            finally
            {
                fishingBoatBed.basicNeeds_Manager = oldNeeds;
                fishingBoatPurchasedLine.value = purchased; fishingBoatPurchasedLine.UpdateStrike();
            }
        }

        private void SetRearSeatFoldValue(float value)
        {
            rearSeat.Value1 = value;
            UpdateRearSeatVisual.Invoke(rearSeat, null);
        }

        private void BeginChatInputProbe()
        {
            var assets = UnityEngine.Object.FindObjectOfType<GameAssets>();
            chatActions = assets.inputs.asset.Concat(assets.vehicleInputs.asset)
                .Concat(UnityEngine.Object.FindObjectsOfType<NWH.DWP2.ShipController.InputSystemShipInputProvider>()
                    .Where(ship => ship.shipInputActions != null).SelectMany(ship => ship.shipInputActions.asset)).Distinct().ToArray();
            chatEnabled = chatActions.Select(action => action.enabled).ToArray();
            plugin.Chat!.Begin();
            Require(plugin.Chat.Open, "Could not open chat before the native bed callback.");
        }
        private void RequireChatHeldForSleep()
        {
            Require(!plugin.Chat!.Open && !plugin.Chat.BlocksInput && chatActions.All(action => !action.enabled),
                "Chat restored a native action while native sleep still owned input.");
        }
        private void RequireChatRestoredAfterSleep()
        {
            Require(!plugin.Chat!.BlocksInput && chatActions.Select(action => action.enabled).SequenceEqual(chatEnabled),
                "Cancelling native sleep stranded chat's deferred Player, vehicle or ship actions.");
        }
        internal static void ObservePersonalRecovery(BasicNeeds_Manager manager)
        { if (recoveryProbe != null && manager == recoveryProbe.needs) recoveryProbe.personalRecoveries++; }

        private void Move(Vector3 p)
        { gameplay.PlayerWalking.transform.position = p; gameplay.Get_Player_Rigidbody.position = p; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }

        private bool Host()
        {
            var sleep = plugin.World!.Sleep;
            if (phase == 4 && gameplay.Get_IsSleeping && plugin.RemoteAvatarRest(guest) == Core.Protocol.PlayerRest.Sleeping)
            {
                var occupancy = sleep.BedOccupancy(guest, bed);
                var root = plugin.RemoteAvatarRoot(guest);
                if (occupancy.Count == 2 && root != null && Vector3.Distance(root.position, gameplay.Player.mainCamera.transform.position) >= 0.3f)
                    separatedSleepers = true;
            }
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                guest = plugin.Session.Peers.Keys.Single(p => p != 0);
                if (!sleep.TestIsReady(guest)) return false;
                // The waiting guest is shown asleep in the bed. Its ready request follows the native
                // four-second bed entry, and it gets up a second later, so check it now.
                Require(plugin.RemoteAvatarRest(guest) == Core.Protocol.PlayerRest.Sleeping && (!plugin.RemoteAvatarStockBody(guest) || plugin.RemoteAvatarLying(guest)),
                    "The guest waiting in bed was not shown lying asleep: rest=" + plugin.RemoteAvatarRest(guest) + "; body=" + plugin.RemoteAvatarBody(guest));
                try
                {
                    var root = plugin.RemoteAvatarRoot(guest);
                    if (root != null) AvatarPreviewCapture.SaveBitmap(root, System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "MonMP", "diagnostics", "avatar-live-sleep.bmp"), AvatarPreviewCapture.View.Side);
                }
                catch (Exception) { }
                startTicks = ticks.Get_totalTickSinceGameStart; phase = 1; next = Time.unscaledTime + 2;
            }
            else if (phase == 1)
            {
                if (Time.unscaledTime < next) return false;
                Require(sleep.State.Phase == SleepPhase.Idle && ticks.Get_totalTickSinceGameStart - startTicks < 30, "World advanced while only the guest was in bed.");
                phase = 2;
            }
            else if (phase == 2 && !sleep.TestIsReady(guest)) phase = 3;
            else if (phase == 3 && sleep.TestIsReady(guest))
            {
                startTicks = ticks.Get_totalTickSinceGameStart;
                Move(bed.transform.position + Vector3.up * 0.2f); bed.MouseDown();
                Require(gameplay.Get_IsSleeping, "Host bed did not start native sleep.");
                phase = 4;
            }
            else if (phase == 4 && sleep.State.Completed == 1 && !gameplay.Get_IsSleeping && gameplay.Get_PlayerState == PlayerState.Walking)
            {
                int advanced = ticks.Get_totalTickSinceGameStart - startTicks;
                Require(advanced >= 360 && advanced < 420, "World did not advance once by the longest request: " + advanced);
                Require(needs.GetEnergy < 0.01f, "Host native sleep did not restore energy.");
                Require(personalRecoveries == 1, "Host native sleep recovery was missing or repeated: " + personalRecoveries);
                Require(separatedSleepers, "Two players sharing the bed were never visibly separated.");
                phase = 5;
            }
            else if (phase == 5 && plugin.World.Saves?.Pending == false)
            {
                Require(plugin.World.Saves.LastFailure == "", "Post-sleep coordinated save failed: " + plugin.World.Saves.LastFailure);
                Require(personalRecoveries == 1, "Post-sleep save repeated host recovery.");
                phase = 6;
            }
            else if (phase == 6 && plugin.Session!.Peers.Count == 1) { plugin.EndPairedTestSession(); return true; }
            return false;
        }

        private bool Guest()
        {
            if (Time.unscaledTime < next || plugin.World?.PersonalProfileReady != true) return false;
            var sleep = plugin.World.Sleep;
            if (phase == 0)
            {
                Move(bed.transform.position + Vector3.up * 0.2f); bed.MouseDown();
                Require(gameplay.Get_IsSleeping, "Guest bed did not start native sleep.");
                phase = 1; next = Time.unscaledTime + 5;
            }
            else if (phase == 1)
            {
                Require(sleep.LocalWaiting && gameplay.Get_IsSleeping && sleep.State.Ready == 1 && sleep.State.Required == 2, "Guest was not waiting in bed: " + Status);
                float energy = needs.GetEnergy;
                sleep.RequestGetUp(); phase = 2; next = Time.unscaledTime + 1.5f;
                Require(Math.Abs(energy - 0.6f) < 0.05f, "Guest energy changed while waiting.");
            }
            else if (phase == 2)
            {
                Require(!gameplay.Get_IsSleeping && !sleep.LocalWaiting && gameplay.Get_PlayerState == PlayerState.Walking && needs.GetEnergy > 0.5f,
                    "Getting up did not restore walking control without sleeping.");
                Move(bed.transform.position + Vector3.up * 0.2f); bed.MouseDown();
                phase = 3; next = Time.unscaledTime + 5;
            }
            else if (phase == 3)
            {
                Require(sleep.LocalWaiting, "Guest did not wait in bed again.");
                startTicks = ticks.Get_totalTickSinceGameStart; thirst = needs.GetMappleSyrup; phase = 4;
            }
            else if (phase == 4 && sleep.State.Completed == 1 && !gameplay.Get_IsSleeping && gameplay.Get_PlayerState == PlayerState.Walking)
            {
                Require(ticks.Get_totalTickSinceGameStart - startTicks >= 360, "Guest clock did not follow the host's sleep.");
                Require(needs.GetEnergy < 0.01f, "Guest native sleep did not restore energy.");
                Require(personalRecoveries == 1, "Guest native sleep recovery was missing or repeated: " + personalRecoveries);
                Require(needs.GetMappleSyrup <= thirst + 0.01f, "Guest thirst decayed as if awake during sleep: " + thirst + " -> " + needs.GetMappleSyrup);
                phase = 5;
            }
            else if (phase == 5 && plugin.World.SaveRounds?.Phase == SavePhase.Complete)
            {
                Require(plugin.World.Sleep.State.Phase == SleepPhase.Idle, "Sleep did not return to idle.");
                Require(personalRecoveries == 1, "Post-sleep save repeated guest recovery.");
                plugin.EndPairedTestSession(); return true;
            }
            else if (phase == 5) Require(plugin.World.SaveRounds?.Phase != SavePhase.Failed, "Post-sleep coordinated save failed on the guest.");
            return false;
        }

        internal void Cleanup()
        {
            if (cleaned) return;
            if (ReferenceEquals(recoveryProbe, this)) recoveryProbe = null;
            var failures = new List<Exception>();
            void Restore(Action action)
            {
                try { action(); }
                catch (Exception ex) { failures.Add(ex); }
            }
            if (gameplay.Get_IsSleeping)
            {
                Restore(() => plugin.World?.Sleep.RequestGetUp());
                Restore(() => gameplay.SleepingOverlay.gameObject.SetActive(false));
                Restore(() => AudioListener.volume = gameplay.MasterVolume);
                Restore(() => gameplay.PlayerWalking.ExitBed());
                Restore(() => gameplay.PlayerControl_Enable());
                Restore(() => gameplay.Sleep_Stop());
            }
            Restore(() => backSeatBed.basicNeeds_Manager = originalBackSeatNeeds);
            Restore(() => fishingBoatBed.basicNeeds_Manager = originalFishingBoatNeeds);
            Restore(() => { fishingBoatPurchasedLine.value = originalFishingBoatPurchased; fishingBoatPurchasedLine.UpdateStrike(); });
            Restore(() => SetRearSeatFoldValue(originalRearSeatValue));
            Restore(() => needs.GetEnergy = originalEnergy);
            Restore(() => needs.GetMappleSyrup = originalMapleSyrup);
            Restore(() => gameplay.PlayerWalking.transform.SetPositionAndRotation(originalPosition, originalRotation));
            Restore(() => gameplay.Get_Player_Rigidbody.position = originalPosition);
            Restore(() => gameplay.Get_Player_Rigidbody.velocity = originalVelocity);
            Restore(() => AudioListener.volume = originalAudioVolume);
            Restore(() => plugin.Chat?.Close());
            cleaned = true;
            if (failures.Count != 0) throw new AggregateException("Sleep fixture cleanup could not restore every isolated test value.", failures);
        }

        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Sleep test: " + message); }
    }
    [HarmonyPatch]
    internal static class PairedSleepNativeRecoveryProbe
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(BasicNeeds_Manager), nameof(BasicNeeds_Manager.Fill_Sleep));
            yield return AccessTools.Method(typeof(BasicNeeds_Manager), nameof(BasicNeeds_Manager.SkipNight));
        }
        private static void Postfix(BasicNeeds_Manager __instance) => PairedSleepTest.ObservePersonalRecovery(__instance);
    }
}
