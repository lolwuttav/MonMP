using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // Each native pour step (gas can or pump gun) runs inside its coroutine's MoveNext: mark it so
    // tank level changes made during that step are reported as a pour, never engine burn or applies.
    [HarmonyPatch]
    internal static class FuelPourPatch
    {
        internal static IEnumerable<MethodInfo> Targets()
        {
            yield return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(Tool_FuelCanister), "LookingForContainerHole"));
            yield return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(Kaligaz_FuelGun), "LookingForContainerHole"));
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static void Prefix(object __instance) => SharedFuel.InPour = Traverse.Create(__instance).Field("<>4__this").GetValue() as Component;
        private static void Finalizer() => SharedFuel.InPour = null;
    }
    [HarmonyPatch(typeof(Part_FuelTank), "set_Get_FuelLevel")]
    internal static class FuelLevelPatch
    {
        private static void Prefix(Part_FuelTank __instance, out float __state) => __state = SharedFuel.InPour != null ? __instance.Get_FuelLevel : 0;
        private static void Postfix(Part_FuelTank __instance, float __state)
        {
            if (SharedFuel.InPour != null) MonMPPlugin.Instance?.World?.Fuel.LevelChanged(__instance, __state, __instance.Get_FuelLevel);
        }
    }
}
