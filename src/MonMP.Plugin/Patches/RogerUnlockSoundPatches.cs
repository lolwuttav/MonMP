using System;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Actor_Roger), nameof(Actor_Roger.Greetings), new Type[] { typeof(bool) })]
    internal static class RogerUnlockSoundRelayPatch
    {
        private static void Prefix(out RogerUnlockPresentation.GreetingScope __state) => __state = RogerUnlockPresentation.Begin();
        private static void Postfix(Actor_Roger __instance, string __result, RogerUnlockPresentation.GreetingScope? __state) =>
            RogerUnlockPresentation.Completed(__instance, __state, __result);
        private static void Finalizer(RogerUnlockPresentation.GreetingScope? __state) => RogerUnlockPresentation.Close(__state);
    }
}
