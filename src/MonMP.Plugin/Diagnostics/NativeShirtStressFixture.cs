using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class NativeShirtStressFixture : IDisposable
    {
        internal static void TestBacking(Gameplay gameplay, string directory)
        {
            var root = new GameObject("MonMP shoulder backing acceptance"); root.transform.position = new Vector3(0, -20, 0);
            StockAvatarBody? body = null;
            try
            {
                body = StockAvatarBody.TryCreate(root.transform, Color.cyan, out string reason) ?? throw new InvalidOperationException(reason);
                foreach (string pose in new[] { "wave", "point", "thumbs-up", "map", "zoom" })
                {
                    body.Present(StockAvatarBody.Mode.Stand, 0, 0, 0, StockAvatarBody.Overlay.Look(0), 0.016f);
                    var rightHand = root.GetComponentsInChildren<Transform>(true).Single(t => t.name == "Hand.R");
                    var neutralHand = rightHand.rotation;
                    var overlay = StockAvatarBody.Overlay.Look(0);
                    if (pose == "map") overlay.Map = 1;
                    else if (pose == "zoom") overlay.Zoom = 1;
                    else { overlay.Gesture = pose == "wave" ? PlayerEmoteKind.Wave : pose == "point" ? PlayerEmoteKind.Point : PlayerEmoteKind.ThumbsUp; overlay.GestureTime = 0.6f; }
                    for (int frame = 0; frame < 30; frame++)
                    {
                        body.Present(StockAvatarBody.Mode.Stand, 0, 0, 0, overlay, 0.016f);
                        body.Solve(new StockAvatarBody.Goals(), 0.016f);
                    }
                    Require(Quaternion.Angle(neutralHand, rightHand.rotation) > 1f, "Shirt capture did not apply " + pose + " hand pose.");
                    Require(body.TestSleeveMaskApplied, "Native shoulder skin mask did not apply.");
                    var shirt = Shirt(root);
                    var report = GeometryCheck(shirt);
                    File.WriteAllText(Path.Combine(directory, "shirt-backing-" + pose + ".json"), Newtonsoft.Json.JsonConvert.SerializeObject(report));
                    NativeShirtCloseCapture.Save(root.transform, shirt, directory, "shirt-backing-" + pose);
                }
                Debug.Log("MonMP native shirt backing passed: wave, point, thumbs-up, map and zoom; original skin/UV vertices, 180 reverse shoulder faces, posed bake parity and six views per pose.");
            }
            finally { body?.Destroy(); UnityEngine.Object.Destroy(root); }
        }

        private readonly Gameplay gameplay;
        private readonly Action<string, object> write;
        private readonly List<StockAvatarBody?> bodies = new List<StockAvatarBody?>();
        private readonly List<GameObject?> roots = new List<GameObject?>();
        private readonly List<int> colors = new List<int>();
        private readonly List<(Mesh Mesh, Material Material)> assets = new List<(Mesh, Material)>();
        private readonly int baselineMeshes, baselineLeases;
        private readonly string id;
        private int phase;
        private float next;
        private object? initial, fourteen, recreated, geometry, retry;
        internal bool Running { get; private set; } = true;
        internal NativeShirtStressFixture(Gameplay gameplay, string id, Action<string, object> write)
        {
            this.gameplay = gameplay; this.id = id; this.write = write;
            baselineMeshes = StockAvatarShirt.CachedMeshCount; baselineLeases = StockAvatarShirt.CachedLeaseCount;
            for (int i = 0; i < 15; i++) { bodies.Add(null); roots.Add(null); colors.Add(i % 9); }
        }
        internal void Tick()
        {
            if (!Running) return;
            try
            {
                foreach (var body in bodies) body?.Present(StockAvatarBody.Mode.Stand, 0, 0, 0, StockAvatarBody.Overlay.Look(0), Time.unscaledDeltaTime);
                if (Time.unscaledTime < next) return;
                if (phase == 0)
                {
                    for (int i = 0; i < 15; i++) Create(i);
                    phase = 1; next = Time.unscaledTime + 0.25f; return;
                }
                if (phase == 1)
                {
                    initial = Check(); geometry = GeometryCheck(Shirt(roots[0]!)); retry = DelayedRetryCheck();
                    var owner = (StockAvatarShirt)AccessTools.Field(typeof(StockAvatarBody), "shirtFabric").GetValue(bodies[0]);
                    var sharedMesh = Shirt(roots[0]!).sharedMesh;
                    Destroy(0);
                    Require(StockAvatarShirt.CachedLeaseCount == baselineLeases + 14 && sharedMesh != null, "Removing one of fifteen destroyed shared geometry or retained a lease.");
                    int after = StockAvatarShirt.CachedLeaseCount;
                    owner.Dispose(); Require(StockAvatarShirt.CachedLeaseCount == after, "Double lease disposal decremented ownership twice.");
                    fourteen = new { leases = after, relativeLeases = after - baselineLeases, sharedMeshAlive = sharedMesh != null, doubleDisposeSafe = true };
                    Create(0);
                    for (int i = 1; i <= 5; i++) { Destroy(i); colors[i] = (colors[i] + 3) % 9; Create(i); }
                    phase = 2; next = Time.unscaledTime + 0.25f; return;
                }
                if (phase == 2)
                {
                    recreated = Check(); for (int i = 0; i < 15; i++) Destroy(i);
                    phase = 3; next = Time.unscaledTime + 0.25f; return;
                }
                Require(StockAvatarShirt.CachedLeaseCount == baselineLeases && StockAvatarShirt.CachedMeshCount == baselineMeshes,
                    "Stress cleanup retained private shirt cache ownership.");
                Require(assets.All(a => a.Material == null), "Stress cleanup retained an avatar-local material.");
                write(id, new { passed = true, simultaneousBodies = 15, allNativeColorEnums = 9, baselineMeshes, baselineLeases, initial, fourteen, recreated, geometry, retry, cleanup = Status() });
                Running = false;
            }
            catch (Exception ex) { Dispose(); write(id, new { failed = true, error = ex.ToString(), cleanup = Status() }); }
        }
        private void Create(int i)
        {
            var root = new GameObject("MonMP Shirt Stress " + i);
            // Stay outside the players' area while keeping BakeMesh matrix cancellation near
            // the origin, where a sub-millimetre skinning comparison has meaningful precision.
            root.transform.position = new Vector3(i * 3, -20, 0); roots[i] = root;
            Color color = gameplay.Player.Get_VisualHands.GetShirtColorFromEnum((Player_ShirtColor)colors[i]);
            bodies[i] = StockAvatarBody.TryCreate(root.transform, color, out string reason) ?? throw new InvalidOperationException(reason);
            bodies[i]!.Present(StockAvatarBody.Mode.Stand, 0, 0, 0, StockAvatarBody.Overlay.Look(0), 0.016f);
        }
        private object Check()
        {
            var shirts = roots.Select(r => Shirt(r!)).ToArray();
            var native = (SkinnedMeshRenderer)AccessTools.Field(typeof(PlayerVisualHands), "Mesh_Shirt").GetValue(gameplay.Player.Get_VisualHands);
            for (int i = 0; i < shirts.Length; i++)
            {
                var shirt = shirts[i]; Color expected = gameplay.Player.Get_VisualHands.GetShirtColorFromEnum((Player_ShirtColor)colors[i]);
                Require(Vector4.Distance(shirt.sharedMaterial.color, expected) < 0.00001f, "Independent native shirt tint changed at actor " + i);
                Require(shirt.sharedMaterial.mainTexture == native.sharedMaterial.mainTexture && shirt.sharedMaterial.GetTexture("_BumpMap") == native.sharedMaterial.GetTexture("_BumpMap"), "Stress shirt lost native fabric maps.");
                Require(shirt.bones.Length == 33 && shirt.sharedMesh.vertexCount == StockAvatarShirtUv.SourceVertices.Length, "Stress body lost original rig or private remapped geometry.");
                assets.Add((shirt.sharedMesh, shirt.sharedMaterial));
            }
            Require(shirts.Select(s => s.sharedMaterial.GetInstanceID()).Distinct().Count() == 15, "Stress bodies share mutable tint material.");
            Require(shirts.Select(s => s.sharedMesh.GetInstanceID()).Distinct().Count() == 1, "Stress bodies did not share the remapped geometry cache.");
            Require(StockAvatarShirt.CachedLeaseCount == baselineLeases + 15, "Stress cache lease count is not baseline plus fifteen.");
            return new
            {
                privateMaterials = 15, sharedMeshes = 1, meshes = StockAvatarShirt.CachedMeshCount, leases = StockAvatarShirt.CachedLeaseCount,
                colors = colors.ToArray(), sources = bodies.Select(b => b!.Sources).Distinct().ToArray()
            };
        }
        private static object GeometryCheck(SkinnedMeshRenderer target)
        {
            var cache = (IDictionary)AccessTools.Field(typeof(StockAvatarShirt), "geometries").GetValue(null);
            Mesh? original = null;
            foreach (DictionaryEntry pair in cache)
                if ((Mesh)AccessTools.Field(pair.Value!.GetType(), "Mesh").GetValue(pair.Value) == target.sharedMesh) original = (Mesh)pair.Key;
            Require(original != null, "Cannot identify borrowed stock source mesh.");
            var actual = target.sharedMesh; var mapping = StockAvatarShirtUv.SourceVertices;
            Require(actual.triangles.SequenceEqual(StockAvatarShirtBacking.Triangles()), "Shoulder backing lost exact source faces or reverse winding.");
            var nativeBindposes = original!.bindposes; var bindposes = actual.bindposes;
            Require(bindposes.Length == 33 && nativeBindposes.Length == 33, "Shirt bind pose count changed.");
            for (int i = 0; i < bindposes.Length; i++) for (int j = 0; j < 16; j++)
                Require(bindposes[i][j] == nativeBindposes[i][j], "Shirt bind pose data changed.");
            var sourceCounts = original.GetBonesPerVertex(); var sourceWeights = original.GetAllBoneWeights();
            var counts = actual.GetBonesPerVertex(); var weights = actual.GetAllBoneWeights();
            var offsets = new int[sourceCounts.Length]; int cursor = 0;
            for (int i = 0; i < offsets.Length; i++) { offsets[i] = cursor; cursor += sourceCounts[i]; }
            cursor = 0; float maximumWeightError = 0;
            for (int i = 0; i < mapping.Length; i++)
            {
                int source = mapping[i]; Require(counts[i] == sourceCounts[source], "Per-vertex bone count changed.");
                for (int j = 0; j < counts[i]; j++)
                {
                    var a = weights[cursor++]; var b = sourceWeights[offsets[source] + j];
                    maximumWeightError = Mathf.Max(maximumWeightError, Mathf.Abs(a.weight - b.weight));
                    Require(a.boneIndex == b.boneIndex && Mathf.Abs(a.weight - b.weight) <= 0.000001f, "Per-vertex skin weight changed beyond native float normalization precision.");
                }
            }
            var tangents = actual.tangents;
            Require(tangents.Length == mapping.Length && tangents.All(t => Finite(t.x) && Finite(t.y) && Finite(t.z) && Finite(t.w)), "Remapped tangents are missing or non-finite.");
            var compare = new GameObject("MonMP Shirt Source Comparison");
            var reference = compare.AddComponent<SkinnedMeshRenderer>();
            compare.transform.SetParent(target.transform.parent, false);
            compare.transform.localPosition = target.transform.localPosition; compare.transform.localRotation = target.transform.localRotation; compare.transform.localScale = target.transform.localScale;
            reference.sharedMesh = original; reference.bones = target.bones; reference.rootBone = target.rootBone; reference.enabled = false;
            var before = new Mesh(); var after = new Mesh(); float maximum = 0;
            try
            {
                reference.BakeMesh(before); target.BakeMesh(after);
                var sourceVertices = before.vertices; var vertices = after.vertices;
                for (int i = 0; i < mapping.Length; i++) maximum = Mathf.Max(maximum, Vector3.Distance(vertices[i], sourceVertices[mapping[i]]));
                Require(maximum < 0.0001f, "Remapped skinning moved posed vertices: " + maximum);
            }
            finally { UnityEngine.Object.Destroy(before); UnityEngine.Object.Destroy(after); UnityEngine.Object.Destroy(compare); }
            return new { vertices = mapping.Length, bindposes = 33, sameBoneIndicesAndCounts = true, maximumWeightError, weightTolerance = 0.000001f, finiteTangents = tangents.Length, maximumBakedPositionError = maximum };
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private object DelayedRetryCheck()
        {
            int before = StockAvatarShirt.CachedLeaseCount;
            var root = new GameObject("MonMP Shirt Delayed Retry"); root.transform.position = new Vector3(0, -20, 0);
            var native = gameplay.Player.Get_VisualHands;
            var expectedTexture = ((SkinnedMeshRenderer)AccessTools.Field(typeof(PlayerVisualHands), "Mesh_Shirt").GetValue(native)).sharedMaterial.mainTexture;
            var body = StockAvatarBody.TryCreate(root.transform, native.GetShirtColorFromEnum((Player_ShirtColor)1), out string reason)
                ?? throw new InvalidOperationException(reason);
            try
            {
                var renderer = Shirt(root);
                assets.Add((renderer.sharedMesh, body.ShirtMaterial));
                var ownerField = AccessTools.Field(typeof(StockAvatarBody), "shirtFabric");
                ((StockAvatarShirt)ownerField.GetValue(body)).Dispose(); ownerField.SetValue(body, null);
                var fallback = (Material)AccessTools.Field(typeof(StockAvatarBody), "fallbackShirtMaterial").GetValue(body);
                AccessTools.Property(typeof(StockAvatarBody), "ShirtMaterial").SetValue(body, fallback);
                AccessTools.Field(typeof(StockAvatarBody), "shirtFabricAttempts").SetValue(body, 0);
                AccessTools.Field(typeof(StockAvatarBody), "shirtFabricRetryAt").SetValue(body, 0f);
                body.SetShadowOnly(true);
                body.Present(StockAvatarBody.Mode.Stand, 0, 0, 0, StockAvatarBody.Overlay.Look(0), 0.016f);
                Require(body.IsShadowOnly && ownerField.GetValue(body) != null, "Delayed fabric retry lost shadow state or did not acquire fabric.");
                body.SetShadowOnly(false);
                Require(renderer.sharedMaterial == body.ShirtMaterial && renderer.sharedMaterial != fallback && renderer.sharedMaterial.mainTexture == expectedTexture,
                    "Leaving shadow-only after delayed retry restored the fallback material.");
                var purple = native.GetShirtColorFromEnum((Player_ShirtColor)7);
                body.ShirtMaterial.color = purple;
                body.SetShadowOnly(true); body.SetShadowOnly(false);
                Require(renderer.sharedMaterial == body.ShirtMaterial && Vector4.Distance(renderer.sharedMaterial.color, purple) < 0.00001f,
                    "Delayed shirt material lost a subsequent live color change.");
                assets.Add((renderer.sharedMesh, body.ShirtMaterial));
                return new { forcedFallback = true, retryKeptShadowState = true, thirdPersonRestoredFabric = true, subsequentColorRetained = true };
            }
            finally
            {
                body.Destroy(); UnityEngine.Object.Destroy(root);
                Require(StockAvatarShirt.CachedLeaseCount == before, "Delayed retry fixture retained a cache lease.");
            }
        }
        private static SkinnedMeshRenderer Shirt(GameObject root) => root.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.name == "Shirt_LongSleeve");
        private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
        private void Destroy(int i)
        {
            bodies[i]?.Destroy(); bodies[i] = null;
            if (roots[i] != null) UnityEngine.Object.Destroy(roots[i]); roots[i] = null;
        }
        internal object Status() => new
        {
            meshes = StockAvatarShirt.CachedMeshCount, leases = StockAvatarShirt.CachedLeaseCount,
            ownedMaterialsAlive = assets.Count(a => a.Material != null), remappedMeshesAlive = assets.Count(a => a.Mesh != null)
        };
        public void Dispose() { for (int i = 0; i < bodies.Count; i++) Destroy(i); Running = false; }
    }
}
