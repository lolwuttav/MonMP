using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UniStorm;

namespace MonMP.Plugin.Replication
{
    // Native freeze lifetime only; the save coordinator still owns transaction draining and disk I/O.
    internal sealed class SaveRuntimeGate : IDisposable
    {
        private readonly SharedWorld world;
        private readonly InputActionMap player;
        private readonly UniStormSystem sky;
        private bool playerEnabled, inputRestorePending;
        private float scale;
        private UniStormSystem.EnableFeature flow, realTime;
        public bool Active { get; private set; }
        public bool Frozen { get; private set; }
        internal SaveRuntimeGate(SharedWorld world)
        {
            this.world = world; player = UnityEngine.Object.FindObjectOfType<GameAssets>().inputs.Player.Get();
            sky = UniStormSystem.Instance;
        }
        public void Begin(bool recovery = false)
        {
            if (Active)
            {
                if (recovery) return;
                throw new InvalidOperationException("Save mutation gate is already active.");
            }
            scale = Time.timeScale;
            Active = true;
            try
            {
                playerEnabled = player.enabled || inputRestorePending; inputRestorePending = false;
                flow = sky.TimeFlow; realTime = sky.RealWorldTime;
                world.BlockNewTransactions(true); Enforce();
            }
            catch (Exception ex)
            {
                if (!recovery) { Dispose(); throw; }
                Debug.LogWarning("MonMP save gate recovery: " + ex.Message);
            }
        }
        public void Freeze(bool recovery = false)
        {
            if (!Active || (!recovery && (!world.TransactionsSettled || !world.LocalSaveFlushComplete || world.Profile.HasPendingSaveEffect)))
                throw new InvalidOperationException("Ownership, transactions and personal effects must settle before freezing.");
            Frozen = true; Enforce();
        }
        public void Enforce()
        {
            if (!Active) { ReleaseInput(); return; }
            // Native save animations and ResumeGame may restore controls while collection
            // is still running. Remember that intent before enforcing the temporary gate.
            playerEnabled |= player.enabled;
            player.Disable();
            if (!Frozen) return;
            Time.timeScale = 0; sky.TimeFlow = sky.RealWorldTime = UniStormSystem.EnableFeature.Disabled;
        }
        public void Dispose()
        {
            if (!Active) return;
            Active = Frozen = false;
            try
            {
                Time.timeScale = scale; sky.TimeFlow = flow; sky.RealWorldTime = realTime;
                playerEnabled |= player.enabled;
                inputRestorePending = playerEnabled;
                ReleaseInput();
            }
            finally
            {
                try { world.BlockNewTransactions(false); }
                catch (Exception ex) { Debug.LogWarning("MonMP save gate release: " + ex.Message); }
            }
        }
        private void ReleaseInput()
        {
            if (!inputRestorePending || Active) return;
            // A retained intent belongs to this exact native map, never a replacement scene.
            if (UnityEngine.Object.FindObjectOfType<GameAssets>()?.inputs.Player.Get() != player)
            { inputRestorePending = false; return; }
            var gameplay = UnityEngine.Object.FindObjectOfType<Gameplay>();
            if (gameplay == null || gameplay.Get_isGamePaused || gameplay.Get_IsDead || gameplay.Get_IsSleeping) return;
            player.Enable(); inputRestorePending = false;
        }
    }
}
