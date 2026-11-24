using System;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal static class NativeRepairFixtureAim
    {
        // Only camera/player pose is staged. The native tool still resolves and acts on
        // its exact panel through its ordinary masked viewport ray on subsequent frames.
        internal static string LastDiagnostics { get; private set; } = "not-staged";
        private static string lookDiagnostics = "not-aimed";

        internal static void SetLook(Gameplay gameplay, Camera camera, Vector3 direction)
        {
            var controller = gameplay.PlayerWalking.playerCameraController;
            if (controller == null) throw new InvalidOperationException("Native repair camera controller is unavailable.");
            var parent = controller.transform.parent;
            // The ray camera can be a rotated/offset child of the native look controller.
            // Solve the controller orientation needed for this camera's actual forward.
            bool child = camera.transform.IsChildOf(controller.transform);
            Quaternion desiredCamera = Quaternion.LookRotation(direction, Vector3.up);
            Quaternion cameraFromController = child
                ? Quaternion.Inverse(controller.transform.rotation) * camera.transform.rotation : Quaternion.identity;
            Quaternion desiredController = desiredCamera * Quaternion.Inverse(cameraFromController);
            Quaternion local = parent != null ? Quaternion.Inverse(parent.rotation) * desiredController : desiredController;
            Vector3 angles = local.eulerAngles;
            controller.SetRotationAngles(Mathf.DeltaAngle(0, angles.x), angles.y);
            // The native controller stores its angles, while the actual tool camera may
            // be separately serialized. Stage its public transform as well; acceptance
            // still requires the native tool's later polling of this exact camera ray.
            camera.transform.rotation = desiredCamera;
            lookDiagnostics = "cameraPath=" + BoundedPath(camera.transform) + "/controllerPath=" + BoundedPath(controller.transform) +
                "/cameraChildOfController=" + child + "/desiredDirection=" + direction.normalized.ToString("F3") +
                "/actualDirection=" + camera.transform.forward.ToString("F3");
        }

        internal static bool TryStage(Gameplay gameplay, Camera? camera, Collider target,
            Vector3[] sides, int mask, out Vector3 stagedSide, out float stagedDistance)
        {
            stagedSide = Vector3.zero; stagedDistance = 0;
            if (camera == null) throw new InvalidOperationException("Native repair kit camera is unavailable.");
            var walker = gameplay.PlayerWalking;
            var body = walker.GetComponent<Collider>();
            var mover = walker.mover;
            if (body == null || !body.enabled || body.isTrigger || mover == null || !mover.IsGrounded() || mover.GetGroundCollider() == null)
            { LastDiagnostics = "requires actual grounded native walking body"; return false; }
            // Preserve the native standing step clearance, rather than putting the
            // standing player underground to align an eye-level ray with a low panel.
            float footClearance = Mathf.Max(0f, body.bounds.min.y - mover.GetGroundPoint().y);
            int bodyMask = 0;
            for (int layer = 0; layer < 32; layer++)
                if (!Physics.GetIgnoreLayerCollision(body.gameObject.layer, layer) && layer != LayerMask.NameToLayer("Ignore Raycast"))
                    bodyMask |= 1 << layer;
            int surfaceHits = 0, supported = 0, cameraHits = 0;
            string lastRay = "none";
            string rejection = "none";
            Bounds bounds = target.bounds;
            float reach = bounds.extents.magnitude + 1f;
            foreach (Vector3 horizontal in sides)
            foreach (float elevation in new[] { 0.75f, 1.25f, 0.35f })
            foreach (float standOff in new[] { 1.2f, 1.65f, 2.05f })
            {
                Vector3 direction = (horizontal + Vector3.up * elevation).normalized;
                if (!target.Raycast(new Ray(bounds.center + direction * reach, -direction), out var surface, reach * 2f))
                { rejection = "target surface ray missed"; continue; }
                surfaceHits++;
                Vector3 origin = surface.point + direction * standOff;
                SetLook(gameplay, camera, surface.point - origin);
                Move(gameplay, origin - (camera.transform.position - walker.transform.position));
                bool viable = true;
                // Rotation can change the eye offset. Settle the real standing body,
                // then aim from that actual eye position, without freezing physics.
                for (int pass = 0; pass < 3; pass++)
                {
                    Physics.SyncTransforms();
                    if (!TrySupport(body, walker.transform, bodyMask, footClearance, out var ground))
                    { rejection = "standing support missing"; viable = false; break; }
                    float adjustment = ground.y + footClearance - body.bounds.min.y;
                    Move(gameplay, walker.transform.position + Vector3.up * adjustment);
                    SetLook(gameplay, camera, surface.point - camera.transform.position);
                }
                Physics.SyncTransforms();
                if (!viable || !TrySupport(body, walker.transform, bodyMask, footClearance, out var finalGround) ||
                    Mathf.Abs(body.bounds.min.y - finalGround.y - footClearance) > 0.04f ||
                    Penetrates(body, walker.transform, bodyMask))
                { if (viable) rejection = "final support, foot clearance, or penetration check failed"; continue; }
                supported++;
                Ray nativeRay = camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
                bool hitAny = Physics.Raycast(nativeRay, out var hit, 2.4f, mask);
                lastRay = "camera=" + camera.name + "/origin=" + nativeRay.origin.ToString("F3") +
                    "/direction=" + nativeRay.direction.ToString("F3") + "/bodyBottom=" + body.bounds.min.y.ToString("F3") +
                    "/ground=" + finalGround.y.ToString("F3") + "/nativeFootClearance=" + footClearance.ToString("F3") +
                    "/hit=" + (hitAny ? hit.collider.name + "@" + hit.distance.ToString("F3") : "none");
                if (!hitAny || hit.collider != target)
                { rejection = hitAny ? "native ray hit another collider" : "native ray missed"; continue; }
                cameraHits++;
                LastDiagnostics = "verified standing " + lastRay + "; " + lookDiagnostics;
                stagedSide = direction; stagedDistance = hit.distance;
                return true;
            }
            LastDiagnostics = "surfaceHits=" + surfaceHits + "/supported=" + supported + "/cameraHits=" + cameraHits +
                "; lastSupportedCandidateRay=" + lastRay + "; lastRejection=" + rejection +
                "; currentCameraPosition=" + camera.transform.position.ToString("F3") +
                "/currentCameraDirection=" + camera.transform.forward.ToString("F3") + "; " + lookDiagnostics;
            return false;
        }
        private static string BoundedPath(Transform value)
        {
            string path = value.name;
            for (int depth = 0; value.parent != null && depth < 8; depth++)
            {
                value = value.parent;
                path = value.name + "/" + path;
            }
            return path.Length <= 240 ? path : "..." + path.Substring(path.Length - 237);
        }
        private static void Move(Gameplay gameplay, Vector3 position)
        {
            gameplay.PlayerWalking.transform.position = position;
            if (gameplay.Get_Player_Rigidbody == null) return;
            gameplay.Get_Player_Rigidbody.position = position;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private static bool TrySupport(Collider body, Transform walker, int mask, float footClearance, out Vector3 ground)
        {
            ground = Vector3.zero;
            Bounds bounds = body.bounds;
            float best = float.MaxValue;
            // A body-top cast can select an overhead hood/roof and lift the actor
            // onto it. Search only beneath the collider's feet, within the measured
            // native standing step clearance; never snap to a plane through the body.
            float stepReach = Mathf.Max(0.05f, footClearance);
            Vector3 feet = new Vector3(bounds.center.x, bounds.min.y - footClearance, bounds.center.z);
            Vector3 origin = feet + Vector3.up * (footClearance + 0.02f);
            foreach (var hit in Physics.RaycastAll(origin,
                Vector3.down, footClearance + stepReach + 0.04f, mask, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider == body || hit.collider.transform.IsChildOf(walker) || hit.normal.y < 0.6f ||
                    hit.point.y > bounds.min.y + 0.02f || Mathf.Abs(hit.point.y - feet.y) > stepReach + 0.02f ||
                    hit.distance >= best) continue;
                best = hit.distance; ground = hit.point;
            }
            return best != float.MaxValue;
        }
        private static bool Penetrates(Collider body, Transform walker, int mask)
        {
            Bounds bounds = body.bounds;
            foreach (var other in Physics.OverlapBox(bounds.center, bounds.extents, Quaternion.identity, mask, QueryTriggerInteraction.Ignore))
            {
                if (other == body || other.transform.IsChildOf(walker)) continue;
                if (Physics.ComputePenetration(body, body.transform.position, body.transform.rotation,
                    other, other.transform.position, other.transform.rotation, out _, out float distance) && distance > 0.02f) return true;
            }
            return false;
        }

    }
}
