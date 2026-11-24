using System;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UniStorm;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // A loose native bolt in the host container is sold by a guest sign click. Cash and the
    // item tombstone must both come from the host, while range and duplicate requests stay inert.
    internal sealed class PairedMetalSaleTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedMetalSales sales;
        private readonly Cash_Manager cash;
        private readonly ushort prefab;
        private readonly Guid distant = Guid.NewGuid();
        private readonly Guid sale = Guid.NewGuid();
        private ulong bolt;
        private int phase;
        private bool sawUnsettledBeforeTombstone;
        private float next, deadline, earned;
        internal string Status => "metal sale phase=" + phase + "; bolt=" + bolt.ToString("X") + "; cash=" + cash.GetCash + "; result=" + sales.TestLastReply?.Status;

        internal static void Prepare(bool host)
        {
            UniStormSystem.Instance.TimeFlow = UniStormSystem.EnableFeature.Disabled;
            UnityEngine.Object.FindObjectOfType<Cash_Manager>().LoadSaveSystem(host ? 10000 : 420, new float[5]);
        }

        internal PairedMetalSaleTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay; sales = plugin.World!.MetalSales;
            cash = UnityEngine.Object.FindObjectOfType<Cash_Manager>();
            prefab = plugin.Items!.TestPrefab("Hardware/Hardware_Bolt12");
            earned = GameManager.Get_PlayerStats[3];
            Require(sales.TestContainer != null && sales.TestSign != null, "native container/sign missing.");
            if (plugin.TestOptions.IsHost) plugin.World.Channel.CommandRequested += ObserveSaleCommand;
        }

        private void ObserveSaleCommand(uint peer, ulong id, byte[] payload)
        {
            if (peer == 0 || id != sales.TestEntity || !SharedMetalSales.SaleRequest.Is(payload) ||
                sales.TestNativeSales != 1 || sawUnsettledBeforeTombstone) return;
            Require(bolt != 0 && plugin.Items!.TestState(bolt).Exists && !sales.TransactionsSettled,
                "native sale settled before the shared item tombstone was captured.");
            sawUnsettledBeforeTombstone = true;
        }

        internal bool Tick()
        {
            if (deadline == 0) deadline = Time.unscaledTime + 120;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Metal sale test stalled: " + Status);
            if (Time.unscaledTime < next || plugin.World?.Channel.FullySynchronized != true) return false;
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            var items = plugin.Items!;
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                var renderer = (MeshRenderer)AccessTools.Field(typeof(Scrapyard_Container_SellMetal), "DetectionBox").GetValue(sales.TestContainer!);
                Require(renderer != null, "native detection box missing.");
                sales.TestContainer!.Detection();
                Require(sales.TestContainer.Metal_Amount == 0, "the isolated test container is not empty.");
                var spawned = items.TestSpawn(renderer!.bounds.center, prefab);
                if (spawned.rb != null) spawned.rb.isKinematic = true;
                Physics.SyncTransforms();
                phase = 1; next = Time.unscaledTime + 1;
            }
            else if (phase == 1)
            {
                bolt = items.TestNewestPrefab(prefab);
                if (bolt == 0 || items.TestItem(bolt) == null) return false;
                sales.TestContainer!.Detection();
                Require(sales.TestContainer.Metal_Amount == 1 && sales.TestContainer.Metal_Weight == 1f,
                    "host native detection did not see exactly one loose bolt.");
                phase = 2;
            }
            else if (phase == 2 && sales.TestRangeRejections >= 1) phase = 3;
            else if (phase == 3 && sales.TestNativeSales == 1 && !items.TestState(bolt).Exists && sales.TransactionsSettled)
            {
                Require(sawUnsettledBeforeTombstone, "the native sale never exposed an unsettled transaction before item removal.");
                Require(cash.GetCash == 10200 && GameManager.Get_PlayerStats[3] == earned + 200,
                    "the native host sale did not credit exactly $2 once.");
                phase = 4;
            }
            else if (phase == 4 && plugin.Session!.Peers.Count == 1)
            {
                Require(sales.TestDuplicateReplays >= 1 && sales.TestUnsafeRejections >= 1 &&
                    sales.TestNativeSales == 1 && cash.GetCash == 10200 &&
                    !items.TestState(bolt).Exists && items.TestItem(bolt) == null,
                    "duplicate sale or guest disconnect changed the host sale result.");
                plugin.World!.Channel.CommandRequested -= ObserveSaleCommand;
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            var items = plugin.Items!;
            if (phase == 0)
            {
                bolt = items.TestNewestPrefab(prefab);
                if (bolt == 0 || items.TestItem(bolt) == null) return false;
                Require(cash.GetCash == 10000 && sales.TestSign!.gameObject.activeInHierarchy,
                    "host ledger or native sign did not reach the guest.");
                Move(sales.TestSign!.transform.position + Vector3.right * 30);
                phase = 1; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 1)
            {
                sales.TestRequest(distant);
                phase = 2;
            }
            else if (phase == 2 && sales.TestLastReply?.Operation == distant && sales.TestLastReply?.Status == PurchaseStatus.TooFar)
            {
                Require(cash.GetCash == 10000 && items.TestState(bolt).Exists,
                    "distant sale changed cash or removed the metal.");
                Move(sales.TestSign!.transform.position + Vector3.up);
                phase = 3; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 3)
            {
                // This public sign path must be intercepted before native local sale effects.
                sales.TestSign!.MouseDown();
                // A distinct operation in the same frame must not cash the deferred Destroy twice.
                sales.TestRequest(sale); sales.TestRequest(sale);
                Require(cash.GetCash == 10000 && items.TestState(bolt).Exists,
                    "guest sign click ran the native sale locally.");
                phase = 4;
            }
            else if (phase == 4 && cash.GetCash == 10200 && !items.TestState(bolt).Exists &&
                sales.TestLastReply?.Operation == sale && sales.TestLastReply?.Status == PurchaseStatus.NativeRejected)
            {
                Require(items.TestItem(bolt) == null && GameManager.Get_PlayerStats[3] == earned,
                    "host sale failed to remove the guest replica or replayed host earnings locally.");
                phase = 5; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 5)
            {
                Require(sales.TestLastReply?.Operation == sale && sales.TestLastReply?.Status == PurchaseStatus.NativeRejected &&
                    cash.GetCash == 10200, "an empty or duplicate sale produced another payout.");
                plugin.EndPairedTestSession();
                Require(cash.GetCash == 420 && plugin.World == null, "guest cash baseline was not restored on disconnect.");
                return true;
            }
            return false;
        }

        private void Move(Vector3 at)
        {
            gameplay.PlayerWalking.transform.position = at;
            if (gameplay.Get_Player_Rigidbody == null) return;
            gameplay.Get_Player_Rigidbody.position = at;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Metal sale test: " + reason); }
    }
}
