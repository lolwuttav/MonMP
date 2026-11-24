using System.Collections.Generic;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Stable gameplay identities shared with the development inventory reader.
    internal static class SceneIdentity
    {
        internal static string CanonicalPath(Transform transform)
        {
            var segments = new Stack<string>();
            for (var current = transform; current != null; current = current.parent)
                segments.Push(StaticId.Segment(current.name.Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r"), current.GetSiblingIndex()));
            var scene = transform.gameObject.scene;
            return StaticId.Segment(scene.path, scene.buildIndex) + "/" + string.Join("/", segments);
        }
    }
}
