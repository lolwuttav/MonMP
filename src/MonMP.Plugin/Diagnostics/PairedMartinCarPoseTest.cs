using System;
using System.IO;
using System.Linq;
using BepInEx;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedMartinCarPoseTest
    {
        private const float TimeoutSeconds = 120;
        private readonly MonMPPlugin plugin;
        private readonly SharedMartinCar car;
        private readonly bool host;
        private readonly ulong entity;
        private readonly float deadline;
        private readonly string resultPath;
        private readonly Gameplay gameplay;
        private readonly NWH.VehiclePhysics2.VehicleController nativeVehicle;
        private readonly Vehicle_PassengerSeat passengerSeat;
        private readonly Vehicle_DoorHandle passengerDoor;
        private int passengerPhase;
        private uint passengerGuest, firstPassengerGeneration;
        private float nextPassengerAction;
        private bool passengerApproached;
        private int initialUpdates;
        private int baselineInterpolationFrames;
        private int baselineWheelInterpolationFrames;
        private MartinCarState? stagedGuestState;
        private MartinCarState? baselineGuestState;
        private bool readyAnnounced;
        private bool tickSleepGuardChecked;
        private float lastDiagnosticAt = -1f;
        private float phaseStarted;
        private int phase;
        internal string Status { get; private set; } = "Waiting for the Martin car pose catalog.";

        internal PairedMartinCarPoseTest(MonMPPlugin plugin)
        {
            this.plugin = plugin;
            host = plugin.TestOptions.IsHost;
            car = plugin.World?.MartinCar ?? throw new InvalidOperationException("Martin car adapter is not attached.");
            gameplay = UnityEngine.Object.FindObjectOfType<Gameplay>();
            nativeVehicle = UnityEngine.Object.FindObjectsOfType<NWH.VehiclePhysics2.VehicleController>(true).Single(v => car.Handles(v));
            passengerSeat = nativeVehicle.GetComponentsInChildren<Vehicle_PassengerSeat>(true).Single();
            passengerDoor = nativeVehicle.GetComponentsInChildren<Vehicle_DoorHandle>(true)
                .OrderBy(handle => RuntimeProbe.CanonicalPath(handle.transform), StringComparer.Ordinal)
                .First(handle => handle.vehicle_Door != null && !handle.vehicle_Door.isDriverDoor);
            entity = car.TestEntity;
            if (entity == 0) throw new InvalidOperationException("No supported native Martin car binding exists in this isolated save.");
            deadline = Time.unscaledTime + TimeoutSeconds;
            resultPath = Path.Combine(Paths.BepInExRootPath, "MonMP", "diagnostics", "paired-martin-car-pose.txt");
            File.WriteAllText(resultPath, "RUNNING: host-authoritative Martin car route pose and visual presentation");
        }

        internal bool Tick()
        {
            try
            {
                if (Time.unscaledTime > deadline)
                    throw new InvalidOperationException("Martin car pose fixture timed out: " + Status);
                return host ? TickHost() : TickGuest();
            }
            catch (Exception ex)
            {
                if (host && car.TestHasStagedPose(entity))
                {
                    bool restored = car.TestRestoreStagedPose(entity);
                    Status = ex.Message + "; staged host payload cleanup=" + restored + "; " + HostDiagnostics();
                    File.WriteAllText(resultPath, "FAILED: " + Status);
                }
                throw;
            }
        }

        private bool TickHost()
        {
            var session = plugin.Session;
            if (phase == 2 && !tickSleepGuardChecked)
            {
                string sleepGuard = car.TestRemoteTickSleepGuard();
                if (sleepGuard.StartsWith("skip:", StringComparison.Ordinal))
                { Status = "Waiting to run the Martin car Tick sleep assertion: " + sleepGuard; return false; }
                else if (sleepGuard.Length != 0)
                    throw new InvalidOperationException("Martin car Tick sleep guard: " + sleepGuard + " (#149).");
                else
                    Debug.Log("MonMP Martin car Tick sleep guard: fresh remote presence vetoed only Tick's sleep coroutine; tick-300 night-light update remained active, stale/boundary poses did not suppress, and accelerated time was preserved.");
                tickSleepGuardChecked = true;
            }
            if (phase == 2 && session?.IsRunning == true && session.Peers.Count <= 1)
            {
                var passenger = car.TestState(entity)!.Passenger;
                if (passengerPhase != 4 || passenger.Occupied || car.IsOwner(passengerGuest))
                    throw new InvalidOperationException("Martin guest disconnected before both native claims, flush and authoritative release completed; passengerPhase=" + passengerPhase);
                Status = "Martin interpolation/restoration, exclusive native passenger grant, host contender rejection, native ownership flush and second-ride disconnect cleanup completed.";
                File.WriteAllText(resultPath, "MONMP_MARTIN_CAR_POSE_PASS\n" + Status);
                return true;
            }
            if (session?.IsRunning != true || plugin.World?.PersonalProfileReady != true || session.Peers.Count < 2)
            {
                SetWaitingStatus("Waiting for a paired guest and synchronized world profile", HostDiagnostics());
                return false;
            }

            if (phase == 0)
            {
                if (!car.TestAllGuestsReady)
                {
                    SetWaitingStatus("Waiting for every guest to apply the baseline world snapshot and report AI suppression", HostDiagnostics());
                    return false;
                }
                if (!car.TestStagePose(entity)) { Status = "Waiting to stage a reversible host Martin car sample."; return false; }
                phase = 1; phaseStarted = Time.unscaledTime;
                Status = "Host staged a test-only route pose, run-visual mask and wheel sample; waiting for guest pose and wheel interpolation acknowledgment.";
                File.WriteAllText(resultPath, "RUNNING: waiting for guest interpolation acknowledgment");
            }

            if (phase == 1)
            {
                if (car.TestPoseAcknowledgmentCount > 0)
                {
                    if (!car.TestRestoreStagedPose(entity)) throw new InvalidOperationException("Host Martin car fixture state could not be restored.");
                    phase = 2; phaseStarted = Time.unscaledTime;
                    Status = "Guest acknowledged the interpolated Martin car pose; host restored the native capture. " + HostDiagnostics();
                    File.WriteAllText(resultPath, "RUNNING: waiting for guest to receive the restored host capture");
                }
                else if (Time.unscaledTime - phaseStarted > 20f)
                    throw new InvalidOperationException("Guest did not acknowledge the staged Martin car pose. " + HostDiagnostics());
                return false;
            }

            if (phase == 2)
            {
                TickHostPassenger();
                return false;
            }

            if (session.Peers.Count > 1) return false;
            Status = "Host-authoritative Martin car pose and visual sample were guest-acknowledged and the host fixture payload was restored.";
            File.WriteAllText(resultPath, "MONMP_MARTIN_CAR_POSE_PASS\n" + Status);
            return true;
        }

        private bool TickGuest()
        {
            var origin = car.TestOriginalVehiclePosition(entity);
            var state = car.TestState(entity);
            var position = car.TestVehiclePosition(entity);

            if (phase == 0)
            {
                if (!car.TestRequestReady(entity))
                {
                    SetWaitingStatus("Waiting for baseline snapshot and guest AI suppression", GuestDiagnostics(origin, state));
                    return false;
                }
                if (!readyAnnounced)
                {
                    readyAnnounced = true;
                    baselineGuestState = state;
                    baselineInterpolationFrames = car.InterpolationFrames;
                    baselineWheelInterpolationFrames = car.WheelInterpolationFrames;
                    phaseStarted = Time.unscaledTime;
                    Status = "Baseline snapshot applied; guest-ready request sent to host. " + GuestDiagnostics(origin, state);
                    lastDiagnosticAt = Time.unscaledTime;
                    return false;
                }

                if (origin == null || state == null || position == null || baselineGuestState == null)
                {
                    SetWaitingStatus("Waiting for guest Martin car baseline values", GuestDiagnostics(origin, state));
                    return false;
                }
                float stagedDistance = Vector3.Distance(
                    new Vector3(state.VehiclePose.X, state.VehiclePose.Y, state.VehiclePose.Z),
                    new Vector3(baselineGuestState.VehiclePose.X, baselineGuestState.VehiclePose.Y, baselineGuestState.VehiclePose.Z));
                bool fixtureSampleSeen = stagedDistance >= 2f && state.WheelSpin.Equals(new VehicleWheelSpin(52, -44, 20, -16));
                bool poseInterpolated = car.InterpolationFrames > baselineInterpolationFrames;
                bool wheelsInterpolated = car.WheelInterpolationFrames > baselineWheelInterpolationFrames;
                bool matches = car.TestReplicaMatchesHost(entity, 0.15f);
                if (!fixtureSampleSeen || !poseInterpolated || !wheelsInterpolated)
                {
                    SetWaitingStatus("Guest is ready; waiting for the staged host sample to cross pose and wheel interpolation",
                        GuestDiagnostics(origin, state) + "; stagedDelta=" + stagedDistance.ToString("F2") + "m; fixtureSample=" + fixtureSampleSeen);
                    if (Time.unscaledTime - phaseStarted > 25f)
                        throw new InvalidOperationException("Guest-ready handshake completed, but staged pose/wheel interpolation was not observed. " + Status);
                    return false;
                }
                if (!matches)
                {
                    SetWaitingStatus("Guest is following the staged host pose and visual mask; waiting for interpolation to settle",
                        GuestDiagnostics(origin, state));
                    if (Time.unscaledTime - phaseStarted > 25f)
                        throw new InvalidOperationException("Staged Martin car interpolation did not settle. " + Status);
                    return false;
                }
                car.TestAcknowledgePose(entity);
                initialUpdates = car.ReceivedPoseUpdates;
                stagedGuestState = state;
                phase = 1; phaseStarted = Time.unscaledTime;
                Status = "Guest AI stayed suppressed; pose and wheel samples interpolated, and host state and run visuals match. " + GuestDiagnostics(origin, state);
                File.WriteAllText(resultPath, "RUNNING: interpolation verified; waiting for host restoration snapshot");
            }

            if (phase == 1)
            {
                state = car.TestState(entity);
                if (state == null || car.ReceivedPoseUpdates <= initialUpdates ||
                    state.Equals(stagedGuestState) || !car.TestReplicaMatchesHost(entity, 0.15f))
                {
                    SetWaitingStatus("Waiting for the host's restored pose and visual mask", GuestDiagnostics(origin, state));
                    if (Time.unscaledTime - phaseStarted > 20f) throw new InvalidOperationException(Status);
                    return false;
                }
                phase = 3;
                nextPassengerAction = Time.unscaledTime + 0.5f;
                Status = "Host pose restored; approaching Martin's actual native passenger seat for explicit lease and flush checks (ST-04).";
            }
            if (phase == 3 || phase == 7)
            {
                if (Time.unscaledTime < nextPassengerAction || !passengerSeat.isActiveAndEnabled || !passengerSeat.CanUse()) return false;
                if (!passengerApproached)
                {
                    Move(passengerSeat.seatPosition.position + nativeVehicle.transform.right * 1.5f);
                    passengerApproached = true;
                    nextPassengerAction = Time.unscaledTime + 0.75f; // publish a fresh walking pose before the native click
                    return false;
                }
                passengerSeat.MouseDown();
                phase = phase == 3 ? 4 : 8;
            }
            if (phase == 4 || phase == 8)
            {
                var passenger = car.TestState(entity)!.Passenger;
                if (passenger.Owner != plugin.Session!.LocalPeerId || gameplay.Get_CurrentVehicleController != nativeVehicle ||
                    gameplay.Get_PlayerState != PlayerState.InVehicle_Passenger) return false;
                if (!car.IsOwner(plugin.Session.LocalPeerId) || car.LocalFlushComplete || car.SeatFor(plugin.Session.LocalPeerId) != passengerSeat.seatPosition)
                    throw new InvalidOperationException("Native Martin passenger entry bypassed the authoritative ownership/drain record (ST-04).");
                if (phase == 4)
                {
                    firstPassengerGeneration = passenger.Generation;
                    phase = 5; nextPassengerAction = Time.unscaledTime + 2f;
                }
                else
                {
                    if (passenger.Generation == firstPassengerGeneration)
                        throw new InvalidOperationException("Second native Martin entry reused the released passenger generation.");
                    phase = 9; nextPassengerAction = Time.unscaledTime + 2f;
                }
            }
            if (phase == 5 && Time.unscaledTime >= nextPassengerAction)
            {
                plugin.World!.FlushLocalOwnership();
                phase = 6;
            }
            if (phase == 6)
            {
                if (!car.LocalFlushComplete || car.TestState(entity)!.Passenger.Occupied || gameplay.Get_PlayerState != PlayerState.Walking) return false;
                if (gameplay.Get_CurrentVehicleController == nativeVehicle || car.IsOwner(plugin.Session!.LocalPeerId))
                    throw new InvalidOperationException("Martin flush reported completion while the native passenger remained attached (ST-04).");
                passengerApproached = false;
                phase = 7; nextPassengerAction = Time.unscaledTime + 0.75f;
            }
            if (phase == 9 && Time.unscaledTime >= nextPassengerAction)
            {
                plugin.EndPairedTestSession();
                if (gameplay.Get_PlayerState != PlayerState.Walking || gameplay.Get_CurrentVehicleController == nativeVehicle)
                    throw new InvalidOperationException("Martin disconnect restored the car before evacuating its native passenger (ST-04).");
                Status = "Guest interpolation/restoration, native Martin passenger grant, ownership flush, second entry and disconnect evacuation completed. Nearby-other-vehicle departure and multi-guest contention remain manual checks.";
                File.WriteAllText(resultPath, "MONMP_MARTIN_CAR_POSE_PASS\n" + Status);
                return true;
            }
            return false;
        }

        private void TickHostPassenger()
        {
            var state = car.TestState(entity)!.Passenger;
            if (passengerPhase == 0)
            {
                passengerGuest = plugin.Session!.Peers.Keys.Single(peer => peer != 0);
                Move(passengerDoor.transform.position + nativeVehicle.transform.right * 1.5f);
                if (!passengerDoor.vehicle_Door.Get_IsOpen) passengerDoor.MouseDown();
                passengerPhase = 1;
            }
            else if (passengerPhase == 1 && state.Owner == passengerGuest)
            {
                firstPassengerGeneration = state.Generation;
                if (!car.IsOwner(passengerGuest) || car.SeatFor(passengerGuest) == null ||
                    !plugin.World!.Owners.Contains(passengerGuest))
                    throw new InvalidOperationException("Host Martin passenger lease is missing from shared ownership (ST-04).");
                Move(passengerSeat.seatPosition.position + nativeVehicle.transform.right * 1.5f);
                passengerSeat.MouseDown(); // competing native host click on the occupied single seat
                if (gameplay.Get_PlayerState != PlayerState.Walking || car.TestState(entity)!.Passenger.Owner != passengerGuest)
                    throw new InvalidOperationException("Host native seat click displaced the existing Martin guest claim (ST-04).");
                passengerPhase = 2;
            }
            else if (passengerPhase == 2 && !state.Occupied && state.Generation != firstPassengerGeneration)
                passengerPhase = 3;
            else if (passengerPhase == 3 && state.Owner == passengerGuest && state.Generation != firstPassengerGeneration)
                passengerPhase = 4;
            Status = "Martin native passenger lease phase=" + passengerPhase + "; owner=" + state.Owner + "/" + state.Generation + "; " + HostDiagnostics();
        }

        private void Move(Vector3 position)
        {
            gameplay.PlayerWalking.transform.position = position;
            gameplay.Get_Player_Rigidbody.position = position;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        private string HostDiagnostics() => "peers=" + (plugin.Session?.Peers.Count ?? 0) +
            "; ready=" + car.TestReadyPeerCount + "/" + car.TestExpectedGuestCount +
            "; poseAcks=" + car.TestPoseAcknowledgmentCount + "; staged=" + car.TestHasStagedPose(entity);

        private string GuestDiagnostics(Vector3? origin, MartinCarState? state)
        {
            string distance = "n/a";
            if (origin != null && state != null)
            {
                var hostPosition = new Vector3(state.VehiclePose.X, state.VehiclePose.Y, state.VehiclePose.Z);
                distance = Vector3.Distance(hostPosition, origin.Value).ToString("F2") + "m";
            }
            bool matches = car.TestReplicaMatchesHost(entity, 0.15f);
            return "updates=" + car.ReceivedPoseUpdates + "; hostPos-vs-origin=" + distance +
                "; interpolationFrames=" + car.InterpolationFrames + "; wheelFrames=" + car.WheelInterpolationFrames +
                "; match=" + matches + "; channelReady=" + car.TestChannelReady +
                "; aiSuppressed=" + car.GuestSimulationSuppressed + "; readySent=" + readyAnnounced;
        }

        private void SetWaitingStatus(string message, string diagnostics)
        {
            if (Time.unscaledTime - lastDiagnosticAt < 0.5f && Status.StartsWith(message, StringComparison.Ordinal)) return;
            Status = message + "; " + diagnostics;
            lastDiagnosticAt = Time.unscaledTime;
        }
    }
}
