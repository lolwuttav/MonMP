using System;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Native wrench input, delayed pack ownership, immediate mounting and hand release.
    // Only enabled by an explicit isolated paired-test flag.
    internal sealed class PairedToolPacksTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private ulong pack, toolId, hole, handPack, output;
        private int phase;
        private bool equipRequested;
        private float next, deadline, nextTrace;
        private string trace = "not-started";
        private Vector3 origin, handRelease, thrownAt, lastPackPosition;
        private float quietSince;
        private bool sawThrowTravel;
        private bool stagedPackTarget, priorPlayerEnabled;
        private object? priorTarget, priorTargetItem;
        private static readonly System.Reflection.FieldInfo CurrentTarget = AccessTools.Field(typeof(PlayerInteractable), "currentInteractable");
        private static readonly System.Reflection.FieldInfo CurrentTargetItem = AccessTools.Field(typeof(PlayerInteractable), "currentInteractableItem");
        internal string Status => "tool packs phase=" + phase + "; pack=" + pack.ToString("X") +
            "; handPack=" + handPack.ToString("X") + "; hand=" + HandStatus() + "; hole=" + hole.ToString("X") + "; " + trace;
        private string HandStatus()
        {
            if (handPack == 0 || plugin.Items == null || plugin.Items.TestItem(handPack) == null) return "absent";
            var state = plugin.Items.TestState(handPack);
            return "count=" + plugin.Items.TestPackCount(handPack) + "/lease=" + state.Lease.Owner + "/" + state.Lease.Generation;
        }
        internal PairedToolPacksTest(MonMPPlugin plugin, Gameplay gameplay)
        { this.plugin = plugin; this.gameplay = gameplay; }

        internal bool Tick()
        {
            if (plugin.Session?.IsRunning != true || plugin.World == null || plugin.Items == null)
            {
                RestorePackTarget();
                if (deadline != 0) throw new InvalidOperationException("Tool-pack session ended before completion: " + Status);
                return false;
            }
            var items = plugin.Items!; var bolts = plugin.World!.Bolts;
            if (!items.Ready || !bolts.Ready) return false;
            items.TestToolPackDiagnostics = true;
            if (deadline == 0) deadline = Time.unscaledTime + 90;
            if (Time.unscaledTime > deadline) { RestorePackTarget(); throw new InvalidOperationException("Tool packs stalled: " + Status); }
            if (Time.unscaledTime < next) return false;
            if (hole == 0)
            {
                hole = bolts.TestFirstHoles(bolts.Count).First(id => bolts.TestHole(id).boltSize == BoltSize.Size12);
                origin = bolts.TestHole(hole).HoleLocation.position + Vector3.right * 3f + Vector3.up;
                if (Physics.Raycast(origin + Vector3.up * 5f, Vector3.down, out var ground, 20f, LayerMask.GetMask("Ground")))
                    origin.y = ground.point.y + 1.1f;
                handRelease = origin + Vector3.right * 1.5f;
            }
            if (!plugin.TestOptions.IsHost && pack != 0 && items.TestItem(pack) is InteractablePacks tracePack && Time.unscaledTime >= nextTrace)
            {
                nextTrace = Time.unscaledTime + .5f;
                var active = gameplay.Player.Get_ActiveTool;
                var ownHole = active is Tool_AdjustableSpanner ? AccessTools.Field(typeof(Tool_AdjustableSpanner), "Current_GeneratedBoltHole").GetValue(active) : null;
                trace = "lease=" + items.TestState(pack).Lease.Owner + "/" + items.TestState(pack).Lease.Generation + "; count=" + tracePack.Value1 +
                    "; target=" + gameplay.Player.Get_CurrentInteractable + "; ownHole=" + ownHole + "; grip=" + (active is Tool_AdjustableSpanner wrench ? Held(wrench) : null);
            }
            try { return plugin.TestOptions.IsHost ? Host(items, bolts) : Guest(items, bolts); }
            catch { RestorePackTarget(); throw; }
        }

        private bool Host(SharedItems items, SharedVehicleBolts bolts)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                bolts.TestClear(hole);
                pack = items.TestSpawnPack(origin);
                FreezeFixturePack(items.TestItem(pack)!);
                toolId = items.TestSpawnPresentationTool(typeof(Tool_AdjustableSpanner), origin + Vector3.forward * .3f);
                phase = 1;
            }
            else if (phase == 1 && bolts.TestRecord(hole).Installed && items.TestPackCount(pack) == 1 &&
                items.TestState(pack).Lease.Owner == ItemLease.Unowned)
            {
                Require(bolts.TestNative(hole).Installed, "Fresh output did not mount on the host.");
                handPack = items.TestSpawnPack(origin);
                phase = 2;
            }
            else if (phase == 2 && items.TestPackCount(handPack) == 1 && items.TestState(handPack).Lease.Owner == ItemLease.Unowned)
            {
                var released = items.TestItem(handPack)!.transform.position;
                Require(Mathf.Abs(released.x - handRelease.x) < .45f,
                    "Hand unpack published an old pack position: " + released + " expected x=" + handRelease.x);
                phase = 3;
            }
            else if (phase == 3 && plugin.Session!.Peers.Count == 1)
            { plugin.EndPairedTestSession(); return true; }
            return false;
        }

        private bool Guest(SharedItems items, SharedVehicleBolts bolts)
        {
            if (phase == 0)
            {
                pack = items.TestNewestPack();
                toolId = items.TestPrefabs(typeof(Tool_AdjustableSpanner)).Select(items.TestNewestPrefab).DefaultIfEmpty().Max();
                if (pack == 0 || toolId == 0 || items.TestItem(pack) == null || items.TestItem(toolId) == null || bolts.TestRecord(hole).Installed)
                    return false;
                MoveNear(origin); phase = 1; next = Time.unscaledTime + .8f;
            }
            else if (phase == 1)
            {
                var tool = items.TestItem(toolId) as Tool_AdjustableSpanner;
                Require(tool != null, "Native wrench missing.");
                if (gameplay.Player.Get_ActiveTool != tool || !tool!.isEquiped)
                {
                    if (gameplay.Player.Get_HoldingItem == tool)
                    { if (!equipRequested) { items.TestUse(toolId); equipRequested = true; } }
                    else { items.TestGrab(toolId); next = Time.unscaledTime + .3f; }
                    return false;
                }
                AccessTools.Field(typeof(Tool_AdjustableSpanner), "Current_GeneratedBoltHole").SetValue(tool, null);
                AccessTools.Field(typeof(Tool_AdjustableSpanner), "Current_Bolt").SetValue(tool, null);
                var packItem = items.TestItem(pack)!;
                MoveNear(packItem.itemCollider != null ? packItem.itemCollider.bounds.center : packItem.transform.position);
                StagePackTarget(packItem);
                Require(gameplay.Player.Get_CurrentInteractable == packItem, "The staged native wrench target did not select the fixture pack.");
                // Calls native MouseDown -> CheckForPickingABolt, including the formerly unsafe dereference.
                tool.MouseDown();
                Require(items.TestPackCount(pack) == 2 && Held(tool) == null,
                    "An unleased click extracted a bolt before host approval.");
                Require(!(bool)AccessTools.Field(typeof(Tool_AdjustableSpanner), "isMouseLeftDown").GetValue(tool),
                    "Deferred extraction left the wrench input pressed.");
                phase = 2;
            }
            else if (phase == 2)
            {
                var tool = (Tool_AdjustableSpanner)gameplay.Player.Get_ActiveTool;
                var packItem = items.TestItem(pack)!;
                MoveNear(packItem.itemCollider != null ? packItem.itemCollider.bounds.center : packItem.transform.position);
                StagePackTarget(packItem);
                var fresh = Held(tool);
                if (fresh == null) return false;
                Require(items.TestPackCount(pack) == 1, "One approved extraction changed more than one pack item.");
                RestorePackTarget();
                Debug.Log("MONMP_TOOL_PACK: fresh bolt identity before immediate mount=" + items.TestBoundId(fresh).ToString("X"));
                AccessTools.Field(typeof(Tool_AdjustableSpanner), "CurrentHolding_Bolt").SetValue(tool, null);
                tool.MouseUp();
                MoveNear(bolts.TestHole(hole).HoleLocation.position);
                bolts.TestHole(hole).Attach(fresh, 0);
                phase = 3;
            }
            else if (phase == 3)
            {
                if (!bolts.TestInstallSettled(hole) || !bolts.TestRecord(hole).Installed) return false;
                Require(bolts.TestNative(hole).Installed && bolts.TestRolledBackInstalls == 0,
                    "The fast fresh-bolt install rolled back or lost its canonical hole.");
                items.TestPutAwayTool(); phase = 4;
            }
            else if (phase == 4)
            {
                // Native put-away/drop waits for its host echo. Do not issue the next grab while
                // the wrench is still in the hand, where the ordinary grab gate must reject it.
                if (gameplay.Player.IsHoldingAnObject || gameplay.Player.Get_ActiveTool != null ||
                    items.TestState(toolId).Lease.Owner != ItemLease.Unowned) return false;
                handPack = items.TestNewestPack();
                if (handPack == pack || items.TestItem(handPack) == null || items.TestState(handPack).Lease.Owner != ItemLease.Unowned)
                    return false;
                MoveNear(items.TestItem(handPack)!.transform.position);
                items.TestGrab(handPack); phase = 5;
                // This fixture teleports between the hole and pack. Let the ordinary player
                // presence packets reach the host before testing extraction at the new pose.
                next = Time.unscaledTime + .75f;
            }
            else if (phase == 5)
            {
                if (gameplay.Player.Get_HoldingItem != items.TestItem(handPack)) return false;
                // Move the holder and allow ordinary held-item samples to build a real hand target.
                MoveNear(origin + Vector3.right * 1.5f);
                gameplay.PlayerWalking.playerCameraController.SetRotationAngles(0, 0);
                phase = 8; next = Time.unscaledTime + .8f;
            }
            else if (phase == 8)
            {
                Require(gameplay.Player.Get_HoldingItem == items.TestItem(handPack), "Moved pack left the guest hand before throwing.");
                thrownAt = items.TestItem(handPack)!.transform.position;
                Debug.Log("MONMP_TOOL_PACK: throw requested at=" + thrownAt + "; generation=" + items.TestState(handPack).Lease.Generation);
                items.TestDrop(true);
                phase = 9; next = Time.unscaledTime + .35f;
                quietSince = 0;
            }
            else if (phase == 9)
            {
                if (items.TestState(handPack).Lease.Owner != ItemLease.Unowned || gameplay.Player.IsHoldingAnObject) return false;
                var position = items.TestItem(handPack)!.transform.position;
                float dx = position.x - thrownAt.x, dz = position.z - thrownAt.z;
                if (dx * dx + dz * dz > .25f && (position - thrownAt).sqrMagnitude > .64f) sawThrowTravel = true;
                if ((position - lastPackPosition).sqrMagnitude > .0009f) quietSince = Time.unscaledTime;
                lastPackPosition = position;
                if (!sawThrowTravel || quietSince == 0 || Time.unscaledTime - quietSince < 1f) return false;
                Debug.Log("MONMP_TOOL_PACK: thrown body settled at=" + position + "; travel=" + (position - thrownAt).magnitude);
                MoveNear(position);
                phase = 10; next = Time.unscaledTime + .8f;
            }
            else if (phase == 10)
            {
                handRelease = items.TestItem(handPack)!.transform.position;
                // Use the thrown, resting pack directly. Normal pending hand acquisition must use
                // the host's current body pose before native extraction is replayed.
                Debug.Log("MONMP_TOOL_PACK: hand-before-use count=" + items.TestPackCount(handPack) + "; lease=" + items.TestState(handPack).Lease.Owner + "/" + items.TestState(handPack).Lease.Generation + "; pack=" + items.TestItem(handPack)!.transform.position + "; player=" + gameplay.Player.transform.position);
                items.TestUse(handPack);
                Debug.Log("MONMP_TOOL_PACK: hand-after-use count=" + items.TestPackCount(handPack) + "; holding=" + gameplay.Player.Get_HoldingItem + "; activeTool=" + gameplay.Player.Get_ActiveTool);
                phase = 6;
            }
            else if (phase == 6)
            {
                if (items.TestPackCount(handPack) != 1 || items.TestState(handPack).Lease.Owner != ItemLease.Unowned) return false;
                output = items.TestHeldEntity;
                if (output == 0) return false;
                Require(items.TestItem(output) is Hardware_Bolt && gameplay.Player.Get_HoldingItem != items.TestItem(handPack),
                    "The pack echo re-grabbed the pack instead of retaining its output.");
                Require((items.TestItem(handPack)!.transform.position - handRelease).sqrMagnitude < .64f,
                    "The guest pack jumped back to its prior held position after throw/use.");
                items.TestDrop(false); phase = 7; next = Time.unscaledTime + .6f;
            }
            else if (phase == 7 && items.TestState(output).Lease.Owner == ItemLease.Unowned)
            {
                plugin.EndPairedTestSession();
                Require(items.TestRestored && gameplay.Player.Get_HoldingItem == null && gameplay.Player.Get_ActiveTool == null,
                    "Tool-pack disconnect left equipment or held input behind.");
                return true;
            }
            return false;
        }

        private static Hardware_Bolt? Held(Tool_AdjustableSpanner tool) =>
            AccessTools.Field(typeof(Tool_AdjustableSpanner), "CurrentHolding_Bolt").GetValue(tool) as Hardware_Bolt;
        private void StagePackTarget(InteractableItems packItem)
        {
            if (!stagedPackTarget)
            {
                priorTarget = CurrentTarget.GetValue(gameplay.Player);
                priorTargetItem = CurrentTargetItem.GetValue(gameplay.Player);
                priorPlayerEnabled = gameplay.Player.enabled;
                stagedPackTarget = true;
                // The hidden fixture window cannot promise a stable native camera ray between
                // its click and the next network grant. Stage only that exact input target, as
                // other paired click fixtures do; all tool/pack/lease code still executes natively.
                gameplay.Player.enabled = false;
                Debug.Log("MONMP_TOOL_PACK: staged exact fixture target; native scanner temporarily suspended");
            }
            CurrentTarget.SetValue(gameplay.Player, packItem);
            CurrentTargetItem.SetValue(gameplay.Player, packItem);
        }
        private void RestorePackTarget()
        {
            if (!stagedPackTarget) return;
            stagedPackTarget = false;
            if (gameplay.Player != null)
            {
                CurrentTarget.SetValue(gameplay.Player, priorTarget);
                CurrentTargetItem.SetValue(gameplay.Player, priorTargetItem);
                gameplay.Player.enabled = priorPlayerEnabled;
            }
            priorTarget = null; priorTargetItem = null;
        }
        private void MoveNear(Vector3 target)
        {
            var location = target + new Vector3(0, -.8f, .85f);
            gameplay.PlayerWalking.transform.position = location;
            gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
            var direction = target - gameplay.Player.mainCamera.transform.position;
            if (direction.sqrMagnitude > .001f)
            {
                var angles = Quaternion.LookRotation(direction.normalized).eulerAngles;
                gameplay.PlayerWalking.playerCameraController.SetRotationAngles(Mathf.DeltaAngle(0, angles.x), angles.y);
            }
        }
        private static void Require(bool ok, string reason)
        { if (!ok) throw new InvalidOperationException("Tool packs: " + reason); }
        private static void FreezeFixturePack(InteractableItems item)
        {
            // Only newly spawned fixture packs: stage a stable target away from the vehicle's hole rays.
            if (item.rb != null) item.rb.constraints = RigidbodyConstraints.FreezeAll;
        }
    }
}
