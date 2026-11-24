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
using MonMP.Plugin;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Adapter 20: fixed bolt holes on the shared vehicle fleet. Installed-bolt progress is host
    // simulated from leased-tool hold commands; guest-supplied whole BoltHoleState writes are never
    // accepted. The native host tool remains unchanged. Bolt install/removal and loose bolt handling
    // use separate revision-checked requests tied to the loose-item lease or an untightened hole.
    internal sealed class SharedVehicleBolts : IDisposable
    {
        private const float CommandBudgetPerSecond = 8f;
        private const float CommandBudgetCapacity = 4f;
        private const float HoldTimeout = 0.4f;
        private const float PresenceTimeout = 1f;
        private const float MaxPeerRange = 4f;
        private const float ClientHeartbeat = 0.15f;
        private const float ClientHoldRetrySeconds = 1f;
        private const float ImpactEquipHeartbeat = 0.5f;
        private const float ImpactEquipTimeout = 1.5f;
        private const float ImpactEquipClaimTimeout = 2f;
        private const float ImpactEquipReach = 20f;

        private sealed class Binding
        {
            public ulong Id;
            public BoltHoleDetails Hole = null!;
            public BoltHoleState State, Original;
            public Vehicle_Type Type;
            public ulong PurchasedVehicleEntity;
            public int WaitThroughFrame;
            public bool OriginalCaptured = true;
        }
        private sealed class PurchasedVehicle
        {
            public ulong Entity;
            public Vehicle_Type Type;
            public GameObject Instance = null!;
            public bool Catalogued;
        }
        private sealed class ActiveHold
        {
            public Guid Operation;
            public uint Sequence;
            public ulong Tool;
            public uint Generation;
            public ulong Bolt;
            public BoltToolKind Kind;
            public BoltToolDirection Direction;
            public float LastCommandAt;
            public Vector3 CameraOrigin;
            public float CameraYaw, CameraPitch;
            public InteractableTools NativeTool = null!;
        }
        private sealed class ClientHold
        {
            public Guid Operation;
            public uint Sequence;
            public ulong Tool;
            public uint Generation;
            public ulong Bolt;
            public BoltToolKind Kind;
            public BoltToolDirection Direction;
            public float NextHeartbeat;
            public float LastCanonicalPercent, LastCanonicalProgressAt;
            public InteractableTools NativeTool = null!;
            public bool Sounding;
            public bool Began;
        }
        private sealed class ImpactEquipmentBinding
        {
            public ulong Id;
            public Vector3 OriginalPosition;
            public string Shape = "";
            public SceneImpactGunEquipmentLease Lease = new SceneImpactGunEquipmentLease();
            public float Expires;
        }

        private static readonly FieldInfo SpannerCamera = RequiredField(typeof(Tool_AdjustableSpanner), "mainCamera");
        private static readonly FieldInfo ImpactCamera = RequiredField(typeof(Tool_Air_Impact), "mainCamera");
        private static readonly FieldInfo SpannerHoldingBolt = RequiredField(typeof(Tool_AdjustableSpanner), "CurrentHolding_Bolt");
        private static readonly FieldInfo ImpactHoldingBolt = RequiredField(typeof(Tool_Air_Impact), "CurrentHolding_Bolt");
        private static readonly FieldInfo SpannerLeftDown = RequiredField(typeof(Tool_AdjustableSpanner), "isMouseLeftDown");
        private static readonly FieldInfo SpannerRightDown = RequiredField(typeof(Tool_AdjustableSpanner), "isMouseRightDown");
        private static readonly FieldInfo ImpactLeftDown = RequiredField(typeof(Tool_Air_Impact), "isMouseLeftClickDown");
        private static readonly FieldInfo ImpactRightDown = RequiredField(typeof(Tool_Air_Impact), "isMouseRightClickDown");
        private static readonly FieldInfo ImpactCompressor = RequiredField(typeof(Tool_Air_Impact), "airCompressor");
        private static readonly FieldInfo PlayerToolLocations = RequiredField(typeof(PlayerInteractable), "toolLocations");
        private static readonly MethodInfo PlayerUnEquipTool = AccessTools.Method(typeof(PlayerInteractable), "UnEquipTool", Type.EmptyTypes) ??
            throw new InvalidOperationException("Unsupported native PlayerInteractable.UnEquipTool method.");

        private readonly bool host;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<ulong, PurchasedVehicle> purchasedVehicles = new Dictionary<ulong, PurchasedVehicle>();
        private readonly Dictionary<ulong, (Binding Binding, BoltHoleState State)> deferredPurchasedApply =
            new Dictionary<ulong, (Binding, BoltHoleState)>();
        private readonly NativeEntityRegistry identities;
        private readonly Dictionary<ulong, (Tool_Air_Impact Tool, uint Generation)> impactTools = new Dictionary<ulong, (Tool_Air_Impact, uint)>();
        private readonly Dictionary<Tool_Air_Impact, ulong> impactIds = new Dictionary<Tool_Air_Impact, ulong>();
        private readonly Dictionary<ulong, ImpactEquipmentBinding> impactEquipment = new Dictionary<ulong, ImpactEquipmentBinding>();
        private readonly Dictionary<uint, RemoteSceneAirEquipmentVisual> impactVisuals = new Dictionary<uint, RemoteSceneAirEquipmentVisual>();

#if MONMP_DEVELOPMENT
        internal int TestImpactGuns => impactTools.Count;
#endif

        private readonly Dictionary<BoltHoleDetails, Binding> byHole = new Dictionary<BoltHoleDetails, Binding>();
        private readonly Queue<ulong> nativeRechecks = new Queue<ulong>();
        private readonly HashSet<ulong> nativeRecheckIds = new HashSet<ulong>();
        private float nextNativeRecheck;
        private readonly Dictionary<uint, ActiveHold> activeHolds = new Dictionary<uint, ActiveHold>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, (float Tokens, float Time)> budgets = new Dictionary<uint, (float, float)>();
        private readonly Dictionary<uint, (float Tokens, float Time)> mountBudgets = new Dictionary<uint, (float, float)>();
        private readonly Dictionary<uint, (float Next, int Suppressed)> mountRefusals = new Dictionary<uint, (float, int)>();
        private readonly Dictionary<uint, HashSet<Guid>> operations = new Dictionary<uint, HashSet<Guid>>();
        private WorldChannel? channel;
        private NetworkSession? session;
        private ClientHold? clientHold;
        private sealed class PendingMount
        {
            public BoltMountCommand Command;
            public Hardware_Bolt Bolt = null!;
            public float Until;
        }
        private readonly Dictionary<ulong, PendingMount> pendingMounts = new Dictionary<ulong, PendingMount>();
        private readonly Dictionary<ulong, uint> mountReplyRevisions = new Dictionary<ulong, uint>();
        private readonly Dictionary<ulong, BoltHoleState> mountReplyStates = new Dictionary<ulong, BoltHoleState>();
        private readonly Dictionary<ulong, BoltProgressPresentation> boltPredictions = new Dictionary<ulong, BoltProgressPresentation>();
        private readonly Dictionary<(uint Peer, Guid Operation), (ulong Hole, BoltMountCommand Command, bool Accepted, ulong Output)> mountResults =
            new Dictionary<(uint, Guid), (ulong, BoltMountCommand, bool, ulong)>();
        internal bool HasPendingNativeRemoval => pendingMounts.Values.Any(p => p.Command.Action == BoltMountAction.Remove);
        internal bool IsPendingRemovalOutput(InteractableItems item) => pendingMounts.Values.Any(p => p.Command.Action == BoltMountAction.Remove && ReferenceEquals(p.Bolt, item));
        private bool applying;
        private float nextScan;
        private float nextPurchasedRetry;
        private float nextImpactEquipSend;
        private float localImpactClaimStarted;
        private ulong localImpactRequested;
        private bool impactActionsBlocked;
        private bool disposed;
        public int Count => bindings.Count;
        public bool Ready => channel?.Ready == true;
        internal uint[] Owners => impactEquipment.Values.Select(value => value.Lease.State.Owner)
            .Where(owner => owner != ItemLease.Unowned).Distinct().ToArray();
        internal bool LocalFlushComplete => session != null && pendingMounts.Count == 0 && !Owners.Contains(session.LocalPeerId) &&
            !impactTools.Values.Any(value => value.Tool != null && gameplay?.Player?.Get_ActiveTool == value.Tool);

        // The scene's Gameplay, found once: Singleton<Gameplay>.i creates a blank one when the scene is gone
        // (a guest's per-frame hold polling can run during teardown, #293).
        private readonly Gameplay? gameplay;
        private Gameplay? SceneGameplay => gameplay != null ? gameplay : null;
        private Vector3 impactEquipPosition, playerFromCameraPosition;
        private Quaternion impactEquipRotation, playerFromCameraRotation;
        private bool impactPresentationReady;
        internal IEnumerable<ulong> RequiredCatalogEntityIds => bindings.Values
            .Where(b => b.PurchasedVehicleEntity == 0 && b.Id > WorldRecord.DynamicIdStart).Select(b => b.Id);
        internal bool IsPurchasedRecord(ulong id) => bindings.TryGetValue(id, out var binding) && binding.PurchasedVehicleEntity != 0;

        private readonly AimSegmentTest checkSegment;

        public SharedVehicleBolts(AimSegmentTest checkSegment, bool host, string diagnostics, NativeEntityRegistry identities)
        {
            this.checkSegment = checkSegment;
            this.host = host;
            this.identities = identities ?? throw new ArgumentNullException(nameof(identities));
            gameplay = UnityEngine.Object.FindObjectOfType<Gameplay>();
            if (gameplay?.Player != null && gameplay.Player.mainCamera != null)
            {
                var locations = PlayerToolLocations.GetValue(gameplay.Player) as InteractableToolLocation[];
                var location = locations?.FirstOrDefault(value => value != null && value.toolsType == ToolsType.Air_ImpactGun && value.Location != null);
                if (location != null)
                {
                    impactEquipPosition = location.Location.localPosition;
                    impactEquipRotation = location.Location.localRotation;
                    var camera = gameplay.Player.mainCamera.transform;
                    playerFromCameraPosition = camera.InverseTransformPoint(gameplay.Player.transform.position);
                    playerFromCameraRotation = Quaternion.Inverse(camera.rotation) * gameplay.Player.transform.rotation;
                    impactPresentationReady = true;
                }
            }
            var lines = new List<string>();
            var sales = SharedVehicleSales.Active;
            if (sales != null)
                foreach (var purchased in sales.PurchasedVehicles())
                    RegisterPurchasedVehicle(purchased.Entity, purchased.Type, purchased.Instance);
            int managers = 0;
            foreach (var (type, manager) in SharedVehicleParts.FleetManagers())
            {
                if (SharedVehicleSales.Active?.OwnsSaleClone(manager.transform) == true) continue;
                managers++;
                foreach (var socket in manager.carPartSockets)
                {
                    if (socket?.gameobject == null || socket.boltHolesDetails == null) continue;
                    string socketIdentity = SharedBuildingDoors.StablePath(socket.gameobject.transform);
                    for (int i = 0; i < socket.boltHolesDetails.Length; i++)
                    {
                        var hole = socket.boltHolesDetails[i];
                        if (hole?.HoleLocation == null) continue;
                        string identity = SharedVehiclePartIdentityPolicy.BoltHoleIdentity(type, socketIdentity, i);
                        ulong id = SharedVehiclePartIdentityPolicy.BoltHoleEntityId(type, identity);
                        if (bindings.ContainsKey(id)) { lines.Add("# duplicate bolt identity skipped: " + identity); continue; }
                        var b = new Binding { Id = id, Hole = hole, State = Capture(hole), Type = type }; b.Original = b.State;
                        identities.Bind(id, BoltHoleState.Adapter, hole.HoleLocation.gameObject, identity, true, EntityPersistence.SessionOnly);
                        bindings.Add(id, b); byHole.Add(hole, b);
                        lines.Add(id.ToString("X16") + "\t" + type + "\t" + identity + "\t" + socket.car_PartsType + "\t" + b.State.Installed);
                    }
                }
            }
            // The garage and racetrack impact guns are scene-mounted (IgnoreSaveSystem, never grabbed), so they
            // have no item lease; a guest's every press was swallowed (#197). Each gets a fixed use identity.
            foreach (var impact in UnityEngine.Object.FindObjectsOfType<Tool_Air_Impact>(true).Where(t => t != null && t.gameObject.scene.IsValid()))
            {
                ulong id = StaticId.Hash(SharedBuildingDoors.StablePath(impact.transform) + "/air-impact-use-v1");
                if (bindings.ContainsKey(id) || impactTools.ContainsKey(id)) { lines.Add("# duplicate impact gun identity skipped"); continue; }
                uint generation = unchecked((uint)(id ^ (id >> 32))); if (generation == 0) generation = 1;
                impactTools.Add(id, (impact, generation)); impactIds.Add(impact, id);
                HeldGripOverrides.RegisterSceneEquipment(impact, out _, out string shape);
                impactEquipment.Add(id, new ImpactEquipmentBinding { Id = id, OriginalPosition = impact.transform.position, Shape = shape });
                identities.Bind(id, BoltHoleState.Adapter, impact.gameObject,
                    SharedBuildingDoors.StablePath(impact.transform) + "/air-impact-equipment-v1", true, EntityPersistence.SessionOnly);
            }
            lines.Sort(StringComparer.Ordinal);

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-bolts.tsv"),
                new[] { managers > 0 ? "id\tvehicle\thole-path\tsocket-type\tinstalled" : "# No shared vehicle Vehicle_PartsManager found; bolt holes are not shared this session." }.Concat(lines));
#endif

        }
        private static BoltHoleState Capture(BoltHoleDetails hole)
        {
            // A save can leave a removed bolt's old tightness behind; an empty hole has none.
            // Do not round a still-tightened bolt down to zero: zero permits native removal.
            return new BoltHoleState(hole.isBoltInstalled, hole.isBoltInstalled && hole.BoltedPercent > 0
                ? (byte)Mathf.Max(1, Mathf.RoundToInt(Mathf.Clamp01(hole.BoltedPercent) * 100)) : (byte)0,
                hole.isBoltInstalled ? (byte)Mathf.Clamp(hole.ReturnBoltColor(), 0, BoltHoleState.MaximumColor) : (byte)0);
        }
        public IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, BoltHoleState.Adapter, b.State.Encode()))
            .Concat(impactEquipment.Values.Select(value => new WorldRecord(value.Id, 1, BoltHoleState.Adapter, value.Lease.State.Encode())));

        // Register only sale-backed OlTruck and the source-confirmed ATV DogBed catalog.
        // Each hole follows the sale entity and its containing socket's relative path.
        internal bool RegisterPurchasedVehicle(ulong entity, Vehicle_Type type, GameObject instance)
        {
            if (disposed) return false;
            if (!PurchasedVehiclePartIdentityPolicy.Supports(type)) return false;
            if (entity <= WorldRecord.DynamicIdStart) throw new InvalidOperationException("Purchased vehicle bolt root is not dynamic.");
            if (instance == null || !instance.scene.IsValid()) throw new InvalidOperationException("Purchased vehicle bolt root is outside the active scene.");
            if (purchasedVehicles.TryGetValue(entity, out var existing))
            {
                if (existing.Type != type || existing.Instance != instance)
                    throw new InvalidOperationException("Conflicting purchased vehicle bolt root for entity " + entity.ToString("X16") + ".");
                if (existing.Catalogued) return true;
            }
            else
            {
                if (purchasedVehicles.Values.Any(value => value.Instance == instance && value.Entity != entity))
                    throw new InvalidOperationException("Purchased vehicle already has a different bolt-catalog identity.");
                existing = new PurchasedVehicle { Entity = entity, Type = type, Instance = instance };
                purchasedVehicles.Add(entity, existing);
            }
            if (!TryRegisterPurchasedHoles(existing)) return true;
            existing.Catalogued = true;
            return true;
        }

        private bool TryRegisterPurchasedHoles(PurchasedVehicle purchase)
        {
            var controllers = purchase.Instance.GetComponentsInChildren<VehicleController>(true);
            if (controllers.Length != 1 || controllers[0] == null || controllers[0].vehicle_type != purchase.Type) return false;
            var manager = controllers[0].GetComponent<Vehicle_PartsManager>();
            if (manager == null || manager.carPartSockets == null || manager.carPartSockets.Count == 0) return false;
            foreach (var socket in manager.carPartSockets.Where(value => PurchasedVehiclePartIdentityPolicy.IncludesSocket(purchase.Type, value)))
            {
                if (socket.boltHolesDetails == null) continue;
                if (!PurchasedVehiclePartIdentityPolicy.TryRelativePath(purchase.Instance.transform, socket.gameobject.transform, out string relativePath))
                    throw new InvalidOperationException("Purchased vehicle bolt socket is outside its sale clone.");
                for (int i = 0; i < socket.boltHolesDetails.Length; i++)
                {
                    var hole = socket.boltHolesDetails[i];
                    if (hole?.HoleLocation == null) continue;
                    string identity = PurchasedVehiclePartIdentityPolicy.BoltHoleIdentity(purchase.Entity, purchase.Type, relativePath, i);
                    ulong id = PurchasedVehiclePartIdentityPolicy.BoltHoleEntityId(purchase.Entity, purchase.Type, relativePath, i);
                    if (bindings.TryGetValue(id, out var existingBinding))
                    {
                        if (!ReferenceEquals(existingBinding.Hole, hole) || existingBinding.PurchasedVehicleEntity != purchase.Entity)
                            throw new InvalidOperationException("Purchased vehicle bolt identity collision: " + identity);
                        continue;
                    }
                    if (byHole.TryGetValue(hole, out existingBinding))
                    {
                        if (existingBinding.Id != id || existingBinding.PurchasedVehicleEntity != purchase.Entity)
                            throw new InvalidOperationException("Purchased vehicle bolt already has a different identity.");
                        continue;
                    }
                    var binding = new Binding
                    {
                        Id = id, Hole = hole, Type = purchase.Type, PurchasedVehicleEntity = purchase.Entity,
                        WaitThroughFrame = Time.frameCount, OriginalCaptured = false
                    };
                    binding.State = Capture(hole);
                    binding.Original = binding.State;
                    bindings.Add(id, binding); byHole.Add(hole, binding);
                    identities.Bind(id, BoltHoleState.Adapter, hole.HoleLocation.gameObject, identity, true, EntityPersistence.SessionOnly);
                    if (host && channel != null)
                        channel.Register(new WorldRecord(id, 1, BoltHoleState.Adapter, binding.State.Encode()));
                    else if (!host && channel?.Records.TryGetValue(id, out var record) == true)
                        QueuePurchasedApply(binding, BoltHoleState.Decode(record.Payload));
                }
            }
            return true;
        }

        internal bool IsPurchasedNativeReady(ulong id) => bindings.TryGetValue(id, out var binding) && IsPurchasedNativeReady(binding);

        private static bool IsPurchasedNativeReady(Binding binding)
        {
            return binding.PurchasedVehicleEntity == 0 || Time.frameCount > binding.WaitThroughFrame &&
                binding.Hole != null && binding.Hole.HoleLocation != null && binding.Hole.generated_BoltHole != null &&
                binding.Hole.HoleLocation.gameObject.activeInHierarchy && binding.Hole.SockedBoltedByThisBolt != null &&
                binding.Hole.SockedBoltedByThisBolt.vehicleAttached != null &&
                binding.Hole.SockedBoltedByThisBolt.vehicleAttached.gameObject.activeInHierarchy;
        }

        private void QueuePurchasedApply(Binding binding, BoltHoleState state)
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
                    if (TryRegisterPurchasedHoles(purchase)) purchase.Catalogued = true;
            }
            if (host || channel == null || deferredPurchasedApply.Count == 0) return;
            foreach (var pair in deferredPurchasedApply.Values.ToArray())
            {
                var binding = pair.Binding;
                if (!IsPurchasedNativeReady(binding)) continue;
                if (!channel.Records.TryGetValue(binding.Id, out var record)) continue;
                var state = BoltHoleState.Decode(record.Payload);
                if (!binding.OriginalCaptured)
                {
                    binding.Original = Capture(binding.Hole);
                    binding.State = binding.Original;
                    binding.OriginalCaptured = true;
                }
                Apply(binding, state);
                deferredPurchasedApply.Remove(binding.Id);
            }
        }

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session;
            this.channel = channel;
            session.PoseReceived += PoseReceived; session.PeerLeft += PeerLeft;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command; channel.CommandRejected += MountRejected; channel.ReplyReceived += MountReply;
        }
        [MonMP.Core.World.WorldCommandAdapters(BoltHoleState.Adapter, ItemState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0) return;
            if (impactEquipment.TryGetValue(id, out var impactBinding) && SceneImpactGunEquipmentCommand.HasMarker(payload))
            {
                if (SceneImpactGunEquipmentCommand.Is(payload))
                {
                    SceneImpactGunEquipmentCommand equipmentCommand;
                    try { equipmentCommand = SceneImpactGunEquipmentCommand.Decode(payload); }
                    catch (InvalidDataException) { return; }
                    ImpactEquipmentCommand(peer, impactBinding, equipmentCommand);
                }
                return;
            }
            if (bindings.TryGetValue(id, out var mount) && BoltMountCommand.Is(payload))
            {
                BoltMountCommand request;
                try { request = BoltMountCommand.Decode(payload); } catch (InvalidDataException) { return; }
                MountCommand(peer, mount, request); return;
            }
            if (BoltToolCommand.HasMarker(payload))
            {
                BoltToolCommand command;
                try { command = BoltToolCommand.Decode(payload); }
                catch (InvalidDataException) { return; }
                if (id == command.Tool || (id == command.Bolt && impactTools.ContainsKey(command.Tool))) ToolCommand(peer, command);
                return;
            }
            // Old peers and forged callers cannot select installed state or percent by writing the
            // public bolt-hole record. Only validated mount/tool actions and host polling mutate it.
        }

        private void ImpactEquipmentCommand(uint peer, ImpactEquipmentBinding binding, SceneImpactGunEquipmentCommand command)
        {
            if (!host || channel?.FullySynchronized != true || !impactTools.ContainsKey(binding.Id)) return;
            var before = binding.Lease.State;
            Vector3 position = default;
            bool initialClaim = before.Owner == ItemLease.Unowned;
            bool fresh = !command.Equipped || TryImpactHolder(peer, binding.Id, !initialClaim, out position);
            bool withinReach = !command.Equipped || fresh && (position - binding.OriginalPosition).sqrMagnitude <=
                (initialClaim ? MaxPeerRange : ImpactEquipReach) * (initialClaim ? MaxPeerRange : ImpactEquipReach);
            bool otherImpactOwned = impactEquipment.Values.Any(value => value != binding && value.Lease.State.Owner == peer);
            bool hasNoLeasedItem = !initialClaim || MonMPPlugin.Instance?.Items?.HeldItemOf(peer) == null;
            bool actionsAvailable = !impactActionsBlocked && MonMPPlugin.Instance?.Items?.TransfersBlocked != true &&
                !otherImpactOwned && hasNoLeasedItem;
            if (!binding.Lease.Apply(peer, command, actionsAvailable, fresh, withinReach)) return;
            binding.Expires = command.Equipped ? Time.unscaledTime + ImpactEquipTimeout : 0;
            var after = binding.Lease.State;
            if (after.Owner != before.Owner || after.Generation != before.Generation)
            {
                ResetImpactPresentation(before.Owner);
                channel.Publish(binding.Id, after.Encode());
            }
        }

        private bool TryImpactHolder(uint peer, ulong id, bool requireTool, out Vector3 position)
        {
            position = default;
            if (session == null || !impactTools.TryGetValue(id, out var scene) || scene.Tool == null) return false;
            if (peer == session.LocalPeerId)
            {
                var current = SceneGameplay;
                if (current?.Player == null || current.Get_IsDead || current.Get_IsSleeping ||
                    !BoltToolPolicy.CanOperate((byte)current.Get_PlayerState) ||
                    current.Player.Get_ActiveTool != scene.Tool || !scene.Tool.isEquiped) return false;
                position = current.PlayerWalking.transform.position;
            }
            else
            {
                if (!session.Peers.ContainsKey(peer) || !presence.TryGetValue(peer, out var seen) ||
                    Time.unscaledTime - seen.Time > PresenceTimeout ||
                    !BoltToolPolicy.CanOperate(seen.Pose.State) || seen.Pose.Rest != PlayerRest.None ||
                    (requireTool && (seen.Pose.Hands & PlayerHands.Tool) == 0)) return false;
                position = PeerPosition(seen.Pose);
            }
            return (position - impactEquipment[id].OriginalPosition).sqrMagnitude <= ImpactEquipReach * ImpactEquipReach;
        }

        private void SetImpactEquipmentState(ImpactEquipmentBinding binding, SceneImpactGunEquipmentState state)
        {
            var before = binding.Lease.State;
            binding.Lease.Restore(state);
            if (before.Owner != state.Owner) ResetImpactPresentation(before.Owner);
        }

        private void RevokeImpactEquipment(ImpactEquipmentBinding binding)
        {
            uint owner = binding.Lease.State.Owner;
            if (owner == ItemLease.Unowned) return;
            foreach (var hold in activeHolds.Where(value => value.Value.Tool == binding.Id).Select(value => value.Key).ToArray())
                activeHolds.Remove(hold);
            binding.Lease.Revoke(); binding.Expires = 0;
            ResetImpactPresentation(owner);
            if (channel?.Ready == true) channel.Publish(binding.Id, binding.Lease.State.Encode());
        }

        private void SendImpactEquipment(ImpactEquipmentBinding binding, bool equipped)
        {
            if (channel?.FullySynchronized != true || session == null) return;
            var command = new SceneImpactGunEquipmentCommand(equipped, binding.Lease.State.Generation);
            if (host) ImpactEquipmentCommand(session.LocalPeerId, binding, command);
            else channel.Request(binding.Id, command.Encode(), useLease: true);
        }

        internal bool IsMountPending(InteractableItems item)
        {
            if (IsDeferredInstall(item)) return true;
            foreach (var pending in pendingMounts.Values) if (pending.Bolt == item) return true;
            return false;
        }

        private bool MountReplyPending(Binding binding)
        {
            return mountReplyRevisions.TryGetValue(binding.Id, out uint revision) &&
                (channel == null || !channel.Records.TryGetValue(binding.Id, out var record) || record.Revision < revision);
        }
        private bool MountBusy(Binding binding)
        {
            return pendingMounts.ContainsKey(binding.Id) || deferredInstalls.ContainsKey(binding.Id) || MountReplyPending(binding);
        }

        internal bool AllowLocalMount(BoltHoleDetails hole) => host || disposed || applying || session?.IsRunning != true ||
            !byHole.TryGetValue(hole, out var binding) || !MountBusy(binding);

        // The native Attach callback can run before a just-spawned pack bolt has an item id.
        // SharedItems uses this exact reference to adopt it even though it is already placed.
        internal bool IsDeferredInstall(InteractableItems item)
        {
            if (item is not Hardware_Bolt bolt) return false;
            foreach (var pending in deferredInstalls.Values) if (pending.Bolt == bolt) return true;
            return false;
        }

        internal void NativeInstalled(BoltHoleDetails hole, Hardware_Bolt bolt)
        {
            if (host || disposed || applying || MonMPPlugin.Instance?.World?.VehicleParts.IsApplying == true || !byHole.TryGetValue(hole, out var b)) return;
            // A late native save callback restores this guest's old installed copy. It is
            // not a new player input. Real hand/tool inserts retain the exact bolt reference
            // through Attach, including when the player picked up a saved loose bolt.
            var player = SceneGameplay?.Player;
            if (nativeRecheckIds.Contains(b.Id) && bolt.isSpawnFromTheSaveLoadSystem && player?.Get_HoldingItem != bolt &&
                (player?.Get_ActiveTool is not InteractableTools tool || !IsBoltTool(tool) || HoldingBolt(tool) != bolt)) return;
            // A bolt fresh from a box may not be shared yet, or the hole may still await an earlier reply.
            // Such an install used to stay on this copy alone: stuck "installed" for us, loose for the host (#134).
            deferredInstalls[b.Id] = (bolt, Time.unscaledTime + 6f);
            MonMPPlugin.Instance?.Items?.AdoptPackedBoltForInstall(bolt);
            TryDeferredInstall(b);
        }

        private readonly Dictionary<ulong, (Hardware_Bolt Bolt, float Until)> deferredInstalls = new Dictionary<ulong, (Hardware_Bolt, float)>();

#if MONMP_DEVELOPMENT
        internal int TestRolledBackInstalls { get; private set; }
#endif


        private void TryDeferredInstall(Binding b)
        {
            if (!deferredInstalls.TryGetValue(b.Id, out var deferred)) return;
            var hole = b.Hole; var bolt = deferred.Bolt;
            if (bolt == null || !hole.isBoltInstalled || hole.generated_BoltHole?.hardware_Bolt != bolt) { deferredInstalls.Remove(b.Id); return; }
            var items = MonMPPlugin.Instance?.Items;
            if (channel?.FullySynchronized == true && !pendingMounts.ContainsKey(b.Id) && !MountReplyPending(b) &&
                items?.BeginBoltInstall(bolt, out ulong id, out uint generation) == true)
            {
                deferredInstalls.Remove(b.Id);
                SendMount(b, bolt, new BoltMountCommand(Guid.NewGuid(), BoltMountAction.Install, id, generation));
                return;
            }
            if (Time.unscaledTime < deferred.Until && channel != null) return;
            deferredInstalls.Remove(b.Id);
            bool previous = applying; applying = true;
            try
            {
                hole.Detach();
                if (bolt.rb == null) bolt.CreateBasics();
                // The host may already have installed the bolt's shared copy; its record, applied while
                // ours was pending, compared equal and was skipped. Show whatever the host holds now.
                if (channel != null && channel.Records.TryGetValue(b.Id, out var record))
                { b.State = BoltHoleState.Decode(record.Payload); ApplyToHoleNative(hole, b.State); }
            }
            finally { applying = previous; RefreshLocalHoleInteraction(hole); }

#if MONMP_DEVELOPMENT
            TestRolledBackInstalls++;
#endif

            MonMPPlugin.Instance?.ReportItemUseUnavailable("That bolt could not be shared yet; pick it up and try again.");
        }

        internal void NativeRemoved(BoltHoleDetails hole)
        {
            if (host || disposed || applying || channel?.FullySynchronized != true || !byHole.TryGetValue(hole, out var b) ||
                pendingMounts.ContainsKey(b.Id) || hole.generated_BoltHole?.hardware_Bolt is not Hardware_Bolt bolt) return;
            SendMount(b, bolt, new BoltMountCommand(Guid.NewGuid(), BoltMountAction.Remove));
        }

        private void SendMount(Binding b, Hardware_Bolt bolt, BoltMountCommand request)
        {
            if (pendingMounts.ContainsKey(b.Id)) return;
            pendingMounts[b.Id] = new PendingMount { Command = request, Bolt = bolt, Until = Time.unscaledTime + 5 };
            channel!.Request(b.Id, request.Encode());
        }

        private void MountCommand(uint peer, Binding b, BoltMountCommand request)
        {
            var key = (peer, request.Operation);
            if (mountResults.TryGetValue(key, out var prior))
            {
                bool matches = prior.Hole == b.Id && prior.Command.Equals(request) && prior.Accepted;
                if (!matches) LogMountRefusal(peer, b, request, "operation replay mismatch or prior refusal");
                ReplyMount(peer, b.Id, request.Operation, matches, BoltHoleState.Decode(channel!.Records[b.Id].Payload), matches ? prior.Output : 0);
                return;
            }
            if (mountResults.Keys.Count(k => k.Peer == peer) >= 1024)
            { LogMountRefusal(peer, b, request, "result limit"); ReplyMount(peer, b.Id, request.Operation, false, b.State); return; }
            var items = MonMPPlugin.Instance?.Items;
            bool accepted = false;
            ulong output = 0;
            mountResults[key] = (b.Id, request, false, 0);
            string reason = "";
            if (channel?.FullySynchronized != true) reason = "world not synchronized";
            else if (items == null) reason = "item receiver missing";
            else if (items.TransfersBlocked) reason = "item transfers blocked";
            else if (!TakeMountBudget(peer)) reason = "mount budget";
            else if (!TryClaim(peer, request.Operation)) reason = "operation already claimed or limit";
            else if (b.Hole.HoleLocation == null || !b.Hole.HoleLocation.gameObject.activeInHierarchy) reason = "hole inactive";
            else if (!items.HostPeerNear(peer, b.Hole.HoleLocation.position, MaxPeerRange)) reason = "peer range or presence";
            else
            {
                if (request.Action == BoltMountAction.Install)
                    accepted = items.HostInstallBolt(peer, request.Item, request.Generation, b.Hole, out reason);
                else if (!b.Hole.isBoltInstalled) reason = "hole empty";
                else if (b.Hole.BoltedPercent > 0) reason = "bolt still tightened";
                else if (b.Hole.generated_BoltHole?.hardware_Bolt is not Hardware_Bolt bolt) reason = "native bolt missing";
                else if (!items.CanRegisterNativeOutput(bolt)) reason = "output registration unavailable";
                else
                {
                    b.Hole.Detach(); output = items.HostRegisterNativeOutput(bolt, peer, true); accepted = output != 0;
                    if (!accepted) reason = "output registration failed";
                }
            }
            if (!accepted) LogMountRefusal(peer, b, request, reason);
            mountResults[key] = (b.Id, request, accepted, output);
            b.State = Capture(b.Hole);
            channel!.Publish(b.Id, b.State.Encode());
            ReplyMount(peer, b.Id, request.Operation, accepted, b.State, accepted ? output : 0);
        }

        private void MountRejected(uint peer, ulong id, byte[] payload)
        {
            if (!host || !bindings.ContainsKey(id) || !BoltMountCommand.Is(payload)) return;
            BoltMountCommand request;
            try { request = BoltMountCommand.Decode(payload); } catch (InvalidDataException) { return; }
            if (mountResults.TryGetValue((peer, request.Operation), out var prior))
            {
                bool matches = prior.Hole == id && prior.Command.Equals(request) && prior.Accepted;
                if (!matches) LogMountRefusal(peer, bindings[id], request, "stale replay mismatch or prior refusal");
                ReplyMount(peer, id, request.Operation, matches, BoltHoleState.Decode(channel!.Records[id].Payload), matches ? prior.Output : 0); return;
            }
            LogMountRefusal(peer, bindings[id], request, "world command revision or session gate");
            ReplyMount(peer, id, request.Operation, false, BoltHoleState.Decode(channel!.Records[id].Payload));
        }

        private void LogMountRefusal(uint peer, Binding b, BoltMountCommand request, string reason)
        {
            float now = Time.unscaledTime;
            mountRefusals.TryGetValue(peer, out var previous);
            if (mountRefusals.ContainsKey(peer) && now < previous.Next)
            { mountRefusals[peer] = (previous.Next, previous.Suppressed + 1); return; }
            mountRefusals[peer] = (now + 1f, 0);
            Debug.Log("MonMP bolt mount refused for peer " + peer + ": action=" + request.Action + " hole=" + b.Id +
                " item=" + request.Item + " generation=" + request.Generation + " reason=" + reason +
                " installed=" + b.Hole.isBoltInstalled + " percent=" + b.Hole.BoltedPercent + " suppressed=" + previous.Suppressed);
        }

        private void ReplyMount(uint peer, ulong id, Guid operation, bool accepted, BoltHoleState state, ulong output = 0)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(BoltMountCommand.Marker); writer.Write(operation.ToByteArray()); writer.Write(accepted); writer.Write(state.Encode()); writer.Write(output);
            writer.Write(channel!.Records[id].Revision);
            channel!.Reply(peer, id, stream.ToArray());
        }

        private void MountReply(ulong id, byte[] payload)
        {
            if (host || payload.Length != 30 + BoltHoleState.PayloadLength || payload[0] != BoltMountCommand.Marker || !pendingMounts.TryGetValue(id, out var pending)) return;
            using var reader = new BinaryReader(new MemoryStream(payload, false)); reader.ReadByte();
            if (new Guid(reader.ReadBytes(16)) != pending.Command.Operation) return;
            bool accepted = reader.ReadBoolean(); var state = BoltHoleState.Decode(reader.ReadBytes(BoltHoleState.PayloadLength));
            ulong output = reader.ReadUInt64();
            uint revision = reader.ReadUInt32();
            mountReplyRevisions[id] = revision;
            if (channel!.Records.TryGetValue(id, out var record) && record.Revision >= revision) state = BoltHoleState.Decode(record.Payload);
            mountReplyStates[id] = state;
            FinishMount(id, pending, accepted, state, output);
        }

        private void FinishMount(ulong id, PendingMount pending, bool accepted, BoltHoleState state, ulong output = 0)
        {
            pendingMounts.Remove(id);
            var items = MonMPPlugin.Instance?.Items;
            if (!accepted)
            {
                if (clientHold?.Bolt == id) EndClientHold();
                boltPredictions.Remove(id);
                // Remove our optimistic copy before restoring the canonical hole/item pair.
                var hole = bindings[id].Hole;
                if (hole.generated_BoltHole?.hardware_Bolt == pending.Bolt) { hole.isBoltInstalled = false; hole.BoltedPercent = 0; }
                if (pending.Command.Action == BoltMountAction.Install) items?.RejectBoltInstall(pending.Command.Item, pending.Bolt);
                else items?.DiscardLocalBolt(pending.Bolt);
            }
            else if (pending.Command.Action == BoltMountAction.Remove && pending.Bolt != null) items?.CompleteNativeOutput(output, pending.Bolt);
            Apply(bindings[id], state);
        }

        private void UpdatePendingMounts()
        {
            if (host || pendingMounts.Count == 0) return;
            foreach (var pair in pendingMounts.Where(p => Time.unscaledTime >= p.Value.Until).ToArray())
            {
                // The host may have accepted the removal already. Discarding its native loose
                // bolt here loses the output when the accepted reply eventually arrives.
                // Keep the bounded operation until a definitive reply or session disposal.
                pair.Value.Until = float.PositiveInfinity;
                MonMPPlugin.Instance?.ReportItemUseUnavailable("Bolt confirmation is delayed. Wait for the host before changing this hole again.");
            }
        }

        private void PoseReceived(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);

        private void PeerLeft(uint peer)
        {
            presence.Remove(peer); budgets.Remove(peer); mountBudgets.Remove(peer); mountRefusals.Remove(peer); operations.Remove(peer); activeHolds.Remove(peer);
            foreach (var key in mountResults.Keys.Where(k => k.Peer == peer).ToArray()) mountResults.Remove(key);
            if (host)
                foreach (var binding in impactEquipment.Values.Where(value => value.Lease.State.Owner == peer).ToArray())
                    RevokeImpactEquipment(binding);
            ResetImpactPresentation(peer);
        }

        internal bool InterceptToolInput(InteractableTools tool, BoltToolDirection direction, bool pressed)
        {
            if (disposed || host || session?.IsRunning != true || !IsBoltTool(tool)) return true;
            if (pressed)
            {
                if (!TryResolveLocalTarget(tool, out var binding, out var aim)) return true;
                if (MountBusy(binding))
                {
                    SetButton(tool, direction, true);
                    TryBeginPendingInstallHold(tool, binding, direction, aim);
                    return false;
                }
                if (!binding.Hole.isBoltInstalled) return true;
                if (HoldingBolt(tool) != null) return true;
                if (direction == BoltToolDirection.Loosen && binding.Hole.BoltedPercent <= 0) return true;
                if (channel?.FullySynchronized != true || !TryLocalLease(tool, out ulong toolId, out uint generation))
                {
                    // An impact-gun press can arrive during the short request/grant round trip.
                    // Keep the native button bit so AutoBeginHold can start the shared operation
                    // after the host has granted this exact scene-gun generation. Native progress
                    // remains masked for installed shared bolts during that wait.
                    if (IsLocalImpactClaimPending(tool)) SetButton(tool, direction, true);
                    return false;
                }
                if (BoltToolPolicy.AtLimit(binding.Hole.BoltedPercent, direction)) return false;
                if (!HasPressure(tool)) return false;
                BeginHold(tool, binding, direction, toolId, generation, aim.Origin, aim.Yaw, aim.Pitch, aim.Independent);
                return false;
            }
            if (clientHold == null || clientHold.NativeTool != tool || clientHold.Direction != direction) return true;
            EndClientHold();
            return false;
        }

        internal void ClearPackWaitInput(InteractableTools tool, bool trowHard) =>
            SetButton(tool, trowHard ? BoltToolDirection.Loosen : BoltToolDirection.Tighten, false);

        private void BeginHold(InteractableTools tool, Binding binding, BoltToolDirection direction, ulong toolId, uint generation, Vector3 origin, float yaw, float pitch, bool independentAim)
        {
            if (channel?.FullySynchronized != true || MonMPPlugin.Instance?.Items?.TransfersBlocked != false ||
                binding.Hole.SockedBoltedByThisBolt?.isInstalled != true) return;
            var hold = new ClientHold
            {
                Operation = Guid.NewGuid(), Sequence = 1, Tool = toolId, Generation = generation,
                Bolt = binding.Id, Kind = KindOf(tool), Direction = direction, NativeTool = tool,
                NextHeartbeat = Time.unscaledTime + ClientHeartbeat,
                LastCanonicalPercent = binding.Hole.BoltedPercent, LastCanonicalProgressAt = Time.unscaledTime
            };
            if (clientHold != null)
            {
                boltPredictions.Remove(clientHold.Bolt);
                if (bindings.TryGetValue(clientHold.Bolt, out var old) && old.Hole.isBoltInstalled) ShowBolt(old.Hole, old.Hole.BoltedPercent);
                EndClientHold();
            }
            clientHold = hold;
            SetButton(tool, direction, true);
            boltPredictions[binding.Id] = new BoltProgressPresentation(binding.Hole.BoltedPercent);
            hold.Began = !MountBusy(binding);
            if (hold.Began) Send(hold, BoltToolAction.Begin, origin, yaw, pitch, independentAim);
            HoldSound(hold, true);
        }

        private bool TryBeginPendingInstallHold(InteractableTools tool, Binding binding, BoltToolDirection direction, Integration.ToolAimSample aim)
        {
            if (clientHold != null || direction != BoltToolDirection.Tighten ||
                !pendingMounts.TryGetValue(binding.Id, out var pending) || pending.Command.Action != BoltMountAction.Install ||
                pending.Bolt == null || !ReferenceEquals(binding.Hole.generated_BoltHole?.hardware_Bolt, pending.Bolt) ||
                !binding.Hole.isBoltInstalled || binding.Hole.SockedBoltedByThisBolt?.isInstalled != true ||
                HoldingBolt(tool) != null || channel?.FullySynchronized != true || !HasPressure(tool) ||
                !TryLocalLease(tool, out ulong toolId, out uint generation)) return false;
            // Native Attach already placed this exact input. Animate the held wrench now;
            // send no tool operation until the definitive install reply and its record agree.
            BeginHold(tool, binding, direction, toolId, generation, aim.Origin, aim.Yaw, aim.Pitch, aim.Independent);
            return true;
        }

        // A guest's own progress is masked from the native coroutine (the host turns the bolt), and that
        // coroutine is what plays the wrench's screw loop or the impact gun's burst: nobody heard a guest
        // work a bolt (#261). Play them here as the native tool would; the relay patches then carry them
        // to everyone else.
        private static void HoldSound(ClientHold hold, bool on)
        {
            if (hold.Sounding == on) return;
            hold.Sounding = on;
            SOUND_Manager? sound;
            try { sound = Singleton<SOUND_Manager>.i; } catch { return; }
            if (sound == null) return;
            if (hold.NativeTool is Tool_Air_Impact) { if (on) sound.PlaySound_ImpactUnScrew(); return; }
            if (hold.Direction == BoltToolDirection.Tighten) { if (on) sound.PlaySound_BoltScrewing(); else sound.PlaySound_BoltScrewingOff(); }
            else if (on) sound.PlaySound_BoltUnScrewing();
            else sound.PlaySound_BoltUnScrewingOff();
        }

        // #184: the press that inserts a bolt happens before the hole is installed, so natively the same
        // held click drives it in; here it only reached the host after a second click. A button still
        // held over an installed shared bolt, with no hold running, starts one.
        private float nextAutoBegin;
        private void AutoBeginHold()
        {
            if (Time.unscaledTime < nextAutoBegin || channel?.FullySynchronized != true) return;
            nextAutoBegin = Time.unscaledTime + 0.1f;
            if (SceneGameplay?.Player?.Get_ActiveTool is not InteractableTools tool || !IsBoltTool(tool) || HoldingBolt(tool) != null) return;
            var (left, right) = ButtonFields(tool);
            BoltToolDirection direction;
            if (left.GetValue(tool) is true) direction = BoltToolDirection.Tighten;
            else if (right.GetValue(tool) is true) direction = BoltToolDirection.Loosen;
            else return;
            if (!TryResolveLocalTarget(tool, out var binding, out var aim)) return;
            if (MountBusy(binding)) { TryBeginPendingInstallHold(tool, binding, direction, aim); return; }
            if (!binding.Hole.isBoltInstalled ||
                (direction == BoltToolDirection.Loosen && binding.Hole.BoltedPercent <= 0) ||
                BoltToolPolicy.AtLimit(binding.Hole.BoltedPercent, direction) || !HasPressure(tool) ||
                !TryLocalLease(tool, out ulong toolId, out uint generation)) return;
            BeginHold(tool, binding, direction, toolId, generation, aim.Origin, aim.Yaw, aim.Pitch, aim.Independent);
        }

        // #184: the guest's wrench shows the hovered bolt's percent only when the hover changes; host
        // progress arrives as records, so refresh the label while this player aims at that bolt.
        private UI_InteractableText? percentText;
        private void ShowPercent(BoltHoleDetails hole)
        {
            if (!hole.isBoltInstalled || SceneGameplay?.Player?.Get_ActiveTool is not InteractableTools tool || !IsBoltTool(tool) ||
                HoldingBolt(tool) != null || !TryResolveLocalTarget(tool, out var binding, out _) || binding.Hole != hole)
                return;
            if (percentText == null) percentText = UnityEngine.Object.FindObjectOfType<UI_InteractableText>();
            bool preview = boltPredictions.TryGetValue(binding.Id, out var prediction);
            float percent = preview ? prediction!.Shown : hole.BoltedPercent;
            percentText?.ShowText(Mathf.Floor(Mathf.Clamp01(percent) * 100f) + "%");
        }

        // The vanilla coroutine remains alive on guests for bolt targeting, pickup and installation.
        // Only mask its held buttons while its own center ray is over an installed shared bolt.
        internal int MaskGuestProgress(object stateMachine)
        {
            if (host || disposed || session?.IsRunning != true) return 0;
            var ownerField = AccessTools.Field(stateMachine.GetType(), "<>4__this");
            var tool = ownerField?.GetValue(stateMachine) as InteractableTools;
            if (tool == null || !IsBoltTool(tool) || !TryResolveLocalTarget(tool, out var binding, out _)) return 0;
            bool busy = MountBusy(binding);
            if (!busy && (HoldingBolt(tool) != null || !binding.Hole.isBoltInstalled)) return 0;
            var (left, right) = ButtonFields(tool);
            int mask = (left.GetValue(tool) is true ? 1 : 0) | (right.GetValue(tool) is true ? 2 : 0);
            // Once host progress reaches zero the native coroutine takes the bolt into its mouth.
            if (!busy && binding.Hole.BoltedPercent <= 0) mask &= ~2;
            if (mask == 0) return 0;
            if ((mask & 1) != 0) left.SetValue(tool, false);
            if ((mask & 2) != 0) right.SetValue(tool, false);
            return mask;
        }

        internal void RestoreGuestProgress(object stateMachine, int mask)
        {
            if (mask == 0) return;
            var ownerField = AccessTools.Field(stateMachine.GetType(), "<>4__this");
            var tool = ownerField?.GetValue(stateMachine) as InteractableTools;
            if (tool == null || !IsBoltTool(tool)) return;
            var (left, right) = ButtonFields(tool);
            if ((mask & 1) != 0) left.SetValue(tool, true);
            if ((mask & 2) != 0) right.SetValue(tool, true);
        }

        private void ToolCommand(uint peer, BoltToolCommand command)
        {
            var world = channel;
            if (session == null || world == null || (command.Tool <= WorldRecord.DynamicIdStart && !impactTools.ContainsKey(command.Tool))) return;
            if (command.Action == BoltToolAction.End)
            {
                if (activeHolds.TryGetValue(peer, out var ending) && ending.Operation == command.Operation &&
                    ending.Tool == command.Tool && ending.Generation == command.Generation && ending.Bolt == command.Bolt &&
                    ending.Kind == command.ToolKind && ending.Direction == command.Direction && command.Sequence > ending.Sequence)
                    activeHolds.Remove(peer);
                return;
            }

            bool budget = TakeBudget(peer);
            bool poseFresh = presence.TryGetValue(peer, out var seen) && Time.unscaledTime - seen.Time <= PresenceTimeout &&
                BoltToolPolicy.CanOperate(seen.Pose.State);
            PlayerPose peerPose = poseFresh ? seen.Pose : default;
            bool cameraFits = poseFresh && command.CameraFits(seen.Pose, checkSegment);
            bool exactLease = HostTool(command, peer, out var nativeTool);
            bool toolInRange = exactLease && poseFresh && ToolInRange(peer, command.Tool, peerPose);
            bindings.TryGetValue(command.Bolt, out var target);
            BoltHoleDetails? hole = target?.Hole;
            Transform? holeLocation = hole?.HoleLocation;
            bool targetExists = target != null && hole != null && holeLocation != null && holeLocation.gameObject.activeInHierarchy;
            bool targetInRange = targetExists && poseFresh &&
                (PeerPosition(peerPose) - holeLocation!.position).sqrMagnitude <= MaxPeerRange * MaxPeerRange;
            bool hostRay = poseFresh && cameraFits && targetExists && RayHits(command.CameraX, command.CameraY, command.CameraZ,
                command.CameraYaw, command.CameraPitch, hole!);
            bool compressorReady = exactLease && HasPressure(nativeTool!);
            bool sameToolType = exactLease && KindOf(nativeTool!) == command.ToolKind;
            bool limit = targetExists && BoltToolPolicy.AtLimit(hole!.BoltedPercent, command.Direction);

            if (command.Action == BoltToolAction.Begin)
            {
                if (!TryClaim(peer, command.Operation)) return;
                bool authorized = world.FullySynchronized && MonMPPlugin.Instance?.Items?.TransfersBlocked != true &&
                    !activeHolds.Any(existing => existing.Key != peer && existing.Value.Bolt == command.Bolt) &&
                    sameToolType && BoltToolPolicy.CanBegin(world.FullySynchronized,
                        MonMPPlugin.Instance?.Items?.TransfersBlocked == true, budget, exactLease, toolInRange,
                        poseFresh, cameraFits, targetExists, targetInRange, hostRay,
                        targetExists && hole!.isBoltInstalled, limit, compressorReady);
                if (!authorized)
                {
                    Debug.Log("MonMP bolt hold refused for peer " + peer + ": sync=" + world.FullySynchronized + " budget=" + budget + " lease=" + exactLease +
                        " toolRange=" + toolInRange + " pose=" + poseFresh + " camera=" + cameraFits + " target=" + targetExists + " range=" + targetInRange +
                        " ray=" + hostRay + " installed=" + (targetExists && hole!.isBoltInstalled) + " limit=" + limit + " air=" + compressorReady +
                        " kind=" + sameToolType + " busy=" + activeHolds.Any(existing => existing.Key != peer && existing.Value.Bolt == command.Bolt));
                    return;
                }
                activeHolds[peer] = new ActiveHold
                {
                    Operation = command.Operation, Sequence = command.Sequence, Tool = command.Tool,
                    Generation = command.Generation, Bolt = command.Bolt, Kind = command.ToolKind,
                    Direction = command.Direction, LastCommandAt = Time.unscaledTime,
                    CameraOrigin = new Vector3(command.CameraX, command.CameraY, command.CameraZ),
                    CameraYaw = command.CameraYaw, CameraPitch = command.CameraPitch, NativeTool = nativeTool!
                };
                return;
            }

            if (!activeHolds.TryGetValue(peer, out var hold) || hold.Operation != command.Operation ||
                hold.Tool != command.Tool || hold.Generation != command.Generation || hold.Bolt != command.Bolt ||
                hold.Kind != command.ToolKind || hold.Direction != command.Direction || command.Sequence <= hold.Sequence)
                return;
            if (!budget || !world.FullySynchronized || MonMPPlugin.Instance?.Items?.TransfersBlocked == true ||
                !sameToolType || !exactLease || !toolInRange || !poseFresh || !cameraFits || !targetExists ||
                !targetInRange || !hostRay || !hole!.isBoltInstalled || limit || !compressorReady)
            {
                activeHolds.Remove(peer);
                return;
            }
            hold.Sequence = command.Sequence;
            hold.LastCommandAt = Time.unscaledTime;
            hold.CameraOrigin = new Vector3(command.CameraX, command.CameraY, command.CameraZ);
            hold.CameraYaw = command.CameraYaw; hold.CameraPitch = command.CameraPitch;
        }

        private bool HostToolHeld(uint peer, ulong tool, uint generation, BoltToolKind kind)
        {
            if (impactTools.TryGetValue(tool, out var scene))
            {
                if (kind != BoltToolKind.AirImpact || scene.Tool == null ||
                    !impactEquipment.TryGetValue(tool, out var equipment) ||
                    equipment.Lease.State.Owner != peer || equipment.Lease.State.Generation != generation ||
                    equipment.Expires < Time.unscaledTime) return false;
                if (session != null && peer == session.LocalPeerId)
                    return SceneGameplay?.Player?.Get_ActiveTool == scene.Tool && scene.Tool.isEquiped;
                return presence.TryGetValue(peer, out var seen) && Time.unscaledTime - seen.Time <= PresenceTimeout &&
                    BoltToolPolicy.CanOperate(seen.Pose.State) && seen.Pose.Rest == PlayerRest.None &&
                    (seen.Pose.Hands & PlayerHands.Tool) != 0;
            }
            return MonMPPlugin.Instance?.Items?.HostHasLeasedTool(peer, tool, generation,
                kind == BoltToolKind.AdjustableSpanner ? typeof(Tool_AdjustableSpanner) : typeof(Tool_Air_Impact)) == true;
        }
        // A carried tool is near its holder; a scene impact gun reaches as far as its native hose (20 m).
        private bool ToolInRange(uint peer, ulong tool, PlayerPose pose)
        {
            if (impactTools.TryGetValue(tool, out var scene))
            {
                var anchor = impactEquipment.TryGetValue(tool, out var equipment) ? equipment.OriginalPosition : scene.Tool.transform.position;
                return (PeerPosition(pose) - anchor).sqrMagnitude <= ImpactEquipReach * ImpactEquipReach;
            }
            return MonMPPlugin.Instance?.Items?.HostItemInRange(peer, tool, 4.5f) == true;
        }

        private bool HostTool(BoltToolCommand command, uint peer, out InteractableTools? tool)
        {
            tool = null;
            if (impactTools.TryGetValue(command.Tool, out var scene))
            {
                if (!HostToolHeld(peer, command.Tool, command.Generation, command.ToolKind)) return false;
                tool = scene.Tool;
                return true;
            }
            var items = MonMPPlugin.Instance?.Items;
            if (items == null) return false;
            Type expected = command.ToolKind == BoltToolKind.AdjustableSpanner ? typeof(Tool_AdjustableSpanner) :
                command.ToolKind == BoltToolKind.AirImpact ? typeof(Tool_Air_Impact) : typeof(void);
            if (!items.HostHasLeasedTool(peer, command.Tool, command.Generation, expected) ||
                items.ItemOf(command.Tool) is not InteractableTools found) return false;
            tool = found;
            return true;
        }

        private bool TryClaim(uint peer, Guid operation)
        {
            if (!operations.TryGetValue(peer, out var claimed))
            {
                claimed = new HashSet<Guid>(); operations.Add(peer, claimed);
            }
            if (claimed.Contains(operation) || claimed.Count >= 8192) return false;
            claimed.Add(operation);
            return true;
        }

        private bool TakeBudget(uint peer) { return TakeBudget(peer, budgets); }
        // Mounts remain bounded independently: a delayed tool-heartbeat burst must
        // never spend the tokens needed to install or remove a physical bolt.
        private bool TakeMountBudget(uint peer) { return TakeBudget(peer, mountBudgets); }
        private static bool TakeBudget(uint peer, Dictionary<uint, (float Tokens, float Time)> bucket)
        {
            float now = Time.unscaledTime;
            var state = bucket.TryGetValue(peer, out var existing)
                ? existing : (Tokens: CommandBudgetCapacity, Time: now);
            state.Tokens = Math.Min(CommandBudgetCapacity, state.Tokens + Math.Max(0, now - state.Time) * CommandBudgetPerSecond);
            state.Time = now;
            if (state.Tokens < 1) { bucket[peer] = state; return false; }
            state.Tokens--; bucket[peer] = state;
            return true;
        }

        private void TickHolds()
        {
            if (!host || activeHolds.Count == 0 || session == null || channel == null) return;
            float now = Time.unscaledTime;
            foreach (var pair in activeHolds.ToArray())
            {
                var hold = pair.Value;
                if (!bindings.TryGetValue(hold.Bolt, out var target) || !target.Hole.isBoltInstalled ||
                    now - hold.LastCommandAt > HoldTimeout || !presence.TryGetValue(pair.Key, out var seen) ||
                    now - seen.Time > PresenceTimeout || !BoltToolPolicy.CanOperate(seen.Pose.State) ||
                    MonMPPlugin.Instance?.Items?.TransfersBlocked == true ||
                    !HostToolHeld(pair.Key, hold.Tool, hold.Generation, hold.Kind) || !ToolInRange(pair.Key, hold.Tool, seen.Pose) ||
                    (PeerPosition(seen.Pose) - target.Hole.HoleLocation.position).sqrMagnitude > MaxPeerRange * MaxPeerRange ||
                    !RayHits(hold.CameraOrigin.x, hold.CameraOrigin.y, hold.CameraOrigin.z, hold.CameraYaw, hold.CameraPitch, target.Hole) ||
                    !HasPressure(hold.NativeTool) || BoltToolPolicy.AtLimit(target.Hole.BoltedPercent, hold.Direction))
                {
                    activeHolds.Remove(pair.Key);
                    continue;
                }

                float delta = Mathf.Max(Time.deltaTime, 0);
                if (delta <= 0) continue;
                bool compressorReady = HasPressure(hold.NativeTool);
                if (!compressorReady)
                {
                    activeHolds.Remove(pair.Key);
                    continue;
                }
                if (hold.NativeTool is Tool_Air_Impact impact)
                    ((AirCompressor)ImpactCompressor.GetValue(impact)).UsingAir();
                float before = target.Hole.BoltedPercent;
                float after = BoltToolPolicy.Advance(before, hold.Kind, hold.Direction, delta,
                    compressorReady);
                target.Hole.BoltedPercent = after;
                UpdateBoltVisual(target.Hole, hold.Direction);
                if (hold.Direction == BoltToolDirection.Tighten && before < 1 && after >= 1)
                    target.Hole.SockedBoltedByThisBolt.OneBoltFullyTighten();
                if (BoltToolPolicy.AtLimit(after, hold.Direction)) activeHolds.Remove(pair.Key);
            }
        }

        private void UpdateClientHold()
        {
            var hold = clientHold;
            if (hold == null) { AutoBeginHold(); return; }
            var buttons = ButtonFields(hold.NativeTool);
            bool isDown = (hold.Direction == BoltToolDirection.Tighten ? buttons.Left : buttons.Right).GetValue(hold.NativeTool) is true;
            bool blocked = channel?.FullySynchronized != true || MonMPPlugin.Instance?.Items?.TransfersBlocked != false;
            if (blocked || !TryLocalLease(hold.NativeTool, out ulong tool, out uint generation) ||
                tool != hold.Tool || generation != hold.Generation || !TryResolveLocalTarget(hold.NativeTool, out var target, out var aim) ||
                target.Id != hold.Bolt || !target.Hole.isBoltInstalled || target.Hole.SockedBoltedByThisBolt?.isInstalled != true ||
                HoldingBolt(hold.NativeTool) != null || !HasPressure(hold.NativeTool))
            {
                // Drop the presentation on a changed target, lease, native socket or save gate.
                // A still-held button may begin a new exact operation over the next bolt.
                boltPredictions.Remove(hold.Bolt);
                if (bindings.TryGetValue(hold.Bolt, out var old) && old.Hole.isBoltInstalled) ShowBolt(old.Hole, old.Hole.BoltedPercent);
                EndClientHold(keepButton: isDown && !blocked);
                return;
            }
            if (!isDown) { EndClientHold(); return; }
            if (!hold.Began && !MountBusy(target))
            {
                hold.Began = true;
                Send(hold, BoltToolAction.Begin, aim.Origin, aim.Yaw, aim.Pitch, aim.Independent);
                hold.NextHeartbeat = Time.unscaledTime + ClientHeartbeat;
                hold.LastCanonicalPercent = target.Hole.BoltedPercent;
                hold.LastCanonicalProgressAt = Time.unscaledTime;
            }
            float canonical = target.Hole.BoltedPercent;
            bool advanced = hold.Direction == BoltToolDirection.Tighten
                ? canonical > hold.LastCanonicalPercent : canonical < hold.LastCanonicalPercent;
            if (advanced) hold.LastCanonicalProgressAt = Time.unscaledTime;
            hold.LastCanonicalPercent = canonical;
            if (hold.Began && !MountBusy(target) && !BoltToolPolicy.AtLimit(canonical, hold.Direction) &&
                Time.unscaledTime - hold.LastCanonicalProgressAt >= ClientHoldRetrySeconds)
            {
                // Host aim/pose/heartbeat guards can retire an operation before the trigger is
                // released. KeepAlive cannot reopen it. Retire that ticket and request a fresh,
                // fully validated Begin while the same button/target/lease remain valid.
                BeginHold(hold.NativeTool, target, hold.Direction, tool, generation, aim.Origin, aim.Yaw, aim.Pitch, aim.Independent);
                return;
            }
            // Natively the loop stops once the bolt is fully in (or out) while the button stays down.
            if (hold.Sounding && bindings.TryGetValue(hold.Bolt, out var turning) &&
                (!turning.Hole.isBoltInstalled || BoltToolPolicy.AtLimit(turning.Hole.BoltedPercent, hold.Direction)))
                HoldSound(hold, false);
            if (hold.Began && Time.unscaledTime >= hold.NextHeartbeat)
            {
                hold.Sequence++;
                Send(hold, BoltToolAction.KeepAlive, aim.Origin, aim.Yaw, aim.Pitch, aim.Independent);
                hold.NextHeartbeat = Time.unscaledTime + ClientHeartbeat;
            }
        }

        private void EndClientHold(bool keepButton = false)
        {
            var hold = clientHold;
            if (hold == null) return;
            if (!keepButton) SetButton(hold.NativeTool, hold.Direction, false);
            HoldSound(hold, false);
            hold.Sequence++;
            if (hold.Began) Send(hold, BoltToolAction.End, Vector3.zero, 0, 0, false);
            clientHold = null;
        }

        private void Send(ClientHold hold, BoltToolAction action, Vector3 origin, float yaw, float pitch, bool independentAim)
        {
            if (channel == null || !channel.FullySynchronized) return;
            var command = new BoltToolCommand(action, hold.Operation, hold.Sequence, hold.Tool, hold.Generation,
                hold.Bolt, hold.Kind, hold.Direction, origin.x, origin.y, origin.z, yaw, pitch, independentAim);
            // A scene impact gun has no record of its own; its commands travel on the bolt hole's.
            channel.Request(impactTools.ContainsKey(hold.Tool) ? hold.Bolt : hold.Tool, command.Encode(), true);
        }

        private bool TryLocalLease(InteractableTools tool, out ulong id, out uint generation)
        {
            id = 0; generation = 0;
            if (tool is Tool_Air_Impact impact && impactIds.TryGetValue(impact, out id))
            {
                if (!impactEquipment.TryGetValue(id, out var equipment)) return false;
                generation = equipment.Lease.State.Generation;
                return !host && channel?.FullySynchronized == true && session != null &&
                    equipment.Lease.State.Owner == session.LocalPeerId &&
                    SceneGameplay?.Player?.Get_ActiveTool == tool && impact.isEquiped;
            }
            return MonMPPlugin.Instance?.Items?.TryGetLocalToolLease(tool, out id, out generation) == true;
        }

        private bool IsLocalImpactClaimPending(InteractableTools tool)
        {
            if (host || tool is not Tool_Air_Impact impact || !impactIds.TryGetValue(impact, out ulong id) ||
                !impactEquipment.TryGetValue(id, out var equipment) || session == null || channel?.FullySynchronized != true ||
                SceneGameplay?.Player?.Get_ActiveTool != tool) return false;
            uint owner = equipment.Lease.State.Owner;
            return (owner == ItemLease.Unowned || owner == session.LocalPeerId) && !impactActionsBlocked;
        }

        private bool TryResolveLocalTarget(InteractableTools tool, out Binding binding, out Integration.ToolAimSample aim)
        {
            binding = null!; aim = default;
            var field = tool is Tool_AdjustableSpanner ? SpannerCamera : ImpactCamera;
            var camera = field.GetValue(tool) as Camera ?? Camera.main;
            if (camera == null) return false;
            aim = Integration.LocalInteraction.SampleTool(camera);
            if (!(aim.Pitch >= -90 && aim.Pitch <= 90)) return false;
            var ray = aim.Ray;
            return Physics.Raycast(ray, out var hit, 2f, LayerMask.GetMask("BoltHole")) &&
                hit.collider.GetComponent<Generated_BoltHole>() is Generated_BoltHole generated && generated.boltHoleDetails != null &&
                byHole.TryGetValue(generated.boltHoleDetails, out binding) && generated.gameObject.activeInHierarchy;
        }

        private static bool RayHits(float x, float y, float z, float yaw, float pitch, BoltHoleDetails hole)
        {
            var direction = Quaternion.Euler(pitch, yaw, 0).normalized * Vector3.forward;
            var ray = new Ray(new Vector3(x, y, z), direction);
            // Native wrenches light hole colliders only for the parts installed when the local player
            // equipped one, so the host's copy of a reinstalled part is often dark. Light the target for the test.
            var target = hole.generated_BoltHole != null ? hole.generated_BoltHole.GetComponent<Collider>() : null;
            bool dark = target != null && !target.enabled;
            if (dark) target!.enabled = true;
            try
            {
                return Physics.Raycast(ray, out var hit, 2f, LayerMask.GetMask("BoltHole")) &&
                    hit.collider.GetComponent<Generated_BoltHole>() is Generated_BoltHole generated &&
                    ReferenceEquals(generated.boltHoleDetails, hole);
            }
            finally { if (dark) target!.enabled = false; }
        }

        private static InteractableItems? HoldingBolt(InteractableTools tool) =>
            (tool is Tool_AdjustableSpanner ? SpannerHoldingBolt : ImpactHoldingBolt).GetValue(tool) as InteractableItems;

        private static (FieldInfo Left, FieldInfo Right) ButtonFields(InteractableTools tool) => tool is Tool_AdjustableSpanner
            ? (SpannerLeftDown, SpannerRightDown) : (ImpactLeftDown, ImpactRightDown);

        private static void SetButton(InteractableTools tool, BoltToolDirection direction, bool value)
        {
            var fields = ButtonFields(tool);
            (direction == BoltToolDirection.Tighten ? fields.Left : fields.Right).SetValue(tool, value);
        }

        private static bool IsBoltTool(InteractableTools tool) => tool is Tool_AdjustableSpanner || tool is Tool_Air_Impact;
        private static BoltToolKind KindOf(InteractableTools tool) => tool is Tool_AdjustableSpanner
            ? BoltToolKind.AdjustableSpanner : BoltToolKind.AirImpact;
        private static bool HasPressure(InteractableTools tool) => tool is not Tool_Air_Impact impact ||
            (ImpactCompressor.GetValue(impact) is AirCompressor compressor && compressor.Get_AirPressure > 40f);
        private static Vector3 PeerPosition(PlayerPose pose) => new Vector3(pose.X, pose.Y, pose.Z);
        private static FieldInfo RequiredField(Type type, string name) => AccessTools.Field(type, name) ??
            throw new InvalidOperationException("Unsupported native bolt tool field: " + type.Name + "." + name);
        private static void UpdateBoltVisual(BoltHoleDetails hole, BoltToolDirection direction)
        {
            var visual = hole.generated_BoltHole?.boltGameObject;
            if (visual != null)
            {
                visual.transform.localPosition = Vector3.Lerp(hole.generated_BoltHole!.hardware_Bolt.OffsetWhenUnbolted,
                    Vector3.zero, hole.BoltedPercent);
                float sign = direction == BoltToolDirection.Tighten ? 1 : -1;
                visual.transform.Rotate(Vector3.up * (sign * 540f * Time.deltaTime), Space.Self);
            }
        }
        internal void SetNewActionsBlocked(bool value) => impactActionsBlocked = value;

        internal void FlushLocalOwnership()
        {
            if (disposed || channel?.Ready != true || session == null) return;
            foreach (var binding in impactEquipment.Values)
            {
                CancelLocalImpact(binding);
                if (binding.Lease.State.Owner == session.LocalPeerId) SendImpactEquipment(binding, false);
            }
            localImpactRequested = 0;
        }

        internal void RevokeOwnership(uint peer)
        {
            if (!host) throw new InvalidOperationException("Only the host can revoke impact-gun equipment.");
            foreach (var binding in impactEquipment.Values.Where(value => value.Lease.State.Owner == peer).ToArray())
            {
                if (session?.LocalPeerId == peer) CancelLocalImpact(binding);
                RevokeImpactEquipment(binding);
            }
        }

        internal bool AllowNativeImpactEquip(InteractableTools tool)
        {
            if (disposed || session?.IsRunning != true || tool is not Tool_Air_Impact impact ||
                !impactIds.TryGetValue(impact, out var id) || !impactEquipment.TryGetValue(id, out var binding)) return true;
            uint owner = binding.Lease.State.Owner;
            return !impactActionsBlocked && (owner == ItemLease.Unowned || owner == session.LocalPeerId);
        }

        private void CancelLocalImpact(ImpactEquipmentBinding binding)
        {
            if (gameplay?.Player != null && impactTools.TryGetValue(binding.Id, out var scene) && scene.Tool != null &&
                gameplay.Player.Get_ActiveTool == scene.Tool)
                PlayerUnEquipTool.Invoke(gameplay.Player, null);
        }

        private void UpdateImpactEquipment()
        {
            if (disposed || channel?.FullySynchronized != true || session == null) return;
            if (host)
                foreach (var binding in impactEquipment.Values.ToArray())
                    if (binding.Lease.State.Owner != ItemLease.Unowned &&
                        (Time.unscaledTime > binding.Expires ||
                         !TryImpactHolder(binding.Lease.State.Owner, binding.Id, true, out _)))
                        RevokeImpactEquipment(binding);

            foreach (var pair in impactVisuals.ToArray())
                if (!impactEquipment.Values.Any(value => value.Lease.State.Owner == pair.Key) ||
                    !presence.TryGetValue(pair.Key, out var seen) || Time.unscaledTime - seen.Time > PresenceTimeout ||
                    !BoltToolPolicy.CanOperate(seen.Pose.State) || seen.Pose.Rest != PlayerRest.None ||
                    (seen.Pose.Hands & PlayerHands.Tool) == 0) pair.Value.Reset();

            if (Time.unscaledTime < nextImpactEquipSend) return;
            nextImpactEquipSend = Time.unscaledTime + ImpactEquipHeartbeat;
            ulong equipped = impactTools.FirstOrDefault(value => value.Value.Tool != null && value.Value.Tool.isEquiped &&
                gameplay?.Player?.Get_ActiveTool == value.Value.Tool).Key;
            if (equipped != localImpactRequested) localImpactClaimStarted = Time.unscaledTime;
            if (equipped != 0 && impactEquipment.TryGetValue(equipped, out var selected))
            {
                var state = selected.Lease.State;
                bool conflict = state.Owner != ItemLease.Unowned && state.Owner != session.LocalPeerId;
                bool expiredClaim = state.Owner != session.LocalPeerId && Time.unscaledTime - localImpactClaimStarted > ImpactEquipClaimTimeout;
                var current = SceneGameplay;
                bool invalidPlayer = current == null || current.Get_IsDead || current.Get_IsSleeping ||
                    !BoltToolPolicy.CanOperate((byte)current.Get_PlayerState);
                bool blocked = impactActionsBlocked || MonMPPlugin.Instance?.Items?.TransfersBlocked == true;
                float reach = state.Owner == session.LocalPeerId ? ImpactEquipReach : MaxPeerRange;
                bool tooFar = current != null &&
                    (current.PlayerWalking.transform.position - selected.OriginalPosition).sqrMagnitude > reach * reach;
                if (invalidPlayer || conflict || expiredClaim || blocked || tooFar)
                { CancelLocalImpact(selected); equipped = 0; }
            }
            if (localImpactRequested != 0 && localImpactRequested != equipped && impactEquipment.TryGetValue(localImpactRequested, out var old))
                SendImpactEquipment(old, false);
            localImpactRequested = equipped;
            if (equipped != 0 && impactEquipment.TryGetValue(equipped, out var currentBinding))
                SendImpactEquipment(currentBinding, true);
        }

        internal bool TryPresentImpactGun(uint peer, Vector3 eyes, Quaternion cameraRotation,
            out Vector3 palm, out Quaternion palmRotation, out Vector3? leftPalm, out Quaternion? leftPalmRotation, out float heldWidth,
            Quaternion? bodyRotation = null)
        {
            palm = default; palmRotation = default;
            leftPalm = null; leftPalmRotation = null; heldWidth = 0;
            ImpactEquipmentBinding? binding = null;
            foreach (var value in impactEquipment.Values)
                if (value.Lease.State.Owner == peer) { binding = value; break; }
            if (disposed || !impactPresentationReady || channel?.FullySynchronized != true || session == null ||
                peer == session.LocalPeerId || binding == null || !impactTools.TryGetValue(binding.Id, out var scene) ||
                scene.Tool == null || scene.Tool.isEquiped || !presence.TryGetValue(peer, out var seen) ||
                Time.unscaledTime - seen.Time > PresenceTimeout || !BoltToolPolicy.CanOperate(seen.Pose.State) ||
                seen.Pose.Rest != PlayerRest.None || (seen.Pose.Hands & PlayerHands.Tool) == 0 ||
                (PeerPosition(seen.Pose) - binding.OriginalPosition).sqrMagnitude > ImpactEquipReach * ImpactEquipReach)
            { ResetImpactPresentation(peer); return false; }
            if (!impactVisuals.TryGetValue(peer, out var visual)) impactVisuals[peer] = visual = new RemoteSceneAirEquipmentVisual();

            Quaternion playerRotation = cameraRotation * playerFromCameraRotation;
            Vector3 playerPosition = eyes + cameraRotation * playerFromCameraPosition;
            Vector3 at = playerPosition + playerRotation * impactEquipPosition;
            Quaternion turn = playerRotation * impactEquipRotation;
            var grips = MonMPPlugin.Instance?.HeldGrips;
            if (grips != null && grips.TryGetVisualOffset(scene.Tool, binding.Shape, out var offset, out var extraTurn))
            { at += (bodyRotation ?? cameraRotation) * offset; turn *= extraTurn; }
            if (!visual.PresentImpact(scene.Tool, at, turn)) return false;
            palm = at; palmRotation = turn;
            if (grips != null && grips.TryGetRuntime(scene.Tool, binding.Shape, out var localPalm, out var localTurn, out heldWidth,
                out var localLeft, out var localLeftTurn))
            {
                palm = at + turn * Vector3.Scale(localPalm, scene.Tool.transform.lossyScale); palmRotation = turn * localTurn;
                if (localLeft.HasValue && localLeftTurn.HasValue)
                { leftPalm = at + turn * Vector3.Scale(localLeft.Value, scene.Tool.transform.lossyScale); leftPalmRotation = turn * localLeftTurn.Value; }
            }
            return true;
        }
        internal bool TryPresentImpactGun(uint peer, Vector3 eyes, Quaternion cameraRotation,
            out Vector3 palm, out Quaternion palmRotation)
            => TryPresentImpactGun(peer, eyes, cameraRotation, out palm, out palmRotation, out _, out _, out _);

        internal void ResetImpactPresentation(uint peer)
        { if (impactVisuals.TryGetValue(peer, out var visual)) visual.Reset(); }
        internal Transform? PresentedTransform(uint peer) =>
            impactVisuals.TryGetValue(peer, out var visual) ? visual.PresentedTransform : null;
        internal void TranslatePresentation(uint peer, Vector3 correction)
        { if (impactVisuals.TryGetValue(peer, out var visual)) visual.Translate(correction); }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values.Where(r => r.Adapter == BoltHoleState.Adapter))
            {
                if (impactEquipment.TryGetValue(record.Id, out var impact))
                {
                    SetImpactEquipmentState(impact, SceneImpactGunEquipmentState.Decode(record.Payload));
                    continue;
                }
                if (bindings.TryGetValue(record.Id, out var binding))
                {
                    var state = BoltHoleState.Decode(record.Payload);
                    if (binding.PurchasedVehicleEntity != 0 && !IsPurchasedNativeReady(binding)) QueuePurchasedApply(binding, state);
                    else
                    {
                        CapturePurchasedBaseline(binding);
                        Apply(binding, state);
                    }
                    continue;
                }
                if (record.Id <= WorldRecord.DynamicIdStart)
                    throw new InvalidDataException("Missing static bolt hole binding.");
            }
        }
        internal void RestoreMissingSocketRecords(Vehicle_PartSocket socket, IReadOnlyDictionary<ulong, WorldRecord>? incoming = null)
        {
            if (host || channel == null) return;
            foreach (var binding in bindings.Values.Where(b => ReferenceEquals(b.Hole.SockedBoltedByThisBolt, socket)))
                if (incoming?.ContainsKey(binding.Id) != true && channel.Records.TryGetValue(binding.Id, out var record))
                {
                    var state = BoltHoleState.Decode(record.Payload);
                    if (binding.PurchasedVehicleEntity != 0 && !IsPurchasedNativeReady(binding)) QueuePurchasedApply(binding, state);
                    else
                    {
                        CapturePurchasedBaseline(binding);
                        Apply(binding, state);
                    }
                }
        }
        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != BoltHoleState.Adapter) return;
            if (impactEquipment.TryGetValue(record.Id, out var impact))
            {
                SetImpactEquipmentState(impact, SceneImpactGunEquipmentState.Decode(record.Payload));
                return;
            }
            if (!bindings.TryGetValue(record.Id, out var b))
            {
                if (record.Id > WorldRecord.DynamicIdStart) return;
                throw new InvalidDataException("Missing static bolt hole binding.");
            }
            var hole = b.Hole;
            bool wasInstalled = hole.isBoltInstalled;
            float from = boltShown.TryGetValue(hole, out var shown) ? shown : hole.BoltedPercent;
            var state = BoltHoleState.Decode(record.Payload);
            if (b.PurchasedVehicleEntity != 0 && !IsPurchasedNativeReady(b))
            {
                QueuePurchasedApply(b, state);
                return;
            }
            CapturePurchasedBaseline(b);
            Apply(b, state);
            // A wrench on another copy arrives as small steps at the publish rate: ease the bolt to each
            // step and turn it as the native wrench does, instead of jumping without turning (#133).
            if (!boltPredictions.ContainsKey(b.Id) && wasInstalled && hole.isBoltInstalled && Mathf.Abs(hole.BoltedPercent - from) is float step && step > 0.001f && step < 0.3f)
            {
                boltShown[hole] = from;
                ShowBolt(hole, from);
            }
            else boltShown.Remove(hole);
            ShowPercent(hole);
        }
        private readonly Dictionary<BoltHoleDetails, float> boltShown = new Dictionary<BoltHoleDetails, float>();
        private static void ShowBolt(BoltHoleDetails hole, float percent)
        {
            var visual = hole.generated_BoltHole?.boltGameObject;
            if (visual != null) visual.transform.localPosition = Vector3.Lerp(hole.generated_BoltHole!.hardware_Bolt.OffsetWhenUnbolted, Vector3.zero, percent);
        }
        private void AnimateBolts()
        {
            if (boltShown.Count == 0) return;
            foreach (var hole in boltShown.Keys.ToArray())
            {
                var visual = hole.generated_BoltHole?.boltGameObject;
                if (visual == null || !hole.isBoltInstalled) { boltShown.Remove(hole); continue; }
                float shown = boltShown[hole], target = Mathf.Clamp01(hole.BoltedPercent);
                float next = Mathf.Lerp(shown, target, 1 - Mathf.Exp(-Time.deltaTime * 12f));
                if (Mathf.Abs(target - next) < 0.002f) next = target;
                ShowBolt(hole, next);
                if (next != shown) visual.transform.Rotate(Vector3.up * ((target > shown ? 540f : -540f) * Time.deltaTime), Space.Self);
                if (next == target) boltShown.Remove(hole); else boltShown[hole] = next;
            }
        }

        private void UpdateBoltPredictions()
        {
            foreach (var pair in boltPredictions.ToArray())
            {
                if (!bindings.TryGetValue(pair.Key, out var binding)) { boltPredictions.Remove(pair.Key); continue; }
                var hole = binding.Hole;
                if (channel?.FullySynchronized != true || MonMPPlugin.Instance?.Items?.TransfersBlocked != false ||
                    !hole.isBoltInstalled || hole.SockedBoltedByThisBolt?.isInstalled != true || !NativeInstalledBoltAttached(hole))
                {
                    boltPredictions.Remove(pair.Key);
                    if (hole.isBoltInstalled) ShowBolt(hole, hole.BoltedPercent);
                    continue;
                }
                var prediction = pair.Value;
                float before = prediction.Shown;
                if (clientHold?.Bolt == pair.Key)
                    prediction.Advance(hole.BoltedPercent, clientHold.Kind, clientHold.Direction, Time.deltaTime);
                else if (prediction.Reconcile(hole.BoltedPercent, Time.deltaTime)) boltPredictions.Remove(pair.Key);
                boltShown.Remove(hole);
                ShowBolt(hole, prediction.Shown);
                if (prediction.Shown != before)
                    hole.generated_BoltHole.boltGameObject.transform.Rotate(Vector3.up *
                        ((prediction.Shown > before ? 540f : -540f) * Time.deltaTime), Space.Self);
                ShowPercent(hole);
            }
        }

#if MONMP_DEVELOPMENT
        internal int TestAnimatingBolts => boltShown.Count;


        internal Quaternion? TestBoltTurn(ulong id) => bindings[id].Hole.generated_BoltHole?.boltGameObject?.transform.localRotation;
#endif

        private void Apply(Binding b, BoltHoleState state)
        {
            // Receipts can be sent before their queued world delta. Until
            // that revision arrives, an older delta must not erase a confirmed insertion.
            if (!host && !disposed && MountReplyPending(b) && mountReplyStates.TryGetValue(b.Id, out var receipt)) state = receipt;
            else mountReplyStates.Remove(b.Id);
            b.State = state;
            if (!host && !disposed && nativeRecheckIds.Add(b.Id)) nativeRechecks.Enqueue(b.Id);
            // A canonical update can precede the actor's definitive mount reply. Keep its
            // native input/output until that operation settles, including a pack adoption.
            if (!host && !disposed && (pendingMounts.ContainsKey(b.Id) || deferredInstalls.ContainsKey(b.Id))) return;
            if (!NativeHoleReady(b.Hole)) return;
            // Bolt records can arrive before their part, or outlive its removal. Native
            // InitializeSpawnBolts only places bolts in an installed socket.
            var native = state.Installed && b.Hole.SockedBoltedByThisBolt?.isInstalled == false
                ? new BoltHoleState(false, 0) : state;
            if (Capture(b.Hole).Equals(native) && (native.Installed
                ? NativeInstalledBoltAttached(b.Hole)
                : b.Hole.BoltedPercent == 0 && b.Hole.generated_BoltHole?.hardware_Bolt == null &&
                    b.Hole.generated_BoltHole?.boltGameObject == null && !HasAttachedBolt(b.Hole)))
            { RefreshLocalHoleInteraction(b.Hole); return; }
            ApplyToHole(b.Hole, native);
            RefreshLocalHoleInteraction(b.Hole);
        }
        private void RefreshLocalHoleInteraction(BoltHoleDetails hole)
        {
            if (host || disposed || hole.generatedCollider == null) return;
            var player = SceneGameplay?.Player;
            if (player == null) return;
            // Native grab/equip enables these once; rollback disables them again.
            bool editing = player.Get_HoldingItem is Hardware_Bolt || player.Get_ActiveTool is Tool_AdjustableSpanner ||
                player.Get_ActiveTool is Tool_Air_Impact;
            hole.generatedCollider.enabled = editing && hole.SockedBoltedByThisBolt?.isInstalled == true;
        }
        private static bool NativeInstalledBoltAttached(BoltHoleDetails hole)
        {
            var generated = hole.generated_BoltHole;
            var bolt = generated?.hardware_Bolt;
            return hole.isBoltInstalled && bolt != null && bolt.isPlaced && bolt.transform.parent == hole.HoleLocation &&
                generated!.boltGameObject == bolt.gameObject;
        }
        private static bool NativeHoleReady(BoltHoleDetails hole) => hole.HoleLocation != null &&
            hole.generated_BoltHole != null && hole.HoleLocation.gameObject.scene.IsValid() &&
            hole.HoleLocation.gameObject.scene.isLoaded;

        private static bool HasAttachedBolt(BoltHoleDetails hole)
        {
            if (hole.HoleLocation == null) return false;
            for (int i = 0; i < hole.HoleLocation.childCount; i++)
                if (hole.HoleLocation.GetChild(i).GetComponent<Hardware_Bolt>() != null) return true;
            return false;
        }

        private void ReconcileNativeHoles()
        {
            // Native Start/load callbacks can run after the canonical record was applied.
            // Rotate a fixed budget over only known records, rather than scan every hole
            // each frame. Apply preserves live mount inputs/outputs and loose shared bolts.
            if (host || Time.unscaledTime < nextNativeRecheck || nativeRechecks.Count == 0) return;
            nextNativeRecheck = Time.unscaledTime + 0.1f;
            int count = Math.Min(32, nativeRechecks.Count);
            for (int i = 0; i < count; i++)
            {
                ulong id = nativeRechecks.Dequeue();
                if (!bindings.TryGetValue(id, out var binding)) { nativeRecheckIds.Remove(id); continue; }
                Apply(binding, binding.State);
                nativeRechecks.Enqueue(id);
            }
        }
        private static void CapturePurchasedBaseline(Binding binding)
        {
            if (binding.PurchasedVehicleEntity == 0 || binding.OriginalCaptured) return;
            binding.Original = Capture(binding.Hole);
            binding.State = binding.Original;
            binding.OriginalCaptured = true;
        }
        private void ApplyToHole(BoltHoleDetails hole, BoltHoleState state)
        {
            bool previous = applying; applying = true;
            try { ApplyToHoleNative(hole, state); }
            finally { applying = previous; }
        }
        private void ApplyToHoleNative(BoltHoleDetails hole, BoltHoleState state)
        {
            RestoreNativeHole(hole, state);
        }
        // Socket.InstallPart clears flags without constructing bolts. A fully tightened part
        // record must restore actual native bolt objects, not only the flags used by Capture.
        internal static void RestoreNativeHole(BoltHoleDetails hole, BoltHoleState state)
        {
            if (!state.Installed)
            {
                // InstallPart and Unscrewed clear flags while retaining these references.
                // Retire only attached hole copies: Detach leaves the real loose output
                // referenced here too, and that output must keep its shared item/lease.
                var generated = hole.generated_BoltHole;
                var hardware = generated?.hardware_Bolt;
                var visual = generated?.boltGameObject;
                if (hole.HoleLocation != null)
                {
                    if (hardware != null && hardware.transform.parent == hole.HoleLocation)
                    {
                        hardware.gameObject.SetActive(false);
                        UnityEngine.Object.Destroy(hardware.gameObject);
                    }
                    if (visual != null && (hardware == null || visual != hardware.gameObject) &&
                        visual.transform.parent == hole.HoleLocation)
                    {
                        visual.SetActive(false);
                        UnityEngine.Object.Destroy(visual);
                    }
                    // A later Attach may have overwritten the generated references. Only
                    // direct bolt children belong to this empty hole; loose outputs do not.
                    for (int i = hole.HoleLocation.childCount - 1; i >= 0; i--)
                    {
                        var attached = hole.HoleLocation.GetChild(i).GetComponent<Hardware_Bolt>();
                        if (attached == null) continue;
                        attached.gameObject.SetActive(false);
                        UnityEngine.Object.Destroy(attached.gameObject);
                    }
                }
                if (generated != null) { generated.hardware_Bolt = null; generated.boltGameObject = null; }
                hole.isBoltInstalled = false;
                hole.BoltedPercent = 0;
                if (hole.generatedCollider != null) hole.generatedCollider.enabled = false;
                return;
            }
            if (state.Installed && !NativeInstalledBoltAttached(hole))
            {
                bool interactive = hole.generatedCollider?.enabled == true;
                // Retire attached ghosts while preserving any real loose output.
                RestoreNativeHole(hole, new BoltHoleState(false, 0));
                var clone = UnityEngine.Object.Instantiate(Singleton<GameAssets>.i.Hardware_Bolt(hole.boltSize), hole.HoleLocation).GetComponent<Hardware_Bolt>();
                // Native 12 mm Start defaults fresh bolts to rusty. This is an existing
                // host bolt, so retain its declared color when Start runs after Attach.
                clone.isSpawnFromTheSaveLoadSystem = true;
                clone.IntValue1 = state.Color;
                clone.itemCollider = clone.GetComponent<Collider>();
                hole.Attach(clone, state.Percent / 100f);
                clone.SetBoltColor();
                if (hole.generatedCollider != null) hole.generatedCollider.enabled = interactive;
            }
            else if (state.Installed)
            {
                var bolt = hole.generated_BoltHole!.hardware_Bolt;
                if (bolt.IntValue1 != state.Color)
                {
                    bolt.isSpawnFromTheSaveLoadSystem = true;
                    bolt.IntValue1 = state.Color;
                    if (bolt.itemCollider == null) bolt.itemCollider = bolt.GetComponent<Collider>();
                    bolt.SetBoltColor();
                }
                hole.BoltedPercent = state.Percent / 100f;
                var visual = hole.generated_BoltHole?.boltGameObject;
                if (visual != null) visual.transform.localPosition = Vector3.Lerp(hole.generated_BoltHole!.hardware_Bolt.OffsetWhenUnbolted, Vector3.zero, hole.BoltedPercent);
                if (state.Percent == 100) hole.SockedBoltedByThisBolt.OneBoltFullyTighten();
            }
        }
        public void Update()
        {
            if (disposed) return;
            RetryPurchasedCatalogs();
            if (channel?.Ready != true) return;
            UpdatePendingMounts();
            if (!host && deferredInstalls.Count != 0)
                foreach (var id in deferredInstalls.Keys.ToArray()) TryDeferredInstall(bindings[id]);
            ReconcileNativeHoles();
            UpdateImpactEquipment();
            if (host) TickHolds(); else { UpdateClientHold(); AnimateBolts(); UpdateBoltPredictions(); }
            if (!host || Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 0.1f;
            foreach (var b in bindings.Values)
            {
                var state = Capture(b.Hole);
                if (state.Equals(b.State)) continue;
                b.State = state;
                channel!.Publish(b.Id, state.Encode());
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            if (!host) EndClientHold();
            FlushLocalOwnership();
            foreach (var binding in impactEquipment.Values) CancelLocalImpact(binding);
            if (host)
                foreach (var binding in impactEquipment.Values.Where(value => value.Lease.State.Owner != ItemLease.Unowned &&
                    value.Lease.State.Owner != session?.LocalPeerId).ToArray()) RevokeImpactEquipment(binding);
            disposed = true;
            if (session != null) { session.PoseReceived -= PoseReceived; session.PeerLeft -= PeerLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; channel.CommandRejected -= MountRejected; channel.ReplyReceived -= MountReply; }
            purchasedVehicles.Clear(); deferredPurchasedApply.Clear(); nativeRechecks.Clear(); nativeRecheckIds.Clear();
            foreach (var pair in pendingMounts)
            {
                var hole = bindings[pair.Key].Hole;
                if (hole.generated_BoltHole?.hardware_Bolt == pair.Value.Bolt) { hole.isBoltInstalled = false; hole.BoltedPercent = 0; }
                MonMPPlugin.Instance?.Items?.DiscardLocalBolt(pair.Value.Bolt);
            }
            pendingMounts.Clear(); mountReplyRevisions.Clear(); mountReplyStates.Clear(); mountResults.Clear(); boltPredictions.Clear();
            if (!host)
                foreach (var b in bindings.Values)
                    RestoreGuestBaseline(b);
            foreach (var visual in impactVisuals.Values) visual.Dispose();
            impactVisuals.Clear(); impactEquipment.Clear(); impactTools.Clear(); impactIds.Clear();
            activeHolds.Clear(); presence.Clear(); budgets.Clear(); mountBudgets.Clear(); mountRefusals.Clear(); operations.Clear(); clientHold = null;
            channel = null; session = null;
        }
        private void RestoreGuestBaseline(Binding b)
        {
            // Matching flags are insufficient when a bolt/visual was lost or left behind.
            // Reuse Apply's representation check only with a live generated hole;
            // equal-state scene teardown must not create a replacement object.
            if (b.OriginalCaptured && b.Hole != null && (!Capture(b.Hole).Equals(b.Original) ||
                b.Hole.HoleLocation != null && b.Hole.generated_BoltHole != null &&
                b.Hole.HoleLocation.gameObject.scene.IsValid() && b.Hole.HoleLocation.gameObject.scene.isLoaded))
                Apply(b, b.Original);
        }
        // A bolt this guest spawned itself, never shared, pushed into an empty hole as a hand install would.

#if MONMP_DEVELOPMENT
        internal void TestAttachUnsharedBolt(ulong id)
        {
            var hole = bindings[id].Hole;
            var bolt = UnityEngine.Object.Instantiate(Singleton<GameAssets>.i.Hardware_Bolt(hole.boltSize), hole.HoleLocation.position, Quaternion.identity).GetComponent<Hardware_Bolt>();
            bolt.CreateBasics();
            hole.Attach(bolt, 0);
        }


        internal bool TestInstallSettled(ulong id) => !deferredInstalls.ContainsKey(id) && !pendingMounts.ContainsKey(id);


        internal bool TestLocallyInstalled(ulong id) => bindings[id].Hole.isBoltInstalled;


        internal ulong TestAnyEmptyHole(Vehicle_Type vehicle = Vehicle_Type.MainVehicle) => TestEmptyHoles(1, vehicle)[0];


        internal void TestMountRequest(ulong id, BoltMountCommand request) => channel!.Request(id, request.Encode());


        internal void TestForgedState(ulong id) => channel!.Request(id, new BoltHoleState(true, 100).Encode());
#endif

        // Deterministically ordered by binding id, so host and guest independently agree on the same holes.

#if MONMP_DEVELOPMENT
        internal ulong[] TestEmptyHoles(int count, Vehicle_Type vehicle = Vehicle_Type.MainVehicle)
        {
            var found = bindings.Values.Where(b => b.Type == vehicle && !b.Hole.isBoltInstalled).OrderBy(b => b.Id).Take(count).Select(b => b.Id).ToArray();
            if (found.Length < count) throw new InvalidOperationException("Only " + found.Length + " empty bolt holes found among " + bindings.Count + "; need " + count + ".");
            return found;
        }
#endif

        // Purely a function of the fixed, never-changing binding set (not current install state), so
        // host and guest agree on the exact same holes no matter which side has already acted.

#if MONMP_DEVELOPMENT
        internal ulong[] TestFirstHoles(int count, Vehicle_Type vehicle = Vehicle_Type.MainVehicle) => bindings.Values.Where(b => b.Type == vehicle).Select(b => b.Id).OrderBy(id => id).Take(count).ToArray();


        internal int TestCount(Vehicle_Type vehicle) => bindings.Values.Count(b => b.Type == vehicle);


        internal BoltHoleState TestOriginal(ulong id) => bindings[id].Original;


        internal BoltHoleState TestState(ulong id) => bindings[id].State;


        internal BoltHoleState TestNative(ulong id) => Capture(bindings[id].Hole);


        internal BoltHoleState TestRecord(ulong id) => BoltHoleState.Decode(channel!.Records[id].Payload);
#endif

        // Mirrors what a real wrench does the instant a bolt is placed: attach a fresh bolt at a low percent.

#if MONMP_DEVELOPMENT
        internal void TestInstall(ulong id)
        {
            var hole = bindings[id].Hole;
            var clone = UnityEngine.Object.Instantiate(Singleton<GameAssets>.i.Hardware_Bolt(hole.boltSize), hole.HoleLocation).GetComponent<Hardware_Bolt>();
            hole.Attach(clone, 0.01f);
        }
#endif

        // Mirrors continuous wrench/impact-gun progress; firing the same native completion call at 100%.

#if MONMP_DEVELOPMENT
        internal void TestTighten(ulong id, int percent)
        {
            var hole = bindings[id].Hole;
            hole.BoltedPercent = Mathf.Clamp01(percent / 100f);
            if (percent >= 100) hole.SockedBoltedByThisBolt.OneBoltFullyTighten();
        }


        internal void TestUnscrew(ulong id) => bindings[id].Hole.Unscrewed();


        internal BoltHoleDetails TestHole(ulong id) => bindings[id].Hole;


        internal void TestClear(ulong id) { var hole = bindings[id].Hole; if (hole.isBoltInstalled) hole.DetachAndDestroy(); }
#endif

    }
}
