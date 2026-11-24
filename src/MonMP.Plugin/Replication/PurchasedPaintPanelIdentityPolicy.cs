using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class PurchasedPaintPanelIdentityPolicy
    {
        // A transform-relative path ignores clone names and scene placement. Append the
        // component ordinal so multiple native save roots on one GameObject stay distinct.
        internal static bool TryRelativePath(Transform vehicleRoot, Vehicle_PaintablePanel panel,
            out string relativePath)
        {
            relativePath = "";
            if (vehicleRoot == null || panel == null || panel.transform == null) return false;
            var segments = new List<string>();
            var current = panel.transform;
            while (current != vehicleRoot)
            {
                if (current == null) return false;
                segments.Add(StaticId.Segment(current.name, current.GetSiblingIndex()));
                current = current.parent;
            }
            segments.Reverse();
            var components = panel.GetComponents<Vehicle_PaintablePanel>();
            int ordinal = Array.IndexOf(components, panel);
            if (ordinal < 0) return false;
            segments.Add("@Vehicle_PaintablePanel[" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]");
            relativePath = string.Join("/", segments);
            return true;
        }
    }
}
