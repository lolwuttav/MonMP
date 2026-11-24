using System;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using MonMP.Plugin.Testing;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Garden: the guest digs a dirt patch on the home land (predicted, then adopted from the host's
    // new record), plants potatoes, waters and fertilizes it natively; the host's patch follows. The
    // Real leased fertilizer/basket resources are checked. An owned replica is recreated
    // while infested; its post-Start visibility must survive an unchanged host record.
    internal sealed class PairedGardenTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly PairedGardenResources resources;
        private ulong patch;
        private int phase, baseline;
        private float next, deadline;
        private Vector3 point;
        // The guest plants from a held potato pack; the host takes the seed from its copy (#195).
        private ulong seedsId;
        private Tool_FertilizerPack? fertilizer;
        private Tool_HarvestBasket? basket;
        private ulong fertilizerId, basketId;
        private Garden_DirtPatch? infestationReplica;
        private uint infestationRevision;
        private int replicaFrame, harvested, ack;
        private bool holdingDigPose, originalKinematic;
        private string evidence = "MONMP_PAIRED_GARDEN_RUNNING\n";
        internal string Status
        {
            get
            {
                var g = plugin.World?.Garden;
                string record = g != null && patch != 0 && g.TestExists(patch) ? FormatState(g.TestState(patch)) : "-";
                var local = g != null && patch != 0 ? g.TestPatch(patch) : null;
                return "garden phase=" + phase + "; ack=" + ack + (g == null ? "" : "; reconciled=" + g.Reconciled + " alive=" + g.TestAlive + " adopted=" + g.Adopted + " pending=" + g.TestPending) +
                    "; patch=" + patch.ToString("X") + " record=" + record + " local=" + (local == null ? "-" : local.Get_SeedsType + "/" + local.Get_GrowPercent.ToString("0.00") + "/w" + local.Get_WateredTick);
            }
        }
        private static string FormatState(GardenState s) => "seed" + s.Seed + "/" + s.Grow.ToString("0.00") + "/w" + s.WateredTick + "/" + s.Flags + "/x" + s.Amount;
        internal PairedGardenTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            if (!plugin.TestOptions.Enabled || !LocalTestIsolation.Installed)
                throw new InvalidOperationException("Garden fixture requires an isolated local test instance.");
            this.plugin = plugin; this.gameplay = gameplay;
            resources = new PairedGardenResources(plugin, 1, fertilizerRequired: true);
            if (plugin.TestOptions.IsHost) plugin.World!.Channel.CommandRequested += Acknowledge;
        }
        // Same harmless one-byte stage signal used by the cottage fixture. The native
        // purchase adapter ignores it; the stable lease avoids advancing clock revisions.
        private void Acknowledge(uint peer, ulong entity, byte[] payload)
        {
            if (peer != 0 && entity == CottagePurchaseState.Entity && payload.Length == 1 && payload[0] == 73) ack = 1;
        }
        private void Record(SharedGarden garden, string line)
        { evidence += line + "\n"; garden.TestWriteStartEvidence(evidence); }
        internal bool Tick()
        {
            try
            {
                if (holdingDigPose) MoveNear(point);
                if (deadline == 0) deadline = Time.unscaledTime + 150;
                if (Time.unscaledTime > deadline) throw new InvalidOperationException("Garden test stalled: " + Status);
                if (Time.unscaledTime < next) return false;
                var garden = plugin.World?.Garden ?? throw new InvalidOperationException("Garden fixture session ended before its scenario completed: " + Status);
                resources.Tick();
                return plugin.TestOptions.IsHost ? Host(garden) : Guest(garden);
            }
            catch { ReleaseDigPose(); throw; }
        }
        private static bool Grown(GardenState s) => s.Seed == (byte)SeedsType.Potato && s.Watered && s.Has(GardenFlags.Fertilized);
        private bool Host(SharedGarden garden)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                baseline = garden.TestAlive; phase = 1;
            }
            else if (phase == 1 && garden.TestAlive > baseline)
            {
                patch = garden.TestNewest;
                Require(garden.TestPatch(patch) != null, "The guest's dig did not spawn a host patch.");
                resources.Prepare(garden.TestPatch(patch)!.transform.position);
                phase = 2;
            }
            else if (phase == 2 && garden.TestExists(patch) && Grown(garden.TestState(patch)))
            {
                var p = garden.TestPatch(patch)!;
                Require(p.Get_SeedsType == SeedsType.Potato && p.Get_isFertilized && p.Get_WateredTick != -1, "The host patch does not match its record.");
                uint guest = plugin.Session!.Peers.Keys.Single(id => id != 0);
                Require(plugin.Items!.HeldItemOf(guest) is Tool_FertilizerPack tool && Mathf.Approximately(tool.Value1, 0.95f),
                    "The host did not debit exactly one dose from the guest's held fertilizer pack.");
                garden.TestStageInfestation(patch); infestationRevision = garden.TestRevision(patch);
                Record(garden, "HOST staged infestation grow=0.45; revision=" + infestationRevision + "; held fertilizer debit=1.00->0.95; owned patch native listeners paused.");
                phase = 20;
            }
            else if (phase == 20)
            {
                Require(garden.TestRevision(patch) == infestationRevision && garden.TestState(patch).Has(GardenFlags.Infested),
                    "The host garden record changed before the guest's native Start assertion.");
                if (ack == 0) return false;
                Record(garden, "HOST guest acknowledged post-Start visibility at unchanged revision=" + infestationRevision + ".");
                garden.TestGrow(patch); phase = 3;
            }
            else if (phase == 3 && garden.TestExists(patch) && garden.TestState(patch).Seed == 0)
            {
                Require(garden.TestPatch(patch)!.Get_SeedsType == SeedsType.Useless, "The host patch was not harvested.");
                uint guest = plugin.Session!.Peers.Keys.Single(id => id != 0);
                Require(plugin.Items!.HeldItemOf(guest) is Tool_HarvestBasket held && held.Value1 > 0 && held.IntValue1 == (int)SeedsType.Potato,
                    "The host did not fill the guest's held basket with the accepted potato harvest.");
                Record(garden, "HOST accepted harvest and filled the leased potato basket.");
                phase = 4;
            }
            else if (phase == 4 && !garden.TestExists(patch) && garden.TestPatch(patch) == null) phase = 5;
            else if (phase == 5 && plugin.Session!.Peers.Count == 1)
            {
                plugin.World!.Channel.CommandRequested -= Acknowledge;
                resources.Dispose();
                Record(garden, "MONMP_PAIRED_GARDEN_PASS host resources, stable infestation revision, harvest and removal."); return true;
            }
            return false;
        }
        private bool Guest(SharedGarden garden)
        {
            if (phase == 0)
            {
                if (plugin.World?.PersonalProfileReady != true || !plugin.World.Channel.FullySynchronized ||
                    plugin.World.ReconnectPositions?.Settled != true) return false;
                if (!garden.Reconciled) return false;
                Require(garden.TestDigPoint(out point), "No free home-land ground: " + garden.TestDigSearch);
                originalKinematic = gameplay.Get_Player_Rigidbody.isKinematic;
                gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                gameplay.Get_Player_Rigidbody.isKinematic = true; holdingDigPose = true;
                MoveNear(point);
                phase = 1; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 1)
            {
                Record(garden, "GUEST one dig after 1.5s normal pose publication; point=" + point + "; walker=" + gameplay.PlayerWalking.transform.position + "; seed-only staging.");
                garden.TestDig(point, 90);
                Require(garden.TestPending, "The guest's dig was not mirrored to the host.");
                phase = 2;
            }
            else if (phase == 2 && !garden.TestPending && (patch = garden.TestNewest) != 0 && garden.TestPatch(patch) != null && SeedsHeld())
            {
                Require(garden.Adopted == 1 && (garden.TestPatch(patch)!.transform.position - point).magnitude < 0.05f, "The guest's predicted patch was not adopted.");
                ReleaseDigPose();
                garden.TestPatch(patch)!.Plant(SeedsType.Potato);
                Require(garden.TestPatch(patch)!.Get_SeedsType == SeedsType.Potato, "The guest's planting was not predicted locally.");
                phase = 3; next = Time.unscaledTime + 0.3f;
            }
            else if (phase == 3 && garden.TestState(patch).Seed == (byte)SeedsType.Potato &&
                Mathf.Approximately(plugin.Items!.TestState(seedsId).Values.Value1, 1))
            { garden.TestPatch(patch)!.Water(); plugin.Items!.TestPutAwayTool(); phase = 4; next = Time.unscaledTime + 0.3f; }
            else if (phase == 4 && Released(seedsId) && FertilizerHeld())
            {
                Require(Mathf.Approximately(plugin.Items!.TestState(fertilizerId).Values.Value1, 1), "The leased fertilizer fixture did not start full.");
                Require(garden.TestPatch(patch)!.Fertilize(), "The guest could not fertilize its planted patch.");
                phase = 5;
            }
            else if (phase == 5 && garden.TestExists(patch) && garden.TestState(patch).Has(GardenFlags.Infested) && !garden.TestPredictionPending(patch))
            {
                var pack = plugin.Items!.TestState(seedsId); var fed = plugin.Items.TestState(fertilizerId);
                Require(pack.Exists && Mathf.Approximately(pack.Values.Value1, 1), "The host did not take the planted seed from the guest's pack: " + pack.Values.Value1);
                Require(fed.Exists && Mathf.Approximately(fed.Values.Value1, 0.95f) && garden.TestState(patch).Has(GardenFlags.Fertilized),
                    "The host did not apply the held fertilizer's exact 0.05 debit.");
                infestationRevision = garden.TestRevision(patch); infestationReplica = garden.TestRecreateGroundReplica(patch); replicaFrame = Time.frameCount;
                Require(!garden.TestNativeStarted(patch) && garden.TestInfestationVisible(patch), "The replica did not apply authoritative infestation before native Start.");
                Record(garden, "GUEST pre-Start infestation active=True; nativeStarted=False; revision=" + infestationRevision + "; frame=" + replicaFrame + "; seed debit=2->1; fertilizer debit=1.00->0.95.");
                phase = 20;
            }
            else if (phase == 20 && Time.frameCount >= replicaFrame + 3 && garden.TestNativeStarted(patch))
            {
                Require(garden.TestPatch(patch) == infestationReplica && garden.TestRevision(patch) == infestationRevision && garden.TestInfestationVisible(patch),
                    "Native Start hid infestation or a later host record was needed to repair the recreated replica.");
                Record(garden, "GUEST post-Start infestation active=True; nativeStarted=True; unchanged revision=" + infestationRevision + "; frame=" + Time.frameCount + "; no subsequent host garden record.");
                plugin.World!.Channel.Request(CottagePurchaseState.Entity, new byte[] { 73 }, true);
                plugin.Items!.TestPutAwayTool(); phase = 21;
            }
            else if (phase == 21 && garden.TestExists(patch) && garden.TestState(patch).Has(GardenFlags.Ready) && garden.TestState(patch).Amount > 0 && garden.TestReady(patch) && Released(fertilizerId) && BasketHeld())
            {
                var p = garden.TestPatch(patch)!;
                Require(p.Get_SeedsType == SeedsType.Potato && p.Get_isFertilized && p.Get_AmountToHarvest == garden.TestState(patch).Amount, "The guest's ready plant does not match the host record.");
                Require(plugin.Items!.TestState(basketId).Values.Value1 == 0, "The leased harvest basket did not start empty.");
                harvested = p.Get_AmountToHarvest;
                Require(p.Harvest() && p.Get_SeedsType == SeedsType.Useless, "The guest's harvest did not run locally.");
                phase = 6;
            }
            else if (phase == 6 && garden.TestExists(patch) && garden.TestState(patch).Seed == 0)
            {
                var contents = plugin.Items!.TestState(basketId);
                if (!Mathf.Approximately(contents.Values.Value1, Mathf.Min(harvested, basket!.Value1_Max)) || contents.Values.IntValue1 != (int)SeedsType.Potato) return false;
                Require(contents.Exists && contents.Lease.Owner == plugin.Session!.LocalPeerId, "The accepted harvest lost its held basket identity/lease.");
                Record(garden, "GUEST authoritative potato basket filled=" + contents.Values.Value1 + "; accepted harvest=" + harvested + ".");
                phase = 7; next = Time.unscaledTime + 3;
            }
            else if (phase == 7)
            {
                // The native shovel's right click on an empty ground patch.
                var p = garden.TestPatch(patch)!;
                Require(p.Get_SeedsType == SeedsType.Useless, "The guest's harvested patch still shows a plant.");
                p.RemoveWithAShovel(); UnityEngine.Object.Destroy(p.gameObject);
                phase = 8;
            }
            else if (phase == 8 && !garden.TestExists(patch))
            {
                Record(garden, "MONMP_PAIRED_GARDEN_PASS guest leased resources, native Start infestation, harvest and removal.");
                resources.Dispose();
                plugin.EndPairedTestSession();
                return true;
            }
            return false;
        }
        private bool SeedsHeld()
        {
            seedsId = resources.Seeds;
            return resources.EquipSeeds();
        }
        private bool FertilizerHeld()
        {
            fertilizerId = resources.Fertilizer; fertilizer = resources.Item<Tool_FertilizerPack>(fertilizerId);
            return fertilizer != null && resources.Hold(fertilizerId);
        }
        private bool BasketHeld()
        {
            basketId = resources.Basket; basket = resources.Item<Tool_HarvestBasket>(basketId);
            return basket != null && resources.Hold(basketId);
        }
        private bool Released(ulong id) => id != 0 && plugin.Items!.TestState(id).Lease.Owner != plugin.Session!.LocalPeerId;
        private void MoveNear(Vector3 target)
        {
            var at = target + new Vector3(1.2f, 1, 0);
            gameplay.PlayerWalking.transform.position = at; gameplay.Get_Player_Rigidbody.position = at;
            if (!gameplay.Get_Player_Rigidbody.isKinematic) gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private void ReleaseDigPose()
        {
            if (!holdingDigPose) return;
            gameplay.Get_Player_Rigidbody.isKinematic = originalKinematic; holdingDigPose = false;
        }
        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException("Garden test: " + reason); }
    }
}
