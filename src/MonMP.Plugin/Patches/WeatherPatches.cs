using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UniStorm;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class SharedWeatherChangePatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            foreach (string name in new[] { "ChangeWeather", "InitializeWeather", "TransitionWeather", "GenerateWeather", "CheckGeneratedWeather", "ChangeWeatherUI", "CalculateMoonPhase" })
                yield return AccessTools.Method(typeof(UniStormSystem), name);
            foreach (string name in new[] { "ChangeWeatherWithTransition", "RandomWeather", "RegenerateForecast" })
                yield return AccessTools.Method(typeof(UniStormManager), name);
            yield return AccessTools.Method(typeof(UniStormManager), "ChangeWeatherInstantly", new[] { typeof(WeatherType) });
            yield return AccessTools.Method(typeof(UniStormManager), "ChangeWeatherInstantly", new[] { typeof(string) });
            yield return AccessTools.Method(typeof(UniStorm.Utility.LightningSystem), "GenerateLightning");
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static bool Prefix() => MonMPPlugin.Instance?.World?.Weather.BlocksLocalChanges != true;
    }

    // The host's strikes are counted so guests see and hear the same storm.
    [HarmonyPatch(typeof(UniStorm.Utility.LightningSystem), "GenerateLightning")]
    internal static class SharedLightningStrikePatch
    {
        private static void Postfix(UniStorm.Utility.LightningSystem __instance) => MonMPPlugin.Instance?.World?.Weather.HostLightning(__instance);
    }
}
