using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Forest: the guest's scene trees are reconciled to the host's saved forest. This fixture invokes
    // the native TreeBase.Cut and TreeTrunk.CutAppart methods directly to check their host routing;
    // it does not drive Tool_Chainsaw's raycast or two-second hold coroutine.
    internal sealed class PairedForestTest
    {
        private static readonly string[] Logs = { "Wood_Logs/WoodLog_Birch", "Wood_Logs/WoodLog_Maple", "Wood_Logs/WoodLog_Pine" };
        private const float SplitterLogScale = 0.947f;
        private const float SplitterHeldWaitSeconds = 0.75f;
        private static readonly FieldInfo SplitterEngineRunning = AccessTools.Field(typeof(WoodSplitter), "isEngineRunning")!;
        private static readonly FieldInfo SplitterCutting = AccessTools.Field(typeof(WoodSplitter), "isCutting")!;
        private static readonly FieldInfo SplitterKillSwitchOff = AccessTools.Field(typeof(WoodSplitter), "isKillSwitchOff")!;
        private bool switchWanted;
        private static readonly MethodInfo SplitterTriggerEnter = AccessTools.Method(typeof(WoodSplitter), "OnTriggerEnter", new[] { typeof(Collider) })!;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private ulong tree, trunk, logBaseline, carriedLog;
        private int expectedPieces;
        private ulong[] splitLogs = Array.Empty<ulong>();
        private Vector3 pickupPosition;
        private bool hostCut;
        private int handoffBaseline;
        private int phase, felled;
        private float next, deadline;
        private WoodSplitter? splitter;
        private Collider? splitterTrigger;
        private InteractableItems? splitterLog;
        private ulong splitterLogId;
        private ushort splitterLogPrefab;
        private ushort[] firewoodPrefabs = Array.Empty<ushort>();
        private Dictionary<ushort, ulong> firewoodBaselines = new Dictionary<ushort, ulong>();
        private bool originalSplitterEngineRunning;
        private bool splitterEngineStaged;
        private bool splitterTriggerAttempted;
        private bool splitterDropIssued;
        private float splitterTriggerAt;
        private float splitterDropAt;
        private float splitterInputGoneAt = -1f;
        private float splitterOutputsAt = -1f;
        internal string Status
        {
            get
            {
                var f = plugin.World?.Forest;
                return "forest phase=" + phase + (f == null ? "" : "; reconciled=" + f.Reconciled + " alive=" + f.TestAlive(ForestKind.Tree) + " local=" + f.TestLiveLocal(ForestKind.Tree) +
                    " kept=" + f.Kept + " removed=" + f.Removed + " spawned=" + f.Spawned + " missing=" + f.Missing + " felled=" + f.TestRemoved(ForestKind.Tree) + " trunks=" + f.TestAlive(ForestKind.Trunk)) +
                    "; expectedPieces=" + expectedPieces + "; carriedLog=" + carriedLog.ToString("X") + "; hostCut=" + hostCut + "; tree=" + tree.ToString("X") + " trunk=" + trunk.ToString("X") + "; splitterLog=" + splitterLogId.ToString("X") +
                    "; guardAttempted=" + splitterTriggerAttempted + "; dropIssued=" + splitterDropIssued +
                    "; inputGoneAt=" + splitterInputGoneAt.ToString("F3") + "; outputsAt=" + splitterOutputsAt.ToString("F3");
            }
        }
        internal PairedForestTest(MonMPPlugin plugin, Gameplay gameplay) { this.plugin = plugin; this.gameplay = gameplay; }
        private ulong NewestLog(SharedItems items) => Logs.Select(items.TestPrefab).Select(items.TestNewestPrefab).Where(id => id != 0 && items.TestState(id).Exists).DefaultIfEmpty().Max();
        internal bool Tick()
        {
            var world = plugin.World!; var forest = world.Forest; var items = world.Items;
            if (!items.Ready) return false;
            if (deadline == 0) { deadline = Time.unscaledTime + 150; logBaseline = NewestLog(items); }
            if (Time.unscaledTime > deadline)
            {
                RestoreSplitterEngine();
                throw new InvalidOperationException("Forest test stalled: " + Status);
            }
            if (splitterDropIssued && !plugin.TestOptions.IsHost) ObserveSplitterOutput(items);
            if (Time.unscaledTime < next) return false;
            try { return plugin.TestOptions.IsHost ? Host(forest, items) : Guest(forest, items); }
            catch { RestoreSplitterEngine(); throw; }
        }
        private bool Host(SharedForest forest, SharedItems items)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                Require(forest.TestAlive(ForestKind.Tree) > 0, "The host has no shared trees.");
                felled = forest.TestRemoved(ForestKind.Tree); phase = 1;
            }
            else if (phase == 1 && forest.TestRemoved(ForestKind.Tree) > felled) phase = 2;
            else if (phase == 2 && (trunk = forest.TestNewestTrunk) != 0)
            { expectedPieces = forest.TestTrunkPieces(trunk); Require(expectedPieces > 0, "Missing native trunk segments."); phase = 3; }
            else if (phase == 3 && !forest.TestExists(trunk)) phase = 4;
            else if (phase == 4)
            {
                splitLogs = items.TestVisibleLogsAfter(logBaseline);
                if (splitLogs.Length < expectedPieces) return false;
                Require(splitLogs.Length == expectedPieces, "Host produced duplicate logs for one split.");
                phase = 40;
            }
            else if (phase == 40)
            {
                var guest = plugin.Session!.Peers.Keys.FirstOrDefault(id => id != 0);
                carriedLog = splitLogs.FirstOrDefault(id => items.TestState(id).Lease.Owner == guest && guest != 0);
                if (carriedLog == 0) return false;
                pickupPosition = items.ItemOf(carriedLog)!.transform.position;
                phase = 41;
            }
            else if (phase == 41)
            {
                var item = items.ItemOf(carriedLog);
                if (item == null || (item.transform.position - pickupPosition).sqrMagnitude < .25f) return false;
                Require(items.TestState(carriedLog).Lease.Owner != ItemLease.Unowned, "Client moved the split log without a lease.");
                Debug.Log("MonMP forest: host observed client moving a real log from " + (hostCut ? "host" : "guest") + " split; pieces=" + expectedPieces);
                if (hostCut) phase = 5;
                else
                {
                    logBaseline = splitLogs.Max();
                    tree = forest.TestTreeNear(gameplay.PlayerWalking.transform.position);
                    Require(tree != 0, "No second tree for host cut.");
                    forest.TestCut(tree, Vector3.forward);
                    hostCut = true; phase = 42;
                }
            }
            else if (phase == 42 && items.TestState(carriedLog).Lease.Owner == ItemLease.Unowned &&
                forest.TestNewestTrunk != 0 && forest.TestNewestTrunk != trunk)
            {
                trunk = forest.TestNewestTrunk;
                expectedPieces = forest.TestTrunkPieces(trunk);
                Require(expectedPieces > 0, "Host-cut trunk had no segments.");
                forest.TestCut(trunk, Vector3.zero);
                phase = 3;
            }
            else if (phase == 5)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                if (!FindSplitter()) return false;
                if (SplitterCutting.GetValue(splitter) is bool cutting && cutting) return false;
                firewoodPrefabs = items.TestPrefabs(typeof(Item_FireWood)).Distinct().ToArray();
                Require(firewoodPrefabs.Length >= 2, "The shared prefab catalog has fewer than two Item_FireWood half resources.");
                firewoodBaselines = firewoodPrefabs.ToDictionary(prefab => prefab, items.TestNewestPrefab);
                splitterLogPrefab = items.TestPrefab(Logs[0]);
                var spawn = splitterTrigger!.bounds.center + splitter!.transform.right * (splitterTrigger.bounds.extents.magnitude + 5f);
                splitterLog = items.TestSpawn(spawn, splitterLogPrefab);
                splitterLog.transform.localScale = Vector3.one * SplitterLogScale;
                splitterLog.isSpawnFromTheSaveLoadSystem = true;
                splitterLog.CreateBasics();
                originalSplitterEngineRunning = (bool)SplitterEngineRunning.GetValue(splitter);
                SplitterEngineRunning.SetValue(splitter, true);
                splitterEngineStaged = true;
                items.DiscoverSoon();
                phase = 6;
                Debug.Log("MonMP forest: staged held-log splitter fixture outside receiver at " + spawn.ToString("F2") +
                    "; firewood prefab baselines=" + string.Join(",", firewoodBaselines.Select(pair => pair.Key + ":" + pair.Value.ToString("X"))));
            }
            else if (phase == 6)
            {
                if (!FindSplitter()) return false;
                splitterLogId = items.TestTaggedItem(splitterLogPrefab, SplitterLogScale);
                if (splitterLogId == 0 || !items.TestState(splitterLogId).Exists) return false;
                var session = plugin.Session!;
                uint guest = session.Peers.Keys.FirstOrDefault(peer => peer != 0);
                if (guest == 0 || items.TestState(splitterLogId).Lease.Owner != guest) return false;
                splitterLog = items.ItemOf(splitterLogId) as Item_WoodLog;
                Require(splitterLog != null, "The uniquely tagged shared test log is not an Item_WoodLog.");
                splitterLog!.transform.position = splitterTrigger!.bounds.center;
                Physics.SyncTransforms();
                SplitterTriggerEnter.Invoke(splitter, new object[] { splitterLog.itemCollider });
                // #167: like native, the splitter takes a log on contact even while a guest still holds it.
                Require((bool)SplitterCutting.GetValue(splitter), "The host splitter did not take a guest-held log on contact (#167).");
                splitterTriggerAttempted = true;
                splitterTriggerAt = Time.unscaledTime;
                phase = 8;
                Debug.Log("MonMP forest: host native WoodSplitter took the log held by remote owner=" + guest + " on contact.");
            }
            else if (phase == 7)
            {
                var state = items.TestState(splitterLogId);
                if (state.Lease.Owner != ItemLease.Unowned)
                {
                    if (!state.Exists) throw new InvalidOperationException("Held splitter log vanished before the guest drop (#147).");
                    if (Time.unscaledTime - splitterTriggerAt > 10f)
                        throw new InvalidOperationException("The guest did not drop the overlapping splitter log after the host trigger guard check.");
                    return false;
                }
                Require(splitterTriggerAttempted, "The native splitter guard was not checked before guest release.");
                phase = 8;
            }
            else if (phase == 8)
            {
                if (items.TestState(splitterLogId).Exists)
                {
                    if (Time.unscaledTime - splitterTriggerAt > 10f)
                        throw new InvalidOperationException("The released overlapping log was not consumed by the host native splitter (#148).");
                    return false;
                }
                int halves = CountNewFirewood(items);
                if (halves < 2)
                {
                    if (Time.unscaledTime - splitterTriggerAt > 10f)
                        throw new InvalidOperationException("Host native WoodSplitter did not publish both Item_FireWood halves (#148).");
                    return false;
                }
                RestoreSplitterEngine();
                Debug.Log("MonMP forest: host native splitter consumed the dropped log and published " + halves + " new firewood prefab maxima.");
                phase = 9;
            }
            else if (phase == 9 && plugin.Session!.Peers.Count == 1) return true;
            return false;
        }
        private bool Guest(SharedForest forest, SharedItems items)
        {
            if (phase == 0)
            {
                if (!forest.Reconciled) return false;
                Require(forest.TestAlive(ForestKind.Tree) > 0 && forest.TestLiveLocal(ForestKind.Tree) == forest.TestAlive(ForestKind.Tree) && forest.Missing == 0,
                    "The guest forest does not match the host's: " + Status);
                tree = forest.TestTreeNear(gameplay.PlayerWalking.transform.position);
                Require(tree != 0, "No fellable tree.");
                MoveNear(forest.TestPosition(tree));
                phase = 1; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 1)
            {
                forest.TestCut(tree, gameplay.PlayerWalking.transform.forward);
                Require(forest.TestLocal(tree), "A guest cut felled the tree locally instead of asking the host.");
                phase = 2;
            }
            else if (phase == 2 && !forest.TestExists(tree) && !forest.TestLocal(tree) && (trunk = forest.TestNewestTrunk) != 0 && forest.TestLocal(trunk))
            { expectedPieces = forest.TestTrunkPieces(trunk); Require(expectedPieces > 0, "Missing guest trunk segments."); phase = 3; next = Time.unscaledTime + 6; }
            else if (phase == 3) { MoveNear(forest.TestPosition(trunk)); phase = 4; next = Time.unscaledTime + 1.5f; }
            else if (phase == 4)
            {
                forest.TestCut(trunk, Vector3.zero);
                Require(forest.TestLocal(trunk), "A guest split the trunk locally instead of asking the host.");
                phase = 5;
            }
            else if (phase == 5 && !forest.TestExists(trunk) && !forest.TestLocal(trunk))
            {
                Require(forest.TestStandInLogs > 0 || NewestLog(items) > logBaseline, "(#107) The guest showed no logs the moment its trunk was split.");
                Debug.Log("MonMP forest: guest stand-in logs at split=" + forest.TestStandInLogs);
                phase = 6;
            }
            else if (phase == 6 && NewestLog(items) > logBaseline && forest.TestStandInLogs == 0)
            {
                Require(forest.TestStandInTimeouts == 0, "Temporary split pieces expired instead of handing off to real log replicas.");
                // Measure at the actual replacement event. The fixture may not poll again for several frames.
                Require(forest.TestVisibleLogHandoffs - handoffBaseline == expectedPieces,
                    "A temporary segment vanished before a visible log replaced it.");
                handoffBaseline = forest.TestVisibleLogHandoffs;
                Debug.Log("MonMP forest: guest saw all " + expectedPieces + " visible log handoffs for the " + (hostCut ? "host" : "guest") + " split.");
                phase = 70;
            }
            else if (phase == 70)
            {
                splitLogs = items.TestVisibleLogsAfter(logBaseline);
                if (splitLogs.Length < expectedPieces) return false;
                Require(splitLogs.Length == expectedPieces, "Guest received duplicate log replicas.");
                carriedLog = splitLogs[0]; pickupPosition = items.ItemOf(carriedLog)!.transform.position;
                MoveNear(pickupPosition); phase = 71; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 71) { items.TestGrab(carriedLog); phase = 72; }
            else if (phase == 72 && items.TestLocalHeld(carriedLog) && items.TestState(carriedLog).Lease.Owner == plugin.Session!.LocalPeerId)
            {
                // Wait for the host to see the grant before moving; keep the test on native holding/owner motion.
                phase = 73; next = Time.unscaledTime + 1f;
            }
            else if (phase == 73)
            {
                MoveNear(pickupPosition + Vector3.right * 1.8f);
                phase = 74;
            }
            else if (phase == 74 && !hostCut && forest.TestNewestTrunk != 0 && forest.TestNewestTrunk != trunk &&
                forest.TestLocal(forest.TestNewestTrunk))
            {
                trunk = forest.TestNewestTrunk; expectedPieces = forest.TestTrunkPieces(trunk);
                Require(expectedPieces > 0, "Guest did not see the host-cut trunk.");
                items.TestDrop(false); logBaseline = splitLogs.Max(); hostCut = true; phase = 5;
            }
            else if (phase == 74 && hostCut && items.TestTaggedItem(items.TestPrefab(Logs[0]), SplitterLogScale) != 0)
            { items.TestDrop(false); phase = 75; }
            else if (phase == 75 && !items.TestLocalHeld(carriedLog) && items.TestState(carriedLog).Lease.Owner == ItemLease.Unowned)
            { phase = 8; }
            else if (phase == 8)
            {
                if (!FindSplitter()) return false;
                splitterLogPrefab = items.TestPrefab(Logs[0]);
                splitterLogId = items.TestTaggedItem(splitterLogPrefab, SplitterLogScale);
                if (splitterLogId == 0 || !items.TestState(splitterLogId).Exists || items.ItemOf(splitterLogId) is not Item_WoodLog guestLog)
                    return false;
                firewoodPrefabs = items.TestPrefabs(typeof(Item_FireWood)).Distinct().ToArray();
                Require(firewoodPrefabs.Length >= 2, "The shared prefab catalog has fewer than two Item_FireWood half resources.");
                firewoodBaselines = firewoodPrefabs.ToDictionary(prefab => prefab, items.TestNewestPrefab);
                MoveNear(guestLog.transform.position);
                phase = 80;
                next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 80)
            {
                if (items.ItemOf(splitterLogId) is not Item_WoodLog guestLog) return false;
                MoveNear(guestLog.transform.position);
                items.TestGrab(splitterLogId);
                phase = 9;
            }
            else if (phase == 9)
            {
                if (items.TestState(splitterLogId).Lease.Owner != plugin.Session!.LocalPeerId ||
                    gameplay.Player.Get_HoldingItem != items.ItemOf(splitterLogId)) return false;
                MoveSplitterLogToReceiver(items);
                splitterTriggerAt = splitterDropAt = Time.unscaledTime;
                splitterDropIssued = true;
                phase = 10;
                Debug.Log("MonMP forest: guest holds the WoodLog inside the splitter trigger; waiting for the host to take it (#167).");
            }
            else if (phase == 10)
            {
                // #167: the log is taken while still held, without a drop.
                if (items.TestState(splitterLogId).Exists)
                {
                    if (items.ItemOf(splitterLogId) is Item_WoodLog && gameplay.Player.Get_HoldingItem == items.ItemOf(splitterLogId))
                        MoveSplitterLogToReceiver(items);
                    if (Time.unscaledTime - splitterTriggerAt > 10f)
                        throw new InvalidOperationException("The host splitter did not take the guest-held log on contact (#167).");
                    return false;
                }
                if (Time.unscaledTime - splitterTriggerAt < SplitterHeldWaitSeconds) return false;
                var held = gameplay.Player.Get_HoldingItem;
                Require(held == null || !(held is Item_WoodLog), "The guest still holds a copy of the split log (#167).");
                phase = 11;
            }
            else if (phase == 11)
            {
                if (splitterOutputsAt < 0f || splitterInputGoneAt < 0f)
                {
                    if (Time.unscaledTime - splitterDropAt > 10f)
                        throw new InvalidOperationException("The guest did not receive both firewood halves and the split-log removal.");
                    return false;
                }
                int halves = CountNewFirewood(items);
                Require(halves >= 2, "The guest did not receive both Item_FireWood half resources.");
                float gap = splitterOutputsAt <= splitterInputGoneAt ? 0f : splitterOutputsAt - splitterInputGoneAt;
                Debug.Log("MonMP forest: guest input tombstone to both native firewood halves=" + gap.ToString("F3") + "s; " + halves + " half-prefab maxima advanced; drop input at " + splitterDropAt.ToString("F3"));
                Require(gap <= 0.1f, "(#148) Guest split-log removal preceded both firewood halves by " + gap.ToString("F3") + "s.");
                var splitters = plugin.World!.WoodSplitters;
                Require(splitters.Count > 0, "The forest splitter is not bound to a shared trailer (#167).");
                Require(splitters.TestRamReplays > 0, "The guest splitter's ram did not move for the host's cut (#167).");
                // #167: the guest's own kill-switch click reaches the host and comes back.
                MoveNear(splitter!.transform.position);
                phase = 12; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 12 || phase == 14)
            {
                switchWanted = !(bool)SplitterKillSwitchOff.GetValue(splitter);
                if (switchWanted) splitter!.KillSwitchOn(); else splitter!.KillSwitchOff();
                Require((bool)SplitterKillSwitchOff.GetValue(splitter) != switchWanted, "A guest kill-switch click changed its private splitter instead of asking the host (#167).");
                splitterTriggerAt = Time.unscaledTime; phase++;
            }
            else if (phase == 13 || phase == 15)
            {
                if ((bool)SplitterKillSwitchOff.GetValue(splitter) != switchWanted)
                {
                    if (Time.unscaledTime - splitterTriggerAt > 6f) throw new InvalidOperationException("The guest's splitter kill-switch click never reached the host (#167).");
                    return false;
                }
                Require(plugin.World!.WoodSplitters.TestSwitchVisual(splitter!) == switchWanted, "The guest splitter's kill-switch button did not follow the host's state (#167).");
                Debug.Log("MonMP forest: guest kill switch " + (switchWanted ? "on" : "off") + " through the host with its button (#167)");
                if (phase == 13) { phase = 14; next = Time.unscaledTime + 0.5f; return false; }
                plugin.EndPairedTestSession();
                return true;
            }
            return false;
        }
        private void MoveNear(Vector3 target)
        {
            var at = target + new Vector3(1.2f, 1, 0);
            gameplay.PlayerWalking.transform.position = at; gameplay.Get_Player_Rigidbody.position = at; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        private bool FindSplitter()
        {
            if (splitter == null)
                splitter = Resources.FindObjectsOfTypeAll<WoodSplitter>()
                    .Where(candidate => candidate != null && candidate.gameObject.scene.IsValid() &&
                        candidate.enabled && candidate.gameObject.activeInHierarchy && candidate.GetComponent<SOUND_WoodSplitter>() != null)
                    .OrderBy(candidate => SharedBuildingDoors.StablePath(candidate.transform), StringComparer.Ordinal)
                    .FirstOrDefault();
            if (splitter == null) return false;
            if (splitterTrigger == null)
                splitterTrigger = splitter.GetComponentsInChildren<Collider>(true)
                    .Where(collider => collider != null && collider.enabled && collider.isTrigger && collider.gameObject.activeInHierarchy)
                    .OrderByDescending(collider => collider.bounds.size.sqrMagnitude)
                    .FirstOrDefault();
            if (splitterTrigger == null) return false;
            if (SplitterEngineRunning == null || SplitterCutting == null || SplitterTriggerEnter == null)
                throw new InvalidOperationException("The native WoodSplitter trigger/engine layout is unavailable.");
            return true;
        }

        private void MoveSplitterLogToReceiver(SharedItems items)
        {
            if (!FindSplitter()) throw new InvalidOperationException("The native WoodSplitter disappeared during the fixture.");
            var log = items.ItemOf(splitterLogId) as Item_WoodLog;
            if (log == null) throw new InvalidOperationException("The guest WoodLog copy disappeared before release.");
            var center = splitterTrigger!.bounds.center;
            MoveNear(center);
            log.transform.position = center;
            Physics.SyncTransforms();
        }

        private int CountNewFirewood(SharedItems items)
        {
            int count = 0;
            foreach (ushort prefab in firewoodPrefabs)
            {
                if (!firewoodBaselines.TryGetValue(prefab, out ulong baseline)) continue;
                ulong latest = items.TestNewestPrefab(prefab);
                if (latest > baseline && items.TestState(latest).Exists && items.ItemOf(latest) is Item_FireWood) count++;
            }
            return count;
        }

        private void ObserveSplitterOutput(SharedItems items)
        {
            if (splitterInputGoneAt < 0f && !items.TestState(splitterLogId).Exists)
                splitterInputGoneAt = Time.unscaledTime;
            if (splitterOutputsAt < 0f && CountNewFirewood(items) >= 2)
                splitterOutputsAt = Time.unscaledTime;
        }

        private void RestoreSplitterEngine()
        {
            if (!splitterEngineStaged) return;
            if (splitter != null && SplitterEngineRunning != null)
                SplitterEngineRunning.SetValue(splitter, originalSplitterEngineRunning);
            splitterEngineStaged = false;
        }

        internal void Abort() => RestoreSplitterEngine();

        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException("Forest test: " + reason); }
    }
}
