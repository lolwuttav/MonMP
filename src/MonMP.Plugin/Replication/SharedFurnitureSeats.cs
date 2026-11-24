using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Host-serialized occupancy for ordinary native chairs and decoration seats. Native MouseDown
    // remains the entry/exit mechanism; the chair itself never enters the item holding/visual path.
    internal sealed class SharedFurnitureSeats : IDisposable
    {
        private sealed class Seat
        {
            public ulong Id;
            public string Identity = "";
            public SeatKey Key;
            public Transform Anchor = null!;
            public Furniture_Chair? Chair;
            public Furniture_Decoration_Seat? DecorationSeat;
            public Furniture_Decoration? ParentDecoration;
            public InteractableItems? Item;
            public ulong ItemEntity;
            public FurnitureSeatState State = new FurnitureSeatState(SeatOccupancy.Unoccupied, 1);
            public Vector3 ClaimPosition;
            public float OwnerSince;
            public int EntryFrame = -1;
            public Guid ClaimOperation;
            public bool EntryProbePending;
        }

        private sealed class EntryAttempt
        {
            public Seat Seat = null!;
            public FurnitureSeatCommand Command;
            public float Started;
            public float RetainUntil;
            public bool Cancelled;
            public bool NativeInvoked;
            public uint GrantedGeneration;
        }

        private const float ScanPeriod = 0.75f;
        private const float ClaimTimeout = 4f;
        private const float AttemptRetention = 20f;
        private const float PresenceTimeout = 2f;
        private const float MaxEntryDistance = 4f;
        private const float MovedSeatDistance = 0.45f;
        private static readonly System.Reflection.FieldInfo? ChairSeatField = AccessTools.Field(typeof(Furniture_Chair), "SeatLocation");
        private static readonly System.Reflection.FieldInfo? DecorationSeatField = AccessTools.Field(typeof(Furniture_Decoration_Seat), "SeatLocation");
        private static readonly System.Reflection.FieldInfo? ParentDecorationField = AccessTools.Field(typeof(Furniture_Decoration_Seat), "ParentDecoration");

        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly SharedItems items;
        private readonly SharedExtraSeats extraSeats;
        private readonly NativeEntityRegistry entities;
        private readonly FurnitureSeatAuthority authority = new FurnitureSeatAuthority();
        private readonly Dictionary<ulong, Seat> seats = new Dictionary<ulong, Seat>();
        private readonly HashSet<Seat> occupiedSeats = new HashSet<Seat>();
        private Seat[] occupiedSnapshot = Array.Empty<Seat>();
        private bool occupiedSnapshotDirty;
        private readonly Dictionary<Furniture_Chair, Seat> byChair = new Dictionary<Furniture_Chair, Seat>();
        private readonly Dictionary<Furniture_Decoration_Seat, Seat> byDecorationSeat = new Dictionary<Furniture_Decoration_Seat, Seat>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<ulong, string> identities = new Dictionary<ulong, string>();
        private readonly HashSet<ulong> ambiguous = new HashSet<ulong>();
        private readonly FurnitureDiscoveryCache discovery = new FurnitureDiscoveryCache();
        private readonly FurnitureSeatSources seatSources = new FurnitureSeatSources();
        private readonly FurnitureSceneCatalog<Furniture_Chair> chairCatalog = new FurnitureSceneCatalog<Furniture_Chair>();
        private readonly FurnitureSceneCatalog<Furniture_Decoration_Seat> decorationCatalog = new FurnitureSceneCatalog<Furniture_Decoration_Seat>();
        private readonly List<(string Path, Furniture_Chair Chair, Transform Anchor)> chairCandidates = new List<(string, Furniture_Chair, Transform)>();
        private readonly List<(string Path, Furniture_Decoration_Seat Seat, Transform Anchor, Furniture_Decoration? Parent)> decorationCandidates = new List<(string, Furniture_Decoration_Seat, Transform, Furniture_Decoration?)>();

#if MONMP_DEVELOPMENT
        private readonly HashSet<string> writtenDiagnostics = new HashSet<string>(StringComparer.Ordinal);
#endif

        private readonly Dictionary<Guid, EntryAttempt> attempts = new Dictionary<Guid, EntryAttempt>();

#if MONMP_DEVELOPMENT
        private readonly List<string> diagnosticsRows = new List<string> { "id\tidentity\tstatus" };
#endif


#if MONMP_DEVELOPMENT
        private bool diagnosticsDirty;
#endif

        private NetworkSession? session;
        private WorldChannel? channel;
        private Furniture_Chair? chairPermit;
        private Furniture_Decoration_Seat? decorationPermit;
        private EntryAttempt? localAttempt;
        private float nextScan;
        private bool blocked;
        private bool disposed;
        private readonly string diagnostics;

        internal bool Ready => !disposed && channel?.Ready == true;
        internal bool Active => !disposed && session != null;
        internal SharedSeatAdmission? SeatAdmission { get; set; }
        internal string LastRefusal { get; private set; } = "";
        internal uint[] Owners => seats.Values.Select(seat => seat.State.Owner)
            .Where(owner => owner != SeatOccupancy.Unoccupied).Distinct().ToArray();
        internal bool IsOwner(uint peer) => seats.Values.Any(seat => seat.State.Owner == peer);
        internal bool LocalFlushComplete => session == null ||
            (!Owners.Contains(session.LocalPeerId) && (localAttempt == null || localAttempt.Cancelled));

        internal SharedFurnitureSeats(Gameplay gameplay, bool host, string diagnostics, SharedItems items,
            SharedExtraSeats extraSeats, NativeEntityRegistry entities)
        {
            this.gameplay = gameplay;
            this.host = host;
            this.diagnostics = diagnostics;
            this.items = items;
            this.extraSeats = extraSeats;
            this.entities = entities;
            try
            {
                Scan(forceSources: true);
                AdoptHostLoadedSeat();
            }
            catch { seatSources.Dispose(); throw; }
        }

        internal IEnumerable<WorldRecord> InitialRecords() => seats.Values
            .Where(seat => !ambiguous.Contains(seat.Id))
            .OrderBy(seat => seat.Id)
            .Select(seat => new WorldRecord(seat.Id, 1, FurnitureSeatState.Adapter, seat.State.Encode()));

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected;
            channel = world;
            connected.PoseReceived += OnPose;
            connected.PeerLeft += OnLeft;
            world.SnapshotApplied += Snapshot;
            world.Changed += Changed;
            world.CommandRequested += Command;
            world.CommandRejected += CommandRejected;
            world.ReplyReceived += Reply;
            nextScan = 0;
            AdoptHostLoadedSeat();
        }

        internal bool Handles(Furniture_Chair chair) => chair != null && (byChair.ContainsKey(chair) || Ready);

        internal bool PeerSeatedNear(uint peer, Vector3 position, float range) => host && seats.Values.Any(seat =>
            seat.State.Owner == peer && seat.Anchor != null && (seat.Anchor.position - position).sqrMagnitude <= range * range);

#if MONMP_DEVELOPMENT
        internal (ulong Id, Transform Anchor, bool NpcOccupied)? TestNearest(Vector3 at, float range)
        {
            var seat = seats.Values.Where(s => s.Anchor != null && SeatUsable(s) && (s.Anchor.position - at).sqrMagnitude < range * range)
                .OrderBy(s => (s.Anchor.position - at).sqrMagnitude).FirstOrDefault();
            return seat == null ? null : (seat.Id, seat.Anchor, NpcNear(seat));
        }
#endif

        internal bool Click(Furniture_Chair chair)
        {
            if (chairPermit == chair) { chairPermit = null; return true; }
            if (!Active) return true;
            if (!Ready) { Refuse("World state is still syncing."); return false; }
            ScanIfNeeded(true);
            if (!byChair.TryGetValue(chair, out var seat)) { Refuse("This seat is not ready to share."); return false; }
            return Click(seat);
        }

        internal bool Click(Furniture_Decoration_Seat chair)
        {
            if (decorationPermit == chair) { decorationPermit = null; return true; }
            if (!Active) return true;
            if (!Ready) { Refuse("World state is still syncing."); return false; }
            ScanIfNeeded(true);
            if (!byDecorationSeat.TryGetValue(chair, out var seat)) { Refuse("This seat is not ready to share."); return false; }
            return Click(seat);
        }

        // Controller exit through the local VR bridge: the native click of the shared seat the
        // local player owns and sits in. Click(seat) then returns true for that seat, preserving
        // native click-to-stand and the native bar-drink refusal.
        private Seat? LocalExitSeat()
        {
            if (!Ready || session == null) return null;
            foreach (var seat in seats.Values)
                if (seat.State.Owner == session.LocalPeerId)
                    return IsLocalWalkerAt(seat) && (seat.Chair != null || seat.DecorationSeat != null) ? seat : null;
            return null;
        }
        internal bool HasLocalExitSeat => LocalExitSeat() != null;
        internal void ClickLocalExitSeat()
        {
            var seat = LocalExitSeat();
            if (seat == null) return;
            if (seat.Chair != null) seat.Chair.MouseDown();
            else seat.DecorationSeat!.MouseDown();
        }

        private bool Click(Seat seat)
        {
            if (gameplay.Get_PlayerState == PlayerState.InFurnitureChair)
            {
                var mine = seats.Values.FirstOrDefault(candidate => candidate.State.Owner == session!.LocalPeerId);
                if (mine != null && IsLocalWalkerAt(mine))
                {
                    if (seat == mine) return true; // preserve native click-to-stand and native bar-drink refusal
                    Refuse("Stand up before using another seat.");
                    return false;
                }
                Refuse("Stand up before using another seat.");
                return false;
            }
            if (localAttempt?.Seat == seat) { Cancel(localAttempt); return false; }
            if (seat.State.Owner == session!.LocalPeerId)
            {
                RequestRelease(seat);
                return false;
            }
            if (blocked) { Refuse("Seat transfers are paused while saving."); return false; }
            if (localAttempt != null) { Refuse("Waiting for the previous seat request."); return false; }
            if (gameplay.Get_PlayerState != PlayerState.Walking || gameplay.Player == null || gameplay.PlayerWalking == null)
            { Refuse("Walk before sitting down."); return false; }
            if (gameplay.Player.IsHoldingAnObject) { MonMPPlugin.Instance?.ReportItemUseUnavailable("Put down what you're holding first."); return false; }
            if (seat.State.Occupied || NpcNear(seat)) { Refuse("Someone is already sitting there."); return false; }
            if (!SeatUsable(seat)) { Refuse("That seat is not available."); return false; }
            if (ItemUnavailable(seat, session.LocalPeerId)) { Refuse("Put down or release the chair first."); return false; }
            if (Vector3.Distance(gameplay.PlayerWalking.transform.position, seat.Anchor.position) > MaxEntryDistance)
            { Refuse("Move closer to the seat."); return false; }
            if (SeatAdmission?.TryBeginLocal(this, seat.Id, seat.State.Generation, session.LocalPeerId) == false)
            { Refuse("Waiting for the previous seat request."); return false; }
            var attempt = new EntryAttempt
            {
                Seat = seat,
                Command = new FurnitureSeatCommand(FurnitureSeatAction.Claim, seat.State.Generation, Guid.NewGuid()),
                Started = Time.unscaledTime,
                RetainUntil = Time.unscaledTime + AttemptRetention
            };
            localAttempt = attempt;
            attempts.Add(attempt.Command.Operation, attempt);
            Request(seat, attempt.Command);
            return false;
        }

        private void Request(Seat seat, FurnitureSeatCommand command)
        {
            try { channel!.Request(seat.Id, command.Encode(), useLease: true); }
            catch (InvalidOperationException) { LastRefusal = "Seat request could not be sent."; }
        }

        private void Cancel(EntryAttempt attempt)
        {
            if (attempt.Cancelled) return;
            attempt.Cancelled = true;
            SeatAdmission?.CancelLocal(this);
            attempt.RetainUntil = Time.unscaledTime + AttemptRetention;
            Request(attempt.Seat, new FurnitureSeatCommand(FurnitureSeatAction.Cancel,
                attempt.Command.ExpectedGeneration, attempt.Command.Operation));
            if (localAttempt == attempt) localAttempt = null;
        }

        private void RequestRelease(Seat seat)
        {
            if (session == null || seat.State.Owner != session.LocalPeerId) return;
            var operation = Guid.NewGuid();
            Request(seat, new FurnitureSeatCommand(FurnitureSeatAction.Release, seat.State.Generation, operation));
        }

        internal void FlushLocalOwnership()
        {
            SeatAdmission?.CancelLocal(this);
            if (session == null) return;
            if (localAttempt != null) Cancel(localAttempt);
            var seat = seats.Values.FirstOrDefault(value => value.State.Owner == session.LocalPeerId);
            if (seat == null) return;
            if (IsLocalWalkerAt(seat)) NativeExit(seat);
            if (!IsLocalWalkerAt(seat)) RequestRelease(seat);
        }

        internal void SetNewActionsBlocked(bool value)
        {
            blocked = value;
            if (value && localAttempt != null) Cancel(localAttempt);
        }

        internal void RevokeOwnership(uint peer)
        {
            if (!host || !authority.TryReleasePeer(peer, out var key, out var released)) return;
            var seat = seats.Values.FirstOrDefault(value => value.Key == key);
            if (seat == null) return;
            Apply(seat, new FurnitureSeatState(released.Peer, released.Generation));
            Publish(seat);
        }

        internal void Update()
        {
            if (!Active) return;
            ScanIfNeeded(false);
            if (host) AdoptHostLoadedSeat();
            if (session == null || !Ready) return;

            float now = Time.unscaledTime;
            foreach (var attempt in attempts.Count == 0 ? Array.Empty<EntryAttempt>() : attempts.Values.ToArray())
            {
                if (!attempt.Cancelled && now - attempt.Started > ClaimTimeout) Cancel(attempt);
                if (attempt.Cancelled && now >= attempt.RetainUntil) attempts.Remove(attempt.Command.Operation);
            }

            // State callbacks may release seats synchronously. Keep a detached snapshot, rebuilding
            // only when occupancy changes, rather than copying every discovered chair each frame.
            if (occupiedSnapshotDirty)
            {
                occupiedSnapshot = occupiedSeats.ToArray();
                occupiedSnapshotDirty = false;
            }
            foreach (var seat in occupiedSnapshot)
            {
                if (host && seat.State.Occupied && seat.Anchor != null && NpcNear(seat))
                { ReleaseHost(seat, FurnitureSeatRefusal.Npc); continue; }
                if (seat.State.Owner == session.LocalPeerId)
                {
                    bool here = IsLocalWalkerAt(seat);
                    if (here && seat.Anchor != null && Vector3.Distance(seat.ClaimPosition, seat.Anchor.position) > MovedSeatDistance)
                    {
                        NativeExit(seat);
                        if (!IsLocalWalkerAt(seat)) RequestRelease(seat);
                    }
                    else if (!here && seat.EntryFrame >= 0 && Time.frameCount > seat.EntryFrame + 1)
                    {
                        var attempt = attempts.Values.FirstOrDefault(value => value.Seat == seat && value.NativeInvoked && !value.Cancelled);
                        if (attempt != null) Cancel(attempt);
                        else RequestRelease(seat);
                        seat.EntryFrame = -1;
                        seat.EntryProbePending = false;
                    }
                }
                else if (host && seat.State.Occupied)
                {
                    if (!seat.Anchor || seat.Chair == null && seat.DecorationSeat == null || ItemUnavailable(seat, seat.State.Owner))
                    { ReleaseHost(seat, FurnitureSeatRefusal.MissingSeat); continue; }
                    if (Vector3.Distance(seat.ClaimPosition, seat.Anchor!.position) > MovedSeatDistance)
                    { ReleaseHost(seat, FurnitureSeatRefusal.Moved); continue; }
                    if (presence.TryGetValue(seat.State.Owner, out var current))
                    {
                        if (now - seat.OwnerSince > 1f && current.Pose.State != (byte)PlayerState.InFurnitureChair ||
                            now - current.Time > PresenceTimeout ||
                            Vector3.Distance(new Vector3(current.Pose.X, current.Pose.Y, current.Pose.Z), seat.Anchor.position) > 3.5f)
                            ReleaseHost(seat, FurnitureSeatRefusal.Moved);
                    }
                    else if (now - seat.OwnerSince > PresenceTimeout) ReleaseHost(seat, FurnitureSeatRefusal.Moved);
                }
            }
        }

        private void ScanIfNeeded(bool force)
        {
            if (!force && (Time.unscaledTime < nextScan || !WorldScans.TryClaim())) return;
            nextScan = Time.unscaledTime + ScanPeriod;
            Scan(force);
        }

        private void Scan(bool forceSources = false)
        {
            seatSources.Refresh(Time.unscaledTime, forceSources);
            discovery.BeginPass();
            try
            {
#if MONMP_PERFORMANCE_CAPTURE
                using (Runtime.CaptureDetail.Measure("FurnitureSeats.Identity"))
#endif
                { ScanSeats(); }
            }
            finally { discovery.EndPass(); chairCandidates.Clear(); decorationCandidates.Clear();
#if MONMP_DEVELOPMENT
FlushDiagnostics();
#endif
 }
        }

        private void ScanSeats()
        {
            chairCatalog.BeginPass(); decorationCatalog.BeginPass();
            foreach (var chair in seatSources.Chairs)
            {
                if (chair == null || !chair.gameObject.scene.IsValid() || extraSeats.Handles(chair)) continue;
                if (ChairSeatField?.GetValue(chair) is not Transform anchor) continue;
                var item = chair.GetComponentInParent<InteractableItems>(true);
                if (item == null)
                {
                    string path = discovery.SceneIdentity(chair, "chair");
                    chairCandidates.Add((path, chair, anchor)); chairCatalog.Observe(chair, path);
                }
                else EnsureItemSeat(chair, null, anchor, item);
            }
            foreach (var decorationSeat in seatSources.DecorationSeats)
            {
                if (decorationSeat == null || !decorationSeat.gameObject.scene.IsValid()) continue;
                if (DecorationSeatField?.GetValue(decorationSeat) is not Transform anchor) continue;
                var parent = ParentDecorationField?.GetValue(decorationSeat) as Furniture_Decoration;
                var item = decorationSeat.GetComponentInParent<InteractableItems>(true);
                if (item != null) EnsureItemSeat(null, decorationSeat, anchor, item, parent);
                else
                {
                    string path = discovery.SceneIdentity(decorationSeat, "decoration-seat");
                    decorationCandidates.Add((path, decorationSeat, anchor, parent)); decorationCatalog.Observe(decorationSeat, path);
                }
            }

            // Identical scene paths can occur when a scene is additively loaded twice. Exclude all
            // ambiguous instances together; never let enumeration order decide which one is shared.
            chairCatalog.EndPass(); decorationCatalog.EndPass();
            foreach (var candidate in chairCandidates)
                if (chairCatalog.IsUnique(candidate.Path)) AddSceneSeat(candidate.Path, candidate.Chair, candidate.Anchor);
            foreach (var candidate in decorationCandidates)
                if (decorationCatalog.IsUnique(candidate.Path))
                    AddSceneSeat(candidate.Path, candidate.Seat, candidate.Anchor, candidate.Parent);

            foreach (string path in chairCatalog.AmbiguousPaths)
            {
                ulong id = FurnitureSeatIdentity.Scene(path);
                if (!ambiguous.Add(id)) continue;
                byChair.Where(pair => pair.Value.Id == id).Select(pair => pair.Key).ToArray().ToList().ForEach(chair => byChair.Remove(chair));

#if MONMP_DEVELOPMENT
                Log(id, path, "ambiguous-scene-path");
#endif

            }
            foreach (string path in decorationCatalog.AmbiguousPaths)
            {
                ulong id = FurnitureSeatIdentity.Scene(path);
                if (!ambiguous.Add(id)) continue;
                byDecorationSeat.Where(pair => pair.Value.Id == id).Select(pair => pair.Key).ToArray()
                    .ToList().ForEach(seat => byDecorationSeat.Remove(seat));

#if MONMP_DEVELOPMENT
                Log(id, path, "ambiguous-decoration-path");
#endif

            }
        }

        private void EnsureItemSeat(Furniture_Chair? chair, Furniture_Decoration_Seat? decorationSeat,
            Transform anchor, InteractableItems item, Furniture_Decoration? parent = null)
        {
            var source = chair != null ? chair.transform : decorationSeat!.transform;
            ulong entityId = items.EntityOf(item);
            if (entityId == 0 && host) entityId = items.EnsureHostBinding(item);
            var identity = entityId == 0 ? null : entities.Resolve(entityId);
            if (identity == null || identity.AdapterType != ItemState.Adapter || identity.EntityId != entityId)
            {

#if MONMP_DEVELOPMENT
                Log(0, discovery.CanonicalPath(source), "waiting-item-identity");
#endif

                return;
            }
            var component = (Component?)chair ?? decorationSeat!;
            string identityPath = discovery.ItemIdentity(component, item.transform, entityId,
                chair != null ? "chair" : "decoration-seat", out string relativeRole);
            if (identityPath.Length == 0) return;
            Seat? known = null;
            if (chair != null) byChair.TryGetValue(chair, out known);
            else byDecorationSeat.TryGetValue(decorationSeat!, out known);
            // Reuse the registered identity and hash when the item and complete relative path
            // are unchanged; dynamic item enrollment and native references still refresh above.
            ulong id = known != null && known.Identity == identityPath ? known.Id : FurnitureSeatIdentity.Item(entityId, relativeRole);
            AddSeat(id, identityPath, anchor, chair, decorationSeat, parent, item, entityId);
        }

        private void AddSceneSeat(string identity, Furniture_Chair chair, Transform? anchor) =>
            AddSeat(byChair.TryGetValue(chair, out var known) && known.Identity == identity ? known.Id : FurnitureSeatIdentity.Scene(identity),
                identity, anchor, chair, null, null, null, 0);

        private void AddSceneSeat(string identity, Furniture_Decoration_Seat decoration, Transform? anchor,
            Furniture_Decoration? parent) => AddSeat(byDecorationSeat.TryGetValue(decoration, out var known) && known.Identity == identity ? known.Id : FurnitureSeatIdentity.Scene(identity), identity, anchor,
                null, decoration, parent, null, 0);

        private void AddSeat(ulong id, string identity, Transform? anchor, Furniture_Chair? chair,
            Furniture_Decoration_Seat? decoration, Furniture_Decoration? parent, InteractableItems? item, ulong itemEntity)
        {
            if (id == 0 || anchor == null || ambiguous.Contains(id)) return;
            if (seats.TryGetValue(id, out var existing))
            {
                if (!string.Equals(existing.Identity, identity, StringComparison.Ordinal) ||
                    (existing.Chair != null && chair != null && existing.Chair != chair) ||
                    (existing.DecorationSeat != null && decoration != null && existing.DecorationSeat != decoration))
                {
                    ambiguous.Add(id);
                    ReleaseHost(existing, FurnitureSeatRefusal.MissingSeat);
                    if (existing.Chair != null) byChair.Remove(existing.Chair);
                    if (existing.DecorationSeat != null) byDecorationSeat.Remove(existing.DecorationSeat);

#if MONMP_DEVELOPMENT
                    Log(id, identity, "identity-collision");
#endif

                    return;
                }
                existing.Anchor = anchor;
                existing.Chair = chair ?? existing.Chair;
                existing.DecorationSeat = decoration ?? existing.DecorationSeat;
                existing.ParentDecoration = parent ?? existing.ParentDecoration;
                existing.Item = item ?? existing.Item;
                existing.ItemEntity = itemEntity != 0 ? itemEntity : existing.ItemEntity;
                if (existing.Chair != null) byChair[existing.Chair] = existing;
                if (existing.DecorationSeat != null) byDecorationSeat[existing.DecorationSeat] = existing;
                RegisterHostRecord(existing);
                return;
            }
            if (identities.TryGetValue(id, out string prior) && !string.Equals(prior, identity, StringComparison.Ordinal))
            { ambiguous.Add(id);
#if MONMP_DEVELOPMENT
Log(id, identity, "identity-hash-collision");
#endif
 return; }
            identities[id] = identity;
            var seat = new Seat
            {
                Id = id,
                Identity = identity,
                Key = new SeatKey(id, "ordinary"),
                Anchor = anchor,
                Chair = chair,
                DecorationSeat = decoration,
                ParentDecoration = parent,
                Item = item,
                ItemEntity = itemEntity
            };
            if (host)
            {
                var occupancy = authority.Register(seat.Key);
                seat.State = new FurnitureSeatState(occupancy.Peer, occupancy.Generation);
            }
            else if (channel?.Records.TryGetValue(id, out var record) == true && record.Adapter == FurnitureSeatState.Adapter)
                seat.State = FurnitureSeatState.Decode(record.Payload);
            seats.Add(id, seat);
            if (seat.State.Occupied) { occupiedSeats.Add(seat); occupiedSnapshotDirty = true; }
            if (chair != null) byChair[chair] = seat;
            if (decoration != null) byDecorationSeat[decoration] = seat;
            RegisterHostRecord(seat);

#if MONMP_DEVELOPMENT
            Log(id, identity, host ? "registered" : "local-binding");
#endif

        }

        private void RegisterHostRecord(Seat seat)
        {
            if (!host || channel == null || channel.Records.ContainsKey(seat.Id)) return;
            try { channel.Register(new WorldRecord(seat.Id, 1, FurnitureSeatState.Adapter, seat.State.Encode())); }
            catch (InvalidOperationException) {
#if MONMP_DEVELOPMENT
Log(seat.Id, seat.Identity, "dynamic-registration-failed");
#endif
 }
        }

        private static bool SeatUsable(Seat seat)
        {
            if (seat.DecorationSeat != null)
            {
                try { return seat.DecorationSeat.CanUse(); }
                catch (NullReferenceException) { return false; }
            }
            return seat.Chair != null;
        }

        private bool ItemUnavailable(Seat seat, uint peer)
        {
            if (seat.ItemEntity == 0) return false;
            if (items.TransfersBlocked) return true;
            var state = items.StateOf(seat.ItemEntity);
            return state == null || !state.Exists || state.Lease.Owner != ItemLease.Unowned;
        }

        private bool NpcNear(Seat seat)
        {
            Vector3 position = seat.Anchor.position;
            foreach (var actor in SharedExtraSeats.NpcActors())
                if (actor != null && actor.isActiveAndEnabled && actor.gameObject.activeInHierarchy &&
                    Vector3.ProjectOnPlane(actor.transform.position - position, Vector3.up).sqrMagnitude < 0.45f * 0.45f &&
                    Mathf.Abs(actor.transform.position.y - position.y) < 1.6f) return true;
            return false;
        }

        private void OnPose(uint peer, PlayerPose pose)
        {
            float now = Time.unscaledTime;
            presence[peer] = (pose, now);
            if (!host || pose.State != (byte)PlayerState.InFurnitureChair || pose.Rest != PlayerRest.None ||
                extraSeats.IsOwner(peer)) return;

            if (authority.TryGetSeat(peer, out var currentKey))
            {
                var currentSeat = seats.Values.FirstOrDefault(candidate => candidate.Key == currentKey);
                if (currentSeat != null && currentSeat.Anchor &&
                    Vector3.Distance(new Vector3(pose.X, pose.Y, pose.Z), currentSeat.Anchor.position) <= 3.5f)
                {
                    currentSeat.OwnerSince = now;
                }
                return;
            }

            // A delayed seated pose must not recreate a claim after save cleanup revoked it.
            if (blocked || SeatAdmission?.IsSeated(peer) == true) return;
            // A peer may have loaded an already-seated native profile before joining. Adopt that
            // pose into the same host claim table so the first click from another peer cannot win.
            var position = new Vector3(pose.X, pose.Y, pose.Z);
            Seat? nearest = null;
            float nearestDistance = float.MaxValue;
            foreach (var candidate in seats.Values)
            {
                if (candidate.State.Occupied || ambiguous.Contains(candidate.Id) || !candidate.Anchor || NpcNear(candidate)) continue;
                float distance = Vector3.Distance(position, candidate.Anchor.position);
                if (distance < nearestDistance) { nearest = candidate; nearestDistance = distance; }
            }
            if (nearest == null || nearestDistance > 1.5f) return;
            Guid operation = Guid.NewGuid();
            if (!authority.TryClaim(nearest.Key, peer, nearest.State.Generation, operation, out var claimed)) return;
            nearest.ClaimOperation = operation;
            nearest.ClaimPosition = nearest.Anchor.position;
            nearest.OwnerSince = now;
            nearest.EntryFrame = -1;
            Apply(nearest, new FurnitureSeatState(claimed.Peer, claimed.Generation));
            Publish(nearest);
        }

        private void AdoptHostLoadedSeat()
        {
            if (!host || blocked || gameplay == null || gameplay.Get_PlayerState != PlayerState.InFurnitureChair ||
                gameplay.PlayerWalking == null || authority.TryGetSeat(0, out _) || extraSeats.IsOwner(0u) ||
                SeatAdmission?.IsSeated(0) == true) return;
            var seat = seats.Values.FirstOrDefault(IsLocalWalkerAt);
            if (seat == null || seat.State.Occupied || ambiguous.Contains(seat.Id)) return;
            Guid operation = Guid.NewGuid();
            if (!authority.TryClaim(seat.Key, 0, seat.State.Generation, operation, out var claimed)) return;
            seat.ClaimOperation = operation;
            seat.ClaimPosition = seat.Anchor.position;
            seat.OwnerSince = Time.unscaledTime;
            seat.EntryFrame = Time.frameCount;
            Apply(seat, new FurnitureSeatState(claimed.Peer, claimed.Generation));
            Publish(seat);
        }

        private void OnLeft(uint peer)
        {
            presence.Remove(peer);
            if (host) RevokeOwnership(peer);
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values)
                if (record.Adapter == FurnitureSeatState.Adapter && seats.TryGetValue(record.Id, out var seat))
                    Apply(seat, FurnitureSeatState.Decode(record.Payload));
        }

        private void Changed(WorldRecord record)
        {
            if (record.Adapter == FurnitureSeatState.Adapter && seats.TryGetValue(record.Id, out var seat))
                Apply(seat, FurnitureSeatState.Decode(record.Payload));
        }

        [WorldCommandAdapters(FurnitureSeatState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || !seats.TryGetValue(id, out var seat) || ambiguous.Contains(id)) return;
            var command = FurnitureSeatCommand.Decode(payload);
            if (command.Action == FurnitureSeatAction.Claim) AdoptHostLoadedSeat();
            if (command.Action == FurnitureSeatAction.Release)
            {
                if (authority.TryRelease(seat.Key, peer, command.ExpectedGeneration, out var released))
                {
                    Apply(seat, new FurnitureSeatState(released.Peer, released.Generation));
                    Publish(seat);
                    SendReply(peer, seat, new FurnitureSeatReply(FurnitureSeatAction.Release, command.Operation, true, FurnitureSeatRefusal.None, seat.State));
                }
                else Deny(peer, seat, command, FurnitureSeatRefusal.Stale);
                return;
            }
            if (command.Action == FurnitureSeatAction.Cancel)
            {
                if (authority.TryCancel(seat.Key, peer, command.ExpectedGeneration, command.Operation, out var released))
                {
                    Apply(seat, new FurnitureSeatState(released.Peer, released.Generation));
                    Publish(seat);
                    SendReply(peer, seat, new FurnitureSeatReply(FurnitureSeatAction.Cancel, command.Operation, true, FurnitureSeatRefusal.None, seat.State));
                }
                else Deny(peer, seat, command, FurnitureSeatRefusal.Stale);
                return;
            }
            var refusal = Admission(seat, peer, command.ExpectedGeneration);
            if (refusal != FurnitureSeatRefusal.None) { Deny(peer, seat, command, refusal); return; }
            if (!authority.TryClaim(seat.Key, peer, command.ExpectedGeneration, command.Operation, out var claimed))
            { Deny(peer, seat, command, FurnitureSeatRefusal.Occupied); return; }
            seat.ClaimPosition = seat.Anchor.position;
            seat.OwnerSince = Time.unscaledTime;
            Apply(seat, new FurnitureSeatState(claimed.Peer, claimed.Generation));
            Publish(seat);
            SendReply(peer, seat, new FurnitureSeatReply(FurnitureSeatAction.Claim, command.Operation, true, FurnitureSeatRefusal.None, seat.State));
        }

        private FurnitureSeatRefusal Admission(Seat seat, uint peer, uint expectedGeneration)
        {
            if (blocked) return FurnitureSeatRefusal.TransfersBlocked;
            if (!seat.Anchor || seat.Chair == null && seat.DecorationSeat == null || ItemUnavailable(seat, peer))
                return FurnitureSeatRefusal.MissingSeat;
            if (seat.State.Occupied) return FurnitureSeatRefusal.Occupied;
            if (seat.State.Generation != expectedGeneration) return FurnitureSeatRefusal.Stale;
            if (extraSeats.IsOwner(peer) || SeatAdmission?.IsSeated(peer) == true) return FurnitureSeatRefusal.AlreadySeated;
            Vector3 location;
            if (peer == session!.LocalPeerId)
            {
                if (gameplay.Get_PlayerState != PlayerState.Walking) return FurnitureSeatRefusal.NotWalking;
                if (gameplay.Player == null || gameplay.Player.IsHoldingAnObject) return FurnitureSeatRefusal.Holding;
                location = gameplay.PlayerWalking != null ? gameplay.PlayerWalking.transform.position : gameplay.Player.transform.position;
            }
            else
            {
                if (!presence.TryGetValue(peer, out var current) || Time.unscaledTime - current.Time > PresenceTimeout ||
                    current.Pose.State != (byte)PlayerState.Walking) return FurnitureSeatRefusal.NotWalking;
                if (items.PeerHolding(peer)) return FurnitureSeatRefusal.Holding;
                location = new Vector3(current.Pose.X, current.Pose.Y, current.Pose.Z);
            }
            if (!SeatUsable(seat)) return FurnitureSeatRefusal.NotUsable;
            if (NpcNear(seat)) return FurnitureSeatRefusal.Npc;
            if (Vector3.Distance(location, seat.Anchor.position) > MaxEntryDistance) return FurnitureSeatRefusal.TooFar;
            return FurnitureSeatRefusal.None;
        }

        private void CommandRejected(uint peer, ulong id, byte[] payload)
        {
            if (!host || !seats.TryGetValue(id, out var seat)) return;
            var command = FurnitureSeatCommand.Decode(payload);
            Deny(peer, seat, command, FurnitureSeatRefusal.Stale);
        }

        private void Deny(uint peer, Seat seat, FurnitureSeatCommand command, FurnitureSeatRefusal reason) =>
            SendReply(peer, seat, new FurnitureSeatReply(command.Action, command.Operation, false, reason, seat.State));

        private void SendReply(uint peer, Seat seat, FurnitureSeatReply reply)
        {
            try { channel!.Reply(peer, seat.Id, reply.Encode()); }
            catch (InvalidOperationException) { }
        }

        private void Reply(ulong id, byte[] payload)
        {
            if (!seats.TryGetValue(id, out var seat)) return;
            var reply = FurnitureSeatReply.Decode(payload);
            if (!attempts.TryGetValue(reply.Operation, out var attempt)) return;
            if (attempt.Cancelled)
            {
                if (reply.Action == FurnitureSeatAction.Claim && reply.Accepted)
                    Request(seat, new FurnitureSeatCommand(FurnitureSeatAction.Cancel,
                        attempt.Command.ExpectedGeneration, attempt.Command.Operation));
                if (reply.Action == FurnitureSeatAction.Cancel || !reply.Accepted)
                    attempts.Remove(reply.Operation);
                return;
            }
            if (reply.Action != FurnitureSeatAction.Claim) return;
            if (!reply.Accepted)
            {
                SeatAdmission?.CancelLocal(this);
                LastRefusal = reply.Refusal.ToString();
                attempts.Remove(reply.Operation);
                if (localAttempt == attempt) localAttempt = null;
                MonMPPlugin.Instance?.ReportItemUseUnavailable(SeatRefusals.Message(reply.Refusal));
                return;
            }
            attempt.GrantedGeneration = reply.State.Generation;
            TryEnter(attempt);
        }

        private void TryEnter(EntryAttempt attempt)
        {
            var seat = attempt.Seat;
            if (session == null || attempt.Cancelled || !Ready || blocked || !seat.Anchor ||
                !seat.State.Occupied || seat.State.Owner != session.LocalPeerId ||
                seat.State.Generation != attempt.GrantedGeneration) return;
            if (gameplay.Get_PlayerState != PlayerState.Walking || gameplay.Player == null || gameplay.PlayerWalking == null ||
                gameplay.Player.IsHoldingAnObject || !SeatUsable(seat) || ItemUnavailable(seat, session.LocalPeerId) ||
                NpcNear(seat) || Vector3.Distance(gameplay.PlayerWalking.transform.position, seat.Anchor.position) > MaxEntryDistance)
            { Cancel(attempt); return; }
            if (attempt.NativeInvoked) return;
            attempt.NativeInvoked = true;
            chairPermit = seat.Chair;
            decorationPermit = seat.DecorationSeat;
            try
            {
                if (seat.Chair != null) seat.Chair.MouseDown();
                else if (seat.DecorationSeat != null) seat.DecorationSeat.MouseDown();
            }
            catch (Exception ex) when (ex is NullReferenceException || ex is MissingReferenceException || ex is InvalidOperationException)
            { LastRefusal = "Native seat entry failed: " + ex.GetType().Name; }
            finally { chairPermit = null; decorationPermit = null; }
            if (IsLocalWalkerAt(seat))
            {
                seat.ClaimPosition = seat.Anchor.position;
                seat.ClaimOperation = attempt.Command.Operation;
                seat.EntryFrame = Time.frameCount;
                seat.EntryProbePending = false;
                attempts.Remove(attempt.Command.Operation);
                if (localAttempt == attempt) localAttempt = null;
            }
            else
            {
                seat.EntryProbePending = true;
                seat.EntryFrame = Time.frameCount;
            }
        }

        private bool IsLocalWalkerAt(Seat seat) => gameplay.Get_PlayerState == PlayerState.InFurnitureChair &&
            gameplay.PlayerWalking != null && seat.Anchor != null && gameplay.PlayerWalking.transform.parent == seat.Anchor;

        private void NativeExit(Seat seat)
        {
            if (!IsLocalWalkerAt(seat)) return;
            try { gameplay.EnterExitChair(seat.Anchor); }
            catch (Exception ex) when (ex is NullReferenceException || ex is MissingReferenceException || ex is InvalidOperationException)
            { LastRefusal = "Native seat exit failed: " + ex.GetType().Name; }
        }

        private void Apply(Seat seat, FurnitureSeatState state)
        {
            if (!host && state.Generation != seat.State.Generation && !Wire.IsNewer(state.Generation, seat.State.Generation)) return;
            SeatAdmission?.Observe(this, seat.Id, state.Generation);
            bool wasMine = session != null && seat.State.Owner == session.LocalPeerId;
            bool changedOwner = seat.State.Owner != state.Owner || seat.State.Generation != state.Generation;
            seat.State = state;
            if (!changedOwner) return;
            if (state.Occupied) occupiedSeats.Add(seat);
            else occupiedSeats.Remove(seat);
            occupiedSnapshotDirty = true;
            if (wasMine && state.Owner != session!.LocalPeerId && IsLocalWalkerAt(seat)) NativeExit(seat);
            if (state.Owner == SeatOccupancy.Unoccupied) { seat.EntryFrame = -1; seat.ClaimOperation = Guid.Empty; }
            if (session != null && state.Owner == session.LocalPeerId && localAttempt?.Seat == seat && localAttempt.GrantedGeneration == state.Generation)
                TryEnter(localAttempt);
        }

        private void ReleaseHost(Seat seat, FurnitureSeatRefusal reason)
        {
            if (!host || !seat.State.Occupied || !authority.TryRelease(seat.Key, seat.State.Owner, seat.State.Generation, out var released)) return;
            Apply(seat, new FurnitureSeatState(released.Peer, released.Generation));
            Publish(seat);
        }

        private void Publish(Seat seat)
        {
            if (host && channel != null && channel.Records.ContainsKey(seat.Id) && !ambiguous.Contains(seat.Id))
                channel.Publish(seat.Id, seat.State.Encode());
        }

        private void Refuse(string message)
        {
            LastRefusal = message;
            MonMPPlugin.Instance?.ReportItemUseUnavailable(message);
        }


#if MONMP_DEVELOPMENT
        private void Log(ulong id, string identity, string status)
        {
            string row = id.ToString("X16") + "\t" + identity + "\t" + status;
            if (!writtenDiagnostics.Add(row)) return;
            diagnosticsRows.Add(row);
            diagnosticsDirty = true;
        }
#endif


#if MONMP_DEVELOPMENT
        private void FlushDiagnostics()
        {
            if (!diagnosticsDirty) return;
            try
            {

#if MONMP_DEVELOPMENT
                Directory.CreateDirectory(diagnostics);


                File.WriteAllLines(Path.Combine(diagnostics, "shared-furniture-seats.tsv"), diagnosticsRows);
#endif

                diagnosticsDirty = false;
            }
            catch { /* diagnostic output only */ }
        }
#endif


        public void Dispose()
        {
            if (disposed) return;
            // Release static/native lifecycle subscriptions even if later native
            // exit cleanup fails and the world owner has to continue teardown.
            seatSources.Dispose();
            SeatAdmission?.CancelLocal(this);
            disposed = true;
            if (localAttempt != null) Cancel(localAttempt);
            if (gameplay != null && gameplay.Get_PlayerState == PlayerState.InFurnitureChair)
                foreach (var seat in seats.Values)
                    if (IsLocalWalkerAt(seat)) NativeExit(seat);
            if (session != null)
            {
                session.PoseReceived -= OnPose;
                session.PeerLeft -= OnLeft;
            }
            if (channel != null)
            {
                channel.SnapshotApplied -= Snapshot;
                channel.Changed -= Changed;
                channel.CommandRequested -= Command;
                channel.CommandRejected -= CommandRejected;
                channel.ReplyReceived -= Reply;
            }
            attempts.Clear(); presence.Clear(); byChair.Clear(); byDecorationSeat.Clear(); seats.Clear();
            occupiedSeats.Clear(); occupiedSnapshot = Array.Empty<Seat>(); occupiedSnapshotDirty = false;
            discovery.Clear(); chairCatalog.Clear(); decorationCatalog.Clear(); chairCandidates.Clear(); decorationCandidates.Clear();
            session = null; channel = null;
        }
    }
}
