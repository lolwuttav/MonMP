using System;
using System.Reflection;
using HarmonyLib;

namespace MonMP.Plugin.Replication
{
    // Native setters retain gameplay values separately from meshes/collider geometry.
    // Refresh only presentation: signing, refill and pour actions have gameplay/audio effects.
    internal static class ItemPresentationRefresh
    {
        private static readonly MethodInfo WateringVisual = AccessTools.Method(typeof(Tool_WateringCan), "UpdateVisual", Type.EmptyTypes);
        private static readonly MethodInfo CrateVisual = AccessTools.Method(typeof(Pack_MapleSyrupCrate), "UpdateColliderSize", Type.EmptyTypes);

        internal static void Envelope(Tool_Envelope envelope, int signature)
        {
            if (envelope.Text_Signature != null) envelope.Text_Signature.SetActive(signature == 1);
        }

        internal static void Apply(InteractableItems item, int signature, bool levelChanged, bool force, bool crateChanged = false)
        {
            if (force && item is Tool_Envelope envelope) Envelope(envelope, signature);
            if ((levelChanged || force) && item is Tool_WateringCan can) WateringVisual.Invoke(can, null);
            // Crate setters update contents but leave geometry stale after count/type changes.
            if ((crateChanged || force) && item is Pack_MapleSyrupCrate crate) CrateVisual.Invoke(crate, null);
        }
    }
}
