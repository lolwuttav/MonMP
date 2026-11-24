using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Growth: the guest prunes a tree's cuttings with the native loppers call, plants a cutting on the
    // home land from a real leased pile and fertilizes the sapling with a real leased pack.
    // The host matures it; the guest sees the sapling tombstone and the grown tree's new record.
    internal sealed class PairedForestGrowthTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private ulong tree, sapling, treeBaseline, saplingBaseline, loppersId, prunedTree, cuttingId, fertilizerId;
        private Tree_Type cuttingType;
        private TreeBase[] initialReadyTrees = Array.Empty<TreeBase>();
        private Tool_PruningLoppers? loppers;
        private bool equipRequested, sawLateCuttingMarker;
        private readonly Dictionary<Garden_TreeCuttings, int> cuttingSeen = new Dictionary<Garden_TreeCuttings, int>();
        private readonly Dictionary<TreeBase, int> treeSeen = new Dictionary<TreeBase, int>();
        private int phase, cuttingsBaseline;
        private float next, deadline, merged;
        private Vector3 point;
        internal string Status
        {
            get
            {
                var f = plugin.World?.Forest;
                return "growth phase=" + phase + (f == null ? "" : "; reconciled=" + f.Reconciled + " cuttings=" + f.TestCuttingsCount + " adopted=" + f.Adopted + " pending=" + f.TestPlantPending +
                    " saplings=" + f.TestAlive(ForestKind.Sapling) + (sapling != 0 ? " sapling=" + Format(f.TestState(sapling)) : "")) + "; tree=" + tree.ToString("X") + " sapling=" + sapling.ToString("X") +
                    "; loppers=" + loppersId + "; nativeActive=" + (loppers != null && loppers.isEquiped && gameplay.Player.Get_ActiveTool == loppers) + "; lateCuttingMarker=" + sawLateCuttingMarker +
                    "; activeTool=" + (gameplay.Player.Get_ActiveTool != null ? gameplay.Player.Get_ActiveTool.name : "none") +
                    "; hand=" + (gameplay.Player.Get_HoldingItem != null ? gameplay.Player.Get_HoldingItem.name : "none") + "; pickup=" + PickupStatus();
            }
        }
        private static string Format(ForestState s) => (s.Exists ? "alive" : "gone") + "/t" + s.TreeType + "/" + s.Flags + "/k" + s.Ticks;
        internal PairedForestGrowthTest(MonMPPlugin plugin, Gameplay gameplay) { this.plugin = plugin; this.gameplay = gameplay; }
        internal bool Tick()
        {
            if (deadline == 0) deadline = Time.unscaledTime + 150;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Growth test stalled: " + Status);
            if (Time.unscaledTime < next) return false;
            if (plugin.World == null) return false;
            var forest = plugin.World.Forest;
            return plugin.TestOptions.IsHost ? Host(forest) : Guest(forest);
        }
        private bool Host(SharedForest forest)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                cuttingsBaseline = forest.TestCuttingsCount; saplingBaseline = forest.TestNewest(ForestKind.Sapling);
                Require(cuttingsBaseline > 0, "The host forest has no tree with cuttings.");
                initialReadyTrees = UnityEngine.Object.FindObjectsOfType<TreeBase>().Where(t => t.Get_HasCuttings).ToArray();
                loppersId = plugin.World!.Items.TestSpawnPresentationTool(typeof(Tool_PruningLoppers), gameplay.PlayerWalking.transform.position + Vector3.up);
                fertilizerId = plugin.World.Items.TestSpawnPresentationTool(typeof(Tool_FertilizerPack), gameplay.PlayerWalking.transform.position + Vector3.up * 1.5f);
                phase = 1;
            }
            else if (phase == 1 && forest.TestCuttingsCount < cuttingsBaseline) phase = 2;
            else if (phase == 2 && (sapling = forest.TestNewest(ForestKind.Sapling)) > saplingBaseline && forest.TestSapling(sapling) != null) phase = 3;
            else if (phase == 3 && forest.TestState(sapling).Has(ForestFlags.Fertilized))
            {
                Require(forest.TestSapling(sapling)!.Get_IsFertilized, "The host sapling does not match its fertilized record.");
                treeBaseline = forest.TestNewest(ForestKind.Tree);
                var pruned = initialReadyTrees.FirstOrDefault(t => t != null && !t.Get_HasCuttings);
                Require(pruned != null, "Host could not identify the native tree pruned during this fixture.");
                RegrowNative(pruned!, "original pruned tree");
                forest.TestMature(sapling);
                phase = 4;
            }
            else if (phase == 4 && !forest.TestState(sapling).Exists && (tree = forest.TestNewest(ForestKind.Tree)) > treeBaseline)
            {
                // Native maturity creates a tree without cuttings. Advance only that new tree
                // through native days to exercise its late marker readiness as well.
                RegrowNative(NativeTree(forest, tree), "late mature tree");
                phase = 5;
            }
            else if (phase == 5 && plugin.Session!.Peers.Count == 1)
            {
                plugin.World!.Items.TestDestroy(loppersId);
                phase = 6;
            }
            else if (phase == 6 && !plugin.World!.Items.TestState(loppersId).Exists) return true;
            return false;
        }
        private bool Guest(SharedForest forest)
        {
            if (phase == 0)
            {
                if (plugin.World?.PersonalProfileReady != true || !plugin.World.Channel.FullySynchronized ||
                    plugin.World.ReconnectPositions?.Settled != true) return false;
                if (!forest.Reconciled) return false;
                Require(SharedForest.TestPlantPoint(out point), "No free home-land ground for a cutting.");
                tree = forest.TestTreeWithCuttings(point);
                Require(tree != 0 && forest.TestLocalCuttings(tree), "No reconciled tree with cuttings.");
                MoveNear(forest.TestPosition(tree));
                prunedTree = tree;
                phase = 10; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 10)
            {
                var items = plugin.World!.Items;
                loppersId = items.TestNewest(typeof(Tool_PruningLoppers));
                if (loppersId == 0 || !(items.TestItem(loppersId) is Tool_PruningLoppers tool)) return false;
                loppers = tool;
                if (tool.isEquiped && gameplay.Player.Get_ActiveTool == tool)
                {
                    AssertTreeMarker(NativeTree(forest, tree), true, "native equip ready tree");
                    // Picking up the host-spawned loppers moved this guest away from
                    // the selected tree. Return to native pruning range and let that
                    // pose reach the host before sending the authoritative prune.
                    MoveNear(forest.TestPosition(tree));
                    phase = 1; next = Time.unscaledTime + 1.5f;
                }
                else if (gameplay.Player.Get_HoldingItem == tool)
                {
                    if (!equipRequested) { items.TestUse(loppersId); equipRequested = true; }
                }
                else { MoveNear(tool.transform.position); items.TestGrab(loppersId); next = Time.unscaledTime + .7f; }
            }
            else if (phase == 1)
            {
                forest.TestPrune(tree);
                Require(!forest.TestLocalCuttings(tree), "The guest's pruning was not predicted locally.");
                AssertTreeMarker(NativeTree(forest, tree), false, "native prune hides target");
                // The accepted host prune produces its one native random award. Guest factory
                // calls are deliberately suppressed and cannot mint additional stock.
                phase = 2; merged = Time.unscaledTime + 20;
            }
            else if (phase == 2 && merged > 0)
            {
                var late = UnityEngine.Object.FindObjectsOfType<Garden_TreeCuttings>().Where(c =>
                    Vector3.Distance(c.transform.position, gameplay.PlayerWalking.transform.position) < 6).ToArray();
                Require(loppers != null && loppers.isEquiped && gameplay.Player.Get_ActiveTool == loppers, "Native loppers was lost during late cutting spawn.");
                bool started = late.Length > 0;
                foreach (var cutting in late)
                {
                    if (!cuttingSeen.TryGetValue(cutting, out int frame)) { cuttingSeen[cutting] = Time.frameCount; started = false; continue; }
                    if (Time.frameCount < frame + 3) { started = false; continue; }
                    AssertCuttingMarker(cutting, true); // Native Start's postfix owns late marker initialization.
                }
                if (started) sawLateCuttingMarker = true;
                var piles = plugin.World!.Items.TestCuttingPiles((Tree_Type)forest.TestState(prunedTree).TreeType, gameplay.PlayerWalking.transform.position);
                if (piles.Piles == 1 && piles.Total >= 1 && started) merged = 0;
                else Require(Time.unscaledTime < merged, "The guest's cuttings piles did not combine: piles=" + piles.Piles + ", total=" + piles.Total + ".");
            }
            else if (phase == 2 && !forest.TestState(tree).Has(ForestFlags.Cuttings))
            {
                treeBaseline = forest.TestNewest(ForestKind.Tree); saplingBaseline = forest.TestNewest(ForestKind.Sapling);
                var items = plugin.World!.Items;
                cuttingId = items.TestNewest(typeof(Garden_TreeCuttings));
                if (cuttingId == 0 || items.TestItem(cuttingId) is not Garden_TreeCuttings cutting) return false;
                cuttingType = cutting.Get_TreeType;
                items.TestPutAwayTool();
                MoveNear(cutting.transform.position);
                phase = 20; next = Time.unscaledTime + 1f;
            }
            else if (phase == 20)
            {
                var items = plugin.World!.Items;
                if (items.TestItem(cuttingId) is not Garden_TreeCuttings cutting) return false;
                if (!EquipLeasedTool(cuttingId, cutting)) return false;
                MoveNear(point);
                phase = 3; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 3)
            {
                forest.TestPlant(point, 90, cuttingType);
                Require(forest.TestPlantPending, "The guest's planting was not mirrored to the host.");
                phase = 4;
            }
            else if (phase == 4 && !forest.TestPlantPending && (sapling = forest.TestNewest(ForestKind.Sapling)) > saplingBaseline && forest.TestSapling(sapling) != null)
            {
                var local = forest.TestSapling(sapling)!;
                Require((local.transform.position - point).magnitude < 0.05f && forest.TestState(sapling).TreeType == (byte)cuttingType, "The paid host sapling was not replicated.");
                var items = plugin.World!.Items;
                items.TestPutAwayTool();
                fertilizerId = items.TestNewest(typeof(Tool_FertilizerPack));
                Require(fertilizerId != 0, "The host fixture fertilizer pack is missing.");
                MoveNear(items.TestItem(fertilizerId)!.transform.position);
                phase = 22; next = Time.unscaledTime + 1f;
            }
            else if (phase == 22)
            {
                var items = plugin.World!.Items;
                if (items.TestItem(fertilizerId) is not Tool_FertilizerPack fertilizer || !EquipLeasedTool(fertilizerId, fertilizer)) return false;
                MoveNear(point); phase = 23; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 23)
            {
                Require(forest.TestSapling(sapling)!.Fertilize(), "The guest could not fertilize its sapling."); phase = 5;
            }
            else if (phase == 5 && forest.TestState(sapling).Has(ForestFlags.Fertilized))
            {
                plugin.World!.Items.TestPutAwayTool(); MoveNear(loppers!.transform.position); phase = 24; next = Time.unscaledTime + 1f;
            }
            else if (phase == 24)
            {
                Require(loppers != null, "The native loppers disappeared before re-equipping.");
                if (!EquipLeasedTool(loppersId, loppers!)) return false;
                phase = 6;
            }
            else if (phase == 6 && !forest.TestState(sapling).Exists && forest.TestSapling(sapling) == null && (tree = forest.TestNewest(ForestKind.Tree)) > treeBaseline)
            {
                Require((forest.TestPosition(tree) - point).magnitude < 0.5f, "The grown tree record is not at the planted point.");
                if (!forest.TestLocalCuttings(prunedTree)) return false;
                Require(sawLateCuttingMarker, "No late cutting marker was observed while native loppers remained active.");
                AssertTreeMarker(NativeTree(forest, prunedTree), true, "replicated native regrowth");
                var grown = NativeTree(forest, tree);
                if (!grown.Get_HasCuttings) return false;
                if (!treeSeen.TryGetValue(grown, out int frame)) { treeSeen[grown] = Time.frameCount; return false; }
                if (Time.frameCount < frame + 3) return false;
                AssertTreeMarker(grown, true, "late mature tree spawn");
                AccessTools.Method(typeof(PlayerInteractable), "UnEquipTool").Invoke(gameplay.Player, null);
                Require(loppers != null && !loppers.isEquiped && gameplay.Player.Get_ActiveTool != loppers, "Native loppers unequip did not clear the active tool.");
                AssertTreeMarker(NativeTree(forest, prunedTree), false, "unequip regrown tree");
                AssertTreeMarker(grown, false, "unequip late tree");
                foreach (var cutting in UnityEngine.Object.FindObjectsOfType<Garden_TreeCuttings>()) AssertCuttingMarker(cutting, false);
                plugin.World!.Items.TestDrop(false);
                plugin.EndPairedTestSession();
                return true;
            }
            return false;
        }
        private bool EquipLeasedTool(ulong id, InteractableTools tool)
        {
            var items = plugin.World!.Items;
            // Native EquipTool clears HoldingItem after installing activeTool. Check the
            // equipped state first, or a successful equip loops back into pickup forever.
            if (tool.isEquiped && gameplay.Player.Get_ActiveTool == tool)
            {
                Require(items.TryGetLocalToolLease(tool, out var leased, out _) && leased == id,
                    "The equipped native tool lacks its exact local lease: " + PickupStatus());
                Debug.Log("MONMP_GROWTH_NATIVE_TOOL: phase=" + phase + "; item=" + id.ToString("X") + "; tool=" + tool.name + "; " + items.TestPickupStatus(id));
                return true;
            }
            if (gameplay.Player.Get_HoldingItem == tool) items.TestUse(id);
            else if (gameplay.Player.Get_ActiveTool == null && gameplay.Player.Get_HoldingItem == null)
            {
                if (Vector3.Distance(gameplay.PlayerWalking.transform.position, tool.transform.position) > 3.5f) MoveNear(tool.transform.position);
                else if (!items.TestPickupPending(id)) items.TestGrab(id);
            }
            next = Time.unscaledTime + 1f;
            return false;
        }
        private string PickupStatus()
        {
            ulong id = phase == 22 ? fertilizerId : phase == 24 ? loppersId : cuttingId;
            var items = plugin.World?.Items;
            if (id == 0 || items == null || items.ItemOf(id) == null) return id.ToString("X") + "/unbound";
            return id.ToString("X") + "/" + items.TestPickupStatus(id);
        }
        private void MoveNear(Vector3 target)
        {
            var at = target + new Vector3(1.2f, 1, 0);
            gameplay.PlayerWalking.transform.position = at; gameplay.Get_Player_Rigidbody.position = at; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private static TreeBase NativeTree(SharedForest forest, ulong id)
        {
            var position = forest.TestPosition(id);
            var native = UnityEngine.Object.FindObjectsOfType<TreeBase>().OrderBy(t => (t.transform.position - position).sqrMagnitude).FirstOrDefault();
            Require(native != null && Vector3.Distance(native.transform.position, position) < .1f, "Could not resolve exact native tree for marker assertion.");
            return native!;
        }
        private static void RegrowNative(TreeBase tree, string stage)
        {
            var newDay = AccessTools.Method(typeof(TreeBase), "NewDayEvent");
            int days = 0;
            while (!tree.Get_HasCuttings && days++ < 80) newDay.Invoke(tree, new object[] { days });
            Require(tree.Get_HasCuttings, "Native bounded NewDayEvent calls did not regrow cuttings for " + stage);
            Debug.Log("MONMP_LOPPERS_MARKER: host native regrowth; stage=" + stage + "; days=" + days + "; tree=" + tree.name);
        }
        private static void AssertTreeMarker(TreeBase tree, bool visible, string stage)
        {
            var marker = AccessTools.Field(typeof(TreeBase), "CuttingSphere_Ready").GetValue(tree) as GameObject;
            Require(marker != null && marker.activeSelf == visible, stage + " native tree marker differs; tree=" + tree.name + "; expected=" + visible);
            Debug.Log("MONMP_LOPPERS_MARKER: " + stage + "; tree=" + tree.name + "; active=" + marker!.activeSelf);
        }
        private static void AssertCuttingMarker(Garden_TreeCuttings cutting, bool visible)
        {
            var marker = AccessTools.Field(typeof(Garden_TreeCuttings), "CuttingSphere").GetValue(cutting) as GameObject;
            Require(marker != null && marker.activeSelf == visible, "Native late cutting marker differs: " + cutting.name + "; expected=" + visible);
        }
        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException("Growth test: " + reason); }
    }
}
