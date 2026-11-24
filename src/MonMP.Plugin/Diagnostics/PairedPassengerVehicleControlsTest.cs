using System;
using System.Linq;
using System.Text;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using NWH.VehiclePhysics2.Powertrain;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Exercises guest-passenger/host-driver and host-passenger/guest-driver without transferring
    // physics authority for a control click. Requires native stock Konig seats and automatic Park.
    internal sealed class PairedPassengerVehicleControlsTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedVehicles vehicles;
        private readonly SharedPassengerControls controls;
        private int phase, domeBefore;
        private float next, deadline;
        private uint guest, driverGeneration;
        private readonly Vector3 originalPlayerPosition, originalPlayerBodyPosition, originalPlayerVelocity, originalPlayerAngularVelocity;
        private readonly Quaternion originalPlayerRotation, originalBodyRotation;
        private readonly Action restoreVehicle;
        private readonly Vehicle_Door[] fixtureDoors;
        private readonly Vector3[] originalDoorPositions;
        private readonly Quaternion[] originalDoorRotations;
        private bool cleaned;
        private readonly bool originalDriverDoorClosed, originalPassengerDoorClosed;
        private readonly ulong[] switchSources;
        private readonly int[] initialSwitchPosted, initialSwitchPlayed;
        private readonly int initialGlobalPosted, initialGlobalPlayed;
        private readonly byte initialHazard;
        private int repairsBefore;
        private int[] repairPosted = Array.Empty<int>(), repairPlayed = Array.Empty<int>();
        private const byte FixtureReadyMarker = 243;
        private readonly WorldChannel fixtureChannel;
        private uint fixtureReadyPeer;
        private bool fixtureReadyAcknowledged;
        private bool guestDriverConfirmed;
        private float nextReadySignal;
        private float firstGearAt, nextPoseEchoSignal;
        private bool freshPassengerPoseEcho, hostRejectedFirstGear;
        private string hostRejectionEcho = "";
        internal string Status => "Passenger controls phase=" + phase + "; driver=" + vehicles.TestKonigState.Driver.Owner +
            "/" + vehicles.TestKonigState.Driver.Generation + "; passenger=" + vehicles.TestKonigPassengerState.Owner +
            "; ignition=" + vehicles.TestKonigState.Ignition + "; gear=" + vehicles.TestKonigState.Gear +
            "; queue=" + controls.Pending + "; accepted/applied/ack/rejected/release=" + controls.Accepted + "/" + controls.Applied +
            "/" + controls.Acknowledged + "/" + controls.Rejected + "/" + controls.ForcedReleases +
            "; switchPosted=" + string.Join(",", SwitchPosted()) + "; switchPlayed=" + string.Join(",", SwitchPlayed()) +
            "; fixtureReady=" + fixtureReadyPeer + "/" + fixtureReadyAcknowledged + "; deferred=" + controls.DeferredReceived + "/" + controls.DeferredPromoted + "/" + controls.DeferredDropped +
            "; poseEcho=" + freshPassengerPoseEcho + "; lastReject=" + (plugin.TestOptions.IsHost ? controls.LastRejection : hostRejectionEcho);
        internal PairedPassengerVehicleControlsTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay; vehicles = plugin.Vehicles!; controls = plugin.World!.PassengerControls;
            originalPlayerPosition = gameplay.PlayerWalking.transform.position;
            originalPlayerBodyPosition = gameplay.Get_Player_Rigidbody.position;
            originalPlayerRotation = gameplay.PlayerWalking.transform.rotation;
            originalBodyRotation = gameplay.Get_Player_Rigidbody.rotation;
            originalPlayerVelocity = gameplay.Get_Player_Rigidbody.velocity;
            originalPlayerAngularVelocity = gameplay.Get_Player_Rigidbody.angularVelocity;
            restoreVehicle = vehicles.TestCapturePassengerFixtureRestore();
            fixtureDoors = vehicles.TestKonigVehicle.GetComponentsInChildren<Vehicle_Door>(true).Where(door => door.vehicleController == vehicles.TestKonigVehicle).ToArray();
            string doorInventory = string.Join(" | ", fixtureDoors.Select(DescribeDoorBaseline));
            Debug.Log("MONMP_PASSENGER_BASELINE: role=" + (plugin.TestOptions.IsHost ? "host" : "guest") + "; " + doorInventory);
            Require(fixtureDoors.Length == 2 && fixtureDoors.All(door => !door.Get_IsOpen && door.GetComponent<Rigidbody>() == null && door.GetComponent<HingeJoint>() == null),
                "Both isolated Konig doors must initially be closed without hinge bodies. " + doorInventory);
            originalDoorPositions = fixtureDoors.Select(door => door.transform.localPosition).ToArray();
            originalDoorRotations = fixtureDoors.Select(door => door.transform.localRotation).ToArray();
            originalDriverDoorClosed = vehicles.TestKonigVehicle.isDriverDoorClosed;
            originalPassengerDoorClosed = vehicles.TestKonigVehicle.isPassengerDoorClosed;
            var parts = vehicles.TestKonigVehicle.vehicle_PartsManager;
            Require(parts.Seat_Left.isInstalled && parts.Seat_Right.isInstalled, "Both Konig seats must be installed.");
            foreach (var kind in new[] { PassengerVehicleControl.Ignition, PassengerVehicleControl.LowBeam, PassengerVehicleControl.Gear, PassengerVehicleControl.Dome })
                Require(vehicles.TestPassengerControl(kind) != null, "Missing or ambiguous live native control: " + vehicles.TestPassengerControlDiagnostic(kind));
            Debug.Log("MONMP_PASSENGER_CONTROL_DISCOVERY: " + vehicles.TestPassengerControlDiagnostic(PassengerVehicleControl.Gear));
            var transmission = vehicles.TestKonigVehicle.powertrain.transmission;
            Require(vehicles.TestKonigState.Ignition == 0 && vehicles.TestKonigState.Gear == 0 &&
                transmission.TransmissionType == TransmissionComponent.Type.AutomaticSequential &&
                transmission.automaticTransmissionReverseType == TransmissionComponent.AutomaticTransmissionReverseType.RequireShiftInput &&
                transmission.automaticParkModeEngage && transmission.GearName == "P",
                "Fixture requires ignition off and the installed native automatic transmission in Park.");
            Require(!(bool)AccessTools.Field(typeof(Vehicle_IgnitionSwitch), "mouseDown").GetValue(vehicles.TestPassengerControl(PassengerVehicleControl.Ignition)),
                "Fixture cannot begin while a native ignition key is already held.");
            domeBefore = vehicles.TestKonigState.Lighting.InteriorMode;
            initialHazard = (byte)(vehicles.TestKonigState.Lighting.Controls & 1);
            switchSources = new[] { PassengerVehicleControl.Ignition, PassengerVehicleControl.LowBeam, PassengerVehicleControl.Dome }
                .Select(kind => VehicleSwitchPresentation.TestSourceId(vehicles.TestPassengerControl(kind)!)).ToArray();
            Require(switchSources.All(source => source != 0) && switchSources.Distinct().Count() == switchSources.Length,
                "Native ignition, lowbeam and dome switch cue identities must be distinct and bound.");
            Require(VehicleSwitchPresentation.TestSourceId(vehicles.TestPassengerControl(PassengerVehicleControl.Gear)!) == 0,
                "A gear shifter acquired an invented switch sound source.");
            var hazard = vehicles.TestPassengerControl(PassengerVehicleControl.Hazard);
            Require(hazard != null && VehicleSwitchPresentation.TestSourceId(hazard) == 0,
                "Expected native silent hazard control without an invented switch sound source.");
            initialSwitchPosted = SwitchPosted(); initialSwitchPlayed = SwitchPlayed();
            initialGlobalPosted = VehicleSwitchPresentation.TestPosted; initialGlobalPlayed = VehicleSwitchPresentation.TestPlayed;
            // Capturing both copies' native baselines is a fixture prerequisite. Peer membership
            // alone does not mean its personal profile is ready or its fixture ctor has completed.
            fixtureChannel = plugin.World!.Channel;
            if (plugin.TestOptions.IsHost)
            {
                fixtureChannel.CommandRequested += FixtureReadyCommand;
                // The diagnostic signal does not edit the clock, so its clock revision is
                // irrelevant. A validated packet rejected for clock drift still proves readiness.
                fixtureChannel.CommandRejected += FixtureReadyCommand;
            }
            else fixtureChannel.ReplyReceived += FixtureReadyReply;
        }
        internal bool Tick()
        {
            if (plugin.Session?.IsRunning != true || plugin.World?.Channel.FullySynchronized != true || Time.unscaledTime < next) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 130;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Passenger controls timed out: " + Status);
            if (!plugin.TestOptions.IsHost && !fixtureReadyAcknowledged)
            {
                if (Time.unscaledTime >= nextReadySignal)
                {
                    // Clock commands have no gameplay handler. This matches the existing nitrous
                    // diagnostic command/reply transport and never emits a presentation cue.
                    fixtureChannel.Request(ClockState.Entity, new[] { FixtureReadyMarker, (byte)1 });
                    nextReadySignal = Time.unscaledTime + 0.5f;
                }
                return false;
            }
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }
        private void FixtureReadyCommand(uint peer, ulong entity, byte[] payload)
        {
            if (peer == 0 || entity != ClockState.Entity || payload.Length != 2 || payload[0] != FixtureReadyMarker || payload[1] < 1 || payload[1] > 3 ||
                plugin.Session?.Peers.ContainsKey(peer) != true) return;
            if (payload[1] == 3)
            {
                if (vehicles.TestKonigState.Driver.Owner == peer)
                {
                    guestDriverConfirmed = true;
                    Debug.Log("MONMP_PASSENGER_DRIVER_TAKEOVER_READY: guest asserted native/wire Reverse without Park before host passenger clicks.");
                }
                return;
            }
            if (payload[1] == 2)
            {
                var state = vehicles.TestKonigState;
                var intent = new PassengerVehicleIntent(0, vehicles.TestPassengerControlVehicle, peer, vehicles.TestKonigPassengerState.Generation,
                    state.Driver.Owner, state.Driver.Generation, PassengerVehicleControl.Gear, VehicleControlMethod.MouseDown);
                bool fresh = vehicles.AuthenticatePassengerIntent(peer, intent);
                byte[] summary = Encoding.UTF8.GetBytes(controls.LastRejection);
                var reply = new byte[4 + Math.Min(summary.Length, 124)];
                reply[0] = FixtureReadyMarker; reply[1] = 2; reply[2] = (byte)(fresh ? 1 : 0); reply[3] = (byte)(controls.Rejected > 0 ? 1 : 0);
                Array.Copy(summary, 0, reply, 4, reply.Length - 4); fixtureChannel.Reply(peer, entity, reply);
                Debug.Log("MONMP_PASSENGER_POSE_ECHO: fresh=" + fresh + "; " + vehicles.PassengerIntentDiagnostic(peer, intent) + "; rejected=" + controls.LastRejection);
                return;
            }
            fixtureReadyPeer = peer;
            fixtureChannel.Reply(peer, entity, new[] { FixtureReadyMarker, (byte)1 });
            Debug.Log("MONMP_PASSENGER_FIXTURE_READY: host received captured-baseline readiness from peer=" + peer);
        }
        private void FixtureReadyReply(ulong entity, byte[] payload)
        {
            if (entity != ClockState.Entity || payload.Length < 2 || payload[0] != FixtureReadyMarker) return;
            if (payload[1] == 2 && payload.Length >= 4)
            {
                freshPassengerPoseEcho = payload[2] == 1; hostRejectedFirstGear = payload[3] == 1;
                hostRejectionEcho = Encoding.UTF8.GetString(payload, 4, payload.Length - 4);
                Debug.Log("MONMP_PASSENGER_FIRST_GEAR_ECHO: fresh=" + freshPassengerPoseEcho + "; rejected=" + hostRejectedFirstGear + "; " + hostRejectionEcho);
                return;
            }
            if (payload.Length != 2 || payload[1] != 1) return;
            fixtureReadyAcknowledged = true;
            Debug.Log("MONMP_PASSENGER_FIXTURE_READY: guest baseline readiness acknowledged before native door entry.");
        }
        private bool Host()
        {
            var doors = vehicles.Doors;
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                guest = plugin.Session.Peers.Keys.First(value => value != 0);
                if (fixtureReadyPeer != guest) return false;
                Move(doors.TestKonigPosition()); phase = 1; next = Time.unscaledTime + 0.4f;
            }
            else if (phase == 1)
            {
                if (!doors.TestKonigState().Open) doors.TestKonigClick();
                Move(doors.TestKonigPosition(false)); phase = 2; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 2)
            {
                if (!doors.TestKonigState(false).Open) doors.TestKonigClick(false);
                Move(vehicles.TestKonigDriverPosition); phase = 3; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 3 && controls.Accepted >= 4 && controls.Acknowledged >= 4 && vehicles.TestKonigState.Gear == 0 &&
                (vehicles.TestKonigState.Lighting.Controls & 1) == initialHazard && vehicles.TestKonigPassengerState.Owner == guest)
            {
                Require(controls.Rejected == 0 && controls.DeferredDropped == 0, "The immediate first passenger click was lost or deferred then dropped.");
                RequireNativeGear(0, true, "P", "Parked passenger returned to Park");
                Debug.Log("MONMP_PASSENGER_FIRST_CLICK_ACCEPTED: accepted=" + controls.Accepted + "; deferred/promoted/dropped=" +
                    controls.DeferredReceived + "/" + controls.DeferredPromoted + "/" + controls.DeferredDropped + "; no retry was sent.");
                Require(VehicleSwitchPresentation.TestPosted == initialGlobalPosted && VehicleSwitchPresentation.TestPlayed == initialGlobalPlayed,
                    "Parked passenger gear/hazard actions invented switch sound events.");
                vehicles.TestKonigEnterDriver(); phase = 4;
            }
            else if (phase == 4 && vehicles.TestKonigState.Driver.Owner == 0)
            { driverGeneration = vehicles.TestKonigState.Driver.Generation; phase = 5; }
            else if (phase == 5 && controls.Accepted >= 9 && controls.Acknowledged >= 9 && controls.Rejected >= 6 &&
                vehicles.TestKonigState.Ignition == 1 && vehicles.TestKonigState.Gear == -1 && (vehicles.TestKonigState.Lighting.Lamps & 8) != 0)
            {
                RequireNativeGear(-1, false, "R", "Host driver retained native passenger-selected Reverse");
                Require(vehicles.TestKonigState.Driver.Owner == 0 && vehicles.TestKonigState.Driver.Generation == driverGeneration,
                    "Guest passenger clicks changed the host driver lease.");
                RequireSwitchCounts(initialSwitchPosted, new[] { 1, 1, 1 }, posted: true, "Host simulator native switch cues");
                RequireSwitchCounts(initialSwitchPlayed, new[] { 0, 0, 0 }, posted: false, "Host simulator repeated its own switch cues");
                phase = 6;
            }
            else if (phase == 6 && !vehicles.TestKonigPassengerState.Occupied && controls.ForcedReleases > 0 && vehicles.TestKonigState.Ignition == 1)
            { Require(!vehicles.TestKonigVehicle.powertrain.engine.StarterActive, "Passenger departure left the host starter held."); vehicles.TestKonigExitDriver(); phase = 7; }
            else if (phase == 7 && vehicles.TestKonigState.Driver.Owner == guest)
            { driverGeneration = vehicles.TestKonigState.Driver.Generation; Move(vehicles.TestKonigPassengerPosition); phase = 8; next = Time.unscaledTime + 0.8f; }
            else if (phase == 8) { vehicles.TestKonigEnterPassenger(); phase = 9; }
            else if (phase == 9 && vehicles.TestKonigPassengerState.Owner == 0 && guestDriverConfirmed)
            {
                Click(PassengerVehicleControl.Gear, VehicleControlMethod.MouseRightDown);
                Click(PassengerVehicleControl.LowBeam);
                Click(PassengerVehicleControl.Dome);
                phase = 10; next = Time.unscaledTime + 1;
            }
            else if (phase == 10)
            {
                if (vehicles.TestKonigState.Gear != 0 || (vehicles.TestKonigState.Lighting.Lamps & 8) != 0) return false;
                RequireNativeGear(0, true, "P", "Guest driver received host passenger Park");
                Require(vehicles.TestKonigState.Driver.Owner == guest && vehicles.TestKonigState.Driver.Generation == driverGeneration,
                    "Host passenger clicks changed the guest driver lease.");
                // With ignition already on, a short click and release switches it off.
                Click(PassengerVehicleControl.Ignition); phase = 11; next = Time.unscaledTime + 0.1f;
            }
            else if (phase == 11) { Click(PassengerVehicleControl.Ignition, VehicleControlMethod.MouseUp); phase = 12; next = Time.unscaledTime + 1; }
            else if (phase == 12 && vehicles.TestKonigState.Ignition == 0)
            {
                // No MouseUp: the remote simulator must release this key on seat departure.
                Click(PassengerVehicleControl.Ignition); phase = 13; next = Time.unscaledTime + 0.15f;
            }
            else if (phase == 13 && controls.Acknowledged >= 16)
            {
                RequireSwitchCounts(initialSwitchPosted, new[] { 2, 1, 1 }, posted: true, "Host passenger replay posted duplicate native cues");
                if (!SwitchCountsMatch(initialSwitchPlayed, new[] { 3, 1, 1 }, posted: false)) return false;
                RequireSwitchSoundObserved("Host observer of guest simulator");
                LogSwitchEvidence("HOST");
                vehicles.TestKonigExitPassenger(); phase = 14;
            }
            else if (phase == 14 && plugin.Session!.Peers.Count == 1)
            {
                Require(vehicles.TestKonigState.Driver.Owner == ItemLease.Unowned && !vehicles.TestKonigPassengerState.Occupied && controls.Pending == 0,
                    "Departure left a lease or control intent queued.");
                Cleanup(); return true;
            }
            return false;
        }
        private bool Guest()
        {
            if (phase == 0 && vehicles.TestKonigState.Driver.Owner == ItemLease.Unowned && vehicles.Doors.TestKonigState(false).Open)
            { Move(vehicles.TestKonigPassengerPosition); phase = 1; next = Time.unscaledTime + 0.8f; }
            else if (phase == 1) { vehicles.TestKonigEnterPassenger(); phase = 2; }
            else if (phase == 2 && vehicles.TestKonigPassengerState.Owner == plugin.Session!.LocalPeerId && vehicles.TestKonigState.Driver.Owner == ItemLease.Unowned)
            { Click(PassengerVehicleControl.Gear); firstGearAt = Time.unscaledTime; phase = 20; }
            else if (phase == 20 && vehicles.TestKonigState.Gear == -1)
            {
                RequireNativeGear(-1, false, "R", "Immediate parked passenger click selected native Reverse");
                Require(vehicles.TestKonigState.Driver.Owner == ItemLease.Unowned, "Parked passenger gear-up took the driver lease.");
                Click(PassengerVehicleControl.Gear, VehicleControlMethod.MouseRightDown); phase = 21;
            }
            else if (phase == 20 && Time.unscaledTime - firstGearAt >= 0.3f)
            {
                // This remains an immediate first click. Pose echo is diagnostic only; no
                // second click may hide a rejected or expired original request.
                Require(!hostRejectedFirstGear, "Immediate first click rejected: " + hostRejectionEcho);
                if (Time.unscaledTime >= nextPoseEchoSignal)
                {
                    fixtureChannel.Request(ClockState.Entity, new[] { FixtureReadyMarker, (byte)2 });
                    nextPoseEchoSignal = Time.unscaledTime + 0.5f;
                }
            }
            else if (phase == 21 && vehicles.TestKonigState.Gear == 0 && vehicles.TestKonigState.Driver.Owner == ItemLease.Unowned)
            { RequireNativeGear(0, true, "P", "Parked passenger down-click selected native Park"); Click(PassengerVehicleControl.Hazard); phase = 23; }
            else if (phase == 23 && (vehicles.TestKonigState.Lighting.Controls & 1) != initialHazard)
            { Click(PassengerVehicleControl.Hazard); phase = 24; }
            else if (phase == 24 && (vehicles.TestKonigState.Lighting.Controls & 1) == initialHazard && vehicles.TestKonigState.Driver.Owner == 0)
            {
                Require(VehicleSwitchPresentation.TestPosted == initialGlobalPosted && VehicleSwitchPresentation.TestPlayed == initialGlobalPlayed,
                    "Gear/hazard observers heard invented switch cues.");
                phase = 22;
            }
            else if (phase == 22 && vehicles.TestKonigPassengerState.Owner == plugin.Session!.LocalPeerId && vehicles.TestKonigState.Driver.Owner == 0)
            {
                driverGeneration = vehicles.TestKonigState.Driver.Generation;
                var stale = new PassengerVehicleIntent(0, vehicles.TestPassengerControlVehicle, plugin.Session.LocalPeerId,
                    vehicles.TestKonigPassengerState.Generation + 1, 0, driverGeneration, PassengerVehicleControl.Gear, VehicleControlMethod.MouseDown);
                controls.TestRequest(stale);
                // Validly encoded requests still cannot impersonate another actor, reuse an
                // old driver generation, or supply their own durable execution sequence.
                controls.TestRequest(new PassengerVehicleIntent(0, stale.Vehicle, 0, vehicles.TestKonigPassengerState.Generation,
                    0, driverGeneration, PassengerVehicleControl.Gear, VehicleControlMethod.MouseDown));
                controls.TestRequest(new PassengerVehicleIntent(0, stale.Vehicle, plugin.Session.LocalPeerId, vehicles.TestKonigPassengerState.Generation,
                    0, driverGeneration + 1, PassengerVehicleControl.Gear, VehicleControlMethod.MouseDown));
                controls.TestRequest(new PassengerVehicleIntent(1, stale.Vehicle, plugin.Session.LocalPeerId, vehicles.TestKonigPassengerState.Generation,
                    0, driverGeneration, PassengerVehicleControl.Gear, VehicleControlMethod.MouseDown));
                // A seated actor cannot evade canonical seat authentication by claiming the
                // walking mode, and a control index cannot resolve outside the bound hierarchy.
                controls.TestRequest(new PassengerVehicleIntent(0, stale.Vehicle, plugin.Session.LocalPeerId, 0,
                    0, driverGeneration, PassengerVehicleControl.Gear, VehicleControlMethod.MouseDown));
                controls.TestRequest(new PassengerVehicleIntent(0, stale.Vehicle, plugin.Session.LocalPeerId, vehicles.TestKonigPassengerState.Generation,
                    0, driverGeneration, PassengerVehicleControl.Gear, VehicleControlMethod.MouseDown, byte.MaxValue));
                Click(PassengerVehicleControl.Ignition); phase = 3; next = Time.unscaledTime + 0.1f;
            }
            else if (phase == 3) { Click(PassengerVehicleControl.Ignition, VehicleControlMethod.MouseUp); phase = 4; next = Time.unscaledTime + 0.8f; }
            else if (phase == 4 && vehicles.TestKonigState.Ignition == 1)
            {
                Click(PassengerVehicleControl.LowBeam); Click(PassengerVehicleControl.Gear); Click(PassengerVehicleControl.Dome);
                phase = 5; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 5 && vehicles.TestKonigState.Gear == -1 && (vehicles.TestKonigState.Lighting.Lamps & 8) != 0 &&
                vehicles.TestKonigState.Lighting.InteriorMode == (domeBefore + 1) % 3)
            {
                RequireNativeGear(-1, false, "R", "Guest observer received native Reverse and cleared Park");
                Require(vehicles.TestKonigState.Driver.Owner == 0 && vehicles.TestKonigState.Driver.Generation == driverGeneration,
                    "Guest passenger acquired physics authority.");
                if (!SwitchCountsMatch(initialSwitchPlayed, new[] { 1, 1, 1 }, posted: false)) return false;
                RequireSwitchCounts(initialSwitchPosted, new[] { 0, 0, 0 }, posted: true, "Guest passenger posts its unexecuted controls");
                RequireSwitchSoundObserved("Guest observer of host simulator");
                // Exercise the actual received scoped snapshot, then replay its current live log
                // twice. Historical switch cues must stay silent on every application.
                var channel = plugin.World!.Channel;
                if (!channel.RequestResync(SnapshotScope.ForEntity(SharedPresentationEvents.Adapter, SharedPresentationEvents.Entity),
                    "Passenger switch sound snapshot silence fixture")) return false;
                repairsBefore = channel.RepairsApplied; repairPosted = SwitchPosted(); repairPlayed = SwitchPlayed(); phase = 50;
            }
            else if (phase == 50 && !plugin.World!.Channel.RepairPending && plugin.World.Channel.RepairsApplied > repairsBefore)
            {
                Require(SwitchPosted().SequenceEqual(repairPosted) && SwitchPlayed().SequenceEqual(repairPlayed),
                    "Presentation snapshot replay emitted a historical switch cue.");
                var record = plugin.World.Channel.Records[SharedPresentationEvents.Entity];
                var replay = AccessTools.Method(typeof(SharedPresentationEvents), "Changed", new[] { typeof(WorldRecord) });
                replay.Invoke(plugin.World.Events, new object[] { record }); replay.Invoke(plugin.World.Events, new object[] { record });
                Require(SwitchPosted().SequenceEqual(repairPosted) && SwitchPlayed().SequenceEqual(repairPlayed),
                    "Duplicate presentation log replay emitted a historical switch cue.");
                Debug.Log("MONMP_SWITCH_SNAPSHOT_SILENT: actual scoped repair and two duplicate log applications preserved exact source counters.");
                Click(PassengerVehicleControl.Ignition); phase = 6; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 6) { vehicles.TestKonigExitPassenger(); phase = 7; }
            else if (phase == 7 && vehicles.TestKonigState.Driver.Owner == ItemLease.Unowned && !vehicles.TestKonigPassengerState.Occupied)
            { Move(vehicles.TestKonigDriverPosition); phase = 8; next = Time.unscaledTime + 0.8f; }
            else if (phase == 8) { vehicles.TestKonigEnterDriver(); phase = 9; }
            else if (phase == 9 && vehicles.TestKonigState.Driver.Owner == plugin.Session!.LocalPeerId)
            {
                RequireNativeGear(-1, false, "R", "Guest driver takeover preserved Reverse without Park");
                driverGeneration = vehicles.TestKonigState.Driver.Generation;
                fixtureChannel.Request(ClockState.Entity, new[] { FixtureReadyMarker, (byte)3 });
                phase = 10;
            }
            else if (phase == 10 && !vehicles.TestKonigPassengerState.Occupied && controls.ForcedReleases > 0 && controls.Applied >= 6)
            {
                Require(vehicles.TestKonigState.Driver.Owner == plugin.Session!.LocalPeerId && vehicles.TestKonigState.Driver.Generation == driverGeneration,
                    "Host passenger acquired physics authority.");
                Require(!vehicles.TestKonigVehicle.powertrain.engine.StarterActive && vehicles.TestKonigState.Gear == 0,
                    "Remote ignition release or gear intent did not reach the guest simulator.");
                RequireNativeGear(0, true, "P", "Guest simulator retained authenticated passenger Park");
                RequireSwitchCounts(initialSwitchPosted, new[] { 3, 1, 1 }, posted: true, "Guest simulator native switch cues");
                RequireSwitchCounts(initialSwitchPlayed, new[] { 2, 1, 1 }, posted: false, "Guest simulator heard its own switch cues again");
                RequireSwitchSoundObserved("Guest observer of earlier host simulator");
                LogSwitchEvidence("GUEST");
                Cleanup(); return true;
            }
            return false;
        }
        private void Click(PassengerVehicleControl kind, VehicleControlMethod method = VehicleControlMethod.MouseDown)
        {
            var control = vehicles.TestPassengerControl(kind)!;
            AccessTools.Method(control.GetType(), kind == PassengerVehicleControl.Dome ? "ChangeLightMode" : method.ToString(), Type.EmptyTypes).Invoke(control, null);
        }
        private void Move(Vector3 at) { gameplay.PlayerWalking.transform.position = at; gameplay.Get_Player_Rigidbody.position = at; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        private void RequireNativeGear(int gear, bool park, string name, string context)
        {
            var transmission = vehicles.TestKonigVehicle.powertrain.transmission;
            Require(vehicles.TestKonigState.Gear == gear && vehicles.TestKonigState.AutomaticPark == park && transmission.Gear == gear &&
                transmission.automaticParkModeEngage == park && transmission.GearName == name,
                context + ": shared/native=" + vehicles.TestKonigState.Gear + "/" + transmission.Gear +
                "; shared/nativePark=" + vehicles.TestKonigState.AutomaticPark + "/" + transmission.automaticParkModeEngage + "; gearName=" + transmission.GearName);
        }
        private string DescribeDoorBaseline(Vehicle_Door door)
        {
            var body = door.GetComponent<Rigidbody>(); var hinge = door.GetComponent<HingeJoint>();
            var shared = vehicles.Doors.TestKonigState(door.isDriverDoor);
            string original;
            try
            {
                var binding = AccessTools.Method(typeof(SharedVehicleDoors), "TestKonigBinding", new[] { typeof(bool) })
                    .Invoke(vehicles.Doors, new object[] { door.isDriverDoor });
                var captured = (VehicleDoorState)AccessTools.Field(binding.GetType(), "OriginalState").GetValue(binding);
                original = captured.Open + "/" + captured.Angle.ToString("F2");
            }
            catch (Exception ex) { original = "unavailable(" + ex.GetType().Name + ")"; }
            return RuntimeProbe.CanonicalPath(door.transform) + "[driver=" + door.isDriverDoor + ",nativeOpen=" + door.Get_IsOpen +
                ",nativeY=" + door.transform.localEulerAngles.y.ToString("F2") + ",body=" + (body == null ? "none" : body.name + "#" + body.GetInstanceID()) +
                ",hinge=" + (hinge == null ? "none" : hinge.name + "#" + hinge.GetInstanceID()) +
                ",shared=" + shared.Open + "/" + shared.Angle.ToString("F2") + ",original=" + original + "]";
        }
        private int[] SwitchPosted() => switchSources.Select(VehicleSwitchPresentation.TestPostedFor).ToArray();
        private int[] SwitchPlayed() => switchSources.Select(VehicleSwitchPresentation.TestPlayedFor).ToArray();
        private bool SwitchCountsMatch(int[] baseline, int[] expected, bool posted) =>
            (posted ? SwitchPosted() : SwitchPlayed()).Select((count, i) => count - baseline[i]).SequenceEqual(expected);
        private void RequireSwitchCounts(int[] baseline, int[] expected, bool posted, string context) =>
            Require(SwitchCountsMatch(baseline, expected, posted), context + ": expected " + string.Join(",", expected) +
                "; actual " + string.Join(",", (posted ? SwitchPosted() : SwitchPlayed()).Select((count, i) => count - baseline[i])));
        private void RequireSwitchSoundObserved(string context) => Require(switchSources.All(VehicleSwitchPresentation.TestSoundObservedFor),
            context + " did not observe actual source-bound native switch emitters.");
        private void LogSwitchEvidence(string role) => Debug.Log("MONMP_SWITCH_OBSERVER_" + role + ": sourceIds=" +
            string.Join(",", switchSources.Select(source => source.ToString("X16"))) + "; posted=" + string.Join(",", SwitchPosted()) +
            "; played=" + string.Join(",", SwitchPlayed()) + "; actualSound=" + string.Join(",", switchSources.Select(VehicleSwitchPresentation.TestSoundObservedFor)));
        internal void Cleanup()
        {
            if (cleaned) return;
            fixtureChannel.CommandRequested -= FixtureReadyCommand;
            fixtureChannel.CommandRejected -= FixtureReadyCommand;
            fixtureChannel.ReplyReceived -= FixtureReadyReply;
            // End ownership and restore adapter baselines first; host adapter disposal otherwise
            // reapplies its final test state over the restored native vehicle.
            if (plugin.Session?.IsRunning == true) plugin.EndPairedTestSession();
            restoreVehicle();
            for (int i = 0; i < fixtureDoors.Length; i++)
            {
                var door = fixtureDoors[i]; if (door == null) continue;
                var hinge = door.GetComponent<HingeJoint>(); var body = door.GetComponent<Rigidbody>();
                if (hinge != null) UnityEngine.Object.DestroyImmediate(hinge);
                if (body != null) UnityEngine.Object.DestroyImmediate(body);
                AccessTools.Field(typeof(Vehicle_Door), "hj").SetValue(door, null);
                AccessTools.Field(typeof(Vehicle_Door), "rb").SetValue(door, null);
                AccessTools.Field(typeof(Vehicle_Door), "isOpen").SetValue(door, false);
                AccessTools.Field(typeof(Vehicle_Door), "Timer_JustOpened").SetValue(door, 0f);
                door.transform.localPosition = originalDoorPositions[i]; door.transform.localRotation = originalDoorRotations[i];
                if (door.isDriverDoor) door.vehicleController.isDriverDoorClosed = true;
                else door.vehicleController.isPassengerDoorClosed = true;
            }
            vehicles.TestKonigVehicle.isDriverDoorClosed = originalDriverDoorClosed;
            vehicles.TestKonigVehicle.isPassengerDoorClosed = originalPassengerDoorClosed;
            gameplay.PlayerWalking.transform.SetPositionAndRotation(originalPlayerPosition, originalPlayerRotation);
            gameplay.Get_Player_Rigidbody.position = originalPlayerBodyPosition; gameplay.Get_Player_Rigidbody.rotation = originalBodyRotation;
            gameplay.Get_Player_Rigidbody.velocity = originalPlayerVelocity; gameplay.Get_Player_Rigidbody.angularVelocity = originalPlayerAngularVelocity;
            cleaned = true;
            Debug.Log("MONMP_PASSENGER_CONTROL_RESTORE: captured player and exact Konig native state restored; fixture-created door hinges removed.");
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Passenger controls: " + message); }
    }
}
