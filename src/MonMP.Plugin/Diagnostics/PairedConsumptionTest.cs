using System;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedConsumptionTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly BasicNeeds_Manager needs;
        private int phase;
        private float nextAction;
        private ulong syrup, coffee;
        private uint rejectedGeneration;
        private uint originalGuest;
        private float syrupStats, coffeeStats;
        private bool initialized;
        public string Status => "consumption phase " + phase + "; syrupStats=" + GameManager.Get_PlayerStats[1] +
            "; coffeeStats=" + GameManager.Get_PlayerStats[2] + "; thirst=" + needs.GetMappleSyrup +
            "; syrup=" + (syrup != 0 && plugin.Items?.Ready == true ? plugin.Items.TestPickupStatus(syrup) : "pending");
        public PairedConsumptionTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            needs = UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>();
            if (needs == null) throw new InvalidOperationException("Missing native needs manager.");
        }
        private void InitializeNeeds()
        {
            // Only the explicit isolated test changes these values; no normal save is loaded.
            needs.GetMappleSyrup = 0.85f; needs.GetEnergy = 0.8f; needs.GetAnxiety = 0.1f;
            AccessTools.Field(typeof(BasicNeeds_Manager), "EnergyDrinkInBlood").SetValue(needs, 0);
            GameManager.Get_PlayerStats[1] = 0; GameManager.Get_PlayerStats[2] = 0;
        }
        public bool Tick()
        {
            if (Time.unscaledTime < nextAction) return false;
            var items = plugin.Items;
            if (phase == 7) { plugin.BeginPairedTestSession(); phase = 8; return false; }
            if (items?.Ready != true || plugin.World?.PersonalProfileReady != true) return false;
            // Full enters this fixture immediately after the decoration reconnect. The
            // saved profile and position must settle before we set needs or approach food.
            if (!plugin.TestOptions.IsHost && plugin.World.ReconnectPositions?.TestSettled != true) return false;
            if (!initialized) { InitializeNeeds(); initialized = true; }
            if (plugin.TestOptions.IsHost) return Host(items);
            if (phase == 0)
            {
                syrup = items.TestNewestPrefab(1); coffee = items.TestNewestPrefab(2);
                if (syrup == 0 || coffee == 0 || Math.Abs(items.TestState(syrup).ScaleX - 0.81f) > 0.001f ||
                    Math.Abs(items.TestState(coffee).ScaleX - 0.82f) > 0.001f) return false;
                MoveNear(items.TestItem(syrup)!.transform.position); phase = 1; nextAction = Time.unscaledTime + 0.7f;
            }
            else if (phase == 1)
            {
                Require(Vector3.Distance(gameplay.PlayerWalking.transform.position, items.TestItem(syrup)!.transform.position) <= 4.5f,
                    "Guest left the syrup interaction point before use: " + Status);
                items.TestUse(syrup); items.TestUse(syrup); phase = 2;
            }
            // The host already drank one bottle; the guest contributes the second
            // bottle to the same session total while receiving its own needs effect.
            else if (phase == 2 && !items.TestState(syrup).Exists && items.TestAcknowledged(syrup) && GameManager.Get_PlayerStats[1] == 2)
            {
                Require(needs.GetMappleSyrup < 0.25f, "Syrup did not affect the consuming guest's thirst.");
                items.TestReplay(syrup);
                Require(GameManager.Get_PlayerStats[1] == 2, "Repeated syrup receipt applied another effect.");
                needs.GetAnxiety = 0.95f;
                items.TestUse(coffee);
                Require(items.TestState(coffee).Exists && GameManager.Get_PlayerStats[2] == 0, "Coffee preflight failed to reject excessive anxiety.");
                MoveNear(items.TestItem(coffee)!.transform.position); phase = 3; nextAction = Time.unscaledTime + 0.7f;
            }
            else if (phase == 3)
            {
                rejectedGeneration = items.TestState(coffee).Lease.Generation;
                // Simulate eligibility becoming invalid while an already-sent request travels.
                items.TestRequestConsume(coffee); phase = 4;
            }
            else if (phase == 4 && items.TestState(coffee).Exists && items.TestState(coffee).Lease.Generation != rejectedGeneration)
            {
                Require(items.TestItem(coffee) != null && GameManager.Get_PlayerStats[2] == 0, "Rejected coffee consumption lost the item or applied an effect.");
                needs.GetAnxiety = 0.1f;
                MoveNear(items.TestItem(coffee)!.transform.position); phase = 5; nextAction = Time.unscaledTime + 0.7f;
            }
            else if (phase == 5)
            {
                if (items.TestState(coffee).Lease.Owner != plugin.Session!.LocalPeerId)
                { items.TestGrab(coffee); nextAction = Time.unscaledTime + 0.3f; return false; }
                items.TestWithholdConsumptionAcknowledgment = true;
                items.TestUse(coffee); items.TestUse(coffee); phase = 6;
            }
            else if (phase == 6 && !items.TestState(coffee).Exists && GameManager.Get_PlayerStats[2] == 1)
            {
                Require(!items.TestAcknowledged(coffee), "Coffee was acknowledged before the simulated lost acknowledgment.");
                Require(needs.GetEnergy < 0.75f && gameplay.Player.Get_HoldingItem == null, "Coffee did not affect guest energy or clear the held item.");
                items.TestReplay(coffee);
                Require(GameManager.Get_PlayerStats[2] == 1, "Repeated coffee receipt applied another effect.");
                syrupStats = GameManager.Get_PlayerStats[1]; coffeeStats = GameManager.Get_PlayerStats[2];
                // This fixture cuts the session directly to withhold the item receipt
                // acknowledgement. Let the independent shared-stat contribution settle
                // first, as normal player disconnect already does.
                phase = 60; nextAction = Time.unscaledTime + 0.3f;
            }
            else if (phase == 60)
            {
                plugin.World!.Statistics.Update();
                if (!plugin.World.Statistics.LocalFlushComplete) return false;
                plugin.EndPairedTestSession(); Require(items.TestRestored, "Consumption disconnect did not restore original guest objects.");
                phase = 7; nextAction = Time.unscaledTime + 1;
            }
            else if (phase == 8)
            {
                Require(!items.TestState(syrup).Exists && !items.TestState(coffee).Exists, "Reconnect resurrected a consumed item.");
                Require(GameManager.Get_PlayerStats[1] == syrupStats && GameManager.Get_PlayerStats[2] == coffeeStats,
                    "Reconnect changed consumed-effect statistics: " + GameManager.Get_PlayerStats[1] + "/" + GameManager.Get_PlayerStats[2] + " expected " + syrupStats + "/" + coffeeStats);
                if (!items.TestAcknowledged(coffee)) return false;
                phase = 9; nextAction = Time.unscaledTime + 0.5f;
            }
            else if (phase == 9) return true;
            return false;
        }
        private bool Host(SharedItems items)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                foreach (var peer in plugin.Session!.Peers.Keys) if (peer != 0) originalGuest = peer;
                var item = items.TestFirstPrefab(1);
                MoveNear(items.TestItem(item)!.transform.position);
                items.TestUse(item);
                Require(!items.TestState(item).Exists && GameManager.Get_PlayerStats[1] == 1 && needs.GetMappleSyrup < 0.25f,
                    "Host's native use path did not consume a syrup bottle.");
                items.TestReplay(item); Require(GameManager.Get_PlayerStats[1] == 1, "Host receipt replay duplicated an effect.");
                syrupStats = GameManager.Get_PlayerStats[1]; coffeeStats = GameManager.Get_PlayerStats[2];
                var point = gameplay.PlayerWalking.transform.position;
                items.TestSpawn(point + new Vector3(1, 1.5f, 0)).transform.localScale = Vector3.one * 0.81f;
                items.TestSpawn(point + new Vector3(2, 1.5f, 0), 2).transform.localScale = Vector3.one * 0.82f;
                phase = 1; nextAction = Time.unscaledTime + 1.2f;
            }
            else if (phase == 1)
            {
                // Include tombstones: the guest can consume a fresh item before this tick.
                syrup = items.TestNewestPrefab(1); coffee = items.TestNewestPrefab(2); phase = 2;
            }
            else if (phase == 2 && !items.TestState(syrup).Exists && !items.TestState(coffee).Exists && items.TestAcknowledged(syrup) && items.TestAcknowledged(coffee))
            {
                Require(items.TestRejectedConsumptions == 1, "Native coffee rejection did not restore the canonical item exactly once.");
                NativeRegistryTest.Verify(plugin.World!);
                // Contributions can follow the consumption acknowledgement. Wait
                // for both, then require exactly one shared increment per drink.
                if (GameManager.Get_PlayerStats[1] < syrupStats + 1 || GameManager.Get_PlayerStats[2] < coffeeStats + 1) return false;
                Require(GameManager.Get_PlayerStats[1] == syrupStats + 1 && GameManager.Get_PlayerStats[2] == coffeeStats + 1,
                    "Guest consumption did not contribute exactly once to the shared statistics.");
                Require(needs.GetEnergy > 0.78f && needs.GetMappleSyrup > 0.15f, "Guest consumption changed host needs.");
                Require(!plugin.Session!.Peers.ContainsKey(originalGuest) && plugin.Session.Peers.Count == 2,
                    "Pending coffee acknowledgment did not survive reconnect under the same profile.");
                return true;
            }
            return false;
        }
        private void MoveNear(Vector3 item)
        {
            var location = item + new Vector3(0, -0.8f, 1);
            gameplay.PlayerWalking.transform.position = location; gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException("Paired consumption test: " + reason); }
    }
}
