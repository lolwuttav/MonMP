#if MONMP_DEVELOPMENT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Plugin.Replication;
using MonMP.Plugin.UI;
using Newtonsoft.Json;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed partial class NpcAvatarRenderFixture
    {
        private static readonly string[] ContextModels = { "default", "francis", "jacques", "melanie", "carole", "seb", "police", "chef" };
        private sealed class ContextShot
        {
            internal string Key = "", Category = "", Source = "";
            internal ItemPrefabCatalog.Entry? Item;
            internal InteractableTools? SceneTool;
            internal VehicleSeatPoseCatalog.Entry? Seat;
            internal AvatarPreviewCapture.View View;
            internal bool Cutaway;
        }
        private List<ContextShot>? contextShots;
        private GameObject? contextStage, contextCopy;
        private string contextSource = "";
        private readonly List<Mesh> contextMeshes = new List<Mesh>();
        private readonly List<Material> contextMaterials = new List<Material>();
        private sealed class ContextSurface
        {
            internal Renderer Renderer = null!;
            internal Material[] Solid = null!;
            internal Material[]? Cutaway;
            internal MaterialPropertyBlock Block = null!;
            internal MaterialPropertyBlock[] Blocks = null!;
        }
        private readonly List<ContextSurface> contextSurfaces = new List<ContextSurface>();
        private readonly RemoteEquippedPose contextEquip = new RemoteEquippedPose();

        private List<ContextShot> BuildContextShots()
        {
            var shots = new List<ContextShot>();
            var catalog = ItemPrefabCatalog.Load(directory);
            foreach (string path in new[] { "Tool/AdjustableSpanner", "Tool/Chainsaw", "Tool/FishingRod", "Tool/PruningLoppers",
                "Tool/SprayPaint/SprayPaint_Blue", "Tool/FuelCanister_20L", "Tool/WateringCan", "Fishing/FishBucket" })
            {
                var entry = catalog.Entries.Values.Single(e => e.Definition.Path == path);
                AddItem(entry.Definition.Path.Split('/').Last().ToLowerInvariant(), entry, null);
            }
            var impact = UnityEngine.Object.FindObjectsOfType<Tool_Air_Impact>(true).FirstOrDefault();
            Require(impact != null, "Native impact gun was not loaded.");
            HeldGripOverrides.RegisterSceneEquipment(impact!, out _, out _);
            AddItem("impact-gun", null, impact);
            foreach (string prop in new[] { "map", "flashlight", "console", "binoculars" })
                shots.Add(new ContextShot { Key = prop, Source = prop, Category = "hand-prop", View = AvatarPreviewCapture.View.Front });
            var seats = VehicleSeatPoseCatalog.Discover(null);
            File.WriteAllText(Path.Combine(directory, "context-seats.json"), JsonConvert.SerializeObject(
                seats.Select(s => new { s.VehicleKey, s.SeatKey, s.Shape, s.Label }), Formatting.Indented));
            foreach (var spec in new[] {
                (Key: "truck-driver", Model: "OlTruck/OlTruck", Role: "driver/"),
                (Key: "konig-driver", Model: "MainVehicle/Konig", Role: "driver/"),
                (Key: "konig-passenger", Model: "MainVehicle/Konig", Role: "passenger/"),
                (Key: "prinz-driver", Model: "Prinz/Prinz", Role: "driver/"),
                (Key: "missile-driver", Model: "LeMissile/LeMissile", Role: "driver/"),
                (Key: "atv-driver", Model: "SmollATV/SmollATV", Role: "driver/"),
                (Key: "buggy-driver", Model: "Buggy/Buggy", Role: "driver/"),
                (Key: "scrap-konig-driver", Model: "ScrapyardKonig/ScrapyardKonig", Role: "driver/"),
                (Key: "bus-passenger", Model: "AI_Vehicle/NWH_SchoolBus", Role: "passenger/") })
            {
                var seat = seats.FirstOrDefault(s => s.VehicleKey == spec.Model && s.SeatKey.StartsWith(spec.Role, StringComparison.Ordinal));
                Require(seat != null, "Native seat not loaded: " + spec.Key);
                foreach (string view in new[] { "front-cutaway", "side-cutaway", "side-solid" })
                    shots.Add(new ContextShot { Key = spec.Key + "-" + view, Category = "vehicle", Source = spec.Key,
                        Seat = seat, Cutaway = view.EndsWith("cutaway", StringComparison.Ordinal),
                        View = view.StartsWith("front", StringComparison.Ordinal) ? AvatarPreviewCapture.View.Front : AvatarPreviewCapture.View.Side });
            }
            return shots;

            void AddItem(string key, ItemPrefabCatalog.Entry? item, InteractableTools? scene)
            {
                foreach (var view in new[] { AvatarPreviewCapture.View.Front, AvatarPreviewCapture.View.Side })
                    shots.Add(new ContextShot { Key = key + "-" + view.ToString().ToLowerInvariant(), Source = key,
                        Category = "equipment", Item = item, SceneTool = scene, View = view });
            }
        }
        private string? StepContext()
        {
            if (Time.unscaledTime - started > 600) throw new TimeoutException("Context render batch exceeded ten minutes.");
            contextShots ??= BuildContextShots();
            if (modelIndex >= ContextModels.Length)
            {
                Require(firstPerson.sharedMaterial == nativeMaterial && nativeMaterial.mainTexture == nativeTexture &&
                    nativeMaterial.GetTexture("_BumpMap") == nativeNormal && ((SkinnedMeshRenderer)firstPerson).sharedMesh == nativeMesh,
                    "Native first-person assets changed.");
                File.WriteAllText(Path.Combine(directory, "renders.json"), JsonConvert.SerializeObject(reports, Formatting.Indented));
                string result = "MONMP_NPC_AVATAR_RENDER_PASS\nContext=true\nModels=" + modelIndex + "\nRenders=" + reports.Count +
                    "\nDirectory=" + directory + "\nRenderer-only equipment/vehicles; no native equip, sitting, driving or paired session tested.";
                Dispose(); return result;
            }
            string model = ContextModels[modelIndex];
            if (root == null)
            {
                contextStage = new GameObject("MonMP context render stage"); contextStage.transform.position = new Vector3(0, 1000, 0);
                root = new GameObject("MonMP context avatar " + model); root.transform.SetParent(contextStage.transform, false);
                body = StockAvatarBody.TryCreateModel(root.transform, Color.white, model, out reason);
                Require(body != null && !reason.Contains("fell back"), "Context model failed: " + model + ": " + reason);
                props = RemoteHandProps.Create(root.transform);
                caseIndex = frames = 0; modelStarted = Time.unscaledTime;
            }
            var avatar = body!;
            avatar.Present(StockAvatarBody.Mode.Stand, 0, 0, 0, StockAvatarBody.Overlay.Look(0), .05f);
            if (++frames < 3 || !avatar.FabricReady && Time.unscaledTime - modelStarted < 5) return null;
            Require(avatar.FabricReady && avatar.TestSleeveMaskApplied, "Context shirt did not initialize: " + model);
            var shot = contextShots[caseIndex];
            int color = new[] { 0, 4, 6 }[(modelIndex + caseIndex / 2) % 3];
            gameplay.Player.Get_VisualHands.ChangeShirt(color); avatar.ShirtMaterial.color = nativeMaterial.color;
            var overlay = StockAvatarBody.Overlay.Look(0);
            if (shot.Source == "map") overlay.Map = 1;
            if (shot.Source == "flashlight") overlay.Flashlight = 1;
            if (shot.Source == "console") overlay.Console = 1;
            if (shot.Source == "binoculars") overlay.Zoom = 1;
            root.transform.SetPositionAndRotation(contextStage!.transform.position, Quaternion.identity);
            avatar.SetPresentationScale(shot.Source == "missile-driver" ? StockAvatarBody.MissileScale : 1);
            avatar.SetRacingHelmet(shot.Source == "missile-driver");
            string nativeSource;
            bool calibrated;
            if (shot.Seat != null) { nativeSource = RenderContextSeat(shot, avatar, overlay); calibrated = true; }
            else if (shot.Category == "equipment") { nativeSource = RenderContextTool(shot, avatar, overlay); calibrated = true; }
            else
            {
                ReleaseContextCopy();
                var goals = new StockAvatarBody.Goals();
                props!.PrepareMapCalibration(ref goals, MonMPPlugin.Instance!.HeldGrips, overlay.Map > 0);
                props.PrepareFlashlightCalibration(ref goals, MonMPPlugin.Instance.HeldGrips, overlay.Flashlight > 0);
                for (int i = 0; i < 6; i++) { avatar.Present(StockAvatarBody.Mode.Stand, 0, 0, 0, overlay, .2f); avatar.Solve(goals, 1); }
                nativeSource = "native hand prop"; calibrated = shot.Source == "map" || shot.Source == "flashlight";
            }
            props!.Update(avatar, overlay.Map, overlay.Console, overlay.Flashlight, 0, 0, true, zoom: overlay.Zoom > 0, suppressFeedback: true);
            Require(contextStage.GetComponentsInChildren<Collider>(true).Length == 0 && contextStage.GetComponentsInChildren<MonoBehaviour>(true).Length == 0 &&
                contextStage.GetComponentsInChildren<Rigidbody>(true).Length == 0, "Context copy contains gameplay components.");
            string file = model + "-" + shot.Key + ".bmp";
            AvatarPreviewCapture.SaveBitmap(contextStage.transform, Path.Combine(directory, file), shot.View, contextStage.transform,
                shot.Category == "vehicle" ? avatar.Head.position - Vector3.up * .5f : (Vector3?)null,
                shot.Category == "vehicle" ? 1.25f : shot.Source == "fishingrod" ? 1.9f : 1f);
            reports.Add(new { model, shot = shot.Key, file, color, category = shot.Category, context = shot.Source, calibrated,
                nativeSource, sources = reason, fabric = avatar.FabricReady, sleeveMask = avatar.TestSleeveMaskApplied,
                helmet = avatar.RacingHelmetVisible, scale = avatar.VisualScale });
            if (++caseIndex >= contextShots.Count) { ReleaseModel(); modelIndex++; }
            return null;
        }
        private string RenderContextTool(ContextShot shot, StockAvatarBody avatar, StockAvatarBody.Overlay overlay)
        {
            var item = shot.Item?.Asset.GetComponent<InteractableItems>() ?? shot.SceneTool;
            Require(item != null, "Context equipment is missing.");
            string before = ContextStamp(item!.transform);
            if (contextSource != shot.Source)
            {
                ReleaseContextCopy(); contextSource = shot.Source;
                contextCopy = new GameObject("MonMP context equipment"); contextCopy.transform.SetParent(contextStage!.transform, false);
                contextCopy.transform.localScale = item.transform.lossyScale;
                HoldPreviewVisualCopy.Copy(item.transform, contextCopy.transform, 0, contextMeshes, equippedPreview: true);
            }
            var grips = MonMPPlugin.Instance!.HeldGrips;
            string shape;
            if (shot.Item != null) shape = shot.Item.Definition.ShapeHash;
            else HeldGripOverrides.RegisterSceneEquipment((InteractableTools)item, out _, out shape);
            Require(grips.TryGetRuntime(item, shape, out var palm, out var turn, out var width, out var left, out var leftTurn),
                "No calibrated grip for " + shot.Source);
            Require(contextEquip.TryGet(item, gameplay, root!.transform.position + Vector3.up * 1.6f, Quaternion.identity,
                out var position, out var rotation), "Native equip anchor missing: " + shot.Source);
            grips.TryGetVisualOffset(item, shape, out var offset, out var visualTurn);
            for (int i = 0; i < 6; i++)
            {
                contextCopy!.transform.SetPositionAndRotation(position + offset, rotation * visualTurn);
                avatar.Present(StockAvatarBody.Mode.Stand, 0, 0, 0, overlay, .2f);
                var goals = new StockAvatarBody.Goals { Held = contextCopy.transform.TransformPoint(palm),
                    HeldRotation = contextCopy.transform.rotation * turn, HeldWidth = width, ConstrainHeld = true,
                    HeldLeft = left.HasValue ? contextCopy.transform.TransformPoint(left.Value) : (Vector3?)null,
                    HeldLeftRotation = leftTurn.HasValue ? contextCopy.transform.rotation * leftTurn.Value : (Quaternion?)null };
                avatar.Solve(goals, 1, out var translation); contextCopy.transform.position += translation;
            }
            Require(ContextStamp(item.transform) == before, "Native equipment changed during render: " + shot.Source);
            return item.GetType().Name + "/" + HeldGripOverrides.IdentityOf(item);
        }
        private string RenderContextSeat(ContextShot shot, StockAvatarBody avatar, StockAvatarBody.Overlay overlay)
        {
            var seat = shot.Seat!; var source = seat.Vehicle.transform;
            string before = ContextStamp(source);
            var inverse = Quaternion.Inverse(seat.Anchor.rotation);
            Vector3 Map(Vector3 world) => contextStage!.transform.position + inverse * (world - seat.Anchor.position);
            if (contextSource != shot.Source)
            {
                ReleaseContextCopy(); contextSource = shot.Source;
                contextCopy = new GameObject("MonMP context vehicle"); contextCopy.transform.SetParent(contextStage!.transform, false);
                contextCopy.transform.localPosition = inverse * (source.position - seat.Anchor.position);
                contextCopy.transform.localRotation = inverse * source.rotation; contextCopy.transform.localScale = source.lossyScale;
                HoldPreviewVisualCopy.Copy(source, contextCopy.transform, 0, contextMeshes);
                // Installed native parts may remain independent Rigidbody roots rather
                // than children of the chassis. Include only exact installed references,
                // never nearby loose items or arbitrary scene meshes.
                var manager = source.GetComponent<Vehicle_PartsManager>();
                if (manager?.carPartSockets != null)
                {
                    var parts = manager.carPartSockets.Where(s => s != null && s.isInstalled && s.partInstalled != null)
                        .Select(s => s.partInstalled.transform).Where(t => t.gameObject.scene.IsValid() && !t.IsChildOf(source)).Distinct().ToArray();
                    foreach (var part in parts.Where(t => !parts.Any(other => other != t && t.IsChildOf(other))))
                    {
                        string stamp = ContextStamp(part);
                        var copy = new GameObject("Installed " + part.name); copy.transform.SetParent(contextCopy.transform, false);
                        copy.transform.SetPositionAndRotation(Map(part.position), inverse * part.rotation);
                        var scale = contextCopy.transform.lossyScale;
                        copy.transform.localScale = new Vector3(part.lossyScale.x / scale.x, part.lossyScale.y / scale.y, part.lossyScale.z / scale.z);
                        HoldPreviewVisualCopy.Copy(part, copy.transform, 0, contextMeshes);
                        Require(ContextStamp(part) == stamp, "Native installed part changed during copying.");
                    }
                }
                foreach (var surface in contextCopy.GetComponentsInChildren<Renderer>(true))
                {
                    var copy = new ContextSurface { Renderer = surface, Solid = surface.sharedMaterials, Block = new MaterialPropertyBlock() };
                    surface.GetPropertyBlock(copy.Block);
                    copy.Blocks = new MaterialPropertyBlock[copy.Solid.Length];
                    for (int i = 0; i < copy.Blocks.Length; i++) { copy.Blocks[i] = new MaterialPropertyBlock(); surface.GetPropertyBlock(copy.Blocks[i], i); }
                    contextSurfaces.Add(copy);
                }
            }
            SetContextCutaway(shot.Cutaway);
            Require(MonMPPlugin.Instance!.SeatPoses.TryGetRuntime(seat.VehicleKey, seat.SeatKey, seat.Shape, out var pose),
                "No calibrated native seat: " + shot.Source);
            VehicleSeatPosePresentation.BodyPlacement(seat, pose, out var position, out var rotation);
            root!.transform.SetPositionAndRotation(Map(position), inverse * rotation);
            var goals = new StockAvatarBody.Goals();
            VehicleSeatPosePresentation.HandFrame(seat, out var handPosition, out var handRotation);
            if (seat.Mode == StockAvatarBody.Mode.Drive && seat.Wheel != null)
            {
                var grips = VehicleSeatPosePresentation.AutomaticWheelGrips(seat.Wheel, seat.WheelAngle, seat.VehicleRight);
                goals.GripLeft = Map(grips.Left); goals.GripRight = Map(grips.Right); goals.GripCenter = Map(handPosition);
            }
            VehicleSeatPosePresentation.ApplyHands(pose, Map(handPosition), inverse * handRotation, ref goals, seat.Mode == StockAvatarBody.Mode.Drive);
            VehicleSeatPosePresentation.ApplyFeet(pose, contextStage!.transform.position, Quaternion.identity, ref goals);
            for (int i = 0; i < 6; i++)
            {
                avatar.Present(seat.Mode, 0, 0, 0, overlay, .2f);
                avatar.AlignSeatHead(root.transform.position + root.transform.up * 1.52f); avatar.Solve(goals, 1);
            }
            Require(ContextStamp(source) == before, "Native vehicle changed during render: " + shot.Source);
            return seat.VehicleKey + "/" + seat.SeatKey;
        }
        private void SetContextCutaway(bool cutaway)
        {
            foreach (var pair in contextSurfaces)
            {
                if (cutaway && pair.Cutaway == null) pair.Cutaway = pair.Solid.Select((original, index) =>
                {
                    var copy = new Material(Shader.Find("Standard")); if (original != null) copy.CopyPropertiesFromMaterial(original);
                    var color = original != null && original.HasProperty("_Color") ? original.color : Color.white;
                    var tint = pair.Block.GetColor("_Color"); if (tint.a > 0) color = tint;
                    tint = pair.Blocks[index].GetColor("_Color"); if (tint.a > 0) color = tint;
                    color.a = .22f; copy.color = color;
                    copy.SetFloat("_Mode", 2); copy.SetInt("_SrcBlend", 5); copy.SetInt("_DstBlend", 10); copy.SetInt("_ZWrite", 0);
                    copy.DisableKeyword("_ALPHATEST_ON"); copy.DisableKeyword("_ALPHAPREMULTIPLY_ON"); copy.EnableKeyword("_ALPHABLEND_ON"); copy.renderQueue = 3000;
                    contextMaterials.Add(copy); return copy;
                }).ToArray();
                pair.Renderer.sharedMaterials = cutaway ? pair.Cutaway! : pair.Solid;
                pair.Renderer.SetPropertyBlock(cutaway ? null : pair.Block);
                for (int i = 0; i < pair.Solid.Length; i++) pair.Renderer.SetPropertyBlock(cutaway ? null : pair.Blocks[i], i);
            }
        }
        private static string ContextStamp(Transform source) => string.Join("|", source.GetComponentsInChildren<Transform>(true)
            .Select(t => t.GetInstanceID() + ":" + t.localPosition.ToString("R") + t.localRotation.ToString("R") + t.localScale.ToString("R") + t.gameObject.activeSelf)) +
            string.Join("|", source.GetComponentsInChildren<Renderer>(true).Select(r => r.GetInstanceID() + ":" + r.enabled + ":" + r.forceRenderingOff + ":" +
                string.Join(",", r.sharedMaterials.Select(m => m == null ? "null" : m.GetInstanceID() + ":" +
                    (m.HasProperty("_Color") ? m.color.ToString("R") : "")))));
        private void ReleaseContextCopy()
        {
            if (contextCopy != null) { contextCopy.SetActive(false); UnityEngine.Object.Destroy(contextCopy); contextCopy = null; }
            foreach (var mesh in contextMeshes) if (mesh != null) UnityEngine.Object.Destroy(mesh); contextMeshes.Clear();
            foreach (var material in contextMaterials) if (material != null) UnityEngine.Object.Destroy(material); contextMaterials.Clear();
            contextSurfaces.Clear(); contextSource = "";
        }
        private void ReleaseContextStage()
        {
            ReleaseContextCopy();
            if (contextStage != null) { contextStage.SetActive(false); UnityEngine.Object.Destroy(contextStage); contextStage = null; }
        }
    }
}
#endif
