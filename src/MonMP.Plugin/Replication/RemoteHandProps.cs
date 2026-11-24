using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // A remote avatar's copies of the local player's own first-person props: the map, the portable
    // game console, the flashlight, the joint with its tip smoke and exhale, and the vomit
    // particles. Copies are made under an inactive parent so no native script ever wakes, then
    // everything but meshes and particle systems is stripped.
    internal sealed class RemoteHandProps
    {
        internal sealed class Prop
        {
            public GameObject Object = null!;
            // The visual centre and the extent axes (thinnest to longest) in the prop's own space;
            // Beam is a flashlight's light direction and Tip the joint's burning end.
            public Vector3 LocalCenter, Thin, Mid, Long;
            public Vector3? Beam, Tip;
            // Size along each extent axis in the prop's own (unscaled) space.
            public Vector3 LocalSize;
            public float Length => Vector3.Scale(LocalSize, Object.transform.localScale).magnitude > 0
                ? Mathf.Abs(Vector3.Dot(Vector3.Scale(LocalSize, Object.transform.localScale), Beam ?? Long)) : 0.15f;
            // The native light the prop carried, before lights were stripped.
            public Light? SourceLight;
            internal int CalibrationFrame = -1;
            public void Show(bool shown) { if (Object.activeSelf != shown) Object.SetActive(shown); }

            // Turns the prop so its local axis points along direction (and second along
            // secondDirection), then moves its visual centre onto center.
            public void Place(Vector3 center, Vector3 axis, Vector3 direction, Vector3 second, Vector3 secondDirection)
            {
                Object.transform.rotation = Quaternion.LookRotation(direction, secondDirection) * Quaternion.Inverse(Quaternion.LookRotation(axis, second));
                Object.transform.position += center - Object.transform.TransformPoint(LocalCenter);
            }
        }

        private static readonly FieldInfo? MapField = AccessTools.Field(typeof(PlayerVisualHands), "Map");
        private static readonly FieldInfo? FlashLightField = AccessTools.Field(typeof(PlayerVisualHands), "FlashLight");
        private static readonly FieldInfo? WeedJointField = AccessTools.Field(typeof(PlayerVisualHands), "weedJoint");
        private static readonly FieldInfo? JointField = AccessTools.Field(typeof(WeedJoint), "Joint");
        private static readonly FieldInfo? JointMeshField = AccessTools.Field(typeof(WeedJoint), "JointMesh");
        private static readonly FieldInfo? TipField = AccessTools.Field(typeof(WeedJoint), "TipLocation");
        private static readonly FieldInfo? JointSmokeField = AccessTools.Field(typeof(WeedJoint), "JointSmoke");
        private static readonly FieldInfo? ExhaleField = AccessTools.Field(typeof(WeedJoint), "ExaleSmoke");
        private static readonly FieldInfo? VomitField = AccessTools.Field(typeof(DrunkEffect), "VFX_Vomit");
        private static GameObject? staging;

        internal Prop? Map, Console, Flashlight, Joint;
        private string mapShape = "", flashlightShape = "";
        internal static GameObject? FlashlightSource(Gameplay? gameplay)
        {
            var hands = gameplay?.Player?.Get_VisualHands;
            return hands != null ? FlashLightField?.GetValue(hands) as GameObject : null;
        }
        internal void PrepareFlashlightCalibration(ref StockAvatarBody.Goals goals, HeldGripOverrides? store, bool active)
        {
            if (active && Flashlight != null && store?.TryGetNamedRuntime(RemoteFlashlightCalibration.Key, flashlightShape, out var pose) == true)
            { goals.FlashlightProp = Flashlight; goals.FlashlightCalibration = pose; }
        }
        internal static GameObject? MapSource(Gameplay? gameplay)
        {
            var hands = gameplay?.Player?.Get_VisualHands;
            return hands != null ? MapField?.GetValue(hands) as GameObject : null;
        }
        internal void PrepareMapCalibration(ref StockAvatarBody.Goals goals, HeldGripOverrides? store, bool active)
        {
            if (active && Map != null && store?.TryGetNamedRuntime(RemoteMapCalibration.Key, mapShape, out var pose) == true)
            { goals.MapProp = Map; goals.MapCalibration = pose; }
        }
        internal ParticleSystem? JointSmoke, Exhale, Vomit;
        // A small glowing lens on the flashlight's front, so a lit torch reads as on.
        private GameObject? lens;
        private Material? lensMaterial;
        private Material? jointPaper, jointGlow;
        private RemoteExhaleSpace? exhaleSpace;
        private float inhaleTime;
        private bool glowWasSmoking;
        internal Vector3? FlashlightLens => lens != null && lens.activeInHierarchy ? lens.transform.position : (Vector3?)null;
        private bool wasSmoking;
        private float vomitUntil;
        private Transform? parent;
        private RemoteBinoculars? binoculars;
        internal void HideZoom() => binoculars?.Show(false);
#if MONMP_PERFORMANCE_CAPTURE
        internal void AppendCaptureRenderingRoots(List<GameObject> roots)
        {
            if (Map?.Object != null) roots.Add(Map.Object);
            if (Console?.Object != null) roots.Add(Console.Object);
            if (Flashlight?.Object != null) roots.Add(Flashlight.Object);
            if (Joint?.Object != null) roots.Add(Joint.Object);
            if (binoculars?.Object != null) roots.Add(binoculars.Object);
        }
#endif

        internal string Status =>
            string.Join(",", new[] { ("map", Map != null), ("console", Console != null), ("flashlight", Flashlight != null), ("joint", Joint != null),
                ("smoke", JointSmoke != null), ("exhale", Exhale != null), ("vomit", Vomit != null) }.Where(p => p.Item2).Select(p => p.Item1));

        // Copies whatever the local player's hands and drunk effect provide; missing ones stay null.
        internal static RemoteHandProps Create(Transform parent)
        {
            var props = new RemoteHandProps();
            try
            {
                CreateInto(props, parent);
                return props;
            }
            catch
            {
                props.Destroy();
                throw;
            }
        }

        private static void CreateInto(RemoteHandProps props, Transform parent)
        {
            props.parent = parent;
            var hands = UnityEngine.Object.FindObjectOfType<Gameplay>()?.Player?.Get_VisualHands;
            if (hands != null)
            {
                props.Map = CloneProp(MapField?.GetValue(hands) as GameObject, parent, "Map");
                var mapSource = MapSource(UnityEngine.Object.FindObjectOfType<Gameplay>());
                if (mapSource != null) props.mapShape = RemoteMapCalibration.ShapeOf(mapSource);
                props.Console = CloneProp(hands.GameConsole != null ? hands.GameConsole.gameObject : null, parent, "Console");
                var flashlightSource = FlashLightField?.GetValue(hands) as GameObject;
                props.Flashlight = CloneProp(flashlightSource, parent, "Flashlight");
                if (flashlightSource != null) props.flashlightShape = RemoteMapCalibration.ShapeOf(flashlightSource);
                if (props.Flashlight != null) props.AddLens(props.Flashlight);
                if (WeedJointField?.GetValue(hands) is WeedJoint weed && weed != null)
                {
                    var joint = JointField?.GetValue(weed) as Transform;
                    props.Joint = CloneProp(joint != null ? joint.gameObject : null, parent, "Joint", joint != null ? JointBaselineScale(joint) : (Vector3?)null);
                    if (props.Joint != null && joint != null && JointMeshField?.GetValue(weed) is MeshRenderer jointMesh)
                        props.OwnJointMaterials(joint, jointMesh);
                    if (props.Joint != null && joint != null && TipField?.GetValue(weed) is Transform tip && tip != null)
                        props.Joint.Tip = joint.InverseTransformPoint(tip.position);
                    props.JointSmoke = CloneParticles(JointSmokeField?.GetValue(weed) as ParticleSystem, parent, "JointSmoke");
                    props.Exhale = CloneParticles(ExhaleField?.GetValue(weed) as ParticleSystem, parent, "Exhale");
                    if (props.Exhale != null) props.exhaleSpace = new RemoteExhaleSpace(props.Exhale);
                }
            }
            var drunk = UnityEngine.Object.FindObjectOfType<DrunkEffect>();
            if (drunk != null) props.Vomit = CloneParticles(VomitField?.GetValue(drunk) as ParticleSystem, parent, "Vomit");
        }

        private static Vector3 JointBaselineScale(Transform joint)
        {
            // Native AcquireJoint sets localScale to one. Retain the ancestors' world scale,
            // while discarding the viewer's current burn shrink before measuring copied geometry.
            return joint.parent != null ? joint.parent.lossyScale : Vector3.one;
        }

        private void OwnJointMaterials(Transform sourceRoot, MeshRenderer sourceMesh)
        {
            // Resolve the exact serialized JointMesh by child indices copied by Instantiate.
            // If a supplied asset puts it outside Joint, leave that unsupported mapping untouched.
            var path = new Stack<int>();
            var source = sourceMesh.transform;
            while (source != sourceRoot)
            {
                if (source.parent == null) return;
                path.Push(source.GetSiblingIndex()); source = source.parent;
            }
            var target = Joint!.Object.transform;
            foreach (int index in path)
            { if (index >= target.childCount) return; target = target.GetChild(index); }
            var mesh = target.GetComponent<MeshRenderer>();
            if (mesh == null) return;
            var materials = mesh.sharedMaterials;
            if (materials.Length < 2 || materials[0] == null || materials[1] == null ||
                !materials[0].HasProperty("_MainTex") || !materials[1].HasProperty("_EmissionColor")) return;
            jointPaper = new Material(materials[0]); jointGlow = new Material(materials[1]);
            // Native AcquireJoint resets paper UV scale; release resets the glow to this baseline.
            jointPaper.SetTextureScale("_MainTex", Vector2.one);
            jointGlow.SetColor("_EmissionColor", new Color(0.05f, 0.05f, 0.05f));
            materials[0] = jointPaper; materials[1] = jointGlow; mesh.sharedMaterials = materials;
        }

        private void AddLens(Prop flashlight)
        {
            // Unlit/Color may be stripped from the build; an emissive Standard glows the same.
            var unlit = Shader.Find("Unlit/Color");
            var shader = unlit ?? Shader.Find("Standard");
            if (shader == null) return;
            var axis = flashlight.Beam ?? flashlight.Long;
            lens = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            lens.name = "MonMP Flashlight Lens";
            var collider = lens.GetComponent<Collider>();
            if (collider != null) { collider.enabled = false; UnityEngine.Object.Destroy(collider); }
            lensMaterial = new Material(shader) { color = new Color(1f, 0.97f, 0.85f) };
            if (unlit == null)
            {
                lensMaterial.EnableKeyword("_EMISSION");
                lensMaterial.SetColor("_EmissionColor", new Color(1f, 0.95f, 0.8f) * 2.5f);
            }
            var renderer = lens.GetComponent<Renderer>();
            renderer.sharedMaterial = lensMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lens.transform.SetParent(flashlight.Object.transform, false);
            float half = Mathf.Abs(Vector3.Dot(flashlight.LocalSize, axis)) * 0.5f;
            // A flat disc just proud of the torch head.
            float scale = Mathf.Max(flashlight.Object.transform.localScale.x, 0.01f);
            lens.transform.localPosition = flashlight.LocalCenter + axis * (half + 0.003f / scale);
            var along = new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z));
            lens.transform.localScale = (Vector3.one * 0.04f - along * 0.03f) / scale;
        }

        // Places the props for this frame. Amounts are the smoothed hand states (0..1).
        internal void Update(StockAvatarBody? body, float map, float console, float flashlight, float joint, float smoking, bool visible,
            VehicleController? vehicle = null, float elapsed = -1f, bool zoom = false, bool suppressFeedback = false)
        {
            exhaleSpace?.KeepVehicle(vehicle != null ? vehicle.transform : null);
            bool shown = visible && body != null;
            // A visible local self body still needs its held props and glowing joint. Native
            // hands already own the smoke/exhale/vomit, regardless of the selected camera.
            if (suppressFeedback)
            {
                if (JointSmoke != null && JointSmoke.isPlaying) JointSmoke.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                if (Exhale != null && Exhale.isPlaying) Exhale.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                if (Vomit != null && Vomit.isPlaying) Vomit.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                wasSmoking = false;
                vomitUntil = 0;
            }
            if (shown && zoom && parent != null)
            {
                binoculars ??= new RemoteBinoculars(parent);
                binoculars.Place(body!);
            }
            else binoculars?.Show(false);
            Map?.Show(shown && map > 0.5f);
            Console?.Show(shown && console > 0.5f && map <= 0.5f);
            Flashlight?.Show(shown && flashlight > 0.5f);
            Joint?.Show(shown && joint > 0.5f);
            bool isSmoking = shown && joint > 0.5f && smoking > 0.5f;
            UpdateJointGlow(isSmoking, elapsed >= 0 ? elapsed : Time.unscaledDeltaTime);
            if (!shown)
            {
                if (JointSmoke != null && JointSmoke.isPlaying) JointSmoke.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                wasSmoking = false;
                return;
            }
            // An idle avatar still maintains a live exhale's vehicle frame above, but
            // has no hand prop or mouth effect to place. Preserve the release edge:
            // putting the joint away during a puff must still emit its last exhale.
            bool hasVisibleProp = Map?.Object.activeSelf == true || Console?.Object.activeSelf == true ||
                Flashlight?.Object.activeSelf == true || Joint?.Object.activeSelf == true;
            if (!hasVisibleProp && !isSmoking && !wasSmoking && !(JointSmoke != null && joint > 0.5f) &&
                !(Vomit != null && Time.unscaledTime < vomitUntil))
            {
                if (JointSmoke != null && JointSmoke.isPlaying) JointSmoke.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                return;
            }
            var aim = body!.Aim;
            var aimForward = aim * Vector3.forward;
            var aimUp = aim * Vector3.up;
            var aimRight = aim * Vector3.right;
            var head = body.Head.position;
            // Map and console are mutually exclusive; avoid a temporary array per posed avatar.
            var held = map > 0.5f ? Map : console > 0.5f ? Console : null;
            if (held != null && held.Object.activeSelf)
            {
                // Flat side to the eyes, long side across.
                var center = body.HeldUpCenter;
                if (ReferenceEquals(held, Map))
                {
                    HeldGripOverrides.Grip? calibration = null;
                    if (MonMPPlugin.Instance?.HeldGrips?.TryGetNamedRuntime(RemoteMapCalibration.Key, mapShape, out var saved) == true) calibration = saved;
                    RemoteMapCalibration.Place(held, body, calibration);
                }
                else held.Place(center, held.Thin, head - center, held.Long, aimRight);
            }
            if (Flashlight != null && Flashlight.Object.activeSelf && Flashlight.CalibrationFrame != Time.frameCount)
            {
                // Gripped at the palm around its back half, the lens out in front of the fingers.
                var axis = Flashlight.Beam ?? Flashlight.Long;
                Flashlight.Place(body.LeftPalm + aimForward * Mathf.Clamp(Flashlight.Length * 0.3f, 0.02f, 0.08f) - aimUp * 0.015f, axis, aimForward, Perpendicular(Flashlight, axis), aimUp);
            }
            Vector3 tip = body.Mouth;
            if (Joint != null && Joint.Object.activeSelf)
            {
                // Pointing out and a little to the side from the fingers.
                var along = (aimForward - aimRight * 0.35f).normalized;
                Joint.Place(body.LeftHand.position + aimForward * 0.03f, Joint.Long, along, Perpendicular(Joint, Joint.Long), aimUp);
                tip = Joint.Tip.HasValue ? Joint.Object.transform.TransformPoint(Joint.Tip.Value) : Joint.Object.transform.TransformPoint(Joint.LocalCenter) + along * 0.04f;
            }
            if (JointSmoke != null)
            {
                bool burning = !suppressFeedback && joint > 0.5f && smoking < 0.5f;
                JointSmoke.transform.position = tip;
                if (burning && !JointSmoke.isPlaying) JointSmoke.Play(true);
                else if (!burning && JointSmoke.isPlaying) JointSmoke.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
            if (!suppressFeedback && wasSmoking && !isSmoking && Exhale != null)
            {
                exhaleSpace?.SetAnchor(vehicle != null && !vehicle.isAWindowOrDoorIsOpen ? vehicle.transform : null);
                Exhale.transform.SetPositionAndRotation(body.Mouth, aim);
                Exhale.Play(true);
            }
            wasSmoking = !suppressFeedback && isSmoking;
            if (Vomit != null && Time.unscaledTime < vomitUntil) Vomit.transform.SetPositionAndRotation(body.Mouth, Quaternion.LookRotation(aimForward - aimUp * 0.6f, aimUp));
        }

        private void UpdateJointGlow(bool isSmoking, float elapsed)
        {
            float previousInhaleTime = inhaleTime;
            inhaleTime = isSmoking ? Mathf.Min(10f, inhaleTime + elapsed) : 0f;
            // The owned material starts at the native released-puff baseline. Repeated
            // idle frames (including the hidden local shadow body) need no native write.
            bool changed = isSmoking != glowWasSmoking || isSmoking && inhaleTime != previousInhaleTime;
            glowWasSmoking = isSmoking;
            if (jointGlow != null && changed) jointGlow.SetColor("_EmissionColor", isSmoking
                ? new Color(10f + inhaleTime, 0f, 0f) : new Color(0.05f, 0.05f, 0.05f));
        }

        internal void PlayVomit(StockAvatarBody? body, float seconds)
        {
            if (Vomit == null || body == null) return;
            vomitUntil = Time.unscaledTime + seconds;
            Vomit.transform.SetPositionAndRotation(body.Mouth, body.Aim);
            Vomit.Play(true);
        }

        internal void Destroy()
        {
            binoculars?.Dispose(); binoculars = null; parent = null;
            foreach (var prop in new[] { Map, Console, Flashlight, Joint }) if (prop?.Object != null) UnityEngine.Object.Destroy(prop.Object);
            foreach (var particles in new[] { JointSmoke, Exhale, Vomit }) if (particles != null) UnityEngine.Object.Destroy(particles.gameObject);
            if (lensMaterial != null) UnityEngine.Object.Destroy(lensMaterial);
            if (jointPaper != null) UnityEngine.Object.Destroy(jointPaper);
            if (jointGlow != null) UnityEngine.Object.Destroy(jointGlow);
        }

        internal static Vector3 Perpendicular(Prop prop, Vector3 axis)
        {
            // Preserve OrderBy's stable tie order without its array, delegate and sorter.
            var best = prop.Thin;
            float score = Mathf.Abs(Vector3.Dot(best, axis));
            float mid = Mathf.Abs(Vector3.Dot(prop.Mid, axis));
            if (mid < score) { best = prop.Mid; score = mid; }
            if (Mathf.Abs(Vector3.Dot(prop.Long, axis)) < score) best = prop.Long;
            return best;
        }

        private static Transform Staging()
        {
            if (staging == null)
            {
                staging = new GameObject("MonMP Prop Staging");
                staging.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(staging);
            }
            return staging.transform;
        }

        // Copied under the inactive staging parent, so Awake/OnEnable never run on the copy.
        private static GameObject? Copy(GameObject? source, Transform parent, string name, bool keepParticles)
        {
            if (source == null) return null;
            var copy = UnityEngine.Object.Instantiate(source, Staging(), false);
            copy.name = "MonMP " + name;
            copy.SetActive(false);
            foreach (var component in copy.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(component);
            foreach (var component in copy.GetComponentsInChildren<UnityEngine.Joint>(true)) UnityEngine.Object.DestroyImmediate(component);
            foreach (var component in copy.GetComponentsInChildren<Component>(true))
                if (component is Collider || component is Rigidbody || component is AudioSource || component is AudioListener ||
                    component is Camera || component is Light || component is Animator || component is Animation ||
                    (!keepParticles && (component is ParticleSystem || component is ParticleSystemRenderer)))
                    UnityEngine.Object.DestroyImmediate(component);
            foreach (var t in copy.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 0;
            copy.transform.SetParent(parent, false);
            copy.transform.localPosition = Vector3.zero;
            copy.transform.localRotation = Quaternion.identity;
            copy.transform.localScale = source.transform.lossyScale;
            return copy;
        }

        private static Prop? CloneProp(GameObject? source, Transform parent, string name, Vector3? worldScale = null)
        {
            if (source == null) return null;
            // Preserve the native beam direction before stripping lights.
            var light = NativeLight(source);
            var beam = BeamOf(source);
            var copy = Copy(source, parent, name, keepParticles: false);
            if (copy == null) return null;
            RemoteMapCalibration.RemoveOverlay(copy);
            if (worldScale.HasValue) copy.transform.localScale = worldScale.Value;
            return DescribeProp(copy, beam, light);
        }
        private static Light? NativeLight(GameObject source) => source.GetComponentsInChildren<Light>(true).FirstOrDefault(l => l.type == LightType.Spot) ??
            source.GetComponentsInChildren<Light>(true).FirstOrDefault();
        internal static Vector3? BeamOf(GameObject source)
        {
            var light = NativeLight(source);
            return light != null ? source.transform.InverseTransformDirection(light.transform.forward).normalized : (Vector3?)null;
        }
        internal static Prop? DescribeProp(GameObject copy, Vector3? beam = null, Light? light = null)
        {
            var root = copy.transform;
            var min = Vector3.positiveInfinity; var max = Vector3.negativeInfinity;
            void Add(Transform owner, Bounds bounds)
            {
                for (int i = 0; i < 8; i++)
                {
                    var corner = bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var local = root.InverseTransformPoint(owner.TransformPoint(corner));
                    min = Vector3.Min(min, local); max = Vector3.Max(max, local);
                }
            }
            foreach (var filter in copy.GetComponentsInChildren<MeshFilter>(true)) if (filter.sharedMesh != null) Add(filter.transform, filter.sharedMesh.bounds);
            foreach (var skin in copy.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (skin.sharedMesh != null) Add(skin.transform, skin.sharedMesh.bounds);
            if (float.IsInfinity(min.x)) { UnityEngine.Object.Destroy(copy); return null; }
            var size = Vector3.Scale(max - min, root.localScale);
            var axes = new[] { (Vector3.right, size.x), (Vector3.up, size.y), (Vector3.forward, size.z) }.OrderBy(a => a.Item2).Select(a => a.Item1).ToArray();
            return new Prop { Object = copy, LocalCenter = (min + max) * 0.5f, Thin = axes[0], Mid = axes[1], Long = axes[2], Beam = beam, LocalSize = max - min, SourceLight = light };
        }

        private static ParticleSystem? CloneParticles(ParticleSystem? source, Transform parent, string name)
        {
            var copy = Copy(source != null ? source.gameObject : null, parent, name, keepParticles: true);
            if (copy == null) return null;
            var particles = copy.GetComponent<ParticleSystem>();
            if (particles == null) { UnityEngine.Object.Destroy(copy); return null; }
            foreach (var system in copy.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = system.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.customSimulationSpace = null;
                main.playOnAwake = false;
            }
            copy.SetActive(true);
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            return particles;
        }

        // Written by the avatar preview test: what each prop copy is made of.
        internal string Describe()
        {
            var lines = new List<string>();
            foreach (var (name, prop) in new[] { ("map", Map), ("console", Console), ("flashlight", Flashlight), ("joint", Joint) })
            {
                if (prop == null) { lines.Add(name + ": missing"); continue; }
                lines.Add(name + ": scale=" + prop.Object.transform.localScale.ToString("F3") + " center=" + prop.LocalCenter.ToString("F3") +
                    " thin=" + prop.Thin + " long=" + prop.Long + " beam=" + (prop.Beam?.ToString("F2") ?? "-") + " tip=" + (prop.Tip?.ToString("F3") ?? "-"));
                foreach (var renderer in prop.Object.GetComponentsInChildren<Renderer>(true))
                    lines.Add("  " + renderer.GetType().Name + " " + renderer.name + " materials=" + string.Join("|", renderer.sharedMaterials.Where(m => m != null).Select(m => m.name)));
            }
            lines.Add("lens: " + (lens == null ? "missing" : lensMaterial!.shader.name + " at " + lens.transform.localPosition.ToString("F3")));
            foreach (var (name, particles) in new[] { ("smoke", JointSmoke), ("exhale", Exhale), ("vomit", Vomit) })
                lines.Add(name + ": " + (particles == null ? "missing" : particles.name + " duration=" + particles.main.duration.ToString("F2")));
            return string.Join("\n", lines);
        }
    }
}
