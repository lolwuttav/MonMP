using System;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // The generic item path end to end on one Konig bolt hole: the host drops a loose shared bolt; the
    // guest grabs it and installs it by hand (the loose item is absorbed, the hole replicates), then
    // unscrews it into its own grip (the host adopts the natively spawned bolt, held by the guest) and
    // finally lets go so the host's physics takes over again.
    internal sealed class PairedWorldItemsTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly WorldChannel channel;
        private ulong hole, bolt, adopted;
        private ushort prefab;
        private Hardware_Bolt? local;
        private int phase, pickupAttempts, approaches;
        private float next, deadline;
        private PairedMagneticTrayTest? trayTest;
        private PairedPacksTest? packsTest;
        private PairedFishBucketsTest? fishBucketsTest;
        internal string Status => "world items phase=" + phase + "; bolt=" + bolt.ToString("X") + "; adopted=" + adopted.ToString("X") + "; items=" + plugin.Items?.Count +
            "; pickupAttempts=" + pickupAttempts + "; approaches=" + approaches + "; pickup=" + PickupStatus +
            "; adoptedPickup=" + ItemPickupStatus(adopted) + "; hole=" + HoleStatus +
            (plugin.World == null || !ReferenceEquals(plugin.World.Channel, channel) ? "; session unavailable" :
                (trayTest == null ? "" : "; " + trayTest.Status) +
                (fishBucketsTest == null ? "" : "; " + fishBucketsTest.Status) +
                (packsTest == null ? "" : "; " + packsTest.Status));
        private string PickupStatus => ItemPickupStatus(bolt);
        private string ItemPickupStatus(ulong id) => id != 0 && plugin.World != null && ReferenceEquals(plugin.World.Channel, channel) &&
            channel.Records.ContainsKey(id) ? plugin.World.Items.TestPickupStatus(id) : "pending";
        private string HoleStatus
        {
            get
            {
                var world = plugin.World;
                if (hole == 0 || world == null || !ReferenceEquals(world.Channel, channel)) return "pending";
                var bolts = world.Bolts; var native = bolts.TestNative(hole);
                string record = "pending";
                if (channel.Records.TryGetValue(hole, out var value))
                { var state = BoltHoleState.Decode(value.Payload); record = state.Installed + "/" + state.Percent; }
                var socket = bolts.TestHole(hole).SockedBoltedByThisBolt;
                return hole.ToString("X") + " native=" + native.Installed + "/" + native.Percent + "; record=" + record +
                    "; parent=" + socket.car_PartsType + "/" + socket.isInstalled + "/" + socket.gameobject.activeInHierarchy +
                    "; mountSettled=" + bolts.TestInstallSettled(hole) + "; local=" + (local == null ? "missing" : "placed=" + local.isPlaced);
            }
        }
        internal PairedWorldItemsTest(MonMPPlugin plugin, Gameplay gameplay)
        { this.plugin = plugin; this.gameplay = gameplay; channel = plugin.World!.Channel; }
        internal bool Tick()
        {
            // Only the child's explicit post-assertion completion can survive its deliberate
            // disconnect. Any earlier transport loss still fails the original-session guard.
            if (phase == 8 && packsTest?.Completed == true) return true;
            var world = plugin.World;
            if (plugin.Session?.IsRunning != true || world == null || !ReferenceEquals(world.Channel, channel))
                throw new InvalidOperationException("World items fixture session ended or was replaced before completion: " + Status);
            var items = world.Items; var bolts = world.Bolts;
            if (!items.Ready || !bolts.Ready) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 180;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("World items test stalled: " + Status);
            if (hole == 0)
            {
                hole = bolts.TestFirstHoles(1)[0];
                var size = bolts.TestHole(hole).boltSize;
                prefab = items.TestPrefab(Singleton<GameAssets>.i.Hardware_Bolt(size).GetComponent<InteractableItems>().PrefabName);
            }
            if (Time.unscaledTime < next) return false;
            if (phase == 6)
            {
                trayTest ??= new PairedMagneticTrayTest(plugin, gameplay, HolePosition(bolts));
                if (trayTest.Tick()) { phase = 7; next = Time.unscaledTime + 0.5f; }
                return false;
            }
            if (phase == 7)
            {
                fishBucketsTest ??= new PairedFishBucketsTest(plugin, gameplay);
                if (!fishBucketsTest.Tick()) return false;
                phase = 8; next = Time.unscaledTime + 0.25f;
                return false;
            }
            if (phase == 8)
            { packsTest ??= new PairedPacksTest(plugin, gameplay, HolePosition(bolts)); return packsTest.Tick(); }
            return plugin.TestOptions.IsHost ? Host(items, bolts) : Guest(items, bolts);
        }
        private Vector3 HolePosition(SharedVehicleBolts bolts) => bolts.TestHole(hole).HoleLocation.position;
        private bool Host(SharedItems items, SharedVehicleBolts bolts)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                Require(bolts.TestHole(hole).SockedBoltedByThisBolt.isInstalled, "The fixture bolt hole has no installed native parent part.");
                bolts.TestClear(hole);
                var spawned = items.TestSpawn(HolePosition(bolts) + Vector3.up * 0.3f, prefab);
                if (spawned.rb != null) spawned.rb.isKinematic = true; // stay reachable until shared; release resumes physics
                phase = 1; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 1)
            {
                bolt = items.TestNewestPrefab(prefab);
                Require(bolt != 0 && items.TestState(bolt).Exists, "The host's loose bolt was not shared.");
                phase = 2;
            }
            else if (phase == 2 && bolts.TestNative(hole).Installed && !items.TestState(bolt).Exists)
            {
                Require(items.TestItem(bolt) == null, "The absorbed loose bolt stayed in the host world.");
                Require(bolts.TestNative(hole).Percent == 1, "Native hand insertion did not start the host bolt at one percent.");
                // This item/identity fixture stages the native loosened endpoint; physical
                // wrench progress is covered separately. Removal requires an actual zero.
                bolts.TestTighten(hole, 0);
                phase = 3;
            }
            else if (phase == 3 && !bolts.TestNative(hole).Installed)
            {
                ulong newest = items.TestNewestPrefab(prefab);
                if (newest <= bolt || items.TestState(newest).Lease.Owner == ItemLease.Unowned) return false;
                adopted = newest;
                Require(items.TestItem(adopted) != null && items.TestItem(adopted)!.rb.isKinematic, "Adopted guest-held bolt is not a kinematic host twin.");
                phase = 4;
            }
            else if (phase == 4 && items.TestState(adopted).Lease.Owner == ItemLease.Unowned)
            {
                Require(!items.TestItem(adopted)!.rb.isKinematic, "Released adopted bolt did not resume host physics.");
                phase = 5;
            }
            else if (phase == 5 && items.TestState(adopted).Lease.Owner == ItemLease.Unowned)
                phase = 6;
            return false;
        }
        private bool Guest(SharedItems items, SharedVehicleBolts bolts)
        {
            if (phase == 0)
            {
                if (plugin.World?.PersonalProfileReady != true || !channel.FullySynchronized ||
                    plugin.World.ReconnectPositions?.Settled != true || gameplay.Get_PlayerState != PlayerState.Walking) return false;
                bolt = items.TestNewestPrefab(prefab);
                if (bolt == 0 || !items.TestState(bolt).Exists || bolts.TestNative(hole).Installed || items.TestItem(bolt) == null ||
                    (items.TestItem(bolt)!.transform.position - HolePosition(bolts)).sqrMagnitude > 4) return false;
                MoveNear(HolePosition(bolts));
                phase = 1; next = Time.unscaledTime + 1f;
            }
            else if (phase == 1)
            {
                if (items.TestState(bolt).Lease.Owner != plugin.Session!.LocalPeerId || !items.TestLocalHeld(bolt))
                {
                    var target = items.TestItem(bolt);
                    Require(target != null, "The shared input bolt disappeared before pickup.");
                    if (Vector3.Distance(gameplay.PlayerWalking.transform.position, target!.transform.position) > 3.5f)
                    {
                        Require(approaches++ < 6, "The initial bolt approach did not remain in pickup range: " + PickupStatus);
                        MoveNear(target!.transform.position); next = Time.unscaledTime + 1f; return false;
                    }
                    if (!items.TestPickupPending(bolt))
                    {
                        Require(pickupAttempts++ < 6, "The shared input bolt did not receive a native pickup lease: " + PickupStatus);
                        items.TestGrab(bolt);
                    }
                    next = Time.unscaledTime + 0.4f; return false;
                }
                var held = items.TestItem(bolt) as Hardware_Bolt;
                Require(held != null && gameplay.Player.Get_HoldingItem == held, "Granted shared bolt was not in the guest's hand.");
                bolts.TestMountRequest(hole, new BoltMountCommand(Guid.NewGuid(), BoltMountAction.Install, bolt,
                    items.TestState(bolt).Lease.Generation + 1));
                bolts.TestForgedState(hole);
                phase = 50; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 50)
            {
                Require(!bolts.TestRecord(hole).Installed && !bolts.TestNative(hole).Installed && items.TestState(bolt).Exists &&
                    items.TestState(bolt).Lease.Owner == plugin.Session!.LocalPeerId,
                    "Stale bolt lease or forged whole-hole state bypassed host validation.");
                var held = (Hardware_Bolt)items.TestItem(bolt)!;
                // Exactly what a hand release onto an empty matching hole does natively.
                // Attach must observe the exact native hand before UnGrabInteractable clears it.
                AccessTools.Field(typeof(PlayerInteractable), "Current_GeneratedBoltHole").SetValue(gameplay.Player, bolts.TestHole(hole).generated_BoltHole);
                items.TestDrop(false);
                Require(gameplay.Player.Get_HoldingItem == null && held.isPlaced && bolts.TestNative(hole).Installed,
                    "The native hand release did not install its held bolt.");
                local = held;
                phase = 2;
            }
            else if (phase == 2 && bolts.TestRecord(hole).Installed && !items.TestState(bolt).Exists)
            {
                Require(items.TestItem(bolt) == null && local != null && local.isPlaced && bolts.TestNative(hole).Installed,
                    "Absorb destroyed the guest's installed bolt or left a loose binding.");
                phase = 3; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 3 && bolts.TestInstallSettled(hole) && bolts.TestRecord(hole).Installed && bolts.TestRecord(hole).Percent == 0 &&
                bolts.TestNative(hole).Installed && bolts.TestNative(hole).Percent == 0)
            {
                // A wrench unscrewing leaves the bolt in its mouth, under the player's camera.
                bolts.TestHole(hole).Unscrewed();
                local!.transform.SetParent(gameplay.Player.mainCamera.transform, true);
                phase = 4;
            }
            else if (phase == 4)
            {
                adopted = items.TestBoundId(local!);
                if (adopted == 0 || items.TestState(adopted).Lease.Owner != plugin.Session!.LocalPeerId) return false;
                Require(!items.TestAdopting && items.TestItem(adopted) == local, "Adoption spawned a duplicate instead of binding the guest's own bolt.");
                if (gameplay.Player.Get_HoldingItem == local)
                {
                    // A canonical output grant may put the bolt into the native hand. Look
                    // away from the fixture hole so this release drops rather than reinstalls it.
                    AccessTools.Field(typeof(PlayerInteractable), "Current_GeneratedBoltHole").SetValue(gameplay.Player, null);
                    items.TestDrop(false);
                }
                else
                {
                    Require(!gameplay.Player.IsHoldingAnObject, "Another native item/tool occupied the hand before the removed bolt's release.");
                    // A receipt may instead preserve the staged wrench-mouth camera grip.
                    local!.transform.SetParent(null, true);
                    if (local.rb == null) local.CreateBasics();
                    items.ToolBoltReleased(local, false);
                }
                Require(!items.TestLocalHeld(adopted), "The removed bolt's release retained its native hand/tool grip.");
                phase = 5;
            }
            else if (phase == 5 && items.TestState(adopted).Lease.Owner == ItemLease.Unowned && !bolts.TestRecord(hole).Installed)
            {
                phase = 6;
            }
            return false;
        }
        private void MoveNear(Vector3 target)
        {
            var location = target + new Vector3(0, -0.8f, 1.2f);
            gameplay.PlayerWalking.transform.position = location;
            gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException("World items test: " + reason); }
    }
}
