using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Input;
using MonMP.Core.World;
using MonMP.Plugin.Patches;
using MonMP.Plugin.Testing;
using Newtonsoft.Json;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Explicit isolated native fixture. Never starts without its local request file and save isolation.
    // Commands and results are kept per test copy, so this cannot steer another running game.
    internal sealed class NativeSignEmoteFixture : IDisposable
    {
        private static string Root => Path.Combine(BepInEx.Paths.BepInExRootPath, "MonMP");
        private static readonly RequestFilePoll request = new RequestFilePoll(() => File.Exists(Path.Combine(Root, "native-sign-emote.request")));
        internal static bool Requested => request.Check(Time.unscaledTime);
        private readonly MonMPPlugin plugin;
        private readonly string output;
        private bool loaded, ready, disposed;
        private float readyAt = -1, nextPoll;
        private string lastCommand = "";
        private object? gesture;
        private PlayerVisualHands? hands;
        private Animator? animator;
        private float animatorSpeed;
        private Transform[] bones = Array.Empty<Transform>();
        private Quaternion[] baseline = Array.Empty<Quaternion>();
        private Vector3[] baselinePositions = Array.Empty<Vector3>();
        private readonly List<object> emoteResults = new List<object>();
        private int emoteIndex = -1, emotePhase;
        private float emoteAt;
        private string emoteCommand = "";
        private BendOnCollision? impactTarget;
        private string impactCommand = "";
        private float impactStarted;
        private readonly Harmony collisionProbe = new Harmony("monmp.native-sign-emote-fixture");
        private static NativeSignEmoteFixture? active;
        private readonly List<object> nativeCollisions = new List<object>();
        private NativeShirtFixture? shirtFixture;
        private NativeShirtStressFixture? shirtStressFixture;
        private NativeEmotePoseEditorFixture? poseEditorFixture;
        private System.Collections.IEnumerator? presentationRoutine;
        private string presentationCommand = "";
        private int? originalShirt;
        private static readonly EmoteWheelSlot[] Slots = { EmoteWheelSlot.Wave, EmoteWheelSlot.Point, EmoteWheelSlot.ThumbsUp };

        internal NativeSignEmoteFixture(MonMPPlugin plugin)
        {
            if (!plugin.TestOptions.Enabled || !LocalTestIsolation.Installed)
                throw new InvalidOperationException("Native sign/emote fixture requires isolated local test saves.");
            this.plugin = plugin;
            output = Path.Combine(Root, "diagnostics", "native-sign-emote");
            Directory.CreateDirectory(output);
            active = this;
            collisionProbe.Patch(AccessTools.Method(typeof(Gameplay), "VehicleCollision"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(NativeSignEmoteFixture), nameof(ObserveNativeCollision))) { priority = Priority.First });
            Write("boot", new { isolated = true, instance = plugin.TestOptions.Instance, utc = DateTime.UtcNow });
        }

        internal void Tick(Gameplay? gameplay)
        {
            if (disposed) return;
            try
            {
                if (!loaded)
                {
                    var menu = UnityEngine.Object.FindObjectOfType<MainMenu_Manager>();
                    if (menu != null && Time.unscaledTime > 8)
                    {
                        LocalTestIsolation.SetString("SaveName", "MonMPNativeSignEmote");
                        LocalTestIsolation.SetInt("SaveTimestamp", -1);
                        LocalTestIsolation.SetInt("HardMode", 0);
                        LocalTestIsolation.SetInt("Permadeath", 0);
                        menu.StartGame(false); loaded = true;
                    }
                    return;
                }
                if (gameplay?.Player?.mainCamera == null || !NativeLoadGate.Ready || gameplay.Get_isGamePaused) return;
                if (!ready)
                {
                    if (readyAt < 0) readyAt = Time.unscaledTime + 7;
                    if (Time.unscaledTime < readyAt) return;
                    ready = true; Write("ready", Status(gameplay));
                }
                if (emoteIndex >= 0) { TickEmotes(gameplay); return; }
                if (shirtFixture?.Running == true) { shirtFixture.Tick(gameplay); return; }
                if (shirtStressFixture?.Running == true) { shirtStressFixture.Tick(); return; }
                if (poseEditorFixture?.Running == true) { poseEditorFixture.Tick(); return; }
                if (presentationRoutine != null)
                {
                    try { if (presentationRoutine.MoveNext()) return; }
                    catch (Exception ex) { Write(presentationCommand, new { failed = true, error = ex.ToString() }); }
                    (presentationRoutine as IDisposable)?.Dispose(); presentationRoutine = null;
                    return;
                }
                if (impactTarget != null)
                {
                    if ((bool)AccessTools.Field(typeof(BendOnCollision), "alreadyBend").GetValue(impactTarget))
                    {
                        Write(impactCommand, new { bent = true, physical = true, seconds = Time.unscaledTime - impactStarted, nativeCollisions = nativeCollisions.ToArray(), signs = SignStatus() });
                        impactTarget = null;
                    }
                    else if (Time.unscaledTime - impactStarted > 7)
                    {
                        Write(impactCommand, new { bent = false, physical = true, timedOut = true, status = VehicleStatus(gameplay), nativeCollisions = nativeCollisions.ToArray(), signs = SignStatus() });
                        impactTarget = null;
                    }
                    else return;
                }
                if (Time.unscaledTime < nextPoll) return;
                nextPoll = Time.unscaledTime + 0.15f;
                string path = Path.Combine(Root, "native-sign-emote.command");
                if (!File.Exists(path)) return;
                string command = File.ReadAllText(path).Trim();
                if (command.Length == 0 || command == lastCommand) return;
                lastCommand = command;
                var parts = command.Split('|');
                if (parts.Length < 2) throw new InvalidOperationException("Command requires id|action.");
                string id = parts[0], action = parts[1];
                try
                {
                    if (action == "status") Write(id, Status(gameplay));
                    else if (action == "start") { plugin.BeginPairedTestSession(); Write(id, new { started = true }); }
                    else if (action == "disconnect") { plugin.EndPairedTestSession(); Write(id, new { disconnected = true }); }
                    else if (action == "emotes") BeginEmotes(id, gameplay);
                    else if (action == "presentation")
                    { presentationCommand = id; presentationRoutine = NativePlayerPresentationFixture.Run(plugin, output, id, Write); }
                    else if (action == "pose-editor")
                    {
                        poseEditorFixture?.Dispose();
                        poseEditorFixture = new NativeEmotePoseEditorFixture(plugin, gameplay, output, id, Write);
                    }
                    else if (action == "quit") { Write(id, new { quit = true }); Application.Quit(); }
                    else if (action == "signs") Write(id, SignStatus());
                    else if (action == "shirt") Write(id, ShirtStatus(gameplay));
                    else if (action == "shirt-runtime-probe") Write(id, NativeShirtMeshProbe.Run(gameplay, output));
                    else if (action == "shirt-capture")
                    {
                        shirtFixture ??= new NativeShirtFixture(plugin, gameplay, output, Write, Capture);
                        shirtFixture.Begin(id);
                    }
                    else if (action == "shirt-stress")
                    {
                        shirtFixture ??= new NativeShirtFixture(plugin, gameplay, output, Write, Capture);
                        shirtStressFixture?.Dispose();
                        shirtStressFixture = new NativeShirtStressFixture(gameplay, id, Write);
                    }
                    else if (action == "shirt-assets") Write(id, new { capture = shirtFixture?.AssetStatus(gameplay), stress = shirtStressFixture?.Status() });
                    else if (action == "shirt-color-restore")
                    {
                        if (originalShirt is int selected) gameplay.Player.Get_VisualHands.ChangeShirt(selected);
                        Write(id, new { restored = originalShirt, actual = gameplay.Player.Get_VisualHands.Get_ShirtColor });
                    }
                    else if (action.StartsWith("shirt-color-", StringComparison.Ordinal))
                    {
                        int selected = int.Parse(action.Substring("shirt-color-".Length));
                        if (selected < 0 || selected > 8) throw new InvalidOperationException("Invalid native shirt color.");
                        originalShirt ??= gameplay.Player.Get_VisualHands.Get_ShirtColor;
                        gameplay.Player.Get_VisualHands.ChangeShirt(selected);
                        Write(id, new { selected, actual = gameplay.Player.Get_VisualHands.Get_ShirtColor });
                    }
                    else if (action == "vehicle-near")
                    {
                        var shared = plugin.Vehicles!; shared.TestType = Vehicle_Type.SmollATV;
                        var position = shared.TestSeat + Vector3.right * 1.5f;
                        gameplay.PlayerWalking.transform.position = position;
                        if (gameplay.Get_Player_Rigidbody != null) { gameplay.Get_Player_Rigidbody.position = position; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
                        Write(id, VehicleStatus(gameplay));
                    }
                    else if (action == "vehicle-enter") { plugin.Vehicles!.TestEnter(); Write(id, VehicleStatus(gameplay)); }
                    else if (action == "vehicle-exit") { plugin.Vehicles!.TestExit(); Write(id, VehicleStatus(gameplay)); }
                    else if (action == "sign-stage")
                    {
                        if (plugin.Session?.IsHost != true) throw new InvalidOperationException("Only host stages unoccupied test vehicle.");
                        var shared = plugin.Vehicles!; shared.TestType = Vehicle_Type.SmollATV;
                        var vehicle = shared.TestVehicle;
                        var sign = NextPhysicalSign(vehicle.transform.position);
                        StageVehicle(vehicle, sign, 0);
                        Write(id, new { staged = true, sign = RuntimeProbe.CanonicalPath(sign.transform), vehicle = VehicleStatus(gameplay) });
                    }
                    else if (action == "sign-drive")
                    {
                        var shared = plugin.Vehicles!; var vehicle = shared.TestVehicle;
                        if (gameplay.Get_CurrentVehicleController != vehicle || gameplay.Get_PlayerState != PlayerState.InVehicle_Driver)
                            throw new InvalidOperationException("Physical impact requires native active driver seat.");
                        impactTarget = NextPhysicalSign(vehicle.transform.position);
                        StageVehicle(vehicle, impactTarget, 10);
                        impactCommand = id; impactStarted = Time.unscaledTime;
                        nativeCollisions.Clear();
                    }
                    else if (action == "sign-publish-fixture")
                    {
                        if (plugin.Session?.IsHost != true) throw new InvalidOperationException("Host fixture publication requires host.");
                        var sign = UnityEngine.Object.FindObjectsOfType<BendOnCollision>()
                            .OrderBy(s => RuntimeProbe.CanonicalPath(s.transform)).First(s => plugin.World!.RoadSigns.EntityOf(s) != 0);
                        // Explicit synthetic fixture mutation: exercises publication and replay, not a physical impact.
                        sign.transform.localEulerAngles += new Vector3(37, 0, 23);
                        AccessTools.Field(typeof(BendOnCollision), "alreadyBend").SetValue(sign, true);
                        sign.GetComponent<Collider>().enabled = false;
                        plugin.World!.RoadSigns.AfterCollision(sign, true);
                        Write(id, new { synthetic = true, entity = plugin.World.RoadSigns.EntityOf(sign), signs = SignStatus() });
                    }
                    else if (action == "sign-corrupt-fixture")
                    {
                        if (plugin.Session?.IsHost != false) throw new InvalidOperationException("Guest repair corruption requires guest.");
                        var sign = UnityEngine.Object.FindObjectsOfType<BendOnCollision>()
                            .OrderBy(s => RuntimeProbe.CanonicalPath(s.transform)).First(s => plugin.World!.RoadSigns.EntityOf(s) != 0);
                        sign.transform.localEulerAngles += new Vector3(11, 0, 17);
                        sign.GetComponent<Collider>().enabled = true;
                        Write(id, new { synthetic = true, entity = plugin.World!.RoadSigns.EntityOf(sign), signs = SignStatus() });
                    }
                    else if (action == "repair-signs")
                    {
                        bool requested = plugin.World!.Channel.RequestResync(MonMP.Core.World.SnapshotScope.Section(155), "Native road sign fixture repair");
                        Write(id, new { requested });
                    }
                    else throw new InvalidOperationException("Unknown native fixture action: " + action);
                }
                catch (Exception ex) { Write(id, new { failed = true, error = ex.ToString() }); RestoreEmotes(); }
            }
            catch (Exception ex) { Write("fatal", new { failed = true, error = ex.ToString() }); RestoreEmotes(); disposed = true; }
        }

        private object Status(Gameplay gameplay) => new
        {
            ready, focused = Application.isFocused, cursor = Cursor.lockState.ToString(),
            playerState = gameplay.Get_PlayerState.ToString(),
            session = plugin.Session?.State.ToString(), transport = plugin.Session?.TransportDescription,
            synchronized = plugin.World?.Channel.FullySynchronized, personal = plugin.World?.PersonalProfileReady,
            peers = plugin.Session?.Peers.Count, signs = SignStatus(),
            vehicle = VehicleStatus(gameplay),
            cameras = UnityEngine.Object.FindObjectsOfType<Camera>(true).Select(c => new
            {
                c.name, c.enabled, c.depth, c.cullingMask, c.fieldOfView, c.aspect, c.nearClipPlane, c.farClipPlane,
                position = V(c.transform.position), rotation = V(c.transform.eulerAngles)
            }).ToArray()
        };

        private static void ObserveNativeCollision(Collision collision)
        {
            var probe = active;
            if (probe == null || collision?.collider == null) return;
            var sign = collision.collider.GetComponent<BendOnCollision>();
            if (sign == null) return;
            probe.nativeCollisions.Add(new
            {
                route = "Gameplay.VehicleCollision", speed = collision.relativeVelocity.magnitude,
                path = RuntimeProbe.CanonicalPath(sign.transform), contacts = collision.contactCount,
                point = collision.contactCount > 0 ? V(collision.GetContact(0).point) : Array.Empty<float>()
            });
        }

        private object VehicleStatus(Gameplay gameplay)
        {
            var shared = plugin.Vehicles;
            if (shared?.Ready != true) return new { ready = false };
            var vehicle = shared.TestVehicle; var state = shared.TestState;
            return new
            {
                ready = true, vehicle = vehicle.name, type = vehicle.vehicle_type.ToString(), entity = shared.TestId,
                owner = state.Driver.Owner, generation = state.Driver.Generation, localPeer = plugin.Session?.LocalPeerId,
                currentNative = gameplay.Get_CurrentVehicleController == vehicle, state = gameplay.Get_PlayerState.ToString(),
                position = V(vehicle.transform.position), speed = vehicle.vehicleRigidbody.velocity.magnitude,
                acceptedSpeed = new Vector3(state.Velocity.X, state.Velocity.Y, state.Velocity.Z).magnitude,
                kinematic = vehicle.vehicleRigidbody.isKinematic
            };
        }

        private BendOnCollision NextPhysicalSign(Vector3 position) => UnityEngine.Object.FindObjectsOfType<BendOnCollision>()
            .Where(s => plugin.World!.RoadSigns.EntityOf(s) != 0 && s.GetComponent<Collider>().enabled)
            .OrderBy(s => Vector3.Distance(position, s.transform.position)).First();

        private static void StageVehicle(NWH.VehiclePhysics2.VehicleController vehicle, BendOnCollision sign, float speed)
        {
            var collider = sign.GetComponent<Collider>();
            var direction = Vector3.ProjectOnPlane(sign.transform.forward, Vector3.up).normalized;
            if (direction.sqrMagnitude < 0.5f) direction = Vector3.forward;
            var position = collider.bounds.center - direction * 3f;
            if (Physics.Raycast(position + Vector3.up * 10, Vector3.down, out var ground, 30, ~0, QueryTriggerInteraction.Ignore))
                position.y = ground.point.y + 0.7f;
            vehicle.vehicleRigidbody.position = position;
            vehicle.vehicleRigidbody.rotation = Quaternion.LookRotation(direction);
            vehicle.vehicleRigidbody.angularVelocity = Vector3.zero;
            vehicle.vehicleRigidbody.velocity = direction * speed;
            vehicle.brakes.handbrakeValue = 0;
        }

        private object SignStatus() => UnityEngine.Object.FindObjectsOfType<BendOnCollision>(true)
            .OrderBy(s => RuntimeProbe.CanonicalPath(s.transform))
            .Select(s => new
            {
                path = RuntimeProbe.CanonicalPath(s.transform), active = s.gameObject.activeInHierarchy,
                position = V(s.transform.position), euler = V(s.transform.localEulerAngles),
                bent = AccessTools.Field(typeof(BendOnCollision), "alreadyBend").GetValue(s),
                collider = s.GetComponent<Collider>()?.enabled,
                entity = plugin.World?.RoadSigns.EntityOf(s),
                solidity = AccessTools.Field(typeof(BendOnCollision), "Solidity").GetValue(s),
                xAxisOnly = AccessTools.Field(typeof(BendOnCollision), "XAxisOnly").GetValue(s)
            }).ToArray();

        private object ShirtStatus(Gameplay gameplay)
        {
            var first = AccessTools.Field(typeof(PlayerVisualHands), "Mesh_Shirt").GetValue(gameplay.Player.Get_VisualHands) as SkinnedMeshRenderer;
            var renderers = UnityEngine.Object.FindObjectsOfType<SkinnedMeshRenderer>(true)
                .Where(r => r == first || r.name.IndexOf("Shirt_LongSleeve", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            var report = new List<object>();
            foreach (var renderer in renderers)
            {
                var baked = new Mesh();
                try
                {
                    renderer.BakeMesh(baked);
                    var uv = baked.uv;
                    report.Add(new
                    {
                        path = RuntimeProbe.CanonicalPath(renderer.transform), firstPerson = renderer == first,
                        mesh = renderer.sharedMesh?.name, vertices = baked.vertexCount,
                        uv = uv.Select(v => new[] { v.x, v.y }).ToArray(),
                        materials = renderer.sharedMaterials.Select(m => new
                        {
                            m.name, shader = m.shader?.name, keywords = m.shaderKeywords,
                            color = m.HasProperty("_Color") ? new[] { m.color.r, m.color.g, m.color.b, m.color.a } : Array.Empty<float>(),
                            textures = m.GetTexturePropertyNames().Select(p => new
                            {
                                property = p, name = m.GetTexture(p)?.name, width = m.GetTexture(p)?.width, height = m.GetTexture(p)?.height,
                                scale = new[] { m.GetTextureScale(p).x, m.GetTextureScale(p).y }, offset = new[] { m.GetTextureOffset(p).x, m.GetTextureOffset(p).y }
                            }).ToArray()
                        }).ToArray()
                    });
                    if (renderer == first && renderer.sharedMaterial?.mainTexture is Texture source)
                    {
                        var previous = RenderTexture.active;
                        var target = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
                        var read = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                        try
                        {
                            Graphics.Blit(source, target); RenderTexture.active = target;
                            read.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0); read.Apply();
                            File.WriteAllBytes(Path.Combine(output, "native-first-person-shirt.png"), read.EncodeToPNG());
                        }
                        finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); UnityEngine.Object.Destroy(read); }
                    }
                }
                finally { UnityEngine.Object.Destroy(baked); }
            }
            return report;
        }

        private void BeginEmotes(string id, Gameplay gameplay)
        {
            hands = gameplay.Player.Get_VisualHands ?? throw new InvalidOperationException("Native visual hands are absent.");
            animator = AccessTools.Field(typeof(PlayerVisualHands), "animator").GetValue(hands) as Animator;
            if (animator == null) throw new InvalidOperationException("Native hands animator is absent.");
            animatorSpeed = animator.speed; animator.speed = 0;
            bones = hands.GetComponentsInChildren<Transform>(true).Where(t =>
                t.name == "Shoulder.R" || t.name == "Upper Arm.R" || t.name == "Lower Arm.R" || t.name == "Hand.R" ||
                t.name.StartsWith("Hand.Finger", StringComparison.Ordinal) && t.name.EndsWith(".R", StringComparison.Ordinal) ||
                t.name.StartsWith("Thumb0", StringComparison.Ordinal) && t.name.EndsWith(".R", StringComparison.Ordinal)).ToArray();
            if (bones.Length < 15) throw new InvalidOperationException("Expected native right arm/finger rig, found " + bones.Length + " bones.");
            baseline = bones.Select(b => b.localRotation).ToArray();
            baselinePositions = bones.Select(b => b.localPosition).ToArray();
            var type = typeof(MonMPPlugin).Assembly.GetType("MonMP.Plugin.UI.FirstPersonEmoteGesture") ?? throw new MissingMemberException("FirstPersonEmoteGesture");
            gesture = Activator.CreateInstance(type, true) ?? throw new InvalidOperationException("Cannot create production gesture overlay.");
            emoteResults.Clear(); emoteCommand = id; emoteIndex = 0; emotePhase = 0;
            Capture(gameplay, "baseline");
        }

        private void TickEmotes(Gameplay gameplay)
        {
            if (gesture == null || hands == null) throw new InvalidOperationException("Emote fixture lost native rig.");
            var type = gesture.GetType();
            if (emotePhase == 0)
            {
                var slot = Slots[emoteIndex];
                var begin = AccessTools.Method(type, "Begin") ?? throw new MissingMethodException(type.Name, "Begin");
                var parameters = begin.GetParameters();
                var kindType = parameters[2].ParameterType;
                object kind = Enum.Parse(kindType, slot.ToString());
                object? result = begin.Invoke(gesture, new object[] { hands, NativeGestureView(gameplay), kind });
                if (result is bool success && !success) throw new InvalidOperationException("Production overlay refused " + slot);
                emoteAt = Time.unscaledTime; emotePhase = 1;
            }
            // The fixture calls the production overlay after native animation, as WheelEmoteActions does.
            AccessTools.Method(type, "RestoreBeforeAnimation").Invoke(gesture, null);
            AccessTools.Method(type, "Apply").Invoke(gesture, null);
            if (emotePhase == 1 && Time.unscaledTime - emoteAt >= 0.55f)
            {
                var deltas = bones.Select((b, i) => new
                {
                    name = b.name, degrees = Quaternion.Angle(baseline[i], b.localRotation), position = V(b.localPosition), world = V(b.position),
                    mainViewport = V(gameplay.Player.mainCamera.WorldToViewportPoint(b.position)),
                    foregroundViewport = V(UnityEngine.Object.FindObjectOfType<CMF.CameraController>().cam3DInFrontOfEverything.WorldToViewportPoint(b.position))
                }).ToArray();
                Capture(gameplay, Slots[emoteIndex].ToString().ToLowerInvariant());
                float maximum = deltas.Max(d => d.degrees);
                if (maximum < 5) throw new InvalidOperationException("Native " + Slots[emoteIndex] + " did not move its arm/finger bones.");
                AccessTools.Method(type, "Cancel").Invoke(gesture, null);
                float restored = bones.Select((b, i) => Quaternion.Angle(baseline[i], b.localRotation)).Max();
                float restoredPosition = bones.Select((b, i) => Vector3.Distance(baselinePositions[i], b.localPosition)).Max();
                if (restored > 0.05f || restoredPosition > 0.00001f) throw new InvalidOperationException("Native cancellation did not restore bones: " + restored + " degrees, " + restoredPosition + " local units.");
                emoteResults.Add(new { slot = Slots[emoteIndex].ToString(), maximumDegrees = maximum, restoredDegrees = restored, restoredPosition, deltas });
                emotePhase = 2; emoteAt = Time.unscaledTime;
            }
            else if (emotePhase == 2 && Time.unscaledTime - emoteAt > 0.2f)
            {
                // A second camera render in the cancellation frame can reuse Unity's old skin buffer.
                Capture(gameplay, Slots[emoteIndex].ToString().ToLowerInvariant() + "-cancelled");
                emoteIndex++;
                if (emoteIndex < Slots.Length) { emotePhase = 0; return; }
                AccessTools.Method(type, "Begin").Invoke(gesture, new object[] { hands, NativeGestureView(gameplay), MonMP.Core.Protocol.PlayerEmoteKind.Wave });
                emotePhase = 3; emoteAt = Time.unscaledTime; return;
            }
            else if (emotePhase == 3 && Time.unscaledTime - emoteAt > 2.35f)
            {
                bool active = (bool)AccessTools.Property(type, "IsActive").GetValue(gesture);
                float restored = bones.Select((b, i) => Quaternion.Angle(baseline[i], b.localRotation)).Max();
                if (active || restored > 0.05f) throw new InvalidOperationException("Native expiry retained arm pose: active=" + active + ", delta=" + restored);
                emoteResults.Add(new { expiry = true, active, restoredDegrees = restored });
                Capture(gameplay, "expired");
                AccessTools.Method(type, "Begin").Invoke(gesture, new object[] { hands, NativeGestureView(gameplay), MonMP.Core.Protocol.PlayerEmoteKind.Point });
                emotePhase = 4; emoteAt = Time.unscaledTime; return;
            }
            else if (emotePhase == 4 && Time.unscaledTime - emoteAt > 0.55f)
            {
                var view = NativeGestureView(gameplay); float oldFov = view.fieldOfView;
                try
                {
                    view.fieldOfView = 18;
                    AccessTools.Method(type, "Apply").Invoke(gesture, null);
                    bool active = (bool)AccessTools.Property(type, "IsActive").GetValue(gesture);
                    bool began = (bool)AccessTools.Method(type, "Begin").Invoke(gesture,
                        new object[] { hands, view, MonMP.Core.Protocol.PlayerEmoteKind.Wave });
                    float restored = bones.Select((b, i) => Quaternion.Angle(baseline[i], b.localRotation)).Max();
                    if (active || began || restored > 0.05f)
                        throw new InvalidOperationException("Native narrow zoom retained or began first-person pose.");
                    emoteResults.Add(new { zoomFov = 18, active, began, restoredDegrees = restored });
                }
                finally { view.fieldOfView = oldFov; }
                Write(emoteCommand, new
                {
                    passed = true, evidence = "Production overlay on actual native first-person rig; composed world and native foreground cameras. Native animator speed was temporarily frozen for exact cancellation comparison.",
                    focused = Application.isFocused, wheelInputTested = false, results = emoteResults.ToArray()
                });
                RestoreEmotes();
            }
        }

        private void Capture(Gameplay gameplay, string name)
        {
            var main = gameplay.Player.mainCamera;
            var foreground = UnityEngine.Object.FindObjectOfType<CMF.CameraController>()?.cam3DInFrontOfEverything;
            if (main == null || foreground == null) throw new InvalidOperationException("Native world/foreground camera stack is absent.");
            var previousActive = RenderTexture.active;
            var oldMain = main.targetTexture; var oldForeground = foreground.targetTexture;
            var target = new RenderTexture(960, 700, 24, RenderTextureFormat.ARGB32);
            var image = new Texture2D(960, 700, TextureFormat.RGB24, false);
            try
            {
                target.Create(); main.targetTexture = target; foreground.targetTexture = target;
                main.Render(); foreground.Render(); RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 960, 700), 0, 0); image.Apply();
                File.WriteAllBytes(Path.Combine(output, name + ".png"), image.EncodeToPNG());
            }
            finally
            {
                main.targetTexture = oldMain; foreground.targetTexture = oldForeground; RenderTexture.active = previousActive;
                UnityEngine.Object.Destroy(image); target.Release(); UnityEngine.Object.Destroy(target);
            }
        }

        private static Camera NativeGestureView(Gameplay gameplay) =>
            gameplay.PlayerWalking?.playerCameraController?.cam3DInFrontOfEverything ?? gameplay.Player.mainCamera;

        private static float[] V(Vector3 value) => new[] { value.x, value.y, value.z };
        private void Write(string id, object value)
        {
            if (id.Any(c => !char.IsLetterOrDigit(c) && c != '-')) throw new InvalidOperationException("Invalid command id.");
            string path = Path.Combine(output, id + ".json"), temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(value, Formatting.Indented));
            if (File.Exists(path)) File.Delete(path); File.Move(temp, path);
        }
        private void RestoreEmotes()
        {
            if (gesture != null) AccessTools.Method(gesture.GetType(), "Cancel")?.Invoke(gesture, null);
            gesture = null; emoteIndex = -1;
            if (animator != null) animator.speed = animatorSpeed;
            animator = null;
        }
        public void Dispose()
        {
            RestoreEmotes(); shirtFixture?.Dispose(); shirtStressFixture?.Dispose(); poseEditorFixture?.Dispose();
            (presentationRoutine as IDisposable)?.Dispose(); presentationRoutine = null; collisionProbe.UnpatchSelf();
            if (active == this) active = null;
            disposed = true;
        }
    }
}
