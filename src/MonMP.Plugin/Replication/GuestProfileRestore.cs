using System;
using MonMP.Core.Persistence;
using MonMP.Core.Session;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MonMP.Plugin.Replication
{
    internal sealed class GuestProfileRestore : IDisposable
    {
        private readonly SharedWorld world;
        private readonly NetworkSession session;
        private readonly ProfileRestoreChannel channel;
        private readonly InputActionMap player;
        private readonly bool host;
        private bool playerEnabled, released, disposed, initialized;
        private Guid epoch;
        private float deadline = float.MaxValue;
        private PlayerProfileState? baseline;
        public bool Ready => host || initialized;
        internal GuestProfileRestore(SharedWorld world, NetworkSession session)
        {
            this.world = world; this.session = session; host = world.Saves != null;
            channel = new ProfileRestoreChannel(session, world.Channel, profile => world.Saves!.FindProfile(profile));
            player = UnityEngine.Object.FindObjectOfType<GameAssets>().inputs.Player.Get(); playerEnabled = player.enabled;
            if (!host) { CaptureBaseline(); player.Disable(); }
        }
        public void Update()
        {
            if (session.Epoch != Guid.Empty) epoch = session.Epoch;
            if (host || disposed || Ready) { if (!host && Ready) ReleaseInput(); return; }
            playerEnabled |= player.enabled; player.Disable();
            CaptureBaseline();
            // 30 s once the world is in: a slow sync (big save, busy PC) is the sync's business, not this one's.
            if (world.Channel.FullySynchronized && deadline == float.MaxValue) deadline = Time.unscaledTime + 30;
            // Optional personal restoration cannot keep a synchronized friend frozen
            // behind a readiness/settlement gate that never clears.
            if (world.Channel.FullySynchronized && Time.unscaledTime > deadline)
            {
                channel.UseLocalFallback();
                initialized = true;
                Debug.LogWarning("MonMP saved player profile was not ready in time; continuing with local personal state.");
                ReleaseInput();
                return;
            }
            if (!world.Channel.FullySynchronized || !world.TransactionsSettled || !world.Profile.Ready) return;
            try
            {
                channel.TryApply(message =>
                {
                    Apply(MonMPPlugin.Instance?.FindReconnectProfile(epoch) ?? message.State);
                });
                if (channel.Phase == ProfileRestorePhase.Absent || channel.Phase == ProfileRestorePhase.Failed)
                {
                    // A first visit still borrows the offline scene's personal state.
                    // Capture it even when there is no saved or reconnect profile to apply.
                    baseline ??= world.Profile.Capture();
                    var local = MonMPPlugin.Instance?.FindReconnectProfile(epoch);
                    if (local != null) Apply(local);
                }
                initialized = channel.Phase == ProfileRestorePhase.Applied || channel.Phase == ProfileRestorePhase.Absent || channel.Phase == ProfileRestorePhase.Failed;
                if (Ready) ReleaseInput();
            }
            catch (Exception ex)
            {
                channel.UseLocalFallback();
                // Application can partially change fields before native presentation fails. Restore
                // the captured local values immediately; failed capture means no application ran.
                try { if (baseline != null) world.Profile.RestoreLocalBaseline(baseline); }
                catch (Exception rollback) { Debug.LogWarning("MonMP local profile presentation could not refresh: " + rollback.Message); }
                Debug.LogWarning("MonMP saved player profile was unavailable; continuing with local personal state: " + ex.Message);
                initialized = true;
                ReleaseInput();
            }
        }
        private void Apply(PlayerProfileState state)
        {
            baseline ??= world.Profile.Capture();
            world.Profile.Restore(state);
        }
        private void CaptureBaseline()
        {
            if (baseline != null || !world.Profile.CanCapture) return;
            // A read-only snapshot is safe before transaction/native application readiness.
            // Keep it before optional metadata waits, and before fallback admits gameplay.
            try { baseline = world.Profile.Capture(); }
            catch (Exception) { /* Optional native metadata can recover before admission. */ }
        }
        private void ReleaseInput()
        {
            if (released) return;
            if (UnityEngine.Object.FindObjectOfType<GameAssets>()?.inputs.Player.Get() != player) { released = true; return; }
            var gameplay = UnityEngine.Object.FindObjectOfType<Gameplay>();
            if (gameplay != null && !gameplay.Get_isGamePaused && !gameplay.Get_IsDead && !gameplay.Get_IsSleeping && !world.SaveGate.Active)
            { if (playerEnabled) player.Enable(); released = true; }
            world.SaveGate.Enforce();
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true; channel.Dispose();
            try
            {
                // Keep already-applied effects newer than the host's periodic sample. The
                // host epoch prevents applying this local state to a reloaded/new world.
                if (!host && initialized && baseline != null && epoch != Guid.Empty && world.Profile.Ready)
                    MonMPPlugin.Instance?.RememberReconnectProfile(epoch, world.Profile.Capture());
            }
            finally
            {
                try { if (baseline != null) world.Profile.RestoreLocalBaseline(baseline); }
                finally { if (!host) ReleaseInput(); }
            }
        }
    }
}
