using System;
using System.Reflection;
using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    // Only the host runs the combustion tick, which spends fuel and evaluates the kill switch.
    [HarmonyPatch(typeof(HomeGenerator), "Tick")]
    internal static class HomeGeneratorTickPatch
    {
        private static bool Prefix() => MonMPPlugin.Instance?.World?.Generator.GuestBlocks != true;
    }

    // Pulling the guest's cord requests a host-side native start, retaining fuel and switch checks.
    [HarmonyPatch(typeof(HomeGenerator), nameof(HomeGenerator.StartEngine))]
    internal static class HomeGeneratorStartPatch
    {
        private static bool Prefix(HomeGenerator __instance) => MonMPPlugin.Instance?.World?.Generator.InterceptStart(__instance) != true;
    }

    // Hydro availability is an authoritative input to home power. Guests keep their own HydroData,
    // but native consumers see the host's value and the matching combustion-or-hydro power query.
    [HarmonyPatch]
    internal static class HomeGeneratorHydroQueryPatch
    {
        private static MethodBase TargetMethod() => AccessTools.PropertyGetter(typeof(HomeGenerator), nameof(HomeGenerator.Get_IsHydroRunning))
            ?? throw new InvalidOperationException("HomeGenerator.Get_IsHydroRunning getter is missing.");

        private static bool Prefix(HomeGenerator __instance, ref bool __result)
        {
            var shared = MonMPPlugin.Instance?.World?.Generator;
            if (shared == null || !shared.TryGetHydroRunning(__instance, out bool running)) return true;
            __result = running;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class HomeGeneratorPoweredQueryPatch
    {
        private static MethodBase TargetMethod() => AccessTools.PropertyGetter(typeof(HomeGenerator), nameof(HomeGenerator.Get_isEngineRunning))
            ?? throw new InvalidOperationException("HomeGenerator.Get_isEngineRunning getter is missing.");

        private static bool Prefix(HomeGenerator __instance, ref bool __result)
        {
            var shared = MonMPPlugin.Instance?.World?.Generator;
            if (shared == null || !shared.TryGetHydroRunning(__instance, out bool hydroRunning)) return true;
            __result = __instance.Get_IsCombustionEngineRunning || hydroRunning;
            return false;
        }
    }
}
