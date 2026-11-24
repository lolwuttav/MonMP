using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Tool_DogTreats), nameof(Tool_DogTreats.MouseDown))]
    internal static class SharedDogTreatToolPatch
    {
        private static bool Prefix(Tool_DogTreats __instance) =>
            MonMPPlugin.Instance?.World?.DogTreats.Intercept(__instance) != true;
    }
}
