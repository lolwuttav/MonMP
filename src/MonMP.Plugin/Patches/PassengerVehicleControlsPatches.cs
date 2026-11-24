using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    // Native gear-up checks the simulator's local player state. Physical nearby/passenger
    // clicks execute only inside the exact host-authorized shifter intent invocation.
    [HarmonyPatch(typeof(Vehicle_Button_Shifter), nameof(Vehicle_Button_Shifter.CanUse), new System.Type[] { })]
    internal static class PassengerVehicleGearUsePatch
    {
        private static bool Prefix(Vehicle_Button_Shifter __instance, ref bool __result)
        {
            if (MonMPPlugin.Instance?.World?.PassengerControls?.AllowsNativeGear(__instance) != true) return true;
            __result = true; return false;
        }
    }
}
