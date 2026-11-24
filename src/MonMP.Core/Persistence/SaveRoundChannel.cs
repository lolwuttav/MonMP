using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.Session;
using MonMP.Core.World;

namespace MonMP.Core.Persistence
{
    // Transport and collection only. The caller must freeze all relevant native mutations,
    // drain transactions and perform native save + sidecar commit while Phase == Writing.
    public sealed class SaveRoundChannel : IDisposable
    {
        public const ushort Adapter = 11;
        public static readonly ulong CommandEntity = StaticId.Hash("save-round/commands/v1");
        public static readonly ulong StateEntity = StaticId.Hash("save-round/state/v1");
        private readonly NetworkSession session;
        private readonly WorldChannel world;
        private readonly SaveBarrierRound barrier;
        private readonly Action<bool> block;
        private readonly Action flush;
        private readonly Func<bool> ready;
        private readonly Func<PlayerProfileState> capture;
        private readonly Action<uint> revoke;
        private readonly Func<double> clock;
        private readonly Func<uint, Guid, PlayerProfileState?>? cachedProfile;
        private readonly Func<bool>? writingReady;
        private readonly Action? recoverWriting;
        private Dictionary<uint, PeerInfo> participants = new Dictionary<uint, PeerInfo>();
        private Guid submitted;
        private SaveFlushPart[]? localParts;
        private int localPart;
        private double nextLocalAttempt;
        private double nextFlushAttempt;
        private string flushFailure = "", captureFailure = "";
        private bool blocked, disposed;
        public Guid Round { get; private set; }
        public SavePhase Phase { get; private set; }
        public int ExpectedCount { get; private set; }
        public int AcceptedCount { get; private set; }
        public string LocalFailure { get; private set; } = "";
        public bool UsedRecovery => barrier.UsedRecovery;
        public string RecoveryReason => barrier.RecoveryReason;
        public IReadOnlyList<uint> MissingPeers => barrier.MissingPeers;
        public IReadOnlyList<Guid> AcceptedProfileIds => barrier.AcceptedProfiles.Values.Select(p => p.Profile).ToArray();
        public IReadOnlyList<Guid> RetainedProfileIds => barrier.RetainedProfiles.Values.Select(p => p.Profile).ToArray();
        public IReadOnlyList<uint> RetainedPeers => barrier.RetainedProfiles.Keys.OrderBy(p => p).ToArray();
        public static IEnumerable<WorldRecord> InitialRecords()
        {
            yield return new WorldRecord(CommandEntity, 1, Adapter, new byte[] { 1 });
            yield return new WorldRecord(StateEntity, 1, Adapter, Encode(Guid.Empty, SavePhase.Idle, 0, 0));
        }
        public SaveRoundChannel(NetworkSession session, WorldChannel world, Func<double> clock,
            Func<IReadOnlyCollection<uint>> owners, Action<uint> revoke, Action<bool> block,
            Action flush, Func<bool> ready, Func<PlayerProfileState> capture,
            Func<uint, Guid, PlayerProfileState?>? cachedProfile = null, Func<bool>? writingReady = null,
            Action? recoverWriting = null, double settlementGraceSeconds = 0)
        {
            this.session = session; this.world = world; this.revoke = revoke; this.block = block;
            this.flush = flush; this.ready = ready; this.capture = capture;
            this.clock = clock; this.cachedProfile = cachedProfile; this.writingReady = writingReady; this.recoverWriting = recoverWriting;
            barrier = new SaveBarrierRound(clock, owners, (_, bytes) =>
            { try { PlayerProfileState.Decode(bytes); return true; } catch (InvalidDataException) { return false; } }, settlementGraceSeconds);
            world.CommandRequested += Command; world.Changed += Changed; world.SnapshotApplied += Snapshot;
            session.PeerLeft += Left;
        }
        public void Begin(double timeoutSeconds = 5)
        {
            if (disposed || !session.IsHost || !world.FullySynchronized) throw new InvalidOperationException("A ready host is required to begin saving.");
            var peers = session.Peers.ToDictionary(p => p.Key, p => p.Value);
            barrier.Begin(peers.Select(p => new KeyValuePair<uint, Guid>(p.Key, p.Value.ProfileId)), timeoutSeconds);
            participants = peers; LocalFailure = ""; Publish();
        }
        public void Update()
        {
            if (disposed) return;
            if (!session.IsRunning) { SetBlocked(false); return; }
            if (Phase != SavePhase.Collecting || (!session.IsHost && !world.FullySynchronized)) return;
            try
            {
                double now = clock();
                if (now >= nextFlushAttempt)
                {
                    // Keep retrying settlement after early capture, without asking every native
                    // adapter to release again on every rendered frame.
                    try { flush(); flushFailure = ""; }
                    catch (Exception ex) { flushFailure = ex.Message; }
                    nextFlushAttempt = now + 0.25;
                }
                if (now >= nextLocalAttempt)
                {
                    bool failed = false;
                    try
                    {
                        if (submitted != Round)
                        {
                            if (localParts == null && ready()) localParts = SaveFlushPart.Split(Round, capture().Encode()).ToArray();
                            if (localParts != null)
                            {
                                for (; localPart < localParts.Length; localPart++)
                                    if (session.IsHost) barrier.Receive(0, localParts[localPart]); else world.Request(CommandEntity, localParts[localPart].Encode());
                                submitted = Round;
                            }
                        }
                        captureFailure = "";
                    }
                    catch (Exception ex) { captureFailure = ex.Message; failed = true; }
                    nextLocalAttempt = failed ? now + 0.1 : now;
                }
                LocalFailure = captureFailure.Length == 0 ? flushFailure : captureFailure;
                if (session.IsHost)
                {
                    if (barrier.TryEnterWriting(revoke, writingReady,
                        cachedProfile == null ? null : (peer, profile) => cachedProfile(peer, profile)?.Encode(), recoverWriting)) LocalFailure = "";
                    Publish();
                }
            }
            catch (Exception ex)
            {
                LocalFailure = ex.Message;
                if (session.IsHost) { if (barrier.BlocksMutations) barrier.Fail(ex.Message); Publish(); }
                // Guests continue retrying while collecting; the host deadline can retain their
                // validated checkpoint or previous sidecar if a fresh profile never arrives.
            }
        }
        public void Complete()
        { RequireHost(); barrier.Complete(); Publish(); }
        public void RefreshHostProfile(PlayerProfileState state)
        {
            RequireHost();
            if (state == null) throw new ArgumentNullException(nameof(state));
            barrier.RefreshHostProfile(state.Encode()); Publish();
        }
        public void Fail(string reason)
        { RequireHost(); barrier.Fail(reason); LocalFailure = reason; Publish(); }
        private void RequireHost()
        { if (disposed || !session.IsHost) throw new InvalidOperationException("Only the host controls save completion."); }
        public IReadOnlyList<SidecarProfile> MergeProfiles(IEnumerable<SidecarProfile> previous, long savedUtc)
        {
            RequireHost();
            if (Phase != SavePhase.Writing && Phase != SavePhase.Complete) throw new InvalidOperationException("Profile collection is not complete.");
            var merged = previous.ToDictionary(p => p.Id);
            var retained = barrier.RetainedProfiles;
            foreach (var flushed in retained.Values.Concat(barrier.AcceptedProfiles.Values))
            {
                var peer = participants[flushed.Peer];
                var fields = merged.TryGetValue(flushed.Profile, out var old) ? old.State.ToDictionary(p => p.Key, p => p.Value) : new Dictionary<string, SidecarSection>();
                fields[PlayerProfileState.SectionKey] = PlayerProfileState.Decode(flushed.Payload).ToSection();
                long seen = retained.ContainsKey(flushed.Peer) && old != null ? old.LastSeenUtc : savedUtc;
                merged[flushed.Profile] = new SidecarProfile(flushed.Profile, peer.DisplayName, peer.ColorRgb, seen, fields);
            }
            return Array.AsReadOnly(merged.Values.OrderBy(p => p.Id).ToArray());
        }
        [WorldCommandAdapters(Adapter)]
        private void Command(uint peer, ulong entity, byte[] bytes)
        {
            if (!session.IsHost || entity != CommandEntity || Phase != SavePhase.Collecting) return;
            barrier.Receive(peer, SaveFlushPart.Decode(bytes)); Publish();
        }
        private void Left(uint peer) { if (session.IsHost) barrier.Disconnected(peer); }
        private void Publish()
        {
            Apply(Encode(barrier.Round, barrier.Phase, barrier.ExpectedCount, barrier.AcceptedCount));
            world.Publish(StateEntity, Encode(Round, Phase, ExpectedCount, AcceptedCount));
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        { if (!session.IsHost && records.TryGetValue(StateEntity, out var state)) Apply(state.Payload); }
        private void Changed(WorldRecord record)
        { if (!session.IsHost && record.Id == StateEntity) Apply(record.Payload); }
        private static byte[] Encode(Guid round, SavePhase phase, int expected, int accepted)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(round.ToByteArray()); writer.Write((byte)phase); writer.Write((byte)expected); writer.Write((byte)accepted); return stream.ToArray();
        }
        private void Apply(byte[] bytes)
        {
            if (bytes.Length != 19) throw new InvalidDataException("Invalid save state length.");
            using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
            var round = new Guid(reader.ReadBytes(16)); var phase = (SavePhase)reader.ReadByte(); int expected = reader.ReadByte(), accepted = reader.ReadByte();
            if (phase > SavePhase.Failed || expected > 64 || accepted > expected ||
                (phase == SavePhase.Idle ? round != Guid.Empty || expected != 0 : round == Guid.Empty || expected == 0))
                throw new InvalidDataException("Invalid save state.");
            if (Round != round)
            { localParts = null; localPart = 0; nextLocalAttempt = nextFlushAttempt = 0; LocalFailure = flushFailure = captureFailure = ""; }
            Round = round; Phase = phase; ExpectedCount = expected; AcceptedCount = accepted;
            SetBlocked(phase == SavePhase.Collecting || phase == SavePhase.Writing);
        }
        private void SetBlocked(bool value)
        {
            if (blocked == value) return;
            try { block(value); blocked = value; }
            catch
            {
                // Never let collection proceed with a partially applied runtime freeze.
                session.Stop("Save mutation gate failed; multiplayer stopped."); throw;
            }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            world.CommandRequested -= Command; world.Changed -= Changed; world.SnapshotApplied -= Snapshot; session.PeerLeft -= Left;
            SetBlocked(false);
        }
    }
}
