using System;
using System.Linq;
using System.IO;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Focused two-copy exercise of a moving Konig with one driver and one passenger.
    internal sealed class PairedKonigSeatsTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedVehicles vehicles;
        private int phase;
        private float next, deadline, presentationDeadline;
        private uint guest, firstPassengerGeneration;
        private Vector3 startedAt;
        private HoverProbe? hover;
        private float savedAnxiety;
        private ulong staleCrashTarget, otherCrashTarget;
        private int beforeCrashDelivery, beforeOtherDelivery;
        private readonly string releaseEvidence = Path.Combine(BepInEx.Paths.BepInExRootPath, "MonMP", "diagnostics", "konig-passenger-exit.txt");

        internal PairedKonigSeatsTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay; vehicles = plugin.Vehicles!;
            vehicles.TestEnableDriverEntryDiagnostics();
            NativeRegistryTest.Verify(plugin.World!);
            var parts = vehicles.TestKonigVehicle.vehicle_PartsManager;
            Require(parts.Seat_Left.isInstalled && parts.Seat_Right.isInstalled, "Both Konig front seats must be installed in the isolated fixture.");
        }

        internal string Status => "Konig seats phase=" + phase + "; driver=" + vehicles.TestKonigState.Driver.Owner +
            "; passenger=" + vehicles.TestKonigPassengerState.Owner + "/" + vehicles.TestKonigPassengerState.Generation +
            "; player=" + gameplay.Get_PlayerState + "; moved=" + Vector3.Distance(startedAt, vehicles.TestKonigVehicle.transform.position).ToString("F2") +
            "; driverDoorClosed=" + vehicles.TestKonigVehicle.isDriverDoorClosed + "; passengerDoorClosed=" + vehicles.TestKonigVehicle.isPassengerDoorClosed +
            "; exitPending=" + ExitPending() + "; currentMatch=" + (gameplay.Get_CurrentVehicleController == vehicles.TestKonigVehicle) + "; entry=" + vehicles.TestDriverEntryDiagnostic;

        internal bool Tick()
        {
            if (!vehicles.Ready || plugin.World?.Channel.FullySynchronized != true || Time.unscaledTime < next) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 110;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Konig seat test stalled: " + Status);
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            var doors = vehicles.Doors;
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                guest = plugin.Session.Peers.Keys.First(p => p != 0);
                startedAt = vehicles.TestKonigVehicle.transform.position;
                Move(doors.TestKonigPosition()); phase = 1; next = Time.unscaledTime + 0.3f;
            }
            else if (phase == 1)
            {
                if (!doors.TestKonigState().Open) doors.TestKonigClick();
                phase = 2; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 2 && doors.TestKonigState().Open)
            {
                Move(doors.TestKonigPosition(false)); phase = 3; next = Time.unscaledTime + 0.3f;
            }
            else if (phase == 3)
            {
                if (!doors.TestKonigState(false).Open) doors.TestKonigClick(false);
                phase = 4; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 4 && doors.TestKonigState(false).Open)
            {
                Require(doors.TestKonigNative().Open && doors.TestKonigNative(false).Open, "Both native doors did not open.");
                phase = 5;
            }
            else if (phase == 5 && vehicles.TestKonigPassengerState.Owner == guest)
            {
                firstPassengerGeneration = vehicles.TestKonigPassengerState.Generation;
                Require(vehicles.SeatFor(guest) != null, "Passenger reservation has no moving seat anchor.");
                Move(vehicles.TestKonigPassengerPosition + Vector3.right * 1.5f);
                vehicles.TestKonigEnterPassenger();
                Require(vehicles.TestKonigPassengerState.Owner == guest && gameplay.Get_PlayerState == PlayerState.Walking,
                    "Host displaced the guest passenger.");
                Move(vehicles.TestKonigDriverPosition + Vector3.right * 1.5f);
                phase = 6; next = Time.unscaledTime + 0.3f;
            }
            else if (phase == 6)
            {
                vehicles.TestKonigEnterDriver(); phase = 7;
            }
            else if (phase == 7 && vehicles.TestKonigState.Driver.Owner == 0)
            {
                Require(gameplay.Get_CurrentVehicleController == vehicles.TestKonigVehicle && gameplay.Get_PlayerState == PlayerState.InVehicle_Driver,
                    "Host native driver entry failed.");
                Require(vehicles.TestKonigPassengerState.Owner == guest, "Driver entry displaced the passenger.");
                var v = vehicles.TestKonigVehicle;
                var destination = v.vehicleRigidbody.position + v.transform.forward * 1.5f;
                v.vehicleRigidbody.position = destination;
                v.transform.position = destination;
                v.vehicleRigidbody.velocity = Vector3.zero;
                phase = 8; next = Time.unscaledTime + 0.75f;
                presentationDeadline = Time.unscaledTime + 2;
            }
            else if (phase == 7 && gameplay.Get_PlayerState == PlayerState.Walking)
            {
                Move(vehicles.TestKonigDriverPosition + Vector3.right * 1.5f);
                if (vehicles.TestKonigVehicle.isDriverDoorClosed)
                { Move(doors.TestKonigPosition()); if (!doors.TestKonigState().Open) doors.TestKonigClick(); }
                else vehicles.TestKonigEnterDriver();
                next = Time.unscaledTime + .5f;
            }
            else if (phase == 8)
            {
                Require(Vector3.Distance(startedAt, vehicles.TestKonigVehicle.transform.position) > 0.5f,
                    "Authoritative Konig body did not move.");
                var avatar = plugin.RemoteAvatarRoot(guest);
                var anchor = vehicles.SeatFor(guest);
                float gap = avatar != null && anchor != null ? Vector3.Distance(avatar.position, anchor.position) : float.PositiveInfinity;
                if (vehicles.TestKonigPassengerState.Owner != guest || avatar == null || anchor == null || gap >= 0.5f)
                {
                    if (Time.unscaledTime < presentationDeadline) return false;
                    throw new InvalidOperationException("Paired Konig seats test: Guest avatar did not follow the moving passenger seat. " +
                        "owner=" + vehicles.TestKonigPassengerState.Owner + "/" + vehicles.TestKonigPassengerState.Generation +
                        "; guestPose=" + vehicles.TestKonigPeerPose(guest) + "; avatar=" +
                        (avatar == null ? "missing" : avatar.position.ToString()) + "; anchor=" +
                        (anchor == null ? "missing" : anchor.position.ToString()) + "; gap=" + gap.ToString("F2"));
                }
                phase = 9;
            }
            else if (phase == 9 && vehicles.TestKonigPassengerState.Owner == SeatOccupancy.Unoccupied)
            {
                uint releasedGeneration = vehicles.TestKonigPassengerState.Generation;
                vehicles.TestKonigStalePassengerExit(guest, firstPassengerGeneration);
                Require(vehicles.TestKonigPassengerState.Owner == SeatOccupancy.Unoccupied &&
                    vehicles.TestKonigPassengerState.Generation == releasedGeneration,
                    "Stale passenger exit changed the vacancy generation.");
                ulong target = Target(Vehicle_Type.MainVehicle);
                // Deliberately inject a stale host delivery after the real exit.
                // Physical collisions are covered by AnxietyGameplay; this probes
                // the actual Unity/network recipient guard against a stale lease.
                plugin.World!.Events!.HostAnnounce(PresentationKind.VehicleCrash, 0x81, target,
                    vehicles.TestKonigVehicle.transform.position, 20, guest, false);
                File.WriteAllText(releaseEvidence, "Host observed passenger release generation " + releasedGeneration + "; injected stale heavy-crash delivery.\n");
                phase = 10;
            }
            else if (phase == 10 && vehicles.TestKonigPassengerState.Owner == guest)
            {
                Require(vehicles.TestKonigPassengerState.Generation > firstPassengerGeneration,
                    "Second passenger claim reused a stale generation.");
                phase = 101; next = Time.unscaledTime + .75f;
            }
            else if (phase == 101)
            {
                var other = UnityEngine.Object.FindObjectsOfType<NWH.VehiclePhysics2.VehicleController>()
                    .Single(car => car.vehicle_type == Vehicle_Type.OlTruck);
                ulong target = Target(Vehicle_Type.OlTruck);
                plugin.World!.Events!.HostAnnounce(PresentationKind.VehicleCrash, 0x81, target, other.transform.position, 20, guest, false);
                File.AppendAllText(releaseEvidence, "Injected unrelated truck crash while guest occupies the Konig.\n");
                phase = 11;
            }
            else if (phase == 11 && plugin.Session!.Peers.Count == 1)
            {
                Require(!vehicles.TestKonigPassengerState.Occupied && vehicles.TestKonigState.Driver.Owner == 0,
                    "Disconnect did not release only the passenger while preserving the host driver.");
                vehicles.TestKonigExitDriver(); phase = 12;
            }
            else if (phase == 12 && vehicles.TestKonigState.Driver.Owner == ItemLease.Unowned)
            {
                plugin.EndPairedTestSession();
                Require(gameplay.Get_PlayerState == PlayerState.Walking, "Host did not return to walking on exit.");
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            var v = vehicles.TestKonigVehicle;
            if (phase == 0)
            {
                if (!vehicles.Doors.TestKonigState().Open || !vehicles.Doors.TestKonigState(false).Open) return false;
                startedAt = v.transform.position;
                Move(vehicles.TestKonigPassengerPosition + Vector3.right * 30);
                phase = 1; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 1)
            {
                vehicles.TestKonigEnterPassenger(); phase = 2; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 2)
            {
                Require(!vehicles.TestKonigPassengerState.Occupied && gameplay.Get_PlayerState == PlayerState.Walking,
                    "Distant guest acquired the passenger seat.");
                // #164: stand in the open passenger door, looking at the parking brake.
                Move(vehicles.TestKonigPassengerPosition + v.transform.right * 0.9f);
                phase = 30; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 30 || phase == 32)
            {
                var brake = v.vehicle_PartsManager.ParkingBrake;
                Require(brake != null, "The Konig has no parking brake.");
                hover = new HoverProbe(gameplay, brake!, phase == 30 ? "walking" : "passenger",
                    phase == 30 ? brake!.transform.position + v.transform.right * 1.1f + Vector3.up * 0.45f : (Vector3?)null);
                phase++;
            }
            else if (phase == 31 || phase == 33)
            {
                if (!hover!.Tick(out string report)) return false;
                hover.Dispose(); hover = null;
                Debug.Log("MonMP hover probe (#164) " + report);
                if (phase == 31) { Move(vehicles.TestKonigPassengerPosition + Vector3.right * 1.5f); phase = 3; next = Time.unscaledTime + 0.8f; }
                else phase = 5;
            }
            else if (phase == 3)
            {
                vehicles.TestKonigEnterPassenger(); phase = 4;
            }
            else if (phase == 4 && vehicles.TestKonigPassengerState.Owner == plugin.Session!.LocalPeerId)
            {
                Require(gameplay.Get_CurrentVehicleController == v && gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger &&
                    gameplay.PlayerWalking.transform.parent == v.transform, "Guest passenger grant did not enter the native seat.");
                firstPassengerGeneration = vehicles.TestKonigPassengerState.Generation;
                phase = 32; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 5 && vehicles.TestKonigState.Driver.Owner == 0 &&
                Vector3.Distance(startedAt, v.transform.position) > 0.5f)
            {
                Require(gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger &&
                    gameplay.PlayerWalking.transform.parent == v.transform && vehicles.SeatFor(0) != null,
                    "Moving driver update ejected or detached the guest passenger.");
                var avatar = plugin.RemoteAvatarRoot(0);
                Require(avatar != null && Vector3.Distance(avatar.transform.position, vehicles.SeatFor(0)!.position) < 0.5f,
                    "Host driver avatar did not follow its seat on the moving Konig.");
                // Let the host inspect the avatar after its own vehicle/pose update.
                // The earlier fixture exited immediately and vacated the anchor first.
                phase = 55; next = Time.unscaledTime + 3;
            }
            else if (phase == 55)
            {
                var needs = gameplay.GetComponent<BasicNeeds_Manager>(); savedAnxiety = needs.GetAnxiety; needs.GetAnxiety = .4f;
                staleCrashTarget = Target(Vehicle_Type.MainVehicle);
                beforeCrashDelivery = plugin.World!.Events!.TestPlayed;
                // A valid but old generation reproduces the rejected-exit race
                // without changing the host registry or bypassing native exit.
                var binding = AccessTools.Property(typeof(SharedVehicles), "TestKonigBinding").GetValue(vehicles);
                var state = vehicles.TestKonigPassengerState;
                Require(state.Generation > 1, "Passenger grant has no prior generation.");
                AccessTools.Field(binding.GetType(), "PassengerState").SetValue(binding,
                    new VehiclePassengerSeatState(state.Owner, state.Generation - 1));
                vehicles.TestKonigExitPassenger(); phase = 6;
            }
            else if (phase == 6 && !vehicles.TestKonigPassengerState.Occupied)
            {
                Require(gameplay.Get_PlayerState == PlayerState.Walking, "Passenger exit did not restore native walking.");
                Move(vehicles.TestKonigPassengerPosition + Vector3.right * 1.5f);
                phase = 7; next = Time.unscaledTime + 1;
            }
            else if (phase == 6 && !ExitPending())
            {
                // The door's physical open gate can reject a click while its
                // hinge is still settling. Retry the real exit until admitted;
                // once pending, production retry/reconciliation owns recovery.
                if (!vehicles.Doors.TestKonigState(false).Open) vehicles.Doors.TestKonigClick(false);
                vehicles.TestKonigExitPassenger(); next = Time.unscaledTime + .5f;
            }
            else if (phase == 7)
            {
                float anxiety = gameplay.GetComponent<BasicNeeds_Manager>().GetAnxiety;
                Require(ReceivedCrash(staleCrashTarget) && plugin.World!.Events!.TestPlayed > beforeCrashDelivery,
                    "Stale crash packet was not observed in the actual client event stream.");
                Require(anxiety >= .399f && anxiety < .41f, "Stale host crash delivery changed an exited passenger's anxiety: " + anxiety);
                File.WriteAllText(releaseEvidence, "Recovered stale-generation exit; native walking restored; stale crash delivery left anxiety=" + anxiety + ".\n");
                vehicles.TestKonigEnterPassenger(); phase = 8;
            }
            else if (phase == 8 && vehicles.TestKonigPassengerState.Owner == plugin.Session!.LocalPeerId)
            {
                Require(vehicles.TestKonigPassengerState.Generation > firstPassengerGeneration &&
                    gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger,
                    "Passenger could not re-enter after a released claim.");
                // Native entry destroys the old walker Rigidbody at frame end. ExitLocal
                // deliberately defers a same-frame exit, so observe rollback later.
                var other = UnityEngine.Object.FindObjectsOfType<NWH.VehiclePhysics2.VehicleController>()
                    .Single(car => car.vehicle_type == Vehicle_Type.OlTruck);
                otherCrashTarget = Target(Vehicle_Type.OlTruck);
                beforeOtherDelivery = plugin.World!.Events!.TestPlayed;
                phase = 86; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 86)
            {
                Require(ReceivedCrash(otherCrashTarget) && plugin.World!.Events!.TestPlayed > beforeOtherDelivery,
                    "Other-car crash packet did not reach the client event stream.");
                Require(gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger &&
                    gameplay.Get_CurrentVehicleController == v && gameplay.GetComponent<BasicNeeds_Manager>().GetAnxiety < .41f,
                    "Unrelated truck crash affected the Konig passenger's anxiety.");
                File.AppendAllText(releaseEvidence, "Unrelated truck crash received while in Konig; anxiety unchanged.\n");
                // Suppress only MonMP's native-exit notification. The real native
                // method still restores walking; the new watchdog must release it.
                var flag = AccessTools.Field(typeof(SharedVehicles), "applying");
                flag.SetValue(vehicles, true);
                try { AccessTools.Method(typeof(Gameplay), "Force_ExitVehicle").Invoke(gameplay, null); }
                finally { flag.SetValue(vehicles, false); }
                phase = 87;
            }
            else if (phase == 87 && !vehicles.TestKonigPassengerState.Occupied)
            {
                Require(gameplay.Get_PlayerState == PlayerState.Walking, "Watchdog did not retain native walking.");
                File.AppendAllText(releaseEvidence, "Missed NativeExited notification recovered through the ownership watchdog.\n");
                Move(vehicles.TestKonigPassengerPosition + Vector3.right * 1.5f); phase = 88; next = Time.unscaledTime + .8f;
            }
            else if (phase == 88) { vehicles.TestKonigEnterPassenger(); phase = 89; }
            else if (phase == 89 && vehicles.TestKonigPassengerState.Owner == plugin.Session!.LocalPeerId)
            {
                Require(gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger, "Could not re-enter after watchdog release.");
                phase = 9; next = Time.unscaledTime + .2f;
            }
            else if (phase == 9)
            {
                gameplay.GetComponent<BasicNeeds_Manager>().GetAnxiety = savedAnxiety;
                plugin.EndPairedTestSession();
                Require(gameplay.Get_PlayerState == PlayerState.Walking &&
                    v.VehicleMultiplayerInstanceType == NWH.Vehicle.MultiplayerInstanceType.Local,
                    "Guest disconnect did not restore native walking and vehicle physics.");
                return true;
            }
            return false;
        }

        private void Move(Vector3 position)
        {
            gameplay.PlayerWalking.transform.position = position;
            gameplay.Get_Player_Rigidbody.position = position;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        private ulong Target(Vehicle_Type type)
        {
            var previous = vehicles.TestType;
            try { vehicles.TestType = type; return vehicles.TestId; }
            finally { vehicles.TestType = previous; }
        }

        private bool ExitPending()
        {
            var binding = AccessTools.Property(typeof(SharedVehicles), "TestKonigBinding").GetValue(vehicles);
            return (bool)AccessTools.Field(binding.GetType(), "PassengerExitPending").GetValue(binding);
        }

        private bool ReceivedCrash(ulong target) => plugin.World!.Channel.Records.TryGetValue(SharedPresentationEvents.Entity, out var record) &&
            PresentationEvent.DecodeLog(record.Payload).Any(e => e.Target == target && e.Actor == plugin.Session!.LocalPeerId && e.Variant == 0x81 && e.Value == 20);

        private static void Require(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Paired Konig seats test: " + reason);
        }
    }
}
