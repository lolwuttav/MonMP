using System;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Exercises the authoritative native collision attach, rejects a stale tray lease, proves a
    // guest collision cannot mutate locally, then exercises leased attach/remove and body restore.
    internal sealed class PairedMagneticTrayTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly Vector3 position;
        private ulong tray, collisionBolt, requestedBolt;
        private ushort trayPrefab, boltPrefab;
        private int phase;
        private float deadline, next;
        internal string Status => "magnetic tray phase=" + phase + "; tray=" + tray.ToString("X") +
            "; collision bolt=" + collisionBolt.ToString("X") + "; request bolt=" + requestedBolt.ToString("X");

        internal PairedMagneticTrayTest(MonMPPlugin plugin, Gameplay gameplay, Vector3 position)
        { this.plugin = plugin; this.gameplay = gameplay; this.position = position; }

        internal bool Tick()
        {
            var items = plugin.Items!;
            if (!items.Ready) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 45;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Paired magnetic tray test stalled: " + Status);
            return plugin.TestOptions.IsHost ? Host(items) : Guest(items);
        }

        private bool Host(SharedItems items)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                trayPrefab = items.TestTypePrefab(typeof(Tool_MagneticTray));
                boltPrefab = items.TestPrefab(items.TestHardwareBoltPath);
                tray = items.TestSpawnMagneticTray(position + Vector3.up * 0.25f);
                string boltPath = items.TestHardwareBoltPath;
                collisionBolt = items.TestSpawnBolt(position + Vector3.up * 0.25f, boltPath, 1);
                requestedBolt = items.TestSpawnBolt(position + Vector3.up * 0.25f + Vector3.right * 2.5f, boltPath, 2);
                items.TestAllowTrayCollisionNow(collisionBolt);
                items.TestAllowTrayCollisionNow(requestedBolt);
                items.TestTriggerTrayCollision(tray, collisionBolt);
                phase = 1;
            }
            else if (phase == 1 && Attached(items, tray, collisionBolt))
            {
                uint guest = plugin.Session!.Peers.Keys.Single(id => id != plugin.Session.LocalPeerId);
                var trayState = items.TestState(tray);
                if (trayState.Lease.Owner != guest) return false;
                var boltState = items.TestState(requestedBolt);
                items.TestTrayCommandAs(guest, tray, new MagneticTrayCommand(MagneticTrayAction.AttachBolt,
                    trayState.Lease.Generation + 1, requestedBolt, boltState.Lease.Generation));
                Require(!Attached(items, tray, requestedBolt), "A stale tray lease attached a bolt.");
                phase = 2;
            }
            else if (phase == 2 && Attached(items, tray, requestedBolt))
            {
                Require(Attached(items, tray, collisionBolt), "The host collision bolt disappeared during the guest attach.");
                phase = 3;
            }
            else if (phase == 3 && items.TestState(collisionBolt).ContainerEntity == 0 && items.TestState(requestedBolt).ContainerEntity == 0)
            {
                Loose(items, collisionBolt, requestedBolt);
                if (items.TestState(tray).Lease.Owner != ItemLease.Unowned) return false;
                items.TestDestroyItem(tray); items.TestDestroyItem(collisionBolt); items.TestDestroyItem(requestedBolt);
                phase = 4;
            }
            else if (phase == 4 && !items.TestState(tray).Exists && !items.TestState(collisionBolt).Exists && !items.TestState(requestedBolt).Exists)
                return true;
            return false;
        }

        private bool Guest(SharedItems items)
        {
            if (phase == 0)
            {
                trayPrefab = items.TestTypePrefab(typeof(Tool_MagneticTray));
                boltPrefab = items.TestPrefab(items.TestHardwareBoltPath);
                tray = items.TestNewestPrefab(trayPrefab);
                if (tray == 0 || items.TestItem(tray) is not Tool_MagneticTray magneticTray) return false;
                collisionBolt = items.TestBoltInTray(tray);
                if (collisionBolt == 0) return false;
                requestedBolt = items.TestNewestBoltColor(2);
                if (requestedBolt == 0 || items.TestState(requestedBolt).Prefab != boltPrefab || items.TestItem(requestedBolt) is not Hardware_Bolt freeBolt) return false;
                MoveNear(magneticTray.transform.position);
                items.TestAllowTrayCollisionNow(requestedBolt);
                items.TestTriggerTrayCollision(tray, requestedBolt);
                Require(freeBolt.isBoltInMagneticTray == null && freeBolt.transform.parent == null &&
                    items.TestState(requestedBolt).ContainerEntity == 0,
                    "Guest collision mutated its tray or bolt before the tray lease.");
                items.TestUse(tray);
                phase = 1;
            }
            else if (phase == 1 && items.TestState(tray).Lease.Owner == plugin.Session!.LocalPeerId)
            {
                Require(gameplay.Player.Get_HoldingItem == null && gameplay.Player.Get_ActiveTool == items.TestItem(tray),
                    "The guest did not equip the tray after its lease grant.");
                next = Time.unscaledTime + 0.6f;
                phase = 2;
            }
            else if (phase == 2 && Time.unscaledTime >= next)
            {
                Require(items.TestState(requestedBolt).ContainerEntity == 0, "A stale request changed the guest bolt before the valid attach.");
                bool allowed = items.TestRequestTrayAttach(tray, requestedBolt);
                Require(!allowed && items.TestState(requestedBolt).ContainerEntity == 0,
                    "Guest tray attach was not deferred to the host.");
                phase = 3;
            }
            else if (phase == 3 && Attached(items, tray, requestedBolt) && Attached(items, tray, collisionBolt))
            {
                bool allowed = items.TestRequestTrayRemoveAll(tray);
                Require(!allowed, "Guest removal ran the local native mutation instead of requesting the host.");
                phase = 4;
            }
            else if (phase == 4 && items.TestState(collisionBolt).ContainerEntity == 0 && items.TestState(requestedBolt).ContainerEntity == 0)
            {
                Loose(items, collisionBolt, requestedBolt);
                items.TestUse(tray);
                Require(gameplay.Player.Get_ActiveTool == null && gameplay.Player.Get_HoldingItem == items.TestItem(tray),
                    "Use did not return the equipped tray to the player's hand.");
                items.TestDrop(false);
                phase = 5;
            }
            else if (phase == 5 && items.TestState(tray).Lease.Owner == ItemLease.Unowned && gameplay.Player.Get_HoldingItem == null)
                phase = 6;
            else if (phase == 6 && !items.TestState(tray).Exists && !items.TestState(collisionBolt).Exists && !items.TestState(requestedBolt).Exists)
                return true;
            return false;
        }

        private static bool Attached(SharedItems items, ulong trayId, ulong boltId)
        {
            if (items.TestState(boltId).ContainerEntity != trayId || items.TestItem(trayId) is not Tool_MagneticTray tray ||
                items.TestItem(boltId) is not Hardware_Bolt bolt) return false;
            byte slot = items.TestState(boltId).ContainerSlot;
            return slot < MagneticTrayNative.Capacity && MagneticTrayNative.SlotOf(tray, bolt) == slot &&
                bolt.isBoltInMagneticTray == tray && bolt.transform.parent == tray.transform && bolt.rb == null && bolt.itemCollider.isTrigger;
        }

        private static void Loose(SharedItems items, ulong first, ulong second)
        {
            foreach (ulong id in new[] { first, second })
            {
                var bolt = items.TestItem(id) as Hardware_Bolt;
                Require(items.TestState(id).ContainerEntity == 0 && bolt != null && bolt.isBoltInMagneticTray == null &&
                    bolt.transform.parent == null && bolt.rb != null && !bolt.itemCollider.isTrigger,
                    "Tray removal did not restore a loose bolt body.");
            }
        }

        private void MoveNear(Vector3 target)
        {
            var location = target + new Vector3(0, -0.8f, 1.2f);
            gameplay.PlayerWalking.transform.position = location;
            gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        private static void Require(bool ok, string reason)
        { if (!ok) throw new InvalidOperationException("Paired magnetic tray test: " + reason); }
    }
}
