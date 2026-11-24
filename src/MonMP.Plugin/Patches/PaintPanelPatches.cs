using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Tool_RustSolution), nameof(Tool_RustSolution.MouseDown))]
    internal static class SharedRustSolutionMouseDownPatch
    {
        private static bool Prefix(Tool_RustSolution __instance, out RepairToolPresentation.Click? __state)
        {
            bool run = MonMPPlugin.Instance?.World?.PaintPanels?.InterceptRustSolutionMouseDown(__instance) ?? true;
            __state = run ? RepairToolPresentation.Begin(__instance, RepairToolPresentation.Solution) : null;
            return run;
        }
        private static void Postfix(RepairToolPresentation.Click? __state) => RepairToolPresentation.End(__state, true);
        private static System.Exception? Finalizer(System.Exception? __exception, RepairToolPresentation.Click? __state)
        { if (__exception != null) RepairToolPresentation.End(__state, false); return __exception; }
    }

    [HarmonyPatch(typeof(Tool_DentRepairKit), nameof(Tool_DentRepairKit.MouseDown))]
    internal static class SharedDentRepairKitMouseDownPatch
    {
        private static bool Prefix(Tool_DentRepairKit __instance, out RepairToolPresentation.Click? __state)
        {
            bool run = MonMPPlugin.Instance?.World?.PaintPanels?.InterceptDentRepairKitMouseDown(__instance) ?? true;
            __state = run ? RepairToolPresentation.Begin(__instance, RepairToolPresentation.Dent) : null;
            return run;
        }
        private static void Postfix(RepairToolPresentation.Click? __state) => RepairToolPresentation.End(__state, true);
        private static System.Exception? Finalizer(System.Exception? __exception, RepairToolPresentation.Click? __state)
        { if (__exception != null) RepairToolPresentation.End(__state, false); return __exception; }
    }

    [HarmonyPatch(typeof(Tool_RustRepairKit), nameof(Tool_RustRepairKit.MouseDown))]
    internal static class SharedRustRepairKitMouseDownPatch
    {
        private static bool Prefix(Tool_RustRepairKit __instance, out RepairToolPresentation.Click? __state)
        {
            bool run = MonMPPlugin.Instance?.World?.PaintPanels?.InterceptRustRepairKitMouseDown(__instance) ?? true;
            __state = run ? RepairToolPresentation.Begin(__instance, RepairToolPresentation.Rust) : null;
            return run;
        }
        private static void Postfix(RepairToolPresentation.Click? __state) => RepairToolPresentation.End(__state, true);
        private static System.Exception? Finalizer(System.Exception? __exception, RepairToolPresentation.Click? __state)
        { if (__exception != null) RepairToolPresentation.End(__state, false); return __exception; }
    }

    [HarmonyPatch(typeof(Tool_SprayPaintCan), nameof(Tool_SprayPaintCan.MouseDown))]
    internal static class SharedSprayPaintMouseDownPatch
    {
        private static bool Prefix(Tool_SprayPaintCan __instance) =>
            MonMPPlugin.Instance?.World?.PaintPanels?.InterceptSprayMouseDown(__instance) ?? true;
    }

    [HarmonyPatch(typeof(Tool_SprayPaintCan), nameof(Tool_SprayPaintCan.MouseUp))]
    internal static class SharedSprayPaintMouseUpPatch
    {
        private static bool Prefix(Tool_SprayPaintCan __instance) =>
            MonMPPlugin.Instance?.World?.PaintPanels?.InterceptSprayMouseUp(__instance) ?? true;
    }

    [HarmonyPatch(typeof(Vehicle_PaintablePanel), nameof(Vehicle_PaintablePanel.Paint))]
    internal static class SharedPaintPanelPaintPatch
    {
        private static bool Prefix(Vehicle_PaintablePanel __instance) =>
            MonMPPlugin.Instance?.World?.PaintPanels?.InterceptPaint(__instance) ?? true;

        private static void Postfix(Vehicle_PaintablePanel __instance) =>
            MonMPPlugin.Instance?.World?.PaintPanels?.OnNativePaint(__instance);
    }

    [HarmonyPatch(typeof(Vehicle_PaintablePanel), nameof(Vehicle_PaintablePanel.RepairRust))]
    internal static class SharedPaintPanelRepairRustPatch
    {
        private static bool Prefix(Vehicle_PaintablePanel __instance, ref bool __result) =>
            MonMPPlugin.Instance?.World?.PaintPanels?.InterceptRepair(__instance, PaintPanelAction.RepairRust, false, ref __result) ?? true;
        private static void Postfix(bool __result) => RepairToolPresentation.Result(RepairToolPresentation.Rust, __result);
    }

    [HarmonyPatch(typeof(Vehicle_PaintablePanel), nameof(Vehicle_PaintablePanel.RepairDent))]
    internal static class SharedPaintPanelRepairDentPatch
    {
        private static bool Prefix(Vehicle_PaintablePanel __instance, bool doDamage, ref bool __result) =>
            MonMPPlugin.Instance?.World?.PaintPanels?.InterceptRepair(__instance,
                doDamage ? PaintPanelAction.DamageDent : PaintPanelAction.RepairDent, doDamage, ref __result) ?? true;
        private static void Postfix(bool __result) => RepairToolPresentation.Result(RepairToolPresentation.Dent, __result);
    }

    [HarmonyPatch(typeof(Vehicle_PaintablePanel), nameof(Vehicle_PaintablePanel.ForceRust))]
    internal static class SharedPaintPanelForceRustPatch
    {
        private static bool Prefix(Vehicle_PaintablePanel __instance, ref bool __result) =>
            MonMPPlugin.Instance?.World?.PaintPanels?.InterceptRepair(__instance, PaintPanelAction.ForceRust, false, ref __result) ?? true;
    }
}
