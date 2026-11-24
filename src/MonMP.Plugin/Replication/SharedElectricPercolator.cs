using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // Electric percolators are shared as ordinary item records. This adapter routes the child
    // Interactable callbacks to the host, where coffee, baskets and the resulting mug stay native.
    internal sealed class SharedElectricPercolator : IDisposable
    {
        private static readonly FieldInfo ElectricParent = AccessTools.Field(typeof(Item_CoffeePercolator), "ElectricCoffeePercolator")!;
        private static readonly FieldInfo Brewing = AccessTools.Field(typeof(Item_CoffeePercolator), "Coroutine_BrewingCoffee")!;
        private static readonly FieldInfo Furnace = AccessTools.Field(typeof(Item_CoffeePercolator), "OverFurnace")!;
        private static readonly FieldInfo HomeGeneratorField = AccessTools.Field(typeof(Item_CoffeePercolator), "homeGenerator")!;
        private static readonly FieldInfo CoffeeSpawn = AccessTools.Field(typeof(Item_CoffeePercolator), "CoffeeSpawnLocation")!;
        private static readonly MethodInfo BrewRoutine = AccessTools.Method(typeof(Item_CoffeePercolator), "BrewingCoffee", Type.EmptyTypes)!;
        private readonly Gameplay gameplay;
        private readonly SharedItems items;
        private readonly bool host;
        private readonly HashSet<ulong> activeBrews = new HashSet<ulong>();
        private readonly HashSet<Item_CoffeePercolator> nativeBrews = new HashSet<Item_CoffeePercolator>();
        private bool saveProductionScanComplete = true;
        private readonly ElectricPercolatorClaims claims = new ElectricPercolatorClaims();
        private readonly Dictionary<(uint Peer, Guid Operation), ElectricPercolatorReply> results = new Dictionary<(uint, Guid), ElectricPercolatorReply>();
        private readonly Dictionary<Guid, float> pending = new Dictionary<Guid, float>();
        private sealed class HostBrew
        {
            public uint Sequence;
            public uint NativeStartVersion;
            public bool Active;
            public bool SawSteam;
            public float StartedAt;
        }
        private sealed class GuestBrew
        {
            public ElectricPercolatorBrewState State;
            public float ReceivedAt;
            public bool Cue;
            public Item_CoffeePercolator? Child;
            public ElectricPercolatorPresentation? Presentation;
        }
        private readonly Dictionary<ulong, HostBrew> hostBrews = new Dictionary<ulong, HostBrew>();
        private readonly Dictionary<ulong, GuestBrew> guestBrews = new Dictionary<ulong, GuestBrew>();
        private float nextPresentationCapture;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool disposed;
        private const float CommandTimeout = 4f;

        internal bool NewActionsBlocked { get; set; }
        internal bool TransactionsSettled => activeBrews.Count == 0 && nativeBrews.Count == 0 && saveProductionScanComplete;
        internal bool SettleForSave()
        {
            if (!host || disposed) return true;
            bool complete = true;
            // SaveStep catches discovery/visual failures. Keep readiness closed if the scan
            // exits before all already-paid native brews have been accounted for.
            saveProductionScanComplete = false;
            // Include an already-running cottage/non-electric brew: its ten beans were also
            // debited before the delayed CoffeeAmount assignment in the same native factory.
            foreach (var child in Object.FindObjectsOfType<Item_CoffeePercolator>(true))
                if (child != null && IsBrewing(child))
                {
                    nativeBrews.Add(child);
                    bool finished = NativeSaveProduction.Finish(child, "BrewingCoffee") && !IsBrewing(child);
                    complete &= finished;
                    if (finished) { nativeBrews.Remove(child); child.UpdateVisual(); }
                }
            saveProductionScanComplete = true;
            Update();
            return complete && TransactionsSettled;
        }

        private void RefreshNativeBrews()
        {
            if (nativeBrews.Count == 0) return;
            // Only known owners are watched between saves. Native completion can settle a
            // resumed unsupported iterator; a lost owner cannot prove its paid output exists.
            foreach (var child in nativeBrews.ToArray())
                if (child != null && !IsBrewing(child)) nativeBrews.Remove(child);
        }

#if MONMP_DEVELOPMENT
        internal string TestLastStatus { get; private set; } = "idle";


        internal string TestLastRequest { get; private set; } = "none";
#endif


        internal SharedElectricPercolator(Gameplay gameplay, bool host, SharedItems items)
        { this.gameplay = gameplay; this.host = host; this.items = items; }

        internal bool AllowHostNativeUse(Item_CoffeePercolator child)
        {
            if (!host || disposed || session?.IsRunning != true || child == null ||
                ElectricParent.GetValue(child) is not Item_ElectricCoffeePercolator) return true;
            if (!NewActionsBlocked) return true;
            Report("Wait for the multiplayer save to finish before using the electric percolator.");
            return false;
        }

        // Normal host MouseDown starts the native coroutine outside Execute. Track
        // its shared parent as well, without restoring a per-frame catalog scan.
        internal void NativeMouseDownCompleted(Item_CoffeePercolator child)
        {
            if (!host || disposed || session?.IsRunning != true || child == null || !IsBrewing(child)) return;
            if (ElectricParent.GetValue(child) is not Item_ElectricCoffeePercolator parent)
            { nativeBrews.Add(child); return; }
            ulong id = items.EntityOf(parent);
            if (id != 0) { activeBrews.Add(id); PublishBrew(id, child); }
            else nativeBrews.Add(child);
        }

        internal IEnumerable<WorldRecord> InitialRecords()
        {
            if (!host) yield break;
            foreach (var pair in items.LiveItems)
                if (pair.Item is Item_ElectricCoffeePercolator parent && Child(parent) is Item_CoffeePercolator child)
                    yield return new WorldRecord(ElectricPercolatorBrewState.RecordId(pair.Id), 1,
                        ElectricPercolatorBrewState.Adapter, CaptureBrew(pair.Id, child).Encode());
        }

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected; channel = world;
            world.CommandRequested += Command; world.ReplyReceived += Reply;
            world.SnapshotApplied += Snapshot; world.Changed += Changed;
            if (host)
                foreach (var pair in items.LiveItems)
                    if (pair.Item is Item_ElectricCoffeePercolator electric && Child(electric) is Item_CoffeePercolator child && IsBrewing(child))
                        activeBrews.Add(pair.Id);
        }

        private ElectricPercolatorBrewState CaptureBrew(ulong id, Item_CoffeePercolator child)
        {
            if (!hostBrews.TryGetValue(id, out var brew)) hostBrews.Add(id, brew = new HostBrew());
            bool active = IsBrewing(child);
            uint startVersion = ElectricPercolatorPresentation.StartVersion(child);
            if (active && (!brew.Active || (startVersion != 0 && startVersion != brew.NativeStartVersion)))
            {
                brew.Sequence = brew.Sequence == uint.MaxValue ? 1 : brew.Sequence + 1;
                brew.SawSteam = false;
                // The coroutine factory postfix remembers starts even before a multiplayer session.
                brew.StartedAt = ElectricPercolatorPresentation.TryStartTime(child, out var started) ? started : Time.time;
            }
            brew.Active = active; brew.NativeStartVersion = startVersion;
            float duration = Math.Max(20, Singleton<SOUND_Manager>.i?.Coffee_Brewing?.length ?? 20);
            float elapsed = active ? ElectricPercolatorPresentation.NativeElapsed(child,
                Math.Min(duration, Math.Max(0, Time.time - brew.StartedAt)), duration, ref brew.SawSteam) : 0;
            if (active) brew.StartedAt = Time.time - elapsed;
            return new ElectricPercolatorBrewState(id, brew.Sequence, active,
                elapsed, active ? duration : 0);
        }

        private void PublishBrew(ulong id, Item_CoffeePercolator? child)
        {
            if (!host || channel == null) return;
            ulong recordId = ElectricPercolatorBrewState.RecordId(id);
            var state = child != null ? CaptureBrew(id, child) : new ElectricPercolatorBrewState(id,
                hostBrews.TryGetValue(id, out var brew) ? brew.Sequence : 0, false);
            if (channel.Records.TryGetValue(recordId, out var previous))
            {
                if (previous.Adapter != ElectricPercolatorBrewState.Adapter)
                    throw new InvalidOperationException("Electric brew record identity collision.");
                channel.Publish(recordId, state.Encode());
            }
            else channel.Register(new WorldRecord(recordId, 1, ElectricPercolatorBrewState.Adapter, state.Encode()));
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values)
                if (record.Adapter == ElectricPercolatorBrewState.Adapter) ReceiveBrew(record, true);
        }

        private void Changed(WorldRecord record)
        {
            if (disposed) return;
            if (!host)
            {
                if (record.Adapter == ElectricPercolatorBrewState.Adapter) ReceiveBrew(record, false);
                return;
            }
            // SharedItems registers later bought/adopted appliances on this stream. This avoids a
            // per-frame catalog walk and makes their presentation records available to late joins.
            if (record.Adapter != ItemState.Adapter) return;
            if (items.StateOf(record.Id)?.Exists == true && items.ItemOf(record.Id) is Item_ElectricCoffeePercolator parent && Child(parent) is Item_CoffeePercolator child)
            {
                if (IsBrewing(child)) activeBrews.Add(record.Id);
                if (!hostBrews.ContainsKey(record.Id)) PublishBrew(record.Id, child);
            }
            else if (hostBrews.ContainsKey(record.Id)) { activeBrews.Remove(record.Id); PublishBrew(record.Id, null); }
        }

        private void ReceiveBrew(WorldRecord record, bool silent)
        {
            var state = ElectricPercolatorBrewState.Decode(record.Payload);
            if (record.Id != ElectricPercolatorBrewState.RecordId(state.Item))
                throw new InvalidDataException("Electric brew record/item identity disagrees.");
            bool known = guestBrews.TryGetValue(state.Item, out var brew);
            if (!known) guestBrews.Add(state.Item, brew = new GuestBrew());
            bool cue = state.Active && !silent && (!known || brew!.State.Sequence != state.Sequence || brew.Cue);
            brew!.State = state; brew.ReceivedAt = Time.time; brew.Cue = cue;
            ApplyGuestBrew(state.Item, brew);
        }

        private void ApplyGuestBrew(ulong id, GuestBrew brew)
        {
            var parent = items.ItemOf(id) as Item_ElectricCoffeePercolator;
            var child = parent == null ? null : Child(parent);
            if (child == null || items.StateOf(id)?.Exists == false)
            { brew.Presentation?.Dispose(); brew.Presentation = null; brew.Child = null; return; }
            // Item replicas are instantiated/applied before their deferred native Start. Let native
            // initialization capture its own smoke origin before we touch these transforms.
            if (!ElectricPercolatorPresentation.Ready(child)) return;
            if (brew.Child != child)
            {
                brew.Presentation?.Dispose(); brew.Child = child;
                brew.Presentation = new ElectricPercolatorPresentation(child);
            }
            brew.Presentation!.Apply(brew.State.Advance(Time.time - brew.ReceivedAt), brew.Cue);
            brew.Cue = false;
        }

        internal void NativeStartCompleted(Item_CoffeePercolator child)
        {
            if (host || disposed || ElectricParent.GetValue(child) is not Item_ElectricCoffeePercolator parent) return;
            ulong id = items.EntityOf(parent);
            if (guestBrews.TryGetValue(id, out var brew)) ApplyGuestBrew(id, brew);
        }

        private void UpdateGuestPresentation()
        {
            foreach (var pair in guestBrews) ApplyGuestBrew(pair.Key, pair.Value);
        }

        internal bool GuestUse(Item_CoffeePercolator child, ElectricPercolatorAction action)
        {
            var connected = session;
            if (host || disposed || connected?.IsRunning != true || child == null || ElectricParent.GetValue(child) is not Item_ElectricCoffeePercolator parent)
                return false;
            if (channel?.FullySynchronized != true || !items.Ready)
            { Report("Wait for the shared world to finish loading before using the electric percolator."); return true; }
            if (pending.Count != 0) return true;
            ulong id = items.EntityOf(parent);
            if (id == 0 || items.StateOf(id)?.Exists != true)
            { Report("The electric percolator is not registered in the shared world."); return true; }

            ulong basket = 0; uint basketGeneration = 0;
            if (action == ElectricPercolatorAction.BrewOrServe && parent.IntValue2 == 0)
            {
                var candidate = items.LiveItems
                    .Where(pair => pair.Item is Tool_HarvestBasket)
                    .Select(pair => (pair.Id, Basket: (Tool_HarvestBasket)pair.Item, State: items.StateOf(pair.Id)))
                    .Where(value => value.State?.Exists == true && value.Basket.IntValue1 == 3 && value.Basket.Value1 >= 10f &&
                        Vector3.Distance(value.Basket.transform.position, child.transform.position) <= 2f &&
                        (value.State!.Lease.Owner == connected.LocalPeerId || value.State.Lease.Owner == ItemLease.Unowned))
                    .OrderBy(value => (value.Basket.transform.position - child.transform.position).sqrMagnitude)
                    .FirstOrDefault();
                if (candidate.State != null) { basket = candidate.Id; basketGeneration = candidate.State.Lease.Generation; }
            }
            var nearestBasket = items.LiveItems
                .Where(pair => pair.Item is Tool_HarvestBasket)
                .Select(pair => (pair.Id, Basket: (Tool_HarvestBasket)pair.Item, State: items.StateOf(pair.Id)))
                .Where(value => value.State?.Exists == true)
                .OrderBy(value => (value.Basket.transform.position - child.transform.position).sqrMagnitude)
                .FirstOrDefault();

#if MONMP_DEVELOPMENT
            TestLastRequest = action + "; basket=" + basket + (nearestBasket.State == null ? "; nearest=none" :
                "; nearest=" + nearestBasket.Id + " distance=" + Vector3.Distance(nearestBasket.Basket.transform.position, child.transform.position).ToString("F2") +
                " kind=" + nearestBasket.Basket.IntValue1 + " beans=" + nearestBasket.Basket.Value1.ToString("F1") +
                " owner=" + nearestBasket.State.Lease.Owner);
#endif

            var pose = Pose(child.transform);
            var request = new ElectricPercolatorRequest(Guid.NewGuid(), action, pose, basket, basketGeneration);

#if MONMP_DEVELOPMENT
            TestLastStatus = "sent " + action;
#endif

            pending[request.Operation] = Time.unscaledTime + CommandTimeout;
            channel!.Request(id, request.Encode(), true);
            return true;
        }

        internal void Update()
        {
            if (disposed) return;
            if (pending.Count != 0)
                foreach (var pair in pending.Where(value => Time.unscaledTime >= value.Value).ToArray())
                {
                    pending.Remove(pair.Key);
                    Report("The host did not respond to the electric percolator action.");
                }
            if (!host) { UpdateGuestPresentation(); return; }
            RefreshNativeBrews();
            bool capturePresentation = Time.unscaledTime >= nextPresentationCapture;
            if (capturePresentation) nextPresentationCapture = Time.unscaledTime + 0.15f;
            // Only active host brews are watched; the whole item catalog is not walked every frame.
            if (!host || activeBrews.Count == 0) return;
            foreach (ulong id in activeBrews.ToArray())
            {
                if (items.StateOf(id)?.Exists != true || items.ItemOf(id) is not Item_ElectricCoffeePercolator electric || Child(electric) is not Item_CoffeePercolator child)
                { activeBrews.Remove(id); PublishBrew(id, null); continue; }
                if (!IsBrewing(child))
                {
                    activeBrews.Remove(id);
                    items.HostPublishItem(id);
                    PublishBrew(id, child);
                }
                else if (capturePresentation) PublishBrew(id, child);
            }
        }

        [MonMP.Core.World.WorldCommandAdapters(ItemState.Adapter)]

        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || !ElectricPercolatorRequest.Is(payload) || channel == null || session == null) return;
            ElectricPercolatorRequest request;
            try { request = ElectricPercolatorRequest.Decode(payload); }
            catch (InvalidDataException) { return; }
            var key = (peer, request.Operation);
            var claim = claims.Claim(peer, request.Operation);
            if (claim == ElectricPercolatorClaim.Duplicate)
            {
                if (results.TryGetValue(key, out var previous) && session.Peers.ContainsKey(peer)) channel.Reply(peer, id, previous.Encode());
                return;
            }

#if MONMP_DEVELOPMENT
            TestLastRequest = request.Action + "; basket=" + request.Basket + "; " + items.TestPeerRange(peer,
                items.ItemOf(id) is Item_ElectricCoffeePercolator current && Child(current) is Item_CoffeePercolator currentChild
                    ? currentChild.transform.position : Vector3.zero);
#endif

            ElectricPercolatorStatus status = claim == ElectricPercolatorClaim.Capacity
                ? ElectricPercolatorStatus.NotReady : Execute(peer, id, request);
#if MONMP_DEVELOPMENT
            var rangeDetail = "";
            if (status == ElectricPercolatorStatus.TooFar && items.ItemOf(id) is Item_ElectricCoffeePercolator electric &&
                Child(electric) is Item_CoffeePercolator child)
                rangeDetail = "; requestTargetDistance=" + Vector3.Distance(PosePosition(request.Pose), child.transform.position).ToString("F2") +
                    "; " + items.TestPeerRange(peer, child.transform.position);
            var contentsDetail = "";
            if (status == ElectricPercolatorStatus.Poured && items.ItemOf(id) is Item_ElectricCoffeePercolator poured &&
                Child(poured) is Item_CoffeePercolator coffee)
                contentsDetail = "; parentCoffee=" + poured.IntValue2 + "; childCoffee=" + coffee.Get_CoffeeAmount;

#if MONMP_DEVELOPMENT
            TestLastStatus = "host " + request.Action + " => " + status + rangeDetail + contentsDetail;
#endif


#endif
            var reply = new ElectricPercolatorReply(request.Operation, request.Action, status);
            results[key] = reply;
            if (session.Peers.ContainsKey(peer)) channel.Reply(peer, id, reply.Encode());
        }

        private ElectricPercolatorStatus Execute(uint peer, ulong id, ElectricPercolatorRequest request)
        {
            if (NewActionsBlocked || items.TransfersBlocked || channel?.FullySynchronized != true) return ElectricPercolatorStatus.NotReady;
            if (items.ItemOf(id) is not Item_ElectricCoffeePercolator parent || Child(parent) is not Item_CoffeePercolator child ||
                items.StateOf(id)?.Exists != true) return ElectricPercolatorStatus.NotReady;
            if (Vector3.Distance(PosePosition(request.Pose), child.transform.position) > 1.5f ||
                !items.HostPeerNear(peer, child.transform.position, 4.5f)) return ElectricPercolatorStatus.TooFar;
            if (!child.isActiveAndEnabled || !parent.gameObject.activeInHierarchy)
                return ElectricPercolatorStatus.NotReady;
            // Native Use and MouseDown both retain the brewing lock, independently of guest VFX.
            if (IsBrewing(child)) return ElectricPercolatorStatus.Busy;
            if (request.Action == ElectricPercolatorAction.Power)
            {
                // The native child.Use calls ForceUnfreeze on its electric parent, but
                // that native helper checks the host player's local state. The guest's
                // validated interaction should apply the same native parent transition
                // without depending on what the host happens to be doing.
                if (parent.IntValue1 == 1) parent.UnFreeze();
                items.HostPublishItem(id);
                return ElectricPercolatorStatus.Accepted;
            }
            if (child.Get_CoffeeAmount > 0)
            {
                if (items.PeerHandOccupied(peer)) return ElectricPercolatorStatus.HandsBusy;
                Transform? spawn = CoffeeSpawn.GetValue(child) as Transform;
                GameObject? mug = Singleton<GameAssets>.i == null ? null : Singleton<GameAssets>.i.HomemadeCoffeeMug;
                if (spawn == null || mug == null) return ElectricPercolatorStatus.NotReady;
                try
                {
                    if (items.HostSpawnLeasedItem(mug, spawn.position, Quaternion.identity, peer) == 0)
                        return ElectricPercolatorStatus.NotReady;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("MonMP host electric percolator could not spawn coffee: " + ex.GetBaseException().Message);
                    return ElectricPercolatorStatus.NotReady;
                }
                int remainingCoffee = child.Get_CoffeeAmount - 1;
                child.LoadFromSave(remainingCoffee);
                items.HostPublishItem(id);
                Singleton<SOUND_Manager>.i?.PlayOneShotOnTransform(child.transform, Singleton<SOUND_Manager>.i.Coffee_Pourring);
                return ElectricPercolatorStatus.Poured;
            }
            if (!HasElectricity(child)) return ElectricPercolatorStatus.NoPower;
            if (Furnace.GetValue(child) is Cottage_Furnace furnace && furnace != null && furnace.Get_FirePower < 0.3f)
                return ElectricPercolatorStatus.TooCold;
            if (request.Basket == 0) return ElectricPercolatorStatus.NoBasket;
            var state = items.StateOf(request.Basket);
            if (state?.Exists != true || state.Lease.Generation != request.BasketGeneration ||
                (state.Lease.Owner != peer && state.Lease.Owner != ItemLease.Unowned) ||
                items.ItemOf(request.Basket) is not Tool_HarvestBasket basket ||
                Vector3.Distance(basket.transform.position, child.transform.position) > 2f ||
                !items.HostPeerNear(peer, basket.transform.position, 4.5f)) return ElectricPercolatorStatus.NoBasket;
            if (basket.Value1 <= 0f) return ElectricPercolatorStatus.BasketEmpty;
            if (basket.IntValue1 != 3) return ElectricPercolatorStatus.NotCoffee;
            if (basket.Value1 < 10f) return ElectricPercolatorStatus.NotEnoughCoffee;
            if (!child.isActiveAndEnabled) return ElectricPercolatorStatus.NotReady;

            basket.Value1 -= 10f;
            try
            {
                items.HostPublishItem(request.Basket);
                var routine = BrewRoutine.Invoke(child, null) as IEnumerator ?? throw new InvalidOperationException("Coffee brew routine is unavailable.");
                child.StartCoroutine(routine);
                activeBrews.Add(id);
            }
            catch (Exception ex)
            {
                basket.Value1 += 10f;
                items.HostPublishItem(request.Basket);
                Debug.LogWarning("MonMP host electric percolator brew failed: " + ex.GetBaseException().Message);
                return ElectricPercolatorStatus.NotReady;
            }
            // Presentation publication must never refund ingredients after the native brew starts.
            PublishBrew(id, child);
            return IsBrewing(child) ? ElectricPercolatorStatus.Brewing : ElectricPercolatorStatus.NotReady;
        }

        private bool HasElectricity(Item_CoffeePercolator child)
        {
            if (ElectricParent.GetValue(child) is not Item_ElectricCoffeePercolator) return false;
            var house = Singleton<Gameplay>.i.ClosestHouse(child.transform.position);
            if (house == House_Location.Cottage) return false;
            if (house == House_Location.Roulotte)
            {
                var generator = HomeGeneratorField.GetValue(child) as HomeGenerator ?? Object.FindObjectOfType<HomeGenerator>();
                HomeGeneratorField.SetValue(child, generator);
                return generator != null && generator.Get_isEngineRunning;
            }
            return true;
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (host || !ElectricPercolatorReply.Is(payload)) return;
            ElectricPercolatorReply reply;
            try { reply = ElectricPercolatorReply.Decode(payload); }
            catch (InvalidDataException) { return; }

#if MONMP_DEVELOPMENT
            TestLastStatus = "reply " + reply.Status;
#endif

            if (!pending.Remove(reply.Operation)) return;
            if (reply.Status == ElectricPercolatorStatus.Poured)
            {
                // The host pour skips native GrabHomemadeCoffee, so award its achievement to this guest.
                try { Singleton<Steam_AchievementManager>.i?.AchievementDone(AchievementType.MakeHomemadeCoffee); }
                catch (Exception ex) { Debug.LogWarning("MonMP homemade coffee achievement failed: " + ex.Message); }
                return;
            }
            if (reply.Status is ElectricPercolatorStatus.Accepted or ElectricPercolatorStatus.Brewing) return;
            Report(reply.Status switch
            {
                ElectricPercolatorStatus.Busy => "The electric percolator is already brewing.",
                ElectricPercolatorStatus.TooFar => "Move closer to the electric percolator.",
                ElectricPercolatorStatus.HandsBusy => "Free your hands before pouring coffee.",
                ElectricPercolatorStatus.NoPower => "The electric percolator has no electricity.",
                ElectricPercolatorStatus.TooCold => "The furnace is too cold to brew coffee.",
                ElectricPercolatorStatus.NoBasket => "Bring a coffee basket close to the percolator.",
                ElectricPercolatorStatus.BasketEmpty => "The coffee basket is empty.",
                ElectricPercolatorStatus.NotCoffee => "The basket does not contain coffee.",
                ElectricPercolatorStatus.NotEnoughCoffee => "The basket needs at least 10 coffee beans.",
                _ => "The electric percolator is unavailable right now."
            });
        }

        private static Item_CoffeePercolator? Child(Item_ElectricCoffeePercolator parent) =>
            AccessTools.Field(typeof(Item_ElectricCoffeePercolator), "Item_CoffeePercolator")?.GetValue(parent) as Item_CoffeePercolator;
        private static bool IsBrewing(Item_CoffeePercolator child) => (bool)Brewing.GetValue(child);
        private static ItemPose Pose(Transform transform)
        { var p = transform.position; var q = transform.rotation; return new ItemPose(p.x, p.y, p.z, q.x, q.y, q.z, q.w); }
        private static Vector3 PosePosition(ItemPose pose) => new Vector3(pose.X, pose.Y, pose.Z);
        private static void Report(string message) => MonMPPlugin.Instance?.ReportItemUseUnavailable(message);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (channel != null)
            {
                channel.CommandRequested -= Command; channel.ReplyReceived -= Reply;
                channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed;
            }
            foreach (var brew in guestBrews.Values) brew.Presentation?.Dispose();
            guestBrews.Clear(); hostBrews.Clear();
            pending.Clear(); results.Clear(); claims.Clear(); activeBrews.Clear(); nativeBrews.Clear(); saveProductionScanComplete = true; session = null; channel = null;
        }
    }
}
