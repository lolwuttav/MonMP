using System;
using MonMP.Core.Persistence;
using MonMP.Core.World;
using MonMP.Plugin.Patches;
using MonMP.Plugin.Replication;
using MonMP.Plugin.Testing;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Phase 79 exercises native plate clicks, a coordinated save, host menu reload, and a
    // guest reconnect whose snapshot must retain the host-authored text.
    internal sealed class PairedVehiclePlatesTest
    {
        private const string SaveNameKey = "SaveName";
        private const string SaveTimestampKey = "SaveTimestamp";
        private const string GuestCompleteMarker = "PLATEOK";
        private readonly MonMPPlugin plugin;
        private readonly bool host;
        private readonly string saveName;
        private readonly string originalSaveName;
        private readonly int originalSaveTimestamp;
        private readonly bool hadOriginalSaveName;
        private readonly bool hadOriginalSaveTimestamp;
        private SharedVehiclePlates? plates;
        private Gameplay? gameplay;
        private int phase;
        private float next;
        private int savedTimestamp;
        private ulong id;
        private VehiclePlateState initial, first, second, third, guestOriginal;
        private Vector3 savedPosition, savedVelocity;
        private bool savedPlayerPosition, saveSelectionRestored, disposed, watchingCommands, guestCompletedClicks;

        internal string Status => "vehicle plates phase=" + phase + "; count=" + (plates?.Count ?? 0) +
            "; id=" + (id == 0 ? "?" : id.ToString("X16")) + "; save=" + (savedTimestamp == 0 ? "pending" : savedTimestamp.ToString());

        internal PairedVehiclePlatesTest(MonMPPlugin plugin)
        {
            this.plugin = plugin;
            host = plugin.TestOptions.IsHost;
            if (!host)
            {
                saveName = "";
                originalSaveName = "";
                originalSaveTimestamp = 0;
                hadOriginalSaveName = false;
                hadOriginalSaveTimestamp = false;
                return;
            }

            hadOriginalSaveName = LocalTestIsolation.HasKey(SaveNameKey);
            hadOriginalSaveTimestamp = LocalTestIsolation.HasKey(SaveTimestampKey);
            originalSaveName = LocalTestIsolation.GetString(SaveNameKey);
            originalSaveTimestamp = LocalTestIsolation.GetInt(SaveTimestampKey);
            saveName = "MonMPVehiclePlates-" + Guid.NewGuid().ToString("N");
            try { SelectSave(-1); }
            catch
            {
                RestoreSaveSelection();
                throw;
            }
        }

        internal bool Tick(Gameplay? currentGameplay)
        {
            gameplay = currentGameplay;
            var currentPlates = plugin.World?.VehiclePlates;
            if (!ReferenceEquals(currentPlates, plates)) plates = currentPlates;
            return host ? Host() : Guest();
        }

        private bool Host()
        {
            if (phase <= 3)
            {
                if (!PairReady()) return false;
            }

            if (phase == 0)
            {
                Require(LocalTestIsolation.GetString(SaveNameKey) == saveName && LocalTestIsolation.GetInt(SaveTimestampKey) == -1,
                    "Host disposable save was not selected before session setup.");
                initial = plates!.TestState(id);
                Require(plates.TestNative(id) == initial && plates.TestRecord(id) == initial,
                    "Host plate save text and initial static record differ.");
                AttachCommandObserver();
                SavePlayerPosition();
                first = new VehiclePlateState(VehiclePlateCycle.Next(initial.Text));
                phase = 1;
            }
            else if (phase == 1 && plates!.TestState(id) == first)
            {
                Require(plates.TestNative(id) == first && plates.TestRecord(id) == first,
                    "Host did not run the native cycle or publish its first text.");
                second = new VehiclePlateState(VehiclePlateCycle.Next(first.Text));
                phase = 2;
            }
            else if (phase == 2 && plates!.TestState(id) == second)
            {
                Require(plates.TestNative(id) == second && plates.TestRecord(id) == second,
                    "Second guest click did not advance the host native plate exactly once.");
                Move(plates.TestPosition(id) + Vector3.forward);
                plates.TestClick(id);
                third = new VehiclePlateState(VehiclePlateCycle.Next(second.Text));
                Require(plates.TestNative(id) == third && plates.TestRecord(id) == third,
                    "Host native MouseRightDown did not cycle and publish its own click.");
                RestorePlayerPosition();
                phase = 3;
            }
            else if (phase == 3)
            {
                if (!guestCompletedClicks) return false;
                Require(plugin.Session?.Peers.Count == 2, "Guest disconnected before the coordinated plate save.");
                Require(plates!.TestNative(id) == third && plates.TestRecord(id) == third,
                    "Host plate text changed before the coordinated save.");
                SaveSystem.Save(House_Location.SpecialMenu);
                if (plugin.World?.Saves?.Pending != true)
                    throw new InvalidOperationException("Native plate save did not enter the coordinated save pipeline: " +
                        (plugin.World?.Saves?.LastFailure ?? "save coordinator unavailable"));
                phase = 4;
            }
            else if (phase == 4)
            {
                var saves = plugin.World?.Saves;
                if (saves == null || saves.Pending) return false;
                Require(saves.LastFailure.Length == 0 && plugin.World?.SaveRounds?.Phase == SavePhase.Complete,
                    "Coordinated native plate save failed: " + saves.LastFailure);
                savedTimestamp = SaveSystem.Get_LastSaveTimestamp;
                Require(savedTimestamp > 0 && LocalTestIsolation.GetString(SaveNameKey) == saveName &&
                    LocalTestIsolation.GetInt(SaveTimestampKey) == savedTimestamp && saves.LastSavedDirectory.Length != 0,
                    "Disposable native save did not publish its selected name and timestamp.");
                StopWatchingCommands();
                StopHostSessionForReload();
                phase = 5;
                next = Time.unscaledTime + 4f;
            }
            else if (phase == 5)
            {
                if (gameplay != null || UnityEngine.Object.FindObjectOfType<MainMenu_Manager>() == null || Time.unscaledTime < next) return false;
                SelectSave(savedTimestamp);
                UnityEngine.Object.FindObjectOfType<MainMenu_Manager>().StartGame(false);
                phase = 6;
                next = Time.unscaledTime + 5f;
            }
            else if (phase == 6)
            {
                if (gameplay?.Player == null || gameplay.Get_isGamePaused || !NativeLoadGate.Ready || Time.unscaledTime < next) return false;
                plugin.BeginPairedTestSession();
                phase = 7;
            }
            else if (phase == 7)
            {
                if (plugin.Session?.IsRunning != true || plates == null || plates.Count == 0) return false;
                if (id == 0) id = plates.TestPlate(Vehicle_Type.MainVehicle);
                Require(plates.TestNative(id) == third && plates.TestRecord(id) == third,
                    "Host native menu reload did not restore the coordinated plate text.");
                if (!PairReady()) return false;
                Require(plates.TestState(id) == third,
                    "Reloaded host plate record did not seed the reconnect snapshot.");
                phase = 8;
            }
            else if (phase == 8)
            {
                if (plugin.Session?.IsRunning != true || plugin.Session.Peers.Count != 1) return false;
                plates!.TestRestoreHost(id, initial);
                Require(plates.TestNative(id) == initial && plates.TestRecord(id) == initial,
                    "Host fixture did not restore its native plate text after reconnect verification.");
                plugin.EndPairedTestSession();
                RestoreSaveSelection();
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            if (phase <= 7 && !PairReady()) return false;
            if (phase == 0)
            {
                initial = plates!.TestRecord(id);
                guestOriginal = plates.TestOriginal(id);
                Require(plates.TestState(id) == initial && plates.TestNative(id) == initial,
                    "Guest did not apply the host's plate snapshot over its local save value.");
                SavePlayerPosition();
                Move(savedPosition + Vector3.forward * 40f);
                next = Time.unscaledTime + 1f;
                phase = 1;
            }
            else if (phase == 1 && Time.unscaledTime >= next)
            {
                plates!.TestRequest(id, VehiclePlateCycle.Next(initial.Text));
                next = Time.unscaledTime + 1f;
                phase = 2;
            }
            else if (phase == 2 && Time.unscaledTime >= next)
            {
                Require(plates!.TestRecord(id) == initial && plates.TestNative(id) == initial,
                    "Host accepted a valid-looking command from outside plate range.");
                Move(plates.TestPosition(id) + Vector3.forward);
                next = Time.unscaledTime + 1f;
                phase = 3;
            }
            else if (phase == 3 && Time.unscaledTime >= next)
            {
                plates!.TestClick(id);
                Require(plates.TestNative(id) == initial,
                    "Guest native click changed its plate before host acceptance.");
                first = new VehiclePlateState(VehiclePlateCycle.Next(initial.Text));
                next = Time.unscaledTime + 0.75f;
                phase = 4;
            }
            else if (phase == 4 && plates!.TestRecord(id) == first)
            {
                Require(plates.TestNative(id) == first && plates.TestState(id) == first,
                    "Host accepted the nearby guest click but the guest native plate did not follow.");
                second = new VehiclePlateState(VehiclePlateCycle.Next(first.Text));
                third = new VehiclePlateState(VehiclePlateCycle.Next(second.Text));
                plates.TestClick(id);
                Require(plates.TestNative(id) == first,
                    "Second guest click was not held until the host echo.");
                next = Time.unscaledTime + 0.75f;
                phase = 5;
            }
            else if (phase == 5 && plates!.TestRecord(id) == third)
            {
                Require(plates.TestNative(id) == third && plates.TestState(id) == third,
                    "Host's own native plate click did not reach both copies.");
                Move(savedPosition + Vector3.forward * 40f);
                next = Time.unscaledTime + 1f;
                phase = 6;
            }
            else if (phase == 6 && Time.unscaledTime >= next)
            {
                plates!.TestRequest(id, VehiclePlateCycle.Next(third.Text));
                next = Time.unscaledTime + 1f;
                phase = 7;
            }
            else if (phase == 7 && Time.unscaledTime >= next)
            {
                Require(plates!.TestRecord(id) == third && plates.TestNative(id) == third,
                    "Host accepted a forged out-of-range plate command.");
                plates.TestRequest(id, GuestCompleteMarker);
                RestorePlayerPosition();
                phase = 8;
                next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 8)
            {
                if (Time.unscaledTime < next || plugin.Session?.IsRunning == true) return false;
                if (!plugin.DiscoverPairedHost()) return false;
                plugin.BeginPairedTestSession();
                phase = 9;
            }
            else if (phase == 9)
            {
                if (!PairReady()) return false;
                var reconnectedOriginal = plates!.TestOriginal(id);
                Require(reconnectedOriginal == guestOriginal && reconnectedOriginal != third && plates.TestRecord(id) == third &&
                    plates.TestState(id) == third && plates.TestNative(id) == third,
                    "Guest reconnect did not apply the host's saved plate text over the local native label.");
                plugin.EndPairedTestSession();
                Require(plates.TestRestored && plates.TestNative(id) == reconnectedOriginal,
                    "Guest teardown after reconnect did not restore its local saved plate text.");
                return true;
            }
            return false;
        }

        private bool PairReady()
        {
            if (plates == null || plugin.Session?.IsRunning != true || plugin.Session.Peers.Count < 2 ||
                plugin.World?.PersonalProfileReady != true || !plates.Ready) return false;
            if (plates.Count == 0) throw new InvalidOperationException("Phase 79 requires a catalogued scene-bound stock vehicle plate.");
            if (id == 0) id = plates.TestPlate(Vehicle_Type.MainVehicle);
            return true;
        }

        private void HostCommandRequested(uint peer, ulong entity, byte[] payload)
        {
            if (!host || peer == 0 || entity != id || guestCompletedClicks) return;
            try { guestCompletedClicks = VehiclePlateState.Decode(payload).Text == GuestCompleteMarker; }
            catch { }
        }

        internal void AttachCommandObserver()
        {
            if (!host || watchingCommands || plugin.World == null) return;
            plates = plugin.World.VehiclePlates;
            if (plates.Count != 0 && id == 0) id = plates.TestPlate(Vehicle_Type.MainVehicle);
            plugin.World.Channel.CommandRequested += HostCommandRequested;
            watchingCommands = true;
        }

        private void StopWatchingCommands()
        {
            if (!watchingCommands) return;
            if (plugin.World != null) plugin.World.Channel.CommandRequested -= HostCommandRequested;
            watchingCommands = false;
        }

        private void SavePlayerPosition()
        {
            if (gameplay?.PlayerWalking == null) return;
            savedPosition = gameplay.PlayerWalking.transform.position;
            savedVelocity = gameplay.Get_Player_Rigidbody == null ? Vector3.zero : gameplay.Get_Player_Rigidbody.velocity;
            savedPlayerPosition = true;
        }

        private void RestorePlayerPosition()
        {
            if (!savedPlayerPosition) return;
            Move(savedPosition);
            if (gameplay?.Get_Player_Rigidbody != null) gameplay.Get_Player_Rigidbody.velocity = savedVelocity;
            savedPlayerPosition = false;
        }

        private void Move(Vector3 position)
        {
            if (gameplay?.PlayerWalking == null) return;
            gameplay.PlayerWalking.transform.position = position;
            if (gameplay.Get_Player_Rigidbody != null)
            {
                gameplay.Get_Player_Rigidbody.position = position;
                gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
            }
        }

        private void StopHostSessionForReload()
        {
            plugin.EndPairedTestSession();
            var escape = UnityEngine.Object.FindObjectOfType<EscapeMenuV2>()
                ?? throw new InvalidOperationException("Host escape menu is missing before native plate reload.");
            escape.Button_QuitGameConfirmation();
        }

        private void SelectSave(int timestamp)
        {
            LocalTestIsolation.SetString(SaveNameKey, saveName);
            LocalTestIsolation.SetInt(SaveTimestampKey, timestamp);
            LocalTestIsolation.Save();
        }

        private void RestoreSaveSelection()
        {
            if (!host || saveSelectionRestored) return;
            if (hadOriginalSaveName) LocalTestIsolation.SetString(SaveNameKey, originalSaveName);
            else LocalTestIsolation.DeleteKey(SaveNameKey);
            if (hadOriginalSaveTimestamp) LocalTestIsolation.SetInt(SaveTimestampKey, originalSaveTimestamp);
            else LocalTestIsolation.DeleteKey(SaveTimestampKey);
            LocalTestIsolation.Save();
            saveSelectionRestored = true;
        }

        internal void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (host)
            {
                try
                {
                    StopWatchingCommands();
                    if (plates != null && id != 0 && !string.IsNullOrEmpty(initial.Text) && plugin.Session?.IsRunning == true)
                        plates.TestRestoreHost(id, initial);
                    RestorePlayerPosition();
                }
                catch { }
                finally { RestoreSaveSelection(); }
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Vehicle plate phase 79: " + message);
        }
    }
}
