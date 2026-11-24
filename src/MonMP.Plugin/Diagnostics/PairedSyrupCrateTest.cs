using System;
using System.Linq;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // A guest takes a full syrup can out of a host crate with the use key, keeps it in hand, drinks
    // it and keeps the empty can: the path players reported as the can vanishing a few seconds later.
    internal sealed class PairedSyrupCrateTest
    {
        private const float CrateMarker = 0.913f;
        private const float HoldSeconds = 6f;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private ulong crate, can, empty;
        private int phase;
        private float deadline, since, next;

        internal string Status => "syrup crate phase=" + phase + "; crate=" + crate.ToString("X") + "; can=" + can.ToString("X") +
            "; empty=" + empty.ToString("X") + "; holding=" + (gameplay.Player.Get_HoldingItem == null ? "none" : gameplay.Player.Get_HoldingItem.GetType().Name);

        internal PairedSyrupCrateTest(MonMPPlugin plugin, Gameplay gameplay) { this.plugin = plugin; this.gameplay = gameplay; }

        internal bool Tick()
        {
            var items = plugin.World!.Items;
            if (!items.Ready) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 90;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Syrup crate test stalled: " + Status);
            if (Time.unscaledTime < next) return false;
            return plugin.TestOptions.IsHost ? Host(items) : Guest(items);
        }

        private bool Host(Replication.SharedItems items)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                crate = items.TestSpawnSyrupCrate(gameplay.PlayerWalking.transform.position + new Vector3(2f, 0.4f, 2f), CrateMarker, 3);
                phase = 1;
            }
            else if (phase == 1)
            {
                // Done once the guest's drink has left an empty can that the host shares, held by the guest.
                ulong left = items.TestNewest(typeof(Item_MapleSyrupCan_Empty));
                if (left == 0) return false;
                var state = items.TestState(left);
                if (state.Lease.Owner == ItemLease.Unowned || state.Lease.Owner == plugin.Session!.LocalPeerId) return false;
                phase = 2; return true;
            }
            else if (phase == 2) return true;
            return false;
        }

        private bool Guest(Replication.SharedItems items)
        {
            var held = gameplay.Player.Get_HoldingItem;
            if (phase == 0)
            {
                crate = items.TestTaggedItem(items.TestTypePrefab(typeof(Pack_MapleSyrupCrate)), CrateMarker);
                if (crate == 0) return false;
                MoveNear(items.TestItem(crate)!.transform.position);
                phase = 1; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 1)
            {
                items.TestUse(crate);
                phase = 2;
            }
            else if (phase == 2)
            {
                // The full can arrives in hand and is adopted by the host with this guest's lease.
                if (!(held is Item_MapleSyrupCan full)) return false;
                can = items.EntityOf(full);
                if (can == 0 || items.TestState(can).Lease.Owner != plugin.Session!.LocalPeerId) return false;
                phase = 3; since = Time.unscaledTime;
            }
            else if (phase == 3)
            {
                var item = items.ItemOf(can);
                Require(item != null && item.gameObject.activeInHierarchy && held == item, "The full can left the guest's hand while held after " +
                    (Time.unscaledTime - since).ToString("F1") + " s: item=" + (item == null ? "gone" : item.gameObject.activeInHierarchy ? "active" : "inactive") +
                    ", state=" + items.TestState(can).Exists + "/" + items.TestState(can).Lease.Owner + "/" + items.TestState(can).Lease.Generation + ", " + Status);
                Require(items.TestState(can).Lease.Owner == plugin.Session!.LocalPeerId, "The guest lost the full can's lease while holding it.");
                if (Time.unscaledTime - since < HoldSeconds) return false;
                items.TestRequestConsume(can);
                phase = 4;
            }
            else if (phase == 4)
            {
                if (!(held is Item_MapleSyrupCan_Empty left)) return false;
                empty = items.EntityOf(left);
                if (empty == 0 || items.TestState(empty).Lease.Owner != plugin.Session!.LocalPeerId) return false;
                phase = 5; since = Time.unscaledTime;
            }
            else if (phase == 5)
            {
                var item = items.ItemOf(empty);
                Require(item != null && item.gameObject.activeInHierarchy && held == item, "The empty can left the guest's hand while held.");
                if (Time.unscaledTime - since < HoldSeconds) return false;
                phase = 6; return true;
            }
            else if (phase == 6) return true;
            return false;
        }

        private void MoveNear(Vector3 target)
        {
            Vector3 position = target + new Vector3(0, 0, -1.3f);
            gameplay.PlayerWalking.transform.position = position;
            if (gameplay.Get_Player_Rigidbody != null)
            { gameplay.Get_Player_Rigidbody.position = position; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Syrup crate test: " + message); }
    }
}
