using System.Collections.Generic;

namespace MonMP.Plugin.Replication
{
    // Starts still use the ordinary producer budget. A terminal stop may bypass that budget
    // only once for an admitted actor/source, so it cannot become an unlimited cue channel.
    internal sealed class BoltLoopAdmission
    {
        private readonly Dictionary<uint, byte> active = new Dictionary<uint, byte>();
        internal bool Contains(uint actor, bool unscrew) => active.TryGetValue(actor, out byte bits) && (bits & (unscrew ? 2 : 1)) != 0;
        internal void Start(uint actor, bool unscrew)
        {
            active.TryGetValue(actor, out byte bits);
            active[actor] = (byte)(bits | (unscrew ? 2 : 1));
        }
        internal bool Stop(uint actor, bool unscrew)
        {
            if (!Contains(actor, unscrew)) return false;
            byte remaining = (byte)(active[actor] & ~(unscrew ? 2 : 1));
            if (remaining == 0) active.Remove(actor); else active[actor] = remaining;
            return true;
        }
        internal void Remove(uint actor) => active.Remove(actor);
        internal void Clear() => active.Clear();
    }

    // A missed producer stop, lost renewal, or stalled tool must eventually release its emitter.
    // Keys are added only after SpatialSounds has leased an emitter from its bounded pool.
    internal sealed class PresentationLoopLifetime<TKey> where TKey : notnull
    {
        internal const float LeaseSeconds = 2f;
        private readonly Dictionary<TKey, float> deadlines = new Dictionary<TKey, float>();
        internal void Renew(TKey key, float now) => deadlines[key] = now + LeaseSeconds;
        internal void Remove(TKey key) => deadlines.Remove(key);
        internal void Clear() => deadlines.Clear();
        internal IEnumerable<TKey> Expire(float now)
        {
            var expired = new List<TKey>();
            foreach (var pair in deadlines) if (now >= pair.Value) expired.Add(pair.Key);
            foreach (var key in expired) deadlines.Remove(key);
            return expired;
        }
    }
}
