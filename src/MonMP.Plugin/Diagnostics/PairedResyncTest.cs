using System;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Runs only in explicitly isolated paired tests. Damage Unity presentation
    // without changing the DTO ledger, then prove equal-revision snapshots apply.
    internal sealed class PairedResyncTest
    {
        private readonly MonMPPlugin plugin;
        private readonly ulong entity;
        private readonly uint revision;
        private readonly int baseline;
        private int phase;
        private bool waiting;
        private float next;
        internal PairedResyncTest(MonMPPlugin plugin, ulong entity)
        {
            this.plugin = plugin; this.entity = entity;
            revision = plugin.Lights!.TestRecord(entity).Revision;
            baseline = plugin.World!.Channel.RepairsApplied;
        }
        internal bool Tick()
        {
            var channel = plugin.World!.Channel; var lights = plugin.Lights!;
            if (waiting)
            {
                if (channel.RepairsApplied == baseline + phase) return false;
                if (channel.RepairsApplied != baseline + phase + 1 || !lights.TestSynchronized || lights.TestRecord(entity).Revision != revision)
                    throw new InvalidOperationException("Scoped snapshot failed to repair equal-revision Unity light state.");
                phase++; waiting = false; next = Time.unscaledTime + 1.1f;
                if (phase == 3) return true;
            }
            if (Time.unscaledTime < next) return false;
            lights.TestDamageReplica(entity);
            if (lights.TestSynchronized) throw new InvalidOperationException("Isolated test did not introduce replica drift.");
            var scope = phase == 0 ? SnapshotScope.ForEntity(1, entity) : phase == 1 ? SnapshotScope.Section(1) : SnapshotScope.World;
            if (!channel.RequestResync(scope, "Paired test: deliberate Unity light drift")) return false;
            waiting = true; return false;
        }
    }
}
