using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Core.Persistence;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Death is personal. Only the host retains the profile marker; a guest can
    // declare its own death and query its own status, never another player's.
    internal sealed class SharedPermadeath : IDisposable
    {
        internal const ushort Adapter = 156;
        internal const string SectionKey = "permadeath";
        internal static readonly ulong Entity = StaticId.Hash("permadeath/v1");
        private readonly SharedWorld world;
        private readonly NetworkSession session;
        private readonly WorldChannel channel;
        private readonly HashSet<Guid> dead = new HashSet<Guid>();
        private readonly HashSet<Guid> checkedProfiles = new HashSet<Guid>();
        private bool known, localDead, disposed;
        private float nextRequest;
        internal bool LocalDead => localDead;
        internal bool IsPermadead(uint peer) => session.IsHost && MonMPPlugin.Instance?.Gameplay?.Get_isPermadeath == true &&
            session.Peers.TryGetValue(peer, out var player) && IsDead(player.ProfileId);
        internal static IEnumerable<WorldRecord> InitialRecords() => new[] { new WorldRecord(Entity, 1, Adapter, new byte[] { 1 }) };

        internal SharedPermadeath(SharedWorld world, NetworkSession session, WorldChannel channel)
        {
            this.world = world; this.session = session; this.channel = channel;
            channel.CommandRequested += Command; channel.ReplyReceived += Reply;
        }

        internal void Died()
        {
            if (disposed || !session.IsRunning || session.IsHost) return;
            localDead = true; known = false; nextRequest = 0;
            // Begin releasing held objects, seats and driving immediately. Their
            // normal reliable release/retry paths finish while the death fades.
            world.FlushLocalOwnership();
            if (channel.FullySynchronized)
            {
                channel.Request(Entity, new byte[] { 1 });
                nextRequest = Time.unscaledTime + 1f;
            }
        }

        internal void Update()
        {
            if (disposed || session.IsHost || !session.IsReady || !channel.FullySynchronized ||
                session.HostWorld?.Permadeath != true || !world.PersonalProfileReady) return;
            if (!known && Time.unscaledTime >= nextRequest)
            {
                nextRequest = Time.unscaledTime + 1f;
                channel.Request(Entity, new[] { localDead ? (byte)1 : (byte)2 });
            }
            // A saved dead profile is restored only after ordinary personal-state
            // restoration, so profile replay cannot undo its native dead state.
            if (localDead) MonMPPlugin.Instance?.EnforcePermadeathSpectator();
        }

        private bool IsDead(Guid profile)
        {
            if (dead.Contains(profile)) return true;
            if (!checkedProfiles.Add(profile)) return false;
            SidecarSection? section;
            try { section = world.Saves?.SavedSection(profile, SectionKey); }
            catch (Exception ex)
            {
                Debug.LogWarning("MonMP optional permadeath checkpoint was unavailable; using living-player fallback: " + ex.Message);
                return false;
            }
            if (section?.Version != 1 || !section.Payload.SequenceEqual(new byte[] { 1 })) return false;
            dead.Add(profile); return true;
        }

        [WorldCommandAdapters(SharedPermadeath.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (disposed || !session.IsHost || entity != Entity || peer == 0 || payload.Length != 1 ||
                (payload[0] != 1 && payload[0] != 2) || !session.Peers.TryGetValue(peer, out var player) ||
                MonMPPlugin.Instance?.Gameplay?.Get_isPermadeath != true) return;
            bool wasDead = IsDead(player.ProfileId);
            if (payload[0] == 1 && !wasDead)
            {
                dead.Add(player.ProfileId);
                world.RevokeOwnership(peer);
                try { world.Saves?.RecordPermadeath(player.ProfileId); }
                catch (Exception ex) { Debug.LogWarning("MonMP retained permadeath for this session; its player checkpoint could not be written yet: " + ex.Message); }
            }
            channel.Reply(peer, Entity, new[] { IsDead(player.ProfileId) ? (byte)1 : (byte)0 });
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (disposed || session.IsHost || entity != Entity || payload.Length != 1 || payload[0] > 1) return;
            // An older query response must never resurrect a locally dead player.
            if (localDead && payload[0] == 0) return;
            localDead |= payload[0] == 1; known = true;
        }

        internal SidecarProfile Stamp(SidecarProfile profile)
        {
            if (!IsDead(profile.Id)) return profile;
            var state = profile.State.ToDictionary(p => p.Key, p => p.Value);
            state[SectionKey] = new SidecarSection(1, new byte[] { 1 });
            return new SidecarProfile(profile.Id, profile.DisplayName, profile.ColorRgb, profile.LastSeenUtc, state);
        }

        public void Dispose()
        {
            disposed = true;
            channel.CommandRequested -= Command; channel.ReplyReceived -= Reply;
        }
    }
}
