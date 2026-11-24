using System;
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
    // Bin entry gives feedback only. The host disposes eligible loose shared items on the
    // native Wednesday sweep, then SharedItems publishes their tombstones to every guest.
    internal sealed class SharedTrashRecycle : IDisposable
    {
        private sealed class Bin
        {
            public ulong Id;
            public TrashRecycle Native = null!;
            public BoxCollider First = null!, Second = null!;
            public bool Recycle, Both;
        }
        private static readonly FieldInfo IsRecycle = AccessTools.Field(typeof(TrashRecycle), "isRecycle");
        private static readonly FieldInfo IsBoth = AccessTools.Field(typeof(TrashRecycle), "isBoth");
        private static readonly FieldInfo FirstBox = AccessTools.Field(typeof(TrashRecycle), "DetectTrashCollider");
        private static readonly FieldInfo SecondBox = AccessTools.Field(typeof(TrashRecycle), "DetechTrashCollider2");
        private readonly Dictionary<ulong, Bin> bins = new Dictionary<ulong, Bin>();
        private readonly Dictionary<TrashRecycle, Bin> byNative = new Dictionary<TrashRecycle, Bin>();
        private readonly SharedItems items;
        private readonly bool host;
        private readonly PurchaseClaims claims = new PurchaseClaims();
        private readonly Dictionary<(Guid Profile, Guid Operation), (ulong Bin, ulong Item, TrashRecycleReply Reply)> results =
            new Dictionary<(Guid, Guid), (ulong, ulong, TrashRecycleReply)>();
        private readonly TrashRecycleFeedbackTracker feedback = new TrashRecycleFeedbackTracker();
        private readonly HashSet<ulong> claimedItems = new HashSet<ulong>();
        private readonly HashSet<ulong> pendingRemovals = new HashSet<ulong>();
        private readonly Dictionary<ulong, float> dropped = new Dictionary<ulong, float>();
        private readonly Dictionary<(ulong Bin, ulong Item), float> recentFeedback = new Dictionary<(ulong, ulong), float>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool disposed;
        public bool NewDepositsBlocked { get; set; }
        public bool TransactionsSettled => feedback.Count == 0 && pendingRemovals.Count == 0;

#if MONMP_DEVELOPMENT
        internal int TestSwept { get; private set; }


        internal int TestAccepted { get; private set; }


        internal int TestDuplicateReplays { get; private set; }


        internal int TestRangeRejections { get; private set; }


        internal int TestEligibilityRejections { get; private set; }


        internal TrashRecycleReply? TestLastReply { get; private set; }


        internal Guid TestLastSentOperation { get; private set; }


        internal ulong[] TestBins => bins.Keys.OrderBy(id => id).ToArray();


        internal TrashRecycle TestNative(ulong id) => bins[id].Native;


        internal ulong TestRecycleBin => bins.Values.Where(b => b.Recycle && !b.Both && b.Native.gameObject.activeInHierarchy)
            .OrderBy(b => b.Id).Select(b => b.Id).FirstOrDefault();


        internal Vector3 TestCenter(ulong id) => bins[id].Native.transform.TransformPoint(bins[id].First.center);


        internal bool TestContains(ulong bin, InteractableItems item) => Contains(bins[bin], item);


        internal void TestRequest(ulong bin, Guid operation, ulong item) => Send(bin, operation, item);
#endif

        // Exercise the game's Wednesday callback and its patched EmptyTrash path.

#if MONMP_DEVELOPMENT
        internal void TestWednesday(ulong bin) => AccessTools.Method(typeof(TrashRecycle), "NewDay", new[] { typeof(int) })
            .Invoke(bins[bin].Native, new object[] { 3 });
#endif


        public SharedTrashRecycle(bool host, SharedItems items, string diagnostics)
        {
            this.host = host; this.items = items;
            var lines = new List<string> { "id\trecycle\tboth\tpath" };
            foreach (var native in Object.FindObjectsOfType<TrashRecycle>(true).Where(t => t.gameObject.scene.IsValid()))
            {
                string path = SharedBuildingDoors.StablePath(native.transform) + "/trash-recycle-v1";
                ulong id = StaticId.Hash(path);
                if (bins.ContainsKey(id)) throw new InvalidOperationException("Duplicate native trash/recycle bin path: " + path);
                var first = FirstBox.GetValue(native) as BoxCollider;
                var second = SecondBox.GetValue(native) as BoxCollider;
                if (first == null || second == null) throw new InvalidOperationException("Native trash/recycle detection boxes are missing: " + path);
                var bin = new Bin { Id = id, Native = native, First = first, Second = second,
                    Recycle = (bool)IsRecycle.GetValue(native), Both = (bool)IsBoth.GetValue(native) };
                bins.Add(id, bin); byNative.Add(native, bin);
                lines.Add(id.ToString("X16") + "\t" + bin.Recycle + "\t" + bin.Both + "\t" + path);
            }

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-trash-recycle.tsv"), lines.Take(1).Concat(lines.Skip(1).OrderBy(s => s, StringComparer.Ordinal)));
#endif

        }

        public IEnumerable<WorldRecord> InitialRecords() => bins.Values.Select(b =>
            new WorldRecord(b.Id, 1, TrashRecycleProtocol.Adapter, TrashRecycleProtocol.Record()));
        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            channel.CommandRequested += Command; channel.ReplyReceived += Reply;
        }

        // Record the local drop so native host feedback excludes remote items. Guest bodies
        // remain kinematic, so guests wait for the host's lease release and pose instead.
        internal void RecordDrop(InteractableItems item)
        {
            if (disposed || session?.IsRunning != true || channel?.FullySynchronized != true || NewDepositsBlocked) return;
            ulong id = items.EntityOf(item);
            if (id != 0) dropped[id] = Time.unscaledTime + 4;
        }

        // True suppresses the native message. The guest waits for a private host decision.
        internal bool Feedback(TrashRecycle native, InteractableItems item)
        {
            if (disposed || session?.IsRunning != true) return false;
            if (native == null || !byNative.TryGetValue(native, out var bin) || item == null) return true;
            ulong id = items.EntityOf(item);
            if (id == 0)
            { Report("Only shared items can be placed in multiplayer trash or recycling bins."); return true; }
            if (host)
            {
                // The host's native trigger also sees remote drops. Show native feedback
                // only for the host player's own recent local drop.
                if (!dropped.Remove(id)) return true;
                if (items.Loose(id)) return false;
                Report("Put down the shared item before using this bin."); return true;
            }
            if (!dropped.ContainsKey(id) || !items.Unowned(id) || !Contains(bin, item)) return true;
            if (channel?.FullySynchronized != true || NewDepositsBlocked)
            { Report("Wait for the shared world or multiplayer save before using this bin."); return true; }
            if (feedback.Count >= TrashRecycleFeedbackTracker.Capacity)
            { Report("Wait for the pending bin confirmation."); return true; }
            var key = (bin.Id, id);
            if (recentFeedback.TryGetValue(key, out float previous) && Time.unscaledTime - previous < 1.5f) return true;
            recentFeedback[key] = Time.unscaledTime;
            dropped.Remove(id);
            Send(bin.Id, Guid.NewGuid(), id);
            return true;
        }

        private void Send(ulong bin, Guid operation, ulong item)
        {
            if (host || channel == null || !bins.ContainsKey(bin)) return;
            var request = new TrashRecycleRequest(operation, item);
            if (!feedback.TryTrack(operation, bin, item, Time.realtimeSinceStartupAsDouble))
            { Report("Wait for an earlier bin confirmation, then try dropping the item again."); return; }

#if MONMP_DEVELOPMENT
            TestLastSentOperation = operation;
#endif

            channel.Request(bin, request.Encode(), true);
        }

        // True replaces native EmptyTrash while online. Untracked items are deliberately left
        // alone, because no shared identity exists to remove on guests.
        internal bool Sweep(TrashRecycle native)
        {
            if (disposed || session?.IsRunning != true) return false;
            if (!host) return true;
            if (!byNative.TryGetValue(native, out var bin)) return true;
            ExecuteSweep(bin);
            return true;
        }

        private void ExecuteSweep(Bin bin)
        {
            foreach (var item in Detect(bin))
            {
                if (item == null || !Eligible(bin, item)) continue;
                ulong id = items.EntityOf(item);
                if (id == 0 || claimedItems.Contains(id) || !items.Loose(id)) continue;
                claimedItems.Add(id); pendingRemovals.Add(id);
#if MONMP_DEVELOPMENT
TestSwept++;
#endif

                Object.Destroy(item.gameObject);
            }
        }

        private static bool Eligible(Bin bin, InteractableItems item) =>
            TrashRecycleProtocol.Eligible(bin.Recycle, bin.Both, item.CanBeTrashed, item.CanBeRecycled);

        private static IEnumerable<InteractableItems> Detect(Bin bin)
        {
            int mask = ~(1 << LayerMask.NameToLayer("Ground"));
            var seen = new HashSet<InteractableItems>();
            foreach (var c in Physics.OverlapBox(bin.Native.transform.TransformPoint(bin.First.center), bin.First.size * 0.5f,
                bin.Native.transform.rotation, mask).Concat(Physics.OverlapBox(bin.Native.transform.TransformPoint(bin.Second.center),
                bin.Second.size * 0.5f, bin.Second.transform.rotation, mask)))
            {
                if (c == null || c.CompareTag("Water") || c.CompareTag("Player")) continue;
                var item = c.GetComponent<InteractableItems>();
                if (item != null && seen.Add(item)) yield return item;
            }
        }

        private static bool Contains(Bin bin, InteractableItems item) => Detect(bin).Any(found => found == item);
        private bool InRange(uint peer, Bin bin) => items.PurchaseInRange(peer, bin.Native.transform.position) ||
            items.PurchaseInRange(peer, bin.First.bounds.center) || items.PurchaseInRange(peer, bin.Second.bounds.center);

        [MonMP.Core.World.WorldCommandAdapters(TrashRecycleProtocol.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || !bins.TryGetValue(id, out var bin) || !TrashRecycleRequest.Is(payload) || session == null || channel == null) return;
            var request = TrashRecycleRequest.Decode(payload);
            if (!session.Peers.TryGetValue(peer, out var buyer)) return;
            var key = (buyer.ProfileId, request.Operation);
            if (!claims.Claim(key.ProfileId, key.Operation))
            {
                if (results.TryGetValue(key, out var prior))
                {

#if MONMP_DEVELOPMENT
                    TestDuplicateReplays++;
#endif

                    channel.Reply(peer, id, (prior.Bin == id && prior.Item == request.Item ? prior.Reply :
                        new TrashRecycleReply(request.Operation, request.Item, TrashRecycleStatus.RetryRejected)).Encode());
                }
                else channel.Reply(peer, id, new TrashRecycleReply(request.Operation, request.Item, TrashRecycleStatus.Capacity).Encode());
                return;
            }
            void Finish(TrashRecycleStatus status)
            {
                var reply = new TrashRecycleReply(request.Operation, request.Item, status);
                results[key] = (id, request.Item, reply);
#if MONMP_DEVELOPMENT
TestLastReply = reply;
#endif

                if (status == TrashRecycleStatus.Accepted)
#if MONMP_DEVELOPMENT
TestAccepted++;
#else
{}
#endif

                if (status == TrashRecycleStatus.TooFar)
#if MONMP_DEVELOPMENT
TestRangeRejections++;
#else
{}
#endif

                if (status == TrashRecycleStatus.Ineligible)
#if MONMP_DEVELOPMENT
TestEligibilityRejections++;
#else
{}
#endif

                if (session.Peers.TryGetValue(peer, out var connected) && connected.ProfileId == key.ProfileId)
                    channel.Reply(peer, id, reply.Encode());
            }
            if (NewDepositsBlocked) { Finish(TrashRecycleStatus.Blocked); return; }
            var item = items.ItemOf(request.Item);
            bool released = item != null && items.Loose(request.Item) && item.rb != null && !item.rb.isKinematic;
            var status = TrashRecycleProtocol.Decide(bin.Native.gameObject.activeInHierarchy, InRange(peer, bin), item != null,
                items.FreeOrOwnedBy(request.Item, peer), released, item != null && Eligible(bin, item),
                item != null && Contains(bin, item));
            Finish(status);
        }

        private void Reply(ulong id, byte[] payload)
        {
            if (host || !bins.TryGetValue(id, out var bin) || !TrashRecycleReply.Is(payload)) return;
            var reply = TrashRecycleReply.Decode(payload);
            if (!feedback.TryResolve(reply.Operation, id, reply.Item, Time.realtimeSinceStartupAsDouble)) return;

#if MONMP_DEVELOPMENT
            TestLastReply = reply;
#endif

            if (reply.Status == TrashRecycleStatus.Accepted)
                Singleton<UI_EventMessage>.i.Show(Color.green,
                    TEXT.GetText(bin.Both || !bin.Recycle ? "EVENT_TrashRecycle_ItemTrashed" : "EVENT_TrashRecycle_ItemRecycled"), "", 2f);
            else Report(reply.Status == TrashRecycleStatus.TooFar ? "Move closer to the bin before dropping that item." :
                reply.Status == TrashRecycleStatus.Ineligible ? "That item is not eligible for this bin." :
                reply.Status == TrashRecycleStatus.NotLoose ? "Release the shared item before using this bin." :
                reply.Status == TrashRecycleStatus.NotInBin ? "Place the item inside the bin and try again." :
                "The host could not confirm this item for the bin.");
        }

        private static void Report(string message) => MonMPPlugin.Instance?.ReportItemUseUnavailable(message);
        public void Update()
        {
            if (host)
            {
                if (pendingRemovals.Count > 0) foreach (ulong id in pendingRemovals.Where(items.Removed).ToArray()) pendingRemovals.Remove(id);
            }
            else if (channel?.FullySynchronized == true && !NewDepositsBlocked && feedback.Count < TrashRecycleFeedbackTracker.Capacity)
            {
                foreach (var pair in dropped.ToArray())
                {
                    if (Time.unscaledTime >= pair.Value) { dropped.Remove(pair.Key); continue; }
                    if (!items.Unowned(pair.Key)) continue;
                    var item = items.ItemOf(pair.Key);
                    if (item == null) { dropped.Remove(pair.Key); continue; }
                    // Native guest bodies stay kinematic, so their ShowMessage callback does not
                    // report an ineligible drop. Let the existing private host reply explain it.
                    // Preserve eligible-bin priority if detection volumes happen to overlap.
                    var containing = bins.Values.OrderBy(b => b.Id).Where(b => b.Native.gameObject.activeInHierarchy &&
                        Contains(b, item)).ToArray();
                    var bin = containing.FirstOrDefault(b => Eligible(b, item)) ?? containing.FirstOrDefault();
                    if (bin == null) continue;
                    dropped.Remove(pair.Key);
                    Send(bin.Id, Guid.NewGuid(), pair.Key);
                }
            }
            var transitions = feedback.Advance(Time.realtimeSinceStartupAsDouble);
            if (transitions.Any(t => t.Transition == TrashRecycleFeedbackTransition.Delayed))
                Report("Bin confirmation is delayed. The host may still reply; check that the item is in the bin.");
            if (transitions.Any(t => t.Transition == TrashRecycleFeedbackTransition.Expired))
                Report("No host confirmation arrived. Check the item; pick it up and drop it again if you want to retry.");
            if (dropped.Count > 0) foreach (var pair in dropped.Where(p => Time.unscaledTime >= p.Value).ToArray()) dropped.Remove(pair.Key);
            if (recentFeedback.Count > 0) foreach (var pair in recentFeedback.Where(p => Time.unscaledTime - p.Value > 3).ToArray()) recentFeedback.Remove(pair.Key);
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (channel != null) { channel.CommandRequested -= Command; channel.ReplyReceived -= Reply; }
            feedback.Clear(); dropped.Clear(); session = null; channel = null;
        }
    }
}
