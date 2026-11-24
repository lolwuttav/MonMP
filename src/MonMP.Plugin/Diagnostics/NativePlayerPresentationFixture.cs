using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.UI;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Renderer-only previews in the explicitly requested, save-isolated native fixture.
    internal static class NativePlayerPresentationFixture
    {
        internal static IEnumerator Run(MonMPPlugin plugin, string directory, string id, Action<string, object> write)
        {
            string output = Path.Combine(directory, id + "-presentation"); Directory.CreateDirectory(output);
            var stage = new GameObject("MonMP owned presentation probe");
            var avatar = new GameObject("Probe avatar"); avatar.transform.SetParent(stage.transform, false);
            StockAvatarBody? body = null; RemoteBinoculars? glasses = null;
            var texture = new RenderTexture(1280, 960, 24);
            var captures = new List<string>();
            try
            {
                var entry = VehicleSeatPoseCatalog.Discover(plugin.World).First(e => e.Vehicle.vehicle_type == Vehicle_Type.LeMissile && e.Mode == StockAvatarBody.Mode.Drive);
                var car = new GameObject("Renderer-only Le Missile"); car.transform.SetParent(stage.transform, false);
                CopyRenderers(entry.Vehicle.transform, car.transform, true);
                body = StockAvatarBody.TryCreate(avatar.transform, new Color(.25f, .48f, .7f), out var reason) ?? throw new InvalidOperationException(reason);
                glasses = new RemoteBinoculars(avatar.transform);
                var cameraObject = new GameObject("Probe camera"); cameraObject.transform.SetParent(stage.transform, false);
                var camera = cameraObject.AddComponent<Camera>(); camera.enabled = false; camera.targetTexture = texture;
                camera.cullingMask = 1 << 30; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.13f, .16f, .19f);
                camera.fieldOfView = 38; camera.nearClipPlane = .02f; camera.farClipPlane = 30;
                var lightObject = new GameObject("Probe light"); lightObject.transform.SetParent(stage.transform, false);
                var light = lightObject.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.8f; light.cullingMask = 1 << 30;
                light.transform.rotation = Quaternion.Euler(35, -35, 0);
                void Layers() { foreach (var t in stage.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 30; }
                void Capture(string name, Vector3 target, Vector3 offset)
                {
                    Layers(); camera.transform.position = target + offset; camera.transform.LookAt(target);
                    var previous = RenderTexture.active; var image = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
                    try { camera.Render(); RenderTexture.active = texture; image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); image.Apply();
                        string path = Path.Combine(output, name + ".png"); File.WriteAllBytes(path, image.EncodeToPNG()); captures.Add(path); }
                    finally { RenderTexture.active = previous; UnityEngine.Object.Destroy(image); }
                }
                var overlay = StockAvatarBody.Overlay.Look(0); overlay.Zoom = 1;
                car.SetActive(false); avatar.transform.position = entry.Vehicle.transform.position + Vector3.up * 8;
                body.Present(StockAvatarBody.Mode.Stand, 0, 0, 0, overlay, 1);
                body.Solve(new StockAvatarBody.Goals(), 1); glasses.Place(body);
                Layers(); yield return null; yield return null;
                Capture("zoom-front", body.Head.position + Vector3.down * .23f, new Vector3(.8f, .15f, 1.35f));
                Capture("zoom-side", body.Head.position + Vector3.down * .23f, new Vector3(1.4f, .1f, .1f));
                Quaternion palmReference = Quaternion.Inverse(body.Aim) * body.RightHand.rotation;
                Vector3 eyeReference = body.Head.InverseTransformPoint(glasses.Object.transform.position);
                float maxPalmTurn = 0, maxEyeDrift = 0;
                for (int sample = 0; sample < 48; sample++)
                {
                    float crouch = sample >= 24 ? 1 : 0;
                    body.Present(StockAvatarBody.Mode.Stand, 1, 2.5f, crouch, overlay, 1f / 30);
                    body.Solve(new StockAvatarBody.Goals(), 1f / 30); glasses.Place(body);
                    maxPalmTurn = Mathf.Max(maxPalmTurn, Quaternion.Angle(palmReference, Quaternion.Inverse(body.Aim) * body.RightHand.rotation));
                    maxEyeDrift = Mathf.Max(maxEyeDrift, Vector3.Distance(eyeReference, body.Head.InverseTransformPoint(glasses.Object.transform.position)));
                    yield return null;
                    if (sample == 12 || sample == 36)
                        Capture(sample == 12 ? "zoom-walking" : "zoom-crouch-walking", body.Head.position + Vector3.down * .23f, new Vector3(.8f, .15f, 1.35f));
                }
                Require(maxPalmTurn < .1f && maxEyeDrift < .0001f, "Zoom grip/face anchor changed with gait or crouch.");
                Require(glasses.Object.GetComponentsInChildren<Collider>(true).Length == 0 && glasses.Object.GetComponentsInChildren<MonoBehaviour>(true).Length == 0, "Zoom prop contains gameplay components.");
                glasses.Show(false); Require(!glasses.Object.activeSelf, "Zoom prop did not hide.");
                var labelRoot = new GameObject("Probe compact labels"); labelRoot.transform.SetParent(stage.transform, false);
                labelRoot.transform.position = entry.Vehicle.transform.position + new Vector3(-5, 8, 0);
                var font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                const string letters = "LocalHostLocalGuest1";
                font.RequestCharactersInTexture(letters, 64, FontStyle.Bold);
                float glyphHeight = 1, glyphWidth = 0;
                foreach (char letter in letters) if (font.GetCharacterInfo(letter, out var glyph, 64, FontStyle.Bold))
                { glyphHeight = Mathf.Max(glyphHeight, glyph.maxY - Mathf.Min(0, glyph.minY)); glyphWidth += glyph.advance; }
                glyphHeight = Mathf.Max(glyphHeight, font.lineHeight * 64f / Mathf.Max(1, font.fontSize));
                var labels = new TextMesh[2];
                var layouts = new PlayerMapLabelLayout[2];
                for (int row = 0; row < 2; row++)
                {
                    var node = new GameObject("Probe map label"); node.transform.SetParent(labelRoot.transform, false);
                    var text = labels[row] = node.AddComponent<TextMesh>(); text.font = font; text.fontSize = 64; text.fontStyle = FontStyle.Bold;
                    text.anchor = TextAnchor.MiddleCenter; text.color = Color.white; text.text = row == 0 ? "LocalHost" : "LocalGuest1";
                    node.GetComponent<Renderer>().sharedMaterial = font.material;
                    var layout = layouts[row] = PlayerMapLabelLayout.Calculate(2, 1.5f, .004f, glyphWidth, glyphHeight, .012f, row, 2, .5f, .25f);
                    text.characterSize = layout.CharacterSize; node.transform.localPosition = new Vector3(0, layout.V * 1.5f, 0);
                }
                Layers(); yield return null; yield return null;
                Capture("compact-labels", labelRoot.transform.position + Vector3.up * (layouts[0].V + layouts[1].V) * .75f, new Vector3(0, 0, -1));
                float renderedHeight = labels.Max(t => t.GetComponent<Renderer>().bounds.size.y);
                float rowDistance = Vector3.Distance(labels[0].transform.position, labels[1].transform.position);
                write(id + "-labels", new { fontSize = font.fontSize, lineHeight = font.lineHeight, glyphHeight, glyphWidth, renderedHeight, rowDistance, predictedHeight = layouts[0].TextHeight,
                    spacingRatio = rowDistance / renderedHeight, maxPalmTurn, maxEyeDrift });
                labelRoot.SetActive(false);

                object mapProbe, flashlightProbe;
                var ownedStore = new HeldGripOverrides(Path.Combine(output, "owned-map-grips.json"));
                using (var editor = new HoldPositionEditor(ownedStore, ItemPrefabCatalog.Load(output), plugin.Gameplay!))
                {
                    editor.Show(); mapProbe = editor.ProbeMap(output);
                    yield return null; editor.Tick(); yield return null;
                    editor.CaptureMapProbe(Path.Combine(output, "map-calibrator.png"));
                    flashlightProbe = editor.ProbeFlashlight();
                    yield return null; editor.Tick(); yield return null;
                    editor.CaptureMapProbe(Path.Combine(output, "flashlight-calibrator.png"));
                }
                write(id + "-map", mapProbe);
                write(id + "-flashlight", flashlightProbe);
                Require(renderedHeight > 0 && rowDistance / renderedHeight >= 1 && rowDistance / renderedHeight < 1.9f,
                    $"Native TextMesh stack spacing mismatch: glyph={glyphHeight}, predicted={layouts[0].TextHeight}, rendered={renderedHeight}, step={rowDistance}.");
                car.SetActive(true);
                Vector3 seatPosition = entry.Anchor.position; Quaternion seatRotation = entry.Anchor.rotation;
                bool calibrated = plugin.SeatPoses.TryGetRuntime(entry.VehicleKey, entry.SeatKey, entry.Shape, out var pose);
                if (calibrated) VehicleSeatPosePresentation.BodyPlacement(entry, pose, out seatPosition, out seatRotation);
                Vector3 Pose(float scale)
                {
                    avatar.transform.SetPositionAndRotation(seatPosition, seatRotation); body.SetPresentationScale(scale);
                    body.Present(StockAvatarBody.Mode.Drive, 0, 0, 0, StockAvatarBody.Overlay.Look(0), 1);
                    body.AlignSeatHead(seatPosition + seatRotation * Vector3.up * 1.52f);
                    var goals = new StockAvatarBody.Goals();
                    if (calibrated && pose.CustomHands) { VehicleSeatPosePresentation.HandFrame(entry, out var p, out var r); VehicleSeatPosePresentation.ApplyHands(pose, p, r, ref goals, true); }
                    if (calibrated) VehicleSeatPosePresentation.ApplyFeet(pose, entry.Anchor.position, entry.Anchor.rotation, ref goals);
                    body.Solve(goals, 1);
                    return avatar.GetComponentsInChildren<Transform>(true).First(t => t.name == "Hips").position;
                }
                var baselineHips = Pose(1); var baselineHead = body.Head.position;
                yield return null; yield return null;
                Capture("missile-original", seatPosition + Vector3.up * .9f, entry.Vehicle.transform.rotation * new Vector3(-3.2f, 1, .15f));
                var scaledHips = Pose(StockAvatarBody.MissileScale); var scaledHead = body.Head.position;
                Require(Vector3.Distance(baselineHips, scaledHips) < .002f, "Le Missile scaling changed the calibrated hip anchor.");
                body.SetRacingHelmet(true); Require(body.RacingHelmetVisible, "Native derby helmet not found.");
                var helmet = body.Head.Find("MonMP Racing Helmet");
                Require(helmet.GetComponent<MeshFilter>().sharedMesh.name == "Helmet_FullFace", "Wrong helmet mesh.");
                Layers(); yield return null; yield return null;
                Capture("missile-compact-helmet", seatPosition + Vector3.up * .9f, entry.Vehicle.transform.rotation * new Vector3(-3.2f, 1, .15f));
                Capture("helmet-close", body.Head.position, entry.Vehicle.transform.rotation * new Vector3(-.85f, .35f, 1.1f));
                foreach (float candidate in new[] { .85f, .8f })
                {
                    Pose(candidate); yield return null; yield return null;
                    Capture("missile-scale-" + Math.Round(candidate * 100), seatPosition + Vector3.up * .9f, entry.Vehicle.transform.rotation * new Vector3(-3.2f, 1, .15f));
                }
                car.SetActive(false); avatar.transform.SetPositionAndRotation(entry.Vehicle.transform.position + Vector3.up * 8, Quaternion.identity);
                body.SetPresentationScale(1); body.Present(StockAvatarBody.Mode.Stand, 0, 0, 0, StockAvatarBody.Overlay.Look(0), 1);
                body.Solve(new StockAvatarBody.Goals(), 1); yield return null; yield return null;
                Capture("helmet-standing", body.Head.position, new Vector3(.8f, .2f, 1.2f));
                car.SetActive(false); body.SetRacingHelmet(false); body.SetPresentationScale(1);
                Require(!body.RacingHelmetVisible, "Helmet did not hide after leaving the car.");
                Require(Math.Abs(body.VisualScale - 1) < .2f, "Normal avatar scale not restored.");
                write(id, new { passed = true, evidence = "Native renderer preview across frames; no network session or physical UI input.", calibrated, entry.VehicleKey, entry.SeatKey,
                    baselineHips = V(baselineHips), scaledHips = V(scaledHips), baselineHead = V(baselineHead), scaledHead = V(scaledHead),
                    headReductionMetres = baselineHead.y - scaledHead.y, maxPalmTurn, maxEyeDrift,
                    labelSpacingToGlyphHeight = rowDistance / renderedHeight, mapProbe, flashlightProbe, captures });
            }
            finally { glasses?.Dispose(); body?.Destroy(); texture.Release(); UnityEngine.Object.Destroy(texture); UnityEngine.Object.Destroy(stage); }
        }
        private static void CopyRenderers(Transform source, Transform target, bool root)
        {
            if (root) { target.SetPositionAndRotation(source.position, source.rotation); target.localScale = source.lossyScale; }
            else { target.localPosition = source.localPosition; target.localRotation = source.localRotation; target.localScale = source.localScale; }
            var filter = source.GetComponent<MeshFilter>(); var renderer = source.GetComponent<MeshRenderer>();
            if (filter != null && renderer != null && filter.sharedMesh != null)
            { target.gameObject.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh; var copy = target.gameObject.AddComponent<MeshRenderer>(); copy.sharedMaterials = renderer.sharedMaterials; copy.enabled = renderer.enabled; }
            foreach (Transform child in source) { var copy = new GameObject(child.name); copy.transform.SetParent(target, false); CopyRenderers(child, copy.transform, false); copy.SetActive(child.gameObject.activeSelf); }
        }
        private static object V(Vector3 p) => new { p.x, p.y, p.z };
        private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    }
}
