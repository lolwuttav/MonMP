using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    internal static class FoodPrepPatchTargets
    {
        internal static IEnumerable<MethodInfo> Targets()
        {
            yield return AccessTools.Method(typeof(Furniture_FrenchFryCutter), nameof(Furniture_FrenchFryCutter.MouseDown))
                ?? throw new System.InvalidOperationException("French fry cutter interaction is missing.");
            yield return AccessTools.Method(typeof(Furniture_FrenchFryPackager), nameof(Furniture_FrenchFryPackager.MouseDown))
                ?? throw new System.InvalidOperationException("French fry packager interaction is missing.");
            yield return AccessTools.Method(typeof(Furniture_Fryer), nameof(Furniture_Fryer.PowerOnButton))
                ?? throw new System.InvalidOperationException("Fryer power callback is missing.");
            yield return AccessTools.Method(typeof(Furniture_Fryer), "OnTriggerEnter", new[] { typeof(UnityEngine.Collider) })
                ?? throw new System.InvalidOperationException("Fryer item input callback is missing.");
            yield return AccessTools.Method(typeof(Furniture_Fryer), "set_Get_PercentFiller")
                ?? throw new System.InvalidOperationException("Fryer fill state setter is missing.");
        }

    }

    [HarmonyPatch(typeof(Furniture_FrenchFryCutter), nameof(Furniture_FrenchFryCutter.MouseDown))]
    internal static class SharedFrenchFryCutterInputPatch
    {
        private static bool Prefix(Furniture_FrenchFryCutter __instance)
        {
            var prep = MonMPPlugin.Instance?.World?.FoodPrep;
            if (prep?.BlocksGuestMutation != true) return true;
            if (Replication.SharedRemoteClicks.Active?.Intercept(__instance) == true) return false;
            prep.ReportUnavailable();
            return false;
        }
    }

    [HarmonyPatch(typeof(Furniture_FrenchFryPackager), nameof(Furniture_FrenchFryPackager.MouseDown))]
    internal static class SharedFrenchFryPackagerInputPatch
    {
        private static bool Prefix(Furniture_FrenchFryPackager __instance)
        {
            var prep = MonMPPlugin.Instance?.World?.FoodPrep;
            if (prep?.BlocksGuestMutation != true) return true;
            if (Replication.SharedRemoteClicks.Active?.Intercept(__instance) == true) return false;
            prep.ReportUnavailable();
            return false;
        }
    }

    [HarmonyPatch(typeof(Furniture_Fryer), nameof(Furniture_Fryer.PowerOnButton))]
    internal static class SharedFryerPowerInputPatch
    {
        private static bool Prefix(Furniture_Fryer __instance)
        {
            var prep = MonMPPlugin.Instance?.World?.FoodPrep;
            if (prep?.BlocksGuestMutation != true) return true;
            if (Replication.SharedRemoteClicks.Active?.Intercept(__instance) == true) return false;
            prep.ReportUnavailable();
            return false;
        }
    }

    [HarmonyPatch(typeof(Furniture_Fryer), "OnTriggerEnter")]
    internal static class SharedFryerItemInputPatch
    {
        // A guest's fries or curds go to the host with that guest's facing (#20); the host's copy of an
        // item a guest holds is left to that guest's report.
        private static bool Prefix(Furniture_Fryer __instance, Collider other)
        {
            var prep = MonMPPlugin.Instance?.World?.FoodPrep;
            if (prep == null) return true;
            if (prep.BlocksGuestMutation) { prep.GuestInput(__instance, other); return false; }
            return prep.HostAllowsNativeInput(other);
        }
    }

    [HarmonyPatch(typeof(Furniture_Fryer), "set_Get_PercentFiller")]
    internal static class SharedFryerFillSetterPatch
    {
        private static bool Prefix(Furniture_Fryer __instance)
        {
            var prep = MonMPPlugin.Instance?.World?.FoodPrep;
            return prep?.AllowGuestPercentSetter(__instance) != false;
        }
    }

    // The host's validated cutter click plays this presentation-only native method. The
    // requester and observers receive the registered fryer's cue without repeating its debit.
    [HarmonyPatch(typeof(Furniture_Fryer), nameof(Furniture_Fryer.PressPotato), new System.Type[] { })]
    internal static class SharedFryerPressRelayPatch
    {
        private static void Postfix(Furniture_Fryer __instance) =>
            MonMPPlugin.Instance?.World?.FoodPrep.NativePress(__instance);
    }

    [HarmonyPatch(typeof(SOUND_Furniture_Fryer), nameof(SOUND_Furniture_Fryer.Close), new System.Type[] { })]
    internal static class SharedFryerCloseRelayPatch
    {
        private static void Postfix(SOUND_Furniture_Fryer __instance) =>
            MonMPPlugin.Instance?.World?.FoodPrep.NativeCue(__instance, 1);
    }

    [HarmonyPatch(typeof(SOUND_Furniture_Fryer), nameof(SOUND_Furniture_Fryer.Open), new System.Type[] { })]
    internal static class SharedFryerOpenRelayPatch
    {
        private static void Postfix(SOUND_Furniture_Fryer __instance) =>
            MonMPPlugin.Instance?.World?.FoodPrep.NativeCue(__instance, 2);
    }

    [HarmonyPatch(typeof(SOUND_Furniture_Fryer), nameof(SOUND_Furniture_Fryer.BuildPoutine), new System.Type[] { })]
    internal static class SharedFryerScoopRelayPatch
    {
        private static void Postfix(SOUND_Furniture_Fryer __instance) =>
            MonMPPlugin.Instance?.World?.FoodPrep.NativeCue(__instance, 3);
    }
}
