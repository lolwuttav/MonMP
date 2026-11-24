using System;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // PrepareFixture(OlTruck) stages only the isolated trailer. The native driver
    // seat grants authority; real engine/wheel physics must tow it on each owner.
    internal sealed class PairedTrailerTowingTest : IDisposable
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedTrailers trailers;
        private readonly SharedVehicles vehicles;
        private readonly ulong towId, childId;
        private readonly bool host;
        private readonly Vector3 originalTowPosition, originalTowVelocity, originalTowAngular;
        private readonly Quaternion originalTowRotation;
        private readonly int originalIgnition, originalGear, originalParking;
        private readonly bool originalAutoInput;
        private readonly bool originalDoorOpen;
        private readonly float originalHandbrake;
        private int phase, drivePhase, initialMotionCount, entryStep;
        private float deadline, next, driveUntil, doorOpenedAt;
        private float crankAt;
        private string initialEngineState = "not-started";
        private Vector3 driveStart, guestSampleStart, hostSampleStart;
        private bool sawGuest, guestMoved, sawHost, hostMoved, disposed;

        internal PairedTrailerTowingTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            host = plugin.TestOptions.IsHost;
            trailers = plugin.World!.Trailers; vehicles = plugin.Vehicles!;
            vehicles.TestType = Vehicle_Type.OlTruck;
            vehicles.TestEnableDriverEntryDiagnostics();
            (towId, childId) = trailers.TestFixtureIds(Vehicle_Type.OlTruck);
            var tow = vehicles.TestVehicle; var body = tow.vehicleRigidbody;
            originalTowPosition = body.position; originalTowRotation = body.rotation;
            originalTowVelocity = body.velocity; originalTowAngular = body.angularVelocity;
            originalIgnition = tow.Get_IgnitionPosition; originalGear = tow.powertrain.transmission.Gear;
            originalParking = tow.vehicle_PartsManager.ParkingBrake?.LoadFromSave ?? 0;
            originalAutoInput = tow.input.autoSetInput;
            originalHandbrake = tow.brakes.handbrakeValue;
            originalDoorOpen = vehicles.Doors.TestState().Open;
        }

        internal string Status => "trailer towing phase=" + phase + "; owner=" + trailers.TestState(childId).Link.Owner +
            "; guestMotion=" + guestMoved + "; hostMotion=" + hostMoved + "; joint=" + trailers.TestNativeLinkIntact(towId, childId) +
            "; accepted=" + trailers.TestRemoteMotionAcceptedCount + "; published=" + trailers.TestLocalMotionPublishCount +
            "; doorsClosed=" + vehicles.TestVehicle.isDriverDoorClosed + "/" + vehicles.TestVehicle.isPassengerDoorClosed +
            "; doorDistance=" + Vector3.Distance(gameplay.PlayerWalking.transform.position, vehicles.Doors.TestPosition).ToString("F2") +
            "; doorOpen=" + vehicles.Doors.TestState().Open + "/" + vehicles.Doors.TestNativeTruckState().Open +
            "; doorAngle=" + vehicles.Doors.TestNativeTruckState().Angle.ToString("F2") + "; entryStep=" + entryStep +
            "; seatDistance=" + Vector3.Distance(gameplay.PlayerWalking.transform.position, vehicles.TestSeat).ToString("F2") +
            "; entry=" + vehicles.TestDriverEntryDiagnostic;

        internal bool Tick()
        {
            if (!trailers.Ready || plugin.World?.PersonalProfileReady != true) return false;
            vehicles.TestType = Vehicle_Type.OlTruck;
            if (deadline == 0) deadline = Time.unscaledTime + 120;
            Require(Time.unscaledTime < deadline, "Timed out: " + Status);
            return host ? Host() : Guest();
        }

        private bool Host()
        {
            var link = trailers.TestState(childId).Link;
            if (phase == 0 && plugin.Session!.Peers.Count >= 2)
            {
                Require(vehicles.TestState.Driver.Owner == ItemLease.Unowned, "Truck already has a driver.");
                trailers.TestStageAtHitch(towId, childId);
                MovePlayer(trailers.TestTrailer(childId).attachmentPoint.position + Vector3.up * 0.5f);
                trailers.TestRequestAttach(towId, childId, 0);
                phase = 1;
            }
            else if (phase == 1 && link.ParentId == towId)
            {
                Require(trailers.TestNativeLinkIntact(towId, childId), "Host attach lacks a physical truck joint.");
                initialMotionCount = trailers.TestRemoteMotionAcceptedCount;
                phase = 2;
            }
            else if (phase == 2)
            {
                if (link.Owner != ItemLease.Unowned && link.Owner != 0)
                {
                    ObserveMotion(ref sawGuest, ref guestMoved, ref guestSampleStart);
                    Require(trailers.TestNativeLinkIntact(towId, childId), "Guest driving lost the host replica joint.");
                }
                else if (sawGuest && link.Owner == ItemLease.Unowned)
                {
                    Require(guestMoved && trailers.TestRemoteMotionAcceptedCount >= initialMotionCount + 3,
                        "Host did not accept multiple moving guest trailer samples.");
                    Require(trailers.TestLocalWheelMode(childId), "Trailer wheel simulation did not return to host.");
                    entryStep = 0;
                    next = Time.unscaledTime + 0.8f; phase = 3;
                }
            }
            else if (phase == 3 && Time.unscaledTime >= next)
            {
                if (vehicles.TestState.Driver.Owner == ItemLease.Unowned) TryEnterTruck();
                next = Time.unscaledTime + 0.8f;
                if (vehicles.TestState.Driver.Owner == 0 && link.Owner == 0 && DriverReady())
                {
                    BeginDrive(); initialMotionCount = trailers.TestLocalMotionPublishCount; phase = 4;
                }
            }
            else if (phase == 4 && Drive())
            {
                Require(trailers.TestLocalMotionPublishCount >= initialMotionCount + 3,
                    "Host peer 0 did not publish trailer motion during driving.");
                phase = 5; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 5 && Time.unscaledTime >= next && link.Owner == ItemLease.Unowned)
            {
                MovePlayer(trailers.TestTrailer(childId).attachmentPoint.position + Vector3.up * 0.5f);
                trailers.TestRequestDetach(childId, 0); phase = 6;
            }
            else if (phase == 6 && link.ParentId == 0)
            {
                Require(!trailers.TestNativeLinkIntact(towId, childId), "Native detach left a truck joint.");
                Dispose(); return true;
            }
            return false;
        }

        private bool Guest()
        {
            var link = trailers.TestState(childId).Link;
            if (phase == 0 && link.ParentId == towId)
            {
                Require(trailers.TestNativeLinkIntact(towId, childId), "Guest attach lacks a physical truck joint.");
                entryStep = 0;
                phase = 1; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 1 && Time.unscaledTime >= next)
            {
                if (vehicles.TestState.Driver.Owner == ItemLease.Unowned) TryEnterTruck();
                next = Time.unscaledTime + 0.8f;
                if (vehicles.TestState.Driver.Owner == plugin.Session!.LocalPeerId && link.Owner == plugin.Session.LocalPeerId && DriverReady())
                { BeginDrive(); phase = 2; }
            }
            else if (phase == 2 && Drive()) phase = 3;
            else if (phase == 3)
            {
                if (link.Owner == 0)
                {
                    ObserveMotion(ref sawHost, ref hostMoved, ref hostSampleStart);
                    Require(trailers.TestNativeLinkIntact(towId, childId), "Host driving lost the guest replica joint.");
                }
                if (link.ParentId == 0)
                {
                    Require(sawHost && hostMoved, "Guest did not receive moving host-peer-0 trailer samples.");
                    Require(!trailers.TestNativeLinkIntact(towId, childId), "Guest detach left a truck joint.");
                    Dispose(); return true;
                }
            }
            return false;
        }

        private void ObserveMotion(ref bool sawOwner, ref bool moved, ref Vector3 start)
        {
            var pose = trailers.TestState(childId).Pose;
            var position = new Vector3(pose.X, pose.Y, pose.Z);
            if (!sawOwner) { start = position; sawOwner = true; }
            moved |= Vector3.Distance(start, position) > 0.3f;
        }

        private void BeginDrive()
        {
            Require(gameplay.Get_CurrentVehicleController == vehicles.TestVehicle &&
                gameplay.Get_PlayerState == PlayerState.InVehicle_Driver, "Native truck driver seat did not activate.");
            Require(trailers.TestLocalWheelMode(childId), "Trailer did not regain its original wheel update mode.");
            var tow = vehicles.TestVehicle;
            initialEngineState = EngineStatus();
            tow.input.autoSetInput = false; tow.input.states.Reset(); tow.input.states.clutch = 1;
            if (tow.vehicle_PartsManager.ParkingBrake != null) tow.vehicle_PartsManager.ParkingBrake.LoadFromSave = 0;
            tow.brakes.handbrakeValue = 0; tow.powertrain.transmission.ShiftInto(0, true);
            // Native StartEngine queues StartStop, so cranking an already running
            // handoff engine would stop it. Contact also wakes a sleeping vehicle.
            bool running = tow.isEngineRunning;
            tow.Get_IgnitionPosition = 1;
            driveStart = trailers.TestTrailer(childId).VehicleController.vehicleRigidbody.position;
            drivePhase = running ? 0 : -1;
            crankAt = Time.unscaledTime + 0.5f;
            driveUntil = Time.unscaledTime + 12f;
        }

        private string EngineStatus()
        {
            var tow = vehicles.TestVehicle;
            return "running=" + tow.isEngineRunning + ",ignition=" + tow.Get_IgnitionPosition +
                ",engineIgnition=" + tow.powertrain.engine.ignition + ",starter=" + tow.powertrain.engine.StarterActive +
                ",rpm=" + tow.powertrain.engine.RPM.ToString("F1") + ",queued=" + tow.input.EngineStartStop +
                ",awake=" + tow.IsAwake + ",mode=" + tow.VehicleMultiplayerInstanceType +
                ",gear=" + tow.powertrain.transmission.Gear;
        }

        private bool DriverReady() => gameplay.Get_CurrentVehicleController == vehicles.TestVehicle &&
            gameplay.Get_PlayerState == PlayerState.InVehicle_Driver && trailers.TestLocalWheelMode(childId);

        private void TryEnterTruck()
        {
            // Each step is separated by the caller's 0.8s wait so the host sees
            // the approach pose before either interaction. Re-approach if the
            // walking body or the unoccupied truck drifted in the meantime.
            var truck = vehicles.TestVehicle;
            bool closed = truck.isDriverDoorClosed && truck.isPassengerDoorClosed;
            if (entryStep == 0)
            {
                doorOpenedAt = 0;
                if (closed) { ApproachDoor(); entryStep = 1; }
                else { ApproachSeat(); entryStep = 3; }
                return;
            }
            if (entryStep == 1)
            {
                if (Vector3.Distance(gameplay.PlayerWalking.transform.position, vehicles.Doors.TestPosition) > 2f)
                { ApproachDoor(); return; }
                if (!vehicles.Doors.TestState().Open) vehicles.Doors.TestClick();
                doorOpenedAt = Time.unscaledTime; entryStep = 2; return;
            }
            if (entryStep == 2)
            {
                if (closed)
                {
                    if (!vehicles.Doors.TestState().Open) { ApproachDoor(); entryStep = 1; return; }
                    Require(Time.unscaledTime - doorOpenedAt < 8f,
                        "Door record is open but native closed flags still block driver entry. " + Status);
                    return;
                }
                ApproachSeat(); entryStep = 3; return;
            }
            if (closed) { entryStep = 0; return; }
            if (Vector3.Distance(gameplay.PlayerWalking.transform.position, vehicles.TestSeat) > 2f)
            { ApproachSeat(); return; }
            vehicles.TestEnter();
            // A rejected request must get a new, fresh near-seat pose before retry.
            if (vehicles.TestState.Driver.Owner == ItemLease.Unowned) ApproachSeat();
        }

        private Vector3 DoorSide => vehicles.TestVehicle.transform.right *
            (Vector3.Dot(vehicles.Doors.TestPosition - vehicles.TestVehicle.transform.position,
                vehicles.TestVehicle.transform.right) < 0 ? -1f : 1f);
        private void ApproachDoor() => MovePlayer(vehicles.Doors.TestPosition + DoorSide);
        private void ApproachSeat() => MovePlayer(vehicles.TestSeat + DoorSide);

        private bool Drive()
        {
            var tow = vehicles.TestVehicle;
            Require(trailers.TestNativeLinkIntact(towId, childId), "Driving lost the physical truck/trailer joint.");
            Require(trailers.TestLocalWheelMode(childId), "Driving changed trailer wheel update ownership.");
            if (drivePhase == -1)
            {
                if (Time.unscaledTime < crankAt) return false;
                if (!tow.isEngineRunning) tow.Get_IgnitionPosition = 2;
                drivePhase = 0;
                return false;
            }
            if (drivePhase == 0)
            {
                Require(Time.unscaledTime < driveUntil, "Truck native engine did not start. initial=" +
                    initialEngineState + "; current=" + EngineStatus());
                if (!tow.isEngineRunning) return false;
                tow.Get_IgnitionPosition = 1; tow.powertrain.transmission.ShiftInto(1, true);
                tow.input.states.clutch = 1; tow.input.states.throttle = 0.7f;
                drivePhase = 1; driveUntil = Time.unscaledTime + 6f;
                return false;
            }
            if (Time.unscaledTime < driveUntil) return false;
            Require(Vector3.Distance(driveStart, trailers.TestTrailer(childId).VehicleController.vehicleRigidbody.position) > 0.3f,
                "Native truck wheel physics did not tow the attached trailer.");
            // The joint must hold the coupler near the hitch throughout actual motion.
            Require(Vector3.Distance(trailers.TestHitch(towId).attachmentPoint.position,
                trailers.TestTrailer(childId).attachmentPoint.position) < 0.5f, "Moving trailer coupler separated from the truck.");
            tow.input.states.throttle = 0; tow.input.states.brakes = 1; tow.brakes.handbrakeValue = 1;
            tow.input.autoSetInput = originalAutoInput;
            vehicles.TestExit(); return true;
        }

        private void MovePlayer(Vector3 position)
        {
            gameplay.PlayerWalking.transform.position = position;
            if (gameplay.Get_Player_Rigidbody != null)
            { gameplay.Get_Player_Rigidbody.position = position; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        }

        private static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Paired trailer towing: " + message); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (gameplay.Get_CurrentVehicleController == vehicles.TestVehicle && gameplay.Get_PlayerState == PlayerState.InVehicle_Driver)
                vehicles.TestExit();
            if (host)
            {
                var tow = vehicles.TestVehicle; var body = tow.vehicleRigidbody;
                tow.input.states.Reset(); tow.input.autoSetInput = originalAutoInput;
                tow.Get_IgnitionPosition = originalIgnition; tow.powertrain.transmission.ShiftInto(originalGear, true);
                if (tow.vehicle_PartsManager.ParkingBrake != null) tow.vehicle_PartsManager.ParkingBrake.LoadFromSave = originalParking;
                tow.brakes.handbrakeValue = originalHandbrake;
                body.position = originalTowPosition; body.rotation = originalTowRotation;
                tow.transform.SetPositionAndRotation(originalTowPosition, originalTowRotation);
                body.velocity = originalTowVelocity; body.angularVelocity = originalTowAngular;
                if (vehicles.Doors.TestState().Open != originalDoorOpen)
                {
                    MovePlayer(vehicles.Doors.TestPosition + Vector3.right * 1.5f);
                    vehicles.Doors.TestClick();
                }
            }
            PairedTrailerAuthorityTest.RestoreFixture();
        }
    }
}
