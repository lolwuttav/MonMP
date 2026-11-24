using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class VehicleSwitchSoundRelayPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SOUND_Vehicle_Switch), nameof(SOUND_Vehicle_Switch.On), Type.EmptyTypes);
            yield return AccessTools.Method(typeof(SOUND_Vehicle_Switch), nameof(SOUND_Vehicle_Switch.Off), Type.EmptyTypes);
        }
        private static void Postfix(SOUND_Vehicle_Switch __instance, MethodBase __originalMethod) =>
            VehicleSwitchPresentation.Native(__instance, __originalMethod.Name == nameof(SOUND_Vehicle_Switch.On));
    }

    // These controls use generic switch clips instead of SOUND_Vehicle_Switch. Patch the exact
    // native caller, avoiding double relay of furniture/underglow/NOS switch sounds.
    [HarmonyPatch(typeof(Vehicle_Button_ExtraLights), "UpdateVisualSound", new Type[] { })]
    internal static class VehicleExtraLightsSoundRelayPatch
    { private static void Postfix(Vehicle_Button_ExtraLights __instance) => VehicleSwitchPresentation.NativeExtra(__instance); }

    [HarmonyPatch(typeof(Vehicle_Button_LightDomeOptions), nameof(Vehicle_Button_LightDomeOptions.ChangeLightMode), new Type[] { })]
    internal static class VehicleDomeSoundRelayPatch
    { private static void Postfix(Vehicle_Button_LightDomeOptions __instance) => VehicleSwitchPresentation.NativeDome(__instance); }

    [HarmonyPatch]
    internal static class VehicleParkingBrakeSoundRelayPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SOUND_Vehicle_ParkingBrake), nameof(SOUND_Vehicle_ParkingBrake.On), Type.EmptyTypes);
            yield return AccessTools.Method(typeof(SOUND_Vehicle_ParkingBrake), nameof(SOUND_Vehicle_ParkingBrake.Off), Type.EmptyTypes);
        }
        private static void Postfix(SOUND_Vehicle_ParkingBrake __instance, MethodBase __originalMethod) =>
            VehicleSwitchPresentation.NativeParking(__instance, __originalMethod.Name == nameof(SOUND_Vehicle_ParkingBrake.On));
    }

    [HarmonyPatch(typeof(Vehicle_Button_ParkingBrake), nameof(Vehicle_Button_ParkingBrake.Use), new Type[] { })]
    internal static class VehicleParkingBrakeLockSoundRelayPatch
    {
        private static void Prefix(Vehicle_Button_ParkingBrake __instance, out int __state) => __state = __instance.LoadFromSave;
        private static void Postfix(Vehicle_Button_ParkingBrake __instance, int __state, bool __runOriginal)
        { if (__runOriginal) VehicleSwitchPresentation.NativeParkingLock(__instance, __state); }
    }

    [HarmonyPatch(typeof(Vehicle_HighLowShifter), nameof(Vehicle_HighLowShifter.MouseDown), new Type[] { })]
    internal static class VehicleTransferCaseSoundRelayPatch
    {
        private static void Prefix(Vehicle_HighLowShifter __instance, out int __state) =>
            __state = VehicleSwitchPresentation.CurrentTransferPosition(__instance);
        private static void Postfix(Vehicle_HighLowShifter __instance, int __state, bool __runOriginal)
        { if (__runOriginal) VehicleSwitchPresentation.NativeTransfer(__instance, __state); }
    }
}
