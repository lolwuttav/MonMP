using HarmonyLib;
using MonMP.Core.World;

namespace MonMP.Plugin.Patches
{
    internal static class MapleSyrupCargoAuthorityAdapter
    {
        internal static bool MayRunNativeMutation()
        {
            var session = MonMPPlugin.Instance?.Session;
            return MapleSyrupCargoAuthorityPolicy.MayRunNativeMutation(
                session?.IsRunning == true,
                session?.IsHost == true);
        }
    }

    // The host's crate copy consumes shared cans/bottles/mugs through the native
    // collision path. A guest collision must not consume its local item copy first.
    [HarmonyPatch(typeof(Pack_MapleSyrupCrate), "AddToPack", new[] { typeof(UnityEngine.Collider) })]
    internal static class MapleSyrupCratePackingAuthorityPatch
    {
        // The host's copy of a can a guest is holding follows that guest's hand; brushing past the
        // host's crate must not pack it out of the guest's hand. It packs once the guest lets go.
        private static bool Prefix(UnityEngine.Collider __0) => MapleSyrupCargoAuthorityAdapter.MayRunNativeMutation() &&
            (__0 == null || MonMPPlugin.Instance?.World?.Items?.HeldByRemotePlayer(__0.GetComponent<InteractableItems>()) != true);
    }

    // Outside-pump delivery of syrup to a tanker is simulated on the host by
    // SugarShack.Tick; keep the receiving side of that cargo mutation host-owned too.
    [HarmonyPatch(typeof(SugarShack_PumpSyrupOutside), nameof(SugarShack_PumpSyrupOutside.AddSyrup), new[] { typeof(int) })]
    internal static class MapleSyrupTankerFillAuthorityPatch
    {
        private static bool Prefix() => MapleSyrupCargoAuthorityAdapter.MayRunNativeMutation();
    }

    // These native receivers consume or empty shared cargo while paying the local
    // player. Until a host-routed delivery exists, only offline play and the host run them.
    [HarmonyPatch(typeof(Store_Bar), nameof(Store_Bar.Receive), new[] { typeof(Pack_MapleSyrupCrate) })]
    internal static class MapleSyrupCrateSaleAuthorityPatch
    {
        private static bool Prefix() => MapleSyrupCargoAuthorityAdapter.MayRunNativeMutation();
    }

    [HarmonyPatch(typeof(Store_FederationSyrup), nameof(Store_FederationSyrup.Receive), new[] { typeof(Trailer_MapleSyrupTank) })]
    internal static class MapleSyrupTankerSaleAuthorityPatch
    {
        private static bool Prefix() => MapleSyrupCargoAuthorityAdapter.MayRunNativeMutation();
    }
}
