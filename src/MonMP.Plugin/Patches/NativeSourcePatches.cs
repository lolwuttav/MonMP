using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    // Native initialization reports for the component types whose recurring recovery
    // searches read NativeSources instead of walking the native object registry. Separate
    // postfixes: the adapters' own Start patches keep their existing behavior.
    internal static class NativeSourcePatches
    {
        // Tubes declare no Unity lifecycle message; their completed-placement setter is the
        // only report, so their reconciliation walk stays at the item registry's ten seconds.
        private const float TubeFallback = 10f;

        // Called for each new world, before its adapters run their initial discovery.
        internal static void Begin()
        {
            NativeSources<Vehicle_Underglow>.Track();
            NativeSources<Vehicle_Nitrous_Controls>.Track();
            NativeSources<EntreposageGarage_Door>.Track();
            NativeSources<WoodSplitter>.Track();
            NativeSources<Furniture_Radio>.Track();
            NativeSources<Garden_DirtPatch>.Track();
            NativeSources<SugarShack_TubingPost>.Track();
            NativeSources<Tree_Growing>.Track();
            NativeSources<SugarShack_Tubing>.Track(TubeFallback);
            NativeSourceTracking.Log = UnityEngine.Debug.LogWarning;
            if (!sceneHooked)
            {
                // A loaded scene (the guest's world load included) brings inactive components
                // that never report a Start; the next search of each type walks again.
                sceneHooked = true;
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) => NativeSourceTracking.Reset();
            }
            NativeSourceTracking.Reset();
        }
        private static bool sceneHooked;

        internal static IEnumerable<MethodInfo> Targets()
        {
            yield return AccessTools.DeclaredMethod(typeof(Vehicle_Underglow), "Start");
            yield return AccessTools.DeclaredMethod(typeof(Vehicle_Nitrous_Controls), "Start");
            yield return AccessTools.DeclaredMethod(typeof(EntreposageGarage_Door), "Start");
            yield return AccessTools.DeclaredMethod(typeof(WoodSplitter), "Start");
            yield return AccessTools.DeclaredMethod(typeof(Furniture_Radio), "Start");
            yield return AccessTools.DeclaredMethod(typeof(Garden_DirtPatch), "Start");
            yield return AccessTools.DeclaredMethod(typeof(SugarShack_TubingPost), "Start");
            yield return AccessTools.DeclaredMethod(typeof(Tree_Growing), "Start");
            yield return AccessTools.DeclaredMethod(typeof(SugarShack_Tubing), "set_GetTarget", new[] { typeof(TubingConnectionPoint) });
        }
    }

    [HarmonyPatch(typeof(Vehicle_Underglow), "Start")]
    internal static class UnderglowSourcePatch
    {
        private static void Postfix(Vehicle_Underglow __instance) => NativeSources<Vehicle_Underglow>.Started(__instance);
    }
    [HarmonyPatch(typeof(Vehicle_Nitrous_Controls), "Start")]
    internal static class NitrousControlsSourcePatch
    {
        private static void Postfix(Vehicle_Nitrous_Controls __instance) => NativeSources<Vehicle_Nitrous_Controls>.Started(__instance);
    }
    [HarmonyPatch(typeof(EntreposageGarage_Door), "Start")]
    internal static class StorageGarageDoorSourcePatch
    {
        private static void Postfix(EntreposageGarage_Door __instance) => NativeSources<EntreposageGarage_Door>.Started(__instance);
    }
    [HarmonyPatch(typeof(WoodSplitter), "Start")]
    internal static class WoodSplitterSourcePatch
    {
        private static void Postfix(WoodSplitter __instance) => NativeSources<WoodSplitter>.Started(__instance);
    }
    [HarmonyPatch(typeof(Furniture_Radio), "Start")]
    internal static class FurnitureRadioSourcePatch
    {
        private static void Postfix(Furniture_Radio __instance) => NativeSources<Furniture_Radio>.Started(__instance);
    }
    [HarmonyPatch(typeof(Garden_DirtPatch), nameof(Garden_DirtPatch.Start))]
    internal static class GardenPatchSourcePatch
    {
        private static void Postfix(Garden_DirtPatch __instance) => NativeSources<Garden_DirtPatch>.Started(__instance);
    }
    [HarmonyPatch(typeof(SugarShack_TubingPost), nameof(SugarShack_TubingPost.Start))]
    internal static class TubingPostSourcePatch
    {
        private static void Postfix(SugarShack_TubingPost __instance) => NativeSources<SugarShack_TubingPost>.Started(__instance);
    }
    [HarmonyPatch(typeof(Tree_Growing), nameof(Tree_Growing.Start))]
    internal static class SaplingSourcePatch
    {
        private static void Postfix(Tree_Growing __instance) => NativeSources<Tree_Growing>.Started(__instance);
    }
    [HarmonyPatch(typeof(SugarShack_Tubing), "set_GetTarget", new[] { typeof(TubingConnectionPoint) })]
    internal static class TubingSourcePatch
    {
        private static void Postfix(SugarShack_Tubing __instance) => NativeSources<SugarShack_Tubing>.Started(__instance);
    }
}
