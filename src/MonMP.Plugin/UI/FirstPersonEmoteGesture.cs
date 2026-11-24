using System;
using MonMP.Core.Protocol;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.UI
{
    // Poses the player's existing skinned right arm after the native Animator. Restore before the
    // next animation update, including joints the controller does not key in its idle state. The
    // Animator, renderers, objects, input actions and left arm always retain their native owners.
    internal sealed class FirstPersonEmoteGesture
    {
        private const string UpperPath = "Armature/Chest/Shoulder.R/Upper Arm.R";
        // Native zoom reaches 18 degrees. A full open hand cannot fit that narrow lens at the
        // stock arm's anatomical reach; yield the local overlay to zoom without altering its FOV.
        private const float MinimumGestureFieldOfView = 40f;
        private PlayerVisualHands? hands;
        private Camera? camera;
        private Transform? view, upper, lower, hand;
        private Transform[] joints = Array.Empty<Transform>();
        private Quaternion[] native = Array.Empty<Quaternion>(), applied = Array.Empty<Quaternion>();
        private PlayerEmoteKind kind;
        private float startedAt;
        private bool hasApplied;
#if MONMP_DEVELOPMENT
        private bool preview;
#endif

        internal bool IsActive => kind != PlayerEmoteKind.None;

        internal bool Begin(PlayerVisualHands owner, Camera renderCamera, PlayerEmoteKind gesture)
        {
            Cancel();
            if (owner == null || !Bind(owner.transform, renderCamera, gesture)) return false;
            hands = owner;
            return true;
        }

#if MONMP_DEVELOPMENT
        // The editor supplies an isolated transform/renderer copy, never the live hands component.
        internal bool BeginPreview(Transform rigRoot, Camera renderCamera, PlayerEmoteKind gesture)
        {
            Cancel();
            if (!Bind(rigRoot, renderCamera, gesture)) return false;
            preview = true;
            return true;
        }

        internal void ApplyPreview(float elapsed, EmotePoseOverrides.Pose draft)
        {
            if (!preview || !IsActive) return;
            try { EmotePoseOverrides.Validate(EmotePoseOverrides.ViewKind.FirstPerson, kind, draft); }
            catch (System.IO.InvalidDataException) { RestoreBeforeAnimation(); return; }
            ApplyAt(elapsed, draft, keepBindingOnExpiry: true);
        }
#endif

        private bool Bind(Transform rigRoot, Camera renderCamera, PlayerEmoteKind gesture)
        {
            if (rigRoot == null || renderCamera == null || renderCamera.fieldOfView < MinimumGestureFieldOfView ||
                StockAvatarBody.GestureDuration(gesture) <= 0f) return false;
            // These are the actual level2 PlayerVisualHands bones, distinct from the NPC rig's
            // Fingers01.R/Fingers02.R chains used by the remote body and wheel preview.
            var arm = rigRoot.Find(UpperPath);
            var forearm = arm != null ? arm.Find("Lower Arm.R") : null;
            var wrist = forearm != null ? forearm.Find("Hand.R") : null;
            if (arm == null || forearm == null || wrist == null) return false;
            var bones = new Transform[14];
            bones[0] = arm; bones[1] = forearm; bones[2] = wrist;
            for (int finger = 0; finger < 4; finger++)
            {
                var proximal = wrist.Find("Hand.Finger" + finger + "0.R");
                var distal = proximal != null ? proximal.Find("Hand.Finger" + finger + "1.R") : null;
                if (proximal == null || distal == null || distal.Find("Hand.Finger" + finger + "1.R_end") == null)
                    return false;
                bones[3 + finger * 2] = proximal;
                bones[4 + finger * 2] = distal;
            }
            var thumb = wrist.Find("Thumb01.R");
            var thumbMiddle = thumb != null ? thumb.Find("Thumb02.R") : null;
            var thumbTip = thumbMiddle != null ? thumbMiddle.Find("Thumb03.R") : null;
            if (thumb == null || thumbMiddle == null || thumbTip == null || thumbTip.Find("Thumb03.R_end") == null)
                return false;
            bones[11] = thumb; bones[12] = thumbMiddle; bones[13] = thumbTip;
            camera = renderCamera; view = renderCamera.transform; upper = arm; lower = forearm; hand = wrist;
            joints = bones;
            native = new Quaternion[bones.Length]; applied = new Quaternion[bones.Length];
            kind = gesture;
            startedAt = Time.unscaledTime;
            return true;
        }

        internal void RestoreBeforeAnimation()
        {
            if (!hasApplied) return;
            hasApplied = false;
            for (int i = 0; i < joints.Length; i++)
            {
                var joint = joints[i];
                // Native animation or another owner may have already taken over in this frame.
                // Never restore an old snapshot over a rotation we no longer own.
                if (joint != null && Mathf.Abs(Quaternion.Dot(joint.localRotation, applied[i])) > 0.999999f)
                    joint.localRotation = native[i];
            }
        }

        internal void Apply()
        {
            if (!IsActive) return;
            if (hands == null || !hands.isActiveAndEnabled)
            { Cancel(); return; }
            ApplyAt(Time.unscaledTime - startedAt, EmotePoseOverrides.Active?.Get(EmotePoseOverrides.ViewKind.FirstPerson, kind));
        }

        private void ApplyAt(float elapsed, EmotePoseOverrides.Pose? calibration, bool keepBindingOnExpiry = false)
        {
            if (camera == null || camera.fieldOfView < MinimumGestureFieldOfView ||
                view == null || upper == null || lower == null || hand == null)
            { Cancel(); return; }
            float duration = StockAvatarBody.GestureDuration(kind);
            if (float.IsNaN(elapsed) || float.IsInfinity(elapsed) || elapsed < 0f || elapsed >= duration)
            {
                // The scrub endpoint is neutral, but the isolated rig stays bound for restart.
                if (keepBindingOnExpiry) RestoreBeforeAnimation(); else Cancel();
                return;
            }
            // Also makes an accidental second LateTick in one frame idempotent.
            RestoreBeforeAnimation();
            for (int i = 0; i < joints.Length; i++)
            {
                if (joints[i] == null) { Cancel(); return; }
                native[i] = joints[i].localRotation;
            }
            float weight = Mathf.SmoothStep(0f, 1f, Mathf.Min(elapsed / 0.18f, (duration - elapsed) / 0.22f));
            try
            {
                Pose(elapsed, weight, calibration);
                for (int i = 0; i < joints.Length; i++) applied[i] = joints[i].localRotation;
                hasApplied = true;
            }
            catch
            {
                // A destroyed joint during a scene transition must not leave the surviving arm
                // posed. Nothing external can animate between these synchronous operations.
                for (int i = 0; i < joints.Length; i++)
                    if (joints[i] != null) joints[i].localRotation = native[i];
                Cancel();
            }
        }

        internal void Cancel()
        {
            RestoreBeforeAnimation();
            kind = PlayerEmoteKind.None;
            hands = null; camera = null; view = null; upper = null; lower = null; hand = null;
            joints = Array.Empty<Transform>();
            native = Array.Empty<Quaternion>(); applied = Array.Empty<Quaternion>();
#if MONMP_DEVELOPMENT
            preview = false;
#endif
        }

        private void Pose(float elapsed, float weight, EmotePoseOverrides.Pose? calibration)
        {
            if (weight <= 0.0001f || camera == null || view == null || upper == null || lower == null || hand == null) return;
            var right = view.right; var up = view.up; var forward = view.forward;
            float wave = kind == PlayerEmoteKind.Wave ? Mathf.Sin(elapsed * 13f) : 0f;
            if (calibration != null && kind == PlayerEmoteKind.Wave)
                wave = Mathf.Sin(elapsed * 13f * calibration.WaveRate) * calibration.WaveScale;
            Vector3 fingers, palmNormal, thumb;
            float viewportX, viewportY, depth;
            switch (kind)
            {
                case PlayerEmoteKind.Wave:
                    viewportX = 0.68f + wave * 0.025f; viewportY = 0.40f; depth = 0.46f;
                    fingers = (up + right * (wave * 0.28f)).normalized;
                    thumb = -right;
                    palmNormal = Vector3.Cross(fingers, thumb).normalized;
                    break;
                case PlayerEmoteKind.Point:
                    viewportX = 0.70f; viewportY = 0.33f; depth = 0.50f;
                    // A straight camera-forward index is almost entirely foreshortened. Angle
                    // across the view so the first-person gesture has a clear index silhouette.
                    fingers = (forward * 0.75f - right * 0.62f + up * 0.22f).normalized;
                    thumb = up;
                    palmNormal = Vector3.Cross(fingers, thumb).normalized;
                    break;
                default: // ThumbsUp: thumb-up, closed fist in the lower-right of the view.
                    viewportX = 0.70f; viewportY = 0.38f; depth = 0.45f;
                    fingers = forward;
                    thumb = up;
                    palmNormal = -right;
                    break;
            }
            // The foreground camera owns the actual arm projection. Fixed metre offsets put a
            // raised palm outside a narrow/aspect-ratio view even at the stock 60-degree FOV.
            // Anchor the wrist with that camera's current lens and leave room for the whole hand.
            float halfHeight = depth * Mathf.Tan(Mathf.Clamp(camera.fieldOfView, 15f, 120f) * Mathf.Deg2Rad * 0.5f);
            float halfWidth = halfHeight * Mathf.Clamp(camera.aspect, 0.75f, 4f);
            var offset = new Vector3((viewportX - 0.5f) * 2f * halfWidth, (viewportY - 0.5f) * 2f * halfHeight, depth);
            var target = view.position + right * offset.x + up * offset.y + forward * offset.z;
            var pole = right - up * 0.55f;
            if (calibration != null)
            {
                target += right * calibration.X + up * calibration.Y + forward * calibration.Z;
                pole += right * calibration.ElbowX + up * calibration.ElbowY + forward * calibration.ElbowZ;
            }
            Reach(upper, lower, hand, target, pole, weight);

            // Derive the wrist basis from the actual finger/thumbnail positions. Imported bone
            // axes and the player's 100x model scale do not need Euler-angle assumptions.
            var along = (joints[3].position + joints[5].position + joints[7].position + joints[9].position) * 0.25f - hand.position;
            var across = joints[11].position - hand.position;
            var normal = Vector3.Cross(along, across);
            if (along.sqrMagnitude > 1e-8f && normal.sqrMagnitude > 1e-10f)
            {
                var rotation = Quaternion.LookRotation(fingers, palmNormal) *
                    Quaternion.Inverse(Quaternion.LookRotation(along, normal));
                hand.rotation = Quaternion.Slerp(Quaternion.identity, rotation, weight) * hand.rotation;
            }

            for (int finger = 0; finger < 4; finger++)
            {
                var proximal = joints[3 + finger * 2]; var distal = joints[4 + finger * 2];
                var tip = distal.GetChild(0);
                // The stock rig has independent fingers; keep only the index extended for Point.
                float curl = kind == PlayerEmoteKind.Wave || (kind == PlayerEmoteKind.Point && finger == 0) ? 0f : 82f;
                var curled = Quaternion.AngleAxis(curl, -thumb) * fingers;
                Align(proximal, distal, curled, weight);
                Align(distal, tip, Quaternion.AngleAxis(curl * 0.9f, -thumb) * curled, weight);
            }
            var thumbDirection = kind == PlayerEmoteKind.ThumbsUp ? up :
                kind == PlayerEmoteKind.Wave ? (-right + up * 0.25f).normalized : (-up + fingers * 0.35f).normalized;
            Align(joints[11], joints[12], thumbDirection, weight);
            Align(joints[12], joints[13], thumbDirection, weight);
            Align(joints[13], joints[13].GetChild(0), thumbDirection, weight);

            if (calibration == null) return;
            if (calibration.Euler.sqrMagnitude > 0f)
            {
                // Palm adjustment axes belong to the camera, regardless of imported bone axes.
                var rotation = view.rotation * Quaternion.Euler(calibration.Euler) * Quaternion.Inverse(view.rotation);
                hand.rotation = Quaternion.Slerp(Quaternion.identity, rotation, weight) * hand.rotation;
            }
            foreach (var joint in joints)
                if (calibration.Bones.TryGetValue(joint.name, out var rotation))
                    joint.localRotation = joint.localRotation * Quaternion.Slerp(Quaternion.identity,
                        Quaternion.Euler(rotation.X, rotation.Y, rotation.Z), weight);
        }

        private static void Align(Transform joint, Transform end, Vector3 direction, float weight)
        {
            var current = end.position - joint.position;
            if (current.sqrMagnitude > 1e-10f && direction.sqrMagnitude > 1e-6f)
                joint.rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(current, direction), weight) * joint.rotation;
        }

        private static void Reach(Transform arm, Transform forearm, Transform wrist, Vector3 target, Vector3 pole, float weight)
        {
            var a = arm.position; var b = forearm.position; var c = wrist.position;
            float upperLength = (b - a).magnitude, lowerLength = (c - b).magnitude;
            var goal = Vector3.Lerp(c, target, weight) - a;
            if (upperLength < 0.01f || lowerLength < 0.01f || goal.sqrMagnitude < 1e-8f) return;
            var direction = goal.normalized;
            float distance = Mathf.Clamp(goal.magnitude, Mathf.Abs(upperLength - lowerLength) + 0.001f, upperLength + lowerLength - 0.001f);
            pole -= direction * Vector3.Dot(pole, direction);
            if (pole.sqrMagnitude < 1e-8f) return;
            pole.Normalize();
            float along = (upperLength * upperLength - lowerLength * lowerLength + distance * distance) / (2f * distance);
            var elbow = a + direction * along + pole * Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - along * along));
            arm.rotation = Quaternion.FromToRotation(b - a, elbow - a) * arm.rotation;
            b = forearm.position; c = wrist.position;
            forearm.rotation = Quaternion.FromToRotation(c - b, a + direction * distance - b) * forearm.rotation;
        }
    }
}
