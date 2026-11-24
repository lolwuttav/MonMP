using System;
using System.Collections.Generic;
using System.Linq;

namespace MonMP.Core.World
{
    public sealed class InteractionAuditEntry
    {
        public string Type { get; }
        public string Action { get; }
        public string Disposition { get; }
        public int Observed { get; internal set; }
        public int Blocked { get; internal set; }
        internal InteractionAuditEntry(string type, string action, string disposition)
        { Type = type; Action = action; Disposition = disposition; }
    }
    public sealed class InteractionAudit
    {
        private readonly InteractionCoverage coverage;
        private readonly Dictionary<string, InteractionAuditEntry> entries = new Dictionary<string, InteractionAuditEntry>();
        public const int MaxEntries = 512;
        public int Count => entries.Count;
        public IReadOnlyList<InteractionAuditEntry> Entries => entries.Values.OrderBy(e => e.Type, StringComparer.Ordinal).ThenBy(e => e.Action, StringComparer.Ordinal).ToArray();
        public InteractionAudit(InteractionCoverage coverage) => this.coverage = coverage;
        public bool Allow(string type, string action, bool multiplayer, bool strict)
        {
            if (!multiplayer) return true;
            string disposition = coverage.Entries.TryGetValue(type, out var row) ? row[2] : "unlisted";
            if (disposition != "pending" && disposition != "unlisted") return true;
            string key = type + "\n" + action;
            if (!entries.TryGetValue(key, out var entry) && entries.Count < MaxEntries)
            { entry = new InteractionAuditEntry(type, action, disposition); entries.Add(key, entry); }
            if (entry != null)
            {
                if (entry.Observed < int.MaxValue) entry.Observed++;
                if (strict && entry.Blocked < int.MaxValue) entry.Blocked++;
            }
            // Full diagnostics must never change enforcement.
            return !strict;
        }
    }
}
