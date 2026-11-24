using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedItems
    {
        private float blockedPickupWindow = -1, nextBlockedPickupWarning;
        private int blockedPickupAttempts;

        // Only deliberate failed native Grab calls reach this path. No idle scan,
        // extra packets, automatic ownership changes or player popups. A short
        // burst is normal latency; repeated failures leave bounded evidence.
        private void NotePickupAttempt(Binding binding, bool queued)
        {
            if (host) return;
            if (queued) { blockedPickupWindow = -1; blockedPickupAttempts = 0; return; }
            float now = Time.unscaledTime;
            if (blockedPickupWindow < 0 || now < blockedPickupWindow || now - blockedPickupWindow > 6)
            { blockedPickupWindow = now; blockedPickupAttempts = 0; }
            blockedPickupAttempts++;
            if (blockedPickupAttempts < 3 || now - blockedPickupWindow < 1 || now < nextBlockedPickupWarning) return;
            nextBlockedPickupWarning = now + 30;
            Debug.LogWarning("MonMP guest pickup stalled: item=" + binding.Id.ToString("X16") +
                "; ready=" + Ready + "; transfersBlocked=" + TransfersBlocked + "; playerState=" + gameplay.Get_PlayerState +
                "; nativeHolding=" + gameplay.Player.IsHoldingAnObject + "; hand=" + gameplay.Player.Get_HoldingItem +
                "; tool=" + gameplay.Player.Get_ActiveTool + "; pending=" + binding.PendingGrab + "; releasing=" + binding.Releasing +
                "; owner=" + binding.State.Lease.Owner + "; generation=" + binding.State.Lease.Generation +
                "; outstanding=" + outstandingPickups + "; preview=" + (pickupPreview != null));
        }
    }
}
