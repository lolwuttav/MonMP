using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    // A guest's post and spout placement spends the held tool on the host.
    [HarmonyPatch(typeof(Tool_TubingPost), nameof(Tool_TubingPost.MouseDown))]
    internal static class TubingPostPlacePatch
    {
        private static bool Prefix(Tool_TubingPost __instance) => MonMPPlugin.Instance?.World?.Tubing.Place(__instance) != true;
    }
    [HarmonyPatch(typeof(Tool_TubingChalumeau), nameof(Tool_TubingChalumeau.MouseDown))]
    internal static class TubingSpoutPlacePatch
    {
        private static bool Prefix(Tool_TubingChalumeau __instance, out UnityEngine.Vector3? __state)
        {
            var tubing = MonMPPlugin.Instance?.World?.Tubing;
            bool runNative = tubing?.Place(__instance) != true;
            __state = runNative && tubing?.ObserveHostNativeSpout == true ? __instance.transform.position : (UnityEngine.Vector3?)null;
            return runNative;
        }
        private static void Postfix(UnityEngine.Vector3? __state, bool __runOriginal)
        {
            if (__runOriginal && __state.HasValue) MonMPPlugin.Instance?.World?.Tubing.AfterNativeSpoutPlacement(__state.Value);
        }
    }
    [HarmonyPatch(typeof(SugarShack_TubingPost), nameof(SugarShack_TubingPost.MouseRightDown))]
    internal static class TubingPostRemovePatch
    {
        private static bool Prefix(SugarShack_TubingPost __instance, out bool __state)
        {
            bool runNative = MonMPPlugin.Instance?.World?.Tubing.RemovePost(__instance) != true;
            __state = runNative && __instance != null && __instance.CanBeRemoved;
            return runNative;
        }
        private static void Postfix(SugarShack_TubingPost __instance, bool __state, bool __runOriginal)
        {
            if (__runOriginal && __state) MonMPPlugin.Instance?.World?.Tubing.NativePostRemoved(__instance);
        }
    }
    // The roll's finished tube and its tube removal are mirrored to the host.
    [HarmonyPatch(typeof(Tool_TubingRoll), "TubeConnected")]
    internal static class TubingConnectedPatch
    {
        private static void Postfix(Tool_TubingRoll __instance) => MonMPPlugin.Instance?.World?.Tubing.AfterConnect(__instance);
    }
    [HarmonyPatch(typeof(SugarShack_Tubing), nameof(SugarShack_Tubing.DeleteThisTube))]
    internal static class TubingDeletePatch
    {
        private static void Prefix(SugarShack_Tubing __instance, out (SugarShack_TubingPost? Origin, SugarShack_TubingPost? Target) __state)
        {
            __state = (__instance.GetOrigin?.Post, __instance.GetTarget?.Post);
            MonMPPlugin.Instance?.World?.Tubing.RemoveTube(__instance);
        }
        private static void Postfix(SugarShack_Tubing __instance,
            (SugarShack_TubingPost? Origin, SugarShack_TubingPost? Target) __state, bool __runOriginal)
        {
            var tubing = MonMPPlugin.Instance?.World?.Tubing;
            if (__runOriginal) tubing?.NativeTubeRemoved(__instance);
            tubing?.AfterRemoveTube(__state.Origin, __state.Target);
        }
    }
    [HarmonyPatch(typeof(Tool_TubingRoll), nameof(Tool_TubingRoll.Equiped))]
    internal static class TubingRollEquippedPatch
    {
        private static void Postfix(Tool_TubingRoll __instance) => MonMPPlugin.Instance?.World?.Tubing.RollEquipped(__instance);
    }
    [HarmonyPatch(typeof(Tool_TubingRoll), nameof(Tool_TubingRoll.UnEquiped))]
    internal static class TubingRollUnequippedPatch
    {
        private static void Postfix(Tool_TubingRoll __instance) => MonMPPlugin.Instance?.World?.Tubing.RollUnequipped(__instance);
    }
    [HarmonyPatch(typeof(SugarShack_TubingPost), nameof(SugarShack_TubingPost.Start))]
    internal static class TubingPostStartedPatch
    {
        private static void Postfix(SugarShack_TubingPost __instance)
        {
            var tubing = MonMPPlugin.Instance?.World?.Tubing;
            tubing?.NativePostReady(__instance);
            tubing?.RefreshPost(__instance);
        }
    }
    [HarmonyPatch(typeof(SugarShack_Tubing), "set_GetTarget", new[] { typeof(TubingConnectionPoint) })]
    internal static class TubingNativeReadyPatch
    {
        private static void Postfix(SugarShack_Tubing __instance) => MonMPPlugin.Instance?.World?.Tubing.NativeTubeReady(__instance);
    }
}
