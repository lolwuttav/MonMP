using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class DecorationState
    {
        private static readonly FieldInfo canFreeze = AccessTools.Field(typeof(Furniture_Decoration), "CanBeFreeze");
        private static readonly FieldInfo heldObjects = AccessTools.Field(typeof(Furniture_Decoration), "ToDisableWhenGrab");
        private static readonly FieldInfo center = AccessTools.Field(typeof(Furniture_Decoration), "CustomCenterOfMass");
        internal static bool CanFreeze(Furniture_Decoration item) => (bool)canFreeze.GetValue(item);
        internal static GameObject[] HeldObjects(Furniture_Decoration item) => (GameObject[])heldObjects.GetValue(item);
        internal static void ValidatePrefab(Furniture_Decoration item)
        {
            var objects = HeldObjects(item);
            if (objects == null) throw new InvalidOperationException("Missing decoration held-object array: " + item.PrefabName);
            foreach (var obj in objects)
                if (obj == null || obj == item.gameObject || !obj.transform.IsChildOf(item.transform))
                    throw new InvalidOperationException("Decoration held object is outside its prefab: " + item.PrefabName);
        }
        internal static void Presentation(Furniture_Decoration item, bool held)
        {
            foreach (var obj in HeldObjects(item)) if (obj != null && obj.activeSelf == held) obj.SetActive(!held);
            var customCenter = (Transform?)center.GetValue(item);
            if (!held && item.rb != null && customCenter != null)
            {
                var target = customCenter.localPosition;
                // Native decoration Start/UnGrab sets this on transitions. Host
                // capture revisits resting decorations at 10 Hz; avoid repeating
                // the physics write, retaining even small legitimate changes.
                if (!item.rb.centerOfMass.Equals(target)) item.rb.centerOfMass = target;
            }
        }
    }
}
