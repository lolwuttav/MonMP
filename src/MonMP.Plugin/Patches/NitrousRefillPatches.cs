using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class NitrousRefillSoundPatch
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SOUND_NitrousRefillStation), nameof(SOUND_NitrousRefillStation.StartFilling), System.Type.EmptyTypes);
            yield return AccessTools.Method(typeof(SOUND_NitrousRefillStation), nameof(SOUND_NitrousRefillStation.StopFilling), System.Type.EmptyTypes);
        }
        private static void Postfix(SOUND_NitrousRefillStation __instance, System.Reflection.MethodBase __originalMethod) =>
            MonMPPlugin.Instance?.World?.NitrousRefillStation.NativeCue(__instance,
                (byte)(__originalMethod.Name == nameof(SOUND_NitrousRefillStation.StartFilling) ? 0 : 1));
    }

    // The speedway's nitrous refill station charges whoever's copy of a bottle enters its trigger. A
    // guest's wallet is never the shared one, so its copy always answered "not enough cash" and never
    // refilled. The host's copy of the same bottle (following the guest's hand, or dropped in) runs the
    // native refill against the shared wallet; the refilled level and the bottle's pose replicate.
    [HarmonyPatch(typeof(NitrousRefillStation), "BottleEntered")]
    internal static class NitrousRefillStationPatch
    {
        private const float Price = 69.99f;

        private static bool Prefix(NitrousRefillStation __instance, Part_NitrousBottle bottle)
        {
            var plugin = MonMPPlugin.Instance;
            var items = plugin?.Items;
            if (plugin?.Session?.IsRunning != true || items == null || !items.Handles(bottle)) return true;
            // Native BottleEntered silently ignores every bottle while a refill is running.
            // Check this before reclaiming a second guest's bottle out of their hand.
            if (plugin.World?.NitrousRefillStation.IsFilling(__instance) == true) return false;
            if (!plugin.Session.IsHost)
            {
                if (bottle.Value1 >= bottle.Value1_Max) Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText("ERROR_NitrousRefillStation_AlreadyFull"));
                else if (plugin.World?.Economy.CanAffordCents(6999) == false)
                    Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText("ERROR_NotEnoughCash"));
                return false;
            }
            if (!items.HeldByRemotePlayer(bottle)) return true;
            // Only take a guest's bottle out of its hand for a refill that will actually run.
            if (bottle.Value1 >= bottle.Value1_Max || Singleton<Cash_Manager>.i?.isCanAfford(Price) != true) return false;
            return items.HostReclaim(bottle);
        }
    }

    // The installed nitrous controls belong to the host (SharedNitrous).
    [HarmonyPatch(typeof(Vehicle_Nitrous_Controls), nameof(Vehicle_Nitrous_Controls.Button_Arm_On))]
    internal static class NitrousArmOnPatch
    {
        private static bool Prefix(Vehicle_Nitrous_Controls __instance) =>
            MonMPPlugin.Instance?.World?.Nitrous.Intercept(__instance, Replication.SharedNitrous.ArmOn) != true;
    }

    [HarmonyPatch(typeof(Vehicle_Nitrous_Controls), nameof(Vehicle_Nitrous_Controls.Button_Arm_Off))]
    internal static class NitrousArmOffPatch
    {
        private static bool Prefix(Vehicle_Nitrous_Controls __instance) =>
            MonMPPlugin.Instance?.World?.Nitrous.Intercept(__instance, Replication.SharedNitrous.ArmOff) != true;
    }

    [HarmonyPatch(typeof(Vehicle_Nitrous_Controls), nameof(Vehicle_Nitrous_Controls.Power_Button_Toggle))]
    internal static class NitrousPowerTogglePatch
    {
        private static bool Prefix(Vehicle_Nitrous_Controls __instance) =>
            MonMPPlugin.Instance?.World?.Nitrous.Intercept(__instance, Replication.SharedNitrous.PowerToggle) != true;
    }

    // Underglow kit buttons belong to the host (SharedUnderglow).
    [HarmonyPatch(typeof(Vehicle_Underglow), "Update", new System.Type[0])]
    internal static class UnderglowUpdatePatch
    {
        private static bool Prefix(Vehicle_Underglow __instance) =>
            MonMPPlugin.Instance?.World?.Underglow.ShouldRunNativeUpdate(__instance) != false;
    }

    [HarmonyPatch]
    internal static class UnderglowButtonPatch
    {
        private static readonly string[] Methods = { nameof(Vehicle_Underglow.Button_OnOff), nameof(Vehicle_Underglow.Button_Mode),
            nameof(Vehicle_Underglow.Button_CLR), nameof(Vehicle_Underglow.Button_Cycle), nameof(Vehicle_Underglow.Button_Force) };
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
            System.Linq.Enumerable.Select(Methods, name => (System.Reflection.MethodBase)AccessTools.Method(typeof(Vehicle_Underglow), name, System.Type.EmptyTypes));
        private static bool Prefix(Vehicle_Underglow __instance, System.Reflection.MethodBase __originalMethod) =>
            MonMPPlugin.Instance?.World?.Underglow.Intercept(__instance, (byte)(System.Array.IndexOf(Methods, __originalMethod.Name) + 1)) != true;
    }
}
