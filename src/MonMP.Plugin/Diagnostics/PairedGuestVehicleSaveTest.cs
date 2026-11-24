using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.Persistence;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using MonMP.Plugin.Testing;
using NWH;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedGuestVehicleSaveTest
    {
        private static readonly Vehicle_Type[] Types = { Vehicle_Type.SmollATV, Vehicle_Type.OlTruck };
        private const byte NativePoseRequestMarker = 0xF0;
        private const byte NativePoseReplyMarker = 0xF1;
        private const byte NativePoseReplyVersion = 1;
        private const float ReloadPositionTolerance = 0.25f;
        private const float ReloadRotationTolerance = 3f;
        private const float DriveTimeout = 30f;

        private readonly struct NativeSavedPose
        {
            internal readonly Vector3 Position;
            internal readonly Vector3 EulerAngles;
            internal readonly bool EngineRunning;
            internal readonly int Ignition;
            internal readonly int Gear;

            internal NativeSavedPose(Vector3 position, Vector3 eulerAngles, bool engineRunning, int ignition, int gear)
            { Position = position; EulerAngles = eulerAngles; EngineRunning = engineRunning; Ignition = ignition; Gear = gear; }

            internal NativeSavedPose(VehicleControllerData data)
                : this(ReadVector(data.position, "position"), ReadVector(data.rotation, "rotation"), data.IsEngineRunning,
                    data.IgnitionPosition, data.Gear) { }
        }

        private readonly MonMPPlugin plugin;
        private readonly bool host;
        private readonly string saveName;
        private readonly Dictionary<Vehicle_Type, VehicleState> baseline = new Dictionary<Vehicle_Type, VehicleState>();
        private readonly Dictionary<Vehicle_Type, VehicleState> expected = new Dictionary<Vehicle_Type, VehicleState>();
        private readonly Dictionary<Vehicle_Type, NativeSavedPose> hostExitSavedPoses = new Dictionary<Vehicle_Type, NativeSavedPose>();
        private readonly HashSet<uint> pendingNativePoseRequests = new HashSet<uint>();
        private readonly Dictionary<Vehicle_Type, uint> grantedGenerations = new Dictionary<Vehicle_Type, uint>();
        private readonly HashSet<Vehicle_Type> drivenVehicles = new HashSet<Vehicle_Type>();
        private WorldChannel? attachedChannel;
        private int phase, ack, reply, drivePhase, driveIndex, truckDoorPhase;
        private Vehicle_Type drivingType = Vehicle_Type.SmollATV;
        private Vector3 driveStart;
        private float phaseAt, lastActionAt, nextEnterAttemptAt, nextTruckDoorActionAt, nextNativePoseRequestAt, truckDoorDeadline;
        private int priorTimestamp;
        private Guid guestProfile;
        private bool entryDiagnosticsEnabled, hostReloadValidated;

        internal string Status => "guest vehicle save phase=" + phase + "; drive=" + drivingType + "/" + drivePhase +
            "; door=" + truckDoorPhase + "; ack=" + ack + "; reply=" + reply + "; save=" + plugin.World?.SaveRounds?.Phase +
            "; grants=" + string.Join(",", grantedGenerations.Select(value => value.Key + ":" + value.Value).ToArray()) +
            "; driven=" + string.Join(",", drivenVehicles.OrderBy(type => type).ToArray()) +
            "; " + VehicleDiagnostics +
            "; entryTrace=" + (plugin.Vehicles?.TestDriverEntryDiagnostic ?? "no shared vehicle world");

        private string VehicleDiagnostics
        {
            get
            {
                var shared = plugin.Vehicles;
                if (shared == null) return "vehicles=missing";
                if (!shared.Ready) return "vehicles=not-ready";
                var priorType = shared.TestType;
                try
                {
                    return "vehicles=" + string.Join(" || ", Types.Select(type =>
                    {
                        try
                        {
                            shared.TestType = type;
                            var state = shared.TestState;
                            var vehicle = shared.TestVehicle;
                            var position = vehicle.transform.position;
                            string grant = grantedGenerations.TryGetValue(type, out uint value) ? value.ToString() : "-";
                            string origin = baseline.TryGetValue(type, out VehicleState start)
                                ? "(" + start.Pose.X.ToString("F2") + "," + start.Pose.Y.ToString("F2") + "," + start.Pose.Z.ToString("F2") + ")"
                                : "-";
                            return type + "{owner=" + state.Driver.Owner + ",generation=" + state.Driver.Generation +
                                ",grantGeneration=" + grant + ",driven=" + drivenVehicles.Contains(type) +
                                ",pose=(" + state.Pose.X.ToString("F2") + "," + state.Pose.Y.ToString("F2") + "," + state.Pose.Z.ToString("F2") +
                                "),nativePose=(" + position.x.ToString("F2") + "," + position.y.ToString("F2") + "," + position.z.ToString("F2") +
                                "),baseline=" + origin + ",engine=" + vehicle.isEngineRunning + ",ignition=" + vehicle.Get_IgnitionPosition +
                                ",rpm=" + vehicle.powertrain.engine.RPM.ToString("F0") + ",throttle=" + vehicle.input.Throttle.ToString("F2") +
                                ",mode=" + vehicle.VehicleMultiplayerInstanceType + ",kinematic=" + vehicle.vehicleRigidbody.isKinematic + "}";
                        }
                        catch (Exception exception)
                        {
                            return type + "{diagnosticError=" + exception.GetType().Name + ":" + exception.Message + "}";
                        }
                    }).ToArray());
                }
                finally { shared.TestType = priorType; }
            }
        }

        internal static void Prepare(bool host)
        {
            if (!host) return;
            LocalTestIsolation.SetString("SaveName", "MonMPGuestCar-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            LocalTestIsolation.SetInt("SaveTimestamp", -1);
        }

        internal PairedGuestVehicleSaveTest(MonMPPlugin plugin)
        {
            this.plugin = plugin;
            host = plugin.TestOptions.IsHost;
            saveName = NativeSaveWriter.SelectedSave;
            if (host && !saveName.StartsWith("MonMPGuestVehicleSave-", StringComparison.Ordinal))
                throw new InvalidOperationException("Guest vehicle save fixture did not select its isolated save slot.");
        }

        internal bool Tick(Gameplay? gameplay)
        {
            AttachChannel();
            if (Ready && !entryDiagnosticsEnabled)
            {
                plugin.Vehicles!.TestEnableDriverEntryDiagnostics();
                entryDiagnosticsEnabled = true;
            }
            if (host) return TickHost(gameplay);
            return TickGuest(gameplay);
        }

        private void AttachChannel()
        {
            var channel = plugin.World?.Channel;
            if (ReferenceEquals(channel, attachedChannel)) return;
            if (attachedChannel != null)
            {
                attachedChannel.CommandRequested -= OnCommand;
                attachedChannel.ReplyReceived -= OnReply;
            }
            attachedChannel = channel;
            ack = reply = 0;
            if (attachedChannel == null) return;
            attachedChannel.ReplyReceived += OnReply;
            if (host) attachedChannel.CommandRequested += OnCommand;
        }

        private void OnCommand(uint peer, ulong entity, byte[] payload)
        {
            if (peer != 0 && entity == ClockState.Entity && payload.Length == 1 && payload[0] == NativePoseRequestMarker)
            {
                if (hostExitSavedPoses.Count != Types.Length)
                    throw new InvalidOperationException("Guest vehicle save: received a native-pose request before host-exit DTO capture.");
                if (hostReloadValidated) ReplyNativeSavedPoses(peer);
                else pendingNativePoseRequests.Add(peer);
            }
            else if (peer != 0 && entity == ProfileRestoreChannel.Entity && payload.Length == 1)
                ack = Math.Max(ack, payload[0]);
        }

        private void OnReply(ulong entity, byte[] payload)
        {
            if (entity == ClockState.Entity && payload.Length > 1 && payload[0] == NativePoseReplyMarker)
                CaptureNativeSavedPoses(payload);
            else if (entity == ClockState.Entity && payload.Length == 1)
                reply = Math.Max(reply, payload[0]);
        }

        private void Signal(byte value)
        {
            if (plugin.World?.Channel.FullySynchronized == true)
                plugin.World.Channel.Request(ProfileRestoreChannel.Entity, new[] { value });
        }

        private void Reply(uint peer, byte value) =>
            plugin.World!.Channel.Reply(peer, ClockState.Entity, new[] { value });

        private void ReplyNativeSavedPoses(uint peer) =>
            plugin.World!.Channel.Reply(peer, ClockState.Entity, EncodeNativeSavedPoses());

        private void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Guest vehicle save: " + message + "; " + Status);
        }

        private bool Ready => plugin.World?.Channel.FullySynchronized == true &&
            plugin.World.PersonalProfileReady && plugin.Vehicles?.Ready == true;

        private void CaptureBaselines(SharedVehicles shared)
        {
            if (baseline.Count != 0) return;
            foreach (var type in Types)
            {
                shared.TestType = type;
                baseline.Add(type, shared.TestState);
            }
        }

        private bool TickHost(Gameplay? gameplay)
        {
            if (phase == 0)
            {
                if (!Ready) return false;
                if (NativeSaveWriter.SelectedSave != saveName || NativeSaveWriter.SelectedTimestamp != -1)
                    throw new InvalidOperationException("Guest vehicle save fixture lost its isolated new-world selection.");
                CaptureBaselines(plugin.Vehicles!);
                phase = 1;
                return false;
            }

            if (phase == 1 && ack >= 1)
            {
                var shared = plugin.Vehicles!;
                guestProfile = plugin.Session!.Peers.Values.Single(peer => peer.Id != 0).ProfileId;
                foreach (var type in Types)
                    expected[type] = VerifyDriven(shared, type, ItemLease.Unowned);
                priorTimestamp = SaveSystem.Get_LastSaveTimestamp;
                SaveSystem.Save(House_Location.SpecialMenu);
                Require(plugin.World!.Saves!.Pending && SaveSystem.Get_LastSaveTimestamp == priorTimestamp,
                    "Native save bypassed the guest vehicle ownership flush.");
                phase = 2;
                phaseAt = Time.unscaledTime;
                return false;
            }

            if (phase == 2)
            {
                var world = plugin.World;
                if (world?.SaveRounds?.Phase == SavePhase.Failed)
                    throw new InvalidOperationException("Coordinated vehicle save failed: " + world.SaveRounds.LocalFailure);
                if (world?.SaveRounds?.Phase != SavePhase.Complete || world.Saves?.Pending == true) return false;
                var shared = plugin.Vehicles!;
                Require(plugin.Session!.Peers.Count == 2, "Guest disconnected before the coordinated vehicle write finished.");
                Require(!shared.IsDriverOwner(plugin.Session.Peers.Values.Single(peer => peer.Id != 0).Id) &&
                    gameplay?.Get_CurrentVehicleController == null && gameplay?.Get_PlayerState == PlayerState.Walking,
                    "Post-exit save barrier did not leave the guest walking with no vehicle lease before the native write.");
                foreach (var type in Types)
                {
                    shared.TestType = type;
                    var state = shared.TestState;
                    Require(state.Driver.Owner == ItemLease.Unowned, type + " retained a driver owner in the completed save.");
                    Require(state.Driver.Generation == expected[type].Driver.Generation,
                        type + " lease generation changed while the post-exit save was being flushed.");
                    RequireMovedAndRunning(type, state);
                    RequireStateMatches(type, expected[type], state, "post-exit coordinated save");
                }
                var saved = SidecarStore.Load(world.Saves!.LastSavedDirectory, SessionOptions.SupportedGameHash)
                    ?? throw new InvalidOperationException("Native vehicle save did not publish its sidecar.");
                Require(saved.Document.Profiles.ContainsKey(guestProfile), "Coordinated save omitted the connected guest profile.");
                VerifyNativeVehicleRecords();
                if (ack < 2) return false;
                uint guest = plugin.Session.Peers.Values.Single(peer => peer.Id != 0).Id;
                Reply(guest, 3);
                phase = 3;
                phaseAt = Time.unscaledTime;
                return false;
            }

            if (phase == 3 && plugin.Session?.Peers.Count == 1)
            {
                UnityEngine.Object.FindObjectOfType<EscapeMenuV2>().Button_QuitGameConfirmation();
                Require(plugin.HostExitPending && plugin.Session.IsRunning,
                    "Host exit did not wait for the coordinated native save after guest departure.");
                phase = 4;
                phaseAt = Time.unscaledTime;
                return false;
            }

            if (phase == 4 && gameplay == null && UnityEngine.Object.FindObjectOfType<MainMenu_Manager>() != null)
            {
                priorTimestamp = NativeSaveWriter.SelectedTimestamp;
                Require(priorTimestamp > 0 && SaveSystem.Get_LastSaveTimestamp == priorTimestamp,
                    "Host exit did not publish and select a native save generation.");
                VerifyNativeVehicleRecords();
                CaptureHostExitSavedPoses(priorTimestamp);
                var directory = Path.Combine(LocalTestIsolation.SaveRoot, saveName,
                    priorTimestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var saved = SidecarStore.Load(directory, SessionOptions.SupportedGameHash)
                    ?? throw new InvalidOperationException("Host exit save did not publish the sidecar generation.");
                Require(saved.Document.Profiles.ContainsKey(guestProfile), "Host exit save lost the guest's saved profile.");
                UnityEngine.Object.FindObjectOfType<MainMenu_Manager>().StartGame(false);
                phase = 5;
                phaseAt = Time.unscaledTime;
                return false;
            }

            // Gameplay.Player appears before SaveSystem.LoadAfterEverythingMoved finishes
            // restoring the native save. Starting a session in that window snapshots an
            // incomplete vehicle world and NativeLoadGate correctly rejects it.
            if (phase == 5 && gameplay?.Player != null && !gameplay.Get_isGamePaused &&
                MonMP.Plugin.Patches.NativeLoadGate.Ready)
            {
                plugin.BeginPairedTestSession();
                Require(plugin.Session?.IsHost == true && plugin.World != null, "Hosting the native reloaded vehicle save failed.");
                phase = 6;
                return false;
            }

            if (phase == 6 && Ready)
            {
                var shared = plugin.Vehicles!;
                VerifyReloadedStates(shared);
                hostReloadValidated = true;
                foreach (uint peer in pendingNativePoseRequests)
                    ReplyNativeSavedPoses(peer);
                pendingNativePoseRequests.Clear();
                phase = 7;
                return false;
            }

            if (phase == 7 && ack >= 4)
            {
                uint guest = plugin.Session!.Peers.Values.Single(peer => peer.Id != 0).Id;
                Reply(guest, 5);
                phase = 8;
                phaseAt = Time.unscaledTime;
                return false;
            }

            // The guest acknowledges the final result over the same reliable
            // channel before the host closes it. Ending immediately after Reply
            // can discard that last response while the world channel is disposed.
            if (phase == 8 && ack >= 5)
            {
                plugin.EndPairedTestSession();
                return true;
            }

            if (Time.unscaledTime - phaseAt > 180f && phase >= 2 && phase <= 5 ||
                phase == 8 && Time.unscaledTime - phaseAt > 20f)
                throw new InvalidOperationException("Vehicle save/reload fixture stalled: " + Status);
            return false;
        }

        private bool TickGuest(Gameplay? gameplay)
        {
            if (phase == 0)
            {
                Require(plugin.Session?.IsRunning == true && plugin.World != null,
                    "Guest vehicle fixture was created before the paired guest session started.");
                phase = 1;
                return false;
            }

            if (phase == 1 && Ready)
            {
                CaptureBaselines(plugin.Vehicles!);
                driveIndex = 0;
                BeginDrive(gameplay!, Vehicle_Type.SmollATV);
                phase = 2;
                return false;
            }

            if (phase == 2)
            {
                if (driveIndex == 0)
                {
                    if (!DriveTick(gameplay!)) return false;
                    driveIndex = 1;
                }
                if (driveIndex == 1)
                {
                    if (!PrepareTruckDriverDoor(gameplay!, plugin.Vehicles!)) return false;
                    BeginDrive(gameplay!, Vehicle_Type.OlTruck);
                    driveIndex = 2;
                    return false;
                }
                if (driveIndex == 2)
                {
                    if (!DriveTick(gameplay!)) return false;
                    driveIndex = 3;
                }
                Require(grantedGenerations.Count == Types.Length && drivenVehicles.Count == Types.Length && expected.Count == Types.Length,
                    "Both guest vehicle leases were not acquired, driven, and released before the save request.");
                Signal(1);
                phase = 3;
                phaseAt = Time.unscaledTime;
                return false;
            }

            if (phase == 3)
            {
                var world = plugin.World;
                if (world?.SaveRounds?.Phase == SavePhase.Complete)
                {
                    var shared = plugin.Vehicles!;
                    Require(gameplay?.Get_CurrentVehicleController == null && gameplay?.Get_PlayerState == PlayerState.Walking,
                        "Save round did not return the guest to walking.");
                    Require(!shared.IsDriverOwner(plugin.Session!.LocalPeerId), "Guest still owns a vehicle after the save barrier.");
                    foreach (var type in Types)
                    {
                        shared.TestType = type;
                        var state = shared.TestState;
                        Require(state.Driver.Owner == ItemLease.Unowned, type + " has a driver after the save barrier.");
                        Require(state.Driver.Generation == expected[type].Driver.Generation,
                            type + " lease generation changed while the post-exit save was being flushed.");
                        RequireMovedAndRunning(type, state);
                        RequireStateMatches(type, expected[type], state, "post-exit coordinated save");
                    }
                    if (Time.unscaledTime - lastActionAt >= 0.5f)
                    { Signal(2); lastActionAt = Time.unscaledTime; }
                }
                else if (world?.SaveRounds?.Phase == SavePhase.Failed)
                    throw new InvalidOperationException("Host's coordinated native vehicle save failed: " + world.SaveRounds.LocalFailure);
                if (reply >= 3)
                {
                    DetachChannel();
                    plugin.EndPairedTestSession();
                    Require(gameplay?.Player != null, "Guest world vanished before returning to the menu.");
                    UnityEngine.Object.FindObjectOfType<EscapeMenuV2>().Button_QuitGameConfirmation();
                    phase = 4;
                    phaseAt = Time.unscaledTime;
                    return false;
                }
                if (Time.unscaledTime - phaseAt > 150f)
                    throw new InvalidOperationException("Guest did not complete the coordinated vehicle save: " + Status);
                return false;
            }

            if (phase == 4 && gameplay == null && UnityEngine.Object.FindObjectOfType<MainMenu_Manager>() != null)
            {
                if (!plugin.DiscoverPairedHost()) return false;
                plugin.BeginPairedTestSession();
                phase = 5;
                phaseAt = Time.unscaledTime;
                return false;
            }

            if (phase == 5 && plugin.GuestWorld?.Activated == true && Ready)
            {
                Require(plugin.World!.Channel.FullySynchronized, "Guest activated before the reloaded host snapshot arrived.");
                if (hostExitSavedPoses.Count != Types.Length && Time.unscaledTime >= nextNativePoseRequestAt)
                {
                    plugin.World.Channel.Request(ClockState.Entity, new[] { NativePoseRequestMarker });
                    nextNativePoseRequestAt = Time.unscaledTime + 0.5f;
                }
                if (hostExitSavedPoses.Count == Types.Length)
                {
                    VerifyReloadedStates(plugin.Vehicles!);
                    phase = 6;
                    lastActionAt = 0;
                    return false;
                }
            }

            if (phase == 6)
            {
                if (reply >= 5)
                {
                    Signal(5);
                    phase = 7;
                    phaseAt = Time.unscaledTime;
                    return false;
                }
                if (Time.unscaledTime - lastActionAt >= 0.5f)
                { Signal(4); lastActionAt = Time.unscaledTime; }
            }

            if (phase == 7 && plugin.Session?.IsRunning != true)
            {
                DetachChannel();
                return true;
            }

            if (phase == 4 || phase == 5 || phase == 7)
            {
                if (Time.unscaledTime - phaseAt > (phase == 7 ? 20f : 180f))
                    throw new InvalidOperationException("Guest reconnect did not load the host's native vehicle save: " + Status);
            }
            return false;
        }

        private bool PrepareTruckDriverDoor(Gameplay gameplay, SharedVehicles shared)
        {
            shared.TestType = Vehicle_Type.OlTruck;
            var doors = shared.Doors;
            var truck = shared.TestVehicle;
            if (truckDoorPhase == 0)
            {
                truckDoorDeadline = Time.unscaledTime + 20f;
                if (doors.TestState().Open)
                {
                    if (!truck.isDriverDoorClosed && doors.TestSynchronized)
                    {
                        truckDoorPhase = 3;
                        return true;
                    }
                    // The host may already have an open native door; wait for its
                    // synchronized pose instead of issuing a second toggle.
                    truckDoorPhase = 2;
                    return false;
                }
                MovePlayer(gameplay, doors.TestPosition + Vector3.right * 1.5f);
                truckDoorPhase = 1;
                nextTruckDoorActionAt = Time.unscaledTime + 0.7f;
                return false;
            }

            if (Time.unscaledTime >= truckDoorDeadline)
            {
                float handleDistance = Vector3.Distance(gameplay.PlayerWalking.transform.position, doors.TestPosition);
                throw new InvalidOperationException("Guest vehicle save: truck driver door did not open; doorOpen=" +
                    doors.TestState().Open + ", driverDoorClosed=" + truck.isDriverDoorClosed +
                    ", synchronized=" + doors.TestSynchronized + ", handleDistance=" + handleDistance.ToString("F2") + "m; " + Status + ".");
            }

            if (truckDoorPhase == 1 && Time.unscaledTime >= nextTruckDoorActionAt)
            {
                doors.TestClick();
                truckDoorPhase = 2;
                nextTruckDoorActionAt = Time.unscaledTime + 0.8f;
            }
            else if (truckDoorPhase == 2 && doors.TestState().Open && !truck.isDriverDoorClosed && doors.TestSynchronized)
            {
                truckDoorPhase = 3;
                return true;
            }
            else if (truckDoorPhase == 2 && !doors.TestState().Open && Time.unscaledTime >= nextTruckDoorActionAt)
            {
                doors.TestClick();
                nextTruckDoorActionAt = Time.unscaledTime + 0.8f;
            }
            return false;
        }
        private void BeginDrive(Gameplay gameplay, Vehicle_Type type)
        {
            var shared = plugin.Vehicles!;
            shared.TestType = type;
            drivingType = type;
            drivePhase = 0;
            phaseAt = Time.unscaledTime;
            lastActionAt = 0;
            driveStart = shared.TestVehicle.transform.position;
            var vehicle = shared.TestVehicle;
            Require(shared.TestState.Driver.Owner == ItemLease.Unowned && vehicle.vehicleRigidbody.isKinematic &&
                vehicle.VehicleMultiplayerInstanceType == Vehicle.MultiplayerInstanceType.Remote,
                type + " did not start as an unowned host replica.");
            MovePlayer(gameplay, shared.TestSeat + Vector3.right * 1.5f);
            drivePhase = 1;
            // Give the newly moved walking pose time to publish before the first lease
            // request. Keep retrying at a bounded rate until the host grants ownership.
            nextEnterAttemptAt = Time.unscaledTime + 0.7f;
        }

        private bool DriveTick(Gameplay gameplay)
        {
            var shared = plugin.Vehicles!;
            shared.TestType = drivingType;
            var vehicle = shared.TestVehicle;
            if (Time.unscaledTime - phaseAt > DriveTimeout)
            {
                float seatDistance = Vector3.Distance(gameplay.PlayerWalking.transform.position, shared.TestSeat);
                throw new InvalidOperationException("Guest could not drive " + drivingType + ": " + Describe(vehicle, shared) +
                    " localPeer=" + plugin.Session!.LocalPeerId + ", playerState=" + gameplay.Get_PlayerState +
                    ", seatDistance=" + seatDistance.ToString("F2") + "m, entryTrace=" + shared.TestDriverEntryDiagnostic + "; " + Status + ".");
            }

            if (drivePhase == 1)
            {
                if (shared.TestState.Driver.Owner != plugin.Session!.LocalPeerId)
                {
                    if (shared.TestState.Driver.Owner == ItemLease.Unowned && Time.unscaledTime >= nextEnterAttemptAt)
                    {
                        shared.TestEnter();
                        nextEnterAttemptAt = Time.unscaledTime + 0.8f;
                    }
                    return false;
                }
                Require(gameplay.Get_CurrentVehicleController == vehicle && !vehicle.vehicleRigidbody.isKinematic &&
                    vehicle.VehicleMultiplayerInstanceType == Vehicle.MultiplayerInstanceType.Local,
                    drivingType + " lease did not restore native driver simulation.");
                uint expectedGrantGeneration = NextGeneration(baseline[drivingType].Driver.Generation);
                Require(shared.TestState.Driver.Generation == expectedGrantGeneration,
                    drivingType + " grant did not advance exactly one lease generation.");
                grantedGenerations[drivingType] = shared.TestState.Driver.Generation;
                vehicle.input.autoSetInput = false;
                vehicle.input.Throttle = 0;
                vehicle.input.Brakes = 0;
                vehicle.input.Handbrake = 0;
                if (vehicle.vehicle_PartsManager.ParkingBrake != null) vehicle.vehicle_PartsManager.ParkingBrake.LoadFromSave = 0;
                vehicle.brakes.handbrakeValue = 0;
                vehicle.input.states.clutch = 1;
                vehicle.powertrain.transmission.ShiftInto(0, true);
                vehicle.Get_IgnitionPosition = 2;
                drivePhase = 2;
                return false;
            }

            if (drivePhase == 2)
            {
                if (!vehicle.isEngineRunning) return false;
                vehicle.Get_IgnitionPosition = 1;
                vehicle.powertrain.transmission.ShiftInto(1, true);
                vehicle.input.Throttle = 0.6f;
                drivePhase = 3;
                return false;
            }

            if (drivePhase == 3)
            {
                var state = shared.TestState;
                bool moved = Vector3.Distance(vehicle.transform.position, driveStart) > 0.3f;
                bool running = state.EngineIgnition && state.EngineAngularVelocity > 1f && state.Throttle > 0.3f;
                if (!moved || !running) return false;
                drivenVehicles.Add(drivingType);
                vehicle.input.Throttle = 0;
                vehicle.input.Brakes = 1;
                vehicle.input.Handbrake = 1;
                vehicle.brakes.handbrakeValue = 1;
                vehicle.Get_IgnitionPosition = 1;
                vehicle.input.autoSetInput = true;
                shared.TestExit();
                drivePhase = 4;
                return false;
            }

            if (drivePhase == 4 && shared.TestState.Driver.Owner == ItemLease.Unowned)
            {
                Require(gameplay.Get_PlayerState == PlayerState.Walking && gameplay.Get_CurrentVehicleController == null &&
                    vehicle.vehicleRigidbody.isKinematic, drivingType + " release did not restore walking and remote mode.");
                Require(!shared.IsDriverOwner(plugin.Session!.LocalPeerId), "Guest acquired duplicate vehicle ownership.");
                var state = shared.TestState;
                Require(state.Driver.Generation == NextGeneration(grantedGenerations[drivingType]),
                    drivingType + " release did not advance exactly one lease generation after its guest grant.");
                RequireMovedAndRunning(drivingType, state);
                expected[drivingType] = state;
                return true;
            }
            return false;
        }

        private VehicleState VerifyDriven(SharedVehicles shared, Vehicle_Type type, uint owner)
        {
            shared.TestType = type;
            var state = shared.TestState;
            Require(state.Driver.Owner == owner, type + " lease owner did not match the expected driver.");
            Require(state.Driver.Generation == NextGeneration(NextGeneration(baseline[type].Driver.Generation)),
                type + " did not replicate one guest grant and one guest release before the save request.");
            RequireMovedAndRunning(type, state);
            Require(PoseMoved(baseline[type], state), type + " pose did not retain the guest's drive.");
            var vehicle = shared.TestVehicle;
            Require(vehicle.VehicleMultiplayerInstanceType == Vehicle.MultiplayerInstanceType.Local && !vehicle.vehicleRigidbody.isKinematic,
                "Guest-released " + type + " did not restore host-local vehicle simulation.");
            return state;
        }

        private void VerifyReloadedStates(SharedVehicles shared)
        {
            Require(expected.Count == Types.Length, "Fixture has no captured post-drive vehicle states.");
            Require(hostExitSavedPoses.Count == Types.Length, "Fixture has no captured host-exit native vehicle DTO poses.");
            foreach (var type in Types)
            {
                shared.TestType = type;
                var actual = shared.TestState;
                Require(actual.Driver.Owner == ItemLease.Unowned,
                    type + " retained a driver owner after native reload/reconnect.");
                Require(!shared.IsDriverOwner(plugin.Session!.LocalPeerId), "Reconnect assigned the guest a duplicate driver lease.");
                RequireNativePoseMatches(type, hostExitSavedPoses[type], actual, "native reload/reconnect");
                RequireReloadedEngineMatches(type, expected[type], hostExitSavedPoses[type], actual);
                Require(PoseMoved(baseline[type], actual), type + " reloaded at its pre-drive pose.");
                RequireMovedAndRunning(type, actual);
            }
        }

        private void RequireNativePoseMatches(Vehicle_Type type, NativeSavedPose saved, VehicleState actual, string stage)
        {
            float positionError = Vector3.Distance(saved.Position, Position(actual.Pose));
            float angleError = Quaternion.Angle(Quaternion.Euler(saved.EulerAngles), Rotation(actual.Pose));
            Require(positionError <= ReloadPositionTolerance && angleError <= ReloadRotationTolerance,
                type + " pose changed from host-exit native DTO across " + stage + " (position error=" +
                positionError.ToString("0.000") + "m, rotation error=" + angleError.ToString("0.00") + "deg, saved=" +
                Describe(saved) + ", actualPosition=" + Position(actual.Pose).ToString("F3") +
                ", actualRotation=" + Rotation(actual.Pose).eulerAngles.ToString("F2") + ").");
        }

        private void RequireReloadedEngineMatches(Vehicle_Type type, VehicleState priorExpected, NativeSavedPose saved, VehicleState actual)
        {
            Require(actual.EngineIgnition && actual.EngineAngularVelocity > 1f && actual.Ignition > 0,
                type + " lost its running engine across native reload/reconnect.");
            Require(actual.EngineIgnition == saved.EngineRunning && actual.Ignition == saved.Ignition && actual.Gear == saved.Gear,
                type + " engine, ignition, or gear differs from the host-exit native DTO (saved=" + Describe(saved) +
                ", actualEngine=" + actual.EngineIgnition + ", actualIgnition=" + actual.Ignition + ", actualGear=" + actual.Gear + ").");
            Require(Math.Abs(actual.EngineAngularVelocity - priorExpected.EngineAngularVelocity) <=
                Math.Max(300f, priorExpected.EngineAngularVelocity * 0.75f),
                type + " RPM changed beyond tolerance across native reload/reconnect.");
        }

        private void RequireStateMatches(Vehicle_Type type, VehicleState expectedState, VehicleState actual, string stage)
        {
            float positionError = Vector3.Distance(Position(expectedState.Pose), Position(actual.Pose));
            float angleError = Quaternion.Angle(Rotation(expectedState.Pose), Rotation(actual.Pose));
            Require(positionError <= 1.25f && angleError <= 18f,
                type + " pose changed across " + stage + " (position error=" + positionError.ToString("0.00") + "m, rotation error=" + angleError.ToString("0.0") + "deg).");
            RequireEngineStateMatches(type, expectedState, actual, stage);
        }

        private void RequireEngineStateMatches(Vehicle_Type type, VehicleState expectedState, VehicleState actual, string stage)
        {
            Require(actual.EngineIgnition && actual.EngineAngularVelocity > 1f && actual.Ignition > 0,
                type + " lost its running engine across " + stage + ".");
            Require(Math.Abs(actual.EngineAngularVelocity - expectedState.EngineAngularVelocity) <=
                Math.Max(300f, expectedState.EngineAngularVelocity * 0.75f),
                type + " RPM changed beyond tolerance across " + stage + ".");
        }

        private void VerifyNativeVehicleRecords()
        {
            foreach (var type in Types)
                Require(SaveSystem.GetThisVehicleFromLastSave(type) != null,
                    "Native braaaap save has no " + type + " record.");
        }

        private void CaptureHostExitSavedPoses(int timestamp)
        {
            var lastSave = SaveSystem.GetLastSaveData();
            Require(lastSave != null && lastSave.SaveName == saveName && lastSave.TimeStamp == timestamp,
                "Latest native save does not match the host-exit save slot and generation.");
            hostExitSavedPoses.Clear();
            foreach (var type in Types)
            {
                var data = SaveSystem.GetThisVehicleFromLastSave(type);
                if (data == null || data.id != type.ToString())
                    throw new InvalidOperationException("Host-exit native save has no matching " + type + " DTO; " + Status);
                var saved = new NativeSavedPose(data);
                Require(IsFinite(saved.Position) && IsFinite(saved.EulerAngles),
                    type + " host-exit native DTO has a non-finite pose.");
                Require(Vector3.Distance(Position(baseline[type].Pose), saved.Position) > 0.3f,
                    type + " host-exit native DTO reverted to its pre-drive baseline (saved=" + Describe(saved) + ").");
                Require(saved.EngineRunning && saved.Ignition > 0,
                    type + " host-exit native DTO lost its running engine (saved=" + Describe(saved) + ").");
                hostExitSavedPoses.Add(type, saved);
            }
        }

        private void CaptureNativeSavedPoses(byte[] payload)
        {
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadByte() != NativePoseReplyMarker || reader.ReadByte() != NativePoseReplyVersion ||
                reader.ReadByte() != Types.Length)
                throw new InvalidDataException("Invalid host-exit vehicle DTO pose reply header.");

            var received = new Dictionary<Vehicle_Type, NativeSavedPose>();
            foreach (var expectedType in Types)
            {
                var type = (Vehicle_Type)reader.ReadInt32();
                if (type != expectedType) throw new InvalidDataException("Host-exit vehicle DTO pose reply order/type mismatch.");
                var saved = new NativeSavedPose(
                    ReadVector(reader, "position"), ReadVector(reader, "rotation"), reader.ReadBoolean(), reader.ReadInt32(), reader.ReadInt32());
                if (!IsFinite(saved.Position) || !IsFinite(saved.EulerAngles))
                    throw new InvalidDataException(type + " host-exit vehicle DTO pose reply is non-finite.");
                received.Add(type, saved);
            }
            if (stream.Position != stream.Length) throw new InvalidDataException("Trailing bytes in host-exit vehicle DTO pose reply.");

            foreach (var type in Types)
            {
                var saved = received[type];
                Require(Vector3.Distance(Position(baseline[type].Pose), saved.Position) > 0.3f,
                    type + " host-exit native DTO reverted to its pre-drive baseline (saved=" + Describe(saved) + ").");
                Require(saved.EngineRunning && saved.Ignition > 0,
                    type + " host-exit native DTO lost its running engine (saved=" + Describe(saved) + ").");
                hostExitSavedPoses[type] = saved;
            }
        }

        private byte[] EncodeNativeSavedPoses()
        {
            if (hostExitSavedPoses.Count != Types.Length)
                throw new InvalidOperationException("Cannot send incomplete host-exit vehicle DTO poses.");
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(NativePoseReplyMarker);
            writer.Write(NativePoseReplyVersion);
            writer.Write((byte)Types.Length);
            foreach (var type in Types)
            {
                var saved = hostExitSavedPoses[type];
                writer.Write((int)type);
                writer.Write(saved.Position.x); writer.Write(saved.Position.y); writer.Write(saved.Position.z);
                writer.Write(saved.EulerAngles.x); writer.Write(saved.EulerAngles.y); writer.Write(saved.EulerAngles.z);
                writer.Write(saved.EngineRunning); writer.Write(saved.Ignition); writer.Write(saved.Gear);
            }
            return stream.ToArray();
        }

        private static Vector3 ReadVector(float[] values, string label)
        {
            if (values == null || values.Length != 3)
                throw new InvalidDataException("Native vehicle DTO has invalid " + label + " vector length.");
            var result = new Vector3(values[0], values[1], values[2]);
            if (!IsFinite(result)) throw new InvalidDataException("Native vehicle DTO has a non-finite " + label + " vector.");
            return result;
        }

        private static Vector3 ReadVector(BinaryReader reader, string label)
        {
            var result = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (!IsFinite(result)) throw new InvalidDataException("Native vehicle DTO has a non-finite " + label + " vector.");
            return result;
        }

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        private static string Describe(NativeSavedPose saved) =>
            "position=" + saved.Position.ToString("F3") + ",euler=" + saved.EulerAngles.ToString("F2") +
            ",engine=" + saved.EngineRunning + ",ignition=" + saved.Ignition + ",gear=" + saved.Gear;

        private void RequireMovedAndRunning(Vehicle_Type type, VehicleState state)
        {
            Require(state.EngineIgnition && state.EngineAngularVelocity > 1f && state.Ignition > 0,
                type + " engine ignition/RPM was lost during ownership handoff or persistence.");
            Require(PoseMoved(baseline[type], state), type + " pose did not move from its native baseline.");
        }

        private static uint NextGeneration(uint generation) => generation == uint.MaxValue ? 1 : generation + 1;

        private static bool PoseMoved(VehicleState from, VehicleState to) =>
            Vector3.Distance(Position(from.Pose), Position(to.Pose)) > 0.3f;

        private static Vector3 Position(ItemPose pose) => new Vector3(pose.X, pose.Y, pose.Z);
        private static Quaternion Rotation(ItemPose pose) => new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw);

        private void DetachChannel()
        {
            if (attachedChannel == null) return;
            attachedChannel.CommandRequested -= OnCommand;
            attachedChannel.ReplyReceived -= OnReply;
            attachedChannel = null;
        }

        private static void MovePlayer(Gameplay gameplay, Vector3 position)
        {
            gameplay.PlayerWalking.transform.position = position;
            var body = gameplay.Get_Player_Rigidbody;
            if (body != null) { body.position = position; body.velocity = Vector3.zero; }
        }

        private static string Describe(VehicleController vehicle, SharedVehicles shared) =>
            "owner=" + shared.TestState.Driver.Owner + ", running=" + vehicle.isEngineRunning +
            ", ignition=" + vehicle.Get_IgnitionPosition + ", rpm=" + vehicle.powertrain.engine.RPM +
            ", throttle=" + vehicle.input.Throttle + ", mode=" + vehicle.VehicleMultiplayerInstanceType +
            ", kinematic=" + vehicle.vehicleRigidbody.isKinematic + ".";
    }
}
