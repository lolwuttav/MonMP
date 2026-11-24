using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Plugin.Replication;
using MonMP.Plugin.UI;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Playables;

namespace MonMP.Plugin.Diagnostics
{
    // Explicit request-only acceptance of the actual editor stages. No fixture draft is published
    // through Active, and all persistence uses a new directory owned by this isolated test copy.
    internal sealed class NativeEmotePoseEditorFixture : IDisposable
    {
        private sealed class Sample
        {
            internal EmotePoseOverrides.ViewKind View;
            internal PlayerEmoteKind Kind;
            internal bool Custom;
            internal float Seconds;
            internal string Phase = "";
            internal string Name => View.ToString().ToLowerInvariant() + "-" + Kind.ToString().ToLowerInvariant() + "-" + (Custom ? "custom" : "default") + "-" + Phase;
        }

        private readonly Gameplay gameplay;
        private readonly MonMPPlugin plugin;
        private readonly string output, id, storePath;
        private readonly Action<string, object> write;
        private readonly Queue<Sample> samples = new Queue<Sample>();
        private readonly List<object> results = new List<object>();
        private readonly List<object> precision = new List<object>();
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private readonly List<PlayableGraph> graphs = new List<PlayableGraph>();
        private readonly HashSet<int> ownedIds = new HashSet<int>();
        private readonly EmotePoseOverrides? priorActive;
        private readonly int initialMeshCache, initialLeaseCache;
        private readonly CursorLockMode priorCursorLock;
        private readonly bool priorCursorVisible;
        private readonly bool priorCleanFrame;
        private readonly object persistence;
        private readonly Harmony drawProbe = new Harmony("monmp.native-emote-editor-fixture");
        private static NativeEmotePoseEditorFixture? active;
        private readonly List<string> guiImages = new List<string>(), nativeErrors = new List<string>();
        private EmotePoseEditor? previousEditor;
        private int drawCalls, repaintCalls;
        private bool registered, inputBlockObserved;
        private EmotePoseEditor? editor;
        private Sample? current;
        private EmotePoseOverrides.ViewKind selectedView;
        private PlayerEmoteKind selectedKind;
        private bool hasSelection, disposed;
        private int frames, finishing;
        private Dictionary<string, Quaternion> defaultMiddle = new Dictionary<string, Quaternion>();
        private Vector3 defaultWrist;
        private string defaultDomain = "";
        private Dictionary<string, Quaternion> neutral = new Dictionary<string, Quaternion>();
        private string? failure;
        internal bool Running { get; private set; }

        internal NativeEmotePoseEditorFixture(MonMPPlugin plugin, Gameplay gameplay, string directory, string id, Action<string, object> write)
        {
            this.plugin = plugin; this.gameplay = gameplay; this.id = id; this.write = write;
            output = Path.Combine(directory, id + "-pose-editor"); Directory.CreateDirectory(output);
            storePath = Path.Combine(output, "owned-store", "EmotePoses.json");
            priorActive = EmotePoseOverrides.Active;
            initialMeshCache = StockAvatarShirt.CachedMeshCount; initialLeaseCache = StockAvatarShirt.CachedLeaseCount;
            priorCursorLock = Cursor.lockState; priorCursorVisible = Cursor.visible;
            priorCleanFrame = MonMPPlugin.CleanFrame;
            var store = new EmotePoseOverrides(storePath);
            persistence = CheckPersistence(store);
            // Remove the saved test poses before editor defaults are sampled. The document itself
            // remains as round-trip evidence, and this object has its own empty draft dictionary.
            var draftStore = new EmotePoseOverrides(Path.Combine(output, "owned-editor-store", "EmotePoses.json"));
            editor = new EmotePoseEditor(draftStore, gameplay);
            try
            {
                Isolated(() => editor.Show(), "show");
                Require(editor.Visible, "The actual editor did not open.");
                previousEditor = (EmotePoseEditor?)AccessTools.Field(typeof(MonMPPlugin), "emotePoseEditor").GetValue(plugin);
                Require(previousEditor?.Visible != true, "An existing visible editor must be preserved.");
                AccessTools.Field(typeof(MonMPPlugin), "emotePoseEditor").SetValue(plugin, editor); registered = true;
                MonMPPlugin.CleanFrame = false;
                active = this;
                drawProbe.Patch(AccessTools.Method(typeof(EmotePoseEditor), "Draw"), postfix: new HarmonyMethod(AccessTools.Method(typeof(NativeEmotePoseEditorFixture), nameof(ObserveDraw))));
                Application.logMessageReceived += ObserveLog;
            }
            catch { RestoreRegistration(); editor.Dispose(); editor = null; throw; }
            foreach (EmotePoseOverrides.ViewKind view in Enum.GetValues(typeof(EmotePoseOverrides.ViewKind)))
                foreach (var kind in new[] { PlayerEmoteKind.Wave, PlayerEmoteKind.Point, PlayerEmoteKind.ThumbsUp })
                    foreach (bool custom in new[] { false, true })
                    {
                        float duration = StockAvatarBody.GestureDuration(kind);
                        samples.Enqueue(new Sample { View = view, Kind = kind, Custom = custom, Seconds = .08f, Phase = "start" });
                        samples.Enqueue(new Sample { View = view, Kind = kind, Custom = custom, Seconds = .55f, Phase = "middle" });
                        samples.Enqueue(new Sample { View = view, Kind = kind, Custom = custom, Seconds = duration - .08f, Phase = "late" });
                        samples.Enqueue(new Sample { View = view, Kind = kind, Custom = custom, Seconds = duration, Phase = "expired" });
                    }
            Running = true;
        }

        internal void Tick()
        {
            if (!Running || disposed) return;
            try
            {
                if (finishing > 0)
                {
                    if (++finishing < 4) return;
                    var cleanup = Cleanup();
                    Require(owned.All(asset => asset == null), "Editor retained one or more owned native objects after disposal.");
                    Require(graphs.All(graph => !graph.IsValid()), "Editor retained an animation graph after disposal.");
                    Require(StockAvatarShirt.CachedMeshCount == initialMeshCache && StockAvatarShirt.CachedLeaseCount == initialLeaseCache,
                        "Editor did not return native shirt leases to their original baseline.");
                    Require(Cursor.lockState == priorCursorLock && Cursor.visible == priorCursorVisible, "Editor did not restore its cursor ownership.");
                    Require(ReferenceEquals(EmotePoseOverrides.Active, priorActive), "Fixture changed the active production pose store.");
                    Require(drawCalls > 0 && repaintCalls > 0 && inputBlockObserved, "Actual plugin editor Draw/Tick/input-block path was not observed.");
                    Require(guiImages.All(path => File.Exists(path) && new FileInfo(path).Length > 1000), "One or more actual editor GUI screenshots were not produced.");
                    Require(nativeErrors.Count == 0, "Native runtime logged an error while drawing the editor: " + string.Join("\n", nativeErrors));
                    write(id, new { passed = failure == null, failed = failure != null, error = failure, samples = results, precision, persistence, cleanup,
                        gui = new { drawCalls, repaintCalls, inputBlockObserved, screenshots = guiImages, errors = nativeErrors },
                        evidence = "Actual editor isolated native hands copy and StockAvatarBody.Solve preview, sampled over multiple native frames.",
                        manual = "Mouse/gizmo input, user-operated third-person camera, and remote Steam delivery were not exercised." });
                    Running = false; return;
                }
                inputBlockObserved |= plugin.HoldPositionEditorOpen;
                if (current == null)
                {
                    if (samples.Count == 0) { Finish(); return; }
                    current = samples.Dequeue(); frames = 0;
                    if (!hasSelection || selectedView != current.View || selectedKind != current.Kind)
                    {
                        RememberAssets();
                        Isolated(() => editor!.SetPreviewSelection(current.View, current.Kind), "select " + current.Name);
                        selectedView = current.View; selectedKind = current.Kind; hasSelection = true;
                        InspectStage(current.View); RememberAssets();
                        if (current.Kind == PlayerEmoteKind.ThumbsUp) CheckMillimeter(current.View);
                        Isolated(() => editor!.SetPreviewSample(new EmotePoseOverrides.Pose(), 0), "neutral " + current.Name);
                        neutral = Rotations(editor!.PreviewRig!);
                    }
                }
                var pose = current.Custom ? Custom(current.View) : new EmotePoseOverrides.Pose();
                Isolated(() => editor!.SetPreviewSample(pose, current.Seconds), "sample " + current.Name);
                RequirePrivateStageHidden();
                // The actual stage must be stable under repeated samples, with no additive drift.
                var first = Rotations(editor!.PreviewRig!);
                Isolated(() => editor.SetPreviewSample(pose, current.Seconds), "repeat " + current.Name);
                float repeatDelta = RotationDelta(first, Rotations(editor.PreviewRig!));
                Require(repeatDelta < .06f, "Repeated editor sampling accumulated a bone rotation: " + repeatDelta);
                if (++frames < 3) return;
                string image = Path.Combine(output, current.Name + ".png");
                Isolated(() => SaveTexture(editor.PreviewTexture!, image, current.Phase == "middle"), "readback " + current.Name);
                if (current.Phase == "middle")
                {
                    string guiPath = Path.Combine(output, current.Name + "-gui.png");
                    ScreenCapture.CaptureScreenshot(guiPath); guiImages.Add(guiPath);
                }
                var now = Rotations(editor.PreviewRig!);
                float neutralDelta = current.Phase == "expired" ? RotationDelta(neutral, now) : 0;
                Require(current.Phase != "expired" || neutralDelta < .06f, "Expired editor pose did not return to neutral: " + neutralDelta);
                var wrist = editor.PreviewRig!.GetComponentsInChildren<Transform>(true).First(t => t.name == "Hand.R");
                float wristDelta = 0, boneDelta = 0;
                string domain = current.View + "/" + current.Kind;
                if (current.Phase == "middle")
                {
                    if (!current.Custom) { defaultMiddle = now; defaultWrist = wrist.position; defaultDomain = domain; }
                    else
                    {
                        Require(defaultDomain == domain, "Missing default comparison in editor sample sequence.");
                        wristDelta = Vector3.Distance(defaultWrist, wrist.position); boneDelta = RotationDelta(defaultMiddle, now);
                        Require(wristDelta > .005f && boneDelta > 4, "Custom wrist/joint settings did not affect the actual preview rig.");
                    }
                }
                results.Add(new { sample = current.Name, current.Seconds, image, repeatDeltaDegrees = repeatDelta,
                    customWristDeltaMeters = wristDelta, customBoneDeltaDegrees = boneDelta, transforms = now.Count,
                    expiredNeutralDeltaDegrees = neutralDelta,
                    camera = new { fov = editor.PreviewCamera!.fieldOfView, aspect = editor.PreviewCamera.aspect },
                    rightWrist = new[] { wrist.position.x, wrist.position.y, wrist.position.z } });
                current = null;
            }
            catch (Exception ex)
            {
                failure = failure == null ? ex.ToString() : failure + "\nPost-cleanup: " + ex;
                if (finishing > 0) { write(id, new { failed = true, error = failure, samples = results, precision, persistence, cleanup = Cleanup(), gui = new { drawCalls, repaintCalls, inputBlockObserved, screenshots = guiImages, errors = nativeErrors } }); Running = false; return; }
                try { Finish(); } catch (Exception cleanup) { failure += "\nCleanup: " + cleanup; Running = false; write(id, new { failed = true, error = failure, samples = results }); }
            }
        }

        private void CheckMillimeter(EmotePoseOverrides.ViewKind view)
        {
            // The bent thumbs-up target and small inward offset keep the wrist within arm reach,
            // so clamping does not mask whether the preview preserves a 1 mm calibration edit.
            var conservative = new EmotePoseOverrides.Pose { X = -.01f, Y = -.01f, Z = -.02f };
            Isolated(() => editor!.SetPreviewSample(conservative, .55f), "precision baseline");
            var wrist = editor!.PreviewRig!.GetComponentsInChildren<Transform>(true).First(t => t.name == "Hand.R");
            Vector3 before = wrist.position;
            conservative.X += .001f;
            Isolated(() => editor.SetPreviewSample(conservative, .55f), "precision 1 mm edit");
            float expected = .001f;
            if (editor.PreviewBody != null)
                expected *= ((GameObject)AccessTools.Field(typeof(StockAvatarBody), "body").GetValue(editor.PreviewBody)).transform.lossyScale.y;
            float actual = Vector3.Distance(before, wrist.position);
            Require(actual > expected * .7f && actual < expected * 1.3f, "A reachable 1 mm preview edit was lost or distorted: " + actual + " expected " + expected);
            RequirePrivateStageHidden();
            precision.Add(new { view = view.ToString(), requestedMeters = .001f, expectedMeters = expected, actualMeters = actual, relativeError = Mathf.Abs(actual - expected) / expected });
        }

        private void RequirePrivateStageHidden()
        {
            var root = editor!.PreviewRoot ?? throw new InvalidOperationException("Editor lost its preview stage.");
            Require(root.GetComponentsInChildren<Renderer>(true).All(r => !r.enabled) && root.GetComponentsInChildren<Light>(true).All(light => !light.enabled),
                "A preview renderer/light remained enabled outside its private camera render.");
        }

        private static void ObserveDraw(EmotePoseEditor __instance)
        {
            var fixture = active;
            if (fixture == null || fixture.editor != __instance) return;
            fixture.drawCalls++;
            if (Event.current?.type == EventType.Repaint) fixture.repaintCalls++;
        }

        private void ObserveLog(string message, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) nativeErrors.Add(type + ": " + message + "\n" + stackTrace);
        }

        private void RestoreRegistration()
        {
            Application.logMessageReceived -= ObserveLog;
            drawProbe.UnpatchSelf();
            if (registered)
            {
                AccessTools.Field(typeof(MonMPPlugin), "emotePoseEditor").SetValue(plugin, previousEditor); registered = false;
            }
            MonMPPlugin.CleanFrame = priorCleanFrame;
            if (active == this) active = null;
        }

        private object CheckPersistence(EmotePoseOverrides store)
        {
            Require(!File.Exists(storePath), "Fixture store path must be new.");
            int count = 0;
            foreach (EmotePoseOverrides.ViewKind view in Enum.GetValues(typeof(EmotePoseOverrides.ViewKind)))
                foreach (var kind in new[] { PlayerEmoteKind.Wave, PlayerEmoteKind.Point, PlayerEmoteKind.ThumbsUp })
                {
                    Require(store.Get(view, kind) == null, "A new test store did not use stock defaults.");
                    var pose = Custom(view); pose.Z += count++ * .001f;
                    Require(store.Save(view, kind, pose, out string message), message);
                    string saved = JsonConvert.SerializeObject(pose);
                    pose.X = .42f; pose.Bones.First().Value.X = 170;
                    Require(JsonConvert.SerializeObject(store.Get(view, kind)) == saved, "Save retained mutable draft references.");
                }
            var reloaded = new EmotePoseOverrides(storePath);
            Require(reloaded.Count == 6, "Separate first-person/remote domains did not all reload.");
            foreach (EmotePoseOverrides.ViewKind view in Enum.GetValues(typeof(EmotePoseOverrides.ViewKind)))
                foreach (var kind in new[] { PlayerEmoteKind.Wave, PlayerEmoteKind.Point, PlayerEmoteKind.ThumbsUp })
                    Require(JsonConvert.SerializeObject(store.Get(view, kind)) == JsonConvert.SerializeObject(reloaded.Get(view, kind)), "Pose round-trip changed values.");
            string remote = JsonConvert.SerializeObject(reloaded.Get(EmotePoseOverrides.ViewKind.Remote, PlayerEmoteKind.Wave));
            Require(reloaded.Remove(EmotePoseOverrides.ViewKind.FirstPerson, PlayerEmoteKind.Wave, out string removed), removed);
            reloaded.Reload();
            Require(reloaded.Get(EmotePoseOverrides.ViewKind.FirstPerson, PlayerEmoteKind.Wave) == null &&
                JsonConvert.SerializeObject(reloaded.Get(EmotePoseOverrides.ViewKind.Remote, PlayerEmoteKind.Wave)) == remote,
                "Removing a first-person pose changed the separate remote domain.");
            Require(ReferenceEquals(EmotePoseOverrides.Active, priorActive), "Temporary persistence changed the live store.");
            return new { passed = true, ownedPath = storePath, savedDomains = count, roundTrip = true, detachedDrafts = true, separateDomainRemoval = true, activeStoreUnchanged = true };
        }

        private static EmotePoseOverrides.Pose Custom(EmotePoseOverrides.ViewKind view)
        {
            var pose = new EmotePoseOverrides.Pose
            {
                X = view == EmotePoseOverrides.ViewKind.FirstPerson ? -.035f : .09f, Y = .035f, Z = .015f,
                Pitch = 12, Yaw = -10, Roll = 15, ElbowX = .08f, ElbowY = .06f, WaveScale = .7f, WaveRate = 1.25f
            };
            pose.Bones.Add("Hand.R", new EmotePoseOverrides.BoneRotation { X = 6, Y = -8, Z = 12 });
            pose.Bones.Add(view == EmotePoseOverrides.ViewKind.FirstPerson ? "Hand.Finger00.R" : "Fingers01.R", new EmotePoseOverrides.BoneRotation { X = 15, Z = 8 });
            return pose;
        }

        private void InspectStage(EmotePoseOverrides.ViewKind view)
        {
            var rig = editor!.PreviewRig ?? throw new InvalidOperationException("The editor did not create a preview rig.");
            var root = editor.PreviewRoot ?? throw new InvalidOperationException("The editor did not create an owned stage root.");
            var native = gameplay.Player.Get_VisualHands;
            var live = new HashSet<int>(native.GetComponentsInChildren<Transform>(true).Select(t => t.GetInstanceID()));
            Require(rig != native.transform && !rig.GetComponentsInChildren<Transform>(true).Any(t => live.Contains(t.GetInstanceID())), "Preview aliases a live native transform.");
            Require(root.GetComponentsInChildren<PlayerVisualHands>(true).Length == 0 && root.GetComponentsInChildren<Collider>(true).Length == 0 &&
                root.GetComponentsInChildren<Rigidbody>(true).Length == 0, "Preview copied a native action/physics owner.");
            var skins = rig.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            RequirePrivateStageHidden();
            Require(skins.Length > 0 && skins.All(s => s.bones.All(b => b != null && !live.Contains(b.GetInstanceID()))), "Preview skin references live or missing native bones.");
            var nativeMaterials = new HashSet<int>(native.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m != null).Select(m => m.GetInstanceID()));
            Require(editor.PreviewTexture != null && editor.PreviewTexture.IsCreated() && editor.PreviewCamera != null, "Preview render target/camera is missing.");
            if (view == EmotePoseOverrides.ViewKind.FirstPerson)
            {
                Require(skins.SelectMany(s => s.sharedMaterials).Where(m => m != null).All(m => !nativeMaterials.Contains(m.GetInstanceID())), "First-person copy did not create private materials.");
                Require(editor.PreviewBody == null, "First-person preview used the NPC body instead of copied native arms.");
                Require(rig.GetComponentsInChildren<Animator>(true).Length == 0, "Copied native arms retained an Animator owner.");
                var actualSkins = native.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                Require(skins.All(s => actualSkins.Any(n => n.sharedMesh == s.sharedMesh)), "First-person preview did not borrow the actual native arms meshes.");
            }
            else Require(editor.PreviewBody != null, "Remote preview did not use StockAvatarBody.");
        }

        private void RememberAssets()
        {
            if (editor == null) return;
            void Keep(UnityEngine.Object? asset) { if (asset != null && ownedIds.Add(asset.GetInstanceID())) owned.Add(asset); }
            Keep(editor.PreviewRoot); Keep(editor.PreviewCamera); Keep(editor.PreviewTexture);
            foreach (var material in editor.PreviewMaterials) Keep(material);
            if (editor.PreviewBody != null)
            {
                Keep(editor.PreviewBody.ShirtMaterial);
                var graph = (PlayableGraph)AccessTools.Field(typeof(StockAvatarBody), "walkGraph").GetValue(editor.PreviewBody);
                if (graph.IsValid()) graphs.Add(graph);
            }
        }

        private void Isolated(Action action, string operation)
        {
            string before = NativeState();
            action();
            string after = NativeState();
            Require(before == after, "Editor mutated live native hands/Animator/input/material/camera state during " + operation + ".");
        }

        private string NativeState()
        {
            var hands = gameplay.Player.Get_VisualHands;
            var animator = (Animator)AccessTools.Field(typeof(PlayerVisualHands), "animator").GetValue(hands);
            var foreground = gameplay.PlayerWalking.playerCameraController.cam3DInFrontOfEverything;
            var materials = hands.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m != null).Distinct().ToArray();
            return JsonConvert.SerializeObject(new
            {
                hands = hands.GetInstanceID(), hands.enabled, hands.isActiveAndEnabled,
                transforms = hands.GetComponentsInChildren<Transform>(true).Select(t => new { id = t.GetInstanceID(), parent = t.parent?.GetInstanceID(), active = t.gameObject.activeSelf,
                    layer = t.gameObject.layer, p = V(t.localPosition), r = Q(t.localRotation), s = V(t.localScale) }).ToArray(),
                renderers = hands.GetComponentsInChildren<Renderer>(true).Select(r => new { id = r.GetInstanceID(), r.enabled, shadow = r.shadowCastingMode.ToString(), r.receiveShadows,
                    materials = r.sharedMaterials.Select(m => m == null ? 0 : m.GetInstanceID()).ToArray(), skin = r is SkinnedMeshRenderer skin ? SkinState(skin) : null }).ToArray(),
                materials = materials.Select(m => new { id = m.GetInstanceID(), shader = m.shader.GetInstanceID(), m.renderQueue, keywords = m.shaderKeywords,
                    color = m.HasProperty("_Color") ? C(m.GetColor("_Color")) : null, main = m.mainTexture?.GetInstanceID(), scale = V2(m.mainTextureScale), offset = V2(m.mainTextureOffset),
                    normal = m.HasProperty("_BumpMap") ? m.GetTexture("_BumpMap")?.GetInstanceID() : null,
                    floats = new[] { "_Glossiness", "_Metallic", "_BumpScale", "_Cutoff", "_Mode", "_SrcBlend", "_DstBlend", "_ZWrite" }
                        .Where(m.HasProperty).Select(name => new { name, value = m.GetFloat(name) }).ToArray() }).ToArray(),
                animator = new { id = animator.GetInstanceID(), animator.enabled, animator.speed, animator.applyRootMotion, mode = animator.updateMode.ToString(), culling = animator.cullingMode.ToString(), controller = animator.runtimeAnimatorController?.GetInstanceID() },
                input = InputState(GameAssets.i.inputs.asset), vehicleInput = InputState(GameAssets.i.vehicleInputs.asset),
                nativeCamera = new { id = foreground.GetInstanceID(), foreground.enabled, foreground.fieldOfView, foreground.aspect, p = V(foreground.transform.localPosition), r = Q(foreground.transform.localRotation), target = foreground.targetTexture?.GetInstanceID() },
                activeTarget = RenderTexture.active?.GetInstanceID(), playerState = gameplay.Get_PlayerState.ToString()
            });
        }

        private static object SkinState(SkinnedMeshRenderer skin)
        {
            var block = new MaterialPropertyBlock(); skin.GetPropertyBlock(block);
            return new { mesh = skin.sharedMesh?.GetInstanceID(), root = skin.rootBone?.GetInstanceID(), bones = skin.bones.Select(b => b == null ? 0 : b.GetInstanceID()).ToArray(),
                bounds = new { center = V(skin.localBounds.center), extents = V(skin.localBounds.extents) }, quality = skin.quality.ToString(), skin.updateWhenOffscreen,
                blendWeights = Enumerable.Range(0, skin.sharedMesh?.blendShapeCount ?? 0).Select(skin.GetBlendShapeWeight).ToArray(),
                propertyBlock = new { block.isEmpty, color = C(block.GetColor("_Color")), mainSt = V4(block.GetVector("_MainTex_ST")) } };
        }

        private static object InputState(InputActionAsset asset) => new { id = asset.GetInstanceID(), maps = asset.actionMaps.Select(m => new { m.id, m.enabled, actions = m.actions.Select(a => new { a.id, a.enabled }).ToArray() }).ToArray(), devices = asset.devices?.Select(d => d.deviceId).ToArray() };
        private static Dictionary<string, Quaternion> Rotations(Transform root) => root.GetComponentsInChildren<Transform>(true).ToDictionary(t => t.GetInstanceID().ToString(), t => t.localRotation);
        private static float RotationDelta(Dictionary<string, Quaternion> first, Dictionary<string, Quaternion> second) => first.Count == 0 ? 0 : first.Max(p => second.TryGetValue(p.Key, out var rotation) ? Quaternion.Angle(p.Value, rotation) : 180f);
        private static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
        private static float[] V2(Vector2 v) => new[] { v.x, v.y };
        private static float[] Q(Quaternion q) => new[] { q.x, q.y, q.z, q.w };
        private static float[] V4(Vector4 v) => new[] { v.x, v.y, v.z, v.w };
        private static float[] C(Color c) => new[] { c.r, c.g, c.b, c.a };
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        private static void SaveTexture(RenderTexture target, string path, bool requireContent)
        {
            if (target == null || !target.IsCreated()) throw new InvalidOperationException("Editor texture was unavailable for readback.");
            var previous = RenderTexture.active; var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = target; image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
                var pixels = image.GetPixels32();
                Require(!requireContent || pixels.Max(p => (int)p.r + p.g + p.b) - pixels.Min(p => (int)p.r + p.g + p.b) > 20, "Editor midpoint capture contains only its solid background.");
                File.WriteAllBytes(path, image.EncodeToPNG());
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.Destroy(image); }
        }

        private object Cleanup() => new { ownedObjects = owned.Count, ownedObjectsAlive = owned.Count(asset => asset != null), graphs = graphs.Count,
            graphsAlive = graphs.Count(graph => graph.IsValid()), cachedMeshes = StockAvatarShirt.CachedMeshCount, cachedLeases = StockAvatarShirt.CachedLeaseCount,
            initialMeshCache, initialLeaseCache, cursorRestored = Cursor.lockState == priorCursorLock && Cursor.visible == priorCursorVisible,
            activeStoreUnchanged = ReferenceEquals(EmotePoseOverrides.Active, priorActive), cleanFrameRestored = MonMPPlugin.CleanFrame == priorCleanFrame };

        private void Finish()
        {
            RememberAssets();
            if (editor != null) { Isolated(() => editor.Dispose(), "dispose"); editor = null; }
            RestoreRegistration();
            finishing = 1;
        }

        public void Dispose()
        {
            if (disposed) return;
            editor?.Dispose(); editor = null; RestoreRegistration(); Running = false; disposed = true;
        }
    }
}
