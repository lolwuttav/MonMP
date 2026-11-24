using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MonMP.Core.Protocol;
using MonMP.Core.Session;

namespace MonMP.Core.World
{
    // One reliable ordered stream: immutable snapshot first, then deltas.
    // This preserves changes made while a late joiner's snapshot is still in flight.
    // Thrown by a reply subscriber for a reply addressed to it that breaks its protocol. Unlike a
    // decode failure from a stray reply, this still ends the connection.
    public sealed class ReplyProtocolException : IOException
    {
        public ReplyProtocolException(string message, Exception inner) : base(message, inner) { }
    }
    public sealed class WorldChannel : IDisposable
    {
        private sealed class Outbox
        {
            public readonly Queue<(MessageKind Kind, Action<BinaryWriter>? Body, WorldRecord? Record)> Snapshot = new Queue<(MessageKind, Action<BinaryWriter>?, WorldRecord?)>();
            public readonly CoalescedWorldRecordQueue Latest = new CoalescedWorldRecordQueue();
            // Fish buckets, tray slots and clock intervals need their full chronology,
            // including later revisions of an item until its older transitions drain.
            public readonly Queue<WorldRecord> Ordered = new Queue<WorldRecord>();
            public readonly HashSet<WorldRecord> OrderedMembership = new HashSet<WorldRecord>();
            public readonly Dictionary<ulong, int> OrderedItems = new Dictionary<ulong, int>();
            public readonly RetainedPresentationQueue Presentation = new RetainedPresentationQueue();
            public readonly HashSet<WorldRecord> SnapshotPresentation = new HashSet<WorldRecord>();
            public int PendingPresentation;
            public bool PreferPresentation = true;
            // Counts ordered membership history across both live and snapshot queues.
            public int PendingOrderedMembership;
            public bool Overflowed;
            public bool PreferLatest;
            public readonly Dictionary<ulong, WorldRecord> DeferredMotion = new Dictionary<ulong, WorldRecord>();
            public readonly Dictionary<ulong, WorldRecord> DeferredBaseline = new Dictionary<ulong, WorldRecord>();
            public readonly Dictionary<ushort, int> DeferredBaselineCounts = new Dictionary<ushort, int>();
            public readonly Dictionary<ulong, double> DeferredUntil = new Dictionary<ulong, double>();
            public readonly List<ulong> ReadyDeferredMotion = new List<ulong>();
            public PlayerPose? Pose;
            public double PoseTime, NextInterestScan;
            public double NextCheckpoint, TokenTime;
            public double RepairTokens = 4;
            public int CheckpointSection;
            public bool CatchupQueued, CatchupSent, Acknowledged;
        }
        private readonly struct RelayedPose
        {
            public readonly PlayerPose Pose;
            public readonly double Time;
            public RelayedPose(PlayerPose pose, double time) { Pose = pose; Time = time; }
        }
        private readonly NetworkSession session;
        private readonly Dictionary<ulong, WorldRecord> hostRecords;
        private sealed class CatalogSection
        {
            public readonly List<ulong> Ids = new List<ulong>();
            public ulong[]? SortedIds;
        }
        // Catalog membership changes only on registration; ordinary publishes
        // replace immutable records. Retain sorted IDs so rebuilding a moving
        // section or repairing one entity never scans/sorts the entire world.
        private readonly Dictionary<ushort, CatalogSection> catalogSections = new Dictionary<ushort, CatalogSection>();
        private ulong[]? sortedHostIds;
        private readonly SnapshotReceiver receiver;
        private readonly HashSet<ushort> dynamicAdapters;
        private readonly HashSet<ushort> leaseAdapters;
        private readonly HashSet<ulong> requiredDynamicEntities;
        private readonly Dictionary<uint, Outbox> outgoing = new Dictionary<uint, Outbox>();
        // Sends can trigger a peer departure. Retain detached iteration semantics,
        // rebuilding only when membership changes rather than on every pump.
        private KeyValuePair<uint, Outbox>[]? outgoingSnapshot;
        private KeyValuePair<uint, Outbox>[] OutgoingSnapshot => outgoingSnapshot ??= outgoing.ToArray();
        // Records are immutable. Reuse their encoded bytes only within this pump,
        // then release references; each peer still receives its own ordered stream.
        private readonly Dictionary<(MessageKind Kind, WorldRecord Record), byte[]> pumpPackets = new Dictionary<(MessageKind, WorldRecord), byte[]>();
        private sealed class CheckpointBaseline
        {
            public readonly WorldRecord[] Records;
            public string? Digest;
            public CheckpointBaseline(WorldRecord[] records) { Records = records; }
        }
        // An unchanged section has the same immutable records and digest across
        // peers and frames. Invalidate only the section that is published/registered.
        private readonly Dictionary<ushort, CheckpointBaseline> checkpointBaselines = new Dictionary<ushort, CheckpointBaseline>();
        private WorldRecord[]? snapshotBaseline;
        private SnapshotScope snapshotBaselineScope;
        private string snapshotBaselineDigest = "";
        private static readonly Action<BinaryWriter> EmptyBody = _ => { };
        public const int OrderedMembershipLimitPerPeer = 4096;
        public const int PresentationHistoryLimitPerPeer = 96;
        private const string OrderedMembershipOverflowReason = "World synchronization backlog exceeded 4096 ordered world updates; reconnect to receive the current world state.";
        private readonly Dictionary<uint, ulong> operations = new Dictionary<uint, ulong>();
        private readonly Func<double> now;
        private readonly Func<uint, uint, PlayerPose, bool> poseRelayFilter;
        private readonly Dictionary<(uint Source, uint Target), RelayedPose> relayedPoses = new Dictionary<(uint, uint), RelayedPose>();
        private readonly ushort[] sections;
        private readonly double spatialInterestRadiusSquared;
        private const double PoseFreshnessSeconds = 3;
        private const double InterestScanSeconds = 0.5;
        private const double FarMotionIntervalSeconds = 0.5;
        private SnapshotScope? requestedRepair;
        private double lastProgress;
        private double lastRepairRequest = double.NegativeInfinity;
        private bool awaitingInitial = true;
        private ulong nextOperation;
        public bool Ready => session.IsReady && (session.IsHost || receiver.Ready);
        public bool FullySynchronized => Ready && (session.IsHost || caughtUp);
#if MONMP_PERFORMANCE_CAPTURE
        // Channel acknowledgement is distinct from the guest's native/profile
        // activation. Read the current outboxes rather than retaining peer IDs.
        public int CaptureSynchronizedPeerCount
        {
            get
            {
                if (!session.IsHost) return 0;
                int count = 0;
                foreach (var pair in outgoing) if (pair.Value.Acknowledged) count++;
                return count;
            }
        }
#endif
        private bool caughtUp;
#if MONMP_DEVELOPMENT
        public bool TestPeerSynchronized(uint peer) => session.IsHost && outgoing.TryGetValue(peer, out var box) && box.Acknowledged;
        internal NetworkSession SessionForActorManifest => session;
        internal IReadOnlyList<KeyValuePair<ulong, ushort>> CatalogDescriptorsForActorManifest => receiver.CatalogDescriptors;
        internal ushort[] DynamicAdaptersForActorManifest => dynamicAdapters.OrderBy(value => value).ToArray();
        internal ushort[] LeaseAdaptersForActorManifest => leaseAdapters.OrderBy(value => value).ToArray();
        internal ulong[] RequiredDynamicEntitiesForActorManifest => requiredDynamicEntities.OrderBy(value => value).ToArray();
#endif
        public event Action<uint>? PeerSynchronized;
        public IReadOnlyDictionary<ulong, WorldRecord> Records => session.IsHost ? hostRecords : receiver.Records;
        public int RecordCount => Records.Count;
        public int PendingSnapshotPackets => outgoing.Values.Sum(box => box.Snapshot.Count);
        public int PendingDeltas => outgoing.Values.Sum(box => box.Latest.Count + box.Ordered.Count + box.Presentation.Count);
        public int PendingPresentationRecords => outgoing.Values.Sum(box => box.PendingPresentation);
        public long PresentationHistoryDropped { get; private set; }
        public int PendingOrderedMembershipRecords => outgoing.Values.Sum(box => box.PendingOrderedMembership);
        public int OrderedMembershipHighWater { get; private set; }
        public long OrderedMembershipOverflows { get; private set; }
        public string LastOrderedBacklogOverflow { get; private set; } = "";
        public event Action<uint, string>? OrderedBacklogOverflowed;
        public int PendingSpatialDeltas => outgoing.Values.Sum(box => box.DeferredMotion.Count);
        public long SpatialDeltasDeferred { get; private set; }
        public int SnapshotReceived => receiver.ReceivedCount;
        public int SnapshotExpected => receiver.ExpectedCount;
        public bool ReceivingSnapshot => receiver.Receiving;
        // Keep the multicast delegate for direct host calls and an immutable
        // invocation snapshot for isolated receive dispatch. A callback can change
        // subscriptions without changing the current dispatch's membership/order.
        private sealed class Subscribers<T> where T : Delegate
        {
            private readonly object membership = new object();
            private T? value;
            private Delegate[] handlers = Array.Empty<Delegate>();
            public T? Value => Volatile.Read(ref value);
            public Delegate[] Handlers => Volatile.Read(ref handlers);
            public void Add(T? handler)
            {
                lock (membership) Set((T?)Delegate.Combine(value, handler));
            }
            public void Remove(T? handler)
            {
                lock (membership) Set((T?)Delegate.Remove(value, handler));
            }
            private void Set(T? updated)
            {
                if (ReferenceEquals(value, updated)) return;
                Volatile.Write(ref value, updated);
                Volatile.Write(ref handlers, updated?.GetInvocationList() ?? Array.Empty<Delegate>());
            }
        }
        private readonly Subscribers<Action<IReadOnlyDictionary<ulong, WorldRecord>>> snapshotApplied = new Subscribers<Action<IReadOnlyDictionary<ulong, WorldRecord>>>();
        private readonly Subscribers<Action<WorldRecord>> changed = new Subscribers<Action<WorldRecord>>();
        public event Action<IReadOnlyDictionary<ulong, WorldRecord>>? SnapshotApplied { add => snapshotApplied.Add(value); remove => snapshotApplied.Remove(value); }
        public event Action<WorldRecord>? Changed { add => changed.Add(value); remove => changed.Remove(value); }
#if MONMP_DEVELOPMENT
        // Received-delta cost per adapter (ushort.MaxValue: snapshot and repair application), for
        // finding which replicated system floods or stalls a guest.
        public readonly Dictionary<ushort, (long Ticks, long Worst, int Count)> ReceiveCost = new Dictionary<ushort, (long, long, int)>();
        private void Cost(ushort adapter, long ticks)
        {
            ReceiveCost.TryGetValue(adapter, out var cost);
            ReceiveCost[adapter] = (cost.Ticks + ticks, Math.Max(cost.Worst, ticks), cost.Count + 1);
        }
        // Host actions run during session polling, outside the received-delta
        // timers. Count their routed calls and contained failures separately so
        // an action burst can be attributed without tracing every packet to disk.
        public readonly Dictionary<ushort, (long Ticks, long Worst, long Requests, long Calls, long Failures, long Rejected)> CommandCost = new Dictionary<ushort, (long, long, long, long, long, long)>();
        private void CostCommand(ushort adapter, long ticks, int calls, long failures, bool rejected)
        {
            CommandCost.TryGetValue(adapter, out var cost);
            CommandCost[adapter] = (cost.Ticks + ticks, Math.Max(cost.Worst, ticks), cost.Requests + 1,
                cost.Calls + calls, cost.Failures + failures, cost.Rejected + (rejected ? 1 : 0));
        }
#endif
        private sealed class CommandRoute
        {
            public readonly Action<uint, ulong, byte[]>? Value;
            public readonly Delegate[] Handlers;
            public CommandRoute(Delegate[] handlers)
            {
                Handlers = handlers;
                Value = (Action<uint, ulong, byte[]>?)Delegate.Combine(handlers);
            }
        }
        private sealed class CommandSnapshot
        {
            public readonly Action<uint, ulong, byte[]>? All;
            private readonly CommandRoute fallback;
            private readonly Dictionary<ushort, CommandRoute> routes = new Dictionary<ushort, CommandRoute>();
            public CommandSnapshot(Action<uint, ulong, byte[]>? all)
            {
                All = all;
                var handlers = all?.GetInvocationList() ?? Array.Empty<Delegate>();
                var adapters = new HashSet<ushort>?[handlers.Length];
                var keys = new HashSet<ushort>();
                var global = new List<Delegate>();
                for (int i = 0; i < handlers.Length; i++)
                {
                    // Metadata is inspected only when subscriptions change. If a
                    // callback's metadata cannot be read, retain legacy delivery.
                    try
                    {
                        var attributes = handlers[i].Method.GetCustomAttributes(typeof(WorldCommandAdaptersAttribute), false);
                        if (attributes.Length == 1 && attributes[0] is WorldCommandAdaptersAttribute declaration && declaration.Adapters.Length != 0)
                            adapters[i] = new HashSet<ushort>(declaration.Adapters);
                    }
                    catch (Exception ex) when (ex is System.Reflection.CustomAttributeFormatException ||
                        ex is System.Reflection.TargetInvocationException || ex is TypeLoadException ||
                        ex is FileNotFoundException || ex is FileLoadException || ex is NotSupportedException ||
                        ex is InvalidOperationException || ex is ArgumentException)
                    { }
                    if (adapters[i] == null) global.Add(handlers[i]);
                    else keys.UnionWith(adapters[i]!);
                }
                fallback = new CommandRoute(global.ToArray());
                foreach (ushort adapter in keys)
                {
                    var selected = new List<Delegate>();
                    for (int i = 0; i < handlers.Length; i++)
                        if (adapters[i] == null || adapters[i]!.Contains(adapter)) selected.Add(handlers[i]);
                    routes.Add(adapter, new CommandRoute(selected.ToArray()));
                }
            }
            public CommandRoute For(ushort adapter) => routes.TryGetValue(adapter, out var route) ? route : fallback;
        }
        private sealed class CommandSubscribers
        {
            private readonly object membership = new object();
            private CommandSnapshot snapshot = new CommandSnapshot(null);
            public CommandRoute For(ushort adapter) => Volatile.Read(ref snapshot).For(adapter);
            public void Add(Action<uint, ulong, byte[]>? handler)
            {
                lock (membership) Set((Action<uint, ulong, byte[]>?)Delegate.Combine(snapshot.All, handler));
            }
            public void Remove(Action<uint, ulong, byte[]>? handler)
            {
                // Remove from the complete multicast, preserving duplicate and
                // contiguous multicast removal semantics before filtering routes.
                lock (membership) Set((Action<uint, ulong, byte[]>?)Delegate.Remove(snapshot.All, handler));
            }
            private void Set(Action<uint, ulong, byte[]>? updated)
            {
                if (!ReferenceEquals(snapshot.All, updated)) Volatile.Write(ref snapshot, new CommandSnapshot(updated));
            }
        }
        private readonly CommandSubscribers commandRequested = new CommandSubscribers();
        public event Action<uint, ulong, byte[]>? CommandRequested { add => commandRequested.Add(value); remove => commandRequested.Remove(value); }
        // Presentation scopes around applying another player's action: the plugin plays the native
        // one-shot sounds those actions trigger where that player is, not in everyone's ears.
        public Func<uint, IDisposable?>? CommandScope;
        public Func<IDisposable?>? DeltaScope;
        public Func<IDisposable?>? SnapshotApplyScope;
        private readonly Subscribers<Action<uint, ulong, byte[]>> commandRejected = new Subscribers<Action<uint, ulong, byte[]>>();
        private readonly Subscribers<Action<ulong, byte[]>> replyReceived = new Subscribers<Action<ulong, byte[]>>();
        public event Action<uint, ulong, byte[]>? CommandRejected { add => commandRejected.Add(value); remove => commandRejected.Remove(value); }
        public event Action<ulong, byte[]>? ReplyReceived { add => replyReceived.Add(value); remove => replyReceived.Remove(value); }
        // A gameplay handler that failed on one received command, reply or delta. A handler's own
        // InvalidDataException is a deliberate protocol rejection and still ends the offending
        // connection; anything else (a Unity ArgumentException, a null reference in presentation) is
        // a local failure: reported here instead of reaching the session's packet-validation catch,
        // which disconnected the player a moment after an ordinary action (#128).
        public event Action<string, Exception>? HandlerFailed;
        public long HandlerFailures { get; private set; }
        private void Isolated(string what, Delegate[] handlers, Action<Delegate> invoke)
        {
            foreach (var handler in handlers)
            {
                try { invoke(handler); }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException ||
                    ex is NullReferenceException || ex is IndexOutOfRangeException || ex is KeyNotFoundException || ex is InvalidCastException)
                {
                    HandlerFailures++;
                    HandlerFailed?.Invoke(what, ex);
                }
            }
        }
        // Every subscriber sees every reply, and most replies start with a random operation Guid, so a
        // subscriber can mistake another adapter's reply for its own marker (one reply in 256 per
        // marker). A guest has no way to answer for that and the host did nothing wrong, so unlike
        // the other dispatch paths a decode failure here is local too: it must not end the connection
        // or hide the reply from the subscribers after it.
        private void IsolatedReply(Delegate[] handlers, ulong entity, byte[] reply)
        {
            foreach (var handler in handlers)
            {
                try { ((Action<ulong, byte[]>)handler)(entity, reply); }
                catch (Exception ex) when (!(ex is ReplyProtocolException) &&
                    (ex is InvalidDataException || ex is IOException || ex is ArgumentException ||
                    ex is InvalidOperationException || ex is NullReferenceException || ex is IndexOutOfRangeException ||
                    ex is KeyNotFoundException || ex is InvalidCastException))
                {
                    HandlerFailures++;
                    HandlerFailed?.Invoke("reply", ex);
                }
            }
        }
        // A subscriber that threw while applying a received record left its scene behind the record,
        // and the record stays equal until the host changes it again: the channel retries that
        // subscriber with the current record, then asks the host for a scoped repair (#143).
        private const int ApplyRetries = 4;
        private readonly Dictionary<(Delegate Handler, ulong Id), (double At, int Tries)> failedApplies = new Dictionary<(Delegate, ulong), (double, int)>();
        private sealed class FailedSnapshot
        {
            public readonly Delegate Handler;
            public readonly HashSet<ulong> Entities = new HashSet<ulong>();
            public readonly HashSet<ushort> Sections = new HashSet<ushort>();
            public bool World;
            public double At;
            public int Tries;
            public FailedSnapshot(Delegate handler, SnapshotScope scope, double at)
            { Handler = handler; At = at; Include(scope); }
            public void Include(SnapshotScope scope)
            {
                if (World) return;
                if (scope.Kind == SnapshotScopeKind.World) { World = true; Entities.Clear(); Sections.Clear(); }
                else if (scope.Kind == SnapshotScopeKind.Section) Sections.Add(scope.Adapter);
                else Entities.Add(scope.Entity);
            }
            public IReadOnlyDictionary<ulong, WorldRecord> Current(IReadOnlyDictionary<ulong, WorldRecord> committed)
            {
                if (World) return committed;
                var records = new Dictionary<ulong, WorldRecord>();
                foreach (ulong id in Entities) if (committed.TryGetValue(id, out var record)) records[id] = record;
                if (Sections.Count != 0)
                    foreach (var record in committed.Values) if (Sections.Contains(record.Adapter)) records[record.Id] = record;
                return records;
            }
        }
        private readonly List<FailedSnapshot> failedSnapshots = new List<FailedSnapshot>();
        private SnapshotScope receivingScope;
        private double nextApplyRetry = double.PositiveInfinity;
        public int ApplyRetriesPending => failedApplies.Count + failedSnapshots.Count;
        public long ApplyRetriesHealed { get; private set; }
        private bool TryInvoke(string what, Delegate handler, Action<Delegate> invoke)
        {
            try { invoke(handler); return true; }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException ||
                ex is NullReferenceException || ex is IndexOutOfRangeException || ex is KeyNotFoundException || ex is InvalidCastException)
            {
                HandlerFailures++;
                HandlerFailed?.Invoke(what, ex);
                return false;
            }
        }
        private void ApplyDelta(WorldRecord delta)
        {
            foreach (var handler in changed.Handlers)
            {
                var key = (handler, delta.Id);
                if (TryInvoke("delta", handler, h => ((Action<WorldRecord>)h)(delta))) failedApplies.Remove(key);
                else if (!failedApplies.ContainsKey(key))
                {
                    double at = now() + 0.5;
                    failedApplies[key] = (at, 0);
                    nextApplyRetry = Math.Min(nextApplyRetry, at);
                }
            }
        }
        private void ApplySnapshot(IReadOnlyDictionary<ulong, WorldRecord> applied, SnapshotScope scope)
        {
            foreach (var handler in snapshotApplied.Handlers)
                if (!TryInvoke("snapshot", handler, h => ((Action<IReadOnlyDictionary<ulong, WorldRecord>>)h)(applied)))
                {
                    // A dependency can be absent through more than one normal repair.
                    // Coalesce per subscriber so retained retries stay bounded, using
                    // current committed records when that subscriber can finally apply.
                    // Retain the scope, not just its original IDs: adapters can ignore
                    // newly registered deltas until their initial reconciliation heals.
                    int existing = failedSnapshots.FindIndex(failed => failed.Handler == handler);
                    if (existing < 0) failedSnapshots.Add(new FailedSnapshot(handler, scope, now() + 1));
                    else
                    {
                        var failed = failedSnapshots[existing];
                        failed.Include(scope); failed.At = Math.Min(failed.At, now() + 1); failed.Tries = 0;
                    }
                    nextApplyRetry = Math.Min(nextApplyRetry, failedSnapshots[existing < 0 ? failedSnapshots.Count - 1 : existing].At);
                }
        }
        private void RetryApplies()
        {
            if (failedApplies.Count == 0 && failedSnapshots.Count == 0) return;
            double time = now();
            // A failed native binding can remain pending through long backoff.
            // Do not allocate/scan a retry snapshot on every sleeping frame.
            if (time < nextApplyRetry) return;
            foreach (var pair in failedApplies.ToArray())
            {
                if (time < pair.Value.At) continue;
                if (!Records.TryGetValue(pair.Key.Id, out var current)) { failedApplies.Remove(pair.Key); continue; }
                bool ok;
                using (DeltaScope?.Invoke()) ok = TryInvoke("delta retry", pair.Key.Handler, h => ((Action<WorldRecord>)h)(current));
                if (ok) { failedApplies.Remove(pair.Key); ApplyRetriesHealed++; continue; }
                int tries = Math.Min(pair.Value.Tries + 1, ApplyRetries);
                if (tries < ApplyRetries) failedApplies[pair.Key] = (time + 0.5 * (1 << tries), tries);
                else
                {
                    // Another ordinary repair or its cooldown can refuse this request. Keep
                    // the failed native work and drain refusals later; even an accepted repair
                    // is not proof that this specific delta subscriber has healed.
                    bool requested = RequestResync(SnapshotScope.ForEntity(current.Adapter, current.Id), "A received change failed to apply");
                    failedApplies[pair.Key] = (time + (requested ? 30 : 1), tries);
                }
            }
            for (int i = failedSnapshots.Count - 1; i >= 0; i--)
            {
                var failed = failedSnapshots[i];
                if (time < failed.At) continue;
                failedSnapshots.RemoveAt(i);
                var records = failed.Current(Records);
                bool ok;
                using (SnapshotApplyScope?.Invoke()) ok = TryInvoke("snapshot retry", failed.Handler, h => ((Action<IReadOnlyDictionary<ulong, WorldRecord>>)h)(records));
                if (ok) ApplyRetriesHealed++;
                else
                {
                    int tries = Math.Min(failed.Tries + 1, ApplyRetries);
                    // After the initial exponential retries, keep unresolved native
                    // presentation pending and probe slowly instead of losing it.
                    double delay = tries < ApplyRetries ? 1 << tries : 30;
                    failed.At = time + delay; failed.Tries = tries;
                    failedSnapshots.Add(failed);
                }
            }
            nextApplyRetry = double.PositiveInfinity;
            foreach (var failed in failedApplies.Values) nextApplyRetry = Math.Min(nextApplyRetry, failed.At);
            foreach (var failed in failedSnapshots) nextApplyRetry = Math.Min(nextApplyRetry, failed.At);
        }
        public int RepairsApplied { get; private set; }
        public int DivergencesDetected { get; private set; }
        public int CheckpointsVerified { get; private set; }
        public bool RepairPending => requestedRepair.HasValue;
        public string LastRepairReason { get; private set; } = "None";
        public string SynchronizationStatus => receiver.Receiving ? "Receiving state " + receiver.ReceivedCount + "/" + receiver.ExpectedCount :
            RepairPending ? "Waiting for state repair: " + requestedRepair : Ready ? "Ready; repairs " + RepairsApplied + "; verified checks " + CheckpointsVerified : "Waiting for world snapshot";

        public WorldChannel(NetworkSession session, IEnumerable<WorldRecord> initial, IEnumerable<ushort>? dynamicAdapters = null, IEnumerable<ushort>? leaseAdapters = null, Func<double>? clock = null, double spatialInterestRadius = 240, IEnumerable<ulong>? requiredDynamicEntities = null)
        {
            if (spatialInterestRadius < 0 || double.IsNaN(spatialInterestRadius) || double.IsInfinity(spatialInterestRadius))
                throw new ArgumentOutOfRangeException(nameof(spatialInterestRadius));
            this.session = session;
            spatialInterestRadiusSquared = spatialInterestRadius * spatialInterestRadius;
            this.dynamicAdapters = new HashSet<ushort>(dynamicAdapters ?? Array.Empty<ushort>());
            this.leaseAdapters = new HashSet<ushort>(this.dynamicAdapters);
            this.leaseAdapters.UnionWith(leaseAdapters ?? Array.Empty<ushort>());
            var initialRecords = initial.ToArray();
            hostRecords = new Dictionary<ulong, WorldRecord>(initialRecords.Length);
            foreach (var record in initialRecords)
            {
                if (hostRecords.TryGetValue(record.Id, out var previous))
                    throw new InvalidDataException("Duplicate initial world record ID " + record.Id + " (0x" + record.Id.ToString("X16") +
                        "): first adapter " + previous.Adapter + "; duplicate adapter " + record.Adapter + ".");
                hostRecords.Add(record.Id, record);
            }
            foreach (var record in initialRecords) AddCatalogMember(record);
            this.requiredDynamicEntities = new HashSet<ulong>(requiredDynamicEntities ?? Array.Empty<ulong>());
            foreach (ulong id in this.requiredDynamicEntities)
            {
                if (id <= WorldRecord.DynamicIdStart || !hostRecords.TryGetValue(id, out var required) || !this.dynamicAdapters.Contains(required.Adapter))
                    throw new InvalidDataException("A required dynamic world entity is missing or has a non-dynamic adapter.");
            }
            var watch = Stopwatch.StartNew(); now = clock ?? (() => watch.Elapsed.TotalSeconds);
            poseRelayFilter = ShouldRelayPose;
            session.PoseRelayFilter = poseRelayFilter;
            sections = hostRecords.Values.Select(r => r.Adapter).Concat(this.dynamicAdapters).Distinct().OrderBy(a => a).ToArray();
            if (hostRecords.Count > SnapshotReceiver.MaxEntities) throw new InvalidDataException("Entity catalog too large.");
            receiver = new SnapshotReceiver(hostRecords.Values.Where(r =>
                !this.dynamicAdapters.Contains(r.Adapter) || r.Id <= WorldRecord.DynamicIdStart || this.requiredDynamicEntities.Contains(r.Id)), this.dynamicAdapters);
            session.PeerWorldReady += Joined; session.PeerLeft += Left; session.PoseReceived += OnPose; session.WorldPacketReceived += Receive;
        }
        private void Joined(PeerInfo peer)
        {
            if (!session.IsHost) return;
            var box = new Outbox { NextCheckpoint = now() + 5, TokenTime = now() };
            QueueSnapshot(box, SnapshotScope.World);
            outgoing.Add(peer.Id, box);
            outgoingSnapshot = null;
        }
        private void QueueSnapshot(Outbox box, SnapshotScope scope)
        {
            if (box.Overflowed) return;
            // Deliver older membership transitions before a repair's captured view.
            // Otherwise an entity repair can introduce its destination fish before
            // the pending source removal. Initial joins have no queued transitions.
            QueueOrdered(box);
            QueuePresentation(box);
            // Several ordinary peers may join before the host world changes.
            // Their captured immutable baseline and digest are identical.
            if (snapshotBaseline == null || !snapshotBaselineScope.Equals(scope))
            {
                snapshotBaseline = CaptureBaseline(scope);
                snapshotBaselineDigest = WorldRecord.DigestOrdered(snapshotBaseline);
                snapshotBaselineScope = scope;
            }
            var baseline = snapshotBaseline;
            string digest = snapshotBaselineDigest;
            int count = baseline.Length;
            box.Snapshot.Enqueue((MessageKind.SnapshotBegin, w => { scope.Write(w); w.Write(count); Wire.WriteText(w, digest, 64); }, null));
            foreach (var record in baseline) box.Snapshot.Enqueue((MessageKind.SnapshotRecord, null, record));
            foreach (var record in baseline)
                if (record.Adapter == PresentationEvent.Adapter) box.Presentation.RememberBaseline(record);
            box.Snapshot.Enqueue((MessageKind.SnapshotEnd, EmptyBody, null));
            // The captured baseline includes all prior changes in this scope. Keep
            // unrelated pending deltas and changes published after this capture.
            foreach (var id in box.Latest.Values.Where(scope.Contains).Select(r => r.Id).ToArray()) box.Latest.Remove(id);
            foreach (var id in box.DeferredMotion.Values.Where(scope.Contains).Select(r => r.Id).ToArray())
                RemoveDeferredMotion(box, id);
        }
        private void AddCatalogMember(WorldRecord record)
        {
            if (!catalogSections.TryGetValue(record.Adapter, out var section))
                catalogSections.Add(record.Adapter, section = new CatalogSection());
            section.Ids.Add(record.Id);
            section.SortedIds = null;
            sortedHostIds = null;
        }
        private WorldRecord[] CaptureBaseline(SnapshotScope scope)
        {
            if (scope.Kind == SnapshotScopeKind.Entity)
                return hostRecords.TryGetValue(scope.Entity, out var record) && record.Adapter == scope.Adapter ? new[] { record } : Array.Empty<WorldRecord>();
            ulong[] ids;
            if (scope.Kind == SnapshotScopeKind.World)
                ids = sortedHostIds ??= hostRecords.Keys.OrderBy(id => id).ToArray();
            else
            {
                if (!catalogSections.TryGetValue(scope.Adapter, out var section)) return Array.Empty<WorldRecord>();
                ids = section.SortedIds ??= section.Ids.OrderBy(id => id).ToArray();
            }
            var records = new WorldRecord[ids.Length];
            for (int i = 0; i < ids.Length; i++) records[i] = hostRecords[ids[i]];
            return records;
        }
        private static void RemoveDeferredMotion(Outbox box, ulong id)
        {
            box.DeferredMotion.Remove(id);
            if (box.DeferredBaseline.TryGetValue(id, out var baseline))
            {
                box.DeferredBaseline.Remove(id);
                int remaining = box.DeferredBaselineCounts[baseline.Adapter] - 1;
                if (remaining == 0) box.DeferredBaselineCounts.Remove(baseline.Adapter);
                else box.DeferredBaselineCounts[baseline.Adapter] = remaining;
            }
            box.DeferredUntil.Remove(id);
        }
        private static void QueueOrdered(Outbox box)
        {
            while (box.Ordered.Count > 0)
            {
                var record = box.Ordered.Dequeue();
                box.Snapshot.Enqueue((MessageKind.WorldDelta, null, record));
            }
        }
        private static void QueuePresentation(Outbox box)
        {
            while (box.Presentation.Count > 0)
            {
                var record = box.Presentation.Dequeue();
                box.Snapshot.Enqueue((MessageKind.WorldDelta, null, record));
                box.SnapshotPresentation.Add(record);
            }
        }
        private void DropOldestPresentation(Outbox box)
        {
            // A repair may have frozen older cue batches into Snapshot. Remove
            // only cosmetic deltas there, retaining every baseline/control record.
            bool dropped = false;
            // Most overflows occur entirely in the live cue queue. Avoid
            // scanning a large initial world snapshot when it owns no cue batch.
            int count = box.SnapshotPresentation.Count == 0 ? 0 : box.Snapshot.Count;
            while (count-- > 0)
            {
                var message = box.Snapshot.Dequeue();
                if (!dropped && message.Record != null && box.SnapshotPresentation.Remove(message.Record))
                    dropped = true;
                else box.Snapshot.Enqueue(message);
            }
            if (!dropped) box.Presentation.Dequeue();
            box.PendingPresentation--;
            PresentationHistoryDropped++;
        }
        private void IsolatedCommand(string what, Delegate[] handlers, uint sender, ulong entity, byte[] payload, ref int calls)
        {
            // Keep burst dispatch and its counters free of a captured invocation
            // delegate. The cached route still determines exact callback order.
            foreach (var handler in handlers)
            {
                calls++;
                try { ((Action<uint, ulong, byte[]>)handler)(sender, entity, payload); }
                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException ||
                    ex is NullReferenceException || ex is IndexOutOfRangeException || ex is KeyNotFoundException || ex is InvalidCastException)
                {
                    HandlerFailures++;
                    HandlerFailed?.Invoke(what, ex);
                }
            }
        }
        private bool QueueDelta(Outbox box, WorldRecord record, bool itemMembershipChanged = false)
        {
            if (box.Overflowed) return false;
            bool orderedItem = record.Adapter == ItemState.Adapter &&
                (itemMembershipChanged || box.OrderedItems.ContainsKey(record.Id));
            if (record.Adapter == FishBucketState.Adapter || orderedItem ||
                (record.Adapter == ClockState.Adapter && record.Id == ClockState.Entity))
            {
                // A stale command may request the same authoritative record while it
                // already waits on this reliable stream. Its existing copy suffices.
                if (box.OrderedMembership.Contains(record)) return true;
                if (box.PendingOrderedMembership >= OrderedMembershipLimitPerPeer)
                {
                    box.Overflowed = true;
                    OrderedMembershipOverflows++;
                    return false;
                }
                // Remove any superseded ordinary record before starting the item's
                // ordered chain. Later full records must not overtake its detach.
                box.Latest.Remove(record.Id);
                box.Ordered.Enqueue(record);
                box.OrderedMembership.Add(record);
                if (orderedItem)
                {
                    box.OrderedItems.TryGetValue(record.Id, out int pending);
                    box.OrderedItems[record.Id] = pending + 1;
                }
                box.PendingOrderedMembership++;
                OrderedMembershipHighWater = Math.Max(OrderedMembershipHighWater, box.PendingOrderedMembership);
            }
            else if (record.Adapter == PresentationEvent.Adapter && box.Presentation.TryEnqueue(record, out bool added))
            {
                if (added) box.PendingPresentation++;
                // One-shot overload must never kick a player. Retain a generous
                // bounded window per slow peer and discard its oldest cues only.
                if (box.PendingPresentation > PresentationHistoryLimitPerPeer) DropOldestPresentation(box);
            }
            else box.Latest[record.Id] = record;
            return true;
        }
        private void DisconnectOverflowedPeers()
        {
            foreach (var pair in OutgoingSnapshot)
            {
                if (!pair.Value.Overflowed) continue;
                uint peer = pair.Key;
                var box = pair.Value;
                // Diagnose the bounded retained history before discarding it. Do not log
                // payloads or identities, and do not add a periodic scan to healthy pumps.
                try
                {
                    var pressure = session.MetricsFor(peer);
                    string adapters = string.Join(",", box.OrderedMembership.GroupBy(record => record.Adapter)
                        .OrderByDescending(group => group.Count()).ThenBy(group => group.Key).Take(8)
                        .Select(group => group.Key + ":" + group.Count()));
                    LastOrderedBacklogOverflow = "peer=" + peer + "; ordered=" + box.PendingOrderedMembership +
                        "; entities=" + box.OrderedMembership.Select(record => record.Id).Distinct().Count() +
                        "; adapters(count)=" + adapters + "; snapshotPackets=" + box.Snapshot.Count +
                        "; latest=" + box.Latest.Count + "; caughtUp=" + box.CatchupSent +
                        "; reliablePackets=" + pressure.ReliableQueuePackets + "; reliableBytes=" + pressure.ReliableQueueBytes +
                        "; mediaPackets=" + pressure.MediaQueuePackets + "; mediaBytes=" + pressure.MediaQueueBytes +
                        "; rttMs=" + pressure.RoundTripTimeMs;
                }
                catch (Exception ex) { LastOrderedBacklogOverflow = "peer=" + peer + "; diagnostics unavailable: " + ex.GetType().Name; }
                // Remove all channel-owned queued work before requesting the peer-only
                // disconnect. Later publishes cannot recreate this outbox.
                Left(peer);
                // Optional diagnostics must never prevent the capacity disconnect.
                try { OrderedBacklogOverflowed?.Invoke(peer, LastOrderedBacklogOverflow); }
                catch { }
                session.Kick(peer, OrderedMembershipOverflowReason);
            }
        }
        private static void MarkOrderedMembershipSent(Outbox box, WorldRecord record)
        {
            // Snapshot queues can also contain ordinary item deltas captured before
            // a later ordered transition. Count only actual retained history records.
            if (!box.OrderedMembership.Remove(record)) return;
            if (record.Adapter == ItemState.Adapter)
            {
                int remaining = box.OrderedItems[record.Id] - 1;
                if (remaining == 0) box.OrderedItems.Remove(record.Id);
                else box.OrderedItems[record.Id] = remaining;
            }
            if (box.PendingOrderedMembership <= 0)
                throw new InvalidOperationException("Ordered membership history count became inconsistent.");
            box.PendingOrderedMembership--;
        }
        private void Left(uint peer)
        {
            outgoing.Remove(peer); operations.Remove(peer);
            outgoingSnapshot = null;
            foreach (var key in relayedPoses.Keys.Where(key => key.Source == peer || key.Target == peer).ToArray())
                relayedPoses.Remove(key);
        }
        private void OnPose(uint peer, PlayerPose pose)
        {
            if (!session.IsHost || !outgoing.TryGetValue(peer, out var box)) return;
            if (box.Pose.HasValue)
            {
                var previous = box.Pose.Value;
                var previousView = PresentationPosition(previous);
                var currentView = PresentationPosition(pose);
                if (previous.Spectating != pose.Spectating ||
                    DistanceSquared(previous.X, previous.Y, previous.Z, pose.X, pose.Y, pose.Z) >= 256 ||
                    DistanceSquared(previousView.X, previousView.Y, previousView.Z, currentView.X, currentView.Y, currentView.Z) >= 256)
                    box.NextInterestScan = now();
            }
            box.Pose = pose;
            box.PoseTime = now();
        }
        private bool ShouldRelayPose(uint source, uint target, PlayerPose pose)
        {
            double time = now();
            if (!session.IsHost || spatialInterestRadiusSquared == 0 || source == target)
                return true;

            var key = (Source: source, Target: target);
            if (!outgoing.TryGetValue(target, out var box) || !box.Acknowledged || !box.Pose.HasValue ||
                time - box.PoseTime > PoseFreshnessSeconds)
            {
                RememberPose(key, pose, time);
                return true;
            }

            var sourceView = PresentationPosition(pose);
            var recipientView = PresentationPosition(box.Pose.Value);
            if (DistanceSquared(sourceView.X, sourceView.Y, sourceView.Z,
                recipientView.X, recipientView.Y, recipientView.Z) <= spatialInterestRadiusSquared)
            {
                // Nearby players keep the full pose cadence. This also bypasses
                // the far cadence on the first update after either player re-enters.
                // Spectator heads and viewers are at the detached camera, while
                // their native walking roots stay parked for gameplay authority.
                RememberPose(key, pose, time);
                return true;
            }

            if (!relayedPoses.TryGetValue(key, out var previous) || DiscretePoseChanged(previous.Pose, pose) ||
                time - previous.Time >= FarMotionIntervalSeconds)
            {
                RememberPose(key, pose, time);
                return true;
            }
            return false;
        }
        private void RememberPose((uint Source, uint Target) key, PlayerPose pose, double time) =>
            relayedPoses[key] = new RelayedPose(pose, time);
        private static bool DiscretePoseChanged(PlayerPose before, PlayerPose after) =>
            before.State != after.State || before.Crouched != after.Crouched || before.Shirt != after.Shirt || before.Model != after.Model ||
            before.Spectating != after.Spectating;
        private static (float X, float Y, float Z) PresentationPosition(PlayerPose pose) =>
            pose.Spectating ? (pose.SpectatorHeadX, pose.SpectatorHeadY, pose.SpectatorHeadZ) : (pose.X, pose.Y, pose.Z);
        private static double DistanceSquared(float ax, float ay, float az, float bx, float by, float bz)
        {
            double dx = (double)ax - bx, dy = (double)ay - by, dz = (double)az - bz;
            return dx * dx + dy * dy + dz * dz;
        }
        private bool Distant(Outbox box, uint peer, ItemPose position, uint owner, double time)
        {
            if (spatialInterestRadiusSquared == 0 || !box.Acknowledged || !box.Pose.HasValue ||
                time - box.PoseTime > PoseFreshnessSeconds || owner == peer) return false;
            var pose = box.Pose.Value;
            if (DistanceSquared(pose.X, pose.Y, pose.Z, position.X, position.Y, position.Z) <= spatialInterestRadiusSquared)
                return false;
            // World motion remains current around the native root and around a detached
            // spectator camera. Otherwise a camera following a distant vehicle inherits
            // that vehicle's far-motion jumps despite receiving its rider's full poses.
            return !pose.Spectating || DistanceSquared(pose.SpectatorHeadX, pose.SpectatorHeadY, pose.SpectatorHeadZ,
                position.X, position.Y, position.Z) > spatialInterestRadiusSquared;
        }
        private bool Distant(Outbox box, uint peer, WorldRecord record, double time)
        {
            try
            {
                if (record.Adapter == ItemState.Adapter)
                {
                    var item = ItemState.Decode(record.Payload);
                    // Core has no native carrier frame. Local coordinates cannot be
                    // compared with player world positions; keep this motion current.
                    return item.Carrier == 0 && Distant(box, peer, item.Pose, item.Lease.Owner, time);
                }
                if (record.Adapter == VehicleState.Adapter)
                {
                    var vehicle = VehicleState.Decode(record.Payload);
                    return Distant(box, peer, vehicle.Pose, vehicle.Driver.Owner, time);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException) { }
            return false;
        }
        private void FlushDeferredMotion(Outbox box, uint peer, double time)
        {
            if (!box.Acknowledged || box.DeferredMotion.Count == 0 || time < box.NextInterestScan) return;
            box.NextInterestScan = time + InterestScanSeconds;
            var ready = box.ReadyDeferredMotion;
            ready.Clear();
            foreach (var record in box.DeferredMotion.Values)
            {
                // Far motion has a lower cadence. An absent or stale player pose
                // fails open and sends the latest state immediately.
                if (time < box.DeferredUntil[record.Id] &&
                    Distant(box, peer, record, time))
                {
                    box.NextInterestScan = Math.Min(box.NextInterestScan, box.DeferredUntil[record.Id]);
                    continue;
                }
                box.Latest[record.Id] = record;
                ready.Add(record.Id);
            }
            foreach (ulong id in ready) RemoveDeferredMotion(box, id);
            ready.Clear();
        }
        private void SendRecord(uint peer, MessageKind kind, WorldRecord record)
        {
            // Preserve the allocation-light single-recipient path.
            if (outgoing.Count <= 1) { session.SendWorld(peer, kind, record.Write); return; }
            var key = (kind, record);
            if (!pumpPackets.TryGetValue(key, out var bytes))
            {
                bytes = Wire.Encode(kind, session.Epoch, record.Write);
                pumpPackets.Add(key, bytes);
            }
            session.SendEncodedWorldRecord(peer, kind, bytes);
        }
        private (WorldRecord[] Records, string Digest) Checkpoint(Outbox box, SnapshotScope scope)
        {
            if (!checkpointBaselines.TryGetValue(scope.Adapter, out var baseline))
            {
                baseline = new CheckpointBaseline(CaptureBaseline(scope));
                checkpointBaselines.Add(scope.Adapter, baseline);
            }
            var records = baseline.Records;
            bool substituted = false;
            // Deferred item/vehicle motion cannot change an unrelated section's
            // delivered state. Preserve its shared digest without scanning it.
            if (box.DeferredBaselineCounts.ContainsKey(scope.Adapter))
                for (int i = 0; i < records.Length; i++)
                    if (box.DeferredBaseline.TryGetValue(records[i].Id, out var delivered))
                    {
                        if (!substituted) { records = (WorldRecord[])records.Clone(); substituted = true; }
                        records[i] = delivered;
                    }
            if (substituted) return (records, WorldRecord.DigestOrdered(records));
            return (records, baseline.Digest ??= WorldRecord.DigestOrdered(records));
        }
        public void Pump(int recordsPerPeer = 32)
        {
            if (recordsPerPeer < 1) throw new ArgumentOutOfRangeException(nameof(recordsPerPeer));
            if (!session.IsReady) return;
            if (!session.IsHost)
            {
                if (awaitingInitial) { awaitingInitial = false; lastProgress = now(); }
                if (Ready && !receiver.Receiving) RetryApplies();
                if ((!FullySynchronized || receiver.Receiving || RepairPending) && now() - lastProgress > 15)
                    session.Stop("World synchronization stalled for 15 seconds; reconnect to retry.");
                return;
            }
            DisconnectOverflowedPeers();
            try
            {
                foreach (var pair in OutgoingSnapshot)
                {
                    if (pair.Value.Overflowed) continue;
                    int remaining = recordsPerPeer;
                    while (remaining > 0 && pair.Value.Snapshot.Count > 0 && session.HasWorldReliableSendHeadroom(pair.Key))
                    {
                        var message = pair.Value.Snapshot.Dequeue();
                        if (message.Kind == MessageKind.WorldDelta && message.Record != null)
                        {
                            MarkOrderedMembershipSent(pair.Value, message.Record);
                            if (pair.Value.SnapshotPresentation.Remove(message.Record)) pair.Value.PendingPresentation--;
                        }
                        if (message.Record != null) SendRecord(pair.Key, message.Kind, message.Record);
                        else session.SendWorld(pair.Key, message.Kind, message.Body!);
                        remaining--;
                        if (message.Kind == MessageKind.SnapshotEnd && !pair.Value.CatchupQueued)
                        {
                            // Freeze a finite catch-up boundary. A busy host may publish
                            // faster than this peer's budget forever; waiting for Latest
                            // to stay empty would otherwise starve initial activation.
                            QueueOrdered(pair.Value);
                            QueuePresentation(pair.Value);
                            foreach (var record in pair.Value.Latest.Values) pair.Value.Snapshot.Enqueue((MessageKind.WorldDelta, null, record));
                            pair.Value.Latest.Clear();
                            pair.Value.Snapshot.Enqueue((MessageKind.SnapshotCaughtUp, EmptyBody, null)); pair.Value.CatchupQueued = true;
                        }
                        if (message.Kind == MessageKind.SnapshotCaughtUp) pair.Value.CatchupSent = true;
                    }
                    // Snapshot records and the finite catch-up marker define an ordered
                    // boundary. A congested peer retains this queue in place and cannot
                    // send Latest/Ordered deltas around a deferred snapshot record.
                    if (pair.Value.Snapshot.Count > 0) continue;
                    if (pair.Value.Overflowed) continue;
                    FlushDeferredMotion(pair.Value, pair.Key, now());
                    // Preserve membership-transition order while giving ordinary state a turn.
                    // A sustained ordered stream must not starve the clock or other world state.
                    while (remaining > 0 && (pair.Value.Ordered.Count > 0 || pair.Value.Latest.Count > 0 || pair.Value.Presentation.Count > 0) &&
                        session.HasWorldReliableSendHeadroom(pair.Key))
                    {
                        WorldRecord delta;
                        bool ordinary = pair.Value.Ordered.Count > 0 || pair.Value.Latest.Count > 0;
                        if (pair.Value.Presentation.Count > 0 && (!ordinary || pair.Value.PreferPresentation))
                        {
                            delta = pair.Value.Presentation.Dequeue();
                            pair.Value.PendingPresentation--;
                            pair.Value.PreferPresentation = false;
                        }
                        else if (pair.Value.Latest.Count > 0 && (pair.Value.Ordered.Count == 0 || pair.Value.PreferLatest))
                        {
                            delta = pair.Value.Latest.First;
                            pair.Value.Latest.Remove(delta.Id);
                            pair.Value.PreferLatest = false;
                            pair.Value.PreferPresentation = true;
                        }
                        else
                        {
                            delta = pair.Value.Ordered.Dequeue();
                            MarkOrderedMembershipSent(pair.Value, delta);
                            pair.Value.PreferLatest = true;
                            pair.Value.PreferPresentation = true;
                        }
                        SendRecord(pair.Key, MessageKind.WorldDelta, delta); remaining--;
                    }
                    // Checkpoints describe exactly the reliable ordered stream already
                    // sent to this peer, never unsent or coalesced future records.
                    var box = pair.Value;
                    if (remaining > 0 && box.Snapshot.Count == 0 && box.Latest.Count == 0 && box.Ordered.Count == 0 && box.Presentation.Count == 0 &&
                        sections.Length > 0 && now() >= box.NextCheckpoint && session.HasWorldReliableSendHeadroom(pair.Key))
                    {
                        ushort adapter = sections[box.CheckpointSection++ % sections.Length];
                        var scope = SnapshotScope.Section(adapter);
                        // Hash the ordered state actually sent to this peer. For
                        // deferred items that is the last delivered full record.
                        var (records, digest) = Checkpoint(box, scope);
                        session.SendWorld(pair.Key, MessageKind.WorldCheckpoint, w => { scope.Write(w); w.Write(records.Length); Wire.WriteText(w, digest, 64); });
                        box.NextCheckpoint = now() + 5;
                    }
                }
                DisconnectOverflowedPeers();
            }
            finally { pumpPackets.Clear(); }
        }
        public void Publish(ulong entity, byte[] value)
        {
            if (!session.IsHost || !hostRecords.TryGetValue(entity, out var previous)) throw new InvalidOperationException("Only host-owned registered entities can be published.");
            if (previous.SameValue(value)) return;
            var record = previous.Next(value); hostRecords[entity] = record; checkpointBaselines.Remove(record.Adapter); snapshotBaseline = null;
            bool motionOnly = false, itemMembershipChanged = false;
            ItemPose oldPose = default, newPose = default;
            uint owner = ItemLease.Unowned;
            if (outgoing.Count != 0 && previous.Adapter == ItemState.Adapter)
            {
                // The host may also use adapter 2 in synthetic catalogs; invalid
                // item payloads simply take the original, unfiltered path.
                try
                {
                    var oldItem = ItemState.Decode(previous.PayloadSpan); var newItem = ItemState.Decode(record.PayloadSpan);
                    itemMembershipChanged = oldItem.ContainerEntity != newItem.ContainerEntity || oldItem.ContainerSlot != newItem.ContainerSlot;
                    motionOnly = spatialInterestRadiusSquared > 0 && oldItem.Carrier == 0 && newItem.Carrier == 0 && IsItemMotionOnly(oldItem, newItem);
                    oldPose = oldItem.Pose; newPose = newItem.Pose; owner = newItem.Lease.Owner;
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException) { }
            }
            else if (outgoing.Count != 0 && spatialInterestRadiusSquared > 0 && previous.Adapter == VehicleState.Adapter)
            {
                try
                {
                    var oldVehicle = VehicleState.Decode(previous.Payload); var newVehicle = VehicleState.Decode(record.Payload);
                    motionOnly = IsVehicleMotionOnly(oldVehicle, newVehicle);
                    oldPose = oldVehicle.Pose; newPose = newVehicle.Pose; owner = newVehicle.Driver.Owner;
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException) { }
            }
            double time = now();
            foreach (var pair in outgoing)
            {
                var box = pair.Value;
                if (motionOnly && box.Snapshot.Count == 0 && !box.Latest.ContainsKey(entity) && !box.OrderedItems.ContainsKey(entity) &&
                    Distant(box, pair.Key, oldPose, owner, time) && Distant(box, pair.Key, newPose, owner, time))
                {
                    if (!box.DeferredMotion.ContainsKey(entity))
                    {
                        box.DeferredBaseline[entity] = previous;
                        box.DeferredBaselineCounts.TryGetValue(previous.Adapter, out int deferredCount);
                        box.DeferredBaselineCounts[previous.Adapter] = deferredCount + 1;
                        box.DeferredUntil[entity] = time + FarMotionIntervalSeconds;
                    }
                    box.DeferredMotion[entity] = record;
                    SpatialDeltasDeferred++;
                }
                else
                {
                    QueueDelta(box, record, itemMembershipChanged);
                    RemoveDeferredMotion(box, entity);
                }
            }
            changed.Value?.Invoke(record);
        }
        // A replacement's contents must reach the replica before the consumed parent retires.
        // Ordinary coalescing keeps an already queued parent's earlier place (e.g. hand motion),
        // so explicitly put this dependent update behind the records just registered/published.
        public void PublishAfterQueued(ulong entity, byte[] value)
        {
            Publish(entity, value);
            var record = hostRecords[entity];
            foreach (var box in outgoing.Values)
                if (box.Latest.Remove(entity)) box.Latest[entity] = record;
        }
        private static bool IsItemMotionOnly(ItemState before, ItemState after)
        {
            var a = before.Values; var b = after.Values;
            return before.Exists && after.Exists && before.Consumption == null && after.Consumption == null &&
                before.Prefab == after.Prefab && before.Lease.Owner == after.Lease.Owner && before.Lease.Generation == after.Lease.Generation &&
                before.ScaleX == after.ScaleX && before.ScaleY == after.ScaleY && before.ScaleZ == after.ScaleZ &&
                before.ContainerEntity == after.ContainerEntity && before.ContainerSlot == after.ContainerSlot && before.Carrier == after.Carrier &&
                a.Value1 == b.Value1 && a.Value2 == b.Value2 && a.IntValue1 == b.IntValue1 && a.IntValue2 == b.IntValue2 && a.Flags == b.Flags;
        }
        private static bool IsVehicleMotionOnly(VehicleState before, VehicleState after)
        {
            // Keep ownership, controls, lighting and accessory changes immediate even when the body is
            // outside a peer's interest radius. Continuous telemetry (RPM, fuel level, steering and
            // pedal travel) rides the far-motion cadence with the pose; only its edges (a pedal pressed
            // or released, the tank running dry) stay immediate (Astra R18).
            return before.Driver.Owner == after.Driver.Owner && before.Driver.Generation == after.Driver.Generation &&
                (before.Fuel <= 0) == (after.Fuel <= 0) && before.RefuelCredit == after.RefuelCredit &&
                Pressed(before.Throttle) == Pressed(after.Throttle) && Pressed(before.Brake) == Pressed(after.Brake) &&
                Pressed(before.Handbrake) == Pressed(after.Handbrake) &&
                before.Gear == after.Gear && before.AutomaticPark == after.AutomaticPark && before.Ignition == after.Ignition &&
                before.EngineIgnition == after.EngineIgnition && before.Starter == after.Starter &&
                before.ParkingBrake == after.ParkingBrake && before.TransferCase == after.TransferCase &&
                SameAccessory(before.Accessory, after.Accessory) && SameLighting(before.Lighting, after.Lighting) &&
                before.Effects.Flags == after.Effects.Flags;
        }
        private static bool SameAccessory(ItemPose? before, ItemPose? after) => before.HasValue == after.HasValue &&
            (!before.HasValue || SamePose(before.Value, after!.Value));
        private static bool SamePose(ItemPose before, ItemPose after) =>
            before.X.Equals(after.X) && before.Y.Equals(after.Y) && before.Z.Equals(after.Z) &&
            before.Qx.Equals(after.Qx) && before.Qy.Equals(after.Qy) && before.Qz.Equals(after.Qz) && before.Qw.Equals(after.Qw);
        private static bool SameLighting(VehicleLightingState before, VehicleLightingState after) =>
            before.Lamps == after.Lamps && before.Controls == after.Controls && before.LeftPhase == after.LeftPhase &&
            before.RightPhase == after.RightPhase && before.InteriorMode == after.InteriorMode;
        private static bool Pressed(float pedal) => pedal > 0.05f;
        public void Register(WorldRecord record)
        {
            if (!session.IsHost || record.Id <= WorldRecord.DynamicIdStart || !dynamicAdapters.Contains(record.Adapter) ||
                hostRecords.ContainsKey(record.Id) || hostRecords.Count >= SnapshotReceiver.MaxEntities)
                throw new InvalidOperationException("Invalid dynamic registration.");
            hostRecords.Add(record.Id, record); checkpointBaselines.Remove(record.Adapter); snapshotBaseline = null;
            AddCatalogMember(record);
            bool contained = false;
            if (outgoing.Count != 0 && record.Adapter == ItemState.Adapter)
            {
                try { contained = ItemState.Decode(record.PayloadSpan).ContainerEntity != 0; }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException) { }
            }
            foreach (var box in outgoing.Values) QueueDelta(box, record, contained);
            changed.Value?.Invoke(record);
        }
        // Preserve the public signature used by existing compiled adapters.
        public void Request(ulong entity, byte[]? payload, bool useLease) => Request(entity, payload, useLease, null);
        public void Request(ulong entity, byte[]? payload = null, bool useLease = false, uint? expectedRevision = null)
        {
            if (!Ready || !Records.TryGetValue(entity, out var record)) return;
            payload ??= Array.Empty<byte>();
            if (payload.Length > 128 || (useLease && !leaseAdapters.Contains(record.Adapter)) ||
                expectedRevision == 0 || (useLease && expectedRevision.HasValue)) throw new InvalidDataException("Invalid world command payload, revision or lease adapter.");
            if (session.IsHost) commandRequested.For(record.Adapter).Value?.Invoke(0, entity, payload);
            // A correlated host receipt can arrive before its queued world delta.
            // Keep the same host equality check, using only that caller-known revision.
            else session.SendWorld(0, MessageKind.WorldCommand, w => { w.Write(++nextOperation); w.Write(entity); w.Write(useLease ? 0u : expectedRevision ?? record.Revision); w.Write((byte)payload.Length); w.Write(payload); });
        }
        public void Reply(uint peer, ulong entity, byte[] payload)
        {
            if (!session.IsHost || !Ready || !hostRecords.ContainsKey(entity) || payload == null || payload.Length > 128)
                throw new InvalidOperationException("Invalid host reply.");
            var copy = (byte[])payload.Clone();
            if (peer == 0) replyReceived.Value?.Invoke(entity, copy);
            else session.SendWorld(peer, MessageKind.WorldReply, w => { w.Write(entity); w.Write((byte)copy.Length); w.Write(copy); });
        }
        public bool RequestResync(SnapshotScope scope, string reason = "Requested by player")
        {
            if (!FullySynchronized || session.IsHost || RepairPending || receiver.Receiving || now() - lastRepairRequest < 1) return false;
            receiver.ValidateScope(scope);
            if (scope.Kind == SnapshotScopeKind.Entity && !Records.ContainsKey(scope.Entity)) return false;
            requestedRepair = scope; LastRepairReason = reason; lastProgress = now(); lastRepairRequest = lastProgress;
            session.SendWorld(0, MessageKind.ResyncRequest, scope.Write);
            return true;
        }
        private void Receive(uint sender, Packet packet)
        {
            var r = packet.Reader;
            switch (packet.Kind)
            {
                case MessageKind.SnapshotBegin:
                    var scope = SnapshotScope.Read(r); int count = r.ReadInt32(); string digest = Wire.ReadText(r, 64); packet.Complete();
                    if (receiver.Ready && (!requestedRepair.HasValue || !requestedRepair.Value.Equals(scope))) throw new InvalidDataException("Unsolicited repair snapshot.");
                    receiver.Begin(scope, count, digest); receivingScope = scope; lastProgress = now(); break;
                case MessageKind.SnapshotRecord:
                    var initial = WorldRecord.Read(r); packet.Complete(); receiver.Add(initial); lastProgress = now(); break;
                case MessageKind.SnapshotEnd:
                    packet.Complete(); var applied = receiver.Commit();
                    if (requestedRepair.HasValue) { RepairsApplied++; requestedRepair = null; }
                    lastProgress = now();
#if MONMP_DEVELOPMENT
                    long snapshotStart = Stopwatch.GetTimestamp();
#endif
                    // Native snapshot adapters can throw while applying scene state; isolate them per subscriber (#143).
                    using (SnapshotApplyScope?.Invoke()) ApplySnapshot(applied, receivingScope);
#if MONMP_DEVELOPMENT
                    Cost(ushort.MaxValue, Stopwatch.GetTimestamp() - snapshotStart);
#endif
                    break;
                case MessageKind.WorldDelta:
                    var delta = WorldRecord.Read(r); packet.Complete();
                    if (receiver.Apply(delta))
                    {
#if MONMP_DEVELOPMENT
                        long changeStart = Stopwatch.GetTimestamp();
#endif
                        using (DeltaScope?.Invoke()) ApplyDelta(delta);
#if MONMP_DEVELOPMENT
                        Cost(delta.Adapter, Stopwatch.GetTimestamp() - changeStart);
#endif
                    }
                    lastProgress = now(); break;
                case MessageKind.WorldReply:
                    ulong replyEntity = r.ReadUInt64(); int replySize = r.ReadByte();
                    if (replySize > 128 || replySize > r.BaseStream.Length - r.BaseStream.Position || !Records.ContainsKey(replyEntity)) throw new InvalidDataException("Invalid world reply.");
                    byte[] reply = r.ReadBytes(replySize); packet.Complete();
                    IsolatedReply(replyReceived.Handlers, replyEntity, reply); break;
                case MessageKind.WorldCommand:
                    ulong operation = r.ReadUInt64(), entity = r.ReadUInt64(); uint revision = r.ReadUInt32();
                    int size = r.ReadByte(); if (size > 128 || size > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Invalid command length.");
                    byte[] payload = r.ReadBytes(size); packet.Complete();
                    if (operation == 0 || !hostRecords.TryGetValue(entity, out var record)) throw new InvalidDataException("Unknown world command.");
                    if (operations.TryGetValue(sender, out ulong previous) && operation <= previous) return;
                    operations[sender] = operation;
                    bool rejectedCommand = revision != record.Revision && (revision != 0 || !leaseAdapters.Contains(record.Adapter));
#if MONMP_DEVELOPMENT
                    long commandStart = Stopwatch.GetTimestamp(), failuresBefore = HandlerFailures;
#elif MONMP_PERFORMANCE_CAPTURE
                    long commandStart = Stopwatch.GetTimestamp();
#endif
                    int commandCalls = 0;
                    try
                    {
                        if (!rejectedCommand)
                            using (CommandScope?.Invoke(sender))
                            {
                                // A scope may change subscriptions. Resolve after
                                // entering it, as on the original dispatch path.
                                var handlers = commandRequested.For(record.Adapter).Handlers;
                                IsolatedCommand("command", handlers, sender, entity, payload, ref commandCalls);
                            }
                        else
                        {
                            if (outgoing.TryGetValue(sender, out var box)) QueueDelta(box, record);
                            var handlers = commandRejected.Handlers;
                            IsolatedCommand("rejected command", handlers, sender, entity, payload, ref commandCalls);
                        }
                    }
#if MONMP_DEVELOPMENT
                    finally { CostCommand(record.Adapter, Stopwatch.GetTimestamp() - commandStart, commandCalls, HandlerFailures - failuresBefore, rejectedCommand); }
#elif MONMP_PERFORMANCE_CAPTURE
                    finally { CaptureTiming.Command(record.Adapter, Stopwatch.GetTimestamp() - commandStart); }
#else
                    finally { }
#endif
                    break;
                case MessageKind.SnapshotCaughtUp:
                    packet.Complete();
                    if (!receiver.Ready || receiver.Receiving || caughtUp) throw new InvalidDataException("Unexpected initial catch-up marker.");
                    caughtUp = true; lastProgress = now();
                    session.SendWorld(0, MessageKind.SnapshotAcknowledged, _ => { });
                    break;
                case MessageKind.SnapshotAcknowledged:
                    packet.Complete();
                    if (!outgoing.TryGetValue(sender, out var pending) || !pending.CatchupSent || pending.Acknowledged)
                        throw new InvalidDataException("Unexpected snapshot acknowledgment.");
                    pending.Acknowledged = true; PeerSynchronized?.Invoke(sender); break;
                case MessageKind.ResyncRequest:
                    var requested = SnapshotScope.Read(r); packet.Complete(); receiver.ValidateScope(requested);
                    if (!outgoing.TryGetValue(sender, out var target) || target.Snapshot.Count != 0 ||
                        (requested.Kind == SnapshotScopeKind.Entity && (!hostRecords.TryGetValue(requested.Entity, out var found) || found.Adapter != requested.Adapter)))
                        throw new InvalidDataException("Invalid or overlapping repair request.");
                    double time = now(); target.RepairTokens = Math.Min(4, target.RepairTokens + Math.Max(0, time - target.TokenTime)); target.TokenTime = time;
                    if (target.RepairTokens < 1) throw new InvalidDataException("Too many world repair requests.");
                    target.RepairTokens--; QueueSnapshot(target, requested); break;
                case MessageKind.WorldCheckpoint:
                    var check = SnapshotScope.Read(r); int checkCount = r.ReadInt32(); string checkDigest = Wire.ReadText(r, 64); packet.Complete();
                    receiver.ValidateScope(check);
                    if (!receiver.Ready || receiver.Receiving || check.Kind != SnapshotScopeKind.Section || checkCount < 0 || checkCount > SnapshotReceiver.MaxEntities ||
                        checkDigest.Length != 64 || checkDigest.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid world checkpoint.");
                    if (RepairPending) break;
                    var local = receiver.Records.Values.Where(check.Contains).ToArray();
                    if (local.Length != checkCount || WorldRecord.Digest(local) != checkDigest)
                    { DivergencesDetected++; RequestResync(check, "Host checksum differs for " + check); }
                    else CheckpointsVerified++;
                    break;
                default: throw new InvalidDataException("Invalid world message.");
            }
        }
        public void Dispose()
        {
            session.PeerWorldReady -= Joined; session.PeerLeft -= Left; session.PoseReceived -= OnPose; session.WorldPacketReceived -= Receive;
            if (session.PoseRelayFilter == poseRelayFilter) session.PoseRelayFilter = null;
            outgoing.Clear(); operations.Clear();
            outgoingSnapshot = null;
            snapshotBaseline = null; pumpPackets.Clear(); checkpointBaselines.Clear();
            catalogSections.Clear(); sortedHostIds = null;
            failedApplies.Clear(); failedSnapshots.Clear(); nextApplyRetry = double.PositiveInfinity;
            relayedPoses.Clear();
        }
    }
}
