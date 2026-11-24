using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Native bunker lamps are shadowless 18 m point lights. Their lamp-only parent
    // starts active and the native exit trigger is the only outside visibility gate.
    // Loading, teleporting or spectating can bypass that trigger. Keep visibility
    // local to the actual view without changing the replicated switch/power state.
    internal sealed class BunkerLightVisibility : IDisposable
    {
        private sealed class Group
        {

internal readonly Furniture_Light[] Fixtures;

            internal readonly float EntranceCeiling;
            internal Group(Furniture_Light[] fixtures, float entranceCeiling)
            { Fixtures = fixtures; EntranceCeiling = entranceCeiling; }
        }

        private readonly List<Group> groups = new List<Group>();
        private readonly Dictionary<Light, int> masks = new Dictionary<Light, int>();
        private Camera? currentCamera;
        private float nextRefresh;
        private bool disposed;

        internal BunkerLightVisibility(IEnumerable<Furniture_Light> shared)
        {
            var lamps = new HashSet<Furniture_Light>(shared);
            var lightsField = AccessTools.Field(typeof(Home_Bunker), "BunkerLights");
            var parentsField = AccessTools.Field(typeof(Home_Bunker), "ToEnableWhenPlayerInBunker");
            // This is optional local presentation, not a reason to block joining.
            if (lightsField?.FieldType != typeof(Furniture_Light[]) || parentsField?.FieldType != typeof(GameObject[])) return;
            foreach (var bunker in UnityEngine.Object.FindObjectsOfType<Home_Bunker>(true))
            {
                if (bunker == null || !bunker.gameObject.scene.IsValid() ||
                    !(lightsField.GetValue(bunker) is Furniture_Light[] fixtures) ||
                    !(parentsField.GetValue(bunker) is GameObject[] parents)) continue;
                foreach (var parent in parents)
                {
                    if (parent == null) continue;
                    var contained = fixtures.Where(fixture => fixture != null && lamps.Contains(fixture) &&
                        fixture.transform.IsChildOf(parent.transform)).ToArray();
                    if (contained.Length != 0) groups.Add(new Group(contained, EntranceCeiling(bunker)));
                }
            }
        }

        private static float EntranceCeiling(Home_Bunker bunker)
        {
            // The native interaction perimeter extends into the trailer above the
            // stairs. Its top is not an interior-light boundary. Use the authored
            // entrance shell's upper edge (Y98.369 in the supported scene), so a
            // trailer view above the stairs cannot restore the underground lamps.
            // Read the shared mesh once; do not instantiate meshes or change layers.
            var entrance = bunker.transform.Find("HomeBunker/BunkerEntrance")?.GetComponent<MeshFilter>();
            if (entrance == null || entrance.sharedMesh == null) return float.PositiveInfinity;
            var bounds = entrance.sharedMesh.bounds;
            float ceiling = float.NegativeInfinity;
            for (int corner = 0; corner < 8; corner++)
            {
                var point = new Vector3(bounds.center.x + ((corner & 1) == 0 ? -bounds.extents.x : bounds.extents.x),
                    bounds.center.y + ((corner & 2) == 0 ? -bounds.extents.y : bounds.extents.y),
                    bounds.center.z + ((corner & 4) == 0 ? -bounds.extents.z : bounds.extents.z));
                ceiling = Mathf.Max(ceiling, entrance.transform.TransformPoint(point).y);
            }
            return ceiling;
        }

        internal void Update()
        {
            if (disposed || groups.Count == 0 || Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 0.1f;
            var gameplay = Singleton<Gameplay>.i;
            if (gameplay == null) return;
            // Native walking/spectating uses this same camera. Keep an active vehicle
            // camera cached too, so the visibility check does not search tagged objects
            // every frame or allocate a lamp/component inventory while playing.
            var camera = gameplay.Player?.mainCamera;
            if (camera == null || !camera.isActiveAndEnabled)
                camera = currentCamera != null && currentCamera.isActiveAndEnabled ? currentCamera : Camera.main;
            if (camera == null || !camera.isActiveAndEnabled) return;
            currentCamera = camera;
            var viewpoint = camera.transform.position;
            bool inPerimeter = gameplay.IsInABunkerPerimeter(viewpoint);
            foreach (var group in groups)
            {
                // Keep the underground room/entrance/stairwell portion of the
                // perimeter while excluding its aboveground trailer overlap.
                bool inside = inPerimeter && viewpoint.y <= group.EntranceCeiling;
                foreach (var fixture in group.Fixtures)
                {
                    if (fixture == null || fixture.lightSource == null) continue;
                    var light = fixture.lightSource;
                    if (!masks.TryGetValue(light, out int original))
                    { original = light.cullingMask; masks.Add(light, original); }
                    int expected = inside ? original : 0;
                    if (light.cullingMask != expected) light.cullingMask = expected;
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var entry in masks) if (entry.Key != null) entry.Key.cullingMask = entry.Value;
            masks.Clear(); groups.Clear();
            currentCamera = null;
        }
    }
}
