using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    // MapTorch's coroutine burns fuel and calls StartTheFire directly, outside the
    // shared-item Use/StartFire authority path. Guests keep the native flame; the paper it
    // reaches is ignited by the host (SharedPaperIgnition).
    [HarmonyPatch(typeof(Tool_MapTorch), nameof(Tool_MapTorch.MouseDown))]
    internal static class SharedMapTorchMouseDownPatch
    {
        private static bool Prefix() => SharedMapTorchAuthority.AllowNativeIgnition();
    }

    // Also stop a torch coroutine that was already running if a client session starts
    // while gameplay is active. The generated iterator remains nested under Tool_MapTorch.
    [HarmonyPatch]
    internal static class SharedMapTorchCoroutinePatch
    {
        internal static IEnumerable<MethodInfo> Targets()
        {
            Type[] iteratorTypes = typeof(Tool_MapTorch).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo[] moveNextMethods = Array.FindAll(iteratorTypes, t =>
                    t.Name.IndexOf("DetectFlamableObjects", StringComparison.Ordinal) >= 0)
                .Select(t => AccessTools.Method(t, "MoveNext"))
                .Where(m => m != null && m.ReturnType == typeof(bool))
                .Cast<MethodInfo>()
                .ToArray();

            if (moveNextMethods.Length != 1)
                throw new InvalidOperationException("Expected one Tool_MapTorch.DetectFlamableObjects iterator MoveNext method.");
            return moveNextMethods;
        }

        private static IEnumerable<MethodBase> TargetMethods() => Targets();

        private static bool Prefix(ref bool __result)
        {
            if (SharedMapTorchAuthority.AllowNativeIgnition()) return true;
            __result = false;
            return false;
        }
    }

    internal static class SharedMapTorchAuthority
    {
        internal static bool AllowNativeIgnition()
        {
            var plugin = MonMPPlugin.Instance;
            if (MapTorchAuthorityPolicy.AllowNativeMutation(
                    plugin?.Session?.IsRunning == true,
                    plugin?.Session?.IsHost == true,
                    plugin?.Items?.GuestRunning == true)) return true;
            plugin?.ReportItemUseUnavailable("Map torch ignition is host-only until fire effects are synchronized.");
            return false;
        }
    }
}
