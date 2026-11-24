using System;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedCollectiblesTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private int phase, hostJoints;
        private float next;
        private ushort carryPrefab, tirePrefab, packPrefab, canPrefab, emptyCanPrefab, hugePrefab, bigPrefab;
        private ulong carried, tire, packA, packB, can, huge, oldEmptyCan, oldBigPack, oldPackTop, bigPackToUse;
        private float guestSyrupDrinks, hostSyrupDrinks;
        private Vector3 heldAt;
        private float unpackedPackReadyAt;
        private bool sawHeld, blockedFireWhileHeld, burned;
        public string Status => "simple items phase " + phase + "; items=" + plugin.Items?.Count + "; joints=" + GameManager.Get_WeedJointAmount +
            "; held=" + sawHeld + "; fire=" + blockedFireWhileHeld + "/" + burned + "; unpacked=" + bigPackToUse + "; peers=" + plugin.Session?.Peers.Count;
        public PairedCollectiblesTest(MonMPPlugin plugin, Gameplay gameplay) { this.plugin = plugin; this.gameplay = gameplay; }
        public bool Tick()
        {
            if (Time.unscaledTime < next) return false;
            var items = plugin.Items;
            if (items?.Ready != true) return false;
            var prefabs = items.TestSimplePrefabs;
            Require(prefabs.Length >= 20, "Expected the simple item catalog; found " + prefabs.Length + ".");
            carryPrefab = prefabs.First(p => !items.TestConsumable(p));
            tirePrefab = prefabs.First(p => items.TestType(p) == typeof(Item_Driveline_Tires));
            packPrefab = prefabs.Single(p => items.TestType(p) == typeof(Item_WeedPreRollPack));
            canPrefab = prefabs.Single(p => items.TestType(p) == typeof(Item_MapleSyrupCan));
            emptyCanPrefab = prefabs.Single(p => items.TestType(p) == typeof(Item_MapleSyrupCan_Empty));
            hugePrefab = items.TestTypePrefab(typeof(Item_WeedPreRollHugePack));
            bigPrefab = items.TestTypePrefab(typeof(Item_WeedPreRollBigPack));
            Require(items.TestConsumable(packPrefab) && !items.TestConsumable(tirePrefab), "Unexpected simple item capabilities.");
            return plugin.TestOptions.IsHost ? Host(items, prefabs) : Guest(items, prefabs);
        }
        private bool Host(SharedItems items, ushort[] prefabs)
        {
            if (phase == 0)
            {
                hostJoints = GameManager.Get_WeedJointAmount;
                hostSyrupDrinks = GameManager.Get_PlayerStats[1];
                var origin = gameplay.PlayerWalking.transform.position;
                for (int i = 0; i <= prefabs.Length; i++)
                {
                    var item = items.TestSpawn(origin + new Vector3(4 + i % 6 * 1.5f, 1, 3 + i / 6 * 1.5f), i < prefabs.Length ? prefabs[i] : packPrefab);
                    item.isSpawnFromTheSaveLoadSystem = true;
                }
                items.TestSpawn(origin + new Vector3(6, 1, 10), hugePrefab).isSpawnFromTheSaveLoadSystem = true;
                phase = 1; next = Time.unscaledTime + 1.5f; return false;
            }
            if (phase == 1)
            {
                Require(prefabs.All(p => items.TestNewestPrefab(p) != 0) && items.TestFirstPrefab(packPrefab) != items.TestNewestPrefab(packPrefab),
                    "Host did not register every simple item factory.");
                tire = items.TestNewestPrefab(tirePrefab); can = items.TestNewestPrefab(canPrefab); huge = items.TestNewestPrefab(hugePrefab);
                oldBigPack = items.TestNewestPrefab(bigPrefab); oldPackTop = items.TestNewestPrefab(packPrefab); phase = 2; return false;
            }
            // The huge pack creates several big packs; partial delivery can make the guest use a different
            // one than the host's newest ID. Require an accepted unpack, excluding fire/sale removals.
            bigPackToUse = items.TestUnpackedPrefabAfter(bigPrefab, oldBigPack);
            int unpackedPackCount = items.LiveItems.Count(p => p.Id > oldPackTop && p.Item is Item_WeedPreRollPack);
            if (bigPackToUse != 0 && !items.TestState(bigPackToUse).Exists && unpackedPackCount >= 2 && unpackedPackReadyAt == 0)
                unpackedPackReadyAt = Time.unscaledTime + 1.5f;
            sawHeld |= items.TestState(items.TestNewestPrefab(carryPrefab)).Lease.Owner != ItemLease.Unowned;
            if (!blockedFireWhileHeld && items.HeldByRemotePlayer(items.TestItem(tire)))
            {
                var item = items.TestItem(tire)!;
                item.StartFire();
                Require(item.GetComponent<WoodOnFire>() == null && item.GetComponent<InteractableItems>() != null && items.TestState(tire).Exists,
                    "Host native StartFire consumed a flammable item while a guest held its lease.");
                blockedFireWhileHeld = true;
            }
            if (blockedFireWhileHeld && !burned && items.TestState(tire).Lease.Owner == ItemLease.Unowned)
            {
                var item = items.TestItem(tire)!;
                item.StartFire();
                burned = item.GetComponent<WoodOnFire>() != null;
                Require(burned, "Accepted guest drop did not enter the native fire path on the host.");
                next = Time.unscaledTime + 1; return false;
            }
            if (burned && !items.TestState(tire).Exists && (items.TestState(tire).Values.Flags & ItemFlags.Burned) != 0 &&
                sawHeld && plugin.Session!.Peers.Count <= 1 &&
                bigPackToUse != 0 && !items.TestState(bigPackToUse).Exists && unpackedPackCount >= 2 && Time.unscaledTime >= unpackedPackReadyAt)
            {
                Require(GameManager.Get_WeedJointAmount == hostJoints, "Guest consumption applied effects on the host.");
                Require(GameManager.Get_PlayerStats[1] == hostSyrupDrinks, "Guest syrup can changed the host's drink count.");
                Require(!items.TestState(can).Exists && !items.TestState(huge).Exists && items.TestNewestPrefab(bigPrefab) > oldBigPack,
                    "Host did not consume the can and unpack the huge pack.");
                Require(unpackedPackCount >= 2, "Guest use of the shared big pack did not create at least two shared pre-roll packs on the host.");
                plugin.EndPairedTestSession(); return true;
            }
            return false;
        }
        private bool Guest(SharedItems items, ushort[] prefabs)
        {
            if (phase == 0)
            {
                if (!prefabs.All(p => items.TestNewestPrefab(p) != 0 && items.TestItem(items.TestNewestPrefab(p)) != null) ||
                    items.TestFirstPrefab(packPrefab) == items.TestNewestPrefab(packPrefab)) return false;
                carried = items.TestNewestPrefab(carryPrefab); tire = items.TestNewestPrefab(tirePrefab);
                packA = items.TestFirstPrefab(packPrefab); packB = items.TestNewestPrefab(packPrefab);
                can = items.TestNewestPrefab(canPrefab); huge = items.TestNewestPrefab(hugePrefab);
                oldEmptyCan = items.TestNewestPrefab(emptyCanPrefab); oldBigPack = items.TestNewestPrefab(bigPrefab);
                if (can == 0 || huge == 0) return false;
                guestSyrupDrinks = GameManager.Get_PlayerStats[1];
                MoveNear(items.TestItem(carried)!.transform.position); phase = 1; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 1) { items.TestGrab(carried); phase = 2; }
            else if (phase == 2 && items.TestState(carried).Lease.Owner == plugin.Session!.LocalPeerId)
            {
                Require(gameplay.Player.Get_HoldingItem == items.TestItem(carried), "Granted item was not held natively.");
                items.TestUse(carried); phase = 3; next = Time.unscaledTime + 1;
            }
            else if (phase == 3)
            {
                Require(items.TestState(carried).Exists && gameplay.Player.Get_HoldingItem == items.TestItem(carried), "Carry-only item was consumed or dropped by Use.");
                heldAt = items.TestItem(carried)!.transform.position;
                MoveNear(heldAt + Vector3.right * 2); phase = 4; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 4) { items.TestDrop(false); phase = 5; }
            else if (phase == 5 && items.TestState(carried).Lease.Owner == ItemLease.Unowned)
            {
                var pose = items.TestState(carried).Pose;
                Require((new Vector3(pose.X, pose.Y, pose.Z) - heldAt).sqrMagnitude > 0.5f, "Dropped item pose did not reach the host.");
                MoveNear(items.TestItem(tire)!.transform.position);
                phase = 30; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 30)
            {
                items.TestGrab(tire);
                phase = 20; next = Time.unscaledTime + 1.2f;
            }
            else if (phase == 20 && items.TestState(tire).Lease.Owner == plugin.Session!.LocalPeerId)
            {
                var replica = items.TestItem(tire);
                Require(replica != null && gameplay.Player.Get_HoldingItem == replica && replica.GetComponent<WoodOnFire>() == null && items.TestState(tire).Exists,
                    "Guest-held flammable replica burned before the lease was released.");
                items.TestDrop(false);
                phase = 21; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 21 && items.TestState(tire).Lease.Owner == ItemLease.Unowned && !items.TestState(tire).Exists &&
                (items.TestState(tire).Values.Flags & ItemFlags.Burned) != 0)
            {
                GameManager.Get_WeedJointAmount = 0;
                MoveNear(items.TestItem(packA)!.transform.position); phase = 7; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 7) { items.TestUse(packA); phase = 8; }
            else if (phase == 8 && !items.TestState(packA).Exists && items.TestAcknowledged(packA))
            {
                Require(GameManager.Get_WeedJointAmount == 10, "Approved pack did not apply its native effect once.");
                MoveNear(items.TestItem(packB)!.transform.position); phase = 9; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 9) { items.TestUse(packB); phase = 10; next = Time.unscaledTime + 2; }
            else if (phase == 10)
            {
                Require(items.TestState(packB).Exists && items.TestItem(packB) != null && GameManager.Get_WeedJointAmount == 10,
                    "Natively refused consumption was not restored.");
                phase = 11;
            }
            else if (phase == 11 && !items.TestState(tire).Exists)
            {
                Require((items.TestState(tire).Values.Flags & ItemFlags.Burned) != 0, "Host removal did not retain the native burn mark.");
                Require(items.TestItem(tire) == null, "Host-burned item replica was not removed.");
                MoveNear(items.TestItem(can)!.transform.position); phase = 12; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 12) { items.TestGrab(can); phase = 13; }
            else if (phase == 13 && items.TestState(can).Lease.Owner == plugin.Session!.LocalPeerId)
            {
                items.TestUse(can); phase = 14;
            }
            else if (phase == 14 && !items.TestState(can).Exists && items.TestNewestPrefab(emptyCanPrefab) > oldEmptyCan &&
                items.TestState(items.TestNewestPrefab(emptyCanPrefab)).Lease.Owner == plugin.Session!.LocalPeerId)
            {
                Require(GameManager.Get_PlayerStats[1] == guestSyrupDrinks + 1, "Syrup can did not apply exactly one personal drink.");
                Require(gameplay.Player.Get_HoldingItem == items.TestItem(items.TestNewestPrefab(emptyCanPrefab)), "Empty can did not return to the guest's hand.");
                items.TestDrop(false); phase = 15; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 15)
            {
                MoveNear(items.TestItem(huge)!.transform.position); phase = 16; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 16)
            {
                items.TestUse(huge); phase = 17;
            }
            else if (phase == 17 && !items.TestState(huge).Exists && items.TestNewestPrefab(bigPrefab) > oldBigPack &&
                items.TestItem(items.TestNewestPrefab(bigPrefab)) != null)
            {
                bigPackToUse = items.TestNewestPrefab(bigPrefab);
                oldPackTop = items.TestNewestPrefab(packPrefab);
                MoveNear(items.TestItem(bigPackToUse)!.transform.position);
                phase = 18; next = Time.unscaledTime + 1;
            }
            else if (phase == 18)
            {
                Require(items.TestState(bigPackToUse).Exists && items.TestItem(bigPackToUse) != null,
                    "Shared big pre-roll pack disappeared before the guest could use it.");
                items.TestUse(bigPackToUse); phase = 19;
            }
            else if (phase == 19 && !items.TestState(bigPackToUse).Exists &&
                items.LiveItems.Count(p => p.Id > oldPackTop && p.Item is Item_WeedPreRollPack) >= 2)
            {
                Require(items.LiveItems.Count(p => p.Id > oldPackTop && p.Item is Item_WeedPreRollPack) >= 2,
                    "Guest use of the shared big pre-roll pack did not produce at least two shared pre-roll packs.");
                plugin.EndPairedTestSession();
                Require(items.TestRestored, "Guest item replicas were not removed on disconnect.");
                return true;
            }
            return false;
        }
        private void MoveNear(Vector3 p)
        {
            var location = p + Vector3.back * 0.8f;
            gameplay.PlayerWalking.transform.position = location; gameplay.Get_Player_Rigidbody.position = location; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
