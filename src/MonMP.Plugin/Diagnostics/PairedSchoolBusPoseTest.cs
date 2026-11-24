using System;
using System.IO;
using BepInEx;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedSchoolBusPoseTest
    {
        private const float TimeoutSeconds = 150;
        private readonly MonMPPlugin plugin;
        private readonly SharedSchoolBus bus;
        private readonly bool host;
        private readonly ulong entity;
        private readonly float deadline;
        private readonly string resultPath;
        private int initialUpdates;
        private SchoolBusState? stagedGuestState;
        private float phaseStarted;
        private float nextDiagnosticWrite;
        private float hostVisualStableSince;
        private SchoolBusState? previousHostPoseState;
        private int phase;
        internal string Status { get; private set; } = "Waiting for the school bus pose catalog.";

        internal PairedSchoolBusPoseTest(MonMPPlugin plugin)
        {
            this.plugin = plugin;
            host = plugin.TestOptions.IsHost;
            bus = plugin.World?.SchoolBus ?? throw new InvalidOperationException("School bus adapter is not attached.");
            entity = bus.TestEntity;
            if (entity == 0) throw new InvalidOperationException("No supported AI_SchoolBus binding exists in this isolated save.");
            deadline = Time.unscaledTime + TimeoutSeconds;
            resultPath = Path.Combine(Paths.BepInExRootPath, "MonMP", "diagnostics", "paired-schoolbus-pose.txt");
            File.WriteAllText(resultPath, "RUNNING: host school bus pose, Richard and door presentation");
        }

        internal bool Tick()
        {
            if (Time.unscaledTime > deadline)
                throw new InvalidOperationException("School bus pose fixture timed out: " + Status);
            return host ? TickHost() : TickGuest();
        }

        private bool TickHost()
        {
            var session = plugin.Session;
            if (session?.IsRunning != true || plugin.World?.PersonalProfileReady != true || (phase < 2 && plugin.RemoteAvatarCount == 0))
            { Status = "Waiting for the paired guest and synchronized world profile."; return false; }

            if (phase == 0)
            {
                var state = bus.TestState(entity);
                if (state == null || state.MasterVisualCount == 0)
                    throw new InvalidOperationException("The native school bus has no supported master-door visuals for the paired presentation check.");
                if (previousHostPoseState == null || !VisualsStable(previousHostPoseState, state))
                {
                    previousHostPoseState = state;
                    hostVisualStableSince = Time.unscaledTime;
                    Status = "Waiting for the native school-bus master-door animation to settle before staging.";
                    return false;
                }
                previousHostPoseState = state;
                if (Time.unscaledTime - hostVisualStableSince < 1f)
                { Status = "Waiting for the native school-bus master-door animation to settle before staging."; return false; }
                if (!bus.TestStagePose(entity))
                { Status = "Waiting for a supported host bus pose."; return false; }
                phase = 1; phaseStarted = Time.unscaledTime;
                Status = "Host staged a reversible test-only snapshot pose; waiting for the guest interpolation acknowledgment.";
                File.WriteAllText(resultPath, "RUNNING: waiting for guest interpolation acknowledgment");
            }

            if (phase == 1)
            {
                var staged = bus.TestState(entity);
                var fixtureOriginal = bus.TestFixtureOriginalVehiclePosition(entity);
                if (staged == null || fixtureOriginal == null || Vector3.Distance(fixtureOriginal.Value,
                    new Vector3(staged.VehiclePose.X, staged.VehiclePose.Y, staged.VehiclePose.Z)) < 2f)
                { Status = "Waiting for the host snapshot to include the staged pose."; return false; }
                if (bus.TestPoseAcknowledgmentCount > 0)
                {
                    if (!bus.TestRestoreStagedPose(entity)) throw new InvalidOperationException("Host fixture pose could not be restored.");
                    phase = 2; phaseStarted = Time.unscaledTime;
                    Status = "Guest acknowledged smoothed bus/Richard presentation; host restored the original pose.";
                    File.WriteAllText(resultPath, "RUNNING: original host pose restored; waiting for guest departure");
                }
                else if (Time.unscaledTime - phaseStarted > 20f)
                    throw new InvalidOperationException("Guest did not acknowledge the staged host pose.");
                return false;
            }

            if (session.Peers.Count > 1) return false;
            string checkpoint = bus.TestRemoteCheckpointSkip();
            if (checkpoint.StartsWith("skip:", StringComparison.Ordinal))
            { Status = "Waiting to run the native school-bus checkpoint assertion: " + checkpoint; return false; }
            if (checkpoint.Length != 0) throw new InvalidOperationException("School bus native checkpoint skip: " + checkpoint + " (#146).");
            Debug.Log("MonMP school bus checkpoint guard: fresh nearby and destination poses vetoed teleport and restored the index; no-remote and stale-pose cases retained native advancement.");
            Debug.Log("MonMP school bus stop zones: " + bus.TestZones());
            if (bus.TestZoneCount == 0) throw new InvalidOperationException("The school bus has no stop zone for remote walkers: " + bus.TestZones());
            string cycle = bus.TestRemoteZoneCycle();
            if (cycle.Length != 0) throw new InvalidOperationException("School bus remote stop zone: " + cycle + " (#136).");
            string cabin = bus.TestRemoteCabinCycle();
            if (cabin.Length != 0) throw new InvalidOperationException("School bus remote cabin hold: " + cabin);
            Debug.Log("MonMP school bus running objects (host): " + bus.TestRunningObjects());
            Status = "Host-authoritative bus pose was staged, guest-acknowledged after interpolation, restored and disconnected.";
            File.WriteAllText(resultPath, "MONMP_SCHOOLBUS_POSE_PASS\n" + Status);
            return true;
        }

        private bool TickGuest()
        {
            if (!bus.GuestSimulationSuppressed || bus.SuppressedNativeCalls == 0 || bus.ReceivedPoseUpdates == 0)
            { Status = "Waiting for host snapshot and guest AI suppression."; return false; }
            var localOriginal = bus.TestOriginalVehiclePosition(entity);
            var state = bus.TestState(entity);
            var position = bus.TestVehiclePosition(entity);
            if (localOriginal == null || state == null || position == null)
            { Status = "Waiting for the guest school bus baseline."; return false; }

            if (phase == 0)
            {
                var hostPosition = new Vector3(state.VehiclePose.X, state.VehiclePose.Y, state.VehiclePose.Z);
                if (Vector3.Distance(localOriginal.Value, hostPosition) < 2f || bus.VisualInterpolationFrames == 0)
                { Status = "Guest AI is suppressed; waiting for bus, Richard and master-door poses to move through interpolation."; return false; }
                string mismatch = bus.TestReplicaMismatchReason(entity, 0.15f);
                if (mismatch.Length != 0)
                {
                    Status = "Guest interpolated toward the host bus/Richard pose; mismatch: " + mismatch;
                    if (Time.unscaledTime >= nextDiagnosticWrite)
                    {
                        File.WriteAllText(resultPath, "RUNNING: " + Status);
                        nextDiagnosticWrite = Time.unscaledTime + 0.5f;
                    }
                    return false;
                }
                Debug.Log("MonMP school bus running objects (guest): " + bus.TestRunningObjects());
                if (!bus.TestRunningMatches())
                    throw new InvalidOperationException("Guest bus driver and running props do not follow the host bus state (#135): " + bus.TestRunningObjects());
                bus.TestAcknowledgePose(entity);
                initialUpdates = bus.ReceivedPoseUpdates;
                stagedGuestState = state;
                phase = 1; phaseStarted = Time.unscaledTime;
                Status = "Guest bus, Richard and master-door poses match the host after an observed interpolation frame.";
                File.WriteAllText(resultPath, "RUNNING: interpolation verified; waiting for host restoration snapshot");
            }

            if (phase == 1)
            {
                state = bus.TestState(entity);
                string mismatch = bus.TestReplicaMismatchReason(entity, 0.15f);
                if (state == null || bus.ReceivedPoseUpdates <= initialUpdates ||
                    VisualsStable(stagedGuestState!, state) || mismatch.Length != 0)
                {
                    Status = "Waiting for host pose restoration to reach the guest." + (mismatch.Length == 0 ? "" : " Mismatch: " + mismatch);
                    if (Time.unscaledTime - phaseStarted > 20f) throw new InvalidOperationException(Status);
                    if (Time.unscaledTime >= nextDiagnosticWrite)
                    {
                        File.WriteAllText(resultPath, "RUNNING: " + Status);
                        nextDiagnosticWrite = Time.unscaledTime + 0.5f;
                    }
                    return false;
                }
                plugin.EndPairedTestSession();
                Status = "Guest AI stayed suppressed and smoothed host bus, Richard and master-door poses, then followed the host restore.";
                File.WriteAllText(resultPath, "MONMP_SCHOOLBUS_POSE_PASS\n" + Status);
                return true;
            }
            return false;
        }

        private static bool VisualsStable(SchoolBusState previous, SchoolBusState current)
        {
            if (previous.MasterVisualCount != current.MasterVisualCount) return false;
            for (int i = 0; i < current.MasterVisualCount; i++)
            {
                var before = previous.MasterVisual(i); var after = current.MasterVisual(i);
                if (Mathf.Abs(Mathf.DeltaAngle(before.EulerX, after.EulerX)) > 0.5f ||
                    Mathf.Abs(Mathf.DeltaAngle(before.EulerY, after.EulerY)) > 0.5f ||
                    Mathf.Abs(Mathf.DeltaAngle(before.EulerZ, after.EulerZ)) > 0.5f) return false;
            }
            return true;
        }
    }
}
