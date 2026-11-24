using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class FishingCatchRelease
    {
        internal static void Drop(Item_Fish fish)
        {
            // Replicated/save-loaded fish have already retired their catch animator. Native
            // bucket collection can also call this twice, after the rod has released it once.
            bool attached = fish.FishingRod != null || fish.transform.parent != null;
            fish.FishingRod = null;
            fish.UnGrab();
            fish.transform.SetParent(null);
            if (fish.Animator != null)
            {
                fish.Animator.SetFloat("FishSpeed", 0f);
                fish.Animator.Rebind();
                fish.Animator.Update(0f);
                Object.Destroy(fish.Animator);
            }
            if (attached && fish.rb != null)
                fish.rb.AddRelativeTorque(Vector3.up * fish.rb.mass * 0.5f, ForceMode.Impulse);
        }
    }
}
