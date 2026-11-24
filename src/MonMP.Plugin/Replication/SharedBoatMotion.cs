using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using NWH;
using NWH.Common.Input;
using NWH.DWP2.ShipController;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // Host-owned motion for the two stock DWP2 boats. The host always simulates buoyancy and thrust;
    // remote copies are kinematic and receive only vetted input from the current driver lease.
    internal sealed partial class SharedBoatMotion : IDisposable
    {
        private sealed class Binding
        {
            public ulong Id;
            public string Identity = "";
            public AdvancedShipController Ship = null!;
            public Rigidbody Body = null!;
            public Vehicle_DriverSeat DriverSeat = null!;
            public Engine[] Engines = Array.Empty<Engine>();
            public Rudder[] Rudders = Array.Empty<Rudder>();
            public Thruster[] Thrusters = Array.Empty<Thruster>();
            public BoatMotionState State = null!, Original = null!;
            public ShipInputStates OriginalInputs;
            public bool OriginalInputActive, OriginalAutoInput, OriginalKinematic;
            public Vector3 OriginalPosition;
            public Quaternion OriginalRotation;
            public Vector3 TargetPosition, TargetVelocity, TargetAngularVelocity;
            public Quaternion TargetRotation;
            public float PoseReceivedAt, NextSend, NextControl, PendingSince, LastControlAt, DriverGrantedAt, NextReleaseRetry;
            public bool HasPose, PendingEnter, PendingEngineStartStop, ReleasePending, ShapeMismatchLogged, AwaitingDriverPose;
            public bool DisconnectSecuring;
            public float NextDisconnectAnchor;
        }

        private const float InteractionRangeSquared = 25f;
        private const float DriverPoseTimeout = 1.5f;
        private const float InputTimeout = 0.35f;
        private const float SnapshotInterval = 0.05f;
        private const float ControlInterval = 0.05f;

        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly NativeEntityRegistry identities;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<AdvancedShipController, Binding> byShip = new Dictionary<AdvancedShipController, Binding>();
        private readonly Dictionary<Engine, Binding> byEngine = new Dictionary<Engine, Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly BoatEngineFuelGuard<Part_FuelTank> engineFuelGuard = new BoatEngineFuelGuard<Part_FuelTank>();
        private readonly ItemLeaseTable drivers = new ItemLeaseTable();
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, disposed;

        internal int Count => bindings.Count;
        internal bool Ready => !disposed && session?.IsRunning == true && channel?.FullySynchronized == true &&
            MonMPPlugin.Instance?.World?.PersonalProfileReady == true && MonMPPlugin.Instance?.World?.SaveGate.Active != true;
        private bool SessionRunning => !disposed && session?.IsRunning == true;
        internal bool TransfersBlocked { get; set; }
        internal ItemCarriers? Carriers { get; set; }
        internal SharedBoatControls? Controls { get; set; }
        internal event Action<uint, Vector3>? DriverExited;

        internal SharedBoatMotion(Gameplay gameplay, bool host, string diagnostics, NativeEntityRegistry identities)
        {
            this.gameplay = gameplay;
            this.host = host;
            this.identities = identities;
            var lines = new List<string> { "id\tboat\tidentity\tstatus" };
            var boats = Object.FindObjectsOfType<AdvancedShipController>(true)
                .Where(IsSupportedBoat)
                .OrderBy(boat => SharedBuildingDoors.StablePath(boat.transform), StringComparer.Ordinal)
                .ToArray();

            foreach (var boat in boats)
            {
                if (!SupportedShape(boat, out var body, out var seat, out var engines, out var rudders, out var thrusters, out var reason))
                {
                    lines.Add("-\t" + boat.vehicle_type + "\t" + SharedBuildingDoors.StablePath(boat.transform) + "\tskipped-" + reason);
                    continue;
                }

                string identity = SharedBuildingDoors.StablePath(boat.transform) + "/boat-motion-v1";
                ulong id = StaticId.Hash(identity);
                if (id == 0 || bindings.ContainsKey(id) || identities.Resolve(id) != null)
                {
                    lines.Add("-\t" + boat.vehicle_type + "\t" + identity + "\tidentity-collision");
                    continue;
                }
                var lease = new ItemLease(ItemLease.Unowned, 1);
                var state = CaptureInitial(boat, body, engines, lease);
                var binding = new Binding
                {
                    Id = id, Identity = identity, Ship = boat, Body = body, DriverSeat = seat,
                    Engines = engines, Rudders = rudders, Thrusters = thrusters,
                    State = state, Original = state, OriginalInputs = boat.input.states,
                    OriginalInputActive = boat.input.Active, OriginalAutoInput = boat.input.autoSetInput,
                    OriginalKinematic = body.isKinematic, OriginalPosition = body.position,
                    OriginalRotation = body.rotation, TargetPosition = body.position,
                    TargetRotation = body.rotation, TargetVelocity = body.velocity,
                    TargetAngularVelocity = body.angularVelocity
                };
                identities.Bind(id, BoatMotionState.Adapter, boat.gameObject, identity, true, EntityPersistence.SessionOnly);
                bindings.Add(id, binding);
                byShip.Add(boat, binding);
                foreach (var engine in engines) byEngine.Add(engine, binding);
                if (host) drivers.Add(id);
                lines.Add(id.ToString("X16") + "\t" + boat.vehicle_type + "\t" + identity + "\tengines=" + engines.Length + ";rudders=" + rudders.Length + ";thrusters=" + thrusters.Length);
            }


#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-boat-motion.tsv"), lines);
#endif

        }

        private static bool IsSupportedBoat(AdvancedShipController boat) => boat != null && boat.gameObject.scene.IsValid() &&
            boat.gameObject.scene.isLoaded && (boat.vehicle_type == Vehicle_Type.FishingBoat || boat.vehicle_type == Vehicle_Type.SmallCruiser);

        private static bool SupportedShape(AdvancedShipController boat, out Rigidbody body, out Vehicle_DriverSeat seat,
            out Engine[] engines, out Rudder[] rudders, out Thruster[] thrusters, out string reason)
        {
            body = boat.vehicleRigidbody != null ? boat.vehicleRigidbody : boat.GetComponent<Rigidbody>()!;
            var seats = boat.GetComponentsInChildren<Vehicle_DriverSeat>(true)
                .Where(value => value != null && value.vehicleController == boat).ToArray();
            engines = boat.engines?.ToArray() ?? Array.Empty<Engine>();
            rudders = boat.rudders?.ToArray() ?? Array.Empty<Rudder>();
            thrusters = boat.thrusters?.ToArray() ?? Array.Empty<Thruster>();
            seat = seats.Length == 1 ? seats[0] : null!;
            if (body == null || boat.Anchor == null || boat.Anchor.GetComponentInParent<AdvancedShipController>() != boat)
                reason = "boat-rigidbody-or-anchor-topology";
            else if (seats.Length != 1 || seat.seatPosition == null || seat.seatPosition.GetComponentInParent<AdvancedShipController>() != boat)
                reason = "expected-one-native-driver-seat-found-" + seats.Length;
            else if (engines.Length == 0 || engines.Length > BoatMotionState.MaxEngines || engines.Any(value => value == null) || boat.Fuel_Tank == null)
                reason = "engine-or-native-fuel-topology";
            else if (boat.vehicle_Visual_GasPedal == null)
                reason = "native-gas-pedal-topology";
            else if (rudders.Length == 0 || rudders.Length > BoatMotionState.MaxEngines || rudders.Any(value => value == null))
                reason = "rudder-topology";
            else if (thrusters.Length > BoatMotionState.MaxEngines || thrusters.Any(value => value == null))
                reason = "thruster-topology";
            else
            {
                reason = "supported";
                return true;
            }
            return false;
        }

        private static BoatMotionState CaptureInitial(AdvancedShipController ship, Rigidbody body, Engine[] engines, ItemLease lease)
        {
            var p = body.position;
            var q = body.rotation;
            return new BoatMotionState(lease, new ItemPose(p.x, p.y, p.z, q.x, q.y, q.z, q.w),
                Vector(body.velocity), Vector(body.angularVelocity), ship.input.Steering, ship.input.Throttle,
                ship.input.Throttle2, ship.input.Throttle3, ship.input.Throttle4,
                ship.input.BowThruster, ship.input.SternThruster, Vector(ship.Anchor.AnchorPosition),
                engines.Length, EngineMask(engines));
        }

        private static byte EngineMask(Engine[] engines)
        {
            byte mask = 0;
            for (int i = 0; i < engines.Length; i++) if (engines[i].isOn) mask |= (byte)(1 << i);
            return mask;
        }

        private static VehicleVector Vector(Vector3 value) => new VehicleVector(value.x, value.y, value.z);
        private static Vector3 Vector(VehicleVector value) => new Vector3(value.X, value.Y, value.Z);
        private static Vector3 Position(ItemPose pose) => new Vector3(pose.X, pose.Y, pose.Z);
        private static Quaternion Rotation(ItemPose pose) => new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw);

        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.OrderBy(binding => binding.Id)
            .Select(binding => new WorldRecord(binding.Id, 1, BoatMotionState.Adapter, binding.State.Encode()));

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session;
            this.channel = channel;
            session.PoseReceived += Pose;
            session.PeerLeft += Left;
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed;
            channel.CommandRequested += Command;
            if (!host)
            {
                foreach (var binding in bindings.Values)
                {
                    binding.Body.isKinematic = true;
                    SetInput(binding, false, false);
                }
            }
        }

        private void Pose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);

        private void Left(uint peer)
        {
            presence.Remove(peer);
            if (!host) return;
            foreach (var binding in bindings.Values.Where(value => value.State.Driver.Owner == peer).ToArray())
                Release(binding, peer, binding.State.Driver.Generation, true, secure: true);
        }

        internal Part_FuelTank? BeginNativeEngineUpdate(Engine engine)
        {
            bool mappedBoat = byEngine.TryGetValue(engine, out var binding);
            return engineFuelGuard.BeginNativeEngineUpdate(SessionRunning, host, mappedBoat, binding?.Ship.Fuel_Tank);
        }

        internal void EndNativeEngineUpdate(Part_FuelTank tank) => engineFuelGuard.EndNativeEngineUpdate(tank);

        internal bool SuppressNativeFuelWrite(Part_FuelTank tank) => engineFuelGuard.SuppressNativeFuelWrite(tank);

        internal bool AllowNativeFuelWrite(Part_FuelTank tank) => engineFuelGuard.AllowNativeFuelWrite(tank);

        private bool Usable(Binding binding)
        {
            var identity = identities.Resolve(binding.Id);
            return identity != null && identity.IsStaticSceneEntity && identity.AdapterType == BoatMotionState.Adapter &&
                identity.SpawnKey == binding.Identity && identity.Persistence == EntityPersistence.SessionOnly &&
                binding.Ship != null && binding.Ship.gameObject.scene.IsValid() && binding.Ship.gameObject.scene.isLoaded &&
                binding.Body != null && binding.DriverSeat != null && binding.DriverSeat.seatPosition != null &&
                channel?.Records.TryGetValue(binding.Id, out var record) == true && record.Adapter == BoatMotionState.Adapter;
        }

        private Binding? Find(Vehicle? vehicle) => vehicle is AdvancedShipController ship && byShip.TryGetValue(ship, out var binding) ? binding : null;

        // Shared vehicle-seat routing asks this before dispatching to SharedVehicles. This keeps
        // canonical DWP2 boats on their own host-authoritative motion and driver-lease path.
        internal bool Handles(Vehicle? vehicle) => Find(vehicle) is { } binding && Usable(binding);

        // Called before native seat entry. Guests request a lease first, then enter after the host
        // publishes it; this keeps the local copy from briefly acquiring native physics control.
        internal bool EnterExit(Vehicle vehicle, Transform seat, bool driver)
        {
            if (applying || !SessionRunning) return true;
            var binding = Find(vehicle);
            if (binding == null || !Usable(binding)) return true;
            if (gameplay.Get_CurrentVehicleController == vehicle) return true; // native path is an exit or seat swap
            if (!driver) return true;
            if (seat != binding.DriverSeat.seatPosition)
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("This boat seat is not in the shared driver layout.");
                return false;
            }
            if (!Ready || TransfersBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true ||
                gameplay.Get_PlayerState != PlayerState.Walking || gameplay.Player.IsHoldingAnObject)
                return false;
            if (!host && !BoatPurchased(binding.Ship))
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("Buy this boat before requesting its shared driver seat.");
                return false;
            }

            uint local = session!.LocalPeerId;
            if (binding.State.Driver.Owner == local)
            {
                EnterGrantedLocal(binding);
                return false;
            }
            if (binding.State.Driver.Owner != ItemLease.Unowned)
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("Someone else is driving this boat.");
                return false;
            }
            if (SeatAdmission?.TryBeginLocal(this, binding.Id, binding.State.Driver.Generation, local) == false) return false;
            if (host)
            {
                if (!AcquireHostLocal(binding))
                {
                    SeatAdmission?.CancelLocal(this);
                    MonMPPlugin.Instance?.ReportItemUseUnavailable("The boat driver seat is unavailable.");
                    return false;
                }
                binding.Ship.input.autoSetInput = binding.OriginalAutoInput;
                return true;
            }

            binding.PendingEnter = true;
            binding.PendingSince = Time.unscaledTime;
            var request = new BoatMotionCommand(BoatMotionAction.AcquireDriver, binding.State.Driver.Generation);
            channel!.Request(binding.Id, request.Encode(), useLease: true);
            return false;
        }

        private bool AcquireHostLocal(Binding binding)
        {
            var lease = binding.State.Driver;
            if (lease.Owner != ItemLease.Unowned || PeerAlreadySeated(session!.LocalPeerId) ||
                !drivers.TryAcquire(binding.Id, session.LocalPeerId, lease.Generation)) return false;
            var nextLease = drivers[binding.Id];
            SeatAdmission?.Observe(this, binding.Id, nextLease.Generation);
            binding.State = Capture(binding).WithDriver(nextLease);
            ApplyMode(binding);
            channel!.Publish(binding.Id, binding.State.Encode());
            return true;
        }

        private void EnterGrantedLocal(Binding binding)
        {
            if (TransfersBlocked || gameplay.Get_PlayerState != PlayerState.Walking || gameplay.Player.IsHoldingAnObject ||
                Vector3.Distance(gameplay.PlayerWalking.transform.position, binding.DriverSeat.transform.position) > 5f)
            {
                binding.PendingEnter = false;
                RequestReleaseLocal(binding);
                return;
            }
            binding.Ship.input.autoSetInput = binding.OriginalAutoInput;
            applying = true;
            try { gameplay.EnterExitVehicle(binding.Ship, binding.DriverSeat.seatPosition, true); }
            finally { applying = false; }
            binding.PendingEnter = false;
            ApplyMode(binding);
        }

        private void RequestReleaseLocal(Binding binding)
        {
            if (session == null || binding.State.Driver.Owner != session.LocalPeerId ||
                binding.ReleasePending && Time.unscaledTime < binding.NextReleaseRetry) return;
            if (binding.DisconnectSecuring && !DisconnectAnchorReady(binding))
            {
                // Keep the exact driver lease until the host's anchor acknowledgement.
                // Ordinary save/seat exits never enter this path; shutdown retains its
                // caller's bounded ownership-drain deadline if a reply cannot arrive.
                RequestDisconnectAnchor(binding);
                if (!DisconnectAnchorReady(binding)) return;
            }
            if (host) Release(binding, session.LocalPeerId, binding.State.Driver.Generation, true);
            else if (channel?.Ready == true)
            {
                binding.ReleasePending = true;
                binding.NextReleaseRetry = Time.unscaledTime + 0.5f;
                channel.Request(binding.Id, new BoatMotionCommand(BoatMotionAction.ReleaseDriver, binding.State.Driver.Generation).Encode(), useLease: true);
            }
        }

        private void ResetReleaseForDriverChange(Binding binding, ItemLease driver)
        {
            if (binding.State.Driver.Owner == driver.Owner && binding.State.Driver.Generation == driver.Generation) return;
            binding.ReleasePending = false;
            binding.NextReleaseRetry = 0;
            binding.DisconnectSecuring = false;
            binding.NextDisconnectAnchor = 0;
            Controls?.CancelDisconnectAnchor(binding.Ship);
        }

        internal bool SetInputToPlayer(AdvancedShipController ship, bool active)
        {
            if (!SessionRunning || applying || !byShip.TryGetValue(ship, out var binding)) return true;
            if (!active)
            {
                if (binding.State.Driver.Owner == session!.LocalPeerId) RequestReleaseLocal(binding);
                return true;
            }
            // Native EnterExitVehicle enables input before setting InVehicle_Driver. The lease
            // and current vehicle already identify the authorized driver at that point.
            if (!Ready || gameplay.Get_CurrentVehicleController != ship ||
                binding.State.Driver.Owner != session!.LocalPeerId)
            {
                SetInput(binding, false, false);
                return false;
            }
            ship.input.autoSetInput = binding.OriginalAutoInput;
            return true;
        }

        // DWP2 declares these actions on its providers but its ShipInputHandler.Update samples only
        // rudder, the first throttle and camera. Complete the declared native boat inputs only while
        // the local driver owns the host lease.
        internal void SampleNativeInputs(AdvancedShipController ship)
        {
            if (!SessionRunning || !byShip.TryGetValue(ship, out var binding) || !ship.input.Active || !ship.input.autoSetInput ||
                binding.State.Driver.Owner != session!.LocalPeerId || !Ready) return;
            ship.input.Throttle2 = InputProvider.CombinedInput((ShipInputProvider provider) => provider.Throttle2());
            ship.input.Throttle3 = InputProvider.CombinedInput((ShipInputProvider provider) => provider.Throttle3());
            ship.input.Throttle4 = InputProvider.CombinedInput((ShipInputProvider provider) => provider.Throttle4());
            ship.input.BowThruster = InputProvider.CombinedInput((ShipInputProvider provider) => provider.BowThruster());
            ship.input.SternThruster = InputProvider.CombinedInput((ShipInputProvider provider) => provider.SternThruster());
            // The stock provider initializes vehicleInputActions but leaves its optional ship
            // action map null. Its ignition and anchor still work through native interactables.
            ship.input.EngineStartStop = InputProvider.CombinedInput((ShipInputProvider provider) =>
                HasShipActions(provider) && provider.EngineStartStop());
            ship.input.Anchor = InputProvider.CombinedInput((ShipInputProvider provider) =>
                HasShipActions(provider) && provider.Anchor());
            if (ship.input.EngineStartStop) binding.PendingEngineStartStop = true;
        }

        private static bool HasShipActions(ShipInputProvider provider) =>
            provider is not InputSystemShipInputProvider stock || stock.shipInputActions != null;

        private void SetInput(Binding binding, bool active, bool local)
        {
            if (binding.Ship == null) return;
            applying = true;
            try
            {
                binding.Ship.input.autoSetInput = local && binding.OriginalAutoInput;
                binding.Ship.SetInputToPlayer(active);
                if (!active) ResetInput(binding.Ship.input);
            }
            finally { applying = false; }
        }

        private static void ResetInput(ShipInputHandler input)
        {
            input.Steering = 0;
            input.Throttle = 0;
            input.Throttle2 = 0;
            input.Throttle3 = 0;
            input.Throttle4 = 0;
            input.BowThruster = 0;
            input.SternThruster = 0;
            input.SubmarineDepth = 0;
            input.EngineStartStop = false;
            input.Anchor = false;
        }

        private bool IsLocalDriver(Binding binding) => session != null && binding.State.Driver.Owner == session.LocalPeerId;

        private void ApplyMode(Binding binding)
        {
            if (host)
            {
                bool local = binding.State.Driver.Owner == session?.LocalPeerId &&
                    gameplay.Get_CurrentVehicleController == binding.Ship && gameplay.Get_PlayerState == PlayerState.InVehicle_Driver;
                bool remote = binding.State.Driver.Owner != ItemLease.Unowned && !local;
                if (remote) SetInput(binding, false, false);
                else if (local) SetInput(binding, true, true);
                else SetInput(binding, false, false);
                return;
            }

            binding.Body.isKinematic = true;
            bool localDriver = IsLocalDriver(binding);
            if (!localDriver) SetInput(binding, false, false);
            else if (gameplay.Get_CurrentVehicleController == binding.Ship && gameplay.Get_PlayerState == PlayerState.InVehicle_Driver)
                SetInput(binding, true, true);
        }

        internal uint[] Owners => bindings.Values.Select(binding => binding.State.Driver.Owner)
            .Where(owner => owner != ItemLease.Unowned).Distinct().ToArray();
        internal bool IsOwner(uint peer) => bindings.Values.Any(binding => binding.State.Driver.Owner == peer);
        internal SharedSeatAdmission? SeatAdmission { get; set; }

        internal bool LocalFlushComplete => session != null && !Owners.Contains(session.LocalPeerId) &&
            !(gameplay.Get_CurrentVehicleController is AdvancedShipController ship && byShip.ContainsKey(ship) && gameplay.Get_PlayerState == PlayerState.InVehicle_Driver);

        internal void FlushLocalOwnership()
        {
            SeatAdmission?.CancelLocal(this);
            if (session == null) return;
            foreach (var binding in bindings.Values.Where(value => value.State.Driver.Owner == session.LocalPeerId).ToArray())
            {
                if (binding.DisconnectSecuring && !DisconnectAnchorReady(binding))
                {
                    // A Walking pose would independently release this host lease before
                    // the reliable anchor request arrives. Stay seated through its reply.
                    RequestDisconnectAnchor(binding);
                    if (!DisconnectAnchorReady(binding)) continue;
                }
                if (gameplay.Get_CurrentVehicleController == binding.Ship && gameplay.Get_PlayerState == PlayerState.InVehicle_Driver)
                    ExitLocal(binding);
                RequestReleaseLocal(binding);
            }
        }

        internal void PrepareLocalDisconnect()
        {
            if (disposed || session == null) return;
            foreach (var binding in bindings.Values.Where(value => value.State.Driver.Owner != ItemLease.Unowned &&
                (host || value.State.Driver.Owner == session.LocalPeerId)).ToArray())
            {
                binding.DisconnectSecuring = true;
                RequestDisconnectAnchor(binding);
            }
        }
        internal void CancelLocalDisconnect()
        {
            foreach (var binding in bindings.Values)
            {
                binding.DisconnectSecuring = false;
                binding.NextDisconnectAnchor = 0;
                Controls?.CancelDisconnectAnchor(binding.Ship);
            }
        }

        private void RequestDisconnectAnchor(Binding binding)
        {
            if (host)
            {
                if (binding.Ship.Anchor.Dropped) return;
                if (Controls?.DropAnchorOnDisconnect(binding.Ship) != true) binding.Ship.Anchor.Drop();
                return;
            }
            if (Time.unscaledTime < binding.NextDisconnectAnchor) return;
            binding.NextDisconnectAnchor = Time.unscaledTime + 0.5f;
            Controls?.RequestDisconnectAnchor(binding.Ship, binding.State.Driver.Generation);
        }

        private bool DisconnectAnchorReady(Binding binding) => host ? binding.Ship.Anchor.Dropped :
            Controls?.DisconnectAnchorConfirmed(binding.Ship, binding.State.Driver.Generation) == true;

        internal void RevokeOwnership(uint peer)
        {
            if (!host || session?.IsHost != true) throw new InvalidOperationException("Only the host can revoke boat driver ownership.");
            foreach (var binding in bindings.Values.Where(value => value.State.Driver.Owner == peer).ToArray())
                Release(binding, peer, binding.State.Driver.Generation, true);
        }

        internal void NativeExited()
        {
            if (!SessionRunning || applying || session == null) return;
            foreach (var binding in bindings.Values.Where(value => value.State.Driver.Owner == session.LocalPeerId).ToArray())
                if (gameplay.Get_CurrentVehicleController != binding.Ship || gameplay.Get_PlayerState != PlayerState.InVehicle_Driver)
                    RequestReleaseLocal(binding);
        }

        internal IEnumerable<(ulong Id, Transform Frame)> CarrierFrames()
        {
            foreach (var binding in bindings.Values)
                if (binding.Ship != null && binding.Ship.gameObject.activeInHierarchy) yield return (binding.Id, binding.Ship.transform);
        }

        internal bool TryGetSeatFrame(uint peer, out Transform frame)
        {
            var binding = bindings.Values.FirstOrDefault(value => value.State.Driver.Owner == peer);
            if (binding != null && binding.DriverSeat != null && binding.DriverSeat.seatPosition != null)
            { frame = binding.DriverSeat.seatPosition; return true; }
            frame = null!;
            return false;
        }

        internal Vehicle? VehicleForPeer(uint peer) => bindings.Values.FirstOrDefault(value => value.State.Driver.Owner == peer)?.Ship;
        internal bool OwnsDriver(AdvancedShipController ship, uint peer, uint generation) =>
            byShip.TryGetValue(ship, out var binding) && peer != ItemLease.Unowned &&
            binding.State.Driver.Owner == peer && binding.State.Driver.Generation == generation;
        internal bool PrepareDriverDisconnect(AdvancedShipController ship, uint peer, uint generation)
        {
            if (!host || !byShip.TryGetValue(ship, out var binding) || !drivers.Owns(binding.Id, peer, generation)) return false;
            binding.DisconnectSecuring = true;
            return true;
        }
        internal bool TryGetReconnectExit(uint peer, out Vector3 position) =>
            NativeVehicleDeparture.TryExit(VehicleForPeer(peer), true, out position);

        internal bool TryGetSteeringWheel(uint peer, out Transform wheel, out float angle, out Vector3 vehicleRight)
        {
            wheel = null!; angle = 0; vehicleRight = Vector3.right;
            var binding = bindings.Values.FirstOrDefault(value => value.State.Driver.Owner == peer);
            var ship = binding?.Ship;
            if (ship == null || ship.vehicle_Visual_SteeringWheel == null || binding!.Rudders.Length == 0 ||
                !ship.vehicle_Visual_SteeringWheel.gameObject.activeInHierarchy) return false;
            wheel = ship.vehicle_Visual_SteeringWheel;
            angle = binding.Rudders[0].Angle * ship.SteeringWheel_TurnRatio;
            vehicleRight = ship.transform.right;
            return true;
        }

        internal void FixedUpdate()
        {
            if (disposed || host || !Ready) return;
            float dt = Mathf.Max(Time.fixedDeltaTime, 0.001f);
            foreach (var binding in bindings.Values)
            {
                if (!binding.HasPose || binding.Ship == null || !binding.Ship.gameObject.activeInHierarchy) continue;
                float age = Mathf.Clamp(Time.unscaledTime - binding.PoseReceivedAt, 0f, 0.12f);
                Vector3 predictedPosition = binding.TargetPosition + binding.TargetVelocity * age;
                Quaternion predictedRotation = binding.TargetRotation;
                float angularSpeed = binding.TargetAngularVelocity.magnitude;
                if (angularSpeed > 0.001f)
                    predictedRotation = Quaternion.AngleAxis(angularSpeed * age * Mathf.Rad2Deg, binding.TargetAngularVelocity / angularSpeed) * predictedRotation;
                if ((binding.Body.position - predictedPosition).sqrMagnitude > 25f)
                {
                    binding.Body.position = predictedPosition;
                    binding.Body.rotation = predictedRotation;
                }
                else
                {
                    float blend = 1f - Mathf.Exp(-16f * dt);
                    binding.Body.MovePosition(Vector3.Lerp(binding.Body.position, predictedPosition, blend));
                    binding.Body.MoveRotation(Quaternion.Slerp(binding.Body.rotation, predictedRotation, blend));
                }
                binding.Body.velocity = binding.TargetVelocity;
                binding.Body.angularVelocity = binding.TargetAngularVelocity;
            }
        }

        internal void Update()
        {
            if (disposed || !Ready) return;
            if (host)
            {
                foreach (var binding in bindings.Values)
                {
                    if (!Usable(binding)) continue;
                    uint owner = binding.State.Driver.Owner;
                    if (owner != ItemLease.Unowned && owner != session!.LocalPeerId)
                    {
                        bool seated = DriverPresenceValid(binding, owner, out _);
                        if (seated) binding.AwaitingDriverPose = false;
                        // The grant must reach the guest before its first seated pose can return.
                        // Controls still require a validated driver pose throughout this grace.
                        bool entering = binding.AwaitingDriverPose && Time.unscaledTime - binding.DriverGrantedAt <= 3f &&
                            FreshWalkingPose(owner, binding, out _);
                        if (!seated && !entering)
                        {
                            bool driverTimedOut = !presence.TryGetValue(owner, out var lastPose) ||
                                Time.unscaledTime - lastPose.Time > DriverPoseTimeout;
                            Release(binding, owner, binding.State.Driver.Generation, true, secure: driverTimedOut);
                            continue;
                        }
                        if (Time.unscaledTime - binding.LastControlAt > InputTimeout) ResetInput(binding.Ship.input);
                    }
                    else if (owner == session!.LocalPeerId &&
                        (gameplay.Get_CurrentVehicleController != binding.Ship || gameplay.Get_PlayerState != PlayerState.InVehicle_Driver))
                    {
                        Release(binding, owner, binding.State.Driver.Generation, true);
                    }

                    if (Time.unscaledTime < binding.NextSend) continue;
                    binding.NextSend = Time.unscaledTime + SnapshotInterval;
                    var next = Capture(binding);
                    binding.State = next;
                    channel!.Publish(binding.Id, next.Encode());
                }
                return;
            }

            foreach (var binding in bindings.Values)
            {
                if (binding.PendingEnter && Time.unscaledTime - binding.PendingSince > 3f) binding.PendingEnter = false;
                if (binding.State.Driver.Owner != session!.LocalPeerId ||
                    gameplay.Get_CurrentVehicleController != binding.Ship || gameplay.Get_PlayerState != PlayerState.InVehicle_Driver)
                    continue;
                if (Time.unscaledTime < binding.NextControl) continue;
                binding.NextControl = Time.unscaledTime + ControlInterval;
                var input = binding.Ship.input;
                var frame = new BoatMotionCommand(BoatMotionAction.ControlFrame, binding.State.Driver.Generation,
                    input.Steering, input.Throttle, input.Throttle2, input.Throttle3, input.Throttle4,
                    input.BowThruster, input.SternThruster, binding.PendingEngineStartStop);
                binding.PendingEngineStartStop = false;
                channel!.Request(binding.Id, frame.Encode(), useLease: true);
            }
        }

        private BoatMotionState Capture(Binding binding)
        {
            var ship = binding.Ship;
            var p = binding.Body.position;
            var q = binding.Body.rotation;
            return new BoatMotionState(binding.State.Driver, new ItemPose(p.x, p.y, p.z, q.x, q.y, q.z, q.w),
                Vector(binding.Body.velocity), Vector(binding.Body.angularVelocity), ship.input.Steering, ship.input.Throttle,
                ship.input.Throttle2, ship.input.Throttle3, ship.input.Throttle4, ship.input.BowThruster,
                ship.input.SternThruster, Vector(ship.Anchor.AnchorPosition), binding.Engines.Length, EngineMask(binding.Engines));
        }

        private bool DriverPresenceValid(Binding binding, uint peer, out PlayerPose pose)
        {
            pose = default;
            return presence.TryGetValue(peer, out var value) && (pose = value.Pose).State == (byte)PlayerState.InVehicle_Driver &&
                Time.unscaledTime - value.Time <= DriverPoseTimeout && binding.DriverSeat != null &&
                (binding.DriverSeat.seatPosition == null || Vector3.Distance(new Vector3(pose.X, pose.Y, pose.Z), binding.DriverSeat.seatPosition.position) <= 20f);
        }

        private bool FreshWalkingPose(uint peer, Binding binding, out PlayerPose pose)
        {
            pose = default;
            if (!presence.TryGetValue(peer, out var value)) return false;
            pose = value.Pose;
            return pose.State == (byte)PlayerState.Walking && Time.unscaledTime - value.Time <= 2f &&
                Vector3.Distance(new Vector3(pose.X, pose.Y, pose.Z), binding.DriverSeat.seatPosition.position) <= 5f;
        }

        private FurnitureSeatRefusal DriverRefusal(Binding binding, uint peer, uint generation, bool alreadyDriving, bool poseValid)
        {
            var current = binding.State.Driver;
            if (TransfersBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true) return FurnitureSeatRefusal.TransfersBlocked;
            if (current.Owner != ItemLease.Unowned && current.Owner != peer) return FurnitureSeatRefusal.Occupied;
            if (alreadyDriving) return FurnitureSeatRefusal.AlreadySeated;
            if (MonMPPlugin.Instance?.Items?.PeerHolding(peer) == true) return FurnitureSeatRefusal.Holding;
            if (!poseValid)
                return presence.TryGetValue(peer, out var seen) && seen.Pose.State == (byte)PlayerState.Walking &&
                    Time.unscaledTime - seen.Time <= 2f ? FurnitureSeatRefusal.TooFar : FurnitureSeatRefusal.NotWalking;
            return generation != current.Generation ? FurnitureSeatRefusal.Stale : FurnitureSeatRefusal.NotUsable;
        }

        private bool PeerAlreadySeated(uint peer)
        {
            var world = MonMPPlugin.Instance?.World;
            return SeatAdmission?.IsSeated(peer) == true || bindings.Values.Any(value => value.State.Driver.Owner == peer) ||
                world?.Vehicles?.IsDriverOwner(peer) == true || world?.Vehicles?.IsPassengerOwner(peer) == true ||
                world?.SchoolBusPassengers?.IsOwner(peer) == true || world?.ExtraSeats?.IsOwner(peer) == true;
        }

        private static bool BoatPurchased(AdvancedShipController ship)
        {
            var todo = Singleton<ToDoList_Manager>.i;
            if (todo == null) return false;
            return ship.vehicle_type == Vehicle_Type.FishingBoat
                ? todo.isDone(ToDoList_Line.Boat_Fishing_Bought)
                : todo.isDone(ToDoList_Line.Boat_SmallCruiserBought);
        }

        [WorldCommandAdapters(BoatMotionState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || !SessionRunning || channel?.Ready != true || !bindings.TryGetValue(id, out var binding) ||
                !Usable(binding) || peer == 0 || peer == ItemLease.Unowned) return;
            BoatMotionCommand command;
            try { command = BoatMotionCommand.Decode(payload); }
            catch (InvalidDataException) { return; }

            var current = binding.State.Driver;
            if (command.Action == BoatMotionAction.ReleaseDriver)
            {
                if (BoatMotionAuthorityPolicy.CanAcceptRelease(host, SessionRunning, channel.Ready, Usable(binding),
                    peer, command.ExpectedGeneration, current))
                    Release(binding, peer, command.ExpectedGeneration, true);
                return;
            }
            if (!Ready) return;
            if (command.Action == BoatMotionAction.AcquireDriver)
            {
                bool poseValid = FreshWalkingPose(peer, binding, out _);
                bool alreadyDriving = PeerAlreadySeated(peer);
                bool eligible = BoatMotionAuthorityPolicy.CanAcquire(Ready, TransfersBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true,
                    BoatPurchased(binding.Ship), poseValid, poseValid, peer, command.ExpectedGeneration, current);
                if (!eligible || alreadyDriving || MonMPPlugin.Instance?.Items?.PeerHolding(peer) == true ||
                    gameplay.Get_IsSleeping || gameplay.Get_IsDead || !drivers.TryAcquire(id, peer, command.ExpectedGeneration))
                {
                    SeatRefusals.Send(channel, peer, id, command.ExpectedGeneration,
                        DriverRefusal(binding, peer, command.ExpectedGeneration, alreadyDriving, poseValid));
                    return;
                }
                binding.State = Capture(binding).WithDriver(drivers[id]);
                binding.AwaitingDriverPose = true;
                binding.DriverGrantedAt = Time.unscaledTime;
                binding.LastControlAt = Time.unscaledTime;
                ApplyMode(binding);
                channel!.Publish(id, binding.State.Encode());
                return;
            }

            bool hasDriverPose = DriverPresenceValid(binding, peer, out _);
            if (!BoatMotionAuthorityPolicy.CanControl(Ready, TransfersBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true,
                BoatPurchased(binding.Ship), hasDriverPose, hasDriverPose, peer, command.ExpectedGeneration, current) ||
                !drivers.Owns(id, peer, command.ExpectedGeneration) || gameplay.Get_IsSleeping || gameplay.Get_IsDead) return;

            var input = binding.Ship.input;
            input.Steering = command.Steering;
            input.Throttle = command.Throttle;
            input.Throttle2 = command.Throttle2;
            input.Throttle3 = command.Throttle3;
            input.Throttle4 = command.Throttle4;
            input.BowThruster = command.BowThruster;
            input.SternThruster = command.SternThruster;
            if (command.EngineStartStop) input.EngineStartStop = true;
            binding.LastControlAt = Time.unscaledTime;
        }

        private void Release(Binding binding, uint peer, uint generation, bool publish, bool secure = false)
        {
            if (!host || !drivers.Release(binding.Id, peer, generation)) return;
            var lease = drivers[binding.Id];
            if (secure || binding.DisconnectSecuring) RequestDisconnectAnchor(binding);
            ResetInput(binding.Ship.input);
            binding.PendingEngineStartStop = false;
            binding.AwaitingDriverPose = false;
            binding.LastControlAt = 0;
            binding.DisconnectSecuring = false;
            binding.NextDisconnectAnchor = 0;
            binding.State = Capture(binding).WithDriver(lease);
            if (NativeVehicleDeparture.TryExit(binding.Ship, true, binding.State.Pose, out var exit)) DriverExited?.Invoke(peer, exit);
            ApplyMode(binding);
            if (publish && channel?.FullySynchronized == true) channel.Publish(binding.Id, binding.State.Encode());
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            var staged = records.Values.Where(record => record.Adapter == BoatMotionState.Adapter)
                .Select(record => (Binding: bindings.TryGetValue(record.Id, out var binding) ? binding : throw new InvalidDataException("Unknown boat-motion record."),
                    State: BoatMotionState.Decode(record.Payload))).ToArray();
            foreach (var item in staged) Apply(item.Binding, item.State);
        }

        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == BoatMotionState.Adapter)
                Apply(bindings.TryGetValue(record.Id, out var binding) ? binding : throw new InvalidDataException("Unknown boat-motion record."),
                    BoatMotionState.Decode(record.Payload));
        }

        private void Apply(Binding binding, BoatMotionState state)
        {
            if (state.EngineCount != binding.Engines.Length)
            {
                if (!binding.ShapeMismatchLogged)
                {
                    binding.ShapeMismatchLogged = true;
                    Debug.LogWarning("MonMP boat motion excluded a mismatched native engine layout: " + binding.Identity +
                        "; host=" + state.EngineCount + "; local=" + binding.Engines.Length);
                }
                return;
            }

            SeatAdmission?.Observe(this, binding.Id, state.Driver.Generation);
            bool wasLocal = IsLocalDriver(binding);
            ResetReleaseForDriverChange(binding, state.Driver);
            binding.State = state;
            binding.TargetPosition = Position(state.Pose);
            binding.TargetRotation = Rotation(state.Pose);
            binding.TargetVelocity = Vector(state.Velocity);
            binding.TargetAngularVelocity = Vector(state.AngularVelocity);
            binding.PoseReceivedAt = Time.unscaledTime;
            if (!binding.HasPose)
            {
                binding.Body.position = binding.TargetPosition;
                binding.Body.rotation = binding.TargetRotation;
                binding.HasPose = true;
            }
            binding.Ship.Anchor.AnchorPosition = Vector(state.AnchorPosition);
            ApplyMode(binding);
            bool localDriver = IsLocalDriver(binding);
            if (!localDriver)
            {
                CopyInputs(binding.Ship.input, state);
                for (int i = 0; i < binding.Engines.Length; i++)
                {
                    bool running = state.EngineOn(i);
                    if (binding.Engines[i].isOn == running) continue;
                    binding.Engines[i].isOn = running;
                }
            }
            if (wasLocal && !localDriver && gameplay.Get_CurrentVehicleController == binding.Ship &&
                gameplay.Get_PlayerState == PlayerState.InVehicle_Driver)
                ExitLocal(binding);
            else if (localDriver && binding.PendingEnter)
                EnterGrantedLocal(binding);
            // Unowned snapshots sent before the host processes our request must not cancel it.
            else if (!localDriver && binding.PendingEnter && state.Driver.Owner != ItemLease.Unowned)
                binding.PendingEnter = false;
        }

        private static void CopyInputs(ShipInputHandler input, BoatMotionState state)
        {
            input.Steering = state.Steering;
            input.Throttle = state.Throttle;
            input.Throttle2 = state.Throttle2;
            input.Throttle3 = state.Throttle3;
            input.Throttle4 = state.Throttle4;
            input.BowThruster = state.BowThruster;
            input.SternThruster = state.SternThruster;
            input.EngineStartStop = false;
            input.Anchor = false;
        }

        private void ExitLocal(Binding binding)
        {
            if (gameplay.Get_CurrentVehicleController != binding.Ship || gameplay.Get_PlayerState != PlayerState.InVehicle_Driver) return;
            applying = true;
            // Coordinated save/death/lease cleanup must not depend on native door flags.
            try { AccessTools.Method(typeof(Gameplay), "Force_ExitVehicle").Invoke(gameplay, null); }
            finally { applying = false; }
        }

        public void Dispose()
        {
            if (disposed) return;
            SeatAdmission?.CancelLocal(this);
            disposed = true;
            var failures = new List<Exception>();
            void Restore(Action action)
            {
                try { action(); }
                catch (Exception ex) { failures.Add(ex); }
            }
            if (session != null) { session.PoseReceived -= Pose; session.PeerLeft -= Left; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            // A bounded disconnect can expire while waiting for the anchor reply.
            // Evacuate the native walker before moving a guest boat to its baseline.
            foreach (var binding in bindings.Values) Restore(() => ExitLocal(binding));
            if (host)
            {
                foreach (var binding in bindings.Values.Where(value => value.State.Driver.Owner != ItemLease.Unowned).ToArray())
                    Restore(() => Release(binding, binding.State.Driver.Owner, binding.State.Driver.Generation, false));
            }
            else
            {
                applying = true;
                try
                {
                    foreach (var binding in bindings.Values)
                    {
                        Restore(() =>
                        {
                            binding.Body.isKinematic = binding.OriginalKinematic;
                            binding.Body.position = binding.OriginalPosition;
                            binding.Body.rotation = binding.OriginalRotation;
                            binding.Body.velocity = Vector(binding.Original.Velocity);
                            binding.Body.angularVelocity = Vector(binding.Original.AngularVelocity);
                        });
                        Restore(() =>
                        {
                            binding.Ship.input.autoSetInput = binding.OriginalAutoInput;
                            binding.Ship.SetInputToPlayer(binding.OriginalInputActive);
                            binding.Ship.input.states = binding.OriginalInputs;
                        });
                        Restore(() => binding.Ship.Anchor.AnchorPosition = Vector(binding.Original.AnchorPosition));
                        for (int i = 0; i < binding.Engines.Length; i++)
                        {
                            bool running = binding.Original.EngineOn(i);
                            if (binding.Engines[i].isOn == running) continue;
                            int engine = i;
                            Restore(() => binding.Engines[engine].isOn = running);
                        }
                    }
                }
                finally { applying = false; }
            }
            session = null;
            channel = null;
            if (failures.Count != 0) throw new AggregateException("Shared boat restoration failed.", failures);
        }
    }
}
