using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class LooseBoltBody
    {
        internal static bool ToolEquipped(InteractableTools? tool) => tool != null && tool.isEquiped &&
            (tool is Tool_AdjustableSpanner || tool is Tool_Air_Impact);

        // Native wrench pickup raycasts the foreground layer. Keep loose bolts
        // there while either bolt tool is equipped, as Hardware_PopBoltColors does.
        internal static void Repair(Hardware_Bolt bolt, bool locallyHeld, bool boltToolEquipped)
        {
            if (locallyHeld || bolt.isPlaced || bolt.isBoltInMagneticTray != null) return;
            if (bolt.rb == null) bolt.CreateBasics();
            int layer = LayerMask.NameToLayer(boltToolEquipped ? "3DObjectInFrontOfEverything" : "InteractableItem");
            if (bolt.gameObject.layer != layer) bolt.gameObject.layer = layer;
            if (bolt.itemCollider != null && !bolt.itemCollider.enabled) bolt.itemCollider.enabled = true;
        }
    }
}
