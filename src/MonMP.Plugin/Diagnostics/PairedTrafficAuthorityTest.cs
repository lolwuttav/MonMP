using System;
using MonMP.Plugin.Replication;
using MonMP.Plugin.Patches;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedTrafficAuthorityTest
    {
        private const float TimeoutSeconds = 120;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly float deadline;
        private bool synchronizedGuest;
        private bool sawGuestCamera;
        private bool cameraBoundsVerified, triggerExitSuppressionVerified, engineLevelsVerified;
        private bool sawPedestrianStop;
        private float pedestrianUntil;
        private float pedestrianMaxSpeed, pedestrianLastSpeed = -1f;
        private string pedestrianCar = "";
        private Vector3? standAt;
        private float closestGap = float.MaxValue, speedAtClosest;
        private bool densityCaptured, stagingStarted, spawnRequested, restored;
        private int originalDensity, stagedSlot = -1;
        private float spawnRequestedAt;
        private SharedTraffic? fixtureTraffic;
        private string spawnDiagnostics = "not staged";
        private float resumeUntil;
        private int resumeUpdates, resumeFixedUpdates;
        private bool disconnectObserved;
        private string? resumeError;
        private string status = "waiting for shared traffic world";

        internal string Status => status;

        internal PairedTrafficAuthorityTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin;
            this.gameplay = gameplay;
            host = plugin.TestOptions.IsHost;
            deadline = Time.unscaledTime + TimeoutSeconds;
        }

        internal bool Tick()
        {
            if (Time.unscaledTime > deadline)
            {
                Dispose();
                throw new InvalidOperationException("Traffic authority fixture timed out: " + status);
            }
            if (disconnectObserved)
            {
                if (resumeError != null) throw new InvalidOperationException("Native traffic failed after guest disconnect: " + resumeError);
                if (plugin.Session != null || plugin.World != null || TrafficAuthorityPatches.IsGuestSuppressed || fixtureTraffic?.TestOfflineRestored != true || !fixtureTraffic.TestNoUninitializedResumedSlot)
                    throw new InvalidOperationException("Guest traffic disconnect did not restore its original initialized/sleeping native pool.");
                if (Time.unscaledTime < resumeUntil) { status = "observing restored native traffic for two seconds"; return false; }
                if (TrafficAuthorityPatches.ResumedUpdates <= resumeUpdates || TrafficAuthorityPatches.ResumedFixedUpdates <= resumeFixedUpdates)
                    throw new InvalidOperationException("Native traffic Update/FixedUpdate did not resume after guest disconnect.");
                Application.logMessageReceived -= ObserveResumeLog;
                status = "guest offline pool restored; native Update/FixedUpdate resumed for two seconds";
                return true;
            }
            SharedTraffic? traffic = plugin.World?.Traffic;
            var session = plugin.Session;
            if (traffic == null || session?.IsRunning != true) return false;
            fixtureTraffic = traffic;
            if (traffic.TestSlotCount <= 0)
                throw new InvalidOperationException("Traffic fixture found an empty native Gley pool.");

            if (host && spawnRequested && traffic.TestNativeActiveSlots == 0 && Time.unscaledTime - spawnRequestedAt > 10f)
            {
                string pending = traffic.TestTrafficAddPending ? "pending" : "not pending";
                int current = traffic.TestTrafficCurrentCount;
                int active = traffic.TestNativeActiveSlots;
                Dispose();
                throw new InvalidOperationException("Native Gley AddVehicle did not activate within 10 seconds (" + pending +
                    "; native-current=" + current + "; native-active=" + active + "). " + spawnDiagnostics);
            }

            if (host)
            {
                if (session.Peers.Count > 1 && traffic.TestAllPeersSynchronized) synchronizedGuest = true;
                if (!traffic.TestBaselineApplied)
                { status = "waiting for host slot baseline"; return false; }
                if (!synchronizedGuest)
                { status = "waiting for late-joining guest snapshot and acknowledgment"; return false; }
                if (traffic.TestHostCameraCount >= 2) sawGuestCamera = true;
                if (!sawGuestCamera)
                { status = "waiting for guest player pose to enter host traffic camera set"; return false; }
                if (!cameraBoundsVerified)
                {
                    if (!traffic.TestOffGridRemoteCameraFiltering())
                        throw new InvalidOperationException("Gley remote camera filtering did not preserve an in-grid guest and reject an off-grid pose.");
                    cameraBoundsVerified = true;
                }
                if (stagingStarted && traffic.TestTrafficDensity != 0)
                    traffic.TestSetTrafficDensity(0);
                if (stagingStarted && traffic.TestNativeActiveSlots > 1)
                    throw new InvalidOperationException("Native Gley activated more than one slot during isolated fixture staging; " + spawnDiagnostics);
                if (stagingStarted && stagedSlot < 0 && traffic.TestNativeActiveSlots == 1)
                    stagedSlot = traffic.TestNativeActiveSlotIndex;
                if (traffic.TestActiveSlots == 0)
                {
                    if (traffic.TestNativeActiveSlots != 0)
                    { status = "waiting for host traffic activation to enter the replicated slot baseline"; return false; }
                    if (spawnRequested)
                    {
                        if (!traffic.TestTrafficAddPending && Time.unscaledTime - spawnRequestedAt > 1f)
                            throw new InvalidOperationException("Native Gley AddVehicle completed without activating a pool slot. " + spawnDiagnostics);
                        status = "waiting for the prevalidated native Gley AddVehicle request: " + spawnDiagnostics;
                        return false;
                    }
                    if (!densityCaptured)
                    { originalDensity = traffic.TestTrafficDensity; densityCaptured = true; }
                    if (traffic.TestTrafficCurrentCount != 0)
                        throw new InvalidOperationException("Native Gley reports " + traffic.TestTrafficCurrentCount +
                            " active vehicles while every pool slot is inactive; refusing to stage over inconsistent manager state.");
                    if (traffic.TestTrafficAddPending)
                        throw new InvalidOperationException("Native Gley already has a pending AddVehicle coroutine before fixture staging.");
                    if (traffic.TestTrafficDensity != 0) traffic.TestSetTrafficDensity(0);
                    stagingStarted = true;
                    spawnRequested = true;
                    spawnRequestedAt = Time.unscaledTime;
                    spawnDiagnostics = traffic.TestAddNativeTrafficVehicle();
                    status = "native Gley AddVehicle requested at a prevalidated spawn waypoint; waiting for native activation: " + spawnDiagnostics;
                    return false;
                }
                if (traffic.TestHostPoseUpdates + traffic.TestHostActivationUpdates == 0)
                { status = "waiting for host traffic pose or activation update"; return false; }
                var nearest = traffic.TestNearestCarToWalker;
                if (nearest != null && nearest.Value.Gap < closestGap) { closestGap = nearest.Value.Gap; speedAtClosest = nearest.Value.Speed; }
                if (traffic.TestPedestrianStops > 0)
                {
                    if (!sawPedestrianStop) Debug.Log("MonMP traffic pedestrian entered: car " + traffic.TestPedestrianCar);
                    sawPedestrianStop = true;
                    float speed = traffic.TestPedestrianCarSpeed;
                    if (speed >= 0) { pedestrianMaxSpeed = Mathf.Max(pedestrianMaxSpeed, speed); pedestrianLastSpeed = speed; pedestrianCar = traffic.TestPedestrianCar ?? ""; }
                }
                status = "host traffic baseline, guest camera, and native slot update observed; pedestrianStop=" + sawPedestrianStop + "; waiting for guest validation and disconnect";
                if (session.Peers.Count != 1) return false;
                if (!sawPedestrianStop)
                {
                    Dispose();
                    throw new InvalidOperationException("The guest stood in front of an active car, but the host's car never registered the guest in its front trigger.");
                }
                Debug.Log("MonMP traffic pedestrian stop: max speed " + pedestrianMaxSpeed.ToString("F2") + ", last " + pedestrianLastSpeed.ToString("F2") + ", car " + pedestrianCar +
                    ", closest gap " + closestGap.ToString("F2") + " at " + speedAtClosest.ToString("F2") + " m/s");
                if (closestGap < 0.1f && speedAtClosest > 1f)
                {
                    Dispose();
                    throw new InvalidOperationException("The host's car drove into the guest standing on the road: gap " + closestGap.ToString("F2") + " at " + speedAtClosest.ToString("F2") + " m/s.");
                }
                if (pedestrianLastSpeed > 0.5f)
                {
                    Dispose();
                    throw new InvalidOperationException("The host's car registered the guest in front of it but did not stop: speed " +
                        pedestrianLastSpeed.ToString("F2") + " m/s (max " + pedestrianMaxSpeed.ToString("F2") + "), car " + pedestrianCar);
                }
                Dispose();
                return true;
            }

            if (!traffic.TestBaselineApplied)
            { status = "waiting for host traffic slot snapshot"; return false; }
            if (!traffic.TestGuestSimulationSuppressed || traffic.TestSkippedGuestUpdates == 0)
            { status = "waiting for guest Gley simulation suppression"; return false; }
            if (!triggerExitSuppressionVerified)
            {
                if (!traffic.TestSuppressedGuestTriggerExit())
                    throw new InvalidOperationException("Guest Gley OnTriggerExit was not suppressed before its non-authoritative AI callback.");
                triggerExitSuppressionVerified = true;
            }
            if (!traffic.TestGuestReplicaMatches)
            { status = "waiting for all guest slots to match host activation and prefab catalog"; return false; }
            if (traffic.TestActiveSlots == 0)
            { status = "waiting for an active host traffic presentation"; return false; }
            if (traffic.TestReceivedPoseUpdates == 0)
            { status = "waiting for a host traffic pose update"; return false; }
            if (!traffic.TestGuestInterpolated)
            { status = "waiting for guest traffic interpolation"; return false; }
            if (!engineLevelsVerified)
            {
                if (!traffic.TestEngineLevels(out string levels))
                    throw new InvalidOperationException("Guest traffic engine sound is outside Gley's native speed range (#180): " + levels);
                Debug.Log("MonMP traffic guest engine levels: " + levels);
                engineLevelsVerified = true;
            }

            // Stand on the road ahead of a car, as a player would, so the host's car must stop short of the guest.
            if (standAt == null)
            {
                standAt = traffic.TestRoadAhead(6f, out var geometry);
                if (standAt == null) { status = "waiting for an active car to stand in front of"; return false; }
                Debug.Log("MonMP traffic pedestrian standing on the road ahead: " + geometry);
                pedestrianUntil = Time.unscaledTime + 8f;
            }
            var feet = standAt.Value;
            gameplay.PlayerWalking.transform.position = feet;
            if (gameplay.Get_Player_Rigidbody != null) { gameplay.Get_Player_Rigidbody.position = feet; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
            if (Time.unscaledTime < pedestrianUntil) { status = "standing in front of an active car"; return false; }

            int wireObservedUninitialized = traffic.TestNeverInitializedReplicaSlots;
            Debug.Log("MonMP traffic controlled teardown regression: " + traffic.TestStageNeverInitializedReplica() +
                "; wire-observed-uninitialized-activations=" + wireObservedUninitialized);
            resumeUpdates = TrafficAuthorityPatches.ResumedUpdates; resumeFixedUpdates = TrafficAuthorityPatches.ResumedFixedUpdates;
            Application.logMessageReceived += ObserveResumeLog;
            disconnectObserved = true;
            plugin.EndPairedTestSession();
            if (!traffic.TestOfflineRestored || !traffic.TestNoUninitializedResumedSlot)
                throw new InvalidOperationException("Guest traffic original baseline was not restored synchronously during disconnect.");
            resumeUntil = Time.unscaledTime + 2f;
            status = "guest detached; checking native traffic resumption";
            return false;
        }

        private void ObserveResumeLog(string message, string stack, LogType type)
        {
            if ((type == LogType.Exception || type == LogType.Error) &&
                (message.Contains("traffic offline restore") || stack.Contains("GleyTrafficSystem") || message.Contains("GleyTrafficSystem")))
                resumeError ??= message + " " + stack;
        }

        internal void Dispose()
        {
            Application.logMessageReceived -= ObserveResumeLog;
            if (restored) return;
            if (!host || fixtureTraffic == null)
            { restored = true; return; }
            try
            {
                if (stagingStarted)
                {
                    fixtureTraffic.TestCancelPendingNativeTrafficAdd();
                    for (int index = 0; index < fixtureTraffic.TestSlotCount; index++)
                        fixtureTraffic.TestRemoveTrafficSlot(index);
                }
                stagedSlot = -1;
            }
            finally
            {
                if (densityCaptured)
                    fixtureTraffic.TestSetTrafficDensity(originalDensity);
            }
            restored = true;
        }
    }
}
