using System.Collections;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(SOUND_SugarShack), nameof(SOUND_SugarShack.PressSyrup))]
    internal static class SugarShackPressSoundPatch
    {
        private static void Postfix(SOUND_SugarShack __instance) => MonMPPlugin.Instance?.World?.SugarShack.SyrupPressed(__instance);
    }
    [HarmonyPatch(typeof(ItemForSale_SugarShack), nameof(ItemForSale_SugarShack.MouseDown))]
    internal static class SugarShackUpgradePurchasePatch
    {
        private static bool Prefix(ItemForSale_SugarShack __instance) => MonMPPlugin.Instance?.World?.SugarShack.Purchase(__instance) != true;
    }
    // Quote only the hovered registered upgrade from the shared wallet. MouseDown still routes
    // through the host, and native local purchase/debit guards remain active outside this scope.
    [HarmonyPatch(typeof(ItemForSale_SugarShack), nameof(ItemForSale_SugarShack.MouseHover))]
    internal static class SugarShackPriceHoverPatch
    {
        private static void Prefix(ItemForSale_SugarShack __instance, out ItemForSale_SugarShack? __state)
        {
            var shack = MonMPPlugin.Instance?.World?.SugarShack; __state = shack?.HoverSale;
            if (shack != null) shack.HoverSale = __instance;
        }
        private static void Finalizer(ItemForSale_SugarShack? __state)
        {
            var shack = MonMPPlugin.Instance?.World?.SugarShack;
            if (shack != null) shack.HoverSale = __state;
        }
    }
    // Only the host's shack runs the minute tick and the daily tree efficiency roll.
    [HarmonyPatch(typeof(SugarShack), "Tick")]
    internal static class SugarShackTickPatch
    {
        private static bool Prefix() => MonMPPlugin.Instance?.World?.SugarShack.GuestBlocks != true;
        private static void Postfix(SugarShack __instance) => MonMPPlugin.Instance?.World?.SugarShack.Ticked(__instance);
    }
    [HarmonyPatch(typeof(SugarShack), "DayChanged")]
    internal static class SugarShackDayPatch
    {
        private static bool Prefix() => MonMPPlugin.Instance?.World?.SugarShack.GuestBlocks != true;
    }
    // A guest's valves and switches become host commands.
    [HarmonyPatch(typeof(SugarShack), nameof(SugarShack.ToggleValve_TankToEvap))]
    internal static class SugarShackWaterValvePatch
    {
        private static bool Prefix(SugarShack __instance, ref bool __result) => MonMPPlugin.Instance?.World?.SugarShack.Valve(__instance, ShackOp.ValveEvap, ref __result) != true;
    }
    [HarmonyPatch(typeof(SugarShack), nameof(SugarShack.ToggleValve_SyrupToBaril))]
    internal static class SugarShackSyrupValvePatch
    {
        private static bool Prefix(SugarShack __instance, ref bool __result) => MonMPPlugin.Instance?.World?.SugarShack.Valve(__instance, ShackOp.ValveSyrup, ref __result) != true;
    }
    [HarmonyPatch(typeof(SugarShack), nameof(SugarShack.ElectricAutoHeater_TurnOn))]
    internal static class SugarShackHeaterOnPatch
    {
        private static bool Prefix(SugarShack __instance) => MonMPPlugin.Instance?.World?.SugarShack.Intercept(__instance, ShackOp.HeaterOn) != true;
    }
    [HarmonyPatch(typeof(SugarShack), nameof(SugarShack.ElectricAutoHeater_TurnOff))]
    internal static class SugarShackHeaterOffPatch
    {
        private static bool Prefix(SugarShack __instance) => MonMPPlugin.Instance?.World?.SugarShack.Intercept(__instance, ShackOp.HeaterOff) != true;
    }
    [HarmonyPatch(typeof(SugarShack), nameof(SugarShack.OutsidePump_TurnOn))]
    internal static class SugarShackPumpOnPatch
    {
        private static bool Prefix(SugarShack __instance) => MonMPPlugin.Instance?.World?.SugarShack.Intercept(__instance, ShackOp.PumpOn) != true;
    }
    [HarmonyPatch(typeof(SugarShack), nameof(SugarShack.OutsidePump_TurnOff))]
    internal static class SugarShackPumpOffPatch
    {
        private static bool Prefix(SugarShack __instance) => MonMPPlugin.Instance?.World?.SugarShack.Intercept(__instance, ShackOp.PumpOff) != true;
    }
    // A guest's evaporator fire heats the host's evaporator.
    [HarmonyPatch(typeof(SugarShack), nameof(SugarShack.WoodBurningInEvap))]
    internal static class SugarShackBurnPatch
    {
        private static bool Prefix(SugarShack __instance, float _power) => MonMPPlugin.Instance?.World?.SugarShack.Burn(__instance, _power) != true;
    }
    // The crimping machine and the build's log reception consume items; the host's copies run them,
    // triggered by the replicas of guests' cans, lids and logs.
    [HarmonyPatch(typeof(SugarShack_CrimpingMachine), "OnTriggerEnter")]
    internal static class SugarShackCrimpPatch
    {
        private static bool Prefix(SugarShack_CrimpingMachine __instance, Collider other)
        {
            var world = MonMPPlugin.Instance?.World;
            bool feedback = world?.TriggerFeedback.Crimper(__instance, other) == true;
            return world?.SugarShack.GuestBlocks != true && !feedback;
        }
    }
    // The host's build waits for every player, not only the host, to move back before the shack drops.
    [HarmonyPatch(typeof(SugarShack_SlabBuild), "BuildSugarShack")]
    internal static class SugarShackBuildCeremonyPatch
    {
        private static bool Prefix(SugarShack_SlabBuild __instance, ref IEnumerator __result)
        {
            var shack = MonMPPlugin.Instance?.World?.SugarShack;
            if (shack == null || !shack.HostBuild(__instance, out var routine)) return true;
            __result = routine; return false;
        }
    }
    [HarmonyPatch(typeof(SugarShack_ReceptionWoodLogs), "OnTriggerEnter")]
    internal static class SugarShackReceptionPatch
    {
        private static bool Prefix(Collider other) => other == null || other.tag == "Player" || MonMPPlugin.Instance?.World?.SugarShack.GuestBlocks != true;
    }
}
