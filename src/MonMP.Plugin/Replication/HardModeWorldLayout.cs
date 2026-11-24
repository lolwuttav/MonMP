using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // A guest never runs the host's save load, so the host sends the world shape that load left:
    // starter vehicles hidden in storage or removed by hard mode, and hard mode's for-sale displays
    // (Gameplay.HardMode_LoadSave, TwentyFourSevenStore.Start). The guest matches that shape.
    internal static class HardModeWorldLayout
    {
        internal const byte OlTruckForSale = 8, SmollAtvForSale = 16, WoodSplitterForSale = 32, DirtShovelForSale = 64, WateringCanForSale = 128;
        private static readonly FieldInfo? TruckDisplay = AccessTools.Field(typeof(TwentyFourSevenStore), "Vehicle_OlTruck");
        private static readonly FieldInfo? AtvDisplay = AccessTools.Field(typeof(TwentyFourSevenStore), "Vehicle_SmollATV");
        private static readonly FieldInfo? SplitterDisplay = AccessTools.Field(typeof(HardwareStore), "Trailer_WoodSplitter");
        private static readonly FieldInfo? ShovelDisplay = AccessTools.Field(typeof(HardwareStore), "Item_DirtShovel");
        private static readonly FieldInfo? CanDisplay = AccessTools.Field(typeof(HardwareStore), "Item_WateringCan");

        // HardwareStore.Start skips its initially disabled shovel/can children, leaving
        // their native .store null. A guest can have enabled them before matching the
        // host layout. Their exact native anchors still belong to the hardware store
        // in both cases; availability is state, not membership or a store-pointer test.
        internal static Stores? ResolveItemStore(ItemForSale_Display display, HardwareStore? hardware)
        {
            if (hardware != null && (ReferenceEquals(Display(ShovelDisplay, hardware), display) ||
                ReferenceEquals(Display(CanDisplay, hardware), display))) return hardware;
            return display.store;
        }

        // A native disk reload restores bought hard-mode vehicles into their canonical scene roots.
        // A menu guest has no PlayerData, so HardMode_NewSave initializes every Bought flag false
        // and invokes HardMode_LoadSave a frame later. Preserve only the canonical roots the host
        // still owns; live purchased clones keep their separate purchase/snapshot route.
        internal static void PrepareNativeLoad(Gameplay gameplay, uint hidden, uint stored)
        {
            var data = gameplay.Get_HardModeData ?? new HardModeData();
            data.Bought_OlTruck = KeepStarter(Vehicle_Type.OlTruck, hidden, stored);
            data.Bought_SmollATV = KeepStarter(Vehicle_Type.SmollATV, hidden, stored);
            data.Bought_WoodSplitter = KeepStarter(Vehicle_Type.WoodSplitter, hidden, stored);
            gameplay.Get_HardModeData = data;
        }

        private static bool KeepStarter(Vehicle_Type type, uint hidden, uint stored)
        {
            uint bit = 1u << (int)type;
            return (hidden & bit) == 0 || (stored & bit) != 0;
        }

        internal static byte Capture(Gameplay gameplay)
        {
            if (!gameplay.Get_isHardMode) return 0;
            var convenience = Object.FindObjectOfType<TwentyFourSevenStore>(true);
            var hardware = Object.FindObjectOfType<HardwareStore>(true);
            byte layout = 0;
            if (Display(TruckDisplay, convenience)?.gameObject.activeSelf == true) layout |= OlTruckForSale;
            if (Display(AtvDisplay, convenience)?.gameObject.activeSelf == true) layout |= SmollAtvForSale;
            if (Display(SplitterDisplay, hardware)?.gameObject.activeSelf == true) layout |= WoodSplitterForSale;
            if (Display(ShovelDisplay, hardware)?.gameObject.activeSelf == true) layout |= DirtShovelForSale;
            if (Display(CanDisplay, hardware)?.gameObject.activeSelf == true) layout |= WateringCanForSale;
            return layout;
        }

        internal static void Apply(byte layout)
        {
            Debug.Log("MonMP: guest applies the host's hard-mode world layout 0x" + layout.ToString("X2"));
            var convenience = Object.FindObjectOfType<TwentyFourSevenStore>(true);
            var hardware = Object.FindObjectOfType<HardwareStore>(true);
            Show(Display(TruckDisplay, convenience), (layout & OlTruckForSale) != 0, () => convenience!.HardMode_EnableOlTruckForSale());
            Show(Display(AtvDisplay, convenience), (layout & SmollAtvForSale) != 0, () => convenience!.HardMode_EnableSmollATVForSale());
            Show(Display(SplitterDisplay, hardware), (layout & WoodSplitterForSale) != 0, () => hardware!.HardMode_EnableWoodSplitterForSale());
            Show(Display(ShovelDisplay, hardware), (layout & DirtShovelForSale) != 0, () => hardware!.HardMode_EnableDirtShovel());
            Show(Display(CanDisplay, hardware), (layout & WateringCanForSale) != 0, () => hardware!.HardMode_EnableWateringCan());
        }

        // One-of-a-kind scene vehicles. Traffic, pit bikes and the purchasable trailers belong to other adapters.
        private static readonly Vehicle_Type[] Starters = { Vehicle_Type.MainVehicle, Vehicle_Type.OlTruck, Vehicle_Type.SmollATV,
            Vehicle_Type.WoodSplitter, Vehicle_Type.Buggy, Vehicle_Type.LeMissile, Vehicle_Type.SmallCruiser, Vehicle_Type.Racecar,
            Vehicle_Type.ScrapyardKonig, Vehicle_Type.Prinz, Vehicle_Type.FishingBoat };

        // Host: a starter vehicle parked in a storage garage (EntreposageGarage.UnloadThisCar) or removed
        // by hard mode has no active scene copy, so no shared adapter binds it.
        internal static uint CaptureHiddenVehicles()
        {
            uint hidden = 0;
            foreach (var type in Starters)
                if (!SceneStarters(type).Any(v => v.gameObject.activeInHierarchy)) hidden |= 1u << (int)type;
            return hidden;
        }

        // The starters the host keeps unloaded in a storage garage bay this session (a subset of the
        // hidden set). Host and guest both bind these inactive copies so they can be taken out later.
        internal static uint SessionStored { get; set; }
        internal static uint CaptureStoredVehicles()
        {
            uint stored = 0;
            foreach (var type in Starters)
            {
                var copies = SceneStarters(type).ToArray();
                if (!copies.Any(v => v.gameObject.activeInHierarchy) && copies.Any(SharedStorageGarages.InAnyBay)) stored |= 1u << (int)type;
            }
            return stored;
        }
        internal static bool IsSessionStored(NWH.VehiclePhysics2.VehicleController vehicle) =>
            vehicle != null && !vehicle.gameObject.activeInHierarchy && (SessionStored & (1u << (int)vehicle.vehicle_type)) != 0 &&
            vehicle.gameObject.scene.IsValid() && !vehicle.gameObject.name.EndsWith("(Clone)");
        internal static IEnumerable<NWH.VehiclePhysics2.VehicleController> SessionStoredStarters() => SessionStored == 0
            ? Enumerable.Empty<NWH.VehiclePhysics2.VehicleController>()
            : Object.FindObjectsOfType<NWH.VehiclePhysics2.VehicleController>(true).Where(IsSessionStored);

        internal static void HideVehicles(uint hidden)
        {
            if (hidden == 0) return;
            Debug.Log("MonMP: guest hides the host's stored or removed starter vehicles 0x" + hidden.ToString("X"));
            foreach (var type in Starters)
                if ((hidden & (1u << (int)type)) != 0)
                    foreach (var vehicle in SceneStarters(type)) vehicle.gameObject.SetActive(false);
        }

        private static IEnumerable<NWH.VehiclePhysics2.VehicleController> SceneStarters(Vehicle_Type type) =>
            Object.FindObjectsOfType<NWH.VehiclePhysics2.VehicleController>(true)
                .Where(v => v.vehicle_type == type && v.gameObject.scene.IsValid() && !v.gameObject.name.EndsWith("(Clone)"));
        private static Component? Display(FieldInfo? field, Component? store) => store == null ? null : field?.GetValue(store) as Component;
        private static void Show(Component? display, bool forSale, System.Action enable)
        {
            if (display == null || display.gameObject.activeSelf == forSale) return;
            if (forSale) enable(); else display.gameObject.SetActive(false);
        }
    }
}
