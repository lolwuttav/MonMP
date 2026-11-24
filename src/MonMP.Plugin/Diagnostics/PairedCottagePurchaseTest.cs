using System;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Exercises the sign hook, host-side price/cash/range/progression checks, duplicate replay,
    // replicated gate/sign animation and restoration of the guest's original presentation.
    internal sealed class PairedCottagePurchaseTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedCottagePurchase shared;
        private readonly Cash_Manager cash;
        private CottageContentsState initialContents;
        private int contentsRepairBaseline;
        private ushort mugPrefab;
        private ulong previousMug, percolatorMug;
        private int phase, ack;
        private float deadline, next;
        private static readonly Guid TooFarOperation = Guid.Parse("d93c6598-9848-4ff8-b1e2-000000000001");
        private static readonly Guid ChangedPriceOperation = Guid.Parse("d93c6598-9848-4ff8-b1e2-000000000002");
        private static readonly Guid DuplicateOperation = Guid.Parse("d93c6598-9848-4ff8-b1e2-000000000003");
        private static readonly Guid AlreadyBoughtOperation = Guid.Parse("d93c6598-9848-4ff8-b1e2-000000000004");

        internal string Status => "cottage purchase/contents phase=" + phase + "; ack=" + ack;

        internal PairedCottagePurchaseTest(MonMPPlugin plugin, Gameplay gameplay, SharedCottagePurchase shared)
        {
            this.plugin = plugin;
            this.gameplay = gameplay;
            this.shared = shared;
            cash = UnityEngine.Object.FindObjectOfType<Cash_Manager>();
            if (plugin.TestOptions.IsHost) plugin.World!.Channel.CommandRequested += Acknowledge;
        }

        internal static void Prepare(bool host)
        {
            var cottage = UnityEngine.Object.FindObjectOfType<Cottage>();
            var todo = UnityEngine.Object.FindObjectOfType<ToDoList_Manager>();
            if (cottage == null || todo == null) throw new InvalidOperationException("Cottage test requires the native cottage and initialized progression.");
            if (host && todo.isDone(ToDoList_Line.Intermediate_CottageBought))
                throw new InvalidOperationException("Cottage test host save must have the cottage for sale.");
            SeedContents(cottage, host);
            UnityEngine.Object.FindObjectOfType<Cash_Manager>().LoadSaveSystem(100000, new float[5]);
        }

        private static void SeedContents(Cottage cottage, bool host)
        {
            if (cottage.compostMachine == null || cottage.Cottage_Wheelbarrow == null ||
                cottage.CoffeeSeedAutoSpawn == null || cottage.CoffeePercolator == null)
                throw new InvalidOperationException("Cottage test requires every native contents component.");
            cottage.compostMachine.LoadFromSave(host ? 0.27f : 0.81f, host ? 0.64f : 0.16f);
            var barrow = cottage.Cottage_Wheelbarrow.transform;
            barrow.SetPositionAndRotation(barrow.position + (host ? Vector3.right * 0.6f : Vector3.forward * 0.8f),
                barrow.rotation * Quaternion.Euler(0, host ? 35f : 110f, 0));
            cottage.CoffeeSeedAutoSpawn.LoadFromSave(!host);
            cottage.CoffeePercolator.LoadFromSave(host ? 2 : 1);
        }

        private void Acknowledge(uint peer, ulong entity, byte[] payload)
        {
            if (peer != 0 && entity == CottagePurchaseState.Entity && payload.Length == 1) ack = Math.Max(ack, payload[0]);
        }

        // Use the Cottage purchase record's lease: the shared clock revision advances while
        // this long paired scenario runs, which can make otherwise-valid stage signals stale.
        // The one-byte fixture payload is intentionally ignored by the purchase adapter.
        private void Signal(byte value) => plugin.World!.Channel.Request(CottagePurchaseState.Entity, new[] { value }, true);

        internal bool Tick()
        {
            var world = plugin.World;
            if (world?.Channel.FullySynchronized != true || shared.TestSign == null || shared.TestCottage == null) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 180;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Cottage purchase test stalled: " + Status);

            if (plugin.TestOptions.IsHost) return Host(world);
            return Guest(world);
        }

        private bool Host(SharedWorld world)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                Require(!shared.TestState.Purchased && shared.TestSign!.gameObject.activeInHierarchy,
                    "the host fixture did not start with the cottage for sale.");
                Require(cash.GetCash == 100000, "the host test cash was not initialized to $1,000.");
                phase = 1;
            }
            else if (phase == 1 && ack >= 1)
            {
                Require(shared.TestNativePurchases == 0 && cash.GetCash == 100000,
                    "an unaffordable cottage request changed host cash or progression.");
                cash.LoadSaveSystem(1000000, new float[5]);
                phase = 2;
            }
            else if (phase == 2 && ack >= 2)
            {
                Require(shared.TestFundsRejections == 1 && shared.TestRangeRejections == 1 && shared.TestPriceRejections == 1 &&
                    shared.TestNativePurchases == 0 && cash.GetCash == 1000000,
                    $"a distant or wrong-price request was not rejected without side effects (funds={shared.TestFundsRejections}, range={shared.TestRangeRejections}, price={shared.TestPriceRejections}, native={shared.TestNativePurchases}, cash={cash.GetCash}).");
                phase = 3;
            }
            else if (phase == 3 && ack >= 3)
            {
                var todo = UnityEngine.Object.FindObjectOfType<ToDoList_Manager>();
                Require(ack >= 3 && todo.isDone(ToDoList_Line.Intermediate_CottageBought) && shared.TestNativePurchases == 1 &&
                    shared.TestDuplicateReplays >= 1 && cash.GetCash == 470001 && shared.TestContentsState.Purchased && shared.TestContentsMatch,
                    "the duplicate guest command did not produce one native purchase, an exact debit, and a stable contents snapshot.");
                shared.TestAdvanceHostContents();
                mugPrefab = world.Items.TestTypePrefab(typeof(Item_HomemadeCoffeeMug));
                previousMug = world.Items.TestNewestPrefab(mugPrefab);
                phase = 4;
            }
            else if (phase == 4 && ack >= 5)
            {
                Require(shared.TestContentsMatch && shared.TestContentsState.Compost == 0.38f &&
                    shared.TestContentsState.Fertilizer == 0.49f && !shared.TestContentsState.CoffeeSeedsActive &&
                    shared.TestContentsState.PercolatorCoffeeAmount == 0 && shared.TestPercolator!.Get_CoffeeAmount == 0,
                    "the host did not apply the guest's native cottage percolator click to shared coffee contents.");
                percolatorMug = world.Items.TestNewestPrefab(mugPrefab);
                Require(percolatorMug > previousMug && world.Items.TestItem(percolatorMug) != null &&
                    world.Items.TestState(percolatorMug).Lease.Owner == ItemLease.Unowned,
                    "the host did not serve and release the guest-created cottage coffee mug.");
                world.Items.TestDestroy(percolatorMug);
                phase = 5;
            }
            else if (phase == 5 && plugin.Session!.Peers.Count == 1)
            {
                return true;
            }
            return false;
        }

        private bool Guest(SharedWorld world)
        {
            var last = shared.TestLastReply;
            if (phase == 0)
            {
                MoveNear(shared.TestSign!.transform);
                next = Time.unscaledTime + 0.8f;
                phase = 1;
            }
            else if (phase == 1 && Time.unscaledTime >= next)
            {
                Require(shared.TestSign!.Update_NameToAddAfter().Contains("FF777755"),
                    "the guest sign did not show the unaffordable quote from shared cash.");
                shared.TestSign!.MouseDown(); // Goes through the installed native sign prefix.
                phase = 2;
            }
            else if (phase == 2 && last?.Status == PurchaseStatus.Funds)
            {
                Signal(1);
                MoveFar(shared.TestSign!.transform);
                next = Time.unscaledTime + 0.8f;
                phase = 3;
            }
            else if (phase == 3 && Time.unscaledTime >= next)
            {
                shared.TestRequest(TooFarOperation, SharedCottagePurchase.PurchasePriceCents);
                phase = 4;
            }
            else if (phase == 4 && last?.Operation == TooFarOperation && last?.Status == PurchaseStatus.TooFar)
            {
                MoveNear(shared.TestSign!.transform);
                next = Time.unscaledTime + 0.8f;
                phase = 5;
            }
            else if (phase == 5 && Time.unscaledTime >= next)
            {
                shared.TestRequest(ChangedPriceOperation, SharedCottagePurchase.PurchasePriceCents - 1);
                phase = 6;
            }
            else if (phase == 6 && last?.Operation == ChangedPriceOperation && last?.Status == PurchaseStatus.PriceChanged)
            {
                Signal(2);
                next = Time.unscaledTime + 0.8f;
                phase = 9;
            }
            else if (phase == 9 && Time.unscaledTime >= next)
            {
                Require(cash.GetCash == 1000000 && shared.TestSign!.Update_NameToAddAfter().Contains("77FF7A55"),
                    "the guest sign did not show the affordable quote from shared cash.");
                shared.TestRequest(DuplicateOperation, SharedCottagePurchase.PurchasePriceCents);
                shared.TestRequest(DuplicateOperation, SharedCottagePurchase.PurchasePriceCents);
                phase = 7;
            }
            else if (phase == 7 && last?.Operation == DuplicateOperation && last?.Status == PurchaseStatus.Placed &&
                shared.TestState.Purchased && !shared.TestSign!.gameObject.activeSelf &&
                UnityEngine.Object.FindObjectOfType<ToDoList_Manager>().isDone(ToDoList_Line.Intermediate_CottageBought))
            {
                var expectedGate = new Quaternion(shared.TestState.Gate.Qx, shared.TestState.Gate.Qy, shared.TestState.Gate.Qz, shared.TestState.Gate.Qw);
                Require(Quaternion.Angle(shared.TestGate!.rotation, expectedGate) < 0.01f,
                    "the guest gate transform did not follow the host's current animation sample.");
                Require(shared.TestContentsState.Purchased && shared.TestContentsMatch,
                    "the host purchase did not initialize the guest compost, wheelbarrow, coffee-seed and percolator state.");
                initialContents = shared.TestContentsState;
                string guardFailure = shared.TestGuestMutationGuards();
                Require(string.IsNullOrEmpty(guardFailure), "guest cottage mutation guard failed: " + guardFailure);
                Signal(3);
                next = Time.unscaledTime + 0.1f;
                phase = 10;
            }
            else if (phase == 10 && Time.unscaledTime >= next)
            {
                var current = shared.TestContentsState;
                if (current.Purchased && current.Compost == 0.38f && current.Fertilizer == 0.49f &&
                    !current.CoffeeSeedsActive && current.PercolatorCoffeeAmount == 1)
                {
                    Require(Vector3.Distance(
                            new Vector3(initialContents.Wheelbarrow.X, initialContents.Wheelbarrow.Y, initialContents.Wheelbarrow.Z),
                            new Vector3(current.Wheelbarrow.X, current.Wheelbarrow.Y, current.Wheelbarrow.Z)) > 0.2f &&
                        Quaternion.Angle(
                            new Quaternion(initialContents.Wheelbarrow.Qx, initialContents.Wheelbarrow.Qy, initialContents.Wheelbarrow.Qz, initialContents.Wheelbarrow.Qw),
                            new Quaternion(current.Wheelbarrow.Qx, current.Wheelbarrow.Qy, current.Wheelbarrow.Qz, current.Wheelbarrow.Qw)) > 5f,
                        "ongoing host wheelbarrow pose did not reach the guest.");
                    Require(shared.TestContentsMatch, "the guest did not apply the ongoing host cottage contents record.");
                    shared.TestPerturbGuestContents();
                    contentsRepairBaseline = world.Channel.RepairsApplied;
                    Require(world.Channel.RequestResync(SnapshotScope.ForEntity(CottageContentsState.Adapter, CottageContentsState.Entity),
                            "Cottage contents authority repair test"),
                        "guest cottage contents repair request was rejected.");
                    phase = 11;
                }
            }
            else if (phase == 11 && world.Channel.RepairsApplied > contentsRepairBaseline)
            {
                Require(shared.TestContentsMatch, "guest-side cottage contents repair did not restore host state.");
                Signal(4);
                next = Time.unscaledTime + 0.5f;
                phase = 12;
            }
            else if (phase == 12 && Time.unscaledTime >= next)
            {
                MoveNear(shared.TestPercolator!.transform);
                next = Time.unscaledTime + 0.8f;
                phase = 13;
            }
            else if (phase == 13 && Time.unscaledTime >= next)
            {
                var percolator = shared.TestPercolator!;
                Require(shared.TestContentsState.PercolatorCoffeeAmount == 1 && percolator.Get_CoffeeAmount == 1,
                    "the cottage percolator fixture did not have one host-brewed mug for the guest click.");
                mugPrefab = plugin.Items!.TestTypePrefab(typeof(Item_HomemadeCoffeeMug));
                previousMug = plugin.Items.TestNewestPrefab(mugPrefab);
                percolator.MouseDown(); // Native guest callback routes serving to the host.
                deadline = Time.unscaledTime + 10f;
                phase = 14;
            }
            else if (phase == 14)
            {
                if (Time.unscaledTime > deadline) throw new InvalidOperationException("Cottage percolator guest click did not produce a shared mug: " + Status);
                ulong newest = plugin.Items!.TestNewestPrefab(mugPrefab);
                if (newest <= previousMug || plugin.Items.TestItem(newest) is not Item_HomemadeCoffeeMug mug) return false;
                percolatorMug = newest;
                Require(plugin.Items.TestState(percolatorMug).Lease.Owner == plugin.Session!.LocalPeerId &&
                    ReferenceEquals(gameplay.Player.Get_HoldingItem, mug),
                    "The host-created cottage coffee mug was not granted to the guest's vanilla hand.");
                Require(shared.TestContentsMatch && shared.TestContentsState.PercolatorCoffeeAmount == 0,
                    "The host's cottage percolator decrement did not replicate to the guest.");
                plugin.Items.TestDrop(true);
                Signal(5);
                next = Time.unscaledTime + 0.4f;
                phase = 15;
            }
            else if (phase == 15 && Time.unscaledTime >= next &&
                plugin.Items!.TestState(percolatorMug).Lease.Owner == ItemLease.Unowned)
            {
                shared.TestRequest(AlreadyBoughtOperation, SharedCottagePurchase.PurchasePriceCents);
                phase = 8;
            }
            else if (phase == 8 && last?.Operation == AlreadyBoughtOperation && last?.Status == PurchaseStatus.Unavailable)
            {
                plugin.EndPairedTestSession();
                Require(shared.TestRestored && shared.TestContentsRestored,
                    "disconnect did not restore the guest's original cottage progression, presentation and contents: " + shared.TestRestoreDetails);
                return true;
            }
            return false;
        }

        private void MoveNear(Transform target)
        {
            for (float radius = 1; radius <= 3; radius += 1)
                for (int step = 0; step < 16; step++)
                {
                    float angle = step * Mathf.PI / 8;
                    var at = target.position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                    if (!Physics.Raycast(at + Vector3.up * 30, Vector3.down, out var hit, 60, ~0, QueryTriggerInteraction.Ignore)) continue;
                    at = hit.point + Vector3.up;
                    if (Physics.OverlapCapsule(at, at + Vector3.up, 0.4f, ~0, QueryTriggerInteraction.Ignore).Any(c => !(c is TerrainCollider))) continue;
                    SetPlayer(at);
                    return;
                }
            throw new InvalidOperationException("Cottage test: no clear standing point near the sale sign.");
        }

        private void MoveFar(Transform target) => SetPlayer(target.position + Vector3.right * 100);

        private void SetPlayer(Vector3 at)
        {
            gameplay.PlayerWalking.transform.position = at;
            if (gameplay.Get_Player_Rigidbody == null) return;
            gameplay.Get_Player_Rigidbody.position = at;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        private static void Require(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Cottage purchase test: " + reason); }
    }
}
