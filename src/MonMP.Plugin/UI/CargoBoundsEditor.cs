#if MONMP_DEVELOPMENT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MonMP.Plugin.Replication;
using Newtonsoft.Json;
using NWH.VehiclePhysics2;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace MonMP.Plugin.UI
{
    // Development-only, in-world editor for the hand-defined cargo freeze boxes of a carrier
    // model. Boxes live in the carrier frame's local space and are drawn on the real vehicle.
    // Save writes the personal file; Copy JSON gives an entry for the bundled defaults.
    internal sealed class CargoBoundsEditor : IDisposable
    {
        private readonly MonMPPlugin plugin;
        private readonly CargoBoundsOverrides store;
        private readonly Gameplay gameplay;
        private readonly List<LineRenderer> lines = new List<LineRenderer>();
        private Transform? frame;
        private string key = "";
        private CargoBoundsOverrides.Entry draft = new CargoBoundsOverrides.Entry();
        private int selected = -1;
        private float step = 0.05f;
        private bool editing = true, inputsHeld, dirty, priorCursorVisible;
        private CursorLockMode priorCursorLock;
        private int sceneHandle;
        private string message = "", check = "";
        private float nextCheck;
        private Vector2 scroll;
        private Material? material;
        private Rect panel = new Rect(20, 80, 430, 640);
        internal bool Visible { get; private set; }

        internal CargoBoundsEditor(MonMPPlugin plugin, CargoBoundsOverrides store, Gameplay gameplay)
        { this.plugin = plugin; this.store = store; this.gameplay = gameplay; }

        internal void Show()
        {
            if (Visible) return;
            if (gameplay == null || gameplay.Player == null) { message = "Load a gameplay scene first."; return; }
            priorCursorLock = Cursor.lockState; priorCursorVisible = Cursor.visible;
            sceneHandle = SceneManager.GetActiveScene().handle;
            Visible = true; editing = true;
            if (frame == null) Select(Candidates().FirstOrDefault().Frame);
        }

        internal void Hide()
        {
            if (!Visible) return;
            Visible = false;
            ReleaseInputs();
            Cursor.lockState = priorCursorLock; Cursor.visible = priorCursorVisible;
            foreach (var line in lines) if (line != null) line.gameObject.SetActive(false);
        }

        public void Dispose()
        {
            Hide();
            foreach (var line in lines) if (line != null) UnityEngine.Object.Destroy(line.gameObject);
            lines.Clear();
            if (material != null) UnityEngine.Object.Destroy(material);
        }

        internal void Tick()
        {
            if (!Visible) return;
            if (gameplay == null || gameplay.Player == null || sceneHandle != SceneManager.GetActiveScene().handle) { Hide(); return; }
            var mouse = Mouse.current;
            if (mouse != null && mouse.middleButton.wasPressedThisFrame) editing = !editing;
            if (editing)
            {
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
                HoldInputs();
            }
            else
            {
                ReleaseInputs();
                Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
            }
            if (frame == null && key.Length > 0) { message = "The selected vehicle is gone."; key = ""; }
            DrawBoxes();
            if (Time.unscaledTime >= nextCheck) { nextCheck = Time.unscaledTime + 0.25f; check = LookCheck(); }
        }

        // The panel's buttons need the mouse, so native look/move is paused while editing.
        private void HoldInputs()
        {
            if (inputsHeld) return;
            var assets = UnityEngine.Object.FindObjectOfType<GameAssets>();
            if (assets == null) return;
            assets.inputs.Player.Disable(); inputsHeld = true;
        }

        private void ReleaseInputs()
        {
            if (!inputsHeld) return;
            inputsHeld = false;
            UnityEngine.Object.FindObjectOfType<GameAssets>()?.inputs.Player.Enable();
        }

        private IEnumerable<(Transform Frame, float Distance)> Candidates()
        {
            var eye = gameplay.Player.mainCamera != null ? gameplay.Player.mainCamera.transform.position : gameplay.Player.transform.position;
            IEnumerable<Transform> frames = plugin.World?.Items?.Carriers is ItemCarriers carriers
                ? carriers.Frames().Select(c => c.Frame)
                : UnityEngine.Object.FindObjectsOfType<VehicleController>().Select(v => v.transform);
            return frames.Where(f => f != null && f.gameObject.activeInHierarchy && f.GetComponent<Wheelbarrow>() == null)
                .Select(f => (Frame: f, Distance: Vector3.Distance(eye, f.position)))
                .Where(c => c.Distance < 40f).OrderBy(c => c.Distance).Take(8);
        }

        private void Select(Transform? next)
        {
            frame = next; selected = -1; dirty = false;
            if (frame == null) { key = ""; draft = new CargoBoundsOverrides.Entry(); message = "No vehicle within 40 m."; return; }
            key = CargoBoundsOverrides.ModelKey(frame);
            draft = store.GetCopy(key) ?? new CargoBoundsOverrides.Entry { Vehicle = key };
            draft.Vehicle = key;
            selected = draft.Boxes.Count > 0 ? 0 : -1;
            message = "";
        }

        private string Source => store.HasSaved(key) ? "your saved boxes" : store.HasDefault(key) ? "bundled default boxes" : "none (automatic measurement)";

        internal void Draw()
        {
            if (!Visible) return;
            panel = GUI.Window(0x4d4f4e43, panel, Window, "Cargo freeze bounds");
        }

        private void Window(int id)
        {
            GUILayout.Label(editing ? "Editing — middle mouse to walk and look around." : "Walking — middle mouse to edit.");
            GUILayout.Label("Nearby vehicles");
            foreach (var candidate in Candidates())
            {
                string label = (candidate.Frame == frame ? "▶ " : "") + CargoBoundsOverrides.ModelKey(candidate.Frame) + "  (" + candidate.Distance.ToString("F0") + " m)";
                if (GUILayout.Button(label) && candidate.Frame != frame)
                {
                    if (dirty) message = "Discarded unsaved boxes for " + key + ".";
                    Select(candidate.Frame);
                }
            }
            if (frame == null) { GUILayout.Label(message); Footer(); return; }
            GUILayout.Space(6);
            GUILayout.Label("Model: " + key + "\nActive: " + Source + (dirty ? "  • unsaved edits" : ""));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Add at crosshair")) AddAtCrosshair(false);
            if (GUILayout.Button("Measure at crosshair")) AddAtCrosshair(true);
            GUI.enabled = selected >= 0;
            if (GUILayout.Button("Duplicate")) Change(() => { draft.Boxes.Insert(selected + 1, draft.Boxes[selected].Copy()); selected++; });
            if (GUILayout.Button("Delete")) Change(() => { draft.Boxes.RemoveAt(selected); selected = Math.Min(selected, draft.Boxes.Count - 1); });
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            for (int i = 0; i < draft.Boxes.Count; i++)
                if (GUILayout.Toggle(selected == i, "Box " + (i + 1), "Button")) selected = i;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Step", GUILayout.Width(40));
            foreach (float value in new[] { 0.01f, 0.05f, 0.25f })
                if (GUILayout.Toggle(Mathf.Approximately(step, value), (value * 100).ToString("F0") + " cm", "Button")) step = value;
            GUILayout.EndHorizontal();
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(250));
            if (selected >= 0 && selected < draft.Boxes.Count)
            {
                var box = draft.Boxes[selected];
                GUILayout.Label("Faces (vehicle axes: X right, Y up, Z forward)");
                Face(box, "Left (−X)", 0, -1); Face(box, "Right (+X)", 0, 1);
                Face(box, "Floor (−Y)", 1, -1); Face(box, "Top (+Y)", 1, 1);
                Face(box, "Rear (−Z)", 2, -1); Face(box, "Front (+Z)", 2, 1);
                GUILayout.Label("Whole box");
                Move(box, "Move X", 0); Move(box, "Move Y", 1); Move(box, "Move Z", 2);
                GUILayout.Label("Center " + box.X.ToString("F3") + ", " + box.Y.ToString("F3") + ", " + box.Z.ToString("F3") +
                    "   Size " + box.Width.ToString("F3") + " × " + box.Height.ToString("F3") + " × " + box.Length.ToString("F3"));
            }
            else GUILayout.Label("No boxes. Look at the cargo floor and use Add or Measure at crosshair.");
            GUILayout.EndScrollView();
            GUILayout.Label(check);
            GUILayout.BeginHorizontal();
            GUI.enabled = draft.Boxes.Count > 0;
            if (GUILayout.Button("Save")) Try(() => { store.Save(draft); dirty = false; message = "Saved " + draft.Boxes.Count + " boxes for " + key + "."; });
            if (GUILayout.Button("Copy JSON")) { GUIUtility.systemCopyBuffer = JsonConvert.SerializeObject(draft, Formatting.Indented); message = "Copied this vehicle's entry to the clipboard."; }
            GUI.enabled = true;
            if (GUILayout.Button("Revert")) Select(frame);
            GUI.enabled = store.HasSaved(key);
            if (GUILayout.Button("Delete saved")) Try(() => { store.Remove(key); Select(frame); message = "Removed your saved boxes; " + Source + " now apply."; });
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            Footer();
        }

        private void Footer()
        {
            if (message.Length > 0) GUILayout.Label(message);
            GUILayout.Label("Saved to: " + store.Path);
            if (GUILayout.Button("Close")) Hide();
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        private void Face(CargoBoundsOverrides.Box box, string label, int axis, int side)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(90));
            if (GUILayout.Button("in")) Change(() => Resize(box, axis, side, -step));
            if (GUILayout.Button("out")) Change(() => Resize(box, axis, side, step));
            GUILayout.EndHorizontal();
        }

        private void Move(CargoBoundsOverrides.Box box, string label, int axis)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(90));
            if (GUILayout.Button("−")) Change(() => Shift(box, axis, -step));
            if (GUILayout.Button("+")) Change(() => Shift(box, axis, step));
            GUILayout.EndHorizontal();
        }

        // Moves one face outward (+delta) or inward, keeping the opposite face fixed.
        private static void Resize(CargoBoundsOverrides.Box box, int axis, int side, float delta)
        {
            float size = axis == 0 ? box.Width : axis == 1 ? box.Height : box.Length;
            float next = Mathf.Clamp(size + delta, 0.05f, 20f), applied = next - size;
            if (axis == 0) { box.Width = next; box.X += side * applied * .5f; }
            else if (axis == 1) { box.Height = next; box.Y += side * applied * .5f; }
            else { box.Length = next; box.Z += side * applied * .5f; }
        }

        private static void Shift(CargoBoundsOverrides.Box box, int axis, float delta)
        {
            if (axis == 0) box.X = Mathf.Clamp(box.X + delta, -20f, 20f);
            else if (axis == 1) box.Y = Mathf.Clamp(box.Y + delta, -20f, 20f);
            else box.Z = Mathf.Clamp(box.Z + delta, -20f, 20f);
        }

        private void Change(Action action) { action(); dirty = true; }

        private void Try(Action action)
        {
            try { action(); }
            catch (Exception ex) { message = "Failed: " + ex.Message; }
        }

        private bool CrosshairHit(out RaycastHit hit)
        {
            hit = default;
            var camera = gameplay.Player.mainCamera;
            return camera != null && Physics.Raycast(camera.transform.position, camera.transform.forward, out hit, 12f, ~0, QueryTriggerInteraction.Ignore);
        }

        private void AddAtCrosshair(bool measure)
        {
            if (frame == null) return;
            if (draft.Boxes.Count >= CargoBoundsOverrides.MaximumBoxes) { message = "A vehicle can have at most " + CargoBoundsOverrides.MaximumBoxes + " boxes."; return; }
            if (!CrosshairHit(out var hit) || !hit.collider.transform.IsChildOf(frame)) { message = "Aim at the selected vehicle's cargo floor first."; return; }
            Bounds bounds;
            if (measure)
            {
                if (!VehicleCargoSpace.TryMeasure(frame, hit.point, out bounds)) { message = "No floor with walls was measured there; use Add instead."; return; }
            }
            else
            {
                var local = Local(hit.point);
                bounds = new Bounds(local + Vector3.up * 0.25f, new Vector3(1f, 0.5f, 1f));
            }
            Change(() =>
            {
                draft.Boxes.Add(new CargoBoundsOverrides.Box { X = bounds.center.x, Y = bounds.center.y, Z = bounds.center.z,
                    Width = Mathf.Max(bounds.size.x, 0.05f), Height = Mathf.Max(bounds.size.y, 0.05f), Length = Mathf.Max(bounds.size.z, 0.05f) });
                selected = draft.Boxes.Count - 1;
            });
            message = measure ? "Measured box added; adjust its faces to fit." : "Box added at the crosshair; adjust its faces to fit.";
        }

        // Same frame space as VehicleCargoSpace: the carrier body's physics pose, unscaled.
        private Vector3 Local(Vector3 world)
        {
            var body = frame!.GetComponent<Rigidbody>();
            var rotation = body != null ? body.rotation : frame.rotation; var position = body != null ? body.position : frame.position;
            var p = Quaternion.Inverse(rotation) * (world - position); var scale = frame.lossyScale;
            return new Vector3(p.x / scale.x, p.y / scale.y, p.z / scale.z);
        }

        private string LookCheck()
        {
            if (frame == null || !CrosshairHit(out var hit)) return "Look at a loose item to check it against the boxes.";
            var item = hit.collider.GetComponentInParent<InteractableItems>();
            if (item == null) return "Look at a loose item to check it against the boxes.";
            var center = Local(item.itemCollider != null ? item.itemCollider.bounds.center : item.transform.position);
            int inside = -1;
            for (int i = 0; i < draft.Boxes.Count; i++) if (draft.Boxes[i].Bounds.Contains(center)) { inside = i; break; }
            string text = item.name + ": center " + (inside >= 0 ? "inside box " + (inside + 1) : "outside the draft boxes");
            if (!dirty && plugin.World?.Items is SharedItems items)
            {
                try { text += "\n" + items.TestCargoVerdict(item); } catch (Exception) { }
            }
            return text;
        }

        private void DrawBoxes()
        {
            int used = 0;
            if (frame != null)
                for (int i = 0; i < draft.Boxes.Count; i++)
                {
                    var line = Line(used++);
                    if (line.transform.parent != frame) line.transform.SetParent(frame, false);
                    line.transform.localPosition = Vector3.zero; line.transform.localRotation = Quaternion.identity; line.transform.localScale = Vector3.one;
                    var color = i == selected ? new Color(1f, 0.85f, 0.1f) : new Color(0.2f, 0.9f, 1f);
                    line.startColor = line.endColor = color;
                    line.widthMultiplier = i == selected ? 0.015f : 0.01f;
                    var b = draft.Boxes[i].Bounds; var n = b.min; var x = b.max;
                    // One path over all twelve edges (three retraced).
                    line.positionCount = 16;
                    line.SetPositions(new[]
                    {
                        new Vector3(n.x, n.y, n.z), new Vector3(x.x, n.y, n.z), new Vector3(x.x, n.y, x.z), new Vector3(n.x, n.y, x.z), new Vector3(n.x, n.y, n.z),
                        new Vector3(n.x, x.y, n.z), new Vector3(x.x, x.y, n.z), new Vector3(x.x, n.y, n.z), new Vector3(x.x, x.y, n.z),
                        new Vector3(x.x, x.y, x.z), new Vector3(x.x, n.y, x.z), new Vector3(x.x, x.y, x.z), new Vector3(n.x, x.y, x.z),
                        new Vector3(n.x, n.y, x.z), new Vector3(n.x, x.y, x.z), new Vector3(n.x, x.y, n.z),
                    });
                    line.gameObject.SetActive(true);
                }
            for (int i = used; i < lines.Count; i++) if (lines[i] != null) lines[i].gameObject.SetActive(false);
        }

        private LineRenderer Line(int index)
        {
            while (lines.Count <= index)
            {
                material ??= new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Hidden/Internal-Colored"));
                var go = new GameObject("MonMP cargo bounds editor box");
                var line = go.AddComponent<LineRenderer>();
                line.useWorldSpace = false; line.sharedMaterial = material; line.numCapVertices = 0;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; line.receiveShadows = false;
                lines.Add(line);
            }
            if (lines[index] == null)
            {
                lines.RemoveAt(index);
                return Line(index);
            }
            return lines[index];
        }
    }
}
#endif
