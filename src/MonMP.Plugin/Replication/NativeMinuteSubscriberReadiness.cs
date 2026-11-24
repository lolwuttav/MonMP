using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class NativeMinuteSubscriberReadiness
    {
        private static readonly FieldInfo DistancePlayer = AccessTools.Field(typeof(DisableWhenPlayerTooFar), "Player");

        internal static bool Prepare(MonoBehaviour target, Gameplay? gameplay)
        {
            if (!(target is DisableWhenPlayerTooFar) || DistancePlayer.GetValue(target) as Transform != null) return true;
            // A host snapshot can enable this component before its native Start runs.
            // Supply the same scene-owned player binding that Start assigns, without subscribing twice.
            var player = gameplay?.Player?.transform;
            if (player == null) return false;
            DistancePlayer.SetValue(target, player);
            return true;
        }
    }
}
