using System;
using System.Collections.Generic;
using System.Linq;

namespace MonMP.Core.Persistence
{
    // A save keeps a profile for everyone who has joined, capped at 512. Before the cap is reached the
    // least recently seen profiles are dropped; connected players were just stamped and always stay.
    public static class ProfilePruning
    {
        public const int Keep = 480;

        public static IReadOnlyList<SidecarProfile> KeepRecent(IEnumerable<SidecarProfile> profiles, int keep = Keep,
            IEnumerable<Guid>? protectedProfiles = null)
        {
            var list = profiles.ToList();
            if (list.Count <= keep) return list;
            var protectedIds = new HashSet<Guid>(protectedProfiles ?? Array.Empty<Guid>());
            return list.OrderByDescending(p => protectedIds.Contains(p.Id)).ThenByDescending(p => p.LastSeenUtc)
                .ThenBy(p => p.Id).Take(keep).ToList();
        }
    }
}
