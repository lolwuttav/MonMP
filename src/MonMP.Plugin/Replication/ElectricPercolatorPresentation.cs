using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Never runs BrewingCoffee, LoadFromSave, basket mutation or mug creation. Native save values
    // are still applied by SharedItems; this owner only supplies the otherwise missing busy/VFX.
    internal sealed class ElectricPercolatorPresentation : IDisposable
    {
        private sealed class NativeStamp { public bool Ready, HasStart; public float StartedAt; public uint Version; }
        private static readonly ConditionalWeakTable<Item_CoffeePercolator, NativeStamp> stamps = new ConditionalWeakTable<Item_CoffeePercolator, NativeStamp>();
        private static readonly FieldInfo Brewing = AccessTools.Field(typeof(Item_CoffeePercolator), "Coroutine_BrewingCoffee")!;
        private static readonly FieldInfo Smoke = AccessTools.Field(typeof(Item_CoffeePercolator), "VFX_Smoke")!;
        private static readonly FieldInfo Cap = AccessTools.Field(typeof(Item_CoffeePercolator), "Percolator_Cap")!;
        private static readonly FieldInfo Beans = AccessTools.Field(typeof(Item_CoffeePercolator), "Visual_CoffeeBeans")!;
        private readonly Item_CoffeePercolator child;
        private readonly ParticleSystem smoke;
        private readonly Transform cap, beans;
        private readonly bool originalBusy, originalBeansVisible, originalSmokeActive, originalSmokePlaying;
        private readonly Vector3 originalCapEuler, originalBeansScale, originalSmokePosition, originalSmokeScale;
        private readonly ParticleSystem.MinMaxCurve originalGravity;
        private AudioSource? audio;
        private bool disposed, renderedBrew, known;
        private uint sequence;

        internal static void NativeStarted(Item_CoffeePercolator child) => stamps.GetValue(child, _ => new NativeStamp()).Ready = true;
        internal static void NativeBrewCreated(Item_CoffeePercolator child)
        { var stamp = stamps.GetValue(child, _ => new NativeStamp()); stamp.StartedAt = Time.time; stamp.HasStart = true; stamp.Version = stamp.Version == uint.MaxValue ? 1 : stamp.Version + 1; }
        internal static bool Ready(Item_CoffeePercolator child) => stamps.TryGetValue(child, out var stamp) && stamp.Ready;
        internal static bool TryStartTime(Item_CoffeePercolator child, out float started)
        { started = 0; if (!stamps.TryGetValue(child, out var stamp) || !stamp.HasStart) return false; started = stamp.StartedAt; return true; }
        internal static uint StartVersion(Item_CoffeePercolator child) => stamps.TryGetValue(child, out var stamp) ? stamp.Version : 0;

        // The coroutine's scaled waits can overshoot by a frame. Seek its current literal visual
        // phase rather than treating the nominal schedule as evidence that a later phase began.
        internal static float NativeElapsed(Item_CoffeePercolator child, float nominal, float duration, ref bool sawSteam)
        {
            var beans = Beans.GetValue(child) as Transform ?? throw new InvalidOperationException("Native percolator beans are unavailable.");
            var cap = Cap.GetValue(child) as Transform ?? throw new InvalidOperationException("Native percolator cap is unavailable.");
            var smoke = Smoke.GetValue(child) as ParticleSystem ?? throw new InvalidOperationException("Native percolator smoke is unavailable.");
            if (beans.localScale.y < 1) return 4 * Mathf.Clamp01(beans.localScale.y);
            float angle = cap.localEulerAngles.x; if (angle > 180) angle -= 360;
            if (angle <= -99.99f) return Math.Max(4, Math.Min(4.4999f, nominal));
            if (angle < -0.01f) return 4.5f + Mathf.Clamp01((angle + 100) / 100);
            if (smoke.isPlaying)
            {
                sawSteam = true;
                float progress = Mathf.Clamp01((smoke.transform.localScale.y - 0.1f) / 0.3f);
                if (progress < 0.9999f) return 8 + 11 * progress;
                return Math.Max(19, Math.Min(duration - 1.0001f, nominal));
            }
            // A session can attach during the final native one-second tail, after Play/Stop.
            if (!sawSteam && smoke.gameObject.activeSelf && nominal >= duration - 1) sawSteam = true;
            if (!sawSteam) return Math.Max(5.5f, Math.Min(7.9999f, nominal));
            return Math.Max(duration - 1, Math.Min(duration, nominal));
        }

        internal ElectricPercolatorPresentation(Item_CoffeePercolator child)
        {
            this.child = child;
            smoke = Smoke.GetValue(child) as ParticleSystem ?? throw new InvalidOperationException("Native percolator smoke is unavailable.");
            cap = Cap.GetValue(child) as Transform ?? throw new InvalidOperationException("Native percolator cap is unavailable.");
            beans = Beans.GetValue(child) as Transform ?? throw new InvalidOperationException("Native percolator beans are unavailable.");
            originalBusy = Brewing.GetValue(child) is true; originalCapEuler = cap.localEulerAngles;
            originalBeansVisible = beans.gameObject.activeSelf; originalBeansScale = beans.localScale;
            originalSmokeActive = smoke.gameObject.activeSelf; originalSmokePlaying = smoke.isPlaying;
            originalSmokePosition = smoke.transform.localPosition; originalSmokeScale = smoke.transform.localScale;
            originalGravity = smoke.main.gravityModifier;
        }

        internal void Apply(ElectricPercolatorBrewState state, bool cue)
        {
            if (disposed || child == null) return;
            bool newBrew = !known || state.Sequence != sequence;
            if (newBrew || !state.Active) StopAudio();
            if (state.Active && cue && newBrew) PlayAudio(state.Elapsed);
            sequence = state.Sequence; known = true;
            Render(state);
        }

        internal void Render(ElectricPercolatorBrewState state)
        {
            if (disposed || child == null) return;
            Brewing.SetValue(child, state.Active);
            if (!state.Active)
            {
                child.UpdateVisual();
                if (renderedBrew)
                {
                    smoke.Stop(); smoke.transform.localPosition = originalSmokePosition;
                    smoke.transform.localScale = Vector3.one * 0.4f;
                    var main = smoke.main; main.gravityModifier = -0.03f;
                }
                renderedBrew = false; return;
            }
            renderedBrew = true;
            float elapsed = state.Elapsed;
            beans.gameObject.SetActive(true);
            beans.localScale = new Vector3(1, Mathf.Clamp01(elapsed / 4), 1);
            cap.localEulerAngles = new Vector3(Mathf.Lerp(-100, 0, Mathf.Clamp01((elapsed - 4.5f) / 1)), 0, 0);
            bool steamActive = elapsed >= 8, steamPlaying = steamActive && elapsed < state.Duration - 1;
            smoke.gameObject.SetActive(steamActive);
            if (steamPlaying && !smoke.isPlaying) smoke.Play();
            else if (!steamPlaying && smoke.isPlaying) smoke.Stop();
            float steam = Mathf.Clamp01((elapsed - 8) / 11);
            smoke.transform.localScale = Vector3.one * Mathf.Lerp(0.1f, 0.4f, steam);
            smoke.transform.localPosition = originalSmokePosition + new Vector3(0, 0.05f * steam, 0);
            var module = smoke.main; module.gravityModifier = Mathf.Lerp(-0.01f, -0.03f, steam);
        }

        private void PlayAudio(float elapsed)
        {
            var clip = Singleton<SOUND_Manager>.i?.Coffee_Brewing;
            if (clip == null || elapsed >= clip.length || !child.gameObject.activeInHierarchy) return;
            audio = child.gameObject.AddComponent<AudioSource>();
            audio.playOnAwake = false; audio.loop = false; audio.clip = clip; audio.spatialBlend = 1;
            audio.dopplerLevel = 0; audio.rolloffMode = AudioRolloffMode.Linear; audio.volume = 0.2f; audio.priority = 20; audio.maxDistance = 30;
            var assets = Singleton<GameAssets>.i;
            audio.outputAudioMixerGroup = assets?.AUDIO_EffectMixer;
            if (assets?.AudioSourceCustomCurve != null)
            { audio.SetCustomCurve(AudioSourceCurveType.CustomRolloff, assets.AudioSourceCustomCurve); audio.rolloffMode = AudioRolloffMode.Custom; }
            // Join/repair never calls this; a live cue begins at its current point, with no historical prefix.
            audio.time = Math.Max(0, elapsed); audio.Play();
        }

        private void StopAudio()
        { if (audio == null) return; audio.Stop(); UnityEngine.Object.Destroy(audio); audio = null; }

        public void Dispose()
        {
            if (disposed) return; disposed = true; StopAudio();
            if (child == null) return;
            Brewing.SetValue(child, originalBusy); cap.localEulerAngles = originalCapEuler;
            beans.localScale = originalBeansScale; beans.gameObject.SetActive(originalBeansVisible);
            smoke.Stop(); smoke.transform.localPosition = originalSmokePosition; smoke.transform.localScale = originalSmokeScale;
            var main = smoke.main; main.gravityModifier = originalGravity;
            smoke.gameObject.SetActive(originalSmokeActive); if (originalSmokePlaying) smoke.Play();
        }
    }
}
