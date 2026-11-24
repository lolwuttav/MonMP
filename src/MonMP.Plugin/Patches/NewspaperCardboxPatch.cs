using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Newspaper_Cardbox), nameof(Newspaper_Cardbox.MouseDown))]
    internal static class NewspaperCardboxPatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(Newspaper_Cardbox), nameof(Newspaper_Cardbox.MouseDown))!;
        }

        internal static bool Prefix(Newspaper_Cardbox __instance)
        {
            if (SharedNewspaperCardbox.Active?.Intercept(__instance) != true) return true;
            return false;
        }
    }
}
