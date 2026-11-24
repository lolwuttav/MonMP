using UnityEngine;
using UnityEngine.AI;

namespace MonMP.Plugin.Replication
{
    // Keep a client's native AI destination close to its received feet position.
    // Shoreline wandering must not blindly pick a point behind the player in water.
    internal static class LeoFollowTarget
    {
        internal const float SampleRadius = 2f;

        internal static Vector3 Near(NavMeshAgent? agent, Vector3 desired, Vector3 fallback)
        {
            if (agent == null) return fallback;
            var filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = agent.areaMask };
            return NavMesh.SamplePosition(desired, out var hit, SampleRadius, filter) ? hit.position : fallback;
        }

        internal static Vector3 Roam(NavMeshAgent? agent, Vector3 player, Vector3 forward, Vector3 fallback)
        {
            if (agent == null) return fallback;
            var filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = agent.areaMask };
            var behind = player - forward * 3f + Vector3.up;
            if (NavMesh.SamplePosition(behind, out var hit, SampleRadius, filter)) return hit.position;
            if (NavMesh.SamplePosition(player, out hit, SampleRadius, filter)) return hit.position;
            return fallback;
        }
    }
}
