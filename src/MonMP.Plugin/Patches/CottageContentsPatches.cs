using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    // Cottage contents are host-owned. Guests may view the replicated values, but native
    // interactions and local world ticks must not mutate their copy.
    [HarmonyPatch(typeof(Bunker_CompostMachine), "OnTriggerEnter")]
    internal static class CottageCompostInputPatch
    {
        private static bool Prefix(Bunker_CompostMachine __instance, UnityEngine.Collider other)
        {
            var world = MonMPPlugin.Instance?.World;
            if (world == null || (!world.CottagePurchase.HandlesCompost(__instance) && !world.BunkerCompost.Handles(__instance))) return true;
            bool feedback = world.TriggerFeedback.Compost(__instance, other);
            return !world.BunkerCompost.BlocksGuest(__instance) &&
                SharedCottagePurchase.Active?.BlocksGuest(__instance) != true && !feedback;
        }
    }

    [HarmonyPatch(typeof(Bunker_CompostMachine), nameof(Bunker_CompostMachine.Button_MakeFertilizer))]
    internal static class CottageFertilizerButtonPatch
    {
        private static bool Prefix(Bunker_CompostMachine __instance)
        {
            return SharedCottagePurchase.Active?.GuestCollectionClick(__instance) != true &&
                MonMPPlugin.Instance?.World?.RemoteClicks.Intercept(__instance) != true;
        }
    }

    [HarmonyPatch(typeof(Bunker_CompostMachine), "HourChanged")]
    internal static class CottageCompostTickPatch
    {
        private static bool Prefix(Bunker_CompostMachine __instance) =>
            SharedCottagePurchase.Active?.BlocksGuest(__instance) != true &&
            MonMPPlugin.Instance?.World?.BunkerCompost.BlocksGuest(__instance) != true;
    }

    [HarmonyPatch(typeof(CoffeeSeedAutoSpawn), nameof(CoffeeSeedAutoSpawn.MouseDown))]
    internal static class CottageCoffeeSeedsInputPatch
    {
        private static bool Prefix(CoffeeSeedAutoSpawn __instance)
        {
            return SharedCottagePurchase.Active?.GuestCollectionClick(__instance) != true;
        }
    }

    [HarmonyPatch(typeof(CoffeeSeedAutoSpawn), "NewDay")]
    internal static class CottageCoffeeSeedsTickPatch
    {
        private static bool Prefix(CoffeeSeedAutoSpawn __instance) =>
            SharedCottagePurchase.Active?.BlocksGuest(__instance) != true;
    }

    // A guest's percolator click becomes a host brew or pour; the reply plays the native effects.
    [HarmonyPatch(typeof(Item_CoffeePercolator), nameof(Item_CoffeePercolator.MouseDown))]
    internal static class CottagePercolatorInputPatch
    {
        private static bool Prefix(Item_CoffeePercolator __instance) =>
            SharedCottagePurchase.Active?.GuestPercolatorClick(__instance) != true;
    }

    [HarmonyPatch(typeof(Wheelbarrow), nameof(Wheelbarrow.HandleClick))]
    internal static class CottageWheelbarrowInputPatch
    {
        private static bool Prefix(Wheelbarrow __instance)
        {
            var shared = MonMPPlugin.Instance?.World?.Wheelbarrows;
            if (shared != null)
            {
                if (shared.GuestClick(__instance)) return false;
                if (shared.DrivenByGuest(__instance))
                { MonMPPlugin.Instance?.ReportItemUseUnavailable("Another player is pushing the wheelbarrow."); return false; }
            }
            var cottage = SharedCottagePurchase.Active;
            if (cottage == null) return true;
            if (cottage.GuestBarrowClick(__instance)) return false;
            if (!cottage.BarrowDrivenByGuest(__instance)) return true;
            MonMPPlugin.Instance?.ReportItemUseUnavailable("Another player is pushing the wheelbarrow.");
            return false;
        }
    }

    [HarmonyPatch(typeof(Wheelbarrow), nameof(Wheelbarrow.ResetLocation))]
    internal static class CottageWheelbarrowResetPatch
    {
        private static bool Prefix(Wheelbarrow __instance) =>
            SharedCottagePurchase.Active?.BlocksGuest(__instance) != true && SharedCottagePurchase.Active?.BarrowDrivenByGuest(__instance) != true &&
            MonMPPlugin.Instance?.World?.Wheelbarrows.BlocksNativeUpdate(__instance) != true &&
            MonMPPlugin.Instance?.World?.Wheelbarrows.DrivenByGuest(__instance) != true;
    }

    // HandleClick also calls this on the host. Replace its first broad native capture,
    // before it can destroy bodies/parent cargo outside the narrowed tray bounds.
    [HarmonyPatch(typeof(Wheelbarrow), "FreezeContent")]
    internal static class WheelbarrowLoadPatch
    {
        private static bool Prefix(Wheelbarrow __instance, bool value)
        {
            var session = MonMPPlugin.Instance?.Session;
            if (!value || session?.IsRunning != true || !session.IsHost) return true;
            SharedWheelbarrows.FreezeLoad(__instance, Singleton<Gameplay>.i, false);
            return false;
        }
    }

    [HarmonyPatch(typeof(Wheelbarrow), "Update")]
    internal static class CottageWheelbarrowTickPatch
    {
        private static bool Prefix(Wheelbarrow __instance) =>
            SharedCottagePurchase.Active?.BlocksGuest(__instance) != true &&
            MonMPPlugin.Instance?.World?.Wheelbarrows.BlocksNativeUpdate(__instance) != true;
    }
}
