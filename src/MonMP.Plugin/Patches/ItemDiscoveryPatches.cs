using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class ItemDiscoveryLifecyclePatch
    {
        internal static IEnumerable<MethodInfo> Targets()
        {
            // Native audit: no item OnEnable exists. Include declared overrides,
            // even when they do not call base. Never patch an inherited Unity message.
            foreach (var type in typeof(InteractableItems).Assembly.GetTypes())
            {
                if (!typeof(InteractableItems).IsAssignableFrom(type)) continue;
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if ((method.Name == "Awake" || method.Name == "Start" || method.Name == "CreateBasics" ||
                         method.Name == "OnDestroy") && method.ReturnType == typeof(void) &&
                        method.GetParameters().Length == 0 && !method.IsAbstract)
                        yield return method;
            }
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static void Postfix(InteractableItems __instance, MethodBase __originalMethod)
        {
            if (__originalMethod.Name == "OnDestroy") NativeItemDiscovery.Destroyed(__instance);
            else NativeItemDiscovery.Changed(__instance);
        }
    }
    [HarmonyPatch(typeof(InteractableParts), nameof(InteractableParts.UpdatePartPlacement))]
    internal static class ItemDiscoveryPlacementPatch
    {
        private static void Postfix(InteractableParts __instance) => NativeItemDiscovery.Changed(__instance);
    }
    [HarmonyPatch(typeof(Hardware_Bolt), nameof(Hardware_Bolt.RemovedFromTray))]
    internal static class ItemDiscoveryTrayRemovalPatch
    {
        private static void Postfix(Hardware_Bolt __instance) => NativeItemDiscovery.Changed(__instance);
    }
    [HarmonyPatch]
    internal static class ItemDiscoveryBoltRemovalPatch
    {
        internal static IEnumerable<MethodInfo> Targets()
        {
            yield return AccessTools.DeclaredMethod(typeof(BoltHoleDetails), nameof(BoltHoleDetails.Unscrewed));
            yield return AccessTools.DeclaredMethod(typeof(BoltHoleDetails), nameof(BoltHoleDetails.Detach));
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static void Postfix(BoltHoleDetails __instance)
        {
            var bolt = __instance.generated_BoltHole?.hardware_Bolt;
            if (bolt != null) NativeItemDiscovery.Changed(bolt);
        }
    }
}
