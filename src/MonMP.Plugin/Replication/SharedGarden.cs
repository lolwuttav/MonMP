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
    // The host's ground garden: every dirt patch dug with the shovel and the plant growing in it. The
    // host alone runs the native minute tick (growth, drying, rain, pests); guests show replicas.
    // A guest's plant, water, fertilizer, pesticide, harvest and shovel actions run natively at once
    // as a prediction and are mirrored to the host, which re-runs them on its patch; a guest's dig is
    // predicted the same way and adopted when the host's new patch record arrives. Outdoor planter
    // patches are records too, keyed by their planter's shared item id; the planter itself (carry,
    // pose, freeze) is a shared item, and guests find the patch through that item's replica.
    internal sealed class SharedGarden : IDisposable
    {
        private sealed class Binding
        {
            public ulong Id, Item;
            public Garden_DirtPatch? Patch;
            public GardenState State;
            public float PendingUntil, LastCommand;
            public GardenOp LastOp;
            public bool Dirty;
        }
        private static readonly FieldInfo SeedsField = AccessTools.Field(typeof(Garden_DirtPatch), "seedsType");
        private static readonly FieldInfo WateredField = AccessTools.Field(typeof(Garden_DirtPatch), "WateredTick");
        private static readonly FieldInfo InfestedField = AccessTools.Field(typeof(Garden_DirtPatch), "isInfested");
        private static readonly FieldInfo FertilizedField = AccessTools.Field(typeof(Garden_DirtPatch), "isFertilized");
        private static readonly FieldInfo FullyGrownField = AccessTools.Field(typeof(Garden_DirtPatch), "AlreadyFullyGrown");
        private static readonly FieldInfo GrowField = AccessTools.Field(typeof(Garden_DirtPatch), "GrowPercent");
        private static readonly FieldInfo ReadyField = AccessTools.Field(typeof(Garden_DirtPatch), "ReadyToHarvest");
        private static readonly FieldInfo UprightField = AccessTools.Field(typeof(Garden_DirtPatch), "isUpright");
        private static readonly FieldInfo AmountField = AccessTools.Field(typeof(Garden_DirtPatch), "AmountToHarvest");
        private static readonly FieldInfo DeadField = AccessTools.Field(typeof(Garden_DirtPatch), "HARDMODE_IsPlantDead");
        private static readonly FieldInfo VisualField = AccessTools.Field(typeof(Garden_DirtPatch), "VisualPlant");
        private static readonly FieldInfo InfestationField = AccessTools.Field(typeof(Garden_DirtPatch), "GameObject_Infestation");
        private static readonly FieldInfo MeshField = AccessTools.Field(typeof(Garden_DirtPatch), "mesh");
        private static readonly MethodInfo UpdateVisualMethod = AccessTools.Method(typeof(Garden_DirtPatch), "UpdateVisualPlant");
        private static readonly MethodInfo UpdateDirtMethod = AccessTools.Method(typeof(Garden_DirtPatch), "UpdateDirt");
        private static readonly MethodInfo PlantDieMethod = AccessTools.Method(typeof(Garden_DirtPatch), "PlantDie");
        private static readonly FieldInfo StormField = AccessTools.Field(typeof(Garden_DirtPatch), "uniStormSystem");
        private static readonly MethodInfo DayChangedMethod = AccessTools.Method(typeof(Garden_DirtPatch), "DayChanged");
        private const ulong PlanterBit = 0x1000000000000000UL, ItemMask = 0x0FFFFFFFFFFFFFFFUL;
        internal static ulong PlanterId(ulong item) => GardenState.IdBase | PlanterBit | (item & ItemMask);
        private static ulong PlanterItem(ulong id) => (id & PlanterBit) != 0 ? WorldRecord.DynamicIdStart | (id & ItemMask) : 0;
        internal SharedItems? Items { get; set; }
        private const float MatchDistance = 0.05f, Reach = 6, PredictionWindow = 2.5f;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly BindingWorkSet<Binding> updateBindings = new BindingWorkSet<Binding>();
        private readonly BindingWorkSet<Binding> retiredBindings = new BindingWorkSet<Binding>();
        private readonly Dictionary<Garden_DirtPatch, Binding> byPatch = new Dictionary<Garden_DirtPatch, Binding>();
        private readonly List<ulong> planterDiscoveryScratch = new List<ulong>();
        private readonly List<(Garden_DirtPatch Patch, float Since)> pendingDigs = new List<(Garden_DirtPatch, float)>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, float> lastDig = new Dictionary<uint, float>();
        private readonly Dictionary<uint, float> lastSpray = new Dictionary<uint, float>();
        private readonly bool host;
        private readonly string diagnostics;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, disposed;
        private ulong nextId = GardenState.IdBase;
        private float nextCapture, nextDiscover, nextVisualCheck;
        private bool periodicDiscoveryStarted;
        public bool Reconciled { get; private set; }
        public int Kept { get; private set; }
        public int Removed { get; private set; }
        public int Spawned { get; private set; }
        public int Adopted { get; private set; }
        internal bool Applying => applying;
        private bool Ready => !disposed && session?.IsRunning == true && channel?.Ready == true;
        public SharedGarden(bool host, string diagnostics)
        {
            this.host = host; this.diagnostics = diagnostics;
            if (host) Discover();

#if MONMP_DEVELOPMENT
            Report();
#endif

        }

#if MONMP_DEVELOPMENT
        private void Report() => File.WriteAllText(Path.Combine(diagnostics, "shared-garden.txt"),
            "Role=" + (host ? "host" : "guest") + "\nPatches=" + bindings.Values.Count(b => b.State.Exists) + "\nPlanted=" + bindings.Values.Count(b => b.State.Exists && b.State.Seed != 0) +
            "\nRecords=" + bindings.Count + "\nReconciled=" + Reconciled + "\nKept=" + Kept + "\nRemoved=" + Removed + "\nSpawned=" + Spawned + "\nAdopted=" + Adopted);
#endif

        public IEnumerable<WorldRecord> RootRecords() { yield return GardenCommand.RootRecord(); }
        public IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, GardenState.Adapter, b.State.Encode()));
        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.CommandRequested += Command; channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) { presence.Remove(peer); lastDig.Remove(peer); lastSpray.Remove(peer); }

        private static bool Ground(Garden_DirtPatch p) => p != null && !p.Get_isOutdoorPlanter && p.gameObject.scene.IsValid();
        private static GardenState Capture(Garden_DirtPatch p)
        {
            var flags = GardenFlags.None;
            if (p.Get_isInfested) flags |= GardenFlags.Infested;
            if (p.Get_isFertilized) flags |= GardenFlags.Fertilized;
            if (p.Get_AlreadyFullyGrown) flags |= GardenFlags.FullyGrown;
            if ((bool)ReadyField.GetValue(p)) flags |= GardenFlags.Ready;
            if (p.Get_HARDMODE_IsPlantDead) flags |= GardenFlags.Dead;
            var t = p.transform; var at = t.position;
            return new GardenState(true, (byte)Mathf.Clamp(p.Get_IntSeedsType, 0, 3), flags, Mathf.Clamp01(p.Get_GrowPercent), Mathf.Clamp(p.Get_WateredTick, -1, 1000000),
                (short)Mathf.Clamp(p.Get_AmountToHarvest, 0, 1000), at.x, at.y, at.z, t.eulerAngles.y);
        }
        private static bool Same(GardenState a, GardenState b) => a.Exists == b.Exists && a.Seed == b.Seed && a.Flags == b.Flags && a.WateredTick == b.WateredTick &&
            a.Amount == b.Amount && Mathf.Abs(a.Grow - b.Grow) < 0.002f && (a.Grow >= 1) == (b.Grow >= 1) && (a.Grow >= 0.25f) == (b.Grow >= 0.25f);
        private static Vector3 Position(GardenState s) => new Vector3(s.X, s.Y, s.Z);

        // ---- Host ------------------------------------------------------------------------------
        private void Discover(bool periodic = false)
        {
            PruneRetiredBindings();
            foreach (var p in WorldScans.Find<Garden_DirtPatch>(periodic))
                if (p != null && !byPatch.ContainsKey(p) && Ground(p)) Register(p);
            if (Items == null) return;
            Items.SnapshotOutdoorPlanterIds(planterDiscoveryScratch);
            try
            {
                foreach (var item in planterDiscoveryScratch)
                {
                    // Recheck the current binding after any earlier registration callback. A
                    // native replacement, removal or late DirtPatch keeps the existing retry.
                    if (Items.ItemOf(item) is not Garden_OutdoorPlanter planter || planter == null || planter.DirtPatch == null ||
                        byPatch.ContainsKey(planter.DirtPatch) || bindings.ContainsKey(PlanterId(item))) continue;
                    var b = new Binding { Id = PlanterId(item), Item = item, Patch = planter.DirtPatch, State = Capture(planter.DirtPatch) };
                    bindings.Add(b.Id, b); byPatch.Add(planter.DirtPatch, b);
                    updateBindings.Add(b);
                    channel?.Register(new WorldRecord(b.Id, 1, GardenState.Adapter, b.State.Encode()));
                }
            }
            finally { planterDiscoveryScratch.Clear(); }
        }
        private Binding Register(Garden_DirtPatch p)
        {
            ulong id = ++nextId;
            while (channel != null && channel.Records.ContainsKey(id)) id = ++nextId;
            var b = new Binding { Id = id, Patch = p, State = Capture(p) };
            bindings.Add(id, b); byPatch.Add(p, b);
            updateBindings.Add(b);
            channel?.Register(new WorldRecord(id, 1, GardenState.Adapter, b.State.Encode()));
            return b;
        }
        internal void NativeGroundPatchReady(Garden_DirtPatch patch)
        {
            // A shovel finalizes its mound's pose before native Start runs next frame.
            // Register that exact ready patch instead of waiting for the one-second scan.
            // Guest Dig already registered its host result; loaded patches and planters
            // keep their existing identities and never allocate a duplicate record.
            if (!host || applying || !Ready || !Ground(patch) || byPatch.ContainsKey(patch)) return;
            try { Register(patch); }
            catch (Exception ex) { Debug.LogException(ex); nextDiscover = 0; }
        }
        internal void NativePatchStarted(Garden_DirtPatch patch)
        {
            if (host) { NativeGroundPatchReady(patch); return; }
            if (disposed || patch == null || !byPatch.TryGetValue(patch, out var b) || b.Patch != patch || !b.State.Exists) return;
            // Native Start hides infestation after an early snapshot apply. Reconcile the
            // bound guest patch on the next ready update, preserving native initialization
            // and any prediction window. Unbound planters/digs apply when they bind later.
            b.Dirty = true;
        }
        private void Publish(Binding b, GardenState state)
        {
            b.State = state;
            if (!state.Exists)
            {
                updateBindings.Remove(b);
                RetireBinding(b);
            }
            channel!.Publish(b.Id, state.Encode());
        }
        private void RetireBinding(Binding b)
        {
            if (b.Patch != null) { retiredBindings.Add(b); return; }
            if (!ReferenceEquals(b.Patch, null)) byPatch.Remove(b.Patch!);
            b.Patch = null;
            retiredBindings.Remove(b);
        }
        private void PruneRetiredBindings()
        {
            foreach (var b in retiredBindings.Snapshot) RetireBinding(b);
        }
        private bool PeerPosition(uint peer, out Vector3 at)
        {
            at = default;
            if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2) return false;
            at = new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z); return true;
        }
        [MonMP.Core.World.WorldCommandAdapters(GardenState.Adapter, GardenCommand.RootAdapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0) return;
            if (id == GardenCommand.Root)
            {
                var rootCommand = GardenCommand.Decode(payload);
                if (rootCommand.Op == GardenOp.Spray) Spray(peer); else Dig(peer, rootCommand);
                return;
            }
            if (!bindings.TryGetValue(id, out var b) || !b.State.Exists || b.Patch == null) return;
            var command = GardenCommand.Decode(payload);
            var p = b.Patch;
            if (!PeerPosition(peer, out var at) || (p.transform.position - at).magnitude > Reach) return;
            applying = true;
            try
            {
                switch (command.Op)
                {
                    case GardenOp.Plant:
                        HostPlant(peer, p, (SeedsType)command.Seed, command.SeedPack, command.SeedGeneration);
                        break;
                    case GardenOp.Water: p.Water(); break;
                    case GardenOp.Fertilize:
                        HostFertilize(peer, p);
                        break;
                    case GardenOp.Desinfect: p.Desinfect(); break;
                    case GardenOp.Harvest:
                    {
                        // A dead plant clears without a basket; a crop needs room in the guest's basket.
                        if (p.Get_HARDMODE_IsPlantDead) p.Harvest();
                        else if (Items?.HostHarvestIntoBasket(peer, p) == true)
                            GameManager.Get_PlayerStats[10]++; // The host accepts this plant exactly once.
                        break;
                    }
                    case GardenOp.Clear: p.RemovePlantFromShovel(); break;
                    case GardenOp.Remove:
                        if (p.Get_SeedsType != SeedsType.Useless || b.Item != 0) return;
                        DestroyReplica(p);
                        Publish(b, b.State.Removed());
                        return;
                    default: return;
                }
            }
            finally { applying = false; }
            Publish(b, Capture(p));
        }

        private void HostFertilize(uint peer, Garden_DirtPatch patch)
        {
            // Match native eligibility before spending; a refused dose must leave the plant
            // unchanged. The existing resource helper retains its own unchanged validation.
            if (patch.Get_SeedsType == SeedsType.Useless || (bool)FertilizedField.GetValue(patch) ||
                (bool)ReadyField.GetValue(patch)) return;
            if (Items?.HostDebitHeldTool<Tool_FertilizerPack>(peer, 0.05f, destroyWhenEmpty: true) == true)
                patch.Fertilize();
        }
        // A pesticide spray costs its dose whether or not it hits an infested patch.
        private void Spray(uint peer)
        {
            if (lastSpray.TryGetValue(peer, out float last) && Time.unscaledTime - last < 1.8f) return;
            lastSpray[peer] = Time.unscaledTime;
            Items?.HostDebitHeldTool<Tool_PesticideSolution>(peer, 0.02f, destroyWhenEmpty: false);
        }
        internal void MirrorSpray()
        {
            if (host || !Ready) return;
            channel!.Request(GardenCommand.Root, new GardenCommand(GardenOp.Spray).Encode());
        }
        private void Dig(uint peer, GardenCommand command)
        {
#if MONMP_DEVELOPMENT
            TestRecordDigAdmission(peer, command);
#endif
            if (command.Op != GardenOp.Dig || !PeerPosition(peer, out var at)) return;
            var point = new Vector3(command.X, command.Y, command.Z);
            if ((point - at).magnitude > Reach || (lastDig.TryGetValue(peer, out float last) && Time.unscaledTime - last < 0.3f)) return;
            if (!Singleton<Gameplay>.i.IsOnHomeLand(point)) return;
            foreach (var other in byPatch.Keys) if (other != null && (other.transform.position - point).magnitude < 0.5f) return;
            lastDig[peer] = Time.unscaledTime;
            // The native shovel's spawn: the prefab at the ground point, turned to face the digger.
            var p = Object.Instantiate(Singleton<GameAssets>.i.Garden_DirtPatch);
            p.transform.position = point; p.transform.rotation = Quaternion.Euler(0, command.Yaw, 0);
            Register(p);
        }

        // ---- Guest prediction mirroring ---------------------------------------------------------
        // Guests never run the native minute tick or day roll on ground patches.
        internal bool GuestBlocks(Garden_DirtPatch patch) => !host && !disposed && session?.IsRunning == true && patch != null && (Ground(patch) || patch.Get_isOutdoorPlanter);
        internal bool RefreshGuestOrientation(Garden_DirtPatch patch)
        {
            if (!GuestBlocks(patch) || !patch.Get_isOutdoorPlanter) return false;
            bool upright = Vector3.Dot(patch.transform.up, Vector3.down) < -0.5f;
            if (UprightField.GetValue(patch) is bool previous && previous == upright) return false;
            UprightField.SetValue(patch, upright);
            return true;
        }
        private void HostPlant(uint peer, Garden_DirtPatch patch, SeedsType seed, ulong seedPack, uint generation)
        {
            if (seed == SeedsType.Useless || patch.Get_SeedsType != SeedsType.Useless) return;
            // The item debit can refuse even when a matching pack exists (stale hold pose,
            // ownership or empty pack). Never create a free plant on that failed debit.
            if (Items?.HostConsumeHeldSeeds(peer, seed, seedPack, generation) == true) patch.Plant(seed);
        }

        internal void Mirror(Garden_DirtPatch patch, GardenOp op, SeedsType seed = SeedsType.Useless)
        {
            if (host || applying || !Ready || patch == null || !byPatch.TryGetValue(patch, out var b) || !b.State.Exists) return;
            float now = Time.unscaledTime;
            if ((op == GardenOp.Water || op == GardenOp.Fertilize || op == GardenOp.Desinfect) && op == b.LastOp && now - b.LastCommand < 0.5f) return;
            b.LastCommand = now; b.LastOp = op; b.PendingUntil = now + PredictionWindow;
            if (op == GardenOp.Fertilize || op == GardenOp.Plant) b.Dirty = true; // A refusal can leave the host record unchanged.
            ulong seedPack = 0; uint generation = 0;
            if (op == GardenOp.Plant && (Items == null || !Items.GuestSeedPlanted(seed, out seedPack, out generation))) return;
            if (op == GardenOp.Remove) { byPatch.Remove(patch); b.Patch = null; }
            channel!.Request(b.Id, new GardenCommand(op, (byte)seed, seedPack: seedPack, seedGeneration: generation).Encode(), true);
        }
        internal void AfterNative(Garden_DirtPatch patch, GardenOp op)
        {
            if (op == GardenOp.Remove && !disposed && session?.IsRunning == true) UnsubscribeDay(patch);
        }
        // Native OnDestroy removes its minute callback only. The cached weather owner also
        // owns the daily action, including when SharedItems destroys an outdoor planter.
        internal static void NativePatchDestroyed(Garden_DirtPatch patch)
        {
            UnsubscribeDay(patch);
        }
        // Runs after the native shovel's left click: any new unbound ground patch is this guest's dig.
        internal void AfterShovel()
        {
            if (host || applying || !Ready) return;
            foreach (var p in Object.FindObjectsOfType<Garden_DirtPatch>())
            {
                if (!Ground(p) || byPatch.ContainsKey(p) || pendingDigs.Any(d => d.Patch == p)) continue;
                pendingDigs.Add((p, Time.unscaledTime));
                var at = p.transform.position;
                channel!.Request(GardenCommand.Root, new GardenCommand(GardenOp.Dig, 0, at.x, at.y, at.z, p.transform.eulerAngles.y).Encode());
            }
        }

        // ---- Guest apply -----------------------------------------------------------------------
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            var states = records.Values.Where(r => r.Adapter == GardenState.Adapter).Select(r => (r.Id, State: GardenState.Decode(r.Payload))).ToArray();
            if (Reconciled) { foreach (var (id, state) in states) Receive(id, state); return; }
            var locals = Object.FindObjectsOfType<Garden_DirtPatch>().Where(Ground).ToList();
            applying = true;
            try
            {
                foreach (var (id, state) in states)
                {
                    var b = new Binding { Id = id, Item = PlanterItem(id), State = state };
                    bindings[id] = b;
                    if (b.Item != 0) { b.Dirty = true; updateBindings.Add(b); continue; } // settled once the planter's replica resolves
                    if (!state.Exists) continue;
                    updateBindings.Add(b);
                    var local = locals.FirstOrDefault(p => p != null && (p.transform.position - Position(state)).sqrMagnitude <= MatchDistance * MatchDistance);
                    if (local != null) { locals.Remove(local); b.Patch = local; byPatch[local] = b; Kept++; }
                    else { Spawn(b); Spawned++; }
                    ApplyNative(b.Patch!, state);
                }
                foreach (var p in locals) if (p != null) { DestroyReplica(p); Removed++; }
            }
            finally { applying = false; }
            Reconciled = true;

#if MONMP_DEVELOPMENT
            Report();
#endif

        }
        private void Spawn(Binding b)
        {
            var p = Object.Instantiate(Singleton<GameAssets>.i.Garden_DirtPatch, Position(b.State), Quaternion.Euler(0, b.State.Yaw, 0));
            b.Patch = p; byPatch[p] = b;
        }
        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != GardenState.Adapter || !Reconciled) return;
            Receive(record.Id, GardenState.Decode(record.Payload));
        }
        private void Receive(ulong id, GardenState state)
        {
            if (!bindings.TryGetValue(id, out var b))
            {
                b = new Binding { Id = id, Item = PlanterItem(id), State = state };
                bindings[id] = b;
                if (!state.Exists) return;
                // A patch this guest dug: adopt its predicted local patch instead of spawning another.
                int dig = pendingDigs.FindIndex(d => d.Patch != null && (d.Patch.transform.position - Position(state)).sqrMagnitude <= MatchDistance * MatchDistance);
                if (dig >= 0) { b.Patch = pendingDigs[dig].Patch; byPatch[b.Patch] = b; pendingDigs.RemoveAt(dig); Adopted++; }
            }
            b.State = state; b.Dirty = true;
            updateBindings.Add(b);
            if (Time.unscaledTime >= b.PendingUntil) Settle(b);
        }
        private void Settle(Binding b)
        {
            if (b.Item != 0)
            {
                // The planter item owns the patch object; only its garden state is applied here.
                if (!b.State.Exists) { if (!ReferenceEquals(b.Patch, null)) byPatch.Remove(b.Patch!); b.Patch = null; b.Dirty = false; updateBindings.Remove(b); return; }
                if (b.Patch == null)
                {
                    if (!ReferenceEquals(b.Patch, null)) byPatch.Remove(b.Patch!);
                    b.Patch = null;
                    if (Items?.ItemOf(b.Item) is Garden_OutdoorPlanter planter && planter.DirtPatch != null) { b.Patch = planter.DirtPatch; byPatch[b.Patch] = b; }
                }
                if (b.Patch == null) return; // stays dirty until the replica exists
            }
            b.Dirty = false;
            applying = true;
            try
            {
                if (!b.State.Exists)
                {
                    if (b.Patch != null) { byPatch.Remove(b.Patch); DestroyReplica(b.Patch); b.Patch = null; }
                    else if (!ReferenceEquals(b.Patch, null)) { byPatch.Remove(b.Patch!); b.Patch = null; }
                    updateBindings.Remove(b);
                    return;
                }
                if (b.Patch == null) Spawn(b);
                ApplyNative(b.Patch!, b.State);
            }
            finally { applying = false; }
        }
        private static void ApplyNative(Garden_DirtPatch p, GardenState s)
        {
            if (MeshField.GetValue(p) == null) MeshField.SetValue(p, p.GetComponent<MeshRenderer>());
            var seed = (SeedsType)s.Seed;
            var local = p.Get_SeedsType;
            bool localReady = (bool)ReadyField.GetValue(p);
            if (local != SeedsType.Useless && p.Get_HARDMODE_IsPlantDead && !s.Has(GardenFlags.Dead))
            {
                // PlantDie tints private materials; clearing its flag cannot restore them.
                // Rebuild from the native prefab before replaying the host's growth/readiness.
                if (VisualField.GetValue(p) is Component) p.RemovePlantFromShovel();
                else SeedsField.SetValue(p, SeedsType.Useless);
                DeadField.SetValue(p, false);
                local = SeedsType.Useless;
            }
            if (local != seed)
            {
                if (local != SeedsType.Useless)
                {
                    if (VisualField.GetValue(p) is Component) p.RemovePlantFromShovel();
                    else SeedsField.SetValue(p, SeedsType.Useless);
                }
                if (seed != SeedsType.Useless) p.Plant(seed);
            }
            else if (seed != SeedsType.Useless && (VisualField.GetValue(p) as Component) == null)
            {
                // The seed matches but its plant model is gone (a replica rebuilt from item values, or a
                // local harvest the host refused): the mound would say "ready" with nothing to click.
                SeedsField.SetValue(p, SeedsType.Useless);
                p.Plant(seed);
            }
            else if (seed == SeedsType.Coffee && localReady && !s.Has(GardenFlags.Ready) && s.Has(GardenFlags.FullyGrown) && VisualField.GetValue(p) is Component)
                p.Harvest(); // the native coffee re-grow: the bush stays, its harvest state resets
            GrowField.SetValue(p, s.Grow);
            FullyGrownField.SetValue(p, s.Has(GardenFlags.FullyGrown));
            AmountField.SetValue(p, (int)s.Amount);
            InfestedField.SetValue(p, s.Has(GardenFlags.Infested));
            FertilizedField.SetValue(p, s.Has(GardenFlags.Fertilized));
            WateredField.SetValue(p, s.WateredTick);
            var visual = VisualField.GetValue(p) as Garden_PlantReadyToHarvest;
            if (!s.Has(GardenFlags.Ready) && (bool)ReadyField.GetValue(p))
            {
                ReadyField.SetValue(p, false);
                if (visual != null) visual.Update_IsReadyToHarvest(false);
            }
            bool dead = p.Get_HARDMODE_IsPlantDead;
            if (s.Has(GardenFlags.Dead) && !dead && visual != null) PlantDieMethod.Invoke(p, null);
            else if (!s.Has(GardenFlags.Dead) && dead) DeadField.SetValue(p, false);
            UpdateVisualMethod.Invoke(p, null);
            // The native visual update only scales a plant still growing; one (re)planted here starts at
            // zero scale, so a fully grown plant would stay invisible and unclickable.
            if (VisualField.GetValue(p) is Component grown && grown != null && (s.Grow >= 1f || s.Has(GardenFlags.FullyGrown)))
                grown.transform.localScale = Vector3.one;
            UpdateDirtMethod.Invoke(p, null);
            if (InfestationField.GetValue(p) is GameObject pests && pests != null) pests.SetActive(s.Has(GardenFlags.Infested) && s.Grow < 1 && !s.Has(GardenFlags.Dead));
        }

        private void DiscoverIfDue(float now)
        {
            bool periodic = periodicDiscoveryStarted && nextDiscover > 0;
            if (now < nextDiscover || !WorldScans.TryClaim<Garden_DirtPatch>(periodic)) return;
            // Keep the initial ready scan immediate, then separate this fallback
            // from the other world scans without changing its one-second cadence.
            nextDiscover = periodic ? WorldScans.NextDeadline(now, 1f) : now + (periodicDiscoveryStarted ? 1f : 0.25f);
            periodicDiscoveryStarted = true;
            Discover(periodic);
        }
        public void Update()
        {
            if (!Ready) return;
            float now = Time.unscaledTime;
            if (!host)
            {
                var player = Singleton<Gameplay>.i?.Player;
                if (player?.Get_CurrentInteractable is Garden_DirtPatch hovered && RefreshGuestOrientation(hovered))
                    player.ResetCurrentInteractable(hovered);
                foreach (var b in updateBindings.Snapshot)
                {
                    // A planter replica respawned (or arrived after its garden record): re-apply its state.
                    if (b.Item != 0 && b.State.Exists && b.Patch == null) b.Dirty = true;
                    // A planted mound whose plant model is gone or shrunk to nothing: rebuild it.
                    if (now >= nextVisualCheck && b.State.Exists && b.Patch != null && b.State.Seed != (byte)SeedsType.Useless &&
                        !(VisualField.GetValue(b.Patch) is Component plant && plant != null && plant.transform.localScale.sqrMagnitude > 1e-4f)) b.Dirty = true;
                    if (b.Dirty && now >= b.PendingUntil) Settle(b);
                }
                if (now >= nextVisualCheck) nextVisualCheck = now + 2f;
                for (int i = pendingDigs.Count - 1; i >= 0; i--)
                {
                    var (patch, since) = pendingDigs[i];
                    if (patch == null) { pendingDigs.RemoveAt(i); continue; }
                    if (now - since < 4) continue;
                    // The host refused the dig: undo the prediction.
                    pendingDigs.RemoveAt(i);
                    applying = true;
                    try { DestroyReplica(patch); } finally { applying = false; }
                }
                return;
            }
            DiscoverIfDue(now);
            if (now < nextCapture) return;
            nextCapture = now + 0.25f;
            foreach (var b in updateBindings.Snapshot)
            {
                if (!b.State.Exists) continue;
                if (b.Patch == null) { Publish(b, b.State.Removed()); continue; }
                var state = Capture(b.Patch);
                if (!Same(state, b.State)) Publish(b, state);
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.CommandRequested -= Command; channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            updateBindings.Clear(); retiredBindings.Clear();
            session = null; channel = null;
        }

        // Native Start adds a daily UnityEvent listener, but RemoveWithAShovel and OnDestroy
        // detach only the minute tick. Remove this patch's exact listener from its own source.
        private static void UnsubscribeDay(Garden_DirtPatch? patch)
        {
            if (ReferenceEquals(patch, null)) return;
            if (StormField.GetValue(patch) is UniStorm.UniStormSystem storm && storm != null)
                storm.OnDayChangeEvent.RemoveListener((UnityEngine.Events.UnityAction)Delegate.CreateDelegate(
                    typeof(UnityEngine.Events.UnityAction), patch, DayChangedMethod));
        }
        private static void DestroyReplica(Garden_DirtPatch patch)
        {
            UnsubscribeDay(patch);
            patch.RemoveWithAShovel();
            Object.Destroy(patch.gameObject);
        }

        // ---- Test hooks ------------------------------------------------------------------------

#if MONMP_DEVELOPMENT
        internal ulong TestNewest => bindings.Values.Where(b => b.State.Exists).Select(b => b.Id).DefaultIfEmpty().Max();


        internal int TestAlive => bindings.Values.Count(b => b.State.Exists);


        internal GardenState TestState(ulong id) => bindings[id].State;


        internal bool TestExists(ulong id) => bindings.TryGetValue(id, out var b) && b.State.Exists;


        internal Garden_DirtPatch? TestPatch(ulong id) => bindings.TryGetValue(id, out var b) ? b.Patch : null;


        internal bool TestReady(ulong id) => TestPatch(id) is Garden_DirtPatch p && (bool)ReadyField.GetValue(p);


        internal bool TestPending => pendingDigs.Count > 0;


        internal int TestUpdateBindings => updateBindings.Count;


        internal bool TestPlanterBound(ulong item) => bindings.TryGetValue(PlanterId(item), out var b) && b.Patch != null && b.State.Exists;

        internal uint TestRevision(ulong id) => channel!.Records[id].Revision;
        internal bool TestNativeStarted(ulong id) => TestPatch(id) is Garden_DirtPatch p && StormField.GetValue(p) != null;
        internal bool TestInfestationVisible(ulong id) => TestPatch(id) is Garden_DirtPatch p &&
            InfestationField.GetValue(p) is GameObject pests && pests.activeSelf && pests.activeInHierarchy;
        internal bool TestPredictionPending(ulong id) => Time.unscaledTime < bindings[id].PendingUntil;
        internal void TestWriteStartEvidence(string evidence) => File.WriteAllText(Path.Combine(diagnostics, "paired-garden-start.txt"), evidence);

        private void TestRecordDigAdmission(uint peer, GardenCommand command)
        {
            if (MonMPPlugin.Instance?.TestOptions.Enabled != true) return;
            var point = new Vector3(command.X, command.Y, command.Z);
            bool present = PeerPosition(peer, out var at), home = Singleton<Gameplay>.i.IsOnHomeLand(point);
            bool cooling = lastDig.TryGetValue(peer, out float last) && Time.unscaledTime - last < 0.3f;
            bool nearby = byPatch.Keys.Any(p => p != null && (p.transform.position - point).magnitude < 0.5f);
            bool known = presence.TryGetValue(peer, out var seen);
            float age = known ? Time.unscaledTime - seen.Time : -1;
            string lastPose = known ? new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z).ToString() : "missing";
            string reason = command.Op != GardenOp.Dig ? "operation" : !present ? "presence" : (point - at).magnitude > Reach ? "reach" :
                cooling ? "cooldown" : !home ? "home-land" : nearby ? "existing-patch" : "accepted";
            File.WriteAllText(Path.Combine(diagnostics, "paired-garden-dig.txt"), "reason=" + reason + "; peer=" + peer +
                "; presence=" + present + "; poseAge=" + age + "; lastPose=" + lastPose + "; validPose=" + at + "; point=" + point + "; distance=" + (point - at).magnitude +
                "; reach=" + Reach + "; home=" + home + "; cooldown=" + cooling + "; nearbyPatch=" + nearby + "; time=" + Time.unscaledTime);
        }

        // Pause only the new isolated fixture patch's native listeners: a later host update
        // must not mask a failed guest post-Start repair. The fixture grows it explicitly.
        internal void TestStageInfestation(ulong id)
        {
            if (!host || !Ready || !bindings.TryGetValue(id, out var b) || !b.State.Exists || b.Item != 0 ||
                b.Patch == null || b.Patch.Get_SeedsType != SeedsType.Potato || !b.Patch.Get_isFertilized)
                throw new InvalidOperationException("Infestation staging requires the paired fixture's live fertilized ground patch.");
            b.Patch.RemoveWithAShovel();
            GrowField.SetValue(b.Patch, 0.45f);
            InfestedField.SetValue(b.Patch, true);
            UpdateVisualMethod.Invoke(b.Patch, null);
            Publish(b, Capture(b.Patch));
        }

        internal Garden_DirtPatch TestRecreateGroundReplica(ulong id)
        {
            if (host || !Ready || !bindings.TryGetValue(id, out var b) || !b.State.Exists || b.Item != 0 || b.Patch == null ||
                !b.State.Has(GardenFlags.Infested) || b.State.Has(GardenFlags.Dead) || b.State.Grow <= 0 || b.State.Grow >= 1 || TestPredictionPending(id))
                throw new InvalidOperationException("Replica recreation requires the paired fixture's settled infested growing ground patch.");
            applying = true;
            try { byPatch.Remove(b.Patch); DestroyReplica(b.Patch); b.Patch = null; }
            finally { applying = false; }
            Settle(b); // The real Spawn/ApplyNative path, before Unity schedules native Start.
            return b.Patch!;
        }
#endif

        // The host's growth, compressed: finish the plant as the native tick would.

#if MONMP_DEVELOPMENT
        internal void TestGrow(ulong id)
        {
            var p = bindings[id].Patch!;
            GrowField.SetValue(p, 1f);
            UpdateVisualMethod.Invoke(p, null);
        }
#endif

        // A home-land ground point clear of other solid colliders, as the native shovel requires.
        // The search walks the home land's own limit meshes rather than guessing a centre.

#if MONMP_DEVELOPMENT
        internal string TestDigSearch = "";


        internal bool TestDigPoint(out Vector3 point)
        {
            var gameplay = Singleton<Gameplay>.i;
            int rays = 0, terrain = 0, land = 0, blocked = 0;
            var limits = AccessTools.Field(typeof(Gameplay), "HomeTerrainLimitMeshs").GetValue(gameplay) as MeshFilter[] ?? new MeshFilter[0];
            foreach (var limit in limits)
            {
                if (limit == null) continue;
                var bounds = limit.mesh.bounds;
                for (float u = 0.2f; u <= 0.8f; u += 0.1f)
                    for (float v = 0.2f; v <= 0.8f; v += 0.1f)
                    {
                        var local = new Vector3(Mathf.Lerp(bounds.min.x, bounds.max.x, u), bounds.center.y, Mathf.Lerp(bounds.min.z, bounds.max.z, v));
                        var probe = limit.transform.TransformPoint(local) + Vector3.up * 60;
                        rays++;
                        if (!Physics.Raycast(probe, Vector3.down, out var hit, 200, LayerMask.GetMask("Ground"), QueryTriggerInteraction.Ignore) || !(hit.collider is TerrainCollider)) continue;
                        terrain++;
                        if (!gameplay.IsOnHomeLand(hit.point)) continue;
                        land++;
                        if (Physics.OverlapSphere(hit.point, 0.8f, ~0, QueryTriggerInteraction.Ignore).Any(c => !(c is TerrainCollider) && c.tag != "Player" && c.tag != "Water" && c.tag != "Leo")) { blocked++; continue; }
                        point = hit.point; TestDigSearch = "limits=" + limits.Length + " rays=" + rays; return true;
                    }
            }
            TestDigSearch = "limits=" + limits.Length + " rays=" + rays + " terrain=" + terrain + " land=" + land + " blocked=" + blocked;
            point = default; return false;
        }
#endif

        // The native shovel's left click at a chosen point, followed by the same mirroring hook.

#if MONMP_DEVELOPMENT
        internal void TestDig(Vector3 point, float yaw)
        {
            var p = Object.Instantiate(Singleton<GameAssets>.i.Garden_DirtPatch);
            p.transform.position = point; p.transform.rotation = Quaternion.Euler(0, yaw, 0);
            AfterShovel();
        }
#endif

    }
}
