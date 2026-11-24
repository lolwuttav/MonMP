using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using NWH.VehiclePhysics2;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    internal enum PaintPanelAction : byte { Paint = 1, RepairRust = 2, RepairDent = 3, DamageDent = 4, ForceRust = 5 }

    // Vehicle_PaintablePanel is the native save unit for spray paint, rust and dents.
    // Fixed world panels use canonical scene paths. GameObjectData saves item-owned
    // panels with the item or its installed socket; those use separate dynamic records.
    internal sealed class SharedPaintPanels : IDisposable
    {
        internal const ushort Adapter = 40;
        private const float CaptureInterval = 0.1f;
        private const float IdleCaptureInterval = 0.25f;
        private const float DynamicCaptureInterval = 0.5f;
        private const float MaxAcceptedPaintStep = 0.08f;
        private const float MaxRange = 3.2f;

        private enum ReplyStatus : byte { Applied = 0, Rejected = 1, Duplicate = 2, Capacity = 3, NoChange = 4 }
        private enum SprayTargetResult : byte { Found, NoHit, NotPaintable, Unregistered, Inactive, InvalidPose }

        private sealed class Binding
        {
            public ulong Id;
            public string Path = "";
            public Vehicle_PaintablePanel Root = null!;
            public PaintPanelState Original = null!;
            public PaintPanelState Current = null!;
            public PaintPanelState? DirtyState;
            public Guid InFlightOperation;
            public bool Dirty;
            public bool Dynamic;
            public bool PurchasedVehicle;
            public string RelativePanelPath = "";
            public ulong PurchasedVehicleEntity;
            public PaintOwnerKind OwnerKind;
            public ulong OwnerId;
            public ushort Prefab;
        }

        private sealed class ActiveSpray
        {
            public Guid Session;
            public Guid Profile;
            public ulong ToolId;
            public uint Generation;
            public ulong PanelId;
            public Tool_SprayPaintCan Tool = null!;
            public Vector3 Color;
            public float Smoothness;
            public float Metalness;
            public bool OverrideColor;
            public float LastCommand;
            public Vector3 CameraOrigin;
            public float CameraYaw, CameraPitch;
            public bool IndependentAim;
            public float StartedAt;
        }

        private sealed class ClientSpray
        {
            public Guid Session;
            public ulong ToolId;
            public uint Generation;
            public uint Sequence;
            public Tool_SprayPaintCan Tool = null!;
            public float NextUpdate;
        }

        private sealed class DentProgressLease
        {
            public uint Generation;
            public DentRepairClickProgress Progress = new DentRepairClickProgress();
        }

        private sealed class PurchasedVehiclePaintOwner
        {
            public ulong Entity;
            public Vehicle_Type Type;
            public GameObject Instance = null!;
            public int WaitThroughFrame;
        }

        private readonly bool host;

#if MONMP_DEVELOPMENT
        private readonly string invalidDiagnosticsPath;
#endif


#if MONMP_DEVELOPMENT
        private readonly string dynamicDiagnosticsPath;
#endif


#if MONMP_DEVELOPMENT
        private readonly string targetDiagnosticsPath;
#endif


#if MONMP_DEVELOPMENT
        private readonly string purchasedDiagnosticsPath;
#endif

        private readonly HashSet<string> diagnosedInvalidPanels = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<ulong> diagnosedDynamicOwners = new HashSet<ulong>();
        private readonly HashSet<ulong> diagnosedPurchasedPanels = new HashSet<ulong>();
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Vehicle_PaintablePanel, Binding> byPanel = new Dictionary<Vehicle_PaintablePanel, Binding>();
        private readonly Dictionary<ulong, DynamicPaintPanelState> dynamicRecords = new Dictionary<ulong, DynamicPaintPanelState>();
        private readonly Dictionary<ulong, PurchasedVehiclePaintState> purchasedRecords = new Dictionary<ulong, PurchasedVehiclePaintState>();
        private readonly Dictionary<ulong, PurchasedVehiclePaintOwner> purchasedVehicles = new Dictionary<ulong, PurchasedVehiclePaintOwner>();
        private readonly Dictionary<ulong, string> purchasedIdentities = new Dictionary<ulong, string>();
        private readonly Dictionary<Vehicle_PaintablePanel, PaintPanelState> dynamicRollback =
            new Dictionary<Vehicle_PaintablePanel, PaintPanelState>();
        private readonly SharedItems items;
        private readonly SharedVehicleParts parts;
        private readonly Dictionary<Guid, ulong> pending = new Dictionary<Guid, ulong>();
        private readonly Dictionary<Guid, byte> pendingRepairCues = new Dictionary<Guid, byte>();
        private readonly HashSet<(Guid Profile, Guid Operation)> claims = new HashSet<(Guid, Guid)>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, (float Tokens, float Time)> budgets = new Dictionary<uint, (float, float)>();
        private readonly Dictionary<uint, (float Tokens, float Time)> sprayBudgets = new Dictionary<uint, (float, float)>();
        private readonly Dictionary<uint, ActiveSpray> activeSprays = new Dictionary<uint, ActiveSpray>();
        private readonly Dictionary<uint, float> nextSprayTargetWarning = new Dictionary<uint, float>();
        private readonly Dictionary<(uint Peer, ulong Tool), DentProgressLease> dentProgress =
            new Dictionary<(uint, ulong), DentProgressLease>();
        private readonly RustSolutionCooldowns rustSolutionCooldowns = new RustSolutionCooldowns();
        private readonly SprayPaintSequences spraySequences = new SprayPaintSequences();
        private ClientSpray? clientSpray;
        private const float SprayCommandInterval = 0.1f;
        private const float SprayCommandTimeout = 0.75f;
        private const float SprayMaxStep = 0.1f;
        private static readonly System.Reflection.FieldInfo SprayCameraField =
            HarmonyLib.AccessTools.Field(typeof(Tool_SprayPaintCan), "mainCamera") ??
            throw new InvalidOperationException("Unsupported spray-can camera field.");
        private static readonly System.Reflection.FieldInfo SprayColorField =
            HarmonyLib.AccessTools.Field(typeof(Tool_SprayPaintCan), "color") ??
            throw new InvalidOperationException("Unsupported spray-can color field.");
        private static readonly System.Reflection.FieldInfo SpraySmoothnessField =
            HarmonyLib.AccessTools.Field(typeof(Tool_SprayPaintCan), "Smoothness") ??
            throw new InvalidOperationException("Unsupported spray-can smoothness field.");
        private static readonly System.Reflection.FieldInfo SprayMetalnessField =
            HarmonyLib.AccessTools.Field(typeof(Tool_SprayPaintCan), "Metalness") ??
            throw new InvalidOperationException("Unsupported spray-can metalness field.");
        private static readonly System.Reflection.FieldInfo SprayOverrideField =
            HarmonyLib.AccessTools.Field(typeof(Tool_SprayPaintCan), "Override_PaintColorAndSmooth") ??
            throw new InvalidOperationException("Unsupported spray-can override field.");
        private static readonly System.Reflection.FieldInfo SprayParticleField =
            HarmonyLib.AccessTools.Field(typeof(Tool_SprayPaintCan), "PaintGasParticle") ??
            throw new InvalidOperationException("Unsupported spray-can particle field.");
        private static readonly System.Reflection.FieldInfo SprayButtonField =
            HarmonyLib.AccessTools.Field(typeof(Tool_SprayPaintCan), "PressButton") ??
            throw new InvalidOperationException("Unsupported spray-can button field.");
        private static readonly System.Reflection.FieldInfo RustRepairCameraField =
            HarmonyLib.AccessTools.Field(typeof(Tool_RustRepairKit), "mainCamera") ??
            throw new InvalidOperationException("Unsupported rust repair camera field.");
        private static readonly System.Reflection.FieldInfo DentRepairCameraField =
            HarmonyLib.AccessTools.Field(typeof(Tool_DentRepairKit), "mainCamera") ??
            throw new InvalidOperationException("Unsupported dent repair camera field.");
        private static readonly System.Reflection.FieldInfo RustSolutionCameraField =
            HarmonyLib.AccessTools.Field(typeof(Tool_RustSolution), "mainCamera") ??
            throw new InvalidOperationException("Unsupported rust solution camera field.");
        private static readonly System.Reflection.FieldInfo RustSolutionLastUsedField =
            HarmonyLib.AccessTools.Field(typeof(Tool_RustSolution), "LastTimeUsed") ??
            throw new InvalidOperationException("Unsupported rust solution cooldown field.");
        private static readonly System.Reflection.FieldInfo RustSolutionParticleField =
            HarmonyLib.AccessTools.Field(typeof(Tool_RustSolution), "PuishParticle") ??
            throw new InvalidOperationException("Unsupported rust solution particle field.");
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool disposed;
        private bool NewActionsBlocked;
        private float nextCapture;
        private float nextDynamicCapture, nextDynamicResolve;
        private float nextPurchasedCapture, nextPurchasedResolve;
        private bool dynamicDirty;
        private bool purchasedDirty;
        private readonly Dictionary<InteractableItems, Vehicle_PaintablePanel?> rootCache = new Dictionary<InteractableItems, Vehicle_PaintablePanel?>();
        private readonly List<InteractableItems> retiredRootOwners = new List<InteractableItems>();
        // Dynamic discovery and regular polling may both capture a root in this update.
        // Never reuse a capture across updates or after an intervening native mutation.
        private readonly Dictionary<Vehicle_PaintablePanel, PaintPanelState> updateCaptures =
            new Dictionary<Vehicle_PaintablePanel, PaintPanelState>();
        private bool updateCapturePass;
        private float nextGuestSend;


#if MONMP_DEVELOPMENT
        internal int TestAccepted { get; private set; }


        internal bool TestRepairDiagnosticsActive { get; set; }


        internal string TestLastRepairHostDiagnostic { get; private set; } = "not-sampled";


        private int repairDiagnosticLogs;


        private bool RepairDiagnosticsEnabled => TestRepairDiagnosticsActive && MonMPPlugin.Instance?.TestOptions.Enabled == true;


        private void RepairGateDiagnostic(string kind, uint peer, ulong id, ulong tool, uint generation,
            bool budget, bool fresh, PlayerPose pose, float observedTime, bool cameraFits, bool lease,
            bool kitRange, bool targetExists, bool panelRange, bool rayMatches, bool authorized,
            float x, float y, float z, float yaw, float pitch)
        {
            if (!RepairDiagnosticsEnabled) return;
            string kitDiagnostic;
            items.HostItemInRange(peer, tool, 4.5f, out kitDiagnostic);
            var actor = new Vector3(pose.X, pose.Y, pose.Z);
            string panelDiagnostic = "missing";
            if (bindings.TryGetValue(id, out var target) && target.Root != null)
            {
                var root = target.Root;
                var collider = root.GetComponent<Collider>();
                panelDiagnostic = "pivot=" + root.transform.position.ToString("F3") +
                    "/pivotDistance=" + Vector3.Distance(actor, root.transform.position).ToString("F3") +
                    "/boundsClosestDistance=" + (collider != null ? Vector3.Distance(actor, collider.bounds.ClosestPoint(actor)).ToString("F3") : "no-direct-collider");
            }
            var diagnosticRay = new Ray(new Vector3(x, y, z), Quaternion.Euler(pitch, yaw, 0) * Vector3.forward);
            string rayDiagnostic = "miss";
            int mask = LayerMask.GetMask("MainVehicleTrigger", "InteractableItem", "PartSocket", "VehiclePlayerCantGoThrough");
            if (Physics.Raycast(diagnosticRay, out var hit, 2.4f, mask))
            {
                bool resolved = TryResolveRepairPanel(diagnosticRay, out var hitBinding);
                rayDiagnostic = "collider=" + hit.collider.name + "/point=" + hit.point.ToString("F3") +
                    "/distance=" + hit.distance.ToString("F3") + "/resolved=" + resolved +
                    "/identity=" + (resolved ? hitBinding.Id.ToString("X16") : "none");
            }
            var publishedLease = items.StateOf(tool)?.Lease;

#if MONMP_DEVELOPMENT
            TestLastRepairHostDiagnostic = kind + "; requestedPanel=" + id.ToString("X16") + "; tool=" + tool +
                "; generation=" + generation + "; publishedGeneration=" + publishedLease?.Generation +
                "; synchronized=" + channel?.FullySynchronized + "; blocked=" + NewActionsBlocked +
                "; budget=" + budget + "; posePresent=" + presence.ContainsKey(peer) +
                "; freshWalking=" + fresh + "; poseState=" + pose.State +
                "; poseAge=" + (Time.unscaledTime - observedTime).ToString("F3") +
                "; actor=" + actor.ToString("F3") + "/yaw=" + pose.Yaw.ToString("F3") + "/pitch=" + pose.Pitch.ToString("F3") +
                "; camera=" + diagnosticRay.origin.ToString("F3") + "/yaw=" + yaw.ToString("F3") + "/pitch=" + pitch.ToString("F3") +
                "; cameraFits=" + cameraFits + "; exactLease=" + lease + "; kitRange=" + kitRange + "/" + kitDiagnostic +
                "; targetExists=" + targetExists + "; panelRange=" + panelRange + "/" + panelDiagnostic +
                "; gateRayMatches=" + rayMatches + "; diagnosticRay=" + rayDiagnostic + "; authorized=" + authorized;
#endif

            if (TestLastRepairHostDiagnostic.Length > 3000)
#if MONMP_DEVELOPMENT
TestLastRepairHostDiagnostic = TestLastRepairHostDiagnostic.Substring(0, 3000);
#else
{}
#endif

        }


        private void RepairOutcomeDiagnostic(string outcome)
        {
            if (!RepairDiagnosticsEnabled) return;

#if MONMP_DEVELOPMENT
            TestLastRepairHostDiagnostic += "; outcome=" + outcome;
#endif

            if (repairDiagnosticLogs++ < 24) Debug.Log("MONMP_REPAIR_HOST_GATES: " + TestLastRepairHostDiagnostic);
        }


        internal int TestRejected { get; private set; }


        internal int TestDuplicate { get; private set; }


        internal int TestActiveSprays => activeSprays.Count;


        internal bool TestClientSpraying => clientSpray != null;


        internal ulong[] TestPanels => bindings.Keys.OrderBy(id => id).ToArray();


        internal Vehicle_PaintablePanel TestNative(ulong id) => bindings[id].Root;


        internal PaintPanelState TestState(ulong id) => bindings[id].Current;


        internal PaintPanelState TestOriginal(ulong id) => bindings[id].Original;


        internal PaintPanelState TestNativeState(ulong id) => Capture(bindings[id].Root);


        internal uint TestRevision(ulong id) => channel != null && channel.Records.TryGetValue(id, out var record) ? record.Revision : 0;


        internal int TestPending => pending.Count;


        internal bool TestRestored => !host && bindings.Values.All(binding => binding.Root == null ||
            Same(Capture(binding.Root), binding.Original)) &&
            dynamicRollback.All(pair => pair.Key == null || Same(Capture(pair.Key), pair.Value));


        internal ulong[] TestDynamicPanels => bindings.Values.Where(b => b.Dynamic && !b.PurchasedVehicle).Select(b => b.Id).OrderBy(id => id).ToArray();

        internal ulong[] TestPurchasedPanelIds(ulong entity, GameObject instance)
        {
            if (channel == null || !purchasedVehicles.TryGetValue(entity, out var owner) || owner.Instance != instance ||
                !TryGetPurchasedPaintRoots(owner, out var roots) || roots.Count == 0)
                throw new InvalidOperationException("Purchased paint roots are not ready for the exact sale clone.");
            if (bindings.Values.Any(binding => !binding.Dynamic && binding.Root != null && binding.Root.transform.IsChildOf(instance.transform)))
                throw new InvalidOperationException("Purchased paint clone still has a static scene binding.");
            var ids = new List<ulong>();
            foreach (var entry in roots)
            {
                ulong id = PurchasedVehiclePaintState.RecordId(entity, entry.RelativePath);
                if (!bindings.TryGetValue(id, out var binding) || !binding.Dynamic || !binding.PurchasedVehicle ||
                    binding.Root != entry.Root || binding.PurchasedVehicleEntity != entity ||
                    !string.Equals(binding.RelativePanelPath, entry.RelativePath, StringComparison.Ordinal) ||
                    !channel.Records.TryGetValue(id, out var record) || record.Adapter != PurchasedVehiclePaintState.Adapter)
                    throw new InvalidOperationException("Purchased paint has no exact dynamic sale-relative binding or channel record.");
                var accepted = PurchasedVehiclePaintState.Decode(record.Payload);
                if (accepted.VehicleEntity != entity || !string.Equals(accepted.RelativePanelPath, entry.RelativePath, StringComparison.Ordinal) ||
                    !Same(binding.Current, accepted.Paint) || !Same(Capture(entry.Root), accepted.Paint))
                    throw new InvalidOperationException("Purchased paint binding/native state has not applied its exact channel envelope.");
                foreach (var panel in PurchasedAssociatedRoots(instance.transform, entry.Root))
                    if (!byPanel.TryGetValue(panel, out var mapped) || !ReferenceEquals(mapped, binding))
                        throw new InvalidOperationException("Purchased paint associated root is not mapped to its exact sale-relative binding.");
                ids.Add(id);
            }
            return ids.OrderBy(id => id).ToArray();
        }
#endif

        internal bool TransactionsSettled => pending.Count == 0 && !bindings.Values.Any(binding => binding.Dirty);


#if MONMP_DEVELOPMENT
        internal void TestRestoreHostPanel(ulong id, PaintPanelState? baseline = null)
        {
            if (!host || !bindings.TryGetValue(id, out var binding))
                throw new InvalidOperationException("Host panel restoration requires a registered test panel.");
            var restored = baseline ?? binding.Original;
            Apply(binding.Root, restored);
            binding.Current = restored;
            binding.Dirty = false;
            binding.DirtyState = null;
            if (channel?.Ready == true) channel.Publish(id, Payload(binding, restored));
        }
#endif


        private readonly AimSegmentTest checkSegment;

        internal SharedPaintPanels(AimSegmentTest checkSegment, bool host, SharedItems items, SharedVehicleParts parts, string diagnostics)
        {
            this.checkSegment = checkSegment;
            this.host = host;
            this.items = items;
            this.parts = parts;

#if MONMP_DEVELOPMENT
            invalidDiagnosticsPath = Path.Combine(diagnostics, "shared-paint-panels-invalid.tsv");
#endif


#if MONMP_DEVELOPMENT
            dynamicDiagnosticsPath = Path.Combine(diagnostics, "shared-paint-panels-dynamic-excluded.tsv");
#endif


#if MONMP_DEVELOPMENT
            targetDiagnosticsPath = Path.Combine(diagnostics, "shared-paint-panel-targets.tsv");
#endif


#if MONMP_DEVELOPMENT
            purchasedDiagnosticsPath = Path.Combine(diagnostics, "shared-paint-purchased-excluded.tsv");
#endif


#if MONMP_DEVELOPMENT
            File.WriteAllText(targetDiagnosticsPath, "entity\townerKind\townerId\tprefab\tpanelPath\tnativeRootPath\tcanonicalSaveRootPath\tpanelType\tassociated\tcantDent\tisRusted\tdirectCollider\tlayer\n");
#endif


#if MONMP_DEVELOPMENT
            File.WriteAllText(invalidDiagnosticsPath,
                "path\tpanelType\tfield\tisRusted\tforceRust\tred\tgreen\tblue\tdentScale\tsmoothness\tmetalness\terror\n");
#endif


#if MONMP_DEVELOPMENT
            File.WriteAllText(dynamicDiagnosticsPath, "ownerKind\townerId\townerType\tprefab\treason\n");
#endif


#if MONMP_DEVELOPMENT
            File.WriteAllText(purchasedDiagnosticsPath, "vehicleEntity\tvehicleType\tpanelPath\treason\n");
#endif

            var identities = new IdentityRegistry();
            var rows = new List<string> { "entity\tpath\tpanelType\tassociated\tpaintable" };
            var excludedRows = new List<string> { "reason\tpanelPath\trootPath\townerType\tinstances" };
            var panels = Object.FindObjectsOfType<Vehicle_PaintablePanel>(true)
                .Where(panel => panel != null && panel.gameObject.scene.IsValid())
                .OrderBy(panel => RuntimeProbe.CanonicalPath(panel.transform), StringComparer.Ordinal).ToArray();
            var rootPaths = new Dictionary<Vehicle_PaintablePanel, string>();
            var panelsByRoot = new Dictionary<Vehicle_PaintablePanel, List<Vehicle_PaintablePanel>>();
            var candidates = new List<KeyValuePair<string, Vehicle_PaintablePanel>>();
            var excludedItemPanels = new HashSet<Vehicle_PaintablePanel>();
            foreach (var panel in panels)
            {
                if (TruckBedPaintProxy.Contains(panel))
                {
                    excludedRows.Add("installed-part-paint-proxy\t" + RuntimeProbe.CanonicalPath(panel.transform) + "\t-\tVehicle_PartsManager\t1");
                    continue;
                }
                // A save may already own this exact sale clone before construction.
                // Its panels use the sale-relative purchased catalog on both peers.
                if (SharedVehicleSales.Active?.OwnsSaleClone(panel.transform) == true)
                {
                    excludedRows.Add("purchased-vehicle-owned\t" + RuntimeProbe.CanonicalPath(panel.transform) +
                        "\t-\tVehicle_PaintablePanel\t1");
                    continue;
                }
                // Installed membership varies with each save. Every InteractableItems
                // descendant stays out of the static catalog, including installed parts.
                var owner = panel.GetComponentInParent<InteractableItems>(true);
                Vehicle_PaintablePanel root;
                try { root = RootOf(panel); }
                catch (InvalidOperationException) when (owner != null)
                {
                    excludedRows.Add("item-owned-unsupported-root\t" + RuntimeProbe.CanonicalPath(panel.transform) +
                        "\t-\t" + owner.GetType().Name + "\t1");
                    continue;
                }
                owner ??= root.GetComponentInParent<InteractableItems>(true);
                if (owner != null)
                {
                    if (excludedItemPanels.Add(panel))
                    {
                        excludedRows.Add("item-owned\t" + RuntimeProbe.CanonicalPath(panel.transform) + "\t-\t" +
                            owner.GetType().Name + "\t1");
                    }
                    continue;
                }
                string path = RuntimeProbe.CanonicalPath(root.transform);
                if (!panelsByRoot.TryGetValue(root, out var rootPanels))
                {
                    rootPanels = new List<Vehicle_PaintablePanel>();
                    panelsByRoot.Add(root, rootPanels);
                    rootPaths.Add(root, path);
                    candidates.Add(new KeyValuePair<string, Vehicle_PaintablePanel>(path, root));
                }
                rootPanels.Add(panel);
            }

            // A runtime clone can reuse a serialized scene path, especially after a save
            // reload. Do not pick whichever instance Unity enumerates first: every root at
            // an ambiguous path is excluded so no peer can bind that address to another object.
            var uniqueRoots = StaticIdentityResolver.UniqueByPath(candidates, out var ambiguousPaths);
            var ambiguous = new HashSet<string>(ambiguousPaths, StringComparer.Ordinal);
            foreach (string path in ambiguousPaths)
            {
                int count = 0;
                foreach (var pair in rootPaths)
                    if (pair.Value == path) count++;
                excludedRows.Add("ambiguous-path\t" + path + "\t" + path + "\tVehicle_PaintablePanel\t" + count);
            }

            foreach (var pair in uniqueRoots.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                ulong id = identities.Register(pair.Key + "/shared-paint-panel-v1");
                var binding = new Binding { Id = id, Path = pair.Key, Root = pair.Value };
                binding.Original = binding.Current = Capture(pair.Value);
                bindings.Add(id, binding);
            }

            foreach (var pair in panelsByRoot)
            {
                string path = rootPaths[pair.Key];
                if (ambiguous.Contains(path) || !uniqueRoots.TryGetValue(path, out var selectedRoot) || !ReferenceEquals(selectedRoot, pair.Key))
                    continue;
                var binding = bindings[StaticId.Hash(path + "/shared-paint-panel-v1")];
                foreach (var panel in pair.Value)
                {
                    if (byPanel.TryGetValue(panel, out var previous) && previous.Id != binding.Id)
                        throw new InvalidOperationException("Native paint panel was assigned conflicting roots.");
                    byPanel[panel] = binding;

#if MONMP_DEVELOPMENT
                    DiagnoseTarget(binding, panel);
#endif

                    rows.Add(binding.Id.ToString("X16") + "\t" + path + "\t" + pair.Key.Get_PanelType + "\t" +
                        (pair.Key.PannelAssociatedToSave != null) + "\t" + (pair.Key.Get_Material != null));
                }
            }


#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-paint-panels.tsv"),
                rows.Take(1).Concat(rows.Skip(1).Distinct().OrderBy(row => row, StringComparer.Ordinal)));


            File.WriteAllLines(Path.Combine(diagnostics, "shared-paint-panels-excluded.tsv"),
                excludedRows.Take(1).Concat(excludedRows.Skip(1).Distinct().OrderBy(row => row, StringComparer.Ordinal)));
#endif

            if (bindings.Count == 0) throw new InvalidOperationException("No native paintable panels were found.");
        }

        public IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Where(binding => !binding.Dynamic)
            .Select(binding => new WorldRecord(binding.Id, 1, Adapter, binding.Original.Encode()));

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session;
            this.channel = channel;
            session.PoseReceived += OnPose;
            session.PeerLeft += OnLeft;
            channel.CommandRequested += Command;
            channel.CommandRejected += Stale;
            channel.ReplyReceived += Reply;
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed;
        }

        // Attach runs while NetworkSession is still offline. Register dynamic
        // records only after Host() changes the session state to Hosting, before
        // the first network tick can admit a joining peer.
        internal void HostStarted()
        {
            if (!host || session?.IsHost != true || channel == null)
                throw new InvalidOperationException("Dynamic paint registration requires an active host session.");
            RefreshDynamic(true);
            RefreshPurchasedPaint(true);
        }

        // Sale callbacks are replayed for saved clones and raised for live purchases.
        // The root adapter subscribes on both host and guest so late dynamic records can
        // resolve against the same sale entity without changing the static paint catalog.
        internal void RegisterPurchasedVehicle(ulong entity, Vehicle_Type type, GameObject instance)
        {
            if (disposed) return;
            if (entity <= WorldRecord.DynamicIdStart || instance == null || !instance.scene.IsValid())
                throw new InvalidOperationException("Purchased vehicle paint requires a dynamic sale entity and a scene clone.");
            if (purchasedVehicles.TryGetValue(entity, out var existing))
            {
                if (existing.Type != type || existing.Instance != instance)
                    throw new InvalidOperationException("Conflicting purchased vehicle paint clone for entity " + entity.ToString("X16", CultureInfo.InvariantCulture) + ".");
            }
            else
            {
                if (purchasedVehicles.Values.Any(value => value.Instance == instance && value.Entity != entity))
                    throw new InvalidOperationException("Purchased vehicle paint clone already has a different sale identity.");
                purchasedVehicles.Add(entity, new PurchasedVehiclePaintOwner
                {
                    Entity = entity, Type = type, Instance = instance, WaitThroughFrame = Time.frameCount
                });
            }
            purchasedDirty = true;
            if (host && session?.IsHost == true && channel != null) RefreshPurchasedPaint(true);
        }

        private void RefreshPurchasedPaint(bool force = false)
        {
            if (!host || session?.IsHost != true || channel == null) return;
            if (!force && Time.unscaledTime < nextPurchasedCapture) return;
            nextPurchasedCapture = Time.unscaledTime + DynamicCaptureInterval;
            foreach (var owner in purchasedVehicles.Values.OrderBy(value => value.Entity))
            {
                if (!TryGetPurchasedPaintRoots(owner, out var roots)) continue;
                foreach (var entry in roots)
                    RegisterPurchasedPaintRoot(owner, entry.Root, entry.RelativePath);
            }
        }

        private bool RegisterPurchasedPaintRoot(PurchasedVehiclePaintOwner owner,
            Vehicle_PaintablePanel root, string relativePath)
        {
            ulong id;
            try { id = PurchasedVehiclePaintState.RecordId(owner.Entity, relativePath); }
            catch (InvalidDataException) {
#if MONMP_DEVELOPMENT
DiagnosePurchased(owner, relativePath, "invalid-relative-panel-path");
#endif
 return false; }
            string identity = owner.Entity.ToString("X16", CultureInfo.InvariantCulture) + "/" + relativePath;
            if (purchasedIdentities.TryGetValue(id, out var previousIdentity) && !string.Equals(previousIdentity, identity, StringComparison.Ordinal))
            {
#if MONMP_DEVELOPMENT
DiagnosePurchased(owner, relativePath, "identity-hash-collision");
#endif
 return false; }
            purchasedIdentities[id] = identity;
            if ((channel!.Records.TryGetValue(id, out var occupied) && occupied.Adapter != PurchasedVehiclePaintState.Adapter) ||
                (bindings.TryGetValue(id, out var collision) && (!collision.PurchasedVehicle || collision.Root != root ||
                    collision.PurchasedVehicleEntity != owner.Entity || !string.Equals(collision.RelativePanelPath, relativePath, StringComparison.Ordinal))))
            {
#if MONMP_DEVELOPMENT
DiagnosePurchased(owner, relativePath, "record-id-collision");
#endif
 return false; }
            if (byPanel.TryGetValue(root, out var mapped) && (!mapped.PurchasedVehicle || mapped.Id != id))
            {
#if MONMP_DEVELOPMENT
DiagnosePurchased(owner, relativePath, "native-root-already-bound");
#endif
 return false; }

            PaintPanelState native;
            try { native = CaptureForUpdate(root); }
            catch (InvalidDataException) {
#if MONMP_DEVELOPMENT
DiagnosePurchased(owner, relativePath, "invalid-native-paint-state");
#endif
 return false; }

            PurchasedVehiclePaintState state;
            if (channel.Records.TryGetValue(id, out occupied))
            {
                state = PurchasedVehiclePaintState.Decode(occupied.Payload);
                if (state.VehicleEntity != owner.Entity || !string.Equals(state.RelativePanelPath, relativePath, StringComparison.Ordinal) ||
                    PurchasedVehiclePaintState.RecordId(state.VehicleEntity, state.RelativePanelPath) != id)
                {
#if MONMP_DEVELOPMENT
DiagnosePurchased(owner, relativePath, "ledger-identity-mismatch");
#endif
 return false; }
                if (!SameShape(root, state.Paint)) {
#if MONMP_DEVELOPMENT
DiagnosePurchased(owner, relativePath, "associated-shape-mismatch");
#endif
 return false; }
            }
            else state = new PurchasedVehiclePaintState(owner.Entity, relativePath, native);

            if (!bindings.TryGetValue(id, out var binding))
            {
                if (!BindPurchasedPaintRoot(owner, id, relativePath, root, native)) return false;
                binding = bindings[id];
            }
            if (channel.Records.TryGetValue(id, out occupied))
            {
                purchasedRecords[id] = state;
                if (!Same(binding.Current, state.Paint))
                {
                    Apply(root, state.Paint);
                    binding.Current = state.Paint;
                    updateCaptures.Remove(root);
                }
            }
            else
            {
                purchasedRecords[id] = state;
                channel.Register(new WorldRecord(id, 1, PurchasedVehiclePaintState.Adapter, state.Encode()));
            }
            return true;
        }

        private bool BindPurchasedPaintRoot(PurchasedVehiclePaintOwner owner, ulong id, string relativePath,
            Vehicle_PaintablePanel root, PaintPanelState original)
        {
            HashSet<Vehicle_PaintablePanel> closure;
            try { closure = PurchasedAssociatedRoots(owner.Instance.transform, root); }
            catch (InvalidOperationException) {
#if MONMP_DEVELOPMENT
DiagnosePurchased(owner, relativePath, "invalid-associated-panel-graph");
#endif
 return false; }
            foreach (var panel in closure)
                if (byPanel.TryGetValue(panel, out var previous) && previous.Id != id)
                {
#if MONMP_DEVELOPMENT
DiagnosePurchased(owner, relativePath, "associated-root-already-bound");
#endif
 return false; }
            var binding = new Binding
            {
                Id = id, Path = relativePath, Root = root, Original = original, Current = original,
                Dynamic = true, PurchasedVehicle = true, RelativePanelPath = relativePath,
                PurchasedVehicleEntity = owner.Entity
            };
            bindings.Add(id, binding);
            foreach (var panel in closure)
            {
                byPanel[panel] = binding;

#if MONMP_DEVELOPMENT
                DiagnoseTarget(binding, panel);
#endif

            }
            return true;
        }

        private bool TryGetPurchasedPaintRoots(PurchasedVehiclePaintOwner owner,
            out List<(Vehicle_PaintablePanel Root, string RelativePath)> roots)
        {
            roots = new List<(Vehicle_PaintablePanel, string)>();
            if (owner.Instance == null || !owner.Instance.scene.IsValid() || Time.frameCount <= owner.WaitThroughFrame) return false;
            var vehicles = owner.Instance.GetComponentsInChildren<VehicleController>(true)
                .Where(vehicle => vehicle != null && vehicle.gameObject.scene == owner.Instance.scene &&
                    vehicle.transform.IsChildOf(owner.Instance.transform)).ToArray();
            if (vehicles.Length != 1 || vehicles[0].vehicle_type != owner.Type) return false;
            var manager = vehicles[0].vehicle_PartsManager;
            if (manager == null || manager.gameObject.scene != owner.Instance.scene ||
                !manager.transform.IsChildOf(owner.Instance.transform)) return false;
            var saved = manager.Get_PaintablePanelToSave;
            if (saved == null) return false;
            var seen = new HashSet<Vehicle_PaintablePanel>();
            foreach (var savedPanel in saved)
            {
                if (savedPanel == null || TruckBedPaintProxy.Contains(savedPanel)) continue;
                Vehicle_PaintablePanel root;
                try { root = RootOf(savedPanel); }
                catch (InvalidOperationException) {
#if MONMP_DEVELOPMENT
DiagnosePurchased(owner, RuntimeProbe.CanonicalPath(savedPanel.transform), "invalid-parent-panel-graph");
#endif
 continue; }
                if (root == null || root.gameObject.scene != owner.Instance.scene || !root.transform.IsChildOf(owner.Instance.transform))
                {
#if MONMP_DEVELOPMENT
DiagnosePurchased(owner, RuntimeProbe.CanonicalPath(savedPanel.transform), "save-root-outside-sale-clone");
#endif
 continue; }
                // Item and installed-part save records retain their existing adapter 51 identity.
                if (root.GetComponentInParent<InteractableItems>(true) != null || !seen.Add(root)) continue;
                HashSet<Vehicle_PaintablePanel> closure;
                try { closure = PurchasedAssociatedRoots(owner.Instance.transform, root); }
                catch (InvalidOperationException) {
#if MONMP_DEVELOPMENT
DiagnosePurchased(owner, RuntimeProbe.CanonicalPath(root.transform), "invalid-associated-panel-graph");
#endif
 continue; }
                if (Time.frameCount <= owner.WaitThroughFrame || !closure.All(IsPurchasedPaintPanelReady)) continue;
                if (!PurchasedPaintPanelIdentityPolicy.TryRelativePath(owner.Instance.transform, root, out string relativePath))
                {
#if MONMP_DEVELOPMENT
DiagnosePurchased(owner, RuntimeProbe.CanonicalPath(root.transform), "relative-path-unavailable");
#endif
 continue; }
                try { _ = PurchasedVehiclePaintState.RecordId(owner.Entity, relativePath); }
                catch (InvalidDataException) {
#if MONMP_DEVELOPMENT
DiagnosePurchased(owner, relativePath, "invalid-relative-panel-path");
#endif
 continue; }
                roots.Add((root, relativePath));
            }
            roots.Sort((left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
            return true;
        }

        private static bool IsPurchasedPaintPanelReady(Vehicle_PaintablePanel panel) => panel != null &&
            panel.isActiveAndEnabled && panel.gameObject.activeInHierarchy && panel.Get_Material != null;

        private static HashSet<Vehicle_PaintablePanel> PurchasedAssociatedRoots(Transform vehicleRoot,
            Vehicle_PaintablePanel root)
        {
            var allowed = new HashSet<Vehicle_PaintablePanel>();
            for (var current = root; current != null; current = current.PannelAssociatedToSave)
            {
                if (allowed.Count >= PaintPanelState.MaxDepth || !allowed.Add(current) || RootOf(current) != current ||
                    current.gameObject.scene != vehicleRoot.gameObject.scene || !current.transform.IsChildOf(vehicleRoot) ||
                    current.GetComponentInParent<InteractableItems>(true) != null)
                    throw new InvalidOperationException("Invalid purchased vehicle paint association graph.");
            }
            return allowed;
        }


#if MONMP_DEVELOPMENT
        private void DiagnosePurchased(PurchasedVehiclePaintOwner owner, string panelPath, string reason)
        {
            ulong id = StaticId.Hash(owner.Entity.ToString("X16", CultureInfo.InvariantCulture) + "/" + panelPath);
            if (!diagnosedPurchasedPanels.Add(id)) return;
            File.AppendAllText(purchasedDiagnosticsPath, owner.Entity.ToString("X16", CultureInfo.InvariantCulture) + "\t" +
                owner.Type + "\t" + panelPath.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') + "\t" + reason + "\n");
        }
#endif


        private void ResolvePurchasedPaint()
        {
            if (host || disposed || purchasedRecords.Count == 0) return;
            var rootsByOwner = new Dictionary<ulong, Dictionary<string, Vehicle_PaintablePanel>>();
            foreach (var owner in purchasedVehicles.Values)
                if (TryGetPurchasedPaintRoots(owner, out var roots))
                    rootsByOwner[owner.Entity] = roots.ToDictionary(entry => entry.RelativePath, entry => entry.Root, StringComparer.Ordinal);

            foreach (var pair in purchasedRecords.ToArray())
            {
                var state = pair.Value;
                if (!rootsByOwner.TryGetValue(state.VehicleEntity, out var roots) ||
                    !roots.TryGetValue(state.RelativePanelPath, out var root)) continue;
                if (PurchasedVehiclePaintState.RecordId(state.VehicleEntity, state.RelativePanelPath) != pair.Key)
                    throw new InvalidDataException("Purchased paint ledger identity does not match its entity.");
                if (!SameShape(root, state.Paint))
                {

#if MONMP_DEVELOPMENT
                    DiagnosePurchased(purchasedVehicles[state.VehicleEntity], state.RelativePanelPath, "associated-shape-mismatch");
#endif

                    continue;
                }
                if (bindings.TryGetValue(pair.Key, out var collision) &&
                    (!collision.PurchasedVehicle || collision.Root != root || collision.PurchasedVehicleEntity != state.VehicleEntity))
                {

#if MONMP_DEVELOPMENT
                    DiagnosePurchased(purchasedVehicles[state.VehicleEntity], state.RelativePanelPath, "record-id-collision");
#endif

                    continue;
                }
                if (!bindings.TryGetValue(pair.Key, out var binding))
                {
                    if (!BindPurchasedPaintRoot(purchasedVehicles[state.VehicleEntity], pair.Key,
                        state.RelativePanelPath, root, Capture(root))) continue;
                    binding = bindings[pair.Key];
                }
                if (!Same(binding.Current, state.Paint))
                {
                    Apply(root, state.Paint);
                    binding.Current = state.Paint;
                }
            }
        }

        private void UnbindDynamic(Binding binding)
        {
            if (!binding.Dynamic || binding.PurchasedVehicle) return;
            foreach (var panel in byPanel.Where(pair => ReferenceEquals(pair.Value, binding)).Select(pair => pair.Key).ToArray())
                byPanel.Remove(panel);
            foreach (var operation in pending.Where(pair => pair.Value == binding.Id).Select(pair => pair.Key).ToArray())
            {
                pending.Remove(operation);
                pendingRepairCues.Remove(operation);
            }
            bindings.Remove(binding.Id);
        }

        private void BindDynamic(ulong id, DynamicPaintPanelState record, Vehicle_PaintablePanel root,
            PaintPanelState original)
        {
            var binding = new Binding
            {
                Id = id, Root = root, Original = original, Current = original,
                Dynamic = true, OwnerKind = record.OwnerKind, OwnerId = record.OwnerId, Prefab = record.Prefab
            };
            bindings.Add(id, binding);
            if (!host && !dynamicRollback.ContainsKey(root)) dynamicRollback.Add(root, original);
            var owner = root.GetComponent<InteractableItems>();
            if (owner == null) throw new InvalidOperationException("Dynamic paint root lost its item owner.");
            var allowed = AssociatedRoots(owner, root)!;
            foreach (var panel in owner.GetComponentsInChildren<Vehicle_PaintablePanel>(true))
                if (panel != null && panel.GetComponentInParent<InteractableItems>(true) == owner &&
                    allowed.Contains(RootOf(panel)))
                {
                    byPanel[panel] = binding;

#if MONMP_DEVELOPMENT
                    DiagnoseTarget(binding, panel);
#endif

                }
        }

        // Native GameObjectData reads only a panel directly on the item. A nested
        // independent root or a second direct panel has no supported save identity.
        private static Vehicle_PaintablePanel? DirectSaveRoot(InteractableItems owner)
        {
            var direct = owner.GetComponents<Vehicle_PaintablePanel>();
            if (direct.Length != 1 || direct[0] == null || RootOf(direct[0]) != direct[0]) return null;
            var allowed = AssociatedRoots(owner, direct[0]);
            if (allowed == null) return null;
            foreach (var panel in owner.GetComponentsInChildren<Vehicle_PaintablePanel>(true))
                if (panel != null && panel.GetComponentInParent<InteractableItems>(true) == owner &&
                    !allowed.Contains(RootOf(panel)))
                    return null;
            return direct[0];
        }

        private static HashSet<Vehicle_PaintablePanel>? AssociatedRoots(InteractableItems owner,
            Vehicle_PaintablePanel root)
        {
            var allowed = new HashSet<Vehicle_PaintablePanel>();
            for (var current = root; current != null; current = current.PannelAssociatedToSave)
            {
                if (allowed.Count >= PaintPanelState.MaxDepth || !allowed.Add(current) ||
                    current.GetComponentInParent<InteractableItems>(true) != owner || RootOf(current) != current)
                    return null;
            }
            return allowed;
        }

        private bool TryOwner(DynamicPaintPanelState record, HashSet<InteractableItems> installed,
            out Vehicle_PaintablePanel root)
        {
            InteractableItems? owner = null;
            if (record.OwnerKind == PaintOwnerKind.Item)
            {
                var state = items.StateOf(record.OwnerId);
                if (state?.Exists == true && state.Prefab == record.Prefab)
                    owner = items.ItemOf(record.OwnerId);
                if (owner != null && installed.Contains(owner)) owner = null;
            }
            else
            {
                var socket = parts.SocketOf(record.OwnerId);
                if (socket?.isInstalled == true && socket.partInstalled != null &&
                    parts.PaintPrefab(socket.partInstalled) == record.Prefab)
                    owner = socket.partInstalled;
            }
            try { root = owner == null ? null! : DirectSaveRoot(owner)!; }
            catch (InvalidOperationException) { root = null!; }
            return root != null;
        }

        private void RefreshDynamic(bool force = false)
        {
            if (!host || channel == null) return;
            if (!force && Time.unscaledTime < nextDynamicCapture) return;
            // Item paint only changes from a spray (live through TickSprays) or a fresh spawn; a
            // late save has thousands of items to walk, so this pass runs twice a second.
            nextDynamicCapture = Time.unscaledTime + DynamicCaptureInterval;
            PruneRootCache();
            var present = new HashSet<ulong>();
            var installed = new HashSet<InteractableItems>();
            foreach (var (ownerId, socket) in parts.PaintSockets)
            {
                if (!socket.isInstalled || socket.partInstalled == null) continue;
                installed.Add(socket.partInstalled);
                RegisterDynamicOwner(PaintOwnerKind.PartSocket, ownerId,
                    parts.PaintPrefab(socket.partInstalled), socket.partInstalled, present);
            }
            foreach (var (ownerId, item, prefab) in items.LivePrefabs)
            {
                if (installed.Contains(item)) continue;
                RegisterDynamicOwner(PaintOwnerKind.Item, ownerId, prefab, item, present);
            }
            foreach (var pair in dynamicRecords.ToArray())
            {
                if (present.Contains(pair.Key) || pair.Value.Paint == null) continue;
                if (bindings.TryGetValue(pair.Key, out var binding)) UnbindDynamic(binding);
                var absent = new DynamicPaintPanelState(pair.Value.OwnerKind, pair.Value.OwnerId, 0, null);
                dynamicRecords[pair.Key] = absent;
                channel.Publish(pair.Key, absent.Encode());
            }
        }

        private void PruneRootCache()
        {
            // Unity's destroyed objects compare null while their managed wrappers
            // remain valid dictionary keys. Reuse scratch storage; keep live negative
            // lookups cached so churn never restores repeated hierarchy searches.
            retiredRootOwners.Clear();
            foreach (var owner in rootCache.Keys) if (owner == null) retiredRootOwners.Add(owner!);
            foreach (var owner in retiredRootOwners) rootCache.Remove(owner);
            retiredRootOwners.Clear();
        }

        private void RegisterDynamicOwner(PaintOwnerKind kind, ulong ownerId, ushort prefab,
            InteractableItems owner, HashSet<ulong> present)
        {
            if (prefab == 0) {
#if MONMP_DEVELOPMENT
DiagnoseDynamic(kind, ownerId, owner, prefab, "unregistered-prefab");
#endif
 return; }
            // An item's panel hierarchy is fixed once spawned: resolve it once, not every capture
            // (most items have no panels, and a late save has thousands of them).
            if (!rootCache.TryGetValue(owner, out var root))
            {
                try { root = DirectSaveRoot(owner); }
                catch (InvalidOperationException) {
#if MONMP_DEVELOPMENT
DiagnoseDynamic(kind, ownerId, owner, prefab, "invalid-parent-graph");
#endif
 rootCache[owner] = null; return; }
                if (root == null && owner.GetComponentsInChildren<Vehicle_PaintablePanel>(true).Length > 0)

#if MONMP_DEVELOPMENT
                    DiagnoseDynamic(kind, ownerId, owner, prefab, "unsupported-save-shape");
#else
{}
#endif

                rootCache[owner] = root;
            }
            if (root == null) return;
            PaintPanelState state;
            try { state = CaptureForUpdate(root); }
            catch (InvalidDataException) {
#if MONMP_DEVELOPMENT
DiagnoseDynamic(kind, ownerId, owner, prefab, "invalid-paint-state");
#endif
 return; }
            ulong id = DynamicPaintPanelState.RecordId(kind, ownerId);
            if ((channel!.Records.TryGetValue(id, out var occupied) && occupied.Adapter != DynamicPaintPanelState.Adapter) ||
                (dynamicRecords.TryGetValue(id, out var ownerRecord) &&
                 (ownerRecord.OwnerKind != kind || ownerRecord.OwnerId != ownerId)) ||
                (bindings.TryGetValue(id, out var staticBinding) && (!staticBinding.Dynamic || staticBinding.PurchasedVehicle)) ||
                purchasedRecords.ContainsKey(id) || purchasedIdentities.ContainsKey(id))
            {

#if MONMP_DEVELOPMENT
                DiagnoseDynamic(kind, ownerId, owner, prefab, "record-id-collision");
#endif

                return;
            }
            if (!present.Add(id)) return;
            var record = new DynamicPaintPanelState(kind, ownerId, prefab, state);
            if (bindings.TryGetValue(id, out var existing) &&
                (!existing.Dynamic || existing.Root != root || existing.Prefab != prefab))
            {
                if (!existing.Dynamic) throw new InvalidOperationException("Dynamic paint ID collides with a static panel.");
                UnbindDynamic(existing);
            }
            if (!bindings.ContainsKey(id)) BindDynamic(id, record, root, state);
            if (!dynamicRecords.TryGetValue(id, out var previous))
            {
                dynamicRecords.Add(id, record);
                channel!.Register(new WorldRecord(id, 1, DynamicPaintPanelState.Adapter, record.Encode()));
            }
            else if (!previous.SameState(record))
            {
                dynamicRecords[id] = record;
                bindings[id].Current = state;
                channel!.Publish(id, record.Encode());
            }
        }


#if MONMP_DEVELOPMENT
        private void DiagnoseTarget(Binding binding, Vehicle_PaintablePanel panel)
        {
            var nativeRoot = RootOf(panel);
            var collider = panel.GetComponent<Collider>();
            File.AppendAllText(targetDiagnosticsPath, binding.Id.ToString("X16") + "\t" +
                (binding.PurchasedVehicle ? "PurchasedVehicle" : binding.Dynamic ? binding.OwnerKind.ToString() : "Static") + "\t" +
                (binding.PurchasedVehicle ? binding.PurchasedVehicleEntity : binding.OwnerId).ToString("X16") + "\t" +
                (binding.PurchasedVehicle ? (ushort)0 : binding.Prefab) + "\t" +
                RuntimeProbe.CanonicalPath(panel.transform) + "\t" + RuntimeProbe.CanonicalPath(nativeRoot.transform) + "\t" +
                RuntimeProbe.CanonicalPath(binding.Root.transform) + "\t" + nativeRoot.Get_PanelType + "\t" +
                (nativeRoot != binding.Root) + "\t" + CantDentField.GetValue(nativeRoot) + "\t" + nativeRoot.Get_IsRusted + "\t" +
                (collider != null) + "\t" + LayerMask.LayerToName(panel.gameObject.layer) + "\n");
        }
#endif


#if MONMP_DEVELOPMENT
        private void DiagnoseDynamic(PaintOwnerKind kind, ulong ownerId, InteractableItems owner,
            ushort prefab, string reason)
        {
            ulong id = DynamicPaintPanelState.RecordId(kind, ownerId);
            if (!diagnosedDynamicOwners.Add(id)) return;
            File.AppendAllText(dynamicDiagnosticsPath, kind + "\t" + ownerId.ToString("X16") + "\t" +
                owner.GetType().Name + "\t" + prefab + "\t" + reason + "\n");
        }
#endif


        private void ResolveDynamic()
        {
            if (host) return;
            var installed = new HashSet<InteractableItems>(parts.PaintSockets
                .Where(pair => pair.Socket.isInstalled && pair.Socket.partInstalled != null)
                .Select(pair => (InteractableItems)pair.Socket.partInstalled));
            foreach (var pair in dynamicRecords)
            {
                var record = pair.Value;
                if (record.Paint == null || !TryOwner(record, installed, out var root))
                {
                    if (bindings.TryGetValue(pair.Key, out var old)) UnbindDynamic(old);
                    continue;
                }
                if (!SameShape(root, record.Paint))
                {
                    if (bindings.TryGetValue(pair.Key, out var incompatible)) UnbindDynamic(incompatible);
                    continue;
                }
                if (bindings.TryGetValue(pair.Key, out var collision) && (!collision.Dynamic || collision.PurchasedVehicle))
                    throw new InvalidDataException("Dynamic paint ID collides with a static panel.");
                if (bindings.TryGetValue(pair.Key, out var binding) &&
                    (binding.Root != root || binding.Prefab != record.Prefab))
                {
                    UnbindDynamic(binding);
                    binding = null;
                }
                if (binding == null)
                {
                    PaintPanelState original;
                    try { original = Capture(root); }
                    catch (InvalidDataException) { continue; }
                    BindDynamic(pair.Key, record, root, original);
                    binding = bindings[pair.Key];
                }
                if (!Same(binding.Current, record.Paint))
                {
                    Apply(root, record.Paint);
                    binding.Current = record.Paint;
                }
            }
        }

        internal void SetNewActionsBlocked(bool blocked)
        {
            NewActionsBlocked = blocked;
            if (!blocked) return;
            if (host) activeSprays.Clear();
            else StopClientSpray(send: false);
        }

        // Guest mouse input starts a lease-bound host transaction. The native
        // coroutine is suppressed locally: only the host drains the can and
        // applies its native color to a shared panel. Local particles and audio
        // remain responsive while snapshots carry the authoritative can level.
        internal bool InterceptSprayMouseDown(Tool_SprayPaintCan tool)
        {
            if (disposed || session?.IsRunning != true) return true;
            if (host) return !NewActionsBlocked;
            if (!Ready || NewActionsBlocked || tool == null || tool.Value1 <= 0 ||
                !items.TryGetLocalToolLease(tool, out ulong toolId, out uint generation))
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("Wait for the shared world and leased spray can before painting.");
                return false;
            }
            StopClientSpray(send: true);
            var spray = new ClientSpray
            {
                Session = Guid.NewGuid(), ToolId = toolId, Generation = generation,
                Sequence = 1, Tool = tool, NextUpdate = Time.unscaledTime + SprayCommandInterval
            };
            clientSpray = spray;
            if (!SendClientSpray(spray, SprayPaintAction.Begin))
            {
                clientSpray = null;
                MonMPPlugin.Instance?.ReportItemUseUnavailable("The spray camera is not ready yet.");
                return false;
            }
            SetSprayEffects(tool, true);
            return false;
        }

        internal bool InterceptSprayMouseUp(Tool_SprayPaintCan tool)
        {
            if (disposed || session?.IsRunning != true) return true;
            if (host) return true;
            if (clientSpray?.Tool == tool) StopClientSpray(send: true);
            return false;
        }

        // A guest repair kit sends one lease-bound request. The guest never runs
        // the native RepairRust callback or destroys its local replica; both the
        // panel change and kit tombstone are committed by the host.
        internal bool InterceptRustRepairKitMouseDown(Tool_RustRepairKit tool)
        {
            if (disposed || session?.IsRunning != true) return true;
            if (host) return !NewActionsBlocked;
            if (!Ready || NewActionsBlocked || tool == null || !tool.isEquiped ||
                !items.TryGetLocalToolLease(tool, out ulong toolId, out uint generation))
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("Wait for the shared world and leased rust repair kit before repairing a panel.");
                return false;
            }

            var camera = RustRepairCameraField.GetValue(tool) as Camera;
            if (camera == null || pending.Count >= 16 || !TryResolveLocalRepairPanel(camera, out var binding, out var aim))
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("Aim the leased rust repair kit at a shared vehicle panel within reach.");
                return false;
            }
            if (binding.InFlightOperation != Guid.Empty) return false;

            RustRepairToolCommand command;
            try
            {
                command = new RustRepairToolCommand(Guid.NewGuid(), toolId, generation,
                    aim.Origin.x, aim.Origin.y, aim.Origin.z, aim.Yaw, aim.Pitch, aim.Independent);
            }
            catch (InvalidDataException)
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("The rust repair camera pose is not ready yet.");
                return false;
            }

            binding.InFlightOperation = command.Operation;
            pending.Add(command.Operation, binding.Id);
            pendingRepairCues.Add(command.Operation, RepairToolPresentation.Rust);
            channel!.Request(binding.Id, command.Encode(), true);
            return false;
        }

        // A guest submits one authorized native MouseDown. The host derives the
        // repair/damage stage from its own click history and panel result.
        internal bool InterceptDentRepairKitMouseDown(Tool_DentRepairKit tool)
        {
            if (disposed || session?.IsRunning != true) return true;
            if (host) return true;
            if (!Ready || NewActionsBlocked || tool == null || !tool.isEquiped ||
                !items.TryGetLocalToolLease(tool, out ulong toolId, out uint generation))
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("Wait for the shared world and leased dent repair kit before repairing a panel.");
                return false;
            }

            var camera = DentRepairCameraField.GetValue(tool) as Camera;
            if (camera == null || pending.Count >= 16 || !TryResolveLocalRepairPanel(camera, out var binding, out var aim))
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("Aim the leased dent repair kit at a shared vehicle panel within 2.4 m.");
                return false;
            }
            if (binding.InFlightOperation != Guid.Empty) return false;

            DentRepairToolCommand command;
            try
            {
                command = new DentRepairToolCommand(Guid.NewGuid(), toolId, generation,
                    aim.Origin.x, aim.Origin.y, aim.Origin.z, aim.Yaw, aim.Pitch, aim.Independent);
            }
            catch (InvalidDataException)
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("The dent repair camera pose is not ready yet.");
                return false;
            }

            binding.InFlightOperation = command.Operation;
            pending.Add(command.Operation, binding.Id);
            pendingRepairCues.Add(command.Operation, RepairToolPresentation.Dent);
            channel!.Request(binding.Id, command.Encode(), true);
            return false;
        }

        // The guest plays only the bottle's local feedback. ForceRust is always
        // performed against the host's canonical panel after lease and ray checks.
        internal bool InterceptRustSolutionMouseDown(Tool_RustSolution tool)
        {
            if (disposed || session?.IsRunning != true) return true;
            if (host) return !NewActionsBlocked;
            if (!Ready || NewActionsBlocked || tool == null || !tool.isEquiped ||
                !items.TryGetLocalToolLease(tool, out ulong toolId, out uint generation))
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("Wait for the shared world and leased rust solution before treating a panel.");
                return false;
            }

            float now = Time.timeSinceLevelLoad;
            float lastUse = (float)RustSolutionLastUsedField.GetValue(tool)!;
            if (!RustSolutionToolPolicy.CooldownElapsed(now, lastUse)) return false;

            var camera = RustSolutionCameraField.GetValue(tool) as Camera;
            if (camera == null || pending.Count >= 16 || !TryResolveLocalRepairPanel(camera, out var binding, out var aim))
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("Aim the leased rust solution at a shared vehicle panel within 2.4 m.");
                return false;
            }
            if (binding.InFlightOperation != Guid.Empty) return false;

            RustSolutionToolCommand command;
            try
            {
                command = new RustSolutionToolCommand(Guid.NewGuid(), toolId, generation,
                    aim.Origin.x, aim.Origin.y, aim.Origin.z, aim.Yaw, aim.Pitch, aim.Independent);
            }
            catch (InvalidDataException)
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("The rust solution camera pose is not ready yet.");
                return false;
            }

            binding.InFlightOperation = command.Operation;
            pending.Add(command.Operation, binding.Id);
            RustSolutionLastUsedField.SetValue(tool, now);
            PlayRustSolutionFeedback(tool);
            channel!.Request(binding.Id, command.Encode(), true);
            return false;
        }

        private static void PlayRustSolutionFeedback(Tool_RustSolution tool)
        {
            try
            {
                Singleton<SOUND_Manager>.i.PlayOneShot2D(
                    Singleton<SOUND_Manager>.i.Tool_BottleSpray, _randomPitch: true);
                var particle = RustSolutionParticleField.GetValue(tool) as ParticleSystem;
                particle?.Stop();
                particle?.Play();
    #if MONMP_DEVELOPMENT
            RepairToolPresentation.RecordLocal(RepairToolPresentation.Solution);
#endif
            }
            catch (Exception ex) { Debug.LogWarning("MonMP rust solution feedback failed: " + ex.Message); }
        }

        private bool SendClientSpray(ClientSpray spray, SprayPaintAction action)
        {
            if (channel == null) return false;
            Integration.ToolAimSample aim = default;
            if (action != SprayPaintAction.End)
            {
                var camera = SprayCameraField.GetValue(spray.Tool) as Camera;
                if (camera == null) return false;
                aim = Integration.LocalInteraction.SampleTool(camera);
            }
            SprayPaintCommand command;
            try
            {
                command = new SprayPaintCommand(action, spray.Session, spray.Sequence,
                    spray.ToolId, spray.Generation, aim.Origin.x, aim.Origin.y, aim.Origin.z, aim.Yaw, aim.Pitch, aim.Independent);
            }
            catch (InvalidDataException) { return false; }
            channel.Request(spray.ToolId, command.Encode(), true);
            return true;
        }

        private void StopClientSpray(bool send)
        {
            var spray = clientSpray;
            clientSpray = null;
            if (spray == null) return;
            if (send && channel != null)
            {
                spray.Sequence++;
                SendClientSpray(spray, SprayPaintAction.End);
            }
            SetSprayEffects(spray.Tool, false);
        }

        private static void SetSprayEffects(Tool_SprayPaintCan tool, bool playing)
        {
            try
            {
                var particles = SprayParticleField.GetValue(tool) as ParticleSystem;
                if (playing) particles?.Play(); else particles?.Stop();
            }
            catch (Exception ex) { Debug.LogWarning("MonMP spray particles failed: " + ex.Message); }
            try
            {
                if (playing) Singleton<SOUND_Manager>.i.PlaySound_TirePump_AirOut_Start();
                else Singleton<SOUND_Manager>.i.PlaySound_TirePump_AirOut_Stop();
            }
            catch (Exception ex) { Debug.LogWarning("MonMP spray sound failed: " + ex.Message); }
        }

        // Returning false blocks an unsynchronized or unregistered native action.
        // Guest paint remains optimistic locally and is submitted as a coalesced state.
        internal bool InterceptPaint(Vehicle_PaintablePanel panel)
        {
            if (disposed || session?.IsRunning != true) return true;
            if (host) return !NewActionsBlocked && TryCanonicalBinding(panel, out _, forceRegistration: true);
            if (!TryCanonicalBinding(panel, out var binding)) return false;
            if (!CanAcceptNewAction()) return false;
            binding.DirtyState = Capture(binding.Root);
            binding.Dirty = true;
            return true;
        }

        // Repair operations are executed by the host. Guests never mutate a local
        // save copy with an unreplicated rust/dent change.
        internal bool InterceptRepair(Vehicle_PaintablePanel panel, PaintPanelAction kind, bool doDamage, ref bool result)
        {
            if (disposed || session?.IsRunning != true) return true;
            if (host) return !NewActionsBlocked && TryCanonicalBinding(panel, out _, forceRegistration: true);
            // RustRepair is only available through the lease-bound repair-kit
            // transaction below. Do not accept an untyped panel-only request.
            if (kind == PaintPanelAction.RepairRust || kind == PaintPanelAction.RepairDent ||
                kind == PaintPanelAction.DamageDent || kind == PaintPanelAction.ForceRust)
            { result = false; return false; }
            result = false;
            if (!TryCanonicalBinding(panel, out var binding) || !CanAcceptNewAction()) return false;
            if (pending.Count >= 16 || binding.InFlightOperation != Guid.Empty) return false;
            SendAction(binding, kind);
            return false;
        }

        private bool CanAcceptNewAction()
        {
            if (channel?.FullySynchronized != true || NewActionsBlocked)
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("Wait for the shared world and multiplayer save to finish before painting.");
                return false;
            }
            return true;
        }

        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer)
        {
            presence.Remove(peer); budgets.Remove(peer); sprayBudgets.Remove(peer); activeSprays.Remove(peer); nextSprayTargetWarning.Remove(peer);
            rustSolutionCooldowns.RemovePeer(peer);
            spraySequences.RemovePeer(peer);
            foreach (var key in dentProgress.Keys.Where(key => key.Peer == peer).ToArray()) dentProgress.Remove(key);
        }

        private void SendAction(Binding binding, PaintPanelAction kind)
        {
            if (channel == null || host) return;
            Guid operation = Guid.NewGuid();
            var request = new PaintPanelRequest(operation, (byte)kind, false, null);
            binding.InFlightOperation = operation;
            pending.Add(operation, binding.Id);
            channel.Request(binding.Id, request.Encode(), true);
        }

        private void SendPaint(Binding binding)
        {
            if (channel == null || host || !binding.Dirty || binding.InFlightOperation != Guid.Empty || binding.DirtyState == null) return;
            PaintPanelState requested = binding.DirtyState;
            if (Same(requested, binding.Current)) { binding.Dirty = false; binding.DirtyState = null; return; }
            Guid operation = Guid.NewGuid();
            binding.InFlightOperation = operation;
            binding.Dirty = false;
            binding.DirtyState = null;
            pending.Add(operation, binding.Id);
            channel!.Request(binding.Id, new PaintPanelRequest(operation, (byte)PaintPanelAction.Paint, false, requested).Encode(), true);
        }

        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (DentRepairToolCommand.HasMarker(payload))
            {
                if (host) DentRepair(peer, id, payload);
                return;
            }
            if (RustRepairToolCommand.HasMarker(payload))
            {
                if (host) RustRepair(peer, id, payload);
                return;
            }
            if (RustSolutionToolCommand.HasMarker(payload))
            {
                if (host) RustSolution(peer, id, payload);
                return;
            }
            if (SprayPaintCommand.Is(payload)) { if (host) SprayCommand(peer, id, payload); return; }
            if (host) RefreshDynamic(true);
            if (!host || peer == 0 || !bindings.TryGetValue(id, out var binding) || session == null || channel == null) return;
            PaintPanelRequest request;
            try { request = PaintPanelRequest.Decode(payload); }
            catch (InvalidDataException) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }
            if (!session.Peers.TryGetValue(peer, out var remote)) return;
            var key = (remote.ProfileId, request.Operation);
            if (claims.Contains(key)) {
#if MONMP_DEVELOPMENT
TestDuplicate++;
#endif
 Reply(peer, id, request.Operation, ReplyStatus.Duplicate); return; }
            if (claims.Count >= 16384) { Reply(peer, id, request.Operation, ReplyStatus.Capacity); return; }
            claims.Add(key);
            if (NewActionsBlocked || !TakeBudget(peer) || !PeerInRange(peer, binding))
            {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 Reply(peer, id, request.Operation, ReplyStatus.Rejected); return; }

            // Repairs and damage are coupled to their exact leased tool identity;
            // a generic panel request cannot bypass item authority or click state.
            if (request.Kind == (byte)PaintPanelAction.RepairRust ||
                request.Kind == (byte)PaintPanelAction.RepairDent ||
                request.Kind == (byte)PaintPanelAction.DamageDent ||
                request.Kind == (byte)PaintPanelAction.ForceRust)
            {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 Reply(peer, id, request.Operation, ReplyStatus.Rejected); return; }

            try
            {
                if (request.Kind == (byte)PaintPanelAction.Paint)
                {
                    var state = request.State!;
                    if (!PaintOnlyChange(binding.Current, state) || !SameShape(binding.Root, state))
                    {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 Reply(peer, id, request.Operation, ReplyStatus.Rejected); return; }
                    Apply(binding.Root, state);
                    binding.Current = state;
                    channel.Publish(id, Payload(binding, state));

#if MONMP_DEVELOPMENT
                    TestAccepted++;
#endif

                    Reply(peer, id, request.Operation, ReplyStatus.Applied);
                    return;
                }

                bool changed = (PaintPanelAction)request.Kind switch
                {
                    PaintPanelAction.RepairRust => binding.Root.RepairRust(),
                    PaintPanelAction.RepairDent => binding.Root.RepairDent(),
                    PaintPanelAction.DamageDent => binding.Root.RepairDent(doDamage: true),
                    PaintPanelAction.ForceRust => binding.Root.ForceRust(),
                    _ => false
                };
                var result = Capture(binding.Root);
                if (changed && !Same(result, binding.Current))
                {
                    binding.Current = result;
                    channel.Publish(id, Payload(binding, result));

#if MONMP_DEVELOPMENT
                    TestAccepted++;
#endif

                    Reply(peer, id, request.Operation, ReplyStatus.Applied);
                }
                else Reply(peer, id, request.Operation, ReplyStatus.NoChange);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);

#if MONMP_DEVELOPMENT
                TestRejected++;
#endif

                Reply(peer, id, request.Operation, ReplyStatus.Rejected);
            }
        }

        private void RustRepair(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || session == null || channel == null) return;
            RustRepairToolCommand command;
            try { command = RustRepairToolCommand.Decode(payload); }
            catch (InvalidDataException) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }

            if (!session.Peers.TryGetValue(peer, out var remote)) return;
            if (claims.Contains((remote.ProfileId, command.Operation)))
            {
#if MONMP_DEVELOPMENT
TestDuplicate++;
#endif
 Reply(peer, id, command.Operation, ReplyStatus.Duplicate); return; }
            if (claims.Count >= 16384)
            { Reply(peer, id, command.Operation, ReplyStatus.Capacity); return; }
            claims.Add((remote.ProfileId, command.Operation));

            bool requestBudget = TakeBudget(peer);
            bool hasPose = presence.TryGetValue(peer, out var observed) && Time.unscaledTime - observed.Time <= 2 &&
                observed.Pose.State == (byte)PlayerState.Walking;
            bool cameraFits = hasPose && command.CameraFits(observed.Pose, checkSegment);
            bool targetExists = bindings.TryGetValue(id, out var binding) && binding.Root != null &&
                binding.Root.gameObject.activeInHierarchy;
            bool exactKitLease = items.HostHasLeasedTool(peer, command.Tool, command.Generation, typeof(Tool_RustRepairKit));
            bool kitInRange = items.HostItemInRange(peer, command.Tool, 4.5f);
            Vehicle_PaintablePanel repairPanel = null!;
            bool rayHitsPanel = hasPose && cameraFits && TryResolveRepairPanel(command.CameraX, command.CameraY,
                command.CameraZ, command.CameraYaw, command.CameraPitch, out ulong hitPanelId, out repairPanel) && hitPanelId == id;
            // Native kit reach is the exact 2.4m surface ray, not the save-root pivot.
            bool panelInRange = rayHitsPanel;
            bool authorized = RustRepairPolicy.CanExecuteHostRequest(channel.FullySynchronized, NewActionsBlocked, requestBudget,
                exactKitLease, kitInRange, hasPose, cameraFits, targetExists, panelInRange, rayHitsPanel, id);

#if MONMP_DEVELOPMENT
            RepairGateDiagnostic("rust", peer, id, command.Tool, command.Generation, requestBudget, hasPose,
                observed.Pose, observed.Time, cameraFits, exactKitLease, kitInRange, targetExists, panelInRange,
                rayHitsPanel, authorized, command.CameraX, command.CameraY, command.CameraZ, command.CameraYaw, command.CameraPitch);
#endif

            if (!authorized)
            {

#if MONMP_DEVELOPMENT
                RepairOutcomeDiagnostic("gate-rejected");


                TestRejected++;
#endif

                Reply(peer, id, command.Operation, ReplyStatus.Rejected);
                return;
            }

            Binding target = binding!;
            bool mutationRan = false;
            bool nativeRepairChanged = false;
            bool consumed = items.HostMutateLeasedTool(peer, command.Tool, command.Generation,
                typeof(Tool_RustRepairKit), tool =>
                {
                    mutationRan = true;
                    nativeRepairChanged = repairPanel.RepairRust();
                    var outcome = RustRepairPolicy.Complete(true, true, nativeRepairChanged);
                    if (!RustRepairPolicy.ConsumeKit(outcome)) return false;
                    tool.Value1 = 0;
                    return true;
                }, destroyWhenEmpty: true, publish: false);

            var result = RustRepairPolicy.Finalize(true, mutationRan, nativeRepairChanged, consumed);

#if MONMP_DEVELOPMENT
            RepairOutcomeDiagnostic("mutationRan=" + mutationRan + "/nativeChanged=" + nativeRepairChanged +
                "/kitConsumed=" + consumed + "/finalize=" + result);
#endif

            if (result == RustRepairResult.Applied)
            {
                RepairToolPresentation.Announce(RepairToolPresentation.Rust, id, repairPanel.transform.position, peer);
                var repairedState = Capture(target!.Root!);
                target.Current = repairedState;
                channel.Publish(id, Payload(target, repairedState));

#if MONMP_DEVELOPMENT
                TestAccepted++;
#endif

            }
            else if (result == RustRepairResult.Rejected)
#if MONMP_DEVELOPMENT
TestRejected++;
#else
{}
#endif

            Reply(peer, id, command.Operation, (ReplyStatus)result);
        }

        private void DentRepair(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || session == null || channel == null) return;
            DentRepairToolCommand command;
            try { command = DentRepairToolCommand.Decode(payload); }
            catch (InvalidDataException) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }

            if (!session.Peers.TryGetValue(peer, out var remote)) return;
            if (claims.Contains((remote.ProfileId, command.Operation)))
            {
#if MONMP_DEVELOPMENT
TestDuplicate++;
#endif
 Reply(peer, id, command.Operation, ReplyStatus.Duplicate); return; }
            if (claims.Count >= 16384)
            { Reply(peer, id, command.Operation, ReplyStatus.Capacity); return; }
            claims.Add((remote.ProfileId, command.Operation));

            bool requestBudget = TakeBudget(peer);
            bool hasPose = presence.TryGetValue(peer, out var observed) && Time.unscaledTime - observed.Time <= 2 &&
                observed.Pose.State == (byte)PlayerState.Walking;
            bool cameraFits = hasPose && command.CameraFits(observed.Pose, checkSegment);
            bool targetExists = bindings.TryGetValue(id, out var binding) && binding.Root != null &&
                binding.Root.gameObject.activeInHierarchy;
            bool exactKitLease = items.HostHasLeasedTool(peer, command.Tool, command.Generation, typeof(Tool_DentRepairKit));
            bool kitInRange = items.HostItemInRange(peer, command.Tool, 4.5f);
            Vehicle_PaintablePanel repairPanel = null!;
            bool rayHitsPanel = hasPose && cameraFits && TryResolveRepairPanel(command.CameraX, command.CameraY,
                command.CameraZ, command.CameraYaw, command.CameraPitch, out ulong hitPanelId, out repairPanel) && hitPanelId == id;
            // Native kit reach is the exact 2.4m surface ray, not the save-root pivot.
            bool panelInRange = rayHitsPanel;
            bool authorized = DentRepairToolPolicy.CanExecuteHostRequest(channel.FullySynchronized, NewActionsBlocked,
                requestBudget, exactKitLease, kitInRange, hasPose, cameraFits, targetExists, panelInRange,
                rayHitsPanel, id);

#if MONMP_DEVELOPMENT
            RepairGateDiagnostic("dent", peer, id, command.Tool, command.Generation, requestBudget, hasPose,
                observed.Pose, observed.Time, cameraFits, exactKitLease, kitInRange, targetExists, panelInRange,
                rayHitsPanel, authorized, command.CameraX, command.CameraY, command.CameraZ, command.CameraYaw, command.CameraPitch);
#endif

            if (!authorized)
            {

#if MONMP_DEVELOPMENT
                RepairOutcomeDiagnostic("gate-rejected");


                TestRejected++;
#endif

                Reply(peer, id, command.Operation, ReplyStatus.Rejected);
                return;
            }

            var key = (Peer: peer, Tool: command.Tool);
            if (!dentProgress.TryGetValue(key, out var lease) || lease.Generation != command.Generation)
            {
                if (!dentProgress.ContainsKey(key) && dentProgress.Count >= 2048)
                {

#if MONMP_DEVELOPMENT
                    RepairOutcomeDiagnostic("dent-progress-capacity");
#endif

                    Reply(peer, id, command.Operation, ReplyStatus.Capacity);
                    return;
                }
                lease = new DentProgressLease { Generation = command.Generation };
                dentProgress[key] = lease;
            }

            DentRepairClickResult result;
            try
            {
                bool canDent = !repairPanel.Get_CantBeDent;
                // Associated accessories share a save record, but native click progression
                // resets when the independently repairable panel changes.
                ulong progressionPanel = unchecked((uint)repairPanel.GetInstanceID());
                result = lease.Progress.Click(progressionPanel, Time.realtimeSinceStartup, canDent,
                    () => repairPanel.RepairDent(), () => repairPanel.RepairDent(doDamage: true));
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);

#if MONMP_DEVELOPMENT
                TestRejected++;
#endif

                Reply(peer, id, command.Operation, ReplyStatus.Rejected);

#if MONMP_DEVELOPMENT
                RepairOutcomeDiagnostic("native-dent-exception=" + ex.GetType().Name);
#endif

                return;
            }


#if MONMP_DEVELOPMENT
            RepairOutcomeDiagnostic("native-dent-result=" + result);
#endif

            if (result == DentRepairClickResult.Rejected)
            {

#if MONMP_DEVELOPMENT
                TestRejected++;
#endif

                Reply(peer, id, command.Operation, ReplyStatus.Rejected);
                return;
            }

            var updated = Capture(binding!.Root!);
            if (!Same(updated, binding.Current))
            {
                binding.Current = updated;
                channel.Publish(id, Payload(binding, updated));
            }

            if (result == DentRepairClickResult.Repaired || result == DentRepairClickResult.Damaged)
            {
                RepairToolPresentation.Announce(RepairToolPresentation.Dent, id, repairPanel.transform.position, peer);

#if MONMP_DEVELOPMENT
                TestAccepted++;
#endif

                Reply(peer, id, command.Operation, ReplyStatus.Applied);
            }
            else Reply(peer, id, command.Operation, ReplyStatus.NoChange);
        }

        private void RustSolution(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || session == null || channel == null) return;
            RustSolutionToolCommand command;
            try { command = RustSolutionToolCommand.Decode(payload); }
            catch (InvalidDataException) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }

            if (!session.Peers.TryGetValue(peer, out var remote)) return;
            if (claims.Contains((remote.ProfileId, command.Operation)))
            {
#if MONMP_DEVELOPMENT
TestDuplicate++;
#endif
 Reply(peer, id, command.Operation, ReplyStatus.Duplicate); return; }
            if (claims.Count >= 16384)
            { Reply(peer, id, command.Operation, ReplyStatus.Capacity); return; }
            claims.Add((remote.ProfileId, command.Operation));

            float now = Time.timeSinceLevelLoad;
            bool requestBudget = TakeBudget(peer);
            bool hasPose = presence.TryGetValue(peer, out var observed) && Time.unscaledTime - observed.Time <= 2 &&
                observed.Pose.State == (byte)PlayerState.Walking;
            bool cameraFits = hasPose && command.CameraFits(observed.Pose, checkSegment);
            bool targetExists = bindings.TryGetValue(id, out var binding) && binding.Root != null &&
                binding.Root.gameObject.activeInHierarchy;
            bool exactBottleLease = items.HostHasLeasedTool(peer, command.Tool, command.Generation, typeof(Tool_RustSolution));
            bool bottleInRange = items.HostItemInRange(peer, command.Tool, 4.5f);
            Vehicle_PaintablePanel repairPanel = null!;
            bool rayHitsPanel = hasPose && cameraFits && TryResolveRepairPanel(command.CameraX, command.CameraY,
                command.CameraZ, command.CameraYaw, command.CameraPitch, out ulong hitPanelId, out repairPanel) && hitPanelId == id;
            // Native kit reach is the exact 2.4m surface ray, not the save-root pivot.
            bool panelInRange = rayHitsPanel;
            bool cooldownReady = rustSolutionCooldowns.IsReady(peer, command.Tool, command.Generation, now);
            bool authorized = RustSolutionToolPolicy.CanExecuteHostRequest(channel.FullySynchronized, NewActionsBlocked,
                requestBudget, cooldownReady, exactBottleLease, bottleInRange, hasPose, cameraFits,
                targetExists, panelInRange, rayHitsPanel, id);
            if (!authorized)
            {

#if MONMP_DEVELOPMENT
                TestRejected++;
#endif

                Reply(peer, id, command.Operation, ReplyStatus.Rejected);
                return;
            }

            Binding target = binding!;
            bool nativeChanged = false;
            bool invoked = false;
            try
            {
                bool leasedMutation = items.HostMutateLeasedTool(peer, command.Tool, command.Generation,
                    typeof(Tool_RustSolution), tool =>
                    {
                        if (!rustSolutionCooldowns.RecordUse(peer, command.Tool, command.Generation, now)) return false;
                        RustSolutionLastUsedField.SetValue(tool, now);
                        invoked = true;
                        nativeChanged = repairPanel.ForceRust();
                        return true;
                    }, publish: false);
                if (!leasedMutation || !invoked)
                {

#if MONMP_DEVELOPMENT
                    TestRejected++;
#endif

                    Reply(peer, id, command.Operation, ReplyStatus.Rejected);
                    return;
                }

                RepairToolPresentation.Announce(RepairToolPresentation.Solution, command.Tool, repairPanel.transform.position, peer);

                PaintPanelState updated = Capture(target.Root!);
                if (nativeChanged && !Same(updated, target.Current))
                {
                    target.Current = updated;
                    channel.Publish(id, Payload(target, updated));

#if MONMP_DEVELOPMENT
                    TestAccepted++;
#endif

                    Reply(peer, id, command.Operation, ReplyStatus.Applied);
                }
                else Reply(peer, id, command.Operation, ReplyStatus.NoChange);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);

#if MONMP_DEVELOPMENT
                TestRejected++;
#endif

                Reply(peer, id, command.Operation, ReplyStatus.Rejected);
            }
        }

        private void SprayCommand(uint peer, ulong entity, byte[] payload)
        {
            if (!host || peer == 0 || session == null || channel == null ||
                !session.Peers.TryGetValue(peer, out var remote)) return;
            SprayPaintCommand command;
            try { command = SprayPaintCommand.Decode(payload); }
            catch (InvalidDataException) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }
            if (command.Tool != entity) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
                WarnSprayRejected(peer, command, "entity-mismatch");
 return; }

            float now = Time.unscaledTime;
            if (activeSprays.TryGetValue(peer, out var expired) && now - expired.LastCommand > SprayCommandTimeout)
                activeSprays.Remove(peer);

            // A new session is bounded before taking a permanent replay entry.
            // Once admitted to sequencing, consume ordered updates even when a
            // transient pose/range/save check refuses their gameplay effect.
            // Otherwise the missing sequence makes every later update fail.
            if (command.Action == SprayPaintAction.Begin && !TakeSprayBudget(peer))
            {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
                WarnSprayRejected(peer, command, "budget");
                return;
            }
            var sequenceResult = spraySequences.Claim(peer, command);
            if (sequenceResult != SprayPaintSequenceResult.Accepted)
            {
#if MONMP_DEVELOPMENT
TestDuplicate++;
#endif
                // A replay or out-of-order packet must never cancel a newer
                // valid hold, nor create a hold without its recorded Begin.
                WarnSprayRejected(peer, command, "sequence-" + sequenceResult);
                return;
            }

            activeSprays.TryGetValue(peer, out var active);
            bool sameActive = active != null && active.Session == command.Session &&
                active.Profile == remote.ProfileId && active.ToolId == command.Tool && active.Generation == command.Generation;
            if (command.Action == SprayPaintAction.End)
            {
                // End remains a tombstone even if Begin was temporarily
                // refused or Tick already retired its active presentation.
                if (sameActive) activeSprays.Remove(peer);
                return;
            }
            if (active != null && !sameActive)
            {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
                WarnSprayRejected(peer, command, "another-session-active");
                return;
            }

            Vector3 color = default;
            float smoothness = 0, metalness = 0;
            bool overrideColor = false;
            ulong panelId = 0;
            var can = items.ItemOf(command.Tool) as Tool_SprayPaintCan;
            string? reason = null;
            if (command.Action == SprayPaintAction.Update && !TakeSprayBudget(peer)) reason = "budget";
            else if (NewActionsBlocked) reason = "save-or-not-ready";
            else if (!items.HostHasLeasedTool(peer, command.Tool, command.Generation, typeof(Tool_SprayPaintCan))) reason = "lease";
            else if (!items.HostItemInRange(peer, command.Tool, 4.5f)) reason = "range";
            else if (can == null || can.Value1 <= 0) reason = "can-empty-or-missing";
            else if (active == null && !ReadSpraySettings(can, out color, out smoothness, out metalness, out overrideColor)) reason = "settings";
            else if (!TryResolveSprayTarget(peer, command, out panelId)) reason = "camera-pose";
            if (reason != null)
            {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
                if (sameActive) activeSprays.Remove(peer);
                WarnSprayRejected(peer, command, reason);
                return;
            }

            if (active == null)
            {
                // A known ordered Update may resume an interrupted hold, but
                // it passes every native admission gate again before painting.
                active = new ActiveSpray
                {
                    Session = command.Session, Profile = remote.ProfileId, ToolId = command.Tool,
                    Generation = command.Generation, Tool = can!, Color = color, Smoothness = smoothness,
                    Metalness = metalness, OverrideColor = overrideColor, StartedAt = now
                };
                activeSprays[peer] = active;
            }
            active.PanelId = panelId;
            active.CameraOrigin = new Vector3(command.CameraX, command.CameraY, command.CameraZ);
            active.CameraYaw = command.CameraYaw;
            active.CameraPitch = command.CameraPitch;
            active.IndependentAim = command.IndependentAim;
            active.LastCommand = now;
        }

        private bool TryResolveSprayTarget(uint peer, SprayPaintCommand command, out ulong id)
            => TryResolveSprayTarget(peer, new Vector3(command.CameraX, command.CameraY, command.CameraZ),
                command.CameraYaw, command.CameraPitch, out id, out _, out _, out _, items.ItemOf(command.Tool)?.transform, command.IndependentAim);

        private bool TryResolveSprayTarget(uint peer, Vector3 origin, float yaw, float pitch, out ulong id,
            out Vehicle_PaintablePanel? paintPanel, out Collider? targetCollider, out SprayTargetResult result, Transform? heldTool, bool independentAim)
        {
            id = 0; paintPanel = null; targetCollider = null; result = SprayTargetResult.InvalidPose;
            if (!presence.TryGetValue(peer, out var observed) || Time.unscaledTime - observed.Time > 2) return false;
            var pose = observed.Pose.WithIndependentAim(independentAim);
            if ((pose.State != (byte)PlayerState.Walking && pose.State != (byte)PlayerState.InCreeper) ||
                !ToolAimPolicy.CameraOriginFits(pose, origin.x, origin.y, origin.z, allowCreeper: true, clear: checkSegment) ||
                !ToolAimPolicy.CameraAimFits(pose, yaw, pitch, allowCreeper: true)) return false;

            var direction = Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;
            int mask = LayerMask.GetMask("MainVehicleTrigger", "InteractableItem", "PartSocket",
                "VehiclePlayerCantGoThrough", "Default");
            result = SprayTargetResult.NoHit;
            if (!SprayPaintRaycast.TryHit(origin, direction, 2.4f, mask, heldTool, out var hit)) return true;
            targetCollider = hit.collider;

            var panel = hit.collider.GetComponent<Vehicle_PaintablePanel>();
            if (panel == null)
            {
                var part = hit.collider.GetComponent<Generated_Part>();
                if (part != null && part.carPartSocket != null && part.carPartSocket.isInstalled && part.carPartSocket.partInstalled != null)
                    panel = part.carPartSocket.partInstalled.GetComponent<Vehicle_PaintablePanel>();
            }
            result = SprayTargetResult.NotPaintable;
            if (panel == null) return true;
            result = SprayTargetResult.Unregistered;
            if (!TryCanonicalBinding(panel, out var binding) || binding.Root == null) return true;
            result = SprayTargetResult.Inactive;
            if (!binding.Root.gameObject.activeInHierarchy) return true;
            paintPanel = panel;
            if (!paintPanel.gameObject.activeInHierarchy) { paintPanel = null; return true; }
            id = binding.Id;
            result = SprayTargetResult.Found;
            return true;
        }

        private void WarnSprayRejected(uint peer, SprayPaintCommand command, string reason) =>
            WarnSprayRejected(peer, command.Tool, command.Generation,
                new Vector3(command.CameraX, command.CameraY, command.CameraZ), command.CameraYaw, command.CameraPitch,
                command.Action.ToString(), reason);

        private void WarnSprayRejected(uint peer, ActiveSpray active, string reason) =>
            WarnSprayRejected(peer, active.ToolId, active.Generation, active.CameraOrigin,
                active.CameraYaw, active.CameraPitch, "Tick", reason);

        private void WarnSprayRejected(uint peer, ulong tool, uint generation, Vector3 origin, float yaw, float pitch,
            string stage, string reason)
        {
            float now = Time.unscaledTime;
            if (nextSprayTargetWarning.TryGetValue(peer, out float next) && now < next) return;
            nextSprayTargetWarning[peer] = now + 10f;
            bool observed = presence.TryGetValue(peer, out var player);
            string pose = observed ? new Vector3(player.Pose.X, player.Pose.Y, player.Pose.Z) +
                ";poseAge=" + (now - player.Time) + ";state=" + player.Pose.State +
                ";poseYaw=" + player.Pose.Yaw + ";posePitch=" + player.Pose.Pitch : "missing";
            bool lease = items.HostHasLeasedTool(peer, tool, generation, typeof(Tool_SprayPaintCan));
            bool range = items.HostItemInRange(peer, tool, 4.5f, out string rangeDetails);
            var can = items.ItemOf(tool) as Tool_SprayPaintCan;
            LogSprayWarning("MonMP spray rejected: peer=" + peer + ";stage=" + stage + ";reason=" + reason +
                ";tool=" + tool + ";generation=" + generation + ";lease=" + lease + ";inRange=" + range +
                ";rangeDetails=" + rangeDetails + ";canLevel=" + (can == null ? "missing" : can.Value1.ToString(CultureInfo.InvariantCulture)) +
                ";presentedTool=" + (can == null ? "missing" : can.transform.position.ToString()) +
                (reason == "settings" && can != null ? ";settings=" + SpraySettingsDetails(can) : "") +
                ";pose=" + pose + ";origin=" + origin + ";yaw=" + yaw + ";pitch=" + pitch);
        }

        private void WarnMissingSprayTarget(uint peer, ActiveSpray active, SprayTargetResult result, Collider? targetCollider)
        {
            float now = Time.unscaledTime;
            if (now - active.StartedAt < 1f || nextSprayTargetWarning.TryGetValue(peer, out float next) && now < next) return;
            // One line per peer per ten seconds, only for sustained spraying
            // without a target. Ordinary spraying into air remains permitted.
            nextSprayTargetWarning[peer] = now + 10f;
            string hit = targetCollider == null ? "none" : RuntimeProbe.CanonicalPath(targetCollider.transform) +
                ";layer=" + targetCollider.gameObject.layer + ";trigger=" + targetCollider.isTrigger;
            LogSprayWarning("MonMP spray paint has no host target: peer=" + peer + ";reason=" + result +
                ";origin=" + active.CameraOrigin + ";yaw=" + active.CameraYaw + ";pitch=" + active.CameraPitch +
                ";globalTriggers=" + Physics.queriesHitTriggers + ";paintTriggers=Collide;hit=" + hit);
        }

        private static void LogSprayWarning(string message)
        {
            // Player configs may omit Unity messages from BepInEx/LogOutput.log.
            // Keep these bounded diagnostics on the mod's own log source.
            var plugin = MonMPPlugin.Instance;
            if (plugin != null) plugin.LogSprayWarning(message);
            else Debug.LogWarning(message);
        }

        private bool TryCanonicalBinding(Vehicle_PaintablePanel panel, out Binding binding, bool forceRegistration = false)
        {
            Vehicle_PaintablePanel root;
            try { root = RootOf(panel); }
            catch (InvalidOperationException) { binding = null!; return false; }
            // Native camper-shell colliders change ParentPanel on installation/removal.
            // Their construction-time alias entry cannot identify the live save owner.
            if (host && forceRegistration && !byPanel.ContainsKey(root)) RefreshDynamic(true);
            if (!byPanel.TryGetValue(root, out var found)) { binding = null!; return false; }
            binding = found;
            return true;
        }

        private bool TryResolveLocalRepairPanel(Camera camera, out Binding binding, out Integration.ToolAimSample aim)
        {
            aim = Integration.LocalInteraction.SampleTool(camera);
            return TryResolveRepairPanel(aim.Ray, out binding);
        }

        private bool TryResolveRepairPanel(float x, float y, float z, float yaw, float pitch, out ulong id, out Vehicle_PaintablePanel repairPanel)
        {
            id = 0;
            repairPanel = null!;
            var direction = Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;
            if (!TryResolveRepairPanel(new Ray(new Vector3(x, y, z), direction), out var binding, out repairPanel)) return false;
            id = binding.Id;
            return true;
        }

        private bool TryResolveRepairPanel(Ray ray, out Binding binding) =>
            TryResolveRepairPanel(ray, out binding, out _);

        private bool TryResolveRepairPanel(Ray ray, out Binding binding, out Vehicle_PaintablePanel repairPanel)
        {
            repairPanel = null!;
            int mask = LayerMask.GetMask("MainVehicleTrigger", "InteractableItem", "PartSocket", "VehiclePlayerCantGoThrough");
            if (!Physics.Raycast(ray, out var hit, 2.4f, mask))
            { binding = null!; return false; }

            var panel = hit.collider.GetComponent<Vehicle_PaintablePanel>();
            if (panel == null)
            {
                var part = hit.collider.GetComponent<Generated_Part>();
                if (part != null && part.carPartSocket != null && part.carPartSocket.isInstalled && part.carPartSocket.partInstalled != null)
                    panel = part.carPartSocket.partInstalled.GetComponent<Vehicle_PaintablePanel>();
            }
            if (panel == null || !TryCanonicalBinding(panel, out var found) || found.Root == null ||
                !found.Root.gameObject.activeInHierarchy)
            { binding = null!; return false; }
            // Native methods redirect ParentPanel themselves; associated accessories
            // remain independent targets within the enclosing save record.
            repairPanel = panel;
            if (!repairPanel.gameObject.activeInHierarchy)
            { binding = null!; repairPanel = null!; return false; }
            binding = found;
            return true;
        }

        private bool TakeSprayBudget(uint peer)
        {
            float now = Time.unscaledTime;
            var state = sprayBudgets.TryGetValue(peer, out var current) ? current : (Tokens: 12f, Time: now);
            state.Tokens = Math.Min(12, state.Tokens + Math.Max(0, now - state.Time) * 12);
            state.Time = now;
            if (state.Tokens < 1) { sprayBudgets[peer] = state; return false; }
            state.Tokens--; sprayBudgets[peer] = state; return true;
        }

        private void TickSprays()
        {
            // A spray can modify an associated panel as well as its canonical root.
            if (activeSprays.Count != 0) updateCaptures.Clear();
            float now = Time.unscaledTime;
            float delta = Mathf.Clamp(Time.deltaTime, 0, SprayMaxStep);
            foreach (var pair in activeSprays.ToArray())
            {
                var active = pair.Value;
                if (active.Tool == null)
                { WarnSprayRejected(pair.Key, active, "can-empty-or-missing"); activeSprays.Remove(pair.Key); continue; }
                string? reason = null;
                if (now - active.LastCommand > SprayCommandTimeout) reason = "command-timeout";
                else if (NewActionsBlocked) reason = "save-or-not-ready";
                else if (!items.HostHasLeasedTool(pair.Key, active.ToolId, active.Generation, typeof(Tool_SprayPaintCan))) reason = "lease";
                else if (!items.HostItemInRange(pair.Key, active.ToolId, 4.5f)) reason = "range";
                else if (active.Tool.Value1 <= 0) reason = "can-empty-or-missing";
                if (reason != null)
                { WarnSprayRejected(pair.Key, active, reason); activeSprays.Remove(pair.Key); continue; }
                // A paused/zero-delta frame must not permanently cancel a valid
                // held spray; no paint or can consumption occurs on that frame.
                if (delta <= 0) continue;

                if (!TryResolveSprayTarget(pair.Key, active.CameraOrigin, active.CameraYaw, active.CameraPitch,
                    out active.PanelId, out var panel, out var targetCollider, out var targetResult, active.Tool.transform, active.IndependentAim))
                { WarnSprayRejected(pair.Key, active, "camera-or-player-pose"); activeSprays.Remove(pair.Key); continue; }
                if (panel == null) WarnMissingSprayTarget(pair.Key, active, targetResult, targetCollider);
                bool changed;
                try
                {
                    changed = items.HostMutateLeasedTool(pair.Key, active.ToolId, active.Generation,
                        typeof(Tool_SprayPaintCan), native =>
                        {
                            var can = (Tool_SprayPaintCan)native;
                            if (can.Value1 <= 0) { SetSprayCanDepleted(can); return false; }
                            if (panel != null)
                                panel.Paint(active.Color, active.Smoothness * Time.deltaTime * 0.3f,
                                    active.Metalness * Time.deltaTime * 0.3f, active.OverrideColor);
                            can.Value1 = Mathf.Max(0, can.Value1 - 0.02f * delta);
                            if (can.Value1 <= 0) SetSprayCanDepleted(can);
                            return true;
                        }, publish: false);
                }
                catch (Exception ex)
                {
                    WarnSprayRejected(pair.Key, active, "native-exception-" + ex.GetType().Name + ": " + ex.Message);
                    Debug.LogException(ex); activeSprays.Remove(pair.Key); continue;
                }
                if (!changed) WarnSprayRejected(pair.Key, active, "native-tool-mutation-refused");
                if (!changed || active.Tool.Value1 <= 0) activeSprays.Remove(pair.Key);
            }
        }

        private static bool ReadSpraySettings(Tool_SprayPaintCan can, out Vector3 color,
            out float smoothness, out float metalness, out bool overrideColor)
        {
            color = default; smoothness = metalness = 0; overrideColor = false;
            smoothness = (float)SpraySmoothnessField.GetValue(can);
            metalness = (float)SprayMetalnessField.GetValue(can);
            overrideColor = (bool)SprayOverrideField.GetValue(can);
            if (!Finite(smoothness) || !Finite(metalness) || Mathf.Abs(smoothness) > 1 || Mathf.Abs(metalness) > 1)
                return false;
            if (can.IntValue1 >= 1000000000)
            {
                string digits = can.IntValue1.ToString(CultureInfo.InvariantCulture);
                if (digits.Length != 10) return false;
                byte red = unchecked((byte)int.Parse(digits.Substring(1, 3), CultureInfo.InvariantCulture));
                byte green = unchecked((byte)int.Parse(digits.Substring(4, 3), CultureInfo.InvariantCulture));
                byte blue = unchecked((byte)int.Parse(digits.Substring(7, 3), CultureInfo.InvariantCulture));
                Color custom = new Color32(red, green, blue, byte.MaxValue);
                color = new Vector3(custom.r, custom.g, custom.b);
            }
            else color = (Vector3)SprayColorField.GetValue(can);
            // Stock cans use signed RGB targets (Red = 1,-1,-1; Black = -1,-1,-1).
            // Native Paint moves toward these targets and clamps the resulting material color.
            return Finite(color.x) && Finite(color.y) && Finite(color.z) &&
                color.x >= -1 && color.x <= 1 && color.y >= -1 && color.y <= 1 && color.z >= -1 && color.z <= 1;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static string SpraySettingsDetails(Tool_SprayPaintCan can)
        {
            var color = (Vector3)SprayColorField.GetValue(can);
            return string.Format(CultureInfo.InvariantCulture,
                "color=({0:R},{1:R},{2:R});smoothness={3:R};metalness={4:R};override={5};custom={6}",
                color.x, color.y, color.z, SpraySmoothnessField.GetValue(can), SprayMetalnessField.GetValue(can),
                SprayOverrideField.GetValue(can), can.IntValue1);
        }

        internal static void SetSprayCanDepleted(Tool_SprayPaintCan can)
        {
            if (can == null || can.Value1 > 0) return;
            var button = SprayButtonField.GetValue(can) as GameObject;
            if (button != null && button.activeSelf) button.SetActive(false);
        }

        private bool PaintOnlyChange(PaintPanelState before, PaintPanelState after)
        {
            if (before.IsRusted != after.IsRusted || before.ForceRust != after.ForceRust ||
                Math.Abs(before.DentScale - after.DentScale) > 0.0001f ||
                Math.Abs(before.Red - after.Red) > MaxAcceptedPaintStep ||
                Math.Abs(before.Green - after.Green) > MaxAcceptedPaintStep ||
                Math.Abs(before.Blue - after.Blue) > MaxAcceptedPaintStep ||
                Math.Abs(before.Smoothness - after.Smoothness) > MaxAcceptedPaintStep ||
                Math.Abs(before.Metalness - after.Metalness) > MaxAcceptedPaintStep)
                return false;
            if (before.Associated == null || after.Associated == null) return before.Associated == after.Associated;
            return PaintOnlyChange(before.Associated, after.Associated);
        }

        private bool TakeBudget(uint peer)
        {
            float now = Time.unscaledTime;
            var state = budgets.TryGetValue(peer, out var current) ? current : (Tokens: 8f, Time: now);
            state.Tokens = Math.Min(8, state.Tokens + Math.Max(0, now - state.Time) * 8);
            state.Time = now;
            if (state.Tokens < 1) { budgets[peer] = state; return false; }
            state.Tokens -= 1;
            budgets[peer] = state;
            return true;
        }

        private bool PeerInRange(uint peer, Binding binding)
        {
            if (!binding.Root.gameObject.activeInHierarchy || peer == 0 || !presence.TryGetValue(peer, out var observed) ||
                Time.unscaledTime - observed.Time > 2 || observed.Pose.State != (byte)PlayerState.Walking) return false;
            float dx = observed.Pose.X - binding.Root.transform.position.x;
            float dy = observed.Pose.Y - binding.Root.transform.position.y;
            float dz = observed.Pose.Z - binding.Root.transform.position.z;
            return dx * dx + dy * dy + dz * dz <= MaxRange * MaxRange;
        }

        private void Reply(uint peer, ulong entity, Guid operation, ReplyStatus status)
        {
            if (channel == null || session == null || !session.Peers.ContainsKey(peer)) return;
            channel.Reply(peer, entity, new PaintPanelReply(operation, (byte)status).Encode());
        }

        private void Stale(uint peer, ulong id, byte[] payload)
        {
            if (!host) return;
            if (DentRepairToolCommand.HasMarker(payload))
            {
                try { Reply(peer, id, DentRepairToolCommand.Decode(payload).Operation, ReplyStatus.Rejected); }
                catch (InvalidDataException) { }
                return;
            }
            if (RustRepairToolCommand.HasMarker(payload))
            {
                try { Reply(peer, id, RustRepairToolCommand.Decode(payload).Operation, ReplyStatus.Rejected); }
                catch (InvalidDataException) { }
                return;
            }
            if (RustSolutionToolCommand.HasMarker(payload))
            {
                try { Reply(peer, id, RustSolutionToolCommand.Decode(payload).Operation, ReplyStatus.Rejected); }
                catch (InvalidDataException) { }
                return;
            }
            if (!bindings.ContainsKey(id)) return;
            try { Reply(peer, id, PaintPanelRequest.Decode(payload).Operation, ReplyStatus.Rejected); }
            catch (InvalidDataException) { }
        }

        private void Reply(ulong id, byte[] payload)
        {
            if (host) return;
            PaintPanelReply reply;
            try { reply = PaintPanelReply.Decode(payload); }
            catch (InvalidDataException) { return; }
            if (!pending.Remove(reply.Operation, out ulong requestedId) || requestedId != id ||
                !bindings.TryGetValue(id, out var binding) || binding.InFlightOperation != reply.Operation) return;
            binding.InFlightOperation = Guid.Empty;
            if (pendingRepairCues.Remove(reply.Operation, out byte cue) && reply.Status == (byte)ReplyStatus.Applied)
                RepairToolPresentation.Local(cue);
            PaintPanelState? retry = binding.DirtyState;
            bool retryDirty = binding.Dirty;
            if (reply.Status == (byte)ReplyStatus.Rejected || reply.Status == (byte)ReplyStatus.Capacity)
            {
                binding.Dirty = false;
                binding.DirtyState = null;
                Apply(binding.Root, binding.Current);
                return;
            }
            if (retryDirty && retry != null)
            {
                Apply(binding.Root, retry);
                binding.Dirty = true;
                binding.DirtyState = retry;
            }
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values.Where(record => record.Adapter == Adapter ||
                record.Adapter == DynamicPaintPanelState.Adapter)) ApplyRecord(record);
            ResolveDynamic();
            foreach (var record in records.Values.Where(record => record.Adapter == PurchasedVehiclePaintState.Adapter)) ApplyRecord(record);
            ResolvePurchasedPaint();
        }

        private void Changed(WorldRecord record)
        {
            if (!host && (record.Adapter == Adapter || record.Adapter == DynamicPaintPanelState.Adapter ||
                record.Adapter == PurchasedVehiclePaintState.Adapter)) ApplyRecord(record);
        }

        private void ApplyRecord(WorldRecord record)
        {
            if (record.Adapter == PurchasedVehiclePaintState.Adapter)
            {
                var purchased = PurchasedVehiclePaintState.Decode(record.Payload);
                if (PurchasedVehiclePaintState.RecordId(purchased.VehicleEntity, purchased.RelativePanelPath) != record.Id)
                    throw new InvalidDataException("Purchased paint record names a different sale panel.");
                string identity = purchased.VehicleEntity.ToString("X16", CultureInfo.InvariantCulture) + "/" + purchased.RelativePanelPath;
                if ((purchasedIdentities.TryGetValue(record.Id, out var known) && !string.Equals(known, identity, StringComparison.Ordinal)) ||
                    bindings.TryGetValue(record.Id, out var bound) && !bound.PurchasedVehicle)
                    throw new InvalidDataException("Purchased paint record collides with another paint identity.");
                purchasedIdentities[record.Id] = identity;
                purchasedRecords[record.Id] = purchased;
                purchasedDirty = true;
                return;
            }
            if (record.Adapter == DynamicPaintPanelState.Adapter)
            {
                var dynamic = DynamicPaintPanelState.Decode(record.Payload);
                if (DynamicPaintPanelState.RecordId(dynamic.OwnerKind, dynamic.OwnerId) != record.Id)
                    throw new InvalidDataException("Dynamic paint record names a different owner.");
                dynamicRecords[record.Id] = dynamic;
                dynamicDirty = true;
                return;
            }
            if (!bindings.TryGetValue(record.Id, out var binding) || record.Adapter != Adapter)
                throw new InvalidDataException("Unknown paint panel record.");
            var incoming = PaintPanelState.Decode(record.Payload);
            PaintPanelState? predicted = binding.Dirty ? binding.DirtyState : null;
            Apply(binding.Root, incoming);
            binding.Current = incoming;
            if (predicted != null && PaintOnlyChange(incoming, predicted))
            {
                Apply(binding.Root, predicted);
                binding.Dirty = true;
                binding.DirtyState = predicted;
            }
            else if (predicted != null)
            {
                binding.Dirty = false;
                binding.DirtyState = null;
            }
        }

        internal void OnNativePaint(Vehicle_PaintablePanel panel)
        {
            SeatGlowPaint.Changed(panel);
            updateCaptures.Clear();
            if (disposed || session?.IsRunning != true || !TryCanonicalBinding(panel, out var binding)) return;
            var current = Capture(binding.Root);
            if (host)
            {
                // Host-side polling publishes through one bounded cadence below.
                return;
            }
            if (channel?.FullySynchronized != true || NewActionsBlocked) return;
            if (!Same(current, binding.Current)) { binding.DirtyState = current; binding.Dirty = true; }
        }

        public void Update()
        {
            updateCapturePass = host;
            try
            {
                if (host)
                {
                    RefreshDynamic();
                    RefreshPurchasedPaint();
                }
                else if (dynamicDirty || Time.unscaledTime >= nextDynamicResolve)
                {
                    // Record changes resolve on the next frame; part installs are picked up within 0.25 s.
                    dynamicDirty = false; nextDynamicResolve = Time.unscaledTime + 0.25f;
                    ResolveDynamic();
                }
                if (!host && (purchasedDirty || Time.unscaledTime >= nextPurchasedResolve))
                {
                    purchasedDirty = false;
                    nextPurchasedResolve = Time.unscaledTime + 0.25f;
                    ResolvePurchasedPaint();
                }
                if (!Ready) return;
                float now = Time.unscaledTime;
                if (host)
                {
                    TickSprays();
                    if (now < nextCapture) return;
                    // Full rate while a guest sprays; otherwise the host's own paint work settles at 4 Hz.
                    nextCapture = now + (activeSprays.Count != 0 ? CaptureInterval : IdleCaptureInterval);
                    foreach (var binding in bindings.Values)
                    {
                        var current = CaptureForUpdate(binding.Root);
                        if (!Same(current, binding.Current))
                        {
                            binding.Current = current;
                            channel!.Publish(binding.Id, Payload(binding, current));
                        }
                    }
                    return;
                }

                UpdateClientSpray(now);

                if (now < nextGuestSend) return;
                nextGuestSend = now + CaptureInterval;
                foreach (var binding in bindings.Values) SendPaint(binding);
            }
            finally
            {
                updateCapturePass = false;
                updateCaptures.Clear();
            }
        }

        private void UpdateClientSpray(float now)
        {
            var spray = clientSpray;
            if (spray == null) return;
            if (spray.Tool == null || !spray.Tool.isEquiped ||
                spray.Tool.Value1 <= 0 || !items.TryGetLocalToolLease(spray.Tool, out ulong id, out uint generation) ||
                id != spray.ToolId || generation != spray.Generation)
            { StopClientSpray(send: true); return; }
            if (now < spray.NextUpdate) return;
            spray.Sequence++;
            if (!SendClientSpray(spray, SprayPaintAction.Update))
            {
                spray.Sequence--;
                StopClientSpray(send: true);
                return;
            }
            spray.NextUpdate = now + SprayCommandInterval;
        }

        private bool Ready => !disposed && session?.IsRunning == true && channel?.Ready == true;

        private byte[] Payload(Binding binding, PaintPanelState state)
        {
            if (binding.PurchasedVehicle)
            {
                var purchased = new PurchasedVehiclePaintState(binding.PurchasedVehicleEntity, binding.RelativePanelPath, state);
                purchasedRecords[binding.Id] = purchased;
                return purchased.Encode();
            }
            if (!binding.Dynamic) return state.Encode();
            var record = new DynamicPaintPanelState(binding.OwnerKind, binding.OwnerId, binding.Prefab, state);
            dynamicRecords[binding.Id] = record;
            return record.Encode();
        }

        private static Vehicle_PaintablePanel RootOf(Vehicle_PaintablePanel panel)
        {
            var seen = new HashSet<Vehicle_PaintablePanel>();
            var current = panel;
            while (true)
            {
                if (!seen.Add(current)) throw new InvalidOperationException("Native paint panel parent cycle at " + RuntimeProbe.CanonicalPath(panel.transform));
                var parent = ParentPanelField.GetValue(current) as Vehicle_PaintablePanel;
                if (parent == null || parent == current) return current;
                current = parent;
            }
        }

        private static readonly System.Reflection.FieldInfo CantDentField =
            HarmonyLib.AccessTools.Field(typeof(Vehicle_PaintablePanel), "CantBeDent") ??
            throw new InvalidOperationException("Unsupported Vehicle_PaintablePanel dent eligibility field.");

        private static readonly System.Reflection.FieldInfo ParentPanelField =
            HarmonyLib.AccessTools.Field(typeof(Vehicle_PaintablePanel), "ParentPanel") ??
            throw new InvalidOperationException("Unsupported Vehicle_PaintablePanel parent field.");

        // Every panel is captured several times a second; reuse the cycle guard instead of allocating one each time.
        private readonly HashSet<Vehicle_PaintablePanel> captureSeen = new HashSet<Vehicle_PaintablePanel>();
        private PaintPanelState CaptureForUpdate(Vehicle_PaintablePanel panel)
        {
            if (!updateCapturePass) return Capture(panel);
            if (!updateCaptures.TryGetValue(panel, out var state))
            {
                state = Capture(panel);
                updateCaptures.Add(panel, state);
            }
            return state;
        }

        private PaintPanelState Capture(Vehicle_PaintablePanel panel)
        {
            captureSeen.Clear();
            return Capture(panel, 0, captureSeen);
        }

        private PaintPanelState Capture(Vehicle_PaintablePanel panel, int depth, HashSet<Vehicle_PaintablePanel> seen)
        {
            if (depth >= PaintPanelState.MaxDepth || !seen.Add(panel)) throw new InvalidDataException("Native associated paint panel graph is cyclic or too deep.");
            var color = panel.Get_ActualColor;
            PaintPanelState? associated = panel.PannelAssociatedToSave == null ? null : Capture(panel.PannelAssociatedToSave, depth + 1, seen);
            seen.Remove(panel);
            bool isRusted = panel.Get_IsRusted, forceRust = panel.Get_ForceRust;
            float dentScale = panel.Get_ActualDantScale, smoothness = panel.Get_ActualSmoothness, metalness = panel.Get_ActualMetalness;
            string? invalidField = InvalidField(color.r, color.g, color.b, dentScale, smoothness, metalness);
            if (invalidField != null)
            {
                string path = RuntimeProbe.CanonicalPath(panel.transform);
                string details = "field=" + invalidField + "; red=" + Format(color.r) + "; green=" + Format(color.g) +
                    "; blue=" + Format(color.b) + "; dentScale=" + Format(dentScale) + "; smoothness=" + Format(smoothness) +
                    "; metalness=" + Format(metalness);
                if (diagnosedInvalidPanels.Add(path))

#if MONMP_DEVELOPMENT
                    File.AppendAllText(invalidDiagnosticsPath, path + "\t" + panel.Get_PanelType + "\t" + invalidField + "\t" +
                        isRusted + "\t" + forceRust + "\t" + Format(color.r) + "\t" + Format(color.g) + "\t" + Format(color.b) + "\t" +
                        Format(dentScale) + "\t" + Format(smoothness) + "\t" + Format(metalness) + "\t" + details.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') + "\n");
#else
{}
#endif

                throw new InvalidDataException("Invalid native paint panel at " + path + ": " + details);
            }
            return new PaintPanelState(isRusted, forceRust, color.r, color.g, color.b,
                dentScale, smoothness, metalness, associated);
        }

        private static string? InvalidField(float red, float green, float blue, float dentScale, float smoothness, float metalness)
        {
            if (!ValidNativeValue(red, 1f)) return "red";
            if (!ValidNativeValue(green, 1f)) return "green";
            if (!ValidNativeValue(blue, 1f)) return "blue";
            if (!ValidNativeValue(dentScale, PaintPanelState.MaxNativeDentScale)) return "dentScale";
            if (!ValidNativeValue(smoothness, 1f)) return "smoothness";
            if (!ValidNativeValue(metalness, 1f)) return "metalness";
            return null;
        }

        private static bool ValidNativeValue(float value, float bound) => ItemPose.FiniteValue(value, bound) && value >= 0f;
        private static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static bool SameShape(Vehicle_PaintablePanel panel, PaintPanelState state) =>
            (panel.PannelAssociatedToSave == null) == (state.Associated == null) &&
            (state.Associated == null || SameShape(panel.PannelAssociatedToSave!, state.Associated));

        private static void Apply(Vehicle_PaintablePanel panel, PaintPanelState state)
        {
            if (!SameShape(panel, state)) throw new InvalidDataException("Paint panel association graph differs from the host snapshot.");
            Apply(panel, state, 0);
        }

        private static void Apply(Vehicle_PaintablePanel panel, PaintPanelState state, int depth)
        {
            if (depth >= PaintPanelState.MaxDepth) throw new InvalidDataException("Paint panel association graph is too deep.");
            var data = new PaintPannelData("Chrome")
            {
                PAINT_IsRusted = state.IsRusted,
                PAINT_ForceRust = state.ForceRust,
                PaintColor = new[] { state.Red, state.Green, state.Blue },
                PaintDetails = new[] { state.DentScale, state.Smoothness, state.Metalness },
                PannelAssociatedToSave = state.Associated == null ? null : MakeData(state.Associated)
            };
            panel.LoadSaveSystem(data);
            SeatGlowPaint.Changed(panel);
        }

        private static PaintPannelData MakeData(PaintPanelState state)
        {
            var data = new PaintPannelData("Chrome")
            {
                PAINT_IsRusted = state.IsRusted,
                PAINT_ForceRust = state.ForceRust,
                PaintColor = new[] { state.Red, state.Green, state.Blue },
                PaintDetails = new[] { state.DentScale, state.Smoothness, state.Metalness },
                PannelAssociatedToSave = state.Associated == null ? null : MakeData(state.Associated)
            };
            return data;
        }

        private static bool Same(PaintPanelState a, PaintPanelState b) => a.SameState(b);

        public void Dispose()
        {
            if (disposed) return;
            StopClientSpray(send: false);
            activeSprays.Clear(); spraySequences.Clear(); nextSprayTargetWarning.Clear();
            updateCapturePass = false; updateCaptures.Clear();
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null)
            {
                channel.CommandRequested -= Command;
                channel.CommandRejected -= Stale;
                channel.ReplyReceived -= Reply;
                channel.SnapshotApplied -= Snapshot;
                channel.Changed -= Changed;
            }
            if (!host)
            {
                foreach (var binding in bindings.Values)
                    if (binding.Root != null) Apply(binding.Root, binding.Original);
                foreach (var pair in dynamicRollback)
                    if (pair.Key != null) Apply(pair.Key, pair.Value);
            }
            presence.Clear(); budgets.Clear(); pending.Clear(); pendingRepairCues.Clear();
            rootCache.Clear(); retiredRootOwners.Clear();
            purchasedVehicles.Clear(); purchasedRecords.Clear(); purchasedIdentities.Clear(); session = null; channel = null;
        }
    }

    internal sealed class PaintPanelRequest
    {
        public Guid Operation { get; }
        public byte Kind { get; }
        public bool DoDamage { get; }
        public PaintPanelState? State { get; }

        public PaintPanelRequest(Guid operation, byte kind, bool doDamage, PaintPanelState? state)
        {
            Operation = operation;
            Kind = kind;
            DoDamage = doDamage;
            State = state;
            if (operation == Guid.Empty || Kind < 1 || Kind > 5 || (Kind == 1) != (state != null) || doDamage)
                throw new InvalidDataException("Invalid paint panel request.");
        }

        public byte[] Encode()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)0xD8);
            writer.Write(Operation.ToByteArray());
            writer.Write(Kind);
            writer.Write((byte)(DoDamage ? 1 : 0));
            var state = State?.Encode() ?? Array.Empty<byte>();
            writer.Write((byte)state.Length);
            writer.Write(state);
            if (stream.Length > 128) throw new InvalidDataException("Paint panel command is too large.");
            return stream.ToArray();
        }

        public static PaintPanelRequest Decode(byte[] payload)
        {
            if (payload == null || payload.Length < 20 || payload.Length > 128) throw new InvalidDataException("Invalid paint panel command size.");
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadByte() != 0xD8) throw new InvalidDataException("Invalid paint panel command marker.");
            var operation = new Guid(reader.ReadBytes(16));
            byte kind = reader.ReadByte(), damage = reader.ReadByte(), length = reader.ReadByte();
            if (damage > 1 || length != stream.Length - stream.Position) throw new InvalidDataException("Invalid paint panel command fields.");
            PaintPanelState? state = length == 0 ? null : PaintPanelState.Decode(reader.ReadBytes(length));
            return new PaintPanelRequest(operation, kind, damage != 0, state);
        }
    }

    internal sealed class PaintPanelReply
    {
        private const byte Marker = 0xD9;
        public Guid Operation { get; }
        public byte Status { get; }

        public PaintPanelReply(Guid operation, byte status)
        {
            if (operation == Guid.Empty || status > 4) throw new InvalidDataException("Invalid paint panel reply.");
            Operation = operation; Status = status;
        }
        public byte[] Encode()
        {
            byte[] result = new byte[18]; result[0] = Marker; Array.Copy(Operation.ToByteArray(), 0, result, 1, 16); result[17] = Status; return result;
        }
        public static PaintPanelReply Decode(byte[] payload)
        {
            if (payload == null || payload.Length != 18 || payload[0] != Marker) throw new InvalidDataException("Invalid paint panel reply size or marker.");
            return new PaintPanelReply(new Guid(payload.Skip(1).Take(16).ToArray()), payload[17]);
        }
    }
}
