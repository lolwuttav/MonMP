using HarmonyLib;
using MonMP.Plugin;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(SlotMachine), nameof(SlotMachine.ButtonPressed))]
    internal static class SharedSlotMachineButtonPatch
    {
        private static bool Prefix(SlotMachine __instance, SlotMachine_Button.ButtonType buttonType)
        {
            var shared = MonMPPlugin.Instance?.World?.SlotMachines;
            return shared?.InterceptButton(__instance, buttonType) ?? true;
        }
    }

    // The bar's daily opening resets the machine, which natively forfeits any unplayed credit and
    // leaves the old number on the display. Shared credit is several players' money: keep it over
    // the reset (and show what is really there) instead of losing it overnight (#114).
    [HarmonyPatch(typeof(SlotMachine), nameof(SlotMachine.ResetMachine))]
    internal static class SlotMachineKeepCreditPatch
    {
        private static void Prefix(SlotMachine __instance, out int __state) =>
            __state = MonMPPlugin.Instance?.Session?.IsRunning == true ? __instance.Get_SaveData[1] : -1;
        private static void Postfix(SlotMachine __instance, int __state)
        {
            if (__state < 0) return;
            var data = __instance.Get_SaveData;
            data[1] = __state;
            __instance.Get_SaveData = data;
        }
    }
}
