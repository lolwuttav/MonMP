using MonMP.Core.World;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedItems
    {
        private void RepairLoosePartPickup(Binding binding)
        {
            // A loose host record is authoritative; local placement, removal and hand
            // transitions still own their native bodies until their exact reply settles.
            if (host || !binding.State.Exists || binding.Absorbed || binding.Releasing ||
                binding.PendingGrab != GrabMode.None || binding.State.Lease.Owner != ItemLease.Unowned ||
                binding.Item is not InteractableParts part || presenting.Contains(binding.Id) ||
                IsPendingNativeOutput(part)) return;
            LoosePartBody.Repair(part, LocalHeld(part));
        }
    }
}
