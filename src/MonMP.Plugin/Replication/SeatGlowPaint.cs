using System;
using System.Collections.Generic;
using System.Reflection;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Paint remains owned by native panels. Only their already-bound render surfaces
    // need a notification; no scene scans, extra paint identities or polling are added.
    internal static class SeatGlowPaint
    {
        private static readonly Dictionary<MeshRenderer, Action> surfaces = new Dictionary<MeshRenderer, Action>();
        private static readonly FieldInfo Related = typeof(Vehicle_PaintablePanel).GetField("PannelsRelated",
            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new InvalidOperationException("Native paint surfaces unavailable.");

        internal static void Bind(MeshRenderer renderer, Action refresh) => surfaces.Add(renderer, refresh);
        internal static void Unbind(MeshRenderer renderer, Action refresh)
        {
            if (surfaces.TryGetValue(renderer, out var current) && current == refresh) surfaces.Remove(renderer);
        }

        internal static void Changed(Vehicle_PaintablePanel panel, int depth = 0)
        {
            if (surfaces.Count == 0 || panel == null || depth >= PaintPanelState.MaxDepth) return;
            if (Related.GetValue(panel) is PaintablePanel_Informations[] related)
                foreach (var info in related)
                    if (info != null && info.mesh != null && surfaces.TryGetValue(info.mesh, out var refresh)) refresh();
            if (panel.PannelAssociatedToSave != null) Changed(panel.PannelAssociatedToSave, depth + 1);
        }
    }
}
