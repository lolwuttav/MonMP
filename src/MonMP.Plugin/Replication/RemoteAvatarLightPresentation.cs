using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class RemoteAvatarLightPresentation
    {
        private static readonly Vector3 FlashlightOffset = new Vector3(0.2f, -0.05f, 0.2f);

        internal static void Present(Light light, StockAvatarBody? body, Transform fallbackHead,
            RemoteHandProps? props, float amount, float pitch, float cameraYaw)
        {
            // Self lighting belongs to the native player, and an ordinary peer's disabled
            // beam has no placement work. Apply enables it before Update, so its first visible
            // frame still uses the latest solved hand/lens rather than an older hidden pose.
            if (!light.enabled) return;
            var look = Quaternion.Euler(pitch, cameraYaw, 0);
            var lens = amount > 0.5f ? props?.FlashlightLens : null;
            if (body != null && lens.HasValue)
            {
                var prop = props!.Flashlight!;
                var beam = prop.Object.transform.TransformDirection(prop.Beam ?? prop.Long);
                var beamRotation = Quaternion.LookRotation(beam, look * Vector3.up);
                light.transform.SetPositionAndRotation(lens.Value + beam * .02f, beamRotation);
            }
            else if (body != null && props?.Flashlight != null && amount > 0.5f)
                light.transform.SetPositionAndRotation(body.LeftHand.position + look * Vector3.forward * 0.15f, look);
            else
            {
                var head = body != null ? body.Head.position : fallbackHead.position;
                light.transform.SetPositionAndRotation(head + Quaternion.Euler(0, cameraYaw, 0) * FlashlightOffset, look);
            }
        }
    }
}
