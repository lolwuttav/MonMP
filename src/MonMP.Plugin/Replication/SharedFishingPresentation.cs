using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // A durable, session-only current phase. The angler still runs native fishing locally;
    // the host arbitrates reports against the equipped rod's live item lease and pose.
    internal sealed class SharedFishingPresentation : IDisposable
    {
        internal const ushort Adapter = FishingPresentationState.Adapter;
        internal static readonly ulong Entity = StaticId.Hash("fishing-presentation/v1");
        internal static readonly ulong TrashEntity = StaticId.Hash("fishing-trash-cosmetics/v1");
        internal static SharedFishingPresentation? Active { get; private set; }
        private readonly bool host;
        private readonly SharedItems items;
        private readonly PlayerInteractable player;
        private readonly Dictionary<ulong, FishingPresentationState> current = new Dictionary<ulong, FishingPresentationState>();
        private readonly Dictionary<ulong, float> reported = new Dictionary<ulong, float>();
        private readonly Dictionary<uint, (PlayerPose Pose, float At)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, (float At, int Count)> rates = new Dictionary<uint, (float, int)>();
        private readonly FishingCatchTicketBook catchTickets = new FishingCatchTicketBook();
        private readonly Dictionary<(ulong Rod, uint Generation, uint Ticket), (FishingTrashCosmeticState State, float ExpiresAt)> trashCosmetics =
            new Dictionary<(ulong, uint, uint), (FishingTrashCosmeticState, float)>();
        private sealed class TrashVisual
        {
            public GameObject? Object;
            public float ExpiresAt;
            public bool Spent;
        }
        private readonly Dictionary<(ulong Rod, uint Generation, uint Ticket), TrashVisual> trashVisuals =
            new Dictionary<(ulong, uint, uint), TrashVisual>();
        private readonly HashSet<(ulong Rod, uint Generation, uint Ticket)> retiredTrashVisuals =
            new HashSet<(ulong, uint, uint)>();
        private readonly Queue<(ulong Rod, uint Generation, uint Ticket)> retiredTrashOrder =
            new Queue<(ulong, uint, uint)>();
        private readonly Dictionary<(ulong Rod, uint Generation, uint Ticket), (uint Owner, ushort Template, float ExpiresAt)> ownTrashTickets =
            new Dictionary<(ulong, uint, uint), (uint, ushort, float)>();
        private const float TrashOutputRetentionSeconds = 12f;
        private float nextTrashRefresh;
        private FishingTrashCosmeticBatch trashMotionBatch;
        private NetworkSession? session;
        private WorldChannel? channel;
        private float nextPrune;

#if MONMP_DEVELOPMENT
        internal int TestAccepted { get; private set; }


        internal int TestRejected { get; private set; }


        internal int TestCues { get; private set; }
#endif

        internal bool Ready => session?.IsRunning == true && channel?.Ready == true;

        internal SharedFishingPresentation(Gameplay gameplay, bool host, SharedItems items) { this.host = host; this.items = items; player = gameplay.Player; }
        internal static IEnumerable<WorldRecord> InitialRecords() => new[]
        {
            new WorldRecord(Entity, 1, Adapter, FishingPresentationState.EncodeCurrent(Array.Empty<FishingPresentationState>())),
            new WorldRecord(TrashEntity, 1, Adapter, FishingTrashCosmeticState.Encode(Array.Empty<FishingTrashCosmeticState>()))
        };
        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected; channel = world; Active = this;
            connected.PoseReceived += OnPose; connected.PeerLeft += OnLeft;
            world.SnapshotApplied += Snapshot; world.Changed += Changed; world.CommandRequested += Command;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer)
        {
            presence.Remove(peer); rates.Remove(peer);
            catchTickets.RemoveOwner(peer);
            foreach (var key in ownTrashTickets.Where(pair => pair.Value.Owner == peer).Select(pair => pair.Key).ToArray()) ownTrashTickets.Remove(key);
            bool changed = false;
            foreach (ulong rod in current.Values.Where(s => s.Owner == peer).Select(s => s.Rod).ToArray())
            { Remove(rod); changed = true; }
            if (host && changed && Ready) Publish();
            if (host && trashCosmetics.Keys.Any(key => trashCosmetics[key].State.Owner == peer))
            {
                foreach (var key in trashCosmetics.Where(pair => pair.Value.State.Owner == peer).Select(pair => pair.Key).ToArray())
                    trashCosmetics.Remove(key);
                PublishTrashCosmetics();
            }
        }
        internal void Report(Tool_FishingRod rod, FishingPresentationPhase phase, Vector3 endpoint,
            FishingCatchOutcome outcome = FishingCatchOutcome.None, ushort trashTemplate = FishingPresentationState.NoTrashTemplate)
        {
            if (!Ready || rod == null) return;
            ulong id = items.EntityOf(rod); var lease = items.StateOf(id)?.Lease;
            if (id == 0 || !lease.HasValue || lease.Value.Owner != session!.LocalPeerId) return;
            var state = new FishingPresentationState(id, session.LocalPeerId, lease.Value.Generation, phase, endpoint.x, endpoint.y, endpoint.z,
                outcome, 0, trashTemplate);
            if (!ItemPose.FiniteValue(endpoint.x, 100000) || !ItemPose.FiniteValue(endpoint.y, 100000) || !ItemPose.FiniteValue(endpoint.z, 100000)) return;
            if (host) Accept(session.LocalPeerId, state);
            else channel!.Request(Entity, state.Encode(), useLease: true);
        }
        [MonMP.Core.World.WorldCommandAdapters(SharedFishingPresentation.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || !Ready || (entity != Entity && entity != TrashEntity) || peer == 0 || !session!.Peers.ContainsKey(peer)) return;
            var second = Mathf.Floor(Time.unscaledTime); rates.TryGetValue(peer, out var rate);
            if (rate.At != second) rate = (second, 0);
            rates[peer] = (second, rate.Count + 1);
            if (rate.Count >= 8) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }
            try
            {
                if (entity == TrashEntity)
                {
                    AcceptTrashCosmetic(peer, FishingTrashCosmeticRequest.Decode(payload), trustedLocal: false);
                    return;
                }
                var submitted = FishingPresentationState.Decode(payload);
                if (submitted.CatchTicket != 0 || !ValidResultTemplate(submitted)) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }
                Accept(peer, submitted.WithOwner(peer));
            }
            catch (InvalidDataException) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 }
        }
        private static bool ValidResultTemplate(FishingPresentationState state) =>
            state.Outcome != FishingCatchOutcome.Trash || FishingTrashTemplateCatalog.IsValidIndex(state.TrashTemplate);
        private bool Owns(FishingPresentationState state, bool equipped)
        {
            if (session == null || (state.Owner != 0 && !session.Peers.ContainsKey(state.Owner))) return false;
            var item = items.StateOf(state.Rod);
            if (item == null || !item.Exists || item.Lease.Owner != state.Owner || item.Lease.Generation != state.Generation ||
                !(items.ItemOf(state.Rod) is Tool_FishingRod rod) || rod == null) return false;
            if (!equipped) return true;
            if (state.Owner == session.LocalPeerId) return rod.isEquiped && player.Get_ActiveTool == rod;
            // A catch can also lease a fish to the same player; select the held tool rather
            // than requiring their rod to be the only owned item.
            return items.HeldItemOf(state.Owner) == rod && presence.TryGetValue(state.Owner, out var p) &&
                Time.unscaledTime - p.At <= 3f &&
                (rod.transform.position - new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z)).sqrMagnitude <= 8f * 8f;
        }
        private bool Valid(FishingPresentationState state)
        {
            if (!Owns(state, state.Phase != FishingPresentationPhase.Unequip)) return false;
            var rod = items.ItemOf(state.Rod)!;
            var end = new Vector3(state.X, state.Y, state.Z);
            return (rod.transform.position - end).sqrMagnitude <= 60f * 60f;
        }
        private void Accept(uint peer, FishingPresentationState state)
        {
            if (state.Owner != peer || !Valid(state) || !ValidResultTemplate(state)) {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }

#if MONMP_DEVELOPMENT
            TestAccepted++;
#endif

            if (current.TryGetValue(state.Rod, out var prior) && prior.Owner == state.Owner && prior.Generation == state.Generation &&
                prior.Phase == FishingPresentationPhase.Caught && prior.Outcome == FishingCatchOutcome.Trash && prior.CatchTicket != 0 &&
                state.Phase != FishingPresentationPhase.Caught)
                ownTrashTickets[(prior.Rod, prior.Generation, prior.CatchTicket)] = (prior.Owner, prior.TrashTemplate, Time.unscaledTime + TrashOutputRetentionSeconds);
            if (state.Phase == FishingPresentationPhase.Unequip)
            {
                if (current.TryGetValue(state.Rod, out var old) && old.Owner == peer && old.Generation == state.Generation) Remove(state.Rod);
            }
            else
            {
                foreach (ulong other in current.Values.Where(s => s.Owner == peer && s.Rod != state.Rod).Select(s => s.Rod).ToArray()) Remove(other);
                if (!current.ContainsKey(state.Rod) && current.Count >= FishingPresentationState.Capacity) return;
                if (state.Phase == FishingPresentationPhase.Caught)
                {
                    FishingPresentationState? previous = current.TryGetValue(state.Rod, out var old) ? old : (FishingPresentationState?)null;
                    if (!catchTickets.TryAccept(state, previous, Time.unscaledTime, TrashOutputRetentionSeconds, out state))
                    {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return; }
                }
                current[state.Rod] = state; reported[state.Rod] = Time.unscaledTime;
                if (peer != session!.LocalPeerId) RemoteFishing.ApplyCurrent(state);
            }
            Publish();
        }
        private void Publish() => channel!.Publish(Entity, FishingPresentationState.EncodeCurrent(current.Values.OrderBy(s => s.Rod).ToArray()));
        private void Remove(ulong rod) { current.Remove(rod); reported.Remove(rod); RemoteFishing.RemoveCurrent(rod); }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            if (records.TryGetValue(Entity, out var record)) Changed(record);
            if (records.TryGetValue(TrashEntity, out var trash)) Changed(trash);
        }
        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != Adapter) return;
            if (record.Id == TrashEntity)
            {
                try { ApplyTrashCosmetics(FishingTrashCosmeticState.Decode(record.Payload)); }
                catch (InvalidDataException) { }
                return;
            }
            if (record.Id != Entity) return;
            FishingPresentationState[] states;
            try { states = FishingPresentationState.DecodeCurrent(record.Payload); }
            catch (InvalidDataException) { return; }
            var retained = new HashSet<ulong>(states.Select(s => s.Rod));
            foreach (var old in current.Values.Where(s => s.Owner == session?.LocalPeerId && s.Outcome == FishingCatchOutcome.Trash && s.CatchTicket != 0 &&
                (!retained.Contains(s.Rod) || !states.Any(next => next.Rod == s.Rod && next.CatchTicket == s.CatchTicket))))
                ownTrashTickets[(old.Rod, old.Generation, old.CatchTicket)] = (old.Owner, old.TrashTemplate, Time.unscaledTime + TrashOutputRetentionSeconds);
            foreach (ulong rod in current.Keys.Where(id => !retained.Contains(id)).ToArray()) Remove(rod);
            // Item records may bind after this record. Keep the validated wire state and
            // retry presentation as items arrive; native input never runs on apply.
            foreach (var state in states) current[state.Rod] = state;
            ApplyCurrent();
        }
        private void ApplyCurrent()
        {
            foreach (var state in current.Values)
            {
                if (state.Owner == session?.LocalPeerId) continue;
                var lease = items.StateOf(state.Rod)?.Lease;
                if (session?.Peers.ContainsKey(state.Owner) == true && lease.HasValue && lease.Value.Owner == state.Owner &&
                    lease.Value.Generation == state.Generation && items.ItemOf(state.Rod) is Tool_FishingRod rod && rod != null &&
                    (rod.transform.position - new Vector3(state.X, state.Y, state.Z)).sqrMagnitude <= 60f * 60f)
                    RemoteFishing.ApplyCurrent(state);
                else RemoteFishing.RemoveCurrent(state.Rod);
            }
        }
        internal bool AcceptCue(uint peer, PresentationEvent e) => e.Variant == (byte)FishingPresentationPhase.Cast &&
            current.TryGetValue(e.Target, out var state) && state.Owner == peer && state.Phase == FishingPresentationPhase.Cast &&
            CueGeneration(e, state.Generation) &&
            Valid(state) && (new Vector3(e.X, e.Y, e.Z) - new Vector3(state.X, state.Y, state.Z)).sqrMagnitude <= 4f;
        private static bool CueGeneration(PresentationEvent e, uint generation) => generation > 0 && generation <= 100000 && e.Value == generation;
        internal void PlayCue(PresentationEvent e)
        {
            if (!Ready || e.Variant != (byte)FishingPresentationPhase.Cast) return;
            // State and event records can arrive in either order in a coalesced delta.
            // Host acceptance already checked the current phase; an observer checks the
            // live owner/rod and bounds, without depending on record iteration order.
            var lease = items.StateOf(e.Target)?.Lease;
            if (!lease.HasValue || !CueGeneration(e, lease.Value.Generation) || !Valid(new FishingPresentationState(e.Target, e.Actor, lease.Value.Generation,
                FishingPresentationPhase.Cast, e.X, e.Y, e.Z))) return;
            RemoteFishing.Play(e);
#if MONMP_DEVELOPMENT
TestCues++;
#endif

        }
        internal void Update()
        {
            if (!Ready) return;
            PruneOutputTickets();
            UpdateTrashVisuals();
            if (!host) { ApplyCurrent(); return; }
            bool trashChanged = false;
            if (trashCosmetics.Count > 0) foreach (var key in trashCosmetics.Where(pair => pair.Value.ExpiresAt <= Time.unscaledTime).Select(pair => pair.Key).ToArray())
            { trashCosmetics.Remove(key); trashChanged = true; }
            if (trashChanged || trashMotionBatch.Due(Time.unscaledTime) ||
                (trashCosmetics.Count > 0 && Time.unscaledTime >= nextTrashRefresh)) PublishTrashCosmetics();
            if (Time.unscaledTime < nextPrune) return;
            nextPrune = Time.unscaledTime + 0.25f; bool changed = false;
            foreach (var state in current.Values.ToArray())
                if (!Valid(state) || !reported.TryGetValue(state.Rod, out float at) || Time.unscaledTime - at > 5f)
                { Remove(state.Rod); changed = true; }
            if (changed) Publish();
        }

#if MONMP_DEVELOPMENT
        internal FishingPresentationState? TestCurrent(ulong rod) => current.TryGetValue(rod, out var state) ? state : (FishingPresentationState?)null;


        internal void TestReplaySnapshot() { if (channel != null) Snapshot(channel.Records); }


        internal void TestReportAs(uint peer, FishingPresentationState state) => Accept(peer, state.WithOwner(peer));


        internal void TestRequest(FishingPresentationState state) => channel!.Request(Entity, state.Encode(), useLease: true);
#endif


        internal bool SubmitTrashCosmetic(FishingTrashCosmeticRequest request)
        {
            if (!Ready || !FishingTrashTemplateCatalog.IsValidIndex(request.Template) || items.SupportsFishingTrashTemplate(request.Template)) return false;
            if (host) return AcceptTrashCosmetic(session!.LocalPeerId, request, trustedLocal: true);
            channel!.Request(TrashEntity, request.Encode(), useLease: false);
            return true;
        }

        private bool AcceptTrashCosmetic(uint peer, FishingTrashCosmeticRequest request, bool trustedLocal)
        {
            var key = (request.Rod, request.Generation, request.Ticket);
            if (!host || !Ready || (trustedLocal ? peer != session!.LocalPeerId : peer == 0 || !session!.Peers.ContainsKey(peer)) ||
                !FishingTrashTemplateCatalog.IsValidIndex(request.Template) || items.SupportsFishingTrashTemplate(request.Template))
            {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return false; }
            if (trashCosmetics.TryGetValue(key, out var existing))
            {
                if (!FishingTrashCosmeticRefresh.TryApply(existing.State, existing.ExpiresAt, peer,
                    request, Time.unscaledTime, out var refreshed, out float expiry)) return false;
                trashCosmetics[key] = (refreshed, expiry);
                trashMotionBatch.Changed(existing.State.Pose, refreshed.Pose);
                return true;
            }
            // The rod authorizes creation once. Its later pickup, lease generation or
            // destruction does not control the already released native trash's motion.
            var rodState = items.StateOf(request.Rod);
            if (rodState == null || !rodState.Exists ||
                (rodState.Lease.Owner != peer && rodState.Lease.Owner != ItemLease.Unowned))
            {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return false; }
            if (!catchTickets.CanConsume(request.Rod, request.Generation, request.Ticket, peer, request.Template,
                    request.Pose.X, request.Pose.Y, request.Pose.Z, Time.unscaledTime, 8) ||
                (!trustedLocal && !NearActor(peer, new Vector3(request.Pose.X, request.Pose.Y, request.Pose.Z), 6)) ||
                !catchTickets.Consume(request.Rod, request.Generation, request.Ticket, peer, request.Template, Time.unscaledTime))
            {
#if MONMP_DEVELOPMENT
TestRejected++;
#endif
 return false; }
            var state = new FishingTrashCosmeticState(request.Rod, peer, request.Generation, request.Ticket, request.Template,
                request.Pose, request.RemainingMilliseconds);
            if (trashCosmetics.Count >= FishingTrashCosmeticState.Capacity)
            {
                var oldest = trashCosmetics.OrderBy(pair => pair.Value.ExpiresAt).First();
                trashCosmetics.Remove(oldest.Key);
            }
            trashCosmetics.Add(key, (state, Time.unscaledTime + request.RemainingMilliseconds * 0.001f));

#if MONMP_DEVELOPMENT
            TestAccepted++;
#endif

            PublishTrashCosmetics();
            return true;
        }

        private void PublishTrashCosmetics()
        {
            if (!host || !Ready) return;
            float now = Time.unscaledTime;
            var states = trashCosmetics.Values.Select(pair =>
            {
                ushort remaining = (ushort)Math.Max(1, Math.Min(6000, Math.Ceiling((pair.ExpiresAt - now) * 1000f)));
                var state = pair.State;
                return new FishingTrashCosmeticState(state.Rod, state.Owner, state.Generation, state.Ticket, state.Template, state.Pose, remaining);
            }).OrderBy(state => state.Owner).ThenBy(state => state.Rod).ThenBy(state => state.Ticket).ToArray();
            channel!.Publish(TrashEntity, FishingTrashCosmeticState.Encode(states));
            nextTrashRefresh = now + 1f;
            trashMotionBatch.Published(now);
            ApplyTrashCosmetics(states);
        }

        private void ApplyTrashCosmetics(IReadOnlyList<FishingTrashCosmeticState> states)
        {
            float now = Time.unscaledTime;
            var retained = new HashSet<(ulong Rod, uint Generation, uint Ticket)>();
            foreach (var state in states)
            {
                var key = (state.Rod, state.Generation, state.Ticket);
                if (retiredTrashVisuals.Contains(key)) continue;
                if (state.Owner == session?.LocalPeerId || !FishingTrashTemplateCatalog.IsValidIndex(state.Template)) continue;
                retained.Add(key);
                float proposedExpiry = now + state.RemainingMilliseconds * 0.001f;
                if (trashVisuals.TryGetValue(key, out var existing))
                {
                    if (existing.Object == null && !existing.Spent) existing.Spent = true;
                    if (!existing.Spent)
                    {
                        existing.ExpiresAt = Math.Min(existing.ExpiresAt, proposedExpiry);
                        existing.Object!.GetComponent<FishingTrashCosmeticVisual>()?.MoveTo(state.Pose);
                    }
                    continue;
                }
                var instance = SpawnTrashCosmetic(state);
                trashVisuals.Add(key, new TrashVisual { Object = instance, ExpiresAt = proposedExpiry, Spent = instance == null });
            }
            foreach (var key in trashVisuals.Keys.Where(key => !retained.Contains(key)).ToArray())
            {
                var visual = trashVisuals[key];
                if (visual.Object != null) Object.Destroy(visual.Object);
                trashVisuals.Remove(key);
                RetireTrashVisual(key);
            }
        }

        private void RetireTrashVisual((ulong Rod, uint Generation, uint Ticket) key)
        {
            if (!retiredTrashVisuals.Add(key)) return;
            retiredTrashOrder.Enqueue(key);
            while (retiredTrashOrder.Count > 128) retiredTrashVisuals.Remove(retiredTrashOrder.Dequeue());
        }

        private static GameObject? SpawnTrashCosmetic(FishingTrashCosmeticState state)
        {
            var template = FishingTrashTemplateCatalog.TemplateAt(state.Template);
            if (template == null) return null;
            GameObject? staging = null; GameObject? visual = null;
            try
            {
                staging = new GameObject("MonMP fishing cosmetic staging"); staging.SetActive(false);
                visual = Object.Instantiate(template, staging.transform);
                visual.SetActive(false);
                visual.transform.position = new Vector3(state.Pose.X, state.Pose.Y, state.Pose.Z);
                visual.transform.rotation = new Quaternion(state.Pose.Qx, state.Pose.Qy, state.Pose.Qz, state.Pose.Qw);
                foreach (var behaviour in visual.GetComponentsInChildren<MonoBehaviour>(true))
                    if (!(behaviour is Fishing_Trash)) behaviour.enabled = false;
                foreach (var collider in visual.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                foreach (var body in visual.GetComponentsInChildren<Rigidbody>(true))
                { body.isKinematic = true; body.detectCollisions = false; }
                var trash = visual.GetComponent<Fishing_Trash>() ?? visual.AddComponent<Fishing_Trash>();
                if (visual.GetComponent<FishingTrashCosmeticVisual>() == null) visual.AddComponent<FishingTrashCosmeticVisual>();
                trash.DropFromFishingRod();
                // Native DropFromFishingRod adds its Rigidbody after the staging checks above.
                // Keep this replica purely visual. Accepted native drop poses move its
                // renderer; a second simulation must never push items or create a pickup.
                foreach (var body in visual.GetComponentsInChildren<Rigidbody>(true))
                { body.isKinematic = true; body.detectCollisions = false; body.useGravity = false; }
                trash.enabled = false;
                Object.Destroy(staging);
                visual.SetActive(true);
                float remaining = Math.Min(6f, state.RemainingMilliseconds * 0.001f);
                Object.Destroy(visual, remaining);
                return visual;
            }
            catch
            {
                if (visual != null) Object.Destroy(visual);
                if (staging != null) Object.Destroy(staging);
                return null;
            }
        }

        private void UpdateTrashVisuals()
        {
            foreach (var pair in trashVisuals.Values)
            {
                if (pair.Spent) continue;
                if (pair.Object == null || Time.unscaledTime >= pair.ExpiresAt)
                {
                    if (pair.Object != null) Object.Destroy(pair.Object);
                    pair.Object = null; pair.Spent = true;
                }
            }
        }

        private void PruneOutputTickets()
        {
            float now = Time.unscaledTime;
            catchTickets.Prune(now);
            foreach (var key in ownTrashTickets.Where(p => p.Value.ExpiresAt <= now).Select(p => p.Key).ToArray()) ownTrashTickets.Remove(key);
        }
        internal bool TryLocalTrashTicket(ulong rod, uint generation, ushort template, out uint ticket)
        {
            ticket = 0;
            if (session == null) return false;
            if (current.TryGetValue(rod, out var state) && state.Owner == session.LocalPeerId && state.Generation == generation &&
                state.Phase == FishingPresentationPhase.Caught && state.Outcome == FishingCatchOutcome.Trash &&
                state.TrashTemplate == template && state.CatchTicket != 0)
            { ticket = state.CatchTicket; return true; }
            var match = ownTrashTickets.Where(p => p.Key.Rod == rod && p.Key.Generation == generation && p.Value.Owner == session.LocalPeerId &&
                p.Value.Template == template && p.Value.ExpiresAt > Time.unscaledTime)
                .OrderByDescending(p => p.Key.Ticket).FirstOrDefault();
            if (match.Key.Ticket == 0) return false;
            ticket = match.Key.Ticket; return true;
        }
        internal bool CanAdoptTrashOutput(uint peer, FishingTrashSpawnRequest request, Type prefabType, string prefabPath, ItemPose pose)
        {
            if (!host || !Ready || peer == 0 || !session!.Peers.ContainsKey(peer) || request.Spawn.Hold ||
                request.Spawn.Nonce == 0 || !FishingTrashTemplateCatalog.Matches(request.Template, prefabType, prefabPath)) return false;
            var rodState = items.StateOf(request.Rod);
            if (rodState == null || !rodState.Exists ||
                (rodState.Lease.Owner != peer && rodState.Lease.Owner != ItemLease.Unowned)) return false;
            Vector3 at = new Vector3(pose.X, pose.Y, pose.Z);
            if (!catchTickets.CanConsume(request.Rod, request.RodGeneration, request.CatchTicket, peer, request.Template,
                at.x, at.y, at.z, Time.unscaledTime, 8)) return false;
            return NearActor(peer, at, 6);
        }
        private bool NearActor(uint peer, Vector3 target, float range) => presence.TryGetValue(peer, out var p) &&
            Time.unscaledTime - p.At <= 2f &&
            (target - new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z)).sqrMagnitude <= range * range;
        internal bool ConsumeTrashOutput(uint peer, FishingTrashSpawnRequest request)
        {
            return host && catchTickets.Consume(request.Rod, request.RodGeneration, request.CatchTicket, peer, request.Template, Time.unscaledTime);
        }
        public void Dispose()
        {
            if (Active == this) Active = null;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            foreach (ulong rod in current.Keys.ToArray()) Remove(rod);
            foreach (var visual in trashVisuals.Values) if (visual.Object != null) Object.Destroy(visual.Object);
            trashVisuals.Clear(); retiredTrashVisuals.Clear(); retiredTrashOrder.Clear();
            trashCosmetics.Clear(); presence.Clear(); rates.Clear(); ownTrashTickets.Clear(); session = null; channel = null;
        }
    }
}
