using System;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Real native equip/unequip on both peers; no synthetic root-scale assignments.
    internal sealed class PairedEquippedToolScaleTest
    {
        private static readonly Type[] Types = { typeof(Tool_Chainsaw), typeof(Tool_DirtShovel), typeof(Tool_MapTorch),
            typeof(Tool_HarvestBasket), typeof(Tool_TubingRoll), typeof(Tool_AdjustableSpanner) };
        private static readonly float[] EquipScales = { .3f, .3f, .3f, .5f, .18f, .6f };
        // The first remote avatar/item presentation can stall a frame longer than the
        // previous two-second native hold. Leave eight seconds for the other peer to
        // start observing, while still requiring its full one-second scale window.
        private const float EquippedHoldSeconds = 8f;
        private const float FixtureTimeoutSeconds = 240f;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly Vector3 originalPosition;
        private readonly float started = Time.unscaledTime;
        private ulong id;
        private int index, phase;
        private float holdUntil, nextGrab, observerStarted;
        private bool requestedEquip, observedHost, observedGuest;

        internal string Status => "equipped tool scale tool=" + (index < Types.Length ? Types[index].Name : "done") +
            "; phase=" + phase + "; id=" + id + "; observedHost=" + observedHost + "; observedGuest=" + observedGuest;
        internal PairedEquippedToolScaleTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            originalPosition = gameplay.PlayerWalking.transform.position;
        }
        internal bool Tick()
        {
            if (Time.unscaledTime - started > FixtureTimeoutSeconds) throw new InvalidOperationException("Equipped tool scale timed out: " + Status);
            if (plugin.World?.Channel.FullySynchronized != true) return false;
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }
        private bool Host()
        {
            var items = plugin.Items!;
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                id = items.TestSpawnPresentationTool(Types[index], gameplay.PlayerWalking.transform.position + Vector3.up);
                requestedEquip = observedGuest = false;
                phase = 1;
            }
            if (phase == 1 && Equip(items))
            {
                AssertNative(items);
                holdUntil = Time.unscaledTime + EquippedHoldSeconds;
                Debug.Log("MONMP_EQUIPPED_SCALE: host native hold seconds=" + EquippedHoldSeconds + "; " + Status);
                phase = 2;
            }
            if (phase == 2)
            {
                AssertNative(items);
                if (!UnitRecord(items)) return false;
                if (Time.unscaledTime < holdUntil) return false;
                UnequipAndDrop(items);
                phase = 3;
            }
            if (phase == 3)
            {
                var state = items.TestState(id);
                if (state.Lease.Owner != ItemLease.Unowned && state.Lease.Owner != plugin.Session!.LocalPeerId)
                {
                    AssertObserver(items, "host observes guest", !observedGuest);
                    if (!observedGuest) observerStarted = Time.unscaledTime;
                    observedGuest = true;
                }
                if (!observedGuest || state.Lease.Owner != ItemLease.Unowned) return false;
                Require(Time.unscaledTime - observerStarted >= 1f, "Guest equipped observation window was too short.");
                Debug.Log("MONMP_EQUIPPED_SCALE: host observed guest seconds=" + (Time.unscaledTime - observerStarted) + "; " + Status);
                AssertObserver(items, "host after guest unequip");
                items.TestDestroy(id);
                phase = 4;
            }
            if (phase == 4 && !items.TestState(id).Exists)
            {
                if (++index < Types.Length) { phase = 0; id = 0; return false; }
                RestorePosition();
                phase = 5;
            }
            return phase == 5 && plugin.Session!.Peers.Count == 1;
        }
        private bool Guest()
        {
            var items = plugin.Items!;
            if (phase == 0)
            {
                id = items.TestNewest(Types[index]);
                if (id == 0 || items.TestItem(id) == null || items.TestState(id).Lease.Owner != 0) return false;
                AssertObserver(items, "guest observes host");
                observedHost = true;
                observerStarted = Time.unscaledTime;
                requestedEquip = false;
                phase = 1;
            }
            if (phase == 1)
            {
                if (items.TestState(id).Lease.Owner == 0)
                {
                    // Keep sampling beyond the lease grant: the native equip transition and
                    // its following item snapshots occur after the initial pickup snapshot.
                    AssertObserver(items, "guest observes host equipped window", false);
                    return false;
                }
                if (items.TestState(id).Lease.Owner != ItemLease.Unowned && !items.TestLocalHeld(id)) return false;
                Require(Time.unscaledTime - observerStarted >= 1f, "Host equipped observation window was too short.");
                Debug.Log("MONMP_EQUIPPED_SCALE: guest observed host seconds=" + (Time.unscaledTime - observerStarted) + "; " + Status);
                if (!Equip(items)) return false;
                AssertNative(items);
                holdUntil = Time.unscaledTime + EquippedHoldSeconds;
                Debug.Log("MONMP_EQUIPPED_SCALE: guest native hold seconds=" + EquippedHoldSeconds + "; " + Status);
                phase = 2;
            }
            if (phase == 2)
            {
                AssertNative(items);
                if (!UnitRecord(items) || Time.unscaledTime < holdUntil) return false;
                UnequipAndDrop(items);
                phase = 3;
            }
            if (phase == 3 && !items.TestState(id).Exists)
            {
                if (++index < Types.Length) { phase = 0; id = 0; observedHost = false; return false; }
                RestorePosition();
                plugin.EndPairedTestSession();
                return true;
            }
            return false;
        }
        private bool Equip(SharedItems items)
        {
            var tool = items.TestItem(id) as InteractableTools;
            if (tool == null) return false;
            if (gameplay.Player.Get_ActiveTool == tool && tool.isEquiped) return true;
            if (gameplay.Player.Get_HoldingItem == tool)
            {
                if (!requestedEquip) { items.TestUse(id); requestedEquip = true; }
            }
            else if (Time.unscaledTime >= nextGrab)
            {
                var near = tool.transform.position + Vector3.right;
                gameplay.PlayerWalking.transform.position = near;
                var rig = gameplay.PlayerWalking.GetComponent<Rigidbody>();
                if (rig != null) { rig.position = near; rig.velocity = Vector3.zero; }
                items.TestGrab(id);
                nextGrab = Time.unscaledTime + .7f;
            }
            return false;
        }
        private void AssertNative(SharedItems items)
        {
            var tool = (InteractableTools)items.TestItem(id)!;
            Require(tool.isEquiped && gameplay.Player.Get_ActiveTool == tool && items.TestLocalHeld(id), "Real native equipment/lease was lost.");
            Require(Vector3.Distance(tool.transform.localScale, Vector3.one * EquipScales[index]) < .001f,
                "Native first-person equip scale differs from verified native factor: " + HeldItemPresentationProbe.DescribeItemScale(tool));
        }
        private static bool UnitRecord(SharedItems items, ulong entity)
        {
            var state = items.TestState(entity);
            return Mathf.Abs(state.ScaleX - 1) < .001f && Mathf.Abs(state.ScaleY - 1) < .001f && Mathf.Abs(state.ScaleZ - 1) < .001f;
        }
        private bool UnitRecord(SharedItems items) => UnitRecord(items, id);
        private void AssertObserver(SharedItems items, string label, bool log = true)
        {
            var tool = items.TestItem(id)!;
            Require(UnitRecord(items) && tool.transform.parent == null && Vector3.Distance(tool.transform.lossyScale, Vector3.one) < .001f,
                label + " did not render native world scale1: " + HeldItemPresentationProbe.DescribeItemScale(tool));
            if (log) Debug.Log("MONMP_EQUIPPED_SCALE: " + label + "; " + HeldItemPresentationProbe.DescribeItemScale(tool));
        }
        private void UnequipAndDrop(SharedItems items)
        {
            var tool = (InteractableTools)items.TestItem(id)!;
            AccessTools.Method(typeof(PlayerInteractable), "UnEquipTool").Invoke(gameplay.Player, null);
            Require(!tool.isEquiped && Vector3.Distance(tool.transform.localScale, Vector3.one) < .001f,
                "Native unequip did not restore world scale1: " + HeldItemPresentationProbe.DescribeItemScale(tool));
            Require(gameplay.Player.Get_HoldingItem == tool, "Native unequip did not return this tool to the carrying hand.");
            // Check the native local scale before UnGrab detaches from the view rig;
            // SetParent(null) preserves world scale, including any parent multiplier.
            items.TestDrop(false);
        }
        private void RestorePosition()
        {
            if (gameplay == null || gameplay.PlayerWalking == null) return;
            gameplay.PlayerWalking.transform.position = originalPosition;
            var rig = gameplay.PlayerWalking.GetComponent<Rigidbody>();
            if (rig != null) { rig.position = originalPosition; rig.velocity = Vector3.zero; }
        }
        internal void Cleanup()
        {
            if (plugin.World != null && id != 0 && phase < 4 && gameplay != null && gameplay.Player != null)
                plugin.Items!.TestPutAwayTool();
            RestorePosition();
        }
        private void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message + "; " + Status);
        }
    }
}
