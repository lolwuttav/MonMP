using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MonMP.Plugin.Replication
{
    // Keep the complete native source set separate from identity validation. Native
    // Start enrolls new usable seats; scene changes/forced clicks refresh everything,
    // with a bounded fallback for inactive or third-party-created components.
    internal sealed class FurnitureSeatSources : IDisposable
    {
        private const float FallbackSeconds = 10f;
        private static event Action<Component>? started;
        private readonly List<Furniture_Chair> chairs = new List<Furniture_Chair>();
        private readonly List<Furniture_Decoration_Seat> decorationSeats = new List<Furniture_Decoration_Seat>();
        private readonly HashSet<Furniture_Chair> knownChairs = new HashSet<Furniture_Chair>(ReferenceComparer<Furniture_Chair>.Instance);
        private readonly HashSet<Furniture_Decoration_Seat> knownDecorations = new HashSet<Furniture_Decoration_Seat>(ReferenceComparer<Furniture_Decoration_Seat>.Instance);
        private Furniture_Chair[] chairSnapshot = Array.Empty<Furniture_Chair>();
        private Furniture_Decoration_Seat[] decorationSnapshot = Array.Empty<Furniture_Decoration_Seat>();
        // The periodic fallback walks chairs on one seat scan and decorations on the next;
        // sources that start in between are not in the staged chair walk.
        private Furniture_Chair[]? fallbackChairs;
        private readonly List<Component> startedDuringFallback = new List<Component>();
        private bool invalidated = true, snapshotDirty, disposed;
        private float nextFallback;
        internal IReadOnlyList<Furniture_Chair> Chairs { get { Snapshot(); return chairSnapshot; } }
        internal IReadOnlyList<Furniture_Decoration_Seat> DecorationSeats { get { Snapshot(); return decorationSnapshot; } }

        internal FurnitureSeatSources()
        {
            started += NativeStarted;
            SceneManager.sceneLoaded += SceneLoaded;
            SceneManager.sceneUnloaded += SceneUnloaded;
        }

        internal static void NotifyStarted(Component component) => started?.Invoke(component);
        internal void Invalidate() => invalidated = true;
        private void SceneLoaded(Scene scene, LoadSceneMode mode) => Invalidate();
        private void SceneUnloaded(Scene scene) => Invalidate();

        private void NativeStarted(Component component)
        {
            try { Enroll(component); if (fallbackChairs != null && component != null) startedDuringFallback.Add(component); }
            // Discovery observes native initialization; it must never turn a
            // successful native Start into a failed gameplay initialization.
            catch (Exception) { Invalidate(); }
        }

        private void Snapshot()
        {
            if (!snapshotDirty) return;
            // A synchronous native Start during seat identity enrollment may
            // add a source. The current complete pass keeps its detached view.
            chairSnapshot = chairs.ToArray(); decorationSnapshot = decorationSeats.ToArray();
            snapshotDirty = false;
        }

        private void Enroll(Component component)
        {
            if (disposed || component == null || !component.gameObject.scene.IsValid()) return;
            if (component is Furniture_Chair chair && knownChairs.Add(chair)) { chairs.Add(chair); snapshotDirty = true; }
            else if (component is Furniture_Decoration_Seat decoration && knownDecorations.Add(decoration)) { decorationSeats.Add(decoration); snapshotDirty = true; }
        }

        internal void Refresh(float now, bool force = false)
        {
            if (disposed) return;
            if (force || invalidated) Enumerate(now, null);
            else if (now >= nextFallback)
            {
                // Each native registry walk costs several milliseconds; the periodic
                // fallback spreads its two walks over consecutive seat scans.
                if (fallbackChairs == null)
                {
#if MONMP_PERFORMANCE_CAPTURE
                    using (Runtime.CaptureDetail.Measure("FurnitureSeats.Enumerate"))
#endif
                    { startedDuringFallback.Clear(); fallbackChairs = UnityEngine.Object.FindObjectsOfType<Furniture_Chair>(true); }
                }
                else Enumerate(now, fallbackChairs);
            }
            // Destroyed components do not invoke native Start again; retire those
            // references without asking Unity to enumerate the whole scene.
            for (int i = chairs.Count - 1; i >= 0; i--)
                if (chairs[i] == null)
                { knownChairs.Remove(chairs[i]); chairs.RemoveAt(i); snapshotDirty = true; }
            for (int i = decorationSeats.Count - 1; i >= 0; i--)
                if (decorationSeats[i] == null)
                { knownDecorations.Remove(decorationSeats[i]); decorationSeats.RemoveAt(i); snapshotDirty = true; }
        }

        private void Enumerate(float now, Furniture_Chair[]? stagedChairs)
        {
#if MONMP_PERFORMANCE_CAPTURE
            using (Runtime.CaptureDetail.Measure("FurnitureSeats.Enumerate"))
#endif
            {
                // Acquire both arrays before replacing the source set. A failed
                // enumeration must not turn a partial pass into a complete catalog.
                var foundChairs = stagedChairs ?? UnityEngine.Object.FindObjectsOfType<Furniture_Chair>(true);
                var foundDecorations = UnityEngine.Object.FindObjectsOfType<Furniture_Decoration_Seat>(true);
                chairs.Clear(); decorationSeats.Clear(); knownChairs.Clear(); knownDecorations.Clear();
                snapshotDirty = true;
                foreach (var chair in foundChairs) Enroll(chair);
                foreach (var decoration in foundDecorations) Enroll(decoration);
                if (stagedChairs != null) foreach (var component in startedDuringFallback) Enroll(component);
                fallbackChairs = null; startedDuringFallback.Clear();
                nextFallback = now + FallbackSeconds;
                invalidated = false;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            started -= NativeStarted;
            SceneManager.sceneLoaded -= SceneLoaded;
            SceneManager.sceneUnloaded -= SceneUnloaded;
            chairs.Clear(); decorationSeats.Clear(); knownChairs.Clear(); knownDecorations.Clear();
            fallbackChairs = null; startedDuringFallback.Clear();
            chairSnapshot = Array.Empty<Furniture_Chair>(); decorationSnapshot = Array.Empty<Furniture_Decoration_Seat>(); snapshotDirty = false;
        }
    }
}
