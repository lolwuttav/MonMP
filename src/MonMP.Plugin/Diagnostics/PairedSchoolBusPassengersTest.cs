using System;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Paired acceptance fixture for the bus passenger adapter. The existing reversible
    // bus pose stage supplies deterministic movement, independent of the AI route timer.
    internal sealed class PairedSchoolBusPassengersTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedSchoolBusPassengers passengers;
        private readonly SharedSchoolBus bus;
        private readonly bool host;
        private readonly ulong busEntity;
        private readonly float deadline;
        private readonly string seatPreparationStatus;
        private int phase;
        private float next;
        private uint guest;
        private uint firstGeneration;
        private Vector3 baseBusPosition, baseSeatPosition, stagedBusPosition, rendezvousMarkerPosition;
        private int receivedBeforeStage;
        private int receivedBeforeEcho;

        internal string Status => "School bus passengers phase=" + phase + "; owners=" +
            string.Join(",", passengers.Owners.Select(value => value.ToString()).ToArray()) +
            "; local=" + gameplay.Get_PlayerState + "; busUpdates=" + bus.ReceivedPoseUpdates +
            "; seats=" + seatPreparationStatus + (host ? "; poseEcho=" + passengers.TestPoseEchoDiagnostic +
            "; lastHostGate=" + passengers.LastClaimDiagnostic : "");

        internal PairedSchoolBusPassengersTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            passengers = plugin.World?.SchoolBusPassengers ?? throw new InvalidOperationException("School-bus passenger adapter is unavailable.");
            bus = plugin.World.SchoolBus;
            host = plugin.TestOptions.IsHost;
            busEntity = bus.TestEntity;
            if (passengers.SeatCount != 2 || busEntity == 0)
                throw new InvalidOperationException("Paired school-bus passengers test requires exactly two supported seats and one shared bus.");
            if (bus.TestState(busEntity) == null || bus.TestState(busEntity)!.MasterVisualCount == 0)
                throw new InvalidOperationException("Paired school-bus passengers test requires the supported reversible bus-pose fixture.");
            seatPreparationStatus = passengers.TestStageSeatBackrests();
            deadline = Time.unscaledTime + 150f;
        }

        internal bool Tick()
        {
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Paired school-bus passengers test timed out: " + Status);
            if (plugin.Session?.IsRunning != true || plugin.World?.Channel.FullySynchronized != true || Time.unscaledTime < next) return false;
            return host ? Host() : Guest();
        }

        private bool Host()
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                guest = plugin.Session.Peers.Keys.First(peer => peer != 0);
                if (!bus.TestHoldNativePose(busEntity))
                    throw new InvalidOperationException("Host could not hold the native school bus at a reversible passenger-seat rendezvous pose.");
                if (!bus.TestStageRendezvousPose(busEntity))
                    throw new InvalidOperationException("Host could not stage the passenger-seat rendezvous marker.");
                passengers.TestBeginPoseEcho(guest);
                phase = 1;
            }
            else if (phase == 1 && bus.TestPoseAcknowledgmentCount > 0 && passengers.TestPeerWithinSeat(guest, 0))
            {
                Require(bus.TestRestoreStagedPose(busEntity), "Host could not restore the rendezvous marker after the guest pose echo.");
                phase = 2;
            }
            else if (phase == 2 && passengers.TestState(0).Owner == guest)
            {
                firstGeneration = passengers.TestState(0).Generation;
                var anchor = passengers.SeatFor(guest);
                var avatar = plugin.RemoteAvatarRoot(guest);
                float gap = anchor != null && avatar != null ? Vector3.Distance(anchor.position, avatar.position) : float.PositiveInfinity;
                if (anchor == null || avatar == null || gap >= 0.75f)
                {
                    if (Time.unscaledTime < deadline - 100f) return false;
                    throw new InvalidOperationException("Guest avatar did not bind to the claimed bus seat; gap=" + gap.ToString("F2"));
                }
                if (!bus.TestStagePose(busEntity)) return false;
                phase = 3;
            }
            else if (phase == 3 && bus.TestPoseAcknowledgmentCount > 0)
            {
                Require(bus.TestRestoreStagedPose(busEntity), "Host could not restore its reversible staged bus pose.");
                phase = 4;
            }
            else if (phase == 4 && !passengers.TestState(0).Occupied)
            {
                Require(passengers.TestState(0).Generation > firstGeneration,
                    "Save flush did not release the first passenger claim with a fresh generation.");
                // The guest now moves to the second anchor and makes a fresh request.
                phase = 5;
            }
            else if (phase == 5 && passengers.TestState(1).Owner == guest)
            {
                Require(passengers.TestState(1).Generation > 1, "Second native bus seat claim did not advance its generation.");
                phase = 6;
            }
            else if (phase == 6 && plugin.Session!.Peers.Count == 1)
            {
                Require(!passengers.TestState(0).Occupied && !passengers.TestState(1).Occupied,
                    "Guest disconnect did not release its school-bus passenger seat.");
                Require(gameplay.Get_PlayerState == PlayerState.Walking,
                    "Host native player was not left walking after guest disconnect cleanup.");
                passengers.TestRestoreSeatBackrests();
                Require(bus.TestReleaseNativePose(busEntity), "Host could not restore the school bus after its reversible rendezvous hold.");
                plugin.EndPairedTestSession();
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            var firstSeat = passengers.TestSeatPosition(0);
            if (firstSeat == null) throw new InvalidOperationException("First bus passenger seat has no native anchor.");
            if (phase == 0)
            {
                var localOriginal = bus.TestOriginalVehiclePosition(busEntity);
                var state = bus.TestState(busEntity);
                if (localOriginal == null || state == null || bus.VisualInterpolationFrames == 0 ||
                    Vector3.Distance(localOriginal.Value, StatePosition(state)) < 2f ||
                    bus.TestReplicaMismatchReason(busEntity, 0.15f).Length != 0) return false;
                baseBusPosition = StatePosition(state);
                Move(firstSeat.position + Vector3.right * 30f);
                phase = 1; next = Time.unscaledTime + 0.4f;
            }
            else if (phase == 1)
            {
                passengers.TestEnterExit(0);
                phase = 2; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 2)
            {
                Require(!passengers.TestState(0).Occupied && gameplay.Get_PlayerState == PlayerState.Walking,
                    "Distant guest acquired the first passenger seat.");
                Move(firstSeat.position);
                phase = 3; next = Time.unscaledTime + 0.35f;
            }
            else if (phase == 3)
            {
                Move(firstSeat.position);
                if (bus.TestReplicaMismatchReason(busEntity, 0.15f).Length != 0) return false;
                rendezvousMarkerPosition = StatePosition(bus.TestState(busEntity));
                receivedBeforeEcho = bus.ReceivedPoseUpdates;
                bus.TestAcknowledgePose(busEntity);
                phase = 4; next = Time.unscaledTime + 0.35f;
            }
            else if (phase == 4)
            {
                var state = bus.TestState(busEntity);
                if (state == null || bus.ReceivedPoseUpdates <= receivedBeforeEcho ||
                    Vector3.Distance(StatePosition(state), rendezvousMarkerPosition) < 0.35f ||
                    bus.TestReplicaMismatchReason(busEntity, 0.15f).Length != 0) return false;
                // The host only restores the marker after its echoed guest pose passed the
                // native seat-distance check. Follow the exact restored host anchor, let that
                // position travel through normal pose replication, then make the real request.
                baseBusPosition = StatePosition(state);
                Move(firstSeat.position);
                phase = 5; next = Time.unscaledTime + 0.4f;
            }
            else if (phase == 5)
            {
                Move(firstSeat.position);
                passengers.TestEnterExit(0);
                phase = 6;
            }
            else if (phase == 6 && passengers.TestState(0).Owner == plugin.Session!.LocalPeerId)
            {
                Require(gameplay.Get_CurrentVehicleController != null && gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger,
                    "Host grant did not enter the guest into the native bus passenger seat.");
                firstGeneration = passengers.TestState(0).Generation;
                baseSeatPosition = firstSeat.position;
                receivedBeforeStage = bus.ReceivedPoseUpdates;
                phase = 7;
            }
            else if (phase == 7)
            {
                float anchorDelta = Vector3.Distance(baseSeatPosition, firstSeat.position);
                var state = bus.TestState(busEntity);
                bool receivedTestPose = state != null && Vector3.Distance(baseBusPosition, StatePosition(state)) > 1f;
                if (!receivedTestPose || anchorDelta < 0.75f || bus.VisualInterpolationFrames == 0) return false;
                stagedBusPosition = StatePosition(state);
                var seatedVehicle = gameplay.Get_CurrentVehicleController;
                Require(passengers.TestState(0).Owner == plugin.Session!.LocalPeerId &&
                    gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger &&
                    seatedVehicle != null && gameplay.PlayerWalking.transform.parent == seatedVehicle.transform,
                    "Interpolated bus movement detached the guest from its passenger seat.");
                bus.TestAcknowledgePose(busEntity);
                phase = 8;
            }
            else if (phase == 8)
            {
                var state = bus.TestState(busEntity);
                var current = bus.TestVehiclePosition(busEntity);
                if (state == null || bus.ReceivedPoseUpdates <= receivedBeforeStage ||
                    Vector3.Distance(StatePosition(state), stagedBusPosition) < 1f || current == null ||
                    Vector3.Distance(current.Value, StatePosition(state)) > 0.75f) return false;
                plugin.World!.FlushLocalOwnership();
                phase = 9;
            }
            else if (phase == 9 && !passengers.TestState(0).Occupied)
            {
                Require(gameplay.Get_PlayerState == PlayerState.Walking,
                    "Save-flush passenger exit did not restore native walking.");
                var secondSeat = passengers.TestSeatPosition(1)!;
                Move(secondSeat.position);
                phase = 10; next = Time.unscaledTime + 0.35f;
            }
            else if (phase == 10)
            {
                Move(passengers.TestSeatPosition(1)!.position);
                passengers.TestEnterExit(1);
                phase = 11;
            }
            else if (phase == 11 && passengers.TestState(1).Owner == plugin.Session!.LocalPeerId)
            {
                Require(passengers.TestState(1).Generation > 1 && gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger,
                    "Guest could not claim the second native seat after save flush.");
                phase = 12; next = Time.unscaledTime + 0.2f;
            }
            else if (phase == 12)
            {
                plugin.EndPairedTestSession();
                Require(gameplay.Get_PlayerState == PlayerState.Walking,
                    "Disconnect did not restore guest native walking from its bus seat.");
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

        private static Vector3 StatePosition(SchoolBusState? state) => state == null ? Vector3.zero :
            new Vector3(state.VehiclePose.X, state.VehiclePose.Y, state.VehiclePose.Z);

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Paired school-bus passengers test: " + message);
        }
    }
}
