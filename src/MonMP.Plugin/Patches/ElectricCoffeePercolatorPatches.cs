using System;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    // The coffee-brewing child is an Interactable, not an InteractableItems entry, so its clicks
    // bypass SharedItems.Use. Route its two guest callbacks through the electric parent's item id.
    internal static class ElectricCoffeePercolatorGuestGuard
    {
        private static readonly FieldInfo ElectricParent = AccessTools.Field(typeof(Item_CoffeePercolator), "ElectricCoffeePercolator")
            ?? throw new MissingFieldException(typeof(Item_CoffeePercolator).FullName, "ElectricCoffeePercolator");

        internal static bool AllowNative(Item_CoffeePercolator percolator, ElectricPercolatorAction action)
        {
            var plugin = MonMPPlugin.Instance;
            var session = plugin?.Session;
            if (plugin?.World?.ElectricPercolator?.AllowHostNativeUse(percolator) == false) return false;
            bool hasElectricParent = ElectricParent.GetValue(percolator) is Item_ElectricCoffeePercolator;
            if (!ElectricCoffeePercolatorPolicy.BlocksGuestInteraction(session?.IsRunning == true, session?.IsHost == true, hasElectricParent))
                return true;

            if (plugin?.World?.ElectricPercolator?.GuestUse(percolator, action) != true)
                plugin?.ReportItemUseUnavailable("The electric percolator is unavailable until the shared world is ready.");
            return false;
        }
    }

    [HarmonyPatch(typeof(Item_CoffeePercolator), nameof(Item_CoffeePercolator.MouseDown))]
    internal static class ElectricCoffeePercolatorBrewPatch
    {
        private static bool Prefix(Item_CoffeePercolator __instance) =>
            ElectricCoffeePercolatorGuestGuard.AllowNative(__instance, ElectricPercolatorAction.BrewOrServe);
        private static void Postfix(Item_CoffeePercolator __instance) =>
            MonMPPlugin.Instance?.World?.ElectricPercolator?.NativeMouseDownCompleted(__instance);
    }

    [HarmonyPatch(typeof(Item_CoffeePercolator), nameof(Item_CoffeePercolator.Use))]
    internal static class ElectricCoffeePercolatorPowerPatch
    {
        private static bool Prefix(Item_CoffeePercolator __instance, ref bool __result)
        {
            if (ElectricCoffeePercolatorGuestGuard.AllowNative(__instance, ElectricPercolatorAction.Power)) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Item_CoffeePercolator), nameof(Item_CoffeePercolator.Start))]
    internal static class ElectricCoffeePercolatorStartPatch
    {
        private static void Postfix(Item_CoffeePercolator __instance)
        {
            ElectricPercolatorPresentation.NativeStarted(__instance);
            MonMPPlugin.Instance?.World?.ElectricPercolator?.NativeStartCompleted(__instance);
        }
    }

    [HarmonyPatch(typeof(Item_CoffeePercolator), "BrewingCoffee")]
    internal static class ElectricCoffeePercolatorBrewClockPatch
    {
        // Observe creation only. The original iterator still performs all host gameplay effects.
        private static void Postfix(Item_CoffeePercolator __instance) =>
            ElectricPercolatorPresentation.NativeBrewCreated(__instance);
    }
}
