using System;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedItems
    {
        private static readonly MethodInfo NativePruningAward = AccessTools.Method(typeof(Tool_PruningLoppers), "SpawnAddTreeCutting");
        private static readonly MethodInfo NativeHarvestAward = AccessTools.Method(typeof(Tool_HarvestBasket), "SpawnAddSeeds");

        // A preview cutting can be eight metres from its owner. Its lease and exact stock, rather
        // than the ordinary hand-item pose envelope, authorize the payment; SharedForest checks
        // the planting point against the native ground/land/clearance rules before spending it.
        internal bool HostCuttingSource(uint peer, ulong id, uint generation, byte type, out Garden_TreeCuttings? cutting)
        {
            cutting = null;
            if (!host || !Ready || TransfersBlocked || peer == 0 || !bindings.TryGetValue(id, out var b) ||
                !b.State.Exists || b.Item is not Garden_TreeCuttings source || !leases.Owns(id, peer, generation) ||
                (byte)source.Get_TreeType != type || source.IntValue1 < 1) return false;
            cutting = source;
            return true;
        }

        internal bool HostSpendPlantedCutting(uint peer, ulong id, uint generation, byte type)
        {
            if (!HostCuttingSource(peer, id, generation, type, out var cutting)) return false;
            var b = bindings[id];
            b.LastOwnerUpdate = Time.unscaledTime;
            if (cutting!.IntValue1 > 1) { cutting.IntValue1--; Publish(b); return true; }
            ReleaseLease(id, peer, generation);
            identities.Unbind(id); byItem.Remove(cutting);
            cutting.gameObject.SetActive(false); Object.Destroy(cutting.gameObject);
            b.Item = null; b.State = Capture(b); channel!.Publish(id, b.State.Encode());
            return true;
        }

        internal bool HostPruningTool(uint peer, out Tool_PruningLoppers? loppers)
        {
            loppers = HeldItemOf(peer) as Tool_PruningLoppers;
            return loppers != null && byItem.TryGetValue(loppers, out var b) && !TransfersBlocked &&
                HostHasLeasedTool(peer, b.Id, leases[b.Id].Generation, typeof(Tool_PruningLoppers));
        }

        internal void HostPruningAward(Tool_PruningLoppers loppers, TreeBase tree)
        {
            // Invoke the supported game's random award and nearby-pile merge only for a source
            // action already accepted by the host. ProductionOutputPatches captures new outputs.
            NativePruningAward.Invoke(loppers, new object[] { tree });
            DiscoverSoon();
        }

        internal bool HostHarvestIntoBasket(uint peer, Garden_DirtPatch patch)
        {
            if (!host || !Ready || TransfersBlocked || peer == 0 || patch == null || patch.Get_HARDMODE_IsPlantDead ||
                HeldItemOf(peer) is not Tool_HarvestBasket basket || !byItem.TryGetValue(basket, out var b)) return false;
            var seeds = patch.Get_SeedsType;
            int amount = patch.Get_AmountToHarvest;
            if (seeds == SeedsType.Useless || amount <= 0 || basket.Value1 >= basket.Value1_Max ||
                (basket.Value1 > 0 && basket.IntValue1 != (int)SeedsType.Useless && basket.IntValue1 != (int)seeds) ||
                ((seeds == SeedsType.Cannabis || seeds == SeedsType.Coffee) && basket.Value1 + amount > basket.Value1_Max)) return false;
            // Capture the plant's native award position before Harvest destroys its model.
            var at = patch.transform.position;
            return HostMutateLeasedTool(peer, b.Id, leases[b.Id].Generation, typeof(Tool_HarvestBasket), tool =>
            {
                if (!patch.Harvest()) return false;
                tool.IntValue1 = (int)seeds;
                tool.Value1 = Mathf.Min(tool.Value1 + amount, tool.Value1_Max);
                NativeHarvestAward.Invoke(basket, new object[] { at });
                DiscoverSoon();
                return true;
            });
        }
    }
}
