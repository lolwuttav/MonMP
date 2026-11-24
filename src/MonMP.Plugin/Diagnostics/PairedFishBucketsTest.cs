using System;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Exercises the bucket sidecar over the real paired WorldChannel while keeping every
    // native mutation on the host. Its last phase stands in for a guest's rod catch: a fish appears
    // in the guest's hand at a rolled size and the host must adopt it, held by the guest, at that size.
    internal sealed class PairedFishBucketsTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly ushort bucketPrefab;
        private readonly ushort[] fishPrefabs;
        private readonly int acceptedBefore, rejectedBefore, replayedBefore;
        private Tool_FishBucket? hostBucket;
        private Item_Fish? hostFishA, hostFishB, hostFishC;
        private ulong bucket, fishA, fishB, fishC;
        private int phase;
        private float deadline, next;
        private bool grabRequested;
        private const float CatchScale = 1.234f;
        private Item_Fish? guestCatch;

        internal string Status => "fish buckets phase=" + phase + "; bucket=" + bucket.ToString("X") +
            "; members=" + (bucket != 0 && plugin.World!.FishBuckets.TestBound(bucket)
                ? string.Join(",", plugin.World.FishBuckets.TestState(bucket).Fish.Select(id => id.ToString("X"))) : "pending");

        internal PairedFishBucketsTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            var items = plugin.World!.Items;
            bucketPrefab = items.TestTypePrefab(typeof(Tool_FishBucket));
            fishPrefabs = items.TestPrefabs(typeof(Item_Fish));
            if (fishPrefabs.Length < 2) throw new InvalidOperationException("Fish bucket fixture requires two fish prefab identities.");
            acceptedBefore = plugin.World.FishBuckets.TestAcceptedCommands;
            rejectedBefore = plugin.World.FishBuckets.TestRejectedCommands;
            replayedBefore = plugin.World.FishBuckets.TestReplayedCommands;
        }

        internal bool Tick()
        {
            var world = plugin.World!; var items = world.Items; var buckets = world.FishBuckets;
            if (!items.Ready || !buckets.Ready) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 90;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Fish bucket test stalled: " + Status);
            if (Time.unscaledTime < next) return false;
            return plugin.TestOptions.IsHost ? Host(items, buckets) : Guest(items, buckets);
        }

        private bool Host(SharedItems items, SharedFishBuckets buckets)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                Vector3 origin = gameplay.PlayerWalking.transform.position + new Vector3(3f, 0.6f, 3f);
                hostBucket = (Tool_FishBucket)items.TestSpawn(origin, bucketPrefab);
                // Publish the guest's ready marker only after checking the empty baseline.
                hostBucket.transform.localScale = Vector3.one * 0.600f;
                hostBucket.CreateBasics(); if (hostBucket.rb != null) hostBucket.rb.isKinematic = true;
                // Keep loose fish outside the native bucket trigger until the fixture issues
                // an explicit authoritative add; close spawns can attach through physics.
                hostFishA = SpawnFish(items, fishPrefabs[0], origin + Vector3.right * 2f, 0.731f);
                hostFishB = SpawnFish(items, fishPrefabs[1], origin + Vector3.left * 2f, 0.839f);
                hostFishC = SpawnFish(items, fishPrefabs[0], origin + Vector3.forward * 2.5f, 0.947f);
                phase = 1; next = Time.unscaledTime + 1;
            }
            else if (phase == 1)
            {
                bucket = items.EntityOf(hostBucket!); fishA = items.EntityOf(hostFishA!);
                fishB = items.EntityOf(hostFishB!); fishC = items.EntityOf(hostFishC!);
                if (bucket == 0 || fishA == 0 || fishB == 0 || fishC == 0 || !buckets.TestBound(bucket)) return false;
                Require(items.TestState(fishA).Prefab != items.TestState(fishB).Prefab,
                    "Fixture fish did not retain distinct species prefab identities.");
                Require(buckets.TestState(bucket).Fish.Count == 0, "Fixture bucket did not start empty.");
                hostBucket!.transform.localScale = Vector3.one * 0.641f;
                items.HostPublishItem(bucket);
                phase = 2;
            }
            else if (phase == 2 && buckets.TestBound(bucket))
            {
                var state = buckets.TestState(bucket);
                if (state.Fish.SequenceEqual(new[] { fishB }) && buckets.TestNativeCount(bucket) == 1 &&
                    items.TestState(bucket).Lease.Owner == ItemLease.Unowned)
                {
                    Require(!state.Fish.Contains(fishA) && !state.Fish.Contains(fishC), "Removed or rejected fish stayed in the host bucket.");
                    Require(buckets.TestAcceptedCommands - acceptedBefore >= 3, "Host did not accept add/add/remove exactly through the authoritative adapter.");
                    Require(buckets.TestReplayedCommands - replayedBefore >= 1, "Duplicate fish bucket request did not replay its original result.");
                    Require(buckets.TestRejectedCommands - rejectedBefore >= 1, "Stale fish bucket request was not rejected.");
                    Require(!buckets.BlockFishSale(), "Host fish sales are blocked in multiplayer (#235)."); // The host sells for everyone; guests leave the sale to it.
                    phase = 3;
                }
            }
            else if (phase == 3)
            {
                ulong caught = items.TestTaggedItem(fishPrefabs[0], CatchScale);
                if (caught == 0) return false;
                var state = items.TestState(caught);
                Require(state.Lease.Owner != ItemLease.Unowned && state.Lease.Owner != plugin.Session!.LocalPeerId,
                    "Host adopted the guest's catch without the guest holding it.");
                Require(Math.Abs(items.TestItem(caught)!.Value1 - CatchScale) < 0.001f, "Host copy of the guest's catch lost its size value.");
                phase = 4; return true;
            }
            else if (phase == 4) return true;
            return false;
        }

        private bool Guest(SharedItems items, SharedFishBuckets buckets)
        {
            if (phase == 0)
            {
                bucket = items.TestTaggedItem(bucketPrefab, 0.641f);
                fishA = items.TestTaggedItem(fishPrefabs[0], 0.731f);
                fishB = items.TestTaggedItem(fishPrefabs[1], 0.839f);
                fishC = items.TestTaggedItem(fishPrefabs[0], 0.947f);
                if (bucket == 0 || fishA == 0 || fishB == 0 || fishC == 0 || !buckets.TestBound(bucket)) return false;
                MoveNear(items.TestItem(bucket)!.transform.position);
                phase = 1; next = Time.unscaledTime + 0.25f;
            }
            else if (phase == 1)
            {
                if (items.TestState(bucket).Lease.Owner != plugin.Session!.LocalPeerId)
                {
                    if (!grabRequested) { items.TestGrab(bucket); grabRequested = true; next = Time.unscaledTime + 0.25f; }
                    return false;
                }
                Require(gameplay.Player.Get_HoldingItem == items.TestItem(bucket), "Guest did not receive the bucket lease into its native hand.");
                if (Time.realtimeSinceStartup < 2.1f) return false;
                Require(buckets.TestGuestAdd(bucket, fishA), "Guest Add_Fish mutated its local native bucket before host approval.");
                phase = 2;
            }
            else if (phase == 2)
            {
                var state = buckets.TestState(bucket);
                if (!state.Fish.SequenceEqual(new[] { fishA }) || buckets.TestNativeCount(bucket) != 1) return false;
                var bucketState = items.TestState(bucket); var fishBState = items.TestState(fishB); var fishCState = items.TestState(fishC);
                uint staleGeneration = bucketState.Lease.Generation == 1 ? 2 : bucketState.Lease.Generation - 1;
                const uint duplicateNonce = 0x61f15a;
                buckets.TestRequest(bucket, FishBucketAction.Add, fishB, bucketState.Lease.Generation, fishBState.Lease.Generation, duplicateNonce);
                buckets.TestRequest(bucket, FishBucketAction.Add, fishB, bucketState.Lease.Generation, fishBState.Lease.Generation, duplicateNonce);
                buckets.TestRequest(bucket, FishBucketAction.Add, fishC, staleGeneration, fishCState.Lease.Generation, duplicateNonce + 1);
                phase = 3;
            }
            else if (phase == 3)
            {
                var state = buckets.TestState(bucket);
                if (!state.Fish.SequenceEqual(new[] { fishA, fishB }) || buckets.TestNativeCount(bucket) != 2) return false;
                Require(buckets.TestGuestRemoveFirst(bucket), "Guest MouseRightDown removed a fish before the host authorized the removal.");
                phase = 4;
            }
            else if (phase == 4)
            {
                var nativeFish = items.TestItem(fishA)!;
                if (!buckets.TestState(bucket).Fish.SequenceEqual(new[] { fishB }) || buckets.TestNativeCount(bucket) != 1 ||
                    nativeFish.IntValue1 != 0 || nativeFish.transform.parent != null) return false;
                // Reapply the same full adapter snapshot twice. This is the snapshot-repair/rejoin apply path;
                // a real transport disconnect/reconnect remains an explicit paired-suite follow-up.
                buckets.TestReapplySnapshot(); buckets.TestReapplySnapshot();
                Require(buckets.TestNativeCount(bucket) == 1 && buckets.TestNativeFishIds(bucket).SequenceEqual(new[] { fishB }),
                    "Repeated full snapshots duplicated or reordered native fish contents.");
                items.TestDrop(true);
                phase = 5; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 5)
            {
                var nativeBucket = items.TestItem(bucket) as Tool_FishBucket;
                var nativeFish = items.TestItem(fishB);
                if (nativeBucket == null || nativeFish == null || items.TestState(bucket).Lease.Owner != ItemLease.Unowned) return false;
                Require(nativeFish.transform.parent == nativeBucket.transform, "Throwing the bucket detached its contained fish replica.");
                Require(buckets.TestState(bucket).Fish.SequenceEqual(new[] { fishB }), "Bucket throw changed authoritative membership.");
                Require(buckets.BlockFishSale(), "A guest replica ran its own fish sale.");
                // A rod catch: the native rod instantiates the fish at its tip (under the camera),
                // scales it to the roll and stores the roll in Value1.
                var camera = gameplay.Player.mainCamera.transform;
                guestCatch = (Item_Fish)items.TestSpawn(camera.position + camera.forward, fishPrefabs[0]);
                guestCatch.transform.SetParent(camera, true);
                guestCatch.transform.localScale = Vector3.one * CatchScale; guestCatch.Value1 = CatchScale;
                if (guestCatch.rb != null) guestCatch.rb.isKinematic = true;
                items.LocalSpawned();
                phase = 6;
            }
            else if (phase == 6)
            {
                if (guestCatch == null) throw new InvalidOperationException("Fish bucket test: the guest's catch was destroyed before adoption.");
                ulong caught = items.EntityOf(guestCatch);
                if (caught == 0) return false;
                Require(items.TestState(caught).Lease.Owner == plugin.Session!.LocalPeerId, "Guest's adopted catch was not leased to the guest.");
                phase = 7; return true;
            }
            else if (phase == 7) return true;
            return false;
        }

        private static Item_Fish SpawnFish(SharedItems items, ushort prefab, Vector3 position, float markerScale)
        {
            var fish = (Item_Fish)items.TestSpawn(position, prefab);
            fish.transform.localScale = Vector3.one * markerScale;
            fish.CreateBasics(); if (fish.rb != null) fish.rb.isKinematic = true;
            return fish;
        }
        private void MoveNear(Vector3 target)
        {
            Vector3 position = target + new Vector3(0, -0.8f, 1.2f);
            gameplay.PlayerWalking.transform.position = position;
            if (gameplay.Get_Player_Rigidbody != null)
            { gameplay.Get_Player_Rigidbody.position = position; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Fish bucket test: " + message); }
    }
}
