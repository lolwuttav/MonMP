using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class RemoteMapCalibration
    {
        internal const string Key = "MonMP/HandProps/Map";
        internal const string OverlayName = "MonMP transient map player markers";

        internal static string ShapeOf(GameObject source)
        {
            var lines = new List<string> { "MonMP native hand-map v1" };
            foreach (var filter in source.GetComponentsInChildren<MeshFilter>(true))
            {
                if (filter.sharedMesh == null || IsOverlay(filter.transform)) continue;
                lines.Add(filter.name + "/" + filter.sharedMesh.name + "/" + filter.sharedMesh.vertexCount + "/" + filter.sharedMesh.subMeshCount);
            }
            lines.Sort(StringComparer.Ordinal);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)))).Replace("-", "");
        }
        private static bool IsOverlay(Transform node)
        { for (var current = node; current != null; current = current.parent) if (current.name == OverlayName) return true; return false; }
        internal static void RemoveOverlay(GameObject ownedCopy)
        {
            foreach (var node in ownedCopy.GetComponentsInChildren<Transform>(true))
                if (node != null && node.name == OverlayName) UnityEngine.Object.DestroyImmediate(node.gameObject);
        }
        internal static void Place(RemoteHandProps.Prop map, StockAvatarBody body, HeldGripOverrides.Grip? pose)
        {
            var center = body.MapCenter;
            map.Place(center, map.Thin, body.Head.position - center, map.Long, body.Aim * Vector3.right);
            if (pose == null) return;
            map.Object.transform.position += body.BodyRotation * pose.ItemPosition;
            map.Object.transform.rotation *= Quaternion.Euler(pose.ItemEuler);
        }
    }
}
