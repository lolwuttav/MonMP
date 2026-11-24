using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace MonMP.Core.Persistence
{
    public enum SavePhase : byte { Idle, Collecting, Writing, Complete, Failed }
    public enum FlushAcceptance { Ignored, Partial, WaitingForRelease, Accepted, Invalid }
    public sealed class SaveFlushPart
    {
        public const int ChunkBytes = 96;
        public const int MaxProfileBytes = 8192;
        private readonly byte[] payload;
        public Guid Round { get; }
        public ushort Index { get; }
        public ushort TotalBytes { get; }
        public byte[] Payload => (byte[])payload.Clone();
        public SaveFlushPart(Guid round, ushort index, ushort totalBytes, byte[] bytes)
        {
            int start = index * ChunkBytes;
            if (round == Guid.Empty || totalBytes < 1 || totalBytes > MaxProfileBytes || start >= totalBytes || bytes == null || bytes.Length != Math.Min(ChunkBytes, totalBytes - start))
                throw new InvalidDataException("Invalid owner flush part.");
            Round = round; Index = index; TotalBytes = totalBytes; payload = (byte[])bytes.Clone();
        }
        public byte[] Encode()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(Round.ToByteArray()); writer.Write(Index); writer.Write(TotalBytes); writer.Write((byte)payload.Length); writer.Write(payload); return stream.ToArray();
        }
        public static SaveFlushPart Decode(byte[] bytes)
        {
            if (bytes.Length < 22 || bytes.Length > 117) throw new InvalidDataException("Invalid owner flush packet size.");
            using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
            var round = new Guid(reader.ReadBytes(16)); ushort index = reader.ReadUInt16(), total = reader.ReadUInt16(); int size = reader.ReadByte();
            if (size != stream.Length - stream.Position) throw new InvalidDataException("Invalid owner flush part length.");
            return new SaveFlushPart(round, index, total, reader.ReadBytes(size));
        }
        public static IEnumerable<SaveFlushPart> Split(Guid round, byte[] profile)
        {
            if (profile == null || profile.Length < 1 || profile.Length > MaxProfileBytes) throw new InvalidDataException("Invalid owner profile size.");
            var copy = (byte[])profile.Clone(); var parts = new List<SaveFlushPart>();
            for (int offset = 0; offset < copy.Length; offset += ChunkBytes)
                parts.Add(new SaveFlushPart(round, (ushort)(offset / ChunkBytes), (ushort)copy.Length, copy.Skip(offset).Take(ChunkBytes).ToArray()));
            return parts.AsReadOnly();
        }
    }
    public sealed class FlushedProfile
    {
        private readonly byte[] payload;
        public uint Peer { get; }
        public Guid Profile { get; }
        public byte[] Payload => (byte[])payload.Clone();
        internal FlushedProfile(uint peer, Guid profile, byte[] bytes) { Peer = peer; Profile = profile; payload = (byte[])bytes.Clone(); }
    }
    // Host state machine only. The runtime adapter must enforce BlocksMutations, send requests,
    // supply live owners, and call native save + sidecar commit only after TryEnterWriting succeeds.
    // Optional recovery requires an adapter that can save a degraded authoritative world snapshot.
    public sealed class SaveBarrierRound
    {
        private sealed class Assembly
        {
            internal byte[] Bytes = null!;
            internal bool[] Parts = null!;
            internal bool Validated;
        }
        private readonly Func<double> clock;
        private readonly Func<IReadOnlyCollection<uint>> owners;
        private readonly Func<uint, byte[], bool> validateProfile;
        private readonly double settlementGraceSeconds;
        private Dictionary<uint, Guid> participants = new Dictionary<uint, Guid>();
        private readonly Dictionary<uint, Assembly> incoming = new Dictionary<uint, Assembly>();
        private readonly Dictionary<uint, FlushedProfile> accepted = new Dictionary<uint, FlushedProfile>();
        private readonly Dictionary<uint, FlushedProfile> retained = new Dictionary<uint, FlushedProfile>();
        private readonly HashSet<uint> departed = new HashSet<uint>();
        private readonly HashSet<uint> invalid = new HashSet<uint>();
        private uint[] missing = Array.Empty<uint>();
        private double deadline;
        private bool authoritativeRecovery;
        public Guid Round { get; private set; }
        public SavePhase Phase { get; private set; }
        public string Failure { get; private set; } = "";
        public bool UsedRecovery { get; private set; }
        public string RecoveryReason { get; private set; } = "";
        public bool BlocksMutations => Phase == SavePhase.Collecting || Phase == SavePhase.Writing;
        public int ExpectedCount => participants.Count;
        public int AcceptedCount => accepted.Count;
        public IReadOnlyDictionary<uint, FlushedProfile> AcceptedProfiles => new ReadOnlyDictionary<uint, FlushedProfile>(new Dictionary<uint, FlushedProfile>(accepted));
        public IReadOnlyDictionary<uint, FlushedProfile> RetainedProfiles => new ReadOnlyDictionary<uint, FlushedProfile>(new Dictionary<uint, FlushedProfile>(retained));
        public IReadOnlyList<uint> MissingPeers => Array.AsReadOnly((uint[])missing.Clone());
        public SaveBarrierRound(Func<double> clock, Func<IReadOnlyCollection<uint>> owners, Func<uint, byte[], bool> validateProfile,
            double settlementGraceSeconds = 0)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock)); this.owners = owners ?? throw new ArgumentNullException(nameof(owners));
            this.validateProfile = validateProfile ?? throw new ArgumentNullException(nameof(validateProfile));
            if (double.IsNaN(settlementGraceSeconds) || double.IsInfinity(settlementGraceSeconds) || settlementGraceSeconds < 0 || settlementGraceSeconds > 10)
                throw new ArgumentOutOfRangeException(nameof(settlementGraceSeconds));
            this.settlementGraceSeconds = settlementGraceSeconds;
        }
        private double Now()
        { double value = clock(); if (double.IsNaN(value) || double.IsInfinity(value) || value < 0) throw new InvalidOperationException("Invalid save clock."); return value; }
        private HashSet<uint> Owned()
        {
            var live = owners(); if (live == null || live.Count > 64 || live.Any(p => p == uint.MaxValue)) throw new InvalidOperationException("Invalid ownership inventory.");
            return new HashSet<uint>(live);
        }
        public Guid Begin(IEnumerable<KeyValuePair<uint, Guid>> peers, double timeoutSeconds = 5)
        {
            if (BlocksMutations) throw new InvalidOperationException("A save is already active.");
            if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || timeoutSeconds <= 0 || timeoutSeconds > 60) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            var copy = new Dictionary<uint, Guid>();
            foreach (var p in peers)
            {
                if (p.Key == uint.MaxValue || p.Value == Guid.Empty || copy.ContainsKey(p.Key) || copy.ContainsValue(p.Value) || copy.Count >= 64) throw new InvalidDataException("Invalid save participants.");
                copy.Add(p.Key, p.Value);
            }
            if (!copy.ContainsKey(0)) throw new InvalidDataException("Host profile is required.");
            double end = Now() + timeoutSeconds; if (double.IsInfinity(end)) throw new InvalidOperationException("Invalid save deadline.");
            participants = copy; incoming.Clear(); accepted.Clear(); retained.Clear(); departed.Clear(); invalid.Clear(); missing = Array.Empty<uint>(); Failure = "";
            UsedRecovery = authoritativeRecovery = false; RecoveryReason = "";
            Round = Guid.NewGuid(); deadline = end; Phase = SavePhase.Collecting; return Round;
        }
        public FlushAcceptance Receive(uint peer, SaveFlushPart part)
        {
            if (Phase != SavePhase.Collecting || part.Round != Round || !participants.ContainsKey(peer) || departed.Contains(peer)) return FlushAcceptance.Ignored;
            if (invalid.Contains(peer)) return FlushAcceptance.Invalid;
            if (accepted.ContainsKey(peer)) return FlushAcceptance.Accepted;
            if (!incoming.TryGetValue(peer, out var assembly))
            {
                assembly = new Assembly { Bytes = new byte[part.TotalBytes], Parts = new bool[(part.TotalBytes + SaveFlushPart.ChunkBytes - 1) / SaveFlushPart.ChunkBytes] };
                incoming.Add(peer, assembly);
            }
            if (assembly.Bytes.Length != part.TotalBytes) return Reject(peer);
            byte[] bytes = part.Payload; int offset = part.Index * SaveFlushPart.ChunkBytes;
            if (assembly.Parts[part.Index])
            { for (int i = 0; i < bytes.Length; i++) if (assembly.Bytes[offset + i] != bytes[i]) return Reject(peer); }
            else { Array.Copy(bytes, 0, assembly.Bytes, offset, bytes.Length); assembly.Parts[part.Index] = true; }
            if (assembly.Parts.Any(p => !p)) return FlushAcceptance.Partial;
            if (!assembly.Validated)
            {
                bool valid;
                try { valid = validateProfile(peer, (byte[])assembly.Bytes.Clone()); }
                catch { Reject(peer); throw; }
                if (!valid) return Reject(peer);
                assembly.Validated = true;
            }
            if (Owned().Contains(peer)) return FlushAcceptance.WaitingForRelease;
            Accept(peer, assembly); return FlushAcceptance.Accepted;
        }
        private FlushAcceptance Reject(uint peer) { invalid.Add(peer); incoming.Remove(peer); return FlushAcceptance.Invalid; }
        private void Accept(uint peer, Assembly assembly)
        { accepted[peer] = new FlushedProfile(peer, participants[peer], assembly.Bytes); incoming.Remove(peer); retained.Remove(peer); }
        public void Disconnected(uint peer)
        { if (Phase == SavePhase.Collecting && participants.ContainsKey(peer)) departed.Add(peer); }
        private void Promote(HashSet<uint> owned)
        { foreach (var pair in incoming.Where(p => p.Value.Validated && !owned.Contains(p.Key)).ToArray()) Accept(pair.Key, pair.Value); }
        public bool TryEnterWriting(Action<uint> revokeOwnership, Func<bool>? writingReady = null,
            Func<uint, Guid, byte[]?>? retainedProfile = null, Action? recoverWriting = null)
        {
            if (Phase != SavePhase.Collecting) return false;
            if (revokeOwnership == null) throw new ArgumentNullException(nameof(revokeOwnership));
            try
            {
                double now = Now();
                string pending = "";
                HashSet<uint>? live = null;
                try { live = Owned(); Promote(live); }
                catch (Exception ex) when (recoverWriting != null) { pending = ex.Message; }
                if (now < deadline && participants.Keys.Any(p => !accepted.ContainsKey(p) && !departed.Contains(p))) return false;
                // Recheck all current owners, including unexpected newly joined owners. An ACK alone
                // is not permission to save; unresolved bookkeeping needs explicit adapter recovery.
                if (live != null)
                {
                    foreach (uint peer in live)
                    {
                        try { revokeOwnership(peer); }
                        catch (Exception ex) when (recoverWriting != null) { pending = ex.Message; }
                    }
                    try
                    {
                        live = Owned(); Promote(live);
                        if (live.Count != 0) pending = "Ownership remains after save revocation.";
                        else if (recoverWriting != null) pending = ""; // Actual release can succeed before an adapter throws.
                    }
                    catch (Exception ex) when (recoverWriting != null) { live = null; pending = ex.Message; }
                }
                if (now >= deadline && retainedProfile != null)
                {
                    foreach (var peer in participants.Where(p => !accepted.ContainsKey(p.Key) && !retained.ContainsKey(p.Key)))
                    {
                        try
                        {
                            byte[]? bytes = retainedProfile(peer.Key, peer.Value);
                            if (bytes == null || bytes.Length < 1 || bytes.Length > SaveFlushPart.MaxProfileBytes || !validateProfile(peer.Key, (byte[])bytes.Clone())) continue;
                            retained.Add(peer.Key, new FlushedProfile(peer.Key, peer.Value, bytes));
                        }
                        catch (Exception ex) when (recoverWriting != null) { pending = ex.Message; }
                    }
                }
                if (!accepted.ContainsKey(0) && !retained.ContainsKey(0)) pending = "Host profile was not flushed.";
                if (pending.Length == 0 && writingReady != null)
                {
                    try { if (!writingReady()) pending = "World settlement is still pending."; }
                    catch (Exception ex) when (recoverWriting != null) { pending = ex.Message; }
                }
                if (pending.Length != 0)
                {
                    if (settlementGraceSeconds > 0 && now < deadline + settlementGraceSeconds) return false;
                    if (recoverWriting == null) throw new InvalidOperationException(pending);
                    // The adapter explicitly opts into saving an authoritative recovery snapshot.
                    // It must settle/cancel native work or choose a safe degraded representation;
                    // no personal profile is invented when neither fresh nor retained data exists.
                    recoverWriting();
                    Promote(new HashSet<uint>());
                    UsedRecovery = authoritativeRecovery = true; RecoveryReason = pending.Substring(0, Math.Min(pending.Length, 512));
                }
                if (retained.Count != 0)
                {
                    UsedRecovery = true;
                    if (RecoveryReason.Length == 0) RecoveryReason = "Recent player checkpoints were retained.";
                }
                missing = participants.Keys.Where(p => !accepted.ContainsKey(p)).OrderBy(p => p).ToArray();
                Phase = SavePhase.Writing; return true;
            }
            catch (Exception ex) { Fail(ex.Message); throw; }
        }
        public void Complete()
        { if (Phase != SavePhase.Writing) throw new InvalidOperationException("Save has not entered writing."); Phase = SavePhase.Complete; }
        public void RefreshHostProfile(byte[] bytes)
        {
            if (Phase != SavePhase.Writing) throw new InvalidOperationException("Host refresh requires a writing save.");
            if (bytes == null || bytes.Length < 1 || bytes.Length > SaveFlushPart.MaxProfileBytes || !validateProfile(0, (byte[])bytes.Clone()))
                throw new InvalidDataException("Invalid refreshed host profile.");
            accepted[0] = new FlushedProfile(0, participants[0], bytes); retained.Remove(0);
            missing = missing.Where(peer => peer != 0).ToArray();
            if (!authoritativeRecovery && retained.Count == 0) { UsedRecovery = false; RecoveryReason = ""; }
        }
        public void Fail(string reason)
        {
            if (!BlocksMutations) throw new InvalidOperationException("No active save to fail.");
            Failure = string.IsNullOrEmpty(reason) ? "Save failed." : reason.Substring(0, Math.Min(reason.Length, 256)); Phase = SavePhase.Failed;
        }
    }
}
