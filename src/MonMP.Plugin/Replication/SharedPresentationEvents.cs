using System;
using System.Collections.Generic;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal enum PresentationKind : byte
    {
        VehicleCrash = 1,
        GardenFertilize,
        GardenPlant,
        GardenShovel,
        NpcVoice,
        MachinePullStart,
        LeoBite,
        LeoEat,
        LeoWhistle,
        LeoNeeds,
        AnimalCrossing,
        BoltWork,
        PartInstalled,
        SpeedwayCue,
        SpeedwayDent,
        SpeedwayRepair,
        MachineDone,
        Fishing,
        FryerPress,
        NitrousRefillStation,
        VehicleSwitch,
        RogerUnlock,
        VehicleDoor,
        TirePump,
        RepairTool,
        VehicleWindowClick,
        SugarShackPress,
        CashCue,
        PizzaSnackOutcome,
        FishBucket,
        Consumption,
        RadioButton,
        PlayerAnxietyWarning,
        WorldNotice,
        MachineControl,
        HoodAction,
    }

    // One-shot sights and sounds the native game plays only where the action ran: a crash, a dig, an
    // NPC's greeting voice, Leo's bite. The machine that played it posts it (a guest through the host),
    // and every other peer plays it once. Events already in the log when a peer joins are history.
    internal sealed class SharedPresentationEvents : IDisposable
    {
        public const ushort Adapter = PresentationEvent.Adapter;
        internal static readonly ulong Entity = StaticId.Hash("presentation-events/v1");
        private const int PostsPerSecond = 30;
        // Pace producer bursts to half the rolling log per pump. The channel retains uncovered
        // batches for slower peers; this separate bounded producer queue contains native floods.
        private const int PerPump = PresentationEvent.LogCapacity / 2, PendingLimit = 96;

        private sealed class Handler
        {
            public Action<PresentationEvent> Play = null!;
            public float GuestRange;
        }

        private bool host => session?.IsHost == true;
        private readonly Dictionary<byte, Handler> handlers = new Dictionary<byte, Handler>();
        private readonly Dictionary<uint, (Vector3 At, float Time)> presence = new Dictionary<uint, (Vector3, float)>();
        private readonly Dictionary<uint, (float Second, int Count)> rates = new Dictionary<uint, (float, int)>();
        private readonly List<PresentationEvent> log = new List<PresentationEvent>();
        private readonly Queue<PresentationEvent> pending = new Queue<PresentationEvent>();
        private readonly BoltLoopAdmission boltLoops = new BoltLoopAdmission();
        private int sincePump;
        internal int Dropped { get; private set; }
        private NetworkSession? session;
        private WorldChannel? channel;
        private uint sequence, seen;
        private bool known, disposed;


#if MONMP_DEVELOPMENT
        internal int TestPlayed { get; private set; }


        internal int TestPosted { get; private set; }
#endif


        internal static IEnumerable<WorldRecord> InitialRecords() =>
            new[] { new WorldRecord(Entity, 1, Adapter, PresentationEvent.EncodeLog(Array.Empty<PresentationEvent>())) };

        internal bool Live => !disposed && session?.IsRunning == true && channel?.Ready == true;
        internal uint LocalPeer => session?.LocalPeerId ?? 0;

#if MONMP_DEVELOPMENT
        internal bool TestRepairHistorySilent()
        {
            if (host || channel == null) return false;
            int before = TestPlayed;
            Snapshot(channel.Records);
            if (channel.Records.TryGetValue(Entity, out var record)) { Changed(record); Changed(record); }
            return before == TestPlayed;
        }
#endif

        internal bool IsHost => Live && host;

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected; channel = world;
            Register(PresentationKind.TirePump, PresentationReplays.TirePump);
            Register(PresentationKind.RepairTool, RepairToolPresentation.Play);
            Register(PresentationKind.RadioButton, e => MonMPPlugin.Instance?.World?.FurnitureRadios.PlayButtonClick(e));
            Register(PresentationKind.PlayerAnxietyWarning, PlayerAnxietyWarningPresentation.Play, 6f);
            connected.PoseReceived += OnPose; connected.PeerLeft += OnLeft;
            world.SnapshotApplied += Snapshot; world.Changed += Changed; world.CommandRequested += Command;
        }

        // guestRange: how far from its own avatar a guest may report this kind (0: host only).
        internal void Register(PresentationKind kind, Action<PresentationEvent> play, float guestRange = 0)
            => handlers[(byte)kind] = new Handler { Play = play, GuestRange = guestRange };

        private static PresentationEvent Make(PresentationKind kind, byte variant, ulong target, Vector3 at, float value) =>
            new PresentationEvent(0, (byte)kind, variant, 0, target, at.x, at.y, at.z, value);

        // This machine already played it; everyone else plays it too.
        internal void Post(PresentationKind kind, byte variant, ulong target, Vector3 at, float value = 0)
        {
            if (!Live || !Finite(at) || float.IsNaN(value) || float.IsInfinity(value)) return;
            var e = Make(kind, variant, target, at, value);
            if (host) { Append(e, 0); return; }
            // An append, not an edit of the log: two players' cues made against the same log revision both count.
            channel!.Request(Entity, e.EncodeRequest(), useLease: true);
        }

        // The simulator records recipients when the contact happens. The host validates
        // each against occupancy (including a short seat-exit race) and retains that peer
        // in the delivery rather than applying it to whoever occupies the chassis later.
        internal void PostCrashEffect(PresentationEvent report)
        {
            if (!Live || !Finite(new Vector3(report.X, report.Y, report.Z)) || !VehicleCrashGameplay.ValidReport(report)) return;
            if (host) { DeliverCrashEffect(report); return; }
            channel!.Request(Entity, report.EncodeRequest(), useLease: true);
        }
        private void DeliverCrashEffect(PresentationEvent report)
        {
            if (!VehicleCrashGameplay.AcceptRecipient(report)) return;
            Play(Append(VehicleCrashGameplay.Deliver(report), report.Actor));
        }

        // Host: an action it ran for a guest. The actor (who already presented it) skips it; the host
        // plays it here when its own native run did not.
        internal void HostAnnounce(PresentationKind kind, byte variant, ulong target, Vector3 at, float value, uint actor, bool playHere)
        {
            if (!Live || !host || !Finite(at)) return;
            var e = Make(kind, variant, target, at, value);
            Append(e, actor);
            if (playHere) Play(e.WithSequence(sequence, actor));
        }

        private PresentationEvent Append(PresentationEvent e, uint actor)
        {
            var stamped = e.WithSequence(++sequence, actor);

#if MONMP_DEVELOPMENT
            TestPosted++;
#endif

            bool admitted = true;
            if (pending.Count == 0 && sincePump < PerPump) Commit(stamped);
            else if (pending.Count < PendingLimit) pending.Enqueue(stamped);
            else if (Critical(stamped) && DisplaceCosmetic()) pending.Enqueue(stamped);
            else { admitted = false; DroppedEvent(); }
            if (admitted && BoltStart(e)) boltLoops.Start(actor, e.Variant == (byte)PresentationReplays.BoltCue.UnscrewStart);
            if (BoltStop(e)) boltLoops.Stop(actor, e.Variant == (byte)PresentationReplays.BoltCue.UnscrewStop);
            return stamped;
        }
        private static bool BoltStart(PresentationEvent e) => e.Kind == (byte)PresentationKind.BoltWork &&
            (e.Variant == (byte)PresentationReplays.BoltCue.ScrewStart || e.Variant == (byte)PresentationReplays.BoltCue.UnscrewStart);
        private static bool BoltStop(PresentationEvent e) => e.Kind == (byte)PresentationKind.BoltWork &&
            (e.Variant == (byte)PresentationReplays.BoltCue.ScrewStop || e.Variant == (byte)PresentationReplays.BoltCue.UnscrewStop);
        private static bool Critical(PresentationEvent e) => VehicleCrashGameplay.IsDelivery(e) || BoltStop(e) || e.Kind == (byte)PresentationKind.WorldNotice;
        private bool DisplaceCosmetic()
        {
            bool removed = false;
            int count = pending.Count;
            while (count-- > 0)
            {
                var queued = pending.Dequeue();
                if (!removed && !Critical(queued)) { removed = true; DroppedEvent(); }
                else pending.Enqueue(queued);
            }
            return removed;
        }
        private void DroppedEvent()
        {
            if (++Dropped <= 5 || Dropped % 100 == 0)
                Debug.LogWarning("MonMP dropped a presentation event under load (" + Dropped + " total).");
        }
        private void Commit(PresentationEvent stamped)
        {
            if (!ActorStillPresent(stamped)) return;
            log.Add(stamped);
            while (log.Count > PresentationEvent.LogCapacity) log.RemoveAt(0);
            sincePump++;
            channel!.Publish(Entity, PresentationEvent.EncodeLog(log));
        }
        // Host, once per frame just before the channel pump: release waiting events into the log.
        internal void Update()
        {
            PresentationReplays.TickBoltWork();
            SpatialSounds.Tick();
            VehicleCrashGameplay.Tick();
            VehicleDoorPresentation.Tick();
            TirePumpPresentation.Tick();
            RepairToolPresentation.Tick();
            if (!IsHost) { pending.Clear(); sincePump = 0; return; }
            while (pending.Count > 0 && sincePump < PerPump) Commit(pending.Dequeue());
            sincePump = 0;
        }

        // True while a received event plays: the native calls it makes are not this machine's own actions.
        internal static bool Replaying { get; private set; }

        // Loops and fishing lines belong to the actor's lifetime. A host-run machine completion
        // may still be observed after its requester leaves, so world one-shots are kept.
        private static bool ActorLifetime(PresentationEvent e) =>
            e.Kind == (byte)PresentationKind.BoltWork || e.Kind == (byte)PresentationKind.Fishing || e.Kind == (byte)PresentationKind.TirePump;
        // Host 0 is always present for a live session; guest/local membership is established before
        // world records independently of poses. All owner cue variants share this departure rule.
        private bool ActorStillPresent(PresentationEvent e) =>
            e.Actor == 0 || !ActorLifetime(e) || session?.Peers.ContainsKey(e.Actor) == true;

        private void Play(PresentationEvent e)
        {
            // PeerLeft may precede a coalesced delta carrying an earlier start cue.
            if (!ActorStillPresent(e)) return;
            if (!handlers.TryGetValue(e.Kind, out var handler)) return;
            Replaying = true;
            try { handler.Play(e);
#if MONMP_DEVELOPMENT
TestPlayed++;
#endif
 }
            catch (Exception ex) { Debug.LogWarning("MonMP presentation event " + (PresentationKind)e.Kind + " failed: " + ex.Message); }
            finally { Replaying = false; }
        }

        private static bool Finite(Vector3 v) => ItemPose.FiniteValue(v.x, 100000) && ItemPose.FiniteValue(v.y, 100000) && ItemPose.FiniteValue(v.z, 100000);

        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (new Vector3(pose.X, pose.Y, pose.Z), Time.unscaledTime);
        private void OnLeft(uint peer)
        {
            presence.Remove(peer); rates.Remove(peer); boltLoops.Remove(peer);
            int queued = pending.Count;
            while (queued-- > 0)
            {
                var e = pending.Dequeue();
                if (e.Actor != peer || !ActorLifetime(e)) pending.Enqueue(e);
            }
            SpatialSounds.StopOwner(peer); RemoteFishing.PeerLeft(peer);
            TirePumpPresentation.Remove(peer);
        }

        [MonMP.Core.World.WorldCommandAdapters(SharedPresentationEvents.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || entity != Entity || peer == 0) return;
            PresentationEvent e;
            try { e = PresentationEvent.DecodeRequest(payload); }
            catch (System.IO.InvalidDataException) { return; }
            bool boltStop = BoltStop(e);
            if (e.Kind == (byte)PresentationKind.BoltWork &&
                (e.Target != 0 || e.Value != 0 || e.Variant < 1 || e.Variant > (byte)PresentationReplays.BoltCue.Impact)) return;
            // A stop ends an admitted sound even if the actor moved or its last pose expired.
            // It grants no new effects, and consuming the active bit bounds duplicate stops.
            if (boltStop && !boltLoops.Contains(peer, e.Variant == (byte)PresentationReplays.BoltCue.UnscrewStop)) return;
            if (e.Kind == (byte)PresentationKind.Fishing &&
                MonMPPlugin.Instance?.World?.FishingPresentation.AcceptCue(peer, e) != true) return;
            if (e.Kind == (byte)PresentationKind.VehicleSwitch && !VehicleSwitchPresentation.AcceptCue(peer, e)) return;
            bool vehicleCrash = e.Kind == (byte)PresentationKind.VehicleCrash;
            if (vehicleCrash && !VehicleCrashPresentation.AcceptCue(peer, e)) return;
            if (e.Kind == (byte)PresentationKind.PlayerAnxietyWarning && !PlayerAnxietyWarningPresentation.AcceptCue(peer, e)) return;
            if (!handlers.TryGetValue(e.Kind, out var handler) || handler.GuestRange <= 0) return;
            // A native impact can wait behind an earlier clip, retaining its original contact point.
            // Validate its simulator/target above; interaction reach would reject a fast driver who
            // has moved away by playback time. Every guest still needs a fresh presence sample.
            if (!boltStop && (!presence.TryGetValue(peer, out var seenAt) || Time.unscaledTime - seenAt.Time > 3f ||
                (!vehicleCrash && Vector3.Distance(seenAt.At, new Vector3(e.X, e.Y, e.Z)) > handler.GuestRange))) return;
            var second = Mathf.Floor(Time.unscaledTime);
            rates.TryGetValue(peer, out var rate);
            if (rate.Second != second) rate = (second, 0);
            if (!boltStop && ++rate.Count > PostsPerSecond) { rates[peer] = rate; return; }
            rates[peer] = rate;
            if (VehicleCrashGameplay.IsReport(e)) { DeliverCrashEffect(e); return; }
            Play(Append(e, peer));
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host || !records.TryGetValue(Entity, out var record)) return;
            try
            {
                var events = PresentationEvent.DecodeLog(record.Payload);
                seen = events.Length > 0 ? events[events.Length - 1].Sequence : 0; known = true;
            }
            catch (System.IO.InvalidDataException) { }
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Id != Entity) return;
            PresentationEvent[] events;
            try { events = PresentationEvent.DecodeLog(record.Payload); }
            catch (System.IO.InvalidDataException) { return; }
            if (!known) { seen = events.Length > 0 ? events[events.Length - 1].Sequence : 0; known = true; return; }
            uint local = LocalPeer;
            foreach (var e in events)
            {
                if (e.Sequence <= seen) continue;
                seen = e.Sequence;
                if (e.Actor != local || VehicleCrashGameplay.IsDelivery(e)) Play(e);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            handlers.Clear(); log.Clear(); pending.Clear(); boltLoops.Clear(); PresentationReplays.Reset();
            VehicleSwitchPresentation.Reset();
            RogerUnlockPresentation.Reset();
            VehicleDoorPresentation.Reset();
            TirePumpPresentation.Reset();
            RepairToolPresentation.Reset();
            VehicleCrashGameplay.Reset();
            session = null; channel = null;
        }
    }
}
