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
using NWH.DWP2.ShipController;
using NWH.VehiclePhysics2;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // Placement, cable length and crank timing belong to the host. The vehicle's current
    // simulator applies the pull; every other copy only presents the attachment.
    internal sealed class SharedHoistPullers : IDisposable
    {
        private sealed class VehicleBinding
        {
            public ulong Id;
            public ulong PurchasedVehicleEntity;
            public Vehicle Vehicle = null!;
        }

        private sealed class Binding
        {
            public ulong Id;
            public ulong PurchasedVehicleEntity = 0;
            public int WaitThroughFrame = -1;
            public bool OriginalCaptured = true;
            public HoistPullerState? PendingState = null;
            public Tool_HoistPuller Tool = null!;
            public Tool_HoistPuller_Handle Handle = null!;
            public HoistPullerState State;
            public HoistPullerState OriginalState;
            public bool OriginalGroundPlaced;
            public bool OriginalAttachmentActive;
            public Rigidbody? OriginalCarBody;
            public Vector3 OriginalGroundAnchor, OriginalCarAnchor;
            public float OriginalCrankTimer;
            public bool OriginalHandleEnabled, OriginalHandleColliderEnabled;
            public bool OriginalEquipped;
            public Transform? OriginalPlayerHand;
            public Transform? OriginalTransformParent;
            public Vector3 OriginalLocalPosition;
            public Quaternion OriginalLocalRotation;
            public bool OriginalCarPlaced;
            public float OriginalMinRange, OriginalMaxRange;
            public float LastObservedTimer;
            public uint CrankSequence;
        }

        private static readonly FieldInfo HandleField = AccessTools.Field(typeof(Tool_HoistPuller), "hoistPuller_Handle");
        private static readonly FieldInfo CrankTimerField = AccessTools.Field(typeof(Tool_HoistPuller), "Timer_LastHoist");
        private static readonly FieldInfo GroundPlacedField = AccessTools.Field(typeof(Tool_HoistPuller), "_isGroundPlaced");
        private static readonly FieldInfo? CarPlacedField = AccessTools.Field(typeof(Tool_HoistPuller), "isCarPlaced");
        private static readonly FieldInfo MinRangeField = AccessTools.Field(typeof(Tool_HoistPuller), "MinRange");
        private static readonly FieldInfo MaxRangeField = AccessTools.Field(typeof(Tool_HoistPuller), "MaxRange");
        private static readonly FieldInfo MinForceField = AccessTools.Field(typeof(Tool_HoistPuller), "MinForce");
        private static readonly FieldInfo MaxForceField = AccessTools.Field(typeof(Tool_HoistPuller), "MaxForce");
        private static readonly FieldInfo ForceModeField = AccessTools.Field(typeof(Tool_HoistPuller), "ForceMode");
        private static readonly FieldInfo? OriginalParentField = AccessTools.Field(typeof(Tool_HoistPuller), "OriginalParent");
        private static readonly FieldInfo? OriginalPositionField = AccessTools.Field(typeof(Tool_HoistPuller), "OriginalPosition");
        private static readonly FieldInfo? OriginalRotationField = AccessTools.Field(typeof(Tool_HoistPuller), "OriginalRotation");
        private static readonly FieldInfo? BoxToolField = AccessTools.Field(typeof(Vehicle_HoistPullerBox), "tool_HoistPuller");
        private const float ClaimSeconds = 180f;
        // Host: the guest using each hoist it took from its case (#116).
        private readonly Dictionary<ulong, (uint Peer, float Since)> claims = new Dictionary<ulong, (uint, float)>();
        private readonly List<ulong> expiredClaims = new List<ulong>();
        // Guest: the hoist this player holds and is aiming, and the one it asked the host for.
        private Binding? localHold, pendingClaim;
        private Binding? pendingAttach, returnToHands;
        private HoistPullerPlacement pendingAttachment;
        private bool pendingAttachGranted;
        private float pendingAttachSince;
        private int returnAfterFrame;
        private float nextClaimRefresh;
        private HoistPullerPlacementKind pendingPickupKind;
        private readonly HoistClaimPending pendingClaimRequest = new HoistClaimPending();
        private uint nextPlacementOperation;
        private readonly bool host;
        private readonly Gameplay gameplay;
        private readonly NativeEntityRegistry identities;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Tool_HoistPuller, Binding> byTool = new Dictionary<Tool_HoistPuller, Binding>();
        private readonly Dictionary<Tool_HoistPuller_Handle, Binding> byHandle = new Dictionary<Tool_HoistPuller_Handle, Binding>();
        private readonly Dictionary<ulong, VehicleBinding> vehicles = new Dictionary<ulong, VehicleBinding>();
        private readonly Dictionary<Rigidbody, VehicleBinding> byBody = new Dictionary<Rigidbody, VehicleBinding>();
        private readonly List<Binding> retiredPurchasedTools = new List<Binding>();
        private readonly List<VehicleBinding> retiredPurchasedVehicles = new List<VehicleBinding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, float> nextCrankRequest = new Dictionary<uint, float>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool disposed;
        private float nextCapture;

        internal const float CaptureInterval = 0.05f;
        internal static SharedHoistPullers? Current { get; private set; }
        internal bool NewActionsBlocked { get; set; }
        internal bool TransactionsSettled => !pendingClaimRequest.HasPending && pendingAttach == null;
        internal IEnumerable<ulong> RequiredCatalogEntityIds => bindings.Values
            .Where(binding => binding.PurchasedVehicleEntity == 0 && binding.Id > WorldRecord.DynamicIdStart)
            .Select(binding => binding.Id);

#if MONMP_DEVELOPMENT
        internal int TestAcceptedCommands { get; private set; }


        internal int TestRejectedCommands { get; private set; }
#endif


        private readonly AimSegmentTest checkSegment;

        internal SharedHoistPullers(AimSegmentTest checkSegment, bool host, NativeEntityRegistry identities, Gameplay gameplay,
            IEnumerable<(ulong Entity, Vehicle_Type Type, GameObject Instance)>? purchasedVehicles = null)
        {
            this.checkSegment = checkSegment;
            this.host = host;
            this.gameplay = gameplay != null ? gameplay : throw new ArgumentNullException(nameof(gameplay));
            this.identities = identities ?? throw new ArgumentNullException(nameof(identities));
            if (HandleField?.FieldType != typeof(Tool_HoistPuller_Handle) || CrankTimerField?.FieldType != typeof(float) ||
                GroundPlacedField?.FieldType != typeof(bool) || CarPlacedField?.FieldType != typeof(bool) ||
                MinRangeField?.FieldType != typeof(float) || MaxRangeField?.FieldType != typeof(float) ||
                MinForceField?.FieldType != typeof(float) || MaxForceField?.FieldType != typeof(float) ||
                ForceModeField?.FieldType != typeof(ForceMode))
                throw new InvalidOperationException("Unsupported native hoist puller field layout.");
            if (Current != null) throw new InvalidOperationException("A shared hoist puller adapter is already active.");

            // Resolve loaded sale clones before capturing an already-attached native hoist.
            // Dynamic purchases and late-join clones use this same sale-root identity.
            var initialPurchases = purchasedVehicles?.ToArray() ?? Array.Empty<(ulong Entity, Vehicle_Type Type, GameObject Instance)>();
            foreach (var purchase in initialPurchases)
                RegisterPurchasedTarget(purchase.Entity, purchase.Type, purchase.Instance);

            foreach (var vehicle in Object.FindObjectsOfType<Vehicle>(true)
                .Where(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.scene.isLoaded &&
                    IsSupportedVehicle(value) && SharedVehicleSales.Active?.OwnsSaleClone(value.transform) != true)
                .OrderBy(value => RuntimeProbe.CanonicalPath(value.transform), StringComparer.Ordinal))
            {
                if (vehicles.Values.Any(value => value.Vehicle == vehicle)) continue;
                string identity = RuntimeProbe.CanonicalPath(vehicle.transform) + "/hoist-puller-vehicle-v1";
                ulong id = StaticId.Hash(identity);
                var binding = new VehicleBinding { Id = id, Vehicle = vehicle };
                if (vehicles.TryGetValue(id, out var existing) && existing.Vehicle != vehicle)
                    throw new InvalidOperationException("Duplicate vehicle identity for hoist puller: " + identity);
                vehicles[id] = binding;
                if (vehicle.vehicleRigidbody != null) byBody[vehicle.vehicleRigidbody] = binding;
            }

            foreach (var purchase in initialPurchases)
                RegisterPurchasedVehicle(purchase.Entity, purchase.Type, purchase.Instance);

            var tools = Object.FindObjectsOfType<Tool_HoistPuller>(true)
                .Where(value => value != null && value.gameObject.scene.IsValid() &&
                    !byTool.ContainsKey(value) && !IsSaleOwnedTool(value))
                .Select(value => (Tool: value, Path: RuntimeProbe.CanonicalPath(value.transform),
                    Index: Array.IndexOf(value.GetComponents<Tool_HoistPuller>(), value)))
                .Where(value => value.Index >= 0)
                .OrderBy(value => value.Path, StringComparer.Ordinal).ThenBy(value => value.Index).ToArray();

            foreach (var candidate in tools)
            {
                string identity = candidate.Path + "/hoist-puller-state-v1/" + candidate.Index;
                ulong id = StaticId.Hash(identity);
                RegisterTool(id, identity, candidate.Tool, 0);
            }
            Current = this;
        }

        // The native hoist accepts NWH.Vehicle. Preserve its existing vehicle-physics targets and
        // add only the two stock boats already owned by SharedBoatMotion.
        internal static bool IsSupportedVehicle(Vehicle vehicle) => vehicle is VehicleController ||
            vehicle is AdvancedShipController boat &&
            (boat.vehicle_type == Vehicle_Type.SmallCruiser || boat.vehicle_type == Vehicle_Type.FishingBoat);

        internal bool RegisterPurchasedVehicle(ulong entity, Vehicle_Type type, GameObject instance)
        {
            if (!RegisterPurchasedTarget(entity, type, instance)) return false;
            RegisterPurchasedTools(entity, type, instance);
            return true;
        }

        private bool RegisterPurchasedTarget(ulong entity, Vehicle_Type type, GameObject instance)
        {
            if (disposed || type != Vehicle_Type.OlTruck && type != Vehicle_Type.SmollATV) return false;
            if (entity <= WorldRecord.DynamicIdStart || instance == null || !instance.scene.IsValid())
                throw new InvalidOperationException("Purchased hoist target must be a scene-backed sale entity.");
            var controllers = instance.GetComponentsInChildren<VehicleController>(true);
            if (controllers.Length != 1 || controllers[0] == null || controllers[0].vehicle_type != type)
                throw new InvalidOperationException("Purchased hoist target does not match its sale vehicle.");
            var vehicle = controllers[0];
            string identity = "monmp/purchased-hoist-target/v1/" + entity.ToString("X16") + "/" + type;
            ulong id = StaticId.Hash(identity) | WorldRecord.DynamicIdStart;
            if (vehicles.TryGetValue(id, out var binding))
            {
                if (binding.Vehicle != vehicle || binding.PurchasedVehicleEntity != entity)
                {
                    if (binding.Vehicle != null || binding.PurchasedVehicleEntity != entity)
                        throw new InvalidOperationException("Conflicting purchased hoist target identity.");
                    foreach (var body in byBody.Where(pair => pair.Value == binding).Select(pair => pair.Key).ToArray()) byBody.Remove(body);
                    binding.Vehicle = vehicle;
                }
            }
            else
            {
                if (vehicles.Values.Any(value => value.Vehicle == vehicle || value.PurchasedVehicleEntity == entity))
                    throw new InvalidOperationException("Purchased hoist target already has a different identity.");
                binding = new VehicleBinding { Id = id, Vehicle = vehicle, PurchasedVehicleEntity = entity };
                vehicles.Add(id, binding);
            }
            if (vehicle.vehicleRigidbody != null)
            {
                if (byBody.TryGetValue(vehicle.vehicleRigidbody, out var bodyBinding) && bodyBinding != binding)
                    throw new InvalidOperationException("Purchased hoist target rigidbody already has a different identity.");
                byBody[vehicle.vehicleRigidbody] = binding;
            }
            return true;
        }

        private static bool IsSaleOwnedTool(Tool_HoistPuller tool)
        {
            var sales = SharedVehicleSales.Active;
            if (sales == null) return false;
            if (sales.OwnsSaleClone(tool.transform)) return true;
            return OriginalParentField?.GetValue(tool) is Transform parent && sales.OwnsSaleClone(parent);
        }

        private void RegisterPurchasedTools(ulong entity, Vehicle_Type type, GameObject instance)
        {
            // The serialized case is a stable anchor even while the host holds or places its
            // tool outside the clone hierarchy. Never derive a purchased ID from a scene path.
            var anchored = new Dictionary<Tool_HoistPuller, string>();
            foreach (var box in instance.GetComponentsInChildren<Vehicle_HoistPullerBox>(true))
            {
                if (box == null || BoxToolField?.GetValue(box) is not Tool_HoistPuller tool || tool == null) continue;
                if (!PurchasedVehiclePartIdentityPolicy.TryRelativePath(instance.transform, box.transform, out string path))
                    throw new InvalidOperationException("Purchased hoist case is outside its sale clone.");
                string anchor = path + "/case-tool";
                if (anchored.TryGetValue(tool, out var previous) && previous != anchor)
                    throw new InvalidOperationException("Purchased hoist tool belongs to multiple cases.");
                anchored[tool] = anchor;
            }
            foreach (var tool in instance.GetComponentsInChildren<Tool_HoistPuller>(true))
            {
                if (tool == null || anchored.ContainsKey(tool)) continue;
                if (!PurchasedVehiclePartIdentityPolicy.TryRelativePath(instance.transform, tool.transform, out string path))
                    throw new InvalidOperationException("Purchased hoist tool is outside its sale clone.");
                anchored.Add(tool, path);
            }
            foreach (var pair in anchored.OrderBy(value => value.Value, StringComparer.Ordinal))
            {
                int index = Array.IndexOf(pair.Key.GetComponents<Tool_HoistPuller>(), pair.Key);
                if (index < 0) throw new InvalidOperationException("Purchased hoist tool has no native component identity.");
                string identity = "monmp/purchased-hoist-tool/v1/" + entity.ToString("X16") + "/" + (int)type + "/" + pair.Value + "/" + index;
                ulong id = WorldRecord.DynamicIdStart | Math.Max(1UL, StaticId.Hash(identity) & 0x7FFFFFFFFFFFFFFFUL);
                RegisterTool(id, identity, pair.Key, entity);
            }
        }

        private void RegisterTool(ulong id, string identity, Tool_HoistPuller tool, ulong purchase)
        {
            if (bindings.TryGetValue(id, out var existing))
            {
                if (ReferenceEquals(existing.Tool, tool))
                {
                    if (!host && purchase != 0 && channel?.Records.TryGetValue(id, out var retained) == true)
                        Apply(existing, HoistPullerState.Decode(retained.Payload), true);
                    return;
                }
                if (existing.Tool != null || existing.PurchasedVehicleEntity != purchase)
                    throw new InvalidOperationException("Conflicting hoist tool identity: " + identity);
                // Unity-null still carries the managed dictionary key until native teardown.
                byTool.Remove(existing.Tool!); byHandle.Remove(existing.Handle); bindings.Remove(id); identities.Unbind(id);
                claims.Remove(id);
                if (localHold == existing) localHold = null;
                if (pendingClaim == existing) { pendingClaim = null; pendingClaimRequest.Clear(); }
            }
            if (byTool.ContainsKey(tool)) throw new InvalidOperationException("Hoist tool already has a different shared identity.");
            var handle = HandleField.GetValue(tool) as Tool_HoistPuller_Handle;
            if (handle == null || byHandle.ContainsKey(handle)) throw new InvalidOperationException("Hoist tool has no unique serialized handle: " + identity);
            var binding = new Binding { Id = id, PurchasedVehicleEntity = purchase, Tool = tool, Handle = handle,
                WaitThroughFrame = Time.frameCount, OriginalCaptured = purchase == 0 };
            if (binding.OriginalCaptured) CaptureOriginal(binding);
            binding.State = Capture(binding); binding.OriginalState = binding.State;
            binding.LastObservedTimer = binding.State.CrankTimer;
            identities.Bind(id, HoistPullerState.Adapter, tool.gameObject, identity, true, EntityPersistence.SessionOnly);
            bindings.Add(id, binding); byTool.Add(tool, binding); byHandle.Add(handle, binding);
            if (purchase == 0 || channel == null) return;
            if (channel.Records.TryGetValue(id, out var record))
            {
                if (record.Adapter != HoistPullerState.Adapter) throw new InvalidDataException("Purchased hoist record has a different adapter.");
                Apply(binding, HoistPullerState.Decode(record.Payload), true);
            }
            else if (host) channel.Register(new WorldRecord(id, 1, HoistPullerState.Adapter, binding.State.Encode()));
        }

        private bool CapturePurchasedBaseline(Binding binding)
        {
            if (binding.OriginalCaptured) return true;
            if (binding.Tool == null || Time.frameCount <= binding.WaitThroughFrame ||
                OriginalParentField?.GetValue(binding.Tool) is not Transform) return false;
            CaptureOriginal(binding);
            binding.OriginalCaptured = true;
            return true;
        }

        internal void ReplayPurchasedRecords()
        {
            foreach (var binding in bindings.Values)
            {
                if (!CapturePurchasedBaseline(binding)) continue;
                if (!binding.PendingState.HasValue) continue;
                var state = binding.PendingState.Value;
                if (channel?.Records.TryGetValue(binding.Id, out var record) == true)
                    state = HoistPullerState.Decode(record.Payload);
                Apply(binding, state, true);
            }
        }

        private void RemoveDeadPurchasedBindings()
        {
            // Optional ledger records contain only bytes and may remain available for a
            // later reconstruction. Native ownership must end with this particular clone.
            retiredPurchasedTools.Clear();
            foreach (var binding in bindings.Values)
                if (binding.PurchasedVehicleEntity != 0 && binding.Tool == null) retiredPurchasedTools.Add(binding);
            foreach (var binding in retiredPurchasedTools)
            {
                byTool.Remove(binding.Tool!); byHandle.Remove(binding.Handle); bindings.Remove(binding.Id); identities.Unbind(binding.Id);
                claims.Remove(binding.Id);
                if (localHold == binding) localHold = null;
                if (pendingClaim == binding) { pendingClaim = null; pendingClaimRequest.Clear(); }
                if (pendingAttach == binding) pendingAttach = null;
                if (returnToHands == binding) returnToHands = null;
            }
            retiredPurchasedTools.Clear();
            retiredPurchasedVehicles.Clear();
            foreach (var target in vehicles.Values)
                if (target.PurchasedVehicleEntity != 0 && target.Vehicle == null) retiredPurchasedVehicles.Add(target);
            foreach (var target in retiredPurchasedVehicles)
            {
                foreach (var body in byBody.Where(pair => pair.Value == target).Select(pair => pair.Key).ToArray()) byBody.Remove(body);
                vehicles.Remove(target.Id);
            }
            retiredPurchasedVehicles.Clear();
        }

#if MONMP_DEVELOPMENT
        internal ulong[] TestPurchasedToolIds(ulong entity, GameObject instance)
        {
            if (disposed || instance == null || channel == null) throw new InvalidOperationException("Purchased hoist diagnostic is unavailable.");
            var expected = instance.GetComponentsInChildren<Tool_HoistPuller>(true)
                .Concat(instance.GetComponentsInChildren<Vehicle_HoistPullerBox>(true)
                    .Select(box => BoxToolField?.GetValue(box) as Tool_HoistPuller).Where(tool => tool != null).Cast<Tool_HoistPuller>())
                .Where(tool => tool != null).Distinct().ToArray();
            var result = new List<ulong>();
            foreach (var tool in expected)
            {
                if (!byTool.TryGetValue(tool, out var binding) || binding.PurchasedVehicleEntity != entity ||
                    binding.Id <= WorldRecord.DynamicIdStart || !bindings.TryGetValue(binding.Id, out var byId) || byId != binding ||
                    !byHandle.TryGetValue(binding.Handle, out var byNativeHandle) || byNativeHandle != binding ||
                    !ReferenceEquals(HandleField.GetValue(tool), binding.Handle))
                    throw new InvalidOperationException("Purchased hoist native routing is incomplete.");
                var identity = identities.Resolve(binding.Id);
                if (identity == null || identity.gameObject != tool.gameObject || identity.AdapterType != HoistPullerState.Adapter ||
                    !identity.SpawnKey.StartsWith("monmp/purchased-hoist-tool/v1/" + entity.ToString("X16") + "/", StringComparison.Ordinal) ||
                    (WorldRecord.DynamicIdStart | Math.Max(1UL, StaticId.Hash(identity.SpawnKey) & 0x7FFFFFFFFFFFFFFFUL)) != binding.Id)
                    throw new InvalidOperationException("Purchased hoist native identity is inconsistent.");
                if (!channel.Records.TryGetValue(binding.Id, out var record) || record.Adapter != HoistPullerState.Adapter)
                    throw new InvalidOperationException("Purchased hoist has no retained authoritative record.");
                var accepted = HoistPullerState.Decode(record.Payload);
                if (!host && (!binding.OriginalCaptured || binding.PendingState.HasValue || !Same(binding.State, accepted)))
                    throw new InvalidOperationException("Purchased hoist authoritative state is not hydrated.");
                result.Add(binding.Id);
            }
            if (bindings.Values.Count(binding => binding.PurchasedVehicleEntity == entity) != expected.Length)
                throw new InvalidOperationException("Purchased hoist retains stale native tool bindings.");
            return result.OrderBy(id => id).ToArray();
        }
#endif

        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values
            .Select(binding => new WorldRecord(binding.Id, 1, HoistPullerState.Adapter,
                (host ? Capture(binding) : binding.State).Encode()));

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected ?? throw new ArgumentNullException(nameof(connected));
            channel = world ?? throw new ArgumentNullException(nameof(world));
            session.PoseReceived += OnPose;
            session.PeerLeft += OnPeerLeft;
            channel.CommandRequested += Command;
            channel.CommandRejected += Rejected;
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed;
            channel.ReplyReceived += Reply;
        }

        internal void SetNewActionsBlocked(bool value) => NewActionsBlocked = value;
        private bool Running => !disposed && session?.IsRunning == true;
        private bool GuestRunning => Running && !host;
        internal bool SuppressGuestHoistCoroutine(PlayerInteractable player)
        {
            if (!GuestRunning || localHold != null) return false;
            if (player != null && player.Get_ActiveTool is Tool_HoistPuller)
                Report("Hoist placement is host-authoritative; ask the host to set the hoist down.");
            return true;
        }

        // True means the guest-side native callback was consumed; the host applies crank/recover.
        internal bool InterceptHandle(Tool_HoistPuller_Handle handle, bool crank)
        {
            if (host && !crank && byHandle.TryGetValue(handle, out var hostBinding) &&
                BlockOccupiedHostRecovery(hostBinding.Tool)) return true;
            if (!GuestRunning) return false;
            if (!byHandle.TryGetValue(handle, out var binding))
            {
                Report("This hoist handle is not registered in the shared world.");
                return true;
            }
            if (!crank)
            {
                BeginPickup(binding, HoistPullerPlacementKind.Recover);
                return true;
            }
            if (!Ready || NewActionsBlocked || !binding.OriginalCaptured || binding.PendingState.HasValue)
            {
                Report("Wait for the shared world and save to finish before cranking the hoist.");
                return true;
            }
            Camera? camera = gameplayCamera();
            if (camera == null)
            {
                Report("The local camera is unavailable for a shared hoist interaction.");
                return true;
            }
            try
            {
                var aim = Integration.LocalInteraction.SampleTool(camera);
                var request = new HoistPullerCommand(HoistPullerCommandKind.Crank,
                    aim.Origin.x, aim.Origin.y, aim.Origin.z, aim.Yaw, aim.Pitch, aim.Independent);
                channel!.Request(binding.Id, request.Encode(), useLease: true);
            }
            catch (InvalidDataException ex) { Report("The hoist request could not be encoded: " + ex.Message); }
            return true;
        }

        private Camera? gameplayCamera()
        {
            try { return gameplay != null ? gameplay.Player?.mainCamera : null; }
            catch { return null; }
        }

        internal bool InterceptBox(Vehicle_HoistPullerBox box)
        {
            if (!Running) return false;
            var tool = BoxToolField?.GetValue(box) as Tool_HoistPuller;
            if (tool == null || !byTool.TryGetValue(tool, out var binding)) return GuestRunning;
            if (host)
            {
                ExpireClaims();
                // A guest is aiming this hoist: the host can't take it out from under them.
                if (!claims.ContainsKey(binding.Id) && !HostHolds(tool) && !tool.isGroundPlaced) return false;
                Report("Another player is using this hoist puller.");
                return true;
            }
            BeginPickup(binding, HoistPullerPlacementKind.Claim);
            return true;
        }

        // Recovering an attached tool is a pickup, just like taking it from its case.
        // Keep it attached until the host grants ownership and use the same correlated
        // reply/timeout path so a delayed answer cannot equip an unrelated retry.
        private void BeginPickup(Binding binding, HoistPullerPlacementKind kind)
        {
            if (!Ready || NewActionsBlocked || !binding.OriginalCaptured || binding.PendingState.HasValue) { Report("Wait for the shared world and save to finish before taking the hoist."); return; }
            var player = gameplayPlayer();
            if (player == null || gameplay.Get_PlayerState != PlayerState.Walking) { Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText("ERROR_YouCantTakeThat")); return; }
            if (player.IsHoldingAnObject) { Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText("ERROR_AlreadyHaveAItemInHand")); return; }
            if (localHold != null || pendingClaim != null || pendingAttach != null || returnToHands != null && returnToHands != binding) return;
            uint operation = NextPlacementOperation();
            pendingClaim = binding;
            pendingPickupKind = kind;
            pendingClaimRequest.Begin(binding.Id, operation, Time.unscaledTime);
            if (!Send(binding, new HoistPullerPlacement(kind, operation: operation)))
            { pendingClaim = null; pendingClaimRequest.Clear(); }
        }

        // Native attachment clears PlayerInteractable.activeTool without UnEquiped(), so
        // isEquiped alone is not an ownership test (and stays true after the first use).
        private bool HostHolds(Tool_HoistPuller tool) => gameplayPlayer()?.Get_ActiveTool == tool;
        private PlayerInteractable? gameplayPlayer()
        {
            try { return gameplay != null ? gameplay.Player : null; }
            catch { return null; }
        }

        private void ExpireClaims()
        {
            if (!host || session == null || claims.Count == 0) return;
            float now = Time.unscaledTime;
            expiredClaims.Clear();
            foreach (var pair in claims)
                if (HoistPullerOwnershipPolicy.ClaimExpired(now, pair.Value.Since,
                    ClaimSeconds, session.Peers.ContainsKey(pair.Value.Peer))) expiredClaims.Add(pair.Key);
            foreach (var stale in expiredClaims)
                claims.Remove(stale);
            expiredClaims.Clear();
        }

        internal bool InterceptNativePickup(Tool_HoistPuller tool)
        {
            if (!Running || !byTool.ContainsKey(tool)) return false;
            if (!host) return localHold?.Tool != tool;
            ExpireClaims();
            if (!claims.ContainsKey(byTool[tool].Id) && !HostHolds(tool)) return false;
            Report("Another player is using this hoist puller.");
            return true;
        }

        // A left click can fall from the animated handle onto the tool body. Native
        // grabbing refuses attached hoists; it must not turn that miss into Recover.
        internal bool InterceptToolGrab(InteractableItems? item)
        {
            if (!Running || item is not Tool_HoistPuller tool) return false;
            if (!tool.canGrab()) return true;
            return InterceptToolPickup(item);
        }

        // E uses the same deliberate claim/recovery transaction as right-click.
        // Native EquipThisTool cannot recover an attached shared hoist on a guest.
        internal bool InterceptToolPickup(InteractableItems? item)
        {
            if (!Running || item is not Tool_HoistPuller tool || localHold?.Tool == tool || HostHolds(tool)) return false;
            if (!byTool.TryGetValue(tool, out var binding))
            {
                if (!host) Report("This hoist puller is not registered in the shared world.");
                return !host;
            }
            if (host)
            {
                if (!Ready || NewActionsBlocked || !binding.OriginalCaptured || binding.PendingState.HasValue) { Report("Wait for the shared world and save to finish before taking the hoist."); return true; }
                var player = gameplayPlayer();
                if (player == null || gameplay.Get_PlayerState != PlayerState.Walking) { Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText("ERROR_YouCantTakeThat")); return true; }
                if (player.IsHoldingAnObject) { Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText("ERROR_AlreadyHaveAItemInHand")); return true; }
                ExpireClaims();
                if (claims.ContainsKey(binding.Id)) { Report("Another player is using this hoist puller."); return true; }
                if (tool.isGroundPlaced || tool.carRigidbody != null) ReturnToCase(binding);
                player.Grab_HoistPuller(tool);
                return true;
            }
            BeginPickup(binding, binding.State.Phase == HoistPullerPhase.StoredOrHeld
                ? HoistPullerPlacementKind.Claim : HoistPullerPlacementKind.Recover);
            return true;
        }

        internal bool SuppressGuestToolRightClick(Tool_HoistPuller tool)
        {
            if (BlockOccupiedHostRecovery(tool)) return true;
            if (!GuestRunning || localHold?.Tool == tool) return false;
            return InterceptToolPickup(tool);
        }

        private bool BlockOccupiedHostRecovery(Tool_HoistPuller tool)
        {
            if (!host || !Running || !byTool.ContainsKey(tool) || tool.carRigidbody == null ||
                gameplayPlayer()?.IsHoldingAnObject != true) return false;
            // Native DetachHoist disables its handle before reporting occupied hands.
            // Preserve the native error while leaving the usable attached hoist intact.
            Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText("ERROR_AlreadyHaveAItemInHand"));
            return true;
        }

        // Guests keep native placement/timing disabled. Only the car's active driver
        // applies the synchronized pull; a held tool can still draw its aiming cable.
        internal bool SuppressReplicaPhysics(Tool_HoistPuller tool)
        {
            if (!GuestRunning || !byTool.TryGetValue(tool, out var binding)) return false;
            ApplyDriverForce(binding);
            return !(localHold == binding && tool.carRigidbody == null);
        }

        // The host still runs the native timer and cable-release logic on a remote car.
        // Its kinematic replica must not receive the force as well as the active driver.
        internal static void ApplyNativeForce(Rigidbody body, Vector3 force, Vector3 position, ForceMode mode)
        {
            if (Current?.Running != true || !body.isKinematic) body.AddForceAtPosition(force, position, mode);
        }

        private void ApplyDriverForce(Binding binding)
        {
            var state = binding.State;
            if (!vehicles.TryGetValue(state.Vehicle, out var target) || !(target.Vehicle is VehicleController car)) return;
            var shared = MonMPPlugin.Instance?.World?.Vehicles;
            var body = car.vehicleRigidbody;
            bool localSimulator = session != null && shared != null && shared.CanControl(car) &&
                shared.DrivenBy(car) == session.LocalPeerId;
            if (!HoistPullerPhysicsPolicy.CanApplyForce(GuestRunning, Ready,
                state.Phase == HoistPullerPhase.AttachedToVehicle, localSimulator, body != null && !body.isKinematic)) return;
            var point = body!.transform.TransformPoint(new Vector3(state.AnchorX, state.AnchorY, state.AnchorZ));
            var delta = new Vector3(state.GroundX, state.GroundY, state.GroundZ) - point;
            float force = HoistPullerPhysicsPolicy.ForceMagnitude(delta.magnitude, state.MinRange, state.MaxRange,
                (float)MinForceField.GetValue(binding.Tool), (float)MaxForceField.GetValue(binding.Tool));
            if (force > 0f)
                body.AddForceAtPosition(delta.normalized * force, point, (ForceMode)ForceModeField.GetValue(binding.Tool));
        }

        private uint NextPlacementOperation()
        {
            do { nextPlacementOperation++; } while (nextPlacementOperation == 0);
            return nextPlacementOperation;
        }

        private bool Send(Binding binding, HoistPullerPlacement placement)
        {
            bool available = placement.Kind == HoistPullerPlacementKind.Release ? Running && channel?.Ready == true : Ready;
            if (!available || channel == null || !channel.Records.ContainsKey(binding.Id)) return false;
            try
            {
                if (placement.Operation == 0) placement = placement.WithOperation(NextPlacementOperation());
                // Anchor/timer presentation is not an ownership token. Validate the
                // live claim, pose and cable bounds on the host instead of its revision.
                channel.Request(binding.Id, placement.Encode(), useLease: true);
                return true;
            }
            catch (InvalidDataException ex) { Report("The hoist request could not be encoded: " + ex.Message); return false; }
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (host || !bindings.TryGetValue(entity, out var binding) ||
                !HoistPullerPlacement.TryDecodeReply(payload, out var kind, out var status, out uint operation)) return;
            if (kind == HoistPullerPlacementKind.Claim || kind == HoistPullerPlacementKind.Recover)
                CompletePickup(binding, kind, status, operation);
            else if (kind == HoistPullerPlacementKind.Attach)
            {
                if (pendingAttach != binding || pendingAttachment.Operation != operation) return;
                if (status == HoistPullerPlacementStatus.Granted)
                {
                    pendingAttachGranted = true;
                    if (MatchesPendingAttachment(binding, binding.State))
                    { pendingAttach = null; localHold = null; Apply(binding, binding.State, true); }
                    return;
                }
                QueueReturnToHands(binding);
                Report("The host couldn't attach the hoist there. Returning it to your hands so you can try again.");
            }
        }

        private void CompletePickup(Binding binding, HoistPullerPlacementKind kind, HoistPullerPlacementStatus status, uint operation)
        {
            if (pendingClaim != binding || pendingPickupKind != kind || !pendingClaimRequest.Complete(binding.Id, operation)) return;
            pendingClaim = null;
            if (status != HoistPullerPlacementStatus.Granted)
            {
                if (returnToHands == binding) { returnToHands = null; Apply(binding, binding.State, true); }
                Report(status == HoistPullerPlacementStatus.Busy ? "Someone else is using this hoist puller." : "Move closer to take the hoist puller.");
                return;
            }
            var player = gameplayPlayer();
            if (player == null || player.IsHoldingAnObject || gameplay.Get_PlayerState != PlayerState.Walking)
            {
                if (returnToHands == binding) { returnToHands = null; Apply(binding, binding.State, true); }
                Send(binding, new HoistPullerPlacement(HoistPullerPlacementKind.Release)); return;
            }
            // Replies can arrive before the queued StoredOrHeld update. Native
            // Equiped clears the body/cable but leaves isCarPlaced and the handle;
            // reset those before Apply starts preserving this locally held tool.
            ReturnToCase(binding);
            localHold = binding;
            nextClaimRefresh = Time.unscaledTime + 30f;
            if (returnToHands == binding) returnToHands = null;
            player.Grab_HoistPuller(binding.Tool);
            if (player.Get_ActiveTool != binding.Tool)
            { localHold = null; Send(binding, new HoistPullerPlacement(HoistPullerPlacementKind.Release)); }
        }

        // Guest: follow this player's native aiming until it hooks a vehicle or is put away.
        private void TrackLocalHold()
        {
            var binding = localHold;
            if (binding == null) return;
            if (pendingAttach == binding) return;
            var tool = binding.Tool;
            var player = gameplayPlayer();
            if (tool.carRigidbody != null)
            {
                tool.isEquiped = false;
                tool.playerHand = null;
                var controller = tool.carRigidbody.GetComponentInParent<Vehicle>();
                var vehicle = byBody.TryGetValue(tool.carRigidbody, out var found) ? found
                    : vehicles.Values.FirstOrDefault(value => value.Vehicle == controller);
                if (vehicle == null)
                {
                    QueueReturnToHands(binding);
                    Report("That vehicle isn't shared, so the hoist can't pull it.");
                    return;
                }
                var ground = tool.GroundAnchor; var anchor = tool._AnchorPositionOnCar;
                pendingAttachment = new HoistPullerPlacement(HoistPullerPlacementKind.Attach, ground.x, ground.y, ground.z,
                    vehicle.Id, anchor.x, anchor.y, anchor.z, NextPlacementOperation());
                pendingAttach = binding; pendingAttachSince = Time.unscaledTime; pendingAttachGranted = false;
                if (!Send(binding, pendingAttachment)) QueueReturnToHands(binding);
                // The local copy stays on the vehicle until the host's attached state (or a refusal) arrives.
                return;
            }
            if (player == null || player.Get_ActiveTool != tool)
            {
                localHold = null;
                Send(binding, new HoistPullerPlacement(HoistPullerPlacementKind.Release));
                ReturnToCase(binding);
                return;
            }
            // A player may spend longer than the host claim timeout aiming a
            // cable. Renew only an existing, live hand claim, never a placement.
            if (Ready && !NewActionsBlocked && Time.unscaledTime >= nextClaimRefresh)
            {
                nextClaimRefresh = Time.unscaledTime + 30f;
                Send(binding, new HoistPullerPlacement(HoistPullerPlacementKind.Claim));
            }
        }

        private void QueueReturnToHands(Binding binding)
        {
            pendingAttach = null;
            pendingAttachGranted = false;
            localHold = null;
            returnToHands = binding;
            // Let the old native placement coroutine observe its cleared active
            // tool and finish before a new Grab_HoistPuller starts another one.
            returnAfterFrame = Time.frameCount + 1;
        }

        private bool MatchesPendingAttachment(Binding binding, HoistPullerState state)
        {
            return pendingAttach == binding && state.Phase == HoistPullerPhase.AttachedToVehicle && state.Vehicle == pendingAttachment.Vehicle &&
                state.GroundX == pendingAttachment.GroundX && state.GroundY == pendingAttachment.GroundY && state.GroundZ == pendingAttachment.GroundZ &&
                state.AnchorX == pendingAttachment.AnchorX && state.AnchorY == pendingAttachment.AnchorY && state.AnchorZ == pendingAttachment.AnchorZ;
        }

        private bool PreserveLocalPlacement(Binding binding, HoistPullerState state)
        {
            binding.State = state;
            if (pendingAttachGranted && MatchesPendingAttachment(binding, state)) { pendingAttach = null; localHold = null; }
            return binding == localHold || binding == returnToHands;
        }

        private void UpdateClientRequests()
        {
            if (pendingClaimRequest.Expire(Time.unscaledTime))
            {
                var expired = pendingClaim; pendingClaim = null;
                if (expired != null)
                {
                    if (returnToHands == expired) { returnToHands = null; Apply(expired, expired.State, true); }
                    Send(expired, new HoistPullerPlacement(HoistPullerPlacementKind.Release));
                }
                Report("The hoist request timed out. Try taking it again.");
            }
            if (pendingAttach != null && Time.unscaledTime - pendingAttachSince >= HoistClaimPending.TimeoutSeconds)
            {
                QueueReturnToHands(pendingAttach);
                Report("The attachment wasn't confirmed. Checking ownership before returning the hoist to your hands.");
            }
            var returning = returnToHands;
            if (returning == null || pendingClaim != null) return;
            var player = gameplayPlayer();
            if (!Ready || NewActionsBlocked || Time.frameCount <= returnAfterFrame || player == null ||
                player.IsHoldingAnObject || gameplay.Get_PlayerState != PlayerState.Walking ||
                !returning.OriginalCaptured || returning.PendingState.HasValue) return;
            // Revalidate the existing claim instead of assuming a refused or
            // timed-out attachment still belongs to us. Claim never detaches an
            // already accepted attachment, unlike Recover.
            BeginPickup(returning, HoistPullerPlacementKind.Claim);
        }

        private static void ReturnToCase(Binding binding)
        {
            var tool = binding.Tool;
            tool.isEquiped = false;
            tool.playerHand = null;
            binding.Handle.isActive(false);
            tool.isGroundPlaced = false;
            tool.carRigidbody = null;
            tool.HoistCarAttachment.SetActive(false);
            CarPlacedField?.SetValue(tool, false);
            CrankTimerField.SetValue(tool, 0f);
            if (OriginalParentField?.GetValue(tool) is Transform parent && parent != null)
            {
                tool.transform.SetParent(parent);
                if (OriginalPositionField?.GetValue(tool) is Vector3 position) tool.transform.localPosition = position;
                if (OriginalRotationField?.GetValue(tool) is Quaternion rotation) tool.transform.localRotation = rotation;
            }
            tool.gameObject.layer = LayerMask.NameToLayer("Ignore Raycast");
        }

        private void Placement(uint peer, ulong entity, byte[] payload)
        {
            if (!host || session?.IsHost != true || peer == 0 || !session.Peers.ContainsKey(peer) || !bindings.TryGetValue(entity, out var binding)) return;
            HoistPullerPlacement request;
            try { request = HoistPullerPlacement.Decode(payload); }
            catch (InvalidDataException) {
#if MONMP_DEVELOPMENT
TestRejectedCommands++;
#endif
 return; }
            float now = Time.unscaledTime;
            ExpireClaims();
            var tool = binding.Tool;
            bool claimedByPeer = claims.TryGetValue(binding.Id, out var claim) && claim.Peer == peer;
            bool Near(Vector3 point, float range) => presence.TryGetValue(peer, out var seen) &&
                now - seen.Time <= HoistPullerAuthorityPolicy.FreshPoseSeconds &&
                (new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z) - point).sqrMagnitude <= range * range;
            bool open = Ready && !NewActionsBlocked;
            var status = HoistPullerPlacementStatus.Rejected;
            switch (request.Kind)
            {
                case HoistPullerPlacementKind.Claim:
                    if (HoistPullerOwnershipPolicy.ClaimBusy(claims.ContainsKey(binding.Id), claim.Peer, peer,
                        HostHolds(tool), tool.isGroundPlaced)) status = HoistPullerPlacementStatus.Busy;
                    else if (open && (Near(tool.transform.position, 4f) || claimedByPeer &&
                        presence.TryGetValue(peer, out var claimant) && now - claimant.Time <= HoistPullerAuthorityPolicy.FreshPoseSeconds &&
                        claimant.Pose.State == (byte)PlayerState.Walking && !claimant.Pose.Spectating))
                    { claims[binding.Id] = (peer, now); status = HoistPullerPlacementStatus.Granted; }
                    break;
                case HoistPullerPlacementKind.Release:
                    status = HoistPullerPlacementStatus.Granted;
                    break;
                case HoistPullerPlacementKind.Attach:
                    if (claimedByPeer && open && !HostHolds(tool) && !tool.isGroundPlaced &&
                        vehicles.TryGetValue(request.Vehicle, out var vehicle) && vehicle.Vehicle.vehicleRigidbody != null)
                    {
                        var ground = new Vector3(request.GroundX, request.GroundY, request.GroundZ);
                        var anchor = new Vector3(request.AnchorX, request.AnchorY, request.AnchorZ);
                        var body = vehicle.Vehicle.vehicleRigidbody;
                        var worldAnchor = body.transform.TransformPoint(anchor);
                        // The player walks from the ground anchor to a car along
                        // the native 15m cable; requiring them to remain within 6m
                        // of the ground wrongly rejects ordinary longer placements.
                        // Include camera height/reach and a small pose-latency
                        // margin while still requiring the player at the car.
                        if (Near(ground, tool.MaxCableRange + 5f) && Near(worldAnchor, 5f) && anchor.magnitude < 20f &&
                            (ground - worldAnchor).magnitude < tool.MaxCableRange + 2f)
                        {
                            tool.transform.SetParent(null, true);
                            tool.transform.position = ground;
                            tool.isGroundPlaced = true;
                            tool.GroundAnchor = ground;
                            tool.carRigidbody = body;
                            tool.AnchorPositionOnCar = anchor;
                            status = HoistPullerPlacementStatus.Granted;
                        }
                    }
                    break;
                case HoistPullerPlacementKind.Recover:
                    if (HoistPullerOwnershipPolicy.CanRecover(open, claims.ContainsKey(binding.Id), tool.isGroundPlaced,
                        HostHolds(tool), Near(tool.transform.position, 4f)))
                    { RecoverToClaim(binding, peer, now); status = HoistPullerPlacementStatus.Granted; }
                    break;
            }
            // Failed attachment keeps the requester's existing hold available for
            // a correlated Claim retry. Only success or an explicit release ends it.
            if (HoistPullerOwnershipPolicy.EndsClaim(request.Kind, claimedByPeer, status == HoistPullerPlacementStatus.Granted)) claims.Remove(binding.Id);
            else if (request.Kind == HoistPullerPlacementKind.Attach && claimedByPeer) claims[binding.Id] = (peer, now);
            if (status == HoistPullerPlacementStatus.Granted)
#if MONMP_DEVELOPMENT
TestAcceptedCommands++;
#else
{}
#endif
 else
#if MONMP_DEVELOPMENT
TestRejectedCommands++;
#else
{}
#endif

            if (status == HoistPullerPlacementStatus.Granted && request.Kind != HoistPullerPlacementKind.Release)
            {
                binding.State = Capture(binding);
                channel!.Publish(binding.Id, binding.State.Encode());
            }
            channel!.Reply(peer, entity, HoistPullerPlacement.EncodeReply(request.Kind, status, request.Operation));
        }

        private void RecoverToClaim(Binding binding, uint peer, float now)
        {
            // The host clears the attachment without equipping its own player. The
            // requester equips on the correlated reply and retains the claim while
            // aiming, as native DetachHoist(false) immediately puts it in that hand.
            ReturnToCase(binding);
            claims[binding.Id] = (peer, now);
        }

        private void Rejected(uint peer, ulong entity, byte[] payload)
        {
            if (!host || channel == null || !bindings.ContainsKey(entity) || !HoistPullerPlacement.Is(payload)) return;
            try
            {
                var request = HoistPullerPlacement.Decode(payload);
                channel.Reply(peer, entity, HoistPullerPlacement.EncodeReply(request.Kind,
                    HoistPullerPlacementStatus.Rejected, request.Operation));
            }
            catch (InvalidDataException) { }
        }

        internal void Update()
        {
            if (disposed || channel == null || !Running) return;
            RemoveDeadPurchasedBindings();
            ReplayPurchasedRecords();
            if (!host) UpdateClientRequests();
            if (!channel.FullySynchronized) return;
            if (!host)
            {
                TrackLocalHold();
                foreach (var binding in bindings.Values) if (binding != localHold && binding != returnToHands) Present(binding);
                return;
            }
            float now = Time.unscaledTime;
            ExpireClaims();
            if (now < nextCapture) return;
            nextCapture = now + CaptureInterval;
            foreach (var binding in bindings.Values)
            {
                if (!binding.OriginalCaptured) continue;
                if (!HostHolds(binding.Tool))
                {
                    binding.Tool.isEquiped = false;
                    if (binding.Tool.carRigidbody != null) binding.Tool.playerHand = null;
                }
                var next = Capture(binding);
                if (!Same(binding.State, next))
                {
                    binding.State = next;
                    channel.Publish(binding.Id, next.Encode());
                }
            }
        }

        private bool Ready => Running && channel?.FullySynchronized == true;
        private static bool Same(HoistPullerState a, HoistPullerState b) => HoistPullerStateComparison.Same(a, b);

        private static void CaptureOriginal(Binding binding)
        {
            var tool = binding.Tool;
            binding.OriginalGroundPlaced = tool.isGroundPlaced;
            binding.OriginalEquipped = tool.isEquiped;
            binding.OriginalPlayerHand = tool.playerHand;
            binding.OriginalTransformParent = tool.transform.parent;
            binding.OriginalLocalPosition = tool.transform.localPosition;
            binding.OriginalLocalRotation = tool.transform.localRotation;
            binding.OriginalCarPlaced = CarPlacedField?.GetValue(tool) is true;
            binding.OriginalMinRange = (float)MinRangeField.GetValue(tool);
            binding.OriginalMaxRange = (float)MaxRangeField.GetValue(tool);
            binding.OriginalCarBody = tool.carRigidbody;
            binding.OriginalGroundAnchor = tool.GroundAnchor;
            binding.OriginalCarAnchor = tool.AnchorPositionOnCar;
            binding.OriginalCrankTimer = (float)CrankTimerField.GetValue(tool);
            binding.OriginalAttachmentActive = tool.HoistCarAttachment != null && tool.HoistCarAttachment.activeSelf;
            binding.OriginalHandleEnabled = binding.Handle.enabled;
            var box = AccessTools.Field(typeof(Tool_HoistPuller_Handle), "boxCollider")?.GetValue(binding.Handle) as Collider;
            binding.OriginalHandleColliderEnabled = box != null && box.enabled;
        }

        // A new native crank can start between two 10 Hz timer samples. Observe
        // the accepted call itself, otherwise a short zero gap loses its event.
        internal void NativeCranked(Tool_HoistPuller tool, float before)
        {
            if (!host || !Running || !(before <= 0f) || !byTool.TryGetValue(tool, out var binding) ||
                !tool.isGroundPlaced || tool.carRigidbody == null) return;
            float after = Mathf.Clamp((float)CrankTimerField.GetValue(tool), 0f, 1.1f);
            if (!(after > 0f)) return;
            binding.CrankSequence = unchecked(binding.CrankSequence + 1);
            // Keep the timer-edge fallback from counting the same start again.
            binding.LastObservedTimer = after;
        }

        private HoistPullerState Capture(Binding binding)
        {
            var tool = binding.Tool;
            float timer = Mathf.Clamp((float)CrankTimerField.GetValue(tool), 0f, 1.1f);
            if (timer > 0f && binding.LastObservedTimer <= 0f && tool.isGroundPlaced && tool.carRigidbody != null)
                binding.CrankSequence = unchecked(binding.CrankSequence + 1);
            binding.LastObservedTimer = timer;

            var hand = tool.playerHand;
            float hx = 0, hy = 0, hz = 0;
            if (tool.isGroundPlaced && tool.carRigidbody == null && hand != null)
            { hx = hand.position.x; hy = hand.position.y; hz = hand.position.z; }

            if (!tool.isGroundPlaced)
                return new HoistPullerState(HoistPullerPhase.StoredOrHeld, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, timer, binding.CrankSequence);
            if (tool.carRigidbody == null)
                return new HoistPullerState(HoistPullerPhase.GroundPlaced,
                    tool.GroundAnchor.x, tool.GroundAnchor.y, tool.GroundAnchor.z, 0, 0, 0, 0, hx, hy, hz, timer, binding.CrankSequence);

            if (!byBody.TryGetValue(tool.carRigidbody, out var vehicleBinding))
            {
                var controller = tool.carRigidbody.GetComponentInParent<Vehicle>();
                if (controller != null && vehicles.Values.FirstOrDefault(value => value.Vehicle == controller) is VehicleBinding found)
                {
                    vehicleBinding = found;
                    byBody[tool.carRigidbody] = found;
                }
                else throw new InvalidOperationException("Hoist puller is attached to a vehicle without a shared identity.");
            }
            var anchor = tool.AnchorPositionOnCar;
            return new HoistPullerState(HoistPullerPhase.AttachedToVehicle,
                tool.GroundAnchor.x, tool.GroundAnchor.y, tool.GroundAnchor.z, vehicleBinding.Id,
                anchor.x, anchor.y, anchor.z, 0, 0, 0, timer, binding.CrankSequence,
                (float)MinRangeField.GetValue(tool), (float)MaxRangeField.GetValue(tool));
        }

        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnPeerLeft(uint peer)
        {
            presence.Remove(peer);
            nextCrankRequest.Remove(peer);
            foreach (var claimed in claims.Where(pair => pair.Value.Peer == peer).Select(pair => pair.Key).ToArray()) claims.Remove(claimed);
        }

        [MonMP.Core.World.WorldCommandAdapters(HoistPullerState.Adapter)]

        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (HoistPullerPlacement.Is(payload)) { Placement(peer, entity, payload); return; }
            if (payload == null || payload.Length == 0 || payload[0] != HoistPullerCommand.Marker) return;
            if (!host || session?.IsHost != true || !bindings.TryGetValue(entity, out var binding)) return;
            HoistPullerCommand request;
            try { request = HoistPullerCommand.Decode(payload); }
            catch (InvalidDataException) {
#if MONMP_DEVELOPMENT
TestRejectedCommands++;
#endif
 return; }

            float now = Time.unscaledTime;
            bool peerKnown = peer != 0 && session.Peers.ContainsKey(peer);
            (PlayerPose Pose, float Time) observed = default;
            bool freshPose = peerKnown && presence.TryGetValue(peer, out observed) &&
                now >= observed.Time && now - observed.Time <= HoistPullerAuthorityPolicy.FreshPoseSeconds;
            PlayerPose pose = freshPose ? observed.Pose : default;
            bool walking = freshPose && pose.State == (byte)PlayerState.Walking;
            bool cameraFits = freshPose && request.CameraFits(pose, checkSegment);
            var handle = binding.Handle;
            bool handleActive = handle != null && handle.isActiveAndEnabled && handle.gameObject.activeInHierarchy;
            bool attached = binding.Tool.isGroundPlaced && binding.Tool.carRigidbody != null;
            bool rayHitsHandle = handle != null && cameraFits && HostRayHitsHandle(handle, request);
            bool rateLimited = !nextCrankRequest.TryGetValue(peer, out float nextAllowed) || now >= nextAllowed;
            if (!HoistPullerAuthorityPolicy.CanAcceptCrank(peer, host, Ready, NewActionsBlocked, freshPose, walking,
                cameraFits, handleActive, attached, rayHitsHandle, rateLimited))
            {
#if MONMP_DEVELOPMENT
TestRejectedCommands++;
#endif
 return; }

            nextCrankRequest[peer] = now + 0.15f;
            binding.Tool.Crink();

#if MONMP_DEVELOPMENT
            TestAcceptedCommands++;
#endif

        }

        private static bool HostRayHitsHandle(Tool_HoistPuller_Handle handle, HoistPullerCommand request)
        {
            var origin = new Vector3(request.CameraX, request.CameraY, request.CameraZ);
            var direction = Quaternion.Euler(request.CameraPitch, request.CameraYaw, 0f) * Vector3.forward;
            if (!Physics.Raycast(origin, direction, out var hit, HoistPullerAuthorityPolicy.InteractionRange)) return false;
            return hit.collider.GetComponentInParent<Tool_HoistPuller_Handle>() == handle;
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values.Where(value => value.Adapter == HoistPullerState.Adapter))
            {
                if (!bindings.TryGetValue(record.Id, out var binding))
                {
                    if (record.Id > WorldRecord.DynamicIdStart) continue;
                    throw new InvalidDataException("Received an unknown hoist puller identity.");
                }
                Apply(binding, HoistPullerState.Decode(record.Payload), true);
            }
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != HoistPullerState.Adapter) return;
            if (!bindings.TryGetValue(record.Id, out var binding))
            {
                if (record.Id > WorldRecord.DynamicIdStart) return;
                throw new InvalidDataException("Received an unknown hoist puller identity.");
            }
            Apply(binding, HoistPullerState.Decode(record.Payload), false);
        }

        private bool PrepareReplicaApply(Binding binding, HoistPullerState state)
        {
            if (!CapturePurchasedBaseline(binding) || state.Phase == HoistPullerPhase.AttachedToVehicle &&
                (!vehicles.TryGetValue(state.Vehicle, out var target) || target.Vehicle == null || target.Vehicle.vehicleRigidbody == null))
            {
                binding.PendingState = state;
                return false;
            }
            binding.PendingState = null;
            binding.CrankSequence = state.CrankSequence;
            return true;
        }

        private void Apply(Binding binding, HoistPullerState state, bool snap)
        {
            if (!PrepareReplicaApply(binding, state)) return;
            if (state.Phase == HoistPullerPhase.AttachedToVehicle && !vehicles.TryGetValue(state.Vehicle, out _))
                throw new InvalidDataException("Hoist puller references an unknown vehicle identity.");
            var tool = binding.Tool;
            var previous = binding.State.Phase;
            // This player is aiming it: the host still shows it stored until the attach lands.
            if (PreserveLocalPlacement(binding, state)) { binding.OriginalState = state; return; }
            tool.isEquiped = false;
            tool.playerHand = null;
            tool.GroundAnchor = new Vector3(state.GroundX, state.GroundY, state.GroundZ);
            tool.carRigidbody = null;
            tool._AnchorPositionOnCar = Vector3.zero;
            if (state.Phase == HoistPullerPhase.StoredOrHeld)
            {
                // Recovered (or a refused attach): back into its case here too.
                if (previous != HoistPullerPhase.StoredOrHeld || tool.transform.parent == null) ReturnToCase(binding);
                tool.isGroundPlaced = false;
                binding.Handle.isActive(false);
                tool.HoistCarAttachment.SetActive(false);
            }
            else
            {
                tool.transform.SetParent(null, true);
                tool.transform.position = tool.GroundAnchor;
                tool.isGroundPlaced = true;
                if (state.Phase == HoistPullerPhase.AttachedToVehicle)
                {
                    var vehicle = vehicles[state.Vehicle].Vehicle;
                    if (vehicle.vehicleRigidbody == null)
                        throw new InvalidDataException("Hoist puller target vehicle has no rigidbody.");
                    tool.carRigidbody = vehicle.vehicleRigidbody;
                    tool.AnchorPositionOnCar = new Vector3(state.AnchorX, state.AnchorY, state.AnchorZ);
                    MinRangeField.SetValue(tool, state.MinRange);
                    MaxRangeField.SetValue(tool, state.MaxRange);
                }
                else
                {
                    binding.Handle.isActive(false);
                    tool.HoistCarAttachment.SetActive(false);
                }
            }
            CrankTimerField.SetValue(tool, state.CrankTimer);
            if (!snap && state.CrankSequence != binding.OriginalState.CrankSequence && tool.animator != null &&
                state.Phase == HoistPullerPhase.AttachedToVehicle)
                tool.animator.SetTrigger("Hoist");
            binding.OriginalState = state;
            Present(binding);
        }

        private static void Present(Binding binding)
        {
            var state = binding.State;
            var tool = binding.Tool;
            if (state.Phase == HoistPullerPhase.GroundPlaced)
            {
                var endpoint = new Vector3(state.HandX, state.HandY, state.HandZ);
                tool.transform.LookAt(endpoint);
                tool.cable.SetPosition(0, tool.HoistCablePosition.position);
                tool.cable.SetPosition(1, endpoint);
                return;
            }
            if (state.Phase != HoistPullerPhase.AttachedToVehicle || tool.carRigidbody == null) return;
            var point = tool.carRigidbody.transform.TransformPoint(new Vector3(state.AnchorX, state.AnchorY, state.AnchorZ));
            tool.transform.LookAt(point);
            tool.HoistCarAttachment.SetActive(true);
            tool.HoistCarAttachment.transform.position = point;
            tool.HoistCarAttachment.transform.LookAt(tool.transform);
            tool.cable.SetPosition(0, tool.HoistCablePosition.position);
            tool.cable.SetPosition(1, tool.CarCablePosition.position);
        }

        private static void Report(string message)
        {
            try { MonMPPlugin.Instance?.ReportItemUseUnavailable(message); }
            catch (Exception ex) { Debug.LogWarning("MonMP hoist feedback failed: " + ex.Message); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (session != null)
                {
                    session.PoseReceived -= OnPose;
                    session.PeerLeft -= OnPeerLeft;
                }
                if (channel != null)
                {
                    channel.CommandRequested -= Command;
                    channel.CommandRejected -= Rejected;
                    channel.SnapshotApplied -= Snapshot;
                    channel.Changed -= Changed;
                    channel.ReplyReceived -= Reply;
                }
                if (!host)
                    foreach (var binding in bindings.Values)
                    {
                        if (!binding.OriginalCaptured) continue;
                        var tool = binding.Tool;
                        if (tool != null && binding.OriginalCaptured)
                        {
                            tool.isEquiped = binding.OriginalEquipped;
                            tool.playerHand = binding.OriginalPlayerHand;
                            tool.carRigidbody = binding.OriginalCarBody;
                            tool.GroundAnchor = binding.OriginalGroundAnchor;
                            tool._AnchorPositionOnCar = binding.OriginalCarAnchor;
                            // Replica placement detaches the root and the native anchor setter changes
                            // cable force ranges. Restore the complete captured offline baseline directly.
                            tool.transform.SetParent(binding.OriginalTransformParent != null ? binding.OriginalTransformParent : null, false);
                            tool.transform.localPosition = binding.OriginalLocalPosition;
                            tool.transform.localRotation = binding.OriginalLocalRotation;
                            CarPlacedField?.SetValue(tool, binding.OriginalCarPlaced);
                            MinRangeField.SetValue(tool, binding.OriginalMinRange);
                            MaxRangeField.SetValue(tool, binding.OriginalMaxRange);
                            // The native setter also dereferences cable.gameObject. Its cable
                            // may already be destroyed during scene teardown; restore the
                            // native field and each surviving presentation object separately.
                            GroundPlacedField.SetValue(tool, binding.OriginalGroundPlaced);
                            if (tool.cable != null) tool.cable.gameObject.SetActive(binding.OriginalGroundPlaced);
                            if (tool.HoistCarAttachment != null) tool.HoistCarAttachment.SetActive(binding.OriginalAttachmentActive);
                            CrankTimerField.SetValue(tool, binding.OriginalCrankTimer);
                        }
                        // Native isActive dereferences its serialized collider too. Keep the
                        // captured component/collider flags independent when either survives.
                        if (binding.Handle != null)
                        {
                            binding.Handle.enabled = binding.OriginalHandleEnabled;
                            var collider = AccessTools.Field(typeof(Tool_HoistPuller_Handle), "boxCollider")?.GetValue(binding.Handle) as Collider;
                            if (collider != null) collider.enabled = binding.OriginalHandleColliderEnabled;
                        }
                    }
            }
            finally
            {
                claims.Clear(); localHold = null; pendingClaim = null; pendingClaimRequest.Clear();
                pendingAttach = null; returnToHands = null;
                presence.Clear(); nextCrankRequest.Clear(); bindings.Clear(); byTool.Clear(); byHandle.Clear(); vehicles.Clear(); byBody.Clear();
                retiredPurchasedTools.Clear(); retiredPurchasedVehicles.Clear();
                session = null; channel = null;
                if (Current == this) Current = null;
            }
        }
    }
}
