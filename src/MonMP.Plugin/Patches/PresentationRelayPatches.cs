using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    // Native one-shot presentation this machine played, reported to the presentation event bus so
    // every other player replays it (docs/static-scan-presentation 2026-09-28, #16, #19, #21).

    // A generator or wood-splitter pull-cord attempt on the host, including one that never starts.
    [HarmonyPatch(typeof(SOUND_WoodSplitter), nameof(SOUND_WoodSplitter.PullStart))]
    internal static class MachinePullStartRelayPatch
    {
        private static void Postfix(SOUND_WoodSplitter __instance)
        {
            if (MachineControlPresentation.NativePull(__instance)) return;
            if (PresentationReplays.Events?.IsHost == true)
                PresentationReplays.Post(PresentationKind.MachinePullStart, 0, 0, __instance.transform.position);
        }
    }

    // A grinder, packager or compost machine finished for this machine's own player (#240).
    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.Play_General_LeatherCrafting))]
    internal static class MachineCraftingRelayPatch
    {
        private static void Postfix() => SharedRemoteClicks.Active?.NativeDone(0);
    }
    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.Play_General_TapePacking))]
    internal static class MachineTapeRelayPatch
    {
        private static void Postfix() => SharedRemoteClicks.Active?.NativeDone(1);
    }

    // This machine's driver set a road-crossing animal running.
    [HarmonyPatch(typeof(AnimalRunAcrossTheRoad), nameof(AnimalRunAcrossTheRoad.PlayerEnteredZone))]
    internal static class AnimalCrossingRelayPatch
    {
        private static void Prefix(AnimalRunAcrossTheRoad __instance, out bool __state) => __state = PresentationReplays.IsAnimalRunning(__instance);
        private static void Postfix(AnimalRunAcrossTheRoad __instance, bool __state)
        {
            if (!__state && PresentationReplays.IsAnimalRunning(__instance)) PresentationReplays.AnimalStarted(__instance);
        }
    }

    // Bolt work sounds of this player's spanner or impact wrench.
    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.PlaySound_BoltScrewing))]
    internal static class BoltScrewingRelayPatch
    { private static void Postfix() => PresentationReplays.LocalBoltCue(PresentationReplays.BoltCue.ScrewStart); }

    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.PlaySound_BoltScrewingOff))]
    internal static class BoltScrewingOffRelayPatch
    { private static void Postfix() => PresentationReplays.LocalBoltCue(PresentationReplays.BoltCue.ScrewStop); }

    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.PlaySound_BoltUnScrewing))]
    internal static class BoltUnScrewingRelayPatch
    { private static void Postfix() => PresentationReplays.LocalBoltCue(PresentationReplays.BoltCue.UnscrewStart); }

    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.PlaySound_BoltUnScrewingOff))]
    internal static class BoltUnScrewingOffRelayPatch
    { private static void Postfix() => PresentationReplays.LocalBoltCue(PresentationReplays.BoltCue.UnscrewStop); }

    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.PlaySound_BoltScrewingInstalled))]
    internal static class BoltInstalledRelayPatch
    { private static void Postfix() => PresentationReplays.LocalBoltCue(PresentationReplays.BoltCue.Installed); }

    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.PlaySound_BoltScrewingUnInstalled))]
    internal static class BoltUninstalledRelayPatch
    { private static void Postfix() => PresentationReplays.LocalBoltCue(PresentationReplays.BoltCue.Uninstalled); }

    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.PlaySound_ImpactUnScrew))]
    internal static class ImpactUnScrewRelayPatch
    { private static void Postfix() => PresentationReplays.LocalBoltCue(PresentationReplays.BoltCue.Impact); }

    // This machine's player equipped or put away a fishing rod: its casts are shown to everyone (Astra R16).
    [HarmonyPatch(typeof(Tool_FishingRod), nameof(Tool_FishingRod.Start))]
    internal static class FishingRodStartRelayPatch
    { private static void Postfix(Tool_FishingRod __instance) => RemoteFishing.NativeStarted(__instance); }

    [HarmonyPatch(typeof(Tool_FishingRod), nameof(Tool_FishingRod.Equiped))]
    internal static class FishingRodEquipRelayPatch
    { private static void Postfix(Tool_FishingRod __instance) => RemoteFishing.Equipped(__instance); }

    [HarmonyPatch(typeof(Tool_FishingRod), nameof(Tool_FishingRod.UnEquiped))]
    internal static class FishingRodUnequipRelayPatch
    { private static void Postfix(Tool_FishingRod __instance) => RemoteFishing.Unequipped(__instance); }
}
