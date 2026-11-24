using System;
using System.IO;
using System.Linq;
using MonMP.Core.World;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class RuntimeInteractionAudit
    {
        private readonly InteractionAudit audit;
        private readonly string path;
        private readonly Func<bool> multiplayer, strict;
        internal bool? TestStrictOverride;
        internal InteractionAudit Model => audit;
        internal RuntimeInteractionAudit(InteractionCoverage coverage, string directory, Func<bool> multiplayer, Func<bool> strict)
        { audit = new InteractionAudit(coverage); path = Path.Combine(directory, "pending-interactions.tsv"); this.multiplayer = multiplayer; this.strict = strict; Flush(); }
        internal bool Allow(Interactable item, string action)
        {
            if (item == null || !multiplayer()) return true;
            int before = audit.Count;
            bool allowed = audit.Allow(item.GetType().FullName!, action, true, TestStrictOverride ?? strict());
            if (audit.Count != before) Flush();
            return allowed;
        }
        internal void Flush() => File.WriteAllLines(path, new[] { "type\taction\tdisposition\tobserved\tblocked" }.Concat(audit.Entries.Select(e =>
            e.Type + "\t" + e.Action + "\t" + e.Disposition + "\t" + e.Observed + "\t" + e.Blocked)));
    }
}
