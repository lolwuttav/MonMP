using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // Vetted item prefabs. Inventory and persistent fields stay on the host;
    // native use effects run only on the requesting player's process after approval.
    // Prefab paths come from the supplied game's live item inventory, never from a network packet.
    internal sealed partial class SharedItems : IDisposable
    {
        private sealed class Prefab
        {
            public ushort Id; public string Path = ""; public Type Type = null!; public GameObject Asset = null!;
            public PrefabFeatures Features;
        }
        private enum GrabMode : byte { None, Hand, Tool, Equip, ToolPack }
        private sealed class Binding
        {
            public ulong Id; public ushort Prefab;
            private InteractableItems? item;
            private ItemState state = null!;
            private SharedItems? observer;
            public long PresentationOrder { get; private set; }
            public InteractableItems? Item
            {
                get => item;
                set
                {
                    if (!ReferenceEquals(item, value))
                    {
                        if (item is Furniture_CarLift previousLift) CarLiftAudio.Forget(previousLift);
                        if (observer != null && !ReferenceEquals(item, null)) observer.byItem.Remove(item!);
                        HeldRenderers.Clear(); HeldRendererFrame = -1; DropPreview = null; DropPreviewAcknowledged = null; LiftWasHeld = false;
                        BodyConfigured = false;
                    }
                    item = value; observer?.RefreshActiveBinding(this); observer?.RefreshItemWork(this);
                }
            }
            public ItemState State
            {
                get => state;
                set { state = value; observer?.RefreshActiveBinding(this); observer?.RefreshItemWork(this); }
            }
            public ItemState? LastPublishedState;
            public WorldRecord? LastPublishedRecord;
            public void Observe(SharedItems owner, long order)
            {
                observer = owner; PresentationOrder = order; owner.RefreshActiveBinding(this); owner.RefreshItemWork(this);
            }
            public float LastOwnerUpdate;
            public float RejectedMoveSince = -1, NextMoveWarning;
            // Guest: where the item sat in the player's view when it was clicked, restored at the grant.
            public Vector3? GrabLocalPosition; public Quaternion GrabLocalRotation;
            public ConsumptionCommit? Consumption;
            // Guest: serial native knob intents and the newest optimistic value shown while they settle.
            public LooseSuspensionEditQueue? SuspensionEdits;
            public SeedCountPrediction? SeedPrediction;
            public int BodyDestroyedFrame = -1;
            public bool LiftWasHeld;
            public int LastUnpackedFrame = -1;
            public float LastUnpackedCount = -1;
            // Host: native fire consumed this item (a log became WoodOnFire, paper burst).
            public bool Burned;
            public bool Sold;
            public bool RetireAfterPackOutputs;
            // Fishing trash is a one-catch transient result. The local angler owns its native
            // six-second destroy timer; the host mirrors the timer after its accepted drop.
            public bool FishingTrashOutput, LocalFishingTrash;
            public float FishingTrashExpiresAt;
            // Local-only bookkeeping: how to take hold once the lease arrives (hand or an equipped
            // tool's own grip, e.g. a wrench mouth), whether a native release is awaiting the host,
            // whether the item was installed into world structure, and the smoothed replica target.
            public GrabMode PendingGrab; public InteractableTools? PendingTool; public float PendingSince;
            public ItemPickupClaim? Pickup;
            public Integration.PickupPresentation PickupPresentation;
            public uint IgnoredPickupGeneration;
            public bool PendingUse, NativePackUse;
            public bool Releasing, Absorbed, HasTarget;
            public LocalItemDropPreview? DropPreview;
            public ItemState? DropPreviewAcknowledged;
            public float DropPreviewStartedAt;
            public Vector3 FromPosition, TargetPosition;
            public Quaternion FromRotation, TargetRotation;
            public float TargetReceivedAt;
            // Measured spacing of pose updates, so held-item interpolation spans the real gap
            // instead of stopping between late packets (the stop-start read as a wobble).
            public float TargetInterval = HeldInterpolationSeconds;
            public bool Settled;
            public bool DeferredCanisterValues;
            // What the guest's per-frame body check last applied (see KeepBodyConfigured).
            public bool BodyConfigured, ConfiguredHeld, ConfiguredLocal, ConfiguredKinematic, ConfiguredTrigger;
            public Transform? ConfiguredParent;
            public Rigidbody? ConfiguredBody;
            // Guest: a loose item riding a shared vehicle or wheelbarrow, presented in that carrier's space.
            public Transform? CarrierFrame; public ulong IgnoredCarrier; public Collider[]? IgnoredItemColliders, IgnoredCarrierColliders;
            public Vector3 CarrierPosition, CarrierTargetPosition;
            public Quaternion CarrierRotation, CarrierTargetRotation;
            // Reused for this item's grip geometry. Discover at most once per posed frame;
            // native child/mesh changes are picked up on the next frame without renderer arrays.
            public readonly List<Renderer> HeldRenderers = new List<Renderer>();
            public int HeldRendererFrame = -1;
        }
        private static readonly System.Reflection.FieldInfo CurrentBoltHole = AccessTools.Field(typeof(PlayerInteractable), "Current_GeneratedBoltHole");
        private static readonly System.Reflection.FieldInfo SpannerGrip = AccessTools.Field(typeof(Tool_AdjustableSpanner), "CurrentHolding_Bolt");
        private static readonly System.Reflection.FieldInfo ImpactGrip = AccessTools.Field(typeof(Tool_Air_Impact), "CurrentHolding_Bolt");
        private static readonly System.Reflection.FieldInfo SpannerBoltHole = AccessTools.Field(typeof(Tool_AdjustableSpanner), "Current_GeneratedBoltHole");
        private static readonly System.Reflection.FieldInfo ImpactBoltHole = AccessTools.Field(typeof(Tool_Air_Impact), "Current_GeneratedBoltHole");
        private static readonly System.Reflection.FieldInfo CurrentGeneratedPart = AccessTools.Field(typeof(PlayerInteractable), "currentGeneratedPart");
        private static readonly System.Reflection.MethodInfo UnEquipTool = AccessTools.Method(typeof(PlayerInteractable), "UnEquipTool");
        private static readonly System.Reflection.MethodInfo UseKeyDown = AccessTools.Method(typeof(PlayerInteractable), "UseKeyDown");
        private static readonly System.Reflection.MethodInfo UpdatePack = AccessTools.Method(typeof(InteractablePacks), "UpdatePack");
        private static readonly System.Reflection.MethodInfo TrayTriggerEnter = AccessTools.Method(typeof(Tool_MagneticTray), "OnTriggerEnter", new[] { typeof(Collider) });
        private static readonly System.Reflection.FieldInfo BoltTrayCooldown = AccessTools.Field(typeof(Hardware_Bolt), "LastTimePickFromMagneticTray");
        private static readonly System.Reflection.FieldInfo ChainsawHolderItem = AccessTools.Field(typeof(Vehicle_Chainsaw_Holder), "chainsaw");
        private static readonly System.Reflection.FieldInfo ChainsawHolderLocation = AccessTools.Field(typeof(Vehicle_Chainsaw_Holder), "Chainsaw_HoldLocation");
        private static readonly System.Reflection.FieldInfo ChainsawHolderLoaded = AccessTools.Field(typeof(Vehicle_Chainsaw_Holder), "saveLoaded");
        private static readonly System.Reflection.FieldInfo ChainsawPickTime = AccessTools.Field(typeof(Tool_Chainsaw), "LastTimePickFromHolder");
        private static readonly System.Reflection.MethodInfo ChainsawHolderAttach = AccessTools.Method(typeof(Vehicle_Chainsaw_Holder), "Chainsaw_Attach", new[] { typeof(bool) });
        private static readonly System.Reflection.MethodInfo ElectricPercolatorUpdateFreeze = AccessTools.Method(typeof(Item_ElectricCoffeePercolator), "UpdateFreeze", new[] { typeof(bool) });
        private static readonly System.Reflection.MethodInfo FenceUpdateVisual = AccessTools.Method(typeof(Tool_Fence), "UpdateVisualOption", Type.EmptyTypes);
        private static readonly System.Reflection.FieldInfo FenceOptions = AccessTools.Field(typeof(Tool_Fence), "FenceOptionsDetails")!;
        private readonly Dictionary<ushort, ulong> descriptorByPrefab = new Dictionary<ushort, ulong>();
        private readonly Dictionary<ulong, ushort> prefabByDescriptor = new Dictionary<ulong, ushort>();
        // Guest: items that existed when the session attached (the guest's own save copy). Anything
        // catalogued that appears later came from a local native action and is offered for adoption.
        private readonly HashSet<InteractableItems> baseline = new HashSet<InteractableItems>();
        private readonly Dictionary<InteractableItems, (uint Nonce, float Time)> adopting = new Dictionary<InteractableItems, (uint, float)>();
        private readonly Dictionary<uint, InteractableItems> adoptNonces = new Dictionary<uint, InteractableItems>();
        private readonly Dictionary<uint, bool> adoptHeld = new Dictionary<uint, bool>();
        private readonly HashSet<InteractableItems> adoptionCandidates = new HashSet<InteractableItems>();
        private readonly Dictionary<uint, (ushort Prefab, ItemSpawnRequest Request)> adoptionRequests = new Dictionary<uint, (ushort, ItemSpawnRequest)>();
        // Exact provenance for bolts just produced by native pack extraction. This lets an immediate
        // tool grab or bolt-hole attach join the shared identity before the slower discovery pass.
        private readonly Dictionary<InteractableItems, float> pendingPackedBoltOutputs = new Dictionary<InteractableItems, float>();
        private readonly Dictionary<uint, RemoteFishing.FishingTrashOrigin> fishingTrashNonces = new Dictionary<uint, RemoteFishing.FishingTrashOrigin>();
        private readonly Dictionary<uint, (float Tokens, float Time)> adoptBudget = new Dictionary<uint, (float, float)>();
        private uint nextNonce;
        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly Dictionary<ushort, Prefab> prefabs = new Dictionary<ushort, Prefab>();
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<InteractableItems, Binding> byItem = new Dictionary<InteractableItems, Binding>();
        private readonly HashSet<ulong> pendingContainment = new HashSet<ulong>();
        private readonly Dictionary<(ulong Tray, ulong Bolt), float> pendingTrayRequests = new Dictionary<(ulong, ulong), float>();
        private readonly Dictionary<(ulong Item, ulong Holder), float> pendingChainsawDocks = new Dictionary<(ulong, ulong), float>();
        private readonly Dictionary<ulong, Vehicle_Chainsaw_Holder> chainsawHolders = new Dictionary<ulong, Vehicle_Chainsaw_Holder>();
        private readonly Dictionary<Vehicle_Chainsaw_Holder, ulong> chainsawHolderIds = new Dictionary<Vehicle_Chainsaw_Holder, ulong>();
        private readonly Dictionary<Vehicle_Chainsaw_Holder, ChainsawHolderContacts> chainsawHolderContacts = new Dictionary<Vehicle_Chainsaw_Holder, ChainsawHolderContacts>();
        private readonly Dictionary<InteractableItems, bool> hidden = new Dictionary<InteractableItems, bool>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly ItemLeaseTable leases = new ItemLeaseTable();
        private readonly List<(InteractableItems Item, uint Peer, float Deadline)> handoffs = new List<(InteractableItems, uint, float)>();
        private readonly ConsumptionInbox consumption;
        private readonly ItemPrefabCatalog catalog;
        private NetworkSession? session;
        private WorldChannel? channel;
        private readonly NativeEntityRegistry identities;
        private ulong nextId = WorldRecord.DynamicIdStart;
        private float nextDiscovery, nextSend, nextPackedBoltSweep;
        // When this guest last took an item out of a shared pack natively. Pack contents are often
        // simple consumables (syrup cans, joint packs, wine), which are adopted like general items
        // right after an unpack instead of being hidden as unshared local copies.
        private float lastLocalUnpack = float.NegativeInfinity;
        private bool applying, applyingChainsawAttachment, disposed;

#if MONMP_DEVELOPMENT
        private int testFailedPackGrants;
#endif

        private const float HeldInterpolationSeconds = 0.1f;
        public bool Applying => applying;
        internal ItemCarriers? Carriers { get; set; }
        public bool Ready => channel?.Ready == true;
        public bool TransfersBlocked { get; set; }
        // Collection drains native outputs that already exist before the save freeze. Writing
        // closes this narrow window; normal carry/production commands remain blocked throughout.
        internal Func<bool>? CanDrainNativeOutputs { get; set; }
        private bool NativeOutputDrainAllowed => TransfersBlocked && CanDrainNativeOutputs?.Invoke() == true;
        public bool TransactionsSettled => bindings.Values.All(b => b.State.Consumption == null || b.State.Consumption.Acknowledged ||
            (!host && b.State.Consumption.Consumer != session?.ProfileId));
        public uint[] Owners => bindings.Values.Select(b => host ? leases[b.Id].Owner : b.State.Lease.Owner)
            .Where(p => p != ItemLease.Unowned).Distinct().ToArray();
        public bool LocalFlushComplete => session != null && pendingNativeOutputs.Count == 0 && adopting.Count == 0 && adoptionCandidates.Count == 0 && !Owners.Contains(session.LocalPeerId) && !gameplay.Player.IsHoldingAnObject;
        public void FlushLocalOwnership()
        {
            if (!Ready || session == null) return;
            // Discover before readiness is tested, including output spawned just before Begin.
            if (!host && NativeOutputDrainAllowed) Discover();
            // Native equipment and an item whose lease has already ended cannot send a
            // valid Drop command, but their stale native hold still prevents profile capture.
            var tool = gameplay.Player.Get_ActiveTool;
            if (tool != null && (!byItem.TryGetValue(tool, out var toolBinding) || !OwnedLocally(toolBinding))) CancelLocal(tool);
            var held = gameplay.Player.Get_HoldingItem;
            if (held != null && !IsPendingNativeOutput(held) && (!byItem.TryGetValue(held, out var heldBinding) || !OwnedLocally(heldBinding))) CancelLocal(held);
            foreach (var b in bindings.Values.Where(OwnedLocally).ToArray())
                if (b.Item != null) Request(b, ItemAction.Drop);
        }
        public void RevokeOwnership(uint peer)
        {
            if (!host || session?.IsHost != true) throw new InvalidOperationException("Only the host can revoke item ownership.");
            // Resolve the latest accepted hand pose before clearing the lease. CaptureOwnerPose
            // otherwise switches to the deliberately delayed presentation transform.
            foreach (var b in bindings.Values.Where(b => leases[b.Id].Owner == peer)) SettleRemoteHeld(b);
            foreach (var id in ReleasePeerLeases(peer))
            {
                var b = bindings[id];
                if (b.Item != null && peer == session.LocalPeerId) CancelLocal(b.Item);
                b.HasTarget = false; b.Settled = true;
                if (b.FishingTrashOutput && peer != session.LocalPeerId) b.FishingTrashExpiresAt = Time.unscaledTime + 6f;
                b.State = Capture(b);
                if (b.Item != null) { ConfigureBody(b, false); RefreshPlanterLocation(b.Item); }
                Publish(b);
            }
        }
        public int Count => bindings.Values.Count(b => b.State.Exists);
        public bool PeerHolding(uint peer) => bindings.Values.Any(b => b.State.Exists && b.State.Lease.Owner == peer);
        internal bool PeerHoldingOtherThan(uint peer, ulong entity) => bindings.Values.Any(b => b.Id != entity && b.State.Exists && b.State.Lease.Owner == peer);
        internal bool PeerHandOccupied(uint peer) => bindings.Values.Any(b => b.State.Exists && b.State.Lease.Owner == peer &&
            !(b.Item is InteractableTools));
        internal bool ChainsawHolderTrigger(Vehicle_Chainsaw_Holder holder, Collider other)
        {
            var chainsaw = other == null ? null : other.GetComponent<Tool_Chainsaw>();
            if (applyingChainsawAttachment || chainsaw == null || !Handles(chainsaw)) return true;
            if (!chainsawHolderIds.TryGetValue(holder, out ulong holderId) || !ChainsawHoldTransform(holder, out _)) return false;
            if (ChainsawHolderLoaded.GetValue(holder) is not true ||
                !chainsawHolderContacts[holder].Enter(chainsaw, other!)) return false;
            var binding = byItem[chainsaw];
            if (host) return leases[binding.Id].Owner == 0 || leases[binding.Id].Owner == ItemLease.Unowned;

            var lease = binding.State.Lease;
            if (!Ready || TransfersBlocked || session == null ||
                (lease.Owner != session.LocalPeerId && lease.Owner != ItemLease.Unowned) ||
                chainsaw.IntValue1 != 0 || !chainsaw.CanPlaceThisInHolder)
                return false;
            var pendingKey = (binding.Id, holderId);
            if (pendingChainsawDocks.ContainsKey(pendingKey)) return false;
            pendingChainsawDocks[pendingKey] = Time.unscaledTime;
            channel!.Request(binding.Id, new ChainsawHolderCommand(holderId, lease.Generation, Pose(chainsaw.transform)).Encode(), true);
            return false;
        }
        internal void ChainsawHolderExit(Vehicle_Chainsaw_Holder holder, Tool_Chainsaw saw)
        {
            if (chainsawHolderIds.TryGetValue(holder, out ulong holderId) && byItem.TryGetValue(saw, out var binding))
                pendingChainsawDocks.Remove((binding.Id, holderId));
        }
        private void RetainChainsawRackContact(Tool_Chainsaw saw)
        {
            foreach (var pair in chainsawHolderContacts)
                if (ChainsawHoldTransform(pair.Key, out var hold) && saw.transform.parent == hold)
                    pair.Value.Hold(saw);
        }
        internal bool PeerHoldsOnly(uint peer, ulong itemId)
        {
            if (peer == ItemLease.Unowned || itemId == 0 || (host && session?.IsHost != true) || (!host && session == null)) return false;
            var owned = bindings.Values.Where(b => b.State.Exists &&
                (host ? leases[b.Id].Owner : b.State.Lease.Owner) == peer).Select(b => b.Id).ToArray();
            return owned.Length == 1 && owned[0] == itemId;
        }
        // Presentation lookup only: the item remains independently replicated and is never
        // parented to or moved by the remote-player avatar.
        // The held item's position and, for items too wide for one hand, its width across the body.
        // Long tools (loppers, shovels, rods) are held near one end of their long axis: both ends
        // in item space, cached per item; the one nearer the owner's shoulder is the handle.
        private readonly Dictionary<InteractableItems, (Vector3 A, Vector3 B)?> longGrips = new Dictionary<InteractableItems, (Vector3, Vector3)?>();
        private (Vector3 A, Vector3 B)? LongGrip(InteractableItems item)
        {
            if (longGrips.TryGetValue(item, out var cached)) return cached;
            if (longGrips.Count > 256) longGrips.Clear();
            var root = item.transform;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            void Add(Transform t, Bounds b)
            {
                for (int i = 0; i < 8; i++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var local = root.InverseTransformPoint(t.TransformPoint(corner));
                    min = Vector3.Min(min, local); max = Vector3.Max(max, local);
                }
            }
            foreach (var filter in item.GetComponentsInChildren<MeshFilter>())
                if (filter.sharedMesh != null && filter.GetComponent<Renderer>() is Renderer r && !(r is ParticleSystemRenderer)) Add(filter.transform, filter.sharedMesh.bounds);
            foreach (var skinned in item.GetComponentsInChildren<SkinnedMeshRenderer>()) Add(skinned.transform, skinned.localBounds);
            (Vector3, Vector3)? result = null;
            if (min.x <= max.x)
            {
                var size = Vector3.Scale(max - min, root.lossyScale);
                int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
                float length = size[axis], second = Mathf.Max(axis == 0 ? size.y : size.x, axis == 2 ? size.y : size.z);
                if (length > 0.4f && length > 2.2f * second)
                {
                    var center = (min + max) * 0.5f;
                    var offset = Vector3.zero; offset[axis] = (max[axis] - min[axis]) * 0.5f * 0.8f;
                    result = (center - offset, center + offset);
                }
            }
            longGrips[item] = result;
            return result;
        }

        internal bool TryGetHeldItemPresentation(uint peer, Vector3 bodyRight, out Vector3 position, out float twoHandedWidth) =>
            TryGetHeldItemPresentation(peer, bodyRight, null, out position, out twoHandedWidth);
        internal bool TryGetHeldItemPresentation(uint peer, Vector3 bodyRight, Vector3? shoulder, out Vector3 position, out float twoHandedWidth)
            => TryGetHeldItemPresentation(peer, bodyRight, shoulder, out position, out twoHandedWidth, out _);
        internal bool TryGetHeldItemPresentation(uint peer, Vector3 bodyRight, Vector3? shoulder, out Vector3 position,
            out float twoHandedWidth, out Quaternion? handRotation)
            => TryGetHeldItemPresentation(peer, bodyRight, shoulder, out position, out twoHandedWidth, out handRotation, out _, out _);
        internal bool TryGetHeldItemPresentation(uint peer, Vector3 bodyRight, Vector3? shoulder, out Vector3 position,
            out float twoHandedWidth, out Quaternion? handRotation, out Vector3? leftPosition, out Quaternion? leftRotation)
        {
            twoHandedWidth = 0;
            handRotation = null;
            leftPosition = null; leftRotation = null;
            var selected = SelectHeldForPresentation(peer, out bool isTool);
            if (selected?.Item == null) { position = default; return false; }
            var overrides = MonMPPlugin.Instance?.HeldGrips;
            if (selected.Item is InteractableTools && overrides != null && overrides.TryGetRuntime(selected.Item, catalog.Entries[selected.Prefab].Definition.ShapeHash,
                out var localPosition, out var localRotation, out var calibratedWidth, out var localLeft, out var localLeftRotation))
            {
                position = selected.Item.transform.TransformPoint(localPosition);
                handRotation = selected.Item.transform.rotation * localRotation;
                twoHandedWidth = calibratedWidth;
                if (localLeft.HasValue && localLeftRotation.HasValue)
                { leftPosition = selected.Item.transform.TransformPoint(localLeft.Value); leftRotation = selected.Item.transform.rotation * localLeftRotation.Value; }
                return true;
            }
            position = selected.Item.transform.position;
            // Cans and buckets are carried by the handle on top, not through their middle.
            if (selected.Item is Tool_WateringCan || selected.Item is Tool_FishBucket || selected.Item is Tool_FuelCanister)
            {
                var top = default(Bounds); bool any = false;
                foreach (var renderer in HeldRenderers(selected))
                {
                    if (renderer is ParticleSystemRenderer) continue;
                    if (!any) { top = renderer.bounds; any = true; } else top.Encapsulate(renderer.bounds);
                }
                if (any) position = new Vector3(top.center.x, top.max.y - 0.03f, top.center.z);
            }
            else if (shoulder.HasValue && LongGrip(selected.Item) is { } ends)
            {
                var a = selected.Item.transform.TransformPoint(ends.A);
                var b = selected.Item.transform.TransformPoint(ends.B);
                position = (a - shoulder.Value).sqrMagnitude <= (b - shoulder.Value).sqrMagnitude ? a : b;
            }
            if (isTool) return true;
            float width = 0;
            foreach (var renderer in HeldRenderers(selected))
            {
                var extents = renderer.bounds.extents;
                width = Mathf.Max(width, 2f * (Mathf.Abs(bodyRight.x) * extents.x + Mathf.Abs(bodyRight.y) * extents.y + Mathf.Abs(bodyRight.z) * extents.z));
            }
            if (width > 0.45f) twoHandedWidth = width;
            return true;
        }
        internal InteractableItems? HeldItemOf(uint peer) => SelectHeld(peer, out _)?.Item;
        internal InteractableItems? HeldItemForPresentation(uint peer) => SelectHeldForPresentation(peer, out _)?.Item;
        internal bool TryGetRemoteHeldEquipment(uint peer, out InteractableItems item, out string shape)
        {
            var selected = SelectHeldForPresentation(peer, out _);
            item = selected?.Item!; shape = "";
            if (item == null || session == null || peer == session.LocalPeerId ||
                item == gameplay.Player.Get_ActiveTool || item == gameplay.Player.Get_HoldingItem) return false;
            shape = catalog.Entries[selected!.Prefab].Definition.ShapeHash;
            return true;
        }
        internal bool IsRemoteFishingRod(Tool_FishingRod rod, uint owner, uint generation) =>
            !disposed && !TransfersBlocked && session != null && owner != session.LocalPeerId && rod != null &&
            byItem.TryGetValue(rod, out var binding) && binding.State.Exists &&
            binding.State.Lease.Owner == owner && binding.State.Lease.Generation == generation;
        internal InteractableItems? RemoteFishingCatch(Tool_FishingRod rod, uint owner, uint generation,
            FishingCatchOutcome outcome, ushort trashTemplate)
        {
            if (!IsRemoteFishingRod(rod, owner, generation) ||
                !byItem.TryGetValue(rod, out var heldRod) ||
                !activePresentation.TrySelectOther(owner, heldRod, out var caught) || caught?.Item == null ||
                !caught.State.Exists || caught.State.Lease.Owner != owner) return null;
            var item = caught.Item;
            bool matches = outcome == FishingCatchOutcome.Fish ? item is Item_Fish :
                outcome == FishingCatchOutcome.Trash && FishingTrashTemplateCatalog.Matches(trashTemplate,
                    prefabs[caught.Prefab].Type, prefabs[caught.Prefab].Path);
            return matches ? item : null;
        }
        // Host: an untamed Leo eats a guest's held treats whole, as he does the host's (#251).
        internal bool HostHoldsTreats(uint peer) => host && peer != 0 && HeldItemOf(peer) is Tool_DogTreats;
        internal bool HostConsumeHeldTreats(uint peer)
        {
            if (!host || peer == 0 || !(HeldItemOf(peer) is Tool_DogTreats held) || !byItem.TryGetValue(held, out var b)) return false;
            return HostMutateLeasedTool(peer, b.Id, leases[b.Id].Generation, prefabs[b.Prefab].Type,
                tool => { tool.Value1 = 0; return true; }, destroyWhenEmpty: true);
        }
        internal bool TryGetHeldItemPresentationPosition(uint peer, out Vector3 position)
        {
            var selected = SelectHeldForPresentation(peer, out _);
            position = selected?.Item != null ? selected.Item.transform.position : default;
            return selected?.Item != null;
        }
        // A tool in hand wins over other items the peer leases.
        private Binding? SelectHeld(uint peer, out bool selectedIsTool)
        {
            return SelectHeldForPresentation(peer, out selectedIsTool);
        }
        // The host presents a guest-held item a sample behind the latest pose. A host rule check reads
        // the latest pose the guest sent, so a request arriving with its final move is judged there.
        private void SettleRemoteHeld(Binding b)
        {
            if (!host || b.Item == null || !b.HasTarget) return;
            var owner = leases[b.Id].Owner;
            if (owner == ItemLease.Unowned || owner == 0) return;
            b.Item.transform.SetPositionAndRotation(b.TargetPosition, b.TargetRotation);
            b.FromPosition = b.TargetPosition; b.FromRotation = b.TargetRotation;
        }
        // Range checks run every frame during tool holds; read the latest guest pose without
        // moving the presented item, or its interpolation restarts and the held item steps at 10 Hz.
        private Vector3 RulePosition(Binding b)
        {
            var owner = leases[b.Id].Owner;
            return b.HasTarget && owner != ItemLease.Unowned && owner != 0 ? b.TargetPosition : b.Item!.transform.position;
        }
        internal ulong EntityOf(InteractableItems item) => item != null && byItem.TryGetValue(item, out var b) ? b.Id : 0;
        // The shared lease holder of an item (ItemLease.Unowned when loose), or null when it is not shared.
        internal uint? LeaseOwnerOf(InteractableItems item)
        {
            if (item == null || !byItem.TryGetValue(item, out var b)) return null;
            return host ? leases[b.Id].Owner : b.State.Lease.Owner;
        }
        internal InteractableItems? ItemOf(ulong id) => bindings.TryGetValue(id, out var b) && b.State.Exists ? b.Item : null;
        internal bool TryGetSharedWheel(ulong id, out Part_Driveline_Wheel wheel, out ItemLease lease)
        {
            wheel = null!; lease = default;
            if (disposed || !bindings.TryGetValue(id, out var binding) || !binding.State.Exists ||
                !(binding.Item is Part_Driveline_Wheel found) || found == null) return false;
            wheel = found;
            lease = host ? leases[id] : binding.State.Lease;
            return true;
        }
        private readonly ActiveItemPresentationIndex<Binding> activePresentation = new ActiveItemPresentationIndex<Binding>();
        private readonly Dictionary<uint, (ulong Id, uint Generation)> handTools = new Dictionary<uint, (ulong, uint)>();
        private readonly List<Binding> remotePresentationScratch = new List<Binding>();
        private long nextPresentationOrder;
        private void ObserveBinding(Binding binding) => binding.Observe(this, ++nextPresentationOrder);
        private void RefreshActiveBinding(Binding binding)
        {
            bool live = !disposed && binding.Item != null;
            uint owner = live && binding.State.Exists ? binding.State.Lease.Owner : ItemLease.Unowned;
            uint actualOwner = live && host ? leases[binding.Id].Owner : ItemLease.Unowned;
            bool remoteOwned = live && actualOwner != ItemLease.Unowned && actualOwner != 0;
            if (owner == ItemLease.Unowned && !remoteOwned) { activePresentation.Remove(binding.Id); return; }
            activePresentation.Update(binding.Id, binding.PresentationOrder, binding, owner,
                owner != ItemLease.Unowned && typeof(InteractableTools).IsAssignableFrom(prefabs[binding.Prefab].Type), remoteOwned);
        }
        private bool AcquireLease(ulong id, uint peer, uint generation, int maxHeld = 1)
        {
            bool acquired = leases.TryAcquire(id, peer, generation, maxHeld);
            if (acquired) ReleaseCargoForPickup(id);
            if (acquired && bindings.TryGetValue(id, out var binding)) RefreshActiveBinding(binding);
            return acquired;
        }
        private bool ReleaseLease(ulong id, uint peer, uint generation)
        {
            bool released = leases.Release(id, peer, generation);
            if (released && bindings.TryGetValue(id, out var binding)) RefreshActiveBinding(binding);
            return released;
        }
        private ulong[] ReleasePeerLeases(uint peer)
        {
            var ids = leases.ReleasePeer(peer);
            foreach (var id in ids) if (bindings.TryGetValue(id, out var binding)) RefreshActiveBinding(binding);
            return ids;
        }
        internal void PrepareHeldPresentation()
        {
            // Selection is updated by accepted state/native-instance changes, including
            // same-frame ownership changes. Retained tombstones need no presentation pass.
        }
        private Binding? SelectHeldForPresentation(uint peer, out bool selectedIsTool)
        {
            if (!disposed && host && handTools.TryGetValue(peer, out var chosen) &&
                leases.Owns(chosen.Id, peer, chosen.Generation) && bindings.TryGetValue(chosen.Id, out var chosenBinding) &&
                chosenBinding.State.Exists && chosenBinding.Item is InteractableTools)
            { selectedIsTool = true; return chosenBinding; }
            while (!disposed && activePresentation.TrySelect(peer, out var selected, out selectedIsTool))
            {
                if (selected!.Item != null && selected.State.Exists && selected.State.Lease.Owner == peer) return selected;
                // Unity may destroy a native item between capture ticks. Repair only
                // the affected owner, allowing its remaining tool/item to take over.
                RefreshActiveBinding(selected);
            }
            selectedIsTool = false;
            return null;
        }
        private static List<Renderer> HeldRenderers(Binding binding)
        {
            if (binding.HeldRendererFrame != Time.frameCount)
            {
                binding.HeldRenderers.Clear();
                binding.Item!.GetComponentsInChildren(false, binding.HeldRenderers);
                binding.HeldRendererFrame = Time.frameCount;
            }
            return binding.HeldRenderers;
        }
        internal void RestoreEnvelopePresentation(Tool_Envelope envelope)
        {
            if (!host && !disposed && byItem.TryGetValue(envelope, out var binding) && binding.State.Exists)
                ItemPresentationRefresh.Envelope(envelope, binding.State.Values.IntValue1);
        }
        internal ItemState? StateOf(ulong id) => bindings.TryGetValue(id, out var b) ? (host ? Capture(b) : b.State) : null;
        internal bool TryGetLocalHydroEnvelope(Tool_Envelope envelope, out ulong id, out uint generation)
        {
            id = 0; generation = 0;
            if (host || session?.IsRunning != true || !Ready || envelope == null ||
                !byItem.TryGetValue(envelope, out var binding) || !binding.State.Exists ||
                !IsHydroEnvelope(envelope)) return false;
            var lease = binding.State.Lease;
            if (lease.Generation == 0 || (lease.Owner != ItemLease.Unowned && lease.Owner != session.LocalPeerId)) return false;
            id = binding.Id; generation = lease.Generation;
            return true;
        }
        internal bool TryGetLocalLease(InteractableItems item, out ulong id, out uint generation)
        {
            id = 0; generation = 0;
            if (host || !Ready || TransfersBlocked || item == null || session == null ||
                !byItem.TryGetValue(item, out var binding) || !binding.State.Exists ||
                binding.State.Lease.Owner != session.LocalPeerId || binding.State.Lease.Generation == 0) return false;
            id = binding.Id; generation = binding.State.Lease.Generation;
            return true;
        }
        internal bool HostAuthorizeHydroEnvelope(uint peer, ulong id, uint generation, out Tool_Envelope? envelope)
        {
            envelope = null;
            if (!host || session?.IsHost != true || peer == 0 || generation == 0 ||
                !bindings.TryGetValue(id, out var binding) || !binding.State.Exists ||
                binding.Item is not Tool_Envelope candidate || !IsHydroEnvelope(candidate)) return false;
            var lease = leases[id];
            if (lease.Generation != generation || (lease.Owner != peer && lease.Owner != ItemLease.Unowned)) return false;
            envelope = candidate;
            return true;
        }
        internal bool HostHydroEnvelopeOwnedByHost(ulong id) => host && bindings.TryGetValue(id, out var binding) &&
            binding.State.Exists && binding.Item is Tool_Envelope envelope && IsHydroEnvelope(envelope) && leases[id].Owner == 0;
        internal bool HostPeerNear(uint peer, Vector3 position, float range) => host && peer != 0 && InRange(peer, position, range);

#if MONMP_DEVELOPMENT
        internal string TestPeerRange(uint peer, Vector3 target)
        {
            if (!host) return "not host";
            if (!presence.TryGetValue(peer, out var value)) return "peer pose missing";
            var player = new Vector3(value.Pose.X, value.Pose.Y, value.Pose.Z);
            return "peerState=" + value.Pose.State + "; peerPoseAge=" + (Time.unscaledTime - value.Time).ToString("F2") +
                "; peerDistance=" + Vector3.Distance(player, target).ToString("F2");
        }
#endif

        private static bool IsHydroEnvelope(Tool_Envelope envelope) =>
            envelope.envelope_Type == Envelope_Type.Hydro_Facture || envelope.envelope_Type == Envelope_Type.Hydro_StartConstruction;
        // Contact callbacks can arrive between the ordinary hand-motion updates. Queue
        // the current pose before the dependent action on the same reliable channel.
        internal bool FlushLocalItemPose(InteractableItems item)
        {
            return !host && Ready && !TransfersBlocked && item != null &&
                byItem.TryGetValue(item, out var binding) && binding.State.Exists &&
                OwnedLocally(binding) && !binding.Releasing && Request(binding, ItemAction.Move);
        }

        internal void HostConsumeNpcItem(uint peer, ulong id, uint generation)
        {
            if (!host || !bindings.TryGetValue(id, out var binding) || !binding.State.Exists ||
                !leases.Owns(id, peer, generation) || ReferenceEquals(binding.Item, null)) return;
            var item = binding.Item!;
            AbsorbLocal(binding); // Publish the committed native consumption before its receipt.
            item.gameObject.SetActive(false);
        }

        internal void GuestConsumeNpcItem(ulong id)
        {
            if (host || !bindings.TryGetValue(id, out var binding) || !binding.State.Exists) return;
            binding.Absorbed = true; // Old motion records cannot recreate a confirmed consumed item.
            var item = binding.Item;
            binding.Item = null; identities.Unbind(id);
            if (ReferenceEquals(item, null)) return;
            byItem.Remove(item); ignored.Add(item); CancelLocal(item);
            if (item != null) { item.gameObject.SetActive(false); Object.Destroy(item.gameObject); }
        }

        internal bool TryGetNpcGiftIdentity(InteractableItems? item, out ulong id, out uint generation,
            out uint owner, out NpcGiftKind kind)
        {
            id = 0; generation = 0; owner = ItemLease.Unowned; kind = default;
            if (item == null || !byItem.TryGetValue(item, out var binding) || !binding.State.Exists ||
                !TryNpcGiftKind(item, out kind)) return false;
            id = binding.Id;
            var lease = host ? leases[id] : binding.State.Lease;
            generation = lease.Generation;
            owner = lease.Owner;
            return generation != 0;
        }
        internal bool HostAuthorizeNpcGiftItem(uint peer, ulong id, uint generation, NpcGiftKind kind,
            out InteractableItems? item)
        {
            item = null;
            if (!host || peer == 0 || !bindings.TryGetValue(id, out var binding) || !binding.State.Exists ||
                binding.Item == null || !leases.Owns(id, peer, generation) ||
                !TryNpcGiftKind(binding.Item, out var actualKind) || actualKind != kind) return false;
            SettleRemoteHeld(binding);
            item = binding.Item;
            return true;
        }
        internal bool TryGetNpcWeedSaleIdentity(InteractableItems? item, out ulong id, out uint generation,
            out uint owner, out NpcWeedPackKind kind)
        {
            id = 0; generation = 0; owner = ItemLease.Unowned; kind = default;
            if (item == null || !byItem.TryGetValue(item, out var binding) || !binding.State.Exists ||
                !TryNpcWeedPackKind(item, out kind)) return false;
            id = binding.Id;
            var lease = host ? leases[id] : binding.State.Lease;
            generation = lease.Generation; owner = lease.Owner;
            return generation != 0;
        }
        internal bool HostAuthorizeNpcWeedSaleItem(uint peer, ulong id, uint generation, NpcWeedPackKind kind,
            out InteractableItems? item)
        {
            item = null;
            if (!host || peer == 0 || !bindings.TryGetValue(id, out var binding) || !binding.State.Exists ||
                binding.Item == null || !leases.Owns(id, peer, generation) ||
                !TryNpcWeedPackKind(binding.Item, out var actualKind) || actualKind != kind) return false;
            SettleRemoteHeld(binding);
            item = binding.Item;
            return true;
        }
        internal bool HostNpcWeedSaleItemOverlapsActor(Actor actor, InteractableItems item)
        {
            var packCollider = item == null ? null : item.itemCollider;
            if (actor == null || packCollider == null || !packCollider.enabled || !packCollider.gameObject.activeInHierarchy)
                return false;
            if (byItem.TryGetValue(item!, out var packBinding)) SettleRemoteHeld(packBinding);
            Physics.SyncTransforms();
            foreach (var actorCollider in actor.GetComponentsInChildren<Collider>(true))
            {
                if (actorCollider == null || !actorCollider.enabled || !actorCollider.gameObject.activeInHierarchy ||
                    !packCollider.bounds.Intersects(actorCollider.bounds)) continue;
                if (Physics.ComputePenetration(packCollider, packCollider.transform.position, packCollider.transform.rotation,
                    actorCollider, actorCollider.transform.position, actorCollider.transform.rotation, out _, out _)) return true;
            }
            return false;
        }
        private static bool TryNpcWeedPackKind(InteractableItems item, out NpcWeedPackKind kind)
        {
            if (item is Item_WeedPreRollPack) { kind = NpcWeedPackKind.Small; return true; }
            if (item is Item_WeedPreRollBigPack) { kind = NpcWeedPackKind.Big; return true; }
            kind = default; return false;
        }
        internal bool HostNpcGiftItemOverlapsActor(Actor actor, InteractableItems item, out string diagnostics)
        {
            var giftCollider = item == null ? null : item.itemCollider;
            if (actor == null || giftCollider == null || !giftCollider.enabled || !giftCollider.gameObject.activeInHierarchy)
            {
                diagnostics = "giftCollider=missing-or-inactive";
                return false;
            }
            if (byItem.TryGetValue(item!, out var giftBinding)) SettleRemoteHeld(giftBinding);
            Physics.SyncTransforms();
            int actorColliders = 0, activeActorColliders = 0, boundsHits = 0;
            string firstBoundsHit = "none";
            foreach (var actorCollider in actor.GetComponentsInChildren<Collider>(true))
            {
                actorColliders++;
                if (actorCollider == null || !actorCollider.enabled || !actorCollider.gameObject.activeInHierarchy) continue;
                activeActorColliders++;
                if (!giftCollider.bounds.Intersects(actorCollider.bounds)) continue;
                boundsHits++;
                if (firstBoundsHit == "none")
                    firstBoundsHit = actorCollider.GetType().Name + "/" + actorCollider.name + " bounds=" + actorCollider.bounds.ToString();
                if (Physics.ComputePenetration(giftCollider, giftCollider.transform.position, giftCollider.transform.rotation,
                    actorCollider, actorCollider.transform.position, actorCollider.transform.rotation, out Vector3 direction, out float distance))
                {
                    diagnostics = OverlapDiagnostic(giftCollider, actorColliders, activeActorColliders, boundsHits) +
                        ";penetrating=" + actorCollider.GetType().Name + "/" + actorCollider.name +
                        ";depth=" + distance.ToString("F3") + ";direction=" + direction.ToString("F2");
                    return true;
                }
            }
            diagnostics = OverlapDiagnostic(giftCollider, actorColliders, activeActorColliders, boundsHits) +
                ";firstBoundsHit=" + firstBoundsHit + ";penetrating=none";
            return false;
        }
        private static string OverlapDiagnostic(Collider giftCollider, int actorColliders, int activeActorColliders, int boundsHits) =>
            "giftCollider=" + giftCollider.GetType().Name + "/" + giftCollider.name + ":enabled=" + giftCollider.enabled +
            ":active=" + giftCollider.gameObject.activeInHierarchy + ":trigger=" + giftCollider.isTrigger +
            ":bounds=" + giftCollider.bounds.ToString() + ";actorColliders=" + actorColliders +
            ";activeActorColliders=" + activeActorColliders + ";boundsHits=" + boundsHits;
        private static bool TryNpcGiftKind(InteractableItems item, out NpcGiftKind kind)
        {
            if (item is Item_MapleSyrupCan) { kind = NpcGiftKind.MapleSyrup; return true; }
            if (item is Item_MapleWineBottle) { kind = NpcGiftKind.MapleWine; return true; }
            if (item is Item_HomemadeCoffeeMug) { kind = NpcGiftKind.HomemadeCoffee; return true; }
            kind = default;
            return false;
        }
        internal bool NearPeer(uint peer, Vector3 position, float range) => InRange(peer, position, range);
        internal ulong EnsureHostBinding(InteractableItems item)
        {
            if (item == null || !host) return 0;
            if (byItem.TryGetValue(item, out var existing)) return existing.Id;
            var prefab = Supported(item);
            if (prefab == null || !item.gameObject.scene.IsValid()) return 0;
            if (item.itemCollider == null || (item.HasARigidBody && item.rb == null)) item.CreateBasics();
            if (!(LocalHeld(item) || Physical(item))) return 0;
            return Register(item, prefab, LocalHeld(item) ? 0 : ItemLease.Unowned).Id;
        }
        internal void ReleaseHostItem(ulong id)
        {
            if (!host || !bindings.TryGetValue(id, out var b)) return;
            var lease = leases[id];
            if (lease.Owner != ItemLease.Unowned) ReleaseLease(id, lease.Owner, lease.Generation);
            if (b.Item == null) return;
            b.State = Capture(b); ConfigureBody(b, false);
            if (channel?.Ready == true) channel.Publish(id, b.State.Encode());
        }
        internal void PublishHostItem(ulong id)
        {
            if (!host || !bindings.TryGetValue(id, out var b) || b.Item == null) return;
            Publish(b); ConfigureBody(b, false);
        }
        internal bool HostReleaseTireChangerItem(InteractableItems item, uint peer, uint generation)
        {
            if (!host || session?.IsHost != true || !Ready || item == null ||
                !byItem.TryGetValue(item, out var binding) || !binding.State.Exists ||
                !leases.Owns(binding.Id, peer, generation)) return false;
            if (!ReleaseLease(binding.Id, peer, generation)) return false;
            binding.LastOwnerUpdate = Time.unscaledTime;
            binding.State = Capture(binding);
            ConfigureBody(binding, false);
            Publish(binding);
            return true;
        }
        internal bool HostGiveTireChangerItemToPeer(InteractableItems item, uint peer)
        {
            if (!host || session?.IsHost != true || !Ready || item == null || peer == 0 ||
                !session.Peers.ContainsKey(peer) || !byItem.TryGetValue(item, out var binding) ||
                !binding.State.Exists || leases[binding.Id].Owner != ItemLease.Unowned || PeerHolding(peer)) return false;
            var lease = leases[binding.Id];
            if (LocalHeld(item)) CancelLocal(item);
            if (!AcquireLease(binding.Id, peer, lease.Generation, 1)) return false;
            binding.LastOwnerUpdate = Time.unscaledTime;
            binding.State = Capture(binding);
            ConfigureBody(binding, false);
            Publish(binding);
            return true;
        }
        // Create a host-owned copy of a native factory item and lease it directly to the
        // requesting player. Callers must validate the world interaction before reaching here.
        internal ulong HostSpawnLeasedItem(GameObject factoryAsset, Vector3 position, Quaternion rotation, uint owner,
            ItemValues? initialValues = null)
        {
            if (!host || session?.IsHost != true || !Ready || TransfersBlocked || factoryAsset == null || PeerHolding(owner) ||
                (owner != 0 && !session.Peers.ContainsKey(owner)) ||
                (owner == 0 && gameplay.Player.IsHoldingAnObject)) return 0;
            var factory = factoryAsset.GetComponent<InteractableItems>();
            var prefab = factory == null ? null : Supported(factory);
            if (prefab == null || prefab.Asset == null || prefab.Type != factory!.GetType()) return 0;

            GameObject? go = null;
            Binding? registered = null;
            try
            {
                go = Object.Instantiate(prefab.Asset, position, rotation);
                var item = go.GetComponent<InteractableItems>();
                if (item == null || item.GetType() != prefab.Type || item.PrefabName != prefab.Path)
                    throw new InvalidOperationException("Native item factory no longer matches the shared prefab catalog: " + prefab.Path);
                item.isSpawnFromTheSaveLoadSystem = true;
                // Native full-crate factories serialize a placeholder and save as the ordinary
                // crate. An approved factory can provide its initialized values before the first
                // shared record/grant; replicas must never unpack that placeholder.
                if (initialValues.HasValue)
                {
                    ValidateValues(prefab, initialValues.Value);
                    ApplyValues(item, initialValues.Value, force: true);
                }
                item.CreateBasics();
                registered = Register(item, prefab, owner);
                ConfigureBody(registered, false);
                if (owner == 0) VanillaGrab(item);
                Publish(registered);
                return registered.Id;
            }
            catch (Exception ex)
            {
                // The durable world record is the commit point. A later native grip/body/audio
                // callback cannot erase that reward and let its caller mint it again on retry.
                if (go != null && go.GetComponent<InteractableItems>() is { } committedItem &&
                    byItem.TryGetValue(committedItem, out var committed) && EnsureNativeOutputRecord(committed))
                { Debug.LogException(ex); return committed.Id; }
                if (registered != null)
                {
                    if (registered.Item != null)
                    {
                        if (owner == 0) CancelLocal(registered.Item);
                        var lease = leases[registered.Id];
                        if (lease.Owner != ItemLease.Unowned) ReleaseLease(registered.Id, lease.Owner, lease.Generation);
                        identities.Unbind(registered.Id);
                        byItem.Remove(registered.Item);
                        registered.Item.gameObject.SetActive(false);
                        registered.Item = null;
                        registered.State = Capture(registered);
                        try { if (channel?.Ready == true) channel.Publish(registered.Id, registered.State.Encode()); }
                        catch (Exception rollbackError) { Debug.LogException(rollbackError); }
                    }
                }
                if (go != null) { go.SetActive(false); Object.Destroy(go); }
                throw;
            }
        }
        internal void RefreshReplicaBody(ulong id)
        {
            if (host || !bindings.TryGetValue(id, out var b) || b.Item == null || session == null) return;
            ConfigureBody(b, b.State.Lease.Owner == session.LocalPeerId);
        }
        // Live items with their catalog prefab, without capturing each item's full state.
        internal IEnumerable<(ulong Id, InteractableItems Item, ushort Prefab)> LivePrefabs
        {
            get { foreach (var b in bindings.Values) if (b.State.Exists && b.Item != null) yield return (b.Id, b.Item, b.Prefab); }
        }
        internal IEnumerable<(ulong Id, InteractableItems Item)> LiveItems => bindings.Values.Where(b => b.State.Exists && b.Item != null).Select(b => (b.Id, b.Item!));
        internal bool TryGetLocalToolLease(InteractableTools tool, out ulong id, out uint generation)
        {
            id = 0; generation = 0;
            if (host || !Ready || TransfersBlocked || tool == null || session == null ||
                gameplay.Player.Get_ActiveTool != tool || !byItem.TryGetValue(tool, out var binding) || !ActiveToolLeaseValid(binding)) return false;
            id = binding.Id; generation = binding.State.Lease.Generation;
            return true;
        }

        internal bool BeginBoltInstall(Hardware_Bolt bolt, out ulong id, out uint generation)
        {
            id = 0; generation = 0;
            if (host || !Ready || TransfersBlocked || !byItem.TryGetValue(bolt, out var b) || !OwnedLocally(b)) return false;
            if (!FlushLocalItemPose(bolt)) return false;
            id = b.Id; generation = b.State.Lease.Generation;
            // The hole owns the native copy now. Its explicit install request ends the host lease.
            b.Absorbed = true; identities.Unbind(b.Id); byItem.Remove(bolt); b.Item = null;
            return true;
        }

        internal bool HostInstallBolt(uint peer, ulong id, uint generation, BoltHoleDetails hole, out string reason)
        {
            reason = "";
            if (!host) reason = "not host";
            else if (TransfersBlocked) reason = "item transfers blocked";
            else if (!bindings.TryGetValue(id, out var b) || !b.State.Exists) reason = "shared bolt missing or consumed";
            else if (b.Item is not Hardware_Bolt bolt) reason = "shared item is not a bolt";
            else if (bolt.isPlaced) reason = "bolt already placed";
            else if (bolt.isBoltInMagneticTray != null) reason = "bolt in magnetic tray";
            else if (hole.isBoltInstalled) reason = "hole occupied";
            else if (bolt.boltSize != hole.boltSize) reason = "bolt size mismatch";
            else if (!leases.Owns(id, peer, generation)) reason = "bolt lease or generation";
            else if (!InRange(peer, hole.HoleLocation.position, 4.5f)) reason = "hole range or presence";
            else if (!HostItemInRange(peer, id, 4.5f)) reason = "canonical bolt range";
            if (reason.Length != 0) return false;
            var installed = bindings[id]; var nativeBolt = (Hardware_Bolt)installed.Item!;
            hole.Attach(nativeBolt, 0.01f); // Native hand, spanner and impact-gun insertion all start at 1%.
            AbsorbLocal(installed);
            return true;
        }

        internal bool TryGetLocalPartInstall(InteractableParts part, out ulong id, out uint generation)
        {
            id = 0; generation = 0;
            if (!byItem.TryGetValue(part, out var binding)) return false;
            id = binding.Id; generation = binding.State.Lease.Generation;
            return !host && Ready && !TransfersBlocked && binding.State.Exists && OwnedLocally(binding);
        }

        internal bool HostInstallPart(uint peer, ulong id, uint generation, Vehicle_PartSocket socket, Func<InteractableParts, bool> valid)
        {
            if (!host || TransfersBlocked || socket.isInstalled || !bindings.TryGetValue(id, out var binding) || !binding.State.Exists ||
                binding.Item is not InteractableParts part || part.isPlaced || !leases.Owns(id, peer, generation) ||
                !InRange(peer, socket.gameobject.transform.position, 4.5f) || !HostItemInRange(peer, id, 4.5f) || !valid(part)) return false;
            socket.InstallPart(part);
            if (socket.car_PartsType == Car_PartsType.Jackstand)
                socket.vehicleAttached.StartCoroutine(socket.ExtendCollider());
            return HostFinalizePartInstall(peer, id, generation, socket);
        }

        internal bool HostFinalizePartInstall(uint peer, ulong id, uint generation, Vehicle_PartSocket socket)
        {
            if (!host || !bindings.TryGetValue(id, out var binding)) return false;
            if (binding.Absorbed || !binding.State.Exists) return true;
            if (binding.Item is not InteractableParts part || !socket.isInstalled || socket.partInstalled != part || !part.isPlaced || !leases.Owns(id, peer, generation)) return false;
            AbsorbLocal(binding);
            return true;
        }

        internal void CompleteLocalPartInstall(InteractableParts part)
        {
            if (!byItem.TryGetValue(part, out var binding)) return;
            binding.Absorbed = true; identities.Unbind(binding.Id); byItem.Remove(part); binding.Item = null;
        }

        internal void RejectPartInstall(ulong id, InteractableParts part)
        {
            if (id == 0 && byItem.TryGetValue(part, out var found)) id = found.Id;
            if (channel == null || !bindings.TryGetValue(id, out var binding) || !channel.Records.TryGetValue(id, out var record))
            { DiscardNativePart(part); return; }
            part.TempPlaced = false; part.UpdatePartPlacement(false); part.transform.SetParent(null, true);
            var old = binding.Item;
            if (old != null && old != part) { byItem.Remove(old); DiscardNativePart((InteractableParts)old); }
            binding.Absorbed = false; binding.Item = part; byItem[part] = binding;
            identities.Unbind(id); identities.Bind(id, ItemState.Adapter, part.gameObject, prefabs[binding.Prefab].Path, false);
            Apply(id, Decode(record), true);
        }

        internal void DiscardNativePart(InteractableParts part)
        {
            CancelLocal(part); baseline.Add(part); ignored.Add(part); part.gameObject.SetActive(false); Object.Destroy(part.gameObject);
        }

        internal void RejectBoltInstall(ulong id, Hardware_Bolt bolt)
        {
            DiscardLocalBolt(bolt);
            if (!bindings.TryGetValue(id, out var b) || channel == null || !channel.Records.TryGetValue(id, out var record)) return;
            b.Absorbed = false; b.Item = null;
            Apply(id, Decode(record), true);
        }

        internal void DiscardLocalBolt(Hardware_Bolt bolt)
        {
            if (bolt == null) return;
            CancelLocal(bolt); bolt.gameObject.SetActive(false); Object.Destroy(bolt.gameObject);
        }

        internal void NativeBoltRemoved(Hardware_Bolt bolt)
        {
            baseline.Remove(bolt); ignored.Remove(bolt); ItemChanged(bolt); nextDiscovery = 0;
        }

        // Apply one bounded remote-tool effect to the host's leased copy, then
        // publish its native save values or a depletion tombstone immediately.
        internal bool HostMutateLeasedTool(uint peer, ulong id, uint generation, Type expectedType,
            Func<InteractableTools, bool> mutation, bool destroyWhenEmpty = false, bool publish = true, bool releaseOnSuccess = false)
        {
            if (!host || session?.IsHost != true || peer == 0 || !bindings.TryGetValue(id, out var binding) ||
                !binding.State.Exists || binding.Item is not InteractableTools tool || prefabs[binding.Prefab].Type != expectedType ||
                !leases.Owns(id, peer, generation) || !InRange(peer, tool.transform.position, 4.5f)) return false;
            if (!mutation(tool)) return false;
            binding.LastOwnerUpdate = Time.unscaledTime;
            if (destroyWhenEmpty && tool.Value1 <= 0)
            {
                ReleaseLease(id, peer, generation);
                identities.Unbind(id); byItem.Remove(tool); tool.gameObject.SetActive(false); Object.Destroy(tool.gameObject);
                binding.Item = null; binding.State = Capture(binding); channel!.Publish(id, binding.State.Encode());
                return true;
            }
            if (releaseOnSuccess)
            {
                if (!ReleaseLease(id, peer, generation)) return false;
                binding.State = Capture(binding);
                ConfigureBody(binding, false);
                if (publish) channel!.Publish(id, binding.State.Encode());
            }
            else if (publish) Publish(binding);
            return true;
        }

        internal bool HostSetLeasedToolPose(uint peer, ulong id, uint generation, Type expectedType, ItemPose pose)
        {
            try { pose.Validate(); } catch (InvalidDataException) { return false; }
            if (!host || session?.IsHost != true || peer == 0 || !bindings.TryGetValue(id, out var binding) ||
                !binding.State.Exists || binding.Item is not InteractableTools tool || prefabs[binding.Prefab].Type != expectedType ||
                !leases.Owns(id, peer, generation) || !InRange(peer, Position(pose), 4.5f)) return false;
            tool.transform.SetParent(null, true);
            tool.transform.SetPositionAndRotation(Position(pose), Rotation(pose));
            binding.HasTarget = false;
            binding.LastOwnerUpdate = Time.unscaledTime;
            return true;
        }

        // A native coilover/camber knob can be clicked without grabbing the loose part. Keep that
        // local native feedback, but send only the exact one-step value transition to the host.
        internal void NotifyLooseSuspensionValuesChanged(InteractableParts part, MountedVehiclePartKind kind,
            MountedPartTuning before)
        {
            if (applying || disposed || part == null ||
                (kind != MountedVehiclePartKind.Coilover && kind != MountedVehiclePartKind.TunedControlArm) ||
                !byItem.TryGetValue(part, out var binding) || binding.Item != part || !binding.State.Exists ||
                !prefabs.TryGetValue(binding.Prefab, out var prefab) || !IsLooseSuspensionPart(part, prefab.Type, kind)) return;

            var after = ReadSuspensionTuning(part);
            if (before.Equals(after)) return;
            if (!MountedVehiclePartValues.IsOneNativeStep(before, after, kind))
            { CancelLooseSuspensionEdits(binding, restore: true); return; }

            if (!Ready || session == null)
            {
                if (!host) CancelLooseSuspensionEdits(binding, restore: true);
                return;
            }
            if (host)
            {
                var lease = leases[binding.Id];
                if (session.IsHost && !TransfersBlocked &&
                    (lease.Owner == ItemLease.Unowned || lease.Owner == 0) &&
                    before.Equals(ReadSuspensionTuning(binding.State.Values)) &&
                    InRange(0, part.transform.position, 4.5f))
                {
                    binding.LastOwnerUpdate = Time.unscaledTime;
                    binding.State = Capture(binding);
                    channel!.Publish(binding.Id, binding.State.Encode());
                }
                else CancelLooseSuspensionEdits(binding, restore: true);
                return;
            }

            var guestLease = binding.State.Lease;
            if (TransfersBlocked || !CanHandleItems(gameplay.Get_PlayerState) ||
                (guestLease.Owner != ItemLease.Unowned && guestLease.Owner != session.LocalPeerId) ||
                guestLease.Generation == 0 || !NearLocal(part.transform.position, 4.5f))
            { CancelLooseSuspensionEdits(binding, restore: true); return; }

            var queue = binding.SuspensionEdits ??= new LooseSuspensionEditQueue();
            if (!queue.Enqueue(guestLease.Owner, guestLease.Generation, before, after, kind))
            { CancelLooseSuspensionEdits(binding, restore: true); return; }
            SendLooseSuspensionEdit(binding, Time.unscaledTime);
        }

        private static bool IsLooseSuspensionPart(InteractableParts part, Type prefabType, MountedVehiclePartKind kind)
        {
            var expected = kind == MountedVehiclePartKind.Coilover ? typeof(Part_Shock_Coilover) : typeof(Part_ControlArm_Tuned);
            return prefabType == expected && part.GetType() == expected && !part.isPlaced && !part.TempPlaced;
        }

        private static MountedPartTuning ReadSuspensionTuning(InteractableParts part) =>
            new MountedPartTuning(part.Value1, part.Value2, part.IntValue1, part.IntValue2);

        private static MountedPartTuning ReadSuspensionTuning(ItemValues values) =>
            new MountedPartTuning(values.Value1, values.Value2, values.IntValue1, values.IntValue2);

        private void RestoreSuspensionValues(Binding binding, InteractableParts part)
        {
            if (binding.Item == part && binding.State.Exists && !part.isPlaced && !part.TempPlaced)
                ApplyValues(part, binding.State.Values, force: true);
        }

        private void CancelLooseSuspensionEdits(Binding binding, bool restore)
        {
            binding.SuspensionEdits?.Clear();
            if (restore && binding.Item is InteractableParts part) RestoreSuspensionValues(binding, part);
        }

        private void ApplySuspensionDisplay(Binding binding)
        {
            if (binding.SuspensionEdits?.Display is not MountedPartTuning display || binding.Item is not InteractableParts part ||
                part == null || part.isPlaced || part.TempPlaced || !binding.State.Exists) return;
            var values = new ItemValues(display.SpringForce, display.Rebound, display.Height, display.Damper, binding.State.Values.Flags);
            ApplyValues(part, values, force: true);
        }

        private void SendLooseSuspensionEdit(Binding binding, double now)
        {
            var queue = binding.SuspensionEdits;
            if (host || queue == null) return;
            if (binding.Item is not InteractableParts part || part == null || !binding.State.Exists || part.isPlaced || part.TempPlaced)
            { CancelLooseSuspensionEdits(binding, restore: false); return; }
            var lease = binding.State.Lease;
            var progress = queue.Prepare(ReadSuspensionTuning(binding.State.Values), lease.Owner, lease.Generation,
                channel != null && channel.Records.TryGetValue(binding.Id, out var record) ? record.Revision : 0,
                now, 5, out var command);
            if (progress == LooseSuspensionEditProgress.LeaseChanged || progress == LooseSuspensionEditProgress.Rejected ||
                progress == LooseSuspensionEditProgress.TimedOut)
            { CancelLooseSuspensionEdits(binding, restore: true); return; }
            if (progress == LooseSuspensionEditProgress.Complete)
            { RestoreSuspensionValues(binding, part); return; }
            if (progress != LooseSuspensionEditProgress.RequestReady) return;
            try { channel!.Request(binding.Id, command.Encode(), useLease: true); }
            catch
            {
                queue.RequestFailed();
                CancelLooseSuspensionEdits(binding, restore: true);
                throw;
            }
        }

        private void LooseSuspensionEdit(uint peer, Binding binding, LooseSuspensionEditCommand command)
        {
            if (!host || disposed || session?.IsHost != true || !Ready || !bindings.ContainsKey(binding.Id)) return;

            var lease = leases[binding.Id];
            bool itemExists = binding.State.Exists && !binding.Absorbed && !binding.Releasing;
            bool expectedType = binding.Item is InteractableParts part && part != null &&
                prefabs.TryGetValue(binding.Prefab, out var prefab) && IsLooseSuspensionPart(part, prefab.Type, command.Kind);
            bool inRange = expectedType && InRange(peer, RulePosition(binding), 4.5f);
            var authoritative = binding.Item is InteractableParts nativePart && nativePart != null
                ? ReadSuspensionTuning(nativePart) : default;
            if (!LooseSuspensionEditCommand.CanApply(peer, lease.Owner, lease.Generation, TransfersBlocked,
                    itemExists, expectedType, inRange, authoritative, command))
            { ReplyLooseSuspensionEdit(peer, binding.Id, command.Nonce, LooseSuspensionEditQueue.RejectedReply); return; }

            var values = new ItemValues(command.After.SpringForce, command.After.Rebound,
                command.After.Height, command.After.Damper, binding.State.Values.Flags);
            bool wasApplying = applying;
            applying = true;
            try { ApplyValues((InteractableParts)binding.Item!, values); }
            finally { applying = wasApplying; }
            if (!ReadSuspensionTuning((InteractableParts)binding.Item!).Equals(command.After))
            { ReplyLooseSuspensionEdit(peer, binding.Id, command.Nonce, LooseSuspensionEditQueue.RejectedReply); return; }

            binding.LastOwnerUpdate = Time.unscaledTime;
            binding.State = Capture(binding);
            channel!.Publish(binding.Id, binding.State.Encode());
            ReplyLooseSuspensionEdit(peer, binding.Id, command.Nonce, LooseSuspensionEditQueue.AcceptedReply);
        }

        private void ReplyLooseSuspensionEdit(uint peer, ulong id, uint nonce, byte status)
        {
            if (!host || disposed || channel?.Ready != true || !channel.Records.TryGetValue(id, out var record)) return;
            channel.Reply(peer, id, LooseSuspensionEditQueue.EncodeReply(nonce, status, record.Revision));
        }

        internal bool HostHasLeasedTool(uint peer, ulong id, uint generation, Type expectedType) =>
            host && session?.IsHost == true && peer != 0 && bindings.TryGetValue(id, out var binding) &&
            binding.State.Exists && binding.Item is InteractableTools && prefabs[binding.Prefab].Type == expectedType &&
            leases.Owns(id, peer, generation);

        internal bool HostPublishItem(ulong id)
        {
            if (!host || session?.IsHost != true || !bindings.TryGetValue(id, out var binding) || !binding.State.Exists || binding.Item == null) return false;
            Publish(binding); return true;
        }

        // Easter progress is a shared one-shot pickup, not a food/needs consumption receipt.
        // Retire the binding before deferred native destruction or the next queued pickup.
        internal bool HostConsumeEasterEgg(ulong id)
        {
            if (!host || disposed || session?.IsHost != true || channel?.Ready != true || TransfersBlocked ||
                !bindings.TryGetValue(id, out var binding) || !binding.State.Exists ||
                binding.Item is not Item_Event_EasterEgg egg || egg == null) return false;
            var previous = Capture(binding);
            var lease = leases[id];
            if (lease.Owner != ItemLease.Unowned) ReleaseLease(id, lease.Owner, lease.Generation);
            var removed = new ItemState(binding.Prefab, false, leases[id], WorldPose(previous),
                previous.ScaleX, previous.ScaleY, previous.ScaleZ, values: previous.Values);
            byte[] payload = removed.Encode();
            binding.State = removed;
            byItem.Remove(egg);
            binding.Item = null;
            identities.Unbind(id);
            CancelLocal(egg);
            egg.gameObject.SetActive(false);
            Object.Destroy(egg.gameObject);
            channel.Publish(id, payload);
            return true;
        }

        // Host: the item, as its holder last placed it, lies within range of a point.
        internal bool HostItemNear(ulong id, Vector3 target, float range) =>
            host && bindings.TryGetValue(id, out var near) && near.State.Exists && near.Item != null &&
            (RulePosition(near) - target).sqrMagnitude <= range * range;
        internal bool HostItemInRange(uint peer, ulong id, float range)
        {
            if (!host || !bindings.TryGetValue(id, out var binding) || !binding.State.Exists || binding.Item == null) return false;
            return InRange(peer, RulePosition(binding), range);
        }
        internal bool HostItemInRange(uint peer, ulong id, float range, out string diagnostics)
        {
            if (!host || !bindings.TryGetValue(id, out var binding) || !binding.State.Exists || binding.Item == null)
            {
                diagnostics = "item=missing-or-unavailable";
                return false;
            }
            Vector3 itemPosition = RulePosition(binding);
            bool inRange = InRange(peer, itemPosition, range);
            if (peer == 0)
            {
                Vector3 hostPlayerPosition = gameplay.PlayerWalking.transform.position;
                float hostDistance = Vector3.Distance(itemPosition, hostPlayerPosition);
                bool hostWalking = CanHandleItems(gameplay.Get_PlayerState);
                diagnostics = "item=" + itemPosition.ToString("F2") + ";host=" + hostPlayerPosition.ToString("F2") +
                    ";hostState=" + gameplay.Get_PlayerState + ";distance=" + hostDistance.ToString("F2") +
                    ";limit=" + range.ToString("F2") + ";walking=" + hostWalking + ";near=" + inRange;
                return inRange;
            }
            if (!presence.TryGetValue(peer, out var presenceState))
            {
                diagnostics = "item=" + itemPosition.ToString("F2") + ";peerPose=missing;near=" + inRange;
                return inRange;
            }
            float age = Time.unscaledTime - presenceState.Time;
            Vector3 playerPosition = new Vector3(presenceState.Pose.X, presenceState.Pose.Y, presenceState.Pose.Z);
            float distance = Vector3.Distance(itemPosition, playerPosition);
            bool fresh = !(age > 2f);
            bool walking = CanHandleItems((PlayerState)presenceState.Pose.State);
            diagnostics = "item=" + itemPosition.ToString("F2") + ";peer=" + playerPosition.ToString("F2") +
                ";peerState=" + presenceState.Pose.State + ";poseAge=" + age.ToString("F2") +
                ";distance=" + distance.ToString("F2") + ";limit=" + range.ToString("F2") +
                ";fresh=" + fresh + ";walking=" + walking + ";near=" + inRange;
            return inRange;
        }
        // Decorations (lift included) and outdoor planters: a frozen flag in IntValue1 that removes the body.
        private static bool Freezable(Type type) => typeof(Furniture_Decoration).IsAssignableFrom(type) || type == typeof(Garden_OutdoorPlanter);
        private uint OwnerOf(ulong id) => host ? leases[id].Owner : bindings[id].State.Lease.Owner;
        internal bool OwnedBy(ulong id, uint peer) => bindings.ContainsKey(id) && OwnerOf(id) == peer;
        internal bool TryLocalTintTool(InteractableTools tool, out ulong id, out uint generation)
        {
            id = 0; generation = 0;
            if (tool == null || !byItem.TryGetValue(tool, out var binding) ||
                !binding.State.Exists || !OwnedLocally(binding)) return false;
            id = binding.Id; generation = binding.State.Lease.Generation;
            return true;
        }
        internal bool TryAuthorizeTintTool(uint peer, ulong id, uint generation, bool remove, out InteractableTools? tool)
        {
            tool = null;
            if (!host || !bindings.TryGetValue(id, out var binding) || !binding.State.Exists ||
                !leases.Owns(id, peer, generation)) return false;
            if (remove ? binding.Item is not Tool_WindowTintRemover : binding.Item is not Tool_WindowTint)
                return false;
            tool = (InteractableTools)binding.Item!;
            return remove || tool.IntValue1 > 0;
        }
        internal void CommitTintUse(ulong id)
        {
            if (!host || !bindings.TryGetValue(id, out var binding) || binding.Item is not Tool_WindowTint roll ||
                roll.IntValue1 <= 0) throw new InvalidOperationException("Tint roll cannot be consumed.");
            roll.IntValue1--;
            TintRollVisual(roll);
            Publish(binding);
        }
        private static void TintRollVisual(Tool_WindowTint roll) =>
            AccessTools.Method(typeof(Tool_WindowTint), "UpdateRoll")?.Invoke(roll, null);
        internal bool FreeOrOwnedBy(ulong id, uint peer) => bindings.ContainsKey(id) && (OwnerOf(id) == peer || OwnerOf(id) == ItemLease.Unowned);
        internal bool Unowned(ulong id) => bindings.ContainsKey(id) && OwnerOf(id) == ItemLease.Unowned;
        internal bool Loose(ulong id) => host && bindings.TryGetValue(id, out var b) && b.State.Exists &&
            b.Item != null && leases[id].Owner == ItemLease.Unowned;
        internal bool Removed(ulong id) => host && bindings.TryGetValue(id, out var b) && !b.State.Exists;

#if MONMP_DEVELOPMENT
        internal int TestRejectedConsumptions { get; private set; }


        internal bool TestWithholdConsumptionAcknowledgment { get; set; }


        internal int TestOriginalCount => hidden.Count(p => p.Value && p.Key != null &&
            (p.Key.transform.parent == null || p.Key.transform.parent.gameObject.activeInHierarchy));


        internal ulong TestLastId => bindings.Keys.Max();


        internal ulong TestFirstId => bindings.Keys.Min();


        internal ItemState TestState(ulong id) => bindings[id].State;


        internal ulong TestBoundId(InteractableItems item) => item != null && byItem.TryGetValue(item, out var b) ? b.Id : 0;


        internal ulong TestHeldPart(uint peer) => bindings.Values.Where(b => b.State.Exists && b.Item is InteractableParts &&
            b.State.Lease.Owner == peer).Select(b => b.Id).FirstOrDefault();


        internal void TestReplayItemSnapshot(ulong id)
        {
            if (host || channel == null || !channel.Records.TryGetValue(id, out var record))
                throw new InvalidOperationException("Guest item snapshot record is unavailable.");
            Snapshot(new Dictionary<ulong, WorldRecord> { [id] = record });
        }


        internal bool TestAdopting => adopting.Count > 0;


        internal InteractableItems? TestItem(ulong id) => bindings[id].Item;


        internal ulong TestLoosePart(string prefabName) => bindings.Values.Where(b => b.State.Exists && b.Item is InteractableParts part && !part.isPlaced &&
            part.PrefabName == prefabName && b.State.Lease.Owner == ItemLease.Unowned).Select(b => b.Id).DefaultIfEmpty().Max();


        internal bool TestRestored => hidden.All(p => p.Key == null || p.Key.gameObject.activeSelf == p.Value) &&
            (host || bindings.Values.All(b => b.Item == null || !b.Item.gameObject.activeSelf));


        internal InteractableItems TestSpawn(Vector3 position, ushort prefab = 1) => Object.Instantiate(prefabs[prefab].Asset, position, Quaternion.identity).GetComponent<InteractableItems>();


        internal void TestDestroy(ulong id) => Object.Destroy(bindings[id].Item!.gameObject);


        internal void TestClearDecorations() { foreach (ulong id in TestTaggedDecorations) if (bindings[id].Item != null) Object.Destroy(bindings[id].Item!.gameObject); }
#endif

        // Give native spawns time to start before an explicitly requested reconciliation.
        internal void DiscoverSoon() => discoverAtFrame = Time.frameCount + 2;
        private int discoverAtFrame = int.MaxValue;
        // Replicas of host items this guest has spawned (a split trunk's stand-in logs wait on them).
        internal int RemoteSpawns { get; private set; }
        internal event System.Action<InteractableItems>? RemoteSpawned;
        private Binding? replayUse;

#if MONMP_DEVELOPMENT
        internal bool TestChainsawRacked(ulong id) => bindings[id].Item is Tool_Chainsaw saw && saw.gameObject.activeInHierarchy &&
            chainsawHolders.Values.Any(h => ChainsawHoldTransform(h, out var t) && saw.transform.parent == t &&
                saw.transform.localPosition.sqrMagnitude < 0.0025f);


        internal bool TestChainsawCutPlaying(ulong id) => bindings.TryGetValue(id, out var binding) && ChainsawCutSourcePlaying(binding.Item);


        internal void TestSetChainsawCutting(ulong id, bool cutting)
        {
            var binding = bindings[id];
            if (!(binding.Item is Tool_Chainsaw chainsaw) || !OwnedLocally(binding))
                throw new InvalidOperationException("The chainsaw cut fixture requires its local item lease.");
            ChainsawCutting(chainsaw) = cutting;
        }


        internal Vector3 TestChainsawHold => chainsawHolders.Values.Select(h => ChainsawHoldTransform(h, out var t) ? t.position : Vector3.zero).First(p => p != Vector3.zero);


        internal void TestHostDock(ulong id)
        {
            var b = bindings[id]; var chainsaw = (Tool_Chainsaw)b.Item!;
            var holder = chainsawHolders.Values.First(h => ChainsawHoldTransform(h, out _));
            chainsaw.transform.SetParent(null, true);
            ChainsawHolderItem!.SetValue(holder, chainsaw);
            applyingChainsawAttachment = true;
            try { ChainsawHolderAttach!.Invoke(holder, new object[] { true }); }
            finally { applyingChainsawAttachment = false; }
            b.State = Capture(b); Publish(b);
        }


        internal bool TestLocalHeld(ulong id) => LocalHeld(bindings[id].Item);


        internal void TestGrab(ulong id) => gameplay.Player.GrabNewItemBought(bindings[id].Item!.gameObject);


        internal void TestDrop(bool thrown) => AccessTools.Method(typeof(PlayerInteractable), "UnGrabInteractable").Invoke(gameplay.Player, new object[] { thrown });


        internal void TestUse(ulong id)
        {
            AccessTools.Field(typeof(PlayerInteractable), "currentInteractableItem").SetValue(gameplay.Player, bindings[id].Item);
            AccessTools.Method(typeof(PlayerInteractable), "UseKeyDown").Invoke(gameplay.Player, null);
        }


        internal void TestPutAwayTool()
        {
            AccessTools.Method(typeof(PlayerInteractable), "UnEquipTool").Invoke(gameplay.Player, null);
            if (gameplay.Player.Get_HoldingItem != null) TestDrop(false);
        }


        internal bool TestAcknowledged(ulong id) => bindings[id].State.Consumption?.Acknowledged == true;


        internal void TestReplay(ulong id) => DeliverConsumption(bindings[id]);


        internal void TestRequestConsume(ulong id) => RequestConsume(bindings[id]);


        internal ulong TestFirstPrefab(ushort prefab) => bindings.Values.Where(b => b.Prefab == prefab && b.State.Exists).Min(b => b.Id);


        internal ulong[] TestVisibleLogsAfter(ulong after) => bindings.Values.Where(b => b.Id > after && b.State.Exists &&
            b.Item is Item_WoodLog log && log.gameObject.activeInHierarchy && log.itemCollider != null && log.itemCollider.enabled &&
            log.GetComponentsInChildren<MeshRenderer>().Any(renderer => renderer.enabled && !renderer.forceRenderingOff))
            .Select(b => b.Id).OrderBy(id => id).ToArray();

        internal ulong TestNewestPrefab(ushort prefab) => bindings.Values.Where(b => b.Prefab == prefab).Select(b => b.Id).DefaultIfEmpty().Max();


        internal ulong TestUnpackedPrefabAfter(ushort prefab, ulong after) => bindings.Values.Where(b => b.Prefab == prefab &&
            b.Id > after && !b.State.Exists && b.State.Lease.Generation >= 3 && (b.State.Values.Flags & ItemFlags.RemovalMarks) == 0)
            .Select(b => b.Id).DefaultIfEmpty().Max();


        internal ulong TestBoltInTray(ulong tray) => bindings.Values
            .Where(b => b.Item is Hardware_Bolt && b.State.Exists && b.State.ContainerEntity == tray).Select(b => b.Id).FirstOrDefault();


        internal ulong TestNewestBoltColor(int color) => bindings.Values
            .Where(b => b.Item is Hardware_Bolt && b.State.Exists && b.State.Values.IntValue1 == color).Select(b => b.Id).DefaultIfEmpty().Max();


        internal string TestHardwareBoltPath => prefabs.Values.Where(p => p.Type == typeof(Hardware_Bolt)).OrderBy(p => p.Id).First().Path;


        internal ushort TestPrefab(string path) => prefabs.Values.Single(p => p.Path == path).Id;


        internal ushort[] TestPrefabs(Type type) => prefabs.Values.Where(p => p.Type == type).OrderBy(p => p.Path, StringComparer.Ordinal).Select(p => p.Id).ToArray();


        internal int TestPrefabChildCount<T>(ushort id) where T : Component => prefabs[id].Asset.GetComponentsInChildren<T>(true).Length;


        internal ushort[] TestSimplePrefabs => prefabs.Values.Where(p => ItemPrefabCatalog.Simple(p.Type)).Select(p => p.Id).OrderBy(id => id).ToArray();


        internal ushort TestTypePrefab(Type type) => prefabs.Values.Single(p => p.Type == type).Id;


        internal bool TestConsumable(ushort prefab) => (prefabs[prefab].Features & PrefabFeatures.Consume) != 0;


        internal Type TestType(ushort prefab) => prefabs[prefab].Type;


        internal ulong TestTaggedItem(ushort prefab, float scale) => bindings.Values.Where(b => b.Prefab == prefab &&
            Math.Abs(b.State.ScaleX - scale) < 0.001f).Select(b => b.Id).DefaultIfEmpty().Max();


        internal ulong TestNewest(Type type) => bindings.Values.Where(b => b.State.Exists && b.Item != null && b.Item.GetType() == type).Select(b => b.Id).DefaultIfEmpty().Max();
#endif

        // Guest: drops a cuttings pile as its loppers do (Patches.GuestCuttingSpawnPatch).

#if MONMP_DEVELOPMENT
        internal void TestDropCuttings(Tree_Type type, Vector3 position, int count)
        {
            var cutting = Object.Instantiate(Singleton<GameAssets>.i.GetTreeCuttings(type), position, Quaternion.identity).GetComponent<Garden_TreeCuttings>();
            cutting.ShowCuttingSphere(show: true);
            cutting.IntValue1 = count; cutting.isSpawnFromTheSaveLoadSystem = true;
            LocalSpawned();
        }
#endif

        // The shared loose piles of a tree type within the native prune's 60 m reach, and their total.

#if MONMP_DEVELOPMENT
        internal (int Piles, int Total) TestCuttingPiles(Tree_Type type, Vector3 around)
        {
            var piles = bindings.Values.Where(b => b.State.Exists && b.Item is Garden_TreeCuttings c && c.Get_TreeType == type &&
                (c.transform.position - around).sqrMagnitude <= 60f * 60f).Select(b => (Garden_TreeCuttings)b.Item!).ToArray();
            return (piles.Length, piles.Sum(c => c.IntValue1));
        }


        internal ulong TestSpawnSyrupCrate(Vector3 position, float marker, int cans)
        {
            var prefab = prefabs.Values.Where(p => p.Type == typeof(Pack_MapleSyrupCrate)).OrderBy(p => p.Path, StringComparer.Ordinal).First();
            var go = Object.Instantiate(prefab.Asset, position, Quaternion.identity);
            go.transform.localScale = Vector3.one * marker;
            var crate = (Pack_MapleSyrupCrate)go.GetComponent<InteractableItems>();
            crate.isSpawnFromTheSaveLoadSystem = true;
            crate.IntValue1 = 0; crate.Value1 = cans;
            crate.CreateBasics();
            UpdatePack.Invoke(crate, null);
            if (crate.rb != null) crate.rb.isKinematic = true;
            return Register(crate, prefab, ItemLease.Unowned).Id;
        }


        internal ulong TestNewestPack() => bindings.Values.Where(b => b.State.Exists && b.Item is Pack_Bolts12).Select(b => b.Id).DefaultIfEmpty().Max();


        internal float TestPackCount(ulong id) => bindings[id].Item is InteractablePacks pack ? pack.Value1 : -1;


        internal GameObject? TestGetOnePackedItem(ulong id) => ((InteractablePacks)bindings[id].Item!).GetOnePackedItem();


        internal bool TestPendingUse(ulong id) => bindings[id].PendingUse;


        internal int TestFailedPackGrants => testFailedPackGrants;


        internal ulong TestSpawnMagneticTray(Vector3 position)
        {
            var prefab = prefabs.Values.Single(p => p.Type == typeof(Tool_MagneticTray) && p.Path == "Tool/MagneticTray");
            var item = Object.Instantiate(prefab.Asset, position, Quaternion.identity).GetComponent<InteractableItems>();
            item.isSpawnFromTheSaveLoadSystem = true; item.CreateBasics();
            return Register(item, prefab, ItemLease.Unowned).Id;
        }


        internal ulong TestSpawnBolt(Vector3 position, string path, int color)
        {
            var prefab = prefabs.Values.Single(p => p.Type == typeof(Hardware_Bolt) && p.Path == path);
            var item = Object.Instantiate(prefab.Asset, position, Quaternion.identity).GetComponent<Hardware_Bolt>();
            item.isSpawnFromTheSaveLoadSystem = true; item.IntValue1 = color; item.CreateBasics(); item.SetBoltColor();
            return Register(item, prefab, ItemLease.Unowned).Id;
        }


        internal void TestAllowTrayCollisionNow(ulong bolt)
        {
            if (bindings[bolt].Item is not Hardware_Bolt item) throw new InvalidOperationException("Test bolt is unavailable.");
            BoltTrayCooldown.SetValue(item, Time.realtimeSinceStartup - 3);
        }


        internal void TestTriggerTrayCollision(ulong tray, ulong bolt)
        {
            if (bindings[tray].Item is not Tool_MagneticTray magneticTray || bindings[bolt].Item is not Hardware_Bolt hardwareBolt)
                throw new InvalidOperationException("Test tray collision target is unavailable.");
            TrayTriggerEnter.Invoke(magneticTray, new object[] { hardwareBolt.itemCollider });
        }


        internal bool TestRequestTrayAttach(ulong tray, ulong bolt) => MagneticTrayAdd(
            (Tool_MagneticTray)bindings[tray].Item!, (Hardware_Bolt)bindings[bolt].Item!);


        internal bool TestRequestTrayRemoveAll(ulong tray) => MagneticTrayRemoveAll((Tool_MagneticTray)bindings[tray].Item!);


        internal void TestTrayCommandAs(uint peer, ulong tray, MagneticTrayCommand command) =>
            TrayCommand(peer, bindings[tray], MagneticTrayCommand.Decode(command.Encode()));


        internal void TestDestroyItem(ulong id)
        {
            if (bindings[id].Item != null) Object.Destroy(bindings[id].Item!.gameObject);
        }


        internal bool TestSetOccupiedHand()
        {
            var blocker = hidden.Keys.FirstOrDefault(item => item != null);
            if (blocker == null || gameplay.Player.Get_HoldingItem != null) return false;
            AccessTools.Field(typeof(PlayerInteractable), "HoldingItem").SetValue(gameplay.Player, blocker);
            return true;
        }


        internal void TestClearOccupiedHand()
        {
            var held = gameplay.Player.Get_HoldingItem;
            if (held != null && hidden.ContainsKey(held))
                AccessTools.Field(typeof(PlayerInteractable), "HoldingItem").SetValue(gameplay.Player, null);
        }


        internal ulong TestHeldEntity => gameplay.Player.Get_HoldingItem == null ? 0 : EntityOf(gameplay.Player.Get_HoldingItem);


        internal void TestCommandAs(uint peer, ulong id, ItemAction action, uint generation) =>
            Command(peer, id, new ItemCommand(action, generation, WorldPose(bindings[id].State)).Encode());


        internal ulong TestSpawnPack(Vector3 position)
        {
            var prefab = prefabs.Values.Single(p => p.Type == typeof(Pack_Bolts12) && p.Path == "Pack/Pack_Bolt12");
            var go = Object.Instantiate(prefab.Asset, position, Quaternion.identity);
            var pack = (Pack_Bolts12)go.GetComponent<InteractableItems>();
            // Prevent InteractablePacks.Start from refilling this test pack to Value1_Max.
            pack.isSpawnFromTheSaveLoadSystem = true;
            pack.Value1 = 2;
            pack.CreateBasics();
            UpdatePack.Invoke(pack, null);
            return Register(pack, prefab, ItemLease.Unowned).Id;
        }


        internal (ulong Roll, ulong Remover) TestSpawnTintTools(Vector3 position)
        {
            if (!host || channel == null) throw new InvalidOperationException("Tint fixture requires an attached host.");
            var rollPrefab = prefabs.Values.Where(p => p.Type == typeof(Tool_WindowTint))
                .OrderBy(p => p.Path, StringComparer.Ordinal).First();
            var removerPrefab = prefabs.Values.Single(p => p.Type == typeof(Tool_WindowTintRemover));
            var roll = (Tool_WindowTint)Object.Instantiate(rollPrefab.Asset, position, Quaternion.identity).GetComponent<InteractableItems>();
            roll.transform.localScale = Vector3.one * 0.733f;
            roll.IntValue1 = 2;
            var remover = (Tool_WindowTintRemover)Object.Instantiate(removerPrefab.Asset, position + Vector3.right * 0.5f,
                Quaternion.identity).GetComponent<InteractableItems>();
            remover.transform.localScale = Vector3.one * 0.734f;
            // Keep the isolated fixture beside its target until the guest's
            // walking pose arrives and the host can approve pickup.
            roll.CreateBasics(); remover.CreateBasics();
            if (roll.rb != null) roll.rb.isKinematic = true;
            if (remover.rb != null) remover.rb.isKinematic = true;
            return (Register(roll, rollPrefab, ItemLease.Unowned).Id,
                Register(remover, removerPrefab, ItemLease.Unowned).Id);
        }


        internal ulong TestSpawnSprayCan(Vector3 position, float level, int customColor)
        {
            if (!host || channel == null) throw new InvalidOperationException("Spray fixture requires an attached host.");
            var prefab = ResolveNativeSprayCanPrefab();
            var can = (Tool_SprayPaintCan)Object.Instantiate(prefab.Asset, position, Quaternion.identity).GetComponent<InteractableItems>();
            can.isSpawnFromTheSaveLoadSystem = true;
            can.transform.localScale = Vector3.one * 0.732f;
            can.Value1 = level;
            can.IntValue1 = customColor;
            // Isolate color application while retaining the actual native spray can,
            // its native fill setter, and the normal host lease/replication path.
            (AccessTools.Field(typeof(Tool_SprayPaintCan), "Smoothness") ?? throw new InvalidOperationException("Spray smoothness field is unavailable.")).SetValue(can, 0f);
            (AccessTools.Field(typeof(Tool_SprayPaintCan), "Metalness") ?? throw new InvalidOperationException("Spray metalness field is unavailable.")).SetValue(can, 0f);
            (AccessTools.Field(typeof(Tool_SprayPaintCan), "Override_PaintColorAndSmooth") ?? throw new InvalidOperationException("Spray override field is unavailable.")).SetValue(can, true);
            can.CreateBasics();
            if (can.rb != null) can.rb.isKinematic = true;
            return Register(can, prefab, ItemLease.Unowned).Id;
        }


        internal ulong TestSpawnPresentationTool(Type type, Vector3 position)
        {
            if (!host || channel == null || !typeof(InteractableTools).IsAssignableFrom(type))
                throw new InvalidOperationException("Equipped presentation fixture requires a host and native tool type.");
            var matches = prefabs.Values.Where(p => p.Type == type &&
                !p.Path.Contains("Gold", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Id).ToArray();
            // Native tools can have several legitimate ordinary catalog variants (the
            // harvest basket has two). Choose a real registered prefab deterministically.
            if (matches.Length == 0) throw new InvalidOperationException("Expected a registered ordinary native tool prefab: " + type.Name);
            var prefab = matches[0];
            Debug.Log("MONMP_EQUIPPED_SCALE: registered fixture prefab=" + prefab.Id + "; path=" + prefab.Path +
                "; type=" + type.Name + "; eligibleVariants=" + matches.Length);
            var tool = (InteractableTools)Object.Instantiate(prefab.Asset, position, Quaternion.identity).GetComponent<InteractableItems>();
            tool.isSpawnFromTheSaveLoadSystem = true;
            tool.CreateBasics();
            if (tool.rb != null) tool.rb.isKinematic = true;
            return Register(tool, prefab, ItemLease.Unowned).Id;
        }


        internal ulong TestSpawnInflationWheel(Vector3 position)
        {
            if (!host || channel == null) throw new InvalidOperationException("Tire inflation fixture requires an attached host.");
            var prefab = prefabs.Values.Where(p => p.Type == typeof(Part_Driveline_Wheel))
                .OrderBy(p => p.Path, StringComparer.Ordinal).First();
            var wheel = (Part_Driveline_Wheel)Object.Instantiate(prefab.Asset, position, Quaternion.identity).GetComponent<InteractableItems>();
            wheel.isSpawnFromTheSaveLoadSystem = true;
            wheel.IntValue1 = 1;
            wheel.Value1 = 20f;
            wheel.CreateBasics();
            if (wheel.rb != null) wheel.rb.isKinematic = true;
            return Register(wheel, prefab, ItemLease.Unowned).Id;
        }


        internal ulong TestSpawnRustRepairKit(Vector3 position)
        {
            if (!host || channel == null) throw new InvalidOperationException("Rust repair fixture requires an attached host.");
            var matches = prefabs.Values.Where(p => p.Type == typeof(Tool_RustRepairKit))
                .OrderBy(p => p.Path, StringComparer.Ordinal).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("Rust repair fixture requires one catalogued native Tool_RustRepairKit prefab; found " +
                    matches.Length + " at [" + string.Join(", ", matches.Select(p => p.Path)) + "].");
            var prefab = matches[0];
            var kit = (Tool_RustRepairKit)Object.Instantiate(prefab.Asset, position, Quaternion.identity)
                .GetComponent<InteractableItems>();
            kit.isSpawnFromTheSaveLoadSystem = true;
            kit.transform.localScale = Vector3.one * 0.731f;
            kit.CreateBasics();
            if (kit.rb != null) kit.rb.isKinematic = true;
            return Register(kit, prefab, ItemLease.Unowned).Id;
        }


        internal ulong TestSpawnDentRepairKit(Vector3 position)
        {
            if (!host || channel == null) throw new InvalidOperationException("Dent repair fixture requires an attached host.");
            var matches = prefabs.Values.Where(p => p.Type == typeof(Tool_DentRepairKit))
                .OrderBy(p => p.Path, StringComparer.Ordinal).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("Dent repair fixture requires one catalogued native Tool_DentRepairKit prefab; found " +
                    matches.Length + " at [" + string.Join(", ", matches.Select(p => p.Path)) + "].");
            var prefab = matches[0];
            var kit = (Tool_DentRepairKit)Object.Instantiate(prefab.Asset, position, Quaternion.identity)
                .GetComponent<InteractableItems>();
            kit.isSpawnFromTheSaveLoadSystem = true;
            kit.transform.localScale = Vector3.one * 0.736f;
            kit.CreateBasics();
            if (kit.rb != null) kit.rb.isKinematic = true;
            return Register(kit, prefab, ItemLease.Unowned).Id;
        }
#endif


        private Prefab ResolveNativeSprayCanPrefab()
        {
            var factoryAsset = Singleton<GameAssets>.i != null ? Singleton<GameAssets>.i.SprayPaintCan : null;
            var factoryItem = factoryAsset != null ? factoryAsset.GetComponent<InteractableItems>() : null;
            string factoryPath = factoryItem == null ? "<missing>" : factoryItem.PrefabName;
            var matches = prefabs.Values.Where(p => p.Type == typeof(Tool_SprayPaintCan) &&
                string.Equals(p.Path, factoryPath, StringComparison.Ordinal)).ToArray();
            if (factoryItem is not Tool_SprayPaintCan || matches.Length != 1)
            {
                string catalogPaths = string.Join(", ", catalog.Entries.Values
                    .Where(entry => entry.Type == typeof(Tool_SprayPaintCan))
                    .Select(entry => entry.Definition.Path)
                    .OrderBy(path => path, StringComparer.Ordinal));
                throw new InvalidOperationException("Could not resolve exactly one paired spray-can prefab from the native " +
                    "GameAssets.SprayPaintCan factory. Native path='" + factoryPath + "', matching shared prefabs=" +
                    matches.Length + ", catalogued Tool_SprayPaintCan paths=[" + catalogPaths + "].");
            }
            return matches[0];
        }


#if MONMP_DEVELOPMENT
        internal ulong TestNewestTintTool(bool remover) => bindings.Values
            .Where(b => b.State.Exists && Math.Abs(b.State.ScaleX - (remover ? 0.734f : 0.733f)) < 0.001f &&
                (remover ? b.Item is Tool_WindowTintRemover : b.Item is Tool_WindowTint))
            .Select(b => b.Id).DefaultIfEmpty().Max();


        internal ulong TestNewestSprayCan() => bindings.Values
            .Where(b => b.State.Exists && Math.Abs(b.State.ScaleX - 0.732f) < 0.001f && b.Item is Tool_SprayPaintCan)
            .Select(b => b.Id).DefaultIfEmpty().Max();


        internal bool TestPickupPending(ulong id) => bindings[id].PendingGrab != GrabMode.None;


        internal string TestPickupStatus(ulong id)
        {
            var b = bindings[id];
            var itemPosition = b.Item == null ? Vector3.zero : b.Item.transform.position;
            float distance = b.Item == null ? -1 : Vector3.Distance(gameplay.PlayerWalking.transform.position, itemPosition);
            return "owner=" + b.State.Lease.Owner + ",generation=" + b.State.Lease.Generation +
                ",pending=" + b.PendingGrab + ",held=" + LocalHeld(b.Item) +
                ",handOccupied=" + gameplay.Player.IsHoldingAnObject + ",playerState=" + gameplay.Get_PlayerState +
                ",distance=" + distance.ToString("F2") + ",canGrab=" + (b.Item != null && b.Item.canGrab());
        }


        internal Item_Poutine[] TestOriginalFoods => hidden.Keys.OfType<Item_Poutine>().Where(item => item != null).ToArray();


        internal ushort[] TestDecorationPrefabs => prefabs.Values.Where(p => p.Type == typeof(Furniture_Decoration)).Select(p => p.Id).OrderBy(id => id).ToArray();


        internal ushort TestDecorationTargetPrefab => prefabs.Values.Where(p => p.Type == typeof(Furniture_Decoration) &&
            DecorationState.CanFreeze((Furniture_Decoration)p.Asset.GetComponent<InteractableItems>()))
            .OrderByDescending(p => DecorationState.HeldObjects((Furniture_Decoration)p.Asset.GetComponent<InteractableItems>()).Length).First().Id;


        internal ulong[] TestTaggedDecorations => bindings.Values.Where(b => b.State.Exists && prefabs[b.Prefab].Type == typeof(Furniture_Decoration) &&
            Math.Abs(b.State.ScaleX - 0.66f) < 0.001f).Select(b => b.Id).ToArray();


        internal void TestCommand(ulong id, ItemAction action, uint generation) =>
            channel!.Request(id, new ItemCommand(action, generation, WorldPose(bindings[id].State)).Encode(), true);
#endif

        internal ushort PurchasePrefab(GameObject asset)
        {
            var item = asset == null ? null : asset.GetComponent<InteractableItems>();
            if (item == null) return 0;
            var prefab = Supported(item);
            return prefab != null && prefab.Asset == asset ? prefab.Id : (ushort)0;
        }
        internal ushort StorePurchasePrefab(GameObject asset, out bool prefilledCanCrate)
        {
            prefilledCanCrate = false;
            ushort id = PurchasePrefab(asset);
            if (id != 0) return id;
            var canonical = PrefilledSyrupCanCrate.CanonicalFactory(asset);
            if (canonical == null) return 0;
            id = PurchasePrefab(canonical);
            prefilledCanCrate = id != 0;
            return id;
        }
        internal Func<InteractableItems, bool>? ClaimRemoved;
        private static void Vanish(InteractableItems item)
        {
            foreach (var collider in item.GetComponentsInChildren<Collider>()) collider.enabled = false;
            if (item.rb != null) { item.rb.isKinematic = true; item.rb.detectCollisions = false; }
            MonMPPlugin.Instance!.StartCoroutine(VanishRoutine(item.gameObject));
        }
        private static System.Collections.IEnumerator VanishRoutine(GameObject go)
        {
            var from = go.transform.localScale;
            for (float t = 0f; t < 0.3f; t += Time.deltaTime)
            {
                if (go == null) yield break;
                go.transform.localScale = from * (1f - Mathf.SmoothStep(0f, 1f, t / 0.3f));
                yield return null;
            }
            if (go != null) { go.SetActive(false); Object.Destroy(go); }
        }
        // Host: where a guest-owned item is heading (its latest owner pose), else where it is.
        internal bool TryOwnerPosition(ulong id, out Vector3 position)
        {
            position = default;
            if (!bindings.TryGetValue(id, out var b) || b.Item == null) return false;
            position = b.HasTarget ? b.TargetPosition : b.Item.transform.position;
            return true;
        }
        internal bool DeliverPurchase(InteractableItems item, uint peer, out ulong entity, out bool held)
        {
            entity = 0; held = false;
            if (!host) return false;
            if (!byItem.TryGetValue(item, out var b))
            {
                if (EnsureHostBinding(item) == 0 || !byItem.TryGetValue(item, out b)) return false;
            }
            entity = b.Id;
            if (session!.Peers.ContainsKey(peer) && !PeerHolding(peer) && (peer != 0 || !gameplay.Player.IsHoldingAnObject))
                Command(peer, b.Id, new ItemCommand(ItemAction.Acquire, b.State.Lease.Generation, WorldPose(b.State)).Encode());
            held = b.State.Lease.Owner == peer; return true;
        }
        internal bool PurchaseInRange(uint peer, Vector3 position) => InRange(peer, position, 4);
        // A peer spent a held (or nearby loose) item on a host-built structure: the host destroys its
        // copy and synchronously retires its binding, so another receiver cannot spend it again
        // before native destruction or the next capture. The removal ends the guest's copy too.
        internal bool TakeFrom(uint peer, ulong id, Type type)
        {
            if (!host || !bindings.TryGetValue(id, out var b) || !b.State.Exists || b.Item == null || !type.IsInstanceOfType(b.Item)) return false;
            var owner = leases[id].Owner;
            if (owner != peer && !(owner == ItemLease.Unowned && InRange(peer, b.Item.transform.position, 6))) return false;
            if (peer == 0) CancelLocal(b.Item);
            b.Item.gameObject.SetActive(false); Object.Destroy(b.Item.gameObject);
            AbsorbLocal(b);
            return true;
        }
        public IEnumerable<WorldRecord> Descriptors => catalog.Definitions.Entries.Values.Select(p => p.Descriptor());

        public SharedItems(Gameplay gameplay, bool host, string diagnostics, ConsumptionInbox consumption, NativeEntityRegistry identities)
        {
            this.gameplay = gameplay; this.host = host; this.consumption = consumption; this.identities = identities;
            if (gameplay.Player.IsHoldingAnObject) throw new InvalidOperationException("Put down your item/tool before hosting or joining.");
            catalog = ItemPrefabCatalog.Load(diagnostics);
            AddPrefab(1, "Drink/MapleSyrup", typeof(Item_MapleSyrup));
            AddPrefab(2, "Drink/TomHouston_Cup", typeof(Item_TomHoustonCup));
            foreach (var entry in catalog.Entries.Values.Where(e => e.Type == typeof(Item_Lantern) || e.Type == typeof(Item_ElectricCoffeePercolator) || e.Type == typeof(Furniture_Decoration) || e.Type == typeof(Furniture_CarLift) || e.Type == typeof(Garden_OutdoorPlanter) || e.Type == typeof(Item_Poutine) ||
                ((ItemPrefabCatalog.Simple(e.Type) || ItemPrefabCatalog.General(e.Type)) && (e.Definition.Features & PrefabFeatures.Carry) != 0)))
                AddPrefab(entry.Definition.Id, entry.Definition.Path, entry.Type);
            foreach (var p in prefabs.Values)
            {
                ulong descriptor = catalog.Definitions.Entries[p.Id].Descriptor().Id;
                descriptorByPrefab.Add(p.Id, descriptor); prefabByDescriptor.Add(descriptor, p.Id);
            }
            NativeItemDiscovery.Begin(this);
            try
            {
                if (host) Discover();
                DiscoverChainsawHolders();
#if MONMP_DEVELOPMENT
                File.WriteAllLines(Path.Combine(diagnostics, "shared-items.tsv"), new[] { "prefab-id\tresource\ttype" }.Concat(prefabs.Values.Select(p => p.Id + "\t" + p.Path + "\t" + p.Type.Name)));
#endif
            }
            catch { NativeItemDiscovery.End(this); discoveryItems.Clear(); throw; }
        }
        private void DiscoverChainsawHolders()
        {
            foreach (var holder in Object.FindObjectsOfType<Vehicle_Chainsaw_Holder>(true)
                .Where(value => value != null && value.gameObject.scene.IsValid())
                .OrderBy(value => RuntimeProbe.CanonicalPath(value.transform), StringComparer.Ordinal))
            {
                string path = RuntimeProbe.CanonicalPath(holder.transform);
                if (!ChainsawHoldTransform(holder, out _))
                    throw new InvalidOperationException("Chainsaw holder is missing its native hold transform: " + path);
                ulong id = StaticId.Hash("monmp/chainsaw-holder/v1/" + path);
                if (id == 0 || chainsawHolders.ContainsKey(id))
                    throw new InvalidOperationException("Chainsaw-holder identity collision: " + path);
                chainsawHolders.Add(id, holder); chainsawHolderIds.Add(holder, id);
                var contacts = holder.gameObject.AddComponent<ChainsawHolderContacts>();
                contacts.Initialize(this, holder); chainsawHolderContacts.Add(holder, contacts);
            }
        }
        private static bool ChainsawHoldTransform(Vehicle_Chainsaw_Holder holder, out Transform hold)
        {
            hold = ChainsawHolderLocation?.GetValue(holder) as Transform ?? null!;
            return hold != null;
        }
        private bool AttachChainsawReplica(Binding binding, ItemPose pose)
        {
            if (!(binding.Item is Tool_Chainsaw chainsaw) || chainsaw.IntValue1 != 1) return false;
            Vehicle_Chainsaw_Holder? selected = null;
            float bestDistance = float.PositiveInfinity;
            var position = Position(pose);
            foreach (var candidate in chainsawHolders.Values)
            {
                if (!ChainsawHoldTransform(candidate, out var hold)) continue;
                float distance = (hold.position - position).sqrMagnitude;
                if (distance < bestDistance) { bestDistance = distance; selected = candidate; }
            }
            if (selected == null || bestDistance > 4f) return false;
            if (ChainsawHoldTransform(selected, out var selectedHold) && chainsaw.transform.parent == selectedHold) return true;
            if (ChainsawHolderAttach == null || ChainsawHolderItem == null)
                throw new InvalidOperationException("Native chainsaw-holder attach members are unavailable.");
            ChainsawHolderItem.SetValue(selected, chainsaw);
            bool previous = applyingChainsawAttachment; applyingChainsawAttachment = true;
            try { ChainsawHolderAttach.Invoke(selected, new object[] { true }); }
            finally { applyingChainsawAttachment = previous; }
            return ChainsawHoldTransform(selected, out selectedHold) && chainsaw.transform.parent == selectedHold;
        }
        private void AddPrefab(ushort id, string path, Type type)
        {
            var definition = catalog.Entries[id];
            if (definition.Definition.Path != path || definition.Type != type) throw new InvalidOperationException("Carry prefab does not match the local catalog.");
            var asset = definition.Asset;
            var item = asset != null ? asset.GetComponent<InteractableItems>() : null;
            if (item == null || item.GetType() != type || item.PrefabName != path || item.GetComponent<Collider>() == null || item.GetComponentsInChildren<MeshRenderer>(true).Length == 0 ||
                asset!.GetComponentsInChildren<InteractableItems>(true).Length != 1 || definition.Rigidbodies > 1)
                throw new InvalidOperationException("Unsupported carry prefab: " + path);
            prefabs.Add(id, new Prefab { Id = id, Path = path, Type = type, Asset = asset!, Features = definition.Definition.Features });
            if (!prefabByKey.ContainsKey((type, path))) prefabByKey[(type, path)] = prefabs[id];
        }
        private readonly Dictionary<(Type, string), Prefab> prefabByKey = new Dictionary<(Type, string), Prefab>();
        private Prefab? Supported(InteractableItems item) => item.PrefabName != null && prefabByKey.TryGetValue((item.GetType(), item.PrefabName), out var prefab) ? prefab : null;
        // Fishing output may use a native result prefab that this build deliberately does not
        // admit as an interactable world item. Keep the ordinary catalogued result path intact,
        // and let RemoteFishing use a bounded visual record only for other native array entries.
        internal bool SupportsFishingTrashItem(InteractableItems item, ushort template)
        {
            if (item == null) return false;
            var prefab = Supported(item);
            return prefab != null && (General(prefab) || ItemPrefabCatalog.Simple(prefab.Type)) &&
                FishingTrashTemplateCatalog.Matches(template, prefab.Type, prefab.Path);
        }
        internal bool SupportsFishingTrashTemplate(ushort template)
        {
            var asset = FishingTrashTemplateCatalog.TemplateAt(template);
            var item = asset == null ? null : asset.GetComponent<InteractableItems>();
            return item != null && SupportsFishingTrashItem(item, template);
        }
        private static bool General(Prefab prefab) => ItemPrefabCatalog.General(prefab.Type);
        // Installed into world structure: a socket's part or a hole's bolt belongs to that structure's adapter.
        private static bool Placed(InteractableItems item) => (item is InteractableParts part && part.isPlaced) || (item is Hardware_Bolt bolt && bolt.isPlaced);
        private static bool TrayContained(InteractableItems item) => item is Hardware_Bolt bolt &&
            (bolt.isBoltInMagneticTray != null || bolt.transform.parent?.GetComponent<Tool_MagneticTray>() != null);
        private static bool Physical(InteractableItems item) => item.gameObject.activeInHierarchy && !(item.HasARigidBody && item.rb == null) && item.itemCollider != null;
        // In the local player's hand, equipped, or held by an equipped tool (a wrench mouth is under the camera).
        // A chainsaw's idle and cut loops start and stop only from native Equiped/UnEquiped and its cut
        // coroutine. A shared chainsaw can leave the hand without either running (docked, re-leased,
        // replaced by a replica) and its motor kept running until picked up and set down again (#107).
        private static readonly AccessTools.FieldRef<Tool_Chainsaw, SOUND_Tool_Chainsaw> ChainsawSound =
            AccessTools.FieldRefAccess<Tool_Chainsaw, SOUND_Tool_Chainsaw>("SOUND_Tool_Chainsaw");
        private static readonly AccessTools.FieldRef<SOUND_Tool_Chainsaw, AudioSource> ChainsawIdleSource =
            AccessTools.FieldRefAccess<SOUND_Tool_Chainsaw, AudioSource>("ChainsawIdle");
        private static readonly AccessTools.FieldRef<SOUND_Tool_Chainsaw, AudioSource> ChainsawCutSource =
            AccessTools.FieldRefAccess<SOUND_Tool_Chainsaw, AudioSource>("ChainsawCut");
        private static readonly AccessTools.FieldRef<Tool_Chainsaw, bool> ChainsawCutting =
            AccessTools.FieldRefAccess<Tool_Chainsaw, bool>("isMouseLeftClickDown");
        internal static bool ChainsawCutSourcePlaying(InteractableItems? item)
        {
            if (!(item is Tool_Chainsaw chainsaw) || chainsaw == null) return false;
            var sound = ChainsawSound(chainsaw);
            var cut = sound == null ? null : ChainsawCutSource(sound);
            return cut != null && cut.isPlaying;
        }
        private float nextChainsawSoundCheck;
        // #150: a remote saw's local native coroutine cannot start its cut loop on this replica.
        private bool RemoteChainsawCutting(Binding binding, Tool_Chainsaw chainsaw)
        {
            var owner = OwnerOf(binding.Id);
            if (owner == ItemLease.Unowned || OwnedLocally(binding)) return false;
            var plugin = MonMPPlugin.Instance;
            return plugin != null && plugin.RemoteAvatarToolActive(owner) && HeldItemOf(owner) == chainsaw;
        }
        private void SilenceIdleChainsaws()
        {
#if MONMP_PERFORMANCE_CAPTURE
            using var detail = Runtime.CaptureDetail.Measure("Items.ChainsawSoundCheck");
#endif
            chainsawWork.Snapshot(chainsawWorkScratch);
            foreach (var b in chainsawWorkScratch)
            {
                if (!(b.Item is Tool_Chainsaw chainsaw) || chainsaw == null) continue;
                var sound = ChainsawSound(chainsaw);
                if (sound == null) continue;
                bool inHand = chainsaw.isEquiped && LocalHeld(chainsaw);
                bool remoteCutting = !inHand && RemoteChainsawCutting(b, chainsaw);
                var idle = ChainsawIdleSource(sound); var cut = ChainsawCutSource(sound);
                if (!inHand && idle != null && idle.isPlaying) idle.Stop();
                if ((!inHand || !ChainsawCutting(chainsaw)) && !remoteCutting && cut != null && cut.isPlaying) cut.Stop();
            }
            chainsawWorkScratch.Clear();
        }
        private bool LocalHeld(InteractableItems? item)
        {
            if (item == null || gameplay == null || gameplay.Player == null) return false;
            var player = gameplay.Player;
            // A wrench or impact gun aimed at a matching empty hole parents its bolt onto the hole as a
            // preview; the bolt is still in the tool's grip (a guest's copy was dropped after 0.1 s).
            if (item is Hardware_Bolt && (player.Get_ActiveTool is Tool_AdjustableSpanner || player.Get_ActiveTool is Tool_Air_Impact) &&
                ReferenceEquals((player.Get_ActiveTool is Tool_AdjustableSpanner ? SpannerGrip : ImpactGrip).GetValue(player.Get_ActiveTool), item)) return true;
            return player.Get_HoldingItem == item || player.Get_ActiveTool == item || item.transform.IsChildOf(player.transform) ||
                (player.mainCamera != null && item.transform.IsChildOf(player.mainCamera.transform)) ||
                (gameplay.PlayerWalking != null && item.transform.IsChildOf(gameplay.PlayerWalking.transform));
        }
        private bool BoltToolEquipped => LooseBoltBody.ToolEquipped(gameplay?.Player?.Get_ActiveTool);
        private bool OwnedLocally(Binding b) => host ? leases[b.Id].Owner == 0 : session != null && b.State.Lease.Owner == session.LocalPeerId;
        private readonly ItemDiscoveryRegistry<InteractableItems> discoveryItems =
            new ItemDiscoveryRegistry<InteractableItems>(item => item != null);
        private Action<InteractableItems>? discoverItemAction;
        private float nextSafetyDiscovery, lastDiscoverySlice = -1, discoveryCredit;
        internal void ItemChanged(InteractableItems item) { if (!disposed && item != null) discoveryItems.Add(item); }
        internal void ItemDestroyed(InteractableItems item) => discoveryItems.Remove(item);
#if MONMP_DEVELOPMENT
        private int testNativeDiscoveryScans;
        internal int TestNativeDiscoveryScans => testNativeDiscoveryScans;
        internal bool TestDiscoveryContains(InteractableItems item) => discoveryItems.Contains(item);
        internal bool TestDiscoveryProcessed(InteractableItems item) => byItem.ContainsKey(item) || hidden.ContainsKey(item);
#endif
        private void Discover(bool periodic = false)
        {
#if MONMP_DEVELOPMENT
            testNativeDiscoveryScans++;
#endif
#if MONMP_PERFORMANCE_CAPTURE
            using var detail = Runtime.CaptureDetail.Measure("Items.Discover");
#endif
            int missed = 0; string? missedType = null;
            foreach (var item in WorldScans.Find<InteractableItems>(periodic, true))
            {
                if (item == null) continue;
                if (discoveryItems.Add(item, changed: false) && periodic)
                { missed++; missedType ??= item.GetType().Name; }
                DiscoverItem(item);
            }
            nextSafetyDiscovery = Time.unscaledTime + 10f;
            if (missed != 0)
                Debug.LogWarning("MonMP item registry safety scan recovered " + missed + " item(s); first type: " + missedType +
                    ". Inactive instantiation or a missed native lifecycle path may be responsible.");
            if (host) RefreshTrayLinks();
        }
        private void DiscoverItem(InteractableItems item)
        {
            if (item == null) return;
            // Cheap lookups first: a late-game save has thousands of installed parts and bolts
            // that are revisited by the managed sweep.
            if (byItem.ContainsKey(item) || hidden.ContainsKey(item) || adopting.ContainsKey(item) || ignored.Contains(item)) return;
            // AddComponent runs Awake before the producer finishes initializing the item.
            // In particular, a split trunk only gets its species-specific log key in Start.
            // Keep unnamed components eligible for the existing Start hook and registry sweep.
            if (string.IsNullOrEmpty(item.PrefabName)) return;
            // Native planting/load adds Tree_Growing and defers destruction of the
            // cutting component until frame end. Forest owns that entire object:
            // hiding/adopting the still-live component also hides its new sapling.
            if (item is Garden_TreeCuttings && item.GetComponent<Tree_Growing>() != null) { ignored.Add(item); return; }
            if (HasPendingNativeOutput(item)) return;
            bool deferredBolt = !host && item is Hardware_Bolt && MonMPPlugin.Instance?.World?.Bolts.IsDeferredInstall(item) == true;
            // Installed parts/bolts remain discoverable after native removal. Test
            // their live placement flag before searching parent component metadata.
            if (Placed(item) && !deferredBolt) return;
            if (!host && item is InteractableParts && MonMPPlugin.Instance?.World?.VehicleParts.IsPartChangePending(item) == true) return;
            if (item.GetComponentInParent<FishingTrashCosmeticVisual>(true) != null) return;
            if (!host && MonMPPlugin.Instance?.World?.Bolts.IsMountPending(item) == true && !deferredBolt) return;
            if (!item.gameObject.scene.IsValid() || item.GetComponentInParent<ConsumptionEffectRoot>(true) != null) { ignored.Add(item); return; }
            bool fishingTrash = !host && item.GetComponent<Fishing_Trash>() != null;
            RemoteFishing.FishingTrashOrigin trashOrigin = default;
            if (fishingTrash && !RemoteFishing.TryResolveLocalFishingTrash(item, out trashOrigin, out bool pendingTrash))
            {
                // Native result objects can arrive before the host accepts the caught phase. Keep
                // that exact object queued for the ticket; unrelated/local trash stays local.
                if (pendingTrash) return;
                ignored.Add(item); return;
            }
            var prefab = Supported(item);
            if (prefab == null) { ignored.Add(item); return; }
            bool adoptableFishingTrash = fishingTrash && SupportsFishingTrashItem(item, trashOrigin.Template);
            if (fishingTrash && !adoptableFishingTrash) { ignored.Add(item); return; }
            bool held = LocalHeld(item);
            if (!host)
            {
                bool unpacked = ItemPrefabCatalog.Simple(prefab.Type) && (Time.unscaledTime - lastLocalUnpack < 3 || adoptionCandidates.Contains(item));
                if (session != null && (General(prefab) || unpacked || adoptableFishingTrash) && !baseline.Contains(item))
                {
                    // A local native action spawned this (unscrewed bolt, removed part, unpacked item):
                    // offer it to the host. Anything else new is the guest's own world simulation.
                    if (!Ready) return;
                    if (item.gameObject.activeInHierarchy && (deferredBolt || held || Physical(item)) && NearLocal(item.transform.position, 4))
                    { Adopt(item, prefab, held || deferredBolt, fishingTrash ? trashOrigin : (RemoteFishing.FishingTrashOrigin?)null); return; }
                    if (adoptionCandidates.Contains(item)) return;
                }
                // Vetted types are always host-owned. General items only hide when the host would
                // share them too; frozen/display copies (no body) stay visible as local scenery.
                if (!General(prefab) || Physical(item) || !baseline.Contains(item) || TrayContained(item))
                { hidden.Add(item, item.gameObject.activeSelf); item.gameObject.SetActive(false); }
                return;
            }
            if (!item.gameObject.activeInHierarchy || !(held || Physical(item) || TrayContained(item))) return;
            var registered = Register(item, prefab, held ? 0 : ItemLease.Unowned);
            if (item.GetComponent<Fishing_Trash>() != null && RemoteFishing.IsLocalFishingTrashOutput(item)) registered.FishingTrashOutput = true;
        }
        private void DiscoverSlice(float now)
        {
            discoverItemAction ??= DiscoverItem;
#if MONMP_PERFORMANCE_CAPTURE
            using var detail = Runtime.CaptureDetail.Measure("Items.DiscoverySlice");
#endif
            discoveryItems.DrainDirty(discoverItemAction);
            float elapsed = lastDiscoverySlice < 0 ? 0 : now - lastDiscoverySlice;
            lastDiscoverySlice = now;
            int count = ItemCaptureSlices.Take(ref discoveryCredit, discoveryItems.Count, elapsed, 1f);
            discoveryItems.Sweep(count, discoverItemAction);
            // Keep the original once-per-pass tray reconciliation cadence.
            if (nextDiscovery > 0 && now >= nextDiscovery)
            { nextDiscovery = now + 1f; if (host) RefreshTrayLinks(); }
        }
        private readonly List<Binding> trayLinkScratch = new List<Binding>();
        private void RefreshTrayLinks()
        {
#if MONMP_PERFORMANCE_CAPTURE
            using var detail = Runtime.CaptureDetail.Measure("Items.TrayLinks");
#endif
            // Native callbacks/publications may replace bindings. Keep a detached typed view
            // without copying or visiting the unrelated live decorations and consumables.
            trayBoltWork.Snapshot(trayLinkScratch);
            foreach (var b in trayLinkScratch)
            {
                if (b.Item == null || !(b.Item is Hardware_Bolt bolt)) continue;
                CaptureTrayMembership(bolt, out ulong container, out byte slot);
                if (container == b.State.ContainerEntity && slot == b.State.ContainerSlot) continue;
                var state = Capture(b);
                if (state.ContainerEntity == b.State.ContainerEntity && state.ContainerSlot == b.State.ContainerSlot) continue;
                b.State = state;
                if (channel != null) channel.Publish(b.Id, state.Encode());
            }
            trayLinkScratch.Clear();
        }
        private void CaptureTrayMembership(Hardware_Bolt bolt, out ulong container, out byte slot)
        {
            container = 0; slot = byte.MaxValue;
            // Match Capture's existing native-reference/parent fallback and exact slot check.
            var tray = bolt.isBoltInMagneticTray ?? bolt.transform.parent?.GetComponent<Tool_MagneticTray>();
            if (tray != null && byItem.TryGetValue(tray, out var parent))
            {
                int nativeSlot = MagneticTrayNative.SlotOf(tray, bolt);
                if (nativeSlot >= 0) { container = parent.Id; slot = (byte)nativeSlot; }
            }
        }
        private Binding Register(InteractableItems item, Prefab prefab, uint owner)
        {
            ulong id = ++nextId;
            while (channel != null && channel.Records.ContainsKey(id)) id = ++nextId;
            leases.Add(id);
            if (owner != ItemLease.Unowned) AcquireLease(id, owner, 1, 2);
            var b = new Binding { Id = id, Item = item, Prefab = prefab.Id, LastOwnerUpdate = Time.unscaledTime };
            b.State = Capture(b); bindings.Add(id, b); ObserveBinding(b); byItem.Add(item, b);
            identities.Bind(id, ItemState.Adapter, item.gameObject, prefab.Path, false);
            if (channel != null) channel.Register(new WorldRecord(id, 1, ItemState.Adapter, b.State.Encode()));
            return b;
        }
        private bool NearLocal(Vector3 position, float range) => CanHandleItems(gameplay.Get_PlayerState) &&
            (gameplay.PlayerWalking.transform.position - position).sqrMagnitude <= range * range;
        internal void NativePartRemoved(InteractableParts part)
        {
            // A removed original is now a loose item, even if it existed in the guest's
            // baseline. Discover after RemovePart_Timer finishes its native hand grab.
            baseline.Remove(part); ignored.Remove(part); ItemChanged(part); nextDiscovery = 0;
        }
        private void Adopt(InteractableItems item, Prefab prefab, bool held, RemoteFishing.FishingTrashOrigin? trashOrigin = null)
        {
            if (IsExactNativeOutputPending(item)) return;
            adoptionCandidates.Add(item);
            if (adopting.Count >= 8) return;
            uint nonce = ++nextNonce; if (nonce == 0) nonce = ++nextNonce;
            var values = new ItemValues(item.Value1, item.Value2, item.IntValue1, item.IntValue2);
            try { ValidateValues(prefab, values); } catch (InvalidDataException) { adoptionCandidates.Remove(item); hidden.Add(item, item.gameObject.activeSelf); item.gameObject.SetActive(false); return; }
            adopting.Add(item, (nonce, Time.unscaledTime)); adoptNonces.Add(nonce, item); adoptHeld.Add(nonce, held);
            var spawn = new ItemSpawnRequest(nonce, held, Pose(item.transform), values);
            adoptionRequests.Add(nonce, (prefab.Id, spawn));
            byte[] payload = spawn.Encode();
            if (trashOrigin.HasValue)
            {
                var origin = trashOrigin.Value;
                fishingTrashNonces.Add(nonce, origin);
                payload = new FishingTrashSpawnRequest(origin.Rod, origin.Generation, origin.Ticket, origin.Template, spawn,
                    origin.RemainingMilliseconds).Encode();
            }
            channel!.Request(descriptorByPrefab[prefab.Id], payload);
        }
        // Host: create the shared twin of a guest's natively spawned item, at the guest's pose.
        private void AdoptRequest(uint peer, ulong descriptor, ushort prefabId, byte[] payload)
        {
            ItemSpawnRequest request;
            FishingTrashSpawnRequest? fishingRequest = null;
            try
            {
                if (FishingTrashSpawnRequest.Is(payload))
                { fishingRequest = FishingTrashSpawnRequest.Decode(payload); request = fishingRequest.Value.Spawn; }
                else request = ItemSpawnRequest.Decode(payload);
            }
            catch (InvalidDataException) { return; }
            var prefab = prefabs[prefabId];
            if (peer == 0 || !(General(prefab) || ItemPrefabCatalog.Simple(prefab.Type))) return;
            if (adoptionReceipts.TryGetValue((peer, request.Nonce), out var receipt))
            {
                if (receipt.Prefab == prefabId) channel!.Reply(peer, descriptor, new ItemSpawnReply(request.Nonce, receipt.Entity).Encode());
                return;
            }
            // Pruning and harvesting now create their rewards on the accepting host. Their
            // old uncorrelated guest predictions may not become a second independent output.
            if (typeof(Garden_TreeCuttings).IsAssignableFrom(prefab.Type) || typeof(Tool_Seeds).IsAssignableFrom(prefab.Type)) return;
            if (TransfersBlocked && !NativeOutputDrainAllowed || adoptionReceipts.Count + packedAdoptionReceipts.Count >= SnapshotReceiver.MaxEntities) return;
            ValidateValues(prefab, request.Values);
            if (fishingRequest.HasValue && (SharedFishingPresentation.Active == null ||
                !SharedFishingPresentation.Active.CanAdoptTrashOutput(peer, fishingRequest.Value, prefab.Type, prefab.Path, request.Pose))) return;
            float time = Time.unscaledTime;
            var budget = adoptBudget.TryGetValue(peer, out var old) ? (Tokens: Math.Min(10, old.Tokens + (time - old.Time) * 4), Time: time) : (Tokens: 10f, Time: time);
            if (budget.Tokens < 1 || !InRange(peer, Position(request.Pose), 6)) { adoptBudget[peer] = budget; return; }
            adoptBudget[peer] = (budget.Tokens - 1, time);
            var go = Object.Instantiate(prefab.Asset, Position(request.Pose), Rotation(request.Pose));
            var item = go.GetComponent<InteractableItems>();
            item.isSpawnFromTheSaveLoadSystem = true;
            // Start otherwise calls UpdatePartPlacement(false) on the following frame and
            // makes a guest-held part dynamic again after ConfigureBody made it kinematic.
            if (item is InteractableParts adoptedPart) adoptedPart.Get_isJustSpawned = true;
            ApplyValues(item, request.Values); item.CreateBasics();
            // A guest's catch: the fish's size is its scale (the native rod sets both from one roll).
            if (item is Item_Fish && request.Values.Value1 > 0.05f && request.Values.Value1 < 5f) go.transform.localScale = Vector3.one * request.Values.Value1;
            var b = Register(item, prefab, request.Hold ? peer : ItemLease.Unowned);
            adoptionReceipts.Add((peer, request.Nonce), (prefabId, b.Id));
            if (fishingRequest.HasValue)
            {
                b.FishingTrashOutput = true;
                b.FishingTrashExpiresAt = Time.unscaledTime + fishingRequest.Value.RemainingMilliseconds * 0.001f;
            }
            if (item is Garden_TreeCuttings cuttings && !request.Hold) MergeCuttings(cuttings);
            ConfigureBody(b, false); Publish(b);
            if (fishingRequest.HasValue && !SharedFishingPresentation.Active!.ConsumeTrashOutput(peer, fishingRequest.Value))
            {
                if (b.Item != null) { b.Item.gameObject.SetActive(false); Object.Destroy(b.Item.gameObject); b.Item = null; }
                b.State = Capture(b); channel!.Publish(b.Id, b.State.Encode());
                return;
            }
            channel!.Reply(peer, descriptor, new ItemSpawnReply(request.Nonce, b.Id).Encode());
        }
        // Host: a guest's prune drops a fresh pile at its loppers; as the native prune does, every
        // loose pile of that tree type within reach joins it (piles someone holds stay theirs).
        private void MergeCuttings(Garden_TreeCuttings pile)
        {
            int total = pile.IntValue1;
            foreach (var other in bindings.Values.ToArray())
            {
                if (!(other.Item is Garden_TreeCuttings loose) || loose == pile || !other.State.Exists || loose.Get_TreeType != pile.Get_TreeType ||
                    leases[other.Id].Owner != ItemLease.Unowned || (loose.transform.position - pile.transform.position).sqrMagnitude > 60f * 60f) continue;
                total += loose.IntValue1;
                identities.Unbind(other.Id); byItem.Remove(loose); loose.gameObject.SetActive(false); Object.Destroy(loose.gameObject); other.Item = null;
                other.State = Capture(other); Publish(other);
            }
            pile.IntValue1 = total;
        }
        // Guest: the host adopted our local object; bind it in place so no duplicate replica spawns.
        private void Reply(ulong entity, byte[] payload)
        {
            if (host) return;
            if (PickupReply(entity, payload)) return;
            if (PackedOutputReply(ref entity, ref payload)) return;
            if (bindings.TryGetValue(entity, out var liftBinding) && (liftBinding.Item is Furniture_CarLift || liftBinding.Item is InteractablePacks) &&
                NativeErrorReply.TryDecode(payload, out string liftError))
            { Object.FindObjectOfType<UI_ErrorMessage>()?.Show(liftError); return; }
            if (bindings.TryGetValue(entity, out var binding) && binding.SuspensionEdits?.Reply(payload) == true)
            { SendLooseSuspensionEdit(binding, Time.unscaledTime); return; }
            if (!prefabByDescriptor.TryGetValue(entity, out ushort prefab)) return;
            var reply = ItemSpawnReply.Decode(payload);
            if (!adoptNonces.TryGetValue(reply.Nonce, out var item)) return;
            adoptNonces.Remove(reply.Nonce);
            adoptionRequests.Remove(reply.Nonce);
            if (!ReferenceEquals(item, null)) { adopting.Remove(item); adoptionCandidates.Remove(item); }
            bool requestedHeld = adoptHeld.TryGetValue(reply.Nonce, out bool requested) && requested;
            adoptHeld.Remove(reply.Nonce);
            bool localFishingTrash = fishingTrashNonces.Remove(reply.Nonce);
            if (item == null) return;
            bool locallyHeld = LocalHeld(item);
            bool deferredBoltInstall = item is Hardware_Bolt && MonMPPlugin.Instance?.World?.Bolts.IsMountPending(item) == true;
            var (adoptionLease, dropBeforeApply) = ResolveAdoptedLease(session!.LocalPeerId, requestedHeld, locallyHeld, deferredBoltInstall);
            if (bindings.TryGetValue(reply.Entity, out var existing))
            {
                if (existing.Prefab != prefab) return;
                if (!existing.State.Exists || (existing.State.Lease.Owner != ItemLease.Unowned && existing.State.Lease.Owner != session!.LocalPeerId))
                {
                    // A delayed receipt can follow a tombstone or a later owner's pickup. Dispose
                    // only our prediction; the current canonical item/owner remains authoritative.
                    if (!ReferenceEquals(existing.Item, item)) { CancelLocal(item); ignored.Add(item); item.gameObject.SetActive(false); Object.Destroy(item.gameObject); }
                    return;
                }
                if (!ReferenceEquals(existing.Item, item))
                {
                    // A dynamic record can beat its spawn reply. The exact nonce identifies
                    // our native output; preserve its hand grip or imminent mount in place.
                    var replica = existing.Item;
                    if (replica != null)
                    {
                        identities.Unbind(existing.Id); byItem.Remove(replica);
                        replica.gameObject.SetActive(false); Object.Destroy(replica.gameObject);
                    }
                    existing.Item = item; byItem[item] = existing;
                    ApplyValues(item, existing.State.Values, force: true);
                    identities.Bind(existing.Id, ItemState.Adapter, item.gameObject, prefabs[prefab].Path, false);
                    if (deferredBoltInstall && Placed(item)) { existing.HasTarget = false; existing.Settled = true; }
                }
                existing.FishingTrashOutput = localFishingTrash;
                existing.LocalFishingTrash = localFishingTrash;
                if (!OwnedLocally(existing)) { CancelLocal(item); Apply(existing.Id, existing.State, true); return; }
                ConfigureBody(existing, LocalHeld(item));
                if (dropBeforeApply) { existing.Releasing = true; existing.HasTarget = false; Request(existing, ItemAction.Drop); }
                return;
            }
            var b = new Binding
            {
                Id = reply.Entity, Item = item, Prefab = prefab,
                // The request's hold intent survives a native attach that makes LocalHeld false.
                State = new ItemState(prefab, true, adoptionLease, Pose(item.transform),
                    values: new ItemValues(item.Value1, item.Value2, item.IntValue1, item.IntValue2)),
                FishingTrashOutput = localFishingTrash,
                LocalFishingTrash = localFishingTrash
            };
            bindings.Add(b.Id, b); ObserveBinding(b); byItem.Add(item, b);
            identities.Bind(b.Id, ItemState.Adapter, item.gameObject, prefabs[prefab].Path, false);
            if (dropBeforeApply) { b.Releasing = true; b.HasTarget = false; Request(b, ItemAction.Drop); }
        }
        internal static (ItemLease Lease, bool DropBeforeApply) ResolveAdoptedLease(uint peer, bool requestedHeld, bool locallyHeld, bool deferredBoltInstall)
        {
            // The host grants ownership only from ItemSpawnRequest.Hold. A later native grab or
            // attach cannot predict a lease the host never acquired for this nonce.
            return (requestedHeld ? new ItemLease(peer, 2) : new ItemLease(ItemLease.Unowned, 1),
                requestedHeld && !locallyHeld && !deferredBoltInstall);
        }
        private bool PendingFishingAdoption(ItemState state) => !host && session != null &&
            state.Lease.Owner == session.LocalPeerId && prefabs[state.Prefab].Type == typeof(Item_Fish) &&
            adoptNonces.Values.Any(item => item != null && item is Item_Fish && Supported(item)?.Id == state.Prefab);
        public IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, ItemState.Adapter, b.State.Encode()));
        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            session.SettingsChanged += CargoSettingsChanged;
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed; channel.CommandRequested += Command; channel.CommandRejected += Rejected; channel.ReplyReceived += Reply;
            if (!host)
            {
                foreach (var item in Object.FindObjectsOfType<InteractableItems>(true)) baseline.Add(item);
                Discover();
            }
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer)
        {
            handTools.Remove(peer);
            pickupNonceByPeer.Remove(peer);
            presence.Remove(peer);
            ReleasePeerAdoptions(peer);
            if (!host || session?.IsHost != true) return;
            // A leaving player cannot release a held lift button; stop the hydraulics for them.
            foreach (var pair in liftOperators.Where(p => p.Value == peer).ToArray())
            {
                liftOperators.Remove(pair.Key);
                if (bindings.TryGetValue(pair.Key, out var liftBinding) && liftBinding.Item is Furniture_CarLift stranded) CarLiftState.Stop(stranded);
            }
            RevokeOwnership(peer);
        }
        private ItemState Decode(WorldRecord record)
        {
            if (record.Adapter != ItemState.Adapter || record.Id <= WorldRecord.DynamicIdStart) throw new InvalidDataException("Invalid dynamic item.");
            var state = ItemState.Decode(record.Payload);
            if (!prefabs.ContainsKey(state.Prefab) || (bindings.TryGetValue(record.Id, out var b) && b.Prefab != state.Prefab)) throw new InvalidDataException("Unregistered or changed item prefab.");
            ValidateValues(prefabs[state.Prefab], state.Values);
            if (Freezable(prefabs[state.Prefab].Type) && state.Values.IntValue1 == 1 && state.Lease.Owner != ItemLease.Unowned)
                throw new InvalidDataException("Frozen decoration cannot have a carry owner.");
            if (state.ContainerEntity != 0 && (prefabs[state.Prefab].Type != typeof(Hardware_Bolt) || state.ContainerEntity == record.Id))
                throw new InvalidDataException("Only a bolt may name a distinct container entity.");
            return state;
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            var staged = records.Values.Where(r => r.Adapter == ItemState.Adapter).Select(r => (r.Id, State: Decode(r))).ToArray();
            // Entity repairs include only the repaired child. Validate the complete committed
            // view, including unaffected parents/slots, before applying this partial batch.
            var accepted = (channel?.Records ?? records).Values.Where(r => r.Adapter == ItemState.Adapter)
                .ToDictionary(r => r.Id, r => Decode(r));
            var occupied = new HashSet<(ulong Container, byte Slot)>();
            foreach (var state in accepted.Values)
            {
                if (state.ContainerEntity == 0) continue;
                if (!accepted.TryGetValue(state.ContainerEntity, out var parent) || !parent.Exists ||
                    prefabs[parent.Prefab].Type != typeof(Tool_MagneticTray) || !occupied.Add((state.ContainerEntity, state.ContainerSlot)))
                    throw new InvalidDataException("Invalid or duplicate magnetic tray link in world snapshot.");
            }
            // A valid final view may move two existing bolts into each other's old slots.
            // Release outgoing native links before assigning any destination, and defer
            // old pending links until every binding holds this batch's accepted state.
            applyingContainmentBatch = true;
            try
            {
                foreach (var pair in staged)
                    if (bindings.TryGetValue(pair.Id, out var binding)) CancelLooseSuspensionEdits(binding, restore: false);
                PrepareContainment(staged);
                foreach (var pair in staged) Apply(pair.Id, pair.State, true, false);
                foreach (var pair in staged) if (pair.State.ContainerEntity != 0 || bindings[pair.Id].Item is Hardware_Bolt)
                    ApplyContainment(bindings[pair.Id], pair.State);
            }
            finally { applyingContainmentBatch = false; }
            ResolvePendingContainment();
        }
        private bool applyingContainmentBatch;
        private void PrepareContainment((ulong Id, ItemState State)[] staged)
        {
            foreach (var pair in staged)
            {
                if (!bindings.TryGetValue(pair.Id, out var b) || !(b.Item is Hardware_Bolt bolt)) continue;
                var tray = bolt.isBoltInMagneticTray ?? bolt.transform.parent?.GetComponent<Tool_MagneticTray>();
                if (tray == null) continue;
                if (pair.State.ContainerEntity != 0 && bindings.TryGetValue(pair.State.ContainerEntity, out var parent) &&
                    parent.Item == tray && ReferenceEquals(MagneticTrayNative.Slots(tray)[pair.State.ContainerSlot], bolt)) continue;
                MagneticTrayNative.Detach(bolt);
            }
        }
        private void Changed(WorldRecord record)
        {
            if (record.Adapter != ItemState.Adapter || host) return;
            var state = Decode(record);
            ValidateChangedContainment(record.Id, state);
            foreach (var key in pendingTrayRequests.Keys.Where(key => key.Bolt == record.Id).ToArray()) pendingTrayRequests.Remove(key);
            if (bindings.TryGetValue(record.Id, out var binding) && binding.SuspensionEdits?.HasPending == true &&
                !binding.SuspensionEdits.MatchesLease(state.Lease.Owner, state.Lease.Generation))
                CancelLooseSuspensionEdits(binding, restore: false);
            Apply(record.Id, state);
            if (bindings.TryGetValue(record.Id, out binding) && binding.SuspensionEdits?.HasPending == true)
            {
                ApplySuspensionDisplay(binding);
                SendLooseSuspensionEdit(binding, Time.unscaledTime);
            }
        }
        private void ValidateChangedContainment(ulong id, ItemState state)
        {
            if (state.ContainerEntity == 0) return;
            if (bindings.TryGetValue(state.ContainerEntity, out var parent) && parent.Prefab != 0 &&
                (prefabs[parent.Prefab].Type != typeof(Tool_MagneticTray) || !parent.State.Exists))
                throw new InvalidDataException("Magnetic tray link names a missing or incompatible container.");
            if (bindings.Values.Any(other => other.Id != id && other.State.Exists && other.State.ContainerEntity == state.ContainerEntity &&
                other.State.ContainerSlot == state.ContainerSlot))
                throw new InvalidDataException("Two bolts claim the same magnetic tray slot.");
        }
        private void Apply(ulong id, ItemState state, bool forceValues = false, bool applyContainment = true)
        {
            if (!host && pendingNativeOutputs.ContainsKey(id))
            { if (BindNativeOutput(id, state)) return; }
            if (!bindings.TryGetValue(id, out var b))
            { b = new Binding { Id = id, Prefab = state.Prefab, State = state }; bindings.Add(id, b); ObserveBinding(b); }
            bool cancelledPickup = ObservePickup(b, state);
            // Native use destroys a depleted local pack. Intermediate acknowledgments must
            // not respawn its already-predicted seeds before the final debit/timeout settles.
            if (!host && b.Item == null && state.Exists && b.SeedPrediction != null && state.Lease.Owner == session?.LocalPeerId &&
                b.SeedPrediction.Reconcile(state.Values.Value1, state.Lease.Generation, Time.unscaledTime, forceValues) <= 0 && b.SeedPrediction.HasPending)
            { b.State = state; return; }
            if (forceValues || !state.Exists) { b.DropPreview = null; b.DropPreviewAcknowledged = null; }
            // Installed or confirmed consumed here; the host's loose twin is ending. Never respawn it.
            if (b.Absorbed) { if (!state.Exists) b.State = state; return; }
            if (presenting.Contains(id))
            {
                if (b.Item == null) presenting.Remove(id);
                else if (state.Exists) { b.State = state; return; }
                else
                {
                    presenting.Remove(id);
                    CancelLocal(b.Item); byItem.Remove(b.Item); ignored.Add(b.Item);
                    b.Item = null; b.State = state; DeliverConsumption(b); return;
                }
            }
            if (!state.Exists)
            {
                identities.Unbind(id);
                // Installed here a moment ago and the host's loose twin already ended: the part system owns
                // our copy. Hiding it left a guest's new wheel invisible on its hub, its socket and bolts
                // still working (#163).
                if (b.Item != null && Placed(b.Item))
                {
                    byItem.Remove(b.Item); ignored.Add(b.Item);
                    b.Absorbed = true; b.Item = null; b.State = state; return;
                }
                if (b.Item is Hardware_Bolt doomedBolt && (doomedBolt.isBoltInMagneticTray != null || doomedBolt.transform.parent?.GetComponent<Tool_MagneticTray>() != null))
                    MagneticTrayNative.Detach(doomedBolt);
                if (b.Item != null && (state.Values.Flags & ItemFlags.Burned) != 0 && BurnLocalCopy(b.Item))
                { b.Item = null; b.State = state; return; }
                // A native sale on the host (sawmill, kiosk, syrup, brother): play the store's slide here.
                if (b.Item != null && (state.Values.Flags & ItemFlags.Sold) != 0)
                {
                    var sold = b.Item;
                    CancelLocal(sold); byItem.Remove(sold);
                    if (SalePresentation.Present(sold)) { b.Item = null; b.State = state; DeliverConsumption(b); return; }
                    byItem[sold] = b;
                }
                if (b.Item != null)
                {
                    bool loose = !forceValues && b.Item.transform.parent == null && !LocalHeld(b.Item);
                    CancelLocal(b.Item); byItem.Remove(b.Item);
                    // Unbound but still in the scene while it shrinks away (or until end of frame): discovery
                    // must not take it for a new local item and ask the host to spawn it. A host that
                    // installed a held part got a copy of it dropped at the socket that way (#132).
                    ignored.Add(b.Item);
                    // A structure that consumed the host's copy may keep ours as its presentation.
                    if (ClaimRemoved?.Invoke(b.Item) != true)
                    {
                        // A loose item the host delivered, sold or burned shrinks away rather than popping.
                        if (loose && MonMPPlugin.Instance != null) Vanish(b.Item);
                        else { b.Item.gameObject.SetActive(false); Object.Destroy(b.Item.gameObject); }
                    }
                }
                else if (!ReferenceEquals(b.Item, null)) byItem.Remove(b.Item);
                b.Item = null; b.State = state; DeliverConsumption(b); return;
            }
            if (b.Item == null)
            {
                if (ShouldDeferNativeOutput(state))
                { deferredNativeOutputSpawns.Add(id); b.State = state; return; }
                // A catch's dynamic record can arrive before the adoption reply identifies
                // our fish still attached to the rod. Creating a second fish here would try
                // to grant it into an occupied hand and prematurely release its host lease.
                if (PendingFishingAdoption(state)) { b.State = state; return; }
                // The local angler's native Fishing_Trash timer destroys this mirrored result
                // after its six-second loose lifetime. Keep waiting for the host tombstone instead
                // of recreating the transient output from its last live item record.
                if (!host && b.LocalFishingTrash)
                {
                    if (!ReferenceEquals(b.Item, null)) byItem.Remove(b.Item);
                    identities.Unbind(id); b.State = state; return;
                }
                // Native code on this machine may have destroyed our copy (an NPC taking a sold pack,
                // an unpack, a machine consuming it) without going through this adapter. Drop the
                // stale identity before respawning the host's still-existing item.
                if (!ReferenceEquals(b.Item, null)) { byItem.Remove(b.Item); b.Item = null; }
                identities.Unbind(id);
                var spawnPose = WorldPose(state);
                var go = Object.Instantiate(prefabs[state.Prefab].Asset, Position(spawnPose), Rotation(spawnPose));
                b.Item = go.GetComponent<InteractableItems>();
                b.Item.isSpawnFromTheSaveLoadSystem = true;
                ApplyValues(b.Item, state.Values);
                b.Item.CreateBasics(); byItem.Add(b.Item, b);
                identities.Bind(b.Id, ItemState.Adapter, go, prefabs[state.Prefab].Path, false);
                forceValues = true; RemoteSpawns++;
                RemoteSpawned?.Invoke(b.Item);
            }
            // Installed here a moment ago (a wheel on its hub) and not yet absorbed by the send loop: the
            // part system owns it now. A pose echo used to unparent it, so a guest's fresh wheel dropped
            // off the hub and left its hitbox hanging beside it (#137).
            if (!host && Placed(b.Item))
            {
                if (b.Item is InteractableParts && MonMPPlugin.Instance?.World?.VehicleParts.IsPartChangePending(b.Item) == true)
                { b.State = state; b.HasTarget = false; b.Settled = true; return; }
                if (b.Item is Hardware_Bolt && MonMPPlugin.Instance?.World?.Bolts.IsMountPending(b.Item) == true)
                { b.State = state; b.HasTarget = false; b.Settled = true; return; }
                b.State = state; AbsorbLocal(b); return;
            }
            bool wasDockedChainsaw = b.Item is Tool_Chainsaw priorChainsaw && priorChainsaw.IntValue1 == 1;
            // Seed the native pickup cooldown once, only if this copy is still
            // physically docked. An echo must not restart a native local grab.
            if (wasDockedChainsaw && b.Item is Tool_Chainsaw liftedChainsaw && state.Values.IntValue1 == 0)
            {
                RetainChainsawRackContact(liftedChainsaw);
                ChainsawPickTime?.SetValue(liftedChainsaw, Time.realtimeSinceStartup);
            }
            bool pendingLocalRelease = cancelledPickup || PendingLocalRelease(b, state, session!.LocalPeerId);
            bool easeLocalDrop = EaseLocalDropAcknowledgement(b, state, session!.LocalPeerId, forceValues);
            bool keepLocalDrop = b.DropPreview != null && KeepLocalDropAcknowledgement(b, state, WorldPose(state), DropGravityProjection(state.Carrier), session.LocalPeerId, forceValues, Time.unscaledTime);
            if (keepLocalDrop) { b.DropPreviewAcknowledged = state; Uncarry(b); }
            else if (!pendingLocalRelease) { b.DropPreview = null; b.DropPreviewAcknowledged = null; }
            b.State = state;
            // Mid-pour, the host's echo of a gas can's level lags this machine's native pour.
            if (forceValues || !(b.Item is Tool_FuelCanister can && MonMPPlugin.Instance?.World?.Fuel.Busy(can) == true))
            {
                ApplyValues(b.Item, state.Values, forceValues);
                b.DeferredCanisterValues = false;
            }
            else b.DeferredCanisterValues = true;
            if (state.Values.IntValue1 == 1 && b.Item is Tool_Chainsaw)
                foreach (var key in pendingChainsawDocks.Keys.Where(key => key.Item == b.Id).ToArray()) pendingChainsawDocks.Remove(key);
            bool local = state.Lease.Owner == session!.LocalPeerId;
            if (!pendingLocalRelease) b.Releasing = false;
            if (!local && state.Lease.Owner != ItemLease.Unowned)
            { b.PendingGrab = GrabMode.None; b.PendingTool = null; b.PendingUse = false; }
            if (!local && LocalHeld(b.Item)) CancelLocal(b.Item);
            bool held = LocalHeld(b.Item);
            bool dockedChainsaw = b.Item is Tool_Chainsaw && state.Values.IntValue1 == 1;
            bool bucketedFish = b.Item is Item_Fish && MonMPPlugin.Instance?.World?.FishBuckets?.ContainsFish(b.Id) == true;
            // A bolt riding in a magnetic tray follows the tray's transform. Its world samples lag the tray's
            // own, so snapping and smoothing toward them made tray bolts jump while the tray moved (#199).
            bool trayed = state.ContainerEntity != 0 && b.Item is Hardware_Bolt trayBolt && trayBolt.isBoltInMagneticTray != null &&
                bindings.TryGetValue(state.ContainerEntity, out var trayBinding) && trayBinding.Item != null && trayBolt.isBoltInMagneticTray == trayBinding.Item;
            if (trayed) { b.HasTarget = false; b.Settled = true; }
            // This guest already released at its click-time view pose. Older held-state
            // echoes must not pull it backward or grant it again while the host settles
            // that exact lease. A new owner/generation still applies normally.
            if ((!local || !held) && !pendingLocalRelease && !keepLocalDrop && !bucketedFish && !trayed)
            {
                if (state.ContainerEntity == 0 && b.Item.transform.parent != null &&
                    b.Item.GetComponentInParent<Furniture_TireChanger_Manual>(true) == null && !dockedChainsaw)
                    b.Item.transform.SetParent(null);
                b.Item.transform.localScale = new Vector3(state.ScaleX, state.ScaleY, state.ScaleZ);
                var worldPose = WorldPose(state);
                var position = Position(worldPose); var rotation = Rotation(worldPose);
                if (dockedChainsaw)
                {
                    if (!AttachChainsawReplica(b, worldPose)) b.Item.transform.SetPositionAndRotation(position, rotation);
                    // Racked, the saw rides the holder's transform. A leftover hand or ground target
                    // made the smoothing loop drag it off the rack: floating beside the ATV, or gone
                    // back to where it was picked up (#108).
                    else { b.HasTarget = false; b.Settled = true; }
                }
                else
                {
                    if (wasDockedChainsaw && b.Item is Tool_Chainsaw releasedChainsaw && releasedChainsaw.rb == null)
                        releasedChainsaw.CreateBasics();
                    // A remote hand receives discrete 10 Hz poses. Interpolate each sample over
                    // that interval so the visible item follows the smoothed remote player.
                    bool remoteHeld = state.Lease.Owner != ItemLease.Unowned && !local;
                    float snapDistanceSquared = remoteHeld ? 100f : 4f;
                    // A repair snapshot also forces native values. Keep an existing
                    // remote-held presentation in flight unless the pose is a large correction.
                    bool snap = !b.HasTarget || (forceValues && !remoteHeld) ||
                        (b.Item.transform.position - position).sqrMagnitude > snapDistanceSquared;
                    bool preserveDropPose = easeLocalDrop && (b.Item.transform.position - position).sqrMagnitude <= snapDistanceSquared;
                    var dropPosition = preserveDropPose ? b.Item.transform.position : default;
                    var dropRotation = preserveDropPose ? b.Item.transform.rotation : Quaternion.identity;
                    SnapItemToHost(b, state, snap, preserveDropPose, position, rotation);
                    SetTarget(b, worldPose);
                    Carry(b, state);
                    if (preserveDropPose && b.CarrierFrame != null)
                    {
                        b.CarrierPosition = b.CarrierFrame.InverseTransformPoint(dropPosition);
                        b.CarrierRotation = Quaternion.Inverse(b.CarrierFrame.rotation) * dropRotation;
                        b.Item.transform.SetPositionAndRotation(dropPosition, dropRotation);
                    }
                }
            }
            if (state.Carrier == 0 || (local && held)) Uncarry(b);
            if (applyContainment) ApplyContainment(b, state);
            if (local && !held && !b.Releasing && !Grant(b)) return;
            ConfigureBody(b, local);
            ResolvePendingContainment();
        }
        private void ApplyContainment(Binding b, ItemState state)
        {
            pendingContainment.Remove(b.Id);
            if (!(b.Item is Hardware_Bolt bolt))
            {
                if (state.ContainerEntity != 0) throw new InvalidDataException("A non-bolt item cannot be contained in a magnetic tray.");
                return;
            }
            if (state.ContainerEntity == 0)
            {
                if (bolt.isBoltInMagneticTray != null || bolt.transform.parent?.GetComponent<Tool_MagneticTray>() != null)
                    MagneticTrayNative.Detach(bolt);
                return;
            }
            if (!bindings.TryGetValue(state.ContainerEntity, out var parent) || parent.Item == null)
            { pendingContainment.Add(b.Id); return; }
            if (!(parent.Item is Tool_MagneticTray tray) || !parent.State.Exists)
                throw new InvalidDataException("Magnetic tray link resolved to an incompatible item.");
            var occupied = MagneticTrayNative.Slots(tray)[state.ContainerSlot];
            if (occupied != null && !ReferenceEquals(occupied, bolt) && occupied.isBoltInMagneticTray == tray)
                throw new InvalidDataException("Two bolts claim the same magnetic tray slot.");
            MagneticTrayNative.AttachReplica(tray, bolt, state.ContainerSlot);
        }
        private void ResolvePendingContainment()
        {
            if (applyingContainmentBatch) return;
            foreach (ulong id in pendingContainment.ToArray())
            {
                if (!bindings.TryGetValue(id, out var b) || b.Item == null || !b.State.Exists) { pendingContainment.Remove(id); continue; }
                if (!bindings.ContainsKey(b.State.ContainerEntity)) continue;
                ApplyContainment(b, b.State);
            }
        }
        // Take hold of an item whose lease was just granted to this player: into the hand, or into an
        // equipped tool's own grip when that tool asked for it (a wrench picking up a loose bolt).
        private bool Grant(Binding b)
        {
            if (!host && pickupPreview is { } pending && pending != b) CancelPickup(pending);
            var mode = b.PendingGrab; var tool = b.PendingTool;
            b.PendingGrab = GrabMode.None; b.PendingTool = null;
            if (mode == GrabMode.Tool && tool != null && gameplay.Player.Get_ActiveTool == tool && b.Item is Hardware_Bolt)
            {
                var grab = AccessTools.Method(tool.GetType(), "GrabBolt");
                if (grab != null) { applying = true; try { grab.Invoke(tool, new object[] { b.Item }); } finally { applying = false; } return true; }
            }
            if (mode == GrabMode.ToolPack)
            {

#if MONMP_DEVELOPMENT
                if (TestToolPackDiagnostics && b.Item is InteractablePacks tracePack)
                {
                    var traceHole = tool is Tool_AdjustableSpanner ? SpannerBoltHole : tool is Tool_Air_Impact ? ImpactBoltHole : null;
                    var traceGrip = tool is Tool_AdjustableSpanner ? SpannerGrip : tool is Tool_Air_Impact ? ImpactGrip : null;
                    Debug.Log("MONMP_TOOL_PACK_GRANT: id=" + b.Id.ToString("X") + "; lease=" + b.State.Lease.Owner + "/" + b.State.Lease.Generation +
                        "; count=" + tracePack.Value1 + "; ready=" + Ready + "; blocked=" + TransfersBlocked + "; player=" + gameplay.Get_PlayerState +
                        "; active=" + (tool != null && gameplay.Player.Get_ActiveTool == tool) +
                        "; target=" + gameplay.Player.Get_CurrentInteractable + "; hole=" + (tool == null ? null : traceHole?.GetValue(tool)) +
                        "; grip=" + (tool == null ? null : traceGrip?.GetValue(tool)) + "; valid=" + ToolPackGrantStillValid(tool, tracePack));
                }
#endif

                // Now owned: the native extraction runs (and reports Unpacked); the pack itself stays put.
                if (tool != null && b.Item is InteractablePacks pack && ToolPackGrantStillValid(tool, pack) &&
                    pack.GetOnePackedItem()?.GetComponent<Hardware_Bolt>() is Hardware_Bolt bolt)
                {
                    var grab = AccessTools.Method(tool.GetType(), "GrabBolt");
                    grab?.Invoke(tool, new object[] { bolt });
                }
                if (!b.Releasing) { b.Releasing = true; Request(b, ItemAction.Drop); }
                return false;
            }
            // Only materialize a guest hand pickup after Apply accepted the
            // authoritative ownership generation. Both leases retain normal
            // move, save, conflict, drop and disconnect handling.
            bool wanted = b.PickupPresentation.TryBeginGrant(b.Item != null ? b.Item.gameObject.GetInstanceID() : 0,
                gameplay.Player.IsHoldingAnObject, out var presentationScope);
            b.PickupPresentation = default;
            using var handScope = presentationScope;
            if (!wanted || gameplay.Player.IsHoldingAnObject)
            {
                if (b.PendingUse && b.Item is InteractablePacks)
#if MONMP_DEVELOPMENT
testFailedPackGrants++;
#else
{}
#endif

                b.PendingUse = false;
                if (host) { var lease = leases[b.Id]; ReleaseLease(b.Id, lease.Owner, lease.Generation); b.State = Capture(b); ConfigureBody(b, false); }
                else { b.Releasing = true; Request(b, ItemAction.Drop); }
                return false;
            }
            if (consumedInHand is { } before && Time.unscaledTime - before.At < 5f && b.Item is Item_MapleSyrupCan_Empty)
            {
                consumedInHand = null;
                var hand = gameplay.Player.transform;
                b.Item!.transform.SetPositionAndRotation(hand.TransformPoint(before.Position), hand.rotation * before.Rotation);
            }
            // A tool keeps its grab-time offset as its unequipped hold, so a late grant left the wrench at
            // an odd place in view once put away.
            else if ((mode == GrabMode.Hand || mode == GrabMode.Equip) && b.GrabLocalPosition is Vector3 grabbed && Time.unscaledTime - b.PendingSince < 3f)
            {
                // The native grab keeps the item where it sits in view. The grant comes a round trip after
                // the click, after the player turned or the item moved (out of a rolling barrow): it was
                // held beside or behind the player (#162). Put it back where it was clicked.
                var view = gameplay.Player.transform;
                b.Item!.transform.SetPositionAndRotation(view.TransformPoint(grabbed), view.rotation * b.GrabLocalRotation);
            }
            else if (mode == GrabMode.None && purchaseView is { } click && Time.unscaledTime - click.At < 5f)
            {
                var view = gameplay.Player.transform;
                var item = b.Item!.transform;
                item.SetPositionAndRotation(view.TransformPoint(click.WorldToView.MultiplyPoint3x4(item.position)),
                    view.rotation * (Quaternion.Inverse(click.Rotation) * item.rotation));
            }
            if (mode == GrabMode.None) purchaseView = null;
            b.GrabLocalPosition = null;
            if (b.Item is Tool_Chainsaw liftingSaw && liftingSaw.IntValue1 == 1) RetainChainsawRackContact(liftingSaw);
            VanillaGrab(b.Item!);
            if (b.PendingUse && !LocalHeld(b.Item)) b.PendingUse = false;
            // The host already approved the grab: open the pack on the next frame, not at the next
            // 10 Hz send tick (outside this apply, so the native use cannot re-enter it).
            if (b.PendingUse) replayUse = b;
            if (mode == GrabMode.Equip && b.Item is InteractableTools equipped && gameplay.Player.Get_HoldingItem == b.Item)
                gameplay.Player.EquipThisTool(equipped);
            return true;
        }
        public bool AllowFire(InteractableItems item) => host || !Handles(item);
        public bool Handles(InteractableItems? item) => !disposed && session?.IsRunning == true && item != null && byItem.ContainsKey(item);
        // PlayerInteractable dispatches mouse input straight to activeTool. A native equip can
        // leave that field set before the host grants the item lease (or after it is released).
        // Never let a guest invoke a shared tool in either state; clear the stale native equip.
        private bool ActiveToolLeaseValid(Binding b) => b.State.Exists && !b.Absorbed && !b.Releasing &&
            b.PendingGrab == GrabMode.None && session != null && b.State.Lease.Owner == session.LocalPeerId && b.State.Lease.Generation != 0;
        // Host: a shared item currently in another player's hand (or tool).
        internal bool HeldByRemotePlayer(InteractableItems? item) => host && item != null && byItem.TryGetValue(item, out var b) &&
            leases[b.Id].Owner != ItemLease.Unowned && leases[b.Id].Owner != 0;
        internal bool GuestRunning => !host && !disposed && session?.IsRunning == true;
        // Host: a native machine takes a shared item out of a guest's hand (the nitrous refill station
        // mounting a bottle). The lease ends here, the guest's hold is cancelled by the publish, and
        // the machine then owns the host's copy.
        internal bool HostReclaim(InteractableItems item)
        {
            if (!host || !byItem.TryGetValue(item, out var b) || b.Item == null) return false;
            var lease = leases[b.Id];
            if (lease.Owner == ItemLease.Unowned || lease.Owner == 0) return false;
            ReleaseLease(b.Id, lease.Owner, lease.Generation);
            b.LastOwnerUpdate = Time.unscaledTime;
            b.State = Capture(b); ConfigureBody(b, false); Publish(b);
            return true;
        }
        internal bool AllowActiveToolInput(InteractableTools? tool)
        {
            if (!GuestRunning || tool == null) return true;
            if (!byItem.TryGetValue(tool, out var b)) return true;
            if (ActiveToolLeaseValid(b))
            {
                if (!Ready || TransfersBlocked) return false;
                if (Integration.LocalInteraction.SelectsOperatedItem && gameplay.Player.Get_ActiveTool == tool)
                    // Selection and the following native action share the same ordered
                    // stream. Repeat it on input rather than caching an unconfirmed send.
                    return Request(b, ItemAction.SelectHandTool);
                return true;
            }
            if (gameplay.Player.Get_ActiveTool == tool) CancelLocal(tool);
            return false;
        }

        private void SelectGuestHandTool(uint peer, Binding binding, uint generation)
        {
            if (peer != 0 && binding.Item is InteractableTools tool && tool != null &&
                leases.Owns(binding.Id, peer, generation)) handTools[peer] = (binding.Id, generation);
        }

#if MONMP_DEVELOPMENT
        internal bool TestActiveToolInputAllowed(ulong id) => Ready && !TransfersBlocked && ActiveToolLeaseValid(bindings[id]);


        internal bool TestActiveToolInputWhileReleasing(ulong id)
        {
            var b = bindings[id]; bool wasReleasing = b.Releasing;
            b.Releasing = true;
            try { return TestActiveToolInputAllowed(id); }
            finally { b.Releasing = wasReleasing; }
        }


        internal bool TestActiveToolInputWhileTransfersBlocked(ulong id)
        {
            bool wasBlocked = TransfersBlocked;
            TransfersBlocked = true;
            try { return TestActiveToolInputAllowed(id); }
            finally { TransfersBlocked = wasBlocked; }
        }


        internal bool TestRejectEquippedToolDuringRelease(ulong id)
        {
            var b = bindings[id]; bool wasReleasing = b.Releasing;
            b.Releasing = true;
            try { return AllowActiveToolInput(b.Item as InteractableTools); }
            finally { b.Releasing = wasReleasing; }
        }
#endif

        internal bool AllowLocalFoodUpdate(Item_Poutine item) => host || disposed || applying || session == null ||
            (!byItem.ContainsKey(item) && !hidden.ContainsKey(item));
        public bool Grab(InteractableItems item)
        {
            if (!Handles(item)) return true;
            if (item is Item_Fish && item.IntValue1 == 1) return false;
            if (!applying)
            {
                bool queued = !TransfersBlocked && Ready && !gameplay.Player.IsHoldingAnObject && CanHandleItems(gameplay.Get_PlayerState) &&
                    BeginPickup(byItem[item], GrabMode.Hand);
                NotePickupAttempt(byItem[item], queued);
            }
            return applying;
        }
        // An equipped tool gripping a shared item (wrench/impact gun picking up a loose bolt).
        public bool ToolGrab(InteractableTools tool, InteractableItems item)
        {
            if (!Handles(item) || applying) return true;
            var b = byItem[item];
            if (OwnedLocally(b)) return true;
            if (!TransfersBlocked && Ready && CanHandleItems(gameplay.Get_PlayerState))
            { b.PendingGrab = GrabMode.Tool; b.PendingTool = tool; b.PendingSince = Time.unscaledTime; Request(b, ItemAction.Acquire); }
            return false;
        }
        internal void ToolGrabCompleted(InteractableTools tool, Hardware_Bolt? bolt)
        {
            // A host's bolt extracted from a pack acquires its final pose only after the native
            // wrench/impact grip finishes. Share that exact loose output now, retaining the host
            // lease and the existing guest extraction/ticket path below.
            if (host && Ready && bolt != null && !Placed(bolt) && tool == gameplay.Player.Get_ActiveTool)
            {
                var grip = tool is Tool_AdjustableSpanner ? SpannerGrip : tool is Tool_Air_Impact ? ImpactGrip : null;
                if (grip != null && ReferenceEquals(grip.GetValue(tool), bolt))
                {
                    try { if (EnsureHostBinding(bolt) == 0) nextDiscovery = 0; }
                    catch (Exception ex) { Debug.LogException(ex); nextDiscovery = 0; }
                }
                return;
            }
            if (!GuestRunning || bolt == null || !LocalHeld(bolt)) return;
            AdoptRecentPackedBolt(bolt, allowPlaced: false);
        }
        internal void AdoptPackedBoltForInstall(Hardware_Bolt? bolt)
        {
            if (!GuestRunning || bolt == null || MonMPPlugin.Instance?.World?.Bolts.IsDeferredInstall(bolt) != true) return;
            AdoptRecentPackedBolt(bolt, allowPlaced: true);
        }
        private bool AdoptRecentPackedBolt(Hardware_Bolt bolt, bool allowPlaced)
        {
            if (IsExactNativeOutputPending(bolt)) return false;
            if (byItem.ContainsKey(bolt) || adopting.ContainsKey(bolt) || baseline.Contains(bolt) || hidden.ContainsKey(bolt) ||
                !pendingPackedBoltOutputs.TryGetValue(bolt, out float expires) || Time.unscaledTime >= expires ||
                !bolt.gameObject.activeInHierarchy || !NearLocal(bolt.transform.position, 4)) return false;
            bool deferred = allowPlaced && MonMPPlugin.Instance?.World?.Bolts.IsDeferredInstall(bolt) == true;
            if (Placed(bolt) && !deferred) return false;
            if (!deferred && !LocalHeld(bolt)) return false;
            var prefab = Supported(bolt);
            if (prefab == null || prefab.Type != typeof(Hardware_Bolt)) return false;
            Adopt(bolt, prefab, held: true);
            return adopting.ContainsKey(bolt);
        }
        public bool Drop(InteractableItems item, bool thrown)
        {
            if (!Handles(item) || applying) return true;
            // InteractablePacks natively release themselves before spawning their contents.
            // Let that release finish only while the corresponding UseKeyDown is in flight.
            if (byItem[item].NativePackUse) return true;
            byItem[item].PendingUse = false;
            // Releasing a bolt onto an empty matching hole, or a part onto its socket, installs it
            // natively; the structure's adapter replicates the install and the loose item is absorbed.
            if (WillPlace(item)) return true;
            if (Ready)
            {
                var b = byItem[item];
                if (host) Request(b, thrown ? ItemAction.Throw : ItemAction.Drop);
                else if (OwnedLocally(b) && !b.Releasing)
                {
                    // Freeze the intent before cancelling the native hold. Waiting for
                    // the host kept the item under the camera for another round trip,
                    // then snapped it back behind a player who was turning quickly.
                    b.Releasing = true; b.HasTarget = false; b.Settled = true;
                    if (!Request(b, thrown ? ItemAction.Throw : ItemAction.Drop)) b.Releasing = false;
                    CancelLocal(item);
                    // The host still owns physics and accepts the exact lease/range.
                    ConfigureBody(b, true);
                    if (b.Releasing) BeginLocalDropPreview(b, thrown);
                }
            }
            return false;
        }
        private static bool PendingLocalRelease(Binding b, ItemState incoming, uint peer) =>
            b.Releasing && incoming.Lease.Owner == peer && incoming.Lease.Generation == b.State.Lease.Generation;
        private static bool EaseLocalDropAcknowledgement(Binding b, ItemState incoming, uint peer, bool forceValues) =>
            !forceValues && b.DropPreview?.HasMotion == true && MatchesLocalDropRelease(b, incoming, peer);
        private static bool MatchesLocalDropRelease(Binding b, ItemState incoming, uint peer) =>
            b.DropPreview != null && incoming.Exists && incoming.Lease.Owner == ItemLease.Unowned && incoming.ContainerEntity == 0 &&
            incoming.Lease.Generation == (b.DropPreview.Generation == uint.MaxValue ? 1 : b.DropPreview.Generation + 1) &&
            ((b.Releasing && b.State.Lease.Owner == peer && b.State.Lease.Generation == b.DropPreview.Generation) ||
             (b.DropPreviewAcknowledged != null && b.State.Lease.Owner == ItemLease.Unowned && b.State.Lease.Generation == incoming.Lease.Generation));
        private static bool KeepLocalDropAcknowledgement(Binding b, ItemState incoming, ItemPose worldPose, Vector3 canonicalGravity, uint peer, bool forceValues, float now)
        {
            if (forceValues || b.DropPreview == null ||
                (!b.DropPreview.Active && !b.DropPreview.CanRetainPose) ||
                !MatchesLocalDropRelease(b, incoming, peer) || b.Item == null ||
                canonicalGravity.sqrMagnitude <= 1e-8f ||
                (b.Item.transform.position - Position(worldPose)).sqrMagnitude > 4f) return false;
            var prior = b.DropPreviewAcknowledged;
            if (prior != null && incoming.Carrier != prior.Carrier) return false;
            // Compare consecutive host poses in the same carrier frame. A higher
            // delayed sample is not an upward bounce; a rising host trajectory is.
            float descent = prior == null ? 0 : Vector3.Dot(Position(incoming.Pose) - Position(prior.Pose), canonicalGravity);
            float lead = Vector3.Dot(b.Item.transform.position - Position(worldPose), Physics.gravity.normalized);
            float age = now - b.DropPreviewStartedAt;
            return LocalItemDropHandoff.Keep(age, b.DropPreview.Active && age < LocalItemDropPreview.Duration,
                b.DropPreview.HasMotion, descent, lead, b.DropPreview.Speed);
        }
        private Vector3 DropGravityProjection(ulong carrier)
        {
            var gravity = Physics.gravity.normalized;
            if (carrier == 0) return gravity;
            var frame = Carriers?.Resolve(carrier);
            if (frame == null) return Vector3.zero;
            // A local delta's dot product with this projection is its world-space
            // descent, including carrier rotation and scale without render motion.
            return new Vector3(Vector3.Dot(frame.TransformVector(Vector3.right), gravity),
                Vector3.Dot(frame.TransformVector(Vector3.up), gravity), Vector3.Dot(frame.TransformVector(Vector3.forward), gravity));
        }
        private void BeginLocalDropPreview(Binding b, bool thrown = false)
        {
            if (host || b.Item == null || LocalHeld(b.Item) || Placed(b.Item) || b.State.ContainerEntity != 0) return;
            Uncarry(b);
            var body = b.Item.rb;
            if (body != null) { body.position = b.Item.transform.position; body.rotation = b.Item.transform.rotation; }
            var velocity = thrown ? gameplay.Player.mainCamera.transform.forward * 4f : Vector3.zero;
            b.DropPreview = LocalItemDropPreview.Begin(b.Item, gameplay.PlayerWalking.transform, b.State.Lease.Generation, Time.unscaledTime, velocity);
            b.DropPreviewAcknowledged = null; b.DropPreviewStartedAt = Time.unscaledTime;
        }
        private bool PresentLocalDrop(Binding b, float now)
        {
            var preview = b.DropPreview;
            if (preview == null) return false;
            bool pending = b.Releasing && OwnedLocally(b) && b.State.Lease.Generation == preview.Generation;
            bool acknowledged = b.DropPreviewAcknowledged != null && MatchesLocalDropRelease(b, b.State, session!.LocalPeerId);
            if ((!pending && !acknowledged) || b.Item == null || LocalHeld(b.Item) || Placed(b.Item) || b.PendingGrab != GrabMode.None ||
                b.State.ContainerEntity != 0 || pending && b.State.Carrier != 0)
            { b.DropPreview = null; b.DropPreviewAcknowledged = null; return false; }
            preview.Step(b.Item, gameplay.PlayerWalking.transform, now, Time.deltaTime);
            if (acknowledged && !KeepLocalDropAcknowledgement(b, b.State, WorldPose(b.State), DropGravityProjection(b.State.Carrier), session!.LocalPeerId, false, now))
            {
                // Continuous motion preserves the fall until the host target can sustain it.
                // Contact stops speculative motion; a real bounce/correction or
                // the bounded acknowledgement deadline still returns authority.
                Apply(b.Id, b.State);
                return false;
            }
            return true;
        }
        // Guest: a wrench or impact gun let go of its bolt. Release it at once from where it left the
        // mouth, flung forward on a right-click as natively; the 0.1 s pass sent a plain drop, and the
        // hold's trailing target pulled the loose bolt back toward where the hand had been.
        internal void ToolBoltReleased(InteractableItems? bolt, bool thrown)
        {
            if (host || bolt == null || !Ready || !byItem.TryGetValue(bolt, out var b) || b.Item == null ||
                !OwnedLocally(b) || b.Releasing || LocalHeld(bolt) || Placed(bolt)) return;
            b.HasTarget = false;
            b.Releasing = true; Request(b, thrown ? ItemAction.Throw : ItemAction.Drop);
        }
        private bool WillPlace(InteractableItems item)
        {
            if (item is Hardware_Bolt bolt)
            {
                var hole = CurrentBoltHole.GetValue(gameplay.Player) as Generated_BoltHole;
                return hole != null && hole.boltHoleDetails != null && !hole.boltHoleDetails.isBoltInstalled && hole.boltHoleDetails.boltSize == bolt.boltSize;
            }
            return item is InteractableParts && CurrentGeneratedPart.GetValue(gameplay.Player) as Object != null;
        }
        private static readonly Dictionary<Type, bool> nativeUse = new Dictionary<Type, bool>();
        private int unavailableUseWarnings;
        private static bool HasNativeUse(Type type)
        {
            if (nativeUse.TryGetValue(type, out bool has)) return has;
            var method = type.GetMethod(nameof(InteractableItems.Use), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, Type.EmptyTypes, null);
            has = method != null && method.DeclaringType != typeof(Interactable) && method.DeclaringType != typeof(InteractableItems);
            nativeUse[type] = has;
            return has;
        }
        // The host's lock check refuses silently; say why here, with native Use's own messages. Furniture
        // spawned away from the player's land looked impossible to lock (#171).
        private bool LockAllowedHere(InteractableItems item)
        {
            if (!(item is Furniture_Decoration || item is Garden_OutdoorPlanter) || item.IntValue1 != 0) return true;
            if (item is Furniture_Decoration decoration && !DecorationState.CanFreeze(decoration)) return true;
            if (gameplay.Player.IsHoldingAnObject) return false;
            string? error = !gameplay.IsOnHomeLand(item.transform.position) ? "ERROR_General_NotOnYourLand"
                : item.rb != null && !item.rb.isKinematic && item.rb.velocity.sqrMagnitude > 0.0001f ? "DECO_CantFreezeObjectIsMoving" : null;
            if (error == null) return true;
            try { Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText(error)); } catch { }
            return false;
        }

        // Returns true when the native use should run.
        public bool Use(InteractableItems item)
        {
            if (!host && adoptionCandidates.Contains(item)) return false;
            if (!host && item is InteractablePacks && packedAdoptions.Count >= MaxPendingPackedOutputs) return false;
            if (!Handles(item) || !Ready || TransfersBlocked) return false;
            var b = byItem[item];
            if (b.State.Lease.Owner != ItemLease.Unowned && b.State.Lease.Owner != session!.LocalPeerId)
            { MonMPPlugin.Instance!.ReportItemUseUnavailable("Another player is holding that item."); return false; }
            if (!host && ItemPrefabCatalog.HostUse(prefabs[b.Prefab].Type)) { Request(b, ItemAction.Interact); return false; }
            if (General(prefabs[b.Prefab]))
            {
                if (item is InteractableTools tool && tool.canEquip())
                {
                    // Native EquipTool sets activeTool before calling GrabInteractable. A shared
                    // GrabIntercept defers that grab for the host lease, leaving activeTool set
                    // without ownership. Defer the entire equip and replay it after the grant.
                    if (gameplay.Player.Get_HoldingItem == item) return OwnedLocally(b);
                    if (!OwnedLocally(b) && b.State.Lease.Owner == ItemLease.Unowned &&
                        !gameplay.Player.IsHoldingAnObject && CanHandleItems(gameplay.Get_PlayerState))
                    {
                        BeginPickup(b, GrabMode.Equip);
                    }
                    return false;
                }
                if (item is InteractablePacks)
                {
                    // A pack mutates its count, may destroy itself, and spawns/grabs a new item.
                    // On guests, defer that native sequence until this pack's host lease arrives.
                    if (host || OwnedLocally(b)) { b.NativePackUse = true; return true; }
                    if (b.State.Lease.Owner != ItemLease.Unowned)
                    { MonMPPlugin.Instance!.ReportItemUseUnavailable("Another player is holding that pack."); return false; }
                    // A guest's equipped tool can make IsHoldingAnObject true while HoldingItem is
                    // null. Never let the native pack path run around the lease gate in that state.
                    if (gameplay.Player.IsHoldingAnObject && gameplay.Player.Get_HoldingItem != item)
                    {
                        MonMPPlugin.Instance!.ReportItemUseUnavailable("Put down your item or tool before using that pack.");
                        return false;
                    }
                    if (!gameplay.Player.IsHoldingAnObject && CanHandleItems(gameplay.Get_PlayerState))
                    {
                        b.PendingGrab = GrabMode.Hand; b.PendingUse = true; b.PendingSince = Time.unscaledTime;
                        NoteGrabPose(b); Request(b, ItemAction.Acquire);
                    }
                    return false;
                }
                // Equipping a held tool is a local hand change. The host's own uses are authoritative
                // and replicate through captured state.
                if (host || item is InteractableTools) return true;
                // Most items have no use at all; natively the key does nothing, so stay silent.
                if (!HasNativeUse(item.GetType()) && item.interactableItemType != InteractableItemType.Food &&
                    item.interactableItemType != InteractableItemType.Drink) return false;
                if (unavailableUseWarnings < 16)
                {
                    unavailableUseWarnings++;
                    Debug.LogWarning("MonMP guest item use has no routed handler: type=" + item.GetType().FullName +
                        "; prefab=" + prefabs[b.Prefab].Path + "; entity=" + b.Id.ToString("X") + ".");
                }
                MonMPPlugin.Instance!.ReportItemUseUnavailable("Only the host can use that item in multiplayer for now.");
                return false;
            }
            if ((prefabs[b.Prefab].Features & PrefabFeatures.Interact) != 0)
            {
                if (!LockAllowedHere(item)) return false;
                Request(b, ItemAction.Interact); return false;
            }
            if ((prefabs[b.Prefab].Features & PrefabFeatures.Consume) == 0) return false;
            if (b.Prefab == 2)
            {
                var needs = Object.FindObjectOfType<BasicNeeds_Manager>();
                if (needs == null || needs.GetAnxiety > 0.8f || (int)AccessTools.Field(typeof(BasicNeeds_Manager), "EnergyDrinkInBlood").GetValue(needs) > 100)
                { MonMPPlugin.Instance!.ReportItemUseUnavailable("You cannot drink more coffee right now."); return false; }
            }
            RequestConsume(b);
            return false;
        }
        // Guest took one item out of a shared pack natively (by hand or a wrench); the spawned item is
        // adopted separately, and the host decrements its authoritative pack.
        public void Unpacked(InteractablePacks pack, InteractableItems? output = null)
        {

#if MONMP_DEVELOPMENT
            if (TestToolPackDiagnostics) TraceToolPack("guest-unpacked count=" + pack.Value1 + "; ready=" + Ready + "; applying=" + applying + "; tracked=" + Handles(pack));
#endif

            if (host || applying || !Ready || !Handles(pack)) return;
            var b = byItem[pack];

#if MONMP_DEVELOPMENT
            if (TestToolPackDiagnostics) TraceToolPack("guest-unpacked id=" + b.Id.ToString("X") + "; lease=" + b.State.Lease.Owner + "/" + b.State.Lease.Generation + "; releasing=" + b.Releasing + "; nativeUse=" + b.NativePackUse);
#endif

            if (b.LastUnpackedFrame == Time.frameCount && b.LastUnpackedCount == pack.Value1) return;
            b.LastUnpackedFrame = Time.frameCount; b.LastUnpackedCount = pack.Value1;
            output ??= gameplay.Player.Get_HoldingItem;
            if (output != null) ItemChanged(output);
            // One command couples this exact prediction to the debit. It cannot fall through to
            // generic adoption after a rejected pack extraction or a lost/deferred receipt.
            b.Releasing = true; b.HasTarget = false;
            if (output == null || output == pack || !QueuePackedOutput(b, output))
            {
                if (output != null && output != pack) { CancelLocal(output); ignored.Add(output); output.gameObject.SetActive(false); Object.Destroy(output.gameObject); }
                Apply(b.Id, b.State, true);
                MonMPPlugin.Instance?.ReportItemUseUnavailable("The pack output could not be shared. Its contents were kept by the host.");
                return;
            }
            lastLocalUnpack = Time.unscaledTime;
            // Offer the unpacked item to the host promptly instead of on the once-a-second scan.
            nextDiscovery = Mathf.Min(nextDiscovery, Time.unscaledTime + 0.05f);
        }
        internal bool AllowToolPackPicking(InteractableTools? tool)
        {
            if (!GuestRunning || tool == null) return true;
            var holeField = tool is Tool_AdjustableSpanner ? SpannerBoltHole : tool is Tool_Air_Impact ? ImpactBoltHole : null;
            var gripField = tool is Tool_AdjustableSpanner ? SpannerGrip : tool is Tool_Air_Impact ? ImpactGrip : null;
            if (holeField == null || holeField.GetValue(tool) != null || gripField?.GetValue(tool) != null) return true;
            var target = gameplay.Player.Get_CurrentInteractable;
            var pack = target == null ? null : target.GetComponent<InteractablePacks>();
            if (pack == null || !Handles(pack)) return true;
            if (pack.Value1 <= 0) return false;
            var b = byItem[pack];
            if (OwnedLocally(b)) return Ready && !TransfersBlocked && !b.Releasing;
            if (b.State.Lease.Owner != ItemLease.Unowned)
            {
                MonMPPlugin.Instance!.ReportItemUseUnavailable("Another player is holding that pack.");
                return false;
            }
            if (b.PendingGrab != GrabMode.None || !Ready || TransfersBlocked || !CanHandleItems(gameplay.Get_PlayerState)) return false;
            b.PendingGrab = GrabMode.ToolPack; b.PendingTool = tool; b.PendingSince = Time.unscaledTime;
            Request(b, ItemAction.Acquire);
            return false;
        }
        internal void NativePackedItemExtracted(InteractablePacks pack, GameObject? result, float countBefore)
        {
            if (!GuestRunning || result == null || !Handles(pack) || pack.Value1 >= countBefore) return;
            var bolt = result.GetComponent<Hardware_Bolt>();
            if (bolt != null)
            {
                if (!pendingPackedBoltOutputs.ContainsKey(bolt) && pendingPackedBoltOutputs.Count >= 64)
                {
                    InteractableItems? oldest = null; bool foundOldest = false; float oldestExpiry = float.PositiveInfinity;
                    foreach (var candidate in pendingPackedBoltOutputs)
                        if (!foundOldest || candidate.Value < oldestExpiry) { oldest = candidate.Key; oldestExpiry = candidate.Value; foundOldest = true; }
                    if (foundOldest) pendingPackedBoltOutputs.Remove(oldest!);
                }
                pendingPackedBoltOutputs[bolt] = Time.unscaledTime + 6f;
            }
            Unpacked(pack, result.GetComponent<InteractableItems>());
        }
        internal bool AllowToolUnpack(InteractablePacks pack)
        {
            if (!GuestRunning || !Handles(pack)) return true;
            if (TransfersBlocked || pack.Value1 <= 0 || packedAdoptions.Count >= MaxPendingPackedOutputs) return false;
            var b = byItem[pack];
            if (OwnedLocally(b)) return Ready && !b.Releasing;
            // A wrench or impact gun pulling a bolt straight out of a pack (#197): lease the pack, take one
            // bolt into the tool's grip once the host grants it, then let the pack go again.
            var tool = gameplay.Player.Get_ActiveTool;
            if (tool is Tool_AdjustableSpanner || tool is Tool_Air_Impact)
            {
                if (b.State.Lease.Owner != ItemLease.Unowned)
                { MonMPPlugin.Instance!.ReportItemUseUnavailable("Another player is holding that pack."); return false; }
                if (Ready && b.PendingGrab == GrabMode.None && CanHandleItems(gameplay.Get_PlayerState))
                { b.PendingGrab = GrabMode.ToolPack; b.PendingTool = tool; b.PendingSince = Time.unscaledTime; Request(b, ItemAction.Acquire); }
                return false;
            }
            MonMPPlugin.Instance!.ReportItemUseUnavailable("Pick up the pack first so the host can approve this use.");
            return false;
        }
        internal void EndNativeUse(InteractableItems? item)
        {
            if (item != null && byItem.TryGetValue(item, out var b)) b.NativePackUse = false;
        }
        private void RequestConsume(Binding b)
        {
            // Natively the cap or can opens on the click; the drink itself waits for the host's receipt.
            ConsumptionPresentation.LocalOpen(prefabs[b.Prefab].Type);
            consumption.Track(session!.Epoch, b.Id, b.State.Lease.Generation, b.Prefab);
            // The leftover (the empty can) arrives from the host a moment later; remember where the
            // consumed item sat in the hand so the leftover is taken up exactly there, as natively.
            if (b.Item != null && ItemPrefabCatalog.Leftover(prefabs[b.Prefab].Type) != null && LocalHeld(b.Item))
            {
                var hand = gameplay.Player.transform;
                consumedInHand = (hand.InverseTransformPoint(b.Item.transform.position), Quaternion.Inverse(hand.rotation) * b.Item.transform.rotation, Time.unscaledTime);
            }
            Request(b, ItemAction.Consume);
        }
        private (Vector3 Position, Quaternion Rotation, float At)? consumedInHand;
        internal void PlayConsumption(PresentationEvent e)
        {
            if (ConsumptionPresentation.Play(e))
#if MONMP_DEVELOPMENT
TestRemoteConsumeSounds++;
#else
{}
#endif

        }

#if MONMP_DEVELOPMENT
        internal int TestRemoteConsumeSounds { get; private set; }
#endif

        private void DeliverConsumption(Binding b)
        {
            var receipt = b.State.Consumption;
            if (receipt == null || session == null || receipt.Consumer != session.ProfileId) return;
            if (!consumption.Deliver(session.Epoch, b.Id, receipt.Generation, b.Prefab,
                () => PersonalUse(prefabs[b.Prefab], b.State.Values), out bool applied)) return;

#if MONMP_DEVELOPMENT
            if (TestWithholdConsumptionAcknowledgment) return;
#endif

            channel!.Request(b.Id, new ItemCommand(applied ? ItemAction.ConsumeApplied : ItemAction.ConsumeRejected,
                receipt.Generation, WorldPose(b.State)).Encode(), true);
        }
        // The consumer's own effect of a consumed item, run on the consumer's machine.
        private static bool PersonalUse(Prefab prefab, ItemValues values)
        {
            if (prefab.Type == typeof(Item_Poutine)) return FoodState.Consume(prefab.Asset, values);
            if (prefab.Type == typeof(Item_MapleSyrupCan))
            {
                // The native can's drink, without its world half (the empty can is the host's).
                GameManager.Get_PlayerStats[1]++;
                if (GameManager.Get_PlayerStats[1] >= 50f) Singleton<Steam_AchievementManager>.i.AchievementDone(AchievementType.SyrupDrank_50);
                if (GameManager.Get_PlayerStats[1] >= 250f) Singleton<Steam_AchievementManager>.i.AchievementDone(AchievementType.SyrupDrank_250);
                Object.FindObjectOfType<BasicNeeds_Manager>().Fill_Thirst(90f, _sound: true);
                return true;
            }
            if (prefab.Type == typeof(Item_MapleSyrup))
            {
                // The native bottle's drink without its cap sound, which the click already played.
                GameManager.Get_PlayerStats[1]++;
                if (GameManager.Get_PlayerStats[1] >= 50f) Singleton<Steam_AchievementManager>.i.AchievementDone(AchievementType.SyrupDrank_50);
                if (GameManager.Get_PlayerStats[1] >= 250f) Singleton<Steam_AchievementManager>.i.AchievementDone(AchievementType.SyrupDrank_250);
                var bottleNeeds = Object.FindObjectOfType<BasicNeeds_Manager>();
                switch (prefab.Asset.GetComponent<InteractableItems>().interactableItemType)
                {
                    case InteractableItemType.Food: bottleNeeds.Fill_Hunger(80f, _sound: true); return true;
                    case InteractableItemType.Drink: bottleNeeds.Fill_Thirst(65f, _sound: true); return true;
                    default: return false;
                }
            }
            return prefab.Asset.GetComponent<InteractableItems>().Use();
        }
        private bool Request(Binding b, ItemAction action)
        {
            if (b.Item == null) return false;
            var t = b.Item.transform; Vector3 p = t.position; Quaternion q = t.rotation;
            if (!ItemPose.TryLocal(p.x, p.y, p.z, q.x, q.y, q.z, q.w, out var pose))
            {
                // A flung or non-finite local copy is not the player's intent: put it back where the
                // host last had it instead of sending (or failing on) an impossible pose.
                if (brokenPoseLogged.Add(b.Id))
                    Debug.LogWarning("MonMP item " + b.Id + " (" + b.Item.name + ") had an invalid local pose " + p + " " + q + "; restored the host pose.");
                CancelLocal(b.Item);
                var restore = WorldPose(b.State);
                if (restore.IsValid) t.SetPositionAndRotation(Position(restore), Rotation(restore));
                if (b.Item.rb != null && !b.Item.rb.isKinematic) { b.Item.rb.velocity = Vector3.zero; b.Item.rb.angularVelocity = Vector3.zero; }
                return false;
            }
            bool playerDrop = Patches.CargoDropInputScope.Active && (action == ItemAction.Drop || action == ItemAction.Throw);
            channel!.Request(b.Id, new ItemCommand(action, b.State.Lease.Generation, pose, playerDrop).Encode(), true);
            return true;
        }
        private readonly HashSet<ulong> brokenPoseLogged = new HashSet<ulong>();
        // Items whose type or placement can never be shared (unsupported prefabs, consumption
        // effect copies); skipped by later discovery passes.
        private readonly HashSet<InteractableItems> ignored = new HashSet<InteractableItems>();
        // Guest: items a local native presentation is moving (a posted letter sliding into the box). The
        // host's poses and body setup would fight the slide, and its removal would delete the copy
        // mid-slide; the presentation destroys it when done (#204).
        private readonly HashSet<ulong> presenting = new HashSet<ulong>();
        internal void Present(ulong id) { if (!host && bindings.ContainsKey(id)) presenting.Add(id); }
        // Native GrabInteractable allows a player lying on a mechanic creeper to grab and use items
        // (it only refuses the wheelbarrow), so leases must too (#209).
        private static bool CanHandleItems(PlayerState state) => state == PlayerState.Walking || state == PlayerState.InVehicle_Driver ||
            state == PlayerState.InVehicle_Passenger || state == PlayerState.InCreeper || state == PlayerState.InFurnitureChair;
        private bool TryItemPlayerPosition(uint peer, out Vector3 player)
        {
            player = default;
            if (peer == 0)
            {
                if (!CanHandleItems(gameplay.Get_PlayerState)) return false;
                player = gameplay.PlayerWalking.transform.position;
            }
            else
            {
                if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2 || !CanHandleItems((PlayerState)p.Pose.State)) return false;
                player = new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z);
            }
            return true;
        }
        private bool InRange(uint peer, Vector3 target, float range) =>
            TryItemPlayerPosition(peer, out var player) && (target - player).sqrMagnitude <= range * range;
        private bool HeldPoseInRange(uint peer, Binding b, ItemPose pose)
        {
            var item = b.Item;
            if (!(item is Furniture_Decoration) && !(item is Garden_OutdoorPlanter))
                return InRange(peer, Position(pose), 4.5f);
            if (!TryItemPlayerPosition(peer, out var player)) return false;
            // Native holding is camera-relative, not feet-relative. Looking up can
            // legitimately lift a pivot above the old sphere around the walker's feet.
            var position = Position(pose);
            var nearestBody = player + Vector3.up * Mathf.Clamp(position.y - player.y, 0, 1.6f);
            if ((position - nearestBody).sqrMagnitude <= 4.5f * 4.5f) return true;
            var mesh = item.meshRenderer;
            if (mesh == null || !mesh.transform.IsChildOf(item.transform)) return false;
            // A wide object's surface can be nearby even when its pivot/center is not.
            // Reproject the trusted native mesh bounds into the requested pose; the
            // host's interpolated rotation must not determine whether a Move is legal.
            return HeldSurfaceNear(item, mesh, pose, player) || HeldSurfaceNear(item, mesh, pose, player + Vector3.up * 1.6f);
        }
        private static bool HeldSurfaceNear(InteractableItems item, Renderer mesh, ItemPose pose, Vector3 anchor)
        {
            var root = item.transform;
            var requestedRotation = Rotation(pose);
            var inCurrentFrame = root.position + root.rotation * (Quaternion.Inverse(requestedRotation) * (anchor - Position(pose)));
            var local = mesh.transform.InverseTransformPoint(inCurrentFrame);
            var closest = mesh.transform.TransformPoint(mesh.localBounds.ClosestPoint(local));
            var requestedClosest = Position(pose) + requestedRotation * (Quaternion.Inverse(root.rotation) * (closest - root.position));
            return (requestedClosest - anchor).sqrMagnitude <= 4.5f * 4.5f;
        }
        private void RejectedHeldMove(uint peer, Binding b, ItemCommand command)
        {
            if (peer == 0 || (!(b.Item is Furniture_Decoration) && !(b.Item is Garden_OutdoorPlanter))) return;
            float now = Time.unscaledTime;
            if (b.RejectedMoveSince < 0) b.RejectedMoveSince = now;
            if (now - b.RejectedMoveSince < 1 || now < b.NextMoveWarning) return;
            b.NextMoveWarning = now + 10;
            var lease = leases[b.Id];
            string player = presence.TryGetValue(peer, out var p)
                ? new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z) + "; state=" + p.Pose.State + "; pitch=" + p.Pose.Pitch + "; age=" + (now - p.Time).ToString("F2")
                : "missing";
            Debug.LogWarning("MonMP held furniture move rejected: item=" + b.Id + "; peer=" + peer +
                "; owner=" + lease.Owner + "; generation=" + command.Generation + "/" + lease.Generation +
                "; requested=" + Position(command.Pose) + "; rotation=" + Rotation(command.Pose) + "; player=" + player);
        }
        [MonMP.Core.World.WorldCommandAdapters(ItemState.Adapter, 3)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host) return;
            if (ItemPickupRequest.Is(payload)) { PickupCommand(peer, id, ItemPickupRequest.Decode(payload)); return; }
            if (PackedItemSpawnRequest.Is(payload)) { PackedOutputCommand(peer, id, PackedItemSpawnRequest.Decode(payload)); return; }
            if (SprayPaintCommand.Is(payload)) return;
            if (prefabByDescriptor.TryGetValue(id, out ushort adoptedPrefab)) { AdoptRequest(peer, id, adoptedPrefab, payload); return; }
            if (!bindings.TryGetValue(id, out var b)) return;
            if (LooseSuspensionEditCommand.Is(payload))
            { LooseSuspensionEdit(peer, b, LooseSuspensionEditCommand.Decode(payload)); return; }
            if (ChainsawHolderCommand.Is(payload)) { ChainsawDockCommand(peer, b, ChainsawHolderCommand.Decode(payload)); return; }
            if (MagneticTrayCommand.Is(payload)) { TrayCommand(peer, b, MagneticTrayCommand.Decode(payload)); return; }
            if (LiftCommand.Is(payload)) { Lift(peer, b, LiftCommand.Decode(payload)); return; }
            if (CanisterCapCommand.Is(payload)) { CanisterCapCommandFrom(peer, b, CanisterCapCommand.Decode(payload)); return; }
            if (!ItemCommand.TryDecode(payload, out var command)) return;
            if (command.Action == ItemAction.ConsumeApplied || command.Action == ItemAction.ConsumeRejected)
            { AcknowledgeConsumption(peer, b, command); return; }
            bool releasing = command.Action == ItemAction.Drop || command.Action == ItemAction.Throw;
            if (TransfersBlocked && command.Action != ItemAction.Move && !releasing && command.Action != ItemAction.Absorb) return;
            if (b.Item == null || !b.State.Exists) return;
            if (command.Action == ItemAction.SelectHandTool)
            {
                // This is only a choice among existing leases, never a gameplay
                // action. The action itself checks posture, reach and freshness.
                SelectGuestHandTool(peer, b, command.Generation);
                return;
            }
            if (command.Action == ItemAction.Interact)
            { Interact(peer, b, command); return; }
            if (command.Action == ItemAction.Consume)
            { Consume(peer, b, command); return; }
            if (command.Action == ItemAction.Unpacked)
            {
                if (peer == 0) Unpack(peer, b, command);
                else channel!.Reply(peer, b.Id, NativeErrorReply.Encode("Unpack requires an exact packed-output request."));
                return;
            }
            if (command.Action == ItemAction.Ignite)
            { Ignite(peer, b); return; }
            if (command.Action == ItemAction.Planted)
            { Planted(peer, b, command); return; }
            if (command.Action == ItemAction.Merged)
            { MergeSeeds(peer, b, command); return; }
            if (command.Action == ItemAction.BasketPoured)
            { PourBasket(peer, b, command); return; }
            if (command.Action == ItemAction.Acquire)
            {
                // Two leases per player: a hand item or equipped tool, plus what that tool grips.
                if (b.Item is Item_Fish && b.Item.IntValue1 == 1) return;
                var containingTray = b.Item is Hardware_Bolt trayBolt ?
                    trayBolt.isBoltInMagneticTray ?? trayBolt.transform.parent?.GetComponent<Tool_MagneticTray>() : null;
                var trayBinding = containingTray != null && byItem.TryGetValue(containingTray, out var foundTray) ? foundTray : null;
                if (containingTray != null && trayBinding == null) return;
                if (trayBinding != null && ((leases[trayBinding.Id].Owner != ItemLease.Unowned && leases[trayBinding.Id].Owner != peer) ||
                    !InRange(peer, containingTray!.transform.position, 4))) return;
                if (!InRange(peer, b.Item.transform.position, 4) || (containingTray == null && !b.Item.canGrab()) ||
                    !AcquireLease(id, peer, command.Generation, 2)) return;
                if (containingTray != null && b.Item is Hardware_Bolt containedBolt) MagneticTrayNative.Detach(containedBolt);
                // A guest lifting the chainsaw off its rack: native Grab only runs on the guest,
                // so undock the host's copy here (rack flag, pickup time, rigidbody).
                if (peer != 0 && b.Item is Tool_Chainsaw rackedChainsaw && rackedChainsaw.IntValue1 == 1)
                {
                    RetainChainsawRackContact(rackedChainsaw);
                    rackedChainsaw.IntValue1 = 0;
                    ChainsawPickTime?.SetValue(rackedChainsaw, Time.realtimeSinceStartup);
                    rackedChainsaw.transform.SetParent(null, true);
                    if (rackedChainsaw.rb == null) rackedChainsaw.CreateBasics();
                }
                // A prior holder's motion target can survive a drop/throw while the host body
                // travels freely. Start this lease at that body's current pose, not the old hand.
                b.HasTarget = false; b.Settled = true; b.RejectedMoveSince = -1;
                b.LastOwnerUpdate = Time.unscaledTime; b.State = Capture(b);

#if MONMP_DEVELOPMENT
                if (TestToolPackDiagnostics) TraceToolPack("host-acquire id=" + b.Id.ToString("X") + "; generation=" + b.State.Lease.Generation +
                    "; body=" + b.Item!.transform.position + "; captured=" + Position(b.State.Pose) + "; oldTarget=" + b.TargetPosition + "; hasTarget=" + b.HasTarget);
#endif

                if (b.Item is Item_Lantern) b.Item.IntValue2 = 1;
                if (peer == 0) Grant(b);
                else { b.Item.transform.SetParent(null); ConfigureBody(b, false); }
                Publish(b); return;
            }
            if (command.Action == ItemAction.Absorb)
            {
                // The guest installed its copy into a hole/socket; that adapter replicates the install.
                if (peer == 0 || !leases.Owns(id, peer, command.Generation) || !InRange(peer, Position(command.Pose), 6)) return;
                ReleaseLease(id, peer, command.Generation);
                identities.Unbind(id); byItem.Remove(b.Item); b.Item.gameObject.SetActive(false); Object.Destroy(b.Item.gameObject); b.Item = null;
                b.State = Capture(b); channel!.Publish(id, b.State.Encode()); return;
            }
            if (!leases.Owns(id, peer, command.Generation))
            { if (command.Action == ItemAction.Move) RejectedHeldMove(peer, b, command); return; }
            bool acceptedPose = HeldPoseInRange(peer, b, command.Pose);
            if (!acceptedPose && !releasing)
            { if (command.Action == ItemAction.Move) RejectedHeldMove(peer, b, command); return; }
            // A guest stops sending held poses after its native drop. Refusing the release
            // strands both copies kinematic until the five-second owner timeout. Release
            // the exact holder even if its final pose/presence fails validation, but use
            // only the host's last accepted pose in that case, never the rejected position.
            var finalPose = acceptedPose ? command.Pose : CaptureOwnerPose(b, leases[id]);
            b.LastOwnerUpdate = Time.unscaledTime;
            if (peer != 0)
            {
                if (command.Action == ItemAction.Move) SetTarget(b, command.Pose);
                else
                {
                    var position = Position(finalPose); var rotation = Rotation(finalPose);
                    b.Item.transform.SetPositionAndRotation(position, rotation);
                    // A release can arrive between physics steps. The held render pose
                    // and Rigidbody pose need not agree yet; carrier capture reads the
                    // body immediately after it becomes dynamic. Teleport that body too
                    // so settling starts at the accepted drop, not an older held sample.
                    var body = b.Item.rb;
                    if (body != null) { body.position = position; body.rotation = rotation; }
                }
            }
            if (releasing)
            {
                if (peer == 0) CancelLocal(b.Item);
                ReleaseLease(id, peer, command.Generation);
                b.HasTarget = false; b.Settled = true;
                if (b.FishingTrashOutput) b.FishingTrashExpiresAt = Time.unscaledTime + 6f;
                b.State = Capture(b); ConfigureBody(b, false);
                b.Item.Time_LastUnGrab = Time.timeSinceLevelLoad;
                RefreshPlanterLocation(b.Item);
                // Saving may drain an existing hold, but cannot start a new throw. A
                // throw also needs an accepted final pose and fresh eligible presence.
                if (command.Action == ItemAction.Throw && acceptedPose && !TransfersBlocked && b.Item.rb != null)
                {
                    float yaw = peer == 0 ? gameplay.Player.transform.eulerAngles.y : presence[peer].Pose.Yaw;
                    float pitch = peer == 0 ? Mathf.DeltaAngle(0, gameplay.Player.mainCamera.transform.eulerAngles.x) : presence[peer].Pose.Pitch;
                    b.Item.rb.velocity = Quaternion.Euler(pitch, yaw, 0) * Vector3.forward * 4;
                }
                if (command.PlayerDrop && acceptedPose && !TransfersBlocked) ArmCargoDrop(b);
            }
            Publish(b);
            if (peer != 0 && (command.Action == ItemAction.Drop || command.Action == ItemAction.Throw))
                MonMP.Plugin.Patches.SharedWoodLogTriggerGuard.ReplayAfterDrop(this, peer, id, command);
        }
        private void Rejected(uint peer, ulong id, byte[] payload)
        {
            if (host && ItemPickupRequest.Is(payload))
            {
                var request = ItemPickupRequest.Decode(payload);
                channel!.Reply(peer, id, new ItemPickupReply(request.Nonce, request.Generation, ItemPickupOutcome.Refused).Encode());
                return;
            }
            if (host && LooseSuspensionEditCommand.TryGetNonce(payload, out uint nonce))
                ReplyLooseSuspensionEdit(peer, id, nonce, LooseSuspensionEditQueue.RejectedReply);
        }
        // Guest: set while this adapter lights its own copy of a host-burned item.
        internal static bool ApplyingFire { get; private set; }
        // Guest: set while presentation fires tick; their spread is the host's decision.
        internal static bool GuestFireTicking { get; set; }
        internal void NoteBurned(InteractableItems item)
        {
            if (!host || item == null || !byItem.TryGetValue(item, out var b)) return;
            b.Burned = true;
        }
        internal void NoteSold(InteractableItems item)
        {
            if (!host || item == null || !byItem.TryGetValue(item, out var b)) return;
            b.Sold = true;
        }
        // Guest: the host's copy caught fire. Light ours natively (log fire and particles) or play
        // the paper's burst, instead of silently removing the item.
        private bool BurnLocalCopy(InteractableItems item)
        {
            try
            {
                CancelLocal(item); byItem.Remove(item);
                if (item is Newspaper_Crumpled || item is Tool_Envelope)
                {
                    Object.Instantiate(Singleton<GameAssets>.i.VFX_TinyExplosion, item.transform.position, Quaternion.identity).SetActive(true);
                    item.gameObject.SetActive(false); Object.Destroy(item.gameObject);
                    return true;
                }
                if (!item.CanBeLitOnFire || item.transform.parent != null) return false;
                ApplyingFire = true;
                try { item.StartFire(); }
                finally { ApplyingFire = false; }
                return item.GetComponent<WoodOnFire>() != null;
            }
            catch (Exception ex) { Debug.LogWarning("MonMP local fire presentation failed: " + ex.Message); return false; }
        }
        private readonly Dictionary<ulong, float> igniteRequests = new Dictionary<ulong, float>();
        // Guest: one request per paper while the torch keeps touching it. The small burst is local
        // presentation; the paper disappears when the host's removal arrives.
        internal void RequestIgnite(InteractableItems item)
        {
            if (!Ready || !byItem.TryGetValue(item, out var b)) return;
            float now = Time.unscaledTime;
            if (igniteRequests.TryGetValue(b.Id, out float until) && now < until) return;
            igniteRequests[b.Id] = now + 1f;
            if (igniteRequests.Count > 64) foreach (var stale in igniteRequests.Where(pair => pair.Value < now).Select(pair => pair.Key).ToArray()) igniteRequests.Remove(stale);
            // The burst plays once, when the host's burned removal arrives.
            channel!.Request(b.Id, new ItemCommand(ItemAction.Ignite, Math.Max(1u, b.State.Lease.Generation), Pose(item.transform)).Encode(), true);
        }
        // Guest: one cutting from a held shared pile went into the ground (the sapling itself goes
        // through the forest adapter). The last one leaves the pile like an installed part.
        internal void CuttingPlanted(Garden_TreeCuttings cutting, bool spent)
        {
            if (host || !Ready || cutting == null || !byItem.TryGetValue(cutting, out var b) || !OwnedLocally(b)) return;
            if (spent) AbsorbLocal(b);
            else Request(b, ItemAction.Planted);
        }
        // Guest: a native action just spawned an item here; offer it to the host promptly.
        internal void LocalSpawned() { if (!host) nextDiscovery = Mathf.Min(nextDiscovery, Time.unscaledTime + 0.05f); }

#if MONMP_DEVELOPMENT
        internal void TestDiscoverSoon() => nextDiscovery = 0;
#endif

        // Host: a guest planted from the seed pack it holds (SharedGarden). Guest-held tool values never
        // travel back, so the pack's count is taken here; an emptied pack ends as natively.
        internal bool HostConsumeHeldSeeds(uint peer, SeedsType seeds, ulong seedPack, uint generation)
        {
            if (!host || TransfersBlocked || peer == 0 || !bindings.TryGetValue(seedPack, out var b) ||
                b.Item is not Tool_Seeds pack || pack == null || pack.Get_SeedsType != seeds) return false;
            return HostMutateLeasedTool(peer, seedPack, generation, typeof(Tool_Seeds),
                tool => { if (tool.Value1 < 1) return false; tool.Value1 -= 1; return true; }, destroyWhenEmpty: true);
        }
        internal bool GuestSeedPlanted(SeedsType seeds, out ulong seedPack, out uint generation)
        {
            seedPack = 0; generation = 0;
            if (gameplay.Player.Get_ActiveTool is not Tool_Seeds pack || pack == null || pack.Get_SeedsType != seeds ||
                !TryGetLocalLease(pack, out seedPack, out generation)) return false;
            var b = byItem[pack];
            (b.SeedPrediction ??= new SeedCountPrediction()).Spend(b.State.Values.Value1, generation, Time.unscaledTime);
            return true;
        }

        private void ResolveSeedPrediction(Binding b, float now)
        {
            if (b.SeedPrediction?.Expired(now) != true) return;
            b.SeedPrediction = null;
            // Restore the newest authoritative count even if no new item delta was
            // needed for a rejected plant. The native last-seed use may destroy its copy.
            if (b.Item != null) ApplyValues(b.Item, b.State.Values);
            else if (b.State.Exists && b.State.Values.Value1 > 0) Apply(b.Id, b.State, true);
        }
        // Host: a native dose from a guest's held tool (fertilizer 0.05 per fed plant, pesticide 0.02 per
        // spray). The guest's own copy already dropped; without this the host's echo refilled it (#297).
        internal bool HostDebitHeldTool<T>(uint peer, float amount, bool destroyWhenEmpty) where T : InteractableTools
        {
            if (!host || peer == 0 || !(HeldItemOf(peer) is T held) || !byItem.TryGetValue(held, out var b)) return false;
            return HostMutateLeasedTool(peer, b.Id, leases[b.Id].Generation, prefabs[b.Prefab].Type,
                tool => { if (tool.Value1 <= 0f) return false; tool.Value1 = Mathf.Clamp01(tool.Value1 - amount); return true; }, destroyWhenEmpty);
        }
        internal bool HostCanFillHeldBasket(uint peer, SeedsType seeds) =>
            host && peer != 0 && HeldItemOf(peer) is Tool_HarvestBasket basket && byItem.TryGetValue(basket, out var binding) &&
            leases[binding.Id].Owner == peer && (basket.Value1 <= 0 || basket.IntValue1 == (int)seeds || basket.IntValue1 == (int)SeedsType.Useless);
        // Host: a guest harvested a patch into the basket it holds. The guest's native basket filled
        // only its own copy, and the host's echo emptied it again; fill the host's copy the same way.
        internal bool HostFillHeldBasket(uint peer, SeedsType seeds, int amount)
        {
            if (amount <= 0 || seeds == SeedsType.Useless || !HostCanFillHeldBasket(peer, seeds) ||
                !(HeldItemOf(peer) is Tool_HarvestBasket basket) || !byItem.TryGetValue(basket, out var b)) return false;
            return HostMutateLeasedTool(peer, b.Id, leases[b.Id].Generation, prefabs[b.Prefab].Type, tool =>
            {
                tool.IntValue1 = (int)seeds;
                tool.Value1 = Mathf.Min(tool.Value1 + amount, tool.Value1_Max);
                return true;
            });
        }
        // Guest: the held seed pack natively absorbs another pack of the same seeds. The absorbed pack
        // is a replica, so the host ends it and adds its count to the host's copy of the held pack.
        internal void GuestMergeSeeds(Tool_Seeds held, Tool_Seeds target)
        {
            if (host || !Ready || !byItem.TryGetValue(held, out var source) || !OwnedLocally(source) ||
                !byItem.TryGetValue(target, out var absorbed)) return;
            channel!.Request(source.Id, new ItemCommand(ItemAction.Merged, source.State.Lease.Generation, Pose(target.transform)).Encode(), true);
            absorbed.Absorbed = true; identities.Unbind(absorbed.Id); byItem.Remove(target); absorbed.Item = null;
        }
        private void MergeSeeds(uint peer, Binding b, ItemCommand command)
        {
            if (peer == 0 || !(b.Item is Tool_Seeds held) || !leases.Owns(b.Id, peer, command.Generation)) return;
            var at = Position(command.Pose);
            if (!InRange(peer, at, 4.5f)) return;
            Binding? target = null; float best = 1f;
            foreach (var other in bindings.Values)
            {
                if (other == b || !(other.Item is Tool_Seeds pack) || pack.Get_SeedsType != held.Get_SeedsType || !other.State.Exists ||
                    leases[other.Id].Owner != ItemLease.Unowned) continue;
                float distance = (pack.transform.position - at).sqrMagnitude;
                if (distance < best) { best = distance; target = other; }
            }
            if (target?.Item == null) return;
            var item = target.Item;
            held.Value1 += item.Value1;
            identities.Unbind(target.Id); byItem.Remove(item); item.gameObject.SetActive(false); Object.Destroy(item.gameObject);
            target.Item = null; target.State = Capture(target); channel!.Publish(target.Id, target.State.Encode());
            b.LastOwnerUpdate = Time.unscaledTime; Publish(b);
        }
        // Guest: the held basket natively poured into the basket it looks at (#297). Both are shared
        // items; the host pours its own copies the same way and publishes them.
        internal void GuestPourBasket(Tool_HarvestBasket held, Tool_HarvestBasket target)
        {
            if (host || !Ready || !byItem.TryGetValue(held, out var source) || !OwnedLocally(source) || !byItem.ContainsKey(target)) return;
            channel!.Request(source.Id, new ItemCommand(ItemAction.BasketPoured, source.State.Lease.Generation, Pose(target.transform)).Encode(), true);
        }
        private void PourBasket(uint peer, Binding b, ItemCommand command)
        {
            if (peer == 0 || !(b.Item is Tool_HarvestBasket held) || !leases.Owns(b.Id, peer, command.Generation) || held.Value1 <= 0f) return;
            var at = Position(command.Pose);
            if (!InRange(peer, at, 4.5f)) return;
            Binding? target = null; float best = 1f;
            foreach (var other in bindings.Values)
            {
                if (other == b || !(other.Item is Tool_HarvestBasket) || !other.State.Exists) continue;
                uint owner = leases[other.Id].Owner;
                if (owner != ItemLease.Unowned && owner != peer) continue;
                float distance = (other.Item.transform.position - at).sqrMagnitude;
                if (distance < best) { best = distance; target = other; }
            }
            if (!(target?.Item is Tool_HarvestBasket into)) return;
            // Tool_HarvestBasket.MouseDown's transfer, on the host's copies.
            if (into.Value1 > 0f && into.IntValue1 != held.IntValue1) return;
            float room = into.Value1_Max - into.Value1;
            if (room <= 0f) return;
            into.IntValue1 = held.IntValue1;
            if (held.Value1 > room) { into.Value1 = into.Value1_Max; held.Value1 -= room; }
            else { into.Value1 += held.Value1; held.Value1 = 0f; }
            b.LastOwnerUpdate = Time.unscaledTime; Publish(b);
            target.State = Capture(target); channel!.Publish(target.Id, target.State.Encode());
        }
        private void Planted(uint peer, Binding b, ItemCommand command)
        {
            if (peer == 0 || !(b.Item is Garden_TreeCuttings cutting) || !leases.Owns(b.Id, peer, command.Generation) ||
                !InRange(peer, Position(command.Pose), 6) || cutting.IntValue1 <= 1) return;
            cutting.IntValue1--;
            b.State = Capture(b); Publish(b);
        }
        private void Ignite(uint peer, Binding b)
        {
            // Only loose paper (nobody holds it) that the requesting player's torch can reach.
            if (peer == 0 || b.Item == null || leases[b.Id].Owner != ItemLease.Unowned || !InRange(peer, b.Item.transform.position, 4.5f)) return;
            if (b.Item is Newspaper_Crumpled paper) paper.StartTheFire();
            else if (b.Item is Tool_Envelope envelope) envelope.StartTheFire();
        }
        private void Consume(uint peer, Binding b, ItemCommand command)
        {
            if ((prefabs[b.Prefab].Features & PrefabFeatures.Consume) == 0) return;
            var lease = leases[b.Id];
            bool wasHeld = lease.Owner == peer;
            if (lease.Owner == ItemLease.Unowned)
            {
                if (!InRange(peer, b.Item!.transform.position, 4) || !AcquireLease(b.Id, peer, command.Generation)) return;
                lease = leases[b.Id];
            }
            else if (!leases.Owns(b.Id, peer, command.Generation) || !InRange(peer, Position(command.Pose), 4.5f)) return;
            var receipt = new ConsumptionReceipt(session!.Peers[peer].ProfileId, command.Generation);
            if (peer == 0) CancelLocal(b.Item!);
            var previous = Capture(b);
            ReleaseLease(b.Id, peer, lease.Generation);
            b.Consumption = new ConsumptionCommit(receipt);
            b.State = new ItemState(b.Prefab, false, leases[b.Id], previous.Pose, previous.ScaleX, previous.ScaleY, previous.ScaleZ, receipt, previous.Values);
            var at = b.Item!.transform;
            if (ItemPrefabCatalog.Leftover(prefabs[b.Prefab].Type) is GameObject leftover)
            {
                var left = Object.Instantiate(leftover, at.position, at.rotation).GetComponent<InteractableItems>();
                if (wasHeld && left != null) handoffs.Add((left, peer, Time.unscaledTime + 3));
                // Share it on the next frame rather than the once-a-second scan, so the hand isn't left empty.
                nextDiscovery = Mathf.Min(nextDiscovery, Time.unscaledTime + 0.02f);
            }
            identities.Unbind(b.Id); byItem.Remove(b.Item!); b.Item!.gameObject.SetActive(false); Object.Destroy(b.Item.gameObject); b.Item = null;
            channel!.Publish(b.Id, b.State.Encode());
            if (peer == 0) DeliverConsumption(b);
        }
        private void AcknowledgeConsumption(uint peer, Binding b, ItemCommand command)
        {
            if (b.Consumption == null || !session!.Peers.TryGetValue(peer, out var actor) ||
                !b.Consumption.Complete(actor.ProfileId, command.Generation, command.Action == ItemAction.ConsumeApplied)) return;
            if (command.Action == ItemAction.ConsumeApplied)
            {
                var old = b.State;
                b.State = new ItemState(b.Prefab, false, old.Lease, old.Pose, old.ScaleX, old.ScaleY, old.ScaleZ,
                    new ConsumptionReceipt(actor.ProfileId, command.Generation, true), old.Values);
                channel!.Publish(b.Id, b.State.Encode());
                var prefab = prefabs[b.Prefab];
                var pose = WorldPose(b.State);
                if (pose.IsValid) ConsumptionPresentation.Announce(b.Id, prefab.Type, prefab.Asset, b.State,
                    Position(pose), peer, gameplay.Get_isHardMode);
                return;
            }

#if MONMP_DEVELOPMENT
            TestRejectedConsumptions++;
#endif

            // Native eligibility can change during the round trip (coffee limits/anxiety).
            // A rejected effect restores the canonical item once, with a fresh lease.
            var state = b.State; var restored = WorldPose(state);
            var go = Object.Instantiate(prefabs[b.Prefab].Asset, Position(restored), Rotation(restored));
            go.transform.localScale = new Vector3(state.ScaleX, state.ScaleY, state.ScaleZ);
            b.Item = go.GetComponent<InteractableItems>(); b.Item.isSpawnFromTheSaveLoadSystem = true;
            ApplyValues(b.Item, state.Values); b.Item.CreateBasics(); byItem.Add(b.Item, b);
                identities.Bind(b.Id, ItemState.Adapter, go, prefabs[state.Prefab].Path, false);
            b.Consumption = null; b.State = Capture(b); ConfigureBody(b, false); Publish(b);
        }
        // A bought item is leased to the buyer at the display and grabbed natively where it sits in
        // view, but on a guest that grab runs a round trip after the click: if the player turned, the
        // tire from the gas station was held off to the side (#208 for purchases). The view at the
        // purchase click puts it back where the native buy would have held it.
        private (Matrix4x4 WorldToView, Quaternion Rotation, float At)? purchaseView;
        internal void NotePurchaseView()
        {
            if (host) return;
            var view = gameplay.Player.transform;
            purchaseView = (view.worldToLocalMatrix, view.rotation, Time.unscaledTime);
        }
        private void NoteGrabPose(Binding b)
        {
            if (b.Item == null) return;
            var view = gameplay.Player.transform;
            b.GrabLocalPosition = view.InverseTransformPoint(b.Item.transform.position);
            b.GrabLocalRotation = Quaternion.Inverse(view.rotation) * b.Item.transform.rotation;
        }
        private void VanillaGrab(InteractableItems item)
        {
            applying = true; try { gameplay.Player.GrabNewItemBought(item.gameObject); } finally { applying = false; }
        }
        private void Interact(uint peer, Binding b, ItemCommand command)
        {
            if (b.Item == null || !InRange(peer, b.Item.transform.position, 4) ||
                (prefabs[b.Prefab].Features & PrefabFeatures.Interact) == 0) return;
            var lease = leases[b.Id];
            bool transient = lease.Owner == ItemLease.Unowned;
            if (b.Item is Furniture_Decoration decoration)
            {
                if (!transient || !DecorationState.CanFreeze(decoration) || b.BodyDestroyedFrame == Time.frameCount ||
                    (peer == 0 && gameplay.Player.IsHoldingAnObject)) return;
                if (decoration is Furniture_CarLift carLift && carLift.IntValue1 == 1 && (!CarLiftState.AtGround(carLift) || CarLiftState.IsMoving(carLift))) return;
                if (decoration.IntValue1 == 0 && (decoration.rb == null || decoration.rb.velocity.sqrMagnitude > 0.0001f ||
                    !gameplay.IsOnHomeLand(decoration.transform.position))) return;
            }
            if (b.Item is Garden_OutdoorPlanter planter)
            {
                if (!transient || b.BodyDestroyedFrame == Time.frameCount || (peer == 0 && gameplay.Player.IsHoldingAnObject)) return;
                if (planter.IntValue1 == 0 && (planter.rb == null || planter.rb.velocity.sqrMagnitude > 0.0001f || !gameplay.IsOnHomeLand(planter.transform.position))) return;
            }
            if (transient)
            {
                // Advance the generation for unheld interactions so competing or
                // stale requests cannot toggle the same revision twice.
                if (!AcquireLease(b.Id, peer, command.Generation)) return;
            }
            else if (!leases.Owns(b.Id, peer, command.Generation)) return;
            if (ItemPrefabCatalog.HostUse(b.Item.GetType()))
            {
                // The native use on the host's copy; its spawned items are shared by discovery.
                bool used;
                applying = true; try { used = b.Item.Use(); } finally { applying = false; }
                var lease2 = leases[b.Id];
                if (lease2.Owner != ItemLease.Unowned) ReleaseLease(b.Id, lease2.Owner, lease2.Generation);
                if (!used) { Publish(b); return; }
                // The native pack-use hook already registered its exact outputs. Queue this
                // retirement after them, including any older pending motion of this pack.
                if (peer == 0) CancelLocal(b.Item);
                identities.Unbind(b.Id); byItem.Remove(b.Item); b.Item.gameObject.SetActive(false); Object.Destroy(b.Item.gameObject); b.Item = null;
                Publish(b); return;
            }
            if (b.Item is Item_Lantern lantern) lantern.Button_ToggleLight();
            else if (b.Item is Furniture_Decoration deco)
            {
                if (deco.IntValue1 == 0) { deco.Freeze(); b.BodyDestroyedFrame = Time.frameCount; }
                else deco.UnFreeze();
            }
            else if (b.Item is Garden_OutdoorPlanter pot)
            {
                // The native planter freeze: the flag plus the body's removal (or its re-creation).
                if (pot.IntValue1 == 0) { pot.IntValue1 = 1; Object.Destroy(pot.rb); b.BodyDestroyedFrame = Time.frameCount; }
                else { pot.IntValue1 = 0; pot.CreateBasics(); }
            }
            if (transient) ReleaseLease(b.Id, peer, leases[b.Id].Generation);
            else b.LastOwnerUpdate = Time.unscaledTime;
            Publish(b); ConfigureBody(b, peer == 0);
        }
        // Guest car lift buttons and arm clicks run on the host; returns true when the native call should run.
        private readonly Dictionary<ulong, uint> liftOperators = new Dictionary<ulong, uint>();
        internal bool LiftInput(Furniture_CarLift lift, LiftCommand command)
        {
            if (host || applying || !Handles(lift)) return true;
            // Releasing an existing hydraulic hold must remain possible while saves block new work.
            if (Ready && (!TransfersBlocked || command.Op == LiftOp.Stop)) channel!.Request(byItem[lift].Id, command.Encode(), true);
            return false;
        }
        // Tool_MagneticTray funnels mouse placement and both physics collision paths through its
        // private AddBolt method. Guests always suppress that local native mutation and ask the host;
        // the host leaves vanilla placement enabled only when neither item belongs to a remote peer.
        internal bool MagneticTrayAdd(Tool_MagneticTray tray, Hardware_Bolt bolt)
        {
            if (!Handles(tray) || applying) return true;
            var trayBinding = byItem[tray];
            if (!Handles(bolt)) return host ? leases[trayBinding.Id].Owner == ItemLease.Unowned || leases[trayBinding.Id].Owner == 0 : false;
            var boltBinding = byItem[bolt];
            if (host)
                return (leases[trayBinding.Id].Owner == ItemLease.Unowned || leases[trayBinding.Id].Owner == 0) &&
                    (leases[boltBinding.Id].Owner == ItemLease.Unowned || leases[boltBinding.Id].Owner == 0);
            if (!Ready || TransfersBlocked || !OwnedLocally(trayBinding) || boltBinding.State.ContainerEntity != 0 ||
                bolt.isBoltInMagneticTray != null || trayBinding.State.Lease.Owner != session!.LocalPeerId ||
                (boltBinding.State.Lease.Owner != ItemLease.Unowned && boltBinding.State.Lease.Owner != session!.LocalPeerId))
                return false;
            var key = (trayBinding.Id, boltBinding.Id);
            if (pendingTrayRequests.TryGetValue(key, out var sent) && Time.unscaledTime - sent < 2) return false;
            pendingTrayRequests[key] = Time.unscaledTime;
            channel!.Request(trayBinding.Id, new MagneticTrayCommand(MagneticTrayAction.AttachBolt,
                trayBinding.State.Lease.Generation, boltBinding.Id, boltBinding.State.Lease.Generation).Encode(), true);
            return false;
        }
        // Right-click removal is another native multi-entity mutation. The host runs it normally;
        // guests route the request through the tray's lease and receive per-bolt detach states.
        internal bool MagneticTrayRemoveAll(Tool_MagneticTray tray)
        {
            if (!Handles(tray) || applying) return true;
            var b = byItem[tray];
            if (host) return leases[b.Id].Owner == ItemLease.Unowned || leases[b.Id].Owner == 0;
            if (!Ready || TransfersBlocked || !OwnedLocally(b) || b.State.Lease.Owner != session!.LocalPeerId) return false;
            channel!.Request(b.Id, new MagneticTrayCommand(MagneticTrayAction.RemoveAll, b.State.Lease.Generation).Encode(), true);
            return false;
        }
        private void TrayCommand(uint peer, Binding binding, MagneticTrayCommand command)
        {
            if (!host || TransfersBlocked || !(binding.Item is Tool_MagneticTray tray) ||
                !leases.Owns(binding.Id, peer, command.TrayGeneration) || !InRange(peer, tray.transform.position, 4)) return;
            if (command.Action == MagneticTrayAction.RemoveAll)
            {
                var bolts = new HashSet<Hardware_Bolt>();
                foreach (var slotBolt in MagneticTrayNative.Slots(tray)) if (slotBolt != null) bolts.Add(slotBolt);
                foreach (var candidate in bindings.Values)
                    if (candidate.Item is Hardware_Bolt linked && candidate.State.ContainerEntity == binding.Id) bolts.Add(linked);
                foreach (var containedBolt in bolts)
                {
                    if (containedBolt == null) continue;
                    MagneticTrayNative.Detach(containedBolt);
                    if (byItem.TryGetValue(containedBolt, out var boltBinding)) Publish(boltBinding);
                }
                return;
            }
            if (command.Action != MagneticTrayAction.AttachBolt ||
                !bindings.TryGetValue(command.BoltEntity, out var target) || !(target.Item is Hardware_Bolt bolt) ||
                !target.State.Exists || Placed(bolt) || !InRange(peer, bolt.transform.position, 4) || !MagneticTrayNative.HasRoom(tray) ||
                target.State.ContainerEntity != 0 || bolt.isBoltInMagneticTray != null) return;
            var boltLease = leases[target.Id];
            if ((boltLease.Owner != ItemLease.Unowned && boltLease.Owner != peer) || boltLease.Generation != command.BoltGeneration) return;
            bool transientLease = boltLease.Owner == ItemLease.Unowned;
            if (transientLease && !AcquireLease(target.Id, peer, command.BoltGeneration, 2)) return;
            bool releaseLeaseOnFailure = transientLease;
            try
            {
                applying = true;
                try { MagneticTrayNative.AddNative(tray, bolt); }
                finally { applying = false; }
                if (MagneticTrayNative.SlotOf(tray, bolt) < 0 || bolt.isBoltInMagneticTray != tray)
                {
                    MagneticTrayNative.Detach(bolt);
                    return;
                }
                var acquired = leases[target.Id];
                if (acquired.Owner != ItemLease.Unowned) ReleaseLease(target.Id, acquired.Owner, acquired.Generation);
                releaseLeaseOnFailure = false;
                Publish(target);
            }
            finally
            {
                var current = leases[target.Id];
                if (releaseLeaseOnFailure && current.Owner != ItemLease.Unowned && current.Owner == peer)
                    ReleaseLease(target.Id, peer, current.Generation);
            }
        }

#if MONMP_DEVELOPMENT
        internal bool TestToolPackDiagnostics;


        private void TraceToolPack(string detail) => Debug.Log("MONMP_TOOL_PACK_TRANSITION: " + detail);
#endif

        internal bool ToolPackGrantStillValid(InteractableTools? tool, InteractablePacks pack)
        {
            if (!GuestRunning || !Ready || TransfersBlocked || !CanHandleItems(gameplay.Get_PlayerState) ||
                tool == null || gameplay.Player.Get_ActiveTool != tool || pack.Value1 <= 0) return false;
            var holeField = tool is Tool_AdjustableSpanner ? SpannerBoltHole : tool is Tool_Air_Impact ? ImpactBoltHole : null;
            var gripField = tool is Tool_AdjustableSpanner ? SpannerGrip : tool is Tool_Air_Impact ? ImpactGrip : null;
            var targetPack = gameplay.Player.Get_CurrentInteractable?.GetComponent<InteractablePacks>();
            return holeField != null && gripField != null && holeField.GetValue(tool) == null && gripField.GetValue(tool) == null &&
                ReferenceEquals(targetPack, pack);
        }
        private void Lift(uint peer, Binding b, LiftCommand command)
        {
            if (!(b.Item is Furniture_CarLift lift) || lift.IntValue1 != 1 || leases[b.Id].Owner != ItemLease.Unowned) return;
            if (command.Op != LiftOp.Stop && !InRange(peer, lift.transform.position, 5)) return;
            if (peer != 0) Patches.RemoteActionScope.LastError = null;
            applying = true;
            try
            {
                using (peer != 0 ? Patches.RemoteActionScope.Enter() : null)
                {
                    if (command.Op == LiftOp.Up || command.Op == LiftOp.Down) { lift.LiftButtonPressed(command.Op == LiftOp.Up); liftOperators[b.Id] = peer; }
                    else if (command.Op == LiftOp.Stop) { lift.LiftButtonReleased(); liftOperators.Remove(b.Id); }
                    else lift.MoveArms(command.Direction, command.Arm);
                }
            }
            finally { applying = false; }
            if (peer != 0 && Patches.RemoteActionScope.LastError is string error)
            { channel!.Reply(peer, b.Id, NativeErrorReply.Encode(error)); Patches.RemoteActionScope.LastError = null; }
            Publish(b);
        }
        private static void ValidateValues(Prefab prefab, ItemValues values)
        {
            values.Validate();
            // Removal marks are valid on any prefab; validate the item's own fields without them.
            if ((values.Flags & ItemFlags.RemovalMarks) != 0)
                values = new ItemValues(values.Value1, values.Value2, values.IntValue1, values.IntValue2, values.Flags & ~ItemFlags.RemovalMarks);
            if (prefab.Type == typeof(Tool_WindowTint) && (values.IntValue1 < 0 || values.IntValue1 > 6))
                throw new InvalidDataException("Invalid tint roll count.");
            if (prefab.Type == typeof(Hardware_Bolt) && (values.IntValue1 < 0 || values.IntValue1 > BoltHoleState.MaximumColor))
                throw new InvalidDataException("Invalid bolt color.");
            if (General(prefab))
            {
                // Exactly the native save's per-item fields; any finite value the game itself can save.
                if (values.Flags != ItemFlags.None && !(CanisterCap.Strip(values.Flags) == ItemFlags.CapOff && prefab.Type == typeof(Tool_FuelCanister)))
                    throw new InvalidDataException("General items carry no adapter flags.");
                return;
            }
            if (ItemPrefabCatalog.Simple(prefab.Type))
            {
                // Simple items expose only read-only per-prefab constants, such as a tire's type.
                var asset = prefab.Asset.GetComponent<InteractableItems>();
                if (values.Flags != ItemFlags.None || values.Value1 != asset.Value1 || values.Value2 != asset.Value2 ||
                    values.IntValue1 != asset.IntValue1 || values.IntValue2 != asset.IntValue2)
                    throw new InvalidDataException("Simple item values differ from their prefab.");
                return;
            }
            if (prefab.Type == typeof(Garden_OutdoorPlanter))
            {
                var asset = prefab.Asset.GetComponent<InteractableItems>();
                if (values.Flags != ItemFlags.None || values.IntValue1 < 0 || values.IntValue1 > 1 || values.Value1 != asset.Value1 || values.Value2 != asset.Value2 || values.IntValue2 != asset.IntValue2)
                    throw new InvalidDataException("Invalid outdoor planter fields.");
                return;
            }
            if (prefab.Type == typeof(Furniture_CarLift))
            {
                if (values.Flags != ItemFlags.None || (values.Value2 != -1 && values.Value2 != 0 && values.Value2 != 1) || values.IntValue1 < 0 || values.IntValue1 > 1 ||
                    !ItemPose.FiniteValue(values.Value1, 10) || !LiftCommand.ValidArms(values.IntValue2))
                    throw new InvalidDataException("Invalid car lift fields.");
                return;
            }
            if (prefab.Type == typeof(Item_ElectricCoffeePercolator))
            {
                if (values.Flags != ItemFlags.None || values.Value1 != 0 || values.Value2 != 0 ||
                    values.IntValue1 < 0 || values.IntValue1 > 1 || values.IntValue2 < 0 || values.IntValue2 > 2)
                    throw new InvalidDataException("Invalid electric percolator contents.");
                return;
            }
            if (prefab.Type == typeof(Tool_Fence))
            {
                int styleCount = (FenceOptions.GetValue(prefab.Asset.GetComponent<Tool_Fence>()) as Array)?.Length ?? 0;
                int maxStyle = styleCount == 0 ? 0 : styleCount - 1;
                if (values.Flags != ItemFlags.None || values.Value1 != 0 || values.Value2 != 0 ||
                    values.IntValue1 < 0 || values.IntValue1 > 1 || values.IntValue2 < 0 || values.IntValue2 > maxStyle)
                    throw new InvalidDataException("Invalid fence state.");
                return;
            }
            if (prefab.Type == typeof(Item_Poutine))
            {
                if (values.Value1 < 0 || values.Value1 > 1 || values.Value2 != 0 || values.IntValue1 != 0 || values.IntValue2 != 0)
                    throw new InvalidDataException("Invalid poutine freshness fields.");
                return;
            }
            if (values.Flags != ItemFlags.None || values.Value1 != 0 || values.Value2 != 0 ||
                (prefab.Type == typeof(Item_Lantern) || prefab.Type == typeof(Furniture_Decoration) ?
                    (values.IntValue1 < 0 || values.IntValue1 > 1 || values.IntValue2 < 0 || values.IntValue2 > (prefab.Type == typeof(Item_Lantern) ? 1 : 0)) :
                    (values.IntValue1 != 0 || values.IntValue2 != 0)))
                throw new InvalidDataException("Item values do not match the vetted adapter.");
        }
        // The canister's cap is a child of the item, which SharedFuel's fixed caps leave out: nobody else saw
        // it come off. Its travel rides in the flag byte (CanisterCap levels), so others see the native screw
        // path; a local screw coroutine is never interrupted, and a guest's own turning is sent to the host (#179).
        private static readonly AccessTools.FieldRef<Tool_FuelCanister, Part_FuelTankCap> CanisterCapRef = AccessTools.FieldRefAccess<Tool_FuelCanister, Part_FuelTankCap>("CanisterFuelCap");
        private static readonly AccessTools.FieldRef<Part_FuelTankCap, float> CapScrewed = AccessTools.FieldRefAccess<Part_FuelTankCap, float>("Screwed");
        private static readonly AccessTools.FieldRef<Part_FuelTankCap, bool> CapRunning = AccessTools.FieldRefAccess<Part_FuelTankCap, bool>("CoroutineRunning");
        private static readonly AccessTools.FieldRef<Part_FuelTankCap, GameObject> CapHole = AccessTools.FieldRefAccess<Part_FuelTankCap, GameObject>("FuelTankHole");
        private static readonly System.Reflection.MethodInfo CapVisual = AccessTools.Method(typeof(Part_FuelTankCap), "UpdateVisual");
        private static ItemFlags CanisterCapFlags(Tool_FuelCanister canister) =>
            CanisterCapRef(canister) is Part_FuelTankCap cap && cap != null
                ? CanisterCap.Encode(capAuthority.TryGetValue(cap, out float level) ? level : CapScrewed(cap)) : ItemFlags.None;
        private static byte CapLevel(float screwed) => CanisterCap.CommandLevel(screwed);
        private static void ApplyCanisterCap(Tool_FuelCanister canister, float screwed, bool ease)
        {
            var cap = CanisterCapRef(canister);
            if (cap == null || CapRunning(cap)) { if (cap != null) { capTargets.Remove(cap); capAuthority.Remove(cap); } return; }
            // This guest just turned it: the host's echoes of its own earlier steps would pull it back.
            if (ease && localCapTurns.TryGetValue(cap, out float turned) && Time.unscaledTime - turned < 1.5f) return;
            float before = CapScrewed(cap);
            // Mid-travel steps arrive a few times a second; a guest eases the cap between them like the
            // native screw (#179). Coming off or going on is the native one-shot path. The host eases its
            // view of a guest's turn too, but publishes the level the guest turned to (capAuthority).
            if (ease && screwed > 0 && before > 0) { if (CapLevel(before) != CapLevel(screwed) || capTargets.ContainsKey(cap)) capTargets[cap] = screwed; return; }
            capTargets.Remove(cap); capAuthority.Remove(cap);
            if (CapLevel(before) == CapLevel(screwed)) return;
            CapScrewed(cap) = screwed;
            if (screwed > 0 && before > 0) cap.transform.Rotate(Vector3.up * (600f * (screwed - before)), Space.Self);
            // UpdateVisual re-parents (and sounds) a fully open cap on every call; once is the native path.
            var hole = CapHole(cap);
            if (screwed > 0 || hole == null || !hole.activeInHierarchy) CapVisual.Invoke(cap, null);
        }
        private static readonly Dictionary<Part_FuelTankCap, float> capTargets = new Dictionary<Part_FuelTankCap, float>();
        private static readonly Dictionary<Part_FuelTankCap, float> localCapTurns = new Dictionary<Part_FuelTankCap, float>();
        // Host: the level a guest turned a cap to while the host's own view still eases toward it.
        private static readonly Dictionary<Part_FuelTankCap, float> capAuthority = new Dictionary<Part_FuelTankCap, float>();
        private void EaseCaps()
        {
            if (capTargets.Count == 0) return;
            // A little faster than the native 0.5/s so a remote turn never falls behind.
            float step = 0.65f * Time.deltaTime;
            foreach (var pair in capTargets.ToArray())
            {
                var cap = pair.Key;
                if (cap == null || CapRunning(cap)) { capTargets.Remove(cap!); continue; }
                float before = CapScrewed(cap), next = Mathf.MoveTowards(before, pair.Value, step);
                if (next <= 0 || before <= 0) { capTargets.Remove(cap); continue; }
                CapScrewed(cap) = next;
                // The native coroutine turns the cap 300 deg/s while its screw level moves 0.5/s.
                cap.transform.Rotate(Vector3.up * (600f * (next - before)), Space.Self);
                applying = true;
                try { CapVisual.Invoke(cap, null); }
                finally { applying = false; }
                if (Mathf.Approximately(next, pair.Value)) capTargets.Remove(cap);
            }
            if (capAuthority.Count > 0)
                foreach (var cap in capAuthority.Keys.ToArray()) if (cap == null || !capTargets.ContainsKey(cap)) capAuthority.Remove(cap!);
        }
        private readonly Dictionary<ulong, byte> pendingCapLevels = new Dictionary<ulong, byte>();
        private readonly Dictionary<ulong, float> nextCapSend = new Dictionary<ulong, float>();
        // Guest: its native screw step on a shared canister's cap (Part_FuelTankCap.UpdateVisual).
        internal void CanisterCapTurned(Part_FuelTankCap cap)
        {
            if (host || applying || !Ready || cap == null) return;
            var canister = cap.GetComponentInParent<Tool_FuelCanister>();
            if (canister == null || CanisterCapRef(canister) != cap || !byItem.TryGetValue(canister, out var b)) return;
            pendingCapLevels[b.Id] = CapLevel(CapScrewed(cap));
            localCapTurns[cap] = Time.unscaledTime;
        }
        private void FlushCapLevels(float now)
        {
            if (pendingCapLevels.Count == 0) return;
            foreach (var pair in pendingCapLevels.ToArray())
            {
                bool end = pair.Value == 0 || pair.Value == CanisterCap.Levels || pair.Value == CanisterCap.SeatedZeroCommand;
                if (!end && nextCapSend.TryGetValue(pair.Key, out float at) && now < at) continue;
                pendingCapLevels.Remove(pair.Key);
                if (!bindings.ContainsKey(pair.Key)) continue;
                nextCapSend[pair.Key] = now + 0.12f;
                channel!.Request(pair.Key, new CanisterCapCommand(pair.Value).Encode(), true);
            }
        }
        // Host: a guest's cap travel, applied through the native visual and published to everyone.
        private void CanisterCapCommandFrom(uint peer, Binding b, CanisterCapCommand command)
        {
            if (!(b.Item is Tool_FuelCanister canister) || !b.State.Exists || !InRange(peer, canister.transform.position, 5)) return;
            var cap = CanisterCapRef(canister);
            if (cap == null || CapRunning(cap)) return;
            applying = true;
            try
            {
                ApplyCanisterCap(canister, CanisterCap.CommandTravel(command.Level), ease: true);
                if (capTargets.TryGetValue(cap, out float target)) capAuthority[cap] = target;
            }
            finally { applying = false; }
            Publish(b);
        }
        private readonly Dictionary<Furniture_CarLift, float> liftTargets = new Dictionary<Furniture_CarLift, float>();
        private void EaseLifts()
        {
            if (liftTargets.Count == 0) return;
            float k = 1f - Mathf.Exp(-Time.deltaTime * 14f);
            foreach (var pair in liftTargets.ToArray())
            {
                var lift = pair.Key;
                if (lift == null) { liftTargets.Remove(lift!); continue; }
                float next = Mathf.Lerp(lift.Value1, pair.Value, k);
                if (Mathf.Abs(next - pair.Value) < 0.002f) { next = pair.Value; liftTargets.Remove(lift); }
                bool wasApplying = applying; applying = true;
                try { lift.Value1 = next; } finally { applying = wasApplying; }
            }
        }
        private void ResolveDeferredCanisterValues(Binding b)
        {
            if (!b.DeferredCanisterValues || b.Item is not Tool_FuelCanister can ||
                MonMPPlugin.Instance?.World?.Fuel.Busy(can) == true) return;
            ApplyValues(can, b.State.Values);
            b.DeferredCanisterValues = false;
        }
        private void ApplyValues(InteractableItems item, ItemValues values, bool force = false)
        {
            bool wasApplying = applying; applying = true;
            try
            {
            // Setters may update materials and lights. Do not replay unchanged
            // effects every time the item's transform is replicated.
            bool sprayLevelChanged = item is Tool_SprayPaintCan && item.Value1 != values.Value1;
            bool wateringLevelChanged = item is Tool_WateringCan && item.Value1 != values.Value1;
            bool crateChanged = item is Pack_MapleSyrupCrate && (item.Value1 != values.Value1 || item.IntValue1 != values.IntValue1);
            bool tubingLevelChanged = item is Tool_TubingRoll && item.Value1 != values.Value1;
            bool fenceInstalledChanged = item is Tool_Fence && item.IntValue1 != values.IntValue1;
            bool bowlChanged = item is Item_FoodBowl && (item.Value1 != values.Value1 || item.Value2 != values.Value2 ||
                item.IntValue1 != values.IntValue1 || item.IntValue2 != values.IntValue2);
            bool boltColorChanged = item is Hardware_Bolt && item.IntValue1 != values.IntValue1;
            float value1 = values.Value1;
            if (!host && item is Tool_Seeds && byItem.TryGetValue(item, out var seedBinding) && seedBinding.SeedPrediction != null)
                value1 = seedBinding.SeedPrediction.Reconcile(value1, seedBinding.State.Lease.Generation, Time.unscaledTime,
                    force || !OwnedLocally(seedBinding) || !LocalHeld(item));
            bool seedCountChanged = item is Tool_Seeds && item.Value1 != value1;
            // The lift platform arrives in 10 Hz steps; ease a guest's copy toward each sample (#200).
            if (!host && !force && item is Furniture_CarLift lift && Mathf.Abs(lift.Value1 - values.Value1) < 1.5f)
            { if (lift.Value1 != values.Value1) liftTargets[lift] = values.Value1; }
            else if (item.Value1 != value1) { item.Value1 = value1; if (item is Furniture_CarLift snapped) liftTargets.Remove(snapped); }
            if (seedCountChanged && item is Tool_Seeds seedPack && seedPack.isEquiped) seedPack.Update_UI_Tool_Info();
            if (item is Furniture_CarLift audioLift)
            { if (!host) CarLiftAudio.Apply(audioLift, values.Value2); }
            else if (item.Value2 != values.Value2) item.Value2 = values.Value2;
            if (item.IntValue1 != values.IntValue1) item.IntValue1 = values.IntValue1;
            if (item is Tool_WindowTint tintRoll) TintRollVisual(tintRoll);
            if (item.IntValue2 != values.IntValue2) item.IntValue2 = values.IntValue2;
            // The native bolt setter stores its enum only. Its material refresh is separate.
            if ((boltColorChanged || force) && item is Hardware_Bolt bolt) bolt.SetBoltColor();
            ItemPresentationRefresh.Apply(item, values.IntValue1, wateringLevelChanged, force, crateChanged);
            if (item is Tool_FuelCanister canister) ApplyCanisterCap(canister, CanisterCap.Screwed(values.Flags), ease: !host);
            // The bowl's setters store the level only; its water and kibble meshes follow UpdateVisual.
            if (bowlChanged || (force && item is Item_FoodBowl))
                foreach (var bowl in item.GetComponentsInChildren<Leo.FoodBowl>(true)) bowl.UpdateVisual();
            if (item is Tool_SprayPaintCan sprayCan)
            {
                SharedPaintPanels.SetSprayCanDepleted(sprayCan);
                if (sprayLevelChanged && sprayCan.isEquiped) sprayCan.Update_UI_Tool_Info();
            }
            if (tubingLevelChanged && item is Tool_TubingRoll tubingRoll && tubingRoll.isEquiped)
                tubingRoll.Update_UI_Tool_Info();
            if (force && item is Item_Lantern lantern)
                foreach (var source in lantern.LightSource.lightSources)
                { if (values.IntValue1 == 1) source.TurnOn(); else source.TurnOff(); }
            if (!host && item is Item_ElectricCoffeePercolator electric && electric.itemCollider != null && ElectricPercolatorUpdateFreeze != null)
                ElectricPercolatorUpdateFreeze.Invoke(electric, new object[] { values.IntValue1 == 1 });
            if (item is Tool_Fence fence)
            {
                if (!host && fenceInstalledChanged)
                {
                    fence.CreateBasics();
                    if (fence.IntValue1 == 1 && fence.itemCollider != null) fence.itemCollider.isTrigger = false;
                }
                FenceUpdateVisual?.Invoke(fence, null);
            }
            if (item is Item_Poutine food)
                FoodState.Apply(food, (Item_Poutine)Supported(item)!.Asset.GetComponent<InteractableItems>(), values, gameplay.Get_isHardMode);
            }
            finally { applying = wasApplying; }
        }
        private void Unpack(uint peer, Binding b, ItemCommand command)
        {

#if MONMP_DEVELOPMENT
            if (TestToolPackDiagnostics) TraceToolPack("host-unpack id=" + b.Id.ToString("X") + "; count=" + b.Item?.Value1 +
                "; peer=" + peer + "; commandGeneration=" + command.Generation + "; lease=" + leases[b.Id].Owner + "/" + leases[b.Id].Generation +
                "; poseValid=" + command.Pose.IsValid + "; canAct=" + leases.CanAct(b.Id, peer, command.Generation) +
                "; inRange=" + (command.Pose.IsValid && InRange(peer, Position(command.Pose), 4.5f)));
#endif

            if (!(b.Item is InteractablePacks pack) || pack.Value1 < 1 || !command.Pose.IsValid ||
                !leases.CanAct(b.Id, peer, command.Generation) || !InRange(peer, Position(command.Pose), 4.5f)) return;
            var owner = leases[b.Id].Owner;
            // The pack's held target can be stale by the time extraction reaches the host. Use the
            // command's fresh pose before decrementing/publishing so the released pack stays put.
            pack.transform.SetPositionAndRotation(Position(command.Pose), Rotation(command.Pose));
            b.HasTarget = false; b.Settled = true; b.LastOwnerUpdate = Time.unscaledTime;
            // As natively: a just-unpacked item doesn't fall straight back into its pack.
            applying = true; try { pack.Value1 -= 1; pack.LastTimeItemUnpack = Time.realtimeSinceStartup; UpdatePack.Invoke(pack, null); } finally { applying = false; }
            if (pack.DestroyWhenEmpty && pack.Value1 <= 0)
            {
                // Mirrors the native empty-pack destruction; the next capture publishes its removal.
                if (owner != ItemLease.Unowned) ReleaseLease(b.Id, owner, leases[b.Id].Generation);
                if (peer == 0) CancelLocal(pack);
                pack.gameObject.SetActive(false); Object.Destroy(pack.gameObject); return;
            }
            Publish(b);
        }
        // The local copy was installed into a bolt hole or part socket: stop tracking the object
        // (it now belongs to that structure) and end the loose shared item.
        private void AbsorbLocal(Binding b)
        {
            var item = b.Item!;
            if (host)
            {
                var lease = leases[b.Id];
                if (lease.Owner != ItemLease.Unowned) ReleaseLease(b.Id, lease.Owner, lease.Generation);
                identities.Unbind(b.Id); byItem.Remove(item); b.Item = null;
                b.State = Capture(b); channel!.Publish(b.Id, b.State.Encode());
                return;
            }
            if (OwnedLocally(b)) Request(b, ItemAction.Absorb);
            b.Absorbed = true; identities.Unbind(b.Id); byItem.Remove(item); b.Item = null;
        }
        private void CancelLocal(InteractableItems item)
        {
            if (gameplay == null || gameplay.Player == null || !LocalHeld(item)) return;
            var player = gameplay.Player;
            applying = true;
            try
            {
                if (player.Get_ActiveTool == item) UnEquipTool.Invoke(player, null);
                if (player.Get_HoldingItem == item) player.UnGrabHoldingItem();
                else if (LocalHeld(item))
                {
                    // Gripped by an equipped tool: clear the tool's reference so it does not reuse it.
                    var tool = player.Get_ActiveTool;
                    var grip = tool is Tool_AdjustableSpanner || tool is Tool_Air_Impact
                        ? AccessTools.Field(tool.GetType(), "CurrentHolding_Bolt") : null;
                    if (grip != null && ReferenceEquals(grip.GetValue(tool), item)) grip.SetValue(tool, null);
                    item.UnGrab(); item.transform.SetParent(null);
                    if (item.itemCollider != null) item.itemCollider.enabled = true;
                }
            }
            finally { applying = false; }
        }
        // Reconfigures only when the hold, owner, parent or body changed, or native code flipped the
        // body back; rewriting every item's Rigidbody each frame stalls physics on large saves.
        private void KeepBodyConfigured(Binding b, bool local)
        {
            var item = b.Item!;
            RepairLoosePartPickup(b);
            if (item is Furniture_CarLift heldLift && (b.State.Lease.Owner != ItemLease.Unowned || host && LocalHeld(item)))
                CarLiftState.Sync(heldLift, true, ref b.LiftWasHeld);
            if (item is Hardware_Bolt looseBolt) LooseBoltBody.Repair(looseBolt, LocalHeld(item), BoltToolEquipped);
            bool held = b.State.Lease.Owner != ItemLease.Unowned;
            var body = item.rb;
            if (b.BodyConfigured && b.ConfiguredHeld == held && b.ConfiguredLocal == local && b.ConfiguredParent == item.transform.parent &&
                b.ConfiguredBody == body && (body == null || body.isKinematic == b.ConfiguredKinematic) &&
                (item.itemCollider == null || item.itemCollider.isTrigger == b.ConfiguredTrigger)) return;
            ConfigureBody(b, local);
            b.BodyConfigured = true; b.ConfiguredHeld = held; b.ConfiguredLocal = local; b.ConfiguredParent = item.transform.parent;
            b.ConfiguredBody = item.rb; b.ConfiguredKinematic = item.rb != null && item.rb.isKinematic;
            b.ConfiguredTrigger = item.itemCollider != null && item.itemCollider.isTrigger;
        }
        private void ConfigureBody(Binding b, bool local)
        {
            if (b.Item == null) return;
            RepairLoosePartPickup(b);
            if (KeepCargoFrozen(b)) return;
            if (b.Item is Hardware_Bolt looseBolt) LooseBoltBody.Repair(looseBolt, LocalHeld(b.Item), BoltToolEquipped);
            // Something in the host's own hand or tool (a bolt in the spanner's mouth) is held even
            // between leases: made dynamic under the tool, it fell every frame and the tool snapped it
            // back into the mouth, so a gripped bolt kept sagging and jumping for everyone (#125).
            bool localGrip = host && LocalHeld(b.Item);
            if (localGrip) local = true;
            bool held = b.State.Lease.Owner != ItemLease.Unowned || localGrip;
            if (b.Item is Furniture_Decoration || b.Item is Garden_OutdoorPlanter)
            {
                var frozenItem = b.Item;
                var decoration = frozenItem as Furniture_Decoration;
                if (decoration != null) DecorationState.Presentation(decoration, held);
                var carLift = decoration as Furniture_CarLift;
                if (!frozenItem.HasARigidBody)
                {
                    if (carLift != null) CarLiftState.Sync(carLift, held, ref b.LiftWasHeld);
                    if (frozenItem.rb != null)
                    {
                        if (frozenItem.rb.collisionDetectionMode != CollisionDetectionMode.ContinuousSpeculative)
                            frozenItem.rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                        if (!frozenItem.rb.isKinematic) frozenItem.rb.isKinematic = true;
                        Object.Destroy(frozenItem.rb); b.BodyDestroyedFrame = Time.frameCount;
                    }
                    if (frozenItem.itemCollider.isTrigger) frozenItem.itemCollider.isTrigger = false;
                    return;
                }
                // Destroy is deferred until end of frame. Do not pick up that
                // dying component while applying a newer unfreeze in the same tick.
                if (b.BodyDestroyedFrame == Time.frameCount) return;
                if (frozenItem.rb == null) { frozenItem.CreateBasics(); if (decoration != null) DecorationState.Presentation(decoration, held); }
                // CreateBasics calls native ChangeFreezeMode and rewrites the arms.
                // Consume the release transition only after that body is available.
                if (carLift != null) CarLiftState.Sync(carLift, held, ref b.LiftWasHeld);
            }
            if (b.Item.rb == null) return;
            bool machineMounted = b.Item.GetComponentInParent<Furniture_TireChanger_Manual>(true) != null ||
                b.Item.GetComponentInParent<NitrousRefillStation>(true) != null;
            bool kinematic = !host || held || machineMounted;
            ItemBodyConfiguration.Apply(b.Item.rb, b.Item.itemCollider, kinematic, held || machineMounted);
            if (!local && held && b.Item.transform.parent != null) b.Item.transform.SetParent(null);
        }
        private ItemState Capture(Binding b, ItemPose? verifiedWorldPose = null)
        {
            if (b.Item == null) return ItemStatePublication.Reuse(b.State, b.Prefab, false, leases[b.Id], WorldPose(b.State), b.State.ScaleX, b.State.ScaleY, b.State.ScaleZ, b.State.Consumption,
                b.Burned || b.Sold ? new ItemValues(b.State.Values.Value1, b.State.Values.Value2, b.State.Values.IntValue1, b.State.Values.IntValue2,
                    b.State.Values.Flags | (b.Burned ? ItemFlags.Burned : ItemFlags.None) | (b.Sold ? ItemFlags.Sold : ItemFlags.None)) : b.State.Values);
            var nativeTransform = b.Item.transform;
            // The native pickup keeps the item's local scale under the player's view transform, so a
            // held item's world scale includes that rig's scale; its own size is the local one (#105).
            bool locallyHeld = LocalHeld(b.Item);
            var scale = nativeTransform.parent != null && locallyHeld ? nativeTransform.localScale : nativeTransform.lossyScale;
            // These native tools shrink their root solely for the equipped first-person view;
            // each restores Vector3.one on UnEquiped. Observers render a detached world item,
            // so transmitting that temporary view scale makes its third-person model tiny.
            if (locallyHeld && b.Item is InteractableTools equipped && equipped.isEquiped &&
                (equipped is Tool_Chainsaw || equipped is Tool_DirtShovel || equipped is Tool_MapTorch ||
                 equipped is Tool_HarvestBasket || equipped is Tool_TubingRoll || equipped is Tool_AdjustableSpanner))
                scale = Vector3.one;
            var values = new ItemValues(b.Item.Value1, b.Item is Furniture_CarLift movingLift ? CarLiftState.Direction(movingLift) : b.Item.Value2, b.Item.IntValue1, b.Item.IntValue2,
                (b.Item is Item_Poutine food && food.Get_IsRefrigerated ? ItemFlags.Refrigerated : ItemFlags.None) |
                (b.Item is Tool_FuelCanister canister ? CanisterCapFlags(canister) : ItemFlags.None));
            ValidateValues(prefabs[b.Prefab], values);
            ulong container = 0; byte slot = byte.MaxValue;
            if (b.Item is Hardware_Bolt bolt)
            {
                var tray = bolt.isBoltInMagneticTray ?? bolt.transform.parent?.GetComponent<Tool_MagneticTray>();
                if (tray != null && byItem.TryGetValue(tray, out var parent))
                {
                    int nativeSlot = MagneticTrayNative.SlotOf(tray, bolt);
                    if (nativeSlot >= 0) { container = parent.Id; slot = (byte)nativeSlot; }
                }
            }
            var lease = leases[b.Id];
            var pose = CaptureOwnerPose(b, lease, verifiedWorldPose);
            ulong carrier = 0;
            if (host && container == 0 && CaptureFrozenCargo(b, lease, out var frozenCarrier, out var frozenPose))
            { carrier = frozenCarrier; pose = frozenPose; }
            else if (host && container == 0 && CaptureSettlingCargo(b, out var settlingCarrier, out var settlingPose))
            { carrier = settlingCarrier; pose = settlingPose; }
            else if (host && lease.Owner == ItemLease.Unowned && container == 0 && nativeTransform.parent == null &&
                b.Item.rb != null && !b.Item.rb.isKinematic && Carriers != null &&
                Carriers.TryFind(nativeTransform.position, 0, out var carrierId, out var frame, nativeTransform))
            {
                ItemCarriers.CapturePose(nativeTransform, b.Item.rb, frame, out var lp, out var lq);
                if (ItemPose.TryLocal(lp.x, lp.y, lp.z, lq.x, lq.y, lq.z, lq.w, out var local)) { pose = local; carrier = carrierId; }
            }
            // Native vehicle detectors freeze trunk cargo as children, then recreate its
            // rigidbody without clearing that parent when thawed. Keep both in vehicle space;
            // a live body must still rest on that carrier before its stale parent is trusted.
            else if (host && lease.Owner == ItemLease.Unowned && container == 0 && nativeTransform.parent != null &&
                b.Item.HasARigidBody && Carriers != null &&
                nativeTransform.parent.GetComponentInParent<Vehicle_Detector_Interactables>(true) != null &&
                (b.Item.rb == null || !b.Item.rb.isKinematic) &&
                Carriers.TryFindAttached(nativeTransform, b.Item.rb != null, out var attachedId, out var attachedFrame))
            {
                ItemCarriers.CapturePose(nativeTransform, b.Item.rb, attachedFrame, out var lp, out var lq);
                if (ItemPose.TryLocal(lp.x, lp.y, lp.z, lq.x, lq.y, lq.z, lq.w, out var local)) { pose = local; carrier = attachedId; }
            }
            // A load frozen into a wheelbarrow (a body-less child) moves with it. Shared in world space, it
            // trailed the barrow a guest pushed ahead of the host's copy and looked stuck (#162).
            else if (host && lease.Owner == ItemLease.Unowned && container == 0 && nativeTransform.parent != null &&
                b.Item.rb == null && b.Item.HasARigidBody && Carriers != null &&
                nativeTransform.parent.GetComponentInParent<Wheelbarrow>() is { } barrow && Carriers.IdOf(barrow.transform, out var barrowId))
            {
                var lp = barrow.transform.InverseTransformPoint(nativeTransform.position);
                var lq = Quaternion.Inverse(barrow.transform.rotation) * nativeTransform.rotation;
                if (ItemPose.TryLocal(lp.x, lp.y, lp.z, lq.x, lq.y, lq.z, lq.w, out var local)) { pose = local; carrier = barrowId; }
            }
            return ItemStatePublication.Reuse(b.State, b.Prefab, true, lease, pose, scale.x, scale.y, scale.z,
                values: values, containerEntity: container, containerSlot: slot, carrier: carrier);
        }
        private void Publish(Binding b)
        {
            ItemPose? verifiedWorldPose = null;
            if (b.Item != null)
            {
                if (!RecoverFlung(b, out var nativePose)) return;
                verifiedWorldPose = nativePose;
            }
            b.State = Capture(b, verifiedWorldPose);
            channel!.Records.TryGetValue(b.Id, out var record);
            if (ItemStatePublication.CanSkip(b.State, b.LastPublishedState, b.LastPublishedRecord, record)) return;
            if (!b.State.Exists && b.RetireAfterPackOutputs)
            { channel.PublishAfterQueued(b.Id, b.State.Encode()); b.RetireAfterPackOutputs = false; }
            else channel.Publish(b.Id, b.State.Encode());
            b.LastPublishedState = b.State;
            b.LastPublishedRecord = channel.Records[b.Id];
        }
        // Host: an item physics flung out of the world or to a non-finite pose can't be published.
        // Put it back where it last was valid and still, instead of failing every later item (#126).
        private bool RecoverFlung(Binding b, out ItemPose worldPose)
        {
            var t = b.Item!.transform; Vector3 p = t.position; Quaternion q = t.rotation;
            if (ItemPose.TryLocal(p.x, p.y, p.z, q.x, q.y, q.z, q.w, out worldPose)) return true;
            if (brokenPoseLogged.Add(b.Id))
                Debug.LogWarning("MonMP item " + b.Id + " (" + b.Item.name + ") left the world at " + p + "; restored its last shared pose.");
            var last = WorldPose(b.State);
            if (!last.IsValid) return false;
            if (b.Item.rb != null && !b.Item.rb.isKinematic) { b.Item.rb.velocity = Vector3.zero; b.Item.rb.angularVelocity = Vector3.zero; }
            t.SetPositionAndRotation(Position(last), Rotation(last));
            // Preserve Unity's post-write quaternion normalization on the recovery path.
            worldPose = Pose(t);
            return true;
        }
        // One faulty item must not stop the rest of the host's pass.
        // A lift's height or a canister's cap changes without the item moving (#200, #179).
        private static bool ValuesUnchanged(Binding b)
        {
            var item = b.Item!; var v = b.State.Values;
            return item.Value1 == v.Value1 && (item is Furniture_CarLift lift ? CarLiftState.Direction(lift) : item.Value2) == v.Value2 && item.IntValue1 == v.IntValue1 && item.IntValue2 == v.IntValue2 &&
                !(item is Tool_FuelCanister canister && CanisterCapFlags(canister) != (v.Flags & ((ItemFlags)0xF0 | ItemFlags.CapOff)));
        }
        private int capturePhase, captureCursor;
        private float captureCredit, lastCaptureSlice;
        private void PublishIsolated(Binding b)
        {
            try { Publish(b); }
            catch (Exception ex) when (!UpdateProfiler.Strict)
            {
                if (brokenPoseLogged.Add(b.Id)) Debug.LogWarning("MonMP item " + b.Id + " could not be published: " + ex.Message);
            }
        }
        private void ChainsawDockCommand(uint peer, Binding binding, ChainsawHolderCommand command)
        {
            if (!host || !Ready || TransfersBlocked || peer == 0 || binding.Item is not Tool_Chainsaw chainsaw ||
                !binding.State.Exists || chainsaw.IntValue1 != 0 || !chainsawHolders.TryGetValue(command.HolderId, out var holder) ||
                !ChainsawHoldTransform(holder, out var hold) || ChainsawHolderLoaded.GetValue(holder) is not true ||
                !chainsaw.CanPlaceThisInHolder) return;
            var lease = leases[binding.Id];
            if (lease.Generation != command.Generation || (lease.Owner != ItemLease.Unowned && lease.Owner != peer) ||
                !InRange(peer, hold.position, 4f) || (Position(command.Pose) - hold.position).sqrMagnitude > 1f) return;

            bool temporaryLease = lease.Owner == ItemLease.Unowned;
            if (temporaryLease && !AcquireLease(binding.Id, peer, command.Generation, 2)) return;
            bool attached = false;
            try
            {
                chainsaw.transform.SetParent(null, true);
                chainsaw.transform.SetPositionAndRotation(Position(command.Pose), Rotation(command.Pose));
                ChainsawHolderItem?.SetValue(holder, chainsaw);
                bool previous = applyingChainsawAttachment; applyingChainsawAttachment = true;
                try { (ChainsawHolderAttach ?? throw new InvalidOperationException("Native chainsaw-holder attach method is unavailable."))
                    .Invoke(holder, new object[] { false }); }
                finally { applyingChainsawAttachment = previous; }
                attached = chainsaw.IntValue1 == 1 && ChainsawHoldTransform(holder, out var attachedHold) && chainsaw.transform.parent == attachedHold;
            }
            catch (Exception ex)
            {
                Debug.LogError("MonMP host chainsaw docking failed: " + ex);
            }
            if (!attached)
            {
                // A failed native attach must leave the item usable and its lease available.
                chainsaw.transform.SetParent(null, true);
                if (chainsaw.IntValue1 == 1) chainsaw.IntValue1 = 0;
                if (chainsaw.rb == null) chainsaw.CreateBasics();
                if (chainsaw.itemCollider != null) chainsaw.itemCollider.isTrigger = false;
            }
            var currentLease = leases[binding.Id];
            if (currentLease.Owner != ItemLease.Unowned) ReleaseLease(binding.Id, currentLease.Owner, currentLease.Generation);
            binding.LastOwnerUpdate = Time.unscaledTime;
            binding.State = Capture(binding);
            if (binding.Item != null) ConfigureBody(binding, false);
            Publish(binding);
        }
        private void DiscoverIfDue(float now)
        {
            // Explicit forced discovery keeps its native scan and admission rules.
            // Ordinary one-second passes are entirely managed on both roles.
            bool forced = nextDiscovery <= 0;
            if ((forced || now >= nextSafetyDiscovery) &&
                WorldScans.TryClaim<InteractableItems>(periodic: !forced))
            {
                Discover(periodic: !forced);
                nextDiscovery = now + 1f;
            }
            DiscoverSlice(now);
        }
        public void Update()
        {
            if (disposed || session?.IsRunning != true) return;
            if (!host) PresentPickup();
            ResolveNativeOutputs();
            if (Time.frameCount >= discoverAtFrame) { discoverAtFrame = int.MaxValue; nextDiscovery = 0; }
            DiscoverIfDue(Time.unscaledTime);
            if (!Ready) return;
            if (!host) { EaseLifts(); GhostDrivenVehicle(); }
            float now = Time.unscaledTime;
            if (pendingPackedBoltOutputs.Count > 0 && now >= nextPackedBoltSweep)
            {
                nextPackedBoltSweep = now + 0.5f;
                foreach (var candidate in pendingPackedBoltOutputs.Where(pair => pair.Value <= now || pair.Key == null).ToArray())
                    pendingPackedBoltOutputs.Remove(candidate.Key);
            }
            if (!host)
            {
                using var pendingWork = ItemWorkSnapshot();
                foreach (var binding in pendingWork.Span)
                {
                    if (binding.SuspensionEdits?.HasPending == true) SendLooseSuspensionEdit(binding, now);
                    ResolveSeedPrediction(binding, now);
                }
            }
            if (now >= nextChainsawSoundCheck) { nextChainsawSoundCheck = now + 0.25f; SilenceIdleChainsaws(); }
            EaseCaps();
            if (!host)
            {
                if (now >= nextCarrierRetry) { nextCarrierRetry = now + 0.25f; ResolvePendingCarriers(); }
                if (pendingTrayRequests.Count > 0) foreach (var key in pendingTrayRequests.Where(p => now - p.Value >= 2).Select(p => p.Key).ToArray()) pendingTrayRequests.Remove(key);
                FlushCapLevels(now);
                if (replayUse is { } use)
                {
                    replayUse = null;
                    if (use.PendingUse && use.Item != null && OwnedLocally(use) && LocalHeld(use.Item))
                    { use.PendingUse = false; UseKeyDown.Invoke(gameplay.Player, null); }
                }
            }
            for (int i = handoffs.Count - 1; i >= 0; i--)
            {
                // A leftover goes to its consumer's hand once discovery has shared it.
                var (left, peer, deadline) = handoffs[i];
                if (left == null || now >= deadline || !session.Peers.ContainsKey(peer)) { handoffs.RemoveAt(i); continue; }
                if (byItem.ContainsKey(left) && DeliverPurchase(left, peer, out _, out _)) handoffs.RemoveAt(i);
            }
            if (host)
            {
#if MONMP_PERFORMANCE_CAPTURE
                using var detail = Runtime.CaptureDetail.Measure("Items.CaptureLoop");
#endif
                // Every binding is still visited once per 0.1 s and refreshed on every tenth
                // visit, but as an even share per frame. One pass over the whole list every
                // 0.1 s was a 1-2 ms pulse on ten frames a second.
                using var captureWork = itemWork.ReadSnapshot();
                var captureSpan = captureWork.Span;
                int captureCount = ItemCaptureSlices.Take(ref captureCredit, captureSpan.Length, now - lastCaptureSlice, 0.1f);
                lastCaptureSlice = now;
                for (int visit = 0; visit < captureCount; visit++)
                {
                    if (captureCursor >= captureSpan.Length) { captureCursor = 0; capturePhase++; }
                    int captureIndex = captureCursor++;
                    var b = captureSpan[captureIndex];
                    if (!b.State.Exists) continue;
                    bool refresh = (uint)(captureIndex + capturePhase) % 10 == 0;
                    // A host-side Fishing_Trash starts its native six-second timer when the
                    // angler detaches it. Observe that moment once so the world tombstone uses
                    // the same deadline even when first discovery ran before the detach.
                    if (b.FishingTrashOutput && b.FishingTrashExpiresAt <= 0 && b.Item != null &&
                        RemoteFishing.TryLocalFishingTrashDropTime(b.Item.gameObject, out float droppedAt))
                    { b.FishingTrashExpiresAt = droppedAt + 6f; refresh = true; }
                    if (b.FishingTrashOutput && b.FishingTrashExpiresAt > 0 && now >= b.FishingTrashExpiresAt && b.Item != null)
                    {
                        identities.Unbind(b.Id); byItem.Remove(b.Item); b.Item.gameObject.SetActive(false); Object.Destroy(b.Item.gameObject);
                        b.Item = null; b.FishingTrashExpiresAt = 0; refresh = true;
                    }
                    if (b.PendingGrab != GrabMode.None && now - b.PendingSince > 2) { b.PendingGrab = GrabMode.None; b.PendingTool = null; b.PendingUse = false; }
                    if (b.Item != null && Placed(b.Item) && MonMPPlugin.Instance?.World?.Bolts.IsMountPending(b.Item) != true) { AbsorbLocal(b); continue; }
                    // Native fire destroys the item component; publish its removal and drop the identity.
                    if (b.Item == null) { identities.Unbind(b.Id); ClearRetiredNativeReference(b); }
                    var lease = leases[b.Id];
                    // The host's own hold ended natively without a drop call (a wrench released its
                    // bolt, a pack was used out of the hand): free the item so physics resumes.
                    bool hostLetGo = lease.Owner == 0 && b.Item != null && !LocalHeld(b.Item) && b.PendingGrab == GrabMode.None;
                    if (lease.Owner != ItemLease.Unowned && (b.Item == null || hostLetGo || (lease.Owner != 0 && now - b.LastOwnerUpdate > 5)))
                    { ReleaseLease(b.Id, lease.Owner, lease.Generation); if (b.FishingTrashOutput && lease.Owner != 0) b.FishingTrashExpiresAt = now + 6f; refresh = true; if (b.Item != null) { b.State = Capture(b); ConfigureBody(b, false); RefreshPlanterLocation(b.Item); } }
                    if (b.Item is Furniture_Decoration || b.Item is Garden_OutdoorPlanter) ConfigureBody(b, b.State.Lease.Owner == 0);
                    // A late save has thousands of resting items: an unowned item that has not moved is
                    // re-captured once a second (fuel, food and other values) instead of ten times.
                    if (b.Item != null && !refresh && lease.Owner == ItemLease.Unowned && b.State.Lease.Owner == ItemLease.Unowned &&
                        b.PendingGrab == GrabMode.None && !b.Item.transform.hasChanged && ValuesUnchanged(b)) continue;
                    PublishIsolated(b);
                    if (b.Item != null) b.Item.transform.hasChanged = false;
                }
            }
            else if (!host && now >= nextSend)
            {
                nextSend = now + 0.1f;
                using var sendingWork = ItemWorkSnapshot();
                foreach (var b in sendingWork.Span)
                {
                    TickPickup(b, now);
                    if (b.Pickup == null && b.PendingGrab != GrabMode.None && now - b.PendingSince > 2) { b.PendingGrab = GrabMode.None; b.PendingTool = null; b.PendingUse = false; }
                    if (b.Pickup == null && b.PendingUse && now - b.PendingSince > 2) b.PendingUse = false;
                    if (b.Item == null || b.Absorbed) continue;
                    if (Placed(b.Item))
                    {
                        if (b.Item is InteractableParts && MonMPPlugin.Instance?.World?.VehicleParts.IsPartChangePending(b.Item) == true) continue;
                        if (b.Item is Hardware_Bolt && MonMPPlugin.Instance?.World?.Bolts.IsMountPending(b.Item) == true)
                        { b.State = Capture(b); continue; }
                        AbsorbLocal(b); continue;
                    }
                    if (b.PendingUse && OwnedLocally(b))
                    {
                        b.PendingUse = false;
                        if (LocalHeld(b.Item))
                        {
                            UseKeyDown.Invoke(gameplay.Player, null);
                            continue;
                        }
                        b.Releasing = true; Request(b, ItemAction.Drop); continue;
                    }
                    if (!OwnedLocally(b) || b.Releasing) continue;
                    if (LocalHeld(b.Item)) Request(b, ItemAction.Move);
                    else if (b.PendingGrab == GrabMode.None) { b.Releasing = true; Request(b, ItemAction.Drop); }
                }
                RetryAdoptions(now);
            }
            if (host)
            {
                activePresentation.SnapshotRemote(remotePresentationScratch);
                foreach (var b in remotePresentationScratch)
                {
                    var lease = leases[b.Id];
                    // Installed natively a moment ago (a guest's wheel on its hub) and not yet absorbed by the
                    // 0.1 s capture: the guest's last hand pose dragged it off the socket (#163).
                    if (b.Item == null || lease.Owner == ItemLease.Unowned || lease.Owner == 0)
                    { RefreshActiveBinding(b); continue; }
                    if (Placed(b.Item)) continue;
                    KeepBodyConfigured(b, false);
                    if (!b.HasTarget) continue;
                    float fraction = Mathf.Clamp01((Time.unscaledTime - b.TargetReceivedAt) / b.TargetInterval);
                    b.Item.transform.SetPositionAndRotation(Vector3.Lerp(b.FromPosition, b.TargetPosition, fraction),
                        Quaternion.Slerp(b.FromRotation, b.TargetRotation, fraction));
                }
                remotePresentationScratch.Clear();
            }
            else
            {
                float blend = 1 - Mathf.Exp(-LocalItemDropHandoff.FollowRate * Time.unscaledDeltaTime);
                // Held items are checked every frame; resting ones round-robin over eight frames.
                // A late-game save has thousands of replicas and each check is several native calls.
                int phase = Time.frameCount & 7, slot = 0;
                using var presentationWork = ItemWorkSnapshot();
                foreach (var b in presentationWork.Span)
                {
                    if (b.DropPreview != null && (b.Item == null || Placed(b.Item) || presenting.Contains(b.Id)))
                    { b.DropPreview = null; b.DropPreviewAcknowledged = null; }
                    if (b.Item == null) { ClearRetiredNativeReference(b); continue; }
                    if (Placed(b.Item) || presenting.Contains(b.Id)) continue;
                    if (b.Item is Furniture_CarLift audioLift) CarLiftAudio.Apply(audioLift, b.State.Values.Value2);
                    ResolveDeferredCanisterValues(b);
                    bool local = b.State.Lease.Owner == session.LocalPeerId;
                    if (b.State.Lease.Owner != ItemLease.Unowned || !b.BodyConfigured || (slot++ & 7) == phase) KeepBodyConfigured(b, local);
                    if (PresentLocalDrop(b, now)) continue;
                    if (b.CarrierFrame != null)
                    {
                        if (LocalHeld(b.Item) || b.PendingGrab != GrabMode.None) continue;
                        b.CarrierPosition = Vector3.Lerp(b.CarrierPosition, b.CarrierTargetPosition, blend);
                        b.CarrierRotation = Quaternion.Slerp(b.CarrierRotation, b.CarrierTargetRotation, blend);
                        b.Item.transform.SetPositionAndRotation(b.CarrierFrame.TransformPoint(b.CarrierPosition), b.CarrierFrame.rotation * b.CarrierRotation);
                        continue;
                    }
                    if (!b.HasTarget || b.Settled || (local && LocalHeld(b.Item))) continue;
                    if (b.Item is Item_Fish && MonMPPlugin.Instance?.World?.FishBuckets?.ContainsFish(b.Id) == true)
                        continue;
                    var t = b.Item.transform;
                    if ((t.position - b.TargetPosition).sqrMagnitude < 1e-8f && Quaternion.Dot(t.rotation, b.TargetRotation) > 0.999999f)
                    { if (!local) b.Settled = true; continue; }
                    if (b.State.Lease.Owner != ItemLease.Unowned && !local)
                    {
                        float fraction = Mathf.Clamp01((Time.unscaledTime - b.TargetReceivedAt) / b.TargetInterval);
                        t.SetPositionAndRotation(Vector3.Lerp(b.FromPosition, b.TargetPosition, fraction),
                            Quaternion.Slerp(b.FromRotation, b.TargetRotation, fraction));
                    }
                    else t.SetPositionAndRotation(Vector3.Lerp(t.position, b.TargetPosition, blend),
                        Quaternion.Slerp(t.rotation, b.TargetRotation, blend));
                }
            }
        }
        private static ItemPose CaptureOwnerPose(Binding b, ItemLease lease, ItemPose? verifiedWorldPose = null) =>
            lease.Owner != 0 && lease.Owner != ItemLease.Unowned && b.HasTarget
                ? Pose(b.TargetPosition, b.TargetRotation) : verifiedWorldPose ?? Pose(b.Item!.transform);
        private static ItemPose Pose(Transform t)
        { var p = t.position; var q = t.rotation; ItemPose.TryLocal(p.x, p.y, p.z, q.x, q.y, q.z, q.w, out var pose); return pose; }
        private static ItemPose Pose(Vector3 position, Quaternion rotation) =>
            new ItemPose(position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w);
        private static void SetTarget(Binding b, ItemPose pose)
        {
            if (b.Item == null) return;
            b.FromPosition = b.Item.transform.position; b.FromRotation = b.Item.transform.rotation;
            b.TargetPosition = Position(pose); b.TargetRotation = Rotation(pose);
            if (b.HasTarget)
            {
                float gap = Time.unscaledTime - b.TargetReceivedAt;
                if (gap < 0.5f) b.TargetInterval = Mathf.Clamp(Mathf.Lerp(b.TargetInterval, gap * 1.15f, 0.3f), 0.05f, 0.25f);
            }
            b.TargetReceivedAt = Time.unscaledTime; b.HasTarget = true; b.Settled = false; b.RejectedMoveSince = -1;
        }
        // A carried item's shared pose is in its carrier's space; everything that needs a world pose
        // (spawning, range checks, restores) goes through here.
        private ItemPose WorldPose(ItemState state)
        {
            if (state.Carrier == 0) return state.Pose;
            var frame = Carriers?.Resolve(state.Carrier);
            if (frame == null) return state.Pose;
            var p = frame.TransformPoint(Position(state.Pose)); var q = frame.rotation * Rotation(state.Pose);
            return ItemPose.TryLocal(p.x, p.y, p.z, q.x, q.y, q.z, q.w, out var pose) ? pose : state.Pose;
        }
        // Guest: a carrier moved by its own native Update (a wheelbarrow this player pushes) may run after
        // the follow loop, leaving its load a frame behind and shaking (#162). Re-pin carried replicas late.
        private readonly HashSet<Binding> carried = new HashSet<Binding>();
        internal void LateCarry()
        {
            if (host) { TickCargoFreezing(); return; }
            if (host || carried.Count == 0) return;
            List<Binding>? gone = null;
            foreach (var b in carried)
            {
                if (b.Item == null || b.CarrierFrame == null) { (gone ??= new List<Binding>()).Add(b); continue; }
                if (LocalHeld(b.Item) || b.PendingGrab != GrabMode.None || Placed(b.Item)) continue;
                b.Item.transform.SetPositionAndRotation(b.CarrierFrame.TransformPoint(b.CarrierPosition), b.CarrierFrame.rotation * b.CarrierRotation);
            }
            if (gone != null) foreach (var b in gone)
            {
                Uncarry(b);
                if (b.Item != null && b.State.Exists && b.State.Carrier != 0) pendingCarriers.Add(b.Id);
            }
        }
        // Guest: the vehicle this player drives never collides with item replicas. A replica is kinematic
        // and follows the host, so to the guest's car it was an immovable wall: a Konig full of items
        // could not be driven, and any item in the way stopped a car dead. The host's copy of the car
        // still pushes the host's items, and the guest sees where they go.
        private NWH.Vehicle? ghostVehicle;
        private readonly List<Collider> ghostVehicleColliders = new List<Collider>();
        private readonly HashSet<Collider> ghostActiveVehicleColliders = new HashSet<Collider>();
        private readonly HashSet<Collider> ghostTouchedVehicleColliders = new HashSet<Collider>();
        private readonly HashSet<Collider> ghostedItems = new HashSet<Collider>();
        private Collider[] ghostOverlap = new Collider[256];
        private float nextGhost;
        internal void BeforePhysics()
        {
            if (host) MoveFrozenCargoPhysics();
            else if (!disposed && Ready) GhostDrivenVehicle();
        }

#if MONMP_DEVELOPMENT
        internal bool TestGhostCarrierRelease(ulong itemId, ulong vehicleCarrierId)
        {
            if (host || disposed || !Ready || ghostVehicle == null || Carriers == null ||
                !bindings.TryGetValue(itemId, out var binding) || binding.Item == null ||
                LocalHeld(binding.Item) || binding.CarrierFrame != null) return false;
            var frame = Carriers.Resolve(vehicleCarrierId);
            if (frame == null || !(frame.IsChildOf(ghostVehicle.transform) || ghostVehicle.transform.IsChildOf(frame))) return false;
            var state = binding.State;
            var localPosition = frame.InverseTransformPoint(binding.Item.transform.position);
            var localRotation = Quaternion.Inverse(frame.rotation) * binding.Item.transform.rotation;
            if (!ItemPose.TryLocal(localPosition.x, localPosition.y, localPosition.z,
                localRotation.x, localRotation.y, localRotation.z, localRotation.w, out var localPose)) return false;
            // Exercise both native collision owners against an existing guest replica and
            // real registered carrier; leave authority/state and the item's pose unchanged.
            Carry(binding, new ItemState(state.Prefab, state.Exists, state.Lease, localPose,
                state.ScaleX, state.ScaleY, state.ScaleZ, state.Consumption, state.Values,
                state.ContainerEntity, state.ContainerSlot, vehicleCarrierId));
            bool carriedHere = binding.CarrierFrame == frame;
            Uncarry(binding);
            return carriedHere;
        }
#endif

        private void GhostDrivenVehicle()
        {
            var vehicle = gameplay.Get_PlayerState == PlayerState.InVehicle_Driver ? gameplay.Get_CurrentVehicleController : null;
            if (vehicle != ghostVehicle)
            {
                UnghostVehicle();
                ghostVehicle = vehicle;
                nextGhost = 0f;
            }
            if (vehicle == null) return;
            // Refresh activated/replaced body colliders before each physics step. Keep a
            // separate union for restoring pairs even after a collider leaves this hierarchy.
            vehicle.GetComponentsInChildren<Collider>(false, ghostVehicleColliders);
            ghostVehicleColliders.RemoveAll(c => c == null || !c.enabled || c.isTrigger);
            foreach (var current in ghostVehicleColliders)
            {
                ghostTouchedVehicleColliders.Add(current);
                if (ghostActiveVehicleColliders.Contains(current)) continue;
                foreach (var itemCollider in ghostedItems)
                    if (itemCollider != null) Physics.IgnoreCollision(itemCollider, current, true);
            }
            ghostActiveVehicleColliders.Clear();
            foreach (var current in ghostVehicleColliders) ghostActiveVehicleColliders.Add(current);
            if (Time.unscaledTime < nextGhost) return;
            nextGhost = Time.unscaledTime + 0.2f;
            if (ghostVehicleColliders.Count == 0) return;
            var bounds = ghostVehicleColliders[0].bounds;
            foreach (var c in ghostVehicleColliders) if (c != null) bounds.Encapsulate(c.bounds);
            // Include the swept approach until the next scan. A fixed three-metre margin
            // misses kinematic item replicas above 54km/h, producing a solid invisible hit.
            var velocity = vehicle.vehicleRigidbody != null ? vehicle.vehicleRigidbody.velocity : Vector3.zero;
            var approach = velocity * 0.4f;
            bounds.Encapsulate(new Bounds(bounds.center + approach, bounds.size));
            int count;
            do
            {
                count = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents + Vector3.one * 3f,
                    ghostOverlap, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
                if (count < ghostOverlap.Length || ghostOverlap.Length >= 4096) break;
                Array.Resize(ref ghostOverlap, ghostOverlap.Length * 2);
            } while (true);
            for (int i = 0; i < count; i++)
            {
                var collider = ghostOverlap[i];
                if (collider == null || ghostedItems.Contains(collider) || collider.transform.IsChildOf(vehicle.transform)) continue;
                var item = collider.GetComponentInParent<InteractableItems>();
                if (item == null || item is Furniture_Decoration || item is Garden_OutdoorPlanter || !byItem.ContainsKey(item) || LocalHeld(item)) continue;
                ghostedItems.Add(collider);
                foreach (var own in ghostVehicleColliders) if (own != null) Physics.IgnoreCollision(collider, own, true);
            }
        }
        private void UnghostVehicle()
        {
            if (ghostVehicle != null)
                foreach (var collider in ghostedItems)
                {
                    if (collider == null) continue;
                    // A load carried in this vehicle keeps ignoring it (#120).
                    var item = collider.GetComponentInParent<InteractableItems>();
                    var carrierColliders = item != null && byItem.TryGetValue(item, out var b) &&
                        b.IgnoredItemColliders != null && b.IgnoredItemColliders.Contains(collider)
                        ? b.IgnoredCarrierColliders : null;
                    foreach (var own in ghostTouchedVehicleColliders)
                        if (own != null && (carrierColliders == null || !carrierColliders.Contains(own)))
                            Physics.IgnoreCollision(collider, own, false);
                }
            ghostedItems.Clear();
            ghostVehicle = null;
            ghostVehicleColliders.Clear(); ghostActiveVehicleColliders.Clear(); ghostTouchedVehicleColliders.Clear();
        }
        private void SnapItemToHost(Binding b, ItemState state, bool snap, bool preserveDropPose, Vector3 position, Quaternion rotation)
        {
            if (!snap || preserveDropPose) return;
            // The same carrier's render pose can lag its host physics pose by
            // metres at speed. Carry owns its local correction; a world snap
            // here is undone by LateCarry and repeatedly warps the replica.
            if (state.Carrier != 0 && b.CarrierFrame != null && Carriers?.Resolve(state.Carrier) == b.CarrierFrame) return;
            b.Item?.transform.SetPositionAndRotation(position, rotation);
        }

        private void Carry(Binding b, ItemState state)
        {
            var frame = !host && state.Carrier != 0 ? Carriers?.Resolve(state.Carrier) : null;
            if (frame == null || b.Item == null)
            {
                Uncarry(b);
                if (!host && state.Exists && state.Carrier != 0 && b.Item != null) pendingCarriers.Add(b.Id);
                return;
            }
            pendingCarriers.Remove(b.Id);
            var t = b.Item.transform;
            if (b.CarrierFrame != frame)
            {
                Uncarry(b);
                b.CarrierFrame = frame;
                carried.Add(b);
                // The replica's previous world pose can lag a locally driven carrier by
                // metres. It is not a valid interpolation origin in the new frame: easing
                // from that point sends bed/trunk cargo through the cabin and seats.
                // Establish the accepted local pose once; later samples in this same
                // carrier still ease actual cargo movement in its own coordinate space.
                b.CarrierPosition = Position(state.Pose); b.CarrierRotation = Rotation(state.Pose);
                t.SetPositionAndRotation(frame.TransformPoint(b.CarrierPosition), frame.rotation * b.CarrierRotation);
                // The replica is kinematic and follows the host: it must never shove the vehicle this
                // player drives (a gas can in the bed stopped the truck, #120).
                b.IgnoredItemColliders = b.Item.GetComponentsInChildren<Collider>(true);
                b.IgnoredCarrier = state.Carrier;
                // Keep the exact pairs we suppressed. A registry ID can later resolve to
                // another native frame, or disappear before this binding releases them.
                b.IgnoredCarrierColliders = Carriers?.Colliders(state.Carrier) ?? Array.Empty<Collider>();
                IgnoreCarrier(b, true);
            }
            b.CarrierTargetPosition = Position(state.Pose); b.CarrierTargetRotation = Rotation(state.Pose);
        }
        private readonly HashSet<ulong> pendingCarriers = new HashSet<ulong>();
        private float nextCarrierRetry;
        private void ResolvePendingCarriers()
        {
            // Carrier IDs are stable while their native representations may be replaced.
            // Reconcile bound cargo too; an unchanged relative item needs no new packet.
            foreach (var b in carried.ToArray())
            {
                if (b.Item == null || !b.State.Exists || b.State.Carrier == 0) { Uncarry(b); continue; }
                if (LocalHeld(b.Item) || Placed(b.Item) || b.PendingGrab != GrabMode.None) continue;
                var frame = Carriers?.Resolve(b.State.Carrier);
                if (frame != null && b.CarrierFrame == frame)
                {
                    // Native parts can change while the carrier ID and Transform stay
                    // the same. Consume the registry's periodically refreshed measurement.
                    ReconcileCarrierColliders(b, Carriers!.Colliders(b.State.Carrier));
                    continue;
                }
                Uncarry(b); pendingCarriers.Add(b.Id);
            }
            foreach (ulong id in pendingCarriers.ToArray())
            {
                if (!bindings.TryGetValue(id, out var b) || b.Item == null || !b.State.Exists || b.State.Carrier == 0)
                { pendingCarriers.Remove(id); continue; }
                if (LocalHeld(b.Item) || Placed(b.Item) || b.PendingGrab != GrabMode.None) continue;
                Carry(b, b.State);
                if (b.CarrierFrame == null) continue;
                // The fallback pose was in world space without its dependency. Recover
                // directly to the latest authoritative carrier-relative pose once bound.
                b.CarrierPosition = b.CarrierTargetPosition; b.CarrierRotation = b.CarrierTargetRotation;
                b.HasTarget = false; b.Settled = true;
                b.Item.transform.SetPositionAndRotation(b.CarrierFrame.TransformPoint(b.CarrierPosition), b.CarrierFrame.rotation * b.CarrierRotation);
            }
        }
        private void Uncarry(Binding b)
        {
            pendingCarriers.Remove(b.Id);
            if (b.CarrierFrame == null && b.IgnoredCarrier == 0) return;
            IgnoreCarrier(b, false);
            // Carrier and driven-vehicle collision suppression can own the same pair.
            // Releasing the carrier must preserve suppression while this guest drives.
            if (ghostVehicle != null && b.IgnoredItemColliders != null)
                foreach (var itemCollider in b.IgnoredItemColliders)
                    if (itemCollider != null && ghostedItems.Contains(itemCollider))
                        foreach (var own in ghostTouchedVehicleColliders)
                            if (own != null) Physics.IgnoreCollision(itemCollider, own, true);
            carried.Remove(b);
            b.CarrierFrame = null; b.IgnoredCarrier = 0; b.IgnoredItemColliders = null; b.IgnoredCarrierColliders = null;
        }
        private void ReconcileCarrierColliders(Binding b, Collider[] current)
        {
            var previous = b.IgnoredCarrierColliders ?? Array.Empty<Collider>();
            var previousItems = b.IgnoredItemColliders ?? Array.Empty<Collider>();
            if (b.Item == null) return;
            // Item parts can be added or replaced independently of the carrier. Keep
            // ownership of the exact pair product, including driver-owned old pairs.
            var currentItems = b.Item.GetComponentsInChildren<Collider>(true);
            // Resting cargo normally retains both memberships. Avoid revisiting every pair
            // and repeatedly searching the same arrays until either side actually changes.
            if (previous.SequenceEqual(current) && previousItems.SequenceEqual(currentItems)) return;
            foreach (var own in previous)
            {
                if (own == null) continue;
                foreach (var item in previousItems)
                    if (item != null && (!current.Contains(own) || !currentItems.Contains(item)))
                        Physics.IgnoreCollision(item, own, ghostVehicle != null &&
                            ghostedItems.Contains(item) && ghostTouchedVehicleColliders.Contains(own));
            }
            foreach (var own in current)
            {
                if (own == null) continue;
                foreach (var item in currentItems)
                    if (item != null && (!previous.Contains(own) || !previousItems.Contains(item)))
                        Physics.IgnoreCollision(item, own, true);
            }
            b.IgnoredItemColliders = currentItems;
            b.IgnoredCarrierColliders = current;
        }
        private void IgnoreCarrier(Binding b, bool ignore)
        {
            if (b.IgnoredCarrierColliders == null || b.IgnoredItemColliders == null) return;
            foreach (var carrierCollider in b.IgnoredCarrierColliders)
                foreach (var itemCollider in b.IgnoredItemColliders)
                    if (carrierCollider != null && itemCollider != null) Physics.IgnoreCollision(itemCollider, carrierCollider, ignore);
        }
        private static Vector3 Position(ItemPose p) => new Vector3(p.X, p.Y, p.Z);
        private static Quaternion Rotation(ItemPose p) => new Quaternion(p.Qx, p.Qy, p.Qz, p.Qw);
        public void Dispose()
        {
            if (disposed) return;
            // Release the static root even if a later native teardown action fails.
            NativeItemDiscovery.End(this);
            ClearPickupPreview();
            ReleaseFrozenCargo();
            StopLiftOperators();
            CarLiftAudio.Clear();
            pendingCarriers.Clear();
            UnghostVehicle();
            foreach (var binding in carried.ToArray()) Uncarry(binding);
            capTargets.Clear(); localCapTurns.Clear(); capAuthority.Clear();
            MonMP.Plugin.Patches.SharedWoodLogTriggerGuard.Forget(this);
            if (gameplay != null && gameplay.Player != null)
                foreach (var item in byItem.Keys.Where(i => i != null && LocalHeld(i)).ToArray())
                    if (!host || gameplay.Player.Get_HoldingItem == item) CancelLocal(item);
            disposed = true;
            discoveryItems.Clear();
            DisposeNativeOutputs();
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; channel.CommandRejected -= Rejected; channel.ReplyReceived -= Reply; }
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; session.SettingsChanged -= CargoSettingsChanged; }
            foreach (var b in bindings.Values)
            {
                CancelLooseSuspensionEdits(b, restore: false);
                identities.Unbind(b.Id);
                if (b.Item == null) continue;
                if (!host) { b.Item.gameObject.SetActive(false); Object.Destroy(b.Item.gameObject); }
                else if (b.State.Lease.Owner != ItemLease.Unowned)
                { var l = leases[b.Id]; ReleaseLease(b.Id, l.Owner, l.Generation); b.State = Capture(b); ConfigureBody(b, false); }
            }
            foreach (var pair in hidden) if (pair.Key != null) pair.Key.gameObject.SetActive(pair.Value);
            foreach (var contacts in chainsawHolderContacts.Values)
                if (contacts != null) { contacts.Detach(); Object.Destroy(contacts); }
            chainsawHolderContacts.Clear();
            pendingChainsawDocks.Clear(); chainsawHolders.Clear(); chainsawHolderIds.Clear();
            activePresentation.Clear(); remotePresentationScratch.Clear(); itemWork.Clear();
            ClearTypedItemWork(); trayLinkScratch.Clear();
            if (!host)
                foreach (var item in adoptionCandidates)
                    if (item != null) { CancelLocal(item); item.gameObject.SetActive(false); Object.Destroy(item.gameObject); }
            adopting.Clear(); adoptNonces.Clear(); fishingTrashNonces.Clear();
            adoptHeld.Clear(); pendingPackedBoltOutputs.Clear(); adoptionCandidates.Clear(); adoptionRequests.Clear();
            adoptionReceipts.Clear(); packedAdoptions.Clear(); packedAdoptionReceipts.Clear();
            channel = null; session = null;
        }
    }
}
