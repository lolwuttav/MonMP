using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using NWH;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The bus is driven by AI_SchoolBus and has no SharedVehicles driver binding.
    // Its two native passenger seats still use the standard host-owned seat registry.
    internal sealed partial class SharedSchoolBusPassengers : IDisposable
    {
        private static readonly FieldInfo? VehicleField = AccessTools.Field(typeof(AI_SchoolBus), "vc");
        private static readonly FieldInfo? SeatFoldedField = AccessTools.Field(typeof(Vehicle_PassengerSeat), "isFolded");
        private static readonly FieldInfo? SeatBackrestField = AccessTools.Field(typeof(Vehicle_PassengerSeat), "FoldSeatBackrest");

        private sealed class Binding
        {
            internal ulong Id;
            internal string Identity = "";
            internal SeatKey Key;
            internal VehicleController Vehicle = null!;
            internal Vehicle_PassengerSeat Seat = null!;
            internal VehiclePassengerSeatState State;
            internal bool ExitPending;
            internal int EnteredFrame = -1;

#if MONMP_DEVELOPMENT
            internal bool FixtureFoldStateCaptured;


            internal bool FixtureOriginalFolded;
#endif

        }

        private readonly Gameplay gameplay;
        private readonly SharedVehicles vehicles;
        private readonly SeatRegistry registry = new SeatRegistry();
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<VehicleController, Binding[]> byVehicle = new Dictionary<VehicleController, Binding[]>();
        private readonly HashSet<VehicleController> busVehicles = new HashSet<VehicleController>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly bool host;
        private readonly string diagnosticsDirectory;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, disposed, exitDeferred;

#if MONMP_DEVELOPMENT
        private bool restoreFixtureSeatsAfterExit;


        private uint fixtureEchoPeer;


        private float fixtureEchoAfter;


        private float nextFixtureEchoDiagnostic;


        private string fixturePoseEchoDiagnostic = "pose echo not staged";
#endif


#if MONMP_DEVELOPMENT
        private string claimDiagnosticsPath = "";
#endif


#if MONMP_DEVELOPMENT
        private string lastClaimDiagnostic = "No bus passenger claim has reached the host.";
#endif


        internal bool Applying => applying;
        internal bool TransfersBlocked { get; set; }
        internal SharedSeatAdmission? SeatAdmission { get; set; }
        internal int SeatCount => bindings.Count;

#if MONMP_DEVELOPMENT
        internal string LastClaimDiagnostic => lastClaimDiagnostic;
#endif


#if MONMP_DEVELOPMENT
        internal string TestPoseEchoDiagnostic => fixturePoseEchoDiagnostic;
#endif

        private Binding[] OrderedBindings => bindings.Values.OrderBy(binding => binding.Identity, StringComparer.Ordinal).ToArray();
        internal uint[] Owners => bindings.Values.Select(binding => binding.State.Owner)
            .Where(owner => owner != SeatOccupancy.Unoccupied).Distinct().ToArray();
        internal bool LocalFlushComplete => session != null && !Owners.Contains(session.LocalPeerId) &&
            !exitDeferred && gameplay.Get_CurrentVehicleController == null && gameplay.Get_PlayerState == PlayerState.Walking;

        internal SharedSchoolBusPassengers(Gameplay gameplay, bool host, string diagnosticsDirectory,
            SharedVehicles vehicles, SharedSchoolBus schoolBus, NativeEntityRegistry identities)
        {
            this.gameplay = gameplay; this.host = host; this.diagnosticsDirectory = diagnosticsDirectory; this.vehicles = vehicles;
            var rows = new List<string> { "status\tbus-path\tseat-path\tseat-position-path\tidentity" };
            var controllers = UnityEngine.Object.FindObjectsOfType<AI_SchoolBus>(true)
                .Where(controller => controller != null && controller.gameObject.scene.IsValid())
                .OrderBy(controller => RuntimeProbe.CanonicalPath(controller.transform), StringComparer.Ordinal).ToArray();
            if (VehicleField != null)
                foreach (var controller in controllers)
                    if (VehicleField.GetValue(controller) is VehicleController vehicle) busVehicles.Add(vehicle);

            // Keep the initial adapter deliberately narrow. The loaded game currently has one
            // supported AI bus and exactly two native passenger seats; do not assign role IDs
            // from incidental component enumeration when that native layout changes.
            if (controllers.Length == 1 && schoolBus.BoundCount == 1 && VehicleField != null)
            {
                var controller = controllers[0];
                var vehicle = VehicleField.GetValue(controller) as VehicleController;
                if (vehicle != null) busVehicles.Add(vehicle);
                string busPath = vehicle != null ? RuntimeProbe.CanonicalPath(vehicle.transform) : "-";
                var seats = vehicle == null ? Array.Empty<Vehicle_PassengerSeat>() : vehicle
                    .GetComponentsInChildren<Vehicle_PassengerSeat>(true)
                    .Where(seat => seat != null && seat.vehicleController == vehicle && seat.seatPosition != null &&
                        seat.transform.IsChildOf(vehicle.transform) && seat.seatPosition.IsChildOf(vehicle.transform))
                    .OrderBy(seat => RuntimeProbe.CanonicalPath(seat.transform), StringComparer.Ordinal).ToArray();

                if (vehicle != null && seats.Length == 2)
                {
                    var vehicleBindings = new List<Binding>();
                    foreach (var seat in seats)
                    {
                        string seatPath = RuntimeProbe.CanonicalPath(seat.transform);
                        string seatPositionPath = RuntimeProbe.CanonicalPath(seat.seatPosition);
                        string identity = seatPath + "/school-bus-passenger-v1/" + seatPositionPath;
                        ulong id = StaticId.Hash(identity);
                        string seatRole = "passenger-" + StaticId.Hash(seatPath).ToString("X16");
                        var key = new SeatKey(StaticId.Hash(busPath + "/school-bus-seat-group-v1"), seatRole);
                        var occupancy = registry.Register(key);
                        var binding = new Binding
                        {
                            Id = id, Identity = identity, Key = key, Vehicle = vehicle, Seat = seat,
                            State = new VehiclePassengerSeatState(occupancy.Peer, occupancy.Generation)
                        };
                        identities.Bind(id, VehiclePassengerSeatState.Adapter, seat.gameObject, identity, true, EntityPersistence.SessionOnly);
                        bindings.Add(id, binding);
                        vehicleBindings.Add(binding);
                        rows.Add("bound\t" + busPath + "\t" + seatPath + "\t" + seatPositionPath + "\t" + identity);
                    }
                    byVehicle.Add(vehicle, vehicleBindings.ToArray());
                }
                else
                {
                    rows.Add("skipped\t" + busPath + "\t-\t-\tExpected exactly two native Vehicle_PassengerSeat components with stable anchors; found " + seats.Length);
                }
            }
            else
            {
                rows.Add("skipped\t-\t-\t-\tExpected one supported AI_SchoolBus binding; found controllers=" + controllers.Length + ", bound=" + schoolBus.BoundCount);
            }


#if MONMP_DEVELOPMENT
            Directory.CreateDirectory(diagnosticsDirectory);


            File.WriteAllLines(Path.Combine(diagnosticsDirectory, "shared-school-bus-passengers.tsv"), rows);
#endif


#if MONMP_DEVELOPMENT
            claimDiagnosticsPath = Path.Combine(diagnosticsDirectory, "shared-school-bus-passenger-claims.tsv");
#endif


#if MONMP_DEVELOPMENT
            File.WriteAllText(claimDiagnosticsPath,
                "elapsed\tpeer\tseat-id\tseat-identity\tdecision\tdetails\r\n");
#endif

        }

        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values
            .Select(binding => new WorldRecord(binding.Id, 1, VehiclePassengerSeatState.Adapter, binding.State.Encode()));
        internal bool OwnsPassengerSeatRecord(ulong id) => bindings.ContainsKey(id);

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected; channel = world;
            connected.PoseReceived += OnPose; connected.PeerLeft += OnLeft;
            world.SnapshotApplied += Snapshot; world.Changed += Changed; world.CommandRequested += Command;
        }

        internal bool IsOwner(uint peer) => bindings.Values.Any(binding => binding.State.Owner == peer);
        internal bool IsCurrentVehicle(Vehicle? vehicle) => vehicle is VehicleController controller && IsBusVehicle(controller);
        internal bool IsBusVehicle(VehicleController? vehicle) => vehicle != null && busVehicles.Contains(vehicle);
        internal Transform? SeatFor(uint peer) => bindings.Values.FirstOrDefault(binding => binding.State.Owner == peer)?.Seat.seatPosition;
        internal VehicleController? VehicleForPeer(uint peer) => bindings.Values.FirstOrDefault(binding => binding.State.Owner == peer)?.Vehicle;

        // Kept internal for the focused paired gameplay fixture; runtime callers use the
        // native Gameplay.EnterExitVehicle patch below.

#if MONMP_DEVELOPMENT
        internal Transform? TestSeatPosition(int index) => index >= 0 && index < OrderedBindings.Length
            ? OrderedBindings[index].Seat.seatPosition : null;


        internal VehiclePassengerSeatState TestState(int index) => index >= 0 && index < OrderedBindings.Length
            ? OrderedBindings[index].State : default;


        internal string TestStageSeatBackrests()
        {
            if (gameplay.Get_PlayerState != PlayerState.Walking)
                throw new InvalidOperationException("Cannot stage bus seat backrests while the local player is not walking.");
            if (SeatFoldedField == null || SeatBackrestField == null)
                throw new InvalidOperationException("Native Vehicle_PassengerSeat fold fields could not be inspected.");
            var status = new List<string>();
            foreach (var binding in OrderedBindings)
            {
                if (!binding.FixtureFoldStateCaptured)
                {
                    binding.FixtureOriginalFolded = ReadFolded(binding.Seat);
                    binding.FixtureFoldStateCaptured = true;
                }
                bool before = ReadFolded(binding.Seat);
                if (before && SeatBackrestField.GetValue(binding.Seat) is Transform)
                    binding.Seat.FoldThisSeat();
                bool after = ReadFolded(binding.Seat);
                status.Add(binding.Identity + ":folded=" + before + "->" + after + ",canUse=" + SafeCanUse(binding.Seat) +
                    ",doorless=" + binding.Vehicle.NoDriverDoor + ",passengerDoorClosed=" + binding.Vehicle.isPassengerDoorClosed +
                    ",backrest=" + (SeatBackrestField.GetValue(binding.Seat) is Transform));
                if (after) throw new InvalidOperationException("Could not reversibly unfold a native bus passenger seat for acceptance: " + status[status.Count - 1]);
            }
            return string.Join(" | ", status);
        }


        internal void TestRestoreSeatBackrests() => RestoreFixtureSeatBackrests();


        internal void TestBeginPoseEcho(uint peer)
        {
            if (!host) throw new InvalidOperationException("Only the paired-test host can start the bus seat pose-echo window.");
            fixtureEchoPeer = peer;
            fixtureEchoAfter = Time.unscaledTime;
            nextFixtureEchoDiagnostic = 0;
            fixturePoseEchoDiagnostic = "waiting for peer " + peer + " pose after " + fixtureEchoAfter.ToString("F3");
        }


        internal bool TestPeerWithinSeat(uint peer, int index)
        {
            if (!host || peer == 0 || peer != fixtureEchoPeer || index < 0 || index >= OrderedBindings.Length) return false;
            bool posePresent = presence.TryGetValue(peer, out var current);
            float age = posePresent ? Time.unscaledTime - current.Time : float.PositiveInfinity;
            bool echoedAfterStage = posePresent && current.Time > fixtureEchoAfter;
            bool fresh = posePresent && age <= 2f;
            bool walking = posePresent && current.Pose.State == (byte)PlayerState.Walking;
            bool holding = MonMPPlugin.Instance?.Items?.PeerHolding(peer) != false;
            var binding = OrderedBindings[index];
            var seat = binding.Seat;
            var anchor = seat?.seatPosition;
            bool nativeReady = seat != null && seat.isActiveAndEnabled && seat.gameObject.activeInHierarchy &&
                anchor != null && anchor.gameObject.activeInHierarchy && SafeCanUse(seat);
            Vector3 location = posePresent ? new Vector3(current.Pose.X, current.Pose.Y, current.Pose.Z) : Vector3.zero;
            float distance = nativeReady && anchor != null ? Vector3.Distance(location, anchor.position) : float.PositiveInfinity;
            bool near = distance <= 4f;
            bool ready = echoedAfterStage && fresh && walking && !holding && nativeReady && near;
            if (Time.unscaledTime >= nextFixtureEchoDiagnostic || ready)
            {
                fixturePoseEchoDiagnostic = "peer=" + peer + ";echoedAfterStage=" + echoedAfterStage + ";poseAge=" +
                    (float.IsInfinity(age) ? "inf" : age.ToString("F1")) + ";walking=" + walking + ";holding=" + holding +
                    ";nativeReady=" + nativeReady + ";player=" + location.ToString("F2") + ";hostSeat=" +
                    (nativeReady && anchor != null ? anchor.position.ToString("F2") : "missing") + ";distance=" +
                    (float.IsInfinity(distance) ? "inf" : distance.ToString("F2")) + ";within4m=" + near;
                nextFixtureEchoDiagnostic = Time.unscaledTime + 0.5f;
            }
            return ready;
        }


        internal bool TestEnterExit(int index)
        {
            var ordered = OrderedBindings;
            if (index < 0 || index >= ordered.Length) return false;
            var binding = ordered[index];
            return EnterExit(binding.Vehicle, binding.Seat.seatPosition, false);
        }
#endif


        internal bool HandlesSeat(Vehicle vehicle, Transform seat, bool driver)
            => vehicle is VehicleController controller && IsBusVehicle(controller);

        internal bool EnterExit(Vehicle vehicle, Transform seat, bool driver)
        {
            if (!(vehicle is VehicleController controller)) return true;
            if (!byVehicle.TryGetValue(controller, out var seats)) return !IsBusVehicle(controller);
            if (applying || disposed) return true;
            if (!Ready) return false;
            if (gameplay.Get_CurrentVehicleController == controller)
            {
                if (gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger)
                {
                    var occupied = seats.FirstOrDefault(binding => binding.State.Owner == session!.LocalPeerId);
                    if (occupied != null) { ExitLocal(controller); RequestExit(occupied); }
                }
                return false;
            }
            if (driver || TransfersBlocked || !seats.Any(binding => binding.Seat.seatPosition == seat)) return false;
            if (gameplay.Get_PlayerState != PlayerState.Walking || gameplay.Player.IsHoldingAnObject) return false;
            var target = seats.First(binding => binding.Seat.seatPosition == seat);
            if (SeatAdmission?.IsPendingLocal(this, target.Id) == true) return false; // a repeat click keeps waiting
            if (SeatAdmission?.TryBeginLocal(this, target.Id, target.State.Generation, session!.LocalPeerId) == false) return false;
            channel!.Request(target.Id, new VehiclePassengerSeatCommand(VehiclePassengerSeatAction.Enter, target.State.Generation).Encode(), true);
            return false;
        }

        internal void NativeExited()
        {
            if (applying || disposed || !Ready) return;
            foreach (var binding in bindings.Values.Where(binding => binding.State.Owner == session!.LocalPeerId).ToArray()) RequestExit(binding);
        }

        internal void FlushLocalOwnership()
        {
            SeatAdmission?.CancelLocal(this);
            if (!Ready || session == null) return;
            foreach (var binding in bindings.Values.Where(binding => binding.State.Owner == session.LocalPeerId).ToArray()) RequestExit(binding);
        }

        internal void RevokeOwnership(uint peer)
            => RevokeOwnership(peer, "host-revoke");

        private void RevokeOwnership(uint peer, string reason)
        {
            if (!host || session?.IsHost != true) throw new InvalidOperationException("Only the host can revoke school-bus passenger ownership.");
            foreach (var binding in bindings.Values.Where(binding => binding.State.Owner == peer).ToArray())
            {
                uint previousGeneration = binding.State.Generation;
                if (!registry.TryRelease(binding.Key, peer, binding.State.Generation, out var released)) continue;
                Apply(binding, new VehiclePassengerSeatState(released.Peer, released.Generation));
                Publish(binding);

#if MONMP_DEVELOPMENT
                RecordClaimDecision(peer, binding, "revoke-ownership", "reason=" + reason + "; previousGeneration=" + previousGeneration +
                    "; newGeneration=" + binding.State.Generation + "; newOwner=" + binding.State.Owner);
#endif

            }
        }

        private bool Ready => channel?.Ready == true;
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);

        private static bool ReadFolded(Vehicle_PassengerSeat? seat)
            => seat != null && SeatFoldedField != null && SeatFoldedField.GetValue(seat) is bool folded && folded;

        private static bool SafeCanUse(Vehicle_PassengerSeat? seat)
        {
            try { return seat != null && seat.CanUse(); }
            catch { return false; }
        }


#if MONMP_DEVELOPMENT
        private void RestoreFixtureSeatBackrests()
        {
            if (gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger)
            {
                restoreFixtureSeatsAfterExit = true;
                return;
            }
            foreach (var binding in bindings.Values)
            {
                if (!binding.FixtureFoldStateCaptured) continue;
                if (ReadFolded(binding.Seat) != binding.FixtureOriginalFolded && SeatBackrestField?.GetValue(binding.Seat) is Transform)
                    binding.Seat.FoldThisSeat();
                binding.FixtureFoldStateCaptured = false;
            }
            restoreFixtureSeatsAfterExit = false;
        }
#endif


#if MONMP_DEVELOPMENT
        private void RecordClaimDecision(uint peer, Binding binding, string decision, string details)
        {
            lastClaimDiagnostic = "decision=" + decision + "; peer=" + peer + "; seat=" + binding.Id.ToString("X16") + "; " + details;
            if (claimDiagnosticsPath.Length == 0) return;
            string clean = lastClaimDiagnostic.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            try { File.AppendAllText(claimDiagnosticsPath, Time.unscaledTime.ToString("F3") + "\t" + peer + "\t" +
                binding.Id.ToString("X16") + "\t" + binding.Identity + "\t" + decision + "\t" + clean + "\r\n"); }
            catch { /* Diagnostics must not alter the authority decision. */ }
        }
#endif


        private void RequestExit(Binding binding)
        {
            if (binding.State.Owner != session!.LocalPeerId) return;
            binding.ExitPending = true;
            channel!.Request(binding.Id, new VehiclePassengerSeatCommand(VehiclePassengerSeatAction.Exit, binding.State.Generation).Encode(), true);
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values.Where(record => record.Adapter == VehiclePassengerSeatState.Adapter && bindings.ContainsKey(record.Id)))
                Apply(bindings[record.Id], VehiclePassengerSeatState.Decode(record.Payload));
        }

        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == VehiclePassengerSeatState.Adapter && bindings.TryGetValue(record.Id, out var binding))
                Apply(binding, VehiclePassengerSeatState.Decode(record.Payload));
        }

        [MonMP.Core.World.WorldCommandAdapters(VehiclePassengerSeatState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || !bindings.TryGetValue(id, out var binding)) return;
            var command = VehiclePassengerSeatCommand.Decode(payload);
            if (command.Action == VehiclePassengerSeatAction.Exit)
            {
                bool exitGenerationMatches = command.ExpectedGeneration == binding.State.Generation;
                if (!exitGenerationMatches || !registry.TryRelease(binding.Key, peer, command.ExpectedGeneration, out var released))
                {

#if MONMP_DEVELOPMENT
                    RecordClaimDecision(peer, binding, "reject-exit", "action=exit; expectedGeneration=" + command.ExpectedGeneration +
                        "; stateGeneration=" + binding.State.Generation + "; stateOwner=" + binding.State.Owner +
                        "; reason=" + (!exitGenerationMatches ? "generation-mismatch" : "registry-release-rejected"));
#endif

                    return;
                }
                Apply(binding, new VehiclePassengerSeatState(released.Peer, released.Generation));
                Publish(binding);

#if MONMP_DEVELOPMENT
                RecordClaimDecision(peer, binding, "accept-exit", "action=exit; expectedGeneration=" + command.ExpectedGeneration +
                    "; newGeneration=" + binding.State.Generation + "; newOwner=" + binding.State.Owner);
#endif

                return;
            }

            bool localPeer = peer == 0;
            (PlayerPose Pose, float Time) current = default;
            bool remotePosePresent = presence.TryGetValue(peer, out current);
            bool hasPose = localPeer || remotePosePresent;
            float poseAge = localPeer ? 0 : remotePosePresent ? Time.unscaledTime - current.Time : float.PositiveInfinity;
            bool poseFresh = localPeer || remotePosePresent && poseAge <= 2f;
            bool peerWalking = localPeer ? gameplay.Get_PlayerState == PlayerState.Walking : remotePosePresent && current.Pose.State == (byte)PlayerState.Walking;
            bool peerHolding = localPeer ? gameplay.Player.IsHoldingAnObject : MonMPPlugin.Instance?.Items?.PeerHolding(peer) != false;
            string holdingKnowledge = localPeer || MonMPPlugin.Instance?.Items != null ? "known" : "unavailable";
            Vector3 location = localPeer
                ? (gameplay.PlayerWalking != null ? gameplay.PlayerWalking.transform.position : gameplay.Player.transform.position)
                : remotePosePresent ? new Vector3(current.Pose.X, current.Pose.Y, current.Pose.Z) : Vector3.zero;

            bool driverFree = !vehicles.IsDriverOwner(peer) && MonMPPlugin.Instance?.World?.BoatMotion.IsOwner(peer) != true;
            bool otherPassengerFree = !vehicles.IsPassengerOwner(peer);
            bool busSeatFree = !registry.TryGetSeat(peer, out _);
            var seat = binding.Seat;
            var seatPosition = seat?.seatPosition;
            bool seatActive = seat != null && seat.isActiveAndEnabled;
            bool seatObjectActive = seat != null && seat.gameObject.activeInHierarchy;
            bool anchorActive = seatPosition != null && seatPosition.gameObject.activeInHierarchy;
            bool nativeCanUse = SafeCanUse(seat);
            bool folded = ReadFolded(seat);
            bool generationMatches = command.ExpectedGeneration == binding.State.Generation;
            float distance = anchorActive && seatPosition != null ? Vector3.Distance(location, seatPosition.position) : float.PositiveInfinity;
            bool withinRange = distance <= 4f;

            var failures = new List<string>();
            if (TransfersBlocked) failures.Add("transfers-blocked");
            if (SeatAdmission?.IsSeated(peer) == true) failures.Add("peer-has-shared-seat");
            if (!driverFree) failures.Add("peer-has-driver-seat");
            if (!otherPassengerFree) failures.Add("peer-has-konig-passenger-seat");
            if (!busSeatFree) failures.Add("peer-has-schoolbus-seat");
            if (!poseFresh) failures.Add("peer-pose-missing-or-stale");
            if (!peerWalking) failures.Add("peer-not-walking");
            if (peerHolding) failures.Add("peer-holding-item-or-unknown");
            if (!seatActive) failures.Add("native-seat-disabled");
            if (!seatObjectActive) failures.Add("native-seat-object-inactive");
            if (!anchorActive) failures.Add("seat-anchor-inactive-or-missing");
            if (!nativeCanUse) failures.Add(folded ? "native-seat-folded" : "native-canuse-false");
            if (!withinRange) failures.Add("peer-outside-4m");
            if (!generationMatches) failures.Add("generation-mismatch");
            if (binding.State.Occupied) failures.Add("seat-already-occupied");

            string details = "action=enter; expectedGeneration=" + command.ExpectedGeneration + "; stateGeneration=" + binding.State.Generation +
                "; stateOwner=" + binding.State.Owner + "; transfersBlocked=" + TransfersBlocked +
                "; driverFree=" + driverFree + "; otherPassengerFree=" + otherPassengerFree + "; busSeatFree=" + busSeatFree +
                "; posePresent=" + hasPose + "; poseAge=" + poseAge.ToString("F3") + "; poseFresh=" + poseFresh +
                "; poseState=" + (localPeer ? (byte)gameplay.Get_PlayerState : remotePosePresent ? current.Pose.State : (byte)255) +
                "; peerWalking=" + peerWalking + "; holding=" + peerHolding + "; holdingKnowledge=" + holdingKnowledge +
                "; seatActive=" + seatActive + "; seatObjectActive=" + seatObjectActive + "; anchorActive=" + anchorActive +
                "; canUse=" + nativeCanUse + "; folded=" + folded + "; NoDriverDoor=" + binding.Vehicle.NoDriverDoor +
                "; passengerDoorClosed=" + binding.Vehicle.isPassengerDoorClosed + "; player=" + location +
                "; seat=" + (anchorActive && seatPosition != null ? seatPosition.position.ToString() : "missing") +
                "; distance=" + (float.IsInfinity(distance) ? "inf" : distance.ToString("F3")) + "; within4m=" + withinRange +
                "; failures=" + (failures.Count == 0 ? "none" : string.Join(",", failures));

            if (failures.Count != 0)
            {

#if MONMP_DEVELOPMENT
                RecordClaimDecision(peer, binding, "reject-enter", details);
#endif

                var reason = TransfersBlocked ? FurnitureSeatRefusal.TransfersBlocked :
                    binding.State.Occupied ? FurnitureSeatRefusal.Occupied :
                    SeatAdmission?.IsSeated(peer) == true || !driverFree || !otherPassengerFree || !busSeatFree ? FurnitureSeatRefusal.AlreadySeated :
                    !poseFresh || !peerWalking ? FurnitureSeatRefusal.NotWalking :
                    peerHolding ? FurnitureSeatRefusal.Holding :
                    !seatActive || !seatObjectActive || !anchorActive || !nativeCanUse ? FurnitureSeatRefusal.NotUsable :
                    !withinRange ? FurnitureSeatRefusal.TooFar : FurnitureSeatRefusal.Stale;
                SeatRefusals.Send(channel, peer, binding.Id, command.ExpectedGeneration, reason);
                return;
            }
            if (!registry.TryClaim(binding.Key, peer, command.ExpectedGeneration, out var claimed))
            {

#if MONMP_DEVELOPMENT
                RecordClaimDecision(peer, binding, "reject-enter", details + "; claimRegistry=false");
#endif

                SeatRefusals.Send(channel, peer, binding.Id, command.ExpectedGeneration, FurnitureSeatRefusal.Occupied);
                return;
            }

            Apply(binding, new VehiclePassengerSeatState(claimed.Peer, claimed.Generation));
            Publish(binding);

#if MONMP_DEVELOPMENT
            RecordClaimDecision(peer, binding, "accept-enter", details + "; claimRegistry=true; newGeneration=" + claimed.Generation);
#endif

        }

        private void Apply(Binding binding, VehiclePassengerSeatState state)
        {
            if (!host && state.Generation != binding.State.Generation && !Wire.IsNewer(state.Generation, binding.State.Generation)) return;
            bool allowEntry = SeatAdmission?.AllowsLocalEntry(this, binding.Id, state.Generation) != false;
            SeatAdmission?.Observe(this, binding.Id, state.Generation);
            bool wasLocalSeat = binding.State.Owner == session!.LocalPeerId;
            bool changed = binding.State.Owner != state.Owner || binding.State.Generation != state.Generation;
            binding.State = state;
            if (changed) binding.ExitPending = false;

            // Several native seats share this controller. Only revocation of our own
            // seat may eject us; another passenger's update is presentation-only here.
            if (wasLocalSeat && gameplay.Get_CurrentVehicleController == binding.Vehicle &&
                gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger && state.Owner != session!.LocalPeerId)
                ExitLocal(binding.Vehicle);

            if (state.Owner != session!.LocalPeerId || gameplay.Get_CurrentVehicleController == binding.Vehicle || binding.ExitPending) return;
            if (!allowEntry || TransfersBlocked || gameplay.Get_PlayerState != PlayerState.Walking || gameplay.Player.IsHoldingAnObject)
            {
                RequestExit(binding);
                return;
            }

            applying = true;
            try
            {
                gameplay.EnterExitVehicle(binding.Vehicle, binding.Seat.seatPosition, false);
                binding.EnteredFrame = Time.frameCount;
            }
            finally { applying = false; }
        }

        private void Publish(Binding binding) => channel!.Publish(binding.Id, binding.State.Encode());

        private void OnLeft(uint peer)
        {
            presence.Remove(peer);
            if (host) RevokeOwnership(peer, "peer-disconnected");
        }

        private void ExitLocal(Vehicle expected)
        {
            if (gameplay.Get_CurrentVehicleController != expected) return;
            var local = bindings.Values.FirstOrDefault(binding => binding.Vehicle == expected && binding.EnteredFrame == Time.frameCount);
            if (local != null)
            {
                if (!exitDeferred) { exitDeferred = true; MonMPPlugin.Instance!.StartCoroutine(ExitNextFrame(expected)); }
                return;
            }
            applying = true;
            try { AccessTools.Method(typeof(Gameplay), "Force_ExitVehicle").Invoke(gameplay, null); }
            finally { applying = false; }
        }

        private System.Collections.IEnumerator ExitNextFrame(Vehicle expected)
        {
            yield return null;
            exitDeferred = false;
            var currentGameplay = gameplay;
            if (currentGameplay != null && currentGameplay.Get_CurrentVehicleController == expected) ExitLocal(expected);
#if MONMP_DEVELOPMENT
            if (restoreFixtureSeatsAfterExit && currentGameplay != null && currentGameplay.Get_PlayerState == PlayerState.Walking)

#if MONMP_DEVELOPMENT
                RestoreFixtureSeatBackrests();
#else
{}
#endif


#endif
        }

        public void Dispose()
        {
            if (disposed) return;
            SeatAdmission?.CancelLocal(this);
            if (gameplay != null && IsCurrentVehicle(gameplay.Get_CurrentVehicleController)) ExitLocal(gameplay.Get_CurrentVehicleController!);
#if MONMP_DEVELOPMENT
            if (gameplay == null || gameplay.Get_PlayerState != PlayerState.InVehicle_Passenger)

#if MONMP_DEVELOPMENT
                RestoreFixtureSeatBackrests();
#else
{}
#endif

            else restoreFixtureSeatsAfterExit = true;

#endif
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            channel = null; session = null;
        }
    }
}
