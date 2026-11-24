#if MONMP_PERFORMANCE_CAPTURE
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MonMP.Plugin.Replication;
using UnityEngine;
using UnityEngine.Rendering;

namespace MonMP.Plugin.Runtime
{
    // A capture experiment, never a session rule. Only explicit owned avatar body,
    // mannequin and hand-prop mesh roots participate; native held items, copied
    // fishing lines, transported particles and scene-air equipment do not.
    internal sealed class CaptureAvatarRendering : IDisposable
    {
        private const float RefreshSeconds = 1f;
        private sealed class Entry
        {
            internal readonly Renderer Renderer;
            internal readonly bool Original;
            internal Entry(Renderer renderer) { Renderer = renderer; Original = renderer.forceRenderingOff; }
        }
        private sealed class RendererIdentity : IEqualityComparer<Renderer>
        {
            internal static readonly RendererIdentity Instance = new RendererIdentity();
            public bool Equals(Renderer? left, Renderer? right) => ReferenceEquals(left, right);
            public int GetHashCode(Renderer renderer) => RuntimeHelpers.GetHashCode(renderer);
        }
        private readonly List<GameObject> roots = new List<GameObject>();
        private readonly HashSet<GameObject> uniqueRoots = new HashSet<GameObject>();
        private readonly List<Renderer> rendererScratch = new List<Renderer>();
        private readonly List<Animator> animatorScratch = new List<Animator>();
        private readonly HashSet<Renderer> seen = new HashSet<Renderer>();
        private readonly HashSet<Animator> animators = new HashSet<Animator>();
        private readonly Dictionary<Renderer, Entry> indexed = new Dictionary<Renderer, Entry>(RendererIdentity.Instance);
        private readonly List<Entry> entries = new List<Entry>();
        private RemotePlayers? remotes, selfView;
        private float nextRefresh;
        private bool disposed;
        internal bool Suppressed { get; private set; }
        internal string Experiment => Suppressed ? "avatar-body-props-off" :
            entries.Count == 0 ? "baseline" : "avatar-restore-pending";
        internal string Inventory { get; private set; } = "scope=avatarBodyAndHandProps meshesOnly inventory=notSampled";
        internal string Status { get; private set; } = "Avatar body/hand-prop mesh draws: normal (Ctrl+F9)";

        internal void Toggle(RemotePlayers remotePlayers, RemotePlayers localView)
        {
            if (disposed) return;
            if (Suppressed) { Reset(); return; }
            Reset();
            if (entries.Count != 0) return;
            remotes = remotePlayers; selfView = localView;
            try
            {
                Suppressed = true;
                Refresh();
                Status = "EXPERIMENT: avatar body/hand-prop mesh draws OFF (Ctrl+F9 restores); late visuals refresh within 1s";
            }
            catch (Exception)
            {
                Reset();
                Status = "Avatar draw experiment stopped after inventory failure; " + Status;
            }
        }

        // Called after avatar/prop presentation in LateUpdate. No hierarchy scan or
        // status formatting on ordinary frames; the bounded refresh admits late
        // stock-body retries, headgear, spectator proxies and binocular creation.
        internal void Tick(RemotePlayers remotePlayers, RemotePlayers localView)
        {
            if (disposed) return;
            if (!Suppressed)
            {
                if (entries.Count != 0 && Time.unscaledTime >= nextRefresh) Reset();
                return;
            }
            if (!ReferenceEquals(remotes, remotePlayers) || !ReferenceEquals(selfView, localView)) { Reset(); return; }
            try
            {
                if (Time.unscaledTime >= nextRefresh) Refresh();
                else Apply();
            }
            catch (Exception)
            {
                Reset();
                Status = "Avatar draw experiment stopped after renderer failure; " + Status;
            }
        }

        private void Refresh()
        {
            roots.Clear(); uniqueRoots.Clear(); seen.Clear(); animators.Clear();
            remotes!.AppendCaptureAvatarRoots(roots);
            if (!ReferenceEquals(remotes, selfView)) selfView!.AppendCaptureAvatarRoots(roots);
            int rootCount = 0, active = 0, enabled = 0, activeEnabled = 0, skins = 0, shadowCasters = 0, originalOff = 0;
            long vertices = 0;
            foreach (var root in roots)
            {
                if (root == null || !uniqueRoots.Add(root)) continue;
                rootCount++;
                rendererScratch.Clear(); root.GetComponentsInChildren(true, rendererScratch);
                foreach (var renderer in rendererScratch)
                {
                    // Keep all particle and line update/culling paths outside this
                    // diagnostic, including effects owned by a hand-prop clone.
                    if (renderer == null || !(renderer is MeshRenderer || renderer is SkinnedMeshRenderer) || !seen.Add(renderer)) continue;
                    if (!indexed.TryGetValue(renderer, out var entry))
                    {
                        entry = new Entry(renderer); indexed.Add(renderer, entry); entries.Add(entry);
                    }
                    bool isActive = renderer.gameObject.activeInHierarchy;
                    if (isActive) active++;
                    if (renderer.enabled) enabled++;
                    if (isActive && renderer.enabled) activeEnabled++;
                    if (entry.Original) originalOff++;
                    if (renderer.shadowCastingMode != ShadowCastingMode.Off) shadowCasters++;
                    Mesh? mesh;
                    if (renderer is SkinnedMeshRenderer skin) { skins++; mesh = skin.sharedMesh; }
                    else mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                    if (mesh != null) vertices += mesh.vertexCount;
                }
                animatorScratch.Clear(); root.GetComponentsInChildren(true, animatorScratch);
                foreach (var animator in animatorScratch) if (animator != null) animators.Add(animator);
            }
            int animatorEnabled = 0;
            foreach (var animator in animators) if (animator.enabled) animatorEnabled++;
            // Entries removed from the selected owned roots must not retain a
            // diagnostic flag until the next toggle (for example replaced props).
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                if (entry.Renderer != null && seen.Contains(entry.Renderer)) continue;
                // Unity's destroyed-null comparison does not make its managed
                // wrapper a null dictionary key. Identity also survives ID reuse.
                Restore(entry); indexed.Remove(entry.Renderer!); entries.RemoveAt(i);
            }
            Inventory = "scope=avatarBodyAndHandProps meshesOnly snapshot=lastRefresh lateVisualWarmupSeconds=1 roots=" + rootCount + " renderers=" + seen.Count +
                " active=" + active + " enabled=" + enabled + " activeEnabled=" + activeEnabled + " skins=" + skins +
                " vertexInstances=" + vertices + " shadowConfigured=" + shadowCasters + " originalOff=" + originalOff +
                " animators=" + animators.Count + " animatorEnabled=" + animatorEnabled;
            nextRefresh = Time.unscaledTime + RefreshSeconds;
            Apply();
        }

        private void Apply()
        {
            foreach (var entry in entries)
                if (entry.Renderer != null && !entry.Renderer.forceRenderingOff) entry.Renderer.forceRenderingOff = true;
        }

        private static void Restore(Entry entry)
        {
            if (entry.Renderer != null && entry.Renderer.forceRenderingOff != entry.Original)
                entry.Renderer.forceRenderingOff = entry.Original;
        }

        internal void Reset()
        {
            int failed = 0;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                try { Restore(entry); indexed.Remove(entry.Renderer); entries.RemoveAt(i); }
                catch (Exception) { failed++; }
            }
            // A live renderer that rejected restoration retains its original value
            // for a bounded retry; another failing entry must not stop the rest.
            roots.Clear(); uniqueRoots.Clear();
            rendererScratch.Clear(); animatorScratch.Clear(); seen.Clear(); animators.Clear();
            remotes = selfView = null; Suppressed = false;
            nextRefresh = failed == 0 ? 0 : Time.unscaledTime + RefreshSeconds;
            Status = failed == 0 ? "Avatar body/hand-prop mesh draws: normal (Ctrl+F9)" :
                "Avatar draw experiment stopped; renderer flag restoration failures=" + failed;
        }

        public void Dispose() { if (disposed) return; Reset(); disposed = true; }
    }
}
#endif
