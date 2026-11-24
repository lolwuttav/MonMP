using System;
using System.IO;
using System.Linq;
using MonMP.Core.Persistence;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using MonMP.Plugin.Testing;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedPersistenceReloadTest
    {
        private const byte SignalMarker = 0x9b;
        private readonly MonMPPlugin plugin;
        private readonly string saveName = "MonMPReload-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        private int phase, ack, timestamp;
        private float next, nextSignal;
        private byte pendingSignal;
        private Guid guestProfile, initialSessionEpoch, reloadSessionEpoch;
        private WorldChannel? attachedChannel;
        private HostSaveCoordinator? exitSave;
        private SidecarSnapshot? saved;
        private bool windowsPrepared;
        private float savedKonigTrunkEuler;
        private float savedKonigHoodEuler;
        private Vector3 leftAt;
        public string Status => "paired persistence reload phase " + phase + "; ack=" + ack + "; pendingSignal=" + pendingSignal +
            "; epoch=" + plugin.Session?.Epoch.ToString("N") + "; exitPending=" + plugin.HostExitPending +
            "; saveFailure=" + exitSave?.LastFailure;
        internal PairedPersistenceReloadTest(MonMPPlugin plugin) => this.plugin = plugin;
        private void AttachChannel()
        {
            var channel = plugin.World?.Channel;
            if (ReferenceEquals(channel, attachedChannel)) return;
            if (attachedChannel != null) attachedChannel.CommandRequested -= Acknowledge;
            attachedChannel = channel; ack = 0; pendingSignal = 0; nextSignal = 0;
            if (plugin.TestOptions.IsHost && attachedChannel != null) attachedChannel.CommandRequested += Acknowledge;
        }
        private void Acknowledge(uint peer, ulong entity, byte[] payload)
        { if (peer != 0 && entity == ProfileRestoreChannel.Entity && payload.Length == 2 && payload[0] == SignalMarker && payload[1] >= 1 && payload[1] <= 3) ack = Math.Max(ack, payload[1]); }
        private void Signal(byte value)
        {
            pendingSignal = value; nextSignal = Time.unscaledTime + .5f;
            plugin.World!.Channel.Request(ProfileRestoreChannel.Entity, new[] { SignalMarker, value });
        }
        private static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Paired persistence reload: " + message); }
        internal bool Tick(Gameplay? gameplay)
        {
            AttachChannel();
            if (Time.unscaledTime < next) return false;
            if (plugin.TestOptions.IsHost) return Host(gameplay);
            if (pendingSignal != 0 && plugin.World?.Channel.FullySynchronized == true && Time.unscaledTime >= nextSignal)
                Signal(pendingSignal);
            if (phase == 0)
            {
                if (!plugin.DiscoverPairedHost()) return false;
                plugin.BeginPairedTestSession(); phase = 1;
            }
            else if (phase == 1 && plugin.World?.PersonalProfileReady == true && plugin.World.Channel.FullySynchronized &&
                plugin.World.ReconnectPositions?.Settled == true)
            {
                initialSessionEpoch = plugin.Session!.Epoch;
                Require(initialSessionEpoch != Guid.Empty, "The first guest connection has no session epoch.");
                UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>().GetMappleSyrup = 0.37f;
                GameManager.Get_PlayerStats[17] = 42;
                // Step aside so the rejoin has a position of its own to come back to.
                var walker = gameplay!.PlayerWalking.transform;
                leftAt = walker.position + walker.right * 2.5f;
                walker.position = leftAt;
                if (gameplay.Get_Player_Rigidbody != null) { gameplay.Get_Player_Rigidbody.position = leftAt; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
                Signal(1); phase = 2;
            }
            else if (phase == 2 && plugin.World?.Economy.TestState.CashCents == 1094)
            {
                Require(plugin.Vehicles!.Trunk.TestNativeOpen && plugin.Vehicles.Trunk.TestState.Open && plugin.Vehicles.Trunk.TestSynchronized,
                    "Host Konig trunk did not synchronize before the guest reload check.");
                savedKonigTrunkEuler = plugin.Vehicles.Trunk.TestNativeLocalEuler;
                Require(plugin.Vehicles.Hood.TestNative().Open && plugin.Vehicles.Hood.TestState.Open && plugin.Vehicles.Hood.TestSynchronized,
                    "Host Konig hood did not synchronize before the guest reload check.");
                savedKonigHoodEuler = plugin.Vehicles.Hood.TestNativeLocalEuler;
                plugin.EndPairedTestSession();
                Require(gameplay?.Player != null, "Guest world vanished before explicit menu return.");
                UnityEngine.Object.FindObjectOfType<EscapeMenuV2>().Button_QuitGameConfirmation();
                phase = 3; next = Time.unscaledTime + 5;
            }
            else if (phase == 3 && gameplay == null && UnityEngine.Object.FindObjectOfType<MainMenu_Manager>() != null)
            {
                // A failed host exit keeps advertising the original live session. Probe
                // the owned loopback endpoint and wait for an actual restarted host,
                // rather than rejoining that old world and accepting its cached profile.
                plugin.MenuProbeSavedServers(new[] { new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, plugin.TestOptions.Port) });
                var reloaded = plugin.MenuDiscoveredHosts.FirstOrDefault(host => host.Session.Compatible &&
                    host.Session.Port == plugin.TestOptions.Port && System.Net.IPAddress.IsLoopback(host.Endpoint.Address) &&
                    host.Session.SessionId != initialSessionEpoch);
                if (reloaded == null || !plugin.DiscoverPairedHost()) return false;
                reloadSessionEpoch = reloaded.Session.SessionId;
                Debug.Log("MONMP_PERSISTENCE_RELOAD_DISCOVERED: originalEpoch=" + initialSessionEpoch.ToString("N") +
                    "; reloadEpoch=" + reloadSessionEpoch.ToString("N"));
                plugin.BeginPairedTestSession(); Require(plugin.GuestWorld != null, "Second join did not use native menu loading."); phase = 4;
            }
            else if (phase == 4 && plugin.GuestWorld?.Activated == true && plugin.World?.PersonalProfileReady == true &&
                plugin.World.ReconnectPositions?.TestSettled == true)
            {
                Require(plugin.Session!.Epoch == reloadSessionEpoch && reloadSessionEpoch != initialSessionEpoch,
                    "Guest rejoined the original host session instead of the reloaded save.");
                var back = gameplay!.PlayerWalking.transform.position;
                Require(plugin.World.ReconnectPositions.TestApplied && Vector3.ProjectOnPlane(back - leftAt, Vector3.up).magnitude < 1.5f,
                    "Guest did not rejoin where it left: left=" + leftAt.ToString("F1") + "; back=" + back.ToString("F1"));
                Require(plugin.World.Channel.FullySynchronized, "Guest activated before snapshot catch-up.");
                VerifyLoadedWindows(); VerifyLoadedDoor(); VerifyLoadedTruckDoor(); VerifyLoadedKonigTrunk(); VerifyLoadedKonigHood();
                Require(Math.Abs(UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>().GetMappleSyrup - 0.37f) < 0.01f && GameManager.Get_PlayerStats[17] == 42,
                    "Guest profile was not restored from the reloaded host save.");
                Signal(2); phase = 5;
            }
            else if (phase == 5 && plugin.World?.Economy.TestState.CashCents == 1095)
            { Signal(3); phase = 6; }
            else if (phase == 6 && plugin.Session?.IsRunning != true) return true;
            return false;
        }
        private bool Host(Gameplay? gameplay)
        {
            if (phase == 2 && exitSave != null && !exitSave.Pending)
                Require(exitSave.LastSaveSucceeded && exitSave.LastFailure == "",
                    "Host exit save failed before native menu return: " + exitSave.LastFailure + "; " + Status);
            if (phase == 0)
            {
                LocalTestIsolation.SetString("SaveName", saveName); LocalTestIsolation.SetInt("SaveTimestamp", -1);
                plugin.BeginPairedTestSession(); Require(plugin.World != null, "Initial hosting failed.");
                initialSessionEpoch = plugin.Session!.Epoch; AttachChannel(); phase = 1;
            }
            else if (phase == 1 && ack >= 1)
            {
                var windows = plugin.Vehicles!.Windows;
                if (!windowsPrepared)
                {
                    var location = windows.TestPosition + Vector3.right;
                    Move(gameplay!, location);
                    windows.TestPower(); windows.TestClick(); windowsPrepared = true;
                    plugin.World!.Doors.TestDoor(HomeDoor()).ToggleDoor();
                    Move(gameplay!, plugin.Vehicles!.Doors.TestPosition + Vector3.up * 0.1f);
                    plugin.Vehicles.Doors.TestClick();
                    Require(!plugin.Vehicles.Trunk.TestNativeOpen, "Fixture requires the Konig trunk closed before opening.");
                    Move(gameplay!, plugin.Vehicles.Trunk.TestPosition);
                    plugin.Vehicles.Trunk.TestClick();
                    Require(!plugin.Vehicles.Hood.TestNative().Released, "Fixture requires the Konig hood latched before opening.");
                    Move(gameplay!, plugin.Vehicles.Hood.TestReleasePosition);
                    plugin.Vehicles.Hood.TestRelease();
                    Require(plugin.Vehicles.Hood.TestNative().Released && !plugin.Vehicles.Hood.TestNative().Open &&
                        Mathf.Abs(Mathf.DeltaAngle(-2f, plugin.Vehicles.Hood.TestNativeLocalEuler)) < 0.1f,
                        "Native Konig hood release did not produce the released-only -2 degree state.");
                    Move(gameplay!, plugin.Vehicles.Hood.TestOpenPosition);
                    plugin.Vehicles.Hood.TestOpen();
                    next = Time.unscaledTime + 4; return false;
                }
                Require(windows.TestState.TargetDown && !windows.TestState.Closed && !windows.TestMotorPlaying,
                    "Native host window did not open before saving.");
                Require(plugin.World!.Doors.TestNative(HomeDoor()).Open, "Native home door did not open before saving.");
                Require(plugin.Vehicles!.Doors.TestState().Open && plugin.Vehicles.Doors.TestNativeTruckState().Open &&
                    plugin.Vehicles.Doors.TestSynchronized, "Native truck door did not open and synchronize before saving.");
                Require(plugin.Vehicles.Trunk.TestNativeOpen && plugin.Vehicles.Trunk.TestState.Open && plugin.Vehicles.Trunk.TestSynchronized,
                    "Native Konig trunk did not open and synchronize before saving.");
                savedKonigTrunkEuler = plugin.Vehicles.Trunk.TestNativeLocalEuler;
                Require(plugin.Vehicles.Hood.TestNative().Open && plugin.Vehicles.Hood.TestState.Open && plugin.Vehicles.Hood.TestSynchronized,
                    "Native Konig hood did not open and synchronize before saving.");
                savedKonigHoodEuler = plugin.Vehicles.Hood.TestNativeLocalEuler;
                VerifyNativeTrunkSavePrecondition("before guest departure");
                guestProfile = plugin.Session!.Peers.Values.Single(p => p.Id != 0).ProfileId;
                if (plugin.World!.LiveProfiles!.Find(guestProfile)?.Statistics[17] != 42) return false;
                UnityEngine.Object.FindObjectOfType<Cash_Manager>().LoadSaveSystem(1094, new float[5]); phase = 10;
            }
            else if (phase == 10 && plugin.Session!.Peers.Count == 1)
            {
                VerifyNativeTrunkSavePrecondition("before host exit save");
                exitSave = plugin.World!.Saves;
                Require(exitSave != null, "Host exit has no native save coordinator.");
                UnityEngine.Object.FindObjectOfType<EscapeMenuV2>().Button_QuitGameConfirmation();
                Require(plugin.HostExitPending && plugin.Session.IsRunning, "Exit did not wait for saving after guest departure."); phase = 2;
            }
            else if (phase == 2 && gameplay == null)
            {
                var menu = UnityEngine.Object.FindObjectOfType<MainMenu_Manager>(); if (menu == null) return false;
                timestamp = SaveSystem.Get_LastSaveTimestamp;
                saved = SidecarStore.Load(Path.Combine(LocalTestIsolation.SaveRoot, saveName, timestamp.ToString()), SessionOptions.SupportedGameHash);
                Require(saved != null && saved.Document.Profiles.Count == 2 && saved.Document.Profiles.ContainsKey(guestProfile), "Exit save lost a participant profile.");
                var lastSave = SaveSystem.GetLastSaveData();
                Require(lastSave != null && lastSave.SaveName == saveName && lastSave.TimeStamp == timestamp,
                    "Native vehicle-state readback did not select the just-published host save.");
                var savedTruck = SaveSystem.GetThisVehicleFromLastSave(Vehicle_Type.OlTruck);
                Require(savedTruck != null && (savedTruck.DoorFL_Rotation < -1f || savedTruck.DoorFL_Rotation > 1f),
                    "Native braaaap save did not retain the open truck driver's door angle.");
                var savedKonig = SaveSystem.GetThisVehicleFromLastSave(Vehicle_Type.MainVehicle)
                    ?? throw new InvalidOperationException("Paired persistence reload: reloaded host save has no Konig vehicle record.");
                Require(savedKonig.Door_Trunk_Rotation < -1f || savedKonig.Door_Trunk_Rotation > 1f,
                    "Native braaaap save did not retain the open Konig trunk angle.");
                Require(Mathf.Abs(Mathf.DeltaAngle(savedKonigTrunkEuler, savedKonig.Door_Trunk_Rotation)) < 5f,
                    "Native Konig trunk save angle diverged from the host's open panel state.");
                savedKonigTrunkEuler = savedKonig.Door_Trunk_Rotation;
                Require(!savedKonig.HoodIsReleasedOnly &&
                    (savedKonig.Door_Hood_Rotation < -1f || savedKonig.Door_Hood_Rotation > 1f),
                    "Native braaaap save did not retain the open Konig hood hinge angle.");
                Require(Mathf.Abs(Mathf.DeltaAngle(savedKonigHoodEuler, savedKonig.Door_Hood_Rotation)) < 5f,
                    "Native Konig hood save angle diverged from the host's open panel state.");
                savedKonigHoodEuler = savedKonig.Door_Hood_Rotation;
                var personal = PlayerProfileState.FromSection(saved!.Document.Profiles[guestProfile].State[PlayerProfileState.SectionKey]);
                Require(personal.Statistics[17] == 42 && Math.Abs(personal.Needs[0] - 0.37f) < 0.01f, "Departed guest cache was not included in the host exit save.");
                Require(saved.Document.Profiles[guestProfile].State.ContainsKey(MonMP.Plugin.Replication.SharedReconnectPositions.SectionKey),
                    "The host exit save has no reconnect position for the departed guest.");
                LocalTestIsolation.SetString("SaveName", saveName); LocalTestIsolation.SetInt("SaveTimestamp", timestamp);
                menu.StartGame(false); phase = 3; next = Time.unscaledTime + 10;
            }
            else if (phase == 3 && gameplay?.Player != null && !gameplay.Get_isGamePaused)
            { phase = 4; next = Time.unscaledTime + 5; }
            else if (phase == 4)
            {
                plugin.BeginPairedTestSession(); Require(plugin.World != null, "Hosting the reloaded save failed.");
                Require(plugin.Session!.Epoch != initialSessionEpoch, "Reloaded host reused the original session epoch.");
                VerifyLoadedWindows(); VerifyLoadedDoor(); VerifyLoadedTruckDoor(); VerifyLoadedKonigTrunk(); VerifyLoadedKonigHood();
                AttachChannel(); phase = 5;
            }
            else if (phase == 5 && ack >= 2)
            {
                Require(plugin.Session!.Peers.Values.Any(p => p.ProfileId == guestProfile), "Rejoined guest GUID changed.");
                UnityEngine.Object.FindObjectOfType<Cash_Manager>().LoadSaveSystem(1095, new float[5]); phase = 6;
            }
            else if (phase == 6 && ack >= 3) { plugin.EndPairedTestSession(); return true; }
            return false;
        }
        private void VerifyNativeTrunkSavePrecondition(string checkpoint)
        {
            // A synchronized open flag alone does not establish a native-loadable hinge angle.
            // Use the same dictionary entry and constructor as SaveSystem.SaveVehicles.
            var vehicle = UnityEngine.Object.FindObjectOfType<GlobalClassManager>().Vehicles
                .Single(p => p.Value.vehicle_type == Vehicle_Type.MainVehicle);
            var parts = vehicle.Value.GetComponent<Vehicle_PartsManager>();
            var trunk = parts.Get_Vehicle_Trunk;
            var record = new VehicleControllerData(vehicle.Value, vehicle.Key);
            Debug.Log("MONMP_TRUNK_SAVE_PRECONDITION " + checkpoint + "; id=" + vehicle.Key +
                "; installed=" + parts.Door_Trunk.isInstalled + "; part=" + (parts.Door_Trunk.partInstalled != null) +
                "; active=" + trunk.gameObject.activeInHierarchy + "; open=" + trunk.Get_IsOpen +
                "; nativeAngle=" + trunk.transform.localEulerAngles.x +
                "; sharedAngle=" + plugin.Vehicles!.Trunk.TestNativeLocalEuler +
                "; serializedAngle=" + record.Door_Trunk_Rotation +
                "; body=" + (trunk.GetComponent<Rigidbody>() != null) +
                "; hinge=" + (trunk.GetComponent<HingeJoint>() != null));
            Require(record.Door_Trunk_Rotation < -1f || record.Door_Trunk_Rotation > 1f,
                "Fixture has no native-loadable open Konig trunk angle " + checkpoint + ".");
            Require(Mathf.Abs(Mathf.DeltaAngle(savedKonigTrunkEuler, record.Door_Trunk_Rotation)) < 5f,
                "Native serializer trunk differs from the prepared shared trunk " + checkpoint + ".");
        }
        private ulong HomeDoor() => plugin.World!.Doors.TestEntity(d => d.gameObject.activeInHierarchy &&
            UnityEngine.Object.FindObjectsOfType<Home>(true).Any(h => h.HouseDoors?.Contains(d) == true));
        private void VerifyLoadedDoor()
        {
            var doors = plugin.World!.Doors; ulong id = HomeDoor();
            Require(doors.TestNative(id).Open && doors.TestAtEndpoint(id) && doors.TestSynchronized,
                "Native saved home door did not reload and synchronize as open.");
        }
        private void VerifyLoadedWindows()
        {
            var windows = plugin.Vehicles!.Windows;
            Require(windows.TestCount > 0 && windows.TestState.TargetDown && !windows.TestState.Closed &&
                !windows.TestMotorPlaying && windows.TestSynchronized,
                "Native saved window did not reload and synchronize as open with its motor stopped.");
        }
        private void VerifyLoadedTruckDoor()
        {
            var doors = plugin.Vehicles!.Doors;
            Require(doors.TestNativeTruckState().Open && doors.TestState().Open && doors.TestSynchronized,
                "Native saved truck door did not reload and synchronize as open.");
        }
        private void VerifyLoadedKonigTrunk()
        {
            var trunk = plugin.Vehicles!.Trunk;
            Require(trunk.TestNativeOpen && trunk.TestState.Open && trunk.TestSynchronized,
                "Native saved Konig trunk did not reload and synchronize as open.");
            Require(Mathf.Abs(Mathf.DeltaAngle(savedKonigTrunkEuler, trunk.TestNativeLocalEuler)) < 2f,
                "Native saved Konig trunk angle did not survive host reload.");
        }
        private void VerifyLoadedKonigHood()
        {
            var hood = plugin.Vehicles!.Hood;
            var state = hood.TestNative();
            Require(state.Released && state.Open && hood.TestState.Open && hood.TestSynchronized,
                "Native saved Konig hood did not reload and synchronize as open.");
            Require(Mathf.Abs(Mathf.DeltaAngle(savedKonigHoodEuler, hood.TestNativeLocalEuler)) < 2f,
                "Native saved Konig hood angle did not survive host reload.");
        }
        private static void Move(Gameplay gameplay, Vector3 position)
        {
            gameplay.PlayerWalking.transform.position = position;
            gameplay.Get_Player_Rigidbody.position = position;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
    }
}
