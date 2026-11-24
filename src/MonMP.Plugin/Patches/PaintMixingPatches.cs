using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Workbench_PaintMixing), nameof(Workbench_PaintMixing.TryToBuy))]
    internal static class PaintMixingPurchasePatch
    {
        private static bool Prefix(Workbench_PaintMixing __instance, int _intValue1)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;

            var adapter = SharedPaintMixing.Active;
            if (adapter != null) return !adapter.InterceptTryToBuy(__instance, _intValue1);

            // Adapter startup failures must never let a guest mutate stock or cash locally.
            if (!plugin.Session.IsHost)
            {
                plugin.ReportItemUseUnavailable("Paint mixing is unavailable until the host's mixer is synchronized.");
                return false;
            }
            return true;
        }

        private static void Postfix(Workbench_PaintMixing __instance) =>
            SharedPaintMixing.Active?.AfterNativePurchase(__instance);

        private static System.Exception? Finalizer(System.Exception? __exception)
        {
            SharedPaintMixing.Active?.EndNativePurchase();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Workbench_PaintMixing), "KaliGas_Open")]
    internal static class PaintMixingRestockPatch
    {
        private static bool Prefix(Workbench_PaintMixing __instance)
        {
            var adapter = SharedPaintMixing.Active;
            if (adapter != null) return !adapter.SuppressRestock(__instance);

            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true || plugin.Session.IsHost) return true;
            plugin.ReportItemUseUnavailable("Paint stock is unavailable until the host's mixer is synchronized.");
            return false;
        }

        private static void Postfix(Workbench_PaintMixing __instance) =>
            SharedPaintMixing.Active?.AfterNativeRestock(__instance);
    }
}
