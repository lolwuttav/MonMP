using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Leo;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // Leo's food bowls are generic SharedItems; this adapter routes the native tools
    // that fill them and the bunker reservoir, whose value is native save data.
    internal sealed class SharedLeoSupplies : IDisposable
    {
        private sealed class WaterAccumulator
        {
            public Tool_WateringCan Tool = null!;
            public uint Generation;
            public ulong Target;
            public byte BowlIndex;
            public float Duration;
        }
        private readonly struct RequestKey : IEquatable<RequestKey>
        {
            public readonly ulong Tool;
            public readonly uint Generation, Sequence;
            public RequestKey(ulong tool, uint generation, uint sequence) { Tool = tool; Generation = generation; Sequence = sequence; }
            public bool Equals(RequestKey other) => Tool == other.Tool && Generation == other.Generation && Sequence == other.Sequence;
            public override bool Equals(object? obj) => obj is RequestKey other && Equals(other);
            public override int GetHashCode() => unchecked(((Tool.GetHashCode() * 397) ^ (int)Generation) * 397 ^ (int)Sequence);
        }

        internal const float Reach = 4.5f, InputRange = 2.4f, WaterRequestInterval = 0.1f, OneShotInterval = 0.2f;
        private static readonly FieldInfo ReservoirAmount = AccessTools.Field(typeof(Home_Bunker), "FertilizerAmounInReservoir");
        private static readonly FieldInfo ReservoirVisual = AccessTools.Field(typeof(Home_Bunker), "FertilizerReservoir");
        private static readonly MethodInfo ReservoirUpdateVisual = AccessTools.Method(typeof(Home_Bunker_FertilizerReservoir), "UpdateVisual");
        private static readonly FieldInfo BowlOne = AccessTools.Field(typeof(Item_FoodBowl), "foodBowl1");
        private static readonly FieldInfo BowlTwo = AccessTools.Field(typeof(Item_FoodBowl), "foodBowl2");
        private static readonly MethodInfo BowlUpdateVisual = AccessTools.Method(typeof(FoodBowl), "UpdateVisual");
        private static readonly MethodInfo WaterCanUpdateVisual = AccessTools.Method(typeof(Tool_WateringCan), "UpdateVisual");
        private static readonly FieldInfo WaterCanPressed = AccessTools.Field(typeof(Tool_WateringCan), "isMouseLeftClickDown");

        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<ulong, (uint Generation, uint Sequence)> guestSequences = new Dictionary<ulong, (uint, uint)>();
        private readonly Dictionary<RequestKey, float> pending = new Dictionary<RequestKey, float>();
        private readonly Dictionary<ulong, WaterAccumulator> water = new Dictionary<ulong, WaterAccumulator>();
        private readonly Dictionary<(uint Peer, ulong Tool), float> lastWaterCommand = new Dictionary<(uint, ulong), float>();
        private readonly Dictionary<(uint Peer, ulong Tool), float> lastOneShot = new Dictionary<(uint, ulong), float>();
        private readonly LeoSuppliesSequences sequences = new LeoSuppliesSequences();
        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly string diagnostics;
        private readonly SharedItems items;
        private readonly NativeEntityRegistry identities;
        private NetworkSession? session;
        private WorldChannel? channel;
        private Home_Bunker? bunker;
        private LeoSuppliesState state;
        private LeoSuppliesState original;
        private bool originalCaptured, received, disposed, applying;
        private bool identityBound;
        private float nextCapture, nextDiscovery;

        internal bool NewActionsBlocked { get; set; }
        internal bool TransactionsSettled => pending.Count == 0;
        internal int PendingCount => pending.Count;

#if MONMP_DEVELOPMENT
        internal int TestApplied { get; private set; }


        internal int TestRejected { get; private set; }


        internal int TestDuplicates { get; private set; }


        internal int TestWaterCalls { get; private set; }


        internal int TestWaterTargetedCalls { get; private set; }


        internal float TestWaterDrained { get; private set; }


        internal float TestWaterAdded { get; private set; }


        internal string TestFertilizerGate { get; private set; } = "untried";


        internal LeoSuppliesState TestState => state;


        internal LeoSuppliesState TestNativeState => bunker == null ? default : Capture(bunker);


        internal LeoSuppliesState TestOriginal => original;


        internal bool TestRestored => !host && bunker != null && originalCaptured && TestNativeState.Equals(original);


        internal ulong TestEntity => LeoSuppliesState.Entity;


        internal Home_Bunker? TestBunker => bunker;


        internal Home_Bunker_FertilizerReservoir? TestBunkerReservoir => bunker == null ? null : ReservoirVisual.GetValue(bunker) as Home_Bunker_FertilizerReservoir;


        internal FoodBowl? TestBowl(ulong item, byte index) => items.ItemOf(item) is Item_FoodBowl bowlItem && index <= 1 &&
            (index == 0 ? BowlOne.GetValue(bowlItem) : BowlTwo.GetValue(bowlItem)) is FoodBowl bowl ? bowl : null;


        internal bool TestTraceBunker() => TraceBunker();


        internal void TestReapplyNative() { if (!host) ApplyNative(state); }


        internal void TestSetReservoir(float value)
        {
            if (!host || bunker == null) throw new InvalidOperationException("Only the host Leo supplies fixture can set the bunker reservoir.");
            var next = new LeoSuppliesState(value);
            ReservoirAmount.SetValue(bunker, next.Reservoir);
            if (ReservoirVisual.GetValue(bunker) is Home_Bunker_FertilizerReservoir visual) ReservoirUpdateVisual.Invoke(visual, null);
            state = next;
            if (channel?.FullySynchronized == true) channel.Publish(LeoSuppliesState.Entity, state.Encode());
        }
#endif


        internal SharedLeoSupplies(Gameplay gameplay, bool host, string diagnostics, SharedItems items, NativeEntityRegistry identities)
        {
            this.gameplay = gameplay; this.host = host; this.diagnostics = diagnostics; this.items = items; this.identities = identities;
            ValidateNativeLayout();
            DiscoverBunker();
            if (bunker != null)
            {
                state = Capture(bunker);
                original = state; originalCaptured = !host;
                BindIdentity();
            }

#if MONMP_DEVELOPMENT
            WriteDiagnostic();
#endif

        }

        private static void ValidateNativeLayout()
        {
            if (ReservoirAmount?.FieldType != typeof(float) || ReservoirVisual?.FieldType != typeof(Home_Bunker_FertilizerReservoir) ||
                ReservoirUpdateVisual == null || ReservoirUpdateVisual.ReturnType != typeof(void) ||
                BowlOne?.FieldType != typeof(FoodBowl) || BowlTwo?.FieldType != typeof(FoodBowl) ||
                BowlUpdateVisual == null || BowlUpdateVisual.ReturnType != typeof(void) ||
                WaterCanUpdateVisual == null || WaterCanUpdateVisual.ReturnType != typeof(void) ||
                WaterCanPressed?.FieldType != typeof(bool))
                throw new InvalidOperationException("Unsupported Leo supply native layout.");
        }

        private bool GuestRunning => !host && !disposed && session?.IsRunning == true;
        private bool Ready => !disposed && session?.IsRunning == true && channel?.FullySynchronized == true;

        private void DiscoverBunker()
        {
            if (bunker != null && bunker.gameObject != null && bunker.gameObject.scene.IsValid()) return;
            bunker = Object.FindObjectsOfType<Home_Bunker>(true).FirstOrDefault(candidate => candidate != null && candidate.gameObject.scene.IsValid());
            if (bunker == null) return;
            if (!host && !originalCaptured) { original = Capture(bunker); originalCaptured = true; }
            if (!identityBound) BindIdentity();
            if (!host && received) ApplyNative(state);
        }

        private void BindIdentity()
        {
            if (bunker == null || identityBound) return;
            identities.Bind(LeoSuppliesState.Entity, LeoSuppliesState.Adapter, bunker.gameObject,
                "home-bunker-fertilizer-reservoir/v1", true, EntityPersistence.SessionOnly);
            identityBound = true;
        }

        private static LeoSuppliesState Capture(Home_Bunker native) => new LeoSuppliesState(native.Get_FertilizerAmounInReservoir);

        internal IEnumerable<WorldRecord> InitialRecords()
        {
            if (bunker != null) state = Capture(bunker);
            yield return new WorldRecord(LeoSuppliesState.Entity, 1, LeoSuppliesState.Adapter, state.Encode());
        }

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.CommandRequested += Command; channel.CommandRejected += Stale;
            channel.ReplyReceived += Reply; channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
        }

        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer)
        {
            presence.Remove(peer); sequences.RemovePeer(peer);
            foreach (var key in lastWaterCommand.Keys.Where(key => key.Peer == peer).ToArray()) lastWaterCommand.Remove(key);
            foreach (var key in lastOneShot.Keys.Where(key => key.Peer == peer).ToArray()) lastOneShot.Remove(key);
        }

        internal bool InterceptDryFood(Tool_DryFoodPack tool)
        {
            if (!GuestRunning) return false;
            if (!Ready || NewActionsBlocked || pending.Count >= 32 || !items.TryGetLocalToolLease(tool, out ulong toolId, out uint generation))
            { ReportUnavailable("Wait for the shared world and leased tool before filling Leo's bowl."); return true; }
            if (TraceBowl(out ulong target, out byte bowlIndex))
            {
                Send(new LeoSuppliesCommand(LeoSuppliesAction.FillDryBowl, toolId, generation, NextSequence(toolId, generation), target, bowlIndex));
                // Native MouseDown plays this locally; the intercepted guest click would otherwise be silent.
                var sound = Singleton<SOUND_Manager>.i;
                if (sound != null) sound.PlayOneShot2D(sound.PartInstalledInDirt, _randomPitch: true);
            }
            return true;
        }

        // Bunker reservoir fertilizer is host-mutated. Other targets continue through
        // the established SharedGarden/SharedForest mirrored path.
        internal bool InterceptBunkerFertilizer(Tool_FertilizerPack tool)
        {
            if (!GuestRunning || !TraceBunker()) return false;
            if (!Ready || NewActionsBlocked || pending.Count >= 32 || !items.TryGetLocalToolLease(tool, out ulong toolId, out uint generation))
            { ReportUnavailable("Wait for the shared world and leased tool before filling the bunker reservoir."); return true; }
            Send(new LeoSuppliesCommand(LeoSuppliesAction.FillBunkerReservoir, toolId, generation, NextSequence(toolId, generation)));
            return true;
        }

        internal void ObserveWateringCanTick(Tool_WateringCan tool, float before, bool movedNext)
        {
            if (!GuestRunning || !movedNext || tool == null || !tool.isEquiped || !WaterCanPressed.GetValue(tool)!.Equals(true)) return;
            float elapsed = before - tool.Value1;
            if (!ItemPose.FiniteValue(elapsed, 0.25f) || elapsed <= 0) return;
            if (!Ready || NewActionsBlocked || !items.TryGetLocalToolLease(tool, out ulong toolId, out uint generation)) return;
            ulong target = 0; byte index = 0;
            TraceBowl(out target, out index);
            if (!water.TryGetValue(toolId, out var accumulator) || accumulator.Generation != generation)
            {
                accumulator = new WaterAccumulator { Tool = tool, Generation = generation, Target = target, BowlIndex = index };
                water[toolId] = accumulator;
            }
            else if (accumulator.Target != target || accumulator.BowlIndex != index)
            {
                FlushWater(toolId, accumulator);
                if (accumulator.Duration > 0) return;
                accumulator.Target = target; accumulator.BowlIndex = index;
            }
            accumulator.Duration = Math.Min(LeoSuppliesCommand.MaxWateringDuration, accumulator.Duration + elapsed);
            if (accumulator.Duration >= WaterRequestInterval) FlushWater(toolId, accumulator);
        }

        internal void FlushWatering(Tool_WateringCan tool)
        {
            if (!GuestRunning || tool == null || !items.TryGetLocalToolLease(tool, out ulong toolId, out uint generation) ||
                !water.TryGetValue(toolId, out var accumulator) || accumulator.Generation != generation) return;
            FlushWater(toolId, accumulator);
        }

        private void FlushWater(ulong id, WaterAccumulator accumulator)
        {
            if (accumulator.Duration <= 0 || !Ready || NewActionsBlocked || pending.Count >= 32) return;
            float duration = Math.Min(LeoSuppliesCommand.MaxWateringDuration, accumulator.Duration);
            var command = new LeoSuppliesCommand(LeoSuppliesAction.UseWateringCan, id, accumulator.Generation,
                NextSequence(id, accumulator.Generation), accumulator.Target, accumulator.BowlIndex, duration);
            if (!Send(command)) return;
            accumulator.Duration = Math.Max(0, accumulator.Duration - duration);
        }

        private uint NextSequence(ulong tool, uint generation)
        {
            if (!guestSequences.TryGetValue(tool, out var current) || current.Generation != generation)
                current = (generation, 0);
            if (current.Sequence == uint.MaxValue) throw new InvalidOperationException("Leo supply tool sequence exhausted; reacquire the tool.");
            uint next = current.Sequence + 1;
            guestSequences[tool] = (generation, next);
            return next;
        }

        private bool Send(LeoSuppliesCommand command)
        {
            if (!Ready || NewActionsBlocked || pending.Count >= 32) return false;
            var key = new RequestKey(command.Tool, command.Generation, command.Sequence);
            pending[key] = Time.unscaledTime;
            try { channel!.Request(LeoSuppliesState.Entity, command.Encode(), useLease: true); }
            catch { pending.Remove(key); throw; }
            return true;
        }

        private bool TraceBowl(out ulong target, out byte index)
        {
            target = 0; index = 0;
            var camera = gameplay.Player.mainCamera;
            if (camera == null || !Physics.Raycast(MonMP.Plugin.Integration.LocalInteraction.SelectionRay(camera), out var hit, InputRange)) return false;
            var bowl = hit.collider.GetComponent<FoodBowl>();
            var item = bowl != null ? bowl.GetComponentInParent<Item_FoodBowl>() : null;
            if (bowl == null || item == null) return false;
            if (ReferenceEquals(BowlOne.GetValue(item), bowl)) index = 0;
            else if (ReferenceEquals(BowlTwo.GetValue(item), bowl)) index = 1;
            else return false;
            target = items.EntityOf(item);
            return target > WorldRecord.DynamicIdStart;
        }

        private bool TraceBunker()
        {
            var camera = gameplay.Player.mainCamera;
            if (camera == null || bunker == null ||
                !Physics.Raycast(MonMP.Plugin.Integration.LocalInteraction.SelectionRay(camera), out var hit, InputRange)) return false;
            var reservoir = ReservoirVisual.GetValue(bunker) as Home_Bunker_FertilizerReservoir;
            return reservoir != null && hit.collider.GetComponent<Home_Bunker_FertilizerReservoir>() == reservoir;
        }

        private bool PeerPosition(uint peer, out Vector3 at)
        {
            at = default;
            if (peer == 0 || !presence.TryGetValue(peer, out var observed) || Time.unscaledTime - observed.Time > 2 ||
                observed.Pose.State != (byte)PlayerState.Walking) return false;
            at = new Vector3(observed.Pose.X, observed.Pose.Y, observed.Pose.Z);
            return true;
        }

        private bool Near(uint peer, Vector3 target, float range) => PeerPosition(peer, out var at) && (at - target).sqrMagnitude <= range * range;

        [MonMP.Core.World.WorldCommandAdapters(LeoSuppliesState.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || entity != LeoSuppliesState.Entity || session?.Peers.ContainsKey(peer) != true) return;
            LeoSuppliesCommand command;
            try { command = LeoSuppliesCommand.Decode(payload); }
            catch (InvalidDataException) { return; }
            Type expected = command.Action == LeoSuppliesAction.FillDryBowl ? typeof(Tool_DryFoodPack) :
                command.Action == LeoSuppliesAction.UseWateringCan ? typeof(Tool_WateringCan) : typeof(Tool_FertilizerPack);
            if (!items.HostHasLeasedTool(peer, command.Tool, command.Generation, expected))
            { Reply(peer, command, LeoSuppliesReplyStatus.Rejected); return; }
            var claim = sequences.Claim(peer, command);
            if (claim == LeoSuppliesSequenceResult.Duplicate) {
#if MONMP_DEVELOPMENT
TestDuplicates++;
#endif
 Reply(peer, command, LeoSuppliesReplyStatus.Duplicate); return; }
            if (claim == LeoSuppliesSequenceResult.Capacity) { Reply(peer, command, LeoSuppliesReplyStatus.Capacity); return; }
            if (claim == LeoSuppliesSequenceResult.OutOfOrder) { Reply(peer, command, LeoSuppliesReplyStatus.Rejected); return; }
            if (NewActionsBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true || !channel!.FullySynchronized || !PeerPosition(peer, out _))
            {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 Reply(peer, command, LeoSuppliesReplyStatus.Rejected); return; }

            bool appliedAction;
            switch (command.Action)
            {
                case LeoSuppliesAction.FillDryBowl: appliedAction = FillDryBowl(peer, command); break;
                case LeoSuppliesAction.UseWateringCan: appliedAction = UseWateringCan(peer, command); break;
                case LeoSuppliesAction.FillBunkerReservoir: appliedAction = FillBunkerReservoir(peer, command); break;
                default: appliedAction = false; break;
            }
            if (appliedAction)
#if MONMP_DEVELOPMENT
TestApplied++;
#else
{}
#endif
 else
#if MONMP_DEVELOPMENT
TestRejected++;
#else
{}
#endif

            Reply(peer, command, appliedAction ? LeoSuppliesReplyStatus.Applied : LeoSuppliesReplyStatus.Rejected);
        }

        private bool FillDryBowl(uint peer, LeoSuppliesCommand command)
        {
            if (!items.HostItemInRange(peer, command.Target, Reach) || items.ItemOf(command.Target) is not Item_FoodBowl bowlItem ||
                !TryBowl(bowlItem, command.BowlIndex, out var bowl) || !Near(peer, bowl.transform.position, Reach)) return false;
            bool filled = items.HostMutateLeasedTool(peer, command.Tool, command.Generation, typeof(Tool_DryFoodPack), native =>
            {
                var pack = (Tool_DryFoodPack)native;
                float amount = pack.Value1;
                int current = bowl.WhatsIn;
                if (current != 0 && current != 2) return false;
                float consumed = Math.Min(Math.Max(0, (1 - bowl.amount) / 5f), Math.Max(0, amount));
                bowl.WhatsIn = 2;
                bowl.amount = bowl.amount + consumed * 5;
                pack.Value1 = Mathf.Clamp01(Mathf.Round((amount - consumed) * 100f) / 100f);
                BowlUpdateVisual.Invoke(bowl, null);
                return true;
            }, destroyWhenEmpty: true);
            if (filled) items.HostPublishItem(command.Target);
            return filled;
        }

        private bool UseWateringCan(uint peer, LeoSuppliesCommand command)
        {
            if (!Near(peer, items.ItemOf(command.Tool)!.transform.position, Reach)) return false;
            FoodBowl? bowl = null;
            if (command.Target != 0)
            {
                if (!items.HostItemInRange(peer, command.Target, Reach) || items.ItemOf(command.Target) is not Item_FoodBowl bowlItem ||
                    !TryBowl(bowlItem, command.BowlIndex, out bowl) || !Near(peer, bowl.transform.position, Reach)) return false;
            }
            var key = (Peer: peer, Tool: command.Tool);
            float now = Time.unscaledTime;
            float elapsed = lastWaterCommand.TryGetValue(key, out float last) ? now - last : command.Duration;
            float accepted = Math.Min(command.Duration, Math.Min(LeoSuppliesCommand.MaxWateringDuration, Mathf.Max(0.01f, elapsed + 0.05f)));
            float bowlBefore = bowl?.amount ?? 0;
            float canBefore = items.ItemOf(command.Tool) is Tool_WateringCan previous ? previous.Value1 : 0;
            bool changed = items.HostMutateLeasedTool(peer, command.Tool, command.Generation, typeof(Tool_WateringCan), native =>
            {
                var can = (Tool_WateringCan)native;
                float drained = Math.Min(accepted, Math.Max(0, can.Value1));
                if (drained <= 0) return false;
                can.Value1 = Mathf.Max(0, can.Value1 - drained);
                if (bowl != null) bowl.Fill_Water(drained * 0.4f);
                WaterCanUpdateVisual.Invoke(can, null);
                return true;
            });
            if (!changed) return false;

#if MONMP_DEVELOPMENT
            TestWaterCalls++;
#endif

            if (bowl != null)
#if MONMP_DEVELOPMENT
TestWaterTargetedCalls++;
#else
{}
#endif


#if MONMP_DEVELOPMENT
            TestWaterDrained += canBefore - ((Tool_WateringCan)items.ItemOf(command.Tool)!).Value1;


            TestWaterAdded += (bowl?.amount ?? 0) - bowlBefore;
#endif

            lastWaterCommand[key] = now;
            if (bowl != null) items.HostPublishItem(command.Target);
            return true;
        }

        private bool FillBunkerReservoir(uint peer, LeoSuppliesCommand command)
        {
            DiscoverBunker();
            if (bunker == null) {
#if MONMP_DEVELOPMENT
TestFertilizerGate = "missing bunker";
#endif
 return false; }
            var reservoir = ReservoirVisual.GetValue(bunker) as Home_Bunker_FertilizerReservoir;
            if (reservoir == null) {
#if MONMP_DEVELOPMENT
TestFertilizerGate = "missing reservoir visual";
#endif
 return false; }
            if (!PeerPosition(peer, out var peerAt)) {
#if MONMP_DEVELOPMENT
TestFertilizerGate = "missing walking pose";
#endif
 return false; }
            float distance = Vector3.Distance(peerAt, reservoir.transform.position);
            if (distance > Reach) {
#if MONMP_DEVELOPMENT
TestFertilizerGate = "out of range: " + distance.ToString("F2");
#endif
 return false; }

#if MONMP_DEVELOPMENT
            TestFertilizerGate = "near: " + distance.ToString("F2");
#endif

            bool filled = items.HostMutateLeasedTool(peer, command.Tool, command.Generation, typeof(Tool_FertilizerPack), native =>
            {
                var pack = (Tool_FertilizerPack)native;
                float current = bunker.Get_FertilizerAmounInReservoir;
                if (current >= 1 || pack.Value1 <= 0) return false;
                float capacity = 1 - current;
                float solution = pack.Value1;
                float needed = solution * 0.25f;
                float consumed = needed < capacity ? solution : capacity * 4;
                float amountAdded = Math.Min(capacity, needed);
                ReservoirAmount.SetValue(bunker, current + amountAdded);
                pack.Value1 = Mathf.Clamp01(solution - consumed);
                ReservoirUpdateVisual.Invoke(reservoir, null);
                state = Capture(bunker);
                channel!.Publish(LeoSuppliesState.Entity, state.Encode());
                return consumed > 0;
            }, destroyWhenEmpty: true);

#if MONMP_DEVELOPMENT
            TestFertilizerGate += filled ? "; mutated" : "; lease/mutation rejected; native=" + bunker.Get_FertilizerAmounInReservoir.ToString("F3");
#endif

            return filled;
        }

        private static bool TryBowl(Item_FoodBowl item, byte index, out FoodBowl bowl)
        {
            var found = (index == 0 ? BowlOne.GetValue(item) : BowlTwo.GetValue(item)) as FoodBowl;
            if (found == null) { bowl = null!; return false; }
            bowl = found; return true;
        }

        private void Reply(uint peer, LeoSuppliesCommand command, LeoSuppliesReplyStatus status)
        {
            if (session?.Peers.ContainsKey(peer) != true) return;
            channel!.Reply(peer, LeoSuppliesState.Entity,
                new LeoSuppliesReply(command.Tool, command.Generation, command.Sequence, status).Encode());
        }

        private void Stale(uint peer, ulong entity, byte[] payload)
        {
            if (!host || entity != LeoSuppliesState.Entity) return;
            try { Reply(peer, LeoSuppliesCommand.Decode(payload), LeoSuppliesReplyStatus.Rejected); }
            catch (InvalidDataException) { }
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (host || entity != LeoSuppliesState.Entity) return;
            LeoSuppliesReply reply;
            try { reply = LeoSuppliesReply.Decode(payload); }
            catch (InvalidDataException) { return; }
            if (pending.Remove(new RequestKey(reply.Tool, reply.Generation, reply.Sequence)))
            {
                if (reply.Status == LeoSuppliesReplyStatus.Applied)
#if MONMP_DEVELOPMENT
TestApplied++;
#else
{}
#endif

                else if (reply.Status == LeoSuppliesReplyStatus.Duplicate)
#if MONMP_DEVELOPMENT
TestDuplicates++;
#else
{}
#endif

                else
#if MONMP_DEVELOPMENT
TestRejected++;
#else
{}
#endif

            }
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host || !records.TryGetValue(LeoSuppliesState.Entity, out var record)) return;
            ApplyRecord(record);
        }

        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == LeoSuppliesState.Adapter) ApplyRecord(record);
        }

        private void ApplyRecord(WorldRecord record)
        {
            if (record.Id != LeoSuppliesState.Entity || record.Adapter != LeoSuppliesState.Adapter)
                throw new InvalidDataException("Unknown Leo supplies record.");
            state = LeoSuppliesState.Decode(record.Payload);
            received = true; // Retain accepted state even if the native bunker has not appeared yet.
            DiscoverBunker();
            if (bunker == null) return;
            if (!originalCaptured) { original = Capture(bunker); originalCaptured = true; }
            ApplyNative(state);
        }

        private void ApplyNative(LeoSuppliesState next)
        {
            if (bunker == null) return;
            ReservoirAmount.SetValue(bunker, next.Reservoir);
            if (ReservoirVisual.GetValue(bunker) is Home_Bunker_FertilizerReservoir visual)
                ReservoirUpdateVisual.Invoke(visual, null);
        }

        internal bool GuestBlocksBunker(Home_Bunker native) => GuestRunning && !applying && native == bunker;

        internal void Update()
        {
            if (disposed || session?.IsRunning != true) return;
            if (Time.unscaledTime >= nextDiscovery)
            {
                nextDiscovery = Time.unscaledTime + 1;
                DiscoverBunker();
            }
            if (!Ready) return;
            if (host && bunker != null && Time.unscaledTime >= nextCapture)
            {
                nextCapture = Time.unscaledTime + 0.1f;
                var current = Capture(bunker);
                if (!current.Equals(state)) { state = current; channel!.Publish(LeoSuppliesState.Entity, state.Encode()); }
            }
            if (!host)
            {
                foreach (var pair in water.ToArray())
                {
                    var accumulator = pair.Value;
                    bool stillHeld = accumulator.Tool != null && gameplay.Player.Get_ActiveTool == accumulator.Tool &&
                        WaterCanPressed.GetValue(accumulator.Tool) is bool pressed && pressed;
                    if (accumulator.Duration >= WaterRequestInterval || (!stillHeld && accumulator.Duration > 0)) FlushWater(pair.Key, accumulator);
                }
            }
        }

        private void ReportUnavailable(string message) => MonMPPlugin.Instance?.ReportItemUseUnavailable(message);


#if MONMP_DEVELOPMENT
        private void WriteDiagnostic()
        {

#if MONMP_DEVELOPMENT
            File.WriteAllText(Path.Combine(diagnostics, "shared-leo-supplies.txt"),
                "Role=" + (host ? "host" : "guest") + "\nAdapter=" + LeoSuppliesState.Adapter + "\nEntity=" + LeoSuppliesState.Entity.ToString("X16") +
                "\nBunker=" + (bunker != null) + "\nReservoir=" + state.Reservoir.ToString("F3") + "\nNativeSaveField=Home_Bunker.FertilizerAmounInReservoir");
#endif

        }


        internal LeoSuppliesCommand TestCommand(LeoSuppliesAction action, ulong tool, uint generation, uint sequence,
            ulong target = 0, byte bowlIndex = 0, float duration = 0) =>
            new LeoSuppliesCommand(action, tool, generation, sequence, target, bowlIndex, duration);


        internal void TestRequest(LeoSuppliesCommand command)
        {
            if (host || channel?.FullySynchronized != true) throw new InvalidOperationException("Leo supplies test request requires a synchronized guest.");
            Send(command);
        }


        internal LeoSuppliesSequenceResult TestClaim(uint peer, LeoSuppliesCommand command) => sequences.Claim(peer, command);
#endif


        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null)
            {
                channel.CommandRequested -= Command; channel.CommandRejected -= Stale; channel.ReplyReceived -= Reply;
                channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed;
            }
            pending.Clear(); water.Clear(); guestSequences.Clear(); sequences.Clear(); presence.Clear();
            if (!host && originalCaptured && bunker != null)
            {
                applying = true;
                try { ApplyNative(original); }
                finally { applying = false; }
            }
            if (identityBound) identities.Unbind(LeoSuppliesState.Entity);
            identityBound = false; session = null; channel = null;
        }
    }
}
