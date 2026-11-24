using System.Reflection;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class WoodenKegWinePack
    {
        private static readonly FieldInfo? Full = typeof(Pack_MapleSyrupCrate).GetField("IsCrateFull", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? Contents = typeof(Pack_MapleSyrupCrate).GetField("IsCrateFull_Type", BindingFlags.Instance | BindingFlags.NonPublic);

        // Item_WoodenKeg.Use just instantiated this exact native factory. Its Start has not
        // run yet: NumberInPack=10 and type=0 are serialized placeholders. Replicate the
        // factory's full-wine Start result through the ordinary non-refilling crate instead.
        internal static bool TryInitialValues(GameObject factory, InteractableItems output, out ItemValues values)
        {
            values = default;
            if (factory == null || factory.GetComponent<InteractableItems>() is not Pack_MapleSyrupCrate template ||
                !IsWineFactory(template) || output is not Pack_MapleSyrupCrate native || !IsWineFactory(native)) return false;
            values = new ItemValues(template.Value1_Max, native.Value2, 2, native.IntValue2);
            return true;
        }

        private static bool IsWineFactory(Pack_MapleSyrupCrate crate) =>
            crate.GetType() == typeof(Pack_MapleSyrupCrate) && crate.PrefabName == "Pack/MapleSyrupCrate" &&
            crate.Value1_Max == 12f && Full?.GetValue(crate) is true && Contents?.GetValue(crate) is int kind && kind == 2;

        internal static void HideUseHint(PlayerInteractable player, Item_WoodenKeg keg, InteractableItems? delivered = null)
        {
            // A delayed reply must not erase the hint for a different object/tool.
            if (ReferenceEquals(player.Get_CurrentInteractable, keg) && (!player.IsHoldingAnObject ||
                delivered != null && ReferenceEquals(player.Get_HoldingItem, delivered)))
                Singleton<UI_UseKeyText>.i?.HideUniversal();
        }
    }
}
