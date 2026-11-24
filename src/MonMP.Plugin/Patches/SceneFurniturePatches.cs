using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    internal static class SceneFurniturePatches
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(Furniture_Computer), nameof(Furniture_Computer.PowerOn));
            yield return AccessTools.Method(typeof(Furniture_Computer), nameof(Furniture_Computer.EnterSaveMode));
            yield return AccessTools.Method(typeof(Furniture_Phone), nameof(Furniture_Phone.TakeApareille));
            yield return AccessTools.Method(typeof(TickSystem), nameof(TickSystem.AddOneTickFromSave));
            yield return AccessTools.Method(typeof(Furniture_GardeRobe), nameof(Furniture_GardeRobe.MouseDown));
        }
        [HarmonyPatch(typeof(Furniture_Computer), nameof(Furniture_Computer.PowerOn))]
        internal static class Power { private static bool Prefix(Furniture_Computer __instance) => MonMPPlugin.Instance?.World?.SceneFurniture.AllowPower(__instance) ?? true; }
        [HarmonyPatch(typeof(Furniture_Computer), nameof(Furniture_Computer.EnterSaveMode))]
        internal static class Use { private static bool Prefix(Furniture_Computer __instance) => MonMPPlugin.Instance?.World?.SceneFurniture.AllowUse(__instance) ?? true; }
        [HarmonyPatch(typeof(Furniture_Phone), nameof(Furniture_Phone.TakeApareille))]
        internal static class Phone { private static bool Prefix(Furniture_Phone __instance) => MonMPPlugin.Instance?.World?.SceneFurniture.AllowTake(__instance) ?? true; }
        [HarmonyPatch(typeof(TickSystem), nameof(TickSystem.AddOneTickFromSave))]
        internal static class SaveTick
        {
            private static bool Prefix(out Replication.ComputerTimeScope? __state)
            {
                __state = null;
                if (Replication.SharedPortableConsoleCharger.Active?.AllowConsoleSaveTick() == false) return false;
                var furniture = MonMPPlugin.Instance?.World?.SceneFurniture;
                if (furniture?.AllowSaveTick() == false) return false;
                if (furniture != null)
                {
                    uint user = Replication.ComputerTimeScope.Active ? Replication.ComputerTimeScope.User : furniture.ComputerUser;
                    if (user == MonMP.Core.World.ItemLease.Unowned && MonMPPlugin.Instance?.Session?.IsHost == true)
                        user = MonMPPlugin.Instance.Session.LocalPeerId;
                    __state = new Replication.ComputerTimeScope(user);
                }
                return true;
            }
            private static System.Exception? Finalizer(System.Exception? __exception, Replication.ComputerTimeScope? __state)
            { __state?.Dispose(); return __exception; }
        }
    }
}
