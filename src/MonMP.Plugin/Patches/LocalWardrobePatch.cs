using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    // A wardrobe may initialize after remote avatars. Always resolve the actual local hands.
    [HarmonyPatch(typeof(Furniture_GardeRobe), nameof(Furniture_GardeRobe.MouseDown))]
    internal static class LocalWardrobePatch
    {
        private static void Prefix(ref PlayerVisualHands ___playerVisualHands, Player_ShirtColor ___Player_ShirtColor)
        {
            var hands = Singleton<Gameplay>.i?.Player?.Get_VisualHands;
            if (hands == null) return;
            ___playerVisualHands = hands;
            MonMPPlugin.Instance?.ClosetSelected();
            // Native MouseDown applies the color after its UI and Steam callbacks. Those
            // optional effects must not prevent a remote player's local wardrobe action.
            if (MonMPPlugin.Instance?.Session?.IsRunning == true) hands.ChangeShirt((int)___Player_ShirtColor);
        }
    }
    [HarmonyPatch(typeof(PlayerVisualHands), nameof(PlayerVisualHands.ChangeShirt))]
    internal static class LocalShirtColorPatch
    {
        private static void Postfix(PlayerVisualHands __instance) => MonMPPlugin.Instance?.NativeShirtChanged(__instance);
    }
}
