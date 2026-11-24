using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class ScrapyardGateMutationPatch
    {
        internal static IEnumerable<MethodInfo> Targets()
        {
            yield return AccessTools.DeclaredMethod(typeof(Scrapyard), nameof(Scrapyard.OpenDoor));
            yield return AccessTools.DeclaredMethod(typeof(Scrapyard), nameof(Scrapyard.CloseDoor));
            yield return AccessTools.DeclaredMethod(typeof(Scrapyard), nameof(Scrapyard.OpenDoorNoAnimation));
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static bool Prefix(Scrapyard __instance, MethodBase __originalMethod) =>
            MonMP.Plugin.MonMPPlugin.Instance?.World?.ScrapyardGate.AllowMutation(__instance, __originalMethod.Name) ?? true;
    }
}
