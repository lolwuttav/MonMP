using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedDecorationsTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly List<Furniture_Decoration> spawned = new List<Furniture_Decoration>();
        private int phase, repairs;
        private float next;
        private ulong entity;
        private uint stale;
        private ushort targetPrefab;
        public string Status => "decoration phase " + phase;
        public PairedDecorationsTest(MonMPPlugin plugin, Gameplay gameplay) { this.plugin = plugin; this.gameplay = gameplay; }
        public bool Tick()
        {
            if (Time.unscaledTime < next) return false;
            if (!plugin.TestOptions.IsHost && phase == 11)
            { plugin.BeginPairedTestSession(); phase = 12; return false; }
            var items = plugin.Items;
            if (items?.Ready != true) return false;
            targetPrefab = items.TestDecorationTargetPrefab;
            if (plugin.TestOptions.IsHost) return Host(items);
            if (phase == 0)
            {
                if (items.TestTaggedDecorations.Length != items.TestDecorationPrefabs.Length) return false;
                entity = items.TestNewestPrefab(targetPrefab);
                CheckCatalog(items, false); MoveNear(items.TestItem(entity)!.transform.position + Vector3.right * 30);
                phase = 1; Delay(0.7f);
            }
            else if (phase == 1)
            { stale = items.TestState(entity).Lease.Generation; items.TestUse(entity); phase = 2; Delay(0.4f); }
            else if (phase == 2)
            {
                Require(items.TestState(entity).Values.IntValue1 == 1 && items.TestState(entity).Lease.Generation == stale, "Distant unlock changed decoration.");
                MoveNear(items.TestItem(entity)!.transform.position); phase = 3; Delay(0.7f);
            }
            else if (phase == 3)
            { items.TestGrab(entity); phase = 4; Delay(0.4f); }
            else if (phase == 4)
            {
                Require(items.TestState(entity).Lease.Owner == ItemLease.Unowned && gameplay.Player.Get_HoldingItem == null, "Frozen decoration was picked up.");
                items.TestUse(entity); items.TestUse(entity); phase = 5;
            }
            else if (phase == 5 && items.TestState(entity).Values.IntValue1 == 0 && items.TestItem(entity)!.rb != null)
            {
                Require(items.TestItem(entity)!.rb.isKinematic, "Unlocked guest decoration simulated independently.");
                items.TestCommand(entity, ItemAction.Interact, stale);
                items.TestCommand(entity, ItemAction.Consume, items.TestState(entity).Lease.Generation);
                items.TestGrab(entity); phase = 6;
            }
            else if (phase == 6 && items.TestState(entity).Lease.Owner == plugin.Session!.LocalPeerId)
            {
                CheckHeld(items, true); items.TestUse(entity); phase = 7; Delay(0.7f);
            }
            else if (phase == 7)
            {
                Require(items.TestState(entity).Values.IntValue1 == 0 && items.TestState(entity).Exists, "Held freeze or consume command changed decoration.");
                items.TestDrop(true); phase = 8;
            }
            else if (phase == 8 && items.TestState(entity).Lease.Owner == ItemLease.Unowned)
            { CheckHeld(items, false); phase = 9; Delay(0.6f); }
            else if (phase == 9)
            { MoveNear(items.TestItem(entity)!.transform.position); phase = 10; Delay(0.7f); }
            else if (phase == 10)
            {
                if (items.TestState(entity).Lease.Owner != plugin.Session!.LocalPeerId)
                { items.TestGrab(entity); Delay(0.3f); return false; }
                plugin.EndPairedTestSession();
                Require(items.TestRestored && gameplay.Player.Get_HoldingItem == null, "Disconnect failed to restore decoration originals.");
                phase = 11; Delay(1);
            }
            else if (phase == 12)
            {
                // A resync is only accepted once the reconnect's catch-up has finished, not merely the snapshot.
                if (plugin.World?.Channel.FullySynchronized != true || plugin.World.Channel.RepairPending) return false;
                Require(items.TestState(entity).Values.IntValue1 == 1 && items.TestState(entity).Lease.Owner == ItemLease.Unowned, "Reconnect lost frozen state or ownership release.");
                CheckCatalog(items, false);
                var item = items.TestItem(entity)!; item.IntValue1 = 0; item.CreateBasics();
                Require(item.rb != null, "Repair fixture did not create a stray body.");
                repairs = plugin.World!.Channel.RepairsApplied;
                Require(plugin.World.Channel.RequestResync(SnapshotScope.ForEntity(ItemState.Adapter, entity), "Decoration body repair test"), "Repair request failed.");
                phase = 13;
            }
            else if (phase == 13 && plugin.World!.Channel.RepairsApplied > repairs) { phase = 14; Delay(0.1f); }
            else if (phase == 14)
            {
                Require(items.TestItem(entity)!.IntValue1 == 1 && items.TestItem(entity)!.rb == null, "Snapshot did not repair native freeze/body state.");
                return true;
            }
            return false;
        }
        private bool Host(SharedItems items)
        {
            if (phase == 0)
            {
                var origin = gameplay.PlayerWalking.transform.position;
                int index = 0;
                foreach (ushort prefab in items.TestDecorationPrefabs)
                {
                    var item = (Furniture_Decoration)items.TestSpawn(origin + new Vector3(5 + index % 7 * 2, 1, index / 7 * 2), prefab);
                    item.isSpawnFromTheSaveLoadSystem = true;
                    item.IntValue1 = DecorationState.CanFreeze(item) ? 1 : 0;
                    item.transform.localScale = Vector3.one * (prefab == targetPrefab ? 0.65f : 0.66f);
                    spawned.Add(item); index++;
                }
                Require(spawned.Count == 39, "Unexpected decoration catalog size."); phase = 1; Delay(1.5f);
            }
            else if (phase == 1)
            {
                entity = items.TestNewestPrefab(targetPrefab);
                var item = (Furniture_Decoration)items.TestItem(entity)!;
                Require(spawned.Contains(item) && item.IntValue1 == 1 && item.rb == null, "Initially frozen host prefab was not registered.");
                var point = item.transform.position;
                Require(gameplay.IsOnHomeLand(point), "Test fixture must start on home land.");
                MoveNear(point); items.TestUse(entity);
                Require(item.IntValue1 == 0 && item.rb != null, "Native host unlock did not restore a body.");
                item.transform.position = new Vector3(4000, 2, 4000); MoveNear(item.transform.position);
                item.rb!.velocity = Vector3.zero; items.TestUse(entity);
                Require(item.IntValue1 == 0, "Off-property decoration froze.");
                item.transform.position = point; MoveNear(point); item.rb.velocity = Vector3.right;
                items.TestUse(entity); Require(item.IntValue1 == 0, "Moving decoration froze.");
                item.rb.velocity = Vector3.zero; item.rb.angularVelocity = Vector3.zero; item.rb.Sleep();
                item.transform.localScale = Vector3.one * 0.66f;
                items.TestUse(entity); Require(item.IntValue1 == 1, "Stationary on-property decoration did not freeze.");
                items.TestCommand(entity, ItemAction.Interact, items.TestState(entity).Lease.Generation);
                Require(item.IntValue1 == 1, "Same-frame unlock reused a dying rigidbody.");
                phase = 2;
            }
            else if (phase == 2 && items.TestState(entity).Lease.Owner != ItemLease.Unowned)
            {
                CheckHeld(items, true); uint owner = items.TestState(entity).Lease.Owner;
                items.TestUse(entity); items.TestGrab(entity);
                Require(items.TestState(entity).Lease.Owner == owner && items.TestState(entity).Values.IntValue1 == 0, "Host stole/froze a guest decoration.");
                phase = 3;
            }
            else if (phase == 3 && items.TestState(entity).Lease.Owner == ItemLease.Unowned)
            { CheckHeld(items, false); Require(!items.TestItem(entity)!.rb.isKinematic, "Host did not resume thrown decoration physics."); phase = 4; }
            else if (phase == 4 && items.TestState(entity).Lease.Owner != ItemLease.Unowned) phase = 5;
            else if (phase == 5 && plugin.Session!.Peers.Count == 1)
            {
                Require(items.TestState(entity).Lease.Owner == ItemLease.Unowned, "Disconnect did not release decoration.");
                CheckHeld(items, false);
                var item = items.TestItem(entity)!; MoveNear(item.transform.position);
                item.rb.velocity = Vector3.zero; item.rb.angularVelocity = Vector3.zero; item.rb.Sleep();
                items.TestUse(entity); Require(item.IntValue1 == 1, "Released decoration could not be frozen for reconnect.");
                phase = 6;
            }
            else if (phase == 6 && plugin.Session!.Peers.Count == 2) { CheckCatalog(items, true); return true; }
            return false;
        }
        private void CheckCatalog(SharedItems items, bool host)
        {
            var ids = items.TestTaggedDecorations;
            Require(ids.Length == items.TestDecorationPrefabs.Length && ids.Select(id => items.TestState(id).Prefab).Distinct().Count() == 39, "Decoration factories were lost or duplicated.");
            foreach (ulong id in ids)
            {
                var item = items.TestItem(id)!; bool frozen = items.TestState(id).Values.IntValue1 == 1;
                Require(item != null && item.IntValue1 == (frozen ? 1 : 0) && (frozen ? item.rb == null : item.rb != null), "Decoration native body/state differs from snapshot.");
                if (!frozen && !host) Require(item!.rb!.isKinematic, "Guest decoration has independent physics.");
            }
        }
        private void CheckHeld(SharedItems items, bool held)
        {
            var item = (Furniture_Decoration)items.TestItem(entity)!;
            Require(item.IntValue1 == 0 && item.rb != null && item.itemCollider.isTrigger == held, "Decoration carry body/collider state is wrong.");
            var children = DecorationState.HeldObjects(item);
            Require(children.Length > 0 && children.All(c => c.activeSelf != held), "Decoration child presentation did not follow ownership.");
        }
        private void MoveNear(Vector3 point)
        {
            var location = point + new Vector3(0, -0.8f, 1);
            gameplay.PlayerWalking.transform.position = location;
            gameplay.Get_Player_Rigidbody.position = location; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private void Delay(float seconds) => next = Time.unscaledTime + seconds;
        private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException("Decoration test: " + reason); }
    }
}
