using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    // One player rides the garage creeper at a time; the shared adapter grants it (#209).
    [HarmonyPatch(typeof(MechanicCreeper), nameof(MechanicCreeper.MouseDown))]
    internal static class SharedCreeperClickPatch
    {
        private static bool Prefix(MechanicCreeper __instance) =>
            MonMPPlugin.Instance?.World?.Creepers.Click(__instance) != true;
    }
}
