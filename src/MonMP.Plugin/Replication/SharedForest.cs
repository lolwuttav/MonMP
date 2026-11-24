using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // The host's forest: every standing tree and felled trunk. A saved game replaces the scene's
    // default trees with its own (cut trees gone, grown trees added, random sizes), while a menu-join
    // guest starts from the scene defaults. On catch-up the guest keeps each local tree that matches
    // a host tree by prefab and position, removes the rest and spawns what is missing. Felling and
    // splitting run natively on the host; guests follow tombstones and kinematic trunk replicas, and
    // the resulting logs arrive as shared items. Saplings grown from planted cuttings are records too:
    // only the host runs the daily growth and cuttings regrowth; guest pruning, planting, fertilizing
    // and fertilizing are predicted locally. Destructive sapling cuts and paid cutting placements
    // wait for host acceptance; native reward factories run only on the accepting host.
    internal sealed class SharedForest : IDisposable
    {
        private sealed class Binding
        {
            public ulong Id;
            public ForestKind Kind;
            public TreeBase? Tree;
            public TreeTrunk? Trunk;
            public Tree_Growing? Sapling;
            public GameObject? Go;
            public ForestState State;
            public Vector3 Target;
            public Quaternion TargetRotation;
            public float LastCut;
            public bool RetireAfterOutputs;
        }
        private static readonly FieldInfo TreeLeaves = AccessTools.Field(typeof(TreeBase), "Leaves");
        private static readonly FieldInfo TreeCuttings = AccessTools.Field(typeof(TreeBase), "HasCuttings");
        private static readonly FieldInfo TrunkSegments = AccessTools.Field(typeof(TreeTrunk), "tree_Trunks");
        private static readonly FieldInfo TrunkTrash = AccessTools.Field(typeof(TreeTrunk), "tree_Trash");
        private static readonly FieldInfo SaplingType = AccessTools.Field(typeof(Tree_Growing), "tree_Type");
        private static readonly FieldInfo SaplingFertilized = AccessTools.Field(typeof(Tree_Growing), "isFertilized");
        private static readonly FieldInfo SaplingTicks = AccessTools.Field(typeof(Tree_Growing), "TickSincePlanted");
        private static readonly FieldInfo SaplingSize = AccessTools.Field(typeof(Tree_Growing), "FinalSize");
        private static readonly FieldInfo CuttingPreview = AccessTools.Field(typeof(Garden_TreeCuttings), "isTempPlaced");
        private static readonly MethodInfo CuttingCanPlace = AccessTools.Method(typeof(Garden_TreeCuttings), "CanPlaceItHere");
        private static readonly MethodInfo CuttingTexture = AccessTools.Method(typeof(Garden_TreeCuttings), "GetmainTexture");
        private static readonly MethodInfo SaplingVisual = AccessTools.Method(typeof(Tree_Growing), "UpdateVisual");
        private static readonly MethodInfo SaplingNewDay = AccessTools.Method(typeof(Tree_Growing), "NewDay");
        private static readonly MethodInfo TreeNewDay = AccessTools.Method(typeof(TreeBase), "NewDayEvent");
        private const string SaplingPrefab = "Sapling";
        private const float MatchDistance = 0.05f, Cell = 0.5f;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly BindingWorkSet<Binding> captureBindings = new BindingWorkSet<Binding>();
        private readonly BindingWorkSet<Binding> retiredBindings = new BindingWorkSet<Binding>();
        private readonly Dictionary<TreeBase, Binding> byTree = new Dictionary<TreeBase, Binding>();
        private readonly Dictionary<TreeTrunk, Binding> byTrunk = new Dictionary<TreeTrunk, Binding>();
        private readonly Dictionary<Tree_Growing, Binding> bySapling = new Dictionary<Tree_Growing, Binding>();
        private readonly Dictionary<ulong, (uint Request, float Until)> pendingPlants = new Dictionary<ulong, (uint, float)>();
        private uint nextPlantRequest;
        private readonly Dictionary<ulong, float> pendingFertilizers = new Dictionary<ulong, float>();
        // A split trunk's segments become stand-in logs on the guest at once, as the native split turns
        // them into logs on the host. Each falls and settles like a log, and gives way individually when
        // the host's log for that piece spawns here: the nearest stand-in to a new shared log (#107).
        private sealed class StandIn { public GameObject Part = null!; public float Since, HiddenAt = -1f; }
        private readonly List<StandIn> standIns = new List<StandIn>();
        private const float StandInMatch = 3f, StandInTimeout = 8f;

#if MONMP_DEVELOPMENT
        internal int TestVisibleLogHandoffs { get; private set; }


        internal int TestStandInTimeouts { get; private set; }
        internal int TestStandInLogs => standIns.Count(s => s.HiddenAt < 0 && s.Part != null);
#endif

        private readonly Dictionary<uint, float> lastPlant = new Dictionary<uint, float>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly bool host;
        private readonly string diagnostics;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, disposed;
        private Tool_PruningLoppers? equippedLoppers;
        private ulong nextId = ForestState.IdBase;
        private float nextCapture, nextDiscover;
        private bool periodicDiscoveryStarted;
        public int Count => bindings.Count;
        public bool Reconciled { get; private set; }
        public int Kept { get; private set; }
        public int Removed { get; private set; }
        public int Spawned { get; private set; }
        public int Missing { get; private set; }
        public int Adopted { get; private set; }
        private bool Ready => !disposed && session?.IsRunning == true && channel?.Ready == true;
        private static NativeReplacementScope? replacementOutputs;
        internal sealed class NativeReplacementScope : IDisposable
        {
            private readonly SharedForest owner;
            private readonly Component source;
            private readonly Type outputType;
            private readonly NativeReplacementScope? previous;
            private readonly List<Component> outputs = new List<Component>();
            private bool ended;
            internal NativeReplacementScope(SharedForest owner, Component source, Type outputType)
            {
                this.owner = owner; this.source = source; this.outputType = outputType;
                previous = replacementOutputs; replacementOutputs = this;
            }
            internal void Capture(Component native)
            {
                if (!ended && native != null && native.GetType() == outputType && outputs.Count < 4 && !outputs.Contains(native)) outputs.Add(native);
            }
            internal void Complete()
            {
                if (ended) return;
                Dispose();
                // The native call has finished sizing/detaching its replacement. An older queued
                // update for the source must not coalesce its later removal ahead of that child.
                bool registered = false;
                foreach (var native in outputs)
                {
                    owner.NativeReady(native);
                    if (native is TreeBase tree && owner.byTree.TryGetValue(tree, out var treeBinding) && treeBinding.State.Exists)
                        registered = true;
                    if (native is TreeTrunk trunk && owner.byTrunk.TryGetValue(trunk, out var trunkBinding) && trunkBinding.State.Exists)
                        registered = true;
                }
                if (registered)
                {
                    if (source is Tree_Growing sapling && owner.bySapling.TryGetValue(sapling, out var oldSapling)) oldSapling.RetireAfterOutputs = true;
                    if (source is TreeBase tree && owner.byTree.TryGetValue(tree, out var oldTree)) oldTree.RetireAfterOutputs = true;
                }
            }
            public void Dispose()
            {
                if (ended) return;
                ended = true;
                if (ReferenceEquals(replacementOutputs, this)) replacementOutputs = previous;
            }
        }
        internal NativeReplacementScope? BeginNativeGrowth(Tree_Growing sapling) => host && Ready ? new NativeReplacementScope(this, sapling, typeof(TreeBase)) : null;
        internal NativeReplacementScope? BeginNativeFell(TreeBase tree) => host && Ready ? new NativeReplacementScope(this, tree, typeof(TreeTrunk)) : null;
        internal static void ObserveGrowingTree(TreeBase tree) => replacementOutputs?.Capture(tree);
        public SharedForest(bool host, string diagnostics)
        {
            this.host = host; this.diagnostics = diagnostics;
            if (host) Discover();

#if MONMP_DEVELOPMENT
            Report();
#endif

        }

#if MONMP_DEVELOPMENT
        private void Report() => File.WriteAllText(Path.Combine(diagnostics, "shared-forest.txt"),
            "Role=" + (host ? "host" : "guest") + "\nTrees=" + bindings.Values.Count(b => b.Kind == ForestKind.Tree && b.State.Exists) +
            "\nTrunks=" + bindings.Values.Count(b => b.Kind == ForestKind.Trunk && b.State.Exists) + "\nSaplings=" + bindings.Values.Count(b => b.Kind == ForestKind.Sapling && b.State.Exists) + "\nRecords=" + bindings.Count +
            "\nReconciled=" + Reconciled + "\nKept=" + Kept + "\nRemoved=" + Removed + "\nSpawned=" + Spawned + "\nMissing=" + Missing);
#endif

        public IEnumerable<WorldRecord> RootRecords() { yield return ForestAction.RootRecord(); }
        public IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, ForestState.Adapter, b.State.Encode()));
        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.CommandRequested += Command; channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
            channel.ReplyReceived += PlantReply;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) { presence.Remove(peer); lastPlant.Remove(peer); }

        private static ForestState Capture(ForestKind kind, Component native, string prefab)
        {
            var t = native.transform; var p = t.position; var r = t.rotation; var s = t.localScale;
            var flags = ForestFlags.None; byte type = 0; int ticks = 0;
            if (native is TreeBase tree && tree.Get_HasCuttings) flags |= ForestFlags.Cuttings;
            if (native is Tree_Growing sapling)
            {
                s = sapling.Get_TreeSize; type = (byte)Mathf.Clamp(sapling.Get_Tree_Type, 0, 8); ticks = Mathf.Clamp(sapling.Get_TickSincePlanted, 0, ForestState.MaxTicks);
                if (sapling.Get_IsFertilized) flags |= ForestFlags.Fertilized;
            }
            return new ForestState(kind, true, prefab, p.x, p.y, p.z, r.x, r.y, r.z, r.w, Mathf.Clamp(s.x, 0.01f, 20), Mathf.Clamp(s.y, 0.01f, 20), Mathf.Clamp(s.z, 0.01f, 20), flags, type, ticks);
        }
        private static ForestKind KindOf(Component c) => c is TreeBase ? ForestKind.Tree : c is TreeTrunk ? ForestKind.Trunk : ForestKind.Sapling;
        private static Component? Native(Binding b) => b.Tree != null ? b.Tree : b.Trunk != null ? b.Trunk : b.Sapling != null ? (Component)b.Sapling : null;
        // The native save loader's sapling: the cuttings prefab turned into a growing tree.
        private static Tree_Growing? CreateSapling(byte type, Vector3 at, Quaternion rotation, bool fertilized, int ticks, int lastTick, Vector3 size)
        {
            var asset = Singleton<GameAssets>.i.GetTreeCuttings((Tree_Type)type);
            if (asset == null) return null;
            var go = Object.Instantiate(asset);
            var sapling = go.AddComponent<Tree_Growing>();
            go.transform.position = at; go.transform.rotation = rotation;
            var data = (GardenData_TreeGrowing)FormatterServices.GetUninitializedObject(typeof(GardenData_TreeGrowing));
            data.Tree_Type = type; data.TickSincePlanted = ticks; data.lastTickUpdate = lastTick; data.isFertilized = fertilized;
            data.TreeSize = new[] { size.x, size.y, size.z }; data.Position = new float[3]; data.Rotation = new float[3];
            sapling.LoadSystem(data);
            return sapling;
        }
        private static bool Valid(string prefab) => !string.IsNullOrEmpty(prefab) && prefab.Length <= ForestState.MaxName && prefab.All(c => c >= 0x20 && c <= 0x7E);
        private static Vector3 Position(ForestState s) => new Vector3(s.X, s.Y, s.Z);
        private static Quaternion Rotation(ForestState s) => new Quaternion(s.QX, s.QY, s.QZ, s.QW);

        // ---- Host ------------------------------------------------------------------------------
        private void Discover(bool periodic = false)
        {
            PruneRetiredBindings();
            var manager = Singleton<GlobalClassManager>.i;
            foreach (var tree in manager.garden_Trees)
                if (tree != null && !byTree.ContainsKey(tree) && tree.gameObject.scene.IsValid() && Valid(tree.PrefabName)) Register(ForestKind.Tree, tree, tree.PrefabName);
            foreach (var trunk in manager.garden_TreeParts)
                if (trunk != null && !byTrunk.ContainsKey(trunk) && trunk.gameObject.scene.IsValid() && Valid(trunk.PrefabName)) Register(ForestKind.Trunk, trunk, trunk.PrefabName);
            foreach (var sapling in WorldScans.Find<Tree_Growing>(periodic))
                if (sapling != null && !bySapling.ContainsKey(sapling) && sapling.gameObject.scene.IsValid()) Register(ForestKind.Sapling, sapling, SaplingPrefab);
        }
        // Native Start follows planting, felling and sapling maturation after their final pose and
        // save-loaded values are established. Publish that exact object instead of making guests
        // wait for the one-second recovery scan. Host-requested saplings already have a binding.
        internal void NativeReady(Component native)
        {
            if (!host || !Ready || native == null || !native.gameObject.scene.IsValid()) return;
            try
            {
                if (native is TreeBase tree && !byTree.ContainsKey(tree) && Valid(tree.PrefabName))
                    Register(ForestKind.Tree, tree, tree.PrefabName);
                else if (native is TreeTrunk trunk && !byTrunk.ContainsKey(trunk) && Valid(trunk.PrefabName))
                    Register(ForestKind.Trunk, trunk, trunk.PrefabName);
                else if (native is Tree_Growing sapling && !bySapling.ContainsKey(sapling))
                    Register(ForestKind.Sapling, sapling, SaplingPrefab);
                if (native is TreeTrunk) replacementOutputs?.Capture(native);
            }
            catch (Exception ex) { Debug.LogException(ex); nextDiscover = 0; }
        }
        private void Register(ForestKind kind, Component native, string prefab)
        {
            ulong id = ++nextId;
            while (channel != null && channel.Records.ContainsKey(id)) id = ++nextId;
            var b = new Binding { Id = id, Kind = kind, Tree = native as TreeBase, Trunk = native as TreeTrunk, Sapling = native as Tree_Growing, Go = native.gameObject, State = Capture(kind, native, prefab) };
            bindings.Add(id, b);
            captureBindings.Add(b);
            if (b.Tree != null) byTree.Add(b.Tree, b); else if (b.Trunk != null) byTrunk.Add(b.Trunk, b); else bySapling.Add(b.Sapling!, b);
            channel?.Register(new WorldRecord(id, 1, ForestState.Adapter, b.State.Encode()));
        }
        private void Publish(Binding b, ForestState state)
        {
            b.State = state;
            if (!state.Exists)
            {
                captureBindings.Remove(b);
                RetireBinding(b);
            }
            if (!state.Exists && b.RetireAfterOutputs) channel!.PublishAfterQueued(b.Id, state.Encode());
            else channel!.Publish(b.Id, state.Encode());
        }
        private void RetireBinding(Binding b)
        {
            // Destroy is deferred until the frame ends. Retain its reverse key until then so a
            // discovery pass in this same frame cannot register the removed object a second time.
            if (Native(b) != null) { retiredBindings.Add(b); return; }
            if (!ReferenceEquals(b.Tree, null)) byTree.Remove(b.Tree!);
            if (!ReferenceEquals(b.Trunk, null)) byTrunk.Remove(b.Trunk!);
            if (!ReferenceEquals(b.Sapling, null)) bySapling.Remove(b.Sapling!);
            b.Tree = null; b.Trunk = null; b.Sapling = null; b.Go = null;
            retiredBindings.Remove(b);
        }
        private void PruneRetiredBindings()
        {
            foreach (var b in retiredBindings.Snapshot) RetireBinding(b);
        }
        private bool PeerPosition(uint peer, out Vector3 at)
        {
            at = default;
            if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2 ||
                (p.Pose.State != (byte)PlayerState.Walking && p.Pose.State != (byte)PlayerState.InCreeper)) return false;
            at = new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z); return true;
        }
        private static float Reach(Component native, Vector3 at)
        {
            float best = float.MaxValue;
            foreach (var collider in native.GetComponentsInChildren<Collider>())
                if (collider.enabled && !collider.isTrigger) best = Mathf.Min(best, (collider.bounds.ClosestPoint(at) - at).magnitude); // AABB: ClosestPoint is undefined on concave meshes
            return best == float.MaxValue ? (native.transform.position - at).magnitude : best;
        }
        [MonMP.Core.World.WorldCommandAdapters(ForestState.Adapter, ForestAction.RootAdapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0) return;
            if (id == ForestAction.Root) { PlantRequest(peer, ForestAction.Decode(payload)); return; }
            if (!bindings.TryGetValue(id, out var b) || !b.State.Exists) return;
            if (payload.Length > 0 && payload[0] == ForestAction.Marker) { Act(peer, b, ForestAction.Decode(payload)); return; }
            var cut = ForestCut.Decode(payload);
            if (cut.Kind != b.Kind || Time.unscaledTime - b.LastCut < 0.5f) return;
            if (!PeerPosition(peer, out var at)) { RefuseCut(peer, b, "no fresh walking pose"); return; }
            if (b.Tree != null)
            {
                var tree = b.Tree;
                float reach = Reach(tree, at);
                if (reach > 4) { RefuseCut(peer, b, "reach " + reach.ToString("0.0") + " m"); return; }
                if (tree.Get_SpoutConnected != null) { RefuseCut(peer, b, "maple spout connected"); return; }
                if (tree.OnCottageLand && !Singleton<ToDoList_Manager>.i.isDone(ToDoList_Line.Intermediate_CottageBought)) { RefuseCut(peer, b, "cottage land not bought"); return; }
                var push = new Vector3(cut.PushX, cut.PushY, cut.PushZ);
                b.LastCut = Time.unscaledTime;
                applying = true;
                try
                {
                    tree.Cut(push.sqrMagnitude > 0.01f ? push.normalized : tree.transform.forward);
                    GameManager.Get_PlayerStats[9]++; // Remote cut replaces the native chainsaw's owner counter.
                }
                finally { applying = false; }
            }
            else if (b.Trunk != null)
            {
                float reach = Reach(b.Trunk, at);
                if (reach > 4) { RefuseCut(peer, b, "reach " + reach.ToString("0.0") + " m"); return; }
                b.LastCut = Time.unscaledTime;
                applying = true;
                try { b.Trunk.CutAppart(); }
                finally { applying = false; }
            }
            else return;
            if (b.State.Exists) Publish(b, b.State.Removed());
            if (b.Kind == ForestKind.Tree) nextDiscover = 0; // the felled trunk registers once it starts
        }
        // CutAppart converts the root and child segments in place. Queue the trunk removal
        // before the new logs' Start callbacks, including when the host wielded the saw.
        // This lets a guest replace its temporary trunk pieces as the real log records arrive.
        internal void NativeSplit(TreeTrunk trunk)
        {
            if (host && Ready && byTrunk.TryGetValue(trunk, out var b) && b.State.Exists &&
                trunk.GetComponent<Item_WoodLog>() != null)
                Publish(b, b.State.Removed());
        }

        // A refused remote cut is otherwise silent; a few bounded lines name the reason.
        private int cutRefusalLogs;
        private void RefuseCut(uint peer, Binding b, string reason)
        {
            if (cutRefusalLogs >= 8) return;
            cutRefusalLogs++;
            presence.TryGetValue(peer, out var p);
            Debug.LogWarning("MonMP forest: refused peer " + peer + " " + b.Kind + " cut " + b.Id.ToString("X16") + ": " + reason +
                "; poseAge=" + (p.Time > 0 ? (Time.unscaledTime - p.Time).ToString("0.00") : "none") + " state=" + p.Pose.State);
        }
        private void Act(uint peer, Binding b, ForestAction action)
        {
            var native = Native(b);
            if (native == null || !PeerPosition(peer, out var at) || Reach(native, at) > 4) return;
            applying = true;
            try
            {
                if (action.Op == ForestOp.Prune && b.Tree != null && b.Tree.Get_HasCuttings)
                {
                    if (b.Tree.OnCottageLand && !Singleton<ToDoList_Manager>.i.isDone(ToDoList_Line.Intermediate_CottageBought)) return;
                    var items = MonMPPlugin.Instance?.Items;
                    if (items == null || !items.HostPruningTool(peer, out var loppers)) return;
                    b.Tree.GrabCuttings();
                    items.HostPruningAward(loppers!, b.Tree);
                }
                else if (action.Op == ForestOp.Fertilize && b.Sapling != null)
                {
                    HostFertilize(peer, b.Sapling);
                }
                else if (action.Op == ForestOp.CutSapling && b.Sapling != null)
                {
                    b.Sapling.Cut();
                    Publish(b, b.State.Removed());
                    return;
                }
                else return;
            }
            finally { applying = false; }
            Publish(b, Capture(b.Kind, native, b.State.Prefab));
        }

        private static void HostFertilize(uint peer, Tree_Growing sapling)
        {
            if (sapling.Get_IsFertilized) return;
            if (MonMPPlugin.Instance?.Items?.HostDebitHeldTool<Tool_FertilizerPack>(peer, 0.05f, destroyWhenEmpty: true) == true)
                sapling.Fertilize();
        }
        private void PlantRequest(uint peer, ForestAction action)
        {
            if (action.Op != ForestOp.Plant || action.TreeType > 2 || !PeerPosition(peer, out var at)) return;
            var point = new Vector3(action.X, action.Y, action.Z);
            if ((point - at).magnitude > 10 || (lastPlant.TryGetValue(peer, out float last) && Time.unscaledTime - last < 0.3f)) return;
            if (!Singleton<Gameplay>.i.IsOnHomeLand(point)) return;
            foreach (var other in bindings.Values)
                if (other.State.Exists && other.Kind != ForestKind.Trunk && other.Go != null && (other.Go.transform.position - point).magnitude < 2) return;
            var items = MonMPPlugin.Instance?.Items;
            if (items == null || !items.HostCuttingSource(peer, action.Item, action.LeaseGeneration, action.TreeType, out var cutting) ||
                !CanPlantCutting(cutting!, point)) return;
            var sapling = CreateSapling(action.TreeType, point, Quaternion.Euler(0, action.Yaw, 0), false, 0, Singleton<TickSystem>.i.Get_totalTickSinceGameStart,
                TreeBase.Get_RandomTreeSize((Tree_Type)action.TreeType));
            if (sapling == null) return;
            if (!items.HostSpendPlantedCutting(peer, action.Item, action.LeaseGeneration, action.TreeType))
            { DestroyReplica(sapling); return; }
            lastPlant[peer] = Time.unscaledTime;
            Register(ForestKind.Sapling, sapling, SaplingPrefab);
            channel!.Reply(peer, ForestAction.Root, action.Encode());
        }
        private static bool CanPlantCutting(Garden_TreeCuttings cutting, Vector3 point)
        {
            // Match native CanPlaceItHere, including its terrain texture and exact exclusions.
            foreach (var collider in Physics.OverlapSphere(point + Vector3.up * 0.7f, 2f))
                if (collider != cutting.itemCollider && collider.GetType() != typeof(TerrainCollider) &&
                    collider.tag != "Player" && collider.tag != "Water" && collider.tag != "Leo") return false;
            return (int)CuttingTexture.Invoke(cutting, new object[] { point }) == 5;
        }

        // ---- Guest cut interception -------------------------------------------------------------
        internal bool Intercept(TreeBase tree, Vector3 push)
        {
            if (host || applying || disposed || session?.IsRunning != true) return false;
            if (Ready && byTree.TryGetValue(tree, out var b) && b.State.Exists && Time.unscaledTime - b.LastCut > 0.5f)
            {
                b.LastCut = Time.unscaledTime;
                var d = push.sqrMagnitude > 0.0001f ? push.normalized : Vector3.forward;
                channel!.Request(b.Id, new ForestCut(ForestKind.Tree, d.x, d.y, d.z).Encode(), true);
            }
            return true;
        }
        internal bool Intercept(TreeTrunk trunk)
        {
            if (host || applying || disposed || session?.IsRunning != true) return false;
            if (Ready && byTrunk.TryGetValue(trunk, out var b) && b.State.Exists && Time.unscaledTime - b.LastCut > 0.5f)
            {
                b.LastCut = Time.unscaledTime;
                channel!.Request(b.Id, new ForestCut(ForestKind.Trunk, 0, 0, 0).Encode(), true);
            }
            return true;
        }

        // Guests never run the daily cuttings regrowth or sapling growth; the host's records drive both.
        internal bool GuestBlocks => !host && !disposed && session?.IsRunning == true;
        internal void Mirror(Component native, ForestOp op)
        {
            if (host || applying || !Ready || native == null) return;
            Binding? b = native is TreeBase tree && byTree.TryGetValue(tree, out var t) ? t : native is Tree_Growing sapling && bySapling.TryGetValue(sapling, out var s) ? s : null;
            if (b == null || !b.State.Exists || Time.unscaledTime - b.LastCut < 0.3f) return;
            b.LastCut = Time.unscaledTime;
            if (op == ForestOp.Fertilize) pendingFertilizers[b.Id] = Time.unscaledTime + 2.5f;
            channel!.Request(b.Id, new ForestAction(op).Encode(), true);
        }
        internal bool InterceptSaplingCut(Tree_Growing sapling)
        {
            if (!GuestBlocks || applying) return false;
            // Destruction waits for the accepted tombstone. A rejected cut keeps the original
            // collider, daily subscription and visual instead of leaving a guest-only hole.
            Mirror(sapling, ForestOp.CutSapling);
            return true;
        }
        internal bool PlantCutting(Garden_TreeCuttings cutting)
        {
            if (!GuestBlocks || applying) return false;
            var items = MonMPPlugin.Instance?.Items;
            if (!Ready || !cutting.isEquiped || !(bool)CuttingPreview.GetValue(cutting) ||
                !(bool)CuttingCanPlace.Invoke(cutting, new object[] { true }) ||
                items == null || !items.TryGetLocalToolLease(cutting, out ulong item, out uint generation)) return true;
            var at = cutting.transform.position;
            if (pendingPlants.ContainsKey(item)) return true;
            if (++nextPlantRequest == 0) ++nextPlantRequest;
            pendingPlants[item] = (nextPlantRequest, Time.unscaledTime + 4f);
            channel!.Request(ForestAction.Root, new ForestAction(ForestOp.Plant, (byte)cutting.Get_TreeType,
                at.x, at.y, at.z, cutting.transform.eulerAngles.y, item, generation, nextPlantRequest).Encode());
            return true;
        }
        private void PlantReply(ulong id, byte[] payload)
        {
            if (host || id != ForestAction.Root || payload.Length != ForestAction.Size || payload[0] != ForestAction.Marker) return;
            var action = ForestAction.Decode(payload);
            if (action.Op != ForestOp.Plant || !pendingPlants.TryGetValue(action.Item, out var pending) || pending.Request != action.Request) return;
            pendingPlants.Remove(action.Item);
            // These are the native successful-click effects; stock and sapling already committed
            // together on the host. Another player's tree at this point cannot award them here.
            Singleton<SOUND_Manager>.i.PlaySound_PartInstalledInDirt();
            Singleton<ToDoList_Manager>.i.Garden_PlantATreeCuttings();
            GameManager.Get_PlayerStats[12]++;
            if (MonMPPlugin.Instance?.Items?.ItemOf(action.Item) is Garden_TreeCuttings cutting && cutting != null)
                cutting.Update_UI_Tool_Info();
        }

        // ---- Guest apply -----------------------------------------------------------------------
        private static (int, int) Key(Vector3 p) => (Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.z / Cell));
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            var states = records.Values.Where(r => r.Adapter == ForestState.Adapter).Select(r => (r.Id, State: ForestState.Decode(r.Payload))).ToArray();
            if (Reconciled) { foreach (var (id, state) in states) Apply(id, state); return; }
            // Index the guest's own scene trees and trunks by prefab and position.
            var locals = new Dictionary<(string, int, int), List<Component>>();
            void Index(Component c, string prefab)
            {
                if (c == null || !c.gameObject.scene.IsValid()) return;
                var (x, z) = Key(c.transform.position);
                if (!locals.TryGetValue((prefab, x, z), out var list)) locals.Add((prefab, x, z), list = new List<Component>());
                list.Add(c);
            }
            foreach (var tree in Object.FindObjectsOfType<TreeBase>()) Index(tree, tree.PrefabName ?? "");
            foreach (var trunk in Object.FindObjectsOfType<TreeTrunk>()) Index(trunk, trunk.PrefabName ?? "");
            foreach (var sapling in Object.FindObjectsOfType<Tree_Growing>()) Index(sapling, SaplingPrefab);
            var matched = new HashSet<Component>();
            Component? Match(ForestState s)
            {
                var p = Position(s); var (cx, cz) = Key(p);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (locals.TryGetValue((s.Prefab, cx + dx, cz + dz), out var list))
                            foreach (var c in list)
                                if (c != null && !matched.Contains(c) && (c.transform.position - p).sqrMagnitude <= MatchDistance * MatchDistance) return c;
                return null;
            }
            applying = true;
            try
            {
                foreach (var (id, state) in states)
                {
                    var local = Match(state);
                    if (local != null) matched.Add(local);
                    if (!state.Exists) { bindings[id] = new Binding { Id = id, Kind = state.Kind, State = state }; continue; }
                    if (local != null && KindOf(local) == state.Kind)
                    {
                        var b = Bind(id, state, local);
                        ApplyPose(b, state, true); ApplyNative(b, state); Kept++;
                    }
                    else Spawn(id, state);
                }
                // Unmatched scene defaults, and those matched only to a tombstone (felled on the host).
                foreach (var list in locals.Values)
                    foreach (var c in list)
                    {
                        if (c == null || (c is TreeBase tree && byTree.ContainsKey(tree)) || (c is TreeTrunk trunk && byTrunk.ContainsKey(trunk)) || (c is Tree_Growing sapling && bySapling.ContainsKey(sapling))) continue;
                        DestroyReplica(c); Removed++;
                    }
            }
            finally { applying = false; }
            Reconciled = true;

#if MONMP_DEVELOPMENT
            Report();
#endif

        }
        private Binding Bind(ulong id, ForestState state, Component native)
        {
            var b = new Binding { Id = id, Kind = state.Kind, Tree = native as TreeBase, Trunk = native as TreeTrunk, Sapling = native as Tree_Growing, Go = native.gameObject, State = state };
            bindings[id] = b;
            if (b.Tree != null) byTree[b.Tree] = b;
            if (b.Sapling != null) bySapling[b.Sapling] = b;
            if (b.Trunk != null) { byTrunk[b.Trunk] = b; MakeReplica(b.Trunk); }
            return b;
        }
        private static void MakeReplica(TreeTrunk trunk)
        {
            trunk.Start(); // adds the native rigidbody now instead of next frame
            var rb = trunk.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = true; rb.interpolation = RigidbodyInterpolation.None; }
        }
        private void Spawn(ulong id, ForestState state)
        {
            if (state.Kind == ForestKind.Sapling)
            {
                var sapling = CreateSapling(state.TreeType, Position(state), Rotation(state), state.Has(ForestFlags.Fertilized), state.Ticks, 0, new Vector3(state.SX, state.SY, state.SZ));
                if (sapling == null) { bindings[id] = new Binding { Id = id, Kind = state.Kind, State = state }; Missing++; return; }
                var grown = Bind(id, state, sapling);
                ApplyPose(grown, state, true); ApplyNative(grown, state); Spawned++;
                return;
            }
            var asset = Resources.Load(state.Prefab) as GameObject;
            var native = asset == null ? null : (Component?)asset.GetComponent<TreeBase>() ?? asset.GetComponent<TreeTrunk>();
            if (native == null || (state.Kind == ForestKind.Tree) != (native is TreeBase))
            {
                bindings[id] = new Binding { Id = id, Kind = state.Kind, State = state }; Missing++;
                Debug.LogWarning("MonMP: forest prefab unavailable on this guest: " + state.Prefab);
                return;
            }
            var go = Object.Instantiate(asset!, Position(state), Rotation(state));
            var b = Bind(id, state, state.Kind == ForestKind.Tree ? (Component)go.GetComponent<TreeBase>() : go.GetComponent<TreeTrunk>());
            ApplyPose(b, state, true); ApplyNative(b, state); Spawned++;
        }
        private static void ApplyPose(Binding b, ForestState state, bool snap)
        {
            if (b.Go == null) return;
            b.Target = Position(state); b.TargetRotation = Rotation(state);
            if (b.Kind != ForestKind.Sapling) b.Go.transform.localScale = new Vector3(state.SX, state.SY, state.SZ);
            if (snap || b.Kind == ForestKind.Tree) { b.Go.transform.position = b.Target; b.Go.transform.rotation = b.TargetRotation; }
        }
        // Cuttings on trees; type, fertilizer, growth and final size on saplings (whose scale follows growth).
        private void ApplyNative(Binding b, ForestState state)
        {
            if (b.Tree != null)
            {
                TreeCuttings.SetValue(b.Tree, state.Has(ForestFlags.Cuttings));
                RefreshPruningTarget(b.Tree);
            }
            if (b.Sapling != null)
            {
                SaplingType.SetValue(b.Sapling, (Tree_Type)state.TreeType);
                SaplingFertilized.SetValue(b.Sapling, state.Has(ForestFlags.Fertilized));
                SaplingTicks.SetValue(b.Sapling, state.Ticks);
                SaplingSize.SetValue(b.Sapling, new Vector3(state.SX, state.SY, state.SZ));
                SaplingVisual.Invoke(b.Sapling, null);
            }
        }
        // The native equip pass shows the current target set once. Trees can then arrive or
        // regain/lose cuttings through host records, and new cutting piles can arrive as items.
        // ShowCuttingSphere keeps the native HasCuttings eligibility rule; this is presentation.
        internal void PruningEquipped(Tool_PruningLoppers loppers) => equippedLoppers = loppers;
        internal void PruningUnequipped(Tool_PruningLoppers loppers)
        {
            if (equippedLoppers == loppers) equippedLoppers = null;
        }
        private bool PruningTargetsVisible => !disposed && session?.IsRunning == true && equippedLoppers != null && equippedLoppers.isEquiped &&
            MonMPPlugin.Instance?.Gameplay?.Player?.Get_ActiveTool == equippedLoppers;
        internal void RefreshPruningTarget(TreeBase tree)
        {
            if (PruningTargetsVisible && tree != null) tree.ShowCuttingSphere(true);
        }
        internal void RefreshPruningTarget(Garden_TreeCuttings cuttings)
        {
            if (PruningTargetsVisible && cuttings != null) cuttings.ShowCuttingSphere(true);
        }
        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != ForestState.Adapter || !Reconciled) return;
            Apply(record.Id, ForestState.Decode(record.Payload));
        }
        private void Apply(ulong id, ForestState state)
        {
            if (!bindings.TryGetValue(id, out var b))
            {
                if (!state.Exists) { bindings[id] = new Binding { Id = id, Kind = state.Kind, State = state }; return; }
                applying = true;
                try { Spawn(id, state); } finally { applying = false; }
                return;
            }
            if (b.Kind != state.Kind) throw new InvalidDataException("Forest record kind changed.");
            bool wasAlive = b.State.Exists; b.State = state;
            if (b.Go == null)
            {
                // A live repair must recreate a destructively predicted or otherwise lost replica.
                if (state.Exists)
                {
                    if (!ReferenceEquals(b.Sapling, null)) bySapling.Remove(b.Sapling!);
                    if (!ReferenceEquals(b.Tree, null)) byTree.Remove(b.Tree!);
                    if (!ReferenceEquals(b.Trunk, null)) byTrunk.Remove(b.Trunk!);
                    applying = true;
                    try { Spawn(id, state); } finally { applying = false; }
                }
                return;
            }
            if (!state.Exists) { if (wasAlive) Fell(b); return; }
            ApplyPose(b, state, false);
            applying = true;
            try { ApplyNative(b, state); } finally { applying = false; }
        }
        private void Fell(Binding b)
        {
            UnsubscribeDay(Native(b));
            if (b.Tree != null)
            {
                // The native felling's leaf fall; the trunk itself arrives as its own host record.
                if (TreeLeaves.GetValue(b.Tree) is GameObject leaves && leaves != null)
                {
                    leaves.transform.SetParent(null);
                    leaves.AddComponent<Tree_Trash>().TotalTime = 1.5f;
                }
                byTree.Remove(b.Tree);
            }
            if (b.Trunk != null) byTrunk.Remove(b.Trunk);
            if (b.Sapling != null) bySapling.Remove(b.Sapling);
            if (!host && b.Trunk != null && b.Go != null) SplitStandIn(b.Trunk);
            else Object.Destroy(b.Go);
            b.Go = null; b.Tree = null; b.Trunk = null; b.Sapling = null;
        }

        // The native TreeTrunk.CutAppart, presentation only: the branches fall away and every segment
        // drops as its own loose body, with nothing to click, until the real logs replace it.
        private void SplitStandIn(TreeTrunk trunk)
        {
            var segments = TrunkSegments.GetValue(trunk) as List<GameObject> ?? new List<GameObject>();
            var parts = new[] { trunk.gameObject }.Concat(segments.Where(g => g != null)).Distinct().ToArray();
            if (TrunkTrash.GetValue(trunk) is GameObject trash && trash != null)
            {
                trash.transform.SetParent(null);
                trash.AddComponent<Tree_Trash>().TotalTime = 1f;
            }
            trunk.GetComponent<SOUND_Maps_Tree_Base>()?.BranchSeparation();
            var inherited = trunk.GetComponent<Rigidbody>();
            Vector3 velocity = inherited != null ? inherited.velocity : Vector3.zero;
            foreach (var part in parts)
            {
                part.transform.SetParent(null, true);
                foreach (var extension in part.GetComponents<TreeTrunkExtention>()) Object.Destroy(extension);
                part.tag = "Untagged";
                // Native Item_WoodLog body settings, so the pieces separate and settle the way the host's do.
                var body = part.GetComponent<Rigidbody>();
                if (body == null) body = part.AddComponent<Rigidbody>();
                body.isKinematic = false; body.mass = 40f; body.drag = 0.3f; body.angularDrag = 15f;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                body.velocity = velocity;
                foreach (var collider in part.GetComponents<Collider>()) collider.enabled = true;
                standIns.Add(new StandIn { Part = part, Since = Time.unscaledTime });
            }
            Object.Destroy(trunk);

        }

        // A host log replica arrived: retire the stand-in it replaces before physics sees both overlap.
        internal void ReplicaSpawned(InteractableItems item)
        {
            if (host || disposed || item is not Item_WoodLog || standIns.Count == 0) return;
            Vector3 at = item.transform.position;
            StandIn? best = null; float bestDistance = StandInMatch * StandInMatch;
            foreach (var standIn in standIns)
            {
                if (standIn.HiddenAt >= 0 || standIn.Part == null) continue;
                float d = (standIn.Part.transform.position - at).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = standIn; }
            }
            if (best == null) return;
#if MONMP_DEVELOPMENT
            if (item.gameObject.activeInHierarchy && item.GetComponentsInChildren<MeshRenderer>()
                .Any(renderer => renderer.enabled && !renderer.forceRenderingOff)) TestVisibleLogHandoffs++;
#endif
            Retire(best, Time.unscaledTime);
        }
        private void Retire(StandIn standIn, float now)
        {
            standIn.HiddenAt = now;
            // Hidden, not destroyed yet: the branch-separation sound plays from the root piece.
            if (standIn.Part != null)
            {
                foreach (var renderer in standIn.Part.GetComponentsInChildren<Renderer>()) renderer.enabled = false;
                foreach (var collider in standIn.Part.GetComponentsInChildren<Collider>()) collider.enabled = false;
                if (standIn.Part.GetComponent<Rigidbody>() is { } body) body.isKinematic = true;
            }
        }

        private void DiscoverIfDue(float now)
        {
            bool periodic = periodicDiscoveryStarted && nextDiscover > 0;
            if (now < nextDiscover || !WorldScans.TryClaim<Tree_Growing>(periodic)) return;
            // Preserve immediate initial/recovery discovery. Only the first
            // periodic deadline shifts; subsequent fallbacks remain one second.
            nextDiscover = periodic ? WorldScans.NextDeadline(now, 1f) : now + (periodicDiscoveryStarted ? 1f : 0.5f);
            periodicDiscoveryStarted = true;
            Discover(periodic);
        }
        public void Update()
        {
            if (!Ready) return;
            float now = Time.unscaledTime;
            if (!host)
            {
                SettleFertilizerPredictions(now);
                float k = 1 - Mathf.Exp(-12 * Time.unscaledDeltaTime);
                foreach (var b in byTrunk.Values)
                {
                    if (b.Go == null) continue;
                    var t = b.Go.transform;
                    if ((t.position - b.Target).sqrMagnitude > 25) { t.position = b.Target; t.rotation = b.TargetRotation; continue; }
                    t.position = Vector3.Lerp(t.position, b.Target, k); t.rotation = Quaternion.Slerp(t.rotation, b.TargetRotation, k);
                }
                for (int i = standIns.Count - 1; i >= 0; i--)
                {
                    var standIn = standIns[i];
                    if (standIn.HiddenAt < 0)
                    {
                        if (standIn.Part != null && now - standIn.Since < StandInTimeout) continue;
                        // No shared log landed near it: a miss, not a handoff, so say so in the log.
                        if (standIn.Part != null)
                        {
#if MONMP_DEVELOPMENT
                            TestStandInTimeouts++;
#endif
                            Debug.LogWarning("MonMP forest: a split log stand-in timed out without its shared log.");
                        }
                        Retire(standIn, now);
                    }
                    else if (now - standIn.HiddenAt > 3f)
                    {
                        if (standIn.Part != null) Object.Destroy(standIn.Part);
                        standIns.RemoveAt(i);
                    }
                }
                foreach (var pending in pendingPlants.ToArray()) if (now >= pending.Value.Until) pendingPlants.Remove(pending.Key);
                return;
            }
            DiscoverIfDue(now);
            if (now < nextCapture) return;
            nextCapture = now + 0.1f;
            foreach (var b in captureBindings.Snapshot)
            {
                if (!b.State.Exists) continue;
                var native = Native(b);
                if (native == null)
                {
                    // Split pieces register through their own item lifecycle as each log finishes Start.
                    Publish(b, b.State.Removed()); continue;
                }
                if (b.Tree != null) { if (b.Tree.Get_HasCuttings != b.State.Has(ForestFlags.Cuttings)) Publish(b, Capture(ForestKind.Tree, b.Tree, b.State.Prefab)); continue; }
                if (b.Sapling != null)
                {
                    var sapling = b.Sapling;
                    if (sapling.Get_IsFertilized != b.State.Has(ForestFlags.Fertilized) || Mathf.Clamp(sapling.Get_TickSincePlanted, 0, ForestState.MaxTicks) != b.State.Ticks)
                        Publish(b, Capture(ForestKind.Sapling, sapling, SaplingPrefab));
                    continue;
                }
                var t = native.transform;
                if ((t.position - Position(b.State)).sqrMagnitude > 0.0004f || Quaternion.Angle(t.rotation, Rotation(b.State)) > 1)
                    Publish(b, Capture(ForestKind.Trunk, native, b.State.Prefab));
            }
        }
        private void SettleFertilizerPredictions(float now)
        {
            if (pendingFertilizers.Count == 0) return;
            foreach (var pair in pendingFertilizers.ToArray())
            {
                if (now < pair.Value) continue;
                if (!bindings.TryGetValue(pair.Key, out var binding) || !binding.State.Exists || binding.Sapling == null)
                { pendingFertilizers.Remove(pair.Key); continue; }
                bool previous = applying; applying = true;
                try
                {
                    // Native Fertilize changes only this flag; preserve unrelated growth/pose.
                    SaplingFertilized.SetValue(binding.Sapling, binding.State.Has(ForestFlags.Fertilized));
                    pendingFertilizers.Remove(pair.Key);
                }
                finally { applying = previous; }
            }
        }
        // Native Cut/maturation detach these callbacks. Replica replacement/refusal uses Destroy
        // directly, so it must do the same without spawning a trunk or playing a cutting sound.
        private static void UnsubscribeDay(Component? native)
        {
            var ticks = Singleton<TickSystem>.i;
            if (ticks == null) return;
            MethodInfo? method = native is TreeBase ? TreeNewDay : native is Tree_Growing ? SaplingNewDay : null;
            if (method != null)
                ticks.NewDayEvent -= (TickSystem.MainDayEvent)Delegate.CreateDelegate(typeof(TickSystem.MainDayEvent), native, method);
        }
        private static void DestroyReplica(Component native)
        {
            UnsubscribeDay(native);
            Object.Destroy(native.gameObject);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            equippedLoppers = null;
            foreach (var standIn in standIns) if (standIn.Part != null) Object.Destroy(standIn.Part);
            standIns.Clear();
            pendingPlants.Clear();
            pendingFertilizers.Clear();
            captureBindings.Clear(); retiredBindings.Clear();
            if (MonMPPlugin.Instance?.Items is { } items) items.RemoteSpawned -= ReplicaSpawned;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.CommandRequested -= Command; channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.ReplyReceived -= PlantReply; }
            session = null; channel = null;
        }

        // ---- Test hooks ------------------------------------------------------------------------

#if MONMP_DEVELOPMENT
        internal ulong TestTreeNear(Vector3 at) => bindings.Values.Where(b => b.Kind == ForestKind.Tree && b.State.Exists && b.Tree != null && !b.Tree.OnCottageLand && b.Tree.Get_SpoutConnected == null)
            .OrderBy(b => (Position(b.State) - at).sqrMagnitude).Select(b => b.Id).FirstOrDefault();


        internal int TestTrunkPieces(ulong id) => bindings[id].Trunk is TreeTrunk trunk &&
            TrunkSegments.GetValue(trunk) is List<GameObject> segments ? 1 + segments.Count : 0;

        internal ulong TestNewestTrunk => bindings.Values.Where(b => b.Kind == ForestKind.Trunk && b.State.Exists).Select(b => b.Id).DefaultIfEmpty().Max();


        internal int TestAlive(ForestKind kind) => bindings.Values.Count(b => b.Kind == kind && b.State.Exists);


        internal int TestRemoved(ForestKind kind) => bindings.Values.Count(b => b.Kind == kind && !b.State.Exists);


        internal int TestCaptureBindings => captureBindings.Count;


        internal int TestLiveLocal(ForestKind kind) => bindings.Values.Count(b => b.Kind == kind && b.State.Exists && b.Go != null);


        internal bool TestExists(ulong id) => bindings.TryGetValue(id, out var b) && b.State.Exists;


        internal bool TestLocal(ulong id) => bindings.TryGetValue(id, out var b) && b.Go != null;


        internal Vector3 TestPosition(ulong id) => bindings[id].Go != null ? bindings[id].Go!.transform.position : Position(bindings[id].State);


        internal ulong TestTreeWithCuttings(Vector3 at) => bindings.Values.Where(b => b.Kind == ForestKind.Tree && b.State.Exists && b.State.Has(ForestFlags.Cuttings) && b.Tree != null && !b.Tree.OnCottageLand)
            .OrderBy(b => (Position(b.State) - at).sqrMagnitude).Select(b => b.Id).FirstOrDefault();


        internal ForestState TestState(ulong id) => bindings[id].State;


        internal bool TestLocalCuttings(ulong id) => bindings[id].Tree is TreeBase tree && tree.Get_HasCuttings;


        internal Tree_Growing? TestSapling(ulong id) => bindings.TryGetValue(id, out var b) ? b.Sapling : null;


        internal ulong TestNewest(ForestKind kind) => bindings.Values.Where(b => b.Kind == kind && b.State.Exists).Select(b => b.Id).DefaultIfEmpty().Max();


        internal bool TestPlantPending => pendingPlants.Count > 0;


        internal int TestCuttingsCount => bindings.Values.Count(b => b.Kind == ForestKind.Tree && b.State.Exists && b.State.Has(ForestFlags.Cuttings));


        internal void TestPrune(ulong id) => bindings[id].Tree!.GrabCuttings();
#endif

        // The native preview pose, sent through the real leased cutting's paid placement path.

#if MONMP_DEVELOPMENT
        internal void TestPlant(Vector3 point, float yaw, Tree_Type type)
        {
            if (Singleton<Gameplay>.i.Player.Get_ActiveTool is not Garden_TreeCuttings cutting || cutting.Get_TreeType != type)
                throw new InvalidOperationException("Plant fixture requires a real leased, equipped cutting of the requested type.");
            cutting.transform.SetPositionAndRotation(point, Quaternion.Euler(0, yaw, 0));
            CuttingPreview.SetValue(cutting, true);
            PlantCutting(cutting);
        }
#endif

        // The host's daily growth, compressed: the sapling reaches maturity on its next native day.

#if MONMP_DEVELOPMENT
        internal void TestMature(ulong id)
        {
            var sapling = bindings[id].Sapling!;
            SaplingTicks.SetValue(sapling, 21600);
            SaplingNewDay.Invoke(sapling, new object[] { 0 });
        }
#endif

        // Home-land ground with the 2 m clearance the native cuttings placement requires.

#if MONMP_DEVELOPMENT
        internal static bool TestPlantPoint(out Vector3 point)
        {
            var gameplay = Singleton<Gameplay>.i;
            var limits = AccessTools.Field(typeof(Gameplay), "HomeTerrainLimitMeshs").GetValue(gameplay) as MeshFilter[] ?? new MeshFilter[0];
            foreach (var limit in limits)
            {
                if (limit == null) continue;
                var bounds = limit.mesh.bounds;
                for (float u = 0.15f; u <= 0.85f; u += 0.05f)
                    for (float v = 0.15f; v <= 0.85f; v += 0.05f)
                    {
                        var probe = limit.transform.TransformPoint(new Vector3(Mathf.Lerp(bounds.min.x, bounds.max.x, u), bounds.center.y, Mathf.Lerp(bounds.min.z, bounds.max.z, v))) + Vector3.up * 60;
                        if (!Physics.Raycast(probe, Vector3.down, out var hit, 200, LayerMask.GetMask("Ground"), QueryTriggerInteraction.Ignore) || !(hit.collider is TerrainCollider)) continue;
                        if (!gameplay.IsOnHomeLand(hit.point)) continue;
                        if (Physics.OverlapSphere(hit.point + Vector3.up * 0.7f, 2.2f, ~0, QueryTriggerInteraction.Ignore).Any(c => !(c is TerrainCollider) && c.tag != "Player" && c.tag != "Water" && c.tag != "Leo")) continue;
                        point = hit.point; return true;
                    }
            }
            point = default; return false;
        }


        internal void TestCut(ulong id, Vector3 push)
        {
            var b = bindings[id];
            if (b.Tree != null) b.Tree.Cut(push);
            else b.Trunk!.CutAppart();
        }
#endif

    }
}
