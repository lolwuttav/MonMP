using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using MonMP.Plugin.Testing;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Runs only after the explicit isolated paired test has connected and verified lights.
    internal sealed class PairedItemsTest
    {
        private static readonly FieldInfo FenceTemporaryPlacement = AccessTools.Field(typeof(Tool_Fence), "isTempPlaced")!;
        private static readonly FieldInfo GeneratorRunning = AccessTools.Field(typeof(HomeGenerator), "isEngineRunning")!;
        private static readonly MethodInfo NativeUseKeyDown = AccessTools.Method(typeof(PlayerInteractable), "UseKeyDown")!;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private ItemDiscoveryNativeProbe? discoveryProbe;
        private int phase;
        private int actionPhase;
        private float nextAction;
        private float actionDeadline;
        private int initialCount;
        private int brewedCoffeeAmount;
        private ulong entity, removed;
        private ulong actionElectric, actionMug, actionBasket, previousElectric, previousMug, previousBasket;
        private ulong actionFence, previousFence;
        private ushort electricPrefab, mugPrefab, basketPrefab, fencePrefab;
        private int actionStyle;
        private Vector3 heldAt;
        private Vector3 percolatorStandingPoint;
        private Vector3 fencePosition;
        private Quaternion fenceRotation;
        private HomeGenerator? testGenerator;
        private bool generatorWasRunning;
        public string Status => "carry phase " + phase + "; client action=" + actionPhase + "; items=" + plugin.Items?.Count + "; originals=" + plugin.Items?.TestOriginalCount +
            "; basket=" + actionBasket + "/" + basketPrefab + "; fence=" + actionFence + "/" + fencePrefab + "; previous=" + previousFence +
            "; electric action=" + (plugin.World?.ElectricPercolator?.TestLastStatus ?? "unavailable") +
            "; electric request=" + (plugin.World?.ElectricPercolator?.TestLastRequest ?? "unavailable") +
            "; brewed cups=" + brewedCoffeeAmount + "; electric state=" + ElectricDiagnostic() +
            "; newest=" + (plugin.Items != null && fencePrefab != 0 ? FenceDiagnostic(plugin.Items) : "unavailable");
        public PairedItemsTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            NativeRegistryTest.Verify(plugin.World!);
            fencePrefab = plugin.Items!.TestPrefab("Deco/Fences/Fence_Wood");
            previousFence = plugin.Items.TestNewestPrefab(fencePrefab);
        }
        public bool Tick()
        {
            if (Time.unscaledTime < nextAction) return false;
            var items = plugin.Items;
            // The original carry choreography chooses first/last item IDs. Run
            // synthetic registry items only after that phase and its reconnect.
            bool carryComplete = plugin.TestOptions.IsHost ? phase >= 6 : phase >= 8;
            if (carryComplete && items?.Ready == true)
            {
                discoveryProbe ??= new ItemDiscoveryNativeProbe(items);
                if (!discoveryProbe.Tick()) return false;
            }
            if (plugin.TestOptions.IsHost) return Host(items!);
            if (phase == 7)
            {
                plugin.BeginPairedTestSession(); phase = 8; nextAction = Time.unscaledTime + 0.5f; return false;
            }
            if (items?.Ready != true) return false;
            if (phase == 0)
            {
                if (items.Count <= items.TestOriginalCount) return false;
                entity = items.TestLastId; removed = items.TestFirstId;
                Require(items.TestItem(entity) != null, "Host-only dynamic spawn did not create a guest item.");
                MoveNear(items.TestItem(entity)!.transform.position); phase = 1; nextAction = Time.unscaledTime + 0.7f;
            }
            else if (phase == 1)
            { items.TestGrab(entity); phase = 2; }
            else if (phase == 2 && items.TestState(entity).Lease.Owner == plugin.Session!.LocalPeerId)
            {
                Require(gameplay.Player.Get_HoldingItem == items.TestItem(entity), "Granted item was not held by the vanilla guest player.");
                Require(items.TestItem(entity)!.rb.isKinematic, "Guest held item simulated independent physics.");
                var saveFiles = Directory.GetFiles(LocalTestIsolation.SaveRoot, "*", SearchOption.AllDirectories);
                int timestamp = LocalTestIsolation.GetInt("SaveTimestamp");
                SaveSystem.Save(House_Location.Roulotte);
                Require(timestamp == LocalTestIsolation.GetInt("SaveTimestamp") && saveFiles.SequenceEqual(Directory.GetFiles(LocalTestIsolation.SaveRoot, "*", SearchOption.AllDirectories)),
                    "Connected guest wrote a local save while holding a replica.");
                heldAt = items.TestItem(entity)!.transform.position;
                MoveNear(heldAt + Vector3.right * 2);
                phase = 3; nextAction = Time.unscaledTime + 1.5f;
            }
            else if (phase == 3)
            {
                Require((items.TestItem(entity)!.transform.position - heldAt).sqrMagnitude > 0.2f, "Held item did not follow guest movement.");
                items.TestDrop(true); phase = 4;
            }
            else if (phase == 4 && items.TestState(entity).Lease.Owner == ItemLease.Unowned)
            {
                Require(gameplay.Player.Get_HoldingItem == null && items.TestItem(entity)!.rb.isKinematic, "Guest drop did not restore input and replica physics.");
                phase = 5; nextAction = Time.unscaledTime + 0.6f;
            }
            else if (phase == 5 && !items.TestState(removed).Exists)
            {
                Require(items.TestItem(removed) == null, "Host despawn left a guest object.");
                MoveNear(items.TestItem(entity)!.transform.position); phase = 6; nextAction = Time.unscaledTime + 0.7f;
            }
            else if (phase == 6)
            {
                if (items.TestState(entity).Lease.Owner != plugin.Session!.LocalPeerId) { items.TestGrab(entity); nextAction = Time.unscaledTime + 0.3f; return false; }
                // Capture the baseline before disconnect: the host can spawn the next fixture
                // as soon as we reconnect, before our profile/position restoration has finished.
                electricPrefab = items.TestTypePrefab(typeof(Item_ElectricCoffeePercolator));
                mugPrefab = items.TestTypePrefab(typeof(Item_HomemadeCoffeeMug));
                basketPrefab = items.TestPrefab("Tool/HarvestBasket");
                previousElectric = items.TestNewestPrefab(electricPrefab);
                previousMug = items.TestNewestPrefab(mugPrefab);
                previousBasket = items.TestNewestPrefab(basketPrefab);
                plugin.EndPairedTestSession();
                Require(items.TestRestored && gameplay.Player.Get_HoldingItem == null, "Disconnect did not remove replicas and restore guest originals.");
                phase = 7; nextAction = Time.unscaledTime + 1;
            }
            else if (phase == 8)
            {
                // The reconnect-position reply follows snapshot/profile readiness. Let it
                // finish before staging another interaction, or it teleports us back mid-action.
                if (plugin.World?.PersonalProfileReady != true || plugin.World.ReconnectPositions?.TestSettled != true) return false;
                Require(items.TestState(entity).Lease.Owner == ItemLease.Unowned && !items.TestState(removed).Exists,
                    "Reconnect snapshot lost released ownership or resurrected a tombstone.");
                Require(items.TestItem(entity) != null && items.TestItem(removed) == null, "Reconnect entity binding failed.");
                actionDeadline = Time.unscaledTime + 120f;
                phase = 9;
            }
            else if (phase == 9)
            {
                if (Time.unscaledTime > actionDeadline) throw new InvalidOperationException("Paired carry test timed out during percolator actions: " + Status);
                ulong newest = items.TestNewestPrefab(electricPrefab);
                if (newest <= previousElectric || items.TestItem(newest) is not Item_ElectricCoffeePercolator percolator) return false;
                actionElectric = newest;
                var child = Child(percolator) ?? throw new InvalidOperationException("Paired carry test: host percolator has no coffee child.");
                if (actionPhase == 1)
                {
                    Require(Vector3.Distance(gameplay.PlayerWalking.transform.position, child.transform.position) <= 4.5f,
                        "Guest left the percolator interaction point: player=" + gameplay.PlayerWalking.transform.position +
                        "; child=" + child.transform.position + "; initial=" + percolatorStandingPoint + ".");
                    child.MouseDown();
                    actionPhase = 2; nextAction = Time.unscaledTime + 0.3f; phase = 10;
                    return false;
                }
                ulong basketId = items.TestNewestPrefab(basketPrefab);
                if (basketId <= previousBasket || items.TestItem(basketId) is not Tool_HarvestBasket basket) return false;
                actionBasket = basketId;
                Require(items.TestState(actionElectric).Values.IntValue1 == 1 &&
                    items.TestState(actionElectric).Values.IntValue2 == 0 && child.isActiveAndEnabled,
                    "Host electric percolator fixture did not start powered with an empty pot.");
                Require(items.TestState(actionBasket).Values.IntValue1 == 3 && items.TestState(actionBasket).Values.Value1 >= 10f &&
                    items.TestState(actionBasket).Lease.Owner == ItemLease.Unowned,
                    "Host electric-percolator fixture did not expose a shared 10-bean coffee basket.");
                MoveNearClear(child.transform.position);
                percolatorStandingPoint = gameplay.PlayerWalking.transform.position;
                actionPhase = 1; nextAction = Time.unscaledTime + 0.8f;
            }
            else if (phase == 10)
            {
                if (Time.unscaledTime > actionDeadline) throw new InvalidOperationException("Paired carry test timed out waiting for electric percolator brewing: " + Status);
                if (items.TestState(actionBasket).Values.Value1 > 0f || items.TestState(actionElectric).Values.IntValue2 < 1) return false;
                var percolator = (Item_ElectricCoffeePercolator)items.TestItem(actionElectric)!;
                var child = Child(percolator)!;
                brewedCoffeeAmount = items.TestState(actionElectric).Values.IntValue2;
                Require(items.TestState(actionBasket).Values.IntValue1 == 3 && brewedCoffeeAmount >= 1 &&
                    child.Get_CoffeeAmount == brewedCoffeeAmount,
                    "The guest brew did not consume 10 coffee beans and publish its native coffee count.");
                child.MouseDown();
                actionPhase = 3; phase = 11;
            }
            else if (phase == 11 && actionPhase == 3)
            {
                ulong newest = items.TestNewestPrefab(mugPrefab);
                if (newest <= previousMug || items.TestItem(newest) == null) return false;
                actionMug = newest;
                var electric = (Item_ElectricCoffeePercolator)items.TestItem(actionElectric)!;
                int expectedRemaining = brewedCoffeeAmount - 1;
                if (items.TestState(actionElectric).Values.IntValue2 != expectedRemaining || Child(electric)!.Get_CoffeeAmount != expectedRemaining) return false;
                Require(items.TestState(actionMug).Lease.Owner == plugin.Session!.LocalPeerId &&
                    ReferenceEquals(gameplay.Player.Get_HoldingItem, items.TestItem(actionMug)),
                    "The served coffee mug was not granted to the guest's vanilla hand.");
                Child((Item_ElectricCoffeePercolator)items.TestItem(actionElectric)!)!.Use();
                actionPhase = 4; nextAction = Time.unscaledTime + 0.3f;
            }
            else if (phase == 11 && actionPhase == 4 && items.TestState(actionElectric).Values.IntValue1 == 0)
            {
                Require(items.TestItem(actionElectric)!.IntValue1 == 0,
                    "Guest percolator power callback did not unfreeze the host's fixture.");
                items.TestDrop(false); actionPhase = 4; phase = 12;
            }
            else if (phase == 12 && actionPhase == 4 && !items.TestState(actionElectric).Exists && !items.TestState(actionMug).Exists && !items.TestState(actionBasket).Exists)
            {
                Require(gameplay.Player.Get_HoldingItem == null, "Percolator action cleanup left a mug in the guest's hand.");
                if (!SharedForest.TestPlantPoint(out var plantPoint)) throw new InvalidOperationException("Client-action test could not find clear home-land ground for the guest.");
                MoveNearClear(plantPoint + Vector3.up * 0.1f);
                actionDeadline = Time.unscaledTime + 40f;
                actionPhase = 5; nextAction = Time.unscaledTime + 0.2f;
            }
            else if (phase == 12 && actionPhase == 5)
            {
                if (Time.unscaledTime > actionDeadline) throw new InvalidOperationException("Client-action test timed out waiting for the host fence fixture.");
                ulong newest = items.TestNewestPrefab(fencePrefab);
                if (newest <= previousFence) return false;
                if (items.TestItem(newest) is not Tool_Fence fence)
                    throw new InvalidOperationException("Guest fence fixture could not be resolved: " + FenceDiagnostic(items));
                actionFence = newest;
                fencePosition = fence.transform.position; fenceRotation = fence.transform.rotation;
                MoveNearClear(fencePosition);
                items.TestGrab(actionFence);
                actionPhase = 6; nextAction = Time.unscaledTime + 0.3f;
            }
            else if (phase == 12 && actionPhase == 6)
            {
                if (Time.unscaledTime > actionDeadline) throw new InvalidOperationException("Guest could not equip the shared fence fixture: " + FenceDiagnostic(items));
                var fence = (Tool_Fence)items.TestItem(actionFence)!;
                if (items.TestState(actionFence).Lease.Owner != plugin.Session!.LocalPeerId || gameplay.Player.Get_HoldingItem != fence) return false;
                NativeUseKeyDown.Invoke(gameplay.Player, null);
                Require(gameplay.Player.Get_ActiveTool == fence,
                    "Native Use did not equip the guest-held fence: " + FenceDiagnostic(items));
                fence.transform.SetParent(null, true); fence.transform.SetPositionAndRotation(fencePosition, fenceRotation);
                FenceTemporaryPlacement.SetValue(fence, true);
                fence.MouseDown(); actionPhase = 7; nextAction = Time.unscaledTime + 0.3f;
            }
            else if (phase == 12 && actionPhase == 7)
            {
                if (items.TestState(actionFence).Values.IntValue1 != 1 || items.TestState(actionFence).Lease.Owner != ItemLease.Unowned) return false;
                var fence = (Tool_Fence)items.TestItem(actionFence)!;
                Require(fence.IntValue1 == 1 && fence.itemCollider != null && !fence.itemCollider.isTrigger,
                    "Host fence installation did not replicate installed collision state.");
                actionStyle = fence.IntValue2;
                fence.Use(); actionPhase = 8; nextAction = Time.unscaledTime + 0.3f;
            }
            else if (phase == 12 && actionPhase == 8)
            {
                var fence = (Tool_Fence)items.TestItem(actionFence)!;
                if (items.TestState(actionFence).Values.IntValue2 == actionStyle) return false;
                Require(fence.IntValue2 == items.TestState(actionFence).Values.IntValue2,
                    "Host fence style did not reach the guest replica.");
                actionStyle = items.TestState(actionFence).Values.IntValue2;
                MoveNearClear(fence.transform.position + Vector3.right * 30f);
                actionPhase = 11; nextAction = Time.unscaledTime + 1.2f;
            }
            else if (phase == 12 && actionPhase == 11)
            {
                ((Tool_Fence)items.TestItem(actionFence)!).Use();
                actionPhase = 12; nextAction = Time.unscaledTime + 0.8f;
            }
            else if (phase == 12 && actionPhase == 12)
            {
                var fence = (Tool_Fence)items.TestItem(actionFence)!;
                Require(items.TestState(actionFence).Values.IntValue2 == actionStyle && fence.IntValue2 == actionStyle,
                    "A distant guest fence-style request changed the host or guest fence.");
                MoveNearClear(fence.transform.position);
                actionPhase = 13; nextAction = Time.unscaledTime + 1.2f;
            }
            else if (phase == 12 && actionPhase == 13)
            {
                ((Tool_Fence)items.TestItem(actionFence)!).MouseRightDown();
                actionPhase = 9; actionDeadline = Time.unscaledTime + 5f;
                nextAction = Time.unscaledTime + 0.3f;
            }
            else if (phase == 12 && actionPhase == 9)
            {
                if (Time.unscaledTime > actionDeadline) throw new InvalidOperationException("Guest fence removal did not complete: " + Status);
                if (items.TestState(actionFence).Values.IntValue1 != 0) return false;
                ((Tool_Fence)items.TestItem(actionFence)!).MouseRightUp(); actionPhase = 10;
                nextAction = Time.unscaledTime + 0.3f;
            }
            else if (phase == 12 && actionPhase == 10 && !items.TestState(actionFence).Exists)
            {
                Require(gameplay.Player.Get_ActiveTool == null && gameplay.Player.Get_HoldingItem == null,
                    "Fence install did not return the guest's vanilla hand and tool state.");
                return true;
            }
            return false;
        }
        private bool Host(SharedItems items)
        {
            if (phase == 6) return HostPercolatorActions(items);
            if (phase == 0)
            {
                initialCount = items.Count;
                Require(initialCount > 1, "No carry items in the loaded world.");
                var first = items.TestFirstId;
                MoveNear(items.TestItem(first)!.transform.position);
                items.TestGrab(first);
                Require(items.TestState(first).Lease.Owner == 0 && gameplay.Player.Get_HoldingItem == items.TestItem(first), "Host vanilla pickup failed.");
                items.TestDrop(false);
                Require(items.TestState(first).Lease.Owner == ItemLease.Unowned && gameplay.Player.Get_HoldingItem == null, "Host vanilla drop failed.");
                items.TestSpawn(gameplay.PlayerWalking.transform.position + new Vector3(1, 1.5f, 1));
                phase = 1;
            }
            else if (phase == 1 && items.Count > initialCount)
            {
                entity = items.TestLastId; removed = items.TestFirstId;
                if (items.TestState(entity).Lease.Owner == ItemLease.Unowned) return false;
                uint owner = items.TestState(entity).Lease.Owner;
                heldAt = items.TestItem(entity)!.transform.position;
                items.TestGrab(entity);
                Require(owner != 0 && items.TestState(entity).Lease.Owner == owner && gameplay.Player.Get_HoldingItem == null,
                    "Host stole an item leased to a guest.");
                phase = 2;
            }
            else if (phase == 2 && items.TestState(entity).Lease.Owner == ItemLease.Unowned)
            {
                Require((items.TestItem(entity)!.transform.position - heldAt).sqrMagnitude > 0.2f, "Guest carry movement never reached the host.");
                Require(!items.TestItem(entity)!.rb.isKinematic, "Host did not resume dropped item physics.");
                items.TestDestroy(removed); phase = 3;
            }
            else if (phase == 3 && items.TestState(entity).Lease.Owner != ItemLease.Unowned) phase = 4;
            else if (phase == 4 && plugin.Session!.Peers.Count == 1)
            {
                Require(items.TestState(entity).Lease.Owner == ItemLease.Unowned && !items.TestItem(entity)!.rb.isKinematic,
                    "Disconnect did not release the guest's held item on the host.");
                phase = 5;
            }
            else if (phase == 5 && plugin.Session!.Peers.Count == 2)
            {
                electricPrefab = items.TestTypePrefab(typeof(Item_ElectricCoffeePercolator));
                mugPrefab = items.TestTypePrefab(typeof(Item_HomemadeCoffeeMug));
                basketPrefab = items.TestPrefab("Tool/HarvestBasket");
                previousElectric = items.TestNewestPrefab(electricPrefab);
                previousMug = items.TestNewestPrefab(mugPrefab);
                previousBasket = items.TestNewestPrefab(basketPrefab);
                actionDeadline = Time.unscaledTime + 120f;
                var position = PoweredPercolatorTestPoint() + Vector3.up * 0.1f;
                var fixture = (Item_ElectricCoffeePercolator)items.TestSpawn(position, electricPrefab);
                fixture.isSpawnFromTheSaveLoadSystem = true; fixture.CreateBasics();
                var child = Child(fixture) ?? throw new InvalidOperationException("Paired carry test: host fixture has no coffee child after CreateBasics.");
                var basket = (Tool_HarvestBasket)items.TestSpawn(child.transform.position + Vector3.right * 0.65f, basketPrefab);
                basket.isSpawnFromTheSaveLoadSystem = true; basket.IntValue1 = 3; basket.Value1 = 10f; basket.CreateBasics();
                if (basket.rb != null) { basket.rb.constraints = RigidbodyConstraints.FreezeAll; basket.rb.velocity = Vector3.zero; basket.rb.angularVelocity = Vector3.zero; }
                phase = 6; actionPhase = 1;
            }
            return false;
        }
        private bool HostPercolatorActions(SharedItems items)
        {
            if (Time.unscaledTime > actionDeadline) throw new InvalidOperationException("Paired carry test timed out waiting on percolator actions: " + Status);
            if (actionPhase == 1)
            {
                actionElectric = items.TestNewestPrefab(electricPrefab);
                if (actionElectric <= previousElectric || items.TestItem(actionElectric) is not Item_ElectricCoffeePercolator percolator) return false;
                actionBasket = items.TestNewestPrefab(basketPrefab);
                if (actionBasket <= previousBasket || items.TestItem(actionBasket) is not Tool_HarvestBasket basket) return false;
                var child = Child(percolator) ?? throw new InvalidOperationException("Paired carry test: host percolator has no coffee child.");
                var basketPosition = child.transform.position + Vector3.right * 0.65f;
                basket.transform.SetPositionAndRotation(basketPosition, Quaternion.identity);
                if (basket.rb != null)
                {
                    basket.rb.position = basketPosition; basket.rb.velocity = Vector3.zero; basket.rb.angularVelocity = Vector3.zero;
                    basket.rb.constraints = RigidbodyConstraints.FreezeAll;
                }
                if (percolator.IntValue1 != 1) percolator.Freeze();
                percolator.IntValue2 = 0;
                basket.IntValue1 = 3; basket.Value1 = 10f;
                items.HostPublishItem(actionBasket);
                items.HostPublishItem(actionElectric);
                Require(percolator.IntValue1 == 1 && percolator.IntValue2 == 0 && child.isActiveAndEnabled &&
                    basket.IntValue1 == 3 && basket.Value1 == 10f && Vector3.Distance(basket.transform.position, child.transform.position) <= 1f,
                    "Host could not prepare the empty electric percolator and coffee basket fixtures.");
                actionPhase = 2; return false;
            }
            if (actionPhase == 2)
            {
                if (items.TestState(actionBasket).Values.Value1 > 0f) return false;
                Require(((Tool_HarvestBasket)items.TestItem(actionBasket)!).Value1 <= 0f,
                    "Guest brewing did not consume the 10-bean basket charge on the host.");
                actionDeadline = Time.unscaledTime + 90f;
                actionPhase = 3; return false;
            }
            if (actionPhase == 3)
            {
                if (items.TestState(actionElectric).Values.IntValue2 < 1) return false;
                var percolator = (Item_ElectricCoffeePercolator)items.TestItem(actionElectric)!;
                brewedCoffeeAmount = percolator.IntValue2;
                Require(brewedCoffeeAmount >= 1 && Child(percolator)!.Get_CoffeeAmount == brewedCoffeeAmount,
                    "The host did not finish and publish the native electric-percolator coffee count.");
                actionPhase = 4; return false;
            }
            if (actionPhase == 4)
            {
                actionMug = items.TestNewestPrefab(mugPrefab);
                if (actionMug <= previousMug || items.TestItem(actionMug) == null || items.TestState(actionMug).Lease.Owner == ItemLease.Unowned)
                    return false;
                var electric = (Item_ElectricCoffeePercolator)items.TestItem(actionElectric)!;
                int expectedRemaining = brewedCoffeeAmount - 1;
                if (electric.IntValue2 != expectedRemaining || Child(electric)!.Get_CoffeeAmount != expectedRemaining) return false;
                Require(items.TestState(actionMug).Lease.Owner == plugin.Session!.Peers.Keys.Single(peer => peer != 0),
                    "Served mug lease was granted to the wrong guest.");
                actionPhase = 5; return false;
            }
            if (actionPhase == 5 && items.TestItem(actionElectric)!.IntValue1 == 0 &&
                items.TestState(actionMug).Lease.Owner == ItemLease.Unowned)
            {
                items.TestDestroy(actionElectric); items.TestDestroy(actionMug); items.TestDestroy(actionBasket); RestoreTestGenerator(); actionPhase = 6;
                actionDeadline = Time.unscaledTime + 40f; return false;
            }
            if (actionPhase == 6 && !items.TestState(actionElectric).Exists && !items.TestState(actionMug).Exists && !items.TestState(actionBasket).Exists)
            {
                if (!SharedForest.TestPlantPoint(out var plantPoint)) throw new InvalidOperationException("Client-action test could not find clear home-land ground for the fence fixture.");
                fencePrefab = items.TestPrefab("Deco/Fences/Fence_Wood"); previousFence = items.TestNewestPrefab(fencePrefab);
                var fence = (Tool_Fence)items.TestSpawn(plantPoint + Vector3.up * 0.1f, fencePrefab);
                fence.isSpawnFromTheSaveLoadSystem = true; fence.CreateBasics();
                actionPhase = 20; actionDeadline = Time.unscaledTime + 40f; return false;
            }
            if (actionPhase == 20)
            {
                if (Time.unscaledTime > actionDeadline) throw new InvalidOperationException("Client-action test timed out waiting for the host fence fixture to register.");
                ulong newestFence = items.TestNewestPrefab(fencePrefab);
                if (newestFence <= previousFence || items.TestItem(newestFence) is not Tool_Fence) return false;
                actionFence = newestFence; actionPhase = 21; return false;
            }
            if (actionPhase == 21)
            {
                if (Time.unscaledTime > actionDeadline) throw new InvalidOperationException("Client-action test timed out waiting for guest fence installation.");
                if (items.TestState(actionFence).Values.IntValue1 != 1 || items.TestState(actionFence).Lease.Owner != ItemLease.Unowned) return false;
                actionStyle = items.TestState(actionFence).Values.IntValue2; actionPhase = 22; return false;
            }
            if (actionPhase == 22)
            {
                if (items.TestState(actionFence).Values.IntValue2 == actionStyle) return false;
                actionPhase = 23; return false;
            }
            if (actionPhase == 23)
            {
                if (items.TestState(actionFence).Values.IntValue1 != 0) return false;
                Require(plugin.World!.FenceActions.TestTooFarRejections > 0,
                    "The host did not reject the distant guest fence-style request.");
                items.TestDestroy(actionFence); actionPhase = 24; return false;
            }
            return actionPhase == 24 && !items.TestState(actionFence).Exists && plugin.World!.FenceActions.TestTooFarRejections > 0;
        }
        private static Item_CoffeePercolator? Child(Item_ElectricCoffeePercolator percolator) =>
            percolator.GetComponentsInChildren<Item_CoffeePercolator>(true).SingleOrDefault();
        private string ElectricDiagnostic()
        {
            if (plugin.Items == null || actionElectric == 0) return "unavailable";
            var electric = plugin.Items.TestItem(actionElectric) as Item_ElectricCoffeePercolator;
            if (electric == null) return "destroyed";
            var child = Child(electric);
            return "parent=" + electric.IntValue2 + "; child=" + (child == null ? "missing" : child.Get_CoffeeAmount.ToString()) +
                "; shared=" + plugin.Items.TestState(actionElectric).Values.IntValue2;
        }
        private string FenceDiagnostic(SharedItems items)
        {
            ulong newest = items.TestNewestPrefab(fencePrefab);
            if (newest == 0) return "no bound fence prefab=" + fencePrefab;
            var item = items.TestItem(newest);
            var state = items.TestState(newest);
            return "id=" + newest + "; exists=" + state.Exists + "; statePrefab=" + state.Prefab +
                "; actualType=" + (ReferenceEquals(item, null) ? "<null-ref>" : item.GetType().FullName) +
                "; unityNull=" + (item == null) + "; expectedPrefab=" + fencePrefab;
        }
        private void MoveNear(Vector3 item)
        {
            var location = item + new Vector3(0, -0.8f, 1);
            gameplay.PlayerWalking.transform.position = location;
            gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private void MoveNearClear(Vector3 target)
        {
            int ground = LayerMask.GetMask("Ground");
            for (float radius = 1; radius <= 3; radius += 1)
                for (int step = 0; step < 16; step++)
                {
                    float angle = step * Mathf.PI / 8;
                    var at = target + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;
                    if (!Physics.Raycast(at + Vector3.up * 30, Vector3.down, out var hit, 60, ground, QueryTriggerInteraction.Ignore)) continue;
                    at = hit.point + Vector3.up;
                    // A roof or hillside above the item is not a valid interaction staging point.
                    if (Vector3.Distance(at, target) > 3.5f) continue;
                    if (Physics.OverlapCapsule(at, at + Vector3.up, 0.4f, ~0, QueryTriggerInteraction.Ignore)
                        .Any(c => !(c is TerrainCollider) && c.tag != "Player" && c.tag != "Water" && c.tag != "Leo")) continue;
                    SetPlayer(at);
                    return;
                }
            throw new InvalidOperationException("Client-action test: no clear standing point near " + target + ".");
        }
        private Vector3 PoweredPercolatorTestPoint()
        {
            testGenerator = UnityEngine.Object.FindObjectOfType<HomeGenerator>();
            if (testGenerator == null)
                throw new InvalidOperationException("Client-action test could not find the native Roulotte generator.");
            generatorWasRunning = testGenerator.Get_isEngineRunning;
            GeneratorRunning.SetValue(testGenerator, true);
            var basePoint = testGenerator.transform.position;
            int ground = LayerMask.GetMask("Ground");
            for (float radius = 3; radius <= 30; radius += 2)
                for (int step = 0; step < 32; step++)
                {
                    float angle = step * Mathf.PI / 8;
                    var probe = basePoint + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius + Vector3.up * 60;
                    if (!Physics.Raycast(probe, Vector3.down, out var hit, 200, ground, QueryTriggerInteraction.Ignore)) continue;
                    if (gameplay.ClosestHouse(hit.point) != House_Location.Roulotte) continue;
                    if (Physics.OverlapSphere(hit.point + Vector3.up * 0.7f, 2.2f, ~0, QueryTriggerInteraction.Ignore)
                        .Any(c => !(c is TerrainCollider) && c.tag != "Player" && c.tag != "Water" && c.tag != "Leo")) continue;
                    return hit.point;
                }
            RestoreTestGenerator();
            throw new InvalidOperationException("Client-action test could not find clear ground in the powered Roulotte generator zone.");
        }
        private void RestoreTestGenerator()
        {
            if (testGenerator != null) GeneratorRunning.SetValue(testGenerator, generatorWasRunning);
            testGenerator = null;
        }
        private void SetPlayer(Vector3 at)
        {
            gameplay.PlayerWalking.transform.position = at;
            if (gameplay.Get_Player_Rigidbody == null) return;
            gameplay.Get_Player_Rigidbody.position = at;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException("Paired carry test: " + reason); }
    }
}
