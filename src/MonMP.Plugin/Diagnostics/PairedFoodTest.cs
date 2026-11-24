using System;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedFoodTest
    {
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly BasicNeeds_Manager needs;
        private int phase;
        private float next, anxiety;
        private ulong fresh, rotten, hostMeal;
        private uint originalPeer;
        private Furniture_Fryer? stagedFryer;
        private bool originalBasketVisible, fryerStaged, guestBasketChecked;
        private int originalFriendship;
        private float nextPress;
        public string Status => "food phase " + phase;
        public PairedFoodTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            if (!gameplay.Get_isHardMode) throw new InvalidOperationException("Food test requires isolated hard mode.");
            needs = UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>();
            needs.GetPoutine = 0.85f; needs.GetAnxiety = 0.1f; GameManager.Get_PlayerStats[0] = 0;
        }
        public bool Tick()
        {
            if (Time.unscaledTime < next) return false;
            if (!plugin.TestOptions.IsHost && phase == 6) { plugin.BeginPairedTestSession(); phase = 7; return false; }
            var items = plugin.Items; if (items?.Ready != true) return false;
            ushort prefab = items.TestPrefab("Food/Poutine"), homemade = items.TestPrefab("Food/HomemadePoutine");
            if (plugin.TestOptions.IsHost) return Host(items, prefab, homemade);
            if (phase == 0)
            {
                fresh = items.TestTaggedItem(prefab, 0.61f); rotten = items.TestTaggedItem(homemade, 0.62f);
                if (fresh == 0 || rotten == 0 || items.TestState(rotten).Values.Value1 != 0) return false;
                var prep = plugin.World!.FoodPrep;
                var fryer = prep.TestFryer;
                Require(fryer != null, "Shared fryer visibility fixture is missing.");
                if (!prep.TestState(fryer!).BasketVisible) return false;
                if (!guestBasketChecked)
                {
                    Require(SharedFoodPrep.TestBasketVisible(fryer!), "Host unlocked fryer basket remained hidden on guest.");
                    var before = prep.TestState(fryer!);
                    // Simulate the visibility reset performed by a late native Furniture_Fryer.Start.
                    SharedFoodPrep.TestSetBasketVisible(fryer!, false);
                    prep.Update();
                    Require(SharedFoodPrep.TestBasketVisible(fryer!) && prep.TestState(fryer!).Equals(before),
                        "Late native basket reset was not repaired without changing fryer stock/activity.");
                    guestBasketChecked = true;
                    Debug.Log("MonMP Food guest mirrored host basket unlock and repaired late Start visibility reset.");
                }
                if (prep.TestPressCount == 0) return false;
                Require(prep.TestPressSoundObserved && prep.TestPressStockPreserved,
                    "Native fryer press replay did not start its sound or mutated stock/activity.");
                Debug.Log("MonMP Food guest received native fryer press cue with sound and unchanged machine stock.");
                var food = (Item_Poutine)items.TestItem(fresh)!; var spoiled = (Item_Poutine)items.TestItem(rotten)!;
                Require(food.Get_IsRefrigerated && food.Value1 > 0.49f && food.Value1 < 0.5f, "Fresh/refrigerated host fields did not arrive.");
                Require(FoodState.Renderer(spoiled).material.color == new Color(0, 0.46f, 0.03f), "Spoiled native material was not reconstructed.");
                float life = food.Value1; TickFood(food); food.Get_IsRefrigerated = false;
                Require(food.Value1 == life && food.Get_IsRefrigerated, "Guest clock/fridge overwrote host food state.");
                Require(items.TestOriginalFoods.Length > 0, "Missing hidden native food fixture.");
                foreach (var original in items.TestOriginalFoods)
                {
                    life = original.Value1; bool cold = original.Get_IsRefrigerated;
                    TickFood(original); original.Get_IsRefrigerated = !cold;
                    Require(original.Value1 == life && original.Get_IsRefrigerated == cold, "Hidden guest food aged or changed refrigeration.");
                }
                MoveNear(food.transform.position); phase = 1; Delay(0.7f);
            }
            else if (phase == 1) { items.TestUse(fresh); items.TestUse(fresh); phase = 2; }
            else if (phase == 2 && !items.TestState(fresh).Exists && items.TestAcknowledged(fresh))
            {
                Require(GameManager.Get_PlayerStats[0] == 1 && needs.GetPoutine < 0.1f, "Fresh native nutrition/counter was not applied once.");
                Require(items.TestState(fresh).Values.Value1 > 0.49f && items.TestState(fresh).Values.Flags == ItemFlags.Refrigerated, "Consumption tombstone lost committed food fields.");
                items.TestReplay(fresh); Require(GameManager.Get_PlayerStats[0] == 1, "Fresh receipt replay duplicated effects.");
                needs.GetPoutine = 0.85f; needs.GetAnxiety = 0.1f;
                MoveNear(items.TestItem(rotten)!.transform.position); phase = 3; Delay(0.7f);
            }
            else if (phase == 3)
            { items.TestWithholdConsumptionAcknowledgment = true; items.TestUse(rotten); items.TestUse(rotten); phase = 4; }
            else if (phase == 4 && !items.TestState(rotten).Exists)
            {
                Require(items.TestState(rotten).Values.Value1 == 0 && !items.TestAcknowledged(rotten), "Spoiled receipt fixture did not retain its pending state.");
                Require(GameManager.Get_PlayerStats[0] == 1 && needs.GetAnxiety > 0.34f && needs.GetAnxiety < 0.36f && needs.GetPoutine > 0.79f && needs.GetPoutine < 0.82f,
                    "Spoiled native effect used fresh prefab defaults or ran more than once.");
                anxiety = needs.GetAnxiety; items.TestReplay(rotten);
                Require(needs.GetAnxiety == anxiety, "Spoiled replay duplicated sickness.");
                var prep = plugin.World!.FoodPrep;
                var fryer = prep.TestFryer!;
                bool originalVisibility = prep.TestOriginal(fryer).BasketVisible;
                originalPeer = plugin.Session!.LocalPeerId; plugin.EndPairedTestSession();
                Require(items.TestRestored, "Food disconnect failed to restore originals."); phase = 6; Delay(1);
                Require(SharedFoodPrep.TestBasketVisible(fryer) == originalVisibility, "Fryer basket visibility did not restore on disconnect.");
            }
            else if (phase == 7 && items.TestAcknowledged(rotten))
            {
                Require(plugin.Session!.LocalPeerId != originalPeer && !items.TestState(fresh).Exists && !items.TestState(rotten).Exists,
                    "Reconnect resurrected consumed food.");
                Require(GameManager.Get_PlayerStats[0] == 1 && Math.Abs(needs.GetAnxiety - anxiety) < 0.02f, "Reconnect repeated food effects.");
                Require(UnityEngine.Object.FindObjectsOfType<ConsumptionEffectRoot>(true).Length == 0, "Consumption effect clone leaked.");
                var asset = Resources.Load<GameObject>("Food/Poutine").GetComponent<Item_Poutine>();
                Require(asset.Value1 == 1 && !asset.Get_IsRefrigerated, "Native effect changed the resource prefab.");
                return true;
            }
            return false;
        }
        private bool Host(SharedItems items, ushort prefab, ushort homemade)
        {
            if (phase == 0)
            {
                stagedFryer = plugin.World!.FoodPrep.TestFryer;
                Require(stagedFryer != null, "Native fryer unlock fixture is missing.");
                originalBasketVisible = SharedFoodPrep.TestBasketVisible(stagedFryer!);
                originalFriendship = GameManager.Get_Friendship[12];
                fryerStaged = true;
                SharedFoodPrep.TestSetBasketVisible(stagedFryer!, false);
                GameManager.Get_Friendship[12] = Math.Max(50, originalFriendship);
                stagedFryer!.BasketUnlocked();
                Require(SharedFoodPrep.TestBasketVisible(stagedFryer), "Native host BasketUnlocked did not activate basket.");
                var point = gameplay.PlayerWalking.transform.position;
                Spawn(items, prefab, point + new Vector3(1, 1, 0), 0.61f, 0.5f, true);
                Spawn(items, homemade, point + new Vector3(2, 1, 0), 0.62f, 0.00001f, false);
                Spawn(items, prefab, point + new Vector3(3, 1, 0), 0.63f, 0.7f, false);
                phase = 1; Delay(1.5f);
            }
            else if (phase == 1)
            {
                fresh = items.TestTaggedItem(prefab, 0.61f); rotten = items.TestTaggedItem(homemade, 0.62f); hostMeal = items.TestTaggedItem(prefab, 0.63f);
                var food = (Item_Poutine)items.TestItem(fresh)!; float before = food.Value1; TickFood(food);
                Require(Math.Abs(before - food.Value1 - 0.000049603175f) < 0.0000001f, "Native refrigerated decay rate changed.");
                var spoiled = (Item_Poutine)items.TestItem(rotten)!; TickFood(spoiled);
                Require(spoiled.Value1 == 0 && FoodState.Renderer(spoiled).material.color == new Color(0, 0.46f, 0.03f), "Host native spoilage failed.");
                MoveNear(items.TestItem(hostMeal)!.transform.position); items.TestUse(hostMeal);
                Require(!items.TestState(hostMeal).Exists && GameManager.Get_PlayerStats[0] == 1 && needs.GetPoutine < 0.1f, "Host fresh consumption failed.");
                items.TestReplay(hostMeal); Require(GameManager.Get_PlayerStats[0] == 1, "Host food replay duplicated effects.");
                phase = 2;
            }
            else if (phase == 2 && items.TestAcknowledged(fresh) && !items.TestState(rotten).Exists && plugin.Session!.Peers.Count == 1)
            {
                Require(GameManager.Get_PlayerStats[0] == 1 && needs.GetAnxiety < 0.12f && needs.GetPoutine < 0.12f,
                    "Guest food effects mutated host needs/counters."); phase = 3;
            }
            else if (phase == 3 && items.TestAcknowledged(rotten) && plugin.Session!.Peers.Count == 2)
            {
                Require(items.TestState(rotten).Values.Value1 == 0 && items.TestState(fresh).Values.Flags == ItemFlags.Refrigerated,
                    "Acknowledgment discarded committed food fields."); Cleanup(); return true;
            }
            if (fryerStaged && plugin.Session!.Peers.Count > 1 && Time.unscaledTime >= nextPress)
            {
                stagedFryer!.PressPotato();
                nextPress = Time.unscaledTime + 0.5f;
            }
            return false;
        }
        internal void Cleanup()
        {
            if (!fryerStaged) return;
            fryerStaged = false;
            GameManager.Get_Friendship[12] = originalFriendship;
            if (stagedFryer != null) SharedFoodPrep.TestSetBasketVisible(stagedFryer, originalBasketVisible);
        }
        private static void Spawn(SharedItems items, ushort prefab, Vector3 point, float scale, float life, bool refrigerated)
        {
            var item = (Item_Poutine)items.TestSpawn(point, prefab); item.isSpawnFromTheSaveLoadSystem = true;
            item.Value1 = life; item.Get_IsRefrigerated = refrigerated; item.transform.localScale = Vector3.one * scale;
        }
        private static void TickFood(Item_Poutine item) => AccessTools.Method(typeof(Item_Poutine), "Tick").Invoke(item, new object[] { 1 });
        private void MoveNear(Vector3 point)
        {
            var location = point + new Vector3(0, -0.8f, 1); gameplay.PlayerWalking.transform.position = location;
            gameplay.Get_Player_Rigidbody.position = location; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private void Delay(float seconds) => next = Time.unscaledTime + seconds;
        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException("Food test: " + reason); }
    }
}
