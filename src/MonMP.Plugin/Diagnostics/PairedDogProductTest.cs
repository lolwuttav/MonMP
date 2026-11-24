using System;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Patches;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedDogProductTest
    {
        private const float Price = 7.65f;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedStores stores;
        private readonly Cash_Manager cash;
        private readonly ulong gateListing, rangeListing, fundsListing;
        private readonly Guid gateOperation = Guid.NewGuid();
        private readonly Guid rangeOperation = Guid.NewGuid();
        private readonly Guid fundsOperation = Guid.NewGuid();
        private readonly Guid purchaseOperation = Guid.NewGuid();
        private int phase, acknowledged;
        private int duplicateReplyBaseline;
        private float next;
        private float nextSignal;
        private byte requestedStage;
        private bool hostObserverAttached;
        public string Status => "dog product phase " + phase;

        private static readonly System.Reflection.FieldInfo NeedLeoTamed =
            AccessTools.Field(typeof(ItemForSale_Display_DogProduct), "NeedLeoTamedToPurschase") ??
            throw new InvalidOperationException("Dog-product Leo prerequisite field is missing.");

        internal static void Prepare(bool host)
        {
            DogProductPurchasePatch.ResetTestCounters();
            var displays = UnityEngine.Object.FindObjectsOfType<ItemForSale_Display_DogProduct>(true)
                .Where(d => d.gameObject.scene.IsValid() && d.store != null && d.CustomIntValue1 == -1 && d.Get_RealObjectToBuy != null &&
                    (bool)NeedLeoTamed.GetValue(d)!).OrderBy(d => RuntimeProbe.CanonicalPath(d.transform), StringComparer.Ordinal).ToArray();
            if (displays.Length < 3) throw new InvalidOperationException("Dog-product fixture needs three Leo-gated sale displays.");
            var price = AccessTools.Field(typeof(ItemForSale_Display), "price")!;
            var multiples = AccessTools.Field(typeof(ItemForSale_Display), "CanBuyMultiplesADay")!;
            foreach (var display in displays)
            {
                display.gameObject.SetActive(true);
                display.store.isOpen = host;
                price.SetValue(display, Price);
                multiples.SetValue(display, true);
            }
            UnityEngine.Object.FindObjectOfType<Cash_Manager>().LoadSaveSystem(host ? 10000 : 420, new float[5]);
            // Deliberately make the guest disagree with the host. The host starts untamed so
            // its world state must deny the first attempt, even though the guest is tamed.
            SetLeoTamed(UnityEngine.Object.FindObjectOfType<Gameplay>(), !host);
        }

        internal PairedDogProductTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay; stores = plugin.World!.Stores;
            cash = UnityEngine.Object.FindObjectOfType<Cash_Manager>();
            var candidates = stores.TestListings.Select(id => (Id: id, Display: stores.TestDisplay(id)))
                .Where(p => p.Display is ItemForSale_Display_DogProduct && (bool)NeedLeoTamed.GetValue(p.Display)!)
                .OrderBy(p => RuntimeProbe.CanonicalPath(p.Display.transform), StringComparer.Ordinal).Take(3).ToArray();
            if (candidates.Length != 3) throw new InvalidOperationException("Three supported Leo-gated dog-product listings were not registered.");
            gateListing = candidates[0].Id; rangeListing = candidates[1].Id; fundsListing = candidates[2].Id;
            if (plugin.TestOptions.IsHost)
            {
                plugin.World.Channel.CommandRequested += HostCommand;
                hostObserverAttached = true;
            }
            else plugin.World.Channel.ReplyReceived += TestReply;
        }

        private static void SetLeoTamed(Gameplay gameplay, bool tamed)
        {
            var manager = gameplay.Get_Leo_Manager ?? throw new InvalidOperationException("Leo manager is unavailable in the dog-product fixture.");
            var type = manager.GetType();
            var state = AccessTools.Field(type, tamed ? "tamedSM" : "scaredSM")?.GetValue(manager) ??
                throw new InvalidOperationException("Leo state machine fixture is missing.");
            (AccessTools.Method(type, "ChangeState") ?? throw new InvalidOperationException("Leo state transition method is missing."))
                .Invoke(manager, new[] { state });
            if (manager.Get_IsTamed != tamed) throw new InvalidOperationException("Could not set the local Leo fixture state.");
        }

        private static void Require(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Dog-product test: " + reason); }

        private ItemForSale_Display_DogProduct Display(ulong listing) =>
            stores.TestDisplay(listing) as ItemForSale_Display_DogProduct ?? throw new InvalidOperationException("Dog-product listing changed type.");

        private void MovePlayer(Vector3 position)
        {
            gameplay.PlayerWalking.transform.position = position;
            if (gameplay.Get_Player_Rigidbody != null)
            { gameplay.Get_Player_Rigidbody.position = position; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        }

        private void Signal(byte stage) => plugin.World!.Channel.Request(CottagePurchaseState.Entity, new[] { stage }, true);

        private void RequestStage(byte stage)
        {
            requestedStage = stage;
            SendPendingStage();
        }

        private void SendPendingStage()
        {
            if (requestedStage == 0 || requestedStage <= acknowledged || Time.unscaledTime < nextSignal) return;
            Signal(requestedStage);
            nextSignal = Time.unscaledTime + 0.5f;
        }

        private void TestReply(ulong entity, byte[] payload)
        {
            if (entity == CottagePurchaseState.Entity && payload.Length == 1) acknowledged = Math.Max(acknowledged, payload[0]);
        }

        private void HostCommand(uint peer, ulong entity, byte[] payload)
        {
            if (peer == 0 || entity != CottagePurchaseState.Entity || payload.Length != 1) return;
            byte stage = payload[0];
            if (stage <= acknowledged) { plugin.World!.Channel.Reply(peer, CottagePurchaseState.Entity, new[] { stage }); return; }
            acknowledged = stage;
            if (stage == 1)
            {
                Require(!gameplay.Get_Leo_Manager.Get_IsTamed && stores.TestLeoDenials == 1 && stores.TestPurchases == 0 && cash.GetCash == 10000,
                    "An untamed host allowed a purchase or charged the shared ledger.");
                SetLeoTamed(gameplay, true);
            }
            else if (stage == 2)
            {
                Require(cash.GetCash == 10000, "Range rejection changed host cash.");
                cash.LoadSaveSystem(100, new float[5]);
            }
            else if (stage == 3)
            {
                Require(cash.GetCash == 100, "Insufficient-funds test did not run against the reduced host ledger.");
                cash.LoadSaveSystem(10000, new float[5]);
            }
            else if (stage == 4)
                Require(stores.TestPurchases == 1 && cash.GetCash == 10000 - Mathf.RoundToInt(Price * 100),
                    "The native dog-product click or duplicate replay charged more than once.");
            plugin.World!.Channel.Reply(peer, CottagePurchaseState.Entity, new[] { stage });
        }

        internal bool Tick()
        {
            var world = plugin.World;
            if (world?.Channel.FullySynchronized != true) return false;
            if (!plugin.TestOptions.IsHost) SendPendingStage();
            if (Time.unscaledTime < next) return false;
            if (plugin.TestOptions.IsHost)
            {
                if (acknowledged >= 4)
                {
                    if (hostObserverAttached) { world.Channel.CommandRequested -= HostCommand; hostObserverAttached = false; }
                    return true;
                }
                return false;
            }

            var gateDisplay = Display(gateListing); var rangeDisplay = Display(rangeListing); var fundsDisplay = Display(fundsListing);
            if (phase == 0)
            {
                Require(cash.GetCash == 10000 && gameplay.Get_Leo_Manager.Get_IsTamed && stores.TestState(gateListing).Open &&
                    stores.TestState(gateListing).Price == Price, "The host's dog listing, shared ledger, or deliberately tamed guest fixture did not arrive.");
                MovePlayer(gateDisplay.transform.position + Vector3.up * 0.25f);
                stores.TestNextOperation = gateOperation;
                gateDisplay.MouseDown(); // This override must be intercepted before its local Leo check.
                phase = 1; next = Time.unscaledTime + 0.7f; return false;
            }
            if (phase == 1 && stores.TestLastResult?.Operation == gateOperation)
            {
                Require(stores.TestLastResult!.Status == PurchaseStatus.Unavailable && cash.GetCash == 10000 && stores.TestState(gateListing).Available,
                    "Guest-local Leo state overrode the untamed host's denial.");
                stores.TestRequest(gateListing, gateOperation, Price);
                phase = 2; next = Time.unscaledTime + 0.7f; return false;
            }
            if (phase == 2 && stores.TestLastResult?.Operation == gateOperation && stores.TestReplies >= 2)
            {
                Require(stores.TestLastResult!.Status == PurchaseStatus.Unavailable && cash.GetCash == 10000,
                    "Duplicate denied operation changed the purchase result or ledger.");
                RequestStage(1); phase = 3; next = Time.unscaledTime + 0.4f; return false;
            }
            if (phase == 3 && acknowledged >= 1)
            {
                SetLeoTamed(gameplay, false);
                MovePlayer(rangeDisplay.transform.position + Vector3.right * 30);
                phase = 4; next = Time.unscaledTime + 0.8f; return false;
            }
            if (phase == 4)
            {
                stores.TestRequest(rangeListing, rangeOperation, Price);
                phase = 5; next = Time.unscaledTime + 0.7f; return false;
            }
            if (phase == 5 && stores.TestLastResult?.Operation == rangeOperation)
            {
                Require(stores.TestLastResult!.Status == PurchaseStatus.TooFar && cash.GetCash == 10000,
                    "Out-of-range dog-product purchase succeeded or changed cash.");
                RequestStage(2); phase = 6; next = Time.unscaledTime + 0.5f; return false;
            }
            if (phase == 6 && acknowledged >= 2 && cash.GetCash == 100)
            {
                MovePlayer(fundsDisplay.transform.position + Vector3.up * 0.25f);
                phase = 7; next = Time.unscaledTime + 0.8f; return false;
            }
            if (phase == 7)
            {
                stores.TestRequest(fundsListing, fundsOperation, Price);
                phase = 8; next = Time.unscaledTime + 0.7f; return false;
            }
            if (phase == 8 && stores.TestLastResult?.Operation == fundsOperation)
            {
                Require(stores.TestLastResult!.Status == PurchaseStatus.Funds && cash.GetCash == 100,
                    "Insufficient shared cash allowed a dog-product purchase.");
                RequestStage(3); phase = 9; next = Time.unscaledTime + 0.5f; return false;
            }
            if (phase == 9 && acknowledged >= 3 && cash.GetCash == 10000)
            {
                MovePlayer(gateDisplay.transform.position + Vector3.up * 0.25f);
                phase = 10; next = Time.unscaledTime + 0.8f; return false;
            }
            if (phase == 10)
            {
                Require(!gameplay.Get_Leo_Manager.Get_IsTamed, "Guest Leo fixture unexpectedly became host-authoritative.");
                stores.TestNextOperation = purchaseOperation;
                gateDisplay.MouseDown(); // Host Leo is tamed; the guest's untamed native gate must be bypassed.
                phase = 11; next = Time.unscaledTime + 0.8f; return false;
            }
            if (phase == 11 && stores.TestLastResult?.Operation == purchaseOperation &&
                (stores.TestLastResult!.Status == PurchaseStatus.Held || stores.TestLastResult!.Status == PurchaseStatus.Placed))
            {
                var result = stores.TestLastResult!;
                Require(cash.GetCash == 10000 - Mathf.RoundToInt(Price * 100) && result.Item > WorldRecord.DynamicIdStart &&
                    world.Items.TestItem(result.Item) != null && world.Items.TestState(result.Item).Lease.Owner == plugin.Session!.LocalPeerId,
                    "Host-approved native dog-product click did not debit once and deliver the shared item.");
                world.Items.TestDrop(false);
                duplicateReplyBaseline = stores.TestReplies;
                stores.TestRequest(gateListing, purchaseOperation, Price);
                phase = 12; next = Time.unscaledTime + 0.7f; return false;
            }
            if (phase == 12 && stores.TestReplies > duplicateReplyBaseline && stores.TestLastResult?.Operation == purchaseOperation)
            {
                Require((stores.TestLastResult!.Status == PurchaseStatus.Held || stores.TestLastResult!.Status == PurchaseStatus.Placed) &&
                    cash.GetCash == 10000 - Mathf.RoundToInt(Price * 100), "Duplicate successful operation was charged twice.");
                int blockedBefore = DogProductPurchasePatch.TestUnboundBlocks;
                float cashBefore = cash.GetCash;
                SetLeoTamed(gameplay, true);
                var clone = UnityEngine.Object.Instantiate(gateDisplay.gameObject, gateDisplay.transform.position + Vector3.right * 0.5f, gateDisplay.transform.rotation);
                clone.GetComponent<ItemForSale_Display_DogProduct>().MouseDown();
                UnityEngine.Object.Destroy(clone);
                SetLeoTamed(gameplay, false);
                Require(DogProductPurchasePatch.TestUnboundBlocks == blockedBefore + 1 && cash.GetCash == cashBefore,
                    "An unregistered DogProduct click was not suppressed while multiplayer was active.");
                RequestStage(4); phase = 13; next = Time.unscaledTime + 0.4f; return false;
            }
            if (phase == 13 && acknowledged >= 4)
            {
                plugin.EndPairedTestSession(); return true;
            }
            return false;
        }
    }
}
