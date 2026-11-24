using System;
using System.Collections.Generic;
using System.Globalization;
using MonMP.Core.Protocol;
using MonMP.Plugin.Replication;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace MonMP.Plugin.UI
{
    // Development-only editor. Drafts are passed only to its private preview rigs; Save is
    // the sole operation that publishes an edited pose to normal local presentation.
    internal sealed partial class EmotePoseEditor : IDisposable
    {
        private readonly EmotePoseOverrides overrides;
        private readonly Gameplay gameplay;
        private EmotePoseOverrides.ViewKind view = EmotePoseOverrides.ViewKind.FirstPerson;
        private PlayerEmoteKind kind = PlayerEmoteKind.Wave;
        private StockAvatarBody.Mode previewStance = StockAvatarBody.Mode.Stand;
        private EmotePoseOverrides.Pose draft, initial;
        private readonly List<EmotePoseOverrides.Pose> undo = new List<EmotePoseOverrides.Pose>();
        private readonly List<EmotePoseOverrides.Pose> redo = new List<EmotePoseOverrides.Pose>();
        private readonly EmotePoseEditorNumericInput numericInput = new EmotePoseEditorNumericInput();
        private string activeTextKey = "";
        private Vector2 controlsScroll;
        private string message = "", bone = "";
        private bool playing = true, loop = true, disposed, restoreCursor;
        private bool priorCursorVisible, dirty, rotate, snap, numericGesture;
        private CursorLockMode priorCursorLock;
        private float elapsed, lastTick, positionStep = .01f, angleStep = 5f;
        private int sceneHandle, ownedHotControl, selectionRevision;
        private EmotePoseOverrides.Pose? gestureStart;
        private const int HistoryLimit = 64;
        internal bool Visible { get; private set; }

        internal EmotePoseEditor(EmotePoseOverrides overrides, Gameplay gameplay)
        {
            this.overrides = overrides;
            this.gameplay = gameplay;
            draft = overrides.GetCopy(view, kind);
            initial = draft.Copy();
        }

        internal void Show()
        {
            if (disposed || Visible) return;
            if (gameplay == null || gameplay.Player == null) { message = "Load a gameplay scene first."; return; }
            priorCursorLock = Cursor.lockState; priorCursorVisible = Cursor.visible; restoreCursor = true;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            sceneHandle = SceneManager.GetActiveScene().handle;
            lastTick = Time.unscaledTime; Visible = true;
            RebuildPreview();
        }

        internal void Hide()
        {
            EndEdit();
            if (ownedHotControl != 0 && GUIUtility.hotControl == ownedHotControl) GUIUtility.hotControl = 0;
            ownedHotControl = 0; GUIUtility.keyboardControl = 0;
            Visible = false; DestroyPreview();
            if (restoreCursor)
            {
                Cursor.lockState = priorCursorLock; Cursor.visible = priorCursorVisible; restoreCursor = false;
            }
        }

        internal void Tick()
        {
            if (!Visible) return;
            if (gameplay == null || gameplay.Player == null || sceneHandle != SceneManager.GetActiveScene().handle)
            { Hide(); return; }
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            if (Keyboard.current?.escapeKey.wasPressedThisFrame == true) { Hide(); return; }
            float now = Time.unscaledTime, delta = Mathf.Clamp(now - lastTick, 0f, .1f); lastTick = now;
            if (playing)
            {
                elapsed += delta;
                float duration = StockAvatarBody.GestureDuration(kind);
                if (elapsed >= duration)
                {
                    if (loop) elapsed = Mathf.Repeat(elapsed, duration);
                    else { elapsed = duration; playing = false; }
                }
            }
            RenderPreview();
        }

        internal void Draw()
        {
            if (!Visible) return;
            var oldMatrix = GUI.matrix;
            bool oldWrap = GUI.skin.label.wordWrap;
            GUI.skin.label.wordWrap = true;
            float scale = Mathf.Min(1f, Mathf.Min(Screen.width / 1160f, Screen.height / 810f));
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one * scale);
            var before = draft.Copy();
            int beforeSelection = selectionRevision;
            bool historyAction = false;
            try
            {
                var panel = new Rect((Screen.width / scale - 1140f) * .5f, (Screen.height / scale - 790f) * .5f, 1140f, 790f);
                GUI.Box(panel, "Emote pose calibration — separate first-person and remote poses");
                GUILayout.BeginArea(new Rect(panel.x + 12, panel.y + 28, panel.width - 24, panel.height - 40));
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical(GUILayout.Width(192));
                GUILayout.Label("View / local calibration");
                DrawSelection(EmotePoseOverrides.ViewKind.FirstPerson, "First-person arm");
                DrawSelection(EmotePoseOverrides.ViewKind.Remote, "Remote / self-view body");
                GUILayout.Space(12); GUILayout.Label("Custom gesture");
                DrawGesture(PlayerEmoteKind.Wave, "Wave");
                DrawGesture(PlayerEmoteKind.Point, "Point");
                DrawGesture(PlayerEmoteKind.ThumbsUp, "Thumbs up");
                GUILayout.Space(12);
                GUILayout.Label("Edits affect this isolated preview. Save applies this view and gesture locally.");
                GUILayout.Label("Other players use their own local pose settings.");
                GUILayout.FlexibleSpace();
                GUILayout.Label("Saved to:\n" + overrides.Path);
                GUILayout.EndVertical();
                GUILayout.BeginVertical(GUILayout.Width(480));
                var viewport = GUILayoutUtility.GetRect(480, 450, GUILayout.Width(480), GUILayout.Height(450));
                GUI.Box(viewport, GUIContent.none);
                if (previewTexture != null)
                    GUI.DrawTexture(viewport, previewTexture, ScaleMode.ScaleToFit, false);
                else GUI.Label(viewport, message);
                DrawViewport(ImageRect(viewport));
                GUILayout.Label(rotate ? "Drag a colored ring to rotate the selected palm/bone." : "Drag an axis or center square to move the wrist target.");
                GUILayout.Label(view == EmotePoseOverrides.ViewKind.Remote
                    ? "Right: orbit • Middle / Alt-left: pan • Wheel: zoom • Shift: fine"
                    : previewLens + ". Shift: fine");
                if (view == EmotePoseOverrides.ViewKind.Remote)
                {
                    GUILayout.Label("Preview stance (same saved Remote pose)");
                    GUILayout.BeginHorizontal();
                    DrawStance(StockAvatarBody.Mode.Stand, "Standing");
                    DrawStance(StockAvatarBody.Mode.Sit, "Passenger");
                    DrawStance(StockAvatarBody.Mode.Drive, "Driver");
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Fit body")) FitRemote(false);
                    if (GUILayout.Button("Focus hand")) FitRemote(true);
                    if (GUILayout.Button("Front")) { orbit = 0; elevation = 5; FitRemote(false); }
                    if (GUILayout.Button("Side")) { orbit = 90; elevation = 5; FitRemote(false); }
                    GUILayout.EndHorizontal();
                }
                DrawPlayback();
                if (GUILayout.Button("Refresh preview from current hands / shirt")) RebuildPreview();
                GUILayout.Label(message);
                GUILayout.EndVertical();
                GUILayout.BeginVertical(GUILayout.Width(414));
                controlsScroll = GUILayout.BeginScrollView(controlsScroll, GUILayout.Height(580));
                DrawControls();
                GUILayout.EndScrollView();
                GUILayout.BeginHorizontal();
                bool enabled = GUI.enabled;
                GUI.enabled = enabled && (undo.Count > 0 || gestureStart != null && !Same(gestureStart, draft));
                if (GUILayout.Button("Undo")) { RestoreHistory(undo, redo); historyAction = true; }
                GUI.enabled = enabled && redo.Count > 0 && (gestureStart == null || Same(gestureStart, draft));
                if (GUILayout.Button("Redo")) { RestoreHistory(redo, undo); historyAction = true; }
                GUI.enabled = enabled;
                if (GUILayout.Button("Revert unsaved"))
                { EndEdit(); draft = initial.Copy(); numericInput.Clear(); message = "Unsaved edits reverted."; }
                GUILayout.EndHorizontal();
                if (GUILayout.Button("SAVE THIS VIEW / GESTURE" + (numericInput.HasErrors ? " • invalid fields" : dirty ? " • unsaved" : ""), GUILayout.Height(32))) Save();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Default in draft")) { EndEdit(); draft = new EmotePoseOverrides.Pose(); numericInput.Clear(); message = "Default pose is in the draft; Save to apply."; }
                if (GUILayout.Button("Remove saved override"))
                {
                    if (overrides.Remove(view, kind, out message))
                    { EndEdit(); draft = overrides.GetCopy(view, kind); initial = draft.Copy(); undo.Clear(); redo.Clear(); numericInput.Clear(); historyAction = true; }
                }
                GUILayout.EndHorizontal();
                if (GUILayout.Button("Close (Esc) • keep current draft", GUILayout.Height(28))) Hide();
                GUILayout.EndVertical();
                GUILayout.EndHorizontal(); GUILayout.EndArea();
            }
            finally
            {
                bool enter = Event.current.rawType == EventType.KeyDown &&
                    (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
                if (activeTextKey.Length > 0 && (GUI.GetNameOfFocusedControl() != NumericControlName(activeTextKey) || enter))
                { EndEdit(); historyAction = true; if (enter) GUI.FocusControl(""); }
                if (numericGesture && GUIUtility.hotControl == 0) { EndEdit(); historyAction = true; }
                if (beforeSelection == selectionRevision && !Same(before, draft) && gestureStart == null && !historyAction) Record(before);
                dirty = !Same(initial, draft);
                ownedHotControl = GUIUtility.hotControl;
                GUI.matrix = oldMatrix; GUI.skin.label.wordWrap = oldWrap;
            }
        }

        private void DrawSelection(EmotePoseOverrides.ViewKind candidate, string label)
        {
            if (GUILayout.Toggle(view == candidate, label, GUI.skin.button) && view != candidate) RequestSelection(candidate, kind);
        }
        private void DrawGesture(PlayerEmoteKind candidate, string label)
        {
            if (GUILayout.Toggle(kind == candidate, label, GUI.skin.button) && kind != candidate) RequestSelection(view, candidate);
        }
        private void DrawStance(StockAvatarBody.Mode candidate, string label)
        {
            if (GUILayout.Toggle(previewStance == candidate, label, GUI.skin.button) && previewStance != candidate)
                SetPreviewStance(candidate);
        }
        internal void SetPreviewStance(StockAvatarBody.Mode stance)
        {
            if (stance != StockAvatarBody.Mode.Stand && stance != StockAvatarBody.Mode.Sit && stance != StockAvatarBody.Mode.Drive)
                throw new ArgumentOutOfRangeException(nameof(stance));
            previewStance = stance; RenderPreview();
        }
        private void RequestSelection(EmotePoseOverrides.ViewKind nextView, PlayerEmoteKind nextKind)
        {
            if (!TryCommitNumericInput("switch views or gestures")) return;
            EndEdit();
            if (!Same(initial, draft)) { message = "Save or revert the current draft before switching views or gestures."; return; }
            SetPreviewSelection(nextView, nextKind);
        }
        internal void SetPreviewSelection(EmotePoseOverrides.ViewKind selectedView, PlayerEmoteKind selectedKind)
        {
            if (StockAvatarBody.GestureDuration(selectedKind) <= 0f) throw new ArgumentOutOfRangeException(nameof(selectedKind));
            EndEdit(); view = selectedView; kind = selectedKind; selectionRevision++;
            draft = overrides.GetCopy(view, kind); initial = draft.Copy(); dirty = false;
            undo.Clear(); redo.Clear(); numericInput.Clear(); bone = ""; elapsed = 0f;
            if (Visible) RebuildPreview();
        }
        internal void SetPreviewSample(EmotePoseOverrides.Pose sample, float seconds)
        {
            EmotePoseOverrides.Validate(view, kind, sample);
            if (float.IsNaN(seconds) || float.IsInfinity(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
            EndEdit(); draft = sample.Copy(); elapsed = Mathf.Clamp(seconds, 0f, StockAvatarBody.GestureDuration(kind));
            playing = false; dirty = !Same(initial, draft); numericInput.Clear(); RenderPreview();
        }
        private void Save()
        {
            if (!TryCommitNumericInput("save")) return;
            EndEdit(); GUI.FocusControl("");
            if (overrides.Save(view, kind, draft, out message)) { initial = draft.Copy(); dirty = false; }
        }
        private bool TryCommitNumericInput(string action)
        {
            if (numericInput.TryCommitAll(out var error)) return true;
            message = "Cannot " + action + ". " + error + " Correct the field or choose Revert unsaved.";
            return false;
        }
        private static string NumericControlName(string key) => "MonMP.EmotePose." + key;
        private void BeginTextEdit(string key)
        {
            if (activeTextKey == key) return;
            EndEdit(); activeTextKey = key; BeginEdit();
        }
        private void DrawPlayback()
        {
            GUILayout.BeginHorizontal();
            playing = GUILayout.Toggle(playing, playing ? "Pause" : "Play", GUI.skin.button, GUILayout.Width(74));
            if (GUILayout.Button("Restart", GUILayout.Width(74))) elapsed = 0f;
            loop = GUILayout.Toggle(loop, "Loop");
            GUILayout.Label(elapsed.ToString("0.00", CultureInfo.InvariantCulture) + " / " + StockAvatarBody.GestureDuration(kind).ToString("0.00", CultureInfo.InvariantCulture) + " s");
            GUILayout.EndHorizontal();
            float next = GUILayout.HorizontalSlider(elapsed, 0f, StockAvatarBody.GestureDuration(kind));
            if (next != elapsed) { elapsed = next; playing = false; RenderPreview(); }
        }
        private void Record(EmotePoseOverrides.Pose before)
        {
            if (Same(before, draft)) return;
            if (undo.Count >= HistoryLimit) undo.RemoveAt(0);
            undo.Add(before.Copy()); redo.Clear(); dirty = !Same(initial, draft);
        }
        private void BeginEdit()
        { if (gestureStart == null) gestureStart = draft.Copy(); }
        private void EndEdit()
        {
            if (activeTextKey.Length > 0) { numericInput.TryCommit(activeTextKey); activeTextKey = ""; }
            if (gestureStart != null) { Record(gestureStart); gestureStart = null; }
            numericGesture = false; dragMode = 0;
            if (ownedHotControl != 0 && GUIUtility.hotControl == ownedHotControl) GUIUtility.hotControl = 0;
        }
        private void RestoreHistory(List<EmotePoseOverrides.Pose> from, List<EmotePoseOverrides.Pose> to)
        {
            EndEdit();
            if (from.Count == 0) return;
            if (to.Count >= HistoryLimit) to.RemoveAt(0);
            to.Add(draft.Copy()); draft = from[from.Count - 1].Copy(); from.RemoveAt(from.Count - 1);
            numericInput.Clear(); dirty = !Same(initial, draft); RenderPreview();
        }
        private static bool Same(EmotePoseOverrides.Pose a, EmotePoseOverrides.Pose b)
        {
            if (a.X != b.X || a.Y != b.Y || a.Z != b.Z || a.Pitch != b.Pitch || a.Yaw != b.Yaw || a.Roll != b.Roll ||
                a.ElbowX != b.ElbowX || a.ElbowY != b.ElbowY || a.ElbowZ != b.ElbowZ || a.WaveScale != b.WaveScale || a.WaveRate != b.WaveRate || a.Bones.Count != b.Bones.Count) return false;
            foreach (var pair in a.Bones)
                if (!b.Bones.TryGetValue(pair.Key, out var other) || pair.Value.X != other.X || pair.Value.Y != other.Y || pair.Value.Z != other.Z) return false;
            return true;
        }
        public void Dispose() { if (disposed) return; Hide(); disposed = true; }
    }
}
