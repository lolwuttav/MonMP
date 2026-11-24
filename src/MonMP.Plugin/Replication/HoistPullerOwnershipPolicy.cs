using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    // Host player identity is zero; claim presence must never be inferred from owner != 0.
    internal static class HoistPullerOwnershipPolicy
    {
        internal static bool ClaimBusy(bool hasClaim, uint owner, uint requester, bool nativeHeld, bool groundPlaced) =>
            (hasClaim && owner != requester) || nativeHeld || groundPlaced;

        internal static bool ClaimExpired(float now, float since, float lifetime, bool peerPresent) =>
            !peerPresent || now < since || now - since > lifetime;

        internal static bool CanRecover(bool open, bool hasClaim, bool groundPlaced, bool nativeHeld, bool nearby) =>
            open && !hasClaim && groundPlaced && !nativeHeld && nearby;

        internal static bool EndsClaim(HoistPullerPlacementKind kind, bool claimedByPeer, bool attachmentAccepted) =>
            claimedByPeer && (kind == HoistPullerPlacementKind.Release ||
                kind == HoistPullerPlacementKind.Attach && attachmentAccepted);
    }
}
