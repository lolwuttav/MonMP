using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed class SharedEconomy : IDisposable
    {
        private static readonly FieldInfo cash = AccessTools.Field(typeof(Cash_Manager), "cash");
        private static readonly FieldInfo payments = AccessTools.Field(typeof(Cash_Manager), "CashPerSunday");
        private static readonly FieldInfo CashSound = AccessTools.Field(typeof(Cash_Manager), "sound_General_Cash");
        private static readonly FieldInfo CoroutineRunning = AccessTools.Field(typeof(UI_ShowCash), "CoroutineRunning");
        private static readonly FieldInfo CacheAdded = AccessTools.Field(typeof(UI_ShowCash), "Cache_cashAdded");
        private readonly Cash_Manager manager;
        private readonly UI_ShowCash ui;
        private readonly bool host;
        private readonly EconomyState original;
        private EconomyState current;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool disposed, initialized;
        private float nextCapture;
        public bool BlocksLocalChanges => !host && session != null && !disposed;
        internal bool CanAffordCents(int cents) => !disposed && initialized && cents >= 0 && current.CashCents >= cents;

#if MONMP_DEVELOPMENT
        internal EconomyState TestState => current;


        internal bool TestRestored => Capture().Encode().SequenceEqual(original.Encode());
#endif

        public SharedEconomy(bool host, string diagnostics)
        {
            this.host = host; manager = UnityEngine.Object.FindObjectOfType<Cash_Manager>(); ui = UnityEngine.Object.FindObjectOfType<UI_ShowCash>();
            if (manager == null || ui == null) throw new InvalidOperationException("Wait until the cash manager and display initialize.");
            original = current = Capture();

#if MONMP_DEVELOPMENT
            File.WriteAllText(Path.Combine(diagnostics, "shared-economy.txt"), "MONMP_ECONOMY_REGISTERED\nEntity=" + EconomyState.Entity.ToString("X16") + "\nCashCents=" + current.CashCents);
#endif

        }
        private EconomyState Capture() => new EconomyState(manager.GetCash, manager.GetWeekCash);
        public IEnumerable<WorldRecord> InitialRecords() { yield return new WorldRecord(EconomyState.Entity, 1, EconomyState.Adapter, original.Encode()); }
        public void Attach(NetworkSession session, WorldChannel channel)
        { this.session = session; this.channel = channel; channel.SnapshotApplied += Snapshot; channel.Changed += Changed; }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        { if (!host && records.TryGetValue(EconomyState.Entity, out var record)) Apply(record); }
        private void Changed(WorldRecord record) { if (!host && record.Adapter == EconomyState.Adapter) Apply(record); }
        private void Apply(WorldRecord record)
        {
            if (record.Id != EconomyState.Entity || record.Adapter != EconomyState.Adapter) throw new InvalidDataException("Unknown economy record.");
            var next = EconomyState.Decode(record.Payload);
            // A live change after the first snapshot animates the colored cash delta.
            // Native register cues travel individually; sampled balances can coalesce transactions.
            int delta = next.CashCents - current.CashCents;
            current = next;
            if (initialized && delta != 0) Animate(next, delta);
            else ApplyNative(next);
            initialized = true;
        }
        private void ApplyNative(EconomyState state)
        {
            cash.SetValue(manager, state.CashCents); payments.SetValue(manager, state.SundayPayments.ToArray());
            // Native cash animations cache old text. Cancel them before replacing the displayed ledger.
            ui.StopAllCoroutines();
            CoroutineRunning.SetValue(ui, false);
            CacheAdded.SetValue(ui, 0f);
            ui.UpdateCash(state.CashCents);
        }
        private void Animate(EconomyState state, int deltaCents)
        {
            // A guest's own validated purchase may already have run the native local debit animation.
            bool alreadyShown = manager.GetCash == state.CashCents;
            cash.SetValue(manager, state.CashCents); payments.SetValue(manager, state.SundayPayments.ToArray());
            if (alreadyShown)
            {
                if ((bool)CoroutineRunning.GetValue(ui)) ui.UpdateCash(state.CashCents, 0f); else ui.UpdateCash(state.CashCents);
                return;
            }
            ui.UpdateCash(state.CashCents, deltaCents / 100f);
        }
        internal void RelayCashCue(bool spent)
        {
            if (!host || disposed || SharedPresentationEvents.Replaying) return;
            MonMPPlugin.Instance?.World?.Events?.HostAnnounce(PresentationKind.CashCue, spent ? (byte)0 : (byte)1,
                EconomyState.Entity, Vector3.zero, 0, 0, false);
        }
        internal void PlayCashCue(PresentationEvent e)
        {
            if (host || disposed || e.Target != EconomyState.Entity || e.Variant > 1) return;
            try
            {
                var sound = CashSound.GetValue(manager);
                // Native cash feedback belongs to this player's shared wallet UI. Delta
                // application otherwise spatializes it at the unrelated host's avatar.
                using (SpatialSounds.Local())
                    if (sound != null) AccessTools.Method(sound.GetType(), e.Variant == 0 ? "Spent" : "Received")?.Invoke(sound, null);
            }
            catch (Exception ex) { Debug.LogWarning("MonMP cash sound failed: " + ex.Message); }
        }
        public void Update()
        {
            if (disposed || session?.IsRunning != true) return;
            if (host)
            {
                if (Time.unscaledTime < nextCapture) return;
                nextCapture = Time.unscaledTime + 0.2f; current = Capture(); channel!.Publish(EconomyState.Entity, current.Encode());
            }
            else if (initialized && (manager.GetCash != current.CashCents || !manager.GetWeekCash.SequenceEqual(current.SundayPayments)))
            {
                // Keep a running native delta animation; only correct the ledger under it.
                cash.SetValue(manager, current.CashCents); payments.SetValue(manager, current.SundayPayments.ToArray());
                if ((bool)CoroutineRunning.GetValue(ui)) ui.UpdateCash(current.CashCents, 0f); else ui.UpdateCash(current.CashCents);
            }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            if (!host && manager != null && ui != null) ApplyNative(original);
            session = null; channel = null;
        }
    }
}
