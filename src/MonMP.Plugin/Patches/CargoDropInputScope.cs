using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    // Only native player input can mark a release as a deliberate cargo drop.
    // Save/death/disconnect cleanup and lost-hold repair run outside this scope.
    [HarmonyPatch]
    internal static class CargoDropInputScope
    {
        private static int depth;
        internal static bool Active => depth > 0;
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in new[] { "MouseLeftClickDown", "MouseLeftClickUp", "MouseRightClickDown", "MouseRightClickUp" })
                yield return AccessTools.Method(typeof(PlayerInteractable), name, Type.EmptyTypes);
        }
        private static void Prefix(out bool __state) { depth++; __state = true; }
        private static Exception? Finalizer(Exception? __exception, bool __state)
        { if (__state) depth--; return __exception; }
    }
}
