using System;
using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    // The splitter uses the native switch body, which plays its click before invoking the
    // intercepted WoodSplitter callback. Track that exact body instead of playing a second
    // click or guessing from a recent timestamp. A finalizer also clears the scope on failure.
    [HarmonyPatch(typeof(Furniture_SwitchButton), nameof(Furniture_SwitchButton.MouseDown))]
    internal static class MachineSwitchContextPatch
    {
        private static void Prefix(Furniture_SwitchButton __instance, out IDisposable? __state) =>
            __state = MonMPPlugin.Instance?.World?.WoodSplitters.ObserveNativeSwitch(__instance);
        private static void Finalizer(IDisposable? __state) => __state?.Dispose();
    }
}
