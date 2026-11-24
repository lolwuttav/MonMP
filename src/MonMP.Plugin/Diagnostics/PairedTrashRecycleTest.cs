using System;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UniStorm;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // The native bin confirms a deposit now, but removes eligible items only during its
    // Wednesday sweep. This fixture is intentionally opt-in and requires paired game copies.
    internal sealed class PairedTrashRecycleTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedTrashRecycle recycle;
        private readonly ulong bin;
        private readonly ushort cupPrefab, boltPrefab;
        private readonly Guid far = Guid.NewGuid(), ineligible = Guid.NewGuid(), missing = Guid.NewGuid();
        private ulong cup, bolt;
        private int phase;
        private float deadline, next;
        internal string Status => "trash/recycle phase=" + phase + "; cup=" + cup.ToString("X") +
            "; bolt=" + bolt.ToString("X") + "; result=" + recycle.TestLastReply?.Status;

        internal static void Prepare() => UniStormSystem.Instance.TimeFlow = UniStormSystem.EnableFeature.Disabled;

        internal PairedTrashRecycleTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay; recycle = plugin.World!.TrashRecycle;
            bin = recycle.TestRecycleBin;
            Require(bin != 0, "no native recycle-only bin is registered.");
            cupPrefab = plugin.Items!.TestTypePrefab(typeof(Item_TomHoustonCup));
            boltPrefab = plugin.Items.TestPrefab("Hardware/Hardware_Bolt12");
        }

        internal bool Tick()
        {
            if (deadline == 0) deadline = Time.unscaledTime + 120;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Trash/recycle test stalled: " + Status);
            if (Time.unscaledTime < next || plugin.World?.Channel.FullySynchronized != true) return false;
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            var items = plugin.Items!;
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                var center = recycle.TestCenter(bin);
                items.TestSpawn(center, cupPrefab);
                items.TestSpawn(center + Vector3.right * 0.1f, boltPrefab);
                Physics.SyncTransforms();
                phase = 1; next = Time.unscaledTime + 1;
            }
            else if (phase == 1)
            {
                cup = items.TestNewestPrefab(cupPrefab); bolt = items.TestNewestPrefab(boltPrefab);
                if (cup == 0 || bolt == 0 || items.TestItem(cup) == null || items.TestItem(bolt) == null) return false;
                Require(items.Loose(cup) && items.Loose(bolt) &&
                    recycle.TestContains(bin, items.TestItem(cup)!) && recycle.TestContains(bin, items.TestItem(bolt)!),
                    "the host did not discover both loose items inside the native bin.");
                phase = 2;
            }
            else if (phase == 2 && recycle.TestRangeRejections >= 1 && recycle.TestEligibilityRejections >= 1 &&
                recycle.TestAccepted == 1 && recycle.TestDuplicateReplays >= 1)
            {
                Require(items.TestState(cup).Exists && items.TestState(bolt).Exists,
                    "deposit confirmation removed an item before Wednesday.");
                recycle.TestWednesday(bin);
                Require(!recycle.TransactionsSettled && items.TestState(cup).Exists,
                    "the weekly sweep settled before the shared tombstone was published.");
                phase = 3;
            }
            else if (phase == 3 && !items.TestState(cup).Exists && recycle.TransactionsSettled)
            {
                Require(items.TestState(bolt).Exists && items.TestItem(bolt) != null && recycle.TestSwept == 1,
                    "the native sweep removed an ineligible item or recycled more than once.");
                phase = 4;
            }
            else if (phase == 4 && plugin.Session!.Peers.Count == 1)
            {
                Require(recycle.TestAccepted == 1 && recycle.TestDuplicateReplays >= 1 &&
                    !items.TestState(cup).Exists && items.TestState(bolt).Exists,
                    "a replay or guest disconnect changed the host sweep result.");
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            var items = plugin.Items!;
            if (phase == 0)
            {
                cup = items.TestNewestPrefab(cupPrefab); bolt = items.TestNewestPrefab(boltPrefab);
                if (cup == 0 || bolt == 0 || items.TestItem(cup) == null || items.TestItem(bolt) == null) return false;
                Move(recycle.TestNative(bin).transform.position + Vector3.right * 30);
                phase = 1; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 1)
            {
                recycle.TestRequest(bin, far, cup);
                phase = 2;
            }
            else if (phase == 2 && recycle.TestLastReply?.Operation == far &&
                recycle.TestLastReply?.Status == TrashRecycleStatus.TooFar)
            {
                Require(items.TestState(cup).Exists, "distant request removed the recyclable item.");
                Move(recycle.TestNative(bin).transform.position + Vector3.up);
                phase = 3; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 3)
            {
                recycle.TestRequest(bin, ineligible, bolt);
                phase = 4;
            }
            else if (phase == 4 && recycle.TestLastReply?.Operation == ineligible &&
                recycle.TestLastReply?.Status == TrashRecycleStatus.Ineligible)
            {
                // Exercise the kinematic guest replica's drop-to-confirm path. The host
                // still decides from its own lease, scene overlap and recent walking pose.
                recycle.RecordDrop(items.TestItem(cup)!);
                phase = 5;
            }
            else if (phase == 5 && recycle.TestLastReply?.Status == TrashRecycleStatus.Accepted)
            {
                Guid operation = recycle.TestLastSentOperation;
                Require(operation != Guid.Empty && items.TestState(cup).Exists,
                    "deposit feedback failed or removed the item before the weekly sweep.");
                recycle.TestRequest(bin, operation, cup);
                phase = 6;
            }
            else if (phase == 6 && !items.TestState(cup).Exists)
            {
                Require(items.TestState(bolt).Exists && items.TestItem(cup) == null,
                    "the host sweep did not replicate only the recyclable tombstone.");
                recycle.TestRequest(bin, missing, cup);
                phase = 7;
            }
            else if (phase == 7 && recycle.TestLastReply?.Operation == missing &&
                recycle.TestLastReply?.Status == TrashRecycleStatus.MissingItem)
            {
                plugin.EndPairedTestSession();
                Require(plugin.World == null, "guest session did not close after the sweep.");
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
        { if (!condition) throw new InvalidOperationException("Trash/recycle test: " + reason); }
    }
}
