using System;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using UniStorm;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedStoresTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Cash_Manager cash;
        private readonly ulong listing;
        private readonly Guid purchase = Guid.NewGuid();
        private readonly Guid distant = Guid.NewGuid();
        private int phase, ack, repairs;
        private bool hostHolding;
        private float next;
        private ulong previousItem;
        public string Status => "stores phase " + phase;
        private static ItemForSale_Display Fixture() => UnityEngine.Object.FindObjectsOfType<ItemForSale_Display>(true).Where(d => d.GetType() == typeof(ItemForSale_Display) && d.store != null && d.CustomIntValue1 == -1 && d.Get_RealObjectToBuy != null &&
            d.Get_RealObjectToBuy.GetComponent<InteractableItems>()?.PrefabName == "Drink/MapleSyrup").OrderBy(d => RuntimeProbe.CanonicalPath(d.transform), StringComparer.Ordinal).First();
        internal static void Prepare(bool host)
        {
            UniStormSystem.Instance.TimeFlow = UniStormSystem.EnableFeature.Disabled;
            UnityEngine.Object.FindObjectOfType<Cash_Manager>().LoadSaveSystem(host ? 10000 : 420, new float[5]);
            var display = Fixture(); display.gameObject.SetActive(true); display.store.isOpen = host;
            AccessTools.Field(typeof(ItemForSale_Display), "price").SetValue(display, host ? 12.34f : 9.87f);
            AccessTools.Field(typeof(ItemForSale_Display), "CanBuyMultiplesADay").SetValue(display, false);
        }
        internal PairedStoresTest(MonMPPlugin plugin)
        {
            this.plugin = plugin; cash = UnityEngine.Object.FindObjectOfType<Cash_Manager>();
            var stores = plugin.World!.Stores; var display = Fixture(); listing = stores.TestListings.Single(id => stores.TestDisplay(id) == display);
            previousItem = plugin.World.Items.TestNewestPrefab(stores.TestState(listing).Prefab);
            if (plugin.TestOptions.IsHost)
            {
                plugin.World.Channel.CommandRequested += Acknowledge;
                var item = plugin.World.Items.TestSpawn(UnityEngine.Object.FindObjectOfType<Gameplay>().PlayerWalking.transform.position + Vector3.up);
                item.transform.localScale = Vector3.one * 0.63f;
            }
        }
        private void Acknowledge(uint peer, ulong entity, byte[] payload)
        { if (peer != 0 && entity == ClockState.Entity && payload.Length == 1) ack = Math.Max(ack, payload[0]); }
        private void Signal(byte stage) => plugin.World!.Channel.Request(ClockState.Entity, new[] { stage });
        private void Request(Guid operation, float price = 12.34f) => plugin.World!.Stores.TestRequest(listing, operation, price);
        private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException("Store test: " + reason); }
        internal bool Tick(Gameplay gameplay)
        {
            if (Time.unscaledTime < next) return false;
            if (!plugin.TestOptions.IsHost && phase == 10) { plugin.BeginPairedTestSession(); phase = 11; return false; }
            var world = plugin.World; if (world?.Channel.FullySynchronized != true) return false;
            var stores = world.Stores; var state = stores.TestState(listing); var display = stores.TestDisplay(listing);
            if (plugin.TestOptions.IsHost)
            {
                if (!hostHolding)
                {
                    ulong held = world.Items.TestTaggedItem(1, 0.63f); if (held == 0) return false;
                    world.Items.TestGrab(held); hostHolding = true;
                }
                if (phase == 0 && ack >= 1) { Require(gameplay.Player.IsHoldingAnObject && stores.TestPurchases == 1 && cash.GetCash == 8766, "Purchase was not charged exactly once."); display.gameObject.SetActive(true); phase = 1; }
                else if (phase == 1 && ack >= 2) { display.store.isOpen = false; phase = 2; }
                else if (phase == 2 && ack >= 3) { display.store.isOpen = true; cash.LoadSaveSystem(100, new float[5]); phase = 3; }
                else if (phase == 3 && ack >= 4) { cash.LoadSaveSystem(10000, new float[5]); phase = 4; }
                else if (phase == 4 && ack >= 5)
                { Require(stores.TestPurchases == 1 && cash.GetCash == 10000, "A rejected or replayed purchase produced side effects."); stores.TestDeferDelivery = true; cash.Event_Received(1); phase = 5; }
                else if (phase == 5 && ack >= 6)
                {
                    Require(!stores.TransactionsSettled, "Deferred paid delivery incorrectly reported settled.");
                    world.BlockNewTransactions(true);
                    stores.TestRequest(listing, Guid.NewGuid(), state.Price);
                    Require(stores.TestLastResult?.Status == PurchaseStatus.NativeRejected && stores.TestPurchases == 2 && cash.GetCash == 8866,
                        "Frozen host accepted a fresh raw purchase or changed its ledger.");
                    stores.TestDeferDelivery = false; phase = 6;
                }
                else if (phase == 6 && ack >= 7)
                { Require(stores.TestPurchases == 2 && cash.GetCash == 8866 && stores.TransactionsSettled, "Fallback delivery changed the committed purchase or failed to drain."); world.BlockNewTransactions(false); cash.Event_Received(1); phase = 7; next = Time.unscaledTime + 0.5f; }
                else if (phase == 7) { world.Items.TestDrop(false); world.Channel.CommandRequested -= Acknowledge; return true; }
                return false;
            }
            if (phase == 0)
            {
                Require(state.Price == 12.34f && state.Open && state.Available && !state.Multiple && cash.GetCash == 10000, "Host listing/ledger missing at late join.");
                // Initial player spawn is deliberately far from the selected shop.
                Require(Vector3.Distance(gameplay.PlayerWalking.transform.position, display.transform.position) > 8, "Fixture is not distant from its store.");
                // Sized bolt packs and tires used to be unregistered, so the guest was quoted "not enough cash" for them.
                var sized = stores.TestListings.Select(stores.TestDisplay).FirstOrDefault(d => d.CustomIntValue1 >= 0 && d.GetType() == typeof(ItemForSale_Display));
                Require(sized != null, "No sized (bolt pack/tire) listing is shared.");
                stores.HoverDisplay = sized; bool quoted = stores.CanQuote(sized!.Get_price); stores.HoverDisplay = null;
                Require(quoted, "The guest cannot quote a sized listing.");
                Debug.Log("MonMP stores: guest quotes sized listing " + sized.Get_RealObjectToBuy?.name + " size " + sized.CustomIntValue1);
                Request(distant); phase = 1; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 1)
            {
                Require(stores.TestLastResult?.Status == PurchaseStatus.TooFar && cash.GetCash == 10000 && state.Available, "Out-of-range purchase succeeded.");
                Vector3 position = display.transform.position + new Vector3(0, 0.3f, 1);
                gameplay.PlayerWalking.transform.position = position; gameplay.Get_Player_Rigidbody.position = position; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                phase = 2; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 2) { Request(distant); Request(Guid.NewGuid(), 1); phase = 3; next = Time.unscaledTime + 0.5f; }
            else if (phase == 3)
            {
                Require(stores.TestLastResult?.Status == PurchaseStatus.PriceChanged && cash.GetCash == 10000 && state.Available, "Rejected operation replay or stale price succeeded.");
                Request(purchase); Request(purchase); Request(Guid.NewGuid()); phase = 4;
            }
            else if (phase == 4 && !state.Available && cash.GetCash == 8766 && stores.TestSuccessfulReplies > 0 && world.Items.TestNewestPrefab(state.Prefab) > previousItem)
            {
                var item = world.Items.TestItem(world.Items.TestNewestPrefab(state.Prefab));
                Require(item != null && item.gameObject.activeInHierarchy && item.PrefabName == "Drink/MapleSyrup" && gameplay.Player.Get_HoldingItem == item &&
                    world.Items.TestState(world.Items.TestNewestPrefab(state.Prefab)).Lease.Owner == plugin.Session!.LocalPeerId,
                    "Purchased item did not reach the buyer's native hands and lease.");
                world.Items.TestDrop(false); Signal(1); phase = 5;
            }
            else if (phase == 5 && state.Available)
            {
                Request(purchase); display.gameObject.SetActive(false); AccessTools.Field(typeof(ItemForSale_Display), "price").SetValue(display, 1f);
                repairs = world.Channel.RepairsApplied;
                Require(world.Channel.RequestResync(SnapshotScope.Section(StoreListingState.Adapter), "Store listing repair"), "Store repair request failed.");
                phase = 6; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 6 && world.Channel.RepairsApplied > repairs)
            { Require(!gameplay.Player.IsHoldingAnObject && cash.GetCash == 8766 && state.Available && display.gameObject.activeSelf && display.Get_price == state.Price, "Restock allowed an old operation to execute again."); Signal(2); phase = 7; }
            else if (phase == 7 && !state.Open) { Request(Guid.NewGuid()); phase = 8; next = Time.unscaledTime + 0.5f; }
            else if (phase == 8)
            { Require(stores.TestLastResult?.Status == PurchaseStatus.Closed && cash.GetCash == 8766 && state.Available, "Closed shop purchase succeeded."); Signal(3); phase = 9; }
            else if (phase == 9 && cash.GetCash == 100 && state.Open)
            { Request(Guid.NewGuid()); phase = 12; next = Time.unscaledTime + 0.5f; }
            else if (phase == 12)
            { Require(stores.TestLastResult?.Status == PurchaseStatus.Funds && cash.GetCash == 100 && state.Available, "Insufficient-funds purchase succeeded."); Signal(4); phase = 13; }
            else if (phase == 13 && cash.GetCash == 10000)
            {
                plugin.EndPairedTestSession(); Require(stores.TestRestored && display.Get_price == 9.87f && !display.store.isStoreOpen() && cash.GetCash == 420, "Offline store/ledger baseline not restored.");
                phase = 10; next = Time.unscaledTime + 1;
            }
            else if (phase == 11)
            { Require(state.Available && cash.GetCash == 10000, "Reconnect lost restocked listing."); Request(purchase); phase = 14; next = Time.unscaledTime + 0.5f; }
            else if (phase == 14)
            { Require(stores.TestSuccessfulReplies > 0 && !gameplay.Player.IsHoldingAnObject && state.Available && cash.GetCash == 10000, "Reconnect replay charged twice."); Signal(5); phase = 15; }
            else if (phase == 15)
            { if (cash.GetCash == 10100) { Request(Guid.NewGuid()); phase = 16; } else { Signal(5); next = Time.unscaledTime + 0.3f; } }
            else if (phase == 16 && cash.GetCash == 8866)
            {
                Vector3 far = display.transform.position + new Vector3(30, 1, 0);
                gameplay.PlayerWalking.transform.position = far; gameplay.Get_Player_Rigidbody.position = far; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                phase = 17; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 17) { Signal(6); phase = 18; }
            else if (phase == 18 && stores.TestLastResult?.Status == PurchaseStatus.Placed && world.Channel.Records.ContainsKey(stores.TestLastResult.Item))
            {
                var result = stores.TestLastResult;
                Require(!gameplay.Player.IsHoldingAnObject && world.Items.TestItem(result.Item) != null && world.Items.TestState(result.Item).Lease.Owner == ItemLease.Unowned,
                    "Moving away before delivery did not leave the paid item available in the world."); Signal(7); phase = 19;
            }
            else if (phase == 19) { if (cash.GetCash == 8966) return true; Signal(7); next = Time.unscaledTime + 0.3f; }
            return false;
        }
    }
}
