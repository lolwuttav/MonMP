using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Core.Protocol;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace MonMP.Plugin.Replication
{
    // A remote player's body built from the game's own NPC art. Every human NPC shares one generic
    // 33-bone "Armature"; the St-Jean-Baptiste decoration keeps script- and collider-free copies of
    // it in the master scene, which are cloned here. The only human clip in the game is
    // Actor_Walking, so standing, sitting and driving come from NPCs posed in the scene (Eric,
    // Normand, the school-bus driver, whose square seat also serves chairs) and crouching, looking,
    // gestures and reaching are layered on top.
    internal sealed partial class StockAvatarBody
    {
        // Sit is a vehicle passenger, Chair a furniture chair, Lie on the back (creeper, bed), Down
        // collapsed on the ground (dead or passed out) and Curl asleep on the side, knees drawn up
        // (a vehicle's back seat is too short to lie out on).
        // Ground: sitting on a floor (the truck bed), knees up.
        internal enum Mode { Stand, Sit, Chair, Drive, Lie, Down, Curl, Ground }
        internal const float MissileScale = 0.8f;

        // Layered on the base pose: head look relative to the body, the body's own yaw relative to
        // the avatar root, the native hand states (0..1) and the emote gestures (Sorry's raised
        // hand, a "Sacre" head toss, the spit windup, vomiting, the interaction poke, petting Leo,
        // and the cosmetic wave/point/thumbs-up; timers count seconds since the emote).
        internal struct Overlay
        {
            public float Pitch, HeadYaw, BodyYaw, Sorry, SlurTime, SpitTime, GestureTime;
            public PlayerEmoteKind Gesture;
            public Vector3? PointTarget;
            public float Map, Console, Flashlight, Joint, Smoking, Zoom;
            public float VomitTime, VomitSeconds, InteractTime, PetTime;
            // Off the ground (0..1): legs drawn up through a jump or fall.
            public float Air;
            // Drunk and hungover (0..1) and a per-player clock for the sway.
            public float Drunk, Hangover, Clock;
            public static Overlay Look(float pitch) => new Overlay { Pitch = pitch, SlurTime = -1, SpitTime = -1, VomitTime = -1, InteractTime = -1, PetTime = -1 };
        }

        // Hand and foot goals solved after the body is placed. Grips are both hands on something
        // the player works: a steering wheel (elbows out) or the wheelbarrow handles (elbows back).
        internal struct Goals
        {
            public Vector3? Held;
            public float HeldWidth;
            public Quaternion? HeldRotation;
            // A calibrated second palm takes precedence over the legacy centered width.
            public Vector3? HeldLeft;
            public Quaternion? HeldLeftRotation;
            // Only an observer-owned visual copy may follow the rigid reach correction.
            public bool ConstrainHeld;
            public Vector3? GripLeft, GripRight, GripCenter;
            public Quaternion? GripLeftRotation, GripRightRotation;
            // Passenger calibration is an idle rest goal; tools and hand overlays win first.
            public Vector3? SeatLeft, SeatRight;
            public Quaternion? SeatLeftRotation, SeatRightRotation;
            // Independent seated foot bone targets; the body/head placement stays fixed.
            public Vector3? SeatFootLeft, SeatFootRight;
            public Quaternion? SeatFootLeftRotation, SeatFootRightRotation;
            public bool Push;
            public bool Feet;
            public RemoteHandProps.Prop? MapProp;
            public HeldGripOverrides.Grip? MapCalibration;
            public RemoteHandProps.Prop? FlashlightProp;
            public HeldGripOverrides.Grip? FlashlightCalibration;
        }

        private sealed class Library
        {
            public GameObject Template = null!;
            public AnimationClip? Walk;
            public string[] Bones = Array.Empty<string>();
            public Quaternion[] Stand = Array.Empty<Quaternion>(), Sit = Array.Empty<Quaternion>(), Chair = Array.Empty<Quaternion>(), Drive = Array.Empty<Quaternion>();
            public Vector3 StandHips, SitHips, ChairHips, DriveHips;
            public string Sources = "";
            public Transform[] Candidates = Array.Empty<Transform>();
            public bool UsesModelClothing;
        }

        // Player eyes sit 1.6 m above the walker root; the top of the head a little above that.
        private const float HeadTopHeight = 1.74f;
        private const float WalkStrideSpeed = 1.4f;
        private const float ArmReach = 1.6f;
        // Thigh swing at full crouch; the shin folds back twice as far so the feet stay under the hips.
        private const float CrouchThighAngle = 70f;
        private const float MaxFootOffset = 0.4f;
        private const float FootRayHeight = 0.6f;
        private const float SlurSeconds = 0.8f, SpitWindup = 1.7f, SpitRelease = 1.95f, SpitSeconds = 2.4f;
        // The native interaction bool is up for 0.1 s and the Leo pet for 1 s; the remote gestures
        // are stretched a little so they read at a distance.
        internal const float InteractSeconds = 0.45f, PetSeconds = 1.3f;
        private const float WaveSeconds = 2.1f, PointSeconds = 1.45f, ThumbsUpSeconds = 1.5f;
        private static int groundMask = -1;
        // Walkable world geometry: everything but the player, trigger-only and UI layers.
        internal static int GroundMask => groundMask != -1 ? groundMask : groundMask = ~LayerMask.GetMask("Ignore Raycast", "Player", "UI");
        private static readonly string[] Shown = { "Actor", "Armature", "Hair", "Shirt_LongSleeve", "Pants_Long", "Shoes" };
        private static Library? library;

        internal static float GestureDuration(PlayerEmoteKind kind)
        {
            switch (kind)
            {
                case PlayerEmoteKind.Wave: return WaveSeconds;
                case PlayerEmoteKind.Point: return PointSeconds;
                case PlayerEmoteKind.ThumbsUp: return ThumbsUpSeconds;
                default: return 0f;
            }
        }

        private static bool IsGestureActive(Overlay overlay)
        {
            float duration = GestureDuration(overlay.Gesture);
            return duration > 0f && overlay.GestureTime >= 0f && overlay.GestureTime < duration;
        }

        private readonly GameObject body;
        private readonly Transform armature;
        private AvatarShadowCasting? shadowCasting;
        private readonly Transform[] bones;
        private readonly Library source;
        private readonly Transform hips, spine, neck, head, upperArmR, lowerArmR, handR, upperArmL, lowerArmL, handL, footL, footR;
        private readonly Transform? fingersR, fingersDistalR, fingersL, fingersDistalL, thumbR, thumbDistalR, thumbEndR;
        private readonly Quaternion neutralNeckRotation, neutralHeadRotation;
        private readonly float[] footOffset = new float[2];
        private readonly Vector3[] footNormal = { Vector3.up, Vector3.up };
        private Overlay overlay;
        private Mode mode;
        private readonly Transform[] thighs, shins, feet;
        // The walk is evaluated through a manual graph: AnimationClip.SampleAnimation does not apply
        // Mecanim clips in a player build.
        private PlayableGraph walkGraph;
        private AnimationClipPlayable walkPlayable;
        private readonly Quaternion[] frame;
        private readonly float standFootHeight;
        private Vector3 savedSpectatorBodyPosition;
        private Quaternion savedSpectatorBodyRotation, savedSpectatorNeckRotation, savedSpectatorHeadRotation;
        private bool spectatorHeadOnlyActive;
        private readonly Material fallbackShirtMaterial;
        private readonly SkinnedMeshRenderer shirtRenderer;
        private StockAvatarShirt? shirtFabric;
        private StockAvatarSleeveMask? sleeveMask;
        private int sleeveMaskAttempts;
        private float sleeveMaskRetryAt;
        private int shirtFabricAttempts;
        private float shirtFabricRetryAt;
        internal Material ShirtMaterial { get; private set; }
#if MONMP_DEVELOPMENT
        internal bool TestSleeveMaskApplied => modularPrepared || sleeveMask != null;
        // A private preview draft never changes live avatars or the saved runtime lookup.
        internal EmotePoseOverrides.Pose? EmotePosePreview { get; set; }
#endif
        internal Transform Head => head;
#if MONMP_PERFORMANCE_CAPTURE
        internal GameObject CaptureRenderingRoot => body;
#endif
        internal float VisualScale => body.transform.lossyScale.y;
        internal string Sources => source.Sources;
        internal bool Walks => source.Walk != null;
        private readonly List<Mesh> spectatorHeadMeshes = new List<Mesh>();
        private readonly List<GameObject> spectatorHeadObjects = new List<GameObject>();
        private readonly List<Renderer> spectatorHeadRenderers = new List<Renderer>();
        private bool spectatorHeadPrepared;
        internal int SpectatorHeadTriangleCount { get; private set; }
        internal bool HasSpectatorHeadMesh => SpectatorHeadTriangleCount > 0;
        private float gaitTime;
        // Lying down and getting up blend over a moment instead of snapping.
        private float lieBlend, footCorrection, standStill;
        private bool presented;
        private Vector3 nativeScale;
        private readonly Vector3 binocularEyeLocal;
        private readonly Quaternion binocularPalmLeft, binocularPalmRight;
        private float presentationScale = 1;
        private Quaternion lieRotation = Quaternion.Euler(-90f, 0, 0);
        private Vector3 liePosition = new Vector3(0, 0.25f, 0);

        private StockAvatarBody(GameObject body, Library source, Material shirt, SkinnedMeshRenderer shirtRenderer)
        {
            this.body = body; this.source = source; ShirtMaterial = shirt;
            fallbackShirtMaterial = shirt; this.shirtRenderer = shirtRenderer;
            armature = body.transform.Find("Armature");
            var byName = armature.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            bones = source.Bones.Select(name => byName.TryGetValue(name, out var bone) ? bone : throw new InvalidOperationException("Stock avatar bone missing: " + name)).ToArray();
            Transform Bone(string name) => byName.TryGetValue(name, out var bone) ? bone : throw new InvalidOperationException("Stock avatar bone missing: " + name);
            hips = Bone("Hips"); spine = Bone("Spine"); neck = Bone("Neck"); head = Bone("Head");
            neutralNeckRotation = source.Stand[System.Array.IndexOf(source.Bones, "Neck")];
            neutralHeadRotation = source.Stand[System.Array.IndexOf(source.Bones, "Head")];
            upperArmR = Bone("Upper Arm.R"); lowerArmR = Bone("Lower Arm.R"); handR = Bone("Hand.R");
            byName.TryGetValue(StockGesturePoseMath.ProximalFingerBone, out fingersR);
            byName.TryGetValue(StockGesturePoseMath.DistalFingerBone, out fingersDistalR);
            byName.TryGetValue(StockGesturePoseMath.ThumbBaseBone, out thumbR);
            byName.TryGetValue(StockGesturePoseMath.ThumbDistalBone, out thumbDistalR);
            byName.TryGetValue(StockGesturePoseMath.ThumbEndBone, out thumbEndR);
            upperArmL = Bone("Upper Arm.L"); lowerArmL = Bone("Lower Arm.L"); handL = Bone("Hand.L");
            byName.TryGetValue("Fingers01.L", out fingersL);
            byName.TryGetValue("Fingers02.L", out fingersDistalL);
            footL = Bone("Foot.L"); footR = Bone("Foot.R");
            thighs = new[] { Bone("Upper Leg.L"), Bone("Upper Leg.R") };
            shins = new[] { Bone("Lower Leg.L"), Bone("Lower Leg.R") };
            feet = new[] { footL, footR };
            if (source.Walk != null)
            {
                var animator = body.GetComponent<Animator>();
                if (animator == null) animator = body.AddComponent<Animator>();
                walkGraph = StockAvatarWalkGraph.Create(animator, source.Walk, out walkPlayable);
            }
            frame = new Quaternion[bones.Length];
            Write(source.Stand, source.StandHips);
            standFootHeight = FootHeight();
            // Match the local player's eye height.
            var top = Bone("Head_end");
            float headTop = body.transform.InverseTransformPoint(top.position).y;
            if (headTop > 0.5f) body.transform.localScale = Vector3.one * Mathf.Clamp(HeadTopHeight / headTop, 0.8f, 1.15f);
            nativeScale = body.transform.localScale;
            var referenceAim = body.transform.rotation;
            binocularEyeLocal = head.InverseTransformPoint(head.position + referenceAim *
                new Vector3(0, RemoteBinoculars.EyeHeight, .13f) * VisualScale);
            var center = BinocularEyePosition + referenceAim * Vector3.forward * (.035f * VisualScale);
            var right = referenceAim * Vector3.right; var up = referenceAim * Vector3.up;
            Grip(upperArmL, lowerArmL, handL, center - right * (.075f * VisualScale), -right - up * .7f, up);
            Grip(upperArmR, lowerArmR, handR, center + right * (.075f * VisualScale), right - up * .7f, up);
            binocularPalmLeft = Quaternion.Inverse(referenceAim) * handL.rotation;
            binocularPalmRight = Quaternion.Inverse(referenceAim) * handR.rotation;
            Write(source.Stand, source.StandHips);
        }

        internal static StockAvatarBody? TryCreate(Transform parent, Color shirt, out string reason)
        {
            var lib = Load(out reason);
            if (lib == null) return null;
            return Create(parent, shirt, lib, out reason);
        }
        private static StockAvatarBody? Create(Transform parent, Color shirt, Library lib, out string reason, bool nativeTexture = false)
        {
            bool parentWasActive = parent.gameObject.activeSelf;
            parent.gameObject.SetActive(false);
            GameObject? clone = null;
            Material? shirtMaterial = null;
            SkinnedMeshRenderer? shirtRenderer = null;
            try
            {
                clone = UnityEngine.Object.Instantiate(lib.Template, parent, false);
                clone.name = "StockBody";
                clone.SetActive(true);
                clone.transform.localPosition = Vector3.zero; clone.transform.localRotation = Quaternion.identity; clone.transform.localScale = Vector3.one;
                // Nothing on a remote body may interact, collide, animate on its own or make noise.
                foreach (var component in clone.GetComponentsInChildren<Component>(true))
                    if (component is MonoBehaviour || component is Collider || component is Animation ||
                        component is AudioSource || component is Rigidbody || component is Light)
                        UnityEngine.Object.DestroyImmediate(component);
                if (!lib.UsesModelClothing)
                    foreach (Transform child in clone.transform) child.gameObject.SetActive(Shown.Contains(child.name));
                foreach (var renderer in clone.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    renderer.updateWhenOffscreen = true;
                    if (renderer.name == "Shirt_LongSleeve" && renderer.enabled && ActiveWithinTemplate(renderer.transform, clone.transform))
                    {
                        if (shirtRenderer != null) throw new InvalidOperationException("Avatar template has multiple active shirts.");
                        shirtRenderer = renderer; shirtMaterial = renderer.material; shirtMaterial.color = shirt;
                    }
                }
                if (shirtMaterial == null || shirtRenderer == null) throw new InvalidOperationException("Stock avatar template has no long-sleeve shirt.");
                var created = new StockAvatarBody(clone, lib, shirtMaterial, shirtRenderer);
                created.nativeShirtTexture = nativeTexture;
                created.TryApplyShirtFabric();

                if (lib.UsesModelClothing) created.RememberNpcHeadwear();
                reason = "stock (" + lib.Sources + ")";
                return created;
            }
            catch (Exception ex)
            {
                // renderer.material owns a native copy independently of the cloned
                // GameObject; a failed rig/clip setup must release that allocation too.
                if (shirtMaterial != null) UnityEngine.Object.Destroy(shirtMaterial);
                if (clone != null) UnityEngine.Object.Destroy(clone);
                reason = "stock avatar unavailable: " + ex.Message;
                return null;
            }
            finally { parent.gameObject.SetActive(parentWasActive); }
        }

        internal void Destroy()
        {
            RestoreSpectatorHead();
            shadowCasting?.Restore();
            if (walkGraph.IsValid()) walkGraph.Destroy();
            foreach (var mesh in spectatorHeadMeshes) if (mesh != null) UnityEngine.Object.Destroy(mesh);
            spectatorHeadMeshes.Clear(); spectatorHeadObjects.Clear(); spectatorHeadRenderers.Clear();
            foreach (var mesh in modularMeshes) if (mesh != null) UnityEngine.Object.Destroy(mesh);
            modularMeshes.Clear();
            shirtFabric?.Dispose();
            sleeveMask?.Dispose();
            sleeveMask = null;
            shirtFabric = null;
            UnityEngine.Object.Destroy(fallbackShirtMaterial);
            body.SetActive(false);
            UnityEngine.Object.Destroy(body);
        }

        private void TryApplyShirtFabric()
        {
            // GPU buffers may not exist until the avatar's first rendered frame. Keep the
            // colored body usable and retry briefly; unsupported game meshes stay on that
            // fallback without rescanning native resources on every presentation tick.
            if (nativeShirtTexture || shirtFabric != null || shirtRenderer == null || shirtFabricAttempts >= 4 ||
                Time.unscaledTime < shirtFabricRetryAt) return;
            shirtFabricAttempts++;
            shirtFabricRetryAt = Time.unscaledTime + 1f;
            // First-person shadows retain their own material copies. A late fabric upgrade
            // must update the saved visible material as well, otherwise returning to third
            // person would restore the old plain shirt.
            var casting = shadowCasting;
            bool shadowOnly = casting?.IsShadowOnly ?? false;
            casting?.Restore();
            try
            {
                if (StockAvatarShirt.TryCreate(shirtRenderer, ShirtMaterial.color, out var fabric, out _))
                {
                    shirtFabric = fabric;
                    ShirtMaterial = fabric!.Material;
                    casting?.RefreshMaterials(shirtRenderer);
                }
            }
            finally { if (shadowOnly) casting!.SetShadowOnly(true); }
        }

        internal void SetShadowOnly(bool enabled)
        {
            if (racingHelmet != null) racingHelmet.shadowCastingMode = enabled
                ? UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly : UnityEngine.Rendering.ShadowCastingMode.On;
            if (!enabled && shadowCasting == null) return;
            shadowCasting ??= new AvatarShadowCasting(body.GetComponentsInChildren<Renderer>(true));
            shadowCasting.SetShadowOnly(enabled);
        }

        private void TryApplySleeveMask()
        {
            if (modularPrepared || shirtFabric == null || sleeveMask != null || sleeveMaskAttempts >= 4 || Time.unscaledTime < sleeveMaskRetryAt) return;
            sleeveMaskAttempts++;
            sleeveMaskRetryAt = Time.unscaledTime + 1f;
            var skin = body.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(r => r.name == "Actor");
            if (StockAvatarSleeveMask.TryCreate(skin, out var mask)) sleeveMask = mask;
        }

        internal bool IsShadowOnly => shadowCasting?.IsShadowOnly ?? false;

        internal bool IsHeadRenderer(Renderer renderer)
        {
            if (renderer == null) return false;
            if (spectatorHeadRenderers.Contains(renderer)) return true;
            // A combined skinned source renderer is not itself a head surface. Spectator head
            // geometry is represented by the filtered static proxy; dedicated objects such as
            // Hair remain head surfaces through their own transform/name presentation rules.
            if (renderer is SkinnedMeshRenderer) return false;
            for (var current = renderer.transform; current != null && current != body.transform; current = current.parent)
            {
                if (current == head || current.IsChildOf(head) ||
                    current.name.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    current.name.IndexOf("hair", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        internal void EnsureSpectatorHead()
        {
            if (spectatorHeadPrepared) return;
            spectatorHeadPrepared = true;
            try
            {
                foreach (var sourceRenderer in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (sourceRenderer == null || !sourceRenderer.enabled || !sourceRenderer.gameObject.activeInHierarchy) continue;
                    // Hair is a separate, head-weighted stock renderer. Keep that live renderer
                    // as the single hair surface rather than baking a duplicate over it.
                    if (string.Equals(sourceRenderer.name, "Hair", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(sourceRenderer.transform.name, "Hair", StringComparison.OrdinalIgnoreCase)) continue;
                    var sourceMesh = sourceRenderer.sharedMesh;
                    var sourceBones = sourceRenderer.bones;
                    if (sourceMesh == null || sourceMesh.vertexCount == 0 || sourceMesh.subMeshCount == 0 || sourceBones.Length == 0) continue;

                    var headBones = new bool[sourceBones.Length];
                    bool hasHeadBone = false;
                    for (int i = 0; i < sourceBones.Length; i++)
                    {
                        var bone = sourceBones[i];
                        headBones[i] = bone != null && (bone == head || bone.IsChildOf(head));
                        hasHeadBone |= headBones[i];
                    }
                    if (!hasHeadBone) continue;

                    // These are Allocator.None views over mesh-owned storage; this code
                    // does not own or dispose the memory returned by these getters.
                    var bonesPerVertex = sourceMesh.GetBonesPerVertex();
                    var allWeights = sourceMesh.GetAllBoneWeights();
                    if (!bonesPerVertex.IsCreated || !allWeights.IsCreated || bonesPerVertex.Length != sourceMesh.vertexCount) continue;
                    var headWeightByVertex = new float[sourceMesh.vertexCount];
                    int weightIndex = 0;
                    bool validWeights = true;
                    for (int vertex = 0; vertex < headWeightByVertex.Length; vertex++)
                    {
                        int count = bonesPerVertex[vertex];
                        if (weightIndex + count > allWeights.Length) { validWeights = false; break; }
                        for (int i = 0; i < count; i++)
                        {
                            var weight = allWeights[weightIndex++];
                            if (weight.boneIndex >= 0 && weight.boneIndex < headBones.Length && headBones[weight.boneIndex])
                                headWeightByVertex[vertex] += weight.weight;
                        }
                    }
                    if (!validWeights || weightIndex != allWeights.Length) continue;

                    // Stock model meshes ship without CPU-readable triangle buffers. Bake the
                    // currently posed renderer into a temporary readable snapshot, then filter
                    // that snapshot with the source mesh's accessible skin weights. BakeMesh
                    // preserves the source vertex order and submeshes.
                    var bakedMesh = new Mesh { name = "MonMP Spectator Head Bake " + sourceMesh.name };
                    try
                    {
                        sourceRenderer.BakeMesh(bakedMesh, false);
                        if (!bakedMesh.isReadable || bakedMesh.vertexCount != sourceMesh.vertexCount ||
                            bakedMesh.subMeshCount != sourceMesh.subMeshCount) continue;

                        var trianglesBySubmesh = new List<int>[sourceMesh.subMeshCount];
                        int triangleCount = 0;
                        for (int submesh = 0; submesh < sourceMesh.subMeshCount; submesh++)
                        {
                            var selected = new List<int>();
                            var triangles = bakedMesh.GetTriangles(submesh);
                            for (int i = 0; i + 2 < triangles.Length; i += 3)
                            {
                                int first = triangles[i], second = triangles[i + 1], third = triangles[i + 2];
                                if (first < 0 || second < 0 || third < 0 || first >= headWeightByVertex.Length ||
                                    second >= headWeightByVertex.Length || third >= headWeightByVertex.Length ||
                                    !AvatarHeadMeshPolicy.IsHeadTriangle(headWeightByVertex[first], headWeightByVertex[second], headWeightByVertex[third])) continue;
                                selected.Add(first); selected.Add(second); selected.Add(third);
                            }
                            trianglesBySubmesh[submesh] = selected;
                            triangleCount += selected.Count / 3;
                        }
                        if (triangleCount == 0) continue;

                        var headMesh = UnityEngine.Object.Instantiate(bakedMesh);
                        spectatorHeadMeshes.Add(headMesh);
                        headMesh.name = "MonMP Spectator Head " + sourceMesh.name;
                        for (int submesh = 0; submesh < trianglesBySubmesh.Length; submesh++)
                            headMesh.SetTriangles(trianglesBySubmesh[submesh], submesh, false);

                        // BakeMesh vertices are relative to the source renderer. Rebase all
                        // geometry into Head-local coordinates so the static proxy follows the
                        // posed/floating head. Normals use the inverse-transpose; tangents use
                        // the forward transform, are orthogonalized, and account for mirrors.
                        var rendererToHead = head.worldToLocalMatrix * sourceRenderer.transform.localToWorldMatrix;
                        var vertices = bakedMesh.vertices;
                        for (int i = 0; i < vertices.Length; i++) vertices[i] = rendererToHead.MultiplyPoint3x4(vertices[i]);
                        headMesh.vertices = vertices;
                        var bakedNormals = bakedMesh.normals;
                        Vector3[] headNormals;
                        if (bakedNormals.Length == vertices.Length)
                        {
                            var normalToHead = rendererToHead.inverse.transpose;
                            for (int i = 0; i < bakedNormals.Length; i++)
                            {
                                var normal = normalToHead.MultiplyVector(bakedNormals[i]);
                                bakedNormals[i] = normal.sqrMagnitude > 0.000001f ? normal.normalized : Vector3.up;
                            }
                            headNormals = bakedNormals;
                            headMesh.normals = headNormals;
                        }
                        else
                        {
                            headMesh.RecalculateNormals();
                            headNormals = headMesh.normals;
                        }
                        var bakedTangents = bakedMesh.tangents;
                        if (bakedTangents.Length == vertices.Length)
                        {
                            float handedness = rendererToHead.determinant < 0f ? -1f : 1f;
                            for (int i = 0; i < bakedTangents.Length; i++)
                            {
                                var tangent = rendererToHead.MultiplyVector(new Vector3(bakedTangents[i].x, bakedTangents[i].y, bakedTangents[i].z));
                                if (headNormals.Length == bakedTangents.Length)
                                    tangent -= headNormals[i] * Vector3.Dot(headNormals[i], tangent);
                                tangent = tangent.sqrMagnitude > 0.000001f ? tangent.normalized : Vector3.right;
                                bakedTangents[i] = new Vector4(tangent.x, tangent.y, tangent.z, bakedTangents[i].w * handedness);
                            }
                            headMesh.tangents = bakedTangents;
                        }
                        headMesh.RecalculateBounds();

                        var proxyObject = new GameObject("MonMP Spectator Head Mesh");
                        spectatorHeadObjects.Add(proxyObject);
                        proxyObject.transform.SetParent(head, false);
                        proxyObject.transform.localPosition = Vector3.zero;
                        proxyObject.transform.localRotation = Quaternion.identity;
                        proxyObject.transform.localScale = Vector3.one;
                        var filter = proxyObject.AddComponent<MeshFilter>();
                        filter.sharedMesh = headMesh;
                        var proxy = proxyObject.AddComponent<MeshRenderer>();
                        spectatorHeadRenderers.Add(proxy);
                        proxy.sharedMaterials = sourceRenderer.sharedMaterials;
                        proxy.receiveShadows = sourceRenderer.receiveShadows;
                        proxy.shadowCastingMode = sourceRenderer.shadowCastingMode;
                        proxy.lightProbeUsage = sourceRenderer.lightProbeUsage;
                        proxy.reflectionProbeUsage = sourceRenderer.reflectionProbeUsage;
                        proxy.probeAnchor = sourceRenderer.probeAnchor;
                        proxy.sortingLayerID = sourceRenderer.sortingLayerID;
                        proxy.sortingOrder = sourceRenderer.sortingOrder;
                        proxy.enabled = false;
                        SpectatorHeadTriangleCount += triangleCount;
                    }
                    finally
                    {
                        UnityEngine.Object.Destroy(bakedMesh);
                    }
                }
            }
            catch (Exception ex)
            {
                foreach (var proxyObject in spectatorHeadObjects)
                    if (proxyObject != null) UnityEngine.Object.Destroy(proxyObject);
                foreach (var mesh in spectatorHeadMeshes)
                    if (mesh != null) UnityEngine.Object.Destroy(mesh);
                spectatorHeadObjects.Clear(); spectatorHeadMeshes.Clear(); spectatorHeadRenderers.Clear();
                SpectatorHeadTriangleCount = 0;
                Debug.LogWarning("MonMP spectator head extraction unavailable; using the mannequin head: " + ex.Message);
            }
        }

        internal float HeadHeight => body.transform.parent.InverseTransformPoint(head.position).y;

        internal Transform RightHand => handR;
        internal Transform LeftHand => handL;
        internal Transform LeftFoot => footL;
        internal Transform RightFoot => footR;
        // The middle of the hand, between the wrist bone and the finger ends.
        internal Vector3 LeftPalm => Palm(handL);
        internal Vector3 RightPalm => Palm(handR);
        // Where the look points for hand-held things: the head's yaw and pitch on the body.
        internal Quaternion Aim { get; private set; } = Quaternion.identity;
        // Where a two-handed map or console sits between the hands, facing the eyes.
        internal Vector3 HeldUpCenter { get; private set; }
        internal Vector3 BinocularEyePosition => head.TransformPoint(binocularEyeLocal);
        internal Quaternion BodyRotation => body.transform.rotation;
        internal Vector3 MapCenter
        {
            get
            {
                var forward = Vector3.ProjectOnPlane(Aim * Vector3.forward, body.transform.up);
                forward = forward.sqrMagnitude < .01f ? body.transform.forward : forward.normalized;
                return neck.position + (forward * .36f - body.transform.up * .12f) * VisualScale;
            }
        }
        internal Vector3 Mouth => head.position + (Aim * new Vector3(0, -0.07f, 0.1f)) * body.transform.lossyScale.y;
        // Forget the smoothed ground under the feet (after a preview on temporary geometry).
        // Preview only: how far down the body is (0 upright, 1 lying).
        internal void SetLieBlend(float value) { lieBlend = Mathf.Clamp01(value); presented = true; }
        internal void ResetFeet() { footOffset[0] = footOffset[1] = 0; footNormal[0] = footNormal[1] = Vector3.up; }

        // Poses the body for this frame from the base pose, walk and crouch plus the overlay.
        // gaitSpeed is the ground speed in m/s.
        internal void Present(Mode mode, float walk, float gaitSpeed, float crouch, Overlay overlay, float deltaTime)
        {
            TryApplyShirtFabric();
            TryApplySleeveMask();
            this.mode = mode;
            bool lying = mode == Mode.Lie || mode == Mode.Down || mode == Mode.Curl;
            if (lying) { walk = 0; crouch = 0; overlay.Air = 0; }
            // A crouched walk takes shorter, flatter steps.
            walk *= 1f - 0.45f * crouch;
            var basePose = mode == Mode.Drive ? source.Drive : mode == Mode.Sit ? source.Sit : mode == Mode.Chair || mode == Mode.Ground ? source.Chair : source.Stand;
            var baseHips = mode == Mode.Drive ? source.DriveHips : mode == Mode.Sit ? source.SitHips : mode == Mode.Chair || mode == Mode.Ground ? source.ChairHips : source.StandHips;
            // Lying on the back with the feet at the root and the head behind it; curled up on the
            // left side along the same line.
            if (lying)
            {
                lieRotation = mode == Mode.Curl ? Quaternion.Euler(0, 0, 90f) * Quaternion.Euler(-90f, 0, 0) : Quaternion.Euler(-90f, 0, 0);
                liePosition = mode == Mode.Lie ? new Vector3(0, 0.25f, 0) : mode == Mode.Down ? new Vector3(0, 0.12f, 0) : new Vector3(0, 0.18f, 0);
            }
            // The dead drop in under a second, a sleeper lies down and anyone gets up a little slower.
            if (!presented) lieBlend = lying ? 1f : 0f;
            presented = true;
            lieBlend = Mathf.MoveTowards(lieBlend, lying ? 1f : 0f, deltaTime * (!lying ? 1.4f : mode == Mode.Down ? 1.3f : 0.8f));
            float lie = lying && mode == Mode.Down ? lieBlend * lieBlend : Mathf.SmoothStep(0, 1, lieBlend);
            AvatarTransformWrites.LocalRotation(body.transform, Quaternion.Slerp(Quaternion.Euler(0, mode == Mode.Stand ? overlay.BodyYaw : 0, 0), lieRotation, lie));
            AvatarTransformWrites.LocalPosition(body.transform, Vector3.Lerp(Vector3.zero, liePosition, lie));
            if (mode == Mode.Down) { overlay.HeadYaw = 30f; overlay.Pitch = 10f; }
            if (mode == Mode.Curl) { overlay.HeadYaw = 0; overlay.Pitch = 30f; }
            Array.Copy(basePose, frame, frame.Length);
            var hipsPosition = baseHips;
            standStill = mode == Mode.Stand ? Mathf.Clamp01(1f - walk) : 0f;

            if (mode == Mode.Stand && walk > 0.001f && walkGraph.IsValid())
            {
                gaitTime = Mathf.Repeat(gaitTime + deltaTime * Mathf.Clamp(gaitSpeed / WalkStrideSpeed, 0.5f, 2.2f), source.Walk!.length);
                StockAvatarWalkGraph.Evaluate(walkGraph, walkPlayable, gaitTime, body.transform, armature);
                for (int i = 0; i < bones.Length; i++)
                {
                    bool zoomFinger = overlay.Zoom > .001f &&
                        (source.Bones[i].StartsWith("Fingers", StringComparison.Ordinal) || source.Bones[i].StartsWith("Thumb", StringComparison.Ordinal));
                    frame[i] = zoomFinger ? basePose[i] : Quaternion.Slerp(basePose[i], bones[i].localRotation, walk);
                }
                // The clip's hip bob is flattened further under a crouch.
                hipsPosition = Vector3.Lerp(baseHips, hips.localPosition, walk * (1f - 0.6f * crouch));
            }
            Write(frame, hipsPosition);

            var up = body.transform.up;
            var right = body.transform.right;
            if (mode == Mode.Stand)
            {
                if (crouch > 0.001f)
                {
                    // Squat: knees forward, shins back, feet flat, back leaning over the knees.
                    float thigh = CrouchThighAngle * crouch;
                    for (int leg = 0; leg < 2; leg++)
                    {
                        thighs[leg].rotation = Quaternion.AngleAxis(-thigh, right) * thighs[leg].rotation;
                        shins[leg].rotation = Quaternion.AngleAxis(2f * thigh, right) * shins[leg].rotation;
                        feet[leg].rotation = Quaternion.AngleAxis(-thigh, right) * feet[leg].rotation;
                    }
                    spine.rotation = Quaternion.AngleAxis(25f * crouch, right) * spine.rotation;
                }
                if (overlay.Air > 0.001f)
                {
                    // Off the ground: knees up, feet trailing, whatever the walk was doing.
                    float air = overlay.Air;
                    for (int leg = 0; leg < 2; leg++)
                    {
                        thighs[leg].rotation = Quaternion.AngleAxis(-(leg == 0 ? 45f : 30f) * air, right) * thighs[leg].rotation;
                        shins[leg].rotation = Quaternion.AngleAxis((leg == 0 ? 80f : 65f) * air, right) * shins[leg].rotation;
                        feet[leg].rotation = Quaternion.AngleAxis(-20f * air, right) * feet[leg].rotation;
                    }
                }
                // Keep the feet on the walker's ground plane through the walk cycle and crouch (not
                // in the air, where the legs draw up under standing hips). A crouched walk swings
                // the lowest foot through the gait, so there the lift is smoothed against bobbing.
                float correction = standFootHeight - FootHeight();
                footCorrection = crouch > 0.01f && walk > 0.01f ? Mathf.Lerp(footCorrection, correction, Mathf.Clamp01(deltaTime * 10f)) : correction;
                hips.position += up * footCorrection * (1f - overlay.Air) * body.transform.lossyScale.y;
            }
            else if (mode == Mode.Ground)
            {
                // From the chair pose: knees up (thighs raised about 35 degrees), shins down to the floor
                // in front, feet flat, so bed passengers facing each other keep their feet to themselves.
                for (int leg = 0; leg < 2; leg++)
                {
                    shins[leg].rotation = Quaternion.AngleAxis(leg == 0 ? -10f : -22f, right) * shins[leg].rotation;
                    thighs[leg].rotation = Quaternion.AngleAxis(leg == 0 ? -40f : -30f, right) * thighs[leg].rotation;
                    feet[leg].rotation = Quaternion.AngleAxis(leg == 0 ? 50f : 52f, right) * feet[leg].rotation;
                }
                spine.rotation = Quaternion.AngleAxis(-8f, right) * spine.rotation;
            }
            else if (mode == Mode.Curl)
            {
                // Knees drawn up to the chest, shins folded back, back rounded.
                for (int leg = 0; leg < 2; leg++)
                {
                    thighs[leg].rotation = Quaternion.AngleAxis(-(leg == 0 ? 85f : 75f), right) * thighs[leg].rotation;
                    shins[leg].rotation = Quaternion.AngleAxis(115f, right) * shins[leg].rotation;
                    feet[leg].rotation = Quaternion.AngleAxis(-25f, right) * feet[leg].rotation;
                }
                spine.rotation = Quaternion.AngleAxis(22f, right) * spine.rotation;
            }

            // Emote head gestures add to the look pitch (negative tips the head back).
            float gesturePitch = 0, spineGesture = 0;
            if (overlay.SlurTime >= 0 && overlay.SlurTime < SlurSeconds)
            {
                float e = Mathf.Sin(Mathf.PI * overlay.SlurTime / SlurSeconds);
                gesturePitch -= 14f * e; spineGesture -= 5f * e;
            }
            if (overlay.SpitTime >= 0 && overlay.SpitTime < SpitSeconds)
            {
                float t = overlay.SpitTime, windup;
                if (t < SpitWindup) windup = -20f * Mathf.SmoothStep(0, 1, t / SpitWindup);
                else if (t < SpitRelease) windup = Mathf.Lerp(-20f, 16f, (t - SpitWindup) / (SpitRelease - SpitWindup));
                else windup = Mathf.Lerp(16f, 0f, (t - SpitRelease) / (SpitSeconds - SpitRelease));
                gesturePitch += windup; spineGesture += 0.3f * windup;
            }
            if (overlay.VomitTime >= 0 && overlay.VomitTime < overlay.VomitSeconds)
            {
                // Doubled over for the length of the native vomit sound.
                float e = Mathf.Min(Mathf.SmoothStep(0, 1, overlay.VomitTime / 0.35f), Mathf.SmoothStep(0, 1, (overlay.VomitSeconds - overlay.VomitTime) / 0.5f));
                gesturePitch += 30f * e; spineGesture += 32f * e;
            }
            if (overlay.PetTime >= 0 && overlay.PetTime < PetSeconds && !lying)
            {
                // Bending down to Leo.
                float e = Mathf.Sin(Mathf.PI * overlay.PetTime / PetSeconds);
                gesturePitch += 20f * e; spineGesture += 38f * e;
            }
            if (!lying && (overlay.Drunk > 0.001f || overlay.Hangover > 0.001f))
            {
                // Drunk: a slow, uneven sway of the upper body with the head lolling against it and
                // a lazy nod. Hungover: slumped forward with the head hanging.
                float d = overlay.Drunk, h = overlay.Hangover * (1f - d), t = overlay.Clock;
                float sway = Mathf.Sin(t * 1.1f) + 0.5f * Mathf.Sin(t * 2.3f + 1.7f);
                float nod = Mathf.Sin(t * 0.8f + 0.6f);
                var forward = body.transform.forward;
                spine.rotation = Quaternion.AngleAxis(6f * d * sway, forward) * spine.rotation;
                head.rotation = Quaternion.AngleAxis(-9f * d * sway, forward) * head.rotation;
                if (mode == Mode.Stand) hips.position += right * (0.03f * d * sway * body.transform.lossyScale.y);
                spineGesture += 4f * d * nod + 12f * h;
                gesturePitch += 6f * d * (nod + 0.5f) + 16f * h;
            }
            if (spineGesture != 0) spine.rotation = Quaternion.AngleAxis(spineGesture, right) * spine.rotation;

            // Look: yaw about the body's up, then pitch about the turned head's right.
            float yaw = StockGesturePoseMath.ClampHeadYaw(overlay.HeadYaw), pitch = StockGesturePoseMath.ClampHeadPitch(overlay.Pitch + gesturePitch);
            var lookRight = Quaternion.AngleAxis(yaw, up) * right;
            neck.rotation = Quaternion.AngleAxis(pitch * 0.35f, lookRight) * Quaternion.AngleAxis(yaw * 0.4f, up) * neck.rotation;
            head.rotation = Quaternion.AngleAxis(pitch * 0.65f, lookRight) * Quaternion.AngleAxis(yaw * 0.6f, up) * head.rotation;
            float aimPitch = StockGesturePoseMath.ClampAimPitch(overlay.Pitch);
            Aim = Quaternion.AngleAxis(aimPitch, lookRight) * Quaternion.AngleAxis(yaw, up) * body.transform.rotation;
            if (lying) overlay.Sorry = overlay.Map = overlay.Console = overlay.Flashlight = overlay.Joint = overlay.Smoking = 0;
            this.overlay = overlay;
        }

        // Entered once on the spectator transition. Save the exact ordinary pose so exiting
        // spectating restores the same presentation before the next ordinary Update.
        internal void BeginSpectatorHeadOnly()
        {
            if (spectatorHeadOnlyActive) return;
            savedSpectatorBodyPosition = body.transform.localPosition;
            savedSpectatorBodyRotation = body.transform.localRotation;
            savedSpectatorNeckRotation = neck.localRotation;
            savedSpectatorHeadRotation = head.localRotation;
            spectatorHeadOnlyActive = true;
        }

        // A spectator renders a static head proxy and Hair. Reset the body child to its ordinary
        // standing frame, then update only the neck/head bones and float that head at the camera.
        internal void PresentSpectatorHead(Vector3 target, float bodyYaw, float headYaw, float pitch)
        {
            if (!spectatorHeadOnlyActive) BeginSpectatorHeadOnly();
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.Euler(0f, bodyYaw, 0f);
            neck.localRotation = neutralNeckRotation;
            head.localRotation = neutralHeadRotation;

            var up = body.transform.up;
            var right = body.transform.right;
            float yaw = StockGesturePoseMath.ClampHeadYaw(headYaw);
            float lookPitch = StockGesturePoseMath.ClampHeadPitch(pitch);
            var lookRight = Quaternion.AngleAxis(yaw, up) * right;
            neck.rotation = Quaternion.AngleAxis(lookPitch * 0.35f, lookRight) *
                Quaternion.AngleAxis(yaw * 0.4f, up) * neck.rotation;
            head.rotation = Quaternion.AngleAxis(lookPitch * 0.65f, lookRight) *
                Quaternion.AngleAxis(yaw * 0.6f, up) * head.rotation;
            float aimPitch = StockGesturePoseMath.ClampAimPitch(pitch);
            Aim = Quaternion.AngleAxis(aimPitch, lookRight) * Quaternion.AngleAxis(yaw, up) * body.transform.rotation;

            body.transform.position += target - head.position;
        }

        // Seated states place the head where the seated player's camera is.
        internal void AlignHead(Vector3 target) => body.transform.position += target - head.position;

        internal void SetPresentationScale(float scale)
        {
            presentationScale = Mathf.Clamp(scale, 0.8f, 1f);
            AvatarTransformWrites.LocalScale(body.transform, nativeScale * presentationScale);
        }

        internal void AlignSeatHead(Vector3 target)
        {
            // Preserve the original hip anchor while shortening the torso. Scaling and then
            // pinning the head at the old height would instead lift the hips through the seat.
            body.transform.position += target - head.position -
                (head.position - hips.position) * (1f / presentationScale - 1f);
        }

        internal void RestoreSpectatorHead()
        {
            if (!spectatorHeadOnlyActive) return;
            body.transform.localPosition = savedSpectatorBodyPosition;
            body.transform.localRotation = savedSpectatorBodyRotation;
            neck.localRotation = savedSpectatorNeckRotation;
            head.localRotation = savedSpectatorHeadRotation;
            spectatorHeadOnlyActive = false;
        }
        // Sitting on a surface: the pelvis rests just above it.
        internal Vector3 HipsPosition => hips.position;
        internal void AlignHips(Vector3 surface) => body.transform.position += surface + body.transform.up * (0.1f * body.transform.lossyScale.y) - hips.position;

        // Hands and feet, after the body has its final placement for the frame. Each hand takes the
        // first goal that applies: grips, a carried item, a short gesture, a held-up map or console,
        // the flashlight or joint (left), the Sorry raise (right), resting on the thighs when seated.
        internal void Solve(Goals goals, float deltaTime) => Solve(goals, deltaTime, out _);

        internal void Solve(Goals goals, float deltaTime, out Vector3 heldTranslation)
        {
            heldTranslation = Vector3.zero;
            var up = body.transform.up;
            var right = body.transform.right;
            var forward = body.transform.forward;
            float scale = body.transform.lossyScale.y;
            bool seatFeet = (mode == Mode.Sit || mode == Mode.Chair || mode == Mode.Drive || mode == Mode.Ground) &&
                (goals.SeatFootLeft.HasValue || goals.SeatFootRight.HasValue);
            if (seatFeet)
            {
                // Solve before a working hand goal can return. Saved contacts use leg rotations
                // only: ground rays and hip lowering would disturb the calibrated body/head.
                SolveSeatFoot(0, goals.SeatFootLeft, goals.SeatFootLeftRotation, forward);
                SolveSeatFoot(1, goals.SeatFootRight, goals.SeatFootRightRotation, forward);
            }
            else
            {
                if (goals.Feet) PlaceFeet(up, forward, deltaTime);
            }
            // Foot placement can lower the hips and both shoulders. Constrain only after that
            // final body placement, then solve arms and move the observer's item by this delta.
            if (goals.ConstrainHeld && goals.Held.HasValue) heldTranslation = ConstrainHeld(ref goals);
            if (mode == Mode.Lie || mode == Mode.Down || mode == Mode.Curl)
            {
                // On the back (up points to the head): arms at the sides asleep, flung out when down,
                // tucked in front of the chest curled up. The arms follow the body down as it falls.
                bool down = mode == Mode.Down, curl = mode == Mode.Curl;
                float weight = Mathf.SmoothStep(0, 1, lieBlend);
                for (int arm = 0; arm < 2; arm++)
                {
                    var side = arm == 0 ? -right : right;
                    var rest = curl ? neck.position + (forward * 0.2f - up * (arm == 0 ? 0.26f : 0.18f) + side * 0.04f) * scale
                        : down ? neck.position + (side * 0.6f - up * 0.15f - forward * 0.05f) * scale
                        : hips.position + (side * 0.27f + up * 0.02f - forward * 0.02f) * scale;
                    var pole = curl ? side * 0.4f - up : side * 0.6f - forward;
                    // A creeper rider reaches the held tool up over the chest with the right hand (#209).
                    if (arm == 1 && mode == Mode.Lie && goals.Held.HasValue && (goals.HeldLeft.HasValue || goals.HeldWidth <= 0.01f))
                    {
                        if (goals.HeldRotation.HasValue)
                            Grip(upperArmR, lowerArmR, handR, goals.Held.Value, side * 0.6f - up * 0.3f, forward, goals.HeldRotation);
                        else Reach(upperArmR, lowerArmR, handR, goals.Held.Value, side * 0.6f - up * 0.3f, weight);
                        continue;
                    }
                    if (arm == 0 && mode == Mode.Lie && goals.Held.HasValue && goals.HeldLeft.HasValue)
                    { Grip(upperArmL, lowerArmL, handL, goals.HeldLeft.Value, side * 0.6f - up * 0.3f, forward, goals.HeldLeftRotation); continue; }
                    if (arm == 0) Reach(upperArmL, lowerArmL, handL, rest, pole, weight);
                    else Reach(upperArmR, lowerArmR, handR, rest, pole, weight);
                }
                // The creeper admits cosmetic gestures with clear hands. Preserve every held or
                // working grip above, then layer the same bounded right-arm/finger solve as upright.
                if (mode == Mode.Lie && !goals.Held.HasValue && !goals.HeldLeft.HasValue &&
                    !goals.GripLeft.HasValue && !goals.GripRight.HasValue)
                    SolveCosmeticGesture(up, right, forward, scale);
                return;
            }

            bool leftBusy = false, rightBusy = false;
            if (goals.GripLeft.HasValue && goals.GripRight.HasValue)
            {
                if (goals.Push)
                {
                    // Wheelbarrow handles: elbows back and a little out, fingers over the handles.
                    Grip(upperArmL, lowerArmL, handL, goals.GripLeft.Value, -forward - right * 0.4f - up * 0.3f, forward - up * 0.6f);
                    Grip(upperArmR, lowerArmR, handR, goals.GripRight.Value, -forward + right * 0.4f - up * 0.3f, forward - up * 0.6f);
                    return;
                }

                bool driverRightOverlay = StockGesturePoseMath.DriverReleasesRightHand(
                    mode == Mode.Drive, IsGestureActive(overlay), overlay.Sorry) || overlay.Zoom > 0.001f;
                if (!driverRightOverlay)
                {
                    // Elbows out and down, palms on the rim and the fingers over it towards the hub.
                    var hub = goals.GripCenter ?? (goals.GripLeft.Value + goals.GripRight.Value) * 0.5f;
                    Grip(upperArmL, lowerArmL, handL, goals.GripLeft.Value, -right - up * 0.6f, forward + (hub - goals.GripLeft.Value).normalized * 0.6f, goals.GripLeftRotation);
                    Grip(upperArmR, lowerArmR, handR, goals.GripRight.Value, right - up * 0.6f, forward + (hub - goals.GripRight.Value).normalized * 0.6f, goals.GripRightRotation);
                    return;
                }
                // A seated driver keeps the left hand on the wheel while a cosmetic gesture can
                // temporarily take the right hand. The seat and vehicle still own the body frame;
                // only the remote arm presentation changes for the gesture's short duration.
                var driverHub = goals.GripCenter ?? (goals.GripLeft.Value + goals.GripRight.Value) * 0.5f;
                Grip(upperArmL, lowerArmL, handL, goals.GripLeft.Value, -right - up * 0.6f, forward + (driverHub - goals.GripLeft.Value).normalized * 0.6f, goals.GripLeftRotation);
                // The driver's left grip stays fixed; let the normal right-hand overlay solve run.
                leftBusy = true;
            }
            if (goals.Held.HasValue)
            {
                if (goals.HeldLeft.HasValue)
                {
                    Grip(upperArmL, lowerArmL, handL, goals.HeldLeft.Value, -right * 0.4f - up * 0.7f - forward * 0.3f, forward - up * 0.3f, goals.HeldLeftRotation);
                    Grip(upperArmR, lowerArmR, handR, goals.Held.Value, right * 0.4f - up * 0.7f - forward * 0.3f, forward - up * 0.3f, goals.HeldRotation);
                    return;
                }
                if (goals.HeldWidth > 0.01f)
                {
                    // Large items are carried with both hands either side of them.
                    var side = right * Mathf.Min(goals.HeldWidth * 0.5f, 0.3f);
                    Grip(upperArmL, lowerArmL, handL, goals.Held.Value - side, -right - up * 0.7f - forward * 0.2f, right + forward * 0.5f);
                    Grip(upperArmR, lowerArmR, handR, goals.Held.Value + side, right - up * 0.7f - forward * 0.2f, -right + forward * 0.5f, goals.HeldRotation);
                    return;
                }
                Grip(upperArmR, lowerArmR, handR, goals.Held.Value, right * 0.4f - up * 0.7f - forward * 0.3f, forward - up * 0.3f, goals.HeldRotation);
                rightBusy = true;
            }

            var aimForward = Aim * Vector3.forward;
            var lookRight = Aim * Vector3.right;
            var lookForward = Vector3.ProjectOnPlane(aimForward, up);
            lookForward = lookForward.sqrMagnitude < 0.01f ? forward : lookForward.normalized;
            var chest = neck.position;

            // Short right-hand gestures win over held states for their duration.
            if (!rightBusy && overlay.InteractTime >= 0 && overlay.InteractTime < InteractSeconds)
            {
                float e = Mathf.Sin(Mathf.PI * overlay.InteractTime / InteractSeconds);
                Reach(upperArmR, lowerArmR, handR, upperArmR.position + aimForward * 0.62f * scale, right - up * 0.8f, e);
                rightBusy = true;
            }
            if (!rightBusy && overlay.PetTime >= 0 && overlay.PetTime < PetSeconds)
            {
                float t = overlay.PetTime, e = Mathf.Sin(Mathf.PI * t / PetSeconds);
                var pat = hips.position + (lookForward * (0.5f + 0.05f * Mathf.Sin(t * 4f * Mathf.PI)) - up * 0.55f + right * 0.1f) * scale;
                Reach(upperArmR, lowerArmR, handR, pat, right - forward * 0.3f, e);
                rightBusy = true;
            }

            if (overlay.Zoom > 0.001f && !rightBusy)
            {
                var center = BinocularEyePosition + aimForward * (.035f * scale);
                if (!leftBusy) Grip(upperArmL, lowerArmL, handL, center - lookRight * 0.075f * scale,
                    -right - up * 0.7f, Aim * Vector3.up, Aim * binocularPalmLeft);
                Grip(upperArmR, lowerArmR, handR, center + lookRight * 0.075f * scale,
                    right - up * 0.7f, Aim * Vector3.up, Aim * binocularPalmRight);
                var curlAxis = Aim * Vector3.forward;
                if (fingersR != null) CurlJoint(fingersR, curlAxis, 65f);
                if (fingersDistalR != null) CurlJoint(fingersDistalR, curlAxis, 45f);
                if (!leftBusy)
                {
                    if (fingersL != null) CurlJoint(fingersL, -curlAxis, 65f);
                    if (fingersDistalL != null) CurlJoint(fingersDistalL, -curlAxis, 45f);
                }
                return;
            }

            float twoHands = Mathf.Max(overlay.Map, overlay.Console);
            if (twoHands > 0.001f)
            {
                bool map = overlay.Map >= overlay.Console;
                var center = chest + (lookForward * (map ? 0.36f : 0.3f) - up * (map ? 0.12f : 0.22f)) * scale;
                HeldUpCenter = center;
                var half = lookRight * (map ? 0.19f : 0.09f) * scale;
                if (map && goals.MapProp != null && goals.MapCalibration != null)
                {
                    var pose = goals.MapCalibration;
                    RemoteMapCalibration.Place(goals.MapProp, this, pose);
                    var frame = goals.MapProp.Object.transform;
                    if (pose.TwoHanded) Grip(upperArmL, lowerArmL, handL, frame.TransformPoint(pose.LeftPosition), -lookRight - up * .8f,
                        lookForward, frame.rotation * Quaternion.Euler(pose.LeftEuler));
                    else Reach(upperArmL, lowerArmL, handL, center - half, -lookRight - up * .8f, twoHands);
                    if (!rightBusy) Grip(upperArmR, lowerArmR, handR, frame.TransformPoint(pose.Position), lookRight - up * .8f,
                        lookForward, frame.rotation * Quaternion.Euler(pose.Euler));
                    return;
                }
                Reach(upperArmL, lowerArmL, handL, center - half, -lookRight - up * 0.8f, twoHands);
                if (!rightBusy) Reach(upperArmR, lowerArmR, handR, center + half, lookRight - up * 0.8f, twoHands);
                return;
            }

            if (overlay.Flashlight > 0.001f)
            {
                // Held out at chest height with a bent elbow, pointing where the player looks.
                Reach(upperArmL, lowerArmL, handL, upperArmL.position + (aimForward * 0.4f + lookRight * 0.1f - up * 0.1f) * scale, -right - up, overlay.Flashlight);
                if (overlay.Flashlight > 0.5f) Bend(handL, aimForward, 0.6f);
                if (overlay.Flashlight > .5f && goals.FlashlightProp != null)
                {
                    var prop = goals.FlashlightProp; var pose = goals.FlashlightCalibration;
                    RemoteFlashlightCalibration.Place(prop, this, pose);
                    if (pose != null) Grip(upperArmL, lowerArmL, handL, prop.Object.transform.TransformPoint(pose.Position),
                        -right - up, aimForward, prop.Object.transform.rotation * Quaternion.Euler(pose.Euler));
                }
                leftBusy = true;
            }
            if (!leftBusy && overlay.Joint > 0.001f)
            {
                // The joint rests in the left hand in front of the chest and goes to the mouth for a puff.
                var idle = chest + (lookForward * 0.28f - lookRight * 0.12f - up * 0.32f) * scale;
                var mouth = Mouth + (lookForward * 0.02f - lookRight * 0.02f) * scale;
                Reach(upperArmL, lowerArmL, handL, Vector3.Lerp(idle, mouth, overlay.Smoking), -right - up * 0.9f, overlay.Joint);
                leftBusy = true;
            }
            if (!rightBusy && overlay.Sorry > 0.001f)
            {
                // Sorry: the right hand raised beside the head, elbow out to the side.
                var raised = head.position + (right * 0.26f + forward * 0.12f + up * 0.08f) * scale;
                Reach(upperArmR, lowerArmR, handR, raised, right - up * 0.5f, overlay.Sorry, straightWrist: false);
                rightBusy = true;
            }
            if (!rightBusy) rightBusy = SolveCosmeticGesture(up, right, forward, scale);
            if (!leftBusy && goals.SeatLeft.HasValue)
            {
                Grip(upperArmL, lowerArmL, handL, goals.SeatLeft.Value, -right * 0.5f - forward, forward, goals.SeatLeftRotation);
                leftBusy = true;
            }
            if (!rightBusy && goals.SeatRight.HasValue)
            {
                Grip(upperArmR, lowerArmR, handR, goals.SeatRight.Value, right * 0.5f - forward, forward, goals.SeatRightRotation);
                rightBusy = true;
            }
            // Hands with nothing to do hang from the wrist in line with the forearm (the base poses
            // bend them out nearly flat).
            if (standStill > 0.001f)
            {
                // Standing still, free arms hang at the sides (the base pose holds them forward);
                // the walk clip's own arm swing takes over as the avatar walks.
                for (int arm = 0; arm < 2; arm++)
                {
                    if (arm == 0 ? leftBusy : rightBusy) continue;
                    var side = arm == 0 ? -right : right;
                    var upper = arm == 0 ? upperArmL : upperArmR;
                    var rest = upper.position + (-up * 0.52f + side * 0.07f - forward * 0.02f) * scale;
                    if (arm == 0) Reach(upperArmL, lowerArmL, handL, rest, side * 0.3f - forward, standStill);
                    else Reach(upperArmR, lowerArmR, handR, rest, side * 0.3f - forward, standStill);
                }
            }
            if (mode == Mode.Stand || mode == Mode.Drive)
            {
                if (!leftBusy) Straighten(lowerArmL, handL, 0.7f);
                if (!rightBusy) Straighten(lowerArmR, handR, 0.7f);
            }
            if (mode == Mode.Sit || mode == Mode.Chair || mode == Mode.Ground)
            {
                // Seated hands rest on the thighs near the knees.
                for (int leg = 0; leg < 2; leg++)
                {
                    if (leg == 0 ? leftBusy : rightBusy) continue;
                    var side = leg == 0 ? -right : right;
                    var rest = Vector3.Lerp(thighs[leg].position, shins[leg].position, 0.7f) + (up * 0.07f + side * 0.02f) * scale;
                    if (leg == 0) Reach(upperArmL, lowerArmL, handL, rest, side * 0.5f - forward, 1f);
                    else Reach(upperArmR, lowerArmR, handR, rest, side * 0.5f - forward, 1f);
                }
            }
        }

        // Shared by upright/seated poses and the hands-free creeper. The caller owns hand
        // priority; this method owns only the short right-arm gesture and finger overlay.
        private bool SolveCosmeticGesture(Vector3 up, Vector3 right, Vector3 forward, float scale)
        {
            // Ordinary idle hands have no cosmetic solve. Check the same bounded gesture
            // window before projecting aim or reading native bone positions.
            if (!IsGestureActive(overlay)) return false;
            var aimForward = Aim * Vector3.forward;
            var lookRight = Aim * Vector3.right;
            var lookForward = Vector3.ProjectOnPlane(aimForward, up);
            lookForward = lookForward.sqrMagnitude < 0.01f ? forward : lookForward.normalized;
            // Pointing from a seat or creeper follows the full head/camera direction, including pitch. The
            // standing gesture keeps its established level arm line.
            var pointMath = StockGesturePoseMath.PointDirection(
                mode == Mode.Sit || mode == Mode.Chair || mode == Mode.Drive || mode == Mode.Ground || mode == Mode.Lie,
                new GestureVector(aimForward.x, aimForward.y, aimForward.z), new GestureVector(up.x, up.y, up.z),
                new GestureVector(forward.x, forward.y, forward.z));
            var pointForward = new Vector3(pointMath.X, pointMath.Y, pointMath.Z);
            if (overlay.PointTarget is Vector3 pingTarget && (pingTarget - upperArmR.position).sqrMagnitude > .01f)
                pointForward = (pingTarget - upperArmR.position).normalized;
            var chest = neck.position;
            float gestureSeconds = GestureDuration(overlay.Gesture);
            if (gestureSeconds > 0f && overlay.GestureTime >= 0f && overlay.GestureTime < gestureSeconds)
            {
                float fadeIn = Mathf.SmoothStep(0, 1, Mathf.Clamp01(overlay.GestureTime / 0.16f));
                float fadeOut = Mathf.SmoothStep(0, 1, Mathf.Clamp01((gestureSeconds - overlay.GestureTime) / 0.24f));
                float weight = Mathf.Min(fadeIn, fadeOut);
                if (weight > 0.001f)
                {
                    var calibration = EmotePoseOverrides.Active?.Get(EmotePoseOverrides.ViewKind.Remote, overlay.Gesture);
#if MONMP_DEVELOPMENT
                    calibration = EmotePosePreview ?? calibration;
#endif
                    var wristOffset = calibration != null ?
                        (right * calibration.X + up * calibration.Y + forward * calibration.Z) * scale : Vector3.zero;
                    var poleOffset = calibration != null ?
                        right * calibration.ElbowX + up * calibration.ElbowY + forward * calibration.ElbowZ : Vector3.zero;
                    switch (overlay.Gesture)
                    {
                        case PlayerEmoteKind.Wave:
                            // Raise the right hand beside the head and wave at the wrist and elbow.
                            float wave = overlay.GestureTime * 8f;
                            if (calibration != null) wave *= calibration.WaveRate;
                            bool seatedWave = mode == Mode.Sit || mode == Mode.Chair || mode == Mode.Drive || mode == Mode.Ground;
                            var waveTargetMath = StockGesturePoseMath.WaveTarget(
                                new GestureVector(head.position.x, head.position.y, head.position.z),
                                new GestureVector(right.x, right.y, right.z), new GestureVector(up.x, up.y, up.z),
                                new GestureVector(forward.x, forward.y, forward.z), wave, scale, seatedWave);
                            var waveTarget = new Vector3(waveTargetMath.X, waveTargetMath.Y, waveTargetMath.Z);
                            float waveScale = calibration?.WaveScale ?? 1f;
                            if (waveScale != 1f)
                            {
                                var center = head.position + (right * (seatedWave ? 0.37f : 0.24f) +
                                    up * (seatedWave ? 0.12f : 0.08f) + forward * (seatedWave ? 0.04f : 0.08f)) * scale;
                                waveTarget = center + (waveTarget - center) * waveScale;
                            }
                            Reach(upperArmR, lowerArmR, handR, waveTarget + wristOffset, right - up * 0.55f + poleOffset, weight, straightWrist: false);
                            Bend(handR, forward + up * (0.25f + 0.2f * Mathf.Sin(wave) * waveScale), 0.35f * weight);
                            break;
                        case PlayerEmoteKind.Point:
                            // Extend toward the current look direction; seated aim includes head pitch.
                            var pointTarget = upperArmR.position + (pointForward * 0.62f + lookRight * 0.08f + up * 0.02f) * scale;
                            Reach(upperArmR, lowerArmR, handR, pointTarget + wristOffset, right - up * 0.65f + poleOffset, weight, straightWrist: false);
                            Bend(handR, pointForward, 0.6f * weight);
                            break;
                        case PlayerEmoteKind.ThumbsUp:
                            // Fold the elbow in and turn the palm upright in front of the chest.
                            var thumbTarget = chest + (lookForward * 0.26f + lookRight * 0.12f - up * 0.05f) * scale;
                            Reach(upperArmR, lowerArmR, handR, thumbTarget + wristOffset, right - up * 0.55f + poleOffset, weight, straightWrist: false);
                            PoseThumbsUpFingers(weight, up);
                            break;
                    }
                    if (calibration != null) ApplyEmoteCalibration(calibration, weight);
                    return true;
                }
            }
            return false;
        }

        private void ApplyEmoteCalibration(EmotePoseOverrides.Pose pose, float weight)
        {
            if (pose.Pitch != 0f || pose.Yaw != 0f || pose.Roll != 0f)
            {
                var basis = body.transform.rotation;
                var rotation = basis * Quaternion.Euler(pose.Pitch, pose.Yaw, pose.Roll) * Quaternion.Inverse(basis);
                handR.rotation = Quaternion.Slerp(Quaternion.identity, rotation, weight) * handR.rotation;
            }
            ApplyEmoteJoint(upperArmR, "Upper Arm.R", pose, weight);
            ApplyEmoteJoint(lowerArmR, "Lower Arm.R", pose, weight);
            ApplyEmoteJoint(handR, "Hand.R", pose, weight);
            ApplyEmoteJoint(fingersR, "Fingers01.R", pose, weight);
            ApplyEmoteJoint(fingersDistalR, "Fingers02.R", pose, weight);
            ApplyEmoteJoint(thumbR, "Thumb01.R", pose, weight);
            ApplyEmoteJoint(thumbDistalR, "Thumb02.R", pose, weight);
        }

        private static void ApplyEmoteJoint(Transform? joint, string name, EmotePoseOverrides.Pose pose, float weight)
        {
            if (joint == null || !pose.Bones.TryGetValue(name, out var rotation)) return;
            if (rotation.X == 0f && rotation.Y == 0f && rotation.Z == 0f) return;
            joint.localRotation *= Quaternion.Slerp(Quaternion.identity,
                Quaternion.Euler(rotation.X, rotation.Y, rotation.Z), weight);
        }

        private void SolveSeatFoot(int leg, Vector3? target, Quaternion? rotation, Vector3 forward)
        {
            if (!target.HasValue || !Finite(target.Value) || !Finite(forward)) return;
            var thigh = thighs[leg]; var shin = shins[leg]; var foot = feet[leg];
            if (!Finite(thigh.position) || !Finite(shin.position) || !Finite(foot.position) ||
                !UsableRotation(thigh.rotation) || !UsableRotation(shin.rotation) || !UsableRotation(foot.rotation)) return;
            float upperLength = (shin.position - thigh.position).magnitude;
            float lowerLength = (foot.position - shin.position).magnitude;
            if (!Finite(upperLength) || !Finite(lowerLength) || upperLength < 0.01f || lowerLength < 0.01f ||
                !Finite((target.Value - thigh.position).sqrMagnitude)) return;
            var footRotation = rotation.HasValue && UsableRotation(rotation.Value) ? rotation.Value : foot.rotation;
            // Knees bend towards the body's front. SolveTwoBone keeps both bone lengths and
            // clamps targets beyond this leg's reach without moving the hips or the other leg.
            SolveTwoBone(thigh, shin, foot, target.Value, forward, 1f);
            foot.rotation = footRotation;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
        private static bool UsableRotation(Quaternion value)
        {
            if (!Finite(value.x) || !Finite(value.y) || !Finite(value.z) || !Finite(value.w)) return false;
            float lengthSquared = value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w;
            return Finite(lengthSquared) && lengthSquared > 1e-8f;
        }

        // Each foot is lifted or dropped onto the ground under it; the hips drop as far as the lower
        // foot needs so that leg can reach, and the other knee bends.
        private void PlaceFeet(Vector3 up, Vector3 forward, float deltaTime)
        {
            var rootPosition = body.transform.parent.position;
            float follow = Mathf.Clamp01(deltaTime * 14f);
            for (int leg = 0; leg < 2; leg++)
            {
                var from = feet[leg].position + up * FootRayHeight;
                float target = 0; var normal = up;
                if (Physics.Raycast(from, -up, out var hit, FootRayHeight + MaxFootOffset + 0.2f, GroundMask, QueryTriggerInteraction.Ignore))
                {
                    target = Mathf.Clamp(Vector3.Dot(hit.point - rootPosition, up), -MaxFootOffset, MaxFootOffset);
                    normal = Vector3.Angle(hit.normal, up) < 35f ? hit.normal : up;
                }
                footOffset[leg] = Mathf.Lerp(footOffset[leg], target, follow);
                footNormal[leg] = Vector3.Slerp(footNormal[leg], normal, follow);
            }
            float drop = Mathf.Min(Mathf.Max(0f, -Mathf.Min(footOffset[0], footOffset[1])), MaxFootOffset);
            hips.position -= up * drop;
            for (int leg = 0; leg < 2; leg++)
            {
                var footRotation = feet[leg].rotation;
                var goal = feet[leg].position + up * (footOffset[leg] + drop);
                SolveTwoBone(thighs[leg], shins[leg], feet[leg], goal, forward, 1f);
                // Keep the foot's own orientation, tilted onto the ground.
                feet[leg].rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(up, footNormal[leg]), 0.7f) * footRotation;
            }
        }

        private static HeldReachVector ReachVector(Vector3 value) => new HeldReachVector(value.x, value.y, value.z);

        private static HeldReachArm HeldArm(Transform upper, Transform lower, Transform hand, Vector3 palm, Quaternion? rotation)
        {
            float upperLength = (lower.position - upper.position).magnitude;
            float lowerLength = (hand.position - lower.position).magnitude;
            if (upperLength < 0.01f || lowerLength < 0.01f)
                return new HeldReachArm(default, 0, 0); // Same degenerate-chain guard as SolveTwoBone.
            // Match Grip's intended palm rotation and SolveTwoBone's exact wrist limits. Reading
            // the posed hand preserves the rig's real child offsets and scale, including seats.
            var saved = hand.rotation;
            if (rotation.HasValue) hand.rotation = rotation.Value;
            var palmOffset = Palm(hand) - hand.position;
            if (rotation.HasValue) hand.rotation = saved;
            float minimum = Mathf.Abs(upperLength - lowerLength) + 0.005f;
            float maximum = upperLength + lowerLength - 0.005f;
            if (!rotation.HasValue)
            {
                // Uncalibrated wrists straighten during Grip. Reserving the whole palm length
                // keeps that changing orientation within the wrist's outer reach volume.
                maximum -= palmOffset.magnitude;
                minimum = 0;
                palmOffset = Vector3.zero;
            }
            return new HeldReachArm(ReachVector(upper.position - (palm - palmOffset)), minimum, maximum);
        }

        private Vector3 ConstrainHeld(ref Goals goals)
        {
            var rightPalm = goals.Held!.Value;
            Vector3? leftPalm = goals.HeldLeft;
            if (!leftPalm.HasValue && goals.HeldWidth > 0.01f)
            {
                var side = body.transform.right * Mathf.Min(goals.HeldWidth * 0.5f, 0.3f);
                rightPalm += side; leftPalm = goals.Held.Value - side;
            }
            var primary = HeldArm(upperArmR, lowerArmR, handR, rightPalm, goals.HeldRotation);
            HeldReachArm? secondary = leftPalm.HasValue
                ? HeldArm(upperArmL, lowerArmL, handL, leftPalm.Value, goals.HeldLeftRotation) : (HeldReachArm?)null;
            var forward = body.transform.forward;
            var shoulders = (upperArmR.position + upperArmL.position) * 0.5f;
            float clearance = 0.08f * Mathf.Abs(body.transform.lossyScale.y);
            float front = clearance - Vector3.Dot(forward, rightPalm - shoulders);
            if (leftPalm.HasValue) front = Mathf.Max(front, clearance - Vector3.Dot(forward, leftPalm.Value - shoulders));
            HeldItemReachMath.TryConstrain(primary, secondary, ReachVector(forward), front, out var correction);
            var translation = new Vector3((float)correction.X, (float)correction.Y, (float)correction.Z);
            goals.Held = goals.Held.Value + translation;
            if (goals.HeldLeft.HasValue) goals.HeldLeft = goals.HeldLeft.Value + translation;
            return translation;
        }

        private void Reach(Transform upper, Transform lower, Transform hand, Vector3 target, Vector3 pole, float weight, bool straightWrist = true)
        {
            // A stale or divergent remote goal should not pull the arm away from the body.
            if ((target - upper.position).sqrMagnitude > ArmReach * ArmReach) return;
            SolveTwoBone(upper, lower, hand, target, pole, weight);
            // The base poses hang the hands from bent wrists; a hand that holds or rests on
            // something continues the forearm instead.
            if (straightWrist) Straighten(lower, hand, Mathf.Clamp01(weight) * 0.85f);
        }

        // Puts the palm rather than the wrist on the target, the fingers turned part way to wrap.
        private void Grip(Transform upper, Transform lower, Transform hand, Vector3 target, Vector3 pole, Vector3 wrap, Quaternion? rotation = null)
        {
            if ((target - upper.position).sqrMagnitude > ArmReach * ArmReach) return;
            if (rotation.HasValue)
            {
                // Calibrated rotation refers to this hand's palm in the item's local frame.
                // Solve for its wrist after rotating so the palm stays on the chosen grip.
                hand.rotation = rotation.Value;
                var palmOffset = Palm(hand) - hand.position;
                SolveTwoBone(upper, lower, hand, target - palmOffset, pole, 1f);
                hand.rotation = rotation.Value;
                return;
            }
            Reach(upper, lower, hand, target, pole, 1f);
            Reach(upper, lower, hand, target - (Palm(hand) - hand.position), pole, 1f);
            if (wrap.sqrMagnitude > 1e-6f) Bend(hand, wrap.normalized, 0.4f);
        }

        private static Vector3 Fingers(Transform hand)
        {
            int count = hand.childCount;
            if (count == 0) return Vector3.zero;
            var sum = Vector3.zero;
            // Transform's foreach enumerator allocates each time palms/wrists are solved.
            for (int i = 0; i < count; i++) sum += hand.GetChild(i).position;
            return sum / count - hand.position;
        }
        private static Vector3 Palm(Transform hand) => hand.position + Fingers(hand) * 0.45f;

        // Turns the fingers part way towards direction.
        private static void Bend(Transform hand, Vector3 direction, float amount)
        {
            var along = Fingers(hand);
            if (along.sqrMagnitude > 1e-6f && direction.sqrMagnitude > 1e-6f)
                hand.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(along, direction), amount) * hand.rotation;
        }
        private static void Straighten(Transform lower, Transform hand, float amount)
        {
            if (amount > 0.001f) Bend(hand, hand.position - lower.position, amount);
        }

        // The stock hand has one combined four-finger chain and a separate thumb chain. Curl the
        // verified finger joints toward the palm and align both thumb segments with body-up. Every
        // joint is restored by Write(frame, ...) before the next frame/pose.
        private void PoseThumbsUpFingers(float weight, Vector3 bodyUp)
        {
            bool hasFingerBase = fingersR != null && fingersR.parent == handR;
            bool hasThumbBase = thumbR != null && thumbR.parent == handR;
            if (!StockGesturePoseMath.HasThumbUpFingerChain(hasFingerBase, hasThumbBase)) return;
            if (fingersR == null || thumbR == null) return;
            var finger = fingersR.localPosition;
            var thumb = thumbR.localPosition;
            var axisMath = StockGesturePoseMath.FingerCurlAxis(
                new GestureVector(finger.x, finger.y, finger.z), new GestureVector(thumb.x, thumb.y, thumb.z));
            if (axisMath.X * axisMath.X + axisMath.Y * axisMath.Y + axisMath.Z * axisMath.Z < 1e-6f) return;
            var handAxis = new Vector3(axisMath.X, axisMath.Y, axisMath.Z);
            var worldAxis = handR.TransformDirection(handAxis);
            CurlJoint(fingersR, worldAxis, StockGesturePoseMath.FingerCurlDegrees(weight, distal: false));
            if (fingersDistalR != null && fingersDistalR.parent == fingersR)
                CurlJoint(fingersDistalR, worldAxis, StockGesturePoseMath.FingerCurlDegrees(weight, distal: true));
            if (thumbDistalR != null && thumbDistalR.parent == thumbR)
                ExtendThumbSegment(thumbR, thumbDistalR, bodyUp, weight);
            if (thumbDistalR != null && thumbEndR != null && thumbEndR.parent == thumbDistalR)
                ExtendThumbSegment(thumbDistalR, thumbEndR, bodyUp, weight);
        }

        private static void CurlJoint(Transform joint, Vector3 worldAxis, float degrees)
        {
            if (joint.parent == null || Mathf.Abs(degrees) < 0.001f) return;
            var axisInParent = joint.parent.InverseTransformDirection(worldAxis);
            if (axisInParent.sqrMagnitude > 1e-6f)
                joint.localRotation = Quaternion.AngleAxis(degrees, axisInParent.normalized) * joint.localRotation;
        }

        private static void ExtendThumbSegment(Transform joint, Transform endpoint, Vector3 bodyUp, float weight)
        {
            var current = endpoint.position - joint.position;
            if (current.sqrMagnitude < 1e-8f || bodyUp.sqrMagnitude < 1e-8f) return;
            var blendedMath = StockGesturePoseMath.ExtendDirection(
                new GestureVector(current.x, current.y, current.z), new GestureVector(bodyUp.x, bodyUp.y, bodyUp.z), weight);
            var blended = new Vector3(blendedMath.X, blendedMath.Y, blendedMath.Z);
            if (blended.sqrMagnitude > 1e-6f)
                joint.rotation = Quaternion.FromToRotation(current, blended) * joint.rotation;
        }

        // Analytic two-bone IK in world space: bends the middle joint towards the pole direction and
        // puts the end on the target (or as close as the limb reaches). Rig-axis agnostic.
        private static void SolveTwoBone(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 poleDirection, float weight)
        {
            if (weight <= 0.001f) return;
            Vector3 a = upper.position, b = lower.position, c = end.position;
            float lab = (b - a).magnitude, lbc = (c - b).magnitude;
            if (lab < 0.01f || lbc < 0.01f) return;
            var toGoal = Vector3.Lerp(c, target, Mathf.Clamp01(weight)) - a;
            if (toGoal.sqrMagnitude < 1e-6f) return;
            float goalLength = toGoal.magnitude;
            var direction = toGoal / goalLength;
            float distance = Mathf.Clamp(goalLength, Mathf.Abs(lab - lbc) + 0.005f, lab + lbc - 0.005f);
            var pole = poleDirection - direction * Vector3.Dot(poleDirection, direction);
            if (pole.sqrMagnitude < 1e-6f)
            {
                pole = (b - a) - direction * Vector3.Dot(b - a, direction);
                if (pole.sqrMagnitude < 1e-6f) return;
            }
            pole.Normalize();
            float along = (lab * lab - lbc * lbc + distance * distance) / (2f * distance);
            float bend = Mathf.Sqrt(Mathf.Max(0f, lab * lab - along * along));
            var joint = a + direction * along + pole * bend;
            upper.rotation = Quaternion.FromToRotation(b - a, joint - a) * upper.rotation;
            b = lower.position; c = end.position;
            lower.rotation = Quaternion.FromToRotation(c - b, a + direction * distance - b) * lower.rotation;
        }

        private void Write(Quaternion[] pose, Vector3 hipsPosition)
        {
            AvatarTransformWrites.Pose(bones, pose, hips, hipsPosition);
        }
        private float FootHeight() =>
            Mathf.Min(body.transform.InverseTransformPoint(footL.position).y, body.transform.InverseTransformPoint(footR.position).y);

        private static Library? Load(out string reason)
        {
            if (library != null && library.Template != null && (library.Walk != null || !HasWalkClip()))
            { reason = "cached"; return library; }
            library = null;
            var roots = UnityEngine.Object.FindObjectsOfType<Transform>(true)
                .Where(t => t.name == "Armature" && t.parent != null && t.Find("Hips") != null).Select(t => t.parent).ToArray();
            bool Wearable(Transform root) => Shown.All(name => root.Find(name) != null) && !root.name.StartsWith("ActorsF") && !root.name.Contains("Police");
            // Prefer a copy with nothing attached; any wearable NPC is stripped on clone otherwise.
            var template = roots.Where(Wearable).OrderBy(r => r.GetComponentsInChildren<MonoBehaviour>(true).Length + r.GetComponentsInChildren<Collider>(true).Length)
                .ThenBy(r => r.name, StringComparer.Ordinal).FirstOrDefault();
            if (template == null) { reason = "no stock NPC body in the loaded scene"; return null; }
            var bones = template.Find("Armature").GetComponentsInChildren<Transform>(true).Select(t => t.name).Where(n => n != "Armature").Distinct().ToArray();

            Transform? Pick(params string[] names) =>
                names.Select(name => roots.FirstOrDefault(r => r.name == name && Compatible(r, bones))).FirstOrDefault(r => r != null);
            var stand = Pick("Actors_Eric", "Actors_Jacques", "Actor_Drift_Luc_Standing", "Actor_Legend_Tom_Standing") ?? template;
            var sit = Pick("Actors_Normand", "Actors_Francis", "Actors_Gilles", "Actors_Normand_Bar") ?? stand;
            var drive = Pick("Actor_BusDriver", "Actor_Martin", "Actor_Legend_Tom_Sitting") ?? sit;
            // Chairs use the driver's square seat (both feet forward) with the hands on the thighs.
            var chair = drive;
            var lib = new Library
            {
                Template = template.gameObject, Bones = bones,
                Walk = Resources.FindObjectsOfTypeAll<AnimationClip>().FirstOrDefault(c => c.name == "Actor_Walking"),
                Sources = "body=" + template.name + ", stand=" + stand.name + ", sit=" + sit.name + ", chair=" + chair.name + ", drive=" + drive.name
            };
            lib.Candidates = roots;
            // NPC scripts turn heads towards the player; keep the template's neutral head.
            (lib.Stand, lib.StandHips) = Capture(stand, bones, template);
            (lib.Sit, lib.SitHips) = Capture(sit, bones, template);
            (lib.Chair, lib.ChairHips) = Capture(chair, bones, template);
            (lib.Drive, lib.DriveHips) = Capture(drive, bones, template);
            if (lib.Walk == null) lib.Sources += ", no walk clip";
            library = lib;
            reason = "loaded";
            return lib;
        }
        private static bool HasWalkClip() => Resources.FindObjectsOfTypeAll<AnimationClip>().Any(c => c.name == "Actor_Walking");
        private static bool Compatible(Transform root, string[] bones)
        {
            var names = new HashSet<string>(root.Find("Armature").GetComponentsInChildren<Transform>(true).Select(t => t.name));
            return bones.All(names.Contains);
        }
        private static (Quaternion[] Pose, Vector3 Hips) Capture(Transform root, string[] bones, Transform neutral)
        {
            Dictionary<string, Transform> Map(Transform r) =>
                r.Find("Armature").GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            var map = Map(root);
            var neutralMap = Map(neutral);
            var pose = bones.Select(name => (name == "Neck" || name == "Head" ? neutralMap : map)[name].localRotation).ToArray();
            return (pose, map["Hips"].localPosition);
        }
    }
}
