using System;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using NWH;
using NWH.VehiclePhysics2.Powertrain;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Phase 76 isolates the real guest input-response path from PairedVehiclesTest's
    // later scripted-drive coverage. Only the guest injects input; the host validates
    // that actual gear, control and velocity samples arrived through VehicleState.
    internal sealed class PairedVehicleInputResponseTest : IDisposable
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private Vehicle_Type? selectedType;
        private PairedVehicleInputResponseProbe? probe;
        private int phase;
        private float deadline, nextPhaseAt;
        private float nextEnterAttemptAt;
        private bool entryDiagnosticsEnabled;
        private bool sawGuestDriver, sawGuestThrottle, sawGuestBrake, sawGuestMotion, sawGuestGear, sawHostGear;
        private const float GhostFixtureScale = 0.7737f;
        private InteractableItems? hostGhostFixture;
        private ulong ghostFixtureId;
        private Collider? ghostItemCollider, ghostOriginalVehicleCollider;
        private GameObject? enabledColliderFixture, installedColliderFixture;
        private bool ghostPairsVerified;
        private string evidence = "waiting for a manual scrapyard Konig, ATV or truck";

        internal string Status => "VehicleInputResponse phase " + phase + "; vehicle=" + (selectedType?.ToString() ?? "selecting") +
            "; throttle=" + sawGuestThrottle + "; brake=" + sawGuestBrake + "; motion=" + sawGuestMotion +
            "; gear=" + sawGuestGear + "; hostGear=" + sawHostGear +
            "; entryTrace=" + (plugin.Vehicles?.TestDriverEntryDiagnostic ?? "no shared vehicle world");
        internal string Evidence => evidence;

        internal PairedVehicleInputResponseTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            this.gameplay = gameplay ?? throw new ArgumentNullException(nameof(gameplay));
        }

        internal bool Tick()
        {
            if (Time.unscaledTime < nextPhaseAt) return false;
            var shared = plugin.Vehicles;
            if (shared?.Ready != true || plugin.World?.PersonalProfileReady != true) return false;
            Require(shared.Count == 4 + (shared.TestScrapyardKonigSupported ? 1 : 0) + (shared.TestPrinzBound ? 1 : 0) + (shared.TestLeMissileBound ? 1 : 0) + (shared.TestRacecarBound ? 1 : 0) && shared.TestBuggyReserved,
                "Expected the starting Konig, stock ATV, truck, reserved buggy and any supported native scrapyard Konig, Prinz, Le Missile and racecar; bound=" + shared.Count + ".");
            if (selectedType == null) selectedType = SelectManualVehicle(shared);
            shared.TestType = selectedType.Value;
            if (!entryDiagnosticsEnabled)
            {
                shared.TestEnableDriverEntryDiagnostics();
                entryDiagnosticsEnabled = true;
            }
            var vehicle = shared.TestVehicle;
            return plugin.TestOptions.IsHost ? TickHost(shared, vehicle) : TickGuest(shared, vehicle);
        }

        private bool TickHost(SharedVehicles shared, NWH.VehiclePhysics2.VehicleController vehicle)
        {
            if (phase == 0)
            {
                if (plugin.Session?.Peers.Count < 2) return false;
                Require(shared.TestState.Driver.Owner == ItemLease.Unowned, "Selected input-response vehicle was already leased.");
                var solid = vehicle.GetComponentsInChildren<Collider>(false).FirstOrDefault(c => c.enabled && !c.isTrigger);
                Require(solid != null, "Selected vehicle has no solid collider for the replica collision fixture.");
                hostGhostFixture = plugin.World!.Items.TestSpawn(new Vector3(solid!.bounds.max.x + 1f, solid.bounds.center.y + 0.5f, solid.bounds.center.z));
                // TestSpawn instantiates the native prefab, but InteractableItems creates
                // its body in Start -> CreateBasics, after this synchronous setup. Use
                // that native initializer before checking the real body/collider pair.
                hostGhostFixture?.CreateBasics();
                Require(hostGhostFixture != null && hostGhostFixture.rb != null &&
                    hostGhostFixture.GetComponentsInChildren<Collider>(false).Any(c => c.enabled && !c.isTrigger),
                    "TestSpawn did not provide an isolated physical item with a solid collider.");
                hostGhostFixture!.transform.localScale = Vector3.one * GhostFixtureScale;
                phase = 1; deadline = Time.unscaledTime + 60f;
                evidence = "host is watching guest " + selectedType + " control, motion, braking and gear samples";
                return false;
            }

            var state = shared.TestState;
            if (state.Driver.Owner != ItemLease.Unowned)
            {
                sawGuestDriver = true;
                sawGuestThrottle |= state.Throttle > 0.5f;
                sawGuestBrake |= state.Brake > 0.5f;
                sawGuestMotion |= Magnitude(state.Velocity) > 0.5f;
                sawGuestGear |= state.Gear == 1;
                if (state.Gear == 1 && vehicle.powertrain.transmission.Gear == 1)
                {
                    RemoteGearPresentationProbe.Verify(vehicle, state.Gear);
                    sawHostGear = true;
                }
                if (Time.unscaledTime >= deadline)
                    throw new InvalidOperationException("Paired native vehicle input response: host timed out waiting for guest controls and gear replication. " + Status);
                return false;
            }

            if (!sawGuestDriver)
            {
                if (Time.unscaledTime >= deadline)
                    throw new InvalidOperationException("Guest never acquired the input-response vehicle. " + Status);
                return false;
            }

            if (phase == 1)
            {
                phase = 2; deadline = Time.unscaledTime + 30f;
                evidence = "guest released the input-response vehicle; host is checking received evidence";
            }
            if (plugin.Session?.Peers.Count == 1)
            {
                Require(sawGuestThrottle && sawGuestBrake && sawGuestMotion && sawGuestGear && sawHostGear,
                    "Host did not observe guest input-driven throttle, brake, motion and an applied 0-to-1 gear change. " + Status);
                evidence = "host received guest throttle, braking and motion samples plus gear 0->1; native first gear and every configured digital gear string/shifter rotation matched with remote input disabled";
                return true;
            }
            if (Time.unscaledTime >= deadline)
                throw new InvalidOperationException("Paired native vehicle input response: guest did not disconnect after releasing its seat. " + Status);
            return false;
        }

        private bool TickGuest(SharedVehicles shared, NWH.VehiclePhysics2.VehicleController vehicle)
        {
            if (phase == 0)
            {
                Require(vehicle.VehicleMultiplayerInstanceType == Vehicle.MultiplayerInstanceType.Remote && vehicle.vehicleRigidbody.isKinematic,
                    "Guest did not receive a kinematic vehicle baseline.");
                MovePlayer(shared.TestSeat + Vector3.right * 30);
                phase = 1; nextPhaseAt = Time.unscaledTime + 0.5f; return false;
            }
            if (phase == 1)
            {
                MovePlayer(shared.TestSeat + Vector3.right * 1.5f);
                // The host validates Enter against the most recently received guest
                // walking pose. This fixture deliberately moved the guest away first,
                // so the near-seat pose and interaction request must not be treated as
                // one atomic operation. Give pose replication a beat, then retry while
                // the authoritative driver lease is still unowned.
                phase = 2; deadline = Time.unscaledTime + 20f;
                nextEnterAttemptAt = Time.unscaledTime + 0.7f;
                evidence = "waiting for the guest near-seat pose to reach the host";
                return false;
            }
            if (phase == 2)
            {
                if (shared.TestState.Driver.Owner != plugin.Session!.LocalPeerId)
                {
                    if (Time.unscaledTime >= deadline)
                    {
                        float seatDistance = Vector3.Distance(gameplay.PlayerWalking.transform.position, shared.TestSeat);
                        throw new InvalidOperationException("Guest did not receive the selected manual vehicle driver lease; owner=" +
                            shared.TestState.Driver.Owner + ", localPeer=" + plugin.Session.LocalPeerId + ", playerState=" +
                            gameplay.Get_PlayerState + ", seatDistance=" + seatDistance.ToString("F2") + "m.");
                    }
                    if (shared.TestState.Driver.Owner == ItemLease.Unowned && Time.unscaledTime >= nextEnterAttemptAt)
                    {
                        shared.TestEnter();
                        nextEnterAttemptAt = Time.unscaledTime + 0.8f;
                        evidence = "retrying guest driver lease request for " + selectedType;
                    }
                    return false;
                }
                // The owner update arrives over the network and the native vehicle
                // component may finish its local-mode activation later in this frame.
                // Observe that transition for a short, bounded window before failing.
                phase = 7; deadline = Time.unscaledTime + 3f;
                evidence = "waiting for native driver control and local vehicle physics to finish restoring";
                return false;
            }
            if (phase == 7)
            {
                if (GuestDriverInputReady(vehicle))
                {
                    if (!VerifyGuestGhostPairs(shared, vehicle))
                    {
                        if (Time.unscaledTime >= deadline)
                            throw new InvalidOperationException("Paired native vehicle input response: host's tagged physical replica did not become registered and ignored beside the guest vehicle within the fixture deadline.");
                        return false;
                    }
                    BeginGuestInputResponse(vehicle);
                    return false;
                }
                if (Time.unscaledTime >= deadline)
                    throw new InvalidOperationException("Paired native vehicle input response: driver lease was granted, but native input/physics did not restore within 3 seconds; " +
                        GuestDriverTransitionStatus(shared, vehicle));
                return false;
            }
            if (phase == 3)
            {
                if (!vehicle.isEngineRunning)
                {
                    if (Time.unscaledTime >= deadline)
                        throw new InvalidOperationException("Native engine did not start in neutral for the input-response fixture.");
                    return false;
                }
                vehicle.Get_IgnitionPosition = 1;
                // Keep the selected vehicle stationary until virtual ShiftUp engages first.
                vehicle.brakes.handbrakeValue = 1;
                probe = new PairedVehicleInputResponseProbe(vehicle);
                phase = 4; deadline = 0f;
                evidence = "virtual ShiftUp, throttle and brake response are now being measured";
                return false;
            }
            if (phase == 4)
            {
                if (!probe!.Tick()) return false;
                evidence = probe.Evidence;
                probe.Dispose(); probe = null;
                vehicle.input.states.Reset();
                vehicle.input.autoSetInput = true;
                vehicle.Get_IgnitionPosition = 0;
                shared.TestExit();
                phase = 5; deadline = Time.unscaledTime + 15f;
                return false;
            }
            if (phase == 5)
            {
                if (shared.TestState.Driver.Owner != ItemLease.Unowned)
                {
                    if (Time.unscaledTime >= deadline)
                        throw new InvalidOperationException("Guest input-response fixture did not release the driver seat.");
                    return false;
                }
                Require(gameplay.Get_PlayerState == PlayerState.Walking && vehicle.vehicleRigidbody.isKinematic,
                    "Guest input-response exit did not restore walking and remote vehicle physics.");
                plugin.World!.Items.BeforePhysics();
                Require(ghostPairsVerified && ghostItemCollider != null && ghostOriginalVehicleCollider != null &&
                    !Physics.GetIgnoreCollision(ghostItemCollider!, ghostOriginalVehicleCollider!) &&
                    !Physics.GetIgnoreCollision(ghostItemCollider!, enabledColliderFixture!.GetComponent<Collider>()) &&
                    !Physics.GetIgnoreCollision(ghostItemCollider!, installedColliderFixture!.GetComponent<Collider>()),
                    "Exiting did not restore every touched collision pair after the fixture carrier was released.");
                DestroyColliderFixtures();
                evidence += "; real collision pairs ignored the registered replica, activated/new colliders and carrier release, then restored on exit";
                phase = 6;
                return true;
            }
            return false;
        }

        private bool GuestDriverInputReady(NWH.VehiclePhysics2.VehicleController vehicle) =>
            gameplay.Get_CurrentVehicleController == vehicle && gameplay.Get_PlayerState == PlayerState.InVehicle_Driver &&
            vehicle.VehicleMultiplayerInstanceType == Vehicle.MultiplayerInstanceType.Local && !vehicle.vehicleRigidbody.isKinematic &&
            vehicle.input.autoSetInput && vehicle.input.IsOn && vehicle.input.Active;

        private bool VerifyGuestGhostPairs(SharedVehicles shared, NWH.VehiclePhysics2.VehicleController vehicle)
        {
            if (ghostPairsVerified) return true;
            var items = plugin.World!.Items;
            ghostFixtureId = items.TestTaggedItem(1, GhostFixtureScale);
            if (ghostFixtureId == 0) return false;
            var item = items.TestItem(ghostFixtureId);
            Require(item != null && item.rb != null && item.rb.isKinematic && !items.TestLocalHeld(ghostFixtureId) &&
                items.TestState(ghostFixtureId).Lease.Owner != plugin.Session!.LocalPeerId && items.TestState(ghostFixtureId).Carrier == 0,
                "Collision fixture must be a registered nonlocal kinematic replica without a carrier.");
            ghostItemCollider = item!.GetComponentsInChildren<Collider>(false).FirstOrDefault(c => c.enabled && !c.isTrigger);
            ghostOriginalVehicleCollider = vehicle.GetComponentsInChildren<Collider>(false).FirstOrDefault(c => c.enabled && !c.isTrigger);
            Require(ghostItemCollider != null && ghostOriginalVehicleCollider != null, "Collision fixture has no active solid collider pair.");
            Physics.SyncTransforms();
            items.BeforePhysics();
            if (!Physics.GetIgnoreCollision(ghostItemCollider!, ghostOriginalVehicleCollider!)) return false;

            // Owned tiny diagnostic colliders exercise activation and installation without
            // altering native parts, leases or network state. Check each pair synchronously.
            enabledColliderFixture = NewColliderFixture(vehicle.transform, "MonMP ghost activation fixture", false);
            items.BeforePhysics();
            var activated = enabledColliderFixture.GetComponent<BoxCollider>();
            activated.enabled = true;
            items.BeforePhysics();
            Require(Physics.GetIgnoreCollision(ghostItemCollider!, activated), "An activated vehicle collider missed an already ghosted replica.");
            installedColliderFixture = NewColliderFixture(vehicle.transform, "MonMP ghost installation fixture", true);
            var installed = installedColliderFixture.GetComponent<BoxCollider>();
            items.BeforePhysics();
            Require(Physics.GetIgnoreCollision(ghostItemCollider!, installed), "A newly installed vehicle collider missed an already ghosted replica.");
            Require(items.TestGhostCarrierRelease(ghostFixtureId, shared.TestId),
                "The owned replica could not exercise carrier release through the driven vehicle's registered carrier frame.");
            Require(Physics.GetIgnoreCollision(ghostItemCollider!, ghostOriginalVehicleCollider!) &&
                Physics.GetIgnoreCollision(ghostItemCollider!, activated) && Physics.GetIgnoreCollision(ghostItemCollider!, installed),
                "Carrier release re-enabled a collision pair still owned by driver ghosting.");
            // The detached collider must still be restored by the touched-collider union.
            installedColliderFixture.transform.SetParent(null, true);
            installedColliderFixture.transform.position = vehicle.transform.position + Vector3.up * 100f;
            enabledColliderFixture.transform.SetParent(null, true);
            enabledColliderFixture.transform.position = vehicle.transform.position + Vector3.up * 101f;
            Physics.SyncTransforms();
            Require(Physics.GetIgnoreCollision(ghostItemCollider!, activated) && Physics.GetIgnoreCollision(ghostItemCollider!, installed),
                "Detaching the owned collider fixtures unexpectedly reset their pairs before the exit restoration check.");
            ghostPairsVerified = true;
            return true;
        }

        private static GameObject NewColliderFixture(Transform vehicle, string name, bool enabled)
        {
            var fixture = new GameObject(name);
            fixture.transform.SetParent(vehicle, false);
            // Keep the same collider body when detached, avoiding an unrelated body
            // reassignment during the touched-pair restoration check.
            var body = fixture.AddComponent<Rigidbody>();
            body.isKinematic = true; body.useGravity = false;
            var collider = fixture.AddComponent<BoxCollider>();
            collider.size = Vector3.one * 0.001f;
            collider.enabled = enabled;
            return fixture;
        }

        private void DestroyColliderFixtures()
        {
            foreach (var fixture in new[] { enabledColliderFixture, installedColliderFixture })
            {
                if (fixture == null) continue;
                fixture.GetComponent<Collider>().enabled = false;
                UnityEngine.Object.Destroy(fixture);
            }
            enabledColliderFixture = null; installedColliderFixture = null;
        }

        private string GuestDriverTransitionStatus(SharedVehicles shared, NWH.VehiclePhysics2.VehicleController vehicle)
        {
            var state = shared.TestState;
            return "owner=" + state.Driver.Owner + "/" + state.Driver.Generation +
                ", CurrentVehicle=" + (gameplay.Get_CurrentVehicleController == vehicle) +
                ", PlayerState=" + gameplay.Get_PlayerState +
                ", mode=" + vehicle.VehicleMultiplayerInstanceType +
                ", kinematic=" + vehicle.vehicleRigidbody.isKinematic +
                ", autoSetInput=" + vehicle.input.autoSetInput +
                ", IsOn=" + vehicle.input.IsOn +
                ", Active=" + vehicle.input.Active;
        }

        private void BeginGuestInputResponse(NWH.VehiclePhysics2.VehicleController vehicle)
        {
            // Neutral, engine running and park brake held are setup only. The tested
            // move into first gear, throttle and braking all come from virtual keys.
            vehicle.input.states.Reset();
            vehicle.input.autoSetInput = true;
            vehicle.powertrain.transmission.ShiftInto(0, true);
            if (vehicle.vehicle_PartsManager.ParkingBrake != null) vehicle.vehicle_PartsManager.ParkingBrake.LoadFromSave = 1;
            vehicle.brakes.handbrakeValue = 1;
            vehicle.vehicleRigidbody.velocity = Vector3.zero;
            vehicle.vehicleRigidbody.angularVelocity = Vector3.zero;
            vehicle.Get_IgnitionPosition = 2;
            phase = 3; deadline = Time.unscaledTime + 8f;
            evidence = "starting the native engine in neutral before virtual input";
        }

        private static Vehicle_Type SelectManualVehicle(SharedVehicles shared)
        {
            foreach (var candidate in new[] { Vehicle_Type.SmollATV, Vehicle_Type.OlTruck, Vehicle_Type.ScrapyardKonig })
            {
                shared.TestType = candidate;
                var transmission = shared.TestVehicle.powertrain.transmission;
                if (transmission.TransmissionType == TransmissionComponent.Type.Manual && transmission.ForwardGearCount >= 1)
                    return candidate;
            }
            throw new InvalidOperationException("Paired native vehicle input response requires a supported scrapyard Konig, stock ATV or truck with a manual transmission and at least one forward gear.");
        }

        private void MovePlayer(Vector3 position)
        {
            gameplay.PlayerWalking.transform.position = position;
            if (gameplay.Get_Player_Rigidbody == null) return;
            gameplay.Get_Player_Rigidbody.position = position;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        private static float Magnitude(VehicleVector velocity) =>
            new Vector3(velocity.X, velocity.Y, velocity.Z).magnitude;

        public void Dispose()
        {
            probe?.Dispose();
            probe = null;
            DestroyColliderFixtures();
            if (hostGhostFixture != null) UnityEngine.Object.Destroy(hostGhostFixture.gameObject);
            hostGhostFixture = null;
        }

        private static void Require(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Paired native vehicle input response: " + reason);
        }
    }
}
