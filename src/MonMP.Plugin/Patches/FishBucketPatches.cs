using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // Both manual spherecast pickup and native collision collection converge on Add_Fish.
    // The adapter suppresses the guest mutation and applies only a host-approved membership.
    [HarmonyPatch(typeof(Tool_FishBucket), nameof(Tool_FishBucket.Add_Fish))]
    internal static class SharedFishBucketAddPatch
    {
        private static bool Prefix(Tool_FishBucket __instance, Item_Fish _fish, ref bool __result)
        {
            var buckets = MonMPPlugin.Instance?.World?.FishBuckets;
            return buckets == null || buckets.AddFishPrefix(__instance, _fish, ref __result);
        }
    }

    // Keep the native Add_Fish success result, but let the authoritative action own its
    // one-shot. Each successful spherecast fish still produces one cue; physics races do not.
    [HarmonyPatch]
    internal static class SharedFishBucketCollectionSoundScopePatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(Tool_FishBucket), nameof(Tool_FishBucket.MouseDown));
            yield return AccessTools.Method(typeof(Tool_FishBucket), "OnCollisionEnter", new[] { typeof(Collision) });
            yield return AccessTools.Method(typeof(Tool_FishBucket), "OnTriggerEnter", new[] { typeof(Collider) });
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static void Prefix(out IDisposable? __state) => __state =
            MonMPPlugin.Instance?.World?.FishBuckets?.Active == true ? FishBucketPresentation.BeginNativeCollection() : null;
        private static void Postfix(IDisposable? __state) => __state?.Dispose();
        private static Exception? Finalizer(Exception? __exception, IDisposable? __state)
        { __state?.Dispose(); return __exception; }
    }

    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.PlayOneShot2D))]
    [HarmonyPriority(Priority.First)]
    internal static class SharedFishBucketCollectionSoundPatch
    {
        private static bool Prefix(SOUND_Manager __instance, AudioClip _clip) =>
            FishBucketPresentation.AllowNativeCollectionSound(__instance, _clip);
    }

    // MouseRightDown removes the first fish. The expected fish identity travels with the
    // request, so a stale input cannot eject a different item after a concurrent change.
    [HarmonyPatch(typeof(Tool_FishBucket), "MouseRightDown")]
    internal static class SharedFishBucketRemovePatch
    {
        private static bool Prefix(Tool_FishBucket __instance) =>
            MonMPPlugin.Instance?.World?.FishBuckets?.RemoveFishInput(__instance) ?? true;
    }

    // Both kiosk overloads pay and destroy fish locally: only the host's run, on the shared wallet.
    [HarmonyPatch]
    internal static class SharedLooseFishSalePatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(Store_FarmKiosque), "Receive", new[] { typeof(Item_Fish) })
            ?? throw new System.InvalidOperationException("Native loose fish sale method is missing.");
        private static bool Prefix(Item_Fish __0) =>
            !(MonMPPlugin.Instance?.World?.FishBuckets?.BlockFishSale() ?? false);
    }

    [HarmonyPatch]
    internal static class SharedFishBucketSalePatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(Store_FarmKiosque), "Receive", new[] { typeof(Tool_FishBucket) })
            ?? throw new System.InvalidOperationException("Native fish bucket sale method is missing.");
        private static bool Prefix(Tool_FishBucket __0) =>
            !(MonMPPlugin.Instance?.World?.FishBuckets?.BlockFishSale() ?? false);
    }
}
