using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.Session;
using MonMP.Core.World;

namespace MonMP.Core.Persistence
{
    // Volatile latest-safe profiles. Disk persistence still requires the coordinated barrier.
    public sealed class LiveProfileChannel : IDisposable
    {
        public const ushort Adapter = 13;
        public static readonly ulong Entity = StaticId.Hash("live-personal-profile/v1");
        private readonly NetworkSession session;
        private readonly WorldChannel world;
        private readonly Func<double> clock;
        private readonly Dictionary<uint, Assembly> incoming = new Dictionary<uint, Assembly>();
        private readonly Dictionary<Guid, Entry> profiles = new Dictionary<Guid, Entry>();
        private uint sequence;
        private long revision;
        private double nextSend;
        private bool disposed;
        private sealed class Assembly
        {
            internal uint Sequence;
            internal Guid Transfer;
            internal double Next;
            internal readonly byte[] Bytes = new byte[PlayerProfileState.EncodedBytes];
            internal readonly bool[] Parts = new bool[2];
            internal bool Complete;
        }
        private sealed class Entry
        {
            internal PlayerProfileState State = null!;
            internal long Revision;
            internal PeerInfo? Peer;
            internal string? DisplayName;
            internal uint ColorRgb;
            internal long SeenUtc;
            internal bool Accepted;
        }
        public event Action<SidecarProfile>? SafeProfileAccepted;
        public IEnumerable<Guid> ConnectedProfileIds => session.Peers.Values.Select(p => p.ProfileId);
        public static WorldRecord InitialRecord() => new WorldRecord(Entity, 1, Adapter, new byte[] { 1 });
        public LiveProfileChannel(NetworkSession session, WorldChannel world, Func<double> clock)
        {
            this.session = session; this.world = world; this.clock = clock;
            world.CommandRequested += Receive; session.PeerLeft += Left;
        }
        public PlayerProfileState? Find(Guid profile) => profiles.TryGetValue(profile, out var value) ? value.State : null;
        public long? LastSeenUtc(Guid profile) => profiles.TryGetValue(profile, out var value) ? value.SeenUtc : (long?)null;
        public bool IsAccepted(Guid profile) => profiles.TryGetValue(profile, out var value) && value.Accepted;
        public long AdvanceObservation(Guid profile, long restoredUtc)
        {
            if (!profiles.TryGetValue(profile, out var entry) || !entry.Accepted) return restoredUtc;
            entry.SeenUtc = PersonalProfileHistory.AfterObservation(entry.SeenUtc, restoredUtc);
            return entry.SeenUtc;
        }
        public int Count => profiles.Count;
        public bool CanSubmit => !disposed && session.IsRunning && world.FullySynchronized && clock() >= nextSend;
        public bool Submit(PlayerProfileState state)
        {
            if (!CanSubmit) return false;
            if (state == null) throw new ArgumentNullException(nameof(state));
            nextSend = clock() + 1;
            if (session.IsHost) { AcceptSafe(session.ProfileId, state); return true; }
            if (sequence == uint.MaxValue) throw new InvalidOperationException("Live profile sequence exhausted.");
            uint current = ++sequence;
            foreach (var part in SaveFlushPart.Split(Guid.NewGuid(), state.Encode()))
            {
                using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
                writer.Write(current); writer.Write(part.Encode()); world.Request(Entity, stream.ToArray());
            }
            return true;
        }
        // Also used to replace an older periodic sample with an accepted coordinated flush.
        public void Remember(Guid profile, PlayerProfileState state)
        {
            if (disposed || !session.IsHost || profile == Guid.Empty || state == null) throw new InvalidOperationException("Only the live host stores personal profiles.");
            if (!profiles.ContainsKey(profile) && profiles.Count >= 512)
                profiles.Remove(profiles.OrderBy(p => p.Value.Revision).First().Key);
            var peer = session.Peers.Values.FirstOrDefault(p => p.ProfileId == profile);
            profiles.TryGetValue(profile, out var prior);
            if (peer == null) peer = prior?.Peer;
            profiles[profile] = new Entry
            {
                State = state, Revision = ++revision, Peer = peer,
                DisplayName = peer?.DisplayName ?? prior?.DisplayName,
                ColorRgb = peer?.ColorRgb ?? prior?.ColorRgb ?? 0,
                SeenUtc = PersonalProfileHistory.AfterObservation(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), prior?.SeenUtc ?? 0), Accepted = true
            };
        }
        public void RememberRecovered(SidecarProfile profile)
        {
            if (profile == null || !profile.State.TryGetValue(PlayerProfileState.SectionKey, out var section))
                throw new InvalidDataException("Recovered profile has no personal state.");
            var state = PlayerProfileState.FromSection(section);
            if (profile.LastSeenUtc > PersonalProfileHistory.MaximumSavedObservation) return;
            if (disposed || !session.IsHost) throw new InvalidOperationException("Only the live host stores personal profiles.");
            if (profiles.TryGetValue(profile.Id, out var prior))
            {
                if (prior.Accepted) { AdvanceObservation(profile.Id, profile.LastSeenUtc); return; }
                if (prior.SeenUtc > profile.LastSeenUtc) return;
            }
            if (prior == null && profiles.Count >= 512) profiles.Remove(profiles.OrderBy(p => p.Value.Revision).First().Key);
            var peer = session.Peers.Values.FirstOrDefault(p => p.ProfileId == profile.Id);
            profiles[profile.Id] = new Entry
            {
                State = state, Revision = ++revision, Peer = peer, DisplayName = peer?.DisplayName ?? profile.DisplayName,
                ColorRgb = peer?.ColorRgb ?? profile.ColorRgb, SeenUtc = profile.LastSeenUtc, Accepted = false
            };
        }
        private void AcceptSafe(Guid profile, PlayerProfileState state)
        {
            Remember(profile, state);
            if (profiles.TryGetValue(profile, out var entry) && entry.DisplayName != null)
                SafeProfileAccepted?.Invoke(ToSidecarProfile(profile, entry));
        }
        private static SidecarProfile ToSidecarProfile(Guid id, Entry entry) => new SidecarProfile(id, entry.DisplayName!, entry.ColorRgb,
            entry.SeenUtc, new[] { new KeyValuePair<string, SidecarSection>(PlayerProfileState.SectionKey, entry.State.ToSection()) });
        public IReadOnlyList<SidecarProfile> MergeDisconnected(IEnumerable<SidecarProfile> previous) => MergeForSave(previous, false);
        // Include connected checkpoints before the round overlays fresh responses. This
        // preserves their original recency when a peer cannot answer the bounded flush.
        public IReadOnlyList<SidecarProfile> MergeForSave(IEnumerable<SidecarProfile> previous, bool includeConnected = true)
        {
            if (disposed || !session.IsHost) throw new InvalidOperationException("Only the live host merges cached profiles.");
            var merged = previous.ToDictionary(p => p.Id);
            var connected = new HashSet<Guid>(session.Peers.Values.Select(p => p.ProfileId));
            foreach (var pair in profiles)
            {
                var entry = pair.Value;
                if ((!includeConnected && connected.Contains(pair.Key)) || entry.DisplayName == null) continue;
                merged.TryGetValue(pair.Key, out var old);
                if (old != null && PersonalProfileHistory.ValidPersonalState(old))
                {
                    if (entry.Accepted) AdvanceObservation(pair.Key, old.LastSeenUtc);
                    else if (old.LastSeenUtc > entry.SeenUtc) continue;
                }
                var fields = old != null ? old.State.ToDictionary(p => p.Key, p => p.Value) : new Dictionary<string, SidecarSection>();
                fields[PlayerProfileState.SectionKey] = entry.State.ToSection();
                merged[pair.Key] = new SidecarProfile(pair.Key, entry.DisplayName, entry.ColorRgb, entry.SeenUtc, fields);
            }
            // Cached departed guests can exceed the serialized cap during a merge. Select
            // the intended retention set before that cap is enforced, keeping connected
            // profiles (and their other saved sections) for the coordinated flush to update.
            return Array.AsReadOnly(ProfilePruning.KeepRecent(merged.Values, protectedProfiles: connected).OrderBy(p => p.Id).ToArray());
        }
        [WorldCommandAdapters(Adapter)]
        private void Receive(uint peer, ulong entity, byte[] bytes)
        {
            if (disposed || !session.IsHost || entity != Entity || peer == 0 || !session.Peers.TryGetValue(peer, out var info)) return;
            if (bytes.Length < 26 || bytes.Length > 121) throw new InvalidDataException("Invalid live profile packet length.");
            using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
            uint current = reader.ReadUInt32(); var part = SaveFlushPart.Decode(reader.ReadBytes(bytes.Length - 4));
            if (current == 0 || part.TotalBytes != PlayerProfileState.EncodedBytes) throw new InvalidDataException("Invalid live profile sequence or size.");
            if (!incoming.TryGetValue(peer, out var assembly) || current > assembly.Sequence)
            {
                if (assembly != null && clock() < assembly.Next) return;
                assembly = new Assembly { Sequence = current, Transfer = part.Round, Next = clock() + 0.5 };
                incoming[peer] = assembly;
            }
            if (current < assembly.Sequence) return;
            if (part.Round != assembly.Transfer) throw new InvalidDataException("Live profile transfer changed within a sequence.");
            byte[] chunk = part.Payload; int offset = part.Index * SaveFlushPart.ChunkBytes;
            if (assembly.Parts[part.Index])
            {
                if (!assembly.Bytes.Skip(offset).Take(chunk.Length).SequenceEqual(chunk)) throw new InvalidDataException("Contradictory live profile chunk.");
                return;
            }
            Array.Copy(chunk, 0, assembly.Bytes, offset, chunk.Length); assembly.Parts[part.Index] = true;
            if (assembly.Complete || !assembly.Parts.All(p => p)) return;
            var state = PlayerProfileState.Decode(assembly.Bytes);
            assembly.Complete = true; AcceptSafe(info.ProfileId, state);
        }
        private void Left(uint peer) => incoming.Remove(peer);
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            world.CommandRequested -= Receive; session.PeerLeft -= Left;
            incoming.Clear(); profiles.Clear(); SafeProfileAccepted = null;
        }
    }
}
