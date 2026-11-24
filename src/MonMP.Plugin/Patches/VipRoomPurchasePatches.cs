using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    // Before the world binds its static handles, guest native VIP purchases still must not run.
    [HarmonyPatch(typeof(Furniture_DoorHandle_VIPRoom), nameof(Furniture_DoorHandle_VIPRoom.MouseDown))]
    internal static class VipRoomGuestPurchaseGuardPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix()
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true || plugin.Session.IsHost || plugin.World?.Doors.Ready == true) return true;
            plugin.ReportItemUseUnavailable("Wait for the shared world before unlocking the VIP room.");
            return false;
        }
    }

    // Native hover affordability is a quote over the replicated wallet, not permission to run
    // a local purchase. Replace only this hover's one quote call so other cash guards stay intact.
    [HarmonyPatch(typeof(Furniture_DoorHandle_VIPRoom), nameof(Furniture_DoorHandle_VIPRoom.MouseHover))]
    internal static class VipRoomPriceHoverPatch
    {
        private static readonly MethodInfo Afford = AccessTools.Method(typeof(Cash_Manager), nameof(Cash_Manager.isCanAfford));
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList(); var quotes = code.Where(value => value.Calls(Afford)).ToArray();
            if (quotes.Length != 1) throw new InvalidOperationException("Unsupported native VIP room hover price layout.");
            quotes[0].opcode = OpCodes.Call; quotes[0].operand = AccessTools.Method(typeof(VipRoomPriceHoverPatch), nameof(CanQuote));
            return code;
        }
        internal static bool CanQuote(Cash_Manager cash, float price)
        {
            var session = MonMPPlugin.Instance?.Session;
            if (session?.IsRunning != true || session.IsHost) return cash.isCanAfford(price);
            return !float.IsNaN(price) && !float.IsInfinity(price) && price >= 0 && (float)cash.GetCash >= price * 100f;
        }
    }

    // SharedProgress reproduces the native checklist popup/sound on the requester and peers.
    // The host performing this guest purchase does not get an unrelated local checklist popup.
    [HarmonyPatch(typeof(ToDoList_Manager), "Checked")]
    internal static class VipRoomRemoteChecklistPatch
    {
        private static bool Prefix() => MonMPPlugin.Instance?.World?.Doors.IsExecutingGuestVipUnlock != true;
    }
}
