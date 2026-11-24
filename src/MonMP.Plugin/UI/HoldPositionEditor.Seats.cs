using System;
using System.IO;
using System.Linq;
using System.Text;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.UI
{
    // Seat mode uses the same draft, history, bounded gizmos, picking and camera controls
    // as tool mode. Only the source catalog, placement frames and persistence differ.
    internal sealed partial class HoldPositionEditor
    {
        private readonly VehicleSeatPoseOverrides? seatOverrides;
        private readonly SharedWorld? seatWorld;
        private GameObject? seatBodyRoot, seatVehicleCopy;
        private GameObject? rightFootMarker, leftFootMarker;
        private Material? rightFootMaterial, leftFootMaterial;
        private float rightFootError, leftFootError;
        private int footLiveFrames;
        private string footLiveDirectory = "";
        private Color32[]? footLiveBefore;
        private Vector2 footLiveRight, footLiveLeft;
        private Transform? seatHandFrame, seatWheelCopy;
        private Vector3 seatHandPosition;
        private Quaternion seatHandRotation, seatWheelRotation;
        private float seatCapturedAngle, seatPreviewAngle;
        private bool showSeatVehicle = true, seatCutaway = true;
        private float seatOpacity = 0.2f;
        private sealed class SeatSurface
        {
            internal Renderer Renderer = null!;
            internal Material[] Solid = Array.Empty<Material>(), Cutaway = Array.Empty<Material>();
            internal MaterialPropertyBlock Block = null!;
            internal MaterialPropertyBlock[] Blocks = Array.Empty<MaterialPropertyBlock>();
        }
        private readonly System.Collections.Generic.List<SeatSurface> seatSurfaces = new System.Collections.Generic.List<SeatSurface>();
        private bool SeatMode => seatOverrides != null;
        private SharedWorld? CurrentSeatWorld => MonMPPlugin.Instance?.World ?? seatWorld;

        internal HoldPositionEditor(HeldGripOverrides heldOverrides, VehicleSeatPoseOverrides seats, SharedWorld? world, Gameplay gameplay)
        {
            overrides = heldOverrides; seatOverrides = seats; seatWorld = world; this.gameplay = gameplay;
            editTarget = EditTarget.Item;
            RefreshSeatChoices();
        }

        private void RefreshSeatChoices()
        {
            if (draftChanged) { message = "Save or revert the draft before refreshing seats."; return; }
            var old = selected;
            var entries = VehicleSeatPoseCatalog.Discover(CurrentSeatWorld);
            choices.Clear();
            // One calibration per model/role. Prefer a nearby instance for its current
            // installed parts, seat adjustment and wheel snapshot.
            foreach (var entry in entries.OrderBy(e => gameplay.Player == null ? 0 : Vector3.Distance(e.Anchor.position, gameplay.Player.transform.position))
                .GroupBy(e => (e.VehicleKey, e.SeatKey, e.Shape)).Select(g => g.First()))
                choices.Add(new Choice { Seat = entry, Variant = entry.SeatKey, Entry = new ItemPrefabCatalog.Entry
                {
                    Asset = entry.Vehicle.gameObject, Type = entry.Vehicle.GetType(),
                    Definition = new PrefabDefinition(1, entry.VehicleKey, entry.Vehicle.GetType().FullName!, entry.Shape, (PrefabFeatures)0)
                } });
            choices.Sort((a, b) => StringComparer.Ordinal.Compare(a.Label, b.Label));
            if (Visible)
            {
                var match = old == null ? null : choices.FirstOrDefault(c => c.Entry.Definition.Path == old.Entry.Definition.Path && c.Variant == old.Variant && c.Entry.Definition.ShapeHash == old.Entry.Definition.ShapeHash);
                if (match != null || choices.Count != 0) Select(match ?? choices[0]);
                else { selected = null; draft = initial = null; DestroyItem(); message = "No vehicle seats are available in this scene."; }
            }
        }

        private bool HasSavedPose(string model, string variant, string shape) => SeatMode ? seatOverrides!.HasSaved(model, variant, shape) : overrides.HasSaved(model, variant, shape);
        private bool TryGetPose(string model, string variant, string shape, out HeldGripOverrides.Grip grip)
        {
            if (!SeatMode) return overrides.TryGet(model, variant, shape, out grip);
            if (seatOverrides!.TryGet(model, variant, shape, out var pose)) { grip = pose.ToGrip(); return true; }
            grip = null!; return false;
        }
        private bool CopySavedPose(HeldGripOverrides.Grip source, HeldGripOverrides.Grip destination)
        {
            if (!SeatMode) return overrides.TryCopyPose(source.Prefab, source.Variant, source.Shape, destination);
            if (!seatOverrides!.TryGet(source.Prefab, source.Variant, source.Shape, out var current)) return false;
            var pose = VehicleSeatPoseOverrides.Pose.FromGrip(destination.Prefab, destination.Variant, destination.Shape, destination);
            pose.CopyPoseFrom(current);
            var copy = pose.ToGrip();
            destination.Set(copy.Position, copy.Euler, 0); destination.SetLeft(copy.LeftPosition, copy.LeftEuler); destination.TwoHanded = copy.TwoHanded;
            destination.ItemX = copy.ItemX; destination.ItemY = copy.ItemY; destination.ItemZ = copy.ItemZ;
            destination.ItemPitch = copy.ItemPitch; destination.ItemYaw = copy.ItemYaw; destination.ItemRoll = copy.ItemRoll;
            if (destination is VehicleSeatPoseOverrides.SeatDraft feet) feet.CopyFeetFrom(copy);
            return true;
        }

        private void DrawSeatCatalogActions()
        {
            if (GUILayout.Button("Select current vehicle seat"))
            {
                var world = CurrentSeatWorld;
                var peer = MonMPPlugin.Instance?.Session?.LocalPeerId;
                bool driving = gameplay.Get_PlayerState == PlayerState.InVehicle_Driver;
                Transform? anchor = peer.HasValue ? world?.Vehicles.SeatFor(peer.Value) ?? world?.SchoolBusPassengers.SeatFor(peer.Value) ?? world?.MartinCar?.SeatFor(peer.Value) : null;
                if (anchor == null && peer.HasValue && world?.BoatMotion.TryGetSeatFrame(peer.Value, out var boatSeat) == true) anchor = boatSeat;
                var kind = SharedExtraSeats.SeatKind.Cab;
                if (anchor == null && peer.HasValue) anchor = world?.ExtraSeats.SeatFor(peer.Value, out kind);
                var current = anchor == null ? null : VehicleSeatPoseCatalog.Resolve(world, peer!.Value, anchor, driving, kind);
                var vehicle = gameplay.Get_CurrentVehicleController ?? world?.ExtraSeats.CurrentLocalPassengerVehicle;
                var choice = current == null ? null : choices.FirstOrDefault(c => c.Entry.Definition.Path == current.VehicleKey && c.Variant == current.SeatKey && c.Entry.Definition.ShapeHash == current.Shape);
                choice ??= choices.Where(c => c.Seat!.Vehicle == vehicle && (c.Seat.Mode == StockAvatarBody.Mode.Drive) == driving)
                    .OrderBy(c => Vector3.Distance(c.Seat!.Anchor.position, gameplay.Player.transform.position)).FirstOrDefault();
                if (choice != null && current != null) choice.Seat = current;
                if (choice != null) RequestSelection(choice); else message = "Enter a vehicle seat first, or select one from the list.";
            }
            if (GUILayout.Button("Refresh vehicle seats")) RefreshSeatChoices();
        }

        private void DrawSeatViewControls()
        {
            int view = showSeatVehicle ? seatCutaway ? 0 : 1 : 2;
            int next = GUILayout.Toolbar(view, new[] { "Cutaway vehicle", "Solid vehicle", "Body only" });
            if (next != view) { showSeatVehicle = next != 2; seatCutaway = next == 0; ApplySeatView(); }
            if (showSeatVehicle && seatCutaway)
            {
                float opacity = SeatViewSlider("Vehicle opacity", seatOpacity, 0.08f, 0.65f);
                if (opacity != seatOpacity) { seatOpacity = opacity; ApplySeatView(); }
            }
            if (selected?.Seat?.Wheel != null) seatPreviewAngle = SeatViewSlider("Preview steering", seatPreviewAngle, -100, 100);
        }

        private static float SeatViewSlider(string label, float value, float minimum, float maximum)
        {
            GUILayout.BeginHorizontal(); GUILayout.Label(label, GUILayout.Width(125));
            value = GUILayout.HorizontalSlider(value, minimum, maximum);
            GUILayout.Label(value.ToString("F2"), GUILayout.Width(52)); GUILayout.EndHorizontal(); return value;
        }

        private void SelectSeat()
        {
            ReplaceSeat();
            SeedSeatAutomatic();
            bool calibrated = TryGetPose(selected!.Entry.Definition.Path, selected.Variant, selected.Entry.Definition.ShapeHash, out var saved);
            if (calibrated) draft = saved;
            initial = draft?.Copy(); draftChanged = false;
            framePending = true;
            poseSource = HasSavedPose(selected!.Entry.Definition.Path, selected.Variant, selected.Entry.Definition.ShapeHash) ? "saved seat calibration" : calibrated ? "built-in seat calibration" : "automatic seat presentation";
            message = "Cutaway shows the avatar inside the vehicle. Use Solid vehicle to check normal occlusion; Body only isolates the pose.";
        }

        private void ReplaceSeat()
        {
            var seat = selected?.Seat ?? throw new InvalidOperationException("Seat is unavailable.");
            if (seat.Vehicle == null || seat.Anchor == null) throw new InvalidOperationException("Vehicle was unloaded. Refresh the seat list.");
            DestroyItem();
            itemCopy = new GameObject("MonMP renderer-only vehicle stage"); itemCopy.layer = layer;
            itemCopy.transform.SetParent(previewRoot!.transform, false);
            seatVehicleCopy = new GameObject("MonMP renderer-only vehicle"); seatVehicleCopy.transform.SetParent(itemCopy.transform, false);
            var source = seat.Vehicle.transform;
            var inverse = Quaternion.Inverse(seat.Anchor.rotation);
            seatVehicleCopy.transform.localPosition = inverse * (source.position - seat.Anchor.position);
            seatVehicleCopy.transform.localRotation = inverse * source.rotation;
            seatVehicleCopy.transform.localScale = source.lossyScale;
            HoldPreviewVisualCopy.Copy(source, seatVehicleCopy.transform, layer, ownedMeshes);
            CreateSeatMaterials(); ApplySeatView();
            itemSource = source.gameObject;
            var handFrame = new GameObject("MonMP seat hand frame"); handFrame.layer = layer;
            handFrame.transform.SetParent(itemCopy.transform, false); seatHandFrame = handFrame.transform;
            VehicleSeatPosePresentation.HandFrame(seat, out var hands, out var rotation);
            seatHandPosition = inverse * (hands - seat.Anchor.position); seatHandRotation = inverse * rotation;
            seatCapturedAngle = seat.WheelAngle; seatPreviewAngle = Mathf.Clamp(seatCapturedAngle, -100, 100);
            seatWheelCopy = seat.Wheel == null ? null : FindCopiedTransform(source, seat.Wheel, seatVehicleCopy.transform);
            if (seatWheelCopy != null) seatWheelRotation = seatWheelCopy.rotation;
            UpdateSeatHandFrame();
            picking.Rebuild(itemCopy);
        }

        private void CreateSeatMaterials()
        {
            var shader = Shader.Find("Standard") ?? throw new InvalidOperationException("Vehicle cutaway shader unavailable.");
            foreach (var renderer in seatVehicleCopy!.GetComponentsInChildren<Renderer>(true))
            {
                var surface = new SeatSurface { Renderer = renderer, Solid = renderer.sharedMaterials, Block = new MaterialPropertyBlock() };
                renderer.GetPropertyBlock(surface.Block);
                surface.Blocks = new MaterialPropertyBlock[surface.Solid.Length]; surface.Cutaway = new Material[surface.Solid.Length];
                seatSurfaces.Add(surface);
                for (int i = 0; i < surface.Solid.Length; i++)
                {
                    var source = surface.Solid[i]; var block = new MaterialPropertyBlock(); renderer.GetPropertyBlock(block, i); surface.Blocks[i] = block;
                    var copy = new Material(shader) { name = "MonMP seat cutaway" }; surface.Cutaway[i] = copy;
                    if (source != null) copy.CopyPropertiesFromMaterial(source);
                    Color color = source != null && source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white;
                    // Native paint commonly supplies a tint via property blocks. Clearing
                    // those blocks on the COPY prevents their opaque alpha overriding fade.
                    Color tint = surface.Block.GetColor("_Color"); if (tint.a > 0) color = tint;
                    tint = block.GetColor("_Color"); if (tint.a > 0) color = tint;
                    color.a = seatOpacity; copy.SetColor("_Color", color);
                    copy.SetFloat("_Mode", 2); copy.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    copy.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha); copy.SetInt("_ZWrite", 0);
                    copy.DisableKeyword("_ALPHATEST_ON"); copy.DisableKeyword("_ALPHAPREMULTIPLY_ON"); copy.EnableKeyword("_ALPHABLEND_ON");
                    copy.renderQueue = 3000;
                }
            }
        }

        private void ApplySeatView()
        {
            if (seatVehicleCopy == null) return;
            seatVehicleCopy.SetActive(showSeatVehicle);
            foreach (var surface in seatSurfaces)
            {
                if (surface.Renderer == null) continue;
                foreach (var material in surface.Cutaway) { if (material == null) continue; var color = material.color; color.a = seatOpacity; material.color = color; }
                surface.Renderer.sharedMaterials = seatCutaway ? surface.Cutaway : surface.Solid;
                surface.Renderer.SetPropertyBlock(seatCutaway ? null : surface.Block);
                for (int i = 0; i < surface.Blocks.Length; i++) surface.Renderer.SetPropertyBlock(seatCutaway ? null : surface.Blocks[i], i);
            }
        }

        private void DestroySeatMaterials()
        {
            foreach (var surface in seatSurfaces) foreach (var material in surface.Cutaway) if (material != null) UnityEngine.Object.Destroy(material);
            seatSurfaces.Clear();
        }

        private static Transform? FindCopiedTransform(Transform sourceRoot, Transform source, Transform copyRoot)
        {
            var indices = new System.Collections.Generic.Stack<int>();
            for (var current = source; current != sourceRoot; current = current.parent)
            {
                if (current == null) return null;
                indices.Push(current.GetSiblingIndex());
            }
            var result = copyRoot;
            while (indices.Count > 0) { int index = indices.Pop(); if (index >= result.childCount) return null; result = result.GetChild(index); }
            return result;
        }

        private void UpdateSeatHandFrame()
        {
            if (seatHandFrame == null) return;
            var axis = seatHandRotation * Vector3.forward;
            seatHandFrame.localPosition = seatHandPosition;
            seatHandFrame.localRotation = Quaternion.AngleAxis(seatPreviewAngle - Mathf.Clamp(seatCapturedAngle, -100, 100), axis) * seatHandRotation;
            if (seatWheelCopy != null) seatWheelCopy.rotation = Quaternion.AngleAxis(seatPreviewAngle - seatCapturedAngle, axis) * seatWheelRotation;
        }

        private void RenderSeatPose()
        {
            var seat = selected!.Seat!;
            UpdateSeatHandFrame();
            seatBodyRoot!.transform.SetPositionAndRotation(previewRoot!.transform.position + draft!.ItemPosition, Quaternion.Euler(draft.ItemEuler));
            body!.Present(seat.Mode, 0, 0, 0, StockAvatarBody.Overlay.Look(pitch), 1f);
            if (seat.AlignHips) body.AlignHips(seatBodyRoot.transform.position);
            else body.AlignHead(seatBodyRoot.transform.position + seatBodyRoot.transform.up * 1.52f);
            var goals = new StockAvatarBody.Goals();
            if (seat.Mode == StockAvatarBody.Mode.Drive && seat.Wheel != null && !draft.TwoHanded)
            {
                // Capture rim radius from the native wheel, then rotate its automatic
                // targets with the same orthonormal, bounded frame as the saved targets.
                var grips = VehicleSeatPosePresentation.AutomaticWheelGrips(seat.Wheel, seatCapturedAngle, seat.VehicleRight);
                float radius = Vector3.Distance(grips.Left, grips.Right) * 0.5f;
                goals.GripLeft = seatHandFrame!.position - seatHandFrame.right * radius;
                goals.GripRight = seatHandFrame.position + seatHandFrame.right * radius; goals.GripCenter = seatHandFrame.position;
            }
            if (draft.TwoHanded || (draft as VehicleSeatPoseOverrides.SeatDraft)?.CustomFeet == true)
            {
                var pose = VehicleSeatPoseOverrides.Pose.FromGrip(draft.Prefab, draft.Variant, draft.Shape, draft);
                if (draft.TwoHanded) VehicleSeatPosePresentation.ApplyHands(pose, seatHandFrame!.position, seatHandFrame.rotation, ref goals, seat.Mode == StockAvatarBody.Mode.Drive);
                VehicleSeatPosePresentation.ApplyFeet(pose, itemCopy!.transform.position, itemCopy.transform.rotation, ref goals);
            }
            body.Solve(goals, 1f);
            marker!.SetActive(showMarkers); marker.transform.position = draft.TwoHanded ? seatHandFrame!.TransformPoint(draft.Position) : body.RightPalm;
            leftMarker!.SetActive(showMarkers); leftMarker.transform.position = draft.TwoHanded ? seatHandFrame!.TransformPoint(draft.LeftPosition) : body.LeftPalm;
            var feet = draft as VehicleSeatPoseOverrides.SeatDraft;
            rightFootMarker!.SetActive(showMarkers); rightFootMarker.transform.position = feet?.CustomFeet == true ? itemCopy!.transform.TransformPoint(feet.RightFootPosition) : body.RightFoot.position;
            leftFootMarker!.SetActive(showMarkers); leftFootMarker.transform.position = feet?.CustomFeet == true ? itemCopy!.transform.TransformPoint(feet.LeftFootPosition) : body.LeftFoot.position;
            rightFootError = Vector3.Distance(rightFootMarker.transform.position, body.RightFoot.position);
            leftFootError = Vector3.Distance(leftFootMarker.transform.position, body.LeftFoot.position);
        }

        private void SeedSeatAutomatic()
        {
            if (selected == null || body == null || seatHandFrame == null) return;
            var before = draft;
            var seatDraft = new VehicleSeatPoseOverrides.SeatDraft { Prefab = selected.Entry.Definition.Path, Variant = selected.Variant, Shape = selected.Entry.Definition.ShapeHash };
            draft = seatDraft;
            if (before != null)
            {
                draft.ItemX = before.ItemX; draft.ItemY = before.ItemY; draft.ItemZ = before.ItemZ;
                draft.ItemPitch = before.ItemPitch; draft.ItemYaw = before.ItemYaw; draft.ItemRoll = before.ItemRoll;
                if (before is VehicleSeatPoseOverrides.SeatDraft previousFeet) seatDraft.CopyFeetFrom(previousFeet);
            }
            RenderSeatPose();
            draft.Set(seatHandFrame.InverseTransformPoint(body.RightPalm), Signed((Quaternion.Inverse(seatHandFrame.rotation) * body.RightHand.rotation).eulerAngles), 0);
            draft.SetLeft(seatHandFrame.InverseTransformPoint(body.LeftPalm), Signed((Quaternion.Inverse(seatHandFrame.rotation) * body.LeftHand.rotation).eulerAngles));
            // A fresh automatic draft should capture the CURRENT solved palms when
            // custom hands are first enabled, including any body edits made meanwhile.
            draft.LeftInitialized = false;
            if (before == null) { CaptureSeatFeet(seatDraft); seatDraft.FeetInitialized = false; }
            poseSource = "automatic seat presentation; custom hands off";
            numericText.Clear(); editRangeReady = false;
        }

        private void SetSeatHands(bool enabled)
        {
            if (draft == null || body == null || seatHandFrame == null) return;
            if (enabled && !draft.LeftInitialized)
            {
                RenderSeatPose();
                draft.Set(seatHandFrame.InverseTransformPoint(body.RightPalm), Signed((Quaternion.Inverse(seatHandFrame.rotation) * body.RightHand.rotation).eulerAngles), 0);
                draft.SetLeft(seatHandFrame.InverseTransformPoint(body.LeftPalm), Signed((Quaternion.Inverse(seatHandFrame.rotation) * body.LeftHand.rotation).eulerAngles));
            }
            draft.TwoHanded = enabled; draft.Width = 0;
            numericText.Clear(); pickArmed = false; editRangeReady = false;
        }

        private void CreateSeatFootMarkers()
        {
            GameObject Create(string name, Color color, out Material material)
            {
                var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere); marker.name = name; marker.layer = layer;
                marker.transform.SetParent(previewRoot!.transform, false); marker.transform.localScale = Vector3.one * 0.025f;
                UnityEngine.Object.DestroyImmediate(marker.GetComponent<Collider>());
                material = new Material(markerMaterial!) { color = color }; marker.GetComponent<Renderer>().sharedMaterial = material;
                return marker;
            }
            rightFootMarker = Create("MonMP right foot target", new Color(1, 0.8f, 0.1f), out var right); rightFootMaterial = right;
            leftFootMarker = Create("MonMP left foot target", new Color(1, 0.3f, 0.65f), out var left); leftFootMaterial = left;
        }

        private void DestroySeatFootMarkers()
        {
            if (rightFootMaterial != null) UnityEngine.Object.Destroy(rightFootMaterial); rightFootMaterial = null;
            if (leftFootMaterial != null) UnityEngine.Object.Destroy(leftFootMaterial); leftFootMaterial = null;
            rightFootMarker = leftFootMarker = null;
        }

        private void CaptureSeatFeet(VehicleSeatPoseOverrides.SeatDraft feet)
        {
            if (body == null || itemCopy == null) return;
            var frame = itemCopy.transform;
            feet.SetRightFoot(frame.InverseTransformPoint(body.RightFoot.position), Signed((Quaternion.Inverse(frame.rotation) * body.RightFoot.rotation).eulerAngles));
            feet.SetLeftFoot(frame.InverseTransformPoint(body.LeftFoot.position), Signed((Quaternion.Inverse(frame.rotation) * body.LeftFoot.rotation).eulerAngles));
        }

        private void SetSeatFeet(bool enabled)
        {
            if (!(draft is VehicleSeatPoseOverrides.SeatDraft feet)) return;
            if (enabled && !feet.FeetInitialized) { RenderSeatPose(); CaptureSeatFeet(feet); }
            feet.CustomFeet = enabled;
            numericText.Clear(); pickArmed = false; editRangeReady = false;
        }

        private void SeedSeatFeet()
        {
            if (!(draft is VehicleSeatPoseOverrides.SeatDraft feet)) return;
            feet.CustomFeet = false; RenderSeatPose(); CaptureSeatFeet(feet); feet.FeetInitialized = false;
            numericText.Clear(); pickArmed = false; editRangeReady = false;
            poseSource = "automatic feet; body and palms retained";
        }

        private void SaveSeat()
        {
            try
            {
                seatOverrides!.Save(VehicleSeatPoseOverrides.Pose.FromGrip(draft!.Prefab, draft.Variant, draft.Shape, draft));
                initial = draft.Copy(); draftChanged = false; message = "Saved pose for this vehicle model and seat. Remote avatars and self-view use it immediately.";
            }
            catch (Exception ex) { message = "Save failed; active settings unchanged: " + ex.Message; }
        }

        private void ResetSeat()
        {
            try
            {
                seatOverrides!.Remove(selected!.Entry.Definition.Path, selected.Variant);
                bool bundled = TryGetPose(selected.Entry.Definition.Path, selected.Variant, selected.Entry.Definition.ShapeHash, out var restored);
                draft = bundled ? restored : null;
                if (!bundled) SeedSeatAutomatic();
                poseSource = bundled ? "built-in seat calibration" : "automatic seat presentation";
                initial = draft?.Copy(); draftChanged = false;
                undo.Clear(); redo.Clear(); numericText.Clear(); editRangeReady = false; historyAction = true; pickArmed = false;
                message = bundled ? "Local override removed; built-in seat calibration restored." : "Saved seat override removed; automatic presentation restored.";
            }
            catch (Exception ex) { message = "Reset failed: " + ex.Message; }
        }

        // Unity refreshes skinned mesh buffers once per frame. Let the real preview
        // survive several frames on each pose so this checks rendered boots as well as bones.
        internal string? TestSeatFootLiveStep(string directory)
        {
            try
            {
                if (footLiveFrames == 0)
                {
                    footLiveDirectory = Path.Combine(directory, "seat-foot-live-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(footLiveDirectory);
                    showMarkers = false;
                    CreateStage();
                    Select(choices.FirstOrDefault(c => c.Seat?.Mode == StockAvatarBody.Mode.Drive && c.Label.Contains("Konig")) ?? choices.First());
                    orbit = 90; elevation = 12; framePending = true;
                }
                RenderPreview();
                if (camera == null || body == null || texture == null || itemCopy == null) throw new InvalidOperationException("Live foot preview failed: " + message);
                footLiveFrames++;
                if (footLiveFrames == 4)
                {
                    footLiveBefore = ReadFootLivePixels();
                    footLiveRight = ViewportPoint(camera, new Rect(0, 0, texture.width, texture.height), body.RightFoot.position);
                    footLiveLeft = ViewportPoint(camera, new Rect(0, 0, texture.width, texture.height), body.LeftFoot.position);
                    AvatarPreviewCapture.SaveView(camera, Path.Combine(footLiveDirectory, "before.bmp"));
                    SetSeatFeet(true);
                    var feet = (VehicleSeatPoseOverrides.SeatDraft)draft!;
                    feet.SetRightFoot(feet.RightFootPosition + Vector3.up * 0.2f + Vector3.forward * 0.1f, feet.RightFootEuler + Vector3.right * 20);
                    feet.SetLeftFoot(feet.LeftFootPosition + Vector3.up * 0.12f, feet.LeftFootEuler - Vector3.right * 10);
                }
                if (footLiveFrames < 9) return null;
                var after = ReadFootLivePixels();
                Vector2 right = ViewportPoint(camera, new Rect(0, 0, texture.width, texture.height), body.RightFoot.position);
                Vector2 left = ViewportPoint(camera, new Rect(0, 0, texture.width, texture.height), body.LeftFoot.position);
                int changed = 0;
                float minX = Mathf.Min(footLiveRight.x, footLiveLeft.x, right.x, left.x) - 45;
                float maxX = Mathf.Max(footLiveRight.x, footLiveLeft.x, right.x, left.x) + 45;
                float minY = Mathf.Min(footLiveRight.y, footLiveLeft.y, right.y, left.y) - 45;
                float maxY = Mathf.Max(footLiveRight.y, footLiveLeft.y, right.y, left.y) + 45;
                for (int y = 0; y < texture.height; y++) for (int x = 0; x < texture.width; x++)
                {
                    float guiY = texture.height - 1 - y;
                    if (x < minX || x > maxX || guiY < minY || guiY > maxY) continue;
                    int index = y * texture.width + x; var a = footLiveBefore![index]; var b = after[index];
                    if (Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b) > 30) changed++;
                }
                AvatarPreviewCapture.SaveView(camera, Path.Combine(footLiveDirectory, "after.bmp"));
                if (changed < 40) throw new InvalidOperationException("Boot pixels did not follow the foot edits: " + changed);
                if (rightFootError > 0.015f || leftFootError > 0.015f) throw new InvalidOperationException("Live foot target unreachable.");
                string report = "MONMP_SEAT_FOOT_LIVE_PASS changedBootPixels=" + changed + " rightError=" + rightFootError + " leftError=" + leftFootError + " artifacts=" + footLiveDirectory;
                File.WriteAllText(Path.Combine(footLiveDirectory, "report.txt"), report); return report;
            }
            catch (Exception ex)
            {
                string report = "MONMP_SEAT_FOOT_LIVE_FAIL " + ex;
                if (footLiveDirectory.Length != 0) File.WriteAllText(Path.Combine(footLiveDirectory, "report.txt"), report);
                return report;
            }
        }

        private Color32[] ReadFootLivePixels()
        {
            var previous = RenderTexture.active;
            var image = new Texture2D(texture!.width, texture.height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = texture;
                image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); image.Apply(); return image.GetPixels32();
            }
            finally { RenderTexture.active = previous; UnityEngine.Object.Destroy(image); }
        }

        internal string TestSeatSmoke(string directory)
        {
            if (!SeatMode || Visible) return "MONMP_SEAT_EDITOR_SMOKE_SKIPPED requires a separate seat editor";
            string owned = Path.Combine(directory, "seat-editor-smoke-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(owned);
            var report = new StringBuilder();
            try
            {
                if (choices.Count == 0) throw new InvalidOperationException("No native vehicle seats discovered.");
                CreateStage();
                foreach (var choice in choices.Take(12))
                {
                    var source = choice.Seat!; var position = source.Anchor.position; var rotation = source.Anchor.rotation; var scale = source.Vehicle.transform.lossyScale;
                    var sourceRenderers = source.Vehicle.GetComponentsInChildren<Renderer>(true);
                    var sourceMaterials = sourceRenderers.Select(r => r.sharedMaterials).ToArray();
                    var sourceColors = sourceMaterials.Select(m => m.Select(v => v != null && v.HasProperty("_Color") ? v.GetColor("_Color") : Color.clear).ToArray()).ToArray();
                    Select(choice); RenderPreview();
                    foreach (var component in itemCopy!.GetComponentsInChildren<Component>(true))
                        if (!(component is Transform || component is MeshFilter || component is MeshRenderer)) throw new InvalidOperationException("Unsafe preview component " + component.GetType().Name);
                    if (draft == null || body == null || camera == null) throw new InvalidOperationException("Seat preview failed: " + message);
                    draft.ItemX = 0.05f; draft.ItemYaw = 10; SetSeatHands(true); RenderPreview();
                    if (seatBodyRoot == null || body == null || camera == null) throw new InvalidOperationException("Calibrated preview failed: " + message);
                    SetSeatFeet(true);
                    var feet = (VehicleSeatPoseOverrides.SeatDraft)draft;
                    feet.SetRightFoot(feet.RightFootPosition + Vector3.up * 0.08f, feet.RightFootEuler + Vector3.right * 10);
                    feet.SetLeftFoot(feet.LeftFootPosition + Vector3.up * 0.06f, feet.LeftFootEuler - Vector3.right * 5);
                    RenderPreview();
                    if (body == null || camera == null) throw new InvalidOperationException("Foot preview failed: " + message);
                    if (rightFootError > 0.015f || leftFootError > 0.015f) throw new InvalidOperationException("Seeded feet failed reachable IK: right=" + rightFootError + " left=" + leftFootError);
                    float rightTurnError = Quaternion.Angle(body.RightFoot.rotation, itemCopy.transform.rotation * Quaternion.Euler(feet.RightFootEuler));
                    float leftTurnError = Quaternion.Angle(body.LeftFoot.rotation, itemCopy.transform.rotation * Quaternion.Euler(feet.LeftFootEuler));
                    if (rightTurnError > 0.1f || leftTurnError > 0.1f) throw new InvalidOperationException("Foot rotations did not apply.");
                    if (Vector3.Distance(seatBodyRoot!.transform.position, previewRoot!.transform.position + Vector3.right * 0.05f) > 0.002f) throw new InvalidOperationException("Body frame did not apply offset.");
                    if (Vector3.Distance(source.Anchor.position, position) > 0.0001f || Quaternion.Angle(source.Anchor.rotation, rotation) > 0.001f || source.Vehicle.transform.lossyScale != scale) throw new InvalidOperationException("Native seat was changed.");
                    for (int renderer = 0; renderer < sourceRenderers.Length; renderer++)
                    {
                        if (!sourceRenderers[renderer].sharedMaterials.SequenceEqual(sourceMaterials[renderer])) throw new InvalidOperationException("Native materials were replaced.");
                        for (int material = 0; material < sourceMaterials[renderer].Length; material++)
                        {
                            var value = sourceMaterials[renderer][material];
                            if (value != null && value.HasProperty("_Color") && value.GetColor("_Color") != sourceColors[renderer][material]) throw new InvalidOperationException("Native material tint was changed.");
                        }
                    }
                    string image = Path.Combine(owned, "preview-" + report.Length + ".bmp"); AvatarPreviewCapture.SaveView(camera, image);
                    report.AppendLine(choice.Label + " | renderer-only | native transforms/materials unchanged | cutaway vehicle visible | " + image);
                    report.AppendLine("FRAME seat=" + source.Anchor.position + " head=" + body.Head.position + " hips=" + body.HipsPosition + " handFrame=" + seatHandFrame!.position + " cameraAbove=" + (camera.transform.position.y - cameraPivot.y).ToString("F3"));
                    report.AppendLine("PALMS rightError=" + Vector3.Distance(body.RightPalm, seatHandFrame.TransformPoint(draft.Position)).ToString("F4") + " leftError=" + Vector3.Distance(body.LeftPalm, seatHandFrame.TransformPoint(draft.LeftPosition)).ToString("F4"));
                    report.AppendLine("FEET rightError=" + rightFootError.ToString("F4") + " leftError=" + leftFootError.ToString("F4") + " rightTurnError=" + rightTurnError.ToString("F3") + " leftTurnError=" + leftTurnError.ToString("F3"));
                    if (camera.transform.position.y <= cameraPivot.y) throw new InvalidOperationException("Default preview camera is below its focus.");
                }
                report.Insert(0, "MONMP_SEAT_EDITOR_SMOKE_PASS\n");
            }
            catch (Exception ex) { report.Insert(0, "MONMP_SEAT_EDITOR_SMOKE_FAIL " + ex + "\n"); }
            finally { DestroyStage(); }
            File.WriteAllText(Path.Combine(owned, "report.txt"), report.ToString()); return report.ToString();
        }
    }
}
