using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Core.Persistence;

namespace MonMP.Core.Session
{
    /// <summary>Host-owned permission preferences, separate from native cheat activation.</summary>
    public sealed class RememberedCheatPermissions : IDisposable
    {
        private readonly NetworkSession session;
        private readonly CheatPermissionStore store;
        private readonly Func<IEnumerable<KeyValuePair<Guid, string>>>? knownProfiles;
        public RememberedCheatPermissions(NetworkSession session, CheatPermissionStore store,
            Func<IEnumerable<KeyValuePair<Guid, string>>>? knownProfiles = null)
        {
            if (!session.IsHost) throw new InvalidOperationException("Only the host remembers cheat permissions.");
            this.session = session; this.store = store;
            this.knownProfiles = knownProfiles;
            session.PeerJoined += Restore;
            session.CheatPermissionChanged += Remember;
            foreach (var peer in session.Peers.Values.ToArray()) Restore(peer);
        }
        private void Restore(PeerInfo peer)
        {
            if (peer.Id == 0 || !session.IsHost) return;
            try
            {
                if (store.TryResume(peer.ProfileId, peer.DisplayName, session.Peers.Values.Select(p => p.ProfileId), out bool allowed,
                    KnownIdentities(peer.ProfileId))) session.SetCheatPermission(peer.Id, allowed);
            }
            catch { /* Unavailable optional identity/preferences cannot block joining. */ }
        }
        private IEnumerable<KeyValuePair<Guid, string>> KnownIdentities(Guid joiningProfile)
        {
            // Resolve exact IDs without reading optional native history; only name
            // fallback needs this catalog. Unavailable history cannot authorize it.
            foreach (var profile in knownProfiles?.Invoke() ?? Array.Empty<KeyValuePair<Guid, string>>()) yield return profile;
            // A just-added join isn't evidence of a second historical identity.
            // Other live/departed identities still keep a duplicated name separate.
            foreach (var profile in session.ProfileIdentityHints)
                if (profile.Key != joiningProfile) yield return profile;
        }
        private void Remember(uint peerId, bool allowed)
        {
            if (session.IsHost && peerId != 0 && session.Peers.TryGetValue(peerId, out var peer))
                store.Remember(peer.ProfileId, peer.DisplayName, allowed);
        }
        public void Dispose()
        {
            session.PeerJoined -= Restore;
            session.CheatPermissionChanged -= Remember;
        }
    }
}
