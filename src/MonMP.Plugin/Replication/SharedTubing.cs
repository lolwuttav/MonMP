using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // Maple tubing: placed tubing posts, tree spouts and the tubes between their connection points.
    // The host's objects are the records. A guest's post or spout placement spends the held tool item
    // on the host, which builds the post; a guest's tube is predicted by the native roll and adopted
    // when the host's tube record arrives (undone if refused). Removals run on the host too; a guest's
    // tube removal is predicted and restored if the host keeps the tube.
    internal sealed class SharedTubing : IDisposable
    {
        private sealed class Binding
        {
            public ulong Id;
            public SugarShack_TubingPost? Post;
            public SugarShack_Tubing? Tube;
            public TubingState State;
            public float PendingUntil;
            public bool Dirty;
            public bool RetireAfterOutputs;
        }
        private static readonly FieldInfo TempPlacedPost = AccessTools.Field(typeof(Tool_TubingPost), "isTempPlaced");
        private static readonly FieldInfo TempPlacedSpout = AccessTools.Field(typeof(Tool_TubingChalumeau), "isTempPlaced");
        private static readonly FieldInfo SpotSpout = AccessTools.Field(typeof(Tool_TubingChalumeau), "CorrectSpotTobePlaced");
        private static readonly FieldInfo SpoutTree = AccessTools.Field(typeof(SugarShack_TubingPost), "TreeConnectedTo");
        private static readonly FieldInfo RollOrigin = AccessTools.Field(typeof(Tool_TubingRoll), "current_TubingPost_Connection");
        private Tool_TubingRoll? equippedRoll;
        internal SharedItems? Items { get; set; }
        private const float MatchDistance = 0.05f, Reach = 6, TubeReach = 8, MaxTube = 20.5f, PendingWindow = 4;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly BindingWorkSet<Binding> captureBindings = new BindingWorkSet<Binding>();
        private readonly BindingWorkSet<Binding> dirtyBindings = new BindingWorkSet<Binding>();
        private readonly BindingWorkSet<Binding> retiredBindings = new BindingWorkSet<Binding>();
        private readonly BindingWorkSet<Binding> nativeRemovals = new BindingWorkSet<Binding>();
        private readonly Dictionary<SugarShack_TubingPost, Binding> byPost = new Dictionary<SugarShack_TubingPost, Binding>();
        private readonly Dictionary<SugarShack_Tubing, Binding> byTube = new Dictionary<SugarShack_Tubing, Binding>();
        private readonly List<(SugarShack_Tubing Tube, float Since)> pendingTubes = new List<(SugarShack_Tubing, float)>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, float> lastPlace = new Dictionary<uint, float>();
        private readonly bool host;
        private readonly string diagnostics;
        private NetworkSession? session;
        private WorldChannel? channel;
        private SugarShack? shack;
        private bool applying, disposed, treesDirty, tubeScanDue;
        private ulong nextId = TubingState.IdBase;
        private float nextCapture, nextTrees;
        public bool Reconciled { get; private set; }
        public int Kept { get; private set; }
        public int Removed { get; private set; }
        public int Spawned { get; private set; }
        public int Adopted { get; private set; }

#if MONMP_DEVELOPMENT
        internal string TestLastConnectNote { get; private set; } = "none";
#endif

        private void ConnectNote(string note)
        {

#if MONMP_DEVELOPMENT
            if (MonMPPlugin.Instance?.TestOptions.Enabled != true) return;


            TestLastConnectNote = note;


            File.WriteAllText(Path.Combine(diagnostics, "tubing-connect-test.txt"), note);
#endif

            Debug.Log("[MonMP] tubing connect: " + note);
        }
        private bool Ready => !disposed && session?.IsRunning == true && channel?.Ready == true;
        public SharedTubing(bool host, string diagnostics)
        {
            this.host = host; this.diagnostics = diagnostics;
            if (host) Discover();

#if MONMP_DEVELOPMENT
            Report();
#endif

        }

#if MONMP_DEVELOPMENT
        private void Report() => File.WriteAllText(Path.Combine(diagnostics, "shared-tubing.txt"),
            "Role=" + (host ? "host" : "guest") + "\nPosts=" + bindings.Values.Count(b => b.State.Exists && b.State.Kind == TubingKind.Post) +
            "\nSpouts=" + bindings.Values.Count(b => b.State.Exists && b.State.Kind == TubingKind.Spout) + "\nTubes=" + bindings.Values.Count(b => b.State.Exists && b.State.Kind == TubingKind.Tube) +
            "\nRecords=" + bindings.Count + "\nReconciled=" + Reconciled + "\nKept=" + Kept + "\nRemoved=" + Removed + "\nSpawned=" + Spawned + "\nAdopted=" + Adopted);
#endif

        public IEnumerable<WorldRecord> RootRecords() { yield return TubingCommand.RootRecord(); }
        public IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, TubingState.Adapter, b.State.Encode()));
        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.CommandRequested += Command; channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) { presence.Remove(peer); lastPlace.Remove(peer); }

        // The shack object is inactive until it is built; its pump inlet is still the network's root.
        private SugarShack? Shack => shack != null ? shack : (shack = Resources.FindObjectsOfTypeAll<SugarShack>().FirstOrDefault(s => s.gameObject.scene.IsValid()));
        private SugarShack_TubingPost? Pump => Shack?.Get_VacuumPumpPost;
        private static bool Placed(SugarShack_TubingPost p) => p != null && !p.GetIfisVacuumPump && p.gameObject.scene.IsValid();
        private static TubingKind KindOf(SugarShack_TubingPost p) => p.GetIfIsAChalumeau ? TubingKind.Spout : TubingKind.Post;
        // Posts stand upright; a spout lies along the trunk (x = -90), so its heading is read from its up axis.
        private static float YawOf(SugarShack_TubingPost p)
        {
            if (!p.GetIfIsAChalumeau) return Mathf.Repeat(p.transform.eulerAngles.y, 360);
            var up = p.transform.up;
            return Mathf.Repeat(Mathf.Atan2(-up.x, -up.z) * Mathf.Rad2Deg, 360);
        }
        private static Quaternion Rotation(TubingKind kind, float yaw) => kind == TubingKind.Spout ? Quaternion.Euler(-90, yaw, 0) : Quaternion.Euler(0, yaw, 0);
        private static Vector3 Position(TubingState s) => new Vector3(s.X, s.Y, s.Z);
        private ulong EndpointId(SugarShack_TubingPost? p) => p == null ? 0 : p.GetIfisVacuumPump ? TubingState.Pump : byPost.TryGetValue(p, out var b) && b.State.Exists ? b.Id : 0;
        private SugarShack_TubingPost? EndpointPost(ulong id) => id == TubingState.Pump ? Pump : bindings.TryGetValue(id, out var b) && b.State.Exists ? b.Post : null;
        private static TubingConnectionPoint? Point(SugarShack_TubingPost? post, int index) => post == null ? null : post.GetConnectionPointFromIndex(index);
        private static bool Bound(SugarShack_Tubing t) => t != null && t.GetOrigin?.Post != null && t.GetTarget?.Post != null;
        private static bool StartedPoint(SugarShack_TubingPost? post, TubingConnectionPoint? point) => post != null &&
            post.gameObject.activeInHierarchy && point != null && point.Post == post && point.VisualSphere != null;
        private readonly struct PointSnapshot
        {
            private readonly TubingConnectionPoint point;
            private readonly bool connected, visible;
            private readonly TubingConnectionPoint? other;
            private readonly TubeRenderer? renderer;
            public PointSnapshot(TubingConnectionPoint point)
            {
                this.point = point; connected = point.isConnected; other = point.ConnectedTo;
                renderer = point.Cache_TubeRenderer; visible = point.VisualSphere.activeSelf;
            }
            public void Restore()
            {
                point.isConnected = connected; point.ConnectedTo = other; point.Cache_TubeRenderer = renderer;
                if (point.VisualSphere != null) point.VisualSphere.SetActive(visible);
            }
        }

        // ---- Host ------------------------------------------------------------------------------
        private void Discover()
        {
            DiscoverPosts();
            DiscoverTubes();
        }
        private void DiscoverPosts(bool periodic = false)
        {
            PruneRetiredBindings();
            foreach (var p in WorldScans.Find<SugarShack_TubingPost>(periodic))
                if (p != null && !byPost.ContainsKey(p) && Placed(p)) Register(Placed(KindOf(p), p), post: p);
        }
        private void DiscoverTubes(bool periodic = false)
        {
            foreach (var t in WorldScans.Find<SugarShack_Tubing>(periodic))
            {
                if (t == null || byTube.ContainsKey(t) || !Bound(t)) continue;
                ulong origin = EndpointId(t.GetOrigin.Post), target = EndpointId(t.GetTarget.Post);
                if (origin == 0 || target == 0) continue;
                Register(TubingState.Tube(origin, (byte)t.GetOrigin.IndexOfThisPointOnThePost, target, (byte)t.GetTarget.IndexOfThisPointOnThePost), tube: t);
            }
        }
        private static TubingState Placed(TubingKind kind, SugarShack_TubingPost p) { var at = p.transform.position; return TubingState.Placed(kind, at.x, at.y, at.z, YawOf(p)); }
        // The post's Start has initialized its points and attached a spout to the native tree.
        // The pump is a fixed endpoint and must never become an ordinary placed-post record.
        internal void NativePostReady(SugarShack_TubingPost post)
        {
            if (!host || !Ready || !Placed(post) || byPost.ContainsKey(post)) return;
            try { Register(Placed(KindOf(post), post), post: post); }
            catch (Exception ex) { Debug.LogException(ex); nextCapture = 0; }
        }
        // Native roll placement assigns GetTarget last, after finishing the renderer, collider and
        // connection points. Publish only that completed tube, not the roll's unfinished preview.
        internal void NativeTubeReady(SugarShack_Tubing tube)
        {
            if (!host || !Ready || !Bound(tube) || !tube.gameObject.scene.IsValid() || byTube.ContainsKey(tube)) return;
            ulong origin = EndpointId(tube.GetOrigin.Post), target = EndpointId(tube.GetTarget.Post);
            if (origin == 0 || target == 0) return; // recovery discovery still handles unready endpoints
            try
            {
                Register(TubingState.Tube(origin, (byte)tube.GetOrigin.IndexOfThisPointOnThePost,
                    target, (byte)tube.GetTarget.IndexOfThisPointOnThePost), tube: tube);
            }
            catch (Exception ex) { Debug.LogException(ex); nextCapture = 0; }
        }
        private Binding Register(TubingState state, SugarShack_TubingPost? post = null, SugarShack_Tubing? tube = null)
        {
            ulong id = ++nextId;
            while (channel != null && channel.Records.ContainsKey(id)) id = ++nextId;
            var b = new Binding { Id = id, Post = post, Tube = tube, State = state };
            bindings.Add(id, b);
            captureBindings.Add(b);
            if (post != null) byPost.Add(post, b);
            if (tube != null) byTube.Add(tube, b);
            channel?.Register(new WorldRecord(id, 1, TubingState.Adapter, state.Encode()));
            return b;
        }
        private void Tombstone(Binding b)
        {
            if (!b.State.Exists) return;
            captureBindings.Remove(b);
            nativeRemovals.Remove(b);
            b.State = b.State.Removed();
            RetireBinding(b);
            if (b.RetireAfterOutputs) channel!.PublishAfterQueued(b.Id, b.State.Encode());
            else channel!.Publish(b.Id, b.State.Encode());
        }
        // Native completion callbacks only queue the exact identity. Drain on the next Update,
        // after every factory postfix has published any returned tool, regardless of patch order.
        internal void NativePostRemoved(SugarShack_TubingPost post)
        {
            if (!host || !Ready || post == null || !byPost.TryGetValue(post, out var b)) return;
            // Accepted guest commands retire immediately after the complete native call returns.
            // They still need output ordering, but must not also enroll deferred host work.
            if (applying) { b.RetireAfterOutputs = true; return; }
            QueueNativeRemoval(b);
        }
        internal void NativeTubeRemoved(SugarShack_Tubing tube)
        {
            if (!host || !Ready || applying || tube == null || !byTube.TryGetValue(tube, out var b)) return;
            QueueNativeRemoval(b);
        }
        private void QueueNativeRemoval(Binding b)
        {
            if (!b.State.Exists) return;
            b.RetireAfterOutputs = true;
            if (nativeRemovals.Count < 128) nativeRemovals.Add(b);
            else nextCapture = 0; // exceptionally large bursts use the existing capture workset
        }
        private void DrainNativeRemovals()
        {
            foreach (var b in nativeRemovals.Snapshot) Tombstone(b);
        }
        private void RetireBinding(Binding b)
        {
            if (b.Post != null || b.Tube != null) { retiredBindings.Add(b); return; }
            if (!ReferenceEquals(b.Post, null)) byPost.Remove(b.Post!);
            if (!ReferenceEquals(b.Tube, null)) byTube.Remove(b.Tube!);
            b.Post = null; b.Tube = null;
            retiredBindings.Remove(b);
        }
        private void PruneRetiredBindings()
        {
            foreach (var b in retiredBindings.Snapshot) RetireBinding(b);
        }
        private bool PeerPosition(uint peer, out Vector3 at)
        {
            at = default;
            if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2 || p.Pose.State != (byte)PlayerState.Walking) return false;
            at = new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z); return true;
        }
        [MonMP.Core.World.WorldCommandAdapters(TubingState.Adapter, TubingCommand.RootAdapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || !TubingCommand.Is(payload)) return;
            var command = TubingCommand.Decode(payload);
            try
            {
            if (!PeerPosition(peer, out var at))
            {
                if (command.Op == TubingOp.Connect) ConnectNote("reject peer pose; peer=" + peer + "; presence=" +
                    (presence.TryGetValue(peer, out var sampled) ? "age=" + (Time.unscaledTime - sampled.Time) + "; state=" + sampled.Pose.State : "missing"));
                return;
            }
            if (id == TubingCommand.Root)
            {
                if (command.Op == TubingOp.Place) Place(peer, at, command);
                else if (command.Op == TubingOp.Connect) Connect(peer, at, command);
                return;
            }
            if (command.Op != TubingOp.Remove || !bindings.TryGetValue(id, out var b) || !b.State.Exists) return;
            if (b.Post != null)
            {
                if (!b.Post.CanBeRemoved || (b.Post.transform.position - at).magnitude > Reach) return;
                var post = b.Post;
                // The native removal: the tool item drops back at the post (shared by the item discovery).
                applying = true; try { post.MouseRightDown(); } finally { applying = false; }
                Tombstone(b);
            }
            else if (b.Tube != null)
            {
                var tube = b.Tube; var a = tube.GetOrigin; var z = tube.GetTarget;
                if (a == null || z == null || !StartedPoint(a.Post, a) || !StartedPoint(z.Post, z) ||
                    a.ConnectedTo != z || z.ConnectedTo != a || !a.isConnected || !z.isConnected) return;
                var o = a.VisualSphere.transform.position; var t = z.VisualSphere.transform.position;
                if (DistanceToSegment(at, o, t) > TubeReach) return;
                if (Items == null || !Items.HostMutateLeasedTool(peer, command.Item, command.LeaseGeneration, typeof(Tool_TubingRoll),
                    roll =>
                    {
                        var originBefore = new PointSnapshot(a); var targetBefore = new PointSnapshot(z); float amount = roll.Value1;
                        try
                        {
                            // Hold the unpublished refund until native deletion succeeds.
                            roll.Value1 = Mathf.Min(roll.Value1_Max, amount + 1);
                            applying = true; try { tube.DeleteThisTube(); } finally { applying = false; }
                            return true;
                        }
                        catch
                        {
                            try { roll.Value1 = amount; }
                            finally { originBefore.Restore(); targetBefore.Restore(); }
                            throw;
                        }
                    }, publish: false)) return;
                Tombstone(b);
                RecountTrees();
            }
            }
            finally
            {
                // A refused prediction still needs an authoritative roll value; unchanged host
                // items would otherwise leave the native guest decrement/refund outstanding.
                if (command.Item != 0 && Items?.HostHasLeasedTool(peer, command.Item, command.LeaseGeneration, typeof(Tool_TubingRoll)) == true)
                    Items.PublishHostItem(command.Item);
            }
        }
        private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a; float t = ab.sqrMagnitude < 1e-6f ? 0 : Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
            return (p - (a + ab * t)).magnitude;
        }
        private void Place(uint peer, Vector3 at, TubingCommand command)
        {
            var point = new Vector3(command.X, command.Y, command.Z);
            if ((point - at).magnitude > Reach || (lastPlace.TryGetValue(peer, out float last) && Time.unscaledTime - last < 0.3f)) return;
            if (byPost.Keys.Any(p => p != null && (p.transform.position - point).magnitude < 0.3f)) return;
            var rotation = Rotation(command.Kind, command.Yaw);
            TreeBase? spoutTree = null;
            if (command.Kind == TubingKind.Post)
            {
                if (!Singleton<Gameplay>.i.IsOnHomeLand(point)) return;
            }
            else
            {
                // Resolve the same first tree native Start would attach to, then reserve it
                // synchronously. Position spacing cannot distinguish two heights on one maple.
                spoutTree = ResolveSpoutTree(point);
                if (spoutTree == null || spoutTree.GetTree_Type != Tree_Type.Maple || spoutTree.Get_SpoutConnected != null ||
                    point.y - spoutTree.transform.position.y < 0.8f || point.y - spoutTree.transform.position.y > 2.5f) return;
            }
            if (Items == null || !Items.TakeFrom(peer, command.Item, command.Kind == TubingKind.Post ? typeof(Tool_TubingPost) : typeof(Tool_TubingChalumeau))) return;
            lastPlace[peer] = Time.unscaledTime;
            var assets = Singleton<GameAssets>.i;
            var post = Object.Instantiate(command.Kind == TubingKind.Post ? assets.SugarShack_Placed_TubingPost : assets.SugarShack_Placed_Chalumeau, point, rotation).GetComponent<SugarShack_TubingPost>();
            if (spoutTree != null)
            {
                SpoutTree.SetValue(post, spoutTree);
                spoutTree.Chalumeau_Connect(post);
            }
            Register(TubingState.Placed(command.Kind, point.x, point.y, point.z, Mathf.Repeat(command.Yaw, 360)), post: post);
        }
        private static TreeBase? ResolveSpoutTree(Vector3 point)
        {
            foreach (var collider in Physics.OverlapBox(point, Vector3.one * 0.4f, Quaternion.identity, LayerMask.GetMask("Default")))
                if (collider.tag == "Tree" && collider.GetComponent<TreeBase>() is TreeBase tree && tree != null) return tree;
            return null;
        }
        private void Connect(uint peer, Vector3 at, TubingCommand command)
        {
            SugarShack_TubingPost? origin = EndpointPost(command.Origin), target = EndpointPost(command.Target);
            var a = Point(origin, command.OriginPoint); var b = Point(target, command.TargetPoint);
            if (a == null || b == null || !StartedPoint(origin, a) || !StartedPoint(target, b) || a.isConnected || b.isConnected || origin == target)
            {
                ConnectNote("reject endpoints; origin=" + command.Origin + "/" + command.OriginPoint + "; target=" + command.Target + "/" + command.TargetPoint +
                    "; originStarted=" + StartedPoint(origin, a) + "; targetStarted=" + StartedPoint(target, b) + "; originConnected=" + a?.isConnected + "; targetConnected=" + b?.isConnected);
                return;
            }
            var pa = a.VisualSphere.transform.position; var pb = b.VisualSphere.transform.position;
            if ((pa - pb).magnitude > MaxTube || ((pa - at).magnitude > TubeReach && (pb - at).magnitude > TubeReach))
            { ConnectNote("reject reach; length=" + (pa - pb).magnitude + "; originDistance=" + (pa - at).magnitude + "; targetDistance=" + (pb - at).magnitude + "; peerAt=" + at); return; }
            if (channel == null || channel.Records.Count >= SnapshotReceiver.MaxEntities) { ConnectNote("reject channel capacity"); return; }
            var originBefore = new PointSnapshot(a); var targetBefore = new PointSnapshot(b);
            SugarShack_Tubing? tube = null; InteractableTools? held = null; float amount = 0;
            if (Items == null || !Items.HostMutateLeasedTool(peer, command.Item, command.LeaseGeneration, typeof(Tool_TubingRoll),
                roll =>
                {
                    if (roll.Value1 < 1) return false;
                    held = roll; amount = roll.Value1;
                    tube = Build(origin!, target!, command.OriginPoint, command.TargetPoint);
                    try { roll.Value1 = amount - 1; }
                    catch
                    {
                        try { roll.Value1 = amount; }
                        finally { DiscardBuiltTube(tube, a, b, originBefore, targetBefore); }
                        throw;
                    }
                    return true;
                }, publish: false))
            {
                string itemRange = "missing items";
                if (Items != null) Items.HostItemInRange(peer, command.Item, 4.5f, out itemRange);
                ConnectNote("reject roll; item=" + command.Item + "; generation=" + command.LeaseGeneration +
                    "; leaseValid=" + (Items?.HostHasLeasedTool(peer, command.Item, command.LeaseGeneration, typeof(Tool_TubingRoll)) == true) +
                    "; value=" + Items?.ItemOf(command.Item)?.Value1 + "; nativeRollAt=" + Items?.ItemOf(command.Item)?.transform.position +
                    "; nativeRollDistance=" + (Items?.ItemOf(command.Item) is InteractableItems rollItem ? (rollItem.transform.position - at).magnitude : -1) + "; " + itemRange);
                return;
            }
            try { Register(TubingState.Tube(command.Origin, command.OriginPoint, command.Target, command.TargetPoint), tube: tube); }
            catch
            {
                // Register can notify subscribers after committing the channel record. A committed
                // record is an accepted tube; only an uncommitted registration is rolled back.
                if (tube != null && byTube.TryGetValue(tube, out var failed) && channel.Records.ContainsKey(failed.Id)) throw;
                if (tube != null && byTube.TryGetValue(tube, out var partial)) { byTube.Remove(tube); bindings.Remove(partial.Id); captureBindings.Remove(partial); }
                try { held!.Value1 = amount; }
                finally { if (tube != null) DiscardBuiltTube(tube, a, b, originBefore, targetBefore); }
                throw;
            }
            RecountTrees();
            ConnectNote("accepted; item=" + command.Item + "; generation=" + command.LeaseGeneration + "; rollValue=" + held!.Value1);
        }
        // The native save-load path: a tube object under the origin post, connected by point indexes.
        private static SugarShack_Tubing Build(SugarShack_TubingPost origin, SugarShack_TubingPost target, int originPoint, int targetPoint)
        {
            var a = Point(origin, originPoint); var b = Point(target, targetPoint);
            if (!StartedPoint(origin, a) || !StartedPoint(target, b)) throw new InvalidOperationException("Tubing endpoints have not started.");
            var originBefore = new PointSnapshot(a!); var targetBefore = new PointSnapshot(b!);
            var go = new GameObject("Tubing");
            try
            {
                go.transform.SetParent(origin.transform, false);
                var tube = go.AddComponent<SugarShack_Tubing>();
                tube.LoadConnect(origin, target, originPoint, targetPoint);
                return tube;
            }
            catch
            {
                originBefore.Restore(); targetBefore.Restore();
                go.SetActive(false); Object.Destroy(go);
                throw;
            }
        }
        private void DiscardBuiltTube(SugarShack_Tubing tube, TubingConnectionPoint a, TubingConnectionPoint b, PointSnapshot originBefore, PointSnapshot targetBefore)
        {
            originBefore.Restore(); targetBefore.Restore();
            tube.gameObject.SetActive(false); Object.Destroy(tube.gameObject);
            RefreshPost(a.Post); RefreshPost(b.Post);
        }
        private void RecountTrees() { treesDirty = true; nextTrees = Time.unscaledTime + 0.3f; }

        // ---- Guest controls ----------------------------------------------------------------------
        private bool Guest => !host && !disposed && session?.IsRunning == true;
        // A guest's post or spout click: the host spends the held tool and builds the post.
        internal bool Place(InteractableTools tool)
        {
            if (host && Ready && !applying && tool is Tool_TubingChalumeau && tool.isEquiped &&
                (bool)TempPlacedSpout.GetValue(tool) && (bool)SpotSpout.GetValue(tool))
            {
                // Native preview caches eligibility. A remote placement may have reserved this
                // tree since that preview, so the host click must recheck it before spending.
                var tree = ResolveSpoutTree(tool.transform.position);
                if (tree == null || tree.Get_SpoutConnected != null) return true;
            }
            if (!Guest || applying) return false;
            bool spout = tool is Tool_TubingChalumeau;
            bool ready = tool.isEquiped && (spout ? (bool)TempPlacedSpout.GetValue(tool) && (bool)SpotSpout.GetValue(tool) : (bool)TempPlacedPost.GetValue(tool));
            if (!ready || !Ready) return true;
            if (!spout && !Singleton<Gameplay>.i.IsOnHomeLand(tool.transform.position)) return false; // native error message
            ulong item = Items?.EntityOf(tool) ?? 0;
            if (item == 0) return true;
            var at = tool.transform.position;
            float yaw = spout ? Mathf.Repeat(Mathf.Atan2(-tool.transform.up.x, -tool.transform.up.z) * Mathf.Rad2Deg, 360) : Mathf.Repeat(tool.transform.eulerAngles.y, 360);
            channel!.Request(TubingCommand.Root, TubingCommand.Place(spout ? TubingKind.Spout : TubingKind.Post, at.x, at.y, at.z, yaw, item).Encode());
            return true;
        }
        internal bool ObserveHostNativeSpout => host && Ready;
        internal void AfterNativeSpoutPlacement(Vector3 point)
        {
            if (!ObserveHostNativeSpout) return;
            var tree = ResolveSpoutTree(point);
            if (tree == null || tree.Get_SpoutConnected != null) return;
            // Native MouseDown has just returned; Start has not run yet. Only the unbound spout
            // at this exact accepted click point belongs to that factory invocation.
            foreach (var post in Object.FindObjectsOfType<SugarShack_TubingPost>())
            {
                if (!Placed(post) || !post.GetIfIsAChalumeau || byPost.ContainsKey(post) ||
                    (post.transform.position - point).sqrMagnitude > MatchDistance * MatchDistance) continue;
                SpoutTree.SetValue(post, tree); tree.Chalumeau_Connect(post);
                Register(Placed(TubingKind.Spout, post), post: post);
                return;
            }
        }
        // A guest's post removal: sent to the host (the native path would drop a local tool item).
        internal bool RemovePost(SugarShack_TubingPost post)
        {
            if (!Guest || applying) return false;
            if (!byPost.TryGetValue(post, out var b) || !b.State.Exists) return true; // the pump inlet and unshared posts stay put
            if (!post.CanBeRemoved) return false; // native error message
            if (Ready) channel!.Request(b.Id, TubingCommand.Remove().Encode());
            return true;
        }
        // A guest's roll finished a tube: every new unbound local tube is sent to the host.
        internal void AfterConnect(Tool_TubingRoll roll)
        {
            if (!Guest || applying || !Ready) return;
            bool leased = Items != null && Items.TryGetLocalLease(roll, out _, out _);
            foreach (var t in Object.FindObjectsOfType<SugarShack_Tubing>())
            {
                if (!Bound(t) || byTube.ContainsKey(t) || pendingTubes.Any(p => p.Tube == t)) continue;
                ulong origin = EndpointId(t.GetOrigin.Post), target = EndpointId(t.GetTarget.Post);
                pendingTubes.Add((t, Time.unscaledTime));
                if (origin == 0 || target == 0 || !leased || !Items!.TryGetLocalLease(roll, out ulong item, out uint generation))
                { ConnectNote("guest did not send; origin=" + origin + "; target=" + target + "; leased=" + leased + "; transfersBlocked=" + Items?.TransfersBlocked); continue; } // undone when the window ends
                ConnectNote("guest sent; item=" + item + "; generation=" + generation + "; origin=" + origin + "; target=" + target);
                channel!.Request(TubingCommand.Root, TubingCommand.Connect(origin, (byte)t.GetOrigin.IndexOfThisPointOnThePost, target, (byte)t.GetTarget.IndexOfThisPointOnThePost, item, generation).Encode());
            }
        }
        // A guest's tube removal runs at once and is mirrored; the tube returns if the host keeps it.
        internal void RemoveTube(SugarShack_Tubing tube)
        {
            if (!Guest || applying || tube == null) return;
            pendingTubes.RemoveAll(p => p.Tube == tube);
            if (!byTube.TryGetValue(tube, out var b)) return;
            byTube.Remove(tube); b.Tube = null; MarkDirty(b); b.PendingUntil = Time.unscaledTime + PendingWindow;
            var roll = equippedRoll;
            if (Ready && roll != null && Items != null && Items.TryGetLocalLease(roll, out ulong item, out uint generation))
                channel!.Request(b.Id, TubingCommand.Remove(item, generation).Encode());
        }

        // Native Start hides every sphere. A newly replicated spout must join the held roll's
        // presentation after Start, and load/reconciliation must update the occupied points too.
        internal void RollEquipped(Tool_TubingRoll roll) => equippedRoll = roll;
        internal void RollUnequipped(Tool_TubingRoll roll) { if (equippedRoll == roll) equippedRoll = null; }
        internal void RefreshPost(SugarShack_TubingPost? post)
        {
            if (disposed || session?.IsRunning != true || post == null) return;
            if (post.GetAllConnection == null || post.GetAllConnection.Any(p => p.Post != post)) return; // Start has not run
            if (equippedRoll == null || !equippedRoll.isEquiped || MonMPPlugin.Instance?.Gameplay?.Player?.Get_ActiveTool != equippedRoll)
            { post.TubingRoll_UnEquiped(); return; }
            var selected = RollOrigin.GetValue(equippedRoll) as SugarShack_TubingPost_Connection;
            if (selected != null && selected.GetPost == post) { post.TubingRoll_UnEquiped(); return; } // unfinished origin stays hidden
            post.TubingRoll_Equiped();
        }
        internal void AfterRemoveTube(SugarShack_TubingPost? origin, SugarShack_TubingPost? target)
        {
            RefreshPost(origin); RefreshPost(target);
        }

        // ---- Guest apply -----------------------------------------------------------------------
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            var states = records.Values.Where(r => r.Adapter == TubingState.Adapter).Select(r => (r.Id, State: TubingState.Decode(r.Payload))).ToArray();
            if (Reconciled) { foreach (var (id, state) in states) Receive(id, state); return; }
            var posts = Object.FindObjectsOfType<SugarShack_TubingPost>().Where(Placed).ToList();
            applying = true;
            try
            {
                foreach (var (id, state) in states.Where(s => s.State.Kind != TubingKind.Tube))
                {
                    var b = new Binding { Id = id, State = state };
                    bindings[id] = b;
                    if (!state.Exists) continue;
                    var local = posts.FirstOrDefault(p => p != null && KindOf(p) == state.Kind && (p.transform.position - Position(state)).sqrMagnitude <= MatchDistance * MatchDistance);
                    if (local != null) { posts.Remove(local); b.Post = local; byPost[local] = b; Kept++; }
                    else { SpawnPost(b); Spawned++; }
                }
                var tubes = Object.FindObjectsOfType<SugarShack_Tubing>().Where(Bound).ToList();
                foreach (var (id, state) in states.Where(s => s.State.Kind == TubingKind.Tube))
                {
                    var b = new Binding { Id = id, State = state };
                    bindings[id] = b;
                    if (!state.Exists) continue;
                    var local = tubes.FirstOrDefault(t => state.Joins(EndpointId(t.GetOrigin.Post), t.GetOrigin.IndexOfThisPointOnThePost, EndpointId(t.GetTarget.Post), t.GetTarget.IndexOfThisPointOnThePost));
                    if (local != null) { tubes.Remove(local); b.Tube = local; byTube[local] = b; Kept++; }
                    else { MarkDirty(b); Spawned++; }
                }
                foreach (var t in tubes) if (t != null) { t.DeleteThisTube(); Removed++; }
                foreach (var p in posts) if (p != null) { DestroyPost(p); Removed++; }
                foreach (var b in dirtyBindings.Snapshot) Settle(b);
            }
            finally { applying = false; }
            Reconciled = true;
            treesDirty = true;

#if MONMP_DEVELOPMENT
            Report();
#endif

        }
        private void SpawnPost(Binding b)
        {
            var assets = Singleton<GameAssets>.i;
            var prefab = b.State.Kind == TubingKind.Spout ? assets.SugarShack_Placed_Chalumeau : assets.SugarShack_Placed_TubingPost;
            var post = Object.Instantiate(prefab, Position(b.State), Rotation(b.State.Kind, b.State.Yaw)).GetComponent<SugarShack_TubingPost>();
            b.Post = post; byPost[post] = b;
        }
        private static void DestroyPost(SugarShack_TubingPost p)
        {
            foreach (var point in p.GetAllConnection ?? new TubingConnectionPoint[0])
                if (point.isConnected && point.Cache_TubeRenderer != null && point.Cache_TubeRenderer.GetComponent<SugarShack_Tubing>() is SugarShack_Tubing t && t != null) t.DeleteThisTube();
            if (p.GetIfIsAChalumeau && SpoutTree.GetValue(p) is TreeBase tree && tree != null && tree.Get_SpoutConnected == p) tree.Chalumeau_Connect(null);
            p.gameObject.SetActive(false); Object.Destroy(p.gameObject);
        }
        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != TubingState.Adapter || !Reconciled) return;
            Receive(record.Id, TubingState.Decode(record.Payload));
        }
        private void Receive(ulong id, TubingState state)
        {
            if (!bindings.TryGetValue(id, out var b))
            {
                b = new Binding { Id = id, State = state };
                bindings[id] = b;
                if (!state.Exists) return;
                if (state.Kind == TubingKind.Tube)
                {
                    // A tube this guest drew: adopt the predicted local tube instead of building another.
                    int i = pendingTubes.FindIndex(p => p.Tube != null && state.Joins(EndpointId(p.Tube.GetOrigin.Post), p.Tube.GetOrigin.IndexOfThisPointOnThePost,
                        EndpointId(p.Tube.GetTarget.Post), p.Tube.GetTarget.IndexOfThisPointOnThePost));
                    if (i >= 0) { b.Tube = pendingTubes[i].Tube; byTube[b.Tube] = b; pendingTubes.RemoveAt(i); Adopted++; }
                }
            }
            b.State = state; MarkDirty(b);
            if (Time.unscaledTime >= b.PendingUntil) Settle(b);
        }
        private void Settle(Binding b)
        {
            try { SettleNative(b); }
            finally { if (!b.Dirty) dirtyBindings.Remove(b); }
        }
        private void MarkDirty(Binding b)
        {
            b.Dirty = true;
            dirtyBindings.Add(b);
        }
        private void SettleNative(Binding b)
        {
            applying = true;
            try
            {
                if (!b.State.Exists)
                {
                    b.Dirty = false;
                    if (b.Tube != null) { byTube.Remove(b.Tube); var t = b.Tube; b.Tube = null; t.DeleteThisTube(); treesDirty = true; }
                    if (b.Post != null) { byPost.Remove(b.Post); var p = b.Post; b.Post = null; DestroyPost(p); treesDirty = true; }
                    return;
                }
                if (b.State.Kind != TubingKind.Tube) { b.Dirty = false; if (b.Post == null) { SpawnPost(b); treesDirty = true; } return; }
                if (b.Tube != null) { b.Dirty = false; return; }
                SugarShack_TubingPost? origin = EndpointPost(b.State.Origin), target = EndpointPost(b.State.Target);
                var a = Point(origin, b.State.OriginPoint); var z = Point(target, b.State.TargetPoint);
                if (a == null || z == null || !StartedPoint(origin, a) || !StartedPoint(target, z)) return; // stays dirty until both endpoints start
                // A stale local tube on either point gives way to the host's tube.
                foreach (var point in new[] { a, z })
                    if (point.isConnected && point.Cache_TubeRenderer != null && point.Cache_TubeRenderer.GetComponent<SugarShack_Tubing>() is SugarShack_Tubing stale && stale != null)
                    { if (byTube.TryGetValue(stale, out var other)) { byTube.Remove(stale); other.Tube = null; MarkDirty(other); } pendingTubes.RemoveAll(p => p.Tube == stale); stale.DeleteThisTube(); }
                b.Tube = Build(origin!, target!, b.State.OriginPoint, b.State.TargetPoint); byTube[b.Tube] = b; b.Dirty = false; treesDirty = true;
                RefreshPost(origin); RefreshPost(target);
            }
            finally { applying = false; }
        }

        public void Update()
        {
            if (!Ready) return;
            float now = Time.unscaledTime;
            if (treesDirty && now >= nextTrees && Shack != null)
            {
                treesDirty = false; nextTrees = now + 0.5f;
                // Guests recount for the tube colours; the host's count is the shack record's. The walk
                // needs started posts: an unbuilt shack's pump inlet has none, and a post spawned this
                // frame starts on the next.
                if (Shack.gameObject.activeInHierarchy)
                {
                    try { Shack.UpdateTreeAmountConnected(); }
                    catch (NullReferenceException) { treesDirty = true; }
                }
            }
            if (!host)
            {
                foreach (var b in dirtyBindings.Snapshot)
                    if (b.Dirty && now >= b.PendingUntil) Settle(b);
                for (int i = pendingTubes.Count - 1; i >= 0; i--)
                {
                    var (tube, since) = pendingTubes[i];
                    if (tube == null) { pendingTubes.RemoveAt(i); continue; }
                    if (now - since < PendingWindow) continue;
                    // The host refused the tube: undo the prediction.
                    pendingTubes.RemoveAt(i);
                    applying = true; try { tube.DeleteThisTube(); } finally { applying = false; }
                    treesDirty = true;
                }
                return;
            }
            DrainNativeRemovals();
            // The periodic fallback walks posts and tubes on separate frames; each native
            // registry walk costs several milliseconds. Tubes follow their endpoints.
            if (tubeScanDue && WorldScans.TryClaim<SugarShack_Tubing>(true)) { tubeScanDue = false; DiscoverTubes(true); }
            bool periodic = nextCapture > 0;
            if (now < nextCapture || !WorldScans.TryClaim<SugarShack_TubingPost>(periodic)) return;
            nextCapture = periodic ? WorldScans.NextDeadline(now, 1f) : now + 1;
            DiscoverPosts(periodic);
            tubeScanDue = true;
            foreach (var b in captureBindings.Snapshot)
            {
                if (!b.State.Exists) continue;
                if (b.RetireAfterOutputs || (b.State.Kind == TubingKind.Tube ? b.Tube == null : b.Post == null)) Tombstone(b);
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            equippedRoll = null;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.CommandRequested -= Command; channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            captureBindings.Clear(); dirtyBindings.Clear(); retiredBindings.Clear(); nativeRemovals.Clear();
            session = null; channel = null;
        }

        // ---- Test hooks ------------------------------------------------------------------------

#if MONMP_DEVELOPMENT
        internal int TestAlive(TubingKind kind) => bindings.Values.Count(b => b.State.Exists && b.State.Kind == kind);


        internal int TestCaptureBindings => captureBindings.Count;


        internal int TestDirtyBindings => dirtyBindings.Count;


        internal ulong TestNewest(TubingKind kind) => bindings.Values.Where(b => b.State.Exists && b.State.Kind == kind).Select(b => b.Id).DefaultIfEmpty().Max();


        internal bool TestExists(ulong id) => bindings.TryGetValue(id, out var b) && b.State.Exists;


        internal TubingState TestState(ulong id) => bindings[id].State;


        internal SugarShack_TubingPost? TestPost(ulong id) => bindings.TryGetValue(id, out var b) ? b.Post : null;


        internal SugarShack_Tubing? TestTube(ulong id) => bindings.TryGetValue(id, out var b) ? b.Tube : null;


        internal bool TestPending => pendingTubes.Count > 0;


        internal SugarShack_TubingPost? TestPump => Pump;


        internal int TestTrees => Shack?.GetAmountOfMapleTreeConnected ?? -1;


        internal int TestLocalTubes => Object.FindObjectsOfType<SugarShack_Tubing>().Count(Bound);
#endif

        // A guest's placement click at a chosen ground point, as the equipped tool would send it.

#if MONMP_DEVELOPMENT
        internal void TestPlace(ulong item, TubingKind kind, Vector3 at, float yaw) => channel!.Request(TubingCommand.Root, TubingCommand.Place(kind, at.x, at.y, at.z, yaw, item).Encode());
#endif

        // The native roll's finished tube between two free points, followed by the same mirroring hook.

#if MONMP_DEVELOPMENT
        internal void TestDrawTube(SugarShack_TubingPost origin, int originPoint, SugarShack_TubingPost target, int targetPoint)
        {
            Build(origin, target, originPoint, targetPoint);
            if (equippedRoll == null) throw new InvalidOperationException("Tubing fixture must equip a leased native roll.");
            AfterConnect(equippedRoll);
        }
#endif

    }
}
