using System;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Exercises native buttons while the guest owns each actual seat, then observes a host click.
    internal sealed class PairedKonigSeatAdjustmentsTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedVehicles vehicles;
        private VehicleSeatAdjustmentState before;
        private VehicleSeatAdjustmentState accepted;
        private VehicleSeatAdjustmentState adjusted;
        private VehicleSeatAdjustmentState passengerBefore, passengerGuestAdjusted, passengerAdjusted, driverBeforePassengerAdjustment;
        private int direction, heightDirection;
        private int passengerDirection;
        private int phase;
        private float next, deadline;
        private float beforeSaveForward, beforeSaveHeight, acceptedSaveForward;
        private float passengerClickSaveForward, passengerClickSaveHeight;
        private Vector3 passengerPositionBefore, driverPositionBefore;
        private bool nearForwardPositioned, nearHeightPositioned;

        internal PairedKonigSeatAdjustmentsTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin;
            this.gameplay = gameplay;
            vehicles = plugin.Vehicles!;
            NativeRegistryTest.Verify(plugin.World!);
            Require(vehicles.TestKonigSeatAdjustmentControlsReady,
                "Expected one active native forward and height control under the driver seat; see shared-konig-seat-adjustment-controls.tsv.");
            Require(vehicles.TestKonigPassengerSeatAdjustmentForwardReady,
                "Expected one active native forward control under the passenger seat for the passenger-record test; see shared-konig-seat-adjustment-controls.tsv.");
            var parts = vehicles.TestKonigVehicle.vehicle_PartsManager;
            Require(parts.Seat_Left.isInstalled && parts.CheckSeatSolidity(true) && parts.CheckSeatSolidity(false),
                "Konig driver and passenger seats must be installed and solid in the isolated fixture.");
        }

        internal string Status => "Konig seat adjustment phase=" + phase + "; generation=" + vehicles.TestKonigSeatAdjustment.Generation +
            "; forward/height=" + vehicles.TestKonigSeatAdjustment.ForwardCentimeters + "/" + vehicles.TestKonigSeatAdjustment.HeightCentimeters +
            "; native=" + vehicles.TestKonigNativeSeatAdjustment.ForwardCentimeters + "/" + vehicles.TestKonigNativeSeatAdjustment.HeightCentimeters +
            "; save=" + vehicles.TestKonigSeatCustomParameter(0).ToString("F4") + "/" + vehicles.TestKonigSeatCustomParameter(1).ToString("F4");

        internal bool Tick()
        {
            if (!vehicles.Ready || plugin.World?.Channel.FullySynchronized != true) return false;
            // The final saved/default spawn reply must not undo a fixture teleport.
            if (!plugin.TestOptions.IsHost && phase == 0 &&
                (!plugin.World.PersonalProfileReady || plugin.World.ReconnectPositions?.Settled != true)) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 90;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Konig seat adjustment test stalled: " + Status);
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                before = vehicles.TestKonigSeatAdjustment;
                passengerBefore = vehicles.TestKonigPassengerSeatAdjustment;
                beforeSaveForward = vehicles.TestKonigSeatCustomParameter(0);
                beforeSaveHeight = vehicles.TestKonigSeatCustomParameter(1);
                var doors = vehicles.Doors;
                Move(doors.TestKonigPosition());
                if (!doors.TestKonigState().Open) doors.TestKonigClick();
                Move(doors.TestKonigPosition(false));
                if (!doors.TestKonigState(false).Open) doors.TestKonigClick(false);
                Require(Same(before, vehicles.TestKonigNativeSeatAdjustment), "Host slider state does not match the native starting seat.");
                heightDirection = HeightDirection();
                phase = 1;
                next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 1 && Time.unscaledTime >= next)
            {
                Require(Same(before, vehicles.TestKonigSeatAdjustment) && Same(before, vehicles.TestKonigNativeSeatAdjustment),
                    "Distant guest slider request changed the host seat.");
                Require(Math.Abs(vehicles.TestKonigSeatCustomParameter(0) - beforeSaveForward) < 0.001f &&
                    Math.Abs(vehicles.TestKonigSeatCustomParameter(1) - beforeSaveHeight) < 0.001f,
                    "Distant guest slider request changed the native save parameters.");
                phase = 2;
            }
            else if (phase == 2 && vehicles.TestKonigSeatAdjustment.Generation != before.Generation)
            {
                Require(vehicles.TestKonigState.Driver.Owner != ItemLease.Unowned && vehicles.TestKonigState.Driver.Owner != 0,
                    "Driver forward button was exercised without the guest's driver lease (V-05).");
                accepted = vehicles.TestKonigSeatAdjustment;
                Require(accepted.Generation == VehicleSeatAdjustmentState.NextGeneration(before.Generation) &&
                    Math.Abs(accepted.ForwardCentimeters - before.ForwardCentimeters) == 1 &&
                    accepted.HeightCentimeters == before.HeightCentimeters &&
                    Same(accepted, vehicles.TestKonigNativeSeatAdjustment),
                    "Nearby guest slider request did not apply one native centimeter step and publish it.");
                var nativeSeat = DriverSeat().transform.localPosition;
                float savedForward = vehicles.TestKonigSeatCustomParameter(0), savedHeight = vehicles.TestKonigSeatCustomParameter(1);
                Require(Math.Abs(savedForward - nativeSeat.z) < 0.001f && Math.Abs(savedHeight - nativeSeat.y) < 0.001f,
                    "Host did not update Konig save parameters with its native seat position; saveZ/Y=" +
                    savedForward.ToString("F4") + "/" + savedHeight.ToString("F4") + "; nativeZ/Y=" +
                    nativeSeat.z.ToString("F4") + "/" + nativeSeat.y.ToString("F4"));
                acceptedSaveForward = vehicles.TestKonigSeatCustomParameter(0);
                phase = 3;
            }
            else if (phase == 3 && vehicles.TestKonigSeatAdjustment.Generation != accepted.Generation)
            {
                Require(vehicles.TestKonigState.Driver.Owner != ItemLease.Unowned && vehicles.TestKonigState.Driver.Owner != 0,
                    "Driver height button was exercised without the guest's driver lease (V-05).");
                adjusted = vehicles.TestKonigSeatAdjustment;
                Require(adjusted.Generation == VehicleSeatAdjustmentState.NextGeneration(accepted.Generation) &&
                    adjusted.ForwardCentimeters == accepted.ForwardCentimeters &&
                    adjusted.HeightCentimeters == accepted.HeightCentimeters + heightDirection &&
                    Same(adjusted, vehicles.TestKonigNativeSeatAdjustment),
                    "Nearby guest height click did not apply one native centimeter step and publish it.");
                Require(Math.Abs(vehicles.TestKonigSeatCustomParameter(0) - acceptedSaveForward) < 0.001f &&
                    Math.Abs(vehicles.TestKonigSeatCustomParameter(1) - DriverSeat().transform.localPosition.y) < 0.001f,
                    "Host did not update Konig save parameters after the height adjustment.");
                phase = 4;
            }
            else if (phase == 4 && vehicles.TestKonigPassengerState.Occupied)
            {
                Require(vehicles.TestKonigState.Driver.Owner == ItemLease.Unowned,
                    "Guest passenger entry retained a competing driver lease.");
                Require(vehicles.TestKonigPassengerState.Owner != 0,
                    "Expected the paired guest to own the passenger seat.");
                passengerGuestAdjusted = vehicles.TestKonigPassengerSeatAdjustment;
                if (passengerGuestAdjusted.Generation == passengerBefore.Generation) return false;
                driverBeforePassengerAdjustment = vehicles.TestKonigSeatAdjustment;
                Require(passengerGuestAdjusted.Generation == VehicleSeatAdjustmentState.NextGeneration(passengerBefore.Generation) &&
                    Math.Abs(passengerGuestAdjusted.ForwardCentimeters - passengerBefore.ForwardCentimeters) == 1 &&
                    passengerGuestAdjusted.HeightCentimeters == passengerBefore.HeightCentimeters &&
                    Same(passengerGuestAdjusted, vehicles.TestKonigNativePassengerSeatAdjustment),
                    "Seated guest passenger button did not publish one native centimeter step (V-05).");
                passengerDirection = vehicles.TestKonigPassengerSeatAdjustmentForwardDirection;
                passengerClickSaveForward = vehicles.TestKonigSeatCustomParameter(0);
                passengerClickSaveHeight = vehicles.TestKonigSeatCustomParameter(1);
                phase = 45;
                next = Time.unscaledTime + 1.5f; // let the guest observe its own accepted click before the next native action
            }
            else if (phase == 45 && Time.unscaledTime >= next)
            {
                Require(vehicles.TestKonigPassengerState.Occupied && vehicles.TestKonigPassengerState.Owner != 0,
                    "Guest left the passenger seat before the host adjustment observer step.");
                vehicles.TestClickKonigPassengerSeatAdjustment(VehicleSeatAdjustmentAxis.Forward, passengerDirection);
                phase = 5;
            }
            else if (phase == 5 && vehicles.TestKonigPassengerSeatAdjustment.Generation != passengerGuestAdjusted.Generation)
            {
                passengerAdjusted = vehicles.TestKonigPassengerSeatAdjustment;
                Require(passengerAdjusted.Generation == VehicleSeatAdjustmentState.NextGeneration(passengerGuestAdjusted.Generation) &&
                    passengerAdjusted.ForwardCentimeters == passengerGuestAdjusted.ForwardCentimeters + passengerDirection &&
                    passengerAdjusted.HeightCentimeters == passengerGuestAdjusted.HeightCentimeters &&
                    Same(passengerAdjusted, vehicles.TestKonigNativePassengerSeatAdjustment),
                    "Host passenger forward click did not publish one native centimeter step.");
                Require(Same(driverBeforePassengerAdjustment, vehicles.TestKonigSeatAdjustment) &&
                    Math.Abs(vehicles.TestKonigSeatCustomParameter(0) - passengerClickSaveForward) < 0.001f &&
                    Math.Abs(vehicles.TestKonigSeatCustomParameter(1) - passengerClickSaveHeight) < 0.001f,
                    "Passenger adjustment changed the independent driver seat or its native save parameters.");
                phase = 6;
            }
            else if (phase == 6 && plugin.Session!.Peers.Count == 1)
            {
                Require(!vehicles.TestKonigPassengerState.Occupied && vehicles.TestKonigState.Driver.Owner == ItemLease.Unowned,
                    "Guest disconnect retained a passenger or driver claim.");
                plugin.EndPairedTestSession();
                Require(Same(adjusted, vehicles.TestKonigSeatAdjustment) && Same(adjusted, vehicles.TestKonigNativeSeatAdjustment),
                    "Host authoritative seat adjustment was not retained after disconnect.");
                Require(Same(passengerAdjusted, vehicles.TestKonigPassengerSeatAdjustment) &&
                    Same(passengerAdjusted, vehicles.TestKonigNativePassengerSeatAdjustment),
                    "Host passenger-seat adjustment was not retained after disconnect.");
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            if (phase == 0)
            {
                before = vehicles.TestKonigSeatAdjustment;
                Require(Same(before, vehicles.TestKonigNativeSeatAdjustment), "Initial world snapshot did not apply the host's native seat adjustment.");
                direction = vehicles.TestKonigSeatAdjustmentForwardDirection;
                heightDirection = HeightDirection();
                Move(vehicles.TestKonigSeatForwardPosition + Vector3.right * 30);
                phase = 10;
                next = Time.unscaledTime + 0.7f; // publish the distant pose before the native request
            }
            else if (phase == 10 && Time.unscaledTime >= next)
            {
                float distance = Vector3.Distance(gameplay.PlayerWalking.transform.position, vehicles.TestKonigSeatForwardPosition);
                Require(distance > 20f, "Distant fixture position changed before the native slider click; distance=" + distance.ToString("F2") + "; " + Status);
                vehicles.TestClickKonigSeatAdjustmentForward(direction);
                phase = 1;
                next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 1 && Time.unscaledTime >= next)
            {
                Require(Same(before, vehicles.TestKonigSeatAdjustment) && Same(before, vehicles.TestKonigNativeSeatAdjustment),
                    "Distant guest slider request was accepted locally or by the host.");
                if (!nearForwardPositioned)
                {
                    Move(vehicles.TestKonigDriverPosition + Vector3.right * 1.5f);
                    nearForwardPositioned = true; next = Time.unscaledTime + 0.6f; return false;
                }
                if (vehicles.TestKonigVehicle.isDriverDoorClosed) return false;
                vehicles.TestKonigEnterDriver();
                phase = 11;
                next = Time.unscaledTime + 0.2f;
            }
            else if (phase == 11 && Time.unscaledTime >= next &&
                gameplay.Get_CurrentVehicleController == vehicles.TestKonigVehicle && gameplay.Get_PlayerState == PlayerState.InVehicle_Driver)
            {
                Require(vehicles.TestKonigState.Driver.Owner == plugin.Session!.LocalPeerId && !vehicles.TestKonigPassengerState.Occupied,
                    "Guest entered the driver seat without its exclusive host-authoritative driver lease.");
                driverPositionBefore = DriverSeat().seatPosition.position;
                vehicles.TestClickKonigSeatAdjustmentForward(direction); // seated native MouseDown/MouseRightDown
                phase = 2;
                next = Time.unscaledTime + 0.15f;
            }
            else if (phase == 2 && Time.unscaledTime >= next && vehicles.TestKonigSeatAdjustment.Generation != before.Generation)
            {
                accepted = vehicles.TestKonigSeatAdjustment;
                Require(accepted.Generation == VehicleSeatAdjustmentState.NextGeneration(before.Generation) &&
                    accepted.ForwardCentimeters == before.ForwardCentimeters + direction &&
                    accepted.HeightCentimeters == before.HeightCentimeters &&
                    Same(accepted, vehicles.TestKonigNativeSeatAdjustment),
                    "Guest did not receive the host's native one-step slider update.");
                var heightButton = HeightButton();
                if (!nearHeightPositioned)
                {
                    Require(vehicles.TestKonigState.Driver.Owner == plugin.Session!.LocalPeerId &&
                        gameplay.Get_PlayerState == PlayerState.InVehicle_Driver,
                        "Forward adjustment evicted the seated guest driver.");
                    Require((vehicles.TestKonigWalkerPosition - DriverSeat().seatPosition.position).sqrMagnitude < 0.000001f &&
                        (DriverSeat().seatPosition.position - driverPositionBefore).sqrMagnitude > 0.00000001f,
                        "Seated driver forward snapshot did not reposition the guest avatar.");
                    nearHeightPositioned = true; next = Time.unscaledTime + 0.6f; return false;
                }
                if (heightDirection > 0) heightButton.MouseDown(); else heightButton.MouseRightDown();
                phase = 3;
                next = Time.unscaledTime + 0.15f;
            }
            else if (phase == 3 && Time.unscaledTime >= next && vehicles.TestKonigSeatAdjustment.Generation != accepted.Generation)
            {
                adjusted = vehicles.TestKonigSeatAdjustment;
                Require(adjusted.Generation == VehicleSeatAdjustmentState.NextGeneration(accepted.Generation) &&
                    adjusted.ForwardCentimeters == accepted.ForwardCentimeters &&
                    adjusted.HeightCentimeters == accepted.HeightCentimeters + heightDirection &&
                    Same(adjusted, vehicles.TestKonigNativeSeatAdjustment),
                    "Guest did not receive the host's native one-step height adjustment.");
                Require(vehicles.TestKonigState.Driver.Owner == plugin.Session!.LocalPeerId &&
                    gameplay.Get_PlayerState == PlayerState.InVehicle_Driver,
                    "Height adjustment evicted the seated guest driver.");
                phase = 30; next = Time.unscaledTime + 1f; // retain the seated lease until the host observes this generation
            }
            else if (phase == 30 && Time.unscaledTime >= next)
            {
                vehicles.TestKonigExitDriver();
                phase = 31;
            }
            else if (phase == 31 && gameplay.Get_PlayerState == PlayerState.Walking &&
                vehicles.TestKonigState.Driver.Owner == ItemLease.Unowned)
            {
                passengerBefore = vehicles.TestKonigPassengerSeatAdjustment;
                Require(Same(passengerBefore, vehicles.TestKonigNativePassengerSeatAdjustment),
                    "Initial world snapshot did not apply the host's passenger seat adjustment.");
                passengerDirection = vehicles.TestKonigPassengerSeatAdjustmentForwardDirection;
                passengerPositionBefore = vehicles.TestKonigPassengerSeatTargetPosition;
                Move(vehicles.TestKonigPassengerPosition + Vector3.right * 1.5f);
                phase = 32; next = Time.unscaledTime + 0.75f;
            }
            else if (phase == 32 && Time.unscaledTime >= next && !vehicles.TestKonigVehicle.isPassengerDoorClosed)
            {
                vehicles.TestKonigEnterPassenger();
                phase = 4;
                next = Time.unscaledTime + 0.2f;
            }
            else if (phase == 4 && Time.unscaledTime >= next && gameplay.Get_CurrentVehicleController == vehicles.TestKonigVehicle &&
                gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger)
            {
                Require(vehicles.TestKonigPassengerState.Owner == plugin.Session!.LocalPeerId,
                    "Guest entered the Konig passenger seat without owning its host-authoritative passenger lease.");
                vehicles.TestClickKonigPassengerSeatAdjustment(VehicleSeatAdjustmentAxis.Forward, passengerDirection);
                phase = 5;
                next = Time.unscaledTime + 0.15f;
            }
            else if (phase == 5 && Time.unscaledTime >= next && vehicles.TestKonigPassengerSeatAdjustment.Generation != passengerBefore.Generation)
            {
                passengerGuestAdjusted = vehicles.TestKonigPassengerSeatAdjustment;
                Require(passengerGuestAdjusted.Generation == VehicleSeatAdjustmentState.NextGeneration(passengerBefore.Generation),
                    "Seated guest passenger click skipped its authoritative one-step generation.");
                Require(passengerGuestAdjusted.ForwardCentimeters == passengerBefore.ForwardCentimeters + passengerDirection &&
                    passengerGuestAdjusted.HeightCentimeters == passengerBefore.HeightCentimeters &&
                    Same(passengerGuestAdjusted, vehicles.TestKonigNativePassengerSeatAdjustment),
                    "Guest did not receive its seated native passenger forward adjustment (V-05).");
                Require(Same(adjusted, vehicles.TestKonigSeatAdjustment) && Same(adjusted, vehicles.TestKonigNativeSeatAdjustment),
                    "Passenger adjustment changed the independent driver seat snapshot.");
                Require((vehicles.TestKonigWalkerPosition - vehicles.TestKonigPassengerSeatTargetPosition).sqrMagnitude < 0.000001f &&
                    (vehicles.TestKonigPassengerSeatTargetPosition - passengerPositionBefore).sqrMagnitude > 0.00000001f,
                    "Passenger seat snapshot did not reposition the locally seated guest avatar.");
                phase = 6;
            }
            else if (phase == 6 && vehicles.TestKonigPassengerSeatAdjustment.Generation != passengerGuestAdjusted.Generation)
            {
                passengerAdjusted = vehicles.TestKonigPassengerSeatAdjustment;
                Require(passengerAdjusted.Generation == VehicleSeatAdjustmentState.NextGeneration(passengerGuestAdjusted.Generation) &&
                    Math.Abs(passengerAdjusted.ForwardCentimeters - passengerGuestAdjusted.ForwardCentimeters) == 1 &&
                    passengerAdjusted.HeightCentimeters == passengerGuestAdjusted.HeightCentimeters &&
                    Same(passengerAdjusted, vehicles.TestKonigNativePassengerSeatAdjustment) &&
                    Same(adjusted, vehicles.TestKonigSeatAdjustment) &&
                    vehicles.TestKonigPassengerState.Owner == plugin.Session!.LocalPeerId &&
                    gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger,
                    "Host passenger click did not preserve the seated guest and the independent driver record.");
                plugin.EndPairedTestSession();
                Require(Same(vehicles.TestKonigOriginalSeatAdjustment, vehicles.TestKonigSeatAdjustment) &&
                    Same(vehicles.TestKonigOriginalSeatAdjustment, vehicles.TestKonigNativeSeatAdjustment),
                    "Guest disconnect did not restore its local seat adjustment baseline.");
                Require((DriverSeat().transform.localPosition - vehicles.TestKonigOriginalSeatLocalPosition).sqrMagnitude < 0.000001f,
                    "Guest disconnect did not restore the exact local seat pose.");
                Require(Same(vehicles.TestKonigOriginalPassengerSeatAdjustment, vehicles.TestKonigPassengerSeatAdjustment) &&
                    Same(vehicles.TestKonigOriginalPassengerSeatAdjustment, vehicles.TestKonigNativePassengerSeatAdjustment) &&
                    (PassengerSeat().transform.localPosition - vehicles.TestKonigOriginalPassengerSeatLocalPosition).sqrMagnitude < 0.000001f,
                    "Guest disconnect did not restore the passenger seat state and exact local pose.");
                Require(Math.Abs(vehicles.TestKonigSeatCustomParameter(0) - vehicles.TestKonigOriginalSeatCustomParameter(0)) < 0.001f &&
                    Math.Abs(vehicles.TestKonigSeatCustomParameter(1) - vehicles.TestKonigOriginalSeatCustomParameter(1)) < 0.001f,
                    "Guest disconnect did not restore its native save parameters.");
                return true;
            }
            return false;
        }

        private void Move(Vector3 location)
        {
            gameplay.PlayerWalking.transform.position = location;
            gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        private int HeightDirection()
        {
            var seat = DriverSeat();
            float current = seat.transform.localPosition.y;
            float minimum = (float)AccessTools.Field(typeof(Konig_Seat), "MinSeatHeight")!.GetValue(seat)!;
            float maximum = (float)AccessTools.Field(typeof(Konig_Seat), "MaxSeatHeight")!.GetValue(seat)!;
            if (current + 0.009f <= maximum) return 1;
            if (current - 0.009f >= minimum) return -1;
            throw new InvalidOperationException("Konig height slider has no legal one-step direction.");
        }

        private Konig_Seat DriverSeat() => vehicles.TestKonigVehicle.GetComponentsInChildren<Konig_Seat>(true)
            .Single(seat => seat.Get_isDriverSeat);

        private Konig_Seat PassengerSeat() => vehicles.TestKonigVehicle.GetComponentsInChildren<Konig_Seat>(true)
            .Single(seat => !seat.Get_isDriverSeat);

        private Part_Seat_Height HeightButton() => vehicles.TestKonigSeatHeightButton;

        private static bool Same(VehicleSeatAdjustmentState a, VehicleSeatAdjustmentState b) =>
            a.ForwardCentimeters == b.ForwardCentimeters && a.HeightCentimeters == b.HeightCentimeters && a.Generation == b.Generation;

        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Paired Konig seat adjustment test: " + message); }
    }
}
