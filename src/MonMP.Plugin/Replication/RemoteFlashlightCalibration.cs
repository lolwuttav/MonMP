using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class RemoteFlashlightCalibration
    {
        internal const string Key = "MonMP/HandProps/Flashlight";
        // Called after the ordinary left-arm pose, before applying a saved palm target. Keeping
        // this frame avoids feeding the calibrated palm back into placement during prop update.
        internal static void Place(RemoteHandProps.Prop prop, StockAvatarBody body, HeldGripOverrides.Grip? pose)
        {
            var forward = body.Aim * Vector3.forward; var up = body.Aim * Vector3.up;
            var axis = prop.Beam ?? prop.Long;
            prop.Place(body.LeftPalm + forward * Mathf.Clamp(prop.Length * .3f, .02f, .08f) - up * .015f,
                axis, forward, RemoteHandProps.Perpendicular(prop, axis), up);
            if (pose != null)
            {
                prop.Object.transform.position += body.BodyRotation * pose.ItemPosition;
                prop.Object.transform.rotation *= Quaternion.Euler(pose.ItemEuler);
            }
            prop.CalibrationFrame = Time.frameCount;
        }
    }
}
