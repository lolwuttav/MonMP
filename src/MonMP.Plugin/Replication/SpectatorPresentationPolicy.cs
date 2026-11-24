using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Small world-point rules shared by the remote renderer and focused rendering regressions.
    // The map follows feet; a spectator name tag and range gate follow the floating head.
    internal static class SpectatorPresentationPolicy
    {
        internal const float SpectatorNameOffset = 0.35f;

        internal static Vector3 MapAnchor(Vector3 currentRoot, Vector3 lastRenderedRoot, bool spectating, bool hasLastRenderedRoot) =>
            spectating && hasLastRenderedRoot ? lastRenderedRoot : currentRoot;

        internal static Vector3 NameTagPosition(Vector3 root, Vector3 head, bool spectating, float ordinaryHeight) =>
            spectating ? head + Vector3.up * SpectatorNameOffset : root + Vector3.up * ordinaryHeight;

        internal static Vector3 NameTagDistanceOrigin(Vector3 root, Vector3 head, bool spectating) =>
            spectating ? head : root;
    }
}
