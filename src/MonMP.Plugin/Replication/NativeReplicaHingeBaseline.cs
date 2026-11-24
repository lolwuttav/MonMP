using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Keep the native components themselves alive: recreating a saved-open hinge loses its
    // configured limits, spring and native private references. Only detach it while replicating.
    internal sealed class NativeReplicaHingeBaseline
    {
        private readonly Component native;
        private readonly Rigidbody? body;
        private readonly HingeJoint? hinge;
        private readonly Vector3 position, velocity, angularVelocity, connectedAnchor;
        private readonly Quaternion rotation;
        private readonly RigidbodyInterpolation interpolation;
        private readonly bool kinematic, autoAnchor, closeActive;
        private readonly Rigidbody? connectedBody;
        private readonly GameObject? closeButton;
        private readonly FieldInfo[] nativeFields;
        private readonly object?[] nativeValues;

        internal NativeReplicaHingeBaseline(Component native, GameObject? closeButton)
        {
            this.native = native; this.closeButton = closeButton;
            position = native.transform.localPosition; rotation = native.transform.localRotation;
            body = native.GetComponent<Rigidbody>(); hinge = native.GetComponent<HingeJoint>();
            if (body != null)
            {
                kinematic = body.isKinematic; interpolation = body.interpolation;
                velocity = body.velocity; angularVelocity = body.angularVelocity;
            }
            if (hinge != null)
            {
                connectedBody = hinge.connectedBody; connectedAnchor = hinge.connectedAnchor;
                autoAnchor = hinge.autoConfigureConnectedAnchor;
            }
            closeActive = closeButton != null && closeButton.activeSelf;
            string[] fields = { "isOpen", "isReleased", "isLockedFullyOpen", "Timer_JustOpened", "rb", "hj" };
            nativeFields = new FieldInfo[fields.Length]; nativeValues = new object?[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                nativeFields[i] = AccessTools.Field(native.GetType(), fields[i]) ??
                    throw new InvalidOperationException("Missing native hinge baseline field: " + fields[i]);
                nativeValues[i] = nativeFields[i].GetValue(native);
            }
        }

        internal void SuppressPhysics()
        {
            if (body != null) { body.isKinematic = true; body.interpolation = RigidbodyInterpolation.None; }
            if (hinge != null) { hinge.autoConfigureConnectedAnchor = false; hinge.connectedBody = null; }
            // Native OnDisable and installed/removed callbacks destroy their cached rb/hj.
            // Keep those callbacks from deleting the preserved offline components as visuals
            // activate/deactivate; native interaction/FixedUpdate is already host-only.
            nativeFields[4].SetValue(native, null); nativeFields[5].SetValue(native, null);
        }

        internal void Restore()
        {
            if (native == null) return;
            for (int i = 0; i < nativeFields.Length; i++) nativeFields[i].SetValue(native, nativeValues[i]);
            native.transform.localPosition = position; native.transform.localRotation = rotation;
            if (closeButton != null) closeButton.SetActive(closeActive);
            if (hinge != null)
            {
                hinge.autoConfigureConnectedAnchor = false; hinge.connectedBody = connectedBody;
                hinge.connectedAnchor = connectedAnchor; hinge.autoConfigureConnectedAnchor = autoAnchor;
            }
            if (body != null)
            {
                body.isKinematic = kinematic; body.interpolation = interpolation;
                body.velocity = velocity; body.angularVelocity = angularVelocity;
            }
        }
    }
}
