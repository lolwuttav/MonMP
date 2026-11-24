using System;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Exercises both shared-pack entry points after the guest's bolt has been released:
    // wrench extraction fails closed until it owns the pack, while hand Use acquires first,
    // replays natively, and emits one generation-checked host decrement.
    internal sealed class PairedPacksTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly Vector3 position;
        private readonly WorldChannel channel;
        private ulong pack, output, priorFish, guestOutput;
        private uint guestPeer;
        private byte guestStage, hostStage;
        private int phase, failedGrantBaseline;
        private float deadline, next, nextSignal;
        internal bool Completed { get; private set; }
        internal string Status => "packs phase=" + phase + "; pack=" + pack.ToString("X") + "; output=" + output.ToString("X") +
            "; stages=" + guestStage + "/" + hostStage + "; priorFish=" + priorFish.ToString("X") +
            "; pickup=" + (pack != 0 && plugin.World != null && ReferenceEquals(plugin.World.Channel, channel) && plugin.World.Items.Ready
                ? plugin.World.Items.TestPickupStatus(pack) + ",count=" + plugin.World.Items.TestPackCount(pack) : "pending");
        internal PairedPacksTest(MonMPPlugin plugin, Gameplay gameplay, Vector3 position)
        {
            this.plugin = plugin; this.gameplay = gameplay; this.position = position; channel = plugin.World!.Channel;
            if (plugin.TestOptions.IsHost) channel.CommandRequested += GuestSignal;
            else channel.ReplyReceived += HostSignal;
        }

        // Use a pre-existing control entity. A reply naming a newly spawned pack can
        // precede its item record, which correctly fails the channel's packet validation.
        private bool DecodeSignal(ulong entity, byte[] payload, out byte stage, out ulong item)
        {
            stage = 0; item = 0;
            if (entity != CottagePurchaseState.Entity || payload.Length != 19 || payload[0] != 0x77 || payload[1] != 0x50 ||
                BitConverter.ToUInt64(payload, 3) != pack) return false;
            stage = payload[2]; item = BitConverter.ToUInt64(payload, 11); return true;
        }
        private void GuestSignal(uint peer, ulong entity, byte[] payload)
        {
            if (peer != guestPeer || !DecodeSignal(entity, payload, out byte stage, out ulong item)) return;
            guestStage = Math.Max(guestStage, stage);
            if (stage == 5) guestOutput = item;
        }
        private void HostSignal(ulong entity, byte[] payload)
        {
            if (DecodeSignal(entity, payload, out byte stage, out _)) hostStage = Math.Max(hostStage, stage);
        }
        private void Signal()
        {
            byte stage = plugin.TestOptions.IsHost ? hostStage : guestStage;
            if (stage == 0 || Time.unscaledTime < nextSignal) return;
            var payload = new byte[19]; payload[0] = 0x77; payload[1] = 0x50; payload[2] = stage;
            Array.Copy(BitConverter.GetBytes(pack), 0, payload, 3, 8);
            Array.Copy(BitConverter.GetBytes(output), 0, payload, 11, 8);
            if (plugin.TestOptions.IsHost) channel.Reply(guestPeer, CottagePurchaseState.Entity, payload);
            else channel.Request(CottagePurchaseState.Entity, payload, true);
            nextSignal = Time.unscaledTime + 0.3f;
        }
        private void Unsubscribe()
        { channel.CommandRequested -= GuestSignal; channel.ReplyReceived -= HostSignal; }

        internal bool Tick()
        {
            if (Completed) return true;
            var world = plugin.World;
            if (plugin.Session?.IsRunning != true || world == null || !ReferenceEquals(world.Channel, channel))
            { Unsubscribe(); throw new InvalidOperationException("Packs fixture session ended or was replaced before terminal proof: " + Status); }
            if (plugin.TestOptions.IsHost && phase != 0 && phase != 3 && !plugin.Session.Peers.ContainsKey(guestPeer))
            { Unsubscribe(); throw new InvalidOperationException("Packs guest disconnected before host assertions completed: " + Status); }
            var items = world.Items;
            if (!items.Ready) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 45;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Paired packs test stalled: " + Status);
            if (Time.unscaledTime < next) return false;
            bool result = plugin.TestOptions.IsHost ? Host(items) : Guest(items);
            if (!Completed) Signal();
            return result;
        }

        private bool Host(SharedItems items)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                guestPeer = plugin.Session.Peers.Keys.Single(id => id != plugin.Session.LocalPeerId);
                pack = items.TestSpawnPack(position + Vector3.up * 0.25f);
                Require(pack != 0 && items.TestPackCount(pack) == 2, "The isolated two-bolt pack was not created.");
                phase = 10;
            }
            else if (phase == 10 && guestStage >= 1)
            {
                uint generation = items.TestState(pack).Lease.Generation;
                Require(items.TestState(pack).Lease.Owner == ItemLease.Unowned, "The discovered pack was already leased before free-pack rejection probes.");
                items.TestCommandAs(guestPeer, pack, ItemAction.Unpacked, generation);
                Require(items.TestPackCount(pack) == 2, "A free pack accepted an Unpacked command without a lease.");
                items.TestCommandAs(guestPeer, pack, ItemAction.Unpacked, generation + 1);
                Require(items.TestPackCount(pack) == 2, "A stale Unpacked command changed the host pack.");
                hostStage = 2; phase = 1;
            }
            else if (phase == 1 && guestStage >= 3)
            {
                var state = items.TestState(pack);
                if (state.Lease.Owner != guestPeer) return false;
                Require(items.TestPackCount(pack) == 2, "The stable native pack hold consumed stock before host non-owner validation.");
                MoveNear(items.TestItem(pack)!.transform.position);
                items.TestCommandAs(0, pack, ItemAction.Unpacked, state.Lease.Generation);
                Require(items.TestPackCount(pack) == 2, "A non-owner changed a guest-held pack; count=" + items.TestPackCount(pack) +
                    ", owner=" + state.Lease.Owner + ", generation=" + state.Lease.Generation + ".");
                hostStage = 4; phase = 2;
            }
            else if (phase == 2 && guestStage >= 5)
            {
                var state = items.TestState(pack);
                if (items.TestPackCount(pack) != 1 || state.Lease.Owner != ItemLease.Unowned) return false;
                Require(guestOutput != 0 && items.TestItem(guestOutput) is Hardware_Bolt &&
                    items.TestState(guestOutput).Exists && items.TestState(guestOutput).Lease.Owner == ItemLease.Unowned,
                    "The pack's exact adopted output was not released before host terminal validation.");
                items.TestCommandAs(0, pack, ItemAction.Unpacked, state.Lease.Generation - 1);
                Require(items.TestPackCount(pack) == 1, "A stale post-release Unpacked command changed the pack twice.");
                hostStage = 6; phase = 3;
            }
            else if (phase == 3 && plugin.Session!.Peers.Count == 1)
            { Completed = true; Unsubscribe(); return true; }
            return false;
        }

        private bool Guest(SharedItems items)
        {
            if (phase == 0)
            {
                pack = items.TestNewestPack();
                if (pack == 0 || items.TestItem(pack) == null || items.TestState(pack).Lease.Owner != ItemLease.Unowned) return false;
                // Seeing this host-created pack proves the preceding host fish assertions
                // completed. Release that fixture's adopted catch before starting a new hold.
                if (!ReleasePriorFish(items)) return false;
                MoveNear(items.TestItem(pack)!.transform.position);
                // Let the normal pose stream establish the guest's range before requesting a lease.
                phase = 10; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 10)
            {
                guestStage = 1;
                if (hostStage < 2) return false;
                float originalCount = items.TestPackCount(pack);
                var unauthorized = items.TestGetOnePackedItem(pack);
                Require(unauthorized == null && items.TestPackCount(pack) == originalCount,
                    "Wrench extraction changed or spawned from an unleased guest pack.");
                Require(gameplay.Player.Get_HoldingItem == null, "Guest still held the prior test bolt before using the pack.");
                items.TestGrab(pack); phase = 11;
            }
            else if (phase == 11)
            {
                if (items.TestState(pack).Lease.Owner != plugin.Session!.LocalPeerId || gameplay.Player.Get_HoldingItem != items.TestItem(pack)) return false;
                Require(items.TestPackCount(pack) == 2, "A native pack pickup consumed stock before Use.");
                guestStage = 3; phase = 12;
            }
            else if (phase == 12 && hostStage >= 4)
            {
                items.TestDrop(false); phase = 13;
            }
            else if (phase == 13 && items.TestState(pack).Lease.Owner == ItemLease.Unowned)
            {
                float originalCount = items.TestPackCount(pack);
                Require(gameplay.Player.Get_HoldingItem == null, "The stable native pack hold did not release before the failed-grab race.");
                items.TestUse(pack);
                Require(items.TestPendingUse(pack) && items.TestState(pack).Lease.Owner == ItemLease.Unowned &&
                    items.TestPackCount(pack) == originalCount && gameplay.Player.Get_HoldingItem == null,
                    "Native hand-use ran before the host granted the pack lease.");
                failedGrantBaseline = items.TestFailedPackGrants;
                Require(items.TestSetOccupiedHand(), "Could not stage the guest's existing save item for the failed-grab race.");
                phase = 1;
            }
            else if (phase == 1)
            {
                if (items.TestFailedPackGrants == failedGrantBaseline) return false;
                Require(!items.TestPendingUse(pack), "A failed pack grab retained PendingUse.");
                Require(items.TestPackCount(pack) == 2, "A failed pack grab changed the pack count to " + items.TestPackCount(pack) + ".");
                items.TestClearOccupiedHand();
                if (items.TestState(pack).Lease.Owner != ItemLease.Unowned) return false;
                Require(gameplay.Player.Get_HoldingItem == null, "The failed-grab fixture left a save item in the guest's hand.");
                phase = 2;
            }
            else if (phase == 2)
            {
                if (items.TestState(pack).Lease.Owner != ItemLease.Unowned) return false;
                // Retry after the failed grant. A lingering PendingUse would cause this hold to
                // consume an unexpected second bolt when the pack's normal use is replayed.
                items.TestUse(pack);
                Require(items.TestPendingUse(pack) && items.TestPackCount(pack) == 2 && gameplay.Player.Get_HoldingItem == null,
                    "The clean retry changed the pack before its lease arrived.");
                phase = 3;
            }
            else if (phase == 3)
            {
                if (items.TestPackCount(pack) != 1 || items.TestPendingUse(pack)) return false;
                output = items.TestHeldEntity;
                if (output == 0) return false;
                Require(items.TestItem(output) is Hardware_Bolt && items.TestState(output).Lease.Owner == plugin.Session!.LocalPeerId,
                    "The pack's natively spawned bolt was not adopted under the guest lease.");
                items.TestDrop(false);
                phase = 4;
            }
            else if (phase == 4 && items.TestState(output).Lease.Owner == ItemLease.Unowned &&
                items.TestState(pack).Lease.Owner == ItemLease.Unowned)
            {
                Require(items.TestPackCount(pack) == 1 && items.TestItem(output) is Hardware_Bolt,
                    "Released pack/output lost the one-count native consumption result.");
                guestStage = 5; phase = 5;
            }
            else if (phase == 5 && hostStage >= 6)
            {
                Require(items.TestPackCount(pack) == 1 && items.TestState(pack).Lease.Owner == ItemLease.Unowned &&
                    items.TestState(output).Exists && items.TestState(output).Lease.Owner == ItemLease.Unowned && gameplay.Player.Get_HoldingItem == null,
                    "The guest pack/output changed after host terminal acknowledgement.");
                Unsubscribe();
                plugin.EndPairedTestSession();
                Require(items.TestRestored && gameplay.Player.Get_HoldingItem == null,
                    "Disconnect left the adopted pack item or a held-item input state behind.");
                Completed = true;
                return true;
            }
            return false;
        }

        private bool ReleasePriorFish(SharedItems items)
        {
            uint peer = plugin.Session!.LocalPeerId;
            if (priorFish == 0)
            {
                var held = items.HeldItemOf(peer);
                if (held == null) return true;
                Require(held is Item_Fish, "An unexpected leased item remained before the pack fixture.");
                priorFish = items.EntityOf(held);
                Require(priorFish != 0, "The preceding fish fixture's native catch has no shared identity.");
            }
            if (items.TestState(priorFish).Lease.Owner == ItemLease.Unowned)
            {
                Require(!items.TestLocalHeld(priorFish), "The preceding fish catch retained a native hold after release.");
                return true;
            }
            var fish = items.TestItem(priorFish);
            Require(fish is Item_Fish, "The preceding fish fixture's exact catch disappeared before release.");
            if (gameplay.Player.Get_HoldingItem == fish) items.TestDrop(false);
            else if (items.TestLocalHeld(priorFish))
            {
                Require(!gameplay.Player.IsHoldingAnObject, "An unexpected native hand/tool displaced the preceding camera-held catch.");
                ((Item_Fish)fish!).DropFromFishingRod();
            }
            next = Time.unscaledTime + 0.3f;
            return false;
        }

        private void MoveNear(Vector3 target)
        {
            var location = target + new Vector3(0, -0.8f, 1.2f);
            gameplay.PlayerWalking.transform.position = location;
            gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private static void Require(bool ok, string reason)
        { if (!ok) throw new InvalidOperationException("Paired packs test: " + reason); }
    }
}
