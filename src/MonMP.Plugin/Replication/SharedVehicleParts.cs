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
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Adapter 17: the part sockets of every shared driven vehicle (Konig, truck, ATV, buggy, Prinz). The actor performs the
    // native interaction after bounded input preflight, preserving native feedback and
    // achievements; the resulting socket state is then broadcast so everyone else's copy matches.
    // Session-only: not carried through native save/reload or host save coordination.
    internal sealed partial class SharedVehicleParts : IDisposable
    {
        private sealed class Binding
        {
            public ulong Id;
            public Vehicle_PartSocket Socket = null!;
            public VehiclePartState State, Original;
            public Vehicle_Type Type;
            public ulong PurchasedVehicleEntity;
            public int WaitThroughFrame;
            public bool OriginalCaptured = true;
            public MountedPartEditQueue? Edits;
        }
        private sealed class PurchasedVehicle
        {
            public ulong Entity;
            public Vehicle_Type Type;
            public GameObject Instance = null!;
            public bool Catalogued;
        }
        // Shared part sockets are enumerated from each native manager. Scrap Konig and Prinz each
        // use their single canonical GlobalClassManager entry, including while that native body is inactive.
        internal static readonly Vehicle_Type[] Fleet = SharedVehiclePartIdentityPolicy.Fleet;
        private static bool IsCanonicalScrapyardVehicle(VehicleController vehicle)
        {
            var global = UnityEngine.Object.FindObjectOfType<GlobalClassManager>();
            return global != null && global.GetVehicles(Vehicle_Type.ScrapyardKonig) == vehicle;
        }
        // The canonical scrapyard Konig counts whether or not progression has activated it yet: a
        // played save loads it active, while a menu-joining guest's fresh world keeps it inactive.
        private static IEnumerable<VehicleController> CanonicalScrapyardVehicle()
        {
            var global = UnityEngine.Object.FindObjectOfType<GlobalClassManager>();
            if (global?.GetVehicles(Vehicle_Type.ScrapyardKonig) is VehicleController scrapyard && scrapyard != null) yield return scrapyard;
        }
        internal static IEnumerable<VehicleController> CanonicalPrinzVehicle()
        {
            var prinz = SharedVehiclePartIdentityPolicy.CanonicalPrinzVehicle(UnityEngine.Object.FindObjectOfType<GlobalClassManager>());
            if (prinz != null) yield return prinz;
        }
        private static bool IsCanonicalPrinzVehicle(VehicleController vehicle) =>
            SharedVehiclePartIdentityPolicy.IsCanonicalPrinzVehicle(
                UnityEngine.Object.FindObjectOfType<GlobalClassManager>(), vehicle);
        internal static IEnumerable<(Vehicle_Type Type, Vehicle_PartsManager Manager)> FleetManagers() =>
            UnityEngine.Object.FindObjectsOfType<VehicleController>().Concat(HardModeWorldLayout.SessionStoredStarters())
                .Concat(CanonicalScrapyardVehicle()).Concat(CanonicalPrinzVehicle()).Distinct()
                .Where(v => Array.IndexOf(Fleet, v.vehicle_type) >= 0 && v.gameObject.scene.IsValid() &&
                    (v.vehicle_type != Vehicle_Type.ScrapyardKonig || IsCanonicalScrapyardVehicle(v)) &&
                    (v.vehicle_type != Vehicle_Type.Prinz || IsCanonicalPrinzVehicle(v)) &&
                    v.GetComponent<Vehicle_PartsManager>() != null)
                .OrderBy(v => Array.IndexOf(Fleet, v.vehicle_type)).ThenBy(v => RuntimeProbe.CanonicalPath(v.transform), StringComparer.Ordinal)
                .Select(v => (v.vehicle_type, v.GetComponent<Vehicle_PartsManager>()));

        private readonly bool host;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Vehicle_PartSocket, Binding> bySocket = new Dictionary<Vehicle_PartSocket, Binding>();
        private readonly Dictionary<ulong, PurchasedVehicle> purchasedVehicles = new Dictionary<ulong, PurchasedVehicle>();
        private readonly Dictionary<ulong, (Binding Binding, VehiclePartState State)> deferredPurchasedApply =
            new Dictionary<ulong, (Binding, VehiclePartState)>();
        private readonly Dictionary<string, ushort> prefabByPath = new Dictionary<string, ushort>(StringComparer.Ordinal);
        private readonly Dictionary<ushort, GameObject> assetByPrefab = new Dictionary<ushort, GameObject>();
        private readonly NativeEntityRegistry identities;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying;
        private bool disposed;
        private float nextPurchasedRetry;
        private static readonly FieldInfo TurboWastegateClips = typeof(Part_Engine_ExhaustManifold_Turbo)
            .GetField("clips", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(Part_Engine_ExhaustManifold_Turbo).FullName, "clips");
        private static readonly MethodInfo RpmGaugeKnobText = AccessTools.Method(typeof(Part_Autometer_RPM), "Update_Knob_Text", Type.EmptyTypes)
            ?? throw new MissingMethodException(typeof(Part_Autometer_RPM).FullName, "Update_Knob_Text");
        private static readonly MethodInfo RearSeatVisual = AccessTools.Method(typeof(Part_Seat_Rear), "UpdateFoldVisual", Type.EmptyTypes)
            ?? throw new MissingMethodException(typeof(Part_Seat_Rear).FullName, "UpdateFoldVisual");
        internal bool IsApplying => applying;
        public int Count => bindings.Count;
        public bool Ready => channel?.Ready == true;
        internal IEnumerable<ulong> RequiredCatalogEntityIds => bindings.Values
            .Where(b => b.PurchasedVehicleEntity == 0 && b.Id > WorldRecord.DynamicIdStart).Select(b => b.Id);
        internal bool IsPurchasedRecord(ulong id) => bindings.TryGetValue(id, out var binding) && binding.PurchasedVehicleEntity != 0;

        public SharedVehicleParts(bool host, string diagnostics, NativeEntityRegistry identities)
        {
            this.host = host;
            this.identities = identities ?? throw new ArgumentNullException(nameof(identities));
            var catalog = ItemPrefabCatalog.Load(diagnostics);
            foreach (var entry in catalog.Entries.Values)
            { prefabByPath[entry.Definition.Path] = entry.Definition.Id; assetByPrefab[entry.Definition.Id] = entry.Asset; }
            var lines = new List<string>();
            var sales = SharedVehicleSales.Active;
            if (sales != null)
                foreach (var purchased in sales.PurchasedVehicles())
                    RegisterPurchasedVehicle(purchased.Entity, purchased.Type, purchased.Instance);
            int managers = 0;
            foreach (var (type, manager) in FleetManagers())
            {
                if (SharedVehicleSales.Active?.OwnsSaleClone(manager.transform) == true) continue;
                managers++;
                foreach (var socket in manager.carPartSockets)
                {
                    if (socket?.gameobject == null) continue;
                    string socketPath = SharedBuildingDoors.StablePath(socket.gameobject.transform);
                    string identity = SharedVehiclePartIdentityPolicy.PartSocketIdentity(type, socketPath);
                    ulong id = SharedVehiclePartIdentityPolicy.PartSocketEntityId(type, socketPath);
                    if (bindings.ContainsKey(id) || bySocket.ContainsKey(socket)) { lines.Add("# duplicate socket identity skipped: " + identity); continue; }
                    var b = new Binding { Id = id, Socket = socket, Type = type };
                    b.State = Capture(b);
                    b.Original = b.State;
                    bindings.Add(id, b); bySocket.Add(socket, b);
                    identities.Bind(id, VehiclePartState.Adapter, socket.gameobject, identity, true, EntityPersistence.SessionOnly);
                    lines.Add(id.ToString("X16") + "\t" + type + "\t" + identity + "\t" + socket.car_PartsType + "\t" + b.State.Installed);
                }
            }
            lines.Sort(StringComparer.Ordinal);

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-vehicle-parts.tsv"),
                new[] { managers > 0 ? "id\tvehicle\tsocket-path\ttype\tinstalled" : "# No shared vehicle Vehicle_PartsManager found; part sockets are not shared this session." }.Concat(lines));
#endif

        }
        private ushort LookupPrefab(InteractableParts? part) => part != null && prefabByPath.TryGetValue(part.PrefabName, out ushort id) ? id : (ushort)0;
        internal IEnumerable<(ulong Id, Vehicle_PartSocket Socket)> PaintSockets =>
            bindings.Values.Select(b => (b.Id, b.Socket));
        internal Vehicle_PartSocket? SocketOf(ulong id) => bindings.TryGetValue(id, out var b) ? b.Socket : null;
        // Tire pressure keys the vehicle's wheel socket, not the removable part object's
        // transient transform. A replacement wheel in the same fleet socket therefore
        // keeps the same network identity; non-fleet and tire-changer wheels are excluded.
        internal IEnumerable<(ulong SocketId, Vehicle_PartSocket Socket, Vehicle_Type Type)> TireSockets =>
            bindings.Values.Where(b => b.Socket != null && b.Socket.car_PartsType == Car_PartsType.Wheel &&
                Array.IndexOf(Fleet, b.Type) >= 0)
                .Select(b => (b.Id, b.Socket, b.Type));
        internal bool TryGetTireSocket(Part_Driveline_Wheel wheel, out ulong socketId,
            out Vehicle_PartSocket? socket, out Vehicle_Type vehicleType)
        {
            socketId = 0; socket = null; vehicleType = default;
            if (wheel == null) return false;
            foreach (var binding in bindings.Values)
            {
                if (Array.IndexOf(Fleet, binding.Type) < 0 ||
                    binding.Socket.car_PartsType != Car_PartsType.Wheel || !binding.Socket.isInstalled ||
                    binding.Socket.partInstalled == null || binding.Socket.partInstalled.GetComponent<Part_Driveline_Wheel>() != wheel)
                    continue;
                socketId = binding.Id; socket = binding.Socket; vehicleType = binding.Type;
                return true;
            }
            return false;
        }
        internal ushort PaintPrefab(InteractableParts part) => LookupPrefab(part);
        private VehiclePartState Capture(Binding binding)
        {
            var socket = binding.Socket;
            if (!socket.isInstalled || socket.partInstalled == null) return VehiclePartState.Empty;
            var part = socket.partInstalled;
            return CapturePart(binding, part, socket.CheckIfAllBoltAre100Percent());
        }
        private VehiclePartState CapturePart(Binding binding, InteractableParts part, bool tightened)
        {
            ushort prefab = LookupPrefab(part);
            if (IsSteeringRackBinding(binding) && part is Part_SteeringRack rack)
                return new VehiclePartState(true, prefab, tightened, rack.Value1, rack.Value2, rack.IntValue1, rack.IntValue2);
            if (IsWheelBinding(binding) && part is Part_Driveline_Wheel wheel)
                return new VehiclePartState(true, prefab, tightened, intValue1: wheel.IntValue1);
            if (MountedValuesKind(part) != MountedVehiclePartKind.None)
                return new VehiclePartState(true, prefab, tightened, part.Value1, part.Value2, part.IntValue1, part.IntValue2);
            return new VehiclePartState(true, prefab, tightened);
        }

        private static bool IsSteeringRackBinding(Binding binding) =>
            SharedVehiclePartIdentityPolicy.IsSteeringRackBinding(binding.Type, binding.Socket.car_PartsType);

        // A wheel's mounted tire (native TireType) is part of its state: a bare prefab clone has none.
        private static bool IsWheelBinding(Binding binding) => binding.Socket.car_PartsType == Car_PartsType.Wheel;

        private static MountedVehiclePartKind MountedValuesKind(InteractableParts? part) =>
            part is Part_Shock_Coilover ? MountedVehiclePartKind.Coilover :
            part is Part_ControlArm_Tuned ? MountedVehiclePartKind.TunedControlArm :
            part is Part_Seat_Rear ? MountedVehiclePartKind.RearSeat :
            part is Part_Autometer_RPM ? MountedVehiclePartKind.RpmGaugeShiftThreshold :
            part is Part_Autometer_Speed ? MountedVehiclePartKind.SpeedGaugeUnits :
            part is Part_Engine_ExhaustManifold_Turbo ? MountedVehiclePartKind.TurboWastegateSound :
            part is Part_Suspension_Hub_Stake ? MountedVehiclePartKind.StakePickupHistory :
            part is Part_Spoiler ? MountedVehiclePartKind.SpoilerPickupHistory : MountedVehiclePartKind.None;

        private static int MountedOptionCount(InteractableParts? part) =>
            part is Part_Engine_ExhaustManifold_Turbo turbo && TurboWastegateClips.GetValue(turbo) is AudioClip[] clips
                ? clips.Length : 0;

        private static bool IsValidMountedState(InteractableParts? part, VehiclePartState state)
        {
            var kind = MountedValuesKind(part);
            return kind != MountedVehiclePartKind.None &&
                MountedVehiclePartValues.IsValid(state, kind, MountedOptionCount(part));
        }

        private MountedVehiclePartKind MountedValuesKind(VehiclePartState state) =>
            state.Installed && assetByPrefab.TryGetValue(state.Prefab, out var asset)
                ? MountedValuesKind(asset.GetComponent<InteractableParts>()) : MountedVehiclePartKind.None;

        private bool TryResolvePart(Binding binding, VehiclePartState state, out GameObject? asset, out bool steeringRack)
        {
            asset = null; steeringRack = IsSteeringRackBinding(binding);
            if (!state.Installed) return !state.HasNativeValues;
            if (!assetByPrefab.TryGetValue(state.Prefab, out asset)) return false;
            var part = asset.GetComponent<InteractableParts>();
            if (!SharedVehiclePartIdentityPolicy.IsCompatiblePart(binding.Type, binding.Socket.car_PartsType, part)) return false;
            steeringRack &= part is Part_SteeringRack;
            if (IsSteeringRackBinding(binding) && !steeringRack) return false;
            if (IsWheelBinding(binding)) return part is Part_Driveline_Wheel && state.HasValidWheelValues;
            var mountedKind = MountedValuesKind(part);
            if (mountedKind != MountedVehiclePartKind.None) return IsValidMountedState(part, state);
            return steeringRack ? state.HasValidSteeringRackValues : !state.HasNativeValues;
        }

        public IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, VehiclePartState.Adapter, b.State.Encode()));

        // Sale instances are the only post-session vehicle catalogs admitted here. Their
        // identities are rooted in the stable sale entity, never the clone's scene path.
        internal bool RegisterPurchasedVehicle(ulong entity, Vehicle_Type type, GameObject instance)
        {
            if (disposed) return false;
            if (!PurchasedVehiclePartIdentityPolicy.Supports(type)) return false;
            if (entity <= WorldRecord.DynamicIdStart) throw new InvalidOperationException("Purchased vehicle part root is not dynamic.");
            if (instance == null || !instance.scene.IsValid()) throw new InvalidOperationException("Purchased vehicle part root is outside the active scene.");
            if (purchasedVehicles.TryGetValue(entity, out var existing))
            {
                if (existing.Type != type || existing.Instance != instance)
                    throw new InvalidOperationException("Conflicting purchased vehicle part root for entity " + entity.ToString("X16") + ".");
                if (existing.Catalogued) return true;
            }
            else
            {
                if (purchasedVehicles.Values.Any(value => value.Instance == instance && value.Entity != entity))
                    throw new InvalidOperationException("Purchased vehicle already has a different part-catalog identity.");
                existing = new PurchasedVehicle { Entity = entity, Type = type, Instance = instance };
                purchasedVehicles.Add(entity, existing);
            }
            if (!TryRegisterPurchasedSockets(existing)) return true;
            existing.Catalogued = true;
            return true;
        }

        private bool TryRegisterPurchasedSockets(PurchasedVehicle purchase)
        {
            var controllers = purchase.Instance.GetComponentsInChildren<VehicleController>(true);
            if (controllers.Length != 1 || controllers[0] == null || controllers[0].vehicle_type != purchase.Type) return false;
            var manager = controllers[0].GetComponent<Vehicle_PartsManager>();
            if (manager == null || manager.carPartSockets == null || manager.carPartSockets.Count == 0) return false;
            var sockets = manager.carPartSockets.Where(socket => PurchasedVehiclePartIdentityPolicy.IncludesSocket(purchase.Type, socket)).ToArray();
            if (sockets.Length == 0) return false;
            foreach (var socket in sockets)
            {
                if (!PurchasedVehiclePartIdentityPolicy.TryRelativePath(purchase.Instance.transform, socket.gameobject.transform, out string relativePath))
                    throw new InvalidOperationException("Purchased vehicle socket is outside its sale clone.");
                string identity = PurchasedVehiclePartIdentityPolicy.PartSocketIdentity(purchase.Entity, purchase.Type, relativePath);
                ulong id = PurchasedVehiclePartIdentityPolicy.PartSocketEntityId(purchase.Entity, purchase.Type, relativePath);
                if (bindings.TryGetValue(id, out var existingBinding))
                {
                    if (!ReferenceEquals(existingBinding.Socket, socket) || existingBinding.PurchasedVehicleEntity != purchase.Entity)
                        throw new InvalidOperationException("Purchased vehicle socket identity collision: " + identity);
                    continue;
                }
                if (bySocket.TryGetValue(socket, out existingBinding))
                {
                    if (existingBinding.Id != id || existingBinding.PurchasedVehicleEntity != purchase.Entity)
                        throw new InvalidOperationException("Purchased vehicle socket already has a different identity.");
                    continue;
                }
                var binding = new Binding
                {
                    Id = id, Socket = socket, Type = purchase.Type, PurchasedVehicleEntity = purchase.Entity,
                    WaitThroughFrame = Time.frameCount, OriginalCaptured = false
                };
                binding.State = Capture(binding);
                binding.Original = binding.State;
                bindings.Add(id, binding); bySocket.Add(socket, binding);
                identities.Bind(id, VehiclePartState.Adapter, socket.gameobject, identity, true, EntityPersistence.SessionOnly);
                if (host && channel != null)
                    channel.Register(new WorldRecord(id, 1, VehiclePartState.Adapter, binding.State.Encode()));
                else if (!host && channel?.Records.TryGetValue(id, out var record) == true)
                    QueuePurchasedApply(binding, VehiclePartState.Decode(record.Payload));
            }
            return true;
        }

        internal bool IsPurchasedNativeReady(ulong id) => bindings.TryGetValue(id, out var binding) && IsPurchasedNativeReady(binding);

        // Pressure shares the wheel that a purchased part can create or replace. Give that
        // adapter the exact pre-mirror boundary before this first purchased part apply mutates it.
        internal event Action<ulong>? PurchasedBaselineCapturing;

        private void CapturePurchasedBaseline(Binding binding)
        {
            if (binding.PurchasedVehicleEntity == 0 || binding.OriginalCaptured) return;
            PurchasedBaselineCapturing?.Invoke(binding.Id);
            binding.Original = Capture(binding);
            binding.State = binding.Original;
            binding.OriginalCaptured = true;
        }

        private static bool IsPurchasedNativeReady(Binding binding)
        {
            // Awake supplies the manager catalog; wait through one native frame for Start/save
            // initialization before taking a guest restoration baseline or replaying host state.
            return binding.PurchasedVehicleEntity == 0 || Time.frameCount > binding.WaitThroughFrame &&
                // Native hides empty sockets unless a compatible part is held locally. Their
                // visibility is presentation, not readiness to install an accepted host part.
                binding.Socket != null && binding.Socket.gameobject != null &&
                binding.Socket.vehicleAttached != null && binding.Socket.vehicleAttached.gameObject.activeInHierarchy;
        }

        private void QueuePurchasedApply(Binding binding, VehiclePartState state)
        {
            if (host || binding.PurchasedVehicleEntity == 0) return;
            deferredPurchasedApply[binding.Id] = (binding, state);
        }

        private void RetryPurchasedCatalogs()
        {
            if (Time.unscaledTime >= nextPurchasedRetry)
            {
                nextPurchasedRetry = Time.unscaledTime + 0.1f;
                foreach (var purchase in purchasedVehicles.Values.Where(value => !value.Catalogued).ToArray())
                    if (TryRegisterPurchasedSockets(purchase)) purchase.Catalogued = true;
            }
            if (host || channel == null || deferredPurchasedApply.Count == 0) return;
            foreach (var pair in deferredPurchasedApply.Values.OrderBy(value => SnapshotOrder(value.Binding.Socket.car_PartsType)).ToArray())
            {
                var binding = pair.Binding;
                if (!IsPurchasedNativeReady(binding)) continue;
                if (!channel.Records.TryGetValue(binding.Id, out var record)) continue;
                var state = VehiclePartState.Decode(record.Payload);
                if (!TryResolvePart(binding, state, out var asset, out var steeringRack))
                    throw new InvalidDataException("Invalid deferred purchased vehicle part state.");
                CapturePurchasedBaseline(binding);
                Apply(binding, state, asset, steeringRack);
                deferredPurchasedApply.Remove(binding.Id);
            }
        }

        internal bool AllowNativeSolidityCheck() => host || session?.IsRunning != true;

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PeerLeft += PartPeerLeft;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command;
            channel.CommandRejected += Rejected; channel.ReplyReceived += Reply;
        }
        // Called after the local player's own native install/remove/tighten interaction, win or lose a race.
        public void NotifyChanged(Vehicle_PartSocket socket)
        {
            if (applying || !Ready || !bySocket.TryGetValue(socket, out var b)) return;
            var state = Capture(b);
            if (state.Equals(b.State)) return;
            if (!host && state.Installed != b.State.Installed)
            { BeginPartChange(b, state, socket.partInstalled); return; }
            if (!host)
            {
                var kind = MountedValuesKind(state);
                int optionCount = MountedOptionCount(b.Socket.partInstalled);
                if (kind != MountedVehiclePartKind.None && (b.Edits ??= new MountedPartEditQueue()).Enqueue(b.State, state, kind, optionCount))
                { b.State = state; SendMountedEdit(b); return; }
                b.Edits?.Clear(); // Structural changes retain the ordinary whole-state path.
            }
            b.State = state;
            if (host) channel!.Publish(b.Id, state.Encode());
            else channel!.Request(b.Id, state.Encode());
        }
        private void SendMountedEdit(Binding b)
        {
            if (b.Edits == null || channel?.FullySynchronized != true || channel.RepairPending || channel.ReceivingSnapshot ||
                !channel.Records.TryGetValue(b.Id, out var record)) return;
            var state = VehiclePartState.Decode(record.Payload);
            var kind = MountedValuesKind(state);
            InteractableParts? part = state.Installed && assetByPrefab.TryGetValue(state.Prefab, out var asset)
                ? asset.GetComponent<InteractableParts>() : null;
            if (!b.Edits.Prepare(state, record.Revision, kind, out var payload, MountedOptionCount(part))) return;
            try { channel.Request(b.Id, payload); }
            catch { b.Edits.RequestFailed(); throw; }
        }
        internal void Update()
        {
            if (disposed) return;
            RetryPurchasedCatalogs();
            if (host || !Ready) return;
            UpdatePartChanges();
            foreach (var b in bindings.Values) if (b.Edits?.PendingCount > 0) SendMountedEdit(b);
        }
        private void Reply(ulong id, byte[] payload)
        {
            // ReplyReceived is shared by every adapter. A store receipt starts with a random
            // operation Guid, which can begin with the part marker; it is not a socket reply.
            if (host || !bindings.TryGetValue(id, out var b)) return;
            if (VehiclePartChangeReply.Is(payload)) { PartChangeReply(id, VehiclePartChangeReply.Decode(payload)); return; }
            if (b.Edits?.Reply(payload) == true)
            {
                bool rejected = b.Edits.RejectedByHost;
                SendMountedEdit(b);
                if (rejected && channel!.Records.TryGetValue(id, out var record)) Changed(record);
            }
        }
        private void Rejected(uint peer, ulong id, byte[] payload)
        {
            if (host && VehiclePartChangeCommand.Is(payload) && bindings.TryGetValue(id, out var binding))
            { PartChange(peer, binding, VehiclePartChangeCommand.Decode(payload), true); return; }
            if (host && bindings.ContainsKey(id) && MountedPartEditQueue.DecodeRequest(payload, out uint nonce, out _))
                channel!.Reply(peer, id, MountedPartEditQueue.EncodeReply(nonce, MountedPartEditQueue.Stale, channel.Records[id].Revision));
        }
        internal void NotifyRemoved(Vehicle_PartSocket socket, InteractableParts? part)
        {
            if (applying || !Ready || !bySocket.ContainsKey(socket)) return;
            var binding = bySocket[socket];
            if (!host && binding.State.Installed && !socket.isInstalled)
            { BeginPartChange(binding, VehiclePartState.Empty, part); return; }
            NotifyChanged(socket);
            if (part != null) MonMPPlugin.Instance?.Items?.NativePartRemoved(part);
        }
        internal void NotifyDetached(Vehicle_PartSocket socket, InteractableParts? part)
        {
            if (!bySocket.ContainsKey(socket)) return;
            if (applying)
            {
                if (host && executingPartPeer.HasValue && part != null && bySocket.TryGetValue(socket, out var child) && child.State.Installed)
                {
                    ulong output = MonMPPlugin.Instance!.Items!.HostRegisterNativeOutput(part, executingPartPeer.Value, false);
                    detachedPartOutputs[(executingPartPeer.Value, executingPartOperation, child.Id)] = (child.State, output);
                    return;
                }
                // A native parent replacement can drop its children. The actor's copies are
                // shared loose items; this replay's unregistered copies must not remain too.
                if (part != null) { part.gameObject.SetActive(false); UnityEngine.Object.Destroy(part.gameObject); }
                return;
            }
            NotifyRemoved(socket, part);
        }
        internal void NotifyNativeValuesChanged(Part_SteeringRack part)
        {
            if (applying || part == null) return;
            var binding = bindings.Values.FirstOrDefault(b => b.Socket.partInstalled == part);
            if (binding != null && IsSteeringRackBinding(binding)) NotifyChanged(binding.Socket);
        }
        internal void NotifyMountedValuesChanged(InteractableParts part)
        {
            if (applying || part == null || MountedValuesKind(part) == MountedVehiclePartKind.None) return;
            var binding = bindings.Values.FirstOrDefault(b => b.Socket.isInstalled && b.Socket.partInstalled == part);
            if (binding != null) NotifyChanged(binding.Socket);
        }
        [MonMP.Core.World.WorldCommandAdapters(VehiclePartState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || !bindings.TryGetValue(id, out var b)) return;
            if (VehiclePartChangeCommand.Is(payload))
            { PartChange(peer, b, VehiclePartChangeCommand.Decode(payload), false); return; }
            bool edit = MountedPartEditQueue.DecodeRequest(payload, out uint nonce, out var claimed);
            if (!edit) claimed = VehiclePartState.Decode(payload);
            var current = b.State;
            if (current.Installed != claimed.Installed) return; // structural changes require their item/output operation
            var mountedKind = MountedValuesKind(claimed.Installed ? claimed : current);
            var optionState = claimed.Installed ? claimed : current;
            int optionCount = optionState.Installed && assetByPrefab.TryGetValue(optionState.Prefab, out var mountedAsset)
                ? MountedOptionCount(mountedAsset.GetComponent<InteractableParts>()) : 0;
            if (!TryResolvePart(b, claimed, out var asset, out var steeringRack) ||
                !VehiclePartCommandPolicy.IsValidTransition(current, claimed, steeringRack, IsWheelBinding(b), b.Socket.boltHolesDetails.Length == 0, mountedKind, optionCount) ||
                edit && !MountedVehiclePartValues.IsOneNativeStep(current, claimed, mountedKind, optionCount))
            {
                if (edit) channel!.Reply(peer, id, MountedPartEditQueue.EncodeReply(nonce, MountedPartEditQueue.Rejected, channel.Records[id].Revision));
                return;
            }
            var nativeBefore = Capture(b);
            if (steeringRack && nativeBefore.Equals(current) &&
                VehiclePartCommandPolicy.TryGetSteeringRackStep(current, claimed, out int control, out int direction))
                ApplySteeringRackControl(b.Socket.partInstalled as Part_SteeringRack, control, direction);
            else ApplyToSocket(b, claimed, asset, steeringRack);
            var accepted = Capture(b);
            if (!accepted.Equals(claimed)) throw new InvalidOperationException(
                "Host vehicle part did not accept the validated native state. socket=" + id.ToString("X16") +
                " expected=" + Describe(claimed) + " actual=" + Describe(accepted) +
                " before=" + Describe(nativeBefore));
            b.State = accepted;
            channel!.Publish(id, accepted.Encode());
            // Applying a native parent command suppresses its nested Detach notifications.
            // Publish the resulting child states as well as the requested parent state.
            foreach (var child in bindings.Values)
            {
                var childState = Capture(child);
                if (childState.Equals(child.State)) continue;
                child.State = childState;
                channel.Publish(child.Id, childState.Encode());
            }
            if (edit) channel.Reply(peer, id, MountedPartEditQueue.EncodeReply(nonce, MountedPartEditQueue.Accepted, channel.Records[id].Revision));
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            var staged = new List<(Binding Binding, VehiclePartState State, GameObject? Asset, bool SteeringRack)>();
            foreach (var record in records.Values.Where(r => r.Adapter == VehiclePartState.Adapter))
            {
                if (!bindings.TryGetValue(record.Id, out var binding))
                {
                    if (record.Id > WorldRecord.DynamicIdStart) continue;
                    throw new InvalidDataException("Missing static vehicle part binding.");
                }
                var state = VehiclePartState.Decode(record.Payload);
                if (!TryResolvePart(binding, state, out var asset, out var steeringRack)) throw new InvalidDataException("Invalid native vehicle part state.");
                staged.Add((binding, state, asset, steeringRack));
            }
            // An entity-scoped repair can replace a parent without carrying its children.
            // Use only already accepted host records for the children native RemovePart drops.
            // Received staged records take precedence, so full snapshots need no extra replay.
            var stagedIds = new HashSet<ulong>(staged.Select(v => v.Binding.Id));
            for (int i = 0; i < staged.Count; i++)
            {
                var value = staged[i];
                var current = Capture(value.Binding);
                if (!current.Installed || value.State.Installed && current.Prefab == value.State.Prefab) continue;
                foreach (var childSocket in DetachedChildren(value.Binding.Socket))
                {
                    if (childSocket == null || !bySocket.TryGetValue(childSocket, out var child) || stagedIds.Contains(child.Id) ||
                        channel == null || !channel.Records.TryGetValue(child.Id, out var record)) continue;
                    var childState = VehiclePartState.Decode(record.Payload);
                    if (!TryResolvePart(child, childState, out var childAsset, out var childRack))
                        throw new InvalidDataException("Invalid accepted dependent vehicle part state.");
                    stagedIds.Add(child.Id);
                    staged.Add((child, childState, childAsset, childRack));
                }
            }
            // Removing/replacing these native parents detaches their dependent sockets. Restore
            // parents first so a later engine/hub swap cannot erase a restored child record.
            foreach (var value in staged.OrderBy(v => SnapshotOrder(v.Binding.Socket.car_PartsType)))
            {
                if (value.Binding.PurchasedVehicleEntity != 0 && !IsPurchasedNativeReady(value.Binding))
                {
                    QueuePurchasedApply(value.Binding, value.State);
                    continue;
                }
                CapturePurchasedBaseline(value.Binding);
                Apply(value.Binding, value.State, value.Asset, value.SteeringRack);
            }
            foreach (var value in staged)
                MonMPPlugin.Instance?.World?.Bolts.RestoreMissingSocketRecords(value.Binding.Socket, records);
        }
        private static IEnumerable<Vehicle_PartSocket> DetachedChildren(Vehicle_PartSocket socket)
        {
            var manager = socket.vehicleAttached;
            if (manager == null) yield break;
            if (socket.car_PartsType == Car_PartsType.Engine && manager.Get_Vehicle.vehicle_type != Vehicle_Type.OlTruck)
            {
                yield return manager.Intake; yield return manager.ExhaustManifold; yield return manager.Transmission;
            }
            else if (socket.car_PartsType == Car_PartsType.Hub && manager.Get_Vehicle.vehicle_type != Vehicle_Type.OlTruck)
            {
                yield return socket.car_Side == Car_Side.FL ? manager.FrontRotorLeft : socket.car_Side == Car_Side.FR ? manager.FrontRotorRight :
                    socket.car_Side == Car_Side.RL ? manager.RearRotorLeft : manager.RearRotorRight;
                yield return socket.car_Side == Car_Side.FL ? manager.WheelSpacer_FL : socket.car_Side == Car_Side.FR ? manager.WheelSpacer_FR :
                    socket.car_Side == Car_Side.RL ? manager.WheelSpacer_RL : manager.WheelSpacer_RR;
            }
            else if (socket.car_PartsType == Car_PartsType.Rotor)
                yield return socket.car_Side == Car_Side.FL ? manager.WheelSpacer_FL : socket.car_Side == Car_Side.FR ? manager.WheelSpacer_FR :
                    socket.car_Side == Car_Side.RL ? manager.WheelSpacer_RL : manager.WheelSpacer_RR;
            else if (socket.car_PartsType == Car_PartsType.Door_Trunk && manager.Get_Vehicle.vehicle_type != Vehicle_Type.OlTruck)
                yield return manager.Spoiler;
            else if (socket.car_PartsType == Car_PartsType.NitrousMount) yield return manager.NitrousBottle;
        }
        private static int SnapshotOrder(Car_PartsType type) => type == Car_PartsType.Engine ||
            type == Car_PartsType.Hub || type == Car_PartsType.Door_Trunk || type == Car_PartsType.NitrousMount ? 0 :
            type == Car_PartsType.Rotor ? 1 : 2;

        private VehiclePartState PreservePickupHistory(VehiclePartState original, VehiclePartState current)
        {
            if (!original.Installed || !current.Installed || original.Prefab != current.Prefab) return original;
            var kind = MountedValuesKind(original);
            if (kind != MountedVehiclePartKind.StakePickupHistory && kind != MountedVehiclePartKind.SpoilerPickupHistory ||
                original.IntValue2 == 1 || current.IntValue2 != 1) return original;
            return new VehiclePartState(true, original.Prefab, original.FullyTightened,
                original.Value1, original.Value2, original.IntValue1, 1);
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != VehiclePartState.Adapter) return;
            if (!bindings.TryGetValue(record.Id, out var b))
            {
                if (record.Id > WorldRecord.DynamicIdStart) return;
                throw new InvalidDataException("Missing static vehicle part binding.");
            }
            var state = VehiclePartState.Decode(record.Payload);
            if (!TryResolvePart(b, state, out var asset, out var steeringRack)) throw new InvalidDataException("Invalid native vehicle part state.");
            if (b.PurchasedVehicleEntity != 0 && !IsPurchasedNativeReady(b))
            {
                QueuePurchasedApply(b, state);
                return;
            }
            CapturePurchasedBaseline(b);
            Apply(b, state, asset, steeringRack);
        }
        private void Apply(Binding b, VehiclePartState state, GameObject? asset, bool steeringRack)
        {
            CapturePurchasedBaseline(b);
            if (HoldsBackApply(b.Id)) return; // preserve the actor's input/output until its definitive reply
            var current = Capture(b);
            if (current.Equals(state)) { b.State = state; SendMountedEdit(b); RestoreSocketBolts(b); return; } // the local actor already applied this natively
            if (steeringRack && VehiclePartCommandPolicy.TryGetSteeringRackStep(current, state, out int control, out int direction))
                ApplySteeringRackControl(b.Socket.partInstalled as Part_SteeringRack, control, direction);
            else ApplyToSocket(b, state, asset, steeringRack);
            var accepted = Capture(b);
            // A host part that is not fully bolted (a save mid-repair, or someone mid-wrench) leaves the
            // exact per-hole state to SharedVehicleBolts, which may deliver it before or after this.
            if (!accepted.Equals(state) && !(state.Installed && !state.FullyTightened && accepted.FullyTightened &&
                accepted.Equals(new VehiclePartState(true, state.Prefab, true, state.Value1, state.Value2, state.IntValue1, state.IntValue2)))) throw new InvalidOperationException(
                "Guest vehicle part did not accept the host's native state. socket=" + b.Id.ToString("X16") +
                " expected=" + Describe(state) + " actual=" + Describe(accepted) + " before=" + Describe(current));
            b.State = accepted;
            SendMountedEdit(b);
            RestoreSocketBolts(b);
        }

        private void RestoreSocketBolts(Binding b)
        {
            // InstallPart resets hole flags without removing old visuals. Reapply accepted
            // per-hole records after part validation so either delivery order converges.
            // Native fully-tight callbacks during this replay must not become guest edits.
            bool wasApplying = applying; applying = true;
            try { MonMPPlugin.Instance?.World?.Bolts.RestoreMissingSocketRecords(b.Socket); }
            finally { applying = wasApplying; }
        }

        private void ApplySteeringRackControl(Part_SteeringRack? rack, int control, int direction)
        {
            if (rack == null) throw new InvalidDataException("Validated steering-rack socket has no installed native rack.");
            // These source-verified native knob methods update the backing tuning fields and
            // wheel setup. In particular, Value1/Value2 setters silently no-op if their knob
            // references are null, so one-step commands must use the native operation itself.
            bool wasApplying = applying;
            applying = true;
            try
            {
                switch (control)
                {
                    case 0: rack.KnobToeAngle_FL(direction); break;
                    case 1: rack.KnobToeAngle_FR(direction); break;
                    case 2: rack.KnobAdjustMaxSteerAngle(direction); break;
                    case 3: rack.KnobAdjustSteerSpeed(direction); break;
                    default: throw new InvalidDataException("Invalid native steering-rack control.");
                }
            }
            finally { applying = wasApplying; }
        }

        private static string Describe(VehiclePartState state) => state.Installed
            ? "installed,prefab=" + state.Prefab + ",tightened=" + state.FullyTightened +
                ",Value1=" + state.Value1 + ",Value2=" + state.Value2 +
                ",IntValue1=" + state.IntValue1 + ",IntValue2=" + state.IntValue2
            : "empty";

        private bool RestoreSteeringRackValues(Binding binding, VehiclePartState target)
        {
            var current = Capture(binding);
            if (!IsSteeringRackBinding(binding) || !current.HasValidSteeringRackValues || !target.HasValidSteeringRackValues ||
                current.Prefab != target.Prefab || current.FullyTightened != target.FullyTightened ||
                !(binding.Socket.partInstalled is Part_SteeringRack rack)) return false;

            int steps = 0;
            while (!current.Equals(target))
            {
                int control, direction;
                VehiclePartState next;
                if (current.IntValue1 != target.IntValue1)
                {
                    control = 0; direction = Math.Sign(target.IntValue1 - current.IntValue1);
                    next = new VehiclePartState(true, current.Prefab, current.FullyTightened,
                        current.Value1, current.Value2, current.IntValue1 + direction, current.IntValue2);
                }
                else if (current.IntValue2 != target.IntValue2)
                {
                    control = 1; direction = Math.Sign(target.IntValue2 - current.IntValue2);
                    next = new VehiclePartState(true, current.Prefab, current.FullyTightened,
                        current.Value1, current.Value2, current.IntValue1, current.IntValue2 + direction);
                }
                else if (current.Value1 != target.Value1)
                {
                    control = 2; direction = Math.Sign(target.Value1 - current.Value1);
                    next = new VehiclePartState(true, current.Prefab, current.FullyTightened,
                        current.Value1 + direction, current.Value2, current.IntValue1, current.IntValue2);
                }
                else
                {
                    control = 3; direction = Math.Sign(target.Value2 - current.Value2);
                    next = new VehiclePartState(true, current.Prefab, current.FullyTightened,
                        current.Value1, current.Value2 + direction, current.IntValue1, current.IntValue2);
                }
                if (!VehiclePartCommandPolicy.TryGetSteeringRackStep(current, next, out int checkedControl, out int checkedDirection) ||
                    checkedControl != control || checkedDirection != direction)
                    throw new InvalidOperationException("Cannot restore the guest's native rack through validated knob steps.");
                ApplySteeringRackControl(rack, control, direction);
                var accepted = Capture(binding);
                if (!accepted.Equals(next))
                    throw new InvalidOperationException("Guest rack rollback step failed. expected=" + Describe(next) + " actual=" + Describe(accepted));
                current = accepted;
                if (++steps > 300) throw new InvalidOperationException("Guest rack rollback exceeded the native control bounds.");
            }
            return true;
        }

        private void ApplyToSocket(Binding binding, VehiclePartState state, GameObject? asset, bool steeringRack)
        {
            var socket = binding.Socket;
            // A different part in the same socket (a custom rim over the stock wheel, say): take ours off first.
            bool swap = state.Installed && socket.isInstalled && Capture(binding).Prefab != state.Prefab;
            bool wasApplying = applying;
            applying = true;
            try
            {
                if (swap)
                {
                    var replaced = socket.partInstalled;
                    try { socket.RemovePart(); }
                    finally { if (replaced != null && (!socket.isInstalled || socket.partInstalled != replaced)) RetireNativePart(replaced); }
                }
                if (state.Installed && !socket.isInstalled)
                {
                    if (asset == null) throw new InvalidDataException("Validated local vehicle part asset is unavailable.");
                    var clone = UnityEngine.Object.Instantiate(asset, socket.gameobject!.transform.position, socket.gameobject.transform.rotation);
                    var part = clone.GetComponent<InteractableParts>();
                    if (part == null || part.car_PartsType != socket.car_PartsType) throw new InvalidDataException("Catalog part does not match its native socket.");
                    part.Get_isJustSpawned = true;
                    if (part is Part_Driveline_Wheel wheel) wheel.IntValue1 = state.IntValue1;
                    // Native installation refreshes wheel setup and rear-seat placement before
                    // deferred Start. Seed tuning first so those callbacks see the host's values.
                    ApplyMountedValues(part, state, null);
                    socket.InstallPart(part);
                    if (socket.car_PartsType == Car_PartsType.Jackstand)
                        socket.vehicleAttached.StartCoroutine(socket.ExtendCollider());
                }
                else if (!state.Installed && socket.isInstalled)
                {
                    var old = socket.partInstalled;
                    try { socket.RemovePart(); }
                    // Native callbacks can throw after committing; retire only a source that
                    // actually left this socket, including on that exception path.
                    finally { if (old != null && (!socket.isInstalled || socket.partInstalled != old)) RetireNativePart(old); }
                }
                if (state.Installed && IsWheelBinding(binding) && socket.partInstalled is Part_Driveline_Wheel mounted && mounted.IntValue1 != state.IntValue1)
                    mounted.IntValue1 = state.IntValue1; // a joining guest's own save may have another tire on it
                if (state.Installed && state.FullyTightened && !socket.CheckIfAllBoltAre100Percent())
                    foreach (var hole in socket.boltHolesDetails)
                        SharedVehicleBolts.RestoreNativeHole(hole, new BoltHoleState(true, 100));
                if (state.Installed && steeringRack)
                {
                    if (!(socket.partInstalled is Part_SteeringRack rack) || !state.HasValidSteeringRackValues)
                        throw new InvalidDataException("Steering-rack values do not match the validated native socket.");
                    rack.Value1 = state.Value1; rack.Value2 = state.Value2;
                    rack.IntValue1 = state.IntValue1; rack.IntValue2 = state.IntValue2;
                }
                if (state.Installed) ApplyMountedValues(socket.partInstalled, state, socket.vehicleAttached);
            }
            finally { applying = wasApplying; }
        }

        private static void ApplyMountedValues(InteractableParts? part, VehiclePartState state, Vehicle_PartsManager? manager)
        {
            var kind = MountedValuesKind(part);
            if (kind == MountedVehiclePartKind.None) return;
            if (part == null || !IsValidMountedState(part, state))
                throw new InvalidDataException("Mounted values do not match the validated native part subtype.");
            // These native setters refresh their knobs without replaying local interaction audio
            // or player-reset side effects. The rear-seat setter only stores isFolded, so refresh
            // its pose separately; CanSleep then follows the same native field.
            switch (kind)
            {
                case MountedVehiclePartKind.Coilover:
                    if (part.Value1 != state.Value1) part.Value1 = state.Value1;
                    if (part.Value2 != state.Value2) part.Value2 = state.Value2;
                    if (part.IntValue1 != state.IntValue1) part.IntValue1 = state.IntValue1;
                    if (part.IntValue2 != state.IntValue2) part.IntValue2 = state.IntValue2;
                    break;
                case MountedVehiclePartKind.TunedControlArm:
                    if (part.Value1 != state.Value1) part.Value1 = state.Value1;
                    break;
                case MountedVehiclePartKind.RearSeat:
                    if (part.Value1 != state.Value1) part.Value1 = state.Value1;
                    RearSeatVisual.Invoke(part, null);
                    break;
                case MountedVehiclePartKind.RpmGaugeShiftThreshold:
                    if (part.IntValue1 != state.IntValue1) part.IntValue1 = state.IntValue1;
                    RpmGaugeKnobText.Invoke(part, null);
                    break;
                case MountedVehiclePartKind.SpeedGaugeUnits:
                case MountedVehiclePartKind.TurboWastegateSound:
                    if (part.IntValue1 != state.IntValue1) part.IntValue1 = state.IntValue1;
                    break;
                case MountedVehiclePartKind.StakePickupHistory:
                case MountedVehiclePartKind.SpoilerPickupHistory:
                    if (part.IntValue2 != state.IntValue2) part.IntValue2 = state.IntValue2;
                    break;
            }
            if ((kind == MountedVehiclePartKind.Coilover || kind == MountedVehiclePartKind.TunedControlArm) && part.isPlaced && manager != null)
                manager.Update_Wheels();
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) session.PeerLeft -= PartPeerLeft;
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command;
                channel.CommandRejected -= Rejected; channel.ReplyReceived -= Reply; }
            foreach (var b in bindings.Values) b.Edits?.Clear();
            DisposePartChanges();
            deferredPurchasedApply.Clear(); purchasedVehicles.Clear();
            if (!host)
                foreach (var b in bindings.Values.OrderBy(b => SnapshotOrder(b.Socket.car_PartsType)))
                    RestoreGuestBaseline(b);
            session = null; channel = null;
        }

        private void RestoreGuestBaseline(Binding b)
        {
            if (!b.OriginalCaptured) return;
            var baseline = PreservePickupHistory(b.Original, Capture(b));
            if (b.Socket?.gameobject != null && !Capture(b).Equals(baseline) &&
                TryResolvePart(b, baseline, out var asset, out var steeringRack) &&
                !RestoreSteeringRackValues(b, baseline)) ApplyToSocket(b, baseline, asset, steeringRack);
        }

        // Finds any currently-empty socket whose part type has a catalogued prefab, deterministically
        // (by socket id) so host and guest independently agree on the same one.

#if MONMP_DEVELOPMENT
        internal (ulong Socket, ushort Prefab) TestAnyEmptySocketWithPrefab(Vehicle_Type vehicle = Vehicle_Type.MainVehicle, bool boltedBodyPart = false)
        {
            var candidates = bindings.Values.Where(b => b.Type == vehicle && !b.Socket.isInstalled)
                .Where(b => !boltedBodyPart || (!IsWheelBinding(b) && b.Socket.boltHolesDetails != null && b.Socket.boltHolesDetails.Length > 0 &&
                    b.Socket.car_PartsType != Car_PartsType.Jackstand && b.Socket.CustomObjectToEnableWhenPartIsInstalled == null))
                .Select(b => (Binding: b, Prefab: assetByPrefab.Where(p => p.Value.GetComponent<InteractableParts>()?.car_PartsType == b.Socket.car_PartsType)
                    .OrderBy(p => p.Key).Select(p => (ushort?)p.Key).FirstOrDefault()))
                .Where(p => p.Prefab.HasValue).OrderBy(p => p.Binding.Id).ToArray();
            if (candidates.Length == 0) throw new InvalidOperationException("No empty " + vehicle + " socket has a catalogued prefab among " + bindings.Values.Count(b => b.Type == vehicle) + " sockets.");
            return (candidates[0].Binding.Id, candidates[0].Prefab!.Value);
        }
#endif

        // Ensures a specific socket type (e.g. Door_Hood) is installed, for tests whose target native
        // behavior itself gates on installation (the hood/trunk release methods no-op when uninstalled).

#if MONMP_DEVELOPMENT
        internal (ulong Socket, bool AlreadyInstalled) TestEnsureInstalled(Car_PartsType type, Vehicle_Type vehicle = Vehicle_Type.MainVehicle)
        {
            ulong id = TestSocketOfType(type, vehicle);
            var b = bindings[id];
            if (b.Socket.isInstalled) return (id, true);
            var prefab = assetByPrefab.Where(p => p.Value.GetComponent<InteractableParts>()?.car_PartsType == type).OrderBy(p => p.Key).Select(p => (ushort?)p.Key).FirstOrDefault()
                ?? throw new InvalidOperationException("No catalogued prefab for " + type + ".");
            TestInstall(id, prefab);
            return (id, false);
        }


        internal ulong TestSocketOfType(Car_PartsType type, Vehicle_Type vehicle = Vehicle_Type.MainVehicle) => bindings.Values.Where(b => b.Type == vehicle && b.Socket.car_PartsType == type).OrderBy(b => b.Id).First().Id;


        internal int TestCount(Vehicle_Type vehicle) => bindings.Values.Count(b => b.Type == vehicle);


        internal VehiclePartState TestState(ulong id) => bindings[id].State;


        internal VehiclePartState TestOriginal(ulong id) => bindings[id].Original;


        internal VehiclePartState TestNative(ulong id) => Capture(bindings[id]);


        internal ushort TestSeatPrefab(ulong id)
        {
            var binding = bindings[id];
            if (binding.Socket.car_PartsType != Car_PartsType.Seat)
                throw new InvalidOperationException("Shell seat prefab lookup requires a native Seat socket.");
            var match = assetByPrefab.OrderBy(pair => pair.Key).FirstOrDefault(pair =>
            {
                var candidate = pair.Value.GetComponent<InteractableParts>();
                return candidate is Part_Seat && SharedVehiclePartIdentityPolicy.IsCompatiblePart(
                    binding.Type, binding.Socket.car_PartsType, candidate);
            });
            if (match.Value == null) throw new InvalidDataException("No registered compatible Part_Seat prefab for " + binding.Type + ".");
            return match.Key;
        }


        internal string TestPrefabName(ushort prefab) => assetByPrefab.TryGetValue(prefab, out var asset)
            ? asset.GetComponent<InteractableParts>().PrefabName
            : throw new InvalidDataException("Vehicle part prefab is not registered: " + prefab + ".");


        internal bool TestRecordAvailable(ulong id) => channel != null && channel.Records.ContainsKey(id);


        internal VehiclePartState TestRecord(ulong id) => VehiclePartState.Decode(channel!.Records[id].Payload);


        internal void TestInstall(ulong id, ushort prefab)
        {
            var socket = bindings[id].Socket;
            var clone = UnityEngine.Object.Instantiate(assetByPrefab[prefab], socket.gameobject!.transform.position, socket.gameobject.transform.rotation);
            var part = clone.GetComponent<InteractableParts>(); part.Get_isJustSpawned = true;
            socket.InstallPart(part);
        }


        internal void TestRemove(ulong id) => bindings[id].Socket.RemovePart();


        internal string TestSocketType(ulong id) => bindings[id].Socket.car_PartsType + " " + bindings[id].Socket.gameobject?.name;


        internal Vector3 TestSocketPosition(ulong id) => bindings[id].Socket.gameobject!.transform.position;


        internal InteractableParts TestRemoveIntoHand(ulong id)
        {
            var socket = bindings[id].Socket;
            var part = socket.partInstalled;
            var game = MonMPPlugin.Instance!.Gameplay!;
            // The exact completed RemovePart_Timer sequence, including its native hand grab.
            HarmonyLib.AccessTools.Field(typeof(PlayerInteractable), "currentInteractableItem").SetValue(game.Player, part);
            socket.RemovePart();
            HarmonyLib.AccessTools.Method(typeof(PlayerInteractable), "GrabInteractable").Invoke(game.Player, null);
            return part;
        }
#endif

        // The native aim at a socket (PlayerInteractable's part snap): the held part moves onto the socket's ghost.

#if MONMP_DEVELOPMENT
        internal void TestAimHeld(ulong id)
        {
            var socket = bindings[id].Socket;
            var game = MonMPPlugin.Instance!.Gameplay!;
            var part = game.Player.Get_HoldingItem as InteractableParts ?? throw new InvalidOperationException("No held part to aim.");
            var ghost = Resources.FindObjectsOfTypeAll<Generated_Part>().FirstOrDefault(g => g != null && g.carPartSocket == socket)
                ?? throw new InvalidOperationException("The socket has no native part ghost.");
            HarmonyLib.AccessTools.Field(typeof(PlayerInteractable), "currentGeneratedPart").SetValue(game.Player, ghost);
            part.TempPlaced = true;
            part.transform.SetParent(ghost.transform);
            part.transform.localPosition = Vector3.zero;
            part.transform.localRotation = Quaternion.identity;
            part.transform.localScale = ghost.transform.parent.localScale;
        }
#endif

        // The exact PlayerInteractable install: the held part goes into the socket and the hand is emptied.

#if MONMP_DEVELOPMENT
        internal InteractableParts TestInstallHeld(ulong id)
        {
            var socket = bindings[id].Socket;
            var game = MonMPPlugin.Instance!.Gameplay!;
            var part = game.Player.Get_HoldingItem as InteractableParts ?? throw new InvalidOperationException("No held part to install.");
            socket.InstallPart(part);
            HarmonyLib.AccessTools.Field(typeof(PlayerInteractable), "currentGeneratedPart").SetValue(game.Player, null);
            HarmonyLib.AccessTools.Field(typeof(PlayerInteractable), "HoldingItem").SetValue(game.Player, null);
            return part;
        }


        internal InteractableParts? TestInstalledPart(ulong id) => bindings[id].Socket.partInstalled;


        internal bool TestReplaceWithNativeAlternate(ulong id)
        {
            var binding = bindings[id]; var socket = binding.Socket;
            var original = socket.partInstalled;
            if (!socket.isInstalled || original == null || IsSteeringRackBinding(binding)) return false;
            var alternate = assetByPrefab.OrderBy(p => p.Key).Select(p => p.Value).FirstOrDefault(asset =>
            {
                var candidate = asset.GetComponent<InteractableParts>();
                return candidate != null && SharedVehiclePartIdentityPolicy.IsCompatiblePart(binding.Type, socket.car_PartsType, candidate) &&
                    candidate.PrefabName != original.PrefabName;
            });
            if (alternate == null) return false;
            var holeStates = socket.boltHolesDetails.Select(h => (Hole: h, State: new BoltHoleState(h.isBoltInstalled,
                h.isBoltInstalled && h.BoltedPercent > 0 ? (byte)Mathf.Max(1, Mathf.RoundToInt(Mathf.Clamp01(h.BoltedPercent) * 100)) : (byte)0))).ToArray();
            int tire = original is Part_Driveline_Wheel originalWheel ? originalWheel.IntValue1 : 0;
            bool previous = applying; applying = true;
            try
            {
                // Fixture-only divergence: real native resources and operations, with command
                // publication suppressed so a host repair/disconnect must reconcile the mismatch.
                foreach (var hole in socket.boltHolesDetails) hole.DetachAndDestroy();
                socket.RemovePart(); original.gameObject.SetActive(false); UnityEngine.Object.Destroy(original.gameObject);
                var clone = UnityEngine.Object.Instantiate(alternate).GetComponent<InteractableParts>();
                clone.Get_isJustSpawned = true;
                if (clone is Part_Driveline_Wheel wheel) wheel.IntValue1 = tire;
                socket.InstallPart(clone);
                foreach (var hole in holeStates) SharedVehicleBolts.RestoreNativeHole(hole.Hole, hole.State);
            }
            finally { applying = previous; }
            return true;
        }


        internal bool TestInstalledBoltObjects(ulong id) => bindings[id].Socket.boltHolesDetails.All(hole =>
            !hole.isBoltInstalled || hole.generated_BoltHole?.hardware_Bolt != null &&
            hole.generated_BoltHole.boltGameObject != null &&
            hole.generated_BoltHole.hardware_Bolt.transform.parent == hole.HoleLocation);


        internal static int TestLooseParts(string prefabName) =>
            UnityEngine.Object.FindObjectsOfType<InteractableParts>().Count(part => part.PrefabName == prefabName && !part.isPlaced);


        internal void TestTighten(ulong id)
        {
            var socket = bindings[id].Socket;
            bool previous = applying; applying = true;
            try
            {
                foreach (var hole in socket.boltHolesDetails)
                    SharedVehicleBolts.RestoreNativeHole(hole, new BoltHoleState(true, 100));
            }
            finally { applying = previous; }
            socket.vehicleAttached.VehiclePart_FullyTighten(socket);
        }
#endif

        // Paired shell-seat lifecycle fixture: force a fresh native Part_Seat through the real
        // socket RemovePart/InstallPart callbacks, clearing old native bolts first. The removed
        // replica is hidden before the item scanner can mistake it for a loose player item.

#if MONMP_DEVELOPMENT
        internal void TestFreshSeat(ulong id, ushort prefab)
        {
            TestRemoveSeatForFresh(id);
            TestInstallFreshSeat(id, prefab);
        }


        internal void TestRemoveSeatForFresh(ulong id)
        {
            var socket = bindings[id].Socket;
            if (socket.car_PartsType != Car_PartsType.Seat)
                throw new InvalidOperationException("Shell seat fixture needs a native Seat socket.");
            foreach (var hole in socket.boltHolesDetails)
                if (hole.isBoltInstalled) hole.DetachAndDestroy();
            if (socket.isInstalled)
            {
                var old = socket.partInstalled;
                socket.RemovePart();
                if (old != null) { old.gameObject.SetActive(false); UnityEngine.Object.Destroy(old.gameObject); }
            }
        }


        internal void TestInstallFreshSeat(ulong id, ushort prefab)
        {
            var socket = bindings[id].Socket;
            if (socket.car_PartsType != Car_PartsType.Seat || !assetByPrefab.TryGetValue(prefab, out var asset) ||
                asset.GetComponent<InteractableParts>() is not Part_Seat)
                throw new InvalidOperationException("Shell seat fixture needs a native Seat socket and Part_Seat catalog asset.");
            var clone = UnityEngine.Object.Instantiate(asset, socket.gameobject!.transform.position,
                socket.gameobject.transform.rotation);
            if (clone.GetComponent<InteractableParts>() is not Part_Seat part)
                throw new InvalidDataException("Validated shell seat asset lost its Part_Seat component.");
            part.Get_isJustSpawned = true;
            socket.InstallPart(part);
        }
#endif

        // The paired fixture restores both the part and each original native hole before ending
        // its isolated session. Normal tested installs still travel through the public callbacks.

#if MONMP_DEVELOPMENT
        internal void TestRestoreState(ulong id, VehiclePartState state, BoltHoleState[] holes)
        {
            if (!host) throw new InvalidOperationException("Only the test host restores shell seat fixtures.");
            var binding = bindings[id]; var socket = binding.Socket;
            if (holes == null || holes.Length != socket.boltHolesDetails.Length)
                throw new InvalidOperationException("Shell seat restoration has the wrong bolt-hole count.");
            GameObject? asset = null; bool steeringRack = false;
            if (!TryResolvePart(binding, state, out asset, out steeringRack))
                throw new InvalidDataException("Shell seat restoration has no validated native asset.");
            bool previous = applying; applying = true;
            try
            {
                ApplyToSocket(binding, state, asset, steeringRack);
                for (int i = 0; i < holes.Length; i++) SharedVehicleBolts.RestoreNativeHole(socket.boltHolesDetails[i], holes[i]);
            }
            finally { applying = previous; }
            var restored = Capture(binding);
            if (!restored.Equals(state)) throw new InvalidOperationException("Shell seat fixture restoration differs from its original state.");
            binding.State = restored;
            channel?.Publish(id, restored.Encode());
        }
#endif

    }
}
