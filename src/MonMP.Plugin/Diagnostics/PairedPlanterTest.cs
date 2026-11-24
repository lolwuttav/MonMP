using System;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Planter: the host places an outdoor planter on the home land. The guest freezes it, plants and
    // waters its patch natively (mirrored to the host's planter), harvests once the host finishes the
    // growth, and unfreezes it; the planter's garden record follows its shared item id.
    internal sealed class PairedPlanterTest
    {
        private const string Path = "Garden/Garden_OutdoorPlanter";
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly PairedGardenResources resources;
        private ulong item;
        private int phase;
        private float next, deadline;
        private ulong Patch => SharedGarden.PlanterId(item);
        internal string Status
        {
            get
            {
                var world = plugin.World;
                string frozen = world != null && item != 0 ? world.Items.TestState(item).Values.IntValue1.ToString() : "-";
                string record = world != null && item != 0 && world.Garden.TestExists(Patch) ? Format(world.Garden.TestState(Patch)) : "-";
                return "planter phase=" + phase + "; item=" + item.ToString("X") + " frozen=" + frozen + " patch=" + record +
                    (world != null && item != 0 ? " bound=" + world.Garden.TestPlanterBound(item) : "");
            }
        }
        private static string Format(GardenState s) => "seed" + s.Seed + "/" + s.Grow.ToString("0.00") + "/w" + s.WateredTick + "/" + s.Flags + "/x" + s.Amount;
        internal PairedPlanterTest(MonMPPlugin plugin, Gameplay gameplay)
        { this.plugin = plugin; this.gameplay = gameplay; resources = new PairedGardenResources(plugin, 2, fertilizerRequired: false); }
        internal bool Tick()
        {
            var world = plugin.World!;
            if (!world.Items.Ready) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 150;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Planter test stalled: " + Status);
            if (Time.unscaledTime < next) return false;
            resources.Tick();
            return plugin.TestOptions.IsHost ? Host(world.Items, world.Garden) : Guest(world.Items, world.Garden);
        }
        private bool Host(SharedItems items, SharedGarden garden)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                Require(SharedForest.TestPlantPoint(out var point), "No free home-land ground for the planter.");
                items.TestSpawn(point + Vector3.up * 0.3f, items.TestPrefab(Path));
                phase = 1; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 1 && (item = items.TestNewestPrefab(items.TestPrefab(Path))) != 0 && garden.TestPlanterBound(item))
            {
                var native = items.TestItem(item)!;
                if (native.rb != null) { native.rb.velocity = Vector3.zero; native.rb.angularVelocity = Vector3.zero; native.rb.Sleep(); }
                resources.Prepare(native.transform.position);
                phase = 2;
            }
            else if (phase == 2 && items.TestState(item).Values.IntValue1 == 1) phase = 3;
            else if (phase == 3 && garden.TestExists(Patch) && garden.TestState(Patch).Seed == (byte)SeedsType.Potato && garden.TestState(Patch).Watered)
            {
                var p = ((Garden_OutdoorPlanter)items.TestItem(item)!).DirtPatch;
                Require(garden.TestPatch(Patch) == p && p.Get_SeedsType == SeedsType.Potato && p.Get_WateredTick != -1, "The host planter patch does not match its record.");
                garden.TestGrow(Patch); phase = 4;
            }
            else if (phase == 4 && garden.TestState(Patch).Seed == 0) phase = 5;
            else if (phase == 5 && items.TestState(item).Values.IntValue1 == 0) phase = 6;
            else if (phase == 6 && plugin.Session!.Peers.Count == 1) { resources.Dispose(); return true; }
            return false;
        }
        private bool Guest(SharedItems items, SharedGarden garden)
        {
            if (phase == 0)
            {
                if (plugin.World?.PersonalProfileReady != true || !plugin.World.Channel.FullySynchronized ||
                    plugin.World.ReconnectPositions?.Settled != true) return false;
                if ((item = items.TestNewestPrefab(items.TestPrefab(Path))) == 0 || items.TestItem(item) == null || !garden.TestPlanterBound(item)) return false;
                phase = 1; next = Time.unscaledTime + 3; // let the host's planter settle
            }
            else if (phase == 1) { MoveNear(items.TestItem(item)!.transform); phase = 2; next = Time.unscaledTime + 1.5f; }
            else if (phase == 2) { items.TestUse(item); phase = 3; }
            else if (phase == 3 && items.TestState(item).Values.IntValue1 == 1)
            {
                if (!resources.EquipSeeds()) return false;
                var p = ((Garden_OutdoorPlanter)items.TestItem(item)!).DirtPatch;
                Require(garden.TestPatch(Patch) == p, "The guest's planter patch is not bound to its record.");
                p.Plant(SeedsType.Potato);
                Require(p.Get_SeedsType == SeedsType.Potato, "The guest's planting was not predicted locally.");
                phase = 4; next = Time.unscaledTime + 0.3f;
            }
            else if (phase == 4 && garden.TestState(Patch).Seed == (byte)SeedsType.Potato &&
                Mathf.Approximately(items.TestState(resources.Seeds).Values.Value1, 1))
            { garden.TestPatch(Patch)!.Water(); items.TestPutAwayTool(); phase = 5; }
            else if (phase == 5 && garden.TestState(Patch).Has(GardenFlags.Ready) && garden.TestState(Patch).Amount > 0 && garden.TestReady(Patch))
            {
                if (items.TestState(resources.Seeds).Lease.Owner == plugin.Session!.LocalPeerId || !resources.Hold(resources.Basket)) return false;
                var p = garden.TestPatch(Patch)!;
                Require(p.Get_SeedsType == SeedsType.Potato && p.Get_AmountToHarvest == garden.TestState(Patch).Amount, "The guest's ready planter does not match the host record.");
                Require(p.Harvest() && p.Get_SeedsType == SeedsType.Useless, "The guest's harvest did not run locally.");
                phase = 6;
            }
            else if (phase == 6 && garden.TestState(Patch).Seed == 0)
            {
                var basket = items.TestState(resources.Basket);
                Require(basket.Values.Value1 > 0 && basket.Values.IntValue1 == (int)SeedsType.Potato && basket.Lease.Owner == plugin.Session!.LocalPeerId,
                    "Planter harvest did not fill the leased native potato basket.");
                items.TestPutAwayTool(); phase = 7; next = Time.unscaledTime + 3;
            }
            else if (phase == 7 && items.TestState(resources.Basket).Lease.Owner != plugin.Session!.LocalPeerId) { items.TestUse(item); phase = 8; }
            else if (phase == 8 && items.TestState(item).Values.IntValue1 == 0)
            {
                resources.Dispose();
                plugin.EndPairedTestSession();
                return true;
            }
            return false;
        }
        private void MoveNear(Transform target)
        {
            foreach (var direction in new[] { target.right, -target.right, target.forward, -target.forward })
            {
                var at = target.position + direction * 1.8f + Vector3.up;
                if (Physics.OverlapCapsule(at, at + Vector3.up, 0.4f, ~0, QueryTriggerInteraction.Ignore).Any(c => !(c is TerrainCollider))) continue;
                gameplay.PlayerWalking.transform.position = at; gameplay.Get_Player_Rigidbody.position = at; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                return;
            }
            throw new InvalidOperationException("Planter test: no clear standing point beside the planter.");
        }
        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException("Planter test: " + reason); }
    }
}
