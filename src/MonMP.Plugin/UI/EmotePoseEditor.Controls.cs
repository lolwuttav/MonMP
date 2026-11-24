using System;
using MonMP.Core.Protocol;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.UI
{
    internal sealed partial class EmotePoseEditor
    {
        private int dragMode, dragAxis;
        private Vector2 dragProjected, dragCenter, dragRingU, dragRingV;
        private Vector3 dragPosition, dragEuler, dragAxisWorld, dragMovement;
        private Quaternion dragFrame;
        private float dragLastAngle, dragAngle, dragDepth;
        private const float HandleLength = .12f;

        private void DrawControls()
        {
            GUILayout.Label(view == EmotePoseOverrides.ViewKind.FirstPerson ? "First-person / camera axes" : "Remote / body axes");
            GUILayout.BeginHorizontal();
            rotate = GUILayout.Toggle(rotate, "Rotation handles", GUI.skin.button);
            snap = GUILayout.Toggle(snap, "Snap");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Move step", GUILayout.Width(74));
            if (GUILayout.Button("1 mm")) positionStep = .001f;
            if (GUILayout.Button("1 cm")) positionStep = .01f;
            if (GUILayout.Button("5 cm")) positionStep = .05f;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Angle step", GUILayout.Width(74));
            if (GUILayout.Button("1°")) angleStep = 1f;
            if (GUILayout.Button("5°")) angleStep = 5f;
            if (GUILayout.Button("15°")) angleStep = 15f;
            GUILayout.EndHorizontal();
            GUILayout.Label("Wrist position offset (metres)");
            draft.X = Axis("wrist.x", "X", draft.X, -.6f, .6f, positionStep);
            draft.Y = Axis("wrist.y", "Y", draft.Y, -.6f, .6f, positionStep);
            draft.Z = Axis("wrist.z", "Z", draft.Z, -.6f, .6f, positionStep);
            GUILayout.Label("Palm orientation offset (degrees)");
            draft.Pitch = Axis("palm.x", "Pitch", draft.Pitch, -180, 180, angleStep);
            draft.Yaw = Axis("palm.y", "Yaw", draft.Yaw, -180, 180, angleStep);
            draft.Roll = Axis("palm.z", "Roll", draft.Roll, -180, 180, angleStep);
            GUILayout.Label("Elbow bend direction offset (unitless)");
            draft.ElbowX = Axis("elbow.x", "X", draft.ElbowX, -2, 2, .05f);
            draft.ElbowY = Axis("elbow.y", "Y", draft.ElbowY, -2, 2, .05f);
            draft.ElbowZ = Axis("elbow.z", "Z", draft.ElbowZ, -2, 2, .05f);
            if (kind == PlayerEmoteKind.Wave)
            {
                GUILayout.Label("Wave motion");
                draft.WaveScale = Axis("wave.scale", "Size", draft.WaveScale, 0, 2, .05f);
                draft.WaveRate = Axis("wave.rate", "Speed", draft.WaveRate, .25f, 3, .05f);
            }
            GUILayout.Space(8); GUILayout.Label("Rotation handle target / individual joints");
            if (GUILayout.Toggle(bone.Length == 0, "Palm orientation", GUI.skin.button) && bone.Length > 0)
            {
                if (TryCommitNumericInput("change joints")) { EndEdit(); bone = ""; numericInput.Clear(); }
            }
            foreach (var name in EmotePoseOverrides.BonesFor(view))
            {
                if (GUILayout.Toggle(bone == name, name, GUI.skin.button) && bone != name)
                {
                    if (TryCommitNumericInput("change joints")) { EndEdit(); bone = name; rotate = true; numericInput.Clear(); }
                }
            }
            if (bone.Length > 0)
            {
                GUILayout.Label(bone + " local joint offset (degrees)");
                var current = SelectedEuler();
                var next = new Vector3(
                    Axis("bone.x", "X", current.x, -180, 180, angleStep),
                    Axis("bone.y", "Y", current.y, -180, 180, angleStep),
                    Axis("bone.z", "Z", current.z, -180, 180, angleStep));
                if (next != current) SetSelectedEuler(next);
                if (GUILayout.Button("Clear this joint offset"))
                {
                    EndEdit(); draft.Bones.Remove(bone);
                    numericInput.Remove("bone.x"); numericInput.Remove("bone.y"); numericInput.Remove("bone.z");
                }
            }
        }

        private float Axis(string key, string label, float value, float minimum, float maximum, float step)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(42));
            if (GUILayout.Button("−", GUILayout.Width(25)))
            { EndEdit(); value = Mathf.Clamp(value - step, minimum, maximum); numericInput.Remove(key); message = ""; }
            string current = numericInput.GetText(key, value);
            var background = GUI.backgroundColor;
            if (numericInput.HasError(key)) GUI.backgroundColor = new Color(1f, .45f, .4f);
            GUI.SetNextControlName(NumericControlName(key));
            string nextText = GUILayout.TextField(current, GUILayout.Width(70));
            GUI.backgroundColor = background;
            if (nextText != current)
            {
                BeginTextEdit(key); message = "";
                value = numericInput.Edit(key, NumericFieldLabel(key, label), nextText, value, minimum, maximum);
            }
            if (GUILayout.Button("+", GUILayout.Width(25)))
            { EndEdit(); value = Mathf.Clamp(value + step, minimum, maximum); numericInput.Remove(key); message = ""; }
            float slide = GUILayout.HorizontalSlider(value, minimum, maximum, GUILayout.MinWidth(50));
            if (slide != value)
            {
                if (activeTextKey.Length > 0) EndEdit();
                if (GUIUtility.hotControl != 0) { BeginEdit(); numericGesture = true; }
                value = HoldPreviewGizmoMath.ClampAndSnap(value, slide - value, minimum, maximum, step, snap);
                numericInput.Remove(key); message = "";
            }
            GUILayout.EndHorizontal();
            string hint = numericInput.Hint(key);
            if (hint.Length > 0) GUILayout.Label(hint);
            return value;
        }

        private string NumericFieldLabel(string key, string label)
        {
            if (key.StartsWith("wrist.", StringComparison.Ordinal)) return "Wrist " + label;
            if (key.StartsWith("palm.", StringComparison.Ordinal)) return "Palm " + label;
            if (key.StartsWith("elbow.", StringComparison.Ordinal)) return "Elbow " + label;
            if (key.StartsWith("wave.", StringComparison.Ordinal)) return "Wave " + label;
            return bone + " " + label;
        }

        private Vector3 SelectedEuler()
        {
            if (bone.Length == 0) return draft.Euler;
            return draft.Bones.TryGetValue(bone, out var joint) ? new Vector3(joint.X, joint.Y, joint.Z) : Vector3.zero;
        }
        private void SetSelectedEuler(Vector3 value)
        {
            value = new Vector3(Mathf.Clamp(value.x, -180, 180), Mathf.Clamp(value.y, -180, 180), Mathf.Clamp(value.z, -180, 180));
            if (bone.Length == 0) { draft.Pitch = value.x; draft.Yaw = value.y; draft.Roll = value.z; }
            else if (value == Vector3.zero) draft.Bones.Remove(bone);
            else draft.Bones[bone] = new EmotePoseOverrides.BoneRotation { X = value.x, Y = value.y, Z = value.z };
        }
        private Transform? SelectedJoint()
        {
            if (bone.Length == 0 || previewRig == null) return wrist;
            foreach (var node in previewRig.GetComponentsInChildren<Transform>(true)) if (node.name == bone) return node;
            return wrist;
        }
        private Quaternion EditingFrame()
        {
            if (bone.Length > 0 && rotate)
            {
                var joint = SelectedJoint();
                // Runtime joint offsets postmultiply the posed local rotation and fade with
                // the gesture. Remove the same weighted offset to show its editing frame.
                if (joint != null) return joint.rotation * Quaternion.Inverse(Quaternion.Slerp(
                    Quaternion.identity, Quaternion.Euler(SelectedEuler()), PreviewWeight()));
            }
            return view == EmotePoseOverrides.ViewKind.FirstPerson && previewCamera != null ? previewCamera.transform.rotation : previewRig != null ? previewRig.rotation : Quaternion.identity;
        }
        private float PreviewWeight()
        {
            float duration = StockAvatarBody.GestureDuration(kind);
            if (elapsed <= 0f || elapsed >= duration) return 0f;
            float fadeIn = view == EmotePoseOverrides.ViewKind.FirstPerson ? .18f : .16f;
            float fadeOut = view == EmotePoseOverrides.ViewKind.FirstPerson ? .22f : .24f;
            return Mathf.SmoothStep(0f, 1f, Mathf.Min(elapsed / fadeIn, (duration - elapsed) / fadeOut));
        }
        private Vector2 Project(Rect viewport, Vector3 point)
        {
            var projected = previewCamera!.WorldToViewportPoint(point);
            return new Vector2(viewport.x + projected.x * viewport.width, viewport.y + (1f - projected.y) * viewport.height);
        }
        private static System.Numerics.Vector2 Numeric(Vector2 point) => new System.Numerics.Vector2(point.x, point.y);
        private static Color HandleColor(int axis) => axis == 0 ? new Color(1f, .3f, .25f) : axis == 1 ? Color.green : new Color(.3f, .65f, 1f);
        private static Vector3 AxisVector(int axis) => axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;

        private void DrawViewport(Rect viewport)
        {
            if (previewCamera == null || wrist == null || viewport.width <= 0f) return;
            var current = Event.current;
            int control = GUIUtility.GetControlID("MonMP emote editor viewport".GetHashCode(), FocusType.Passive, viewport);
            var joint = rotate ? SelectedJoint() : wrist;
            if (joint == null) return;
            Vector3 centerWorld = joint.position;
            if (previewCamera.WorldToViewportPoint(centerWorld).z <= .001f) return;
            var center = Project(viewport, centerWorld);
            var frame = EditingFrame();
            int hovered = -1; float closest = 9f;
            Vector2 selectedU = default, selectedV = default, selectedProjected = default;
            for (int axis = 0; axis < 3; axis++)
            {
                var color = HandleColor(axis);
                if (rotate)
                {
                    var u = frame * AxisVector((axis + 1) % 3) * HandleLength;
                    var v = frame * AxisVector((axis + 2) % 3) * HandleLength;
                    var projectedU = Project(viewport, centerWorld + u) - center;
                    var projectedV = Project(viewport, centerWorld + v) - center;
                    bool usable = HoldPreviewGizmoMath.TryRingAngle(Numeric(center + projectedU), Numeric(center), Numeric(projectedU), Numeric(projectedV), out _);
                    Vector2 previous = center + projectedU;
                    for (int segment = 1; segment <= 48; segment++)
                    {
                        float angle = segment * Mathf.PI * 2f / 48f;
                        var next = center + projectedU * Mathf.Cos(angle) + projectedV * Mathf.Sin(angle);
                        DrawLine(previous, next, usable ? color : color * .4f, 2f, viewport);
                        float hit = HoldPreviewGizmoMath.SegmentDistance(Numeric(current.mousePosition), Numeric(previous), Numeric(next));
                        if (usable && hit < closest) { hovered = axis; closest = hit; selectedU = projectedU; selectedV = projectedV; }
                        previous = next;
                    }
                }
                else
                {
                    var end = Project(viewport, centerWorld + frame * AxisVector(axis) * HandleLength);
                    DrawLine(center, end, color, 3f, viewport);
                    var tip = end - center;
                    if (tip.sqrMagnitude > 64f)
                    {
                        float hit = HoldPreviewGizmoMath.SegmentDistance(Numeric(current.mousePosition), Numeric(center), Numeric(end));
                        if (hit < closest) { hovered = axis; closest = hit; selectedProjected = tip; }
                        if (viewport.Contains(end)) GUI.Label(new Rect(end.x - 6, end.y - 8, 16, 20), axis == 0 ? "X" : axis == 1 ? "Y" : "Z");
                    }
                }
            }
            if (!rotate && viewport.Contains(center)) GUI.Box(new Rect(center.x - 5, center.y - 5, 10, 10), GUIContent.none);
            if (current.type == EventType.MouseDown && viewport.Contains(current.mousePosition))
            {
                int mode = current.button == 0 && !(current.alt) ? (rotate ? hovered >= 0 ? 2 : 0 : Vector2.Distance(current.mousePosition, center) < 9f ? 3 : hovered >= 0 ? 1 : 0)
                    : view == EmotePoseOverrides.ViewKind.Remote && current.button == 1 ? 4
                    : view == EmotePoseOverrides.ViewKind.Remote && (current.button == 2 || current.button == 0 && current.alt) ? 5 : 0;
                if (mode == 0) return;
                if (mode <= 3)
                {
                    if (!TryCommitNumericInput("drag the pose handles")) { current.Use(); return; }
                    EndEdit(); GUI.FocusControl("");
                }
                dragMode = mode; dragAxis = hovered; dragProjected = selectedProjected;
                dragPosition = draft.Position; dragEuler = SelectedEuler(); dragFrame = frame;
                dragMovement = Vector3.zero; dragDepth = previewCamera.WorldToViewportPoint(wrist.position).z;
                dragCenter = center; dragRingU = selectedU; dragRingV = selectedV;
                dragAxisWorld = frame * AxisVector(Mathf.Max(0, hovered)); dragAngle = 0;
                if (mode == 2 && !HoldPreviewGizmoMath.TryRingAngle(Numeric(current.mousePosition), Numeric(center), Numeric(selectedU), Numeric(selectedV), out dragLastAngle)) { dragMode = 0; return; }
                if (mode <= 3) { playing = false; BeginEdit(); }
                GUIUtility.hotControl = control; ownedHotControl = control; current.Use();
            }
            else if (current.type == EventType.MouseDrag && GUIUtility.hotControl == control && dragMode != 0)
            {
                float fine = current.shift ? .1f : 1f;
                if (dragMode == 4)
                { orbit += current.delta.x * .5f; elevation = Mathf.Clamp(elevation - current.delta.y * .4f, -85, 85); }
                else if (dragMode == 5)
                {
                    float units = 2f * distance * Mathf.Tan(previewCamera.fieldOfView * Mathf.Deg2Rad * .5f) / viewport.height;
                    cameraPivot += (-previewCamera.transform.right * current.delta.x + previewCamera.transform.up * current.delta.y) * units * fine;
                }
                else if (dragMode == 2)
                {
                    if (HoldPreviewGizmoMath.TryRingAngle(Numeric(current.mousePosition), Numeric(dragCenter), Numeric(dragRingU), Numeric(dragRingV), out float next))
                    {
                        dragAngle += Mathf.DeltaAngle(dragLastAngle, next) * fine; dragLastAngle = next;
                        float turn = HoldPreviewGizmoMath.ClampAndSnap(0f, dragAngle, -360f, 360f, angleStep, snap);
                        var rotation = Quaternion.AngleAxis(turn, AxisVector(dragAxis)) * Quaternion.Euler(dragEuler);
                        var euler = rotation.eulerAngles;
                        SetSelectedEuler(new Vector3(Mathf.DeltaAngle(0, euler.x), Mathf.DeltaAngle(0, euler.y), Mathf.DeltaAngle(0, euler.z))); numericInput.Clear();
                    }
                }
                else
                {
                    Vector3 movement = Vector3.zero;
                    if (dragMode == 1 && HoldPreviewGizmoMath.TryAxisDrag(Numeric(current.delta * fine), Numeric(dragProjected), HandleLength, out float delta)) movement = dragAxisWorld * delta;
                    else if (dragMode == 3)
                    {
                        float units = 2f * dragDepth * Mathf.Tan(previewCamera.fieldOfView * Mathf.Deg2Rad * .5f) / viewport.height;
                        var deltaScreen = current.delta * fine;
                        movement = (previewCamera.transform.right * deltaScreen.x - previewCamera.transform.up * deltaScreen.y) * units;
                    }
                    var local = Quaternion.Inverse(dragFrame) * movement;
                    float scale = view == EmotePoseOverrides.ViewKind.Remote ? Mathf.Max(.001f, Mathf.Abs(wrist.lossyScale.y)) : 1f;
                    dragMovement += local / scale;
                    draft.X = HoldPreviewGizmoMath.ClampAndSnap(dragPosition.x, dragMovement.x, -.6f, .6f, positionStep, snap);
                    draft.Y = HoldPreviewGizmoMath.ClampAndSnap(dragPosition.y, dragMovement.y, -.6f, .6f, positionStep, snap);
                    draft.Z = HoldPreviewGizmoMath.ClampAndSnap(dragPosition.z, dragMovement.z, -.6f, .6f, positionStep, snap); numericInput.Clear();
                }
                RenderPreview(); current.Use();
            }
            else if (current.type == EventType.MouseUp && GUIUtility.hotControl == control)
            { EndEdit(); GUIUtility.hotControl = 0; current.Use(); }
            else if (current.type == EventType.ScrollWheel && viewport.Contains(current.mousePosition) && view == EmotePoseOverrides.ViewKind.Remote)
            { distance = Mathf.Clamp(distance * Mathf.Exp(current.delta.y * .06f), .15f, 8f); UpdateRemoteCamera(); current.Use(); }
        }

        private static void DrawLine(Vector2 start, Vector2 end, Color color, float width, Rect clip)
        {
            // Clip to the viewport before rotating the one-pixel texture. A partly off-screen
            // first-person wrist must never paint its long handles over numeric controls.
            float low = 0f, high = 1f;
            var delta = end - start;
            if (!Clip(-delta.x, start.x - clip.xMin) || !Clip(delta.x, clip.xMax - start.x) ||
                !Clip(-delta.y, start.y - clip.yMin) || !Clip(delta.y, clip.yMax - start.y)) return;
            end = start + delta * high; start += delta * low; delta = end - start;
            if (delta.sqrMagnitude < .01f) return;
            var matrix = GUI.matrix; var previousColor = GUI.color;
            GUI.color = color; GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, start);
            GUI.DrawTexture(new Rect(start.x, start.y - width * .5f, delta.magnitude, width), Texture2D.whiteTexture);
            GUI.matrix = matrix; GUI.color = previousColor;
            bool Clip(float direction, float bound)
            {
                if (Mathf.Abs(direction) < 1e-6f) return bound >= 0f;
                float ratio = bound / direction;
                if (direction < 0f) { if (ratio > high) return false; low = Mathf.Max(low, ratio); }
                else { if (ratio < low) return false; high = Mathf.Min(high, ratio); }
                return true;
            }
        }
    }
}
