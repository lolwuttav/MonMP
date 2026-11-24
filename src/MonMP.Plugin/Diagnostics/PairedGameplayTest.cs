using System;
using System.IO;
using System.Linq;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedGameplayTest
    {
        private readonly MonMPPlugin plugin;
        private readonly GameplayProbe loader;
        private readonly string resultPath;
        private readonly PairedTestEvidence runEvidence;
        private int phase;
        private float nextAction;
        private float placementWaitSince;
        private ulong entity;
        private bool finished;
        // A pass is only written after the session has stayed up for a settle window: late failures such as
        // a disconnect a few seconds after tightening manifold bolts (#128) would otherwise pass unseen.
        private const float SettleSeconds = 8f;
        private string? settlingEvidence;
        private float settleUntil;
        private bool settleNeedsSession;
        private readonly bool productionSettlementOnly = Environment.GetCommandLineArgs().Contains("--monmp-production-settlement-only");
        private readonly bool tireReplayOnly = Environment.GetCommandLineArgs().Contains("--monmp-tire-replay-only");
        private PairedProductionSettlementTest? productionSettlementTest;
        private PairedTireReplayTest? tireReplayTest;
        private PairedItemsTest? itemsTest;
        private PairedVrHandsTest? vrHandsTest;
        private readonly bool vrHandsOnly = Environment.GetCommandLineArgs().Contains("--monmp-vr-hands-only");
        private PairedHeldItemInterpolationTest? heldItemInterpolationTest;
        private PairedConsumptionTest? consumptionTest;
        private PairedVehiclesTest? vehiclesTest;
        private PairedVehicleInputResponseTest? vehicleInputResponseTest;
        private PairedRemoteVehicleEngineAudioTest? remoteEngineAudioTest;
        private PairedGuestVehicleSaveTest? guestVehicleSaveTest;
        private PairedVehiclePoseTest? vehiclePoseTest;
        private PairedResyncTest? resyncTest;
        private PairedLanternTest? lanternTest;
        private PairedDecorationsTest? decorationsTest;
        private PairedFoodTest? foodTest;
        private PairedFryerCookTest? fryerCookTest;
        private PairedNitrousRefillTest? nitrousRefillTest;
        private PairedFishingPresentationTest? fishingPresentationTest;
        private PairedUnderglowTest? underglowTest;
        private PairedTravelingVanTest? travelingVanTest;
        private PairedPurchasedTruckDoorsTest? purchasedTruckDoorsTest;
        private PairedPassengerVehicleControlsTest? passengerControlsTest;
        private PairedEquippedToolScaleTest? equippedToolScaleTest;
        private PairedTrailerTowingTest? trailerTowingTest;
        private PairedTireInflationTest? tireInflationTest;
        private PairedClockTest? clockTest;
        private PairedAnxietyTest? anxietyTest;
        private PairedAnxietyGameplayTest? anxietyGameplayTest;
        private PairedWeatherTest? weatherTest;
        private PairedEconomyTest? economyTest;
        private PairedCheatsTest? cheatsTest;
        private PairedStoresTest? storesTest;
        private PairedProgressTest? progressTest;
        private PairedFlushTest? flushTest;
        private PairedProfileTest? profileTest;
        private PairedSaveRoundTest? saveRoundTest;
        private PairedInteractionAuditTest? auditTest;
        private PairedWindowsTest? windowsTest;
        private PairedDoorsTest? doorsTest;
        private PairedCollectiblesTest? collectiblesTest;
        private PairedSleepTest? sleepTest;
        private PairedChatTest? chatTest;
        private readonly bool chatOnly = Environment.GetCommandLineArgs().Contains("--monmp-chat-only");
        private readonly bool sleepOnly = Environment.GetCommandLineArgs().Contains("--monmp-sleep-only");
        private PairedVehiclePartsTest? partsTest;
        private readonly bool partsOnly = Environment.GetCommandLineArgs().Contains("--monmp-parts-only");
        private PairedVehicleAccessTest? accessTest;
        private readonly bool accessOnly = Environment.GetCommandLineArgs().Contains("--monmp-access-only");
        private PairedBoltsTest? boltsTest;
        private PairedRadiosTest? radiosTest;
        private PairedTowingTest? towingTest;
        private PairedChainsawTest? chainsawTest;
        private PairedSaleTest? saleTest;
        private PairedStorageGarageTest? storageTest;
        private readonly bool boltsOnly = Environment.GetCommandLineArgs().Contains("--monmp-bolts-only");
        private readonly bool radiosOnly = Environment.GetCommandLineArgs().Contains("--monmp-radios-only");
        private readonly bool towingOnly = Environment.GetCommandLineArgs().Contains("--monmp-towing-only");
        private readonly bool chainsawOnly = Environment.GetCommandLineArgs().Contains("--monmp-chainsaw-only");
        private readonly bool salesOnly = Environment.GetCommandLineArgs().Contains("--monmp-sales-only");
        private readonly bool saleCirclesOnly = Environment.GetCommandLineArgs().Contains("--monmp-sale-circles-only");
        private PairedSaleCirclesTest? saleCirclesTest;
        private readonly bool storageOnly = Environment.GetCommandLineArgs().Contains("--monmp-storage-only");
        private PairedWorldItemsTest? worldItemsTest;
        private PairedFishBucketsTest? fishBucketsTest;
        private readonly bool fishBucketsOnly = Environment.GetCommandLineArgs().Contains("--monmp-fish-buckets-only");
        private PairedFuelTest? fuelTest;
        private readonly bool fuelOnly = Environment.GetCommandLineArgs().Contains("--monmp-fuel-only");
        private PairedGarageTest? garageTest;
        private readonly bool garageOnly = Environment.GetCommandLineArgs().Contains("--monmp-garage-only");
        private readonly bool cheatsOnly = Environment.GetCommandLineArgs().Contains("--monmp-cheats-only");
        private PairedMetalSaleTest? metalSaleTest;
        private PairedTrashRecycleTest? trashRecycleTest;
        private PairedScrapyardGateTest? scrapyardGateTest;
        private readonly bool metalSaleOnly = Environment.GetCommandLineArgs().Contains("--monmp-metal-sale-only");
        private readonly bool trashRecycleOnly = Environment.GetCommandLineArgs().Contains("--monmp-trash-recycle-only");
        private readonly bool scrapyardGateOnly = Environment.GetCommandLineArgs().Contains("--monmp-scrapyard-gate-only");
        private PairedForestTest? forestTest;
        private PairedGardenTest? gardenTest;
        private PairedFleetPartsTest? fleetTest;
        private PairedForestGrowthTest? growthTest;
        private PairedCarLiftTest? liftTest;
        private PairedPlanterTest? planterTest;
        private PairedSugarShackTest? shackTest;
        private readonly bool shackOnly = Environment.GetCommandLineArgs().Contains("--monmp-shack-only");
        private PairedGeneratorTest? generatorTest;
        private readonly bool generatorOnly = Environment.GetCommandLineArgs().Contains("--monmp-generator-only");
        private PairedHydroTest? hydroTest;
        private readonly bool hydroOnly = Environment.GetCommandLineArgs().Contains("--monmp-hydro-only");
        private PairedAvatarCrouchTest? avatarCrouchTest;
        private readonly bool avatarOnly = Environment.GetCommandLineArgs().Contains("--monmp-avatar-only");
        private PairedBuggyBuildTest? buggyBuildTest;
        private readonly bool buggyBuildOnly = Environment.GetCommandLineArgs().Contains("--monmp-buggy-only");
        private PairedRacecarBuildTest? racecarBuildTest;
        private readonly bool racecarBuildOnly = Environment.GetCommandLineArgs().Contains("--monmp-racecar-only");
        private PairedCottagePurchaseTest? cottagePurchaseTest;
        private PairedDogProductTest? dogProductTest;
        private readonly bool dogProductOnly = Environment.GetCommandLineArgs().Contains("--monmp-dogproduct-only");
        private PairedLeoTest? leoTest;
        private PairedLeoCarBedTest? leoCarBedTest;
        private PairedActorRoutesTest? actorRoutesTest;
        private PairedSchoolBusPoseTest? schoolBusPoseTest;
        private readonly bool schoolBusOnly = Environment.GetCommandLineArgs().Contains("--monmp-schoolbus-only");
        private PairedSchoolBusPassengersTest? schoolBusPassengersTest;
        private PairedExtraSeatsTest? extraSeatsTest;
        private PairedSyrupCrateTest? syrupCrateTest;
        private readonly bool syrupCrateOnly = Environment.GetCommandLineArgs().Contains("--monmp-syrup-crate-only");
        private PairedMeetTest? meetTest;
        private readonly bool meetOnly = Environment.GetCommandLineArgs().Contains("--monmp-meet-only");
        private readonly bool extraSeatsOnly = Environment.GetCommandLineArgs().Contains("--monmp-extra-seats-only");
        private readonly bool schoolBusPassengersOnly = Environment.GetCommandLineArgs().Contains("--monmp-schoolbus-passengers-only");
        private PairedSteeringRackTest? steeringRackTest;
        private readonly bool steeringRackOnly = Environment.GetCommandLineArgs().Contains("--monmp-steering-rack-only");
        private PairedMartinCarPoseTest? martinCarPoseTest;
        private readonly bool martinCarOnly = Environment.GetCommandLineArgs().Contains("--monmp-martin-car-only");
        private PairedNpcTalkTest? npcTalkTest;
        private PairedNpcSubclassTest? npcSubclassTest;
        private PairedNpcWeedSaleTest? npcWeedSaleTest;
        private PairedNpcRouteTalkTest? npcRouteTalkTest;
        private readonly bool actorRoutesOnly = Environment.GetCommandLineArgs().Contains("--monmp-actor-routes-only");
        private readonly bool npcTalkOnly = Environment.GetCommandLineArgs().Contains("--monmp-npc-talk-only");
        private readonly bool npcCoffeeGiftOnly = Environment.GetCommandLineArgs().Contains("--monmp-npc-coffee-gift-only");
        private readonly bool npcSubclassOnly = Environment.GetCommandLineArgs().Contains("--monmp-npc-subclass-only");
        private readonly bool npcEricMilestoneOnly = Environment.GetCommandLineArgs().Contains("--monmp-npc-eric-milestone-only");
        private readonly bool npcWeedSaleOnly = Environment.GetCommandLineArgs().Contains("--monmp-npc-weed-sale-only");
        private readonly bool npcRouteTalkOnly = Environment.GetCommandLineArgs().Contains("--monmp-npc-route-talk-only");
        private readonly bool leoOnly = Environment.GetCommandLineArgs().Contains("--monmp-leo-only");
        private readonly bool leoTravelOnly = Environment.GetCommandLineArgs().Contains("--monmp-leo-travel-only");
        private readonly bool leoCarBedOnly = Environment.GetCommandLineArgs().Contains("--monmp-leo-carbed-only");
        private PairedLeoSuppliesTest? leoSuppliesTest;
        private readonly bool leoSuppliesOnly = Environment.GetCommandLineArgs().Contains("--monmp-leo-supplies-only");
        private PairedKonigSeatsTest? konigSeatsTest;
        private readonly bool konigSeatsOnly = Environment.GetCommandLineArgs().Contains("--monmp-konig-seats-only");
        private PairedKonigSeatAdjustmentsTest? konigSeatAdjustmentsTest;
        private readonly bool konigSeatAdjustmentsOnly = Environment.GetCommandLineArgs().Contains("--monmp-konig-seat-adjustments-only");
        private PairedSlotMachineTest? slotMachineTest;
        private readonly bool slotMachinesOnly = Environment.GetCommandLineArgs().Contains("--monmp-slot-machines-only");
        private PairedPaintPanelsTest? paintPanelsTest;
        private readonly bool paintPanelsOnly = Environment.GetCommandLineArgs().Contains("--monmp-paint-only");
        private PairedSprayPaintTest? sprayPaintTest;
        private readonly bool sprayPaintOnly = Environment.GetCommandLineArgs().Contains("--monmp-spray-paint-only");
        private readonly bool repairAccessoryOnly = Environment.GetCommandLineArgs().Contains("--monmp-repair-accessory");
        private readonly bool truckGrilleRustOnly = Environment.GetCommandLineArgs().Contains("--monmp-truck-grille-rust");
        private PairedRustRepairKitTest? rustRepairKitTest;
        private readonly bool rustRepairKitOnly = Environment.GetCommandLineArgs().Contains("--monmp-rust-repair-kit-only");
        private PairedDentRepairKitTest? dentRepairKitTest;
        private readonly bool dentRepairKitOnly = Environment.GetCommandLineArgs().Contains("--monmp-dent-repair-kit-only");
        private PairedWindowTintTest? windowTintTest;
        private readonly bool windowTintOnly = Environment.GetCommandLineArgs().Contains("--monmp-window-tint-only");
        private PairedTrailerAuthorityTest? trailerAuthorityTest;
        private readonly bool trailerAuthorityOnly = Environment.GetCommandLineArgs().Contains("--monmp-trailers-only");
        private readonly bool trailerCouplerOnly = Environment.GetCommandLineArgs().Contains("--monmp-trailer-coupler-only");
        private PairedVehiclePlatesTest? vehiclePlatesTest;
        private readonly bool vehiclePlatesOnly = Environment.GetCommandLineArgs().Contains("--monmp-vehicle-plates-only");
        private PairedTrafficAuthorityTest? trafficAuthorityTest;
        private readonly bool trafficAuthorityOnly = Environment.GetCommandLineArgs().Contains("--monmp-traffic-only");
        private readonly bool cottagePurchaseOnly = Environment.GetCommandLineArgs().Contains("--monmp-cottage-only");
        private PairedTubingTest? tubingTest;
        private readonly bool tubingOnly = Environment.GetCommandLineArgs().Contains("--monmp-tubing-only");
        private readonly bool planterOnly = Environment.GetCommandLineArgs().Contains("--monmp-planter-only");
        private readonly bool liftOnly = Environment.GetCommandLineArgs().Contains("--monmp-lift-only");
        private readonly bool growthOnly = Environment.GetCommandLineArgs().Contains("--monmp-growth-only");
        private readonly bool fleetOnly = Environment.GetCommandLineArgs().Contains("--monmp-fleet-only");
        private readonly bool gardenOnly = Environment.GetCommandLineArgs().Contains("--monmp-garden-only");
        private readonly bool forestOnly = Environment.GetCommandLineArgs().Contains("--monmp-forest-only");
        private readonly bool worldItemsOnly = Environment.GetCommandLineArgs().Contains("--monmp-worlditems-only");
        private readonly bool collectiblesOnly = Environment.GetCommandLineArgs().Contains("--monmp-collectibles-only");
        private readonly bool doorsOnly = Environment.GetCommandLineArgs().Contains("--monmp-doors-only");
        private readonly bool windowsOnly = Environment.GetCommandLineArgs().Contains("--monmp-windows-only");
        private readonly bool preownedTruckWindows = Environment.GetCommandLineArgs().Contains("--monmp-preowned-truck-windows");
        private readonly bool auditOnly = Environment.GetCommandLineArgs().Contains("--monmp-audit-only");
        private PairedCoordinatedSaveTest? coordinatedSaveTest;
        private PairedPersistenceReloadTest? persistenceReloadTest;
        private readonly bool persistenceReloadOnly = Environment.GetCommandLineArgs().Contains("--monmp-persistence-reload-only");
        private string itemStatus = "";
        private readonly bool vehiclesOnly = Environment.GetCommandLineArgs().Contains("--monmp-vehicles-only");
        private readonly bool vehicleInputResponseOnly = Environment.GetCommandLineArgs().Contains("--monmp-vehicle-input-response-only");
        private readonly bool truckDoorsOnly = Environment.GetCommandLineArgs().Contains("--monmp-truck-doors-only");
        private readonly bool remoteEngineAudioOnly = Environment.GetCommandLineArgs().Contains("--monmp-remote-engine-audio-only");
        private readonly bool guestVehicleSaveOnly = Environment.GetCommandLineArgs().Contains("--monmp-guest-vehicle-save-only");
        private readonly bool vehiclePoseOnly = Environment.GetCommandLineArgs().Contains("--monmp-vehicle-pose-only");
        private readonly bool consumptionOnly = Environment.GetCommandLineArgs().Contains("--monmp-consumption-only");
        private readonly bool itemsOnly = Environment.GetCommandLineArgs().Contains("--monmp-items-only");
        private readonly bool clientActionsOnly = Environment.GetCommandLineArgs().Contains("--monmp-client-actions-only");
        private readonly bool heldItemsOnly = Environment.GetCommandLineArgs().Contains("--monmp-held-items-only");
        private readonly bool decorationsOnly = Environment.GetCommandLineArgs().Contains("--monmp-decorations-only");
        private readonly bool foodOnly = Environment.GetCommandLineArgs().Contains("--monmp-food-only");
        private readonly bool fryerCookOnly = Environment.GetCommandLineArgs().Contains("--monmp-fryer-cook-only");
        private readonly bool nitrousRefillOnly = Environment.GetCommandLineArgs().Contains("--monmp-nitrous-refill-only");
        private readonly bool fishingPresentationOnly = Environment.GetCommandLineArgs().Contains("--monmp-fishing-presentation-only");
        private readonly bool underglowOnly = Environment.GetCommandLineArgs().Contains("--monmp-underglow-only");
        private readonly bool travelingVanOnly = Environment.GetCommandLineArgs().Contains("--monmp-traveling-van-only");
        private readonly bool purchasedTruckDoorsOnly = Environment.GetCommandLineArgs().Contains("--monmp-purchased-truck-doors-only");
        private readonly bool passengerControlsOnly = Environment.GetCommandLineArgs().Contains("--monmp-passenger-controls-only");
        private readonly bool equippedToolScaleOnly = Environment.GetCommandLineArgs().Contains("--monmp-equipped-tool-scale-only");
        private readonly bool toolPacksOnly = Environment.GetCommandLineArgs().Contains("--monmp-tool-packs-only");
        private readonly bool shellSeatsOnly = Environment.GetCommandLineArgs().Contains("--monmp-shell-seats-only");
        private readonly bool shellSuspensionOnly = Environment.GetCommandLineArgs().Contains("--monmp-shell-suspension-only");
        private readonly bool consoleChargerOnly = Environment.GetCommandLineArgs().Contains("--monmp-console-charger-only");
        private PairedToolPacksTest? toolPacksTest;
        private PairedShellSeatsTest? shellSeatsTest;
        private PairedShellSuspensionTest? shellSuspensionTest;
        private PairedConsoleChargerTest? consoleChargerTest;
        private readonly bool trailerTowingOnly = Environment.GetCommandLineArgs().Contains("--monmp-trailer-towing-only");
        private readonly bool tireInflationOnly = Environment.GetCommandLineArgs().Contains("--monmp-tire-inflation-only");
        private readonly bool coordinatedSaveOnly = Environment.GetCommandLineArgs().Contains("--monmp-coordinated-save-only");
        private readonly bool saveRoundOnly = Environment.GetCommandLineArgs().Contains("--monmp-save-round-only");
        private readonly bool profileOnly = Environment.GetCommandLineArgs().Contains("--monmp-profile-only");
        private readonly bool flushOnly = Environment.GetCommandLineArgs().Contains("--monmp-flush-only");
        private readonly bool progressOnly = Environment.GetCommandLineArgs().Contains("--monmp-progress-only");
        private readonly bool storesOnly = Environment.GetCommandLineArgs().Contains("--monmp-stores-only");
        private readonly bool economyOnly = Environment.GetCommandLineArgs().Contains("--monmp-economy-only");
        private readonly bool weatherOnly = Environment.GetCommandLineArgs().Contains("--monmp-weather-only");
        private readonly bool clockOnly = Environment.GetCommandLineArgs().Contains("--monmp-clock-only");
        private readonly bool anxietyGameplayOnly = Environment.GetCommandLineArgs().Contains("--monmp-anxiety-gameplay-only");
        private readonly bool anxietyOnly = Environment.GetCommandLineArgs().Contains("--monmp-anxiety-only");
        internal PairedGameplayTest(MonMPPlugin plugin, RuntimeProbe probe)
        {
            this.plugin = plugin; loader = new GameplayProbe(probe, false);
            resultPath = Path.Combine(probe.DirectoryPath, "paired-gameplay.txt");
            runEvidence = new PairedTestEvidence(plugin, probe.DirectoryPath);
            File.WriteAllText(resultPath, "RUNNING: loading isolated vanilla gameplay");
        }
        internal void Tick(Gameplay? gameplay)
        {
            if (finished) return;
            if (settlingEvidence != null) { Settle(); return; }
            if (Time.unscaledTime > (saleCirclesOnly ? 900 : anxietyGameplayOnly ? 660 : guestVehicleSaveOnly || vehiclePlatesOnly ? 600 : 300)) { Fail("Timed out in phase " + phase + "; " + itemsTest?.Status + "; " + consumptionTest?.Status + "; " + vehiclesTest?.Status + "; " + lanternTest?.Status + "; " + decorationsTest?.Status + "; " + foodTest?.Status + "; " + clockTest?.Status + "; " + weatherTest?.Status + "; " + economyTest?.Status + "; " + storesTest?.Status + "; " + progressTest?.Status + "; " + flushTest?.Status + "; " + profileTest?.Status + "; " + saveRoundTest?.Status + "; " + coordinatedSaveTest?.Status + "; " + persistenceReloadTest?.Status + "; " + doorsTest?.Status + "; " + collectiblesTest?.Status + "; " + sleepTest?.Status + "; " + partsTest?.Status + "; " + accessTest?.Status + "; " + boltsTest?.Status + "; " + radiosTest?.Status + "; " + towingTest?.Status + "; " + chainsawTest?.Status + "; " + saleTest?.Status + "; " + storageTest?.Status + "; " + fuelTest?.Status + "; " + garageTest?.Status + "; " + forestTest?.Status + "; " + gardenTest?.Status + "; " + fleetTest?.Status + "; " + growthTest?.Status + "; " + liftTest?.Status + "; " + planterTest?.Status + "; " + shackTest?.Status + "; " + tubingTest?.Status + "; " + buggyBuildTest?.Status + "; " + racecarBuildTest?.Status + "; " + hydroTest?.Status + "; " + konigSeatsTest?.Status + "; " + konigSeatAdjustmentsTest?.Status + "; " + dogProductTest?.Status + "; " + slotMachineTest?.Status + "; " + paintPanelsTest?.Status + "; " + windowTintTest?.Status + "; " + trashRecycleTest?.Status + "; " + trafficAuthorityTest?.Status + "; " + schoolBusPassengersTest?.Status + "; " + sprayPaintTest?.Status + "; " + steeringRackTest?.Status + "; " + vehicleInputResponseTest?.Status + "; " + guestVehicleSaveTest?.Status + "; " + vehiclePlatesTest?.Status + "; " + cheatsTest?.Status); return; }
            loader.Tick(gameplay);
            if (persistenceReloadOnly && loader.Ready)
            {
                persistenceReloadTest ??= new PairedPersistenceReloadTest(plugin);
                if (persistenceReloadTest.Tick(gameplay)) { Pass("Departed guest cached-profile persistence, orderly host save/exit, native host scene reload, guest menu rejoin and GUID-bound personal restoration passed."); return; }
                if (itemStatus != persistenceReloadTest.Status) { itemStatus = persistenceReloadTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (steeringRackOnly && phase == 75 && loader.Ready)
            {
                steeringRackTest ??= new PairedSteeringRackTest(plugin);
                if (steeringRackTest.Tick(gameplay))
                {
                    Pass(plugin.TestOptions.IsHost
                        ? "Steering rack: guest invoked all four native Konig tuning controls; the host accepted each one-step change, persisted Value1/Value2/IntValue1/IntValue2 through an isolated native save and menu reload, then restored its rack baseline."
                        : "Steering rack guest: all four native Konig tuning controls were echoed from the host, and disconnect restored the guest's original rack values.");
                    return;
                }
                if (itemStatus != steeringRackTest.Status)
                { itemStatus = steeringRackTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (vehiclePlatesOnly && phase == 79 && loader.Ready)
            {
                vehiclePlatesTest ??= new PairedVehiclePlatesTest(plugin);
                if (vehiclePlatesTest.Tick(gameplay))
                {
                    Pass("Vehicle plate save/reload/reconnect acceptance passed.");
                    return;
                }
                if (itemStatus != vehiclePlatesTest.Status)
                { itemStatus = vehiclePlatesTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (guestVehicleSaveOnly && phase == 78 && guestVehicleSaveTest != null)
            {
                if (guestVehicleSaveTest.Tick(gameplay))
                {
                    Pass("Guest vehicle persistence: guest-driven ATV and truck pose, ignition and RPM survived the coordinated host native save, host reload and guest reconnect; both driver leases were unowned after reconnect.");
                    return;
                }
                if (itemStatus != guestVehicleSaveTest.Status)
                { itemStatus = guestVehicleSaveTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            // This fixture intentionally stops the session while the lift is moving.
            // Its cached native state must still be checked after normal world teardown.
            if (phase == 39 && liftTest != null)
            {
                if (liftTest.Tick()) { Pass(liftTest.Evidence); return; }
                if (itemStatus != liftTest.Status) { itemStatus = liftTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (!loader.Ready || gameplay == null || Time.unscaledTime < nextAction) return;
            bool host = plugin.TestOptions.IsHost;
            if (phase == 215)
            {
                if (anxietyGameplayTest!.Tick()) { Pass("AnxietyGameplay: native clock saturation, item/smoke input callbacks, seat admission, controlled-physics rides and physical impacts; see anxiety-gameplay.tsv."); return; }
                if (itemStatus != anxietyGameplayTest.Status) { itemStatus = anxietyGameplayTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 214)
            {
                if (anxietyTest!.Tick()) { Pass("Anxiety: identical-input native effect/dispatcher probes matched host and client, including personal isolation; see anxiety-comparison.tsv. Vehicle rides, seat entry and actual item interactions require the separate manual matrix."); return; }
                if (itemStatus != anxietyTest.Status) { itemStatus = anxietyTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 213)
            {
                if (consoleChargerTest!.Tick()) { Pass("Portable console: guest native retrieval request reached host, both local native tip panels showed the localized binding/title and console state converged; cleanup passed."); return; }
                if (itemStatus != consoleChargerTest.Status) { itemStatus = consoleChargerTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 212)
            {
                if (shellSuspensionTest!.Tick()) { Pass("Shell suspension: native canonical manager membership, local tuning callbacks and exact wheel geometry compared on both peers; see per-car native measurements."); return; }
                if (itemStatus != shellSuspensionTest.Status) { itemStatus = shellSuspensionTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 211)
            {
                if (shellSeatsTest!.Tick()) { Pass("Shell seats: fresh native removable-seat installation, host/client seat and bolt gates, entry callbacks and restoration passed."); return; }
                if (itemStatus != shellSeatsTest.Status) { itemStatus = shellSeatsTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 210)
            {
                if (toolPacksTest!.Tick()) { Pass("Tool packs: native unleased wrench input, fast fresh-bolt mount, moved/thrown real pack travel and reacquisition/use preserved current body/release poses and output; cleanup passed."); return; }
                if (itemStatus != toolPacksTest.Status) { itemStatus = toolPacksTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 209)
            {
                if (tireInflationTest!.Tick()) { Pass("Tire inflation: actual guest scene hose input inflated a registered loose wheel on the host, consumed compressor air and replicated pressure; release, nozzle reuse, forged-generation rejection and cleanup passed."); return; }
                if (itemStatus != tireInflationTest.Status)
                { itemStatus = tireInflationTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 208)
            {
                if (trailerTowingTest!.Tick()) { Pass("Trailer towing: native truck driver leases, engine and wheel simulation moved the joined trailer for guest and host peer0; physical joint, wheel ownership, streamed motion and detach restoration passed."); return; }
                if (itemStatus != trailerTowingTest.Status)
                { itemStatus = trailerTowingTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 207)
            {
                if (equippedToolScaleTest!.Tick()) { Pass("Equipped tools: native chainsaw, shovel, torch, basket, tubing roll and spanner equip/unequip on both peers preserved first-person sizing and observer world scale."); return; }
                if (itemStatus != equippedToolScaleTest.Status)
                { itemStatus = equippedToolScaleTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 206)
            {
                if (passengerControlsTest!.Tick()) { Pass("Konig passenger controls: parked gear intent and both host/guest driver directions preserved driving authority; native ignition, lighting, gear and dome commands, stale-seat rejection and forced key release passed."); return; }
                if (itemStatus != passengerControlsTest.Status)
                { itemStatus = passengerControlsTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 202)
            {
                if (fishingPresentationTest!.Tick()) { Pass("Fishing current presentation: native equip, staged cast/caught endpoints, silent snapshot replay, end/release/local takeover and departure cleanup passed; actual casting and catch rewards remain manual."); return; }
                if (itemStatus != fishingPresentationTest.Status)
                { itemStatus = fishingPresentationTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 203)
            {
                if (underglowTest!.Tick()) { Pass("Native underglow presentation: RGB and pulse samples, zones, ignition, inactive sources, disconnect restoration and reconnect passed; physical purchase and installation remain manual."); return; }
                if (itemStatus != underglowTest.Status)
                { itemStatus = underglowTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 204)
            {
                if (travelingVanTest!.Tick()) { Pass("Native traveling van: schedule, visible height, host stock and materials, guest bolt purchase once, close/restock, repair and reconnect passed."); return; }
                if (itemStatus != travelingVanTest.Status)
                { itemStatus = travelingVanTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 205)
            {
                if (purchasedTruckDoorsTest!.Tick()) { Pass("Native purchased truck: guest purchase and exact debit, immediate driver/passenger door callbacks before discovery poll, live door and Konig hood sounds, silent repair and disconnect restoration passed."); return; }
                if (itemStatus != purchasedTruckDoorsTest.Status)
                { itemStatus = purchasedTruckDoorsTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 201)
            {
                if (nitrousRefillTest!.Tick())
                {
                    Pass("Native nitrous refill: host-owned bottle charge and wallet debit, guest gauge/loop and live attachment/removal cues, late reconnect without historical cues and original presentation restoration passed.");
                    return;
                }
                if (itemStatus != nitrousRefillTest.Status)
                { itemStatus = nitrousRefillTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 200)
            {
                if (fryerCookTest!.Tick())
                {
                    Pass("Native fryer cooking: host animation/steam/boiling phases reached the guest; late reconnect resumed scooping without historical cues; live close/open/scoop cues, generator interruption, stock authority and fixture restoration passed.");
                    return;
                }
                if (itemStatus != fryerCookTest.Status)
                { itemStatus = fryerCookTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 61)
            {
                if (plugin.World == null) return;
                actorRoutesTest ??= new PairedActorRoutesTest(plugin, host);
                if (actorRoutesTest.Tick())
                {
                    if (host) Pass("Actor route: host movement and active/walking snapshot reached the guest, the guest followed the same real route pose, and the stop state deactivated it; guest NPC interaction remained host-only.");
                    else Pass("Actor route guest: received host-authoritative movement and active/walking state, followed the host pose without local route ticks, and deactivated on the host stop snapshot.");
                    return;
                }
                if (itemStatus != actorRoutesTest.Status) { itemStatus = actorRoutesTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 65)
            {
                if (plugin.World == null) return;
                npcTalkTest ??= new PairedNpcTalkTest(plugin, gameplay, npcCoffeeGiftOnly);
                if (npcTalkTest.Tick())
                {
                    if (npcTalkTest.CoffeeGiftOnly)
                    {
                        Pass(host
                            ? "Carole coffee gift: the guest's native coffee trigger ran on the host; Carole friendship, daily gift state, consumed mug and captured custom greeting replicated, with guest state restored on disconnect."
                            : "Carole coffee gift: the patched guest coffee trigger was host-authoritative; Carole friendship, daily gift state, consumed mug and exact host greeting replicated, with guest state restored on disconnect.");
                        return;
                    }
                    Pass(npcTalkTest.TalkActionTested
                        ? (host
                            ? "Host NPC talk and gift: one guest Actor.MouseDown and one source-approved native syrup gift ran on the host; friendship/day state and the localized greeting replicated; a distant talk request was rejected, and guest state restored on disconnect."
                            : "NPC talk and gift: the patched guest Actor.MouseDown and Actor.OnTriggerEnter were host-authoritative; friendship/day state and localized greeting replicated; distant talk was rejected and guest state restored on disconnect.")
                        : (host
                            ? "Host NPC gift: one guest Actor.OnTriggerEnter for a source-approved base syrup recipient ran on the host; friendship/day state and item removal replicated without invoking that actor's custom dialog callbacks."
                            : "NPC gift: the patched guest Actor.OnTriggerEnter was host-authoritative; friendship/day state and item removal replicated without invoking custom dialog callbacks; disconnect restored the guest's original friendship/day arrays."));
                    return;
                }
                if (itemStatus != npcTalkTest.Status)
                { itemStatus = npcTalkTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 66)
            {
                if (plugin.World == null) return;
                npcSubclassTest ??= new PairedNpcSubclassTest(plugin, gameplay, ericMilestone: npcEricMilestoneOnly);
                if (npcSubclassTest.Tick())
                {
                    Pass(npcEricMilestoneOnly
                        ? (host
                            ? "NPC Eric milestone: the host applied a real guest Actor_Eric click, advanced friendship to 50, ran the exact milestone greeting, rejected distant talk, and restored the fixture state."
                            : "NPC Eric milestone: the host's friendship-50 greeting was captured and shown on the guest; distant talk was rejected and disconnect restored the guest baseline.")
                        : (host
                            ? "NPC subclass: the host applied a real guest Actor_Jacques click, advanced friendship to 25, ran the custom milestone greeting, rejected distant talk, and restored the fixture state."
                            : "NPC subclass: the host's Actor_Jacques friendship-25 greeting was captured and shown exactly on the guest; distant talk was rejected and disconnect restored the guest baseline."));
                    return;
                }
                if (itemStatus != npcSubclassTest.Status)
                { itemStatus = npcSubclassTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 67)
            {
                if (plugin.World == null) return;
                schoolBusPoseTest ??= new PairedSchoolBusPoseTest(plugin);
                if (schoolBusPoseTest.Tick())
                {
                    Pass(host
                        ? "School bus pose: host native authority staged and restored a reversible sleeping bus pose after the guest acknowledged smoothed vehicle, Richard and master-door presentation."
                        : "School bus pose guest: local bus AI was suppressed, vehicle/Richard/master-door presentation interpolated to the host and followed the restored pose.");
                    return;
                }
                if (itemStatus != schoolBusPoseTest.Status)
                { itemStatus = schoolBusPoseTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 91)
            {
                if (chatTest!.Tick()) { Pass("Chat: native T/Sorry preserved, Enter opening, input suspension, Esc cancellation, guest caption with authenticated name on host, host echo to sender and host caption on guest, native save overlap with preserved drafts/action gates, and native death/recovery input restoration passed."); return; }
                if (itemStatus != chatTest.Status) { itemStatus = chatTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 90)
            {
                if (meetTest!.Tick())
                {
                    if (host && plugin.Session!.Peers.Count > 1) return;
                    Pass(meetTest.Evidence);
                    return;
                }
                if (itemStatus != meetTest.Status) { itemStatus = meetTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 89)
            {
                if (syrupCrateTest!.Tick())
                {
                    if (host && plugin.Session!.Peers.Count > 1) return;
                    if (!host) plugin.EndPairedTestSession();
                    Pass("SyrupCrate: a guest took a full syrup can out of a host crate, kept it in hand, drank it and kept the empty can, each shared by the host under the guest's lease.");
                    return;
                }
                if (itemStatus != syrupCrateTest.Status) { itemStatus = syrupCrateTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 88)
            {
                if (extraSeatsTest!.Tick())
                {
                    Pass(host
                        ? "Extra seats: the host granted the guest a truck-bed seat, a cab seat, an ATV rider seat and a bar stool, pinned the guest avatar to each, and released them on stand-up and disconnect. " + extraSeatsTest.Status
                        : "Extra seats guest: native chair clicks seated the guest in the truck bed, the cab, behind the ATV driver and on a bar stool through host grants; clicking again stood up, and leaving the session stood the guest up.");
                    return;
                }
                if (itemStatus != extraSeatsTest.Status)
                { itemStatus = extraSeatsTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 72)
            {
                schoolBusPassengersTest ??= new PairedSchoolBusPassengersTest(plugin, gameplay);
                if (schoolBusPassengersTest.Tick())
                {
                    Pass(host
                        ? "School bus passengers: host authorized two native passenger seats, staged and restored the bus pose while the guest avatar followed its seat anchor, and released guest ownership on save flush and disconnect."
                        : "School bus passengers guest: distant entry was rejected, host grant entered a native passenger seat, interpolated bus movement carried the seat anchor, save flush restored walking, and disconnect cleanup restored the second seat.");
                    return;
                }
                if (itemStatus != schoolBusPassengersTest.Status)
                { itemStatus = schoolBusPassengersTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 68)
            {
                if (remoteEngineAudioTest!.Tick())
                {
                    plugin.EndPairedTestSession();
                    Pass(host
                        ? "Remote vehicle engine audio: host received a guest-driven shared ATV with replicated ignition, RPM and throttle; only the NWH engine-running source started and followed remote RPM/throttle while the vehicle remained kinematic. " + remoteEngineAudioTest.Status
                        : "Remote vehicle engine audio: guest started and revved the shared ATV in neutral, then released its driver lease after the paired host playback check.");
                    return;
                }
                if (itemStatus != remoteEngineAudioTest.Status)
                { itemStatus = remoteEngineAudioTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 69)
            {
                npcWeedSaleTest ??= new PairedNpcWeedSaleTest(plugin, gameplay);
                if (npcWeedSaleTest.Tick())
                {
                    Pass("NPC weed sale: host and guest staged at source-reviewed " + npcWeedSaleTest.BuyerType + " until native dealer-distance activation converged, then a guest-carried big pre-roll pack was rejected out of range and sold once through the host native trigger; the $500 payment, daily purchase flag and item removal replicated, and both player positions plus the guest baseline restored.");
                    return;
                }
                if (itemStatus != npcWeedSaleTest.Status)
                { itemStatus = npcWeedSaleTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 70)
            {
                npcRouteTalkTest ??= new PairedNpcRouteTalkTest(plugin, gameplay);
                if (npcRouteTalkTest.Tick())
                {
                    Pass(host
                        ? "Route-bound NPC talk: an inactive structurally safe route actor was activated with TestStart; guest pose convergence preceded native Actor.MouseDown, friendship/day/greeting replicated, distant talk was rejected, then TestStop restored host route activity and pose."
                        : "Route-bound NPC talk: the guest waited for the host route actor's active pose, native Actor.MouseDown was suppressed and run once on the host, friendship/day/greeting replicated, distant talk was rejected, and disconnect restored the guest route pose.");
                    return;
                }
                if (itemStatus != npcRouteTalkTest.Status)
                { itemStatus = npcRouteTalkTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 71)
            {
                if (plugin.World == null) return;
                martinCarPoseTest ??= new PairedMartinCarPoseTest(plugin);
                if (martinCarPoseTest.Tick())
                {
                    Pass(host
                        ? "Martin car pose: host routed AI remained authoritative while the guest acknowledged interpolated car pose and wheel samples plus synchronized authored run visuals; the host capture was restored; native passenger entry, occupied-seat contention, ownership flush and second-entry disconnect evacuation passed."
                        : "Martin car pose guest: local routed AI stayed suppressed while host pose and wheel samples interpolated, authored run visuals synchronized; native door/seat entry, flush exit and second-entry disconnect restored walking.");
                    return;
                }
                if (itemStatus != martinCarPoseTest.Status)
                { itemStatus = martinCarPoseTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 74)
            {
                if (sprayPaintTest!.Tick())
                {
                    if (host) { phase = 7; return; }
                    Pass("Physical spray paint: a guest leased a real native spray can and invoked native MouseDown/MouseUp; host ray validation painted the camera-hit panel despite a stale native target; distant and Default-layer-occluded rays left the panel unchanged; an installed Generated_Part child was also painted when available; unleased native input was suppressed; disconnect restored guest baselines.");
                    return;
                }
                if (itemStatus != sprayPaintTest.Status)
                { itemStatus = sprayPaintTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 80)
            {
                if (rustRepairKitTest!.Tick())
                {
                    if (host) { phase = 7; return; }
                    Pass("Rust repair kit: guest equipped native leased kits and invoked native MouseDown; the host repaired and replicated a rusted panel and tombstoned the consumed kit, rejected a stale generation, preserved a kit on a no-change repair, and restored guest panel/item state on disconnect.");
                    return;
                }
                if (itemStatus != rustRepairKitTest.Status)
                { itemStatus = rustRepairKitTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 81)
            {
                if (dentRepairKitTest!.Tick())
                {
                    if (host) { phase = 7; return; }
                    Pass("Dent repair kit: guest used one leased native kit through its MouseDown path; the host repaired a staged dent, then derived the 10-click/3-second damage progression after the 1.5-second repair gate; the reusable kit stayed leased through both changes and disconnect restored guest panel and item state.");
                    return;
                }
                if (itemStatus != dentRepairKitTest.Status)
                { itemStatus = dentRepairKitTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 216 || phase == 217)
            {
                bool complete = phase == 216 ? productionSettlementTest!.Tick() : tireReplayTest!.Tick();
                string status = phase == 216 ? productionSettlementTest!.Status : tireReplayTest!.Status;
                if (complete) { Pass("Native paired " + (phase == 216 ? "production settlement" : "tire replay") + ": " + status); return; }
                if (itemStatus != status) { itemStatus = status; File.WriteAllText(resultPath, "RUNNING: " + status); }
                return;
            }
            if (phase == 0)
            {
                if (preownedTruckWindows)
                {
                    if (!windowsOnly) throw new InvalidOperationException("Preowned purchased truck modifier requires the Windows suite.");
                    if (plugin.Session?.IsRunning != true && !PairedPurchasedTruckDoorsTest.PreparePreownedWindows(host)) { nextAction = Time.unscaledTime + 0.05f; return; }
                }
                if (shellSeatsOnly || shellSuspensionOnly) ShellAssemblyTestFixture.Prepare();
                if (partsOnly) PartsTuneBaselineFixture.Prepare(host);
                if (host && repairAccessoryOnly && rustRepairKitOnly) RepairAccessoryFixture.PrepareRust();
                if (trailerTowingOnly) PairedTrailerAuthorityTest.PrepareFixture(Vehicle_Type.OlTruck);
                if (trailerAuthorityOnly || trailerCouplerOnly) PairedTrailerAuthorityTest.PrepareFixture();
                if (guestVehicleSaveOnly && plugin.Session?.IsRunning != true) PairedGuestVehicleSaveTest.Prepare(host);
                if (purchasedTruckDoorsOnly) PairedPurchasedTruckDoorsTest.Prepare(host);
                // The focused Buggy fixture is deliberately an unbuilt baseline. Native saves
                // can still have activated the vehicle before this paired session is created;
                // normalize each isolated copy here so its initial world catalog is stable.
                if (buggyBuildOnly) PairedBuggyBuildTest.PrepareIsolatedFixture();
                if (racecarBuildOnly) PairedRacecarBuildTest.PrepareIsolatedFixture();
                if (flushOnly || profileOnly || saveRoundOnly || coordinatedSaveOnly || consumptionOnly) PairedFlushTest.Prepare();
                if (progressOnly) PairedProgressTest.Prepare(host);
                if (storesOnly) PairedStoresTest.Prepare(host);
                if (metalSaleOnly) PairedMetalSaleTest.Prepare(host);
                if (trashRecycleOnly) PairedTrashRecycleTest.Prepare();
                if (scrapyardGateOnly) PairedScrapyardGateTest.Prepare(gameplay, host);
                if (economyOnly) PairedEconomyTest.Prepare(host);
                if (weatherOnly) PairedWeatherTest.Prepare(host);
                if (clockOnly) PairedClockTest.Prepare(gameplay, host);
                if (meetOnly) PairedMeetTest.Prepare(host);
                if (generatorOnly) PairedGeneratorTest.Prepare(gameplay, host);
                if (hydroOnly) PairedHydroTest.Prepare(host);
                if (cottagePurchaseOnly) PairedCottagePurchaseTest.Prepare(host);
                if (dogProductOnly) PairedDogProductTest.Prepare(host);
                if (leoCarBedOnly) PairedLeoCarBedTest.Prepare(gameplay);
                else if (leoOnly || leoTravelOnly) PairedLeoTest.Prepare(gameplay, host, leoTravelOnly);
                if (leoSuppliesOnly) PairedLeoSuppliesTest.Prepare(gameplay, host);
                if (slotMachinesOnly) PairedSlotMachineTest.Prepare(host);
                if (foodOnly) gameplay.Get_isHardMode = true;
                if (!host) { phase = 1; nextAction = Time.unscaledTime + 7; return; }
                // HostSaveCoordinator anchors the selected native save during session setup.
                // Select fixture saves before startup so later native save requests stay inside
                // the coordinated session.
                if (coordinatedSaveOnly) PairedCoordinatedSaveTest.PrepareHostSave();
                if (productionSettlementOnly) PairedProductionSettlementTest.PrepareHostSave();
                if (steeringRackOnly) steeringRackTest ??= new PairedSteeringRackTest(plugin);
                if (vehiclePlatesOnly) vehiclePlatesTest ??= new PairedVehiclePlatesTest(plugin);
                plugin.BeginPairedTestSession();
                if (plugin.Session?.IsHost != true || plugin.Lights == null) throw new InvalidOperationException("Test host did not start.");
                if (vehiclePlatesOnly) vehiclePlatesTest!.AttachCommandObserver();
                if (guestVehicleSaveOnly) { if (!plugin.World!.PersonalProfileReady) return; guestVehicleSaveTest = new PairedGuestVehicleSaveTest(plugin); phase = 78; File.WriteAllText(resultPath, "RUNNING: guest vehicle lease flush, native save/reload and reconnect"); return; }
                if (trafficAuthorityOnly) { if (!plugin.World!.PersonalProfileReady) return; trafficAuthorityTest = new PairedTrafficAuthorityTest(plugin, gameplay); phase = 62; File.WriteAllText(resultPath, "RUNNING: host-authoritative Gley traffic slots, guest suppression and presentation"); return; }
                if (remoteEngineAudioOnly) { if (plugin.World?.PersonalProfileReady != true) return; remoteEngineAudioTest = new PairedRemoteVehicleEngineAudioTest(plugin, gameplay); phase = 68; File.WriteAllText(resultPath, "RUNNING: host waiting for guest ATV RPM, throttle and remote engine audio"); return; }
                if (actorRoutesOnly) { phase = 61; nextAction = Time.unscaledTime + 1; return; }
                if (npcTalkOnly) { if (!plugin.World!.PersonalProfileReady) return; npcTalkTest = new PairedNpcTalkTest(plugin, gameplay); phase = 65; File.WriteAllText(resultPath, "RUNNING: host-authoritative NPC talk, rejection, guest suppression and rollback"); return; }
                if (npcCoffeeGiftOnly) { if (!plugin.World!.PersonalProfileReady) return; npcTalkTest = new PairedNpcTalkTest(plugin, gameplay, coffeeGiftOnly: true); phase = 65; File.WriteAllText(resultPath, "RUNNING: host-authoritative Actor_Carole coffee gift, custom greeting replication and rollback"); return; }
                if (npcSubclassOnly || npcEricMilestoneOnly) { if (!plugin.World!.PersonalProfileReady) return; npcSubclassTest = new PairedNpcSubclassTest(plugin, gameplay, ericMilestone: npcEricMilestoneOnly); phase = 66; File.WriteAllText(resultPath, npcEricMilestoneOnly ? "RUNNING: host-authoritative Actor_Eric friendship-50 greeting, natural activation, range rejection and rollback" : "RUNNING: host-authoritative Actor_Jacques milestone greeting, range rejection and rollback"); return; }
                if (npcWeedSaleOnly) { if (!plugin.World!.PersonalProfileReady) return; npcWeedSaleTest = new PairedNpcWeedSaleTest(plugin, gameplay); phase = 69; File.WriteAllText(resultPath, "RUNNING: host-authoritative NPC weed sale, range rejection and shared payout"); return; }
                if (npcRouteTalkOnly) { if (!plugin.World!.PersonalProfileReady) return; npcRouteTalkTest = new PairedNpcRouteTalkTest(plugin, gameplay); phase = 70; File.WriteAllText(resultPath, "RUNNING: host-authoritative talk on an exact-gate route-bound Actor, range rejection and rollback"); return; }
                if (schoolBusOnly) { if (!plugin.World!.PersonalProfileReady) return; schoolBusPoseTest = new PairedSchoolBusPoseTest(plugin); phase = 67; File.WriteAllText(resultPath, "RUNNING: host-authoritative school bus test-pose snapshot, Richard and master-door interpolation"); return; }
                if (syrupCrateOnly) { if (!plugin.World!.PersonalProfileReady) return; syrupCrateTest = new PairedSyrupCrateTest(plugin, gameplay); phase = 89; return; }
                if (productionSettlementOnly) { if (!plugin.World!.PersonalProfileReady) return; productionSettlementTest = new PairedProductionSettlementTest(plugin, gameplay); phase = 216; return; }
                if (tireReplayOnly) { if (!plugin.World!.PersonalProfileReady) return; tireReplayTest = new PairedTireReplayTest(plugin, gameplay); phase = 217; return; }
                if (meetOnly) { if (!plugin.World!.PersonalProfileReady) return; meetTest = new PairedMeetTest(plugin); phase = 90; return; }
                if (chatOnly) { if (!plugin.World!.PersonalProfileReady) return; chatTest = new PairedChatTest(plugin); phase = 91; return; }
                if (extraSeatsOnly) { if (!plugin.World!.PersonalProfileReady) return; extraSeatsTest = new PairedExtraSeatsTest(plugin, gameplay); phase = 88; File.WriteAllText(resultPath, "RUNNING: extra truck and bar-stool seats"); return; }
                if (schoolBusPassengersOnly) { if (!plugin.World!.PersonalProfileReady) return; schoolBusPassengersTest = new PairedSchoolBusPassengersTest(plugin, gameplay); phase = 72; File.WriteAllText(resultPath, "RUNNING: host-authoritative SchoolBus passenger claims, moving anchors, save flush and disconnect release"); return; }
                if (truckDoorsOnly) { if (!plugin.World!.PersonalProfileReady) return; vehiclesTest = new PairedVehiclesTest(plugin, gameplay, true, true); phase = 73; File.WriteAllText(resultPath, "RUNNING: truck door host authority, guest interpolation and local hinge teardown"); return; }
                if (steeringRackOnly) { if (!plugin.World!.PersonalProfileReady) return; steeringRackTest ??= new PairedSteeringRackTest(plugin); phase = 75; File.WriteAllText(resultPath, "RUNNING: guest native Konig steering-rack controls, host echo, rollback and isolated save/reload"); return; }
                if (vehicleInputResponseOnly) { if (!plugin.World!.PersonalProfileReady) return; vehicleInputResponseTest = new PairedVehicleInputResponseTest(plugin, gameplay); phase = 76; File.WriteAllText(resultPath, "RUNNING: host waiting for the guest's input-driven vehicle response and manual gear shift"); return; }
                if (martinCarOnly) { if (!plugin.World!.PersonalProfileReady) return; martinCarPoseTest = new PairedMartinCarPoseTest(plugin); phase = 71; File.WriteAllText(resultPath, "RUNNING: host-authoritative Martin car pose, wheel and run-visual presentation"); return; }
                if (generatorOnly) { if (!plugin.World!.PersonalProfileReady) return; generatorTest = new PairedGeneratorTest(plugin, gameplay); phase = 43; return; }
                if (hydroOnly) { if (!plugin.World!.PersonalProfileReady) return; hydroTest = new PairedHydroTest(plugin); phase = 48; File.WriteAllText(resultPath, "RUNNING: host hydro availability replication and guest native power query"); return; }
                if (avatarOnly) { if (!plugin.World!.PersonalProfileReady) return; avatarCrouchTest = new PairedAvatarCrouchTest(plugin); phase = 49; return; }
                if (buggyBuildOnly) { if (!plugin.World!.PersonalProfileReady) return; buggyBuildTest = new PairedBuggyBuildTest(plugin, gameplay); phase = 44; return; }
                if (racecarBuildOnly) { if (!plugin.World!.PersonalProfileReady) return; racecarBuildTest = new PairedRacecarBuildTest(plugin, gameplay); phase = 82; File.WriteAllText(resultPath, "RUNNING: host-authoritative Speedway reward claims and three-pack Racecar construction"); return; }
                if (cottagePurchaseOnly) { if (!plugin.World!.PersonalProfileReady) return; cottagePurchaseTest = new PairedCottagePurchaseTest(plugin, gameplay, plugin.World.CottagePurchase); phase = 45; return; }
                if (dogProductOnly) { if (!plugin.World!.PersonalProfileReady) return; dogProductTest = new PairedDogProductTest(plugin, gameplay); phase = 50; return; }
                if (leoCarBedOnly) { if (!plugin.World!.PersonalProfileReady) return; leoCarBedTest = new PairedLeoCarBedTest(plugin, gameplay); phase = 53; File.WriteAllText(resultPath, "RUNNING: host-authoritative Leo Konig car-bed enter/exit and rollback"); return; }
                if (leoOnly || leoTravelOnly) { if (!plugin.World!.PersonalProfileReady) return; leoTest = new PairedLeoTest(plugin, gameplay, leoTravelOnly); phase = 53; return; }
                if (leoSuppliesOnly) { if (!plugin.World!.PersonalProfileReady) return; leoSuppliesTest = new PairedLeoSuppliesTest(plugin, gameplay); phase = 64; File.WriteAllText(resultPath, "RUNNING: Leo bowl watering and bunker fertilizer host-authority fixture"); return; }
                if (konigSeatsOnly) { if (!plugin.World!.PersonalProfileReady) return; konigSeatsTest = new PairedKonigSeatsTest(plugin, gameplay); phase = 51; return; }
                if (konigSeatAdjustmentsOnly) { if (!plugin.World!.PersonalProfileReady) return; konigSeatAdjustmentsTest = new PairedKonigSeatAdjustmentsTest(plugin, gameplay); phase = 63; return; }
                if (slotMachinesOnly) { if (!plugin.World!.PersonalProfileReady) return; slotMachineTest = new PairedSlotMachineTest(plugin, gameplay); phase = 52; return; }
                if (paintPanelsOnly) { if (!plugin.World!.PersonalProfileReady) return; paintPanelsTest = new PairedPaintPanelsTest(plugin, gameplay); phase = 54; return; }
                if (sprayPaintOnly) { if (!plugin.World!.PersonalProfileReady) return; sprayPaintTest = new PairedSprayPaintTest(plugin, gameplay); phase = 74; File.WriteAllText(resultPath, "RUNNING: host-authoritative physical spray-can mouse path and camera ray validation"); return; }
                if (rustRepairKitOnly) { if (!plugin.World!.PersonalProfileReady) return; rustRepairKitTest = new PairedRustRepairKitTest(plugin, gameplay, associatedAccessory: repairAccessoryOnly, truckGrilleOnly: truckGrilleRustOnly); phase = 80; File.WriteAllText(resultPath, "RUNNING: host-authoritative native rust repair kit lease, repair, no-change and stale-generation fixture"); return; }
                if (dentRepairKitOnly) { if (!plugin.World!.PersonalProfileReady) return; dentRepairKitTest = new PairedDentRepairKitTest(plugin, gameplay, associatedAccessory: repairAccessoryOnly); phase = 81; File.WriteAllText(resultPath, "RUNNING: host-authoritative native dent repair kit lease, repair and damage progression fixture"); return; }
                if (windowTintOnly) { if (!plugin.World!.PersonalProfileReady) return; windowTintTest = new PairedWindowTintTest(plugin, gameplay); phase = 56; return; }
                if (trailerAuthorityOnly) { if (!plugin.World!.PersonalProfileReady) return; trailerAuthorityTest = new PairedTrailerAuthorityTest(plugin, gameplay); phase = 55; File.WriteAllText(resultPath, "RUNNING: guest trailer attach/detach requests and distant-range rejection"); return; }
                if (trailerCouplerOnly) { if (!plugin.World!.PersonalProfileReady) return; trailerAuthorityTest = new PairedTrailerAuthorityTest(plugin, gameplay, nativeCouplerInput: true); phase = 77; File.WriteAllText(resultPath, "RUNNING: phase 77 guest native trailer coupler clicks and host range/ownership gates"); return; }
                if (vehiclePlatesOnly) { if (!plugin.World!.PersonalProfileReady) return; vehiclePlatesTest ??= new PairedVehiclePlatesTest(plugin); phase = 79; File.WriteAllText(resultPath, "RUNNING: phase 79 host-authoritative vehicle plate clicks, coordinated save/reload and guest reconnect snapshot"); return; }
                if (windowsOnly) { if (!plugin.World!.PersonalProfileReady) return; windowsTest = new PairedWindowsTest(plugin, gameplay, preownedTruckWindows); phase = 25; return; }
                if (doorsOnly) { if (!plugin.World!.PersonalProfileReady) return; doorsTest = new PairedDoorsTest(plugin, gameplay); phase = 26; return; }
                if (collectiblesOnly) { if (!plugin.World!.PersonalProfileReady) return; collectiblesTest = new PairedCollectiblesTest(plugin, gameplay); phase = 27; return; }
                if (sleepOnly) { if (!plugin.World!.PersonalProfileReady) return; sleepTest = new PairedSleepTest(plugin, gameplay); phase = 28; return; }
                if (partsOnly) { if (!plugin.World!.PersonalProfileReady) return; partsTest = new PairedVehiclePartsTest(plugin); phase = 29; return; }
                if (accessOnly) { if (!plugin.World!.PersonalProfileReady) return; accessTest = new PairedVehicleAccessTest(plugin, gameplay); phase = 30; return; }
                if (boltsOnly) { if (!plugin.World!.PersonalProfileReady) return; boltsTest = new PairedBoltsTest(plugin); phase = 31; return; }
                if (radiosOnly) { if (!plugin.World!.PersonalProfileReady) return; radiosTest = new PairedRadiosTest(plugin, gameplay); phase = 83; return; }
                if (towingOnly) { if (!plugin.World!.PersonalProfileReady) return; towingTest = new PairedTowingTest(plugin, gameplay); phase = 84; return; }
                if (chainsawOnly) { if (!plugin.World!.PersonalProfileReady) return; chainsawTest = new PairedChainsawTest(plugin, gameplay); phase = 85; return; }
                if (salesOnly) { if (!plugin.World!.PersonalProfileReady) return; saleTest = new PairedSaleTest(plugin, gameplay); phase = 86; return; }
                if (saleCirclesOnly) { if (!plugin.World!.PersonalProfileReady) return; saleCirclesTest = new PairedSaleCirclesTest(plugin, gameplay); phase = 92; return; }
                if (storageOnly) { if (!plugin.World!.PersonalProfileReady) return; storageTest = new PairedStorageGarageTest(plugin, gameplay); phase = 87; return; }
                if (worldItemsOnly) { if (!plugin.World!.PersonalProfileReady) return; worldItemsTest = new PairedWorldItemsTest(plugin, gameplay); phase = 32; return; }
                if (fishBucketsOnly) { if (!plugin.World!.PersonalProfileReady) return; fishBucketsTest = new PairedFishBucketsTest(plugin, gameplay); phase = 60; return; }
                if (fuelOnly) { if (!plugin.World!.PersonalProfileReady) return; fuelTest = new PairedFuelTest(plugin, gameplay); phase = 33; return; }
                if (garageOnly) { if (!plugin.World!.PersonalProfileReady) return; garageTest = new PairedGarageTest(plugin, gameplay); phase = 34; return; }
                if (cheatsOnly) { if (!plugin.World!.PersonalProfileReady) return; cheatsTest = new PairedCheatsTest(plugin, gameplay); phase = 218; return; }
                if (metalSaleOnly) { if (!plugin.World!.PersonalProfileReady) return; metalSaleTest = new PairedMetalSaleTest(plugin, gameplay); phase = 46; return; }
                if (vrHandsOnly) { if (!plugin.World!.PersonalProfileReady) return; vrHandsTest = new PairedVrHandsTest(plugin, gameplay); phase = 500; return; }
                if (heldItemsOnly) { if (!plugin.World!.PersonalProfileReady) return; heldItemInterpolationTest = new PairedHeldItemInterpolationTest(plugin, gameplay); phase = 58; return; }
                if (trashRecycleOnly) { if (!plugin.World!.PersonalProfileReady) return; trashRecycleTest = new PairedTrashRecycleTest(plugin, gameplay); phase = 57; return; }
                if (scrapyardGateOnly) { if (!plugin.World!.PersonalProfileReady) return; scrapyardGateTest = new PairedScrapyardGateTest(plugin, gameplay); phase = 47; return; }
                if (forestOnly) { if (!plugin.World!.PersonalProfileReady) return; forestTest = new PairedForestTest(plugin, gameplay); phase = 35; return; }
                if (gardenOnly) { if (!plugin.World!.PersonalProfileReady) return; gardenTest = new PairedGardenTest(plugin, gameplay); phase = 36; return; }
                if (fleetOnly) { if (!plugin.World!.PersonalProfileReady) return; fleetTest = new PairedFleetPartsTest(plugin); phase = 37; return; }
                if (growthOnly) { if (!plugin.World!.PersonalProfileReady) return; growthTest = new PairedForestGrowthTest(plugin, gameplay); phase = 38; return; }
                if (liftOnly) { if (!plugin.World!.PersonalProfileReady) return; liftTest = new PairedCarLiftTest(plugin, gameplay); phase = 39; return; }
                if (planterOnly) { if (!plugin.World!.PersonalProfileReady) return; planterTest = new PairedPlanterTest(plugin, gameplay); phase = 40; return; }
                if (shackOnly) { if (!plugin.World!.PersonalProfileReady) return; shackTest = new PairedSugarShackTest(plugin, gameplay); phase = 41; return; }
                if (tubingOnly) { if (!plugin.World!.PersonalProfileReady) return; tubingTest = new PairedTubingTest(plugin, gameplay); phase = 42; return; }
                if (auditOnly) { if (!plugin.World!.PersonalProfileReady) return; auditTest = new PairedInteractionAuditTest(plugin); phase = 24; return; }
                if (vehiclesOnly) { if (!plugin.World!.PersonalProfileReady) return; vehiclesTest = new PairedVehiclesTest(plugin, gameplay); phase = 9; return; }
                if (vehiclePoseOnly) { if (!plugin.World!.PersonalProfileReady) return; vehiclePoseTest = new PairedVehiclePoseTest(plugin, gameplay); phase = 59; return; }
                if (consumptionOnly) { if (!plugin.World!.PersonalProfileReady) return; consumptionTest = new PairedConsumptionTest(plugin, gameplay); phase = 8; return; }
                if (coordinatedSaveOnly) { coordinatedSaveTest = new PairedCoordinatedSaveTest(plugin); phase = 23; return; }
                if (saveRoundOnly) { saveRoundTest = new PairedSaveRoundTest(plugin, gameplay); phase = 22; return; }
                if (profileOnly) { profileTest = new PairedProfileTest(plugin); phase = 21; return; }
                if (flushOnly) { flushTest = new PairedFlushTest(plugin, gameplay); phase = 20; return; }
                if (progressOnly) { progressTest = new PairedProgressTest(plugin); phase = 19; return; }
                if (storesOnly) { storesTest = new PairedStoresTest(plugin); phase = 18; return; }
                if (economyOnly) { economyTest = new PairedEconomyTest(plugin); phase = 17; return; }
                if (weatherOnly) { weatherTest = new PairedWeatherTest(plugin); phase = 16; return; }
                if (clockOnly) { clockTest = new PairedClockTest(plugin, gameplay); phase = 15; return; }
                if (anxietyGameplayOnly) { if (!plugin.World!.PersonalProfileReady) return; anxietyGameplayTest = new PairedAnxietyGameplayTest(plugin, gameplay, resultPath); phase = 215; return; }
                if (anxietyOnly) { if (!plugin.World!.PersonalProfileReady) return; anxietyTest = new PairedAnxietyTest(plugin, gameplay, resultPath); phase = 214; return; }
                if (nitrousRefillOnly) { if (!plugin.World!.PersonalProfileReady) return; nitrousRefillTest = new PairedNitrousRefillTest(plugin, gameplay); phase = 201; return; }
                if (tireInflationOnly) { if (!plugin.World!.PersonalProfileReady) return; tireInflationTest = new PairedTireInflationTest(plugin, gameplay); phase = 209; return; }
                if (trailerTowingOnly) { if (!plugin.World!.PersonalProfileReady) return; trailerTowingTest = new PairedTrailerTowingTest(plugin, gameplay); phase = 208; return; }
                if (equippedToolScaleOnly) { if (!plugin.World!.PersonalProfileReady) return; equippedToolScaleTest = new PairedEquippedToolScaleTest(plugin, gameplay); phase = 207; return; }
                if (consoleChargerOnly) { if (!plugin.World!.PersonalProfileReady) return; consoleChargerTest = new PairedConsoleChargerTest(plugin, gameplay); phase = 213; return; }
                if (toolPacksOnly) { if (!plugin.World!.PersonalProfileReady) return; toolPacksTest = new PairedToolPacksTest(plugin, gameplay); phase = 210; return; }
                if (shellSeatsOnly) { if (!plugin.World!.PersonalProfileReady) return; shellSeatsTest = new PairedShellSeatsTest(plugin, gameplay); phase = 211; return; }
                if (shellSuspensionOnly) { if (!plugin.World!.PersonalProfileReady) return; shellSuspensionTest = new PairedShellSuspensionTest(plugin, gameplay); phase = 212; return; }
                if (passengerControlsOnly) { if (!plugin.World!.PersonalProfileReady) return; passengerControlsTest = new PairedPassengerVehicleControlsTest(plugin, gameplay); phase = 206; return; }
                if (fishingPresentationOnly) { if (!plugin.World!.PersonalProfileReady) return; fishingPresentationTest = new PairedFishingPresentationTest(plugin, gameplay); phase = 202; return; }
                if (underglowOnly) { if (!plugin.World!.PersonalProfileReady) return; underglowTest = new PairedUnderglowTest(plugin, gameplay); phase = 203; return; }
                if (travelingVanOnly) { if (!plugin.World!.PersonalProfileReady) return; travelingVanTest = new PairedTravelingVanTest(plugin, gameplay); phase = 204; return; }
                if (purchasedTruckDoorsOnly) { if (!plugin.World!.PersonalProfileReady) return; purchasedTruckDoorsTest = new PairedPurchasedTruckDoorsTest(plugin, gameplay); phase = 205; return; }
                if (fryerCookOnly) { if (!plugin.World!.PersonalProfileReady) return; fryerCookTest = new PairedFryerCookTest(plugin, gameplay); phase = 200; return; }
                if (foodOnly) { foodTest = new PairedFoodTest(plugin, gameplay); phase = 14; return; }
                if (decorationsOnly) { decorationsTest = new PairedDecorationsTest(plugin, gameplay); phase = 13; return; }
                if (itemsOnly) { lanternTest = new PairedLanternTest(plugin, gameplay); phase = 12; return; }
                entity = plugin.Lights.TestEntity; plugin.Lights.TestClick(entity);
                if (plugin.Lights.TestRecord(entity).Revision < 2) throw new InvalidOperationException("Host toggle was not committed.");
                phase = 2;
                File.WriteAllText(resultPath, "RUNNING: host baseline changed; waiting for late join and guest command");
            }
            if (!host && phase == 1)
            {
                if (plugin.Session?.IsRunning != true)
                {
                    if (!plugin.DiscoverPairedHost()) return;
                    if (preownedTruckWindows) PairedPurchasedTruckDoorsTest.AssertPreownedGuestBeforeJoin(plugin);
                    if (trailerTowingOnly) PairedTrailerAuthorityTest.PrepareFixture(Vehicle_Type.OlTruck);
                if (trailerAuthorityOnly || trailerCouplerOnly) PairedTrailerAuthorityTest.PrepareFixture();
                    plugin.BeginPairedTestSession(); nextAction = Time.unscaledTime + 1; return;
                }
                if (plugin.Lights?.Ready != true) return;
                // The one-time reconnect placement lands after full synchronization and can undo a fixture's
                // teleport, so host reach checks refuse the guest. Wait for it, bounded for a guest that never stands.
                if (plugin.World?.ReconnectPositions?.Settled == false)
                {
                    if (placementWaitSince == 0) placementWaitSince = Time.unscaledTime;
                    if (Time.unscaledTime - placementWaitSince < 35) return;
                }
                if (racecarBuildOnly) { if (!plugin.World!.PersonalProfileReady) return; racecarBuildTest = new PairedRacecarBuildTest(plugin, gameplay); phase = 82; File.WriteAllText(resultPath, "RUNNING: guest observing native Speedway rewards and Racecar construction"); return; }
                if (trafficAuthorityOnly) { if (!plugin.World!.PersonalProfileReady) return; trafficAuthorityTest = new PairedTrafficAuthorityTest(plugin, gameplay); phase = 62; File.WriteAllText(resultPath, "RUNNING: host-authoritative Gley traffic slots, guest suppression and presentation"); return; }
                if (remoteEngineAudioOnly) { if (plugin.World?.PersonalProfileReady != true) return; remoteEngineAudioTest = new PairedRemoteVehicleEngineAudioTest(plugin, gameplay); phase = 68; File.WriteAllText(resultPath, "RUNNING: guest starting the shared ATV for remote engine audio"); return; }
                if (actorRoutesOnly) { if (plugin.World == null) return; phase = 61; actorRoutesTest = new PairedActorRoutesTest(plugin, host); File.WriteAllText(resultPath, "RUNNING: guest waiting for the host to start an on-foot route actor"); return; }
                if (npcTalkOnly) { if (!plugin.World!.PersonalProfileReady) return; npcTalkTest = new PairedNpcTalkTest(plugin, gameplay); phase = 65; File.WriteAllText(resultPath, "RUNNING: guest NPC talk fixture is waiting for the host snapshot"); return; }
                if (npcCoffeeGiftOnly) { if (!plugin.World!.PersonalProfileReady) return; npcTalkTest = new PairedNpcTalkTest(plugin, gameplay, coffeeGiftOnly: true); phase = 65; File.WriteAllText(resultPath, "RUNNING: guest Carole coffee gift fixture is waiting for the host snapshot"); return; }
                if (npcSubclassOnly || npcEricMilestoneOnly) { if (!plugin.World!.PersonalProfileReady) return; npcSubclassTest = new PairedNpcSubclassTest(plugin, gameplay, ericMilestone: npcEricMilestoneOnly); phase = 66; File.WriteAllText(resultPath, npcEricMilestoneOnly ? "RUNNING: guest Actor_Eric milestone fixture is waiting for the host's naturally active actor" : "RUNNING: guest NPC subclass fixture is waiting for the host snapshot"); return; }
                if (npcWeedSaleOnly) { if (!plugin.World!.PersonalProfileReady) return; npcWeedSaleTest = new PairedNpcWeedSaleTest(plugin, gameplay); phase = 69; File.WriteAllText(resultPath, "RUNNING: guest NPC weed-sale fixture is waiting for the host actor and pack"); return; }
                if (npcRouteTalkOnly) { if (!plugin.World!.PersonalProfileReady) return; npcRouteTalkTest = new PairedNpcRouteTalkTest(plugin, gameplay); phase = 70; File.WriteAllText(resultPath, "RUNNING: guest route-bound NPC talk fixture is waiting for the host candidate marker"); return; }
                if (schoolBusOnly) { if (!plugin.World!.PersonalProfileReady) return; schoolBusPoseTest = new PairedSchoolBusPoseTest(plugin); phase = 67; File.WriteAllText(resultPath, "RUNNING: guest school bus fixture is waiting for the host test-pose snapshot"); return; }
                if (syrupCrateOnly) { if (!plugin.World!.PersonalProfileReady) return; syrupCrateTest = new PairedSyrupCrateTest(plugin, gameplay); phase = 89; return; }
                if (productionSettlementOnly) { if (!plugin.World!.PersonalProfileReady) return; productionSettlementTest = new PairedProductionSettlementTest(plugin, gameplay); phase = 216; return; }
                if (tireReplayOnly) { if (!plugin.World!.PersonalProfileReady) return; tireReplayTest = new PairedTireReplayTest(plugin, gameplay); phase = 217; return; }
                if (meetOnly) { if (!plugin.World!.PersonalProfileReady) return; meetTest = new PairedMeetTest(plugin); phase = 90; return; }
                if (chatOnly) { if (!plugin.World!.PersonalProfileReady) return; chatTest = new PairedChatTest(plugin); phase = 91; return; }
                if (extraSeatsOnly) { if (!plugin.World!.PersonalProfileReady) return; extraSeatsTest = new PairedExtraSeatsTest(plugin, gameplay); phase = 88; File.WriteAllText(resultPath, "RUNNING: extra truck and bar-stool seats"); return; }
                if (schoolBusPassengersOnly) { if (!plugin.World!.PersonalProfileReady) return; schoolBusPassengersTest = new PairedSchoolBusPassengersTest(plugin, gameplay); phase = 72; File.WriteAllText(resultPath, "RUNNING: guest school-bus passenger fixture is waiting for host-authoritative seat grants"); return; }
                if (guestVehicleSaveOnly) { if (!plugin.World!.PersonalProfileReady) return; guestVehicleSaveTest = new PairedGuestVehicleSaveTest(plugin); phase = 78; File.WriteAllText(resultPath, "RUNNING: guest vehicle lease flush, native save/reload and reconnect"); return; }
                if (truckDoorsOnly) { if (!plugin.World!.PersonalProfileReady) return; vehiclesTest = new PairedVehiclesTest(plugin, gameplay, true, true); phase = 73; File.WriteAllText(resultPath, "RUNNING: guest truck doors are waiting for the host state and interpolation"); return; }
                if (steeringRackOnly) { if (!plugin.World!.PersonalProfileReady) return; steeringRackTest = new PairedSteeringRackTest(plugin); phase = 75; File.WriteAllText(resultPath, "RUNNING: guest native Konig steering-rack controls are waiting for the host's snapshot"); return; }
                if (vehicleInputResponseOnly) { if (!plugin.World!.PersonalProfileReady) return; vehicleInputResponseTest = new PairedVehicleInputResponseTest(plugin, gameplay); phase = 76; File.WriteAllText(resultPath, "RUNNING: guest input-response fixture is waiting for the host vehicle baseline"); return; }
                if (martinCarOnly) { if (!plugin.World!.PersonalProfileReady) return; martinCarPoseTest = new PairedMartinCarPoseTest(plugin); phase = 71; File.WriteAllText(resultPath, "RUNNING: guest Martin car fixture is waiting for the host test-pose snapshot"); return; }
                if (generatorOnly) { if (!plugin.World!.PersonalProfileReady) return; generatorTest = new PairedGeneratorTest(plugin, gameplay); phase = 43; return; }
                if (hydroOnly) { if (!plugin.World!.PersonalProfileReady) return; hydroTest = new PairedHydroTest(plugin); phase = 48; File.WriteAllText(resultPath, "RUNNING: guest hydro availability replication and native power query"); return; }
                if (avatarOnly) { if (!plugin.World!.PersonalProfileReady) return; avatarCrouchTest = new PairedAvatarCrouchTest(plugin); phase = 49; return; }
                if (buggyBuildOnly) { if (!plugin.World!.PersonalProfileReady) return; buggyBuildTest = new PairedBuggyBuildTest(plugin, gameplay); phase = 44; return; }
                if (cottagePurchaseOnly) { if (!plugin.World!.PersonalProfileReady) return; cottagePurchaseTest = new PairedCottagePurchaseTest(plugin, gameplay, plugin.World.CottagePurchase); phase = 45; return; }
                if (dogProductOnly) { if (!plugin.World!.PersonalProfileReady) return; dogProductTest = new PairedDogProductTest(plugin, gameplay); phase = 50; return; }
                if (leoCarBedOnly) { if (!plugin.World!.PersonalProfileReady) return; leoCarBedTest = new PairedLeoCarBedTest(plugin, gameplay); phase = 53; File.WriteAllText(resultPath, "RUNNING: guest native Leo Konig car-bed clicks and host-authoritative enter/exit"); return; }
                if (leoOnly || leoTravelOnly) { if (!plugin.World!.PersonalProfileReady) return; leoTest = new PairedLeoTest(plugin, gameplay, leoTravelOnly); phase = 53; return; }
                if (leoSuppliesOnly) { if (!plugin.World!.PersonalProfileReady) return; leoSuppliesTest = new PairedLeoSuppliesTest(plugin, gameplay); phase = 64; return; }
                if (konigSeatsOnly) { if (!plugin.World!.PersonalProfileReady) return; konigSeatsTest = new PairedKonigSeatsTest(plugin, gameplay); phase = 51; return; }
                if (konigSeatAdjustmentsOnly) { if (!plugin.World!.PersonalProfileReady) return; konigSeatAdjustmentsTest = new PairedKonigSeatAdjustmentsTest(plugin, gameplay); phase = 63; return; }
                if (slotMachinesOnly) { if (!plugin.World!.PersonalProfileReady) return; slotMachineTest = new PairedSlotMachineTest(plugin, gameplay); phase = 52; return; }
                if (paintPanelsOnly) { if (!plugin.World!.PersonalProfileReady) return; paintPanelsTest = new PairedPaintPanelsTest(plugin, gameplay); phase = 54; return; }
                if (sprayPaintOnly) { if (!plugin.World!.PersonalProfileReady) return; sprayPaintTest = new PairedSprayPaintTest(plugin, gameplay); phase = 74; File.WriteAllText(resultPath, "RUNNING: guest native spray-can mouse path, camera ray and host reconciliation"); return; }
                if (rustRepairKitOnly) { if (!plugin.World!.PersonalProfileReady) return; rustRepairKitTest = new PairedRustRepairKitTest(plugin, gameplay, associatedAccessory: repairAccessoryOnly, truckGrilleOnly: truckGrilleRustOnly); phase = 80; File.WriteAllText(resultPath, "RUNNING: guest native rust repair kit lease, repair, no-change and stale-generation fixture"); return; }
                if (dentRepairKitOnly) { if (!plugin.World!.PersonalProfileReady) return; dentRepairKitTest = new PairedDentRepairKitTest(plugin, gameplay, associatedAccessory: repairAccessoryOnly); phase = 81; File.WriteAllText(resultPath, "RUNNING: guest native dent repair kit lease, repair and damage progression fixture"); return; }
                if (windowTintOnly) { if (!plugin.World!.PersonalProfileReady) return; windowTintTest = new PairedWindowTintTest(plugin, gameplay); phase = 56; return; }
                if (trailerAuthorityOnly) { if (!plugin.World!.PersonalProfileReady) return; trailerAuthorityTest = new PairedTrailerAuthorityTest(plugin, gameplay); phase = 55; return; }
                if (trailerCouplerOnly) { if (!plugin.World!.PersonalProfileReady) return; trailerAuthorityTest = new PairedTrailerAuthorityTest(plugin, gameplay, nativeCouplerInput: true); phase = 77; return; }
                if (vehiclePlatesOnly) { if (!plugin.World!.PersonalProfileReady) return; vehiclePlatesTest ??= new PairedVehiclePlatesTest(plugin); phase = 79; File.WriteAllText(resultPath, "RUNNING: phase 79 guest plate rollback and host save/reload reconnect snapshot"); return; }
                if (windowsOnly) { if (!plugin.World!.PersonalProfileReady) return; windowsTest = new PairedWindowsTest(plugin, gameplay, preownedTruckWindows); phase = 25; return; }
                if (doorsOnly) { if (!plugin.World!.PersonalProfileReady) return; doorsTest = new PairedDoorsTest(plugin, gameplay); phase = 26; return; }
                if (collectiblesOnly) { if (!plugin.World!.PersonalProfileReady) return; collectiblesTest = new PairedCollectiblesTest(plugin, gameplay); phase = 27; return; }
                if (sleepOnly) { if (!plugin.World!.PersonalProfileReady) return; sleepTest = new PairedSleepTest(plugin, gameplay); phase = 28; return; }
                if (partsOnly) { if (!plugin.World!.PersonalProfileReady) return; partsTest = new PairedVehiclePartsTest(plugin); phase = 29; return; }
                if (accessOnly) { if (!plugin.World!.PersonalProfileReady) return; accessTest = new PairedVehicleAccessTest(plugin, gameplay); phase = 30; return; }
                if (boltsOnly) { if (!plugin.World!.PersonalProfileReady) return; boltsTest = new PairedBoltsTest(plugin); phase = 31; return; }
                if (radiosOnly) { if (!plugin.World!.PersonalProfileReady) return; radiosTest = new PairedRadiosTest(plugin, gameplay); phase = 83; return; }
                if (towingOnly) { if (!plugin.World!.PersonalProfileReady) return; towingTest = new PairedTowingTest(plugin, gameplay); phase = 84; return; }
                if (chainsawOnly) { if (!plugin.World!.PersonalProfileReady) return; chainsawTest = new PairedChainsawTest(plugin, gameplay); phase = 85; return; }
                if (salesOnly) { if (!plugin.World!.PersonalProfileReady) return; saleTest = new PairedSaleTest(plugin, gameplay); phase = 86; return; }
                if (saleCirclesOnly) { if (!plugin.World!.PersonalProfileReady) return; saleCirclesTest = new PairedSaleCirclesTest(plugin, gameplay); phase = 92; return; }
                if (storageOnly) { if (!plugin.World!.PersonalProfileReady) return; storageTest = new PairedStorageGarageTest(plugin, gameplay); phase = 87; return; }
                if (worldItemsOnly) { if (!plugin.World!.PersonalProfileReady) return; worldItemsTest = new PairedWorldItemsTest(plugin, gameplay); phase = 32; return; }
                if (fishBucketsOnly) { if (!plugin.World!.PersonalProfileReady) return; fishBucketsTest = new PairedFishBucketsTest(plugin, gameplay); phase = 60; return; }
                if (fuelOnly) { if (!plugin.World!.PersonalProfileReady) return; fuelTest = new PairedFuelTest(plugin, gameplay); phase = 33; return; }
                if (garageOnly) { if (!plugin.World!.PersonalProfileReady) return; garageTest = new PairedGarageTest(plugin, gameplay); phase = 34; return; }
                if (cheatsOnly) { if (!plugin.World!.PersonalProfileReady) return; cheatsTest = new PairedCheatsTest(plugin, gameplay); phase = 218; return; }
                if (metalSaleOnly) { if (!plugin.World!.PersonalProfileReady) return; metalSaleTest = new PairedMetalSaleTest(plugin, gameplay); phase = 46; return; }
                if (vrHandsOnly) { if (!plugin.World!.PersonalProfileReady) return; vrHandsTest = new PairedVrHandsTest(plugin, gameplay); phase = 500; return; }
                if (heldItemsOnly) { if (!plugin.World!.PersonalProfileReady) return; heldItemInterpolationTest = new PairedHeldItemInterpolationTest(plugin, gameplay); phase = 58; return; }
                if (trashRecycleOnly) { if (!plugin.World!.PersonalProfileReady) return; trashRecycleTest = new PairedTrashRecycleTest(plugin, gameplay); phase = 57; return; }
                if (scrapyardGateOnly) { if (!plugin.World!.PersonalProfileReady) return; scrapyardGateTest = new PairedScrapyardGateTest(plugin, gameplay); phase = 47; return; }
                if (forestOnly) { if (!plugin.World!.PersonalProfileReady) return; forestTest = new PairedForestTest(plugin, gameplay); phase = 35; return; }
                if (gardenOnly) { if (!plugin.World!.PersonalProfileReady) return; gardenTest = new PairedGardenTest(plugin, gameplay); phase = 36; return; }
                if (fleetOnly) { if (!plugin.World!.PersonalProfileReady) return; fleetTest = new PairedFleetPartsTest(plugin); phase = 37; return; }
                if (growthOnly) { if (!plugin.World!.PersonalProfileReady) return; growthTest = new PairedForestGrowthTest(plugin, gameplay); phase = 38; return; }
                if (liftOnly) { if (!plugin.World!.PersonalProfileReady) return; liftTest = new PairedCarLiftTest(plugin, gameplay); phase = 39; return; }
                if (planterOnly) { if (!plugin.World!.PersonalProfileReady) return; planterTest = new PairedPlanterTest(plugin, gameplay); phase = 40; return; }
                if (shackOnly) { if (!plugin.World!.PersonalProfileReady) return; shackTest = new PairedSugarShackTest(plugin, gameplay); phase = 41; return; }
                if (tubingOnly) { if (!plugin.World!.PersonalProfileReady) return; tubingTest = new PairedTubingTest(plugin, gameplay); phase = 42; return; }
                if (auditOnly) { if (!plugin.World!.PersonalProfileReady) return; auditTest = new PairedInteractionAuditTest(plugin); phase = 24; return; }
                if (vehiclesOnly) { if (!plugin.World!.PersonalProfileReady) return; vehiclesTest = new PairedVehiclesTest(plugin, gameplay); phase = 9; return; }
                if (vehiclePoseOnly) { if (!plugin.World!.PersonalProfileReady) return; vehiclePoseTest = new PairedVehiclePoseTest(plugin, gameplay); phase = 59; return; }
                if (consumptionOnly) { if (!plugin.World!.PersonalProfileReady) return; consumptionTest = new PairedConsumptionTest(plugin, gameplay); phase = 8; return; }
                if (coordinatedSaveOnly) { coordinatedSaveTest = new PairedCoordinatedSaveTest(plugin); phase = 23; return; }
                if (saveRoundOnly) { saveRoundTest = new PairedSaveRoundTest(plugin, gameplay); phase = 22; return; }
                if (profileOnly) { profileTest = new PairedProfileTest(plugin); phase = 21; return; }
                if (flushOnly) { flushTest = new PairedFlushTest(plugin, gameplay); phase = 20; return; }
                if (progressOnly) { progressTest = new PairedProgressTest(plugin); phase = 19; return; }
                if (storesOnly) { storesTest = new PairedStoresTest(plugin); phase = 18; return; }
                if (economyOnly) { economyTest = new PairedEconomyTest(plugin); phase = 17; return; }
                if (weatherOnly) { weatherTest = new PairedWeatherTest(plugin); phase = 16; return; }
                if (clockOnly) { clockTest = new PairedClockTest(plugin, gameplay); phase = 15; return; }
                if (anxietyGameplayOnly) { if (!plugin.World!.PersonalProfileReady) return; anxietyGameplayTest = new PairedAnxietyGameplayTest(plugin, gameplay, resultPath); phase = 215; return; }
                if (anxietyOnly) { if (!plugin.World!.PersonalProfileReady) return; anxietyTest = new PairedAnxietyTest(plugin, gameplay, resultPath); phase = 214; return; }
                if (nitrousRefillOnly) { if (!plugin.World!.PersonalProfileReady) return; nitrousRefillTest = new PairedNitrousRefillTest(plugin, gameplay); phase = 201; return; }
                if (tireInflationOnly) { if (!plugin.World!.PersonalProfileReady) return; tireInflationTest = new PairedTireInflationTest(plugin, gameplay); phase = 209; return; }
                if (trailerTowingOnly) { if (!plugin.World!.PersonalProfileReady) return; trailerTowingTest = new PairedTrailerTowingTest(plugin, gameplay); phase = 208; return; }
                if (equippedToolScaleOnly) { if (!plugin.World!.PersonalProfileReady) return; equippedToolScaleTest = new PairedEquippedToolScaleTest(plugin, gameplay); phase = 207; return; }
                if (consoleChargerOnly) { if (!plugin.World!.PersonalProfileReady) return; consoleChargerTest = new PairedConsoleChargerTest(plugin, gameplay); phase = 213; return; }
                if (toolPacksOnly) { if (!plugin.World!.PersonalProfileReady) return; toolPacksTest = new PairedToolPacksTest(plugin, gameplay); phase = 210; return; }
                if (shellSeatsOnly) { if (!plugin.World!.PersonalProfileReady) return; shellSeatsTest = new PairedShellSeatsTest(plugin, gameplay); phase = 211; return; }
                if (shellSuspensionOnly) { if (!plugin.World!.PersonalProfileReady) return; shellSuspensionTest = new PairedShellSuspensionTest(plugin, gameplay); phase = 212; return; }
                if (passengerControlsOnly) { if (!plugin.World!.PersonalProfileReady) return; passengerControlsTest = new PairedPassengerVehicleControlsTest(plugin, gameplay); phase = 206; return; }
                if (fishingPresentationOnly) { if (!plugin.World!.PersonalProfileReady) return; fishingPresentationTest = new PairedFishingPresentationTest(plugin, gameplay); phase = 202; return; }
                if (underglowOnly) { if (!plugin.World!.PersonalProfileReady) return; underglowTest = new PairedUnderglowTest(plugin, gameplay); phase = 203; return; }
                if (travelingVanOnly) { if (!plugin.World!.PersonalProfileReady) return; travelingVanTest = new PairedTravelingVanTest(plugin, gameplay); phase = 204; return; }
                if (purchasedTruckDoorsOnly) { if (!plugin.World!.PersonalProfileReady) return; purchasedTruckDoorsTest = new PairedPurchasedTruckDoorsTest(plugin, gameplay); phase = 205; return; }
                if (fryerCookOnly) { if (!plugin.World!.PersonalProfileReady) return; fryerCookTest = new PairedFryerCookTest(plugin, gameplay); phase = 200; return; }
                if (foodOnly) { foodTest = new PairedFoodTest(plugin, gameplay); phase = 14; return; }
                if (decorationsOnly) { decorationsTest = new PairedDecorationsTest(plugin, gameplay); phase = 13; return; }
                if (itemsOnly) { lanternTest = new PairedLanternTest(plugin, gameplay); phase = 12; return; }
                entity = plugin.Lights.TestEntity;
                if (plugin.Lights.TestRecord(entity).Revision < 2) throw new InvalidOperationException("Late-join snapshot missed the host's earlier toggle.");
                if (!plugin.Lights.TestSynchronized) throw new InvalidOperationException("Snapshot records were not applied to Unity light state.");
                resyncTest = new PairedResyncTest(plugin, entity); phase = 11;
                File.WriteAllText(resultPath, "RUNNING: repairing deliberate Unity drift with entity, section and world snapshots");
                return;
            }
            if (!host && phase == 11)
            {
                if (!resyncTest!.Tick()) return;
                MovePlayer(gameplay, plugin.Lights!.TestPosition(entity) + new Vector3(0, 0, 30));
                phase = 4; nextAction = Time.unscaledTime + 0.7f;
                File.WriteAllText(resultPath, "RUNNING: late-join Unity state verified; checking out-of-range click rejection");
                return;
            }
            if (!host && phase == 4)
            {
                plugin.Lights!.TestClick(entity); phase = 5; nextAction = Time.unscaledTime + 1;
                return;
            }
            if (!host && phase == 5)
            {
                if (plugin.Lights!.TestRecord(entity).Revision != 2 || !plugin.Lights.TestSynchronized)
                    throw new InvalidOperationException("Out-of-range guest click changed the shared light.");
                MovePlayer(gameplay, plugin.Lights.TestPosition(entity) + new Vector3(0, -0.5f, 1.5f));
                phase = 2; nextAction = Time.unscaledTime + 0.7f;
                File.WriteAllText(resultPath, "RUNNING: distant click rejected; sending nearby guest click through the patched game method");
                return;
            }
            if (!host && phase == 2)
            {
                plugin.Lights!.TestClick(entity); phase = 3; return;
            }
            if (host && phase == 2 && plugin.Lights!.TestRecord(entity).Revision >= 3 && plugin.RemoteAvatarCount > 0)
            {
                gameplay.PauseGame();
                if (Time.timeScale <= 0 || !gameplay.Get_isGamePaused) throw new InvalidOperationException("Host pause stopped network simulation.");
                gameplay.ResumeGame();
                phase = 6; itemsTest = new PairedItemsTest(plugin, gameplay);
                File.WriteAllText(resultPath, "RUNNING: lights and remote avatar verified; testing dynamic items and ownership");
            }
            if (!host && phase == 3 && plugin.Lights!.TestRecord(entity).Revision >= 3 && plugin.RemoteAvatarCount > 0)
            {
                var lights = plugin.Lights;
                if (!lights.TestSynchronized) throw new InvalidOperationException("Authoritative delta was not applied to Unity light state.");
                gameplay.PauseGame();
                if (Time.timeScale <= 0 || !gameplay.Get_isGamePaused) throw new InvalidOperationException("Guest pause stopped network simulation.");
                gameplay.ResumeGame();
                phase = 6; itemsTest = new PairedItemsTest(plugin, gameplay);
                File.WriteAllText(resultPath, "RUNNING: lights verified; testing item pickup, throw, despawn, disconnect and reconnect");
            }
            if (phase == 6 && itemsTest!.Tick())
            {
                if (clientActionsOnly)
                {
                    if (host) { phase = 7; return; }
                    var items = plugin.Items!; plugin.EndPairedTestSession();
                    if (!items.TestRestored || gameplay.Player.Get_HoldingItem != null)
                        throw new InvalidOperationException("Client-action suite rollback failed.");
                    Pass("Client-action suite: guest electric-percolator MouseDown served a host-created mug into the guest hand and native Use changed host power; guest fence install, style change and timed removal replicated, then fixtures and guest tool state were cleaned up.");
                    return;
                }
                lanternTest = new PairedLanternTest(plugin, gameplay); phase = 12;
            }
            if (phase == 12 && lanternTest!.Tick())
            { decorationsTest = new PairedDecorationsTest(plugin, gameplay); phase = 13; }
            if (phase == 13 && decorationsTest!.Tick())
            {
                if (decorationsOnly)
                {
                    if (host) { phase = 7; return; }
                    var items = plugin.Items!; plugin.EndPairedTestSession();
                    if (!items.TestRestored) throw new InvalidOperationException("Decoration suite rollback failed.");
                    Pass("Guest decorations suite: all 39 prefab factories, frozen body state, distance/stale/frozen/held command rejection, native pickup/throw, child presentation, disconnect/placed reconnect and native body repair passed.");
                    return;
                }
                consumptionTest = new PairedConsumptionTest(plugin, gameplay); phase = 8;
            }
            if (phase == 8 && consumptionTest!.Tick())
            {
                if (itemsOnly || foodOnly || consumptionOnly)
                {
                    if (host) { phase = 7; return; }
                    var items = plugin.Items!; plugin.EndPairedTestSession();
                    if (!items.TestRestored) throw new InvalidOperationException("Item suite rollback failed.");
                    Pass(foodOnly ? "Guest food suite: both poutine prefabs, host freshness/refrigeration, native spoilage material, blocked guest/original ticks, fresh/spoiled nutrition and sickness, tombstone values, replay/reconnect recovery and effect-clone cleanup; affected syrup/coffee transactions passed." :
                        "Guest item suite: prefab catalog; lantern native light/emission, range/stale/ownership checks, fields, carry, disconnect and reconnect; syrup/coffee consumption, rejection, receipt replay and pending acknowledgment recovery passed.");
                    return;
                }
                // The decoration grid is spawned beside the parked vehicles and would block their test drive.
                if (host) plugin.Items!.TestClearDecorations();
                vehiclesTest = new PairedVehiclesTest(plugin, gameplay); phase = 9;
            }
            if (phase == 9 && vehiclesTest!.Tick())
            { vehiclesTest = new PairedVehiclesTest(plugin, gameplay, true); phase = 10; }
            if (phase == 10 && vehiclesTest!.Tick())
            {
                if (host) { phase = 7; return; }
                var lights = plugin.Lights!;
                var items = plugin.Items!;
                var vehicles = plugin.Vehicles!;
                string hash = lights.CatalogHash;
                plugin.EndPairedTestSession();
                if (!lights.TestRestored || !items.TestRestored || !vehicles.TestRestored || plugin.RemoteAvatarCount != 0) throw new InvalidOperationException("Guest world/avatar rollback failed.");
                Pass("Guest: entity/section/world repairs recovered deliberate Unity light drift without revision changes; lights, avatars, pause, shared carry and consumption; lantern native emission, range/stale/ownership rejection, persistent fields and held reconnect; ATV and truck range checks, native driving, doors, accessory physics modes, exit, rollback and reconnect passed. Light catalog=" + hash);
            }
            if (phase == 73 && vehiclesTest!.Tick())
            {
                Pass(plugin.TestOptions.IsHost
                    ? "Host truck-door fixture: host-authoritative truck driver/passenger door states synchronized with the guest, then the guest disconnected."
                    : "Guest truck-door fixture: near/far interaction, host door authority, rendered intermediate-angle interpolation, exact hinge/body restoration and native registry cleanup passed.");
                return;
            }
            if (phase == 76 && vehicleInputResponseTest!.Tick())
            {
                if (host) { phase = 7; return; }
                var result = vehicleInputResponseTest.Evidence;
                var registry = plugin.World!.Entities;
                var sharedVehicles = plugin.Vehicles!;
                plugin.EndPairedTestSession();
                if (registry.Count != 0 || !sharedVehicles.TestRestored || plugin.RemoteAvatarCount != 0)
                    throw new InvalidOperationException("Guest input-response disconnect did not restore the vehicle, avatar and native registry baseline.");
                Pass("Guest vehicle input response: " + result + ". Host independently observed and applied the guest's actual first-gear and motion samples.");
                return;
            }
            if (!finished && phase == 6 && itemStatus != itemsTest!.Status)
            { itemStatus = itemsTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (!finished && phase == 8 && itemStatus != consumptionTest!.Status)
            { itemStatus = consumptionTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (!finished && phase == 12 && itemStatus != lanternTest!.Status)
            { itemStatus = lanternTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (!finished && phase == 13 && itemStatus != decorationsTest!.Status)
            { itemStatus = decorationsTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 62)
            {
                if (trafficAuthorityTest!.Tick())
                {
                    if (host) { phase = 7; return; }
                    plugin.EndPairedTestSession();
                    Pass("Traffic authority: host-owned Gley activation and motion, late-join slot catalog, guest AI suppression, remote camera registration, guest kinematic interpolation and a host car stopping for the walking guest passed; locally prepared adapter-record activation of a real never-initialized guest slot, exact offline restoration, and two seconds of resumed native Update/FixedUpdate also passed.");
                    return;
                }
                if (itemStatus != trafficAuthorityTest.Status)
                { itemStatus = trafficAuthorityTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            }
            if (phase == 14 && foodTest!.Tick()) { consumptionTest = new PairedConsumptionTest(plugin, gameplay); phase = 8; }
            if (!finished && phase == 14 && itemStatus != foodTest!.Status)
            { itemStatus = foodTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 29) { if (partsTest!.Tick()) { Pass("Vehicle parts: guest install/tighten/remove echoed through the host; fixture bolts were cleared after both peers observed tightening (physical spanner input is separate); the completed RemovePart_Timer native hand sequence retained its lease, the host copy stayed kinematic with a trigger collider and followed guest hand movement for four seconds, then the guest and then the host installed a held loose part natively with no loose duplicate or lost part on either copy (#132), and disconnected cleanly."); return; } if (itemStatus != partsTest.Status) { itemStatus = partsTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 30) { if (accessTest!.Tick()) { Pass("Vehicle access: guest-initiated Konig driver door, hood release/open/close and trunk toggle applied on the host and echoed back, with guest rollback on disconnect."); return; } if (itemStatus != accessTest.Status) { itemStatus = accessTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 32) { if (worldItemsTest!.Tick()) { Pass("WorldItems: guest bolt install/unscrew/release replicated through host adoption and physics restore. Magnetic tray collision, leased attach/remove and body restoration passed. Fish bucket host-approved add/remove, duplicate/stale rejection, snapshot replay and throw containment passed. Pack hand-use waited for lease grant; a forced failed grab cleared PendingUse and a clean retry unpacked/adopted one bolt. Host rejected free, stale and foreign-owner Unpacked commands; unleased GetOnePackedItem was blocked. Guest wrench/impact pack extraction without a local pack lease remains unsupported."); return; } if (itemStatus != worldItemsTest.Status) { itemStatus = worldItemsTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 60)
            {
                if (fishBucketsTest!.Tick())
                {
                    if (host && plugin.Session!.Peers.Count > 1) return;
                    if (!host) plugin.EndPairedTestSession();
                    Pass("FishBuckets: host-approved add/remove, stale and duplicate rejection, snapshot replay, species identity, throw containment and a guest rod catch adopted at its size passed.");
                    return;
                }
                if (itemStatus != fishBucketsTest.Status) { itemStatus = fishBucketsTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            }
            if (phase == 33) { if (fuelTest!.Tick()) { Pass("Fuel: a guest poured a shared gas can into a parked vehicle (host drained its can copy and filled the tank), turned the filler cap, and pumped paid fuel into the can at Kaligaz (host charged the shared wallet), all echoed back."); return; } if (itemStatus != fuelTest.Status) { itemStatus = fuelTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 218) { if (cheatsTest!.Tick()) { Pass(host ? "Host cheats: guest cash stayed unchanged while cheats were off and enabled-but-ungranted; a granted guest cash action reached the host; revocation followed." : "Guest cheats: a leaked native flag, native activation, forced windows and all 13 native SpecialMenu actions were refused with cheats off and enabled-but-ungranted; the flag returned to the session state; a granted cash action reached the host and revocation hid the window."); return; } if (itemStatus != cheatsTest.Status) { itemStatus = cheatsTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } return; }
            if (phase == 34) { if (garageTest!.Tick()) { Pass("Garages: the host's home garage build stage and door motion appeared on the guest, and two guest speedway garage presses were intercepted, run natively by the host and replayed on the guest."); return; } if (itemStatus != garageTest.Status) { itemStatus = garageTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 46) { if (metalSaleTest!.Tick()) { Pass("Metal sale: guest sign click ran the native sale once on the host; shared cash and item removal reached the guest, while distant and duplicate requests had no extra effects and guest cash restored on disconnect."); return; } if (itemStatus != metalSaleTest.Status) { itemStatus = metalSaleTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 47 && scrapyardGateTest!.Tick()) { Pass("Scrapyard gate: distant guest request rejected; native criminal-record and friendship rules enforced by the host; native gate animation, colliders, pin and dust replicated; guest local mutators blocked and disconnect baseline restored."); return; }
            if (!finished && phase == 47 && itemStatus != scrapyardGateTest!.Status) { itemStatus = scrapyardGateTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 48 && hydroTest!.Tick())
            {
                if (host) { phase = 7; return; }
                Pass("Home hydro availability: host derived running state reached guest native hydro and effective-power queries; guest HydroData stayed local; combustion start/stop under active hydro caused no redundant power transition; disconnect restored the guest baseline."); return;
            }
            if (!finished && phase == 48 && itemStatus != hydroTest!.Status)
            { itemStatus = hydroTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 49 && avatarCrouchTest!.Tick())
            { Pass("Remote avatar: the stock body kept streamed shirt index 7 through crouch and stand poses; the guest's native Sorry/Slur/Spit emotes raised the host avatar's hand, played Oups and Sacre with their text and threw a presentation spit drop that landed; pose previews were rendered, and disconnect removed the avatar."); return; }
            if (!finished && phase == 49 && itemStatus != avatarCrouchTest!.Status)
            { itemStatus = avatarCrouchTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 41) { if (shackTest!.Tick()) { Pass("SugarShack: host-run production and guest controls, all seven host-paid upgrade listings, outdoor-pump item handoff, duplicate rejection, conclusive late-reply settlement and guest structural rollback passed."); return; } if (itemStatus != shackTest.Status) { itemStatus = shackTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 42) { if (tubingTest!.Tick()) { Pass("Tubing: the guest placed two tubing posts and a spout from carried tool items, each built by the host; its predicted tube was adopted from the host's record, its tube removal and post removal ran on the host, and the host's spout tapped the maple tree."); return; } if (itemStatus != tubingTest.Status) { itemStatus = tubingTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 43 && generatorTest!.Tick())
            {
                if (host) { phase = 7; return; }
                Pass("Home generator: late join matched host combustion and kill-switch state; a distant guest switch press was rejected; nearby kill-switch and start-cord requests ran on the host; guest native engine presentation followed and its original local generator returned on disconnect."); return;
            }
            if (!finished && phase == 43 && itemStatus != generatorTest!.Status)
            { itemStatus = generatorTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 44 && buggyBuildTest!.Tick())
            { Pass("Buggy construction and drive: six guest packs installed once on the host, the completed buggy became shared, the guest acquired its driver lease and drove while the host followed, then exit and disconnect restored the local baseline."); return; }
            if (!finished && phase == 44 && itemStatus != buggyBuildTest!.Status)
            { itemStatus = buggyBuildTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 82 && racecarBuildTest!.Tick())
            { Pass("Speedway/Racecar paired acceptance: the guest's eligible Speedway reward click was suppressed, the host completed all three native reward claims, three guest-leased Event_RacecarPack drops were checked for release and position before explicitly invoking the host's native RacecarBuild callback once each, and the complete build state and activation reached the guest; the isolated baseline restored on both copies. Physics trigger delivery was not exercised."); return; }
            if (!finished && phase == 82 && itemStatus != racecarBuildTest!.Status)
            { itemStatus = racecarBuildTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 45 && cottagePurchaseTest!.Tick())
            { Pass("Cottage multiplayer: host-validated purchase and continuous compost/fertilizer, coffee-seed, percolator and wheelbarrow authority; scoped repair, guest mutation guards and disconnect rollback restored the guest baseline."); return; }
            if (!finished && phase == 45 && itemStatus != cottagePurchaseTest!.Status)
            { itemStatus = cottagePurchaseTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 50 && dogProductTest!.Tick())
            {
                if (host) { phase = 7; return; }
                plugin.EndPairedTestSession();
                Pass("Guest dog-product suite: host-only Leo taming eligibility, patched native click, duplicate replay, range and insufficient-funds denial passed."); return;
            }
            if (!finished && phase == 50 && itemStatus != dogProductTest!.Status)
            { itemStatus = dogProductTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 53 && (leoCarBedOnly ? leoCarBedTest!.Tick() : leoTest!.Tick()))
            { Pass(leoCarBedOnly ? "Leo car bed: the guest's native Konig bed click entered Leo into the host's installed bed, the second native click exited to an on-foot state, and both copies restored Leo, DogBed and passenger-door fixtures on disconnect." : leoTravelOnly ? "Leo following-player travel: host state and pose transitioned to the guest, guest native pet/order/treat/bed effects were blocked, and disconnect restored the local state, pose and needs." : "Leo on-foot roaming: host presence and needs reached the guest, a guest pet popped the shared heart through the host, guest native pet/order/treat/bed effects were blocked, and disconnect restored the guest baseline."); return; }
            if (!finished && phase == 53 && itemStatus != (leoCarBedOnly ? leoCarBedTest!.Status : leoTest!.Status))
            { itemStatus = leoCarBedOnly ? leoCarBedTest!.Status : leoTest!.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 64 && leoSuppliesTest!.Tick())
            {
                if (host) { phase = 7; return; }
                Pass("Leo supplies: guest-held dry-food pack filled the host bowl once, duplicate and distant requests were rejected, the native watering-can iterator drained once while filling the second bowl, bunker fertilizer updated the native host reservoir and depleted its source pack, and disconnect restored guest item and save baselines."); return;
            }
            if (!finished && phase == 64 && itemStatus != leoSuppliesTest!.Status)
            { itemStatus = leoSuppliesTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 51 && konigSeatsTest!.Tick())
            { Pass("Konig seats: host checked distance, seat solidity and exclusive passenger claim; host driver and guest passenger followed a moving body, stale exit was rejected, stale-generation exit recovered, stale crash delivery was ignored after exit, a missed exit callback released through the watchdog, re-entry and disconnect restored native walking and physics."); return; }
            if (!finished && phase == 51 && itemStatus != konigSeatsTest!.Status)
            { itemStatus = konigSeatsTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 63 && konigSeatAdjustmentsTest!.Tick())
            { Pass("Konig seat adjustment: the distant guest click was rejected; an actually seated guest driver used native forward/height buttons and a seated guest passenger used its native forward button; authoritative leases, replicated centimeter steps, native attachment and save parameters matched, and disconnect restored the baseline."); return; }
            if (!finished && phase == 63 && itemStatus != konigSeatAdjustmentsTest!.Status)
            { itemStatus = konigSeatAdjustmentsTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 52 && slotMachineTest!.Tick())
            {
                if (host) { phase = 7; return; }
                Pass("Slot machine: the host applied the native guest cash-in and spin, rejected duplicate/distant/insufficient-funds actions, shared the result and paid cash-out once; guest wallet restored on disconnect. The guest's reels spun and stopped on the host's result."); return;
            }
            if (!finished && phase == 52 && itemStatus != slotMachineTest!.Status)
            { itemStatus = slotMachineTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 54 && paintPanelsTest!.Tick())
            {
                if (host) { phase = 7; return; }
                Pass("Paint panels: a direct guest Paint call on an unassociated panel was range checked and applied by the host; the guest matched the host material state and restored its local panel baseline on disconnect. Physical spray-can use and associated-panel mutation were not exercised."); return;
            }
            if (!finished && phase == 54 && itemStatus != paintPanelsTest!.Status)
            { itemStatus = paintPanelsTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 55 && trailerAuthorityTest!.Tick())
            {
                if (host) { phase = 7; return; }
                plugin.EndPairedTestSession();
                Pass("Trailer authority: distant guest attach and detach requests were rejected from fresh walking poses; nearby guest WorldChannel requests drove host native Konig/WoodSplitter attach and detach, with matching links, NWH joints and generation changes on both copies; both copies restored the staged body pose."); return;
            }
            if (!finished && phase == 55 && itemStatus != trailerAuthorityTest!.Status)
            { itemStatus = trailerAuthorityTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 77 && trailerAuthorityTest!.Tick())
            {
                if (host) { phase = 7; return; }
                plugin.EndPairedTestSession();
                Pass("Trailer coupler phase 77: guest Trailer_Coupler.MouseDown was intercepted by the host-authoritative route for attach and detach; distant clicks were rejected, nearby clicks advanced link generations and created/cleared matching NWH hitch joints on both copies, then the staged trailer pose was restored."); return;
            }
            if (!finished && phase == 77 && itemStatus != trailerAuthorityTest!.Status)
            { itemStatus = trailerAuthorityTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 56 && windowTintTest!.Tick())
            {
                if (host) { phase = 7; return; }
                Pass("Window tint: a distant guest command was rejected, a nearby leased roll applied native tint and spent one use, a leased remover cleared it, and guest native material restored on disconnect. Physical mouse targeting was not exercised."); return;
            }
            if (!finished && phase == 56 && itemStatus != windowTintTest!.Status)
            { itemStatus = windowTintTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 57 && trashRecycleTest!.Tick())
            {
                if (host) { phase = 7; return; }
                Pass("Trash/recycle: distant and ineligible guest deposits were rejected, a nearby recyclable deposit was confirmed without early removal, and the host Wednesday sweep published one shared item tombstone with replay and disconnect checks."); return;
            }
            if (!finished && phase == 57 && itemStatus != trashRecycleTest!.Status)
            { itemStatus = trashRecycleTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 500)
            {
                if (vrHandsTest!.Tick()) { Pass("A VR guest held two distinct native tools, moved both, selected each tool on the host, and released them independently."); return; }
                if (itemStatus != vrHandsTest.Status) { itemStatus = vrHandsTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
                return;
            }
            if (phase == 58 && heldItemInterpolationTest!.Tick())
            {
                if (host) { phase = 7; return; }
                Pass("A host-held native item traversed real pose samples; the guest rendered an intermediate position and rotation before the next target, then released the replica on disconnect."); return;
            }
            if (!finished && phase == 58 && itemStatus != heldItemInterpolationTest!.Status)
            { itemStatus = heldItemInterpolationTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 59 && vehiclePoseTest!.Tick())
            {
                if (host) { phase = 7; return; }
                Pass("The guest rendered an intermediate ATV body pose between real host vehicle state samples and restored its local baseline on disconnect."); return;
            }
            if (!finished && phase == 59 && itemStatus != vehiclePoseTest!.Status)
            { itemStatus = vehiclePoseTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 40) { if (planterTest!.Tick()) { Pass("Planter: a guest froze a host outdoor planter, planted and watered its patch (mirrored to the host), harvested the host-grown plant and unfroze it; the patch record followed the planter's shared item id."); return; } if (itemStatus != planterTest.Status) { itemStatus = planterTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }

            if (phase == 38) { if (growthTest!.Tick()) { Pass("Growth: a guest's pruning cleared the host tree's cuttings, a guest-planted cutting became a host sapling adopted by the guest, the guest's fertilizer reached it, and the host's maturation replicated as a sapling tombstone plus a new tree."); return; } if (itemStatus != growthTest.Status) { itemStatus = growthTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 37) { if (fleetTest!.Tick()) { Pass(fleetTest.Evidence); return; } if (itemStatus != fleetTest.Status) { itemStatus = fleetTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 36) { if (gardenTest!.Tick()) { Pass("Garden: a guest dig was predicted and adopted from the host's new patch; the guest's planting, watering and fertilizer ran on the host; the host's growth reached the guest, whose harvest and shovel removal were mirrored back."); return; } if (itemStatus != gardenTest.Status) { itemStatus = gardenTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 35) { if (forestTest!.Tick()) { Pass("Forest: host and guest native cuts produced the expected number of visible log segments. The guest picked up and moved output from each split, the host observed ownership and motion, and temporary segments retired without timing out. The held-log splitter also produced both firewood halves on the guest."); return; } if (itemStatus != forestTest.Status) { itemStatus = forestTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 92) { if (saleCirclesTest!.Tick()) { Pass("SaleCircles: " + saleCirclesTest.Summary); return; } if (itemStatus != saleCirclesTest.Status) { itemStatus = saleCirclesTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 87) { if (storageTest!.Tick()) { Pass("StorageGarage: the host parked the shared ATV in a storage bay and closed the door; the guest mirrored the door, hid its ATV copy with the native unload, showed it again when the host reopened the door, and its own door button was routed to the host."); return; } if (itemStatus != storageTest.Status) { itemStatus = storageTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 86) { if (saleTest!.Tick()) { Pass("Sales: host and guest-dropped logs used native sawmill sale presentation; the receiver preserved a guest-held lease, then processed its accepted drop inside the same trigger."); return; } if (itemStatus != saleTest.Status) { itemStatus = saleTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 85) { if (chainsawTest!.Tick()) { Pass("Chainsaw: host and guest observers started and stopped the shared native cut AudioSource while the other peer held the leased saw, then the guest racked and dropped it. The fixture exercised sound/state replay, not the tree raycast or cut action."); return; } if (itemStatus != chainsawTest.Status) { itemStatus = chainsawTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 84) { if (towingTest!.Tick()) { Pass("Towing: a guest's Konig tow request was charged and queued by the host, a morning recovery blocked by shared actions was deferred and retried, the host's native recovery moved the Konig to the tow yard and the move reached the guest."); return; } if (itemStatus != towingTest.Status) { itemStatus = towingTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 83) { if (radiosTest!.Tick()) { Pass("Radios: guest native power, preset and volume presses on a house radio ran on the host and returned as host state; the guest's station songs followed the host's station clock."); return; } if (itemStatus != radiosTest.Status) { itemStatus = radiosTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 31) { if (boltsTest!.Tick()) { Pass("Bolts: the guest saw the host's Konig bolt install and tightening live mid-progress; the host ignored the guest's direct native bolt write (bolts only move through host-leased tool commands); the guest rolled back on disconnect."); return; } if (itemStatus != boltsTest.Status) { itemStatus = boltsTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 28) { if (sleepTest!.Tick()) { Pass("Coordinated sleep: guest BackSeat.Use and host FishingBoat.MouseDown entered/cancelled shared waits; shared-bed sleepers were visibly separated; unanimous sleep advanced the host clock once by the longest request, restored energy without awake thirst decay and completed the coordinated save; chat preserved deferred input restoration and native Fill_Sleep/SkipNight callbacks occurred once per completed sleep and never on cancelled waits."); return; } if (itemStatus != sleepTest.Status) { itemStatus = sleepTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 27) { if (collectiblesTest!.Tick()) { Pass("Collectibles: enabled factories and shared carry/move/drop passed; host StartFire preserved the guest-held tire, burned it after accepted drop, and replicated the burn mark. Syrup-can empty handoff, huge/big weed-pack unpacking and guest cleanup also passed."); return; } if (itemStatus != collectiblesTest.Status) { itemStatus = collectiblesTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 26) { if (doorsTest!.Tick()) { Pass("Building doors: late-join lock state, distant/stale/locked rejection, native open/close animation, guest mutation guards, host unlock, store-closing presence, scoped repair and guest rollback passed."); return; } if (itemStatus != doorsTest.Status) { itemStatus = doorsTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 25) { if (windowsTest!.Tick()) { Pass("Native window near/far commands, partial travel, close, scoped repair and guest rollback passed." + windowsTest.Evidence); return; } if (itemStatus != windowsTest.Status) { itemStatus = windowsTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); } }
            if (phase == 24)
            {
                if (auditTest!.Tick()) { Pass("Pending-interaction audit: installed native prefixes, strict skip/default return, observe-only execution, bounded report and offline bypass passed."); return; }
                if (itemStatus != auditTest.Status) { itemStatus = auditTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            }
            if (phase == 23 && coordinatedSaveTest!.Tick())
            {
                if (host) { phase = 7; return; }
                plugin.EndPairedTestSession();
                Pass("Guest coordinated native save: two host saves collected profiles, released gates and preserved guest save selection."); return;
            }
            if (!finished && phase == 23 && itemStatus != coordinatedSaveTest!.Status)
            { itemStatus = coordinatedSaveTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 22 && saveRoundTest!.Tick())
            {
                if (host) { phase = 7; return; }
                plugin.EndPairedTestSession();
                Pass("Guest native save-round collection: held-item flush, profile transport, frozen writing, failure cleanup and successful retry cleanup passed; no disk writes."); return;
            }
            if (!finished && phase == 22 && itemStatus != saveRoundTest!.Status)
            { itemStatus = saveRoundTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 21 && profileTest!.Tick())
            {
                if (host) { phase = 7; return; }
                plugin.EndPairedTestSession();
                Pass("Guest personal profile: bounded section roundtrip, native needs/counters/statistics, nine shirt colors, blocked unsafe captures/restores, original profile restoration passed."); return;
            }
            if (!finished && phase == 21 && itemStatus != profileTest!.Status)
            { itemStatus = profileTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 20 && flushTest!.Tick())
            {
                if (host) { phase = 7; return; }
                plugin.EndPairedTestSession();
                Pass("Guest ownership flush: native held-item drop, final vehicle state and exit, frozen acquisition guards, host revocation and native walking restoration passed."); return;
            }
            if (!finished && phase == 20 && itemStatus != flushTest!.Status)
            { itemStatus = flushTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 19 && progressTest!.Tick())
            {
                if (host) { phase = 7; return; }
                plugin.EndPairedTestSession();
                Pass("Guest progression suite: distinct-state late join, native completion guards, category visibility and localized strike UI, no popup replay, equal-revision UI repair, offline restoration and reconnect passed."); return;
            }
            if (!finished && phase == 19 && itemStatus != progressTest!.Status)
            { itemStatus = progressTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 18 && storesTest!.Tick(gameplay))
            {
                if (host) { phase = 7; return; }
                plugin.EndPairedTestSession();
                Pass("Guest stores suite: host listings, remote purchase and automatic lease/native hand delivery, private outcomes and moved-away fallback, range/quote/stock/closed/funds rejection, duplicate claims across restock/reconnect, offline restoration passed."); return;
            }
            if (!finished && phase == 18 && itemStatus != storesTest!.Status)
            { itemStatus = storesTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 17 && economyTest!.Tick())
            {
                if (host) { phase = 7; return; }
                plugin.EndPairedTestSession();
                Pass("Guest economy suite: late-join cash/payout ledger, mutation and purchase guards, no repeated payment effects, native UI, scoped repair, offline restoration and reconnect passed."); return;
            }
            if (!finished && phase == 17 && itemStatus != economyTest!.Status)
            { itemStatus = economyTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 16 && weatherTest!.Tick())
            {
                if (host) { phase = 7; return; }
                plugin.EndPairedTestSession();
                Pass("Guest weather suite: late-join forecast, temperature, moon, transition samples, gradients, blocked local changes, section repair, offline restoration and reconnect passed."); return;
            }
            if (!finished && phase == 16 && itemStatus != weatherTest!.Status)
            { itemStatus = weatherTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (phase == 15 && clockTest!.Tick())
            {
                if (host) { phase = 7; return; }
                plugin.EndPairedTestSession();
                Pass("Guest clock suite: host calendar/clock baseline, blocked local minute/day/fast-forward paths, midnight state, native clock UI, exactly-once local needs, no guest world delegates, host food aging, repair without replay, offline restoration and live-flow reconnect, native hour/day skips with host/client need and timer parity, 48 native day refills, saving-first chronology and no replay on endpoint repair passed."); return;
            }
            if (!finished && phase == 15 && itemStatus != clockTest!.Status)
            { itemStatus = clockTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (!finished && (phase == 9 || phase == 10) && itemStatus != vehiclesTest!.Status)
            { itemStatus = vehiclesTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (!finished && phase == 73 && itemStatus != vehiclesTest!.Status)
            { itemStatus = vehiclesTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (!finished && phase == 76 && itemStatus != vehicleInputResponseTest!.Status)
            { itemStatus = vehicleInputResponseTest.Status; File.WriteAllText(resultPath, "RUNNING: " + itemStatus); }
            if (host && windowTintOnly && phase == 7 && plugin.Session!.Peers.Count == 1 && plugin.RemoteAvatarCount == 0)
            { Pass("Host window tint: rejected a distant request, applied a nearby leased tint roll exactly once, removed it with a leased tool, and published both native window revisions."); return; }
            if (host && dentRepairKitOnly && phase == 7 && plugin.Session!.Peers.Count == 1 && plugin.RemoteAvatarCount == 0)
            { Pass("Host dent repair kit: guest native MouseDown repaired the host-staged dent and completed the host-derived 10-click/3-second damage progression after the 1.5-second successful-repair gate; the reusable leased kit remained present until disconnect, then the exact panel baseline and item set were restored."); return; }
            if (host && phase == 7 && plugin.Session!.Peers.Count == 1 && plugin.RemoteAvatarCount == 0)
                Pass(clientActionsOnly ? "Host client-action suite: guest electric-percolator serving and host power toggle passed; guest-installed fence changed style on the host, completed native timed removal, replicated its removed state and cleaned up." : leoSuppliesOnly ? "Host Leo supplies: dry bowl fill and watering-can consumption, bunker reservoir fill/depleted pack tombstone, duplicate and distant rejection, guest disconnect cleanup, host reservoir restore and fixture item tombstones passed." : trailerAuthorityOnly || trailerCouplerOnly ? "Host trailer authority: distant guest attach/detach requests were rejected; nearby guest WorldChannel requests created and cleared the native Konig/WoodSplitter hitch, and the host restored the isolated fixture pose after disconnect." : rustRepairKitOnly ? "Host rust repair kit: one native leased-kit MouseDown repaired and replicated a rusted panel, consumed and tombstoned that kit, rejected exactly one stale-generation request, preserved the second kit on no-change, and restored all fixture state." : paintPanelsOnly ? "Host paint panels: a nearby guest paint command was accepted once, persisted into the native panel state and received by the guest; the guest returned to its local baseline after disconnect." : slotMachinesOnly ? "Host slot-machine suite: native guest cash-in, single-debit duplicate defense, range/funds rejection, host-only spin and payout, replicated result and cash-out passed; the host's reel stops replicated." : dogProductOnly ? "Host dog-product suite: untamed Leo denial, host-only eligibility, native guest click, duplicate replay, range and insufficient-funds rejection passed." : hydroOnly ? "Host hydro availability: the host cut/restore transitions replicated to the guest and returned to its prepared local baseline after disconnect." : generatorOnly ? "Host generator suite: native start-cord and kill-switch requests ran after nearby walking-pose checks, then their combustion and switch state reached the late-joining guest." : coordinatedSaveOnly ? "Host coordinated native save: native Save interception, duplicate guard, two complete native/sidecar publications, lineage preservation, previous-save integrity and native readback passed." : saveRoundOnly ? "Host native save-round collection: two native owners flushed, GUID-bound profiles, frozen writing, failure and retry cleanup passed; no disk writes." : profileOnly ? "Host personal profile: native needs/counters/statistics, nine shirt colors, pending-effect/death/layout guards and original restoration passed." : flushOnly ? "Host ownership flush: host and guest drops, frozen raw pickup/driver rejection, final fuel, native physics takeover, revocation and stale driver rejection passed." : progressOnly ? "Host progression suite: native completion imports and category unlocks, replicated updates, repair and reconnect passed." : storesOnly ? "Host stores suite: native purchases and single debits, hand/fallback delivery and private outcomes, stock/restock, rejection guards, duplicate/reconnect operation claims passed." : economyOnly ? "Host economy suite: native spending, income rounding, deferred Sunday payout/shift, host-only effects, repair and reconnect passed." : weatherOnly ? "Host weather suite: native weather transition, forecast, temperature, moon, sample replication, repair and reconnect passed." : clockOnly ? "Host clock suite: native midnight/calendar/minute/day events, host-only food aging, stopped and live clock flow, repair acknowledgment and reconnect passed." :
                    foodOnly ? "Host food suite: native refrigerated decay/spoilage, both food factories, host consumption, origin-only guest effects, preserved tombstone fields and pending-acknowledgment reconnect; affected syrup/coffee transactions passed." :
                    decorationsOnly ? "Host decorations suite: all 39 prefab factories, native freeze/unfreeze, home-land/velocity/same-frame guards, ownership, child presentation, throw physics, disconnect takeover and placed reconnect passed." :
                    itemsOnly ? "Host item suite: prefab catalog; lantern and 39 decoration factories, native values/presentation, rejection, carry and reconnect; syrup/coffee consumption, rejection and acknowledgment recovery passed." :
                    "Host: lights, avatars, pause, shared carry and consumption; lantern native emission, range/stale/ownership rejection, persistent fields and held reconnect; ATV and truck native entry/exit, exclusive drivers, replicated motion/mechanics, native door hinges, stale driver rejection and disconnect takeover passed. Light catalog=" + plugin.Lights!.CatalogHash);
        }
        private static void MovePlayer(Gameplay gameplay, Vector3 location)
        {
            // Move only the explicit isolated test player's actual walker.
            gameplay.PlayerWalking.transform.position = location;
            if (gameplay.Get_Player_Rigidbody == null) return;
            gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private void Pass(string evidence)
        {
            vrHandsTest?.Dispose();
            productionSettlementTest?.Cleanup(); tireReplayTest?.Cleanup(); meetTest?.Dispose();
            if (shellSeatsOnly || shellSuspensionOnly) ShellAssemblyTestFixture.Cleanup();
            if (partsOnly) PartsTuneBaselineFixture.Cleanup();
            clockTest?.RestoreWarningFixture();
            anxietyGameplayTest?.Dispose();
            anxietyTest?.Dispose();
            boltsTest?.Restore();
            foodTest?.Cleanup();
            fryerCookTest?.Cleanup();
            nitrousRefillTest?.Cleanup();
            fishingPresentationTest?.Abort();
            underglowTest?.Dispose();
            travelingVanTest?.Cleanup();
            purchasedTruckDoorsTest?.Cleanup();
            windowsTest?.Cleanup();
            if (preownedTruckWindows) PairedPurchasedTruckDoorsTest.RestoreIsolatedFixture();
            passengerControlsTest?.Cleanup();
            equippedToolScaleTest?.Cleanup();
            tireInflationTest?.Cleanup();
            trailerTowingTest?.Dispose();
            RepairAccessoryFixture.RestoreRust();
            if (boltsOnly) evidence += " Seeded native spanner lifecycle: two screw and two unscrew cycles stopped locally and at the observer after native unequip cleared activeTool; actual bolt turning and perceived audio quality remain manual checks.";
            if (foodOnly) evidence += " Fryer presentation: native host basket unlock reached the guest, survived a late-start hide, press cues played without changing guest stock, and original basket visibility was restored on disconnect.";
            if (clockOnly) evidence += " Critical-stat warning regression: host native critical syrup glow stayed off the healthy guest through clock replication; guest native critical syrup produced its own glow; fixture needs fields and warning visuals restored.";
            if (vehicleInputResponseOnly) evidence = plugin.TestOptions.IsHost
                ? "Host vehicle input response: received the guest's throttle, brake and movement samples plus an input-driven neutral-to-first gear change, and applied first gear on the replica."
                : "Guest vehicle input response: " + (vehicleInputResponseTest?.Evidence ?? "") + "; the host independently observed and applied the guest's first-gear and motion samples.";
            if (vehiclePlatesOnly) evidence = plugin.TestOptions.IsHost
                ? "Vehicle plate phase 79: guest and host native clicks reached the host save property; a coordinated native save and menu reload preserved the host-authored label, the reconnect snapshot matched it, distant commands were rejected, and the fixture restored its label and save preferences."
                : "Vehicle plate phase 79: two native guest clicks were applied by the host, the host's own click replicated, distant commands were rejected, the guest reconnected after host save/reload and received the saved host label, and local labels were restored on disconnect.";
            if (vehiclesOnly) evidence = "Focused ATV/truck/door suite: native registry lookup/collision/cleanup, driving ownership, physics/mechanics, native door hinges, stale-driver rejection and reconnect passed.";
            if (remoteEngineAudioOnly) evidence = plugin.TestOptions.IsHost
                ? "Remote engine audio host: guest RPM and throttle reached the host's kinematic shared ATV; the game's own engine sound played at the replicated RPM with NWH's loop kept off, and went silent after the stop (#121)."
                : "Remote engine audio guest: native ATV engine started and revved in neutral; the fixture held its driver lease until the host completed playback observation.";
            if (trafficAuthorityOnly) evidence = "Focused Gley traffic suite: host-authoritative slot baseline and motion, matching guest pool presentation, client simulation suppression, guest camera registration and interpolated traffic updates passed.";
            if (consumptionOnly) evidence = "Focused native consumption: syrup/coffee effects, rejection recreation with registry validation, replay, same-host reconnect and pending acknowledgment recovery passed.";
            if (Environment.GetCommandLineArgs().Contains("--monmp-native-save-test"))
                evidence = plugin.TestOptions.IsHost ? "Host native staging: interrupted write cleanup, complete four-file and sidecar publication, native player readback, GUID-bound profiles and gate cleanup passed." :
                    "Guest native staging: ownership/profile flush, frozen host write, failure recovery and completion cleanup passed.";
            settlingEvidence = evidence; settleUntil = Time.unscaledTime + SettleSeconds;
            runEvidence.PassedChecks();
            settleNeedsSession = plugin.Session?.IsRunning == true;
            File.WriteAllText(resultPath, "RUNNING: passed; settling " + SettleSeconds + " s before recording the result");
        }
        private void Settle()
        {
            if (settleNeedsSession && plugin.Session?.IsRunning != true)
            {
                string evidence = settlingEvidence!; settlingEvidence = null;
                Fail("The suite passed, then the session ended during the " + SettleSeconds + " s settle window (" + plugin.Session?.Status + "). Passed evidence: " + evidence);
                return;
            }
            // Unity's unscaledTime is cached at frame start. Expensive native checks
            // can finish much later in that frame, so measure the actual post-check
            // interval rather than accepting a shorter wall-clock settle window.
            if (!runEvidence.HasSettled(SettleSeconds)) return;
            // The host stays up until the guest has finished its own settle window and left.
            if (plugin.TestOptions.IsHost && plugin.Session?.IsRunning == true && plugin.Session.Peers.Count > 1
                && Time.unscaledTime < settleUntil + 45f) return;
            File.WriteAllText(resultPath, "MONMP_PAIRED_GAMEPLAY_PASS\n" + settlingEvidence);
            runEvidence.Complete(settlingEvidence!, SettleSeconds);
            finished = true; Application.Quit();
        }
        internal void Fail(string reason)
        {
            // A guest whose host stops first fails on the lost session; keep where it was.
            if (!string.IsNullOrEmpty(itemStatus)) reason += "\nLast status: " + itemStatus;
            try { vrHandsTest?.Dispose(); productionSettlementTest?.Cleanup(); tireReplayTest?.Cleanup(); meetTest?.Dispose(); }
            catch (Exception cleanup) { reason += " | Audit fixture cleanup: " + cleanup; }
            if (shellSeatsOnly || shellSuspensionOnly)
            {
                try { shellSeatsTest?.Abort(); shellSuspensionTest?.Abort(); }
                catch (Exception cleanup) { reason += " | Shell native fixture restoration: " + cleanup.Message; }
                try { if (plugin.Session?.IsRunning == true) plugin.EndPairedTestSession(); }
                catch (Exception cleanup) { reason += " | Shell test disconnect: " + cleanup.Message; }
                finally { ShellAssemblyTestFixture.Cleanup(); }
            }
            if (partsOnly)
            {
                try { if (plugin.Session?.IsRunning == true) plugin.EndPairedTestSession(); PartsTuneBaselineFixture.Cleanup(); }
                catch (Exception ex) { reason += "\nParts tune baseline fixture cleanup failed: " + ex; }
            }
            try { trailerTowingTest?.Dispose(); }
            catch (Exception cleanup) { reason += " | Trailer towing fixture cleanup: " + cleanup.Message; }
            try { PairedTrailerAuthorityTest.RestoreFixture(); }
            catch (Exception cleanup) { reason += " | Trailer coupler fixture cleanup: " + cleanup.Message; }
            try { equippedToolScaleTest?.Cleanup(); }
            catch (Exception cleanup) { reason += " | Equipped tool scale fixture cleanup: " + cleanup.Message; }
            try { tireInflationTest?.Cleanup(); }
            catch (Exception cleanup) { reason += " | Tire inflation fixture cleanup: " + cleanup.Message; }
            try { passengerControlsTest?.Cleanup(); }
            catch (Exception cleanup) { reason += " | Passenger controls fixture cleanup: " + cleanup.Message; }
            try { fishingPresentationTest?.Abort(); }
            catch (Exception cleanup) { reason += " | FishingPresentation fixture cleanup: " + cleanup.Message; }
            try { underglowTest?.Dispose(); }
            catch (Exception cleanup) { reason += " | Underglow fixture cleanup: " + cleanup.Message; }
            try { travelingVanTest?.Cleanup(); }
            catch (Exception cleanup) { reason += " | TravelingVan fixture cleanup: " + cleanup.Message; }
            try { windowsTest?.Cleanup(); purchasedTruckDoorsTest?.Cleanup(); }
            catch (Exception cleanup) { reason += " | PurchasedTruckDoors fixture cleanup: " + cleanup.Message; }
            try { PairedPurchasedTruckDoorsTest.RestoreIsolatedFixture(); }
            catch (Exception cleanup) { reason += " | Purchased truck preparation cleanup: " + cleanup.Message; }
            try { nitrousRefillTest?.Cleanup(); }
            catch (Exception cleanup) { reason += " | Nitrous refill fixture cleanup: " + cleanup.Message; }
            try { fryerCookTest?.Cleanup(); }
            catch (Exception cleanup) { reason += " | Fryer cook fixture cleanup: " + cleanup.Message; }
            try { boltsTest?.Restore(); }
            catch (Exception cleanup) { reason += " | Bolt sound fixture cleanup: " + cleanup.Message; }
            try { foodTest?.Cleanup(); }
            catch (Exception cleanup) { reason += " | Fryer fixture cleanup: " + cleanup.Message; }
            try { anxietyGameplayTest?.Dispose(); }
            catch (Exception ex) { reason += "\nAnxiety gameplay fixture cleanup failed: " + ex; }
            try { anxietyTest?.Dispose(); }
            catch (Exception ex) { reason += "\nAnxiety fixture cleanup failed: " + ex; }
            try { clockTest?.RestoreWarningFixture(); }
            catch (Exception ex) { reason += "\nClock warning fixture cleanup failed: " + ex; }
            try { chatTest?.Cleanup(); }
            catch (Exception ex) { reason += "\nChat fixture cleanup failed: " + ex; }
            try { sleepTest?.Cleanup(); }
            catch (Exception ex) { reason += "\nSleep fixture cleanup failed: " + ex; }
            try { racecarBuildTest?.Cleanup(); }
            catch (Exception ex) { reason += "\nRacecar fixture cleanup failed: " + ex; }
            try { PairedRacecarBuildTest.RestoreIsolatedFixture(); }
            catch (Exception ex) { reason += "\nRacecar staged-state restoration failed: " + ex; }
            try { steeringRackTest?.Abort(); }
            catch (Exception ex) { reason += "\nSteering-rack fixture cleanup failed: " + ex; }
            try { vehicleInputResponseTest?.Dispose(); }
            catch (Exception ex) { reason += "\nVehicle input-response fixture cleanup failed: " + ex; }
            try { vehiclePlatesTest?.Dispose(); }
            catch (Exception ex) { reason += "\nVehicle plate fixture cleanup failed: " + ex; }
            try { trafficAuthorityTest?.Dispose(); }
            catch (Exception ex) { reason += "\nTraffic fixture cleanup failed: " + ex; }
            try { npcRouteTalkTest?.Dispose(); }
            catch (Exception ex) { reason += "\nNPC route-talk fixture cleanup failed: " + ex; }
            try { rustRepairKitTest?.Abort();
            dentRepairKitTest?.Abort(); }
            catch (Exception ex) { reason += "\nDent-repair fixture cleanup failed: " + ex; }
            try { RepairAccessoryFixture.RestoreRust(); }
            catch (Exception ex) { reason += "\nAccessory preparation cleanup failed: " + ex; }
            try { chainsawTest?.Abort(); }
            catch (Exception ex) { reason += "\nChainsaw fixture cleanup failed: " + ex; }
            try { forestTest?.Abort(); }
            catch (Exception ex) { reason += "\nForest fixture cleanup failed: " + ex; }
            File.WriteAllText(resultPath, "MONMP_PAIRED_GAMEPLAY_FAIL\n" + reason + "\n" + rustRepairKitTest?.Status + "\n" + dentRepairKitTest?.Status);
            runEvidence.Fail(reason);
            finished = true; Application.Quit(1);
        }
    }
}
