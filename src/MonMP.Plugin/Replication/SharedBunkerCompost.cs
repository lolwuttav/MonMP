using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The electric machine is distinct from the cottage's manual machine. The
    // host alone consumes input, converts it hourly and creates fertilizer packs.
    internal sealed class SharedBunkerCompost : IDisposable
    {
        private static readonly FieldInfo Compost = AccessTools.Field(typeof(Bunker_CompostMachine), "Compost");
        private static readonly FieldInfo Fertilizer = AccessTools.Field(typeof(Bunker_CompostMachine), "Fertilizer");
        private static readonly MethodInfo UpdateVisual = AccessTools.Method(typeof(Bunker_CompostMachine), "UpdateVisual");
        private readonly bool host;
        private readonly Bunker_CompostMachine? machine;
        private readonly BunkerCompostState original;
        private BunkerCompostState state;
        private NetworkSession? session;
        private WorldChannel? channel;
        private float nextCheck;
        private bool applied, disposed;

        internal SharedBunkerCompost(Gameplay gameplay, bool host)
        {
            this.host = host; machine = FindMachine(gameplay);
            original = state = Capture();
        }
        internal static Bunker_CompostMachine? FindMachine(Gameplay gameplay) =>
            gameplay.home != null && gameplay.home.homeBunker != null ? gameplay.home.homeBunker.BunkerCompostMachine : null;
        internal bool Handles(Bunker_CompostMachine candidate) => candidate != null && candidate == machine;
        internal bool BlocksGuest(Bunker_CompostMachine candidate) => !host && !disposed &&
            session?.IsRunning == true && Handles(candidate);
        private BunkerCompostState Capture() => machine == null ? default :
            new BunkerCompostState(machine.Get_Compost, machine.Get_Fertilizer);
        private static bool Same(BunkerCompostState a, BunkerCompostState b) =>
            a.Compost == b.Compost && a.Fertilizer == b.Fertilizer;
        internal IEnumerable<WorldRecord> InitialRecords() => new[]
        { new WorldRecord(BunkerCompostState.Entity, 1, BunkerCompostState.Adapter, state.Encode()) };

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (!host && records.TryGetValue(BunkerCompostState.Entity, out var record)) Apply(record);
        }
        private void Changed(WorldRecord record)
        {
            if (!host && record.Id == BunkerCompostState.Entity) Apply(record);
        }
        private void Apply(WorldRecord record)
        {
            if (record.Adapter != BunkerCompostState.Adapter) throw new InvalidOperationException("Unexpected bunker compost adapter.");
            var next = BunkerCompostState.Decode(record.Payload);
            if (machine != null && (next.Compost > machine.Capacity || next.Fertilizer > machine.Capacity))
                throw new InvalidOperationException("Bunker compost contents exceed native capacity.");
            state = next; applied = true;
            ApplyNative(next);
        }
        private void ApplyNative(BunkerCompostState next)
        {
            if (machine == null) return;
            // LoadFromSave rounds to whole percentages. Directly mirror the
            // host's fields so partial seed/basket inputs retain their precision.
            Compost.SetValue(machine, next.Compost); Fertilizer.SetValue(machine, next.Fertilizer);
            UpdateVisual.Invoke(machine, null);
        }
        internal void Update()
        {
            if (disposed || session?.IsRunning != true || channel?.Ready != true || machine == null || Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + (host ? 0.1f : 1f);
            var current = Capture();
            if (host)
            {
                if (Same(current, state)) return;
                channel.Publish(BunkerCompostState.Entity, current.Encode()); state = current;
            }
            else if (applied && !Same(current, state)) ApplyNative(state);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            try { if (!host) ApplyNative(original); }
            finally { session = null; channel = null; }
        }
    }
}
