using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MonMP.Core.Session
{
    // Friends-first resume hints. Saved credentials are advisory; an active profile is never shared.
    public static class ProfileIdentityPolicy
    {
        public static Guid ReadOrCreate(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id : Guid.NewGuid();

        public static Guid Resolve(Guid requested, string displayName, IEnumerable<KeyValuePair<Guid, string>> history, IEnumerable<Guid> connected)
        {
            var active = new HashSet<Guid>(connected);
            var known = new Dictionary<Guid, string>();
            foreach (var profile in history)
                if (profile.Key != Guid.Empty) known[profile.Key] = profile.Value;
            Guid Fresh()
            {
                Guid id;
                do { id = Guid.NewGuid(); } while (active.Contains(id) || known.ContainsKey(id));
                return id;
            }
            // Copied configs and simultaneous instances join independently, regardless of their name/key.
            if (active.Contains(requested)) return Fresh();
            if (requested != Guid.Empty && known.ContainsKey(requested)) return requested;
            string name = NormalizeName(displayName);
            var matches = known.Where(p => NormalizeName(p.Value) == name && name.Length != 0).Select(p => p.Key).ToArray();
            if (matches.Length == 1 && !active.Contains(matches[0])) return matches[0];
            return requested == Guid.Empty ? Fresh() : requested;
        }

        private static string NormalizeName(string? value)
        {
            try { return (value ?? "").Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant(); }
            catch (ArgumentException) { return ""; } // unreadable saved name is not a resume hint
        }
    }
}
