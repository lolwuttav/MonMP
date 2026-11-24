using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The vanilla bucket keeps fish membership only in a private List<Item_Fish> and
    // parents those items locally. This adapter makes the ordered membership explicit;
    // SharedItems continues to own each fish's prefab, pose, scale and lease.
    internal sealed class SharedFishBuckets : IDisposable
    {
        private sealed class Bucket
        {
            public ulong Entity, RecordId;
            public Tool_FishBucket? Item;
            public FishBucketState State = null!;
            public WorldRecord Record = null!;
            public readonly HashSet<ulong> DesiredFish = new HashSet<ulong>();
        }
        private sealed class Pending
        {
            public ulong RecordId;
            public uint Nonce;
            public float Since;
            public FishBucketAction Action;
            public ulong Fish;
            public bool Rejected;
            public float RejectedAt;
        }
        private sealed class PendingAdd
        {
            public Tool_FishBucket Bucket = null!;
            public Item_Fish Fish = null!;
            public float Since;
        }

        private static readonly FieldInfo NativeContents = AccessTools.Field(typeof(Tool_FishBucket), "FishesInBucket");
        private static readonly FieldInfo BucketBottom = AccessTools.Field(typeof(Tool_FishBucket), "Bucket_Bottom");
        private readonly SharedItems items;
        private readonly bool host;
        private readonly Dictionary<ulong, Bucket> byEntity = new Dictionary<ulong, Bucket>();
        private readonly Dictionary<ulong, Bucket> byRecord = new Dictionary<ulong, Bucket>();
        private readonly Dictionary<ulong, ulong> parentByFish = new Dictionary<ulong, ulong>();
        // Native Add_Fish drops each fish at a random spot on the bucket floor. A replica restored
        // into its bucket goes back to the spot it had, not a new one: repeated restores while a
        // guest carried a full bucket made the fish jump around inside it (#234).
        private readonly Dictionary<ulong, (Vector3 Position, Quaternion Rotation)> spotByFish = new Dictionary<ulong, (Vector3, Quaternion)>();
        private readonly Dictionary<uint, Pending> pending = new Dictionary<uint, Pending>();
        private readonly Dictionary<uint, HashSet<uint>> seenCommands = new Dictionary<uint, HashSet<uint>>();
        private readonly Dictionary<(uint Peer, uint Nonce), (ulong Record, FishBucketCommand Command, bool Accepted)> commandResults =
            new Dictionary<(uint, uint), (ulong, FishBucketCommand, bool)>();
        private readonly Dictionary<uint, Queue<uint>> commandOrder = new Dictionary<uint, Queue<uint>>();
        private readonly List<PendingAdd> pendingAdds = new List<PendingAdd>();
        private readonly HashSet<Bucket> pendingReconcile = new HashSet<Bucket>();
        private readonly List<Bucket> reconcileScratch = new List<Bucket>();
        private readonly List<uint> expiredPending = new List<uint>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private uint nextNonce;
        private float nextDiscover, nextRefresh, nextReconcileRetry, nextReconcileRepair;
        private const float AddRejectionSettleSeconds = 1f;
        private bool applying, disposed;

        internal bool Active => !disposed && session?.IsRunning == true;
        internal bool Ready => Active && channel?.Ready == true;
        internal bool Applying => applying;
        internal bool TransactionsSettled => pending.Count == 0 && pendingAdds.Count == 0;
        internal bool ContainsFish(ulong fish) => parentByFish.ContainsKey(fish);
        internal int Count => byEntity.Values.Count(b => b.State.Exists);

#if MONMP_DEVELOPMENT
        internal bool TestBound(ulong bucket) => byEntity.ContainsKey(bucket);


        internal int TestAcceptedCommands { get; private set; }


        internal int TestRejectedCommands { get; private set; }


        internal int TestReplayedCommands { get; private set; }


        internal int TestBlockedSaleAttempts { get; private set; }


        internal FishBucketState TestState(ulong bucket) => byEntity[bucket].State;


        internal int TestNativeCount(ulong bucket) => byEntity[bucket].Item == null ? -1 : ListOf(byEntity[bucket].Item!).Count;


        internal ulong[] TestNativeFishIds(ulong bucket) => byEntity[bucket].Item == null ? Array.Empty<ulong>() :
            ListOf(byEntity[bucket].Item!).Select(items.EntityOf).ToArray();


        internal bool TestGuestAdd(ulong bucket, ulong fish)
        {
            if (host || byEntity[bucket].Item == null || items.ItemOf(fish) is not Item_Fish nativeFish) return false;
            var nativeBucket = byEntity[bucket].Item!; int before = ListOf(nativeBucket).Count;
            bool added = nativeBucket.Add_Fish(nativeFish);
            return !added && ListOf(nativeBucket).Count == before;
        }


        internal bool TestGuestRemoveFirst(ulong bucket)
        {
            if (host || byEntity[bucket].Item == null) return false;
            var native = byEntity[bucket].Item!; bool wasEquipped = native.isEquiped; int before = ListOf(native).Count;
            try { native.isEquiped = true; native.MouseRightDown(); return ListOf(native).Count == before; }
            finally { native.isEquiped = wasEquipped; }
        }


        internal void TestRequest(ulong bucket, FishBucketAction action, ulong fish, uint bucketGeneration, uint fishGeneration, uint nonce)
        {
            if (!Ready || host || !byEntity.TryGetValue(bucket, out var binding)) throw new InvalidOperationException("Fish bucket request fixture is not ready.");
            channel!.Request(binding.RecordId, new FishBucketCommand(nonce, action, bucketGeneration, fish, fishGeneration).Encode(), true);
        }


        internal void TestReapplySnapshot()
        {
            if (host || channel == null) throw new InvalidOperationException("Only a guest can replay the fish bucket snapshot fixture.");
            Snapshot(channel.Records);
        }
#endif


        internal SharedFishBuckets(SharedItems items, bool host)
        {
            this.items = items; this.host = host;
            if (NativeContents == null || BucketBottom == null)
                throw new InvalidOperationException("Unsupported native fish bucket layout.");
            if (host) DiscoverHostBuckets(initial: true);
        }

        private static List<Item_Fish> ListOf(Tool_FishBucket bucket) =>
            NativeContents.GetValue(bucket) as List<Item_Fish> ?? throw new InvalidOperationException("Native fish bucket list is unavailable.");

        private void DiscoverHostBuckets(bool initial = false)
        {
            if (!host) return;
            foreach (var pair in items.LiveItems)
                if (pair.Item is Tool_FishBucket tool) EnsureHostBucket(pair.Id, tool, initial);
        }

        private Bucket? EnsureHostBucket(ulong entity, Tool_FishBucket item, bool initial = false)
        {
            if (byEntity.TryGetValue(entity, out var existing)) { existing.Item = item; return existing; }
            if (!host || entity <= WorldRecord.DynamicIdStart) return null;
            var fish = new List<ulong>();
            foreach (var itemFish in ListOf(item))
            {
                ulong id = items.EnsureHostBinding(itemFish);
                if (id == 0) continue;
                if (parentByFish.ContainsKey(id)) throw new InvalidOperationException("Native fish appears in more than one bucket.");
                fish.Add(id);
            }
            var state = new FishBucketState(entity, fish);
            var recordId = FishBucketState.RecordId(entity);
            var record = new WorldRecord(recordId, 1, FishBucketState.Adapter, state.Encode());
            var bucket = new Bucket { Entity = entity, RecordId = recordId, Item = item, State = state, Record = record };
            byEntity.Add(entity, bucket); byRecord.Add(recordId, bucket);
            foreach (ulong id in fish) { parentByFish.Add(id, entity); items.ReleaseHostItem(id); }
            if (!initial && channel != null) channel.Register(record);
            return bucket;
        }

        internal IEnumerable<WorldRecord> InitialRecords() => host ? byEntity.Values.Select(b => b.Record) : Array.Empty<WorldRecord>();

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
            channel.CommandRequested += Command; channel.ReplyReceived += Reply;
            session.PeerLeft += OnLeft;
            DiscoverHostBuckets();
        }

        private Bucket? EnsureBucket(Tool_FishBucket item)
        {
            ulong entity = items.EntityOf(item);
            if (entity == 0 && host) entity = items.EnsureHostBinding(item);
            if (entity == 0) return null;
            if (byEntity.TryGetValue(entity, out var bucket)) { bucket.Item = item; return bucket; }
            return host ? EnsureHostBucket(entity, item) : null;
        }

        // Harmony intercepts the one native mutation method shared by spherecast pickup
        // and collision/trigger pickup. Guests request host approval and never mutate locally.
        internal bool AddFishPrefix(Tool_FishBucket bucket, Item_Fish fish, ref bool result)
        {
            result = false;
            if (!Active || applying) return true;
            if (fish == null || bucket == null || !Ready || items.TransfersBlocked) return false;
            if (host) result = AddHost(bucket, fish);
            else QueueOrRequestAdd(bucket, fish);
            return false;
        }

        internal bool RemoveFishInput(Tool_FishBucket bucket)
        {
            if (!Active || applying) return true;
            if (!Ready || items.TransfersBlocked) return false;
            var binding = EnsureBucket(bucket);
            if (binding == null || binding.State.Fish.Count == 0) return false;
            ulong fishId = binding.State.Fish[0];
            if (host)
            {
                var bucketState = items.StateOf(binding.Entity);
                var fishState = items.StateOf(fishId);
                if (bucketState != null && fishState != null)
                    Remove(binding, 0, new FishBucketCommand(NextNonce(), FishBucketAction.Remove, bucketState.Lease.Generation,
                        fishId, fishState.Lease.Generation));
                return false;
            }
            Send(binding, FishBucketAction.Remove, fishId);
            return false;
        }

        private void QueueOrRequestAdd(Tool_FishBucket bucket, Item_Fish fish)
        {
            if (items.EntityOf(bucket) == 0) return;
            ulong fishId = items.EntityOf(fish);
            if (fishId == 0)
            {
                if (!pendingAdds.Any(p => p.Bucket == bucket && p.Fish == fish))
                    pendingAdds.Add(new PendingAdd { Bucket = bucket, Fish = fish, Since = Time.unscaledTime });
                return;
            }
            Send(EnsureBucket(bucket), FishBucketAction.Add, fishId);
        }

        private void Send(Bucket? bucket, FishBucketAction action, ulong fish)
        {
            if (!Ready || items.TransfersBlocked || bucket == null || !byRecord.ContainsKey(bucket.RecordId)) return;
            var bucketState = items.StateOf(bucket.Entity); var fishState = items.StateOf(fish);
            if (bucketState == null || fishState == null || !bucketState.Exists || !fishState.Exists) return;
            uint local = session!.LocalPeerId;
            if (items.ItemOf(fish) is not Item_Fish nativeFish) return;
            if (action == FishBucketAction.Add)
            {
                var native = bucket.Item;
                // NearPeer is the host's received-pose check; the host validates both distances
                // when this request arrives. A guest has no received pose for its own peer.
                if (native == null || !FishBucketCollectionAccess.Allowed(local, bucketState.Lease.Owner, fishState.Lease.Owner,
                    Vector3.Distance(native.transform.position, nativeFish.transform.position) <= 1.25f,
                    true, true, false)) return;
            }
            else if (bucketState.Lease.Owner != local) return;
            if (action == FishBucketAction.Add && parentByFish.ContainsKey(fish)) return;
            ulong expected = action == FishBucketAction.Remove && bucket.State.Fish.Count > 0 ? bucket.State.Fish[0] : fish;
            if (action == FishBucketAction.Remove && expected != fish) return;
            if (pending.Values.Any(p => p.RecordId == bucket.RecordId && p.Action == action && p.Fish == fish)) return;
            uint nonce = NextNonce(); pending.Add(nonce, new Pending { RecordId = bucket.RecordId, Nonce = nonce, Since = Time.unscaledTime, Action = action, Fish = fish });
            channel!.Request(bucket.RecordId, new FishBucketCommand(nonce, action, bucketState.Lease.Generation,
                fish, fishState.Lease.Generation).Encode(), true);
        }

        private bool AddHost(Tool_FishBucket item, Item_Fish fish)
        {
            ulong bucketId = items.EntityOf(item);
            if (bucketId == 0) bucketId = items.EnsureHostBinding(item);
            ulong fishId = items.EnsureHostBinding(fish);
            var binding = EnsureHostBucket(bucketId, item);
            var bucketState = items.StateOf(bucketId); var fishState = items.StateOf(fishId);
            if (binding == null || bucketState == null || fishState == null) return false;
            return Add(binding, 0, new FishBucketCommand(NextNonce(), FishBucketAction.Add, bucketState.Lease.Generation,
                fishId, fishState.Lease.Generation));
        }

        private bool Add(Bucket bucket, uint peer, FishBucketCommand command)
        {
            // Host physics can collect a dropped fish before the guest contact arrives.
            // Its requested membership is already committed; do not mutate or sound twice.
            if (peer != 0 && command.Action == FishBucketAction.Add && FishAlreadyIn(bucket, command.Fish)) return true;
            var native = bucket.Item;
            var fish = items.ItemOf(command.Fish) as Item_Fish;
            var bucketState = items.StateOf(bucket.Entity); var fishState = items.StateOf(command.Fish);
            if (native == null || fish == null || bucketState == null || fishState == null || !bucket.State.Exists ||
                command.Action != FishBucketAction.Add || bucketState.Lease.Generation != command.BucketGeneration ||
                fishState.Lease.Generation != command.FishGeneration || fishState.Values.IntValue1 != 0 ||
                bucket.State.Fish.Count >= FishBucketState.Capacity || bucket.State.Fish.Contains(command.Fish) ||
                parentByFish.ContainsKey(command.Fish) || ListOf(native).Contains(fish) || !fish.CanPlaceThisFishInBucket) return false;

            if (!FishBucketCollectionAccess.Allowed(peer, bucketState.Lease.Owner, fishState.Lease.Owner,
                Vector3.Distance(native.transform.position, fish.transform.position) <= 1.25f,
                items.NearPeer(peer, native.transform.position, 5f), items.NearPeer(peer, fish.transform.position, 5f), host)) return false;

            try
            {
                applying = true;
                if (!native.Add_Fish(fish)) return false;
            }
            catch (Exception ex)
            {
                if (fish.IntValue1 != 1 || !ListOf(native).Contains(fish)) throw;
                Debug.LogWarning("MonMP fish bucket presentation failed after native collection: " + ex.Message);
            }
            finally { applying = false; }

            parentByFish.Add(command.Fish, bucket.Entity);
            if (host)
            {
                items.ReleaseHostItem(command.Fish);
                items.PublishHostItem(command.Fish);
                bucket.State = new FishBucketState(bucket.Entity, ListOf(native).Select(items.EntityOf));
                Publish(bucket);
                // Every accepted fish has one authoritative cue, including host physics or
                // direct Add_Fish calls. Scoped native callers suppress their duplicate clip.
                FishBucketPresentation.Announce(FishBucketAction.Add, bucket.Entity, native.transform.position, true);
                return true;
            }
            return true;
        }

        private bool FishAlreadyIn(Bucket bucket, ulong fish)
        {
            var nativeBucket = items.ItemOf(bucket.Entity) as Tool_FishBucket;
            return bucket.State.Exists && bucket.State.Fish.Contains(fish) && parentByFish.GetValueOrDefault(fish) == bucket.Entity &&
                items.StateOf(bucket.Entity)?.Exists == true && items.StateOf(fish)?.Exists == true &&
                nativeBucket != null && nativeBucket == bucket.Item && items.ItemOf(fish) is Item_Fish native && native.IntValue1 == 1 &&
                native.transform.parent == nativeBucket.transform && ListOf(nativeBucket).Contains(native);
        }

        private bool Remove(Bucket bucket, uint peer, FishBucketCommand command)
        {
            var native = bucket.Item;
            var bucketState = items.StateOf(bucket.Entity); var fishState = items.StateOf(command.Fish);
            if (native == null || bucketState == null || fishState == null || !bucket.State.Exists ||
                command.Action != FishBucketAction.Remove || bucket.State.Fish.Count == 0 || bucket.State.Fish[0] != command.Fish ||
                bucketState.Lease.Owner != peer || bucketState.Lease.Generation != command.BucketGeneration ||
                fishState.Lease.Owner != ItemLease.Unowned || fishState.Lease.Generation != command.FishGeneration ||
                parentByFish.GetValueOrDefault(command.Fish) != bucket.Entity || !items.NearPeer(peer, native.transform.position, 5f)) return false;
            int index = ListOf(native).FindIndex(f => items.EntityOf(f) == command.Fish);
            var fish = index < 0 ? null : ListOf(native)[index];
            if (fish == null) return false;
            applying = true;
            try
            {
                fish.RemoveFromBucket();
                ListOf(native).RemoveAt(index);
                if (native.rb != null) native.rb.mass = native.Mass;
                if (native.isEquiped) native.Update_UI_Tool_Info();
            }
            finally { applying = false; }
            parentByFish.Remove(command.Fish);
            if (host)
            {
                items.PublishHostItem(command.Fish);
                bucket.State = new FishBucketState(bucket.Entity, ListOf(native).Select(items.EntityOf));
                Publish(bucket);
                // MouseRightDown is suppressed on both peers; no native removal caller remains.
                FishBucketPresentation.Announce(FishBucketAction.Remove, bucket.Entity, native.transform.position, true);
            }
            return true;
        }

        [MonMP.Core.World.WorldCommandAdapters(FishBucketState.Adapter)]

        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || !byRecord.TryGetValue(id, out var bucket)) return;
            FishBucketCommand command;
            bool accepted;
            try
            {
                command = FishBucketCommand.Decode(payload);
                if (!seenCommands.TryGetValue(peer, out var seen)) seenCommands.Add(peer, seen = new HashSet<uint>());
                if (!commandOrder.TryGetValue(peer, out var order)) commandOrder.Add(peer, order = new Queue<uint>());
                if (!seen.Add(command.Nonce))
                {
                    bool exactReplay = commandResults.TryGetValue((peer, command.Nonce), out var prior) && prior.Record == id &&
                        prior.Command.Equals(command);
                    bool priorAccepted = exactReplay && prior.Accepted;
                    if (exactReplay)
#if MONMP_DEVELOPMENT
TestReplayedCommands++;
#else
{}
#endif
 else
#if MONMP_DEVELOPMENT
TestRejectedCommands++;
#else
{}
#endif

                    channel!.Reply(peer, id, new FishBucketReply(command.Nonce, priorAccepted).Encode()); return;
                }
                order.Enqueue(command.Nonce);
                while (order.Count > 2048) { uint old = order.Dequeue(); seen.Remove(old); commandResults.Remove((peer, old)); }
                commandResults[(peer, command.Nonce)] = (id, command, false);
                accepted = !items.TransfersBlocked && (command.Action == FishBucketAction.Add ? Add(bucket, peer, command) : Remove(bucket, peer, command));
                commandResults[(peer, command.Nonce)] = (id, command, accepted);
                if (accepted)
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

            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException)
            {
                return;
            }
            channel!.Reply(peer, id, new FishBucketReply(command.Nonce, accepted).Encode());
        }

        private void Reply(ulong id, byte[] payload)
        {
            if (!byRecord.TryGetValue(id, out var bucket)) return;
            FishBucketReply reply;
            try { reply = FishBucketReply.Decode(payload); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException) { return; }
            if (!pending.TryGetValue(reply.Nonce, out var request) || request.RecordId != id) return;
            // Replies are sent immediately; bucket/item deltas are flushed later.
            // Host physics can collect a fish after declining its earlier contact,
            // or the membership may arrive before its native child. Let that exact
            // result settle before telling the player that collection failed.
            if (!reply.Accepted && request.Action == FishBucketAction.Add && !FishAlreadyIn(bucket, request.Fish))
            {
                if (!request.Rejected) { request.Rejected = true; request.RejectedAt = Time.unscaledTime; }
                return;
            }
            pending.Remove(reply.Nonce);
            if (!reply.Accepted && !(request.Action == FishBucketAction.Add && FishAlreadyIn(bucket, request.Fish)))
                MonMPPlugin.Instance?.ReportItemUseUnavailable("The host rejected that fish bucket action; its shared state was kept.");
        }

        private void SettlePending(float now)
        {
            if (pending.Count == 0) return;
            expiredPending.Clear();
            foreach (var pair in pending)
            {
                var request = pair.Value;
                if (!request.Rejected)
                {
                    if (now - request.Since > 4) expiredPending.Add(pair.Key);
                    continue;
                }
                if (byRecord.TryGetValue(request.RecordId, out var bucket) && FishAlreadyIn(bucket, request.Fish))
                {
                    expiredPending.Add(pair.Key);
                    continue;
                }
                if (now - request.RejectedAt < AddRejectionSettleSeconds) continue;
                expiredPending.Add(pair.Key);
                Debug.LogWarning("MonMP fish bucket add rejected after settling: bucketRecord=" + request.RecordId.ToString("X16") +
                    "; fish=" + request.Fish.ToString("X16") + "; sharedMember=" +
                    (bucket != null && bucket.State.Exists && bucket.State.Fish.Contains(request.Fish)) + "; nativeAttached=" +
                    (bucket != null && FishAlreadyIn(bucket, request.Fish)));
                MonMPPlugin.Instance?.ReportItemUseUnavailable("The host rejected that fish bucket action; its shared state was kept.");
            }
            foreach (uint expired in expiredPending) pending.Remove(expired);
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            // SnapshotApplied can carry only an entity repair. Ignore an unrelated repair
            // while buckets still exist in the committed world, retaining pending actions.
            var accepted = channel?.Records ?? records;
            if (!records.Values.Any(r => r.Adapter == FishBucketState.Adapter) &&
                accepted.Values.Any(r => r.Adapter == FishBucketState.Adapter)) return;
            // Rebuild from the committed view, so a repaired/new bucket cannot erase the
            // indexes for other accepted buckets. A full replacement still removes old ones.
            var staged = accepted.Values.Where(r => r.Adapter == FishBucketState.Adapter)
                .Select(r => (Record: r, State: FishBucketState.Decode(r.Payload))).ToArray();
            byEntity.Clear(); byRecord.Clear(); parentByFish.Clear(); pendingReconcile.Clear();
            foreach (var pair in staged) ApplyRecord(pair.Record, pair.State);
            ValidateUniqueMembership(); ReconcileAll();
            var repaired = new HashSet<ulong>(records.Values.Where(r => r.Adapter == FishBucketState.Adapter).Select(r => r.Id));
            foreach (var nonce in pending.Where(p => repaired.Contains(p.Value.RecordId) || !byRecord.ContainsKey(p.Value.RecordId)).Select(p => p.Key).ToArray())
                pending.Remove(nonce);
            pendingAdds.RemoveAll(p => repaired.Contains(FishBucketState.RecordId(items.EntityOf(p.Bucket))) ||
                !byEntity.ContainsKey(items.EntityOf(p.Bucket)));
        }

        private void Changed(WorldRecord record)
        {
            if (host) return;
            if (record.Adapter == FishBucketState.Adapter)
            {
                var state = FishBucketState.Decode(record.Payload);
                ApplyRecord(record, state); ValidateUniqueMembership(); Reconcile(byEntity[state.Bucket]);
            }
            else if (record.Adapter == ItemState.Adapter)
            {
                var state = ItemState.Decode(record.Payload);
                if (state.Prefab == 0) return;
                if (byEntity.TryGetValue(record.Id, out var bucket)) Reconcile(bucket);
                else if (parentByFish.TryGetValue(record.Id, out ulong owner) && byEntity.TryGetValue(owner, out bucket)) Reconcile(bucket);
            }
        }

        private void ApplyRecord(WorldRecord record, FishBucketState state)
        {
            if (FishBucketState.RecordId(state.Bucket) != record.Id) throw new InvalidDataException("Fish bucket record identity mismatch.");
            if (byEntity.TryGetValue(state.Bucket, out var old))
            {
                pendingReconcile.Remove(old);
                foreach (ulong fish in old.State.Fish) if (parentByFish.GetValueOrDefault(fish) == state.Bucket) parentByFish.Remove(fish);
            }
            var item = items.ItemOf(state.Bucket) as Tool_FishBucket;
            var binding = new Bucket { Entity = state.Bucket, RecordId = record.Id, Item = item, State = state, Record = record };
            binding.DesiredFish.UnionWith(state.Fish);
            byEntity[state.Bucket] = binding; byRecord[record.Id] = binding;
            foreach (ulong fish in state.Fish)
                if (!parentByFish.TryAdd(fish, state.Bucket)) throw new InvalidDataException("Fish is listed in more than one shared bucket.");
        }

        private void ValidateUniqueMembership()
        {
            var seen = new HashSet<ulong>();
            foreach (var bucket in byEntity.Values)
                foreach (ulong fish in bucket.State.Fish)
                    if (!seen.Add(fish)) throw new InvalidDataException("Fish is listed in more than one shared bucket.");
        }

        private void ReconcileAll()
        {
            if (host || !Active) return;
            foreach (var bucket in byEntity.Values)
                if (bucket.State.Exists || bucket.Item != null) Reconcile(bucket);
            nextReconcileRepair = Time.unscaledTime + 1;
        }

        private void Reconcile(Bucket bucket)
        {
            pendingReconcile.Remove(bucket);
            // The item adapter may replace/remove the native representation under this identity.
            var native = items.ItemOf(bucket.Entity) as Tool_FishBucket;
            bucket.Item = native;
            if (native == null)
            {
                if (bucket.State.Exists) pendingReconcile.Add(bucket);
                return;
            }
            var desired = bucket.DesiredFish;
            var list = ListOf(native);
            bool removedFish = false;
            applying = true;
            try
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var fish = list[i]; ulong id = items.EntityOf(fish);
                    if (id != 0 && desired.Contains(id)) continue;
                    // A snapshot/repair can visit the destination first. Removing a
                    // stale source-list entry must not detach its newly repaired parent
                    // or discard the destination's cached resting spot.
                    bool attachedElsewhere = fish != null && fish.transform.parent != native.transform &&
                        parentByFish.TryGetValue(id, out ulong owner) && owner != bucket.Entity;
                    if (fish != null && !attachedElsewhere) fish.RemoveFromBucket();
                    list.RemoveAt(i);
                    removedFish = true;
                    if (id != 0 && !attachedElsewhere) { spotByFish.Remove(id); items.RefreshReplicaBody(id); }
                    if (id != 0 && parentByFish.GetValueOrDefault(id) == bucket.Entity) parentByFish.Remove(id);
                }
                int positionInList = 0;
                foreach (ulong id in bucket.State.Fish)
                {
                    var fish = items.ItemOf(id) as Item_Fish;
                    if (fish == null) { pendingReconcile.Add(bucket); continue; } // ItemState can arrive after this container delta.
                    int existingIndex = list.IndexOf(fish);
                    if (existingIndex >= 0)
                    {
                        parentByFish[id] = bucket.Entity;
                        // An ItemState echo can temporarily clear the transform parent while the
                        // bucket sidecar still lists this fish. Restore the native attachment
                        // without duplicating or reordering the bucket's contents.
                        if (fish.transform.parent != native.transform || fish.IntValue1 != 1)
                        {
                            list.RemoveAt(existingIndex);
                            if (!native.Add_Fish(fish, true))
                                throw new InvalidOperationException("Could not restore a contained fish replica.");
                            list.RemoveAt(list.Count - 1);
                            list.Insert(existingIndex, fish);
                            if (spotByFish.TryGetValue(id, out var spot)) { fish.transform.localPosition = spot.Position; fish.transform.localRotation = spot.Rotation; }
                        }
                        if (!spotByFish.ContainsKey(id)) spotByFish[id] = (fish.transform.localPosition, fish.transform.localRotation);
                        if (existingIndex != positionInList)
                        {
                            list.RemoveAt(existingIndex);
                            list.Insert(positionInList, fish);
                        }
                        positionInList++;
                        continue;
                    }
                    var position = fish.transform.position; var rotation = fish.transform.rotation;
                    parentByFish[id] = bucket.Entity;
                    if (native.Add_Fish(fish, true))
                    {
                        // The replica keeps its pose only when it already lies on the bucket floor. One the
                        // other player dropped in was caught mid-fall, and kept that spot floating over the
                        // bucket (#234); it takes the native spot on the floor instead.
                        var bottom = BucketBottom.GetValue(native) as Transform;
                        var rest = fish.transform.position;
                        if (bottom != null)
                        {
                            var offset = bottom.InverseTransformPoint(position);
                            float restHeight = bottom.InverseTransformPoint(rest).y;
                            if (offset.y >= -0.05f && offset.y <= restHeight + 0.05f && new Vector2(offset.x, offset.z).magnitude <= 0.2f)
                                fish.transform.SetPositionAndRotation(position, rotation);
                        }
                        else fish.transform.SetPositionAndRotation(position, rotation);
                        spotByFish[id] = (fish.transform.localPosition, fish.transform.localRotation);
                        // Late children join at their authoritative slot, not their arrival order.
                        list.RemoveAt(list.Count - 1);
                        list.Insert(positionInList++, fish);
                    }
                    else { parentByFish.Remove(id); pendingReconcile.Add(bucket); }
                }
            }
            finally { applying = false; }
            if (removedFish)
            {
                if (native.rb != null) native.rb.mass = native.Mass;
                if (native.isEquiped) native.Update_UI_Tool_Info();
            }
        }

        private void Publish(Bucket bucket)
        {
            var next = new WorldRecord(bucket.RecordId, bucket.Record.Revision == uint.MaxValue ? 1 : bucket.Record.Revision + 1,
                FishBucketState.Adapter, bucket.State.Encode());
            if (bucket.Record.SameValue(next.Payload)) return;
            bucket.Record = next;
            channel!.Publish(bucket.RecordId, next.Payload);
        }

        internal void Update()
        {
            if (!Active) return;
            float now = Time.unscaledTime;
            if (host)
            {
                if (now >= nextDiscover) { nextDiscover = now + 1; DiscoverHostBuckets(); }
                if (now >= nextRefresh)
                {
                    nextRefresh = now + 0.3f;
                    foreach (var binding in byEntity.Values.ToArray())
                    {
                        if (items.ItemOf(binding.Entity) is not Tool_FishBucket native)
                        {
                            if (binding.State.Exists)
                            {
                                foreach (ulong fish in binding.State.Fish)
                                    if (parentByFish.GetValueOrDefault(fish) == binding.Entity) parentByFish.Remove(fish);
                                binding.Item = null; binding.State = new FishBucketState(binding.Entity, Array.Empty<ulong>(), false); Publish(binding);
                            }
                            continue;
                        }
                        binding.Item = native;
                        var ids = new List<ulong>();
                        foreach (var fish in ListOf(native))
                        {
                            ulong id = items.EnsureHostBinding(fish);
                            if (id != 0 && !ids.Contains(id)) ids.Add(id);
                        }
                        if (ids.Count > FishBucketState.Capacity) throw new InvalidOperationException("Native fish bucket exceeded its item limit.");
                        if (!binding.State.Fish.SequenceEqual(ids))
                        {
                            foreach (ulong old in binding.State.Fish) if (!ids.Contains(old) && parentByFish.GetValueOrDefault(old) == binding.Entity) parentByFish.Remove(old);
                            foreach (ulong id in ids)
                            {
                                if (parentByFish.TryGetValue(id, out ulong owner) && owner != binding.Entity) throw new InvalidOperationException("Native fish appears in more than one bucket.");
                                parentByFish[id] = binding.Entity; items.ReleaseHostItem(id);
                            }
                            binding.State = new FishBucketState(binding.Entity, ids); Publish(binding);
                        }
                    }
                }
            }
            else
            {
                for (int i = pendingAdds.Count - 1; i >= 0; i--)
                {
                    var request = pendingAdds[i];
                    if (request.Bucket == null || request.Fish == null || now - request.Since > 3) { pendingAdds.RemoveAt(i); continue; }
                    if (items.EntityOf(request.Bucket) == 0 || items.EntityOf(request.Fish) == 0) continue;
                    pendingAdds.RemoveAt(i); QueueOrRequestAdd(request.Bucket, request.Fish);
                }
                // Records repair their own bucket immediately. Retry only unresolved native
                // dependencies, with a slow sweep for replacement/parent changes that did not
                // produce a sidecar event. Settled buckets do no work on ordinary guest frames.
                if (now >= nextReconcileRepair) ReconcileAll();
                else if (pendingReconcile.Count != 0 && now >= nextReconcileRetry)
                {
                    nextReconcileRetry = now + 0.1f;
                    reconcileScratch.Clear(); reconcileScratch.AddRange(pendingReconcile);
                    foreach (var bucket in reconcileScratch) Reconcile(bucket);
                    reconcileScratch.Clear();
                }
                // Native repair gets this frame's first chance, including when a
                // previously missing fish becomes available at the deadline.
                SettlePending(now);
            }
        }

        private void OnLeft(uint peer)
        {
            if (host)
            {
                seenCommands.Remove(peer); commandOrder.Remove(peer);
                foreach (var key in commandResults.Keys.Where(k => k.Peer == peer).ToArray()) commandResults.Remove(key);
            }
            else pending.Clear();
        }

        private uint NextNonce()
        {
            nextNonce = nextNonce == uint.MaxValue ? 1 : nextNonce + 1;
            return nextNonce == 0 ? ++nextNonce : nextNonce;
        }

        // SellAllFishes empties a reusable bucket; the native kiosk does not cancel the bucket's
        // holder or sell the bucket itself. Settle that membership immediately, preserving its
        // lease, rather than waiting for discovery while its old contents still look collectable.
        internal void CompleteHostSale(Tool_FishBucket native)
        {
            if (!host || !Ready || native == null) return;
            var bucket = EnsureBucket(native);
            if (bucket == null) return;
            ulong[] contents = ListOf(native).Select(items.EntityOf).ToArray();
            if (bucket.State.Fish.SequenceEqual(contents)) return;
            foreach (ulong fish in bucket.State.Fish)
            {
                if (contents.Contains(fish)) continue;
                if (parentByFish.GetValueOrDefault(fish) == bucket.Entity) parentByFish.Remove(fish);
                spotByFish.Remove(fish);
            }
            bucket.State = new FishBucketState(bucket.Entity, contents);
            if (native.rb != null) native.rb.mass = native.Mass;
            if (native.isEquiped) native.Update_UI_Tool_Info();
            Publish(bucket);
            items.PublishHostItem(bucket.Entity);
        }

        // The host's kiosk sells fish on the shared wallet. A guest's own copy never pays
        // out or deletes fish (#235); selling contents keeps the bucket's hand lease intact.
        internal bool BlockFishSale()
        {
            if (!Active || host) return false;

#if MONMP_DEVELOPMENT
            TestBlockedSaleAttempts++;
#endif

            return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (channel != null)
            {
                channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed;
                channel.CommandRequested -= Command; channel.ReplyReceived -= Reply;
            }
            if (session != null) session.PeerLeft -= OnLeft;
            byEntity.Clear(); byRecord.Clear(); parentByFish.Clear(); spotByFish.Clear(); pending.Clear(); pendingAdds.Clear(); seenCommands.Clear(); commandOrder.Clear(); commandResults.Clear();
            pendingReconcile.Clear(); reconcileScratch.Clear(); expiredPending.Clear();
            channel = null; session = null;
        }
    }
}
