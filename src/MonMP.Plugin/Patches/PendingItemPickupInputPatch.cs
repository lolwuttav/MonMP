using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class PendingItemPickupInputPatch
    {
        internal static IEnumerable<MethodInfo> Targets()
        {
            foreach (string name in new[] { "MouseLeftClickDown", "MouseLeftClickUp", "MouseRightClickDown", "MouseRightClickUp", "UseKeyDown", "UseKeyUp" })
                yield return AccessTools.Method(typeof(PlayerInteractable), name, Type.EmptyTypes);
            yield return AccessTools.Method(typeof(PlayerInteractable), "ScrollThisObject", new[] { typeof(float) });
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        [HarmonyPriority(Priority.First + 100)]
        private static bool Prefix(MethodBase __originalMethod, bool __runOriginal) =>
            __runOriginal && MonMPPlugin.Instance?.Items?.PendingPickupInput(__originalMethod.Name) != true;
    }
}
