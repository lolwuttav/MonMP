using System;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // SugarShack: host production and guest controls, then all seven syrup-federation upgrades
    // purchased through the guest UI with host cash/progression and outdoor-pump item handoff.
    internal sealed class PairedSugarShackTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private static readonly ShackUpgrades[] MoreUpgrades = { ShackUpgrades.WaterTank, ShackUpgrades.WaterTank50k,
            ShackUpgrades.Siroptier, ShackUpgrades.Crimping, ShackUpgrades.Heater };
        private int phase, tick = 2000;
        private int purchaseIndex;
        private float next, deadline;
        private int cashBefore;
        private bool activationLogged, cheatRepairChecked;
        internal string Status
        {
            get
            {
                var shack = plugin.World?.SugarShack;
                if (shack == null || shack.TestShack == null) return "shack phase=" + phase + "; no shack";
                var s = shack.TestState;
                return "shack phase=" + phase + "; purchase=" + purchaseIndex + "; record=" + s.Flags + "/" + s.Upgrades + " tank" + s.Tank + " evap" + s.Evaporator + " syrup" + s.Syrup +
                    " temp" + s.Temperature.ToString("0.0") + " fire" + s.Fire.ToString("0.0") + " hostTicks=" + shack.TestHostTicks;
            }
        }
        internal PairedSugarShackTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            Debug.Log("MONMP_SUGAR_ACTIVATION_BASELINE: role=" + (plugin.TestOptions.IsHost ? "host" : "guest") + "; " + plugin.World!.SugarShack.TestActivationDiagnostic);
        }
        internal bool Tick()
        {
            var world = plugin.World!;
            if (!world.Items.Ready) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 210;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Sugar shack test stalled: " + Status);
            if (Time.unscaledTime < next) return false;
            Require(world.SugarShack.TestShack != null, "the scene has no sugar shack.");
            return plugin.TestOptions.IsHost ? Host(world.SugarShack) : Guest(world.SugarShack);
        }
        private bool Host(SharedSugarShack shared)
        {
            var shack = shared.TestShack!;
            // Syrup needs power: keep the generator running for the test.
            var generator = UnityEngine.Object.FindObjectOfType<HomeGenerator>();
            if (generator != null) AccessTools.Field(typeof(HomeGenerator), "isEngineRunning").SetValue(generator, true);
            var s = shared.TestState;
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                shared.TestBuild();
                var todo = UnityEngine.Object.FindObjectOfType<ToDoList_Manager>();
                todo.SugarshackUpgrade(ToDoList_Line.Sugarshack_SugarshackIsBuild);
                todo.SugarshackUpgrade(ToDoList_Line.Intermediate_HydroCompleted);
                var cash = UnityEngine.Object.FindObjectOfType<Cash_Manager>();
                AccessTools.Field(typeof(Cash_Manager), "cash").SetValue(cash, 10000000);
                cashBefore = cash.GetCash;
                var federation = (Store_FederationSyrup)AccessTools.Field(typeof(ItemForSale_SugarShack), "store").GetValue(shared.TestSale(ShackUpgrades.ReverseOsmosis));
                federation.isOpen = true;
                shack.ElectricAutoHeater_Bought();
                if (shack.Get_Valve_TankToEvap) shack.Get_Valve_TankToEvap = false;
                shared.TestLevels(2000, 0, 0);
                MoveFar(shack.transform);
                Debug.Log("MONMP_SUGAR_HOST_FAR: " + shared.TestActivationDiagnostic);
                phase = 1; next = Time.unscaledTime + 1;
            }
            else if (phase == 1 && s.Has(ShackFlags.ValveEvap)) phase = 2;
            else if (phase == 2)
            {
                Require(Vector3.Distance(gameplay.PlayerWalking.transform.position, shack.transform.position) >= 150,
                    "Host moved near the sugar shack during guest-only production.");
                if (!activationLogged)
                { activationLogged = true; Debug.Log("MONMP_SUGAR_GUEST_ONLY_ACTIVATION: " + shared.TestActivationDiagnostic); }
                // The minute tick, faster than the clock; the guest's fire arrives as heat between ticks.
                if (s.Syrup >= 2) { phase = 3; return false; }
                shared.TestTick(tick += 2); next = Time.unscaledTime + 0.6f;
            }
            else if (phase == 3 && !s.Has(ShackFlags.ValveEvap)) phase = 4;
            else if (phase == 4 && s.Has(ShackFlags.ValveSyrup)) phase = 5;
            else if (phase == 5 && !s.Has(ShackFlags.ValveSyrup)) phase = 6;
            else if (phase == 6 && s.Has(ShackFlags.Heater)) phase = 7;
            else if (phase == 7 && !s.Has(ShackFlags.Heater)) phase = 8;
            else if (phase == 8 && s.Has(ShackUpgrades.ReverseOsmosis))
            {
                Require(UnityEngine.Object.FindObjectOfType<Cash_Manager>().GetCash == cashBefore - Mathf.RoundToInt(shared.TestSalePrice(ShackUpgrades.ReverseOsmosis) * 100f),
                    "Guest upgrade did not debit the host exactly once.");
                phase = 9;
            }
            else if (phase == 9 && s.Has(ShackUpgrades.OutdoorPump))
            {
                Require(UnityEngine.Object.FindObjectOfType<Cash_Manager>().GetCash == cashBefore -
                    Mathf.RoundToInt(shared.TestSalePrice(ShackUpgrades.ReverseOsmosis) * 100f) -
                    Mathf.RoundToInt(shared.TestSalePrice(ShackUpgrades.OutdoorPump) * 100f),
                    "Guest upgrade purchases did not debit the host exactly once each.");
                phase = 10;
            }
            else if (phase == 10 && purchaseIndex < MoreUpgrades.Length && s.Has(MoreUpgrades[purchaseIndex]) &&
                UnityEngine.Object.FindObjectOfType<ToDoList_Manager>().isDone(UpgradeLine(MoreUpgrades[purchaseIndex])))
            {
                int spent = PriceCents(shared, ShackUpgrades.ReverseOsmosis) + PriceCents(shared, ShackUpgrades.OutdoorPump);
                for (int i = 0; i <= purchaseIndex; i++) spent += PriceCents(shared, MoreUpgrades[i]);
                Require(UnityEngine.Object.FindObjectOfType<Cash_Manager>().GetCash == cashBefore - spent,
                    "Upgrade " + MoreUpgrades[purchaseIndex] + " was not debited exactly once.");
                purchaseIndex++;
                if (purchaseIndex == MoreUpgrades.Length) phase = 11;
            }
            else if (phase == 11 && plugin.Session!.Peers.Count == 1)
            {
                if (!cheatRepairChecked) { CheckCheatRepair(shared); cheatRepairChecked = true; }
                return true;
            }
            return false;
        }
        private bool Guest(SharedSugarShack shared)
        {
            var shack = shared.TestShack!;
            var s = shared.TestState;
            if (phase == 0)
            {
                if (plugin.World?.PersonalProfileReady != true || !plugin.World.Channel.FullySynchronized ||
                    plugin.World.ReconnectPositions?.Settled != true) return false;
                if (!s.Has(ShackFlags.Built) || !s.Has(ShackUpgrades.Heater) || s.Tank != 2000) return false;
                Require(shack.gameObject.activeInHierarchy && shared.TestLocal.Has(ShackUpgrades.Heater), "the guest did not follow the host's build and heater purchase.");
                Require(shack.Get_WaterLevel_Tank == 2000 && shack.Get_WaterLevel_Evaporator == 0, "the guest's levels do not match the host record.");
                MoveNear(shack.transform);
                phase = 1; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 1)
            {
                Require(Vector3.Distance(gameplay.PlayerWalking.transform.position, shack.transform.position) <= 30f,
                    "Guest approach was overwritten before its first valve click; " + shared.TestActivationDiagnostic);
                shared.TestWaterValve();
                Require(!shack.Get_Valve_TankToEvap, "the guest's valve opened locally.");
                phase = 2;
            }
            else if (phase == 2 && s.Has(ShackFlags.ValveEvap) && shack.Get_Valve_TankToEvap) phase = 3;
            else if (phase == 3)
            {
                if (s.Syrup >= 2)
                {
                    Require(shack.Get_SyrupLevel == s.Syrup && shack.Get_WaterLevel_Evaporator == s.Evaporator && shack.Get_WaterLevel_Tank == s.Tank, "the guest's levels do not follow the host.");
                    Require(Mathf.Abs(shared.TestGauge - Mathf.Clamp(s.Temperature, 0, shared.TestGaugeMax)) < 0.01f && s.Temperature >= 90 && shack.Get_isEvaporatorBoiling, "the guest's evaporator gauge or boiling state does not follow the host.");
                    phase = 4; next = Time.unscaledTime + 1;
                    return false;
                }
                // Wood burning in the guest's evaporator.
                shack.WoodBurningInEvap(3f); next = Time.unscaledTime + 0.2f;
            }
            else if (phase == 4) { shared.TestWaterValve(); phase = 5; }
            else if (phase == 5 && !s.Has(ShackFlags.ValveEvap) && !shack.Get_Valve_TankToEvap) { shared.TestSyrupValve(); phase = 6; }
            else if (phase == 6 && s.Has(ShackFlags.ValveSyrup) && shared.TestSyrupValveOpen) { phase = 7; next = Time.unscaledTime + 0.5f; }
            else if (phase == 7) { shared.TestSyrupValve(); phase = 8; }
            else if (phase == 8 && !s.Has(ShackFlags.ValveSyrup) && !shared.TestSyrupValveOpen) { shared.TestHeaterSwitch.MouseDown(); phase = 9; }
            else if (phase == 9 && s.Has(ShackFlags.Heater) && shack.Get_isAutoElectricHeaterOn && shared.TestHeaterSwitch.isOn) { phase = 10; next = Time.unscaledTime + 0.5f; }
            else if (phase == 10) { shared.TestHeaterSwitch.MouseDown(); phase = 11; }
            else if (phase == 11 && !s.Has(ShackFlags.Heater) && !shack.Get_isAutoElectricHeaterOn && !shared.TestHeaterSwitch.isOn)
            {
                var sale = shared.TestSale(ShackUpgrades.ReverseOsmosis);
                var location = sale.transform.position + Vector3.back * 0.8f + Vector3.up * 0.5f;
                gameplay.PlayerWalking.transform.position = location; gameplay.Get_Player_Rigidbody.position = location; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                phase = 12; next = Time.unscaledTime + 1;
            }
            else if (phase == 12)
            {
                shared.TestSale(ShackUpgrades.ReverseOsmosis).MouseDown();
                phase = 13;
            }
            else if (phase == 13 && s.Has(ShackUpgrades.ReverseOsmosis) && shared.TestLastPurchase?.Status == PurchaseStatus.Placed &&
                UnityEngine.Object.FindObjectOfType<ToDoList_Manager>().isDone(ToDoList_Line.SugarShack_ReverseOsmosis))
            {
                shared.TestSale(ShackUpgrades.ReverseOsmosis).MouseDown();
                phase = 14;
            }
            else if (phase == 14 && shared.TestLastPurchase?.Status == PurchaseStatus.Unavailable)
            {
                var sale = shared.TestSale(ShackUpgrades.OutdoorPump);
                var location = sale.transform.position + Vector3.back * 0.8f + Vector3.up * 0.5f;
                gameplay.PlayerWalking.transform.position = location; gameplay.Get_Player_Rigidbody.position = location; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                phase = 15; next = Time.unscaledTime + 1;
            }
            else if (phase == 15)
            {
                shared.TestSale(ShackUpgrades.OutdoorPump).MouseDown();
                phase = 16;
            }
            else if (phase == 16 && s.Has(ShackUpgrades.OutdoorPump) && shared.TestLastPurchase?.Status == PurchaseStatus.Held &&
                gameplay.Player.Get_HoldingItem is Part_Spoiler_BedTank)
            {
                plugin.Items!.TestDrop(false);
                phase = 17; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 17 && gameplay.Player.Get_HoldingItem == null)
            {
                if (purchaseIndex == MoreUpgrades.Length)
                {
                    Require(shared.TestLateReplySettles(), "a conclusive late purchase reply did not reopen the save gate.");
                    plugin.EndPairedTestSession();
                    Require(shared.TestStructureRestored, "disconnect did not restore the guest's original shack and upgrade structure.");
                    return true;
                }
                var sale = shared.TestSale(MoreUpgrades[purchaseIndex]);
                var location = sale.transform.position + Vector3.back * 0.8f + Vector3.up * 0.5f;
                gameplay.PlayerWalking.transform.position = location; gameplay.Get_Player_Rigidbody.position = location; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                phase = 18; next = Time.unscaledTime + 0.7f;
            }
            else if (phase == 18)
            {
                shared.TestSale(MoreUpgrades[purchaseIndex]).MouseDown();
                phase = 19;
            }
            else if (phase == 19 && s.Has(MoreUpgrades[purchaseIndex]) && shared.TestLastPurchase?.Status == PurchaseStatus.Placed &&
                UnityEngine.Object.FindObjectOfType<ToDoList_Manager>().isDone(UpgradeLine(MoreUpgrades[purchaseIndex])))
            {
                purchaseIndex++; phase = 17; next = Time.unscaledTime + 0.3f;
            }
            return false;
        }
        private void MoveNear(Transform target)
        {
            for (float radius = 4; radius <= 16; radius += 2)
                for (int step = 0; step < 16; step++)
                {
                    float angle = step * Mathf.PI / 8;
                    var at = target.position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                    if (!Physics.Raycast(at + Vector3.up * 30, Vector3.down, out var hit, 60, ~0, QueryTriggerInteraction.Ignore)) continue;
                    at = hit.point + Vector3.up;
                    if (Physics.OverlapCapsule(at, at + Vector3.up, 0.4f, ~0, QueryTriggerInteraction.Ignore).Any(c => !(c is TerrainCollider))) continue;
                    gameplay.PlayerWalking.transform.position = at; gameplay.Get_Player_Rigidbody.position = at; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                    return;
                }
            throw new InvalidOperationException("Sugar shack test: no clear standing point near the shack.");
        }
        private void MoveFar(Transform target)
        {
            if (Vector3.Distance(gameplay.PlayerWalking.transform.position, target.position) >= 150) return;
            for (float radius = 180; radius <= 300; radius += 30)
                for (int step = 0; step < 16; step++)
                {
                    float angle = step * Mathf.PI / 8;
                    var at = target.position + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                    if (!Physics.Raycast(at + Vector3.up * 100, Vector3.down, out var hit, 200, ~0, QueryTriggerInteraction.Ignore) || !(hit.collider is TerrainCollider)) continue;
                    at = hit.point + Vector3.up;
                    if (Physics.OverlapCapsule(at, at + Vector3.up, 0.4f, ~0, QueryTriggerInteraction.Ignore).Any(c => !(c is TerrainCollider))) continue;
                    gameplay.PlayerWalking.transform.position = at; gameplay.Get_Player_Rigidbody.position = at; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
                    return;
                }
            throw new InvalidOperationException("Sugar shack test: no safe standing point at least 150m away.");
        }
        private void CheckCheatRepair(SharedSugarShack shared)
        {
            // The plugin does not depend on the optional debug cheats assembly. When supplied by
            // the isolated launcher, exercise its actual route against a completed-but-uninstalled
            // native heater, including idempotence. Ordinary suites still prove native purchases.
            var type = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "MonMP.Cheats")
                ?.GetType("MonMP.Cheats.Progression");
            if (type == null) { Debug.Log("MONMP_SUGAR_CHEAT_REPAIR_NOT_EXERCISED: optional MonMP.Cheats assembly was not loaded."); return; }
            var complete = AccessTools.Method(type, "Complete", new[] { typeof(Gameplay), typeof(ToDoList_Line) });
            var heater = (GameObject)AccessTools.Field(typeof(SugarShack), "ElectricAutoHeater").GetValue(shared.TestShack);
            var todo = UnityEngine.Object.FindObjectOfType<ToDoList_Manager>();
            Require(todo.isDone(ToDoList_Line.SugarShack_ElectricAutoHeater), "Native heater checklist prerequisite missing at cheat repair check.");
            heater.SetActive(false);
            string result = (string)complete.Invoke(null, new object[] { gameplay, ToDoList_Line.SugarShack_ElectricAutoHeater });
            Require(heater.activeSelf && shared.TestLocal.Has(ShackUpgrades.Heater), "Completed-only cheat heater did not repair its native equipment: " + result);
            complete.Invoke(null, new object[] { gameplay, ToDoList_Line.SugarShack_ElectricAutoHeater });
            Require(heater.activeSelf && shared.TestLocal.Has(ShackUpgrades.Heater), "Repeated native cheat upgrade lost equipment.");
            Debug.Log("MONMP_SUGAR_CHEAT_NATIVE_REPAIR: completed checklist repaired missing heater through native route; repeat remained installed.");
        }
        private static int PriceCents(SharedSugarShack shared, ShackUpgrades upgrade) => Mathf.RoundToInt(shared.TestSalePrice(upgrade) * 100f);
        private static ToDoList_Line UpgradeLine(ShackUpgrades upgrade) => upgrade switch
        {
            ShackUpgrades.WaterTank => ToDoList_Line.SugarShack_WaterTank,
            ShackUpgrades.WaterTank50k => ToDoList_Line.SugarShack_WaterTank50k,
            ShackUpgrades.Siroptier => ToDoList_Line.SugarShack_Siroptier,
            ShackUpgrades.Crimping => ToDoList_Line.SugarShack_CrimpingMachine,
            ShackUpgrades.Heater => ToDoList_Line.SugarShack_ElectricAutoHeater,
            _ => throw new InvalidOperationException("Unsupported fixture upgrade: " + upgrade)
        };
        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException("Sugar shack test: " + reason); }
    }
}
