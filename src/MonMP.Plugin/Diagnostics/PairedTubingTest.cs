using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Tubing: the host drops two tubing posts and a spout as shared tool items. The guest carries each
    // one and places it; the host spends the item and builds the post, which reaches the guest. The
    // guest draws a tube between its two posts (predicted, then adopted from the host's record),
    // removes it with the roll's native removal and removes a post (the host drops the tool item back).
    // The guest then taps a maple tree with the spout, which connects the host's tree, and removes it.
    internal sealed class PairedTubingTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private int phase;
        private int unloadedTries;
        private int rollGrabAttempts;
        private bool rollEquipRequested;
        private float rollEquipDeadline;
        private float next, deadline, since;
        private ulong item, postA, postB, spout, tube;
        private TreeBase? tree;
        internal string Status
        {
            get
            {
                var t = plugin.World?.Tubing;
                if (t == null) return "tubing phase=" + phase;
                return "tubing phase=" + phase + "; posts=" + t.TestAlive(TubingKind.Post) + " spouts=" + t.TestAlive(TubingKind.Spout) + " tubes=" + t.TestAlive(TubingKind.Tube) +
                    " localTubes=" + t.TestLocalTubes + " pending=" + t.TestPending + " item=" + item +
                    "; rollGrabAttempts=" + rollGrabAttempts + " equipRequested=" + rollEquipRequested +
                    " holdingRoll=" + (gameplay.Player.Get_HoldingItem is Tool_TubingRoll) +
                    " activeRoll=" + (gameplay.Player.Get_ActiveTool is Tool_TubingRoll) + "; connect=" + t.TestLastConnectNote;
            }
        }
        internal PairedTubingTest(MonMPPlugin plugin, Gameplay gameplay) { this.plugin = plugin; this.gameplay = gameplay; }
        internal bool Tick()
        {
            var world = plugin.World;
            var session = plugin.Session;
            if (world == null || session == null)
            {
                if (deadline == 0) return false;
                throw new InvalidOperationException("Tubing session ended before fixture completion: " + Status);
            }
            if (!world.Items.Ready || (deadline == 0 && session.Peers.Count < 2)) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 180;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Tubing test stalled: " + Status);
            if (Time.unscaledTime < next) return false;
            int before = phase;
            bool done = plugin.TestOptions.IsHost ? Host(world) : Guest(world);
            if (phase != before) Debug.Log("[MonMP] " + Status);
            return done;
        }
        private static ulong[] Tools<T>(SharedItems items) => items.LiveItems.Where(p => p.Item is T).Select(p => p.Id).OrderBy(id => id).ToArray();
        private bool Host(SharedWorld world)
        {
            var items = world.Items; var tubing = world.Tubing;
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                Require(SharedForest.TestPlantPoint(out var point), "no free home-land ground for the tools.");
                items.TestSpawn(point + new Vector3(0, 0.5f, 0), items.TestPrefab("Tool/TubingPost"));
                items.TestSpawn(point + new Vector3(0.6f, 0.5f, 0), items.TestPrefab("Tool/TubingPost"));
                items.TestSpawn(point + new Vector3(0, 0.5f, 0.6f), items.TestPrefab("Tool/TubingChalumeau"));
                items.TestSpawn(point + new Vector3(0.6f, 0.5f, 0.6f), items.TestPrefab("Tool/TubingRoll"));
                phase = 1; next = Time.unscaledTime + 1;
            }
            else if (phase == 1 && tubing.TestAlive(TubingKind.Tube) == 1)
            {
                var t = tubing.TestTube(tubing.TestNewest(TubingKind.Tube));
                Require(t != null && t.GetOrigin.isConnected && t.GetTarget.isConnected, "the host did not build the guest's tube natively.");
                Require(items.LiveItems.Any(p => p.Item is Tool_TubingRoll roll && roll.Value1 == 9), "the host did not debit the guest's leased tubing roll.");
                // The guest still holds its roll when this new native post arrives; it must
                // expose its free targets without requiring a drop and re-equip.
                UnityEngine.Object.Instantiate(Singleton<GameAssets>.i.SugarShack_Placed_TubingPost,
                    t!.GetTarget.Post.transform.position + Vector3.forward * 2, Quaternion.identity);
                phase = 2;
            }
            else if (phase == 2 && tubing.TestAlive(TubingKind.Spout) == 1) { phase = 22; next = Time.unscaledTime + 0.3f; } // the spout taps its tree on Start
            else if (phase == 22)
            {
                var post = tubing.TestPost(tubing.TestNewest(TubingKind.Spout));
                Require(post != null && UnityEngine.Object.FindObjectsOfType<TreeBase>().Any(t => t.Get_SpoutConnected == post), "the host's spout did not tap a maple tree.");
                phase = 3;
            }
            else if (phase == 3 && plugin.Session!.Peers.Count == 1) return true;
            return false;
        }
        private bool Guest(SharedWorld world)
        {
            var items = world.Items; var tubing = world.Tubing;
            uint me = plugin.Session!.LocalPeerId;
            switch (phase)
            {
                case 0:
                {
                    var posts = Tools<Tool_TubingPost>(items); var spouts = Tools<Tool_TubingChalumeau>(items);
                    if (posts.Length < 2 || spouts.Length < 1) return false;
                    item = posts[0]; phase = 1; next = Time.unscaledTime + 2; // let the tools settle
                    return false;
                }
                case 1: case 5: case 13:
                    if (!MoveNear(items.ItemOf(item)!.transform.position, 1.5f, 5)) { next = Time.unscaledTime + 2; return false; }
                    phase++; next = Time.unscaledTime + 1;
                    return false;
                case 2: case 6: case 14:
                    items.TestGrab(item); since = Time.unscaledTime; phase++;
                    return false;
                case 3: case 7:
                {
                    if (!items.OwnedBy(item, me))
                    {
                        if (Time.unscaledTime - since > 4)
                        {
                            var native = items.ItemOf(item);
                            Debug.Log("[MonMP] tubing grab retry: handles=" + items.Handles(native) + " distance=" + (native == null ? -1 : (native.transform.position - gameplay.PlayerWalking.transform.position).magnitude) +
                                " holding=" + gameplay.Player.IsHoldingAnObject + " state=" + gameplay.Get_PlayerState + " owner=" + items.TestState(item).Lease.Owner + " at=" + native?.transform.position);
                            phase -= 2;
                        }
                        return false;
                    }
                    Require(GroundPoint(phase == 3 ? 0 : 1, out var at), "no home-land ground point beside the guest for a post.");
                    ulong before = tubing.TestNewest(TubingKind.Post);
                    tubing.TestPlace(item, TubingKind.Post, at, 30);
                    if (phase == 3) postA = before; else postB = before; // replaced once the new record arrives
                    phase++; since = Time.unscaledTime;
                    return false;
                }
                case 4: case 8:
                {
                    ulong newest = tubing.TestNewest(TubingKind.Post);
                    ulong previous = phase == 4 ? postA : postB;
                    if (newest == previous || tubing.TestPost(newest) == null || items.ItemOf(item) != null) return false;
                    Require(!gameplay.Player.IsHoldingAnObject, "the guest still holds the spent post tool.");
                    if (phase == 4) { postA = newest; item = Tools<Tool_TubingPost>(items).First(); }
                    else postB = newest;
                    phase++; next = Time.unscaledTime + 0.5f;
                    return false;
                }
                case 9:
                    item = Tools<Tool_TubingRoll>(items).First();
                    if (!MoveNear(items.ItemOf(item)!.transform.position, 1.5f, 5)) { next = Time.unscaledTime + 2; return false; }
                    rollGrabAttempts = 0; rollEquipRequested = false;
                    phase = 81; next = Time.unscaledTime + 1;
                    return false;
                case 81:
                    if (rollGrabAttempts >= 4) throw new InvalidOperationException("Tubing roll pickup exhausted native retries: " + items.TestPickupStatus(item));
                    rollGrabAttempts++; rollEquipRequested = false;
                    items.TestGrab(item); phase = 82; since = Time.unscaledTime;
                    return false;
                case 82:
                    if (!items.OwnedBy(item, me)) { if (Time.unscaledTime - since > 4) phase = 81; return false; }
                    var heldRoll = items.ItemOf(item) as Tool_TubingRoll;
                    Require(heldRoll != null, "the guest's leased roll disappeared.");
                    if (!heldRoll!.isEquiped || gameplay.Player.Get_ActiveTool != heldRoll)
                    {
                        if (!rollEquipRequested && gameplay.Player.Get_HoldingItem == heldRoll)
                        {
                            items.TestUse(item); rollEquipRequested = true; rollEquipDeadline = Time.unscaledTime + 3;
                        }
                        else if ((rollEquipRequested && Time.unscaledTime >= rollEquipDeadline) ||
                            (!rollEquipRequested && Time.unscaledTime - since > 5))
                            throw new InvalidOperationException("Tubing roll native Use did not finish equipping: " + items.TestPickupStatus(item) + "; " + Status);
                        next = Time.unscaledTime + 0.2f;
                        return false;
                    }
                    phase = 83;
                    return false;
                case 83:
                    // TestDrawTube bypasses the native click raycast: explicitly stage at its
                    // origin and allow ordinary player/held-item pose replication before drawing.
                    if (!MoveNear(tubing.TestPost(postA)!.transform.position, 1.5f, 3)) { next = Time.unscaledTime + 2; return false; }
                    phase = 84; next = Time.unscaledTime + 1;
                    return false;
                case 84:
                    Require(items.ItemOf(item) is Tool_TubingRoll activeRoll && activeRoll.isEquiped &&
                        gameplay.Player.Get_ActiveTool == activeRoll && items.TryGetLocalLease(activeRoll, out _, out _),
                        "the staged guest roll lost its real active tool or lease.");
                    // The roll's finished tube between the two posts, predicted locally.
                    tubing.TestDrawTube(tubing.TestPost(postA)!, 0, tubing.TestPost(postB)!, 0);
                    Require(tubing.TestPending, "the guest's tube was not sent to the host.");
                    phase = 10;
                    return false;
                case 10:
                    if (tubing.TestAlive(TubingKind.Tube) != 1 || tubing.TestPending) return false;
                    tube = tubing.TestNewest(TubingKind.Tube);
                    Require(tubing.TestTube(tube) != null && tubing.TestLocalTubes == 1, "the guest did not adopt its predicted tube.");
                    Require(tubing.TestState(tube).Joins(postA, 0, postB, 0), "the host's tube joins the wrong points.");
                    if (items.ItemOf(item)!.Value1 != 9) return false;
                    ulong late = tubing.TestNewest(TubingKind.Post);
                    if (late == postB || tubing.TestPost(late) == null || tubing.TestPost(late)!.GetAllConnection.Any(p => p.Post == null)) return false;
                    Require(tubing.TestPost(late)!.GetAllConnection.All(p => p.isConnected || p.VisualSphere.activeSelf),
                        "a post replicated while the roll was equipped has hidden free targets.");
                    phase = 11; next = Time.unscaledTime + 0.5f;
                    return false;
                case 11:
                    // The roll's native tube removal.
                    tubing.TestTube(tube)!.DeleteThisTube();
                    phase = 12;
                    return false;
                case 12:
                    if (tubing.TestExists(tube) || Time.unscaledTime < next || items.ItemOf(item)!.Value1 != 10) return false;
                    Require(tubing.TestLocalTubes == 0, "the removed tube came back on the guest.");
                    items.TestPutAwayTool();
                    tubing.TestPost(postB)!.MouseRightDown();
                    Require(tubing.TestPost(postB) != null, "the guest removed its post locally.");
                    phase = 20;
                    return false;
                case 20:
                    if (tubing.TestExists(postB) || tubing.TestPost(postB) != null || Tools<Tool_TubingPost>(items).Length < 1) return false;
                    item = Tools<Tool_TubingChalumeau>(items).First();
                    phase = 13;
                    return false;
                case 15:
                {
                    if (!items.OwnedBy(item, me)) { if (Time.unscaledTime - since > 4) phase = 14; return false; }
                    tree = UnityEngine.Object.FindObjectsOfType<TreeBase>()
                        .Where(t => t.GetTree_Type == Tree_Type.Maple && t.Get_SpoutConnected == null && t.tag == "Tree" && t.gameObject.layer == LayerMask.NameToLayer("Default"))
                        .OrderBy(t => (t.transform.position - gameplay.PlayerWalking.transform.position).sqrMagnitude).FirstOrDefault();
                    Require(tree != null, "no free maple tree for the spout.");
                    if (!MoveNear(tree!.transform.position, 2.5f, 5)) { next = Time.unscaledTime + 2; return false; }
                    phase = 16; next = Time.unscaledTime + 1;
                    return false;
                }
                case 16:
                {
                    var trunk = tree!.transform.position + Vector3.up * 1.5f;
                    var from = gameplay.PlayerWalking.transform.position; from.y = trunk.y;
                    Require(Physics.Raycast(from, (trunk - from).normalized, out var hit, 8, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore) && hit.collider.GetComponent<TreeBase>() == tree,
                        "the guest cannot reach the maple trunk.");
                    var look = Quaternion.LookRotation(from - hit.point);
                    tubing.TestPlace(item, TubingKind.Spout, hit.point, Mathf.Repeat(look.eulerAngles.y, 360));
                    phase = 17;
                    return false;
                }
                case 17:
                    if (tubing.TestAlive(TubingKind.Spout) != 1 || items.ItemOf(item) != null) return false;
                    spout = tubing.TestNewest(TubingKind.Spout);
                    if (tubing.TestPost(spout) == null) return false;
                    phase = 18; next = Time.unscaledTime + 0.5f;
                    return false;
                case 18:
                    Require(tree!.Get_SpoutConnected == tubing.TestPost(spout), "the guest's spout replica did not tap the tree.");
                    tubing.TestPost(spout)!.MouseRightDown();
                    phase = 19;
                    return false;
                case 19:
                    if (tubing.TestExists(spout) || tubing.TestPost(spout) != null) return false;
                    Require(tree!.Get_SpoutConnected == null && tubing.TestAlive(TubingKind.Post) >= 1, "the guest's tree kept the removed spout.");
                    plugin.EndPairedTestSession();
                    return true;
            }
            return false;
        }
        // A home-land ground point beside the guest, clear of other posts: where the tool would stand.
        private bool GroundPoint(int slot, out Vector3 point)
        {
            var origin = gameplay.PlayerWalking.transform.position;
            for (int step = 0; step < 16; step++)
            {
                float angle = (step * 2 + slot * 5) * Mathf.PI / 16;
                var at = origin + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * 2.5f;
                if (!Physics.Raycast(at + Vector3.up * 10, Vector3.down, out var hit, 30, LayerMask.GetMask("Ground"), QueryTriggerInteraction.Ignore)) continue;
                if (!Singleton<Gameplay>.i.IsOnHomeLand(hit.point)) continue;
                point = hit.point + Vector3.up * 1.4f;
                var candidate = point;
                if (UnityEngine.Object.FindObjectsOfType<SugarShack_TubingPost>().Any(p => (p.transform.position - candidate).magnitude < 1)) continue;
                return true;
            }
            point = default; return false;
        }
        private bool MoveNear(Vector3 target, float from, float to)
        {
            var blockers = new HashSet<string>();
            for (float radius = from; radius <= to; radius += 0.5f)
                for (int step = 0; step < 16; step++)
                {
                    float angle = step * Mathf.PI / 8;
                    var at = target + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                    if (!Physics.Raycast(at + Vector3.up * 30, Vector3.down, out var hit, 60, ~0, QueryTriggerInteraction.Ignore)) continue;
                    at = hit.point + Vector3.up;
                    // Loose items (and the host standing over them) are pushed aside; only solid scenery blocks.
                    var blocking = Physics.OverlapCapsule(at, at + Vector3.up, 0.4f, ~0, QueryTriggerInteraction.Ignore).Where(c => !(c is TerrainCollider) && c.tag != "Player" &&
                        !(c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic && c.attachedRigidbody.mass < 100) && !c.transform.root.name.StartsWith("MonMP Remote ")).ToArray();
                    if (blocking.Length > 0) { foreach (var c in blocking.Take(3)) blockers.Add(c.transform.root.name + "/" + c.name + "@" + LayerMask.LayerToName(c.gameObject.layer)); continue; }
                    gameplay.PlayerWalking.transform.position = at; gameplay.Get_Player_Rigidbody.position = at; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                    unloadedTries = 0;
                    return true;
                }
            // Nothing under any candidate: the guest's terrain there has not streamed in yet. Stand over
            // the target and let it load before trying again.
            if (blockers.Count == 0 && unloadedTries++ < 4)
            {
                var above = target + Vector3.up * 3;
                gameplay.PlayerWalking.transform.position = above; gameplay.Get_Player_Rigidbody.position = above; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                return false;
            }
            throw new InvalidOperationException("Tubing test: no clear standing point near " + target + "; blockers: " + string.Join(", ", blockers.Take(12)));
        }
        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException("Tubing test: " + reason); }
    }
}
