using System;
using System.Linq;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Isolated fixtures must buy/receive stock from the host rather than mint guest seeds.
    // Exact item IDs are repeated until the guest receives their ordinary shared records.
    internal sealed class PairedGardenResources : IDisposable
    {
        private readonly MonMPPlugin plugin;
        private readonly WorldChannel channel;
        private readonly byte scenario;
        private readonly bool fertilizerRequired;
        private InteractableItems? seed;
        private float nextOffer, nextGrab;
        private ulong grabTarget;
        internal ulong Seeds { get; private set; }
        internal ulong Fertilizer { get; private set; }
        internal ulong Basket { get; private set; }
        internal PairedGardenResources(MonMPPlugin plugin, byte scenario, bool fertilizerRequired)
        {
            this.plugin = plugin; this.scenario = scenario; this.fertilizerRequired = fertilizerRequired;
            channel = plugin.World!.Channel;
            if (!plugin.TestOptions.IsHost) channel.ReplyReceived += Offered;
        }
        internal void Prepare(Vector3 at)
        {
            if (!plugin.TestOptions.IsHost || seed != null) return;
            var items = plugin.Items!;
            string path = Singleton<GameAssets>.i.GetSeeds(SeedsType.Potato).GetComponent<InteractableItems>().PrefabName;
            seed = items.TestSpawn(at + Vector3.right * 1.5f + Vector3.up, items.TestPrefab(path));
            seed.isSpawnFromTheSaveLoadSystem = true; seed.Value1 = 2; seed.CreateBasics();
            if (seed.rb != null) seed.rb.isKinematic = true;
            if (fertilizerRequired) Fertilizer = items.TestSpawnPresentationTool(typeof(Tool_FertilizerPack), at + Vector3.back * 1.5f + Vector3.up);
            Basket = items.TestSpawnPresentationTool(typeof(Tool_HarvestBasket), at - Vector3.right * 1.5f + Vector3.up);
            var basket = items.TestItem(Basket)!; basket.Value1 = 0; basket.IntValue1 = (int)SeedsType.Useless;
            items.TestDiscoverSoon();
        }
        internal void Tick()
        {
            if (!plugin.TestOptions.IsHost || seed == null || !channel.FullySynchronized || plugin.Session!.Peers.Count < 2) return;
            uint peer = plugin.Session.Peers.Keys.Single(value => value != 0);
            if (!channel.TestPeerSynchronized(peer)) return;
            if (Seeds == 0) { Seeds = plugin.Items!.TestBoundId(seed); if (Seeds == 0) { plugin.Items.TestDiscoverSoon(); return; } }
            if (Time.unscaledTime < nextOffer) return;
            nextOffer = Time.unscaledTime + 0.5f;
            var payload = new byte[26]; payload[0] = 0x67; payload[1] = scenario;
            Array.Copy(BitConverter.GetBytes(Seeds), 0, payload, 2, 8);
            Array.Copy(BitConverter.GetBytes(Fertilizer), 0, payload, 10, 8);
            Array.Copy(BitConverter.GetBytes(Basket), 0, payload, 18, 8);
            channel.Reply(peer, CottagePurchaseState.Entity, payload);
        }
        private void Offered(ulong entity, byte[] payload)
        {
            if (entity != CottagePurchaseState.Entity || payload.Length != 26 || payload[0] != 0x67 || payload[1] != scenario) return;
            Seeds = BitConverter.ToUInt64(payload, 2); Fertilizer = BitConverter.ToUInt64(payload, 10); Basket = BitConverter.ToUInt64(payload, 18);
        }
        internal T? Item<T>(ulong id) where T : InteractableItems => id == 0 ? null : plugin.Items?.ItemOf(id) as T;
        // The fixture calls Plant directly on the patch. Stock gameplay only
        // calls it from the equipped seed tool, which supplies the exact lease.
        internal bool EquipSeeds()
        {
            if (Item<Tool_Seeds>(Seeds) is not { } seeds || !Hold(Seeds)) return false;
            var player = plugin.Gameplay!.Player;
            if (player.Get_HoldingItem == seeds) player.EquipThisTool(seeds);
            return player.Get_ActiveTool == seeds && seeds.isEquiped;
        }
        internal bool Hold(ulong id)
        {
            var items = plugin.Items!;
            var item = id == 0 ? null : items.ItemOf(id);
            if (item == null) return false;
            var state = items.TestState(id);
            if (state.Exists && state.Lease.Owner == plugin.Session!.LocalPeerId && items.TestLocalHeld(id)) return true;
            var game = plugin.Gameplay!;
            if (game.Player.IsHoldingAnObject) return false;
            if (grabTarget != id)
            {
                grabTarget = id; nextGrab = Time.unscaledTime + 0.8f;
                return false;
            }
            if (Time.unscaledTime < nextGrab) return false;
            items.TestGrab(id); nextGrab = Time.unscaledTime + 0.7f;
            return false;
        }
        public void Dispose() => channel.ReplyReceived -= Offered;
    }
}
