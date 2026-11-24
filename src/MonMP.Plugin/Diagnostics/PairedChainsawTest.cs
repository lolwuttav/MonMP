using System;
using System.Linq;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Exercise both owners' native chainsaw cut loops on the other peer, then the guest rack path.
    internal sealed class PairedChainsawTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private ulong saw;
        private uint remoteOwner;
        private int phase;
        private float next, deadline, dropY;
        private bool racked, hostCutSeen, hostCutStopped;
        internal string Status => "chainsaw phase=" + phase + "; saw=" + saw.ToString("X16") +
            (saw == 0 || plugin.Items == null ? "" : "; rack=" + plugin.Items.TestState(saw).Values.IntValue1 + "; owner=" + plugin.Items.TestState(saw).Lease.Owner +
                "; localRack=" + plugin.Items.TestItem(saw)?.IntValue1 + "; cutPlaying=" + plugin.Items.TestChainsawCutPlaying(saw) +
                "; toolActive=" + (remoteOwner != 0 && plugin.RemoteAvatarToolActive(remoteOwner)) + "; hostCutSeen=" + hostCutSeen +
                "; hostCutStopped=" + hostCutStopped + "; y=" + plugin.Items.TestItem(saw)?.transform.position.y + "; dropY=" + dropY);
        internal PairedChainsawTest(MonMPPlugin plugin, Gameplay gameplay) { this.plugin = plugin; this.gameplay = gameplay; }

        internal bool Tick()
        {
            var items = plugin.Items!;
            if (deadline == 0) deadline = Time.unscaledTime + 120f;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Chainsaw test stalled: " + Status);
            if (Time.unscaledTime < next) return false;
            if (saw == 0)
            {
                foreach (ushort prefab in items.TestPrefabs(typeof(Tool_Chainsaw)))
                {
                    ulong id = items.TestNewestPrefab(prefab);
                    if (id != 0 && items.TestState(id).Exists) { saw = id; break; }
                }
                if (saw == 0) return false;
            }
            var state = items.TestState(saw);
            var item = items.TestItem(saw);
            if (item == null) return false;
            if (plugin.TestOptions.IsHost)
            {
                if (phase == 0) { if (state.Values.IntValue1 != 1) items.TestHostDock(saw); phase = 1; }
                else if (phase == 1 && plugin.Session!.Peers.Count >= 2 && plugin.RemoteAvatarCount > 0 &&
                    plugin.TestRemoteAvatarNear(plugin.Session.Peers.Keys.First(id => id != 0), items.TestChainsawHold + new Vector3(1.2f, 0, 0), 0.9f))
                {
                    Stand(items.TestChainsawHold + new Vector3(1.2f, 0, 0));
                    items.TestGrab(saw);
                    phase = 10;
                }
                else if (phase == 10 && state.Lease.Owner == 0 && items.TestLocalHeld(saw))
                {
                    StartCut(items, (Tool_Chainsaw)item!);
                    phase = 11; next = Time.unscaledTime + 2.5f;
                }
                else if (phase == 11 && Time.unscaledTime >= next)
                {
                    StopCut(items, (Tool_Chainsaw)item!);
                    phase = 12; next = Time.unscaledTime + 0.75f;
                }
                else if (phase == 12 && Time.unscaledTime >= next)
                {
                    Require(!items.TestChainsawCutPlaying(saw), "(#150) The host's cut source did not stop.");
                    items.TestPutAwayTool();
                    phase = 13;
                }
                else if (phase == 13 && state.Lease.Owner == Core.World.ItemLease.Unowned)
                {
                    items.TestHostDock(saw);
                    phase = 14; next = Time.unscaledTime + 0.75f;
                }
                else if (phase == 14 && Time.unscaledTime >= next && state.Values.IntValue1 == 1) phase = 15;
                else if (phase == 15 && state.Lease.Owner != Core.World.ItemLease.Unowned && state.Lease.Owner != 0 &&
                    plugin.RemoteAvatarToolActive(state.Lease.Owner))
                {
                    remoteOwner = state.Lease.Owner;
                    Require(items.HeldItemOf(remoteOwner) == item, "(#150) The remote tool pose did not resolve to the guest-held chainsaw.");
                    Require(items.TestChainsawCutPlaying(saw), "(#150) The host observer did not start the chainsaw cut source.");
                    phase = 16;
                }
                else if (phase == 16 && !plugin.RemoteAvatarToolActive(remoteOwner) && !items.TestChainsawCutPlaying(saw)) phase = 17;
                else if (phase == 17 && plugin.Session!.Peers.Count == 1 &&
                    state.Lease.Owner == Core.World.ItemLease.Unowned && !items.TestChainsawCutPlaying(saw))
                { plugin.EndPairedTestSession(); return true; }
                return false;
            }
            if (!hostCutSeen && state.Lease.Owner == 0 && plugin.RemoteAvatarToolActive(0))
            {
                if (items.HeldItemOf(0) != item) return false;
                if (!items.TestChainsawCutPlaying(saw)) return false;
                hostCutSeen = true;
            }
            if (hostCutSeen && !hostCutStopped && !plugin.RemoteAvatarToolActive(0) && !items.TestChainsawCutPlaying(saw))
                hostCutStopped = true;
            uint local = plugin.Session!.LocalPeerId;
            switch (phase)
            {
                case 0:
                    if (!racked)
                    {
                        if (state.Values.IntValue1 != 1 || item.IntValue1 != 1 || !items.TestChainsawRacked(saw)) return false;
                        Stand(items.TestChainsawHold + new Vector3(1.2f, 0, 0));
                        racked = true; next = Time.unscaledTime + 1f; return false;
                    }
                    if (!hostCutSeen) Stand(items.TestChainsawHold + new Vector3(1.2f, 0, 0));
                    if (!hostCutSeen || !hostCutStopped) return false;
                    if (Time.unscaledTime < next || state.Values.IntValue1 != 1 || item.IntValue1 != 1) return false;
                    Require(items.TestChainsawRacked(saw), "(#108) The host-racked chainsaw is not on the guest's rack.");
                    racked = false;
                    Stand(items.TestChainsawHold + new Vector3(1.2f, 0, 0));
                    phase = 1; next = Time.unscaledTime + 2f; return false;
                case 1:
                    items.TestGrab(saw);
                    phase = 2; return false;
                case 2:
                    if (state.Lease.Owner != local || !items.TestLocalHeld(saw)) return false;
                    phase = 3; next = Time.unscaledTime + 2.5f; return false;
                case 3:
                    Require(state.Values.IntValue1 == 0, "The host kept the guest-held chainsaw flagged as racked.");
                    Require(item.IntValue1 == 0, "The guest-held chainsaw was re-flagged as racked.");
                    // Carry it clear of the rack trigger, then back in.
                    item.transform.position = items.TestChainsawHold + Vector3.up * 4f;
                    phase = 31; next = Time.unscaledTime + 0.3f; return false;
                case 31:
                    item.transform.position = items.TestChainsawHold;
                    phase = 4; return false;
                case 4:
                    if (state.Values.IntValue1 != 1 || items.TestLocalHeld(saw)) return false;
                    if (!racked) { racked = true; next = Time.unscaledTime + 1.5f; return false; }
                    Require(items.TestChainsawRacked(saw), "(#108) The guest-racked chainsaw left the guest's rack.");
                    UnityEngine.Debug.Log("MonMP chainsaw: guest rack presentation held for host and guest docks");
                    Stand(items.TestChainsawHold + new Vector3(1.2f, 0, 0));
                    phase = 5; next = Time.unscaledTime + 1f; return false;
                case 5:
                    items.TestGrab(saw);
                    phase = 6; return false;
                case 6:
                    if (state.Lease.Owner != local || !items.TestLocalHeld(saw)) return false;
                    Stand(items.TestChainsawHold + new Vector3(5f, 0, 0));
                    phase = 60; next = Time.unscaledTime + 0.8f; return false;
                case 60:
                    StartCut(items, (Tool_Chainsaw)item);
                    phase = 7; next = Time.unscaledTime + 2.5f; return false;
                case 7:
                    if (Time.unscaledTime < next) return false;
                    StopCut(items, (Tool_Chainsaw)item);
                    phase = 8; next = Time.unscaledTime + 0.75f; return false;
                case 8:
                    dropY = item.transform.position.y;
                    if (Time.unscaledTime < next) return false;
                    Require(!items.TestChainsawCutPlaying(saw), "(#150) The acting guest's cut source did not stop.");
                    items.TestPutAwayTool();
                    phase = 9; return false;
                case 9:
                    if (state.Lease.Owner != Core.World.ItemLease.Unowned) return false;
                    Require(state.Values.IntValue1 == 0, "The dropped chainsaw snapped back to the rack.");
                    if (item.transform.position.y > dropY - 0.3f) return false;
                    phase = 10; next = Time.unscaledTime + 1f; return false;
                case 10:
                    plugin.EndPairedTestSession();
                    return true;
            }
            return false;
        }
        private void Stand(Vector3 at)
        {
            gameplay.PlayerWalking.transform.position = at;
            if (gameplay.Get_Player_Rigidbody != null) { gameplay.Get_Player_Rigidbody.position = at; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        }
        private void StartCut(SharedItems items, Tool_Chainsaw chainsaw)
        {
            // Keep the native input flag consistent so SharedItems' local sound watchdog lets the loop run.
            gameplay.Player.EquipThisTool(chainsaw);
            Require(chainsaw.isEquiped, "(#150) The local test player could not equip the leased chainsaw.");
            var sound = chainsaw.GetComponent<SOUND_Tool_Chainsaw>();
            Require(sound != null, "(#150) The native chainsaw sound component is missing.");
            items.TestSetChainsawCutting(saw, true);
            sound!.CutStart();
            Require(items.TestChainsawCutPlaying(saw), "(#150) The acting player did not start the native cut source.");
        }
        private void StopCut(SharedItems items, Tool_Chainsaw chainsaw)
        {
            try { items.TestSetChainsawCutting(saw, false); }
            finally { chainsaw.GetComponent<SOUND_Tool_Chainsaw>()?.CutStop(); }
        }
        internal void Abort()
        {
            var items = plugin.Items;
            if (items == null || saw == 0) return;
            var chainsaw = items.TestItem(saw) as Tool_Chainsaw;
            if (chainsaw == null || !items.TestLocalHeld(saw)) return;
            try { items.TestSetChainsawCutting(saw, false); }
            finally
            {
                chainsaw.GetComponent<SOUND_Tool_Chainsaw>()?.CutStop();
                if (gameplay.Player.Get_ActiveTool == chainsaw || gameplay.Player.Get_HoldingItem == chainsaw) items.TestPutAwayTool();
            }
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
