using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonMP.Core.Session;
using MonMP.Core.Protocol;
using MonMP.Core.World;
using MonMP.Core.Persistence;
using MonMP.Plugin.Diagnostics;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedWorld : IDisposable
    {
        internal Dictionary<int, VehicleSeatPoseCatalog.Entry> SeatPoseEntries { get; } = new Dictionary<int, VehicleSeatPoseCatalog.Entry>();
        public NativeEntityRegistry Entities { get; } = new NativeEntityRegistry();
        public SharedLights Lights { get; }
        internal WorldToolAim ToolAim { get; } = new WorldToolAim();
        public SharedItems Items { get; }
        public SharedFishingPresentation FishingPresentation { get; }
        public SharedElectricPercolator ElectricPercolator { get; }
        public SharedFenceActions FenceActions { get; }
        public SharedTireChanger TireChanger { get; }
        public SharedFishBuckets FishBuckets { get; }
        public SharedBuggyBuild BuggyBuild { get; }
        public SharedRacecarBuild RacecarBuild { get; }
        public SharedVehicles Vehicles { get; }
        public SharedPassengerControls PassengerControls { get; }
        public SharedBoatControls BoatControls { get; }
        public SharedBoatMotion BoatMotion { get; }
        public SharedVehiclePlates VehiclePlates { get; }
        public SharedTraffic Traffic { get; }
        public SharedTrailers Trailers { get; }
        public SharedPaintPanels PaintPanels { get; }
        public SharedWindowTints WindowTints { get; }
        public SharedBuildingDoors Doors { get; }
        public SharedVehicleParts VehicleParts { get; }
        public SharedVehicleBolts Bolts { get; }
        public SharedTirePressure TirePressure { get; }
        public SharedSceneAirEquipment SceneAirEquipment { get; }
        public SharedFuel Fuel { get; }
        public SharedGenerator Generator { get; }
        public SharedFoodPrep FoodPrep { get; }
        public SharedHydroMail HydroMail { get; }
        public SharedScrapyardGate ScrapyardGate { get; }
        public SharedScrapyardRadio ScrapyardRadio { get; }
        public SharedFurnitureRadios FurnitureRadios { get; }
        public SharedStorageGarages StorageGarages { get; }
        public SharedNewspaperCardbox NewspaperCardbox { get; }
        public SharedDogTreats DogTreats { get; }
        public SharedKegPurchase Kegs { get; }
        public SharedBarDrinkPurchase BarDrink { get; }
        public SharedVehicleSales? VehicleSales { get; private set; }
        public SharedPizzaSnack PizzaSnack { get; }
        public SharedChalumeauPack ChalumeauPack { get; }
        public SharedDealerBunkerPurchase DealerBunkerPurchase { get; }
        public SharedPaintMixing PaintMixing { get; }
        public SharedWoodenKegs WoodenKegs { get; }
        public SharedTowingService TowingService { get; }
        public SharedPortableConsoleCharger PortableConsoleCharger { get; }
        public SharedHoistPullers HoistPullers { get; }
        public SharedBunkerBathtub BunkerBathtub { get; }
        public SharedSinks Sinks { get; }
        public SharedTriggerFeedback TriggerFeedback { get; }
        public SharedSceneFurniture SceneFurniture { get; }
        public SharedRoadSigns RoadSigns { get; }
        public SharedPoliceSpots PoliceSpots { get; }
        public SharedFuelNozzles FuelNozzles { get; }
        public SharedRemoteClicks RemoteClicks { get; }
        public SharedNitrous Nitrous { get; }
        public SharedNitrousRefillStation NitrousRefillStation { get; }
        public SharedVehicleTune VehicleTune { get; }
        public SharedVehicleUi VehicleUi { get; }
        public SharedUnderglow Underglow { get; }
        public SharedTravelingVan TravelingVan { get; }
        public SharedGilles Gilles { get; }
        public SharedWoodSplitters WoodSplitters { get; }
        public SharedSyrupCargo SyrupCargo { get; }
        public SharedBunkerWatering BunkerWatering { get; }
        public SharedBunkerCompost BunkerCompost { get; }
        public SharedWheelbarrows Wheelbarrows { get; }
        public SharedCreepers Creepers { get; }
        public SharedDollies Dollies { get; }
        public SharedExtraSeats ExtraSeats { get; }
        public SharedFurnitureSeats FurnitureSeats { get; }
        public SharedGarages Garages { get; }
        public SharedCottagePurchase CottagePurchase { get; }
        public SharedBoatSale BoatSale { get; }
        public SharedStorageGarageSale StorageGarageSale { get; }
        public SharedDealerMachines DealerMachines { get; }
        public SharedSugarShack SugarShack { get; }
        public SharedForest Forest { get; }
        public SharedGarden Garden { get; }
        public SharedTubing Tubing { get; }
        public SharedSleep Sleep { get; }
        public SharedClock Clock { get; }
        public SharedWeather Weather { get; }
        public SharedEconomy Economy { get; }
        public SharedStatistics Statistics { get; }
        public SharedStores Stores { get; }
        public SharedLeo Leo { get; }
        public SharedLeoSupplies LeoSupplies { get; }
        public SharedActorRoutes ActorRoutes { get; }
        public SharedSchoolBus SchoolBus { get; }
        public SharedSchoolBusPassengers SchoolBusPassengers { get; }
        public SharedMartinCar MartinCar { get; }
        public SharedSlotMachines SlotMachines { get; }
        public SharedMetalSales MetalSales { get; }
        public SharedTrashRecycle TrashRecycle { get; }
        public SharedProgress Progress { get; }
        public SharedInformationDisplays Information { get; }
        public SharedNpcInteractions NpcInteractions { get; }
        public NativePlayerProfile Profile { get; }
        public SaveRuntimeGate SaveGate { get; }
        public SaveRoundChannel? SaveRounds { get; private set; }
        public HostSaveCoordinator? Saves { get; }
        public LiveProfileChannel? LiveProfiles { get; private set; }
        public GuestProfileRestore? ProfileRestore { get; private set; }
        public SharedReconnectPositions? ReconnectPositions { get; private set; }
        public SharedPermadeath? Permadeath { get; private set; }
        public SharedLeoHearts? LeoHearts { get; private set; }
        public SharedPresentationEvents? Events { get; private set; }
        public SharedMeetRace? MeetRace { get; private set; }
        public SharedSpeedwayEvents? SpeedwayEvents { get; private set; }
        public SharedEasterEggs? EasterEggs { get; private set; }
        public bool PersonalProfileReady => ProfileRestore?.Ready == true;
        public string CatalogHash { get; }
        private static readonly ushort[] DynamicAdapters = new[] { ItemState.Adapter, FurnitureRadioState.Adapter, CdPlaybackState.Adapter, RadioStationClockState.Adapter, StorageGarageDoorState.Adapter, FishBucketState.Adapter, ForestState.Adapter, GardenState.Adapter, TubingState.Adapter, TrafficSlotState.Adapter, DynamicPaintPanelState.Adapter, PurchasedVehiclePaintState.Adapter, VehicleState.Adapter, TrailerWorldState.Adapter, SharedUnderglow.Adapter, SharedNitrous.Adapter, SharedVehicleTune.Adapter, VehicleUiState.Adapter, ElectricPercolatorBrewState.Adapter, VehiclePartState.Adapter, BoltHoleState.Adapter, TirePressureState.Adapter, FurnitureSeatState.Adapter, VehiclePlateState.Adapter, WindowTintState.Adapter, FuelState.Adapter, VehicleWindowState.Adapter, VehiclePassengerSeatState.Adapter, HoistPullerState.Adapter };
        private IEnumerable<ulong> RequiredDynamicCatalogEntityIds => VehicleParts.RequiredCatalogEntityIds
            .Concat(Bolts.RequiredCatalogEntityIds).Concat(TirePressure.RequiredCatalogEntityIds)
            .Concat(VehiclePlates.RequiredCatalogEntityIds).Concat(WindowTints.RequiredCatalogEntityIds)
            .Concat(Fuel.RequiredCatalogEntityIds).Concat(VehicleUi.RequiredDynamicCatalogEntityIds)
            .Concat(Vehicles.Windows.RequiredCatalogEntityIds).Concat(ExtraSeats.RequiredCatalogEntityIds)
            .Concat(HoistPullers.RequiredCatalogEntityIds).Concat(Vehicles.RequiredPassengerCatalogEntityIds)
            .Concat(SchoolBusPassengers.InitialRecords().Where(record => record.Id > WorldRecord.DynamicIdStart).Select(record => record.Id));
        private WorldChannel? channel;
        internal WorldChannel Channel => channel ?? throw new InvalidOperationException("World not attached.");

#if MONMP_DEVELOPMENT
        internal string ReceiveReport()
        {
            if (channel == null) return "";
            var text = new System.Text.StringBuilder("received deltas by adapter (65535 = snapshot/repair):").AppendLine();
            foreach (var pair in channel.ReceiveCost.OrderByDescending(p => p.Value.Ticks).Take(25))
                text.Append(pair.Key).Append(" count=").Append(pair.Value.Count)
                    .Append(" total=").Append((pair.Value.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0"))
                    .Append("ms worst=").Append((pair.Value.Worst * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.00")).AppendLine("ms");
            text.AppendLine("received host commands by adapter:");
            foreach (var pair in channel.CommandCost.OrderByDescending(p => p.Value.Ticks).Take(25))
                text.Append(pair.Key).Append(" requests=").Append(pair.Value.Requests)
                    .Append(" callbacks=").Append(pair.Value.Calls).Append(" rejected=").Append(pair.Value.Rejected)
                    .Append(" failures=").Append(pair.Value.Failures)
                    .Append(" total=").Append((pair.Value.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0"))
                    .Append("ms worst=").Append((pair.Value.Worst * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.00")).AppendLine("ms");
            text.Append("world queues: deltas=").Append(channel.PendingDeltas)
                .Append(" snapshots=").Append(channel.PendingSnapshotPackets)
                .Append(" deferredMotion=").Append(channel.PendingSpatialDeltas)
                .Append(" orderedHistory=").Append(channel.PendingOrderedMembershipRecords)
                .Append(" orderedHighWater=").Append(channel.OrderedMembershipHighWater)
                .Append(" orderedOverflowPeers=").Append(channel.OrderedMembershipOverflows).AppendLine();
            text.Append("world application: pending retries=").Append(channel.ApplyRetriesPending)
                .Append(" healed retries=").Append(channel.ApplyRetriesHealed)
                .Append(" handler failures=").Append(channel.HandlerFailures).AppendLine();
            if (channel.Records.TryGetValue(ClockState.Entity, out var clockRecord))
            {
                var clockState = ClockState.Decode(clockRecord.Payload);
                text.Append("shared clock: revision=").Append(clockRecord.Revision)
                    .Append(" minute=").Append(clockState.MinuteOfDay)
                    .Append(" totalTicks=").Append(clockState.TotalTicks).AppendLine();
            }
            var session = attachedSession;
            if (session != null)
            {
                text.Append("network: sent=").Append(session.BytesSentPerSecond).Append("B/s received=")
                    .Append(session.BytesReceivedPerSecond).Append("B/s guestCommandQueue=").Append(session.GuestQueueDepth).AppendLine();
                foreach (uint peer in session.Peers.Keys.Where(p => p != session.LocalPeerId && (session.IsHost || p == 0)).OrderBy(p => p))
                {
                    var metrics = session.MetricsFor(peer);
                    text.Append("peer ").Append(peer).Append(" RTT=").Append(metrics.RoundTripTimeMs)
                        .Append("ms jitter=").Append(metrics.JitterMs.ToString("0.0"))
                        .Append("ms transportLoss(cumulative)=").Append(metrics.PacketLossPercent.ToString("0.00"))
                        .Append("% reliableQueue=").Append(metrics.ReliableQueuePackets).AppendLine();
                }
                if (session.LastPacketError.Length != 0) text.Append("last packet error: ").AppendLine(session.LastPacketError);
            }
            return text.ToString();
        }
#endif

        public string SynchronizationStatus => channel?.SynchronizationStatus ?? "Not connected";
        public string LastRepairReason => channel?.LastRepairReason ?? "None";
        public bool RepairWorld() => channel?.RequestResync(SnapshotScope.World) == true;
        public uint[] Owners => Items.Owners.Concat(Vehicles.Owners).Concat(BoatMotion.Owners).Concat(SchoolBusPassengers.Owners).Concat(MartinCar.Owners).Concat(ExtraSeats.Owners).Concat(FurnitureSeats.Owners).Concat(SceneAirEquipment.Owners).Concat(Bolts.Owners).Concat(Creepers.Owners).Concat(Wheelbarrows.Owners).Distinct().ToArray();
        public bool LocalFlushComplete => Items.LocalFlushComplete && Vehicles.LocalFlushComplete && BoatMotion.LocalFlushComplete && SchoolBusPassengers.LocalFlushComplete && MartinCar.LocalFlushComplete && ExtraSeats.LocalFlushComplete && FurnitureSeats.LocalFlushComplete && Creepers.LocalFlushComplete && Wheelbarrows.LocalFlushComplete && SceneAirEquipment.LocalFlushComplete && Bolts.LocalFlushComplete && VehicleParts.LocalFlushComplete && Doors.TransactionsSettled && NpcInteractions.TransactionsSettled && Statistics.LocalFlushComplete;
        public void BlockOwnershipTransfers(bool blocked) { Items.TransfersBlocked = blocked; Vehicles.TransfersBlocked = blocked; BoatMotion.TransfersBlocked = blocked; SchoolBusPassengers.TransfersBlocked = blocked; MartinCar.TransfersBlocked = blocked; ExtraSeats.TransfersBlocked = blocked; FurnitureSeats.SetNewActionsBlocked(blocked); Creepers.TransfersBlocked = blocked; Wheelbarrows.TransfersBlocked = blocked; }
        public bool TransactionsSettled => Items.TransactionsSettled && FishBuckets.TransactionsSettled && Stores.TransactionsSettled && Doors.TransactionsSettled && NpcInteractions.TransactionsSettled &&
            HydroMail.TransactionsSettled && SlotMachines.TransactionsSettled && MetalSales.TransactionsSettled && TrashRecycle.TransactionsSettled &&
            SugarShack.TransactionsSettled && CottagePurchase.TransactionsSettled && BoatSale.TransactionsSettled && StorageGarageSale.TransactionsSettled && PaintPanels.TransactionsSettled &&
            WindowTints.TransactionsSettled && LeoSupplies.TransactionsSettled && DealerMachines.TransactionsSettled && NewspaperCardbox.TransactionsSettled &&
            DogTreats.TransactionsSettled && Kegs.TransactionsSettled && BarDrink.TransactionsSettled && PortableConsoleCharger.TransactionsSettled &&
            HoistPullers.TransactionsSettled && (VehicleSales?.TransactionsSettled ?? true) && DealerBunkerPurchase.TransactionsSettled &&
            PizzaSnack.PendingCount == 0 && ChalumeauPack.TransactionsSettled && PaintMixing.TransactionsSettled && WoodenKegs.TransactionsSettled &&
            TowingService.TransactionsSettled && TireChanger.TransactionsSettled && ElectricPercolator.TransactionsSettled && FenceActions.TransactionsSettled && RemoteClicks.TransactionsSettled && Statistics.LocalFlushComplete;
        // Recovery can bypass an unreachable peer's readiness, but cannot publish paid
        // native production while its delayed output is still uncertain.
        internal bool NativeProductionSettled => RemoteClicks.TransactionsSettled &&
            ElectricPercolator.TransactionsSettled && TireChanger.TransactionsSettled && SlotMachines.TransactionsSettled &&
            DealerMachines.ProductionSettled && BarDrink.LocalProductionSettled && SugarShack.ProductionSettled;
        public void BlockNewTransactions(bool blocked)
        {
            BlockOwnershipTransfers(blocked);
            NpcInteractions.NewActionsBlocked = blocked; RemoteClicks.NewActionsBlocked = blocked;
            ElectricPercolator.NewActionsBlocked = blocked; FenceActions.NewActionsBlocked = blocked;
            if (VehicleSales != null) VehicleSales.NewPurchasesBlocked = blocked;
            Stores.NewPurchasesBlocked = blocked; Doors.NewPurchasesBlocked = blocked; HydroMail.NewActionsBlocked = blocked; SlotMachines.NewActionsBlocked = blocked;
            MetalSales.NewSalesBlocked = blocked; TrashRecycle.NewDepositsBlocked = blocked; SugarShack.NewPurchasesBlocked = blocked;
            CottagePurchase.NewPurchasesBlocked = blocked; BoatSale.NewPurchasesBlocked = blocked; PaintPanels.SetNewActionsBlocked(blocked);
            StorageGarageSale.NewPurchasesBlocked = blocked;
            WindowTints.SetNewActionsBlocked(blocked); LeoSupplies.NewActionsBlocked = blocked; DealerMachines.NewPurchasesBlocked = blocked;
            NewspaperCardbox.NewActionsBlocked = blocked; DogTreats.NewActionsBlocked = blocked; Kegs.NewPurchasesBlocked = blocked;
            BarDrink.NewActionsBlocked = blocked; PortableConsoleCharger.NewActionsBlocked = blocked; HoistPullers.SetNewActionsBlocked(blocked);
            TirePressure.SetNewActionsBlocked(blocked); SceneAirEquipment.SetNewActionsBlocked(blocked);
            Bolts.SetNewActionsBlocked(blocked);
            PizzaSnack.NewActionsBlocked = blocked; ChalumeauPack.NewActionsBlocked = blocked;
            DealerBunkerPurchase.NewPurchasesBlocked = blocked; PaintMixing.NewActionsBlocked = blocked;
            WoodenKegs.NewActionsBlocked = blocked; TowingService.NewActionsBlocked = blocked; TireChanger.NewActionsBlocked = blocked;
        }
        public void FlushLocalOwnership() { SceneAirEquipment.FlushLocalOwnership(); Bolts.FlushLocalOwnership(); Items.FlushLocalOwnership(); Creepers.FlushLocalOwnership(); Wheelbarrows.FlushLocalOwnership(); ExtraSeats.FlushLocalOwnership(); FurnitureSeats.FlushLocalOwnership(); Vehicles.FlushLocalOwnership(); BoatMotion.FlushLocalOwnership(); SchoolBusPassengers.FlushLocalOwnership(); MartinCar.FlushLocalOwnership(); }
        internal void PrepareLocalDisconnect()
        {
            foreach (var prepare in new Action[] { Vehicles.PrepareLocalDisconnect, BoatMotion.PrepareLocalDisconnect })
            {
                try { prepare(); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning("MonMP disconnect preparation: " + ex.Message); }
            }
        }
        internal void CancelLocalDisconnect()
        {
            foreach (var cancel in new Action[] { Vehicles.CancelLocalDisconnect, BoatMotion.CancelLocalDisconnect })
            {
                try { cancel(); }
                catch (Exception ex) { UnityEngine.Debug.LogWarning("MonMP cancelled disconnect: " + ex.Message); }
            }
        }
        public void RevokeOwnership(uint peer)
        {
            foreach (var release in new Action<uint>[] { Items.RevokeOwnership, Vehicles.RevokeOwnership, BoatMotion.RevokeOwnership,
                SchoolBusPassengers.RevokeOwnership, MartinCar.RevokeOwnership, ExtraSeats.RevokeOwnership, FurnitureSeats.RevokeOwnership,
                Trailers.RevokeOwnership, SceneAirEquipment.RevokeOwnership, Bolts.RevokeOwnership, Creepers.RevokeOwnership, Wheelbarrows.RevokeOwnership })
                SaveStep(() => release(peer));
        }
        private static void SaveStep(Action action)
        {
            try { action(); }
            catch (Exception ex) { UnityEngine.Debug.LogWarning("MonMP save recovery: " + ex.Message); }
        }
        internal void PrepareRecoverySave()
        {
            if (!SaveGate.Active) SaveStep(() => SaveGate.Begin(true));
            SaveStep(SettleSaveProduction);
            SaveStep(FlushForSave);
            SaveStep(() => { foreach (uint peer in SaveOwners) RevokeForSave(peer); });
            SaveStep(Profile.SettleForSave);
            SaveStep(() => SaveGate.Freeze(true));
        }
        private double saveSettlementReadyAt;
        private int saveProductionFrame = -1;
        internal bool SaveSnapshotReady => UnityEngine.Time.frameCount > saveProductionFrame;
        private void SettleSaveProduction()
        {
            if (Saves == null) return;
            saveProductionFrame = UnityEngine.Time.frameCount;
            SaveStep(() =>
            {
                if (!RemoteClicks.TransactionsSettled || !ElectricPercolator.TransactionsSettled ||
                    !TireChanger.TransactionsSettled || !SlotMachines.TransactionsSettled ||
                    !DealerMachines.ProductionSettled || !BarDrink.LocalProductionSettled || !SugarShack.ProductionSettled)
                    saveSettlementReadyAt = TimeNow() + 0.05;
            });
            SaveStep(() => RemoteClicks.SettleForSave());
            SaveStep(() => ElectricPercolator.SettleForSave());
            SaveStep(() => TireChanger.SettleForSave());
            SaveStep(() => SlotMachines.SettleForSave());
            SaveStep(() => DealerMachines.SettleForSave());
            SaveStep(() => SugarShack.SettleForSave());
            SaveStep(() => BarDrink.SettleForSave());
        }
        public SharedWorld(Gameplay gameplay, bool host, string diagnostics, ConsumptionInbox consumption)
        {
            try
            {
                // Before any adapter's initial discovery: remembered native components
                // belong to one world and are seeded again by its first complete searches.
                Patches.NativeSourcePatches.Begin();
                Lights = new SharedLights(diagnostics, Entities);
                Items = new SharedItems(gameplay, host, diagnostics, consumption, Entities);
                TriggerFeedback = new SharedTriggerFeedback(host, Items);
                FishingPresentation = new SharedFishingPresentation(gameplay, host, Items);
                ElectricPercolator = new SharedElectricPercolator(gameplay, host, Items);
                FenceActions = new SharedFenceActions(gameplay, host, Items);
                TireChanger = new SharedTireChanger(gameplay, host, Items, diagnostics);
                VehicleSales = new SharedVehicleSales(gameplay, host, Items, Entities, diagnostics);
                PizzaSnack = new SharedPizzaSnack(ToolAim.CheckSegment, gameplay, host, Items);
                ChalumeauPack = new SharedChalumeauPack(host, Items);
                DealerBunkerPurchase = new SharedDealerBunkerPurchase(gameplay, host, Items);
                PaintMixing = new SharedPaintMixing(gameplay, host, Items, Entities);
                WoodenKegs = new SharedWoodenKegs(ToolAim.CheckSegment, gameplay, host, Items);
                TowingService = new SharedTowingService(gameplay, host);
                FishBuckets = new SharedFishBuckets(Items, host);
                BuggyBuild = new SharedBuggyBuild(host, diagnostics, Entities);
                RacecarBuild = new SharedRacecarBuild(host, diagnostics);
                Vehicles = new SharedVehicles(gameplay, host, diagnostics, Entities);
                PassengerControls = new SharedPassengerControls(Vehicles, host);
                BoatControls = new SharedBoatControls(gameplay, host, diagnostics, Entities);
                BoatMotion = new SharedBoatMotion(gameplay, host, diagnostics, Entities);
                BoatMotion.Controls = BoatControls;
                VehiclePlates = new SharedVehiclePlates(gameplay, host, diagnostics, Entities);
                Traffic = new SharedTraffic(gameplay, host, diagnostics);
                Trailers = new SharedTrailers(gameplay, host, Vehicles, Entities, diagnostics);
                Vehicles.Trunk.Trailers = Trailers;
                VehicleSales!.PurchasedVehicleAvailable += BindPurchasedVehicle;
                foreach (var purchased in VehicleSales!.PurchasedVehicles())
                    BindPurchasedVehicle(purchased.Entity, purchased.Type, purchased.Instance);
                VehicleParts = new SharedVehicleParts(host, diagnostics, Entities);
                TirePressure = new SharedTirePressure(ToolAim.CheckSegment, gameplay, host, Items, VehicleParts, Entities);
                SceneAirEquipment = new SharedSceneAirEquipment(gameplay, host, TirePressure, Entities);
                PaintPanels = new SharedPaintPanels(ToolAim.CheckSegment, host, Items, VehicleParts, diagnostics);
                WindowTints = new SharedWindowTints(host, Items, diagnostics);
                Doors = new SharedBuildingDoors(host, diagnostics, Entities);
                Bolts = new SharedVehicleBolts(ToolAim.CheckSegment, host, diagnostics, Entities);
                // Saved purchases were bound to motion before these catalogs existed.
                // Replay only their catalog registration after both adapters are constructed.
                foreach (var purchased in VehicleSales!.PurchasedVehicles())
                {
                    VehicleParts.RegisterPurchasedVehicle(purchased.Entity, purchased.Type, purchased.Instance);
                    Bolts.RegisterPurchasedVehicle(purchased.Entity, purchased.Type, purchased.Instance);
                }
                Fuel = new SharedFuel(gameplay, host, Items, Vehicles, Vehicles.Driven, diagnostics, Entities);
                foreach (var purchased in VehicleSales!.PurchasedVehicles())
                {
                    VehiclePlates.RegisterPurchasedVehicle(purchased.Entity, purchased.Type, purchased.Instance);
                    PaintPanels.RegisterPurchasedVehicle(purchased.Entity, purchased.Type, purchased.Instance);
                    WindowTints.RegisterPurchasedVehicle(purchased.Entity, purchased.Type, purchased.Instance);
                    Fuel.RegisterPurchasedVehicle(purchased.Entity, purchased.Type, purchased.Instance);
                }
                Generator = new SharedGenerator(host, diagnostics, Entities);
                HydroMail = new SharedHydroMail(host, Items);
                FoodPrep = new SharedFoodPrep(host, diagnostics, Entities);
                ScrapyardGate = new SharedScrapyardGate(host, diagnostics, Entities);
                ScrapyardRadio = new SharedScrapyardRadio(gameplay, host, Items);
                FurnitureRadios = new SharedFurnitureRadios(host, Items);
                StorageGarages = new SharedStorageGarages(host, Vehicles);
                NewspaperCardbox = new SharedNewspaperCardbox(gameplay, host, Items);
                DogTreats = new SharedDogTreats(ToolAim.CheckSegment, gameplay, host, Items);
                Kegs = new SharedKegPurchase(host, Items);
                PortableConsoleCharger = new SharedPortableConsoleCharger(gameplay, host, Items, Entities);
                HoistPullers = new SharedHoistPullers(ToolAim.CheckSegment, host, Entities, gameplay, VehicleSales!.PurchasedVehicles());
                BunkerBathtub = new SharedBunkerBathtub(host, diagnostics, Entities);
                Sinks = new SharedSinks(host, diagnostics, Entities);
                SceneFurniture = new SharedSceneFurniture(host, gameplay, Entities);
                RoadSigns = new SharedRoadSigns(host, gameplay, Vehicles, Entities);
                PoliceSpots = new SharedPoliceSpots(host, Entities);
                FuelNozzles = new SharedFuelNozzles(host, gameplay, Entities);
                RemoteClicks = new SharedRemoteClicks(host, diagnostics, Entities);
                Nitrous = new SharedNitrous(host, Vehicles);
                NitrousRefillStation = new SharedNitrousRefillStation(host, diagnostics, Entities);
                VehicleTune = new SharedVehicleTune(host, Vehicles);
                VehicleUi = new SharedVehicleUi(host, Vehicles);
                Underglow = new SharedUnderglow(host, Vehicles);
                TravelingVan = new SharedTravelingVan(host);
                Gilles = new SharedGilles(host, Items);
                WoodSplitters = new SharedWoodSplitters(host, Trailers);
                SyrupCargo = new SharedSyrupCargo(host, diagnostics);
                BunkerWatering = new SharedBunkerWatering(gameplay, host, diagnostics);
                BunkerCompost = new SharedBunkerCompost(gameplay, host);
                Wheelbarrows = new SharedWheelbarrows(gameplay, host, diagnostics, Entities);
                Creepers = new SharedCreepers(gameplay, host, diagnostics, Entities);
                Dollies = new SharedDollies(host, Vehicles, Trailers);
                // Loose cargo in shared vehicles and wheelbarrows is shared relative to its carrier.
                var carriers = new ItemCarriers();
                carriers.Add(Vehicles.CarrierFrames); carriers.Add(BoatMotion.CarrierFrames); carriers.Add(Wheelbarrows.CarrierFrames); carriers.Add(Trailers.CarrierFrames);
                carriers.DetachedSupportFrame = Items.FrozenCargoFrame;
                Items.Carriers = carriers; Vehicles.Carriers = carriers; BoatMotion.Carriers = carriers; Wheelbarrows.Carriers = carriers; Trailers.Carriers = carriers;
                Garages = new SharedGarages(host, diagnostics, Entities);
                CottagePurchase = new SharedCottagePurchase(gameplay, host, Items, diagnostics);
                BoatSale = new SharedBoatSale(gameplay, host, Items, diagnostics);
                StorageGarageSale = new SharedStorageGarageSale(gameplay, host, Items, diagnostics);
                SugarShack = new SharedSugarShack(gameplay, host, Items, diagnostics, Entities);
                Items.ClaimRemoved = item => TireChanger.ClaimRemovedItem(item) || SugarShack.ClaimRemovedLog(item);
                Items.CanDrainNativeOutputs = () => SaveRounds?.Phase == SavePhase.Collecting;
                Forest = new SharedForest(host, diagnostics);
                Items.RemoteSpawned += Forest.ReplicaSpawned;
                Garden = new SharedGarden(host, diagnostics) { Items = Items };
                Tubing = new SharedTubing(host, diagnostics) { Items = Items };
                Clock = new SharedClock(host, diagnostics);
                Sleep = new SharedSleep(host, gameplay);
                Weather = new SharedWeather(host, diagnostics);
                Economy = new SharedEconomy(host, diagnostics);
                Statistics = new SharedStatistics(host);
                Stores = new SharedStores(host, Items, diagnostics);
                BarDrink = new SharedBarDrinkPurchase(gameplay, host, Items, profile => LiveProfiles?.Find(profile));
                Kegs.HostPurchaseRequested += (peer, profile, listing, request) =>
                    Stores.ExecuteKegPurchase(Kegs, peer, profile, listing, request);
                DealerMachines = new SharedDealerMachines(gameplay, host, Items, diagnostics, Entities);
                Leo = new SharedLeo(gameplay, host, diagnostics, Items, Vehicles);
                LeoSupplies = new SharedLeoSupplies(gameplay, host, diagnostics, Items, Entities);
                ActorRoutes = new SharedActorRoutes(host, diagnostics, Entities);
                SchoolBus = new SharedSchoolBus(host, diagnostics, Vehicles, Entities);
                SchoolBusPassengers = new SharedSchoolBusPassengers(gameplay, host, diagnostics, Vehicles, SchoolBus, Entities);
                ExtraSeats = new SharedExtraSeats(gameplay, host, diagnostics, Vehicles, SchoolBusPassengers, Entities);
                // Every adapter now exists. Saved purchases and runtime purchases must take
                // the same child-binding route before catalog hashing or snapshot capture.
                foreach (var purchased in VehicleSales!.PurchasedVehicles())
                    BindPurchasedVehicle(purchased.Entity, purchased.Type, purchased.Instance);
                FurnitureSeats = new SharedFurnitureSeats(gameplay, host, diagnostics, Items, ExtraSeats, Entities);
                MartinCar = new SharedMartinCar(host, diagnostics, Vehicles, Entities);
                carriers.Add(SchoolBus.CarrierFrames); carriers.Add(MartinCar.CarrierFrames);
                var seatAdmission = new SharedSeatAdmission(() => UnityEngine.Time.unscaledTime,
                    Vehicles.IsDriverOwner, Vehicles.IsPassengerOwner, SchoolBusPassengers.IsOwner,
                    ExtraSeats.IsOwner, FurnitureSeats.IsOwner, BoatMotion.IsOwner, MartinCar.IsOwner, Creepers.IsOwner);
                Vehicles.SeatAdmission = seatAdmission; SchoolBusPassengers.SeatAdmission = seatAdmission;
                ExtraSeats.SeatAdmission = seatAdmission; FurnitureSeats.SeatAdmission = seatAdmission;
                BoatMotion.SeatAdmission = seatAdmission; MartinCar.SeatAdmission = seatAdmission; Creepers.SeatAdmission = seatAdmission;
                // Passenger records share an adapter across cars, bus seats and extra seats.
                // Each handler validates its own records; only registered foreign owners are deferred.
                Vehicles.ForeignPassengerSeatRecord = id => SchoolBusPassengers.OwnsPassengerSeatRecord(id) ||
                    ExtraSeats.OwnsPassengerSeatRecord(id);
                SlotMachines = new SharedSlotMachines(host, diagnostics);
                MetalSales = new SharedMetalSales(host, Items, diagnostics);
                TrashRecycle = new SharedTrashRecycle(host, Items, diagnostics);
                Progress = new SharedProgress(host, diagnostics);
                Information = new SharedInformationDisplays(gameplay, host);
                NpcInteractions = new SharedNpcInteractions(host, ActorRoutes);
                Profile = new NativePlayerProfile(gameplay, this);
                SaveGate = new SaveRuntimeGate(this);
                if (host)
                {
                    Saves = new HostSaveCoordinator(this);
                    Vehicles.RestoreConfiguration(Saves.SelectedConfiguration);
                    Stores.DurableClaim = Saves.ClaimStorePurchase;
                    DealerMachines.DurableClaim = Saves.ClaimStorePurchase;
                }
                var catalogRecords = (Lights.InitialRecords().Concat(Items.Descriptors).Concat(ElectricPercolator.InitialRecords()).Concat(TireChanger.InitialRecords()).Concat(BuggyBuild.InitialRecords()).Concat(RacecarBuild.InitialRecords()).Concat(Vehicles.InitialRecords()).Concat(VehicleUi.InitialRecords()).Concat(BoatControls.InitialRecords()).Concat(BoatMotion.InitialRecords()).Concat(VehiclePlates.InitialRecords()).Concat(SchoolBusPassengers.InitialRecords()).Concat(ExtraSeats.InitialRecords()).Concat(FurnitureSeats.InitialRecords()).Concat(Traffic.InitialRecords()).Concat(Trailers.InitialRecords()).Concat(PaintPanels.InitialRecords()).Concat(WindowTints.InitialRecords()).Concat(Doors.InitialRecords()).Concat(VehicleParts.InitialRecords()).Concat(TirePressure.InitialRecords()).Concat(SceneAirEquipment.InitialRecords()).Concat(HoistPullers.InitialRecords()).Concat(Bolts.InitialRecords()).Concat(Fuel.InitialRecords()).Concat(Generator.InitialRecords()).Concat(FoodPrep.InitialRecords()).Concat(ScrapyardGate.InitialRecords()).Concat(ScrapyardRadio.InitialRecords()).Concat(NewspaperCardbox.InitialRecords()).Concat(DogTreats.InitialRecords()).Concat(Kegs.InitialRecords()).Concat(BarDrink.InitialRecords()).Concat(VehicleSales!.InitialRecords()).Concat(DealerBunkerPurchase.InitialRecords()).Concat(PizzaSnack.InitialRecords()).Concat(ChalumeauPack.InitialRecords()).Concat(PaintMixing.InitialRecords()).Concat(WoodenKegs.InitialRecords()).Concat(TowingService.InitialRecords()).Concat(PortableConsoleCharger.InitialRecords()).Concat(BunkerBathtub.InitialRecords()).Concat(Sinks.InitialRecords()).Concat(SceneFurniture.InitialRecords()).Concat(RoadSigns.InitialRecords()).Concat(PoliceSpots.InitialRecords()).Concat(FuelNozzles.InitialRecords()).Concat(RemoteClicks.InitialRecords()).Concat(Nitrous.InitialRecords()).Concat(NitrousRefillStation.InitialRecords()).Concat(TravelingVan.InitialRecords()).Concat(Gilles.InitialRecords()).Concat(SyrupCargo.InitialRecords()).Concat(BunkerWatering.InitialRecords()).Concat(BunkerCompost.InitialRecords()).Concat(Wheelbarrows.InitialRecords()).Concat(Creepers.InitialRecords()).Concat(Dollies.InitialRecords()).Concat(WoodSplitters.InitialRecords()).Concat(Garages.InitialRecords()).Concat(CottagePurchase.InitialRecords()).Concat(CottagePurchase.ContentsInitialRecords()).Concat(BoatSale.InitialRecords()).Concat(StorageGarageSale.InitialRecords()).Concat(SugarShack.InitialRecords()).Concat(Leo.InitialRecords()).Concat(LeoSupplies.InitialRecords()).Concat(ActorRoutes.InitialRecords()).Concat(SchoolBus.InitialRecords()).Concat(MartinCar.InitialRecords()).Concat(SlotMachines.InitialRecords()).Concat(Garden.RootRecords()).Concat(Forest.RootRecords()).Concat(Tubing.RootRecords()).Concat(Clock.InitialRecords()).Concat(Sleep.InitialRecords()).Concat(Weather.InitialRecords()).Concat(Economy.InitialRecords()).Concat(Statistics.InitialRecords()).Concat(Stores.InitialRecords()).Concat(DealerMachines.InitialRecords()).Concat(MetalSales.InitialRecords()).Concat(TrashRecycle.InitialRecords()).Concat(Progress.InitialRecords()).Concat(Information.InitialRecords()).Concat(NpcInteractions.InitialRecords()).Concat(SaveRoundChannel.InitialRecords()).Concat(SharedReconnectPositions.InitialRecords()).Concat(SharedPermadeath.InitialRecords()).Concat(SharedLeoHearts.InitialRecords()).Concat(SharedPresentationEvents.InitialRecords()).Concat(SharedFishingPresentation.InitialRecords()).Concat(PassengerControls.InitialRecords()).Concat(SharedMeetRace.InitialRecords()).Concat(SharedSpeedwayEvents.InitialRecords()).Concat(SharedEasterEggs.InitialRecords()).Append(ProfileRestoreChannel.InitialRecord()).Append(LiveProfileChannel.InitialRecord())).ToArray();
                // Dynamic records (purchased vehicles a save restored, for example) are runtime state the
                // host streams in; a guest loading a fresh world cannot have them, so they stay out of the catalog.
                var requiredDynamicCatalog = new HashSet<ulong>(RequiredDynamicCatalogEntityIds);
                catalogRecords = catalogRecords.Where(r => r.Id <= WorldRecord.DynamicIdStart ||
                    !DynamicAdapters.Contains(r.Adapter) || requiredDynamicCatalog.Contains(r.Id)).ToArray();
                CatalogHash = WorldRecord.Digest(catalogRecords, true);
                // Public builds need the same identity evidence as development builds.
                // Diagnostics never change compatibility or prevent a world from joining.
                string catalogDirectory = string.IsNullOrEmpty(diagnostics)
                    ? Path.Combine(BepInEx.Paths.BepInExRootPath, "MonMP", "diagnostics") : diagnostics;
                if (!WorldCatalogDiagnostics.TryWrite(catalogDirectory, host, SessionOptions.CurrentModVersion,
                    CatalogHash, catalogRecords, record => Entities.Resolve(record.Id) is { } entity
                        ? (RuntimeProbe.CanonicalPath(entity.transform), entity.SpawnKey)
                        : (TireChanger.PathOf(record.Id) ?? "-", "-"), out string diagnosticError))
                    UnityEngine.Debug.LogWarning("MonMP: world catalog diagnostic could not be written (" + diagnosticError + "); world loading continues.");

            }
            catch
            {
                Dispose();
                throw;
            }
        }
        private void BindPurchasedVehicle(ulong entity, Vehicle_Type type, UnityEngine.GameObject instance)
        {
            bool vehicleBound = Vehicles.RegisterPurchasedVehicle(entity, type, instance);
            bool trailerBound = Trailers.RegisterPurchasedVehicle(entity, type, instance);
            if (!vehicleBound && !trailerBound)
                throw new InvalidOperationException("Purchased vehicle is unsupported by the shared vehicle and trailer adapters: " + type);
            if ((type == Vehicle_Type.OlTruck || type == Vehicle_Type.SmollATV) && (!vehicleBound || !trailerBound))
                throw new InvalidOperationException("Purchased tow vehicle did not bind both motion and trailer authority: " + type);
            // During construction motion binds before these catalogs exist; their
            // constructors and the explicit saved-purchase replay cover that branch.
            VehicleParts?.RegisterPurchasedVehicle(entity, type, instance);
            Bolts?.RegisterPurchasedVehicle(entity, type, instance);
            VehiclePlates?.RegisterPurchasedVehicle(entity, type, instance);
            PaintPanels?.RegisterPurchasedVehicle(entity, type, instance);
            WindowTints?.RegisterPurchasedVehicle(entity, type, instance);
            Fuel?.RegisterPurchasedVehicle(entity, type, instance);
            HoistPullers?.RegisterPurchasedVehicle(entity, type, instance);
            ExtraSeats?.RegisterPurchasedVehicle(entity, type, instance);
            Vehicles.Doors.RefreshPurchasedBindings();
            Vehicles.Trunk.RefreshPurchasedBindings();
            VehicleUi?.RefreshPurchasedBindings();
            var nativeVehicle = instance.GetComponentsInChildren<NWH.VehiclePhysics2.VehicleController>(true).SingleOrDefault();
            if (nativeVehicle != null)
                foreach (var record in Vehicles.Windows.RegisterVehicle(nativeVehicle, Entities, entity))
                    if (attachedSession?.IsHost == true && channel != null) channel.Register(record);
        }

        // Preserve existing advisory key sections while joining/resuming uses friends-first identities.
        internal const string ProfileKeySection = "profile-key";
        internal IEnumerable<KeyValuePair<Guid, string>> SavedProfileIdentities() =>
            Saves?.SavedProfileIdentities() ?? Array.Empty<KeyValuePair<Guid, string>>();
        internal IReadOnlyList<SidecarProfile> StampProfileKeys(IEnumerable<SidecarProfile> profiles) => profiles.Select(profile =>
        {
            var hash = attachedSession?.ProfileKeyHashOf(profile.Id);
            if (Permadeath != null) profile = Permadeath.Stamp(profile);
            if (hash == null) return profile;
            var state = profile.State.ToDictionary(pair => pair.Key, pair => pair.Value);
            state[ProfileKeySection] = new SidecarSection(1, hash);
            return new SidecarProfile(profile.Id, profile.DisplayName, profile.ColorRgb, profile.LastSeenUtc, state);
        }).ToArray();
        private NetworkSession? attachedSession;

        public void Attach(NetworkSession session)
        {
            attachedSession = session;
            channel = new WorldChannel(session, Lights.InitialRecords().Concat(Items.Descriptors).Concat(ElectricPercolator.InitialRecords()).Concat(TireChanger.InitialRecords()).Concat(Items.InitialRecords()).Concat(FishBuckets.InitialRecords()).Concat(Forest.InitialRecords()).Concat(Garden.InitialRecords()).Concat(Tubing.InitialRecords()).Concat(BuggyBuild.InitialRecords()).Concat(RacecarBuild.InitialRecords()).Concat(Vehicles.InitialRecords()).Concat(VehicleUi.InitialRecords()).Concat(BoatControls.InitialRecords()).Concat(BoatMotion.InitialRecords()).Concat(VehiclePlates.InitialRecords()).Concat(SchoolBusPassengers.InitialRecords()).Concat(ExtraSeats.InitialRecords()).Concat(FurnitureSeats.InitialRecords()).Concat(Traffic.InitialRecords()).Concat(Trailers.InitialRecords()).Concat(PaintPanels.InitialRecords()).Concat(WindowTints.InitialRecords()).Concat(Doors.InitialRecords()).Concat(VehicleParts.InitialRecords()).Concat(TirePressure.InitialRecords()).Concat(SceneAirEquipment.InitialRecords()).Concat(HoistPullers.InitialRecords()).Concat(Bolts.InitialRecords()).Concat(Fuel.InitialRecords()).Concat(Generator.InitialRecords()).Concat(FoodPrep.InitialRecords()).Concat(ScrapyardGate.InitialRecords()).Concat(ScrapyardRadio.InitialRecords()).Concat(NewspaperCardbox.InitialRecords()).Concat(DogTreats.InitialRecords()).Concat(Kegs.InitialRecords()).Concat(BarDrink.InitialRecords()).Concat(VehicleSales!.InitialRecords()).Concat(DealerBunkerPurchase.InitialRecords()).Concat(PizzaSnack.InitialRecords()).Concat(ChalumeauPack.InitialRecords()).Concat(PaintMixing.InitialRecords()).Concat(WoodenKegs.InitialRecords()).Concat(TowingService.InitialRecords()).Concat(PortableConsoleCharger.InitialRecords()).Concat(BunkerBathtub.InitialRecords()).Concat(Sinks.InitialRecords()).Concat(SceneFurniture.InitialRecords()).Concat(RoadSigns.InitialRecords()).Concat(PoliceSpots.InitialRecords()).Concat(FuelNozzles.InitialRecords()).Concat(RemoteClicks.InitialRecords()).Concat(Nitrous.InitialRecords()).Concat(NitrousRefillStation.InitialRecords()).Concat(TravelingVan.InitialRecords()).Concat(Gilles.InitialRecords()).Concat(SyrupCargo.InitialRecords()).Concat(BunkerWatering.InitialRecords()).Concat(BunkerCompost.InitialRecords()).Concat(Wheelbarrows.InitialRecords()).Concat(Creepers.InitialRecords()).Concat(Dollies.InitialRecords()).Concat(WoodSplitters.InitialRecords()).Concat(Garages.InitialRecords()).Concat(CottagePurchase.InitialRecords()).Concat(CottagePurchase.ContentsInitialRecords()).Concat(BoatSale.InitialRecords()).Concat(StorageGarageSale.InitialRecords()).Concat(SugarShack.InitialRecords()).Concat(Leo.InitialRecords()).Concat(LeoSupplies.InitialRecords()).Concat(ActorRoutes.InitialRecords()).Concat(SchoolBus.InitialRecords()).Concat(MartinCar.InitialRecords()).Concat(SlotMachines.InitialRecords()).Concat(Garden.RootRecords()).Concat(Forest.RootRecords()).Concat(Tubing.RootRecords()).Concat(Clock.InitialRecords()).Concat(Sleep.InitialRecords()).Concat(Weather.InitialRecords()).Concat(Economy.InitialRecords()).Concat(Statistics.InitialRecords()).Concat(Stores.InitialRecords()).Concat(DealerMachines.InitialRecords()).Concat(MetalSales.InitialRecords()).Concat(TrashRecycle.InitialRecords()).Concat(Progress.InitialRecords()).Concat(Information.InitialRecords()).Concat(NpcInteractions.InitialRecords()).Concat(SaveRoundChannel.InitialRecords()).Concat(SharedReconnectPositions.InitialRecords()).Concat(SharedPermadeath.InitialRecords()).Concat(SharedLeoHearts.InitialRecords()).Concat(SharedPresentationEvents.InitialRecords()).Concat(SharedFishingPresentation.InitialRecords()).Concat(PassengerControls.InitialRecords()).Concat(SharedMeetRace.InitialRecords()).Concat(SharedSpeedwayEvents.InitialRecords()).Concat(SharedEasterEggs.InitialRecords()).Append(ProfileRestoreChannel.InitialRecord()).Append(LiveProfileChannel.InitialRecord()), DynamicAdapters, new[] { PortableConsoleChargerState.Adapter, VehicleState.Adapter, VehicleUiState.Adapter, BoatMotionState.Adapter, BoatControlState.Adapter, VehiclePassengerSeatState.Adapter, VehicleSeatAdjustmentState.Adapter, VehicleDoorState.Adapter, VehicleWindowState.Adapter, VehicleHoodState.Adapter, VehicleTrunkState.Adapter, VehiclePartState.Adapter, BoltHoleState.Adapter, FuelState.Adapter, GeneratorState.Adapter, FoodPrepState.Adapter, BunkerBathtubState.Adapter, GarageState.Adapter, SugarShackState.Adapter, BuggyBuildState.Adapter, CottagePurchaseState.Adapter, SharedMetalSales.Adapter, ScrapyardGateState.Adapter, ScrapyardRadioState.Adapter, TirePressureState.Adapter, SceneAirEquipmentState.Adapter, SlotMachineState.Adapter, TrailerWorldState.Adapter, SharedPaintPanels.Adapter, WindowTintState.Adapter, TrashRecycleProtocol.Adapter, LeoTreatCommand.Adapter, LeoSuppliesState.Adapter, NpcInteractionState.Adapter, ActorRouteState.Adapter, SchoolBusState.Adapter, MartinCarState.Adapter, HoistPullerState.Adapter, PizzaSnackCommand.Adapter, TireChangerIdentity.Adapter, LeoWorldState.Adapter, SharedMeetRace.Adapter, SharedSpeedwayEvents.Adapter, SharedEasterEggs.Adapter, SharedWoodSplitters.Adapter, SharedWheelbarrows.Adapter, SharedCreepers.Adapter, SharedDollies.Adapter, SharedVehicleTrunk.LateAdapter, SharedVehicleDoors.LateAdapter, SleepState.Adapter, SharedPresentationEvents.Adapter, SharedFishingPresentation.Adapter, PassengerVehicleControlState.Adapter, SharedUnderglow.Adapter, FurnitureSeatState.Adapter, SharedSceneFurniture.Adapter, SharedFuelNozzles.Adapter, SharedStatisticsState.Adapter, SharedPoliceSpots.Adapter },
                requiredDynamicEntities: RequiredDynamicCatalogEntityIds);
            // Replayed actions sound where the acting player is; a full snapshot applies silently.
            channel.CommandScope = SpatialSounds.AtPeer;
            channel.DeltaScope = SpatialSounds.Remote;
            channel.SnapshotApplyScope = SpatialSounds.Mute;
            channel.HandlerFailed += (what, ex) => { if (channel.HandlerFailures <= 20 || channel.HandlerFailures % 100 == 0) UnityEngine.Debug.LogError("MonMP received " + what + " handler failed (" + channel.HandlerFailures + " total); the session continues: " + ex); };
            channel.ReplyReceived += SeatRefused;
            channel.OrderedBacklogOverflowed += (_, detail) => UnityEngine.Debug.LogWarning("MonMP ordered world backlog overflow: " + detail);
            Lights.Attach(session, channel);
                Items.Attach(session, channel); TriggerFeedback.Attach(session, channel); TireChanger.Attach(session, channel);
            ElectricPercolator.Attach(session, channel); FenceActions.Attach(session, channel);
            VehicleSales!.Attach(session, channel);
            PizzaSnack.Attach(session, channel);
            ChalumeauPack.Attach(session, channel);
            DealerBunkerPurchase.Attach(session, channel);
            PaintMixing.Attach(session, channel);
            WoodenKegs.Attach(session, channel);
            TowingService.Attach(session, channel);
            FishBuckets.Attach(session, channel);
            BuggyBuild.Attach(session, channel);
            RacecarBuild.Attach(session, channel);
            Vehicles.Attach(session, channel);
            BoatControls.Attach(session, channel);
            BoatMotion.Attach(session, channel);
            VehiclePlates.Attach(session, channel);
            Traffic.Attach(session, channel);
            Trailers.Attach(session, channel);
            PaintPanels.Attach(session, channel);
            WindowTints.Attach(session, channel);
            Doors.Attach(session, channel);
            VehicleParts.Attach(session, channel);
            TirePressure.Attach(session, channel);
            SceneAirEquipment.Attach(session, channel);
            Bolts.Attach(session, channel);
            Fuel.Attach(session, channel);
            Generator.Attach(session, channel);
            FoodPrep.Attach(session, channel);
            Garages.Attach(session, channel);
            CottagePurchase.Attach(session, channel);
            BoatSale.Attach(session, channel);
            StorageGarageSale.Attach(session, channel);
            SugarShack.Attach(session, channel);
            Forest.Attach(session, channel);
            Garden.Attach(session, channel);
            Tubing.Attach(session, channel);
            Clock.Attach(session, channel);
            Sleep.Attach(session, channel);
            HydroMail.Attach(session, channel);
            Weather.Attach(session, channel);
            Economy.Attach(session, channel);
            Statistics.Attach(session, channel);
            Stores.Attach(session, channel);
            BarDrink.Attach(session, channel);
            DealerMachines.Attach(session, channel);
            Leo.Attach(session, channel);
            LeoSupplies.Attach(session, channel);
            ActorRoutes.Attach(session, channel);
            SchoolBus.Attach(session, channel);
            SchoolBusPassengers.Attach(session, channel);
            ExtraSeats.Attach(session, channel);
            FurnitureSeats.Attach(session, channel);
            MartinCar.Attach(session, channel);
            SlotMachines.Attach(session, channel);
            MetalSales.Attach(session, channel);
            TrashRecycle.Attach(session, channel);
            Progress.Attach(session, channel);
            Information.Attach(session, channel);
            NpcInteractions.Attach(session, channel);
            ScrapyardGate.Attach(session, channel);
            ScrapyardRadio.Attach(session, channel);
            FurnitureRadios.Attach(session, channel);
            StorageGarages.Attach(session, channel);
            NewspaperCardbox.Attach(session, channel);
            DogTreats.Attach(session, channel);
            Kegs.Attach(session, channel);
            PortableConsoleCharger.Attach(session, channel);
            HoistPullers.Attach(session, channel);
            BunkerBathtub.Attach(session, channel);
            Sinks.Attach(session, channel);
            SceneFurniture.Attach(session, channel);
            RoadSigns.Attach(session, channel);
            PoliceSpots.Attach(session, channel);
            FuelNozzles.Attach(session, channel);
            RemoteClicks.Attach(session, channel);
            Nitrous.Attach(session, channel);
            NitrousRefillStation.Attach(session, channel);
            VehicleTune.Attach(session, channel);
            VehicleUi.Attach(session, channel);
            FishingPresentation.Attach(session, channel);
            PassengerControls.Attach(session, channel);
            Underglow.Attach(session, channel);
            TravelingVan.Attach(session, channel);
            Gilles.Attach(session, channel);
            WoodSplitters.Attach(session, channel);
            SyrupCargo.Attach(session, channel);
            BunkerWatering.Attach(session, channel);
            BunkerCompost.Attach(session, channel);
            Wheelbarrows.Attach(session, channel);
            Creepers.Attach(session, channel);
            Dollies.Attach(session, channel);
            SaveRounds = new SaveRoundChannel(session, channel, () => TimeNow(), () => SaveOwners, RevokeForSave,
                blocked => { if (blocked) SaveGate.Begin(true); else SaveGate.Dispose(); },
                () => { SettleSaveProduction(); Profile.SettleForSave(); FlushForSave(); },
                () => PersonalProfileReady && Profile.CanCapture && (Saves != null || TransactionsSettled && Items.LocalFlushComplete), Profile.Capture,
                (peer, profile) => Saves?.Checkpoint(profile) ?? LiveProfiles?.Find(profile),
                () => TimeNow() >= saveSettlementReadyAt && TransactionsSettled && LocalSaveFlushComplete, PrepareRecoverySave, settlementGraceSeconds: 1);
            LiveProfiles = new LiveProfileChannel(session, channel, () => TimeNow());
            if (Saves != null) LiveProfiles.SafeProfileAccepted += Saves.RecordSafeProfile;
            ProfileRestore = new GuestProfileRestore(this, session);
            ReconnectPositions = new SharedReconnectPositions(Saves != null,
                profile => Saves?.SavedSection(profile, SharedReconnectPositions.SectionKey),
                () => PersonalProfileReady && (MonMPPlugin.Instance?.GuestWorld?.Activated ?? true),
                ReconnectExit, peer => ExtraSeats.Owners.Contains(peer) || FurnitureSeats.Owners.Contains(peer) || SchoolBusPassengers.Owners.Contains(peer));
            Vehicles.DriverExited += ReconnectPositions.RecordVehicleExit;
            BoatMotion.DriverExited += ReconnectPositions.RecordVehicleExit;
            ReconnectPositions.Attach(session, channel);
            Permadeath = new SharedPermadeath(this, session, channel);
            Sleep.IsPermadead = Permadeath.IsPermadead;
            LeoHearts = new SharedLeoHearts();
            LeoHearts.Attach(session, channel);
            Events = new SharedPresentationEvents();
            Events.Attach(session, channel);
            RegisterPresentation(Events);
            MeetRace = new SharedMeetRace();
            MeetRace.Attach(session, channel);
            SpeedwayEvents = new SharedSpeedwayEvents();
            SpeedwayEvents.Attach(session, channel);
            EasterEggs = new SharedEasterEggs(Items);
            EasterEggs.Attach(session, channel);
        }
        private static double TimeNow() => UnityEngine.Time.realtimeSinceStartupAsDouble;
        private UnityEngine.Vector3? ReconnectExit(uint peer, PlayerPose pose)
        {
            if (pose.State == (byte)PlayerState.Walking || pose.Rest != PlayerRest.None) return null;
            if (Vehicles.TryGetReconnectExit(peer, out var point) || BoatMotion.TryGetReconnectExit(peer, out point)) return point;
            if (NativeVehicleDeparture.TryExit(SchoolBusPassengers.VehicleForPeer(peer), false, out point)) return point;
            var anchor = ExtraSeats.SeatFor(peer, out var kind);
            if (kind == SharedExtraSeats.SeatKind.Cab || kind == SharedExtraSeats.SeatKind.Bench)
                if (NativeVehicleDeparture.TryExit(anchor?.GetComponentInParent<NWH.Vehicle>(), false, out point)) return point;
            return null;
        }
        // One-shot sights and sounds each adapter relays (guest range: how far from the reporting
        // guest's avatar the host accepts its report).
        private void RegisterPresentation(SharedPresentationEvents events)
        {
            events.Register(PresentationKind.VehicleCrash, VehicleCrashPresentation.Play, 60f);
            events.Register(PresentationKind.GardenFertilize, PresentationReplays.Fertilized, 8f);
            events.Register(PresentationKind.GardenPlant, PresentationReplays.Planted, 8f);
            events.Register(PresentationKind.GardenShovel, PresentationReplays.Shoveled, 8f);
            events.Register(PresentationKind.MachinePullStart, PresentationReplays.PullStart);
            events.Register(PresentationKind.MachineControl, MachineControlPresentation.PlaySwitch);
            events.Register(PresentationKind.HoodAction, Vehicles.Hood.PlayAnnounced);
            events.Register(PresentationKind.PartInstalled, PresentationReplays.PartInstalled);
            events.Register(PresentationKind.MachineDone, e => RemoteClicks.PlayDoneEvent(e));
            events.Register(PresentationKind.FryerPress, e => FoodPrep.PlayPress(e));
            events.Register(PresentationKind.NitrousRefillStation, NitrousRefillStation.PlayCue);
            events.Register(PresentationKind.Fishing, FishingPresentation.PlayCue, 60f);
            events.Register(PresentationKind.VehicleSwitch, VehicleSwitchPresentation.Play, 60f);
            events.Register(PresentationKind.VehicleWindowClick, PresentationReplays.VehicleWindowClick);
            events.Register(PresentationKind.SugarShackPress, SugarShack.PlaySyrupPress);
            events.Register(PresentationKind.RogerUnlock, RogerUnlockPresentation.Play);
            events.Register(PresentationKind.VehicleDoor, VehicleDoorPresentation.Play);
            events.Register(PresentationKind.AnimalCrossing, PresentationReplays.AnimalCrossing, 500f);
            events.Register(PresentationKind.BoltWork, PresentationReplays.BoltWork, 8f);
            events.Register(PresentationKind.NpcVoice, NpcInteractions.PlayVoice);
            events.Register(PresentationKind.CashCue, Economy.PlayCashCue);
            events.Register(PresentationKind.WorldNotice, SharedWorldNotices.Play);
            events.Register(PresentationKind.PizzaSnackOutcome, PizzaSnack.PlayOutcome);
            events.Register(PresentationKind.FishBucket, FishBucketPresentation.Play);
            events.Register(PresentationKind.Consumption, Items.PlayConsumption);
            events.Register(PresentationKind.SpeedwayCue, e => SpeedwayEvents?.PlayCue(e), 60f);
            events.Register(PresentationKind.SpeedwayDent, e => SpeedwayEvents?.PlayDent(e), 300f);
            events.Register(PresentationKind.SpeedwayRepair, e => SpeedwayEvents?.PlayRepair(e), 300f);
            foreach (var kind in new[] { PresentationKind.LeoBite, PresentationKind.LeoEat, PresentationKind.LeoWhistle, PresentationKind.LeoNeeds })
                events.Register(kind, Leo.Present);
        }
        private (string Name, Action Update)[]? updates;
        public void Update()
        {
            // Kept as a no-op scope; admission now runs at each typed query, without
            // pre-counting deadlines or constructing a shared scene query.
            using var discoveryBatch = WorldScans.BeginBatch(0);
            // Each step is timed and, outside paired tests, isolated: one adapter's failure is
            // logged once instead of ending the session for everyone.
            updates ??= new (string, Action)[]
            {
                ("SaveGate", () => SaveGate.Enforce()),
                ("HydroMail", () => HydroMail.Update()),
                ("Lights", () => Lights.Update()),
                ("Items", () => Items.Update()),
                ("ElectricPercolator", () => ElectricPercolator.Update()),
                ("FenceActions", () => FenceActions.Update()),
                ("TireChanger", () => TireChanger.Update()),
                ("FishBuckets", () => FishBuckets.Update()),
                ("BuggyBuild", () => BuggyBuild.Update()),
                ("RacecarBuild", () => RacecarBuild.Update()),
                ("Vehicles", () => Vehicles.Update()),
                ("VehiclePlates", () => VehiclePlates.Update()),
                ("VehicleParts", () => VehicleParts.Update()),
                ("BoatControls", () => BoatControls.Update()),
                ("BoatMotion", () => BoatMotion.Update()),
                ("Traffic", () => Traffic.Update()),
                ("Trailers", () => Trailers.Update()),
                ("PaintPanels", () => PaintPanels.Update()),
                ("WindowTints", () => WindowTints.Update()),
                ("Doors", () => Doors.Update()),
                ("Bolts", () => Bolts.Update()),
                ("TirePressure", () => TirePressure.Update()),
                ("SceneAirEquipment", () => SceneAirEquipment.Update()),
                ("Fuel", () => Fuel.Update()),
                ("Generator", () => Generator.Update()),
                ("FoodPrep", () => FoodPrep.Update()),
                ("ScrapyardGate", () => ScrapyardGate.Update()),
                ("ScrapyardRadio", () => ScrapyardRadio.Update()),
                ("FurnitureRadios", () => FurnitureRadios.Update()),
                ("StorageGarages", () => StorageGarages.Update()),
                ("NewspaperCardbox", () => NewspaperCardbox.Update()),
                ("DogTreats", () => DogTreats.Update()),
                ("Kegs", () => Kegs.Update()),
                ("BarDrink", () => BarDrink.Update()),
                ("VehicleSales", () => VehicleSales?.Update()),
                ("PizzaSnack", () => PizzaSnack.Update()),
                ("ChalumeauPack", () => ChalumeauPack.Update()),
                ("DealerBunkerPurchase", () => DealerBunkerPurchase.Update()),
                ("PaintMixing", () => PaintMixing.Update()),
                ("WoodenKegs", () => WoodenKegs.Update()),
                ("TowingService", () => TowingService.Update()),
                ("PortableConsoleCharger", () => PortableConsoleCharger.Update()),
                ("HoistPullers", () => HoistPullers.Update()),
                ("BunkerBathtub", () => BunkerBathtub.Update()),
                ("Sinks", () => Sinks.Update()),
                ("Nitrous", () => Nitrous.Update()),
                ("NitrousRefillStation", () => NitrousRefillStation.Update()),
                ("VehicleTune", () => VehicleTune.Update()),
                ("VehicleUi", () => VehicleUi.Update()),
                ("Underglow", () => Underglow.Update()),
                ("TravelingVan", () => TravelingVan.Update()),
                ("Gilles", () => Gilles.Update()),
                ("WoodSplitters", () => WoodSplitters.Update()),
                ("SyrupCargo", () => SyrupCargo.Update()),
                ("BunkerWatering", () => BunkerWatering.Update()),
                ("BunkerCompost", () => BunkerCompost.Update()),
                ("Wheelbarrows", () => Wheelbarrows.Update()),
                ("Creepers", () => Creepers.Update()),
                ("Dollies", () => Dollies.Update()),
                ("ExtraSeats", () => ExtraSeats.Update()),
                ("FurnitureSeats", () => FurnitureSeats.Update()),
                ("Garages", () => Garages.Update()),
                ("CottagePurchase", () => CottagePurchase.Update()),
                ("BoatSale", () => BoatSale.Update()),
                ("SugarShack", () => SugarShack.Update()),
                ("Forest", () => Forest.Update()),
                ("Garden", () => Garden.Update()),
                ("Tubing", () => Tubing.Update()),
                ("Clock", () => Clock.Update()),
                ("Sleep", () => Sleep.Update()),
                ("Weather", () => Weather.Update()),
                ("Economy", () => Economy.Update()),
                ("Stores", () => Stores.Update()),
                ("DealerMachines", () => DealerMachines.Update()),
                ("Leo", () => Leo.Update()),
                ("LeoSupplies", () => LeoSupplies.Update()),
                ("ActorRoutes", () => ActorRoutes.Update()),
                ("SchoolBus", () => SchoolBus.Update()),
                ("MartinCar", () => MartinCar.Update()),
                ("SlotMachines", () => SlotMachines.Update()),
                ("MetalSales", () => MetalSales.Update()),
                ("TrashRecycle", () => TrashRecycle.Update()),
                ("Progress", () => Progress.Update()),
                ("Information", () => Information.Update()),
                ("StorageGarageSale", () => StorageGarageSale.Update()),
                ("NpcInteractions", () => NpcInteractions.Update()),
                ("SceneFurniture", () => SceneFurniture.Update()),
                ("PoliceSpots", () => PoliceSpots.Update()),
                ("FuelNozzles", () => FuelNozzles.Update()),
                ("ProfileRestore", () => ProfileRestore?.Update()),
                ("ReconnectPositions", () => ReconnectPositions?.Update()),
                ("LeoHearts", () => LeoHearts?.Update()),
                ("MeetRace", () => MeetRace?.Update()),
                ("SpeedwayEvents", () => SpeedwayEvents?.Update()),
                ("Statistics", () => Statistics.Update()),
                ("UpdateLiveProfile", () => UpdateLiveProfile()),
                ("Permadeath", () => Permadeath?.Update()),
                ("SaveRounds", () => SaveRounds?.Update()),
                ("Saves", () => Saves?.Update()),
                ("Events", () => Events?.Update()),
                ("CrashSounds", RemoteVehicleSounds.TickCrashPending),
                ("Fishing", RemoteFishing.Update),
                ("FishingState", () => FishingPresentation.Update()),
                ("PassengerControls", () => PassengerControls.Update()),
                ("channel", () => channel?.Pump()),
            };
            foreach (var step in updates)
            {
                if (disposed) break;
                UpdateProfiler.Run(step.Name, step.Update);
            }
        }
        private void UpdateLiveProfile()
        {
            if (LiveProfiles?.CanSubmit == true && PersonalProfileReady && !SaveGate.Active && Profile.CanCapture)
                LiveProfiles.Submit(Profile.Capture());
        }
        private bool disposed;
        internal bool IsDisposed => disposed;
        private static void Cleanup(string name, Action action)
        {
            try { action(); }
            catch (Exception ex) { UnityEngine.Debug.LogError("MonMP world cleanup " + name + " failed: " + ex); }
        }
        // Every seat adapter shares one pending claim, so one handler ends it for all of them.
        private void SeatRefused(ulong entity, byte[] payload)
        {
            if (!SeatRefusalReply.Is(payload)) return;
            var refusal = SeatRefusalReply.Decode(payload);
            if (FurnitureSeats.SeatAdmission?.Refused(entity, refusal.Generation) == true)
                MonMPPlugin.Instance?.ReportItemUseUnavailable(SeatRefusals.Message(refusal.Reason));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                Cleanup("FishingPresentation", () => FishingPresentation?.Dispose());
                Cleanup("PassengerControls", () => PassengerControls?.Dispose());
                Cleanup("SpatialSounds", SpatialSounds.Reset);
                Cleanup("CrashSounds", RemoteVehicleSounds.Reset);
                Cleanup("VehicleSales.Unsubscribe", () => { if (VehicleSales != null) VehicleSales.PurchasedVehicleAvailable -= BindPurchasedVehicle; });
                Cleanup("Permadeath", () => Permadeath?.Dispose());
                Cleanup("SaveJournal", () => Saves?.FlushJournal(true));
                Cleanup("SaveRounds", () => SaveRounds?.Dispose());
                Cleanup("SaveGate", () => SaveGate?.Dispose());
                Cleanup("LiveProfiles", () => LiveProfiles?.Dispose());
                Cleanup("ElectricPercolator", () => ElectricPercolator?.Dispose());
                Cleanup("FenceActions", () => FenceActions?.Dispose());
                Cleanup("TireChanger", () => TireChanger?.Dispose());
                Cleanup("PizzaSnack", () => PizzaSnack?.Dispose());
                Cleanup("ChalumeauPack", () => ChalumeauPack?.Dispose());
                Cleanup("DealerBunkerPurchase", () => DealerBunkerPurchase?.Dispose());
                Cleanup("PaintMixing", () => PaintMixing?.Dispose());
                Cleanup("WoodenKegs", () => WoodenKegs?.Dispose());
                Cleanup("TowingService", () => TowingService?.Dispose());
                Cleanup("BarDrink", () => BarDrink?.Dispose());
                Cleanup("HydroMail", () => HydroMail?.Dispose());
                Cleanup("Traffic", () => Traffic?.Dispose());
                Cleanup("ProfileRestore", () => ProfileRestore?.Dispose());
                Cleanup("ReconnectPositions", () => ReconnectPositions?.Dispose());
                Cleanup("LeoHearts", () => LeoHearts?.Dispose());
                Cleanup("Events", () => Events?.Dispose());
                Cleanup("RemoteFishing", RemoteFishing.Clear);
                Cleanup("MeetRace", () => MeetRace?.Dispose());
                Cleanup("SpeedwayEvents", () => SpeedwayEvents?.Dispose());
                Cleanup("EasterEggs", () => EasterEggs?.Dispose());
                Cleanup("FishBuckets", () => FishBuckets?.Dispose());
                Cleanup("NpcInteractions", () => NpcInteractions?.Dispose());
                Cleanup("MartinCar", () => MartinCar?.Dispose());
                Cleanup("FurnitureSeats", () => FurnitureSeats?.Dispose());
                Cleanup("ExtraSeats", () => ExtraSeats?.Dispose());
                Cleanup("SchoolBusPassengers", () => SchoolBusPassengers?.Dispose());
                Cleanup("SchoolBus", () => SchoolBus?.Dispose());
                Cleanup("ActorRoutes", () => ActorRoutes?.Dispose());
                Cleanup("Progress", () => Progress?.Dispose());
                Cleanup("Information", () => Information?.Dispose());
                Cleanup("ScrapyardGate", () => ScrapyardGate?.Dispose());
                Cleanup("ScrapyardRadio", () => ScrapyardRadio?.Dispose());
                Cleanup("FurnitureRadios", () => FurnitureRadios?.Dispose());
                Cleanup("StorageGarages", () => StorageGarages?.Dispose());
                Cleanup("NewspaperCardbox", () => NewspaperCardbox?.Dispose());
                Cleanup("DogTreats", () => DogTreats?.Dispose());
                Cleanup("Kegs", () => Kegs?.Dispose());
                Cleanup("PortableConsoleCharger", () => PortableConsoleCharger?.Dispose());
                Cleanup("HoistPullers", () => HoistPullers?.Dispose());
                Cleanup("BunkerBathtub", () => BunkerBathtub?.Dispose());
                Cleanup("Sinks", () => Sinks?.Dispose());
                Cleanup("RemoteClicks", () => RemoteClicks?.Dispose());
                Cleanup("Nitrous", () => Nitrous?.Dispose());
                Cleanup("NitrousRefillStation", () => NitrousRefillStation?.Dispose());
                Cleanup("Underglow", () => Underglow?.Dispose());
                Cleanup("TravelingVan", () => TravelingVan?.Dispose());
                Cleanup("Gilles", () => Gilles?.Dispose());
                Cleanup("WoodSplitters", () => WoodSplitters?.Dispose());
                Cleanup("SyrupCargo", () => SyrupCargo?.Dispose());
                Cleanup("BunkerWatering", () => BunkerWatering?.Dispose());
                Cleanup("BunkerCompost", () => BunkerCompost?.Dispose());
                Cleanup("Wheelbarrows", () => Wheelbarrows?.Dispose());
                Cleanup("Creepers", () => Creepers?.Dispose());
                Cleanup("SceneFurniture", () => SceneFurniture?.Dispose());
                Cleanup("RoadSigns", () => RoadSigns?.Dispose());
                Cleanup("PoliceSpots", () => PoliceSpots?.Dispose());
                Cleanup("FuelNozzles", () => FuelNozzles?.Dispose());
                Cleanup("Dollies", () => Dollies?.Dispose());
                Cleanup("TrashRecycle", () => TrashRecycle?.Dispose());
                Cleanup("MetalSales", () => MetalSales?.Dispose());
                Cleanup("SlotMachines", () => SlotMachines?.Dispose());
                Cleanup("LeoSupplies", () => LeoSupplies?.Dispose());
                Cleanup("Leo", () => Leo?.Dispose());
                Cleanup("DealerMachines", () => DealerMachines?.Dispose());
                Cleanup("Stores", () => Stores?.Dispose());
                Cleanup("Economy", () => Economy?.Dispose());
                Cleanup("Statistics", () => Statistics?.Dispose());
                Cleanup("Weather", () => Weather?.Dispose());
                Cleanup("Sleep", () => Sleep?.Dispose());
                Cleanup("Clock", () => Clock?.Dispose());
                Cleanup("WindowTints", () => WindowTints?.Dispose());
                Cleanup("PaintPanels", () => PaintPanels?.Dispose());
                Cleanup("StorageGarageSale", () => StorageGarageSale?.Dispose());
                // Detach optional cargo before vehicle/sale cleanup can remove its parent.
                Cleanup("CargoFreeze", () => Items?.ReleaseFrozenCargo());
                Cleanup("Trailers", () => Trailers?.Dispose());
                Cleanup("VehiclePlates", () => VehiclePlates?.Dispose());
                Cleanup("BoatControls", () => BoatControls?.Dispose());
                Cleanup("BoatMotion", () => BoatMotion?.Dispose());
                Cleanup("Vehicles", () => Vehicles?.Dispose());
                Cleanup("RacecarBuild", () => RacecarBuild?.Dispose());
                Cleanup("BuggyBuild", () => BuggyBuild?.Dispose());
                Cleanup("Doors", () => Doors?.Dispose());
                Cleanup("FoodPrep", () => FoodPrep?.Dispose());
                Cleanup("Generator", () => Generator?.Dispose());
                Cleanup("Fuel", () => Fuel?.Dispose());
                Cleanup("Garages", () => Garages?.Dispose());
                Cleanup("SugarShack", () => SugarShack?.Dispose());
                Cleanup("BoatSale", () => BoatSale?.Dispose());
                Cleanup("CottagePurchase", () => CottagePurchase?.Dispose());
                Cleanup("Forest", () => Forest?.Dispose());
                Cleanup("Garden", () => Garden?.Dispose());
                Cleanup("Tubing", () => Tubing?.Dispose());
                Cleanup("VehicleParts", () => VehicleParts?.Dispose());
                Cleanup("Bolts", () => Bolts?.Dispose());
                Cleanup("VehicleTune", () => VehicleTune?.Dispose());
                Cleanup("VehicleUi", () => VehicleUi?.Dispose());
                Cleanup("SceneAirEquipment", () => SceneAirEquipment?.Dispose());
                Cleanup("TirePressure", () => TirePressure?.Dispose());
                // Restore purchased native bindings before sales restores or releases their clone.
                Cleanup("VehicleSales", () => VehicleSales?.Dispose());
                Cleanup("TriggerFeedback", () => TriggerFeedback?.Dispose());
                Cleanup("Items", () => Items?.Dispose());
                Cleanup("Lights", () => Lights?.Dispose());
                Cleanup("SeatPoseCatalog", () => SeatPoseEntries.Clear());
                Cleanup("Entities", () => Entities?.Dispose());
                Cleanup("channel", () => channel?.Dispose());
                Cleanup("NativeSources", NativeSourceTracking.Reset);
            }
            finally
            {
                SaveRounds = null; LiveProfiles = null; ProfileRestore = null; ReconnectPositions = null;
                LeoHearts = null; Events = null; MeetRace = null; SpeedwayEvents = null; EasterEggs = null;
                channel = null; updates = null;
            }
        }
    }
}
