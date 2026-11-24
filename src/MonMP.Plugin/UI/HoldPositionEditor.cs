using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Globalization;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using MonMP.Plugin.Replication;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MonMP.Plugin.UI
{
    // Pure presentation preview. Item copies contain only transforms, meshes and renderers;
    // constructing them cannot register a native item or run an item Awake/Start/Use method.
    internal sealed partial class HoldPositionEditor : IDisposable
    {
        private sealed class Choice
        {
            internal ItemPrefabCatalog.Entry Entry = null!;
            internal bool SceneEquipment;
            internal bool HandMap, HandFlashlight;
            internal bool HandProp => HandMap || HandFlashlight;
            internal VehicleSeatPoseCatalog.Entry? Seat;
            internal string Variant = "default";
            internal string Label => Seat?.Label ?? (HandMap ? "Map [hand prop]" : HandFlashlight ? "Flashlight [hand prop]" : SceneEquipment ? Entry.Type.Name + " [scene equipment " + Entry.Definition.ShapeHash.Substring(0, 8) + "]" : Entry.Definition.Path) + (Variant == "default" ? "" : " [second spanner]");
        }
        private readonly HeldGripOverrides overrides;
        private readonly Gameplay gameplay;
        private readonly List<Choice> choices = new List<Choice>();
        private readonly List<Mesh> ownedMeshes = new List<Mesh>();
        private Choice? selected;
        private HeldGripOverrides.Grip? draft, initial;
        private GameObject? previewRoot, itemCopy, cameraObject, lightObject, marker, leftMarker;
        private GameObject? itemSource, retainedSource;
        private bool retainedEquippedPreview;
        private StockAvatarBody? body;
        private RemoteHandProps.Prop? handPropPreview;
        private Camera? camera;
        private RenderTexture? texture;
        private Material? markerMaterial, leftMarkerMaterial;
        private readonly HoldPreviewPicking picking = new HoldPreviewPicking();
        private readonly List<HeldGripOverrides.Grip> undo = new List<HeldGripOverrides.Grip>(), redo = new List<HeldGripOverrides.Grip>();
        private readonly Dictionary<string, string> numericText = new Dictionary<string, string>();
        private Choice? pendingSelection;
        private enum EditTarget { Item, Right, Left, RightFoot, LeftFoot }
        private EditTarget editTarget = EditTarget.Right;
        private bool rotateEdit, pickArmed, framePending = true, showMarkers = true;
        private int editAxis, viewportControl, scrubControl, editorHotControl, activeDragButton = -1, dragMouseButton = -1;
        private Vector3 cameraPivot;
        private Bounds cameraBounds;
        private float cameraFit = 1;
        private HeldGripOverrides.Grip? gestureStart;
        private float moveRange = 0.1f, turnRange = 45;
        private bool snapEdits, editRangeReady, copyChooser;
        private Vector3 editRangePosition, editRangeEuler, dragWorldAxis, dragStartPosition, dragStartEuler;
        private Vector2 dragPointerStart, dragRingCenter, dragRingU, dragRingV;
        private Vector2 dragProjectedAxis;
        private Vector3 dragMoveTotal;
        private float dragUnits;
        private Quaternion dragRotationFrame;
        private float dragHandleLength, dragRingLastAngle, dragTurnTotal;
        private int dragAxis;
        private float scrubStartValue, scrubPixels;
        private bool sliderDragging;
        private readonly List<HeldGripOverrides.Grip> copySources = new List<HeldGripOverrides.Grip>();
        private Vector2 copyScroll;
        private string copyFilter = "";
        private float nextRepeat;
        private int repeatControl;
        private Vector2 catalogScroll, controlsScroll;
        private string filter = "", message = "", poseSource = "";
        private float orbit = 20, elevation = 12, distance = 1.1f, pitch, crouch;
        private float positionStep = 0.01f, angleStep = 5;
        private Vector3 itemViewPosition;
        private Quaternion itemViewRotation = Quaternion.identity;
        private int layer;
        private bool draftChanged, restoreCursor, priorCursorVisible;
        private CursorLockMode priorCursorLock;
        internal bool Visible { get; private set; }

        internal HoldPositionEditor(HeldGripOverrides overrides, ItemPrefabCatalog catalog, Gameplay gameplay)
        {
            this.overrides = overrides; this.gameplay = gameplay;
            // Native EquipTool receives InteractableTools. Availability getters may depend on
            // live fuel/pressure/unlocks, so they must not run against resource prefabs here.
            foreach (var entry in catalog.Entries.Values.Where(e => typeof(InteractableTools).IsAssignableFrom(e.Type)).OrderBy(e => e.Definition.Path, StringComparer.Ordinal))
            {
                // This selector is the branch in native PlayerInteractable.EquipTool. Other
                // per-instance save values do not select another native equip anchor. The huge
                // spanner's flag is serialized on its own resource prefab, not a switchable state.
                choices.Add(new Choice { Entry = entry, Variant = HeldGripOverrides.VariantOf(entry.Asset.GetComponent<InteractableItems>()) });
            }
            var canonicalPaths = new HashSet<string>(catalog.Entries.Values.Select(e => e.Definition.Path), StringComparer.Ordinal);
            var sceneKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in UnityEngine.Object.FindObjectsOfType<InteractableTools>(true))
            {
                if (tool == null || !tool.gameObject.scene.IsValid() || !tool.gameObject.scene.isLoaded || canonicalPaths.Contains(tool.PrefabName)) continue;
                HeldGripOverrides.RegisterSceneEquipment(tool, out string key, out string shape);
                if (!sceneKeys.Add(key)) continue;
                choices.Add(new Choice { SceneEquipment = true, Variant = HeldGripOverrides.VariantOf(tool), Entry = new ItemPrefabCatalog.Entry
                { Asset = tool.gameObject, Type = tool.GetType(), Definition = new PrefabDefinition(1, key, tool.GetType().FullName!, shape, PrefabFeatures.Carry) } });
            }
            var mapSource = RemoteHandProps.MapSource(gameplay);
            if (mapSource != null)
                choices.Add(new Choice { HandMap = true, Entry = new ItemPrefabCatalog.Entry
                { Asset = mapSource, Type = typeof(PlayerVisualHands), Definition = new PrefabDefinition(1, RemoteMapCalibration.Key,
                    typeof(PlayerVisualHands).FullName!, RemoteMapCalibration.ShapeOf(mapSource), PrefabFeatures.Carry) } });
            var flashlightSource = RemoteHandProps.FlashlightSource(gameplay);
            if (flashlightSource != null)
                choices.Add(new Choice { HandFlashlight = true, Entry = new ItemPrefabCatalog.Entry
                { Asset = flashlightSource, Type = typeof(PlayerVisualHands), Definition = new PrefabDefinition(1, RemoteFlashlightCalibration.Key,
                    typeof(PlayerVisualHands).FullName!, RemoteMapCalibration.ShapeOf(flashlightSource), PrefabFeatures.Carry) } });
            choices.Sort((a, b) => StringComparer.Ordinal.Compare(a.Label, b.Label));
        }
        internal void Show()
        {
            if (Visible) return;
            if (gameplay == null || gameplay.Player == null) { message = "Load a gameplay scene first."; return; }
            priorCursorLock = Cursor.lockState; priorCursorVisible = Cursor.visible; restoreCursor = true;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true; Visible = true;
            try
            {
                CreateStage();
                if (selected == null && choices.Count > 0) Select(choices[0]);
                else if (selected != null && draft != null)
                {
                    if (SeatMode) ReplaceSeat();
                    else ReplaceItem(retainedSource != null ? retainedSource : selected.Entry.Asset, retainedSource != null ? retainedEquippedPreview : true);
                    if (poseSource.StartsWith("captured", StringComparison.Ordinal)) poseSource = "captured held pose; visible source refreshed on reopen";
                    framePending = true;
                }
                else if (selected != null) Select(selected);
            }
            catch (Exception ex) { message = "Preview unavailable: " + ex.Message; DestroyStage(); }
        }
        internal void Hide()
        {
            EndGesture(); pickArmed = false; pendingSelection = null;
            if (editorHotControl != 0 && GUIUtility.hotControl == editorHotControl) GUIUtility.hotControl = 0;
            editorHotControl = 0; GUIUtility.keyboardControl = 0;
            Visible = false; DestroyStage();
            if (restoreCursor) { Cursor.lockState = priorCursorLock; Cursor.visible = priorCursorVisible; restoreCursor = false; }
        }
        internal void Tick()
        {
            if (!Visible) return;
            if (gameplay == null || gameplay.Player == null) { Hide(); return; }
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            if (Keyboard.current?.escapeKey.wasPressedThisFrame == true)
            { if (pickArmed) pickArmed = false; else Hide(); return; }
            RenderPreview();
        }
        private void RenderPreview()
        {
            if (previewRoot == null || body == null || camera == null || itemCopy == null || draft == null) return;
            try
            {
                if (SeatMode) RenderSeatPose();
                else
                {
                var look = StockAvatarBody.Overlay.Look(pitch);
                if (selected?.HandMap == true) look.Map = 1;
                if (selected?.HandFlashlight == true) look.Flashlight = 1;
                body.Present(StockAvatarBody.Mode.Stand, 0, 0, crouch, look, 1f);
                PlaceViewPose();
                var gripPosition = itemCopy.transform.TransformPoint(draft.Position);
                if (selected?.HandFlashlight == true && handPropPreview != null)
                {
                    body.Solve(new StockAvatarBody.Goals { FlashlightProp = handPropPreview, FlashlightCalibration = draft, Feet = false }, 1);
                    gripPosition = itemCopy.transform.TransformPoint(draft.Position);
                }
                else body.Solve(new StockAvatarBody.Goals { Held = gripPosition, HeldWidth = draft.TwoHanded ? 0 : draft.Width,
                    HeldRotation = itemCopy.transform.rotation * Quaternion.Euler(draft.Euler),
                    HeldLeft = draft.TwoHanded ? itemCopy.transform.TransformPoint(draft.LeftPosition) : (Vector3?)null,
                    HeldLeftRotation = draft.TwoHanded ? itemCopy.transform.rotation * Quaternion.Euler(draft.LeftEuler) : (Quaternion?)null, Feet = false }, 1f);
                if (marker != null) { marker.SetActive(showMarkers); marker.transform.position = draft.TwoHanded || draft.Width <= 0.01f ? gripPosition : body.RightPalm; }
                if (leftMarker != null)
                { leftMarker.SetActive(showMarkers && (draft.TwoHanded || draft.Width > 0.01f)); leftMarker.transform.position = draft.TwoHanded ? itemCopy.transform.TransformPoint(draft.LeftPosition) : body.LeftPalm; }
                }
                if (framePending) FramePreview(SeatMode ? 3 : 0);
                var direction = Quaternion.Euler(-elevation, orbit, 0) * Vector3.forward;
                camera.transform.position = cameraPivot + direction * cameraFit * distance;
                camera.transform.rotation = Quaternion.LookRotation(-direction, Vector3.up);
                camera.farClipPlane = Mathf.Max(30, cameraFit * distance + cameraBounds.size.magnitude * 4);
                camera.Render();
            }
            catch (Exception ex) { message = "Preview stopped: " + ex.Message; DestroyStage(); }
        }
        internal void Draw()
        {
            if (!Visible) return;
            Matrix4x4 oldMatrix = GUI.matrix;
            bool oldWrap = GUI.skin.label.wordWrap;
            GUI.skin.label.wordWrap = true;
            float scale = Mathf.Min(1f, Mathf.Min(Screen.width / 1180f, Screen.height / 800f));
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * scale);
            var before = draft?.Copy();
            try
            {
                var panel = new Rect((Screen.width / scale - 1160) * 0.5f, (Screen.height / scale - 780) * 0.5f, 1160, 780);
                GUI.Box(panel, SeatMode ? "Vehicle seat calibration — remote avatar preview" : "Held-item calibration — remote avatar preview");
                GUILayout.BeginArea(new Rect(panel.x + 12, panel.y + 28, panel.width - 24, panel.height - 40));
                GUILayout.BeginHorizontal();
                DrawCatalog();
                GUILayout.BeginVertical(GUILayout.Width(480));
                Rect viewport = GUILayoutUtility.GetRect(480, 480, GUILayout.Width(480), GUILayout.Height(480));
                GUI.Box(viewport, GUIContent.none);
                if (texture != null) GUI.DrawTexture(viewport, texture, ScaleMode.StretchToFill);
                else GUI.Label(viewport, "Remote body becomes available after the native NPC art loads.");
                DrawViewport(viewport);
                GUILayout.Label(pickArmed ? IsFootTarget ? "Click a vehicle surface for the selected foot / ankle. Esc cancels." : "Click a surface for the selected palm. Esc cancels." :
                    (rotateEdit ? "Drag a colored rotation ring. " : "Drag an arrow; center square moves on the view plane. ") +
                    "Right: orbit • Middle / Alt-left: pan • Wheel: zoom • Shift: fine");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Fit all")) FramePreview(0);
                if (GUILayout.Button(SeatMode ? "Focus vehicle" : "Focus tool")) FramePreview(1);
                if (GUILayout.Button(SeatMode && IsFootTarget ? "Focus feet" : "Focus hands")) FramePreview(SeatMode && IsFootTarget ? 4 : 2);
                if (SeatMode && GUILayout.Button("Focus seat")) FramePreview(3);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Front")) SetCameraView(180, 0);
                if (GUILayout.Button("Side")) SetCameraView(90, 0);
                if (GUILayout.Button("Top")) SetCameraView(180, 80);
                if (GUILayout.Button("Reset view")) { orbit = 20; elevation = 12; FramePreview(0); }
                GUILayout.EndHorizontal();
                if (SeatMode)
                {
                    distance = SeatViewSlider("Zoom", distance, 0.03f, 4);
                    pitch = SeatViewSlider("Look pitch", pitch, -60, 60);
                    DrawSeatViewControls();
                }
                else
                {
                    distance = Slider("Zoom", distance, 0.03f, 4);
                    pitch = Slider("Look pitch", pitch, -60, 60);
                    crouch = Slider("Crouch", crouch, 0, 1);
                }
                showMarkers = GUILayout.Toggle(showMarkers, SeatMode ? "Show palm / foot targets and editing axes" : "Show palms and editing axes (right green / left cyan)");
                GUILayout.Label("Pose: " + poseSource);
                GUILayout.EndVertical();
                GUILayout.BeginVertical(GUILayout.Width(340));
                controlsScroll = GUILayout.BeginScrollView(controlsScroll, GUILayout.Height(578));
                if (selected != null && draft != null) DrawEditControls();
                GUILayout.Label(message);
                GUILayout.EndScrollView();
                DrawHistory();
                if (GUILayout.Button((SeatMode ? "SAVE THIS SEAT" : "SAVE THIS TOOL") + (draftChanged ? " • unsaved edits" : ""), GUILayout.Height(34))) Save();
                if (GUILayout.Button("Close (Esc) • keep draft", GUILayout.Height(28))) Hide();
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                GUILayout.EndArea();
            }
            finally
            {
                // Numeric/button changes form one undo entry. Preview drags are grouped at mouse-up.
                if (before != null && draft != null && !SameGrip(before, draft) && gestureStart == null && !historyAction)
                    RecordEdit(before);
                UpdateDirty();
                editorHotControl = GUIUtility.hotControl;
                historyAction = false;
                GUI.matrix = oldMatrix; GUI.skin.label.wordWrap = oldWrap;
            }
        }
        private bool historyAction;
        private void DrawCatalog()
        {
            var style = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, wordWrap = false };
            GUILayout.BeginVertical(GUILayout.Width(292));
            GUILayout.Label(SeatMode ? "Vehicle seats: " + choices.Count + "; saved: " + seatOverrides!.Count : "Held items / tools: " + choices.Count + "; saved: " + overrides.Count + "; built-in: " + HeldGripOverrides.DefaultCount);
            filter = GUILayout.TextField(filter, GUILayout.Height(25));
            var filtered = choices.Where(c => c.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || c.Entry.Type.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Previous")) Navigate(filtered, -1);
            if (GUILayout.Button("Next")) Navigate(filtered, 1);
            GUILayout.EndHorizontal();
            if (SeatMode) DrawSeatCatalogActions();
            else if (GUILayout.Button("Select equipped tool"))
            {
                var held = gameplay.Player.Get_ActiveTool;
                var choice = held == null ? null : choices.FirstOrDefault(c => c.Entry.Definition.Path == HeldGripOverrides.IdentityOf(held) && c.Variant == HeldGripOverrides.VariantOf(held));
                if (choice != null) RequestSelection(choice); else message = "Equip a tool from this catalog first.";
            }
            catalogScroll = GUILayout.BeginScrollView(catalogScroll, GUILayout.Height(520));
            foreach (var choice in filtered)
            {
                bool saved = HasSavedPose(choice.Entry.Definition.Path, choice.Variant, choice.Entry.Definition.ShapeHash);
                bool calibrated = TryGetPose(choice.Entry.Definition.Path, choice.Variant, choice.Entry.Definition.ShapeHash, out _);
                if (GUILayout.Button(new GUIContent((ReferenceEquals(choice, selected) ? "> " : "") + (saved ? "* " : calibrated ? "[default] " : "") + choice.Label, choice.Label), style, GUILayout.Height(25), GUILayout.Width(270))) RequestSelection(choice);
            }
            GUILayout.EndScrollView();
            if (pendingSelection != null)
            {
                GUILayout.Label("Unsaved edits. Switch to " + pendingSelection.Label + "?");
                if (GUILayout.Button("Save and switch")) { Save(); if (!draftChanged) Select(pendingSelection); }
                if (GUILayout.Button("Discard and switch")) Select(pendingSelection);
                if (GUILayout.Button("Keep editing")) pendingSelection = null;
            }
            else GUILayout.Label("* Local override; [default] built-in calibration. Drafts apply after Save.");
            GUILayout.EndVertical();
        }
        private void RequestSelection(Choice choice)
        {
            if (ReferenceEquals(choice, selected)) return;
            if (draftChanged) pendingSelection = choice; else Select(choice);
        }
        private void DrawEditControls()
        {
            if (draft == null || selected == null) return;
            GUILayout.Label(selected.Label);
            int target = SeatMode ? GUILayout.SelectionGrid((int)editTarget, new[] { "Body", "Right palm", "Left palm", "Right foot", "Left foot" }, 3, GUILayout.Height(48)) :
                GUILayout.Toolbar((int)editTarget, selected.HandFlashlight ? new[] { "Item", "Left palm" } : new[] { "Item", "Right palm", "Left palm" });
            if (target != (int)editTarget) { editTarget = (EditTarget)target; pickArmed = false; editRangeReady = false; GUI.FocusControl(""); }
            bool twoHands = !selected.HandFlashlight && GUILayout.Toggle(draft.TwoHanded, SeatMode ? "Custom hand targets (both palms)" : "Independent two-handed grip");
            if (twoHands != draft.TwoHanded) SetTwoHands(twoHands);
            if (SeatMode && draft is VehicleSeatPoseOverrides.SeatDraft feet)
            {
                bool customFeet = GUILayout.Toggle(feet.CustomFeet, "Custom foot targets (both feet)");
                if (customFeet != feet.CustomFeet) SetSeatFeet(customFeet);
                if (customFeet && (rightFootError > 0.02f || leftFootError > 0.02f))
                    GUILayout.Label("Foot target beyond leg reach. Adjust body or targets (right " + (rightFootError * 100).ToString("F1") + " cm / left " + (leftFootError * 100).ToString("F1") + " cm short).");
            }
            if (!draft.TwoHanded && draft.Width > 0.01f)
                GUILayout.Label("Legacy width grip. Enable independent hands to adjust each palm.");
            bool canEdit = TargetEditable;
            bool oldEnabled = GUI.enabled; GUI.enabled = oldEnabled && canEdit;
            bool rotation = GUILayout.Toolbar(rotateEdit ? 1 : 0, new[] { "Move arrows", "Rotation rings" }) == 1;
            if (rotation != rotateEdit) { rotateEdit = rotation; pickArmed = false; editRangeReady = false; }
            editAxis = GUILayout.Toolbar(editAxis, rotateEdit ? new[] { "All rings", "X", "Y", "Z" } : new[] { "All arrows", "X", "Y", "Z" });
            EnsureEditRange();
            if (rotateEdit)
            {
                GUILayout.BeginHorizontal(); GUILayout.Label("Turn range", GUILayout.Width(85));
                if (GUILayout.Button("±15°")) turnRange = 15;
                if (GUILayout.Button("±45°")) turnRange = 45;
                if (GUILayout.Button("±90°")) turnRange = 90;
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.BeginHorizontal(); GUILayout.Label("Move range", GUILayout.Width(85));
                if (GUILayout.Button("±2 cm")) moveRange = 0.02f;
                if (GUILayout.Button("±10 cm")) moveRange = 0.1f;
                if (GUILayout.Button("±50 cm")) moveRange = 0.5f;
                GUILayout.EndHorizontal();
            }
            snapEdits = GUILayout.Toggle(snapEdits, "Snap handles/sliders to the chosen step");
            if (GUILayout.Button("Recenter adjustment range here")) RecenterEditRange();
            GUILayout.Label(SeatMode ? (editTarget == EditTarget.Item ? "Body offset in seat axes • metres / degrees" : IsFootTarget ? "Foot / ankle target in seat axes • metres / degrees" : selected.Seat?.Wheel != null ? "Palm offset in bounded wheel frame • metres / degrees" : "Palm offset in seat axes • metres / degrees") : editTarget == EditTarget.Item ? (rotateEdit ? "Rotation in tool axes • degrees" : "Translation in body axes • metres") :
                (rotateEdit ? "Palm rotation in tool axes • degrees" : !draft.TwoHanded && draft.Width > 0.01f ? "Legacy carry center in tool axes • metres" : "Palm position in tool axes • metres"));
            GUILayout.Label("Use the colored handles or bounded sliders. Type a value + Enter for an exact offset.");
            Vector3 position = TargetPosition(), euler = TargetEuler();
            if (!rotateEdit)
            {
                position.x = Axis("X", 0, position.x, positionStep, editTarget == EditTarget.Item ? -2 : -10, editTarget == EditTarget.Item ? 2 : 10);
                position.y = Axis("Y", 1, position.y, positionStep, editTarget == EditTarget.Item ? -2 : -10, editTarget == EditTarget.Item ? 2 : 10);
                position.z = Axis("Z", 2, position.z, positionStep, editTarget == EditTarget.Item ? -2 : -10, editTarget == EditTarget.Item ? 2 : 10);
                GUILayout.BeginHorizontal(); GUILayout.Label("Move step", GUILayout.Width(80));
                if (GUILayout.Button("1 mm")) positionStep = 0.001f;
                if (GUILayout.Button("1 cm")) positionStep = 0.01f;
                if (GUILayout.Button("10 cm")) positionStep = 0.1f;
                GUILayout.EndHorizontal();
            }
            else
            {
                euler.x = Axis("Pitch / X", 0, euler.x, angleStep, -360, 360);
                euler.y = Axis("Yaw / Y", 1, euler.y, angleStep, -360, 360);
                euler.z = Axis("Roll / Z", 2, euler.z, angleStep, -360, 360);
                GUILayout.BeginHorizontal(); GUILayout.Label("Turn step", GUILayout.Width(80));
                if (GUILayout.Button("1°")) angleStep = 1;
                if (GUILayout.Button("5°")) angleStep = 5;
                if (GUILayout.Button("15°")) angleStep = 15;
                GUILayout.EndHorizontal();
            }
            if (canEdit) SetTarget(position, euler);
            if (editTarget != EditTarget.Item && GUILayout.Button(pickArmed ? "Cancel point picking" : SeatMode ? IsFootTarget ? "Pick foot point on vehicle" : "Pick palm point on vehicle" : "Pick palm point on tool", GUILayout.Height(30)))
            { if (!draft.TwoHanded && draft.Width > 0.01f) SetTwoHands(true); pickArmed = !pickArmed; rotateEdit = false;
                if (SeatMode && pickArmed) { showSeatVehicle = true; seatVehicleCopy?.SetActive(true); } }
            if (editTarget != EditTarget.Item && GUILayout.Button(IsFootTarget ? "Use preview foot orientation" : "Use preview palm orientation"))
            {
                if (body != null && itemCopy != null) SetTarget(TargetPosition(), Signed((Quaternion.Inverse(TargetFrame!.rotation) *
                    (editTarget == EditTarget.LeftFoot ? body.LeftFoot.rotation : editTarget == EditTarget.RightFoot ? body.RightFoot.rotation : editTarget == EditTarget.Left ? body.LeftHand.rotation : body.RightHand.rotation)).eulerAngles));
                editRangeReady = false;
            }
            GUI.enabled = oldEnabled;
            if (!canEdit) GUILayout.Label(SeatMode ? IsFootTarget ? "Enable custom foot targets to edit either foot." : "Enable custom hand targets to edit either palm." : "Enable two hands to place the left palm.");
            if (GUILayout.Button(SeatMode ? "Zero body offset" : "Zero item visual offset"))
            { draft.ItemX = draft.ItemY = draft.ItemZ = draft.ItemPitch = draft.ItemYaw = draft.ItemRoll = 0; editRangeReady = false; }
            GUILayout.Space(6);
            if (!SeatMode && GUILayout.Button("Capture currently equipped tool pose")) CaptureHeld();
            if (GUILayout.Button("Seed hands from automatic presentation")) SeedAutomatic();
            if (SeatMode && GUILayout.Button("Seed feet from automatic presentation")) SeedSeatFeet();
            DrawPoseCopy();
            if (GUILayout.Button("Revert unsaved edits") && initial != null) { draft = initial.Copy(); numericText.Clear(); editRangeReady = false; }
            if (GUILayout.Button("Remove saved override / restore default")) Reset();
            GUILayout.Label(SeatMode ? "Local avatar presentation for this vehicle model and seat. Zero offset and automatic hands preserve the original pose." : selected.SceneEquipment ? "Scene model: remote use depends on its equipment transport." : "Local remote-avatar presentation settings.");
            GUILayout.Label("Saved to: " + (seatOverrides?.Path ?? overrides.Path));
        }
        private void EnsureEditRange() { if (!editRangeReady) RecenterEditRange(); }
        private void RecenterEditRange()
        { editRangePosition = TargetPosition(); editRangeEuler = TargetEuler(); editRangeReady = true; }
        private void DrawPoseCopy()
        {
            if (GUILayout.Button(copyChooser ? "Close pose chooser" : SeatMode ? "Copy pose from completed seat…" : "Copy pose from completed tool…"))
            {
                copyChooser = !copyChooser; copySources.Clear();
                if (copyChooser) copySources.AddRange(SeatMode ? seatOverrides!.GetCalibratedPoses().Select(p => p.ToGrip()) : overrides.GetCalibratedGrips());
            }
            if (!copyChooser || draft == null) return;
            GUILayout.Label(SeatMode ? "Copies body, both palms and both feet into this seat draft. Save when ready." : "Copies item offsets and both grips into this draft. Save when ready.");
            copyFilter = GUILayout.TextField(copyFilter, GUILayout.Height(24));
            copyScroll = GUILayout.BeginScrollView(copyScroll, GUILayout.Height(145));
            int count = 0;
            foreach (var source in copySources)
            {
                var choice = choices.FirstOrDefault(c => c.Entry.Definition.Path == source.Prefab && c.Variant == source.Variant && c.Entry.Definition.ShapeHash == source.Shape);
                string label = choice?.Label ?? source.Prefab + (source.Variant == "default" ? "" : " [" + source.Variant + "]");
                label += HasSavedPose(source.Prefab, source.Variant, source.Shape) ? " [local]" : " [default]";
                if (choice == null) label += " [model unavailable]";
                if (label.IndexOf(copyFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                count++;
                if (GUILayout.Button(new GUIContent("Copy: " + label, label), GUILayout.Height(28)))
                {
                    try
                    {
                        if (CopySavedPose(source, draft))
                        {
                            numericText.Clear(); editRangeReady = false; pickArmed = false; copyChooser = false;
                            if (selected?.HandFlashlight == true) { draft.TwoHanded = false; draft.Width = 0; }
                            if (!draft.TwoHanded && editTarget == EditTarget.Left) editTarget = EditTarget.Right;
                            message = "Copied calibration from " + label + ". Destination remains unsaved.";
                        }
                        else message = "That source changed. Reopen the chooser to refresh it.";
                    }
                    catch (Exception ex) { message = "Pose copy failed: " + ex.Message; }
                }
            }
            if (count == 0) GUILayout.Label(SeatMode ? "No calibrated seats match this search." : "No calibrated tools match this search.");
            GUILayout.EndScrollView();
        }
        private float Axis(string label, int axis, float value, float step, float min, float max)
        {
            float center = rotateEdit ? editRangeEuler[axis] : editRangePosition[axis];
            float range = rotateEdit ? turnRange : moveRange;
            float low = Mathf.Max(min, center - range), high = Mathf.Min(max, center + range);
            GUILayout.BeginHorizontal();
            Rect scrub = GUILayoutUtility.GetRect(new GUIContent(label), GUI.skin.label, GUILayout.Width(82), GUILayout.Height(24));
            GUI.Label(scrub, new GUIContent(label, "Drag left/right to scrub this value; Shift is ten times finer."));
            int id = GUIUtility.GetControlID(FocusType.Passive, scrub);
            Event current = Event.current;
            if (GUI.enabled && GUIUtility.hotControl == 0 && current.type == EventType.MouseDown && current.button == 0 && scrub.Contains(current.mousePosition))
            { GUI.FocusControl(""); scrubControl = id; GUIUtility.hotControl = id; gestureStart = draft?.Copy(); scrubStartValue = value; scrubPixels = 0; current.Use(); }
            if (GUIUtility.hotControl == id)
            {
                if (current.type == EventType.MouseDrag)
                {
                    scrubPixels += current.delta.x * step * 0.1f * (current.shift ? 0.1f : 1);
                    value = HoldPreviewGizmoMath.ClampAndSnap(scrubStartValue, scrubPixels, low, high, step, snapEdits); current.Use();
                }
                if (current.type == EventType.MouseUp && current.button == 0) { GUIUtility.hotControl = 0; EndGesture(); current.Use(); }
            }
            if (RepeatButton("−", 28)) value = Mathf.Clamp(value - step, low, high);
            string key = editTarget + "/" + rotateEdit + "/" + label;
            string control = "HeldGrip/" + key;
            bool focused = GUI.GetNameOfFocusedControl() == control;
            if (!focused || !numericText.ContainsKey(key)) numericText[key] = value.ToString("0.####", CultureInfo.InvariantCulture);
            GUI.SetNextControlName(control);
            numericText[key] = GUILayout.TextField(numericText[key], GUILayout.Width(91));
            bool valid = float.TryParse(numericText[key], NumberStyles.Float, CultureInfo.InvariantCulture, out float typed) && !float.IsNaN(typed) && !float.IsInfinity(typed);
            bool apply = GUILayout.Button(new GUIContent("↵", "Apply typed value"), GUILayout.Width(25));
            if (focused && current.type == EventType.KeyDown && (current.keyCode == KeyCode.Return || current.keyCode == KeyCode.KeypadEnter))
            { apply = true; current.Use(); }
            if (apply)
            {
                if (valid) { value = typed; editRangeReady = false; GUI.FocusControl(""); } else message = "Enter a finite number using a decimal point.";
            }
            if (RepeatButton("+", 28)) value = Mathf.Clamp(value + step, low, high);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label(low.ToString(rotateEdit ? "0.#" : "0.###", CultureInfo.InvariantCulture), GUILayout.Width(48));
            Rect sliderRect = GUILayoutUtility.GetRect(170, 22, GUILayout.ExpandWidth(true), GUILayout.Height(22));
            bool sliderStart = GUI.enabled && GUIUtility.hotControl == 0 && current.type == EventType.MouseDown && current.button == 0 && sliderRect.Contains(current.mousePosition);
            if (sliderStart) { GUI.FocusControl(""); gestureStart = draft?.Copy(); sliderDragging = true; }
            float slide = GUI.HorizontalSlider(sliderRect, Mathf.Clamp(value, low, high), low, high);
            if (slide != Mathf.Clamp(value, low, high)) value = HoldPreviewGizmoMath.ClampAndSnap(center, slide - center, low, high, step, snapEdits);
            GUILayout.Label(high.ToString(rotateEdit ? "0.#" : "0.###", CultureInfo.InvariantCulture), GUILayout.Width(48));
            GUILayout.EndHorizontal();
            if (sliderDragging && current.rawType == EventType.MouseUp && GUIUtility.hotControl == 0) { sliderDragging = false; EndGesture(); }
            return Mathf.Clamp(value, min, max);
        }
        private bool RepeatButton(string text, float width)
        {
            Rect rect = GUILayoutUtility.GetRect(width, 24, GUILayout.Width(width), GUILayout.Height(24));
            int id = GUIUtility.GetControlID(FocusType.Passive, rect);
            Event current = Event.current;
            bool inside = rect.Contains(current.mousePosition);
            if (current.type == EventType.Repaint) GUI.skin.button.Draw(rect, new GUIContent(text), inside, GUIUtility.hotControl == id, false, false);
            if (GUI.enabled && inside && current.type == EventType.MouseDown && current.button == 0 && GUIUtility.hotControl == 0)
            {
                GUI.FocusControl(""); GUIUtility.hotControl = id; repeatControl = id;
                nextRepeat = Time.realtimeSinceStartup + 0.35f; gestureStart = draft?.Copy(); current.Use(); return true;
            }
            if (GUIUtility.hotControl != id) return false;
            if (current.type == EventType.MouseUp && current.button == 0) { EndGesture(); current.Use(); return false; }
            if (current.type != EventType.Repaint || !inside) return false;
            if (Time.realtimeSinceStartup < nextRepeat) return false;
            nextRepeat = Time.realtimeSinceStartup + 0.075f; return true;
        }
        private static float Slider(string label, float value, float min, float max)
        { GUILayout.Label(label + ": " + value.ToString("F2")); return GUILayout.HorizontalSlider(value, min, max); }
        private bool IsFootTarget => editTarget == EditTarget.RightFoot || editTarget == EditTarget.LeftFoot;
        private bool TargetEditable => draft != null && (SeatMode ? editTarget == EditTarget.Item || (IsFootTarget ? (draft as VehicleSeatPoseOverrides.SeatDraft)?.CustomFeet == true : draft.TwoHanded) : editTarget != EditTarget.Left || draft.TwoHanded);
        private Vector3 TargetPosition() => draft == null ? Vector3.zero : IsFootTarget && draft is VehicleSeatPoseOverrides.SeatDraft feet ? editTarget == EditTarget.LeftFoot ? feet.LeftFootPosition : feet.RightFootPosition : editTarget == EditTarget.Item ? draft.ItemPosition : editTarget == EditTarget.Left ? draft.LeftPosition : draft.Position;
        private Vector3 TargetEuler() => draft == null ? Vector3.zero : IsFootTarget && draft is VehicleSeatPoseOverrides.SeatDraft feet ? editTarget == EditTarget.LeftFoot ? feet.LeftFootEuler : feet.RightFootEuler : editTarget == EditTarget.Item ? draft.ItemEuler : editTarget == EditTarget.Left ? draft.LeftEuler : draft.Euler;
        private void SetTarget(Vector3 position, Vector3 euler)
        {
            if (draft == null) return;
            float limit = editTarget == EditTarget.Item ? 2 : 10;
            for (int i = 0; i < 3; i++) { position[i] = Mathf.Clamp(position[i], -limit, limit); euler[i] = Mathf.Clamp(euler[i], -360, 360); }
            if (IsFootTarget && draft is VehicleSeatPoseOverrides.SeatDraft feet)
            { if (editTarget == EditTarget.LeftFoot) feet.SetLeftFoot(position, euler); else feet.SetRightFoot(position, euler); }
            else if (editTarget == EditTarget.Item)
            { draft.ItemX = position.x; draft.ItemY = position.y; draft.ItemZ = position.z; draft.ItemPitch = euler.x; draft.ItemYaw = euler.y; draft.ItemRoll = euler.z; }
            else if (editTarget == EditTarget.Left) draft.SetLeft(position, euler);
            else draft.Set(position, euler, draft.Width);
        }
        private void SetTwoHands(bool enabled)
        {
            if (draft == null || body == null || itemCopy == null) return;
            if (SeatMode) { SetSeatHands(enabled); return; }
            if (enabled)
            {
                if (draft.LeftInitialized || !draft.LeftPosition.Equals(Vector3.zero) || !draft.LeftEuler.Equals(Vector3.zero))
                { draft.TwoHanded = true; draft.Width = 0; numericText.Clear(); pickArmed = false; editRangeReady = false; return; }
                // Converting a legacy centered grip retains the two solved palm locations.
                RenderPreview();
                var root = itemCopy.transform;
                Vector3 right = draft.Width > 0.01f ? root.InverseTransformPoint(body.RightPalm) : draft.Position;
                Vector3 left = draft.Width > 0.01f ? root.InverseTransformPoint(body.LeftPalm) : root.InverseTransformPoint(root.TransformPoint(right) - Vector3.right * 0.18f);
                draft.Set(right, draft.Euler, 0);
                draft.EnableTwoHands(left, Signed((Quaternion.Inverse(root.rotation) * body.LeftHand.rotation).eulerAngles));
            }
            else { draft.TwoHanded = false; draft.Width = 0; if (editTarget == EditTarget.Left) editTarget = EditTarget.Right; }
            numericText.Clear(); pickArmed = false;
            editRangeReady = false;
        }
        private static bool SameGrip(HeldGripOverrides.Grip a, HeldGripOverrides.Grip b) => a.Prefab == b.Prefab && a.Variant == b.Variant && a.Shape == b.Shape &&
            a.Position.Equals(b.Position) && a.Euler.Equals(b.Euler) && a.Width == b.Width && a.ItemPosition.Equals(b.ItemPosition) && a.ItemEuler.Equals(b.ItemEuler) &&
            a.TwoHanded == b.TwoHanded && a.LeftPosition.Equals(b.LeftPosition) && a.LeftEuler.Equals(b.LeftEuler) && VehicleSeatPoseOverrides.SeatDraft.SameFeet(a, b);
        private void RecordEdit(HeldGripOverrides.Grip before)
        {
            if (draft == null || SameGrip(before, draft)) return;
            if (undo.Count == 64) undo.RemoveAt(0);
            undo.Add(before.Copy()); redo.Clear(); UpdateDirty();
        }
        private void UpdateDirty() { draftChanged = draft != null && (initial == null || !SameGrip(initial, draft)); }
        private void EndGesture()
        {
            if (gestureStart != null) { RecordEdit(gestureStart); gestureStart = null; historyAction = true; editRangeReady = false; }
            if (viewportControl != 0 && GUIUtility.hotControl == viewportControl) GUIUtility.hotControl = 0;
            if (scrubControl != 0 && GUIUtility.hotControl == scrubControl) GUIUtility.hotControl = 0;
            if (repeatControl != 0 && GUIUtility.hotControl == repeatControl) GUIUtility.hotControl = 0;
            repeatControl = 0;
            sliderDragging = false;
            scrubControl = 0;
            activeDragButton = -1; dragMouseButton = -1;
        }
        private void DrawHistory()
        {
            GUILayout.BeginHorizontal();
            bool enabled = GUI.enabled;
            GUI.enabled = enabled && undo.Count > 0;
            if (GUILayout.Button("Undo")) RestoreHistory(undo, redo);
            GUI.enabled = enabled && redo.Count > 0;
            if (GUILayout.Button("Redo")) RestoreHistory(redo, undo);
            GUI.enabled = enabled; GUILayout.EndHorizontal();
        }
        private void RestoreHistory(List<HeldGripOverrides.Grip> from, List<HeldGripOverrides.Grip> to)
        {
            if (draft == null || from.Count == 0) return;
            to.Add(draft.Copy()); draft = from[from.Count - 1]; from.RemoveAt(from.Count - 1);
            if (selected?.HandFlashlight == true) { draft.TwoHanded = false; draft.Width = 0; }
            if (!draft.TwoHanded && editTarget == EditTarget.Left) editTarget = EditTarget.Right;
            numericText.Clear(); pickArmed = false; historyAction = true; UpdateDirty();
            editRangeReady = false;
        }
        private void FramePreview(int focus)
        {
            if (previewRoot == null || itemCopy == null || camera == null || body == null) return;
            if (SeatMode && focus == 4)
            {
                var feet = draft as VehicleSeatPoseOverrides.SeatDraft;
                Vector3 right = feet?.CustomFeet == true ? itemCopy.transform.TransformPoint(feet.RightFootPosition) : body.RightFoot.position;
                Vector3 left = feet?.CustomFeet == true ? itemCopy.transform.TransformPoint(feet.LeftFootPosition) : body.LeftFoot.position;
                cameraBounds = new Bounds(right, Vector3.one * 0.3f); cameraBounds.Encapsulate(new Bounds(left, Vector3.one * 0.3f));
            }
            else if (SeatMode && focus == 3 && seatBodyRoot != null) cameraBounds = VisibleBounds(seatBodyRoot);
            else if (focus == 1) cameraBounds = VisibleBounds(itemCopy);
            else if (focus == 2)
            {
                Vector3 right = draft == null || (!draft.TwoHanded && (SeatMode || draft.Width > 0.01f)) ? body.RightPalm : GripFrame!.TransformPoint(draft.Position);
                cameraBounds = new Bounds(right, Vector3.one * 0.25f);
                if (draft?.TwoHanded == true) cameraBounds.Encapsulate(new Bounds(GripFrame!.TransformPoint(draft.LeftPosition), Vector3.one * 0.25f));
                else if (draft != null && draft.Width > 0.01f) cameraBounds.Encapsulate(new Bounds(body.LeftPalm, Vector3.one * 0.25f));
            }
            else cameraBounds = VisibleBounds(previewRoot);
            cameraPivot = cameraBounds.center;
            cameraFit = FitDistance(cameraBounds, Quaternion.LookRotation(-(Quaternion.Euler(-elevation, orbit, 0) * Vector3.forward)), camera.fieldOfView, camera.aspect);
            distance = 1.1f; framePending = false;
        }
        private void SetCameraView(float yaw, float height)
        { orbit = yaw; elevation = height; FramePreview(0); }
        private Transform? GripFrame => SeatMode ? seatHandFrame : itemCopy?.transform;
        private Transform? TargetFrame => SeatMode && IsFootTarget ? itemCopy?.transform : GripFrame;
        private Vector3 TargetWorldPosition() => itemCopy == null ? Vector3.zero : editTarget == EditTarget.Item ? SeatMode ? seatBodyRoot!.transform.position : itemCopy.transform.position : TargetFrame!.TransformPoint(TargetPosition());
        private Vector3 WorldAxis(int index)
        {
            Vector3 axis = Vector3.zero; axis[index] = 1;
            if (itemCopy == null) return axis;
            if (!rotateEdit) return editTarget == EditTarget.Item ? axis : TargetFrame!.TransformVector(axis).normalized;
            return (editTarget == EditTarget.Item && SeatMode ? Quaternion.identity : TargetFrame!.rotation) * (editTarget == EditTarget.Item ? SeatMode ? Quaternion.Euler(TargetEuler()) * axis : axis : Quaternion.Euler(TargetEuler()) * axis);
        }
        private static Vector2 ViewportPoint(Camera view, Rect rect, Vector3 world)
        {
            var point = view.WorldToViewportPoint(world);
            return new Vector2(rect.x + point.x * rect.width, rect.y + (1 - point.y) * rect.height);
        }
        private static System.Numerics.Vector2 Numeric(Vector2 value) => new System.Numerics.Vector2(value.x, value.y);
        private static Color AxisColor(int axis) => axis == 0 ? new Color(1, 0.35f, 0.3f) : axis == 1 ? Color.green : new Color(0.3f, 0.65f, 1);
        private static string AxisName(int axis) => axis == 0 ? "X / pitch" : axis == 1 ? "Y / yaw" : "Z / roll";
        private void RingBasis(int axis, out Vector3 normal, out Vector3 u, out Vector3 v)
        {
            normal = WorldAxis(axis).normalized;
            u = Vector3.Cross(normal, Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up).normalized;
            v = Vector3.Cross(normal, u).normalized;
        }
        private int DrawAndHitGizmo(Rect rect, out Vector2 center, out float length)
        {
            center = Vector2.zero; length = 0;
            if (camera == null || itemCopy == null || draft == null || !showMarkers) return -1;
            if (!TargetEditable) return -1;
            Vector3 origin = TargetWorldPosition();
            float depth = Vector3.Dot(origin - camera.transform.position, camera.transform.forward);
            if (depth <= camera.nearClipPlane) return -1;
            center = ViewportPoint(camera, rect, origin);
            length = 2 * depth * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f) * 62 / rect.height;
            Vector2 pointer = Event.current.mousePosition;
            int hit = -1; float best = 9;
            bool draw = Event.current.type == EventType.Repaint;
            if (draw) GUI.BeginClip(rect);
            Vector2 clipOffset = new Vector2(rect.x, rect.y);
            for (int axis = 0; axis < 3; axis++)
            {
                if (editAxis != 0 && editAxis != axis + 1) continue;
                Color color = AxisColor(axis);
                bool active = activeDragButton == 0 && dragAxis == axis + 1;
                if (rotateEdit)
                {
                    RingBasis(axis, out _, out Vector3 u, out Vector3 v);
                    Vector2 projectedU = ViewportPoint(camera, rect, origin + u * length) - center;
                    Vector2 projectedV = ViewportPoint(camera, rect, origin + v * length) - center;
                    bool usable = HoldPreviewGizmoMath.TryRingAngle(Numeric(center + projectedU), Numeric(center), Numeric(projectedU), Numeric(projectedV), out _);
                    if (!usable) color.a = 0.25f;
                    Vector2 previous = center + projectedU;
                    for (int segment = 1; segment <= 48; segment++)
                    {
                        float radians = segment * Mathf.PI * 2 / 48;
                        Vector2 next = center + projectedU * Mathf.Cos(radians) + projectedV * Mathf.Sin(radians);
                        if (draw) DrawLine(previous - clipOffset, next - clipOffset, color, active ? 4 : 2);
                        if (usable)
                        {
                            float separation = HoldPreviewGizmoMath.SegmentDistance(Numeric(pointer), Numeric(previous), Numeric(next));
                            if (separation < best) { best = separation; hit = axis + 1; }
                        }
                        previous = next;
                    }
                    if (draw) GUI.Label(new Rect(center.x + projectedU.x - rect.x, center.y + projectedU.y - rect.y, 85, 22), AxisName(axis));
                }
                else
                {
                    Vector3 endWorld = origin + WorldAxis(axis) * length;
                    if (camera.WorldToViewportPoint(endWorld).z <= camera.nearClipPlane) continue;
                    Vector2 end = ViewportPoint(camera, rect, endWorld), projected = end - center;
                    if (projected.sqrMagnitude < 576) continue;
                    Vector2 direction = projected.normalized;
                    Vector2 from = center + direction * 13;
                    if (draw)
                    {
                        DrawLine(from - clipOffset, end - clipOffset, color, active ? 4 : 3);
                        Vector2 side = new Vector2(-direction.y, direction.x) * 5;
                        DrawLine(end - clipOffset, end - direction * 10 + side - clipOffset, color, 3);
                        DrawLine(end - clipOffset, end - direction * 10 - side - clipOffset, color, 3);
                        GUI.Label(new Rect(end.x - rect.x + 3, end.y - rect.y - 12, 30, 24), axis == 0 ? "X" : axis == 1 ? "Y" : "Z");
                    }
                    float separation = HoldPreviewGizmoMath.SegmentDistance(Numeric(pointer), Numeric(from), Numeric(end));
                    if (separation < best) { best = separation; hit = axis + 1; }
                }
            }
            if (!rotateEdit)
            {
                var box = new Rect(center.x - 6, center.y - 6, 12, 12);
                if (box.Contains(pointer)) hit = 0;
                if (draw) GUI.Box(new Rect(box.x - rect.x, box.y - rect.y, box.width, box.height), GUIContent.none);
            }
            if (draw)
            {
                string label = editTarget == EditTarget.Item ? SeatMode ? "Body" : "Item" : editTarget == EditTarget.LeftFoot ? "Left foot" : editTarget == EditTarget.RightFoot ? "Right foot" : editTarget == EditTarget.Left ? "Left palm" : selected?.HandFlashlight == true ? "Left palm" : "Right palm";
                GUI.Label(new Rect(center.x - rect.x + 12, center.y - rect.y - 28, 110, 24), label);
                if (activeDragButton == 0) GUI.Label(new Rect(12, rect.height - 28, rect.width - 24, 24), rotateEdit ?
                    AxisName(dragAxis - 1) + ": " + Mathf.Clamp(dragTurnTotal, -turnRange, turnRange).ToString("0.#") + "° (limit ±" + turnRange + "°)" :
                    "Move limited to ±" + (moveRange * 100).ToString("0.#") + " cm; release to finish");
                GUI.EndClip();
            }
            return hit;
        }
        private void DrawViewport(Rect rect)
        {
            viewportControl = GUIUtility.GetControlID(FocusType.Passive, rect);
            Event current = Event.current;
            bool inside = rect.Contains(current.mousePosition);
            if (camera == null || itemCopy == null || draft == null) { EndGesture(); return; }
            int hovered = DrawAndHitGizmo(rect, out Vector2 center, out float length);
            if (inside && current.type == EventType.ScrollWheel && GUIUtility.hotControl == 0)
            { distance = Mathf.Clamp(distance * Mathf.Exp(current.delta.y * 0.075f), 0.03f, 4); current.Use(); }
            if (inside && current.type == EventType.MouseDown && GUIUtility.hotControl == 0 && current.button <= 2)
            {
                GUI.FocusControl("");
                if (current.button == 0 && pickArmed && !current.alt)
                {
                    var uv = new Vector3((current.mousePosition.x - rect.x) / rect.width, 1 - (current.mousePosition.y - rect.y) / rect.height, 0);
                    if (picking.TryPick(camera.ViewportPointToRay(uv), out Vector3 point, out _, out bool approximate))
                    {
                        SetTarget(TargetFrame!.InverseTransformPoint(point), TargetEuler());
                        pickArmed = false; numericText.Clear(); editRangeReady = false;
                        message = approximate ? "Target seeded from mesh bounds (approximate). Fine-tune position and rotation." : IsFootTarget ? "Foot / ankle point placed on the surface. Fine-tune boot clearance and rotation." : "Palm placed on the surface. Fine-tune position and rotation.";
                    }
                    else message = "No visible tool surface at that point. Orbit the view and try again.";
                    current.Use(); return;
                }
                int button = current.alt && current.button == 0 ? 2 : current.button;
                if (button == 0)
                {
                    if (!TargetEditable || hovered < 0)
                    { message = showMarkers ? "Drag a colored arrow/ring, or use the axis sliders. Orbit for an edge-on handle." : "Enable editing axes to drag a handle, or use the axis sliders."; current.Use(); return; }
                    dragAxis = hovered; dragHandleLength = length;
                    dragStartPosition = TargetPosition(); dragStartEuler = TargetEuler(); dragMoveTotal = Vector3.zero; dragTurnTotal = 0;
                    dragRingCenter = center;
                    dragRotationFrame = editTarget == EditTarget.Item ? SeatMode ? Quaternion.identity : Quaternion.Euler(pitch, 0, 0) * itemViewRotation : TargetFrame!.rotation;
                    float depth = Mathf.Max(camera.nearClipPlane, Vector3.Dot(TargetWorldPosition() - camera.transform.position, camera.transform.forward));
                    dragUnits = 2 * depth * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f) / rect.height;
                    if (rotateEdit)
                    {
                        RingBasis(dragAxis - 1, out dragWorldAxis, out Vector3 u, out Vector3 v);
                        dragRingU = ViewportPoint(camera, rect, TargetWorldPosition() + u * length) - center;
                        dragRingV = ViewportPoint(camera, rect, TargetWorldPosition() + v * length) - center;
                        if (!HoldPreviewGizmoMath.TryRingAngle(Numeric(current.mousePosition), Numeric(center), Numeric(dragRingU), Numeric(dragRingV), out dragRingLastAngle))
                        { message = "Orbit the camera to see that rotation ring, or use its slider."; current.Use(); return; }
                    }
                    else if (dragAxis != 0)
                    {
                        dragWorldAxis = WorldAxis(dragAxis - 1);
                        dragProjectedAxis = ViewportPoint(camera, rect, TargetWorldPosition() + dragWorldAxis * length) - center;
                    }
                    gestureStart = draft.Copy();
                }
                dragPointerStart = current.mousePosition; dragMouseButton = current.button; activeDragButton = button;
                GUIUtility.hotControl = viewportControl; current.Use();
            }
            if (GUIUtility.hotControl != viewportControl) return;
            if (current.type == EventType.MouseDrag)
            {
                float fine = current.shift ? 0.1f : 1;
                Vector2 displacement = current.mousePosition - dragPointerStart; dragPointerStart = current.mousePosition;
                if (activeDragButton == 1)
                { orbit = Mathf.Repeat(orbit + displacement.x * 0.45f * fine + 180, 360) - 180; elevation = Mathf.Clamp(elevation - displacement.y * 0.45f * fine, -85, 85); }
                else if (activeDragButton == 2)
                {
                    float panUnits = 2 * cameraFit * distance * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f) / rect.height;
                    cameraPivot += (-camera.transform.right * displacement.x + camera.transform.up * displacement.y) * panUnits * fine;
                }
                else if (activeDragButton == 0)
                {
                    if (rotateEdit)
                    {
                        if (HoldPreviewGizmoMath.TryRingAngle(Numeric(current.mousePosition), Numeric(dragRingCenter), Numeric(dragRingU), Numeric(dragRingV), out float angle))
                        {
                            dragTurnTotal = Mathf.Clamp(dragTurnTotal + Mathf.DeltaAngle(dragRingLastAngle, angle) * fine, -turnRange, turnRange);
                            dragRingLastAngle = angle;
                            float turn = HoldPreviewGizmoMath.ClampAndSnap(0, dragTurnTotal, -turnRange, turnRange, angleStep, snapEdits);
                            SetTarget(dragStartPosition, turn == 0 ? dragStartEuler : Signed((Quaternion.Inverse(dragRotationFrame) *
                                Quaternion.AngleAxis(turn, dragWorldAxis) * dragRotationFrame * Quaternion.Euler(dragStartEuler)).eulerAngles));
                        }
                    }
                    else
                    {
                        Vector3 worldDelta;
                        if (dragAxis == 0) worldDelta = (camera.transform.right * displacement.x - camera.transform.up * displacement.y) * dragUnits * fine;
                        else if (HoldPreviewGizmoMath.TryAxisDrag(Numeric(displacement * fine), Numeric(dragProjectedAxis), dragHandleLength, out float movement)) worldDelta = dragWorldAxis * movement;
                        else worldDelta = Vector3.zero;
                        dragMoveTotal += editTarget == EditTarget.Item ? worldDelta : TargetFrame!.InverseTransformVector(worldDelta);
                        Vector3 next = dragStartPosition;
                        for (int axis = 0; axis < 3; axis++)
                        {
                            dragMoveTotal[axis] = Mathf.Clamp(dragMoveTotal[axis], -moveRange, moveRange);
                            next[axis] = HoldPreviewGizmoMath.ClampAndSnap(dragStartPosition[axis], dragMoveTotal[axis], dragStartPosition[axis] - moveRange, dragStartPosition[axis] + moveRange, positionStep, snapEdits);
                        }
                        SetTarget(next, dragStartEuler);
                    }
                    numericText.Clear();
                }
                current.Use();
            }
            if (current.type == EventType.MouseUp && current.button == dragMouseButton) { EndGesture(); current.Use(); }
        }
        private static void DrawLine(Vector2 start, Vector2 end, Color color, float width)
        {
            Matrix4x4 matrix = GUI.matrix; Color prior = GUI.color;
            GUI.color = color;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(end.y - start.y, end.x - start.x) * Mathf.Rad2Deg, start);
            GUI.DrawTexture(new Rect(start.x, start.y - width * 0.5f, (end - start).magnitude, width), Texture2D.whiteTexture);
            GUI.matrix = matrix; GUI.color = prior;
        }
        private void Navigate(List<Choice> filtered, int direction)
        {
            if (filtered.Count == 0) return;
            int index = selected == null ? -1 : filtered.IndexOf(selected);
            RequestSelection(filtered[(index + direction + filtered.Count) % filtered.Count]);
        }
        private void Select(Choice choice)
        {
            EndGesture(); pendingSelection = null; pickArmed = false; undo.Clear(); redo.Clear(); numericText.Clear(); historyAction = true;
            editRangeReady = false; copyChooser = false;
            selected = choice; retainedSource = null; draft = null; initial = null; draftChanged = false; message = "";
            try
            {
                if (previewRoot == null) CreateStage();
                if (SeatMode) { SelectSeat(); return; }
                ReplaceItem(choice.Entry.Asset, true);
                PlaceNativePose(choice);
                SeedAutomatic();
                if (TryGetPose(choice.Entry.Definition.Path, choice.Variant, choice.Entry.Definition.ShapeHash, out var saved))
                { draft = saved; draft.LeftInitialized = draft.TwoHanded || !draft.LeftPosition.Equals(Vector3.zero) || !draft.LeftEuler.Equals(Vector3.zero); }
                if (choice.HandFlashlight && draft != null) { draft.TwoHanded = false; draft.Width = 0; }
                initial = draft?.Copy(); draftChanged = false;
                if (editTarget == EditTarget.Left && draft?.TwoHanded != true) editTarget = EditTarget.Right;
                framePending = true;
            }
            catch (Exception ex) { message = "Item preview unavailable: " + ex.Message; }
        }
        private void SeedAutomatic()
        {
            if (selected == null || itemCopy == null || body == null) return;
            if (SeatMode) { SeedSeatAutomatic(); return; }
            if (selected.HandProp)
            {
                var look = StockAvatarBody.Overlay.Look(pitch);
                if (selected.HandMap) look.Map = 1; else look.Flashlight = 1;
                body.Present(StockAvatarBody.Mode.Stand, 0, 0, crouch, look, 1);
                body.Solve(new StockAvatarBody.Goals { FlashlightProp = selected.HandFlashlight ? handPropPreview : null }, 1);
                var previous = draft?.Copy(); draft = null; PlaceViewPose();
                var frame = itemCopy.transform;
                draft = new HeldGripOverrides.Grip { Prefab = selected.Entry.Definition.Path, Variant = "default", Shape = selected.Entry.Definition.ShapeHash };
                draft.Set(frame.InverseTransformPoint(selected.HandFlashlight ? body.LeftPalm : body.RightPalm),
                    Signed((Quaternion.Inverse(frame.rotation) * (selected.HandFlashlight ? body.LeftHand : body.RightHand).rotation).eulerAngles), 0);
                if (selected.HandMap) draft.EnableTwoHands(frame.InverseTransformPoint(body.LeftPalm), Signed((Quaternion.Inverse(frame.rotation) * body.LeftHand.rotation).eulerAngles));
                if (previous != null) { draft.ItemX = previous.ItemX; draft.ItemY = previous.ItemY; draft.ItemZ = previous.ItemZ;
                    draft.ItemPitch = previous.ItemPitch; draft.ItemYaw = previous.ItemYaw; draft.ItemRoll = previous.ItemRoll; }
                draftChanged = true; editRangeReady = false; return;
            }
            var previousOffset = draft?.Copy();
            PlaceViewPose();
            body.Present(StockAvatarBody.Mode.Stand, 0, 0, 0, StockAvatarBody.Overlay.Look(0), 1f);
            var root = itemCopy.transform;
            var renderers = itemCopy.GetComponentsInChildren<Renderer>(false);
            Vector3 target = root.position; float width = 0;
            if (renderers.Length > 0)
            {
                var bounds = renderers[0].bounds;
                foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
                if (selected.Entry.Type == typeof(Tool_WateringCan) || selected.Entry.Type == typeof(Tool_FishBucket) || selected.Entry.Type == typeof(Tool_FuelCanister))
                    target = new Vector3(bounds.center.x, bounds.max.y - 0.03f, bounds.center.z);
                else
                {
                    Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
                    foreach (var mesh in itemCopy.GetComponentsInChildren<MeshFilter>(false))
                    {
                        if (mesh.sharedMesh == null) continue;
                        var b = mesh.sharedMesh.bounds;
                        for (int i = 0; i < 8; i++)
                        {
                            var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                            var point = root.InverseTransformPoint(mesh.transform.TransformPoint(corner)); min = Vector3.Min(min, point); max = Vector3.Max(max, point);
                        }
                    }
                    var size = Vector3.Scale(max - min, root.lossyScale);
                    int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
                    float second = Mathf.Max(axis == 0 ? size.y : size.x, axis == 2 ? size.y : size.z);
                    if (min.x <= max.x && size[axis] > 0.4f && size[axis] > second * 2.2f)
                    {
                        var center = (min + max) * 0.5f; var offset = Vector3.zero; offset[axis] = (max[axis] - min[axis]) * 0.4f;
                        var a = root.TransformPoint(center - offset); var b = root.TransformPoint(center + offset);
                        var shoulder = previewRoot!.transform.position + Vector3.up * 1.35f;
                        target = (a - shoulder).sqrMagnitude <= (b - shoulder).sqrMagnitude ? a : b;
                    }
                }
                if (!typeof(InteractableTools).IsAssignableFrom(selected.Entry.Type) && bounds.size.x > 0.45f) width = Mathf.Min(bounds.size.x, 2);
            }
            body.Solve(new StockAvatarBody.Goals { Held = target, HeldWidth = width, Feet = false }, 1f);
            draft = new HeldGripOverrides.Grip { Prefab = selected.Entry.Definition.Path, Variant = selected.Variant, Shape = selected.Entry.Definition.ShapeHash };
            draft.Set(root.InverseTransformPoint(target), Signed((Quaternion.Inverse(root.rotation) * body.RightHand.rotation).eulerAngles), width);
            if (previousOffset != null)
            {
                draft.ItemX = previousOffset.ItemX; draft.ItemY = previousOffset.ItemY; draft.ItemZ = previousOffset.ItemZ;
                draft.ItemPitch = previousOffset.ItemPitch; draft.ItemYaw = previousOffset.ItemYaw; draft.ItemRoll = previousOffset.ItemRoll;
            }
            draftChanged = true;
            editRangeReady = false;
        }
        private static Vector3 Signed(Vector3 euler) => new Vector3(Mathf.DeltaAngle(0, euler.x), Mathf.DeltaAngle(0, euler.y), Mathf.DeltaAngle(0, euler.z));
        private static Bounds VisibleBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled && !r.forceRenderingOff && !(r is ParticleSystemRenderer) &&
                r.gameObject.name != "MonMP palm target" && r.gameObject.name != "MonMP left palm target").ToArray();
            if (renderers.Length == 0) throw new InvalidOperationException("Preview has no visible meshes.");
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }
        private static float FitDistance(Bounds bounds, Quaternion cameraRotation, float verticalFov, float aspect)
        {
            float tanY = Mathf.Tan(verticalFov * Mathf.Deg2Rad * 0.5f), tanX = tanY * aspect, required = 0.1f;
            var inverse = Quaternion.Inverse(cameraRotation);
            for (int i = 0; i < 8; i++)
            {
                var corner = Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var projected = inverse * corner;
                required = Mathf.Max(required, Mathf.Abs(projected.x) / tanX - projected.z, Mathf.Abs(projected.y) / tanY - projected.z, 0.1f - projected.z);
            }
            return required;
        }
        private void PlaceNativePose(Choice choice)
        {
            if (itemCopy == null || previewRoot == null || gameplay.Player == null) return;
            if (choice.HandProp) { poseSource = choice.HandMap ? "native map hand prop" : "native flashlight hand prop"; PlaceViewPose(); return; }
            var player = gameplay.Player;
            var asset = choice.Entry.Asset.GetComponent<InteractableItems>();
            Transform? location = null;
            if (asset is InteractableTools tool)
            {
                if (tool.toolsType == ToolsType.HoistPuller) location = player.Tool_HoistPuller_Location;
                else if (AccessTools.Field(typeof(PlayerInteractable), "toolLocations").GetValue(player) is InteractableToolLocation[] locations)
                {
                    int skip = choice.Variant == "spanner-second" ? 1 : 0;
                    foreach (var candidate in locations)
                        if (candidate.toolsType == tool.toolsType) { if (skip-- > 0) continue; location = candidate.Location; break; }
                }
            }
            if (location != null)
            {
                // EquipTool copies these local values, regardless of the location's parent.
                itemViewPosition = location.localPosition; itemViewRotation = location.localRotation; poseSource = "native equip anchor";
            }
            else
            {
                itemViewPosition = player.transform.InverseTransformPoint(player.HandLocation.position);
                itemViewRotation = Quaternion.Inverse(player.transform.rotation) * player.HandLocation.rotation;
                poseSource = "No native equip anchor: staging reference; capture the equipped tool";
            }
            PlaceViewPose();
        }
        private void PlaceViewPose()
        {
            if (itemCopy == null || previewRoot == null) return;
            if (SeatMode || selected?.HandFlashlight == true) return;
            if (selected?.HandMap == true && handPropPreview != null && body != null)
            { RemoteMapCalibration.Place(handPropPreview, body, draft); return; }
            var viewRotation = Quaternion.Euler(pitch, 0, 0);
            itemCopy.transform.SetPositionAndRotation(previewRoot.transform.position + Vector3.up * Mathf.Lerp(1.6f, 1.0f, crouch) + viewRotation * itemViewPosition + (draft?.ItemPosition ?? Vector3.zero),
                viewRotation * itemViewRotation * Quaternion.Euler(draft?.ItemEuler ?? Vector3.zero));
        }
        private void CaptureHeld()
        {
            if (selected == null || previewRoot == null || gameplay.Player == null) return;
            if (selected.HandProp)
            { ReplaceItem(selected.Entry.Asset, false); PlaceViewPose(); poseSource = selected.HandMap ? "native map hand prop" : "native flashlight hand prop"; message = "Refreshed hand prop; the draft and saved settings are unchanged."; return; }
            var player = gameplay.Player;
            InteractableItems? held = player.Get_ActiveTool;
            if (held == null || HeldGripOverrides.IdentityOf(held) != selected.Entry.Definition.Path || HeldGripOverrides.VariantOf(held) != selected.Variant)
            { message = "Hold/equip this exact catalog item and variant first."; return; }
            var localPosition = player.transform.InverseTransformPoint(held.transform.position);
            var localRotation = Quaternion.Inverse(player.transform.rotation) * held.transform.rotation;
            ReplaceItem(held.gameObject);
            itemViewPosition = localPosition; itemViewRotation = localRotation; PlaceViewPose();
            poseSource = "captured actual held pose and visible meshes"; message = "Captured preview only; saved grip is unchanged.";
        }
        private void Save()
        {
            if (draft == null) return;
            if (SeatMode) { SaveSeat(); return; }
            try { overrides.Save(draft); initial = draft.Copy(); draftChanged = false; message = selected?.HandProp == true ? "Saved local remote/third-person hand-prop placement and grips." : "Saved local grip. Applies to replicated held copies of this prefab."; }
            catch (Exception ex) { message = "Save failed; active settings unchanged: " + ex.Message; }
        }
        private void Reset()
        {
            if (selected == null) return;
            if (SeatMode) { ResetSeat(); return; }
            try
            {
                overrides.Remove(selected.Entry.Definition.Path, selected.Variant);
                bool bundled = overrides.TryGet(selected.Entry.Definition.Path, selected.Variant, selected.Entry.Definition.ShapeHash, out var restored);
                draft = bundled ? restored : null;
                if (!bundled) SeedAutomatic();
                initial = draft?.Copy(); draftChanged = false; undo.Clear(); redo.Clear(); numericText.Clear(); editRangeReady = false;
                historyAction = true; pickArmed = false;
                message = bundled ? "Local override removed; built-in calibration restored." : "Saved override removed; gameplay uses automatic presentation.";
            }
            catch (Exception ex) { message = "Reset failed: " + ex.Message; }
        }
        private void CreateStage()
        {
            if (previewRoot != null) return;
            layer = Enumerable.Range(8, 24).Reverse().FirstOrDefault(i => string.IsNullOrEmpty(LayerMask.LayerToName(i)));
            if (layer == 0) throw new InvalidOperationException("No unused preview layer.");
            previewRoot = new GameObject("MonMP Hold Calibration Preview");
            // Outside ordinary cameras' far planes, with no colliders or audio.
            previewRoot.transform.position = new Vector3(10000, 10000, 10000);
            if (SeatMode)
            {
                seatBodyRoot = new GameObject("MonMP seat avatar frame"); seatBodyRoot.transform.SetParent(previewRoot.transform, false);
            }
            body = StockAvatarBody.TryCreate(seatBodyRoot != null ? seatBodyRoot.transform : previewRoot.transform, new Color(0.3f, 0.45f, 0.7f), out string reason);
            if (body == null) throw new InvalidOperationException(reason);
            SetLayers(previewRoot);
            cameraObject = new GameObject("MonMP Hold Preview Camera"); camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.09f, 0.1f, 0.13f);
            camera.cullingMask = 1 << layer; camera.nearClipPlane = 0.03f; camera.farClipPlane = 30; camera.fieldOfView = 38; camera.aspect = 1;
            texture = new RenderTexture(640, 640, 24, RenderTextureFormat.ARGB32); texture.Create(); camera.targetTexture = texture;
            lightObject = new GameObject("MonMP Hold Preview Light"); var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = 1.2f; light.shadows = LightShadows.None; light.cullingMask = 1 << layer;
            lightObject.transform.rotation = Quaternion.Euler(30, -20, 0);
            marker = GameObject.CreatePrimitive(PrimitiveType.Sphere); marker.name = "MonMP palm target"; marker.layer = layer;
            marker.transform.SetParent(previewRoot.transform, false); marker.transform.localScale = Vector3.one * 0.025f;
            UnityEngine.Object.DestroyImmediate(marker.GetComponent<Collider>());
            markerMaterial = new Material(Shader.Find("Unlit/Color") ?? Shader.Find("Standard")) { color = Color.green };
            marker.GetComponent<Renderer>().sharedMaterial = markerMaterial;
            leftMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere); leftMarker.name = "MonMP left palm target"; leftMarker.layer = layer;
            leftMarker.transform.SetParent(previewRoot.transform, false); leftMarker.transform.localScale = Vector3.one * 0.025f;
            UnityEngine.Object.DestroyImmediate(leftMarker.GetComponent<Collider>());
            leftMarkerMaterial = new Material(markerMaterial) { color = Color.cyan };
            leftMarker.GetComponent<Renderer>().sharedMaterial = leftMarkerMaterial; leftMarker.SetActive(false);
            if (SeatMode) CreateSeatFootMarkers();
            framePending = true;
        }
        private void ReplaceItem(GameObject source, bool equippedPreview = false)
        {
            DestroyItem();
            if (previewRoot == null) throw new InvalidOperationException("Preview stage unavailable.");
            itemCopy = new GameObject("MonMP renderer-only item"); itemCopy.layer = layer; itemCopy.transform.SetParent(previewRoot.transform, false);
            itemSource = source;
            retainedSource = source;
            retainedEquippedPreview = equippedPreview;
            var tool = source.GetComponent<InteractableTools>();
            // Mirror SharedItems.Capture: these tools shrink only for the first-person view.
            bool shrunkEquip = tool != null && tool.isEquiped && (tool is Tool_Chainsaw || tool is Tool_DirtShovel || tool is Tool_MapTorch ||
                tool is Tool_HarvestBasket || tool is Tool_TubingRoll || tool is Tool_AdjustableSpanner);
            itemCopy.transform.localScale = shrunkEquip ? Vector3.one : source.scene.IsValid() && tool?.isEquiped == true && source.transform.parent != null ?
                source.transform.localScale : source.scene.IsValid() ? source.transform.lossyScale : source.transform.localScale;
            CopyVisualTree(source.transform, itemCopy.transform);
            if (selected?.HandProp == true)
            { RemoteMapCalibration.RemoveOverlay(itemCopy); handPropPreview = RemoteHandProps.DescribeProp(itemCopy, RemoteHandProps.BeamOf(source)); }
            picking.Rebuild(itemCopy);
        }
        private void CopyVisualTree(Transform source, Transform target)
        {
            HoldPreviewVisualCopy.Copy(source, target, layer, ownedMeshes, retainedEquippedPreview);
        }
        private void SetLayers(GameObject root) { foreach (var transform in root.GetComponentsInChildren<Transform>(true)) transform.gameObject.layer = layer; }
        private void DestroyItem()
        {
            handPropPreview = null;
            picking.Clear();
            if (itemCopy != null) { itemCopy.SetActive(false); UnityEngine.Object.Destroy(itemCopy); itemCopy = null; }
            DestroySeatMaterials();
            itemSource = null;
            seatHandFrame = null; seatVehicleCopy = null;
            foreach (var mesh in ownedMeshes) UnityEngine.Object.Destroy(mesh); ownedMeshes.Clear();
        }
        private void DestroyStage()
        {
            EndGesture();
            if (editorHotControl != 0 && GUIUtility.hotControl == editorHotControl) GUIUtility.hotControl = 0;
            editorHotControl = 0;
            DestroyItem(); body?.Destroy(); body = null;
            DestroySeatFootMarkers();
            if (previewRoot != null) { previewRoot.SetActive(false); UnityEngine.Object.Destroy(previewRoot); previewRoot = null; }
            seatBodyRoot = null;
            if (cameraObject != null) UnityEngine.Object.Destroy(cameraObject); cameraObject = null; camera = null;
            if (lightObject != null) UnityEngine.Object.Destroy(lightObject); lightObject = null;
            if (texture != null) { texture.Release(); UnityEngine.Object.Destroy(texture); texture = null; }
            if (markerMaterial != null) UnityEngine.Object.Destroy(markerMaterial); markerMaterial = null; marker = null;
            if (leftMarkerMaterial != null) UnityEngine.Object.Destroy(leftMarkerMaterial); leftMarkerMaterial = null; leftMarker = null;
        }
        // Explicit diagnostic entry point. The persistence test uses only a fresh test-owned
        // subdirectory; it never saves/removes from the user's active override store.
        internal string TestSmoke(string directory)
        {
            if (gestureStart != null || editorHotControl != 0 && GUIUtility.hotControl == editorHotControl)
                return "MONMP_HELD_EDITOR_SMOKE_SKIPPED active editor gesture";
            string ownedDirectory = Path.Combine(directory, "held-editor-smoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ownedDirectory);
            var report = new StringBuilder("MONMP_HELD_EDITOR_SMOKE\n");
            bool wasVisible = Visible;
            Choice? previousSelection = selected;
            var previousDraft = draft?.Copy(); var previousInitial = initial?.Copy(); bool previousChanged = draftChanged;
            var previousUndo = undo.Select(g => g.Copy()).ToList(); var previousRedo = redo.Select(g => g.Copy()).ToList();
            var previousNumeric = new Dictionary<string, string>(numericText); var previousPending = pendingSelection;
            var previousTarget = editTarget; bool previousRotate = rotateEdit, previousPick = pickArmed, previousMarkers = showMarkers;
            float previousMoveRange = moveRange, previousTurnRange = turnRange;
            bool previousSnap = snapEdits, previousRangeReady = editRangeReady, previousCopyChooser = copyChooser;
            Vector3 previousRangePosition = editRangePosition, previousRangeEuler = editRangeEuler;
            var previousCopySources = copySources.Select(g => g.Copy()).ToList();
            int previousAxis = editAxis; bool previousFramePending = framePending, previousHistoryAction = historyAction;
            var previousPivot = cameraPivot; var previousBounds = cameraBounds; float previousFit = cameraFit;
            string previousMessage = message;
            float previousPitch = pitch, previousCrouch = crouch;
            float previousDistance = distance, previousOrbit = orbit, previousElevation = elevation;
            var previousItemSource = itemSource;
            var previousRetainedSource = retainedSource;
            bool previousRetainedEquippedPreview = retainedEquippedPreview;
            Vector3 previousItemPosition = itemViewPosition; Quaternion previousItemRotation = itemViewRotation;
            string previousSource = poseSource;
            try
            {
                if (previewRoot == null) CreateStage();
                var samples = new List<Choice>();
                foreach (var type in new[] { typeof(Tool_AdjustableSpanner), typeof(Tool_WateringCan), typeof(Kaligaz_FuelGun), typeof(Tool_FishingRod) })
                {
                    var choice = choices.FirstOrDefault(c => c.Entry.Type == type);
                    if (choice == null) throw new InvalidOperationException("Diagnostic sample missing: " + type.Name);
                    samples.Add(choice);
                }
                InteractableItems? current = gameplay.Player.Get_ActiveTool;
                if (current != null)
                {
                    var choice = choices.FirstOrDefault(c => c.Entry.Definition.Path == HeldGripOverrides.IdentityOf(current) && c.Variant == HeldGripOverrides.VariantOf(current));
                    if (choice != null && !samples.Contains(choice)) samples.Add(choice);
                }
                Visible = true;
                int index = 0;
                foreach (var choice in samples)
                {
                    pitch = 0; crouch = 0; distance = 1.1f; orbit = 20; elevation = 12; Select(choice);
                    if (current != null && HeldGripOverrides.IdentityOf(current) == choice.Entry.Definition.Path && HeldGripOverrides.VariantOf(current) == choice.Variant) CaptureHeld();
                    if (itemCopy == null || draft == null || body == null || camera == null) throw new InvalidOperationException("Sample has no preview: " + message);
                    foreach (var component in itemCopy.GetComponentsInChildren<Component>(true))
                        if (!(component is Transform) && !(component is MeshFilter) && !(component is MeshRenderer))
                            throw new InvalidOperationException("Unexpected item-preview component: " + component.GetType().Name);
                    RenderPreview();
                    if (camera == null) throw new InvalidOperationException("Sample render failed: " + message);
                    var sourceRenderers = (itemSource ?? choice.Entry.Asset).GetComponentsInChildren<Renderer>(true);
                    var copiedRenderers = itemCopy.GetComponentsInChildren<Renderer>(false).Where(r => r.enabled && !r.forceRenderingOff).ToArray();
                    report.AppendLine("VISUAL_STATE " + choice.Label + "; sourceRenderers=" + sourceRenderers.Length + "; sourceEnabled=" + sourceRenderers.Count(r => r.enabled) +
                        "; sourceForceOff=" + sourceRenderers.Count(r => r.forceRenderingOff) + "; copiedVisible=" + copiedRenderers.Length + "; scale=" + itemCopy.transform.lossyScale);
                    if (copiedRenderers.Length == 0) throw new InvalidOperationException("Canonical item has no visible meshes; capture its actual held form: " + choice.Label);
                    foreach (var renderer in copiedRenderers)
                    {
                        var mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                        report.AppendLine("MESH_SCALE " + choice.Label + "; node=" + renderer.name + "; localScale=" + renderer.transform.localScale.ToString("G9") +
                            "; worldScale=" + renderer.transform.lossyScale.ToString("G9") + "; meshSize=" + (mesh == null ? "none" : mesh.bounds.size.ToString("G9")) +
                            "; worldSize=" + renderer.bounds.size.ToString("G9"));
                    }
                    if (choice.Entry.Type == typeof(Tool_FishingRod))
                    {
                        foreach (var skin in sourceRenderers.OfType<SkinnedMeshRenderer>())
                        {
                            report.AppendLine("ROD_SOURCE_SKIN node=" + skin.name + "; rendererScale=" + skin.transform.lossyScale.ToString("G9") + "; authoredMeshSize=" + skin.sharedMesh.bounds.size.ToString("G9"));
                            foreach (bool compensate in new[] { false, true })
                            {
                                var measured = new Mesh();
                                try
                                {
                                    skin.BakeMesh(measured, compensate);
                                    report.AppendLine("ROD_NATIVE_BAKE useScale=" + compensate + "; meshSize=" + measured.bounds.size.ToString("G9"));
                                    if (compensate && !skin.gameObject.scene.IsValid())
                                    {
                                        var longest = measured.bounds.size; int axis = longest.x >= longest.y && longest.x >= longest.z ? 0 : longest.y >= longest.z ? 1 : 2;
                                        var span = Vector3.zero; span[axis] = longest[axis];
                                        float expectedSpan = skin.transform.TransformVector(span).magnitude;
                                        var copy = copiedRenderers.Single(r => r.name == "Baked skin");
                                        var copiedMesh = copy.GetComponent<MeshFilter>().sharedMesh;
                                        span[axis] = copiedMesh.bounds.size[axis];
                                        float actualSpan = copy.transform.TransformVector(span).magnitude;
                                        float ratio = actualSpan / expectedSpan;
                                        report.AppendLine("ROD_SCALE expectedMetres=" + expectedSpan.ToString("G9", CultureInfo.InvariantCulture) +
                                            "; previewMetres=" + actualSpan.ToString("G9", CultureInfo.InvariantCulture) + "; ratio=" + ratio.ToString("G9", CultureInfo.InvariantCulture));
                                        if (float.IsNaN(ratio) || float.IsInfinity(ratio) || Mathf.Abs(ratio - 1) > 0.02f)
                                            throw new InvalidOperationException("Fishing rod preview does not match native compensated mesh scale.");
                                        var rodStore = new HeldGripOverrides(Path.Combine(ownedDirectory, "rod-visual-scale.json"));
                                        rodStore.Save(new HeldGripOverrides.Grip { Prefab = choice.Entry.Definition.Path, Variant = choice.Variant,
                                            Shape = choice.Entry.Definition.ShapeHash, ItemX = 0.1f, ItemYaw = 20 });
                                        var nativeRod = choice.Entry.Asset.GetComponent<InteractableItems>();
                                        var sourcePosition = nativeRod.transform.position; var sourceRotation = nativeRod.transform.rotation; var sourceScale = nativeRod.transform.localScale;
                                        var sourceForceFlags = sourceRenderers.Select(r => r.forceRenderingOff).ToArray();
                                        using (var rodVisual = new RemoteHeldVisual(rodStore))
                                        {
                                            if (!rodVisual.TryPresent(nativeRod, choice.Entry.Definition.ShapeHash, Quaternion.identity, out _, out _) || rodVisual.PresentedTransform == null)
                                                throw new InvalidOperationException("Calibrated fishing rod visual was not created.");
                                            var rodMesh = rodVisual.PresentedTransform.GetComponentsInChildren<MeshFilter>(true).Single(m => m.name == "Baked skin");
                                            span[axis] = rodMesh.sharedMesh.bounds.size[axis];
                                            float remoteSpan = rodMesh.transform.TransformVector(span).magnitude;
                                            float remoteRatio = remoteSpan / expectedSpan;
                                            report.AppendLine("ROD_REMOTE_SCALE metres=" + remoteSpan.ToString("G9", CultureInfo.InvariantCulture) + "; ratio=" + remoteRatio.ToString("G9", CultureInfo.InvariantCulture));
                                            if (float.IsNaN(remoteRatio) || float.IsInfinity(remoteRatio) || Mathf.Abs(remoteRatio - 1) > 0.02f)
                                                throw new InvalidOperationException("Calibrated fishing rod copy does not match native compensated mesh scale.");
                                        }
                                        if (nativeRod.transform.position != sourcePosition || nativeRod.transform.rotation != sourceRotation || nativeRod.transform.localScale != sourceScale ||
                                            sourceRenderers.Where((r, i) => r.forceRenderingOff != sourceForceFlags[i]).Any())
                                            throw new InvalidOperationException("Calibrated fishing rod copy did not restore the original native state.");
                                    }
                                }
                                finally { UnityEngine.Object.DestroyImmediate(measured); }
                            }
                        }
                    }
                    var framing = VisibleBounds(previewRoot!);
                    for (int cornerIndex = 0; cornerIndex < 8; cornerIndex++)
                    {
                        var corner = framing.center + Vector3.Scale(framing.extents, new Vector3((cornerIndex & 1) == 0 ? -1 : 1, (cornerIndex & 2) == 0 ? -1 : 1, (cornerIndex & 4) == 0 ? -1 : 1));
                        var viewport = camera.WorldToViewportPoint(corner);
                        if (viewport.x < 0 || viewport.x > 1 || viewport.y < 0 || viewport.y > 1 || viewport.z < camera.nearClipPlane || viewport.z > camera.farClipPlane)
                            throw new InvalidOperationException("Auto-fit clipped preview bounds: " + viewport);
                    }
                    string bitmap = Path.Combine(ownedDirectory, "preview-" + index++ + ".bmp");
                    AvatarPreviewCapture.SaveView(camera, bitmap);
                    if (new FileInfo(bitmap).Length < 100) throw new InvalidDataException("Empty preview output.");
                    report.AppendLine("PREVIEW_PASS " + choice.Label + " => " + bitmap);
                    var oldCameraPosition = camera.transform.position; var oldCameraRotation = camera.transform.rotation;
                    draft.ItemX += 0.01f; RenderPreview();
                    if (camera == null || camera.transform.position != oldCameraPosition || Quaternion.Angle(camera.transform.rotation, oldCameraRotation) > 0.001f)
                        throw new InvalidOperationException("Editing an item unexpectedly reframed the camera.");
                    draft.ItemX -= 0.01f;
                    report.AppendLine("STABLE_CAMERA_PASS " + choice.Label);
                }
                if (draft == null) throw new InvalidOperationException("Missing persistence sample.");
                string testPath = Path.Combine(ownedDirectory, "test-overrides.json");
                var testStore = new HeldGripOverrides(testPath);
                var sample = draft.Copy(); sample.X = 0.0123f; testStore.Save(sample);
                var reloaded = new HeldGripOverrides(testPath);
                if (!reloaded.TryGet(sample.Prefab, sample.Variant, sample.Shape, out var loaded) || loaded.X != sample.X)
                    throw new InvalidDataException("Persistence round-trip mismatch.");
                if (reloaded.TryGet(sample.Prefab, sample.Variant, new string('0', 64), out _))
                    throw new InvalidDataException("Shape mismatch accepted.");
                var bad = sample.Copy(); bad.X = float.NaN;
                bool rejected = false;
                try { reloaded.Save(bad); } catch (InvalidDataException) { rejected = true; }
                if (!rejected || reloaded.Count != 1) throw new InvalidDataException("Invalid value changed settings.");
                reloaded.Remove(sample.Prefab, sample.Variant);
                if (!File.Exists(testPath + ".bak") || new HeldGripOverrides(testPath).Count != 0)
                    throw new InvalidDataException("Remove or backup failed.");
                string badPath = Path.Combine(ownedDirectory, "invalid-overrides.json"); File.WriteAllText(badPath, "{invalid");
                var invalid = new HeldGripOverrides(badPath);
                if (invalid.Count != 0 || !invalid.LoadStatus.StartsWith("Overrides were not loaded:", StringComparison.Ordinal))
                    throw new InvalidDataException("Invalid JSON did not fail closed.");
                report.AppendLine("PERSISTENCE_PASS round-trip, shape gating, non-finite rejection, remove, rollback backup, invalid JSON");
                // Exercise the presentation copy against an actual canonical native component,
                // without instantiating any native scripts. The temporary store alone is edited.
                var nativeSample = samples[1].Entry.Asset.GetComponent<InteractableItems>();
                var nativeRoot = nativeSample.transform;
                var oldPosition = nativeRoot.position; var oldRotation = nativeRoot.rotation; var oldScale = nativeRoot.localScale;
                var nativeRenderers = nativeSample.GetComponentsInChildren<Renderer>(true);
                var oldForces = nativeRenderers.Select(r => r.forceRenderingOff).ToArray();
                var oldEnabled = nativeRenderers.Select(r => r.enabled).ToArray();
                sample = new HeldGripOverrides.Grip { Prefab = samples[1].Entry.Definition.Path, Variant = samples[1].Variant,
                    Shape = samples[1].Entry.Definition.ShapeHash, ItemX = 0.2f, ItemY = -0.4f, ItemZ = 0.1f, ItemYaw = 20 };
                testStore.Save(sample);
                using (var ghost = new RemoteHeldVisual(testStore))
                {
                    var bodyRotation = Quaternion.Euler(0, 90, 0);
                    if (!ghost.TryPresent(nativeSample, sample.Shape, bodyRotation, out var visualPosition, out var visualRotation) || ghost.PresentedTransform == null)
                        throw new InvalidOperationException("Visual item override did not create a presentation.");
                    if ((visualPosition - (oldPosition + bodyRotation * sample.ItemPosition)).sqrMagnitude > 1e-6f ||
                        Quaternion.Angle(visualRotation, oldRotation * Quaternion.Euler(sample.ItemEuler)) > 0.01f)
                        throw new InvalidOperationException("Visual item offset used the wrong frame.");
                    if (nativeRoot.position != oldPosition || nativeRoot.rotation != oldRotation || nativeRoot.localScale != oldScale)
                        throw new InvalidOperationException("Visual item override moved the native root.");
                    foreach (var component in ghost.PresentedTransform.GetComponentsInChildren<Component>(true))
                        if (!(component is Transform) && !(component is MeshFilter) && !(component is MeshRenderer))
                            throw new InvalidOperationException("Unsafe visual item component: " + component.GetType().Name);
                    for (int rendererIndex = 0; rendererIndex < nativeRenderers.Length; rendererIndex++)
                        if (nativeRenderers[rendererIndex].enabled != oldEnabled[rendererIndex]) throw new InvalidOperationException("Native renderer enabled flag changed.");
                    ghost.Reset();
                }
                for (int rendererIndex = 0; rendererIndex < nativeRenderers.Length; rendererIndex++)
                    if (nativeRenderers[rendererIndex].forceRenderingOff != oldForces[rendererIndex]) throw new InvalidOperationException("Native forceRenderingOff was not restored.");
                report.AppendLine("VISUAL_OFFSET_PASS body translation frame, item rotation frame, native root/renderer enabled preserved, safe copy, forceRenderingOff restored");
                report.AppendLine("CATALOG_CHOICES=" + choices.Count);
                report.AppendLine("MONMP_HELD_EDITOR_SMOKE_PASS");
            }
            catch (Exception ex) { report.AppendLine("MONMP_HELD_EDITOR_SMOKE_FAIL " + ex); }
            finally
            {
                Visible = wasVisible; pitch = previousPitch; crouch = previousCrouch; distance = previousDistance; orbit = previousOrbit; elevation = previousElevation;
                if (wasVisible && previousSelection != null)
                {
                    Select(previousSelection); if (previousItemSource != null) ReplaceItem(previousItemSource, previousRetainedEquippedPreview);
                    draft = previousDraft; initial = previousInitial; draftChanged = previousChanged;
                    itemViewPosition = previousItemPosition; itemViewRotation = previousItemRotation; poseSource = previousSource;
                }
                else { selected = previousSelection; draft = previousDraft; initial = previousInitial; draftChanged = previousChanged; DestroyStage(); }
                undo.Clear(); undo.AddRange(previousUndo); redo.Clear(); redo.AddRange(previousRedo);
                numericText.Clear(); foreach (var pair in previousNumeric) numericText.Add(pair.Key, pair.Value);
                pendingSelection = previousPending; editTarget = previousTarget; rotateEdit = previousRotate; pickArmed = previousPick; showMarkers = previousMarkers;
                moveRange = previousMoveRange; turnRange = previousTurnRange; snapEdits = previousSnap; editRangeReady = previousRangeReady;
                editRangePosition = previousRangePosition; editRangeEuler = previousRangeEuler; copyChooser = previousCopyChooser;
                copySources.Clear(); copySources.AddRange(previousCopySources);
                editAxis = previousAxis; framePending = previousFramePending; historyAction = previousHistoryAction;
                cameraPivot = previousPivot; cameraBounds = previousBounds; cameraFit = previousFit; message = previousMessage;
                retainedSource = previousRetainedSource;
                retainedEquippedPreview = previousRetainedEquippedPreview;
                File.WriteAllText(Path.Combine(ownedDirectory, "result.txt"), report.ToString());
            }
            return report.ToString();
        }
        public void Dispose() => Hide();
    }
}
