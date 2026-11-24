using System;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;
using NWH.VehiclePhysics2.Effects;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedLanternTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private int phase;
        private float nextAction;
        private ulong entity;
        private uint staleGeneration, originalPeer;
        private InteractableItems? spawned;
        private string statusDetail = "";
        public string Status => "lantern phase " + phase + (statusDetail.Length == 0 ? "" : "; " + statusDetail);
        public PairedLanternTest(MonMPPlugin plugin, Gameplay gameplay) { this.plugin = plugin; this.gameplay = gameplay; }
        public bool Tick()
        {
            if (Time.unscaledTime < nextAction) return false;
            var items = plugin.Items;
            if (!plugin.TestOptions.IsHost && phase == 10)
            { plugin.BeginPairedTestSession(); phase = 11; return false; }
            if (items?.Ready != true) return false;
            ushort prefab = items.TestPrefab("Event/Lantern");
            if (plugin.TestOptions.IsHost) return Host(items, prefab);
            if (phase == 0 || phase == 11)
            {
                // The initial/reconnect placement is asynchronous and can otherwise undo
                // this fixture's range-test teleport after the item snapshot arrives.
                bool profileReady = plugin.World?.PersonalProfileReady == true;
                bool positionSettled = plugin.World?.ReconnectPositions?.Settled == true;
                statusDetail = "profileReady=" + profileReady + "; reconnectSettled=" + positionSettled;
                if (!profileReady || !positionSettled) return false;
            }
            if (phase == 0)
            {
                entity = items.TestNewestPrefab(prefab);
                if (entity == 0) return false;
                if (Math.Abs(items.TestState(entity).ScaleX - 0.77f) > 0.001f || items.TestState(entity).Values.IntValue1 != 1) return false;
                Visual(items, true);
                MoveNear(items.TestItem(entity)!.transform.position + Vector3.right * 30);
                statusDetail = Details(items);
                phase = 1; nextAction = Time.unscaledTime + 0.7f;
            }
            else if (phase == 1)
            {
                statusDetail = Details(items);
                Require(Vector3.Distance(gameplay.PlayerWalking.transform.position, items.TestItem(entity)!.transform.position) > 20,
                    "Distant fixture position was overwritten before interaction; " + statusDetail);
                staleGeneration = items.TestState(entity).Lease.Generation;
                items.TestUse(entity); phase = 2; nextAction = Time.unscaledTime + 0.5f;
            }
            else if (phase == 2)
            {
                Require(items.TestState(entity).Values.IntValue1 == 1 && items.TestState(entity).Lease.Generation == staleGeneration,
                    "Distant interaction changed lantern; before=" + statusDetail + "; after=" + Details(items) +
                    "; expectedLight=1; expectedGeneration=" + staleGeneration);
                MoveNear(items.TestItem(entity)!.transform.position); phase = 3; nextAction = Time.unscaledTime + 0.7f;
            }
            else if (phase == 3)
            { items.TestUse(entity); items.TestUse(entity); phase = 4; }
            else if (phase == 4 && items.TestState(entity).Values.IntValue1 == 0)
            {
                Visual(items, false);
                items.TestCommand(entity, ItemAction.Interact, staleGeneration);
                items.TestCommand(entity, ItemAction.Consume, items.TestState(entity).Lease.Generation);
                phase = 5; nextAction = Time.unscaledTime + 0.5f;
            }
            else if (phase == 5)
            {
                Require(items.TestState(entity).Exists && items.TestState(entity).Values.IntValue1 == 0, "Stale toggle or consume command mutated lantern.");
                items.TestGrab(entity); phase = 6;
            }
            else if (phase == 6 && items.TestState(entity).Lease.Owner == plugin.Session!.LocalPeerId)
            {
                Require(items.TestState(entity).Values.IntValue2 == 1 && gameplay.Player.Get_HoldingItem == items.TestItem(entity), "Pickup flag or native held item missing.");
                phase = 7; nextAction = Time.unscaledTime + 0.7f;
            }
            else if (phase == 7)
            { items.TestUse(entity); phase = 8; }
            else if (phase == 8 && items.TestState(entity).Values.IntValue1 == 1)
            {
                Visual(items, true); originalPeer = plugin.Session!.LocalPeerId;
                plugin.EndPairedTestSession();
                Require(items.TestRestored && gameplay.Player.Get_HoldingItem == null, "Lantern disconnect failed to restore originals.");
                phase = 10; nextAction = Time.unscaledTime + 1;
            }
            else if (phase == 11)
            {
                Require(items.TestState(entity).Values.IntValue1 == 1 && items.TestState(entity).Values.IntValue2 == 1 &&
                    items.TestState(entity).Lease.Owner == ItemLease.Unowned && plugin.Session!.LocalPeerId != originalPeer,
                    "Reconnect lost fields or released ownership; " + Details(items));
                Visual(items, true); return true;
            }
            return false;
        }
        private bool Host(SharedItems items, ushort prefab)
        {
            if (phase == 0)
            {
                spawned = items.TestSpawn(gameplay.PlayerWalking.transform.position + new Vector3(1, 1, 1), prefab);
                spawned.transform.localScale = Vector3.one * 0.77f;
                phase = 1; nextAction = Time.unscaledTime + 1.3f;
            }
            else if (phase == 1)
            {
                entity = items.TestNewestPrefab(prefab);
                Require(items.TestItem(entity) == spawned, "Spawn was not registered.");
                MoveNear(spawned!.transform.position); items.TestUse(entity);
                Require(items.TestState(entity).Values.IntValue1 == 1, "Native host toggle was not committed.");
                Visual(items, true); phase = 2;
            }
            else if (phase == 2 && items.TestState(entity).Lease.Owner != ItemLease.Unowned)
            {
                Require(items.TestState(entity).Values.IntValue1 == 0 && items.TestState(entity).Values.IntValue2 == 1, "Guest toggle/pickup fields did not reach host.");
                uint owner = items.TestState(entity).Lease.Owner;
                items.TestUse(entity); items.TestGrab(entity);
                Require(items.TestState(entity).Values.IntValue1 == 0 && items.TestState(entity).Lease.Owner == owner, "Host changed a guest-owned lantern.");
                phase = 3;
            }
            else if (phase == 3 && plugin.Session!.Peers.Count == 1)
            {
                Require(items.TestState(entity).Values.IntValue1 == 1 && items.TestState(entity).Values.IntValue2 == 1 &&
                    items.TestState(entity).Lease.Owner == ItemLease.Unowned && !items.TestItem(entity)!.rb.isKinematic,
                    "Held toggle or disconnect takeover failed.");
                Visual(items, true); phase = 4;
            }
            else if (phase == 4 && plugin.Session!.Peers.Count == 2) return true;
            return false;
        }
        private void Visual(SharedItems items, bool on)
        {
            var lantern = (Item_Lantern)items.TestItem(entity)!;
            Require(lantern.LightSource.On == on && lantern.LightSource.lightSources.Count > 0, "Native light flag/source missing.");
            foreach (var source in lantern.LightSource.lightSources)
            {
                Require(source.IsOn == on, "Native source state differs.");
                if (source.type == LightSource.LightType.Light) Require(source.light != null && source.light.enabled == on, "Native light visibility differs.");
                else Require(source.meshRenderer != null && source.meshRenderer.materials[source.rendererMaterialIndex].IsKeywordEnabled("_EMISSION") == on, "Native emission differs.");
            }
        }
        private void MoveNear(Vector3 item)
        {
            var location = item + new Vector3(0, -0.8f, 1);
            gameplay.PlayerWalking.transform.position = location;
            gameplay.Get_Player_Rigidbody.position = location; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private string Details(SharedItems items)
        {
            var state = items.TestState(entity);
            var local = gameplay.PlayerWalking.transform.position;
            var item = items.TestItem(entity)!.transform.position;
            return "local=" + local.ToString("F2") + "; item=" + item.ToString("F2") +
                "; distance=" + Vector3.Distance(local, item).ToString("F2") + "; playerState=" + gameplay.Get_PlayerState +
                "; light=" + state.Values.IntValue1 + "; grabbed=" + state.Values.IntValue2 +
                "; owner=" + state.Lease.Owner + "; generation=" + state.Lease.Generation;
        }
        private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException("Paired lantern test: " + reason); }
    }
}
