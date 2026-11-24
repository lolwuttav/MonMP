using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class BunkerProductionButtonPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Home_Bunker), nameof(Home_Bunker.Button_FrenchFriesPress), Type.EmptyTypes);
            yield return AccessTools.Method(typeof(Home_Bunker), nameof(Home_Bunker.Button_BigPackMachine), Type.EmptyTypes);
            yield return AccessTools.Method(typeof(Home_Bunker), nameof(Home_Bunker.Button_HugePackMachine), new[] { typeof(bool) });
        }
        private static bool Prefix(Home_Bunker __instance, MethodBase __originalMethod, object[] __args)
        {
            byte action = __originalMethod.Name == nameof(Home_Bunker.Button_FrenchFriesPress) ? (byte)0 :
                __originalMethod.Name == nameof(Home_Bunker.Button_BigPackMachine) ? (byte)1 : (bool)__args[0] ? (byte)3 : (byte)2;
            if (SharedRemoteClicks.Active?.InterceptBunker(__instance, action) == true) return false;
            return !Guest;
        }
        internal static bool Guest => MonMPPlugin.Instance?.Session?.IsRunning == true && MonMPPlugin.Instance.Session.IsHost == false;
    }

    // Guard both admission and the producer factories. Serialized buttons and direct script
    // calls cannot spend guest replicas or publish an unfunded newly discovered output.
    [HarmonyPatch(typeof(Home_Bunker), "ScanForThisPlant")]
    internal static class BunkerProductionScanPatch
    {
        private static bool Prefix(ref bool __result)
        { if (!BunkerProductionButtonPatch.Guest) return true; __result = false; return false; }
    }

    [HarmonyPatch]
    internal static class BunkerProductionFactoryPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Home_Bunker), "BuildCannabisPack", new[] { typeof(bool), typeof(bool) });
            yield return AccessTools.Method(typeof(Home_Bunker), "BuildFrenchFriesPack", Type.EmptyTypes);
        }
        private static IEnumerator Empty() { yield break; }
        private static bool Prefix(ref IEnumerator __result)
        { if (!BunkerProductionButtonPatch.Guest) return true; __result = Empty(); return false; }
    }
}
