using System;
using System.Collections.Generic;
using UnityEngine;

namespace MonMP.Plugin.UI
{
    // Run before native gameplay/physics and legacy GUI. Unlike OnPostRender,
    // OnGUI follows the camera's image effects in the built-in render pipeline.
    [DefaultExecutionOrder(-32000)]
    internal sealed class PassengerRenderScope : MonoBehaviour
    {
        private struct ChildPose
        {
            internal Transform Child;
            internal Vector3 LocalPosition, WorldPosition;
            internal Quaternion LocalRotation, WorldRotation;
            internal bool RestoreLocal;
        }
        private readonly List<ChildPose> children = new List<ChildPose>(8);
        private Camera? view;
        private Transform? parent;
        private Vector3 nativeLocalPosition, appliedPosition;
        private bool ownsPose;
        private AudioListener? originalListener;
        private GameObject? listenerProxy;
        internal bool OwnsPose => ownsPose;

        internal void Bind(Camera camera)
        {
            if (view != camera) { Unbind(); view = camera; }
            useGUILayout = false;
            enabled = true;
        }
        internal void Apply(Vector3 position)
        {
            if (ownsPose || view == null) return;
            var eye = view.transform;
            EnsureListener();
            parent = eye.parent; nativeLocalPosition = eye.localPosition;
            children.Clear();
            for (int i = 0; i < eye.childCount; i++)
            {
                var child = eye.GetChild(i);
                children.Add(new ChildPose { Child = child, LocalPosition = child.localPosition, LocalRotation = child.localRotation,
                    WorldPosition = child.position, WorldRotation = child.rotation });
            }
            appliedPosition = position;
            ownsPose = true;
            try
            {
                eye.position = position;
                // Equipped/held children and the listener stay at their actual
                // gameplay pose while the camera itself has its real render pose.
                foreach (var saved in children)
                    if (saved.Child != null && saved.Child.parent == eye)
                        saved.Child.SetPositionAndRotation(saved.WorldPosition, saved.WorldRotation);
            }
            catch { Restore(); throw; }
        }
        internal void Restore()
        {
            if (!ownsPose) return;
            ownsPose = false;
            if (view == null) { children.Clear(); return; }
            var eye = view.transform;
            bool ownerCurrent = eye.parent == parent && (eye.position - appliedPosition).sqrMagnitude < .000001f;
            if (ownerCurrent)
            {
                for (int i = 0; i < children.Count; i++)
                {
                    var saved = children[i];
                    if (saved.Child == null || saved.Child.parent != eye) continue;
                    saved.RestoreLocal = (saved.Child.position - saved.WorldPosition).sqrMagnitude < .000001f &&
                        Quaternion.Angle(saved.Child.rotation, saved.WorldRotation) < .001f;
                    saved.WorldPosition = saved.Child.position; saved.WorldRotation = saved.Child.rotation;
                    children[i] = saved;
                }
                // Children added by another render callback also keep their
                // current world pose when the eye moves back.
                for (int i = 0; i < eye.childCount; i++)
                {
                    var child = eye.GetChild(i);
                    bool known = false;
                    foreach (var saved in children) if (saved.Child == child) { known = true; break; }
                    if (!known) children.Add(new ChildPose { Child = child, WorldPosition = child.position, WorldRotation = child.rotation });
                }
                eye.localPosition = nativeLocalPosition;
                foreach (var saved in children)
                {
                    if (saved.Child == null || saved.Child.parent != eye) continue;
                    if (saved.RestoreLocal) { saved.Child.localPosition = saved.LocalPosition; saved.Child.localRotation = saved.LocalRotation; }
                    else saved.Child.SetPositionAndRotation(saved.WorldPosition, saved.WorldRotation);
                }
            }
            children.Clear();
        }
        private void EnsureListener()
        {
            if (view == null || listenerProxy != null) return;
            var listener = view.GetComponent<AudioListener>();
            if (listener == null || !listener.enabled) return;
            originalListener = listener;
            var proxy = new GameObject("MonMP passenger listener");
            proxy.transform.SetParent(view.transform, false);
            listener.enabled = false;
            try
            {
                proxy.AddComponent<AudioListener>().velocityUpdateMode = listener.velocityUpdateMode;
                listenerProxy = proxy;
            }
            catch
            {
                listener.enabled = true; originalListener = null;
                UnityEngine.Object.Destroy(proxy); throw;
            }
        }
        internal void Unbind()
        {
            Restore();
            if (listenerProxy != null)
            {
                var listener = listenerProxy.GetComponent<AudioListener>();
                if (listener != null) listener.enabled = false;
                UnityEngine.Object.Destroy(listenerProxy); listenerProxy = null;
            }
            if (originalListener != null && !originalListener.enabled) originalListener.enabled = true;
            originalListener = null; view = null; parent = null;
            enabled = false;
        }
        private void FixedUpdate() => Restore();
        private void Update() => Restore();
        private void OnGUI() => Restore();
        private void OnDisable() => Restore();
        private void OnDestroy() => Unbind();
    }
}
