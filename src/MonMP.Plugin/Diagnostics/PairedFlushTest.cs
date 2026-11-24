using System;
using System.Linq;
using MonMP.Core.World;
using UnityEngine;
using UniStorm;

namespace MonMP.Plugin.Diagnostics
{
    // Explicit isolated fixture: currency values are stage markers, never production save messages.
    internal sealed class PairedFlushTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private int phase, ack;
        private float next;
        private ulong item;
        public string Status => "ownership flush phase " + phase + "; ack=" + ack;
        internal static void Prepare() => UniStormSystem.Instance.TimeFlow = UniStormSystem.Instance.RealWorldTime = UniStormSystem.EnableFeature.Disabled;
        internal PairedFlushTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            if (!plugin.TestOptions.IsHost) return;
            plugin.World!.Channel.CommandRequested += Acknowledge;
            foreach (float scale in new[] { 0.62f, 0.63f })
            {
                var spawned = plugin.Items!.TestSpawn(gameplay.PlayerWalking.transform.position + Vector3.forward + Vector3.up);
                spawned.transform.localScale = Vector3.one * scale;
            }
        }
        private void Acknowledge(uint peer, ulong entity, byte[] payload)
        { if (peer != 0 && entity == ClockState.Entity && payload.Length == 1) ack = Math.Max(ack, payload[0]); }
        private void Signal(byte stage) => plugin.World!.Channel.Request(ClockState.Entity, new[] { stage });
        private void Mark(int stage) => UnityEngine.Object.FindObjectOfType<Cash_Manager>().LoadSaveSystem(1000 + stage, new float[5]);
        private bool Marked(int stage) => plugin.World!.Economy.TestState.CashCents == 1000 + stage;
        private void MoveTo(Vector3 p)
        { gameplay.PlayerWalking.transform.position = p; gameplay.Get_Player_Rigidbody.position = p; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("Ownership flush: " + message); }
        internal bool Tick()
        {
            var world = plugin.World;
            if (Time.unscaledTime < next || world?.Channel.FullySynchronized != true) return false;
            var items = world.Items; var vehicles = world.Vehicles;
            if (plugin.TestOptions.IsHost)
            {
                if (phase == 0)
                {
                    item = items.TestTaggedItem(1, 0.63f); if (item == 0) return false;
                    items.TestGrab(item); Require(items.PeerHolding(0), "Host fixture could not acquire item."); phase = 1;
                }
                else if (phase == 1 && ack >= 1)
                {
                    Require(world.Owners.Length == 2 && !world.LocalFlushComplete, "Owner inventory missed a native holder.");
                    world.BlockOwnershipTransfers(true); world.FlushLocalOwnership();
                    Require(world.LocalFlushComplete, "Host held item did not flush synchronously."); Mark(1); phase = 2;
                }
                else if (phase == 2 && ack >= 2)
                {
                    Require(world.Owners.Length == 0, "Guest flush or blocked pickup failed.");
                    Require(!items.TestItem(item)!.rb.isKinematic, "Host drop did not restore physics.");
                    items.TestGrab(item); Require(!items.PeerHolding(0), "Frozen host acquired item.");
                    world.BlockOwnershipTransfers(false); Mark(2); phase = 3;
                }
                else if (phase == 3 && ack >= 3)
                { Require(vehicles.Owners.Length == 1, "Guest driver absent."); world.BlockOwnershipTransfers(true); Mark(3); phase = 4; }
                else if (phase == 4 && ack >= 4)
                {
                    Require(world.Owners.Length == 0 && Math.Abs(vehicles.TestState.Fuel - 7.25f) < 0.001f, "Exit lost final fuel or frozen driver entry succeeded.");
                    Require(!vehicles.TestVehicle.vehicleRigidbody.isKinematic, "Host did not resume vehicle simulation.");
                    world.BlockOwnershipTransfers(false); Mark(4); phase = 5;
                }
                else if (phase == 5 && ack >= 5 && Math.Abs(vehicles.TestState.Fuel - 6.5f) < 0.001f)
                {
                    var stale = vehicles.TestState; Require(stale.Driver.Owner != ItemLease.Unowned, "Revoke fixture has no driver.");
                    world.BlockOwnershipTransfers(true); world.RevokeOwnership(stale.Driver.Owner);
                    vehicles.TestStale(stale.Driver.Owner, stale);
                    Require(world.Owners.Length == 0 && Math.Abs(vehicles.TestState.Fuel - 6.5f) < 0.001f, "Revocation lost accepted state or stale owner was accepted.");
                    Mark(5); phase = 6;
                }
                else if (phase == 6 && ack >= 6)
                { world.BlockOwnershipTransfers(false); Mark(6); world.Channel.CommandRequested -= Acknowledge; return true; }
                return false;
            }
            if (phase == 0)
            {
                item = items.TestTaggedItem(1, 0.62f); if (item == 0) return false;
                MoveTo(items.TestItem(item)!.transform.position + Vector3.right); phase = 1; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 1) { items.TestGrab(item); phase = 2; }
            else if (phase == 2 && items.PeerHolding(plugin.Session!.LocalPeerId)) { Signal(1); phase = 3; }
            else if (phase == 3 && Marked(1))
            { world.BlockOwnershipTransfers(true); Require(!world.LocalFlushComplete, "Held item incorrectly reported flushed."); world.FlushLocalOwnership(); phase = 4; }
            else if (phase == 4 && world.LocalFlushComplete)
            {
                // Bypass the local gate to exercise host authority against an unfrozen sender.
                items.TestCommand(item, ItemAction.Acquire, items.TestState(item).Lease.Generation);
                phase = 5; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 5) { Require(world.LocalFlushComplete, "Frozen host accepted raw pickup."); Signal(2); phase = 6; }
            else if (phase == 6 && Marked(2))
            { world.BlockOwnershipTransfers(false); MoveTo(vehicles.TestSeat + Vector3.right * 1.5f); phase = 7; next = Time.unscaledTime + 0.7f; }
            else if (phase == 7) { vehicles.TestEnter(); phase = 8; }
            else if (phase == 8 && vehicles.TestState.Driver.Owner == plugin.Session!.LocalPeerId) { Signal(3); phase = 9; }
            else if (phase == 9 && Marked(3))
            {
                world.BlockOwnershipTransfers(true); Require(!world.LocalFlushComplete, "Seated driver incorrectly reported flushed.");
                vehicles.TestVehicle.vehicle_PartsManager.Fuel_Tank.Get_FuelLevel = 7.25f;
                world.FlushLocalOwnership(); phase = 10;
            }
            else if (phase == 10 && world.LocalFlushComplete)
            {
                MoveTo(vehicles.TestSeat + Vector3.right * 1.5f); phase = 11; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 11)
            {
                world.Channel.Request(vehicles.TestId, new VehicleCommand(VehicleAction.Enter, vehicles.TestState).Encode(), true);
                phase = 12; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 12) { Require(world.LocalFlushComplete, "Frozen host accepted raw driver entry."); Signal(4); phase = 13; }
            else if (phase == 13 && Marked(4)) { world.BlockOwnershipTransfers(false); vehicles.TestEnter(); phase = 14; }
            else if (phase == 14 && vehicles.TestState.Driver.Owner == plugin.Session!.LocalPeerId)
            { vehicles.TestVehicle.vehicle_PartsManager.Fuel_Tank.Get_FuelLevel = 6.5f; phase = 15; next = Time.unscaledTime + 0.3f; }
            else if (phase == 15) { Signal(5); phase = 16; }
            else if (phase == 16 && Marked(5) && world.LocalFlushComplete)
            { Require(vehicles.TestVehicle.vehicleRigidbody.isKinematic, "Revoked guest retained physics."); Signal(6); phase = 17; }
            else if (phase == 17 && Marked(6)) return true;
            return false;
        }
    }
}
