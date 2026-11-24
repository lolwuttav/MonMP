using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using MonMP.Plugin.Testing;
using NWH;
using NWH.VehiclePhysics2;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Diagnostics
{
    // Staged eligibility, real VehicleForSale_Display.MouseDown -> host native purchase.
    // The first door callbacks run just after native Start, before the two-second discovery
    // poll. This deliberately tests callbacks, not physical crosshair/mouse acceptance.
    internal sealed class PairedPurchasedTruckDoorsTest
    {
        private sealed class Fixture
        {
            internal Gameplay Game = null!;
            internal TwentyFourSevenStore Store = null!;
            internal VehicleForSale_Display Display = null!;
            internal Stores? DisplayStore;
            internal bool DisplayActive, StoreOpen, HardMode;
            internal int Price, Cash;
            internal float[] WeekCash = null!, Stats = null!;
            internal float[] StatsReference = null!;
            internal List<VehicleForSale_Display> StoreListings = null!;
            internal VehicleForSale_Display[] Listings = null!;
            internal GlobalClassManager Global = null!;
            internal Dictionary<string, Vehicle> Vehicles = null!;
            internal Vehicle? OriginalTruck;
            internal readonly Dictionary<GameObject, bool> OriginalTruckActive = new Dictionary<GameObject, bool>();
            internal readonly Dictionary<Rigidbody, (Vector3 Position, Quaternion Rotation, Vector3 Velocity, Vector3 AngularVelocity)> OriginalBodies =
                new Dictionary<Rigidbody, (Vector3, Quaternion, Vector3, Vector3)>();
            internal readonly HashSet<int> OriginalInstanceIds = new HashSet<int>();
            internal readonly HashSet<GameObject> OwnedClones = new HashSet<GameObject>();
            internal bool PreownedWindows, PreownedPurchaseMade;
            internal int PreownedPurchaseFrame;
            internal float PreownedStartDeadline;
            internal VehicleController? PreownedTruck;
            internal VehicleController? PreownedCanonicalTruck;
            internal float PreownedCanonicalDeadline;
            internal ulong PreownedEntity;
            internal ulong[] PreownedWindowIds = Array.Empty<ulong>();
            internal string PreownedChildIds = "", PreownedBindingWait = "";
            internal HardModeData? HardData;
            internal bool BoughtTruck;
            internal Vector3 PlayerPosition;
            internal Quaternion PlayerRotation;
            internal Vector3 PlayerVelocity, PlayerAngularVelocity;
        }

        private static Fixture? fixture;
        private static PairedPurchasedTruckDoorsTest? active;
        private static Harmony? hooks;
        private static readonly FieldInfo LatePoll = AccessTools.Field(typeof(SharedVehicleDoors), "nextLateScan");
        private static readonly FieldInfo TruckListing = AccessTools.Field(typeof(TwentyFourSevenStore), "Vehicle_OlTruck");
        private static readonly FieldInfo JustOpened = AccessTools.Field(typeof(Vehicle_Door), "Timer_JustOpened");
        private static readonly MethodInfo NativeDoorFixed = AccessTools.Method(typeof(Vehicle_Door), "FixedUpdate", Type.EmptyTypes);
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedVehicleSales sales;
        private readonly SharedVehicleDoors doors;
        private readonly SharedVehicleHood hood;
        private readonly WorldChannel fixtureChannel;
        private const byte SignalMarker = 244;
        private readonly Dictionary<(Component, string), int> sounds = new Dictionary<(Component, string), int>();
        private readonly ulong listing;
        private VehicleController? truck;
        private int phase, signal, snapshots, snapshotBefore, silentBefore;
        private int coalescedOpenBefore, coalescedCloseBefore;
        private int silentPostedBefore, silentPlayedBefore;
        private bool immediateCallback, driverInitializedOpenSilently;
        private float deadline, next, purchaseAt, finalLatchAfter;
        private int cashBefore, nativeBefore;
        private byte[]? purchaseRequest;
        private Guid operation;

        internal string Status => "purchased truck phase=" + phase + "; signal=" + signal + "; immediate=" + immediateCallback +
            "; truck=" + (truck != null) + "; nativePurchases=" + sales.TestNativePurchases + "; sounds=" + sounds.Values.Sum() +
            "; reply=" + sales.TestLastReply?.Status + "; snapshots=" + snapshots + "; doorBusPosted=" + VehicleDoorPresentation.TestPosted +
            "; doorBusPlayed=" + VehicleDoorPresentation.TestPlayed + "; cueQueued=" + VehicleDoorPresentation.TestQueued +
            "; cueExpired=" + VehicleDoorPresentation.TestExpired + "; nativeQueued=" + VehicleDoorPresentation.TestNativeQueued +
            "; nativeExpired=" + VehicleDoorPresentation.TestNativeExpired + (truck == null ? "" : "; " + DoorCues());

        internal static void Prepare(bool host)
        {
            if (!LocalTestIsolation.Installed) throw new InvalidOperationException("Purchased truck fixture requires isolated test saves.");
            if (fixture != null) return;
            var game = Object.FindObjectOfType<Gameplay>();
            var store = Object.FindObjectOfType<TwentyFourSevenStore>();
            var global = Object.FindObjectOfType<GlobalClassManager>();
            var cash = Object.FindObjectOfType<Cash_Manager>();
            var display = store == null ? null : TruckListing.GetValue(store) as VehicleForSale_Display;
            Require(game != null && store != null && global != null && cash != null && display != null &&
                display.RealObjectToBuy != null && display.TypeForSale == Vehicle_Type.OlTruck,
                "native store, truck listing, cash or prefab is unavailable.");
            Require(!game!.Get_isHardMode, "fixture requires a normal-mode isolated baseline; it never runs destructive HardMode_LoadSave.");
            Require(!game.Player.IsHoldingAnObject, "fixture requires empty hands.");
            var originals = Object.FindObjectsOfType<VehicleController>(true).Where(v => v.gameObject.scene.IsValid()).ToArray();
            var originalTruckDoors = originals.Where(v => v.vehicle_type == Vehicle_Type.OlTruck)
                .SelectMany(v => v.GetComponentsInChildren<Vehicle_Door>(true)).ToArray();
            // Native OnDisable closes open doors and schedules their original hinge/body for
            // destruction. Refuse that baseline before assigning a fixture or changing anything.
            Require(originalTruckDoors.All(d => !d.Get_IsOpen),
                "fixture requires every original truck door closed; no fixture changes were made.");
            Debug.Log("MONMP_PURCHASED_TRUCK_ORIGINAL_GUARD: all " + originalTruckDoors.Length + " original truck doors closed before staging.");
            var saved = new Fixture { Game = game, Store = store!, Display = display!, Global = global!,
                DisplayStore = display!.store, DisplayActive = display.gameObject.activeSelf, Price = display.price,
                StoreOpen = store!.isOpen, HardMode = game.Get_isHardMode, Cash = cash!.GetCash,
                WeekCash = cash.GetWeekCash.ToArray(), StatsReference = GameManager.Get_PlayerStats,
                Stats = GameManager.Get_PlayerStats.ToArray(), StoreListings = store.VehicleForSale,
                Listings = store.VehicleForSale.ToArray(), Vehicles = global!.Vehicles,
                HardData = game.Get_HardModeData, BoughtTruck = game.Get_HardModeData?.Bought_OlTruck == true,
                PlayerPosition = game.PlayerWalking.transform.position, PlayerRotation = game.PlayerWalking.transform.rotation,
                PlayerVelocity = game.Get_Player_Rigidbody.velocity, PlayerAngularVelocity = game.Get_Player_Rigidbody.angularVelocity };
            saved.Vehicles.TryGetValue(Vehicle_Type.OlTruck.ToString(), out saved.OriginalTruck);
            foreach (var vehicle in originals)
            {
                saved.OriginalInstanceIds.Add(vehicle.gameObject.GetInstanceID());
                if (vehicle.vehicle_type == Vehicle_Type.OlTruck)
                {
                    saved.OriginalTruckActive[vehicle.gameObject] = vehicle.gameObject.activeSelf;
                    foreach (var body in vehicle.GetComponentsInChildren<Rigidbody>(true))
                        saved.OriginalBodies[body] = (body.position, body.rotation, body.velocity, body.angularVelocity);
                }
            }
            fixture = saved; // Make every subsequent mutation recoverable even if staging fails.
            foreach (var pair in saved.OriginalTruckActive) pair.Key.SetActive(false);
            // Native Vehicle_PartsManager.Awake uses Dictionary.Add by type. Retain the original
            // object intact but vacate this one owned slot while the purchased copy is alive.
            saved.Vehicles.Remove(Vehicle_Type.OlTruck.ToString());
            store.HardMode_EnableOlTruckForSale();
            store.isOpen = true;
            cash.LoadSaveSystem(checked(display.price * 100 + 100000), saved.WeekCash);
            Debug.Log("MONMP_PURCHASED_TRUCK_FIXTURE: native listing enabled; original truck preserved inactive; hardMode=false; price=" + display.price + "; host=" + host);
            Debug.Log("MONMP_PURCHASED_TRUCK_PREFAB: " + ListingDiagnostics(display));
        }

        // This is a separate opt-in Windows scenario. Unlike the live-purchase door test,
        // the host owns its real native purchase before either shared-world catalog exists.
        internal static bool PreparePreownedWindows(bool host)
        {
            Prepare(host);
            var saved = fixture!;
            Require(MonMPPlugin.Instance?.Session?.IsRunning != true && MonMPPlugin.Instance?.World == null &&
                SharedVehicleSales.Active == null, "preowned truck must be prepared before session/shared-world construction.");
            if (!saved.PreownedWindows)
            {
                saved.PreownedWindows = true;
                Require(PreownedNativeClones(saved).Length == 0, "preowned fixture requires no existing native sale clone on either peer.");
                Debug.Log("MONMP_PREOWNED_TRUCK_BEFORE_WORLD: peer=" + (host ? "host" : "guest") +
                    "; saleClones=0; session=false; sharedWorld=false; frame=" + Time.frameCount);
            }
            if (!host) return PreparePreownedCanonicalTruck(saved);
            if (!saved.PreownedPurchaseMade)
            {
                var cash = Object.FindObjectOfType<Cash_Manager>();
                Require(cash != null && !saved.Game.Get_isHardMode && !saved.Game.Player.IsHoldingAnObject &&
                    saved.Display.gameObject.activeInHierarchy && saved.Store.isStoreOpen(), "offline native purchase prerequisites are not satisfied.");
                int before = cash!.GetCash, price = checked(saved.Display.price * 100);
                Require(before >= price, "prepared offline cash cannot afford the native truck.");
                VehicleController[] emitted = Array.Empty<VehicleController>();
                try { saved.Display.MouseDown(); }
                finally
                {
                    emitted = PreownedNativeClones(saved).Where(v => !saved.OriginalInstanceIds.Contains(v.gameObject.GetInstanceID())).ToArray();
                    foreach (var truck in emitted) saved.OwnedClones.Add(truck.gameObject);
                }
                Require(emitted.Length == 1 && !saved.Display.gameObject.activeSelf && cash.GetCash == before - price &&
                    GameManager.Get_PlayerStats[4] == saved.Stats[4] + price,
                    "offline native MouseDown did not produce exactly one sale clone and one actual cash/stat debit.");
                saved.PreownedTruck = emitted[0]; saved.PreownedPurchaseMade = true;
                saved.PreownedPurchaseFrame = Time.frameCount; saved.PreownedStartDeadline = Time.unscaledTime + 20f;
                PreparePreownedCanonicalTruck(saved);
                Debug.Log("MONMP_PREOWNED_TRUCK_NATIVE_PURCHASE: peer=host; native=VehicleForSale_Display.MouseDown; cashBefore=" + before +
                    "; cashAfter=" + cash.GetCash + "; debit=" + price + "; clones=1; frame=" + Time.frameCount + "; sharedWorld=false; savedToDisk=false");
                return false;
            }
            var windows = saved.PreownedTruck!.GetComponentsInChildren<Vehicle_Button_WindowSwitch>(true);
            bool ready = Time.frameCount > saved.PreownedPurchaseFrame && NativeWindowsStarted(windows) && PreparePreownedCanonicalTruck(saved);
            Require(ready || Time.unscaledTime <= saved.PreownedStartDeadline, "preowned truck native window Start did not become ready within 20 seconds.");
            if (!ready) return false;
            Debug.Log("MONMP_PREOWNED_TRUCK_NATIVE_READY: peer=host; nativeWindows=" + windows.Length + "; frame=" + Time.frameCount +
                "; purchaseFrame=" + saved.PreownedPurchaseFrame + "; sharedWorld=false");
            return true;
        }

        // Prepare normally hides the original truck so native purchase Awake can own its
        // dictionary slot. Once that Awake has run, reactivate the exact preserved original
        // for the unchanged stock-window lifecycle. Re-enabling never reruns native Awake.
        private static bool PreparePreownedCanonicalTruck(Fixture saved)
        {
            if (saved.PreownedCanonicalTruck == null)
            {
                var original = saved.OriginalTruck as VehicleController ??
                    throw new InvalidOperationException("Preowned Windows fixture needs the preserved canonical stock truck.");
                Require(saved.OriginalInstanceIds.Contains(original.gameObject.GetInstanceID()) && saved.OriginalTruckActive.ContainsKey(original.gameObject),
                    "canonical stock truck is not an exact preserved original.");
                bool hadSlot = saved.Vehicles.TryGetValue(Vehicle_Type.OlTruck.ToString(), out var before);
                original.gameObject.SetActive(true);
                bool hasSlot = saved.Vehicles.TryGetValue(Vehicle_Type.OlTruck.ToString(), out var after);
                Require(hadSlot == hasSlot && before == after, "reactivating the original truck changed the native purchased-truck dictionary slot.");
                saved.PreownedCanonicalTruck = original; saved.PreownedCanonicalDeadline = Time.unscaledTime + 20f;
                Debug.Log("MONMP_PREOWNED_TRUCK_CANONICAL: preservedOriginal=true; path=" + RuntimeProbe.CanonicalPath(original.transform) +
                    "; activatedForStockWindowLifecycle=true; nativeDictionaryUnchanged=true; frame=" + Time.frameCount);
            }
            var canonical = saved.PreownedCanonicalTruck ?? throw new InvalidOperationException("Preserved canonical truck was removed during preparation.");
            bool ready = NativeWindowsStarted(canonical.GetComponentsInChildren<Vehicle_Button_WindowSwitch>(true));
            Require(ready || Time.unscaledTime <= saved.PreownedCanonicalDeadline, "preserved canonical stock truck windows did not initialize within 20 seconds.");
            return ready;
        }

        internal static VehicleController PreownedCanonicalTruckForWindows => fixture?.PreownedCanonicalTruck ??
            throw new InvalidOperationException("Preowned canonical stock truck preparation is missing.");

        private static VehicleController[] PreownedNativeClones(Fixture saved) => Object.FindObjectsOfType<VehicleController>(true)
            .Where(v => v != null && v.gameObject.scene == saved.Display.gameObject.scene && v.vehicle_type == Vehicle_Type.OlTruck &&
                v.name == saved.Display.RealObjectToBuy.name + "(Clone)" &&
                (v.transform.parent == saved.Display.transform.parent || v.transform.parent == null)).ToArray();

        internal static void AssertPreownedGuestBeforeJoin(MonMPPlugin plugin)
        {
            var saved = fixture ?? throw new InvalidOperationException("Preowned Windows preparation is missing.");
            Require(!plugin.TestOptions.IsHost && saved.PreownedWindows && plugin.Session?.IsRunning != true && plugin.World == null &&
                PreownedNativeClones(saved).Length == 0, "guest must have no native sale clone immediately before joining/rejoining.");
            Debug.Log("MONMP_PREOWNED_TRUCK_GUEST_BEFORE_JOIN: saleClones=0; session=false; sharedWorld=false; frame=" + Time.frameCount);
        }

        private static bool NativeWindowsStarted(Vehicle_Button_WindowSwitch[] windows)
        {
            if (windows.Length == 0 || windows.Length > 8) return false;
            var up = AccessTools.Field(typeof(Vehicle_Button_WindowSwitch), "UpRotation");
            var motor = AccessTools.Field(typeof(SOUND_Vehicle_ElectricWindow), "ElectricWindow");
            return windows.All(button => button != null && button.gameObject.activeInHierarchy && button.enabled &&
                up.GetValue(button) is Quaternion q && Quaternion.Dot(q, q) > 0.5f &&
                button.GetComponent<SOUND_Vehicle_ElectricWindow>() is SOUND_Vehicle_ElectricWindow sound && motor.GetValue(sound) is AudioSource);
        }

        internal static bool TryAssertPreownedWindows(MonMPPlugin plugin, string stage)
        {
            var saved = fixture ?? throw new InvalidOperationException("Preowned Windows preparation is missing.");
            Require(saved.PreownedWindows, "preowned Windows preparation is missing.");
            var world = plugin.World;
            if (world == null || !world.PersonalProfileReady || !world.Channel.FullySynchronized) return false;
            var saleAdapter = world.VehicleSales ?? throw new InvalidOperationException("Preowned Windows sale adapter is missing.");
            var purchases = saleAdapter.PurchasedVehicles().Where(p => p.Type == Vehicle_Type.OlTruck).ToArray();
            if (purchases.Length == 0) return false;
            Require(purchases.Length == 1, "expected exactly one purchased truck in the synchronized sale catalog.");
            var purchase = purchases[0]; var truck = purchase.Instance.GetComponent<VehicleController>();
            Require(truck != null && !saved.OriginalInstanceIds.Contains(purchase.Instance.GetInstanceID()), "preowned sale resolved to an original fixture vehicle.");
            saved.OwnedClones.Add(purchase.Instance);
            if (plugin.TestOptions.IsHost)
                Require(saved.PreownedPurchaseMade && truck == saved.PreownedTruck && Time.frameCount > saved.PreownedPurchaseFrame,
                    "host replaced its native pre-session purchase or constructed the catalog before native Start.");
            Require(saleAdapter.TestNativePurchases == 0, "preowned scenario unexpectedly used an in-session purchase transaction.");
            var buttons = truck!.GetComponentsInChildren<Vehicle_Button_WindowSwitch>(true);
            if (!NativeWindowsStarted(buttons)) return false;
            var ids = new List<ulong>();
            var records = world.Vehicles.Windows.InitialRecords().ToDictionary(r => r.Id);
            foreach (var button in buttons)
            {
                string relative = "$root";
                Require(button.transform == truck.transform || PurchasedVehiclePartIdentityPolicy.TryRelativePath(truck.transform, button.transform, out relative),
                    "purchased native window has no stable relative path.");
                int component = Array.IndexOf(button.GetComponents<Vehicle_Button_WindowSwitch>(), button);
                string identity = "purchased-vehicle-window-v1/" + purchase.Entity.ToString("X16") + "/" + (int)Vehicle_Type.OlTruck + "/" + relative + "/component/" + component;
                ulong id = WorldRecord.DynamicIdStart | Math.Max(1UL, StaticId.Hash(identity) & 0x7FFFFFFFFFFFFFFFUL);
                var native = world.Entities.Resolve(id);
                Require(native != null && native.AdapterType == VehicleWindowState.Adapter && native.gameObject == button.gameObject && native.SpawnKey == identity &&
                    records.ContainsKey(id) && world.Channel.Records.TryGetValue(id, out var record) && record.Adapter == VehicleWindowState.Adapter &&
                    !world.Vehicles.Windows.RequiredCatalogEntityIds.Contains(id), "sale-scoped native window binding/optional record is missing: " + id.ToString("X16"));
                Require(plugin.TestOptions.IsHost || world.Vehicles.Windows.IsReplica(button), "guest native purchased window was not bound as a replica.");
                ids.Add(id);
            }
            var ordered = ids.OrderBy(id => id).ToArray();
            ulong[] doorIds, trunkIds, seatIds, toolIds, paintIds;
            ulong uiId;
            try
            {
                doorIds = world.Vehicles.Doors.TestPurchasedDoorIds(purchase.Entity, purchase.Instance);
                trunkIds = world.Vehicles.Trunk.TestPurchasedTrunkIds(purchase.Entity, purchase.Instance);
                seatIds = world.ExtraSeats.TestPurchasedSeatIds(purchase.Entity, purchase.Instance);
                toolIds = world.HoistPullers.TestPurchasedToolIds(purchase.Entity, purchase.Instance);
                paintIds = world.PaintPanels.TestPurchasedPanelIds(purchase.Entity, purchase.Instance);
                uiId = world.VehicleUi.TestPurchasedUiId(purchase.Entity, purchase.Instance);
            }
            catch (InvalidOperationException exception)
            {
                if (saved.PreownedBindingWait != exception.Message)
                    Debug.Log("MONMP_PREOWNED_BINDING_WAIT: peer=" + (plugin.TestOptions.IsHost ? "host" : "guest") + "; stage=" + stage + "; reason=" + exception.Message);
                saved.PreownedBindingWait = exception.Message;
                return false; // The caller bounds native Start/retained-payload readiness to 20 seconds.
            }
            Require(doorIds.Length == 2 && trunkIds.Length == 1 && seatIds.Length == 6 && toolIds.Length == 1 && paintIds.Length > 0,
                "preowned child binding counts differ from the native truck layout.");
            string childIds = "doors=" + HexIds(doorIds) + "; trunks=" + HexIds(trunkIds) + "; seats=" + HexIds(seatIds) +
                "; tools=" + HexIds(toolIds) + "; paint=" + HexIds(paintIds) + "; ui=" + uiId.ToString("X16");
            Require(saved.PreownedEntity == 0 || saved.PreownedEntity == purchase.Entity && saved.PreownedWindowIds.SequenceEqual(ordered),
                "rejoin changed the sale entity or purchased native window identities.");
            Require(saved.PreownedChildIds.Length == 0 || saved.PreownedChildIds == childIds,
                "rejoin changed the sale-scoped doors, tailgate, seats, hoist tool, paint or dashboard identities.");
            saved.PreownedChildIds = childIds; saved.PreownedBindingWait = "";
            saved.PreownedEntity = purchase.Entity; saved.PreownedWindowIds = ordered;
            var canonical = world.Entities.Snapshot().Where(i => i.AdapterType == VehicleWindowState.Adapter && i.EntityId > WorldRecord.DynamicIdStart &&
                !i.SpawnKey.StartsWith("purchased-vehicle-window-v1/", StringComparison.Ordinal)).ToArray();
            Require(canonical.All(i => world.Vehicles.Windows.RequiredCatalogEntityIds.Contains(i.EntityId)), "canonical high-bit windows were made optional.");
            Debug.Log("MONMP_PREOWNED_TRUCK_WINDOWS: peer=" + (plugin.TestOptions.IsHost ? "host" : "guest") + "; stage=" + stage +
                "; catalog=" + world.CatalogHash + "; saleEntity=" + purchase.Entity.ToString("X16") + "; nativeWindows=" + buttons.Length +
                "; windowIds=" + string.Join(",", ordered.Select(id => id.ToString("X16"))) + "; canonicalHighBitRequired=" + canonical.Length +
                "; " + childIds + "; localFullySynchronized=true; nativePurchasesDuringSession=0");
            return true;
        }
        private static string HexIds(IEnumerable<ulong> ids) => string.Join(",", ids.Select(id => id.ToString("X16")));
        internal static string PreownedBindingWait => fixture?.PreownedBindingWait ?? "fixture unavailable";

        internal PairedPurchasedTruckDoorsTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            Require(fixture != null, "Prepare must run before shared-world construction.");
            sales = plugin.World!.VehicleSales!; doors = plugin.World.Vehicles.Doors; hood = plugin.World.Vehicles.Hood;
            var records = sales.InitialRecords().ToArray();
            var candidates = records.Where(r => VehicleSaleListingState.Decode(r.Payload).NativeVehicleType == (int)Vehicle_Type.OlTruck).ToArray();
            Require(candidates.Length == 1, "expected one registered truck listing, found " + candidates.Length + "; catalog=" +
                string.Join(",", records.Select(r => r.Id.ToString("X16") + ":" + (Vehicle_Type)VehicleSaleListingState.Decode(r.Payload).NativeVehicleType)) +
                "; native=" + ListingDiagnostics(fixture!.Display));
            var record = candidates[0];
            listing = record.Id;
            Require(!sales.TestState(listing).Purchased && sales.TestDisplay(listing) == fixture!.Display, "truck sale fixture was not registered.");
            cashBefore = Object.FindObjectOfType<Cash_Manager>().GetCash; nativeBefore = sales.TestNativePurchases;
            deadline = Time.unscaledTime + 120f;
            // A 60-second embargo on only the periodic scan proves that callbacks/command receipt
            // discover the new instance themselves. Production capture/hinge updates still run.
            LatePoll.SetValue(doors, Time.unscaledTime + 60f);
            active = this;
            InstallHooks();
            sales.PurchasedVehicleAvailable += Purchased;
            fixtureChannel = plugin.World.Channel;
            fixtureChannel.CommandRequested += SignalReceived;
            fixtureChannel.CommandRejected += SignalReceived;
            fixtureChannel.SnapshotApplied += SnapshotApplied;
            Require(!hood.TestNative().Open && !hood.TestNative().Released, "Konig hood baseline must be latched closed.");
        }

        private static void InstallHooks()
        {
            Require(hooks == null, "diagnostic hooks already installed.");
            hooks = new Harmony("MonMP.LocalTest.PurchasedTruckDoors");
            try
            {
                var start = AccessTools.DeclaredMethod(typeof(Vehicle_Door), "Start", Type.EmptyTypes);
                Require(start != null && start.ReturnType == typeof(void), "native Vehicle_Door.Start signature changed.");
                hooks.Patch(start, postfix: new HarmonyMethod(AccessTools.Method(typeof(PairedPurchasedTruckDoorsTest), nameof(DoorStarted))));
                PatchHealth.VerifyManualPatchInstalled(hooks, start!);
                var bindingType = typeof(SharedVehicleDoors).GetNestedType("Binding", BindingFlags.NonPublic);
                Require(bindingType != null, "shared door binding type changed.");
                var apply = AccessTools.DeclaredMethod(typeof(SharedVehicleDoors), "Apply", new[] { bindingType!, typeof(VehicleDoorState), typeof(bool) });
                Require(apply != null && apply.ReturnType == typeof(void) &&
                    AccessTools.Field(bindingType, "Door")?.FieldType == typeof(Vehicle_Door) &&
                    AccessTools.Field(bindingType, "State")?.FieldType == typeof(VehicleDoorState), "shared door apply signature changed.");
                hooks.Patch(apply, prefix: new HarmonyMethod(AccessTools.Method(typeof(PairedPurchasedTruckDoorsTest), nameof(DoorApplying))));
                PatchHealth.VerifyManualPatchInstalled(hooks, apply!);
                foreach (var type in new[] { typeof(SOUND_Vehicle_Door), typeof(SOUND_Vehicle_Hood) })
                foreach (var name in type == typeof(SOUND_Vehicle_Door) ? new[] { "Open", "Close" } : new[] { "Release", "Open", "Close" })
                {
                    var method = AccessTools.DeclaredMethod(type, name, Type.EmptyTypes);
                    Require(method != null && method.ReturnType == typeof(void), "native sound signature changed: " + type.Name + "." + name);
                    hooks.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(typeof(PairedPurchasedTruckDoorsTest), nameof(SoundCalled))));
                    PatchHealth.VerifyManualPatchInstalled(hooks, method!);
                }
            }
            catch { hooks.UnpatchSelf(); hooks = null; active = null; throw; }
        }
        private static void DoorStarted(Vehicle_Door __instance) => active?.ImmediateDoor(__instance);
        private static void DoorApplying(SharedVehicleDoors __instance, object __0, VehicleDoorState __1, bool __2)
        {
            var test = active;
            if (test == null || !ReferenceEquals(__instance, test.doors)) return;
            var bindingType = __0.GetType();
            var door = AccessTools.Field(bindingType, "Door").GetValue(__0) as Vehicle_Door;
            if (door == null || door.vehicleController != test.truck) return;
            var previous = (VehicleDoorState)AccessTools.Field(bindingType, "State").GetValue(__0);
            if (__2 && __1.Open && door.isDriverDoor) test.driverInitializedOpenSilently = true;
            if (previous.Open != __1.Open)
                Debug.Log("MONMP_PURCHASED_TRUCK_DOOR_APPLY: side=" + (door.isDriverDoor ? "driver" : "passenger") +
                    "; snapshot=" + __2 + "; oldOpen=" + previous.Open + "; newOpen=" + __1.Open +
                    "; cuesBefore=" + test.Cue(door, "Open") + "/" + test.Cue(door, "Close"));
        }
        private static void SoundCalled(Component __instance, MethodBase __originalMethod)
        {
            var test = active;
            if (test == null) return;
            var door = __instance.GetComponent<Vehicle_Door>();
            var hood = __instance.GetComponent<Vehicle_Hood>();
            if (door?.vehicleController != test.truck && hood?.vehicleController?.vehicle_type != Vehicle_Type.MainVehicle) return;
            var key = (__instance, __originalMethod.Name);
            test.sounds.TryGetValue(key, out int count); test.sounds[key] = count + 1;
        }
        private void Purchased(ulong entity, Vehicle_Type type, GameObject instance)
        {
            if (type != Vehicle_Type.OlTruck) return;
            Require(entity == sales.TestState(listing).VehicleEntity && !fixture!.OriginalInstanceIds.Contains(instance.GetInstanceID()), "purchase callback did not identify a new truck.");
            fixture!.OwnedClones.Add(instance);
            truck = instance.GetComponent<VehicleController>(); purchaseAt = Time.unscaledTime;
            Require(truck != null && truck.GetComponentsInChildren<Vehicle_Hood>(true).Length == 0,
                "stock truck shape changed: separate Konig hood coverage must be reconsidered.");
            Require(plugin.World!.Vehicles.HasPurchasedVehicleBinding(entity, type, instance), "purchase callback did not complete exact movement binding.");
            Debug.Log("MONMP_PURCHASED_TRUCK_BINDING: entity=" + entity.ToString("X16") + "; peer=" + (plugin.TestOptions.IsHost ? "host" : "guest") +
                "; mode=" + truck!.VehicleMultiplayerInstanceType + "; rootKinematic=" + truck.vehicleRigidbody.isKinematic +
                "; hitchSupported=" + SharedTrailers.HasSupportedPurchasedHitch(type, instance) + "; exactMovementBinding=True");
        }
        private void ImmediateDoor(Vehicle_Door door)
        {
            if (truck == null || door.vehicleController != truck || immediateCallback || door.isDriverDoor != plugin.TestOptions.IsHost) return;
            Require(Time.unscaledTime < (float)LatePoll.GetValue(doors) && Time.unscaledTime - purchaseAt < 2f,
                "native first usable callback missed the pre-poll window.");
            var handle = HandleForDoor(door);
            if (plugin.TestOptions.IsHost) Move(handle.transform.position);
            handle.MouseDown();
            Require(doors.Handles(door), "first usable purchased door callback did not bind the exact purchased truck.");
            immediateCallback = true;
            Debug.Log("MONMP_PURCHASED_TRUCK_IMMEDIATE: peer=" + (plugin.TestOptions.IsHost ? "host driver" : "guest passenger") +
                "; native Start postfix -> handle MouseDown; age=" + (Time.unscaledTime - purchaseAt).ToString("F3") +
                "; door=" + DiagnosticPath(door.transform) + "; handle=" + DiagnosticPath(handle.transform) +
                "; mapped=" + (handle.vehicle_Door == door && door.vehicleController == truck) +
                "; periodic discovery embargo active; physical mouse not exercised.");
        }

        private void SignalReceived(uint peer, ulong id, byte[] payload)
        {
            // Clock has no native command handler. These fixture markers are independent of
            // its constantly changing revision, so rejected packets use the same validation.
            if (!plugin.TestOptions.IsHost || peer == 0 || id != ClockState.Entity || payload.Length != 2 ||
                payload[0] != SignalMarker || payload[1] < 1 || payload[1] > 5 || plugin.Session?.Peers.ContainsKey(peer) != true) return;
            signal = Math.Max(signal, payload[1]);
        }
        private void SnapshotApplied(IReadOnlyDictionary<ulong, WorldRecord> records) => snapshots++;
        private void Signal(byte value) => fixtureChannel.Request(ClockState.Entity, new[] { SignalMarker, value });
        internal bool Tick()
        {
            Require(plugin.Session?.IsRunning == true && plugin.World != null,
                "session ended before purchased-truck fixture completed; " + Status);
            Require(!immediateCallback || truck != null,
                "owned purchased truck disappeared before fixture completed; " + Status);
            Require(Time.unscaledTime <= deadline, "timed out: " + Status);
            if (Time.unscaledTime < next) return false;
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }
        private bool Host()
        {
            if (phase == 0 && truck != null && immediateCallback && Door(false).Get_IsOpen)
            {
                var cash = Object.FindObjectOfType<Cash_Manager>();
                Require(sales.TestNativePurchases == nativeBefore + 1 && cash.GetCash == cashBefore - fixture!.Display.price * 100,
                    "guest purchase did not create exactly one native clone and debit.");
                Require(GameManager.Get_PlayerStats[4] == fixture!.Stats[4] + fixture.Display.price * 100 &&
                    (gameplay.Get_HardModeData?.Bought_OlTruck == true) == fixture.BoughtTruck, "native purchase stats/normal-mode ownership changed unexpectedly.");
                Require(doors.TestSynchronized && Door(true).Get_IsOpen, "host lost its immediate driver open.");
                phase = 1;
            }
            else if (phase == 1 && signal >= 1)
            {
                Require(sales.TestNativePurchases == nativeBefore + 1 &&
                    Object.FindObjectOfType<Cash_Manager>().GetCash == cashBefore - fixture!.Display.price * 100,
                    "duplicate purchase replay debited again.");
                phase = 2;
            }
            else if (phase == 2 && signal >= 2 && !Door(false).Get_IsOpen)
            {
                var driver = Door(true);
                Require(driver.Get_IsOpen && JustOpened.FieldType == typeof(float) && NativeDoorFixed.ReturnType == typeof(void),
                    "coalesced native close/open/close requires the exact native timer and FixedUpdate signature.");
                Move(Handle(true).transform.position);
                int opens = Cue(driver, "Open"), closes = Cue(driver, "Close"), posts = VehicleDoorPresentation.TestPosted;
                int frame = Time.frameCount;
                CloseNativeDoorNow(driver); Handle(true).MouseDown(); CloseNativeDoorNow(driver);
                Require(!driver.Get_IsOpen && Time.frameCount == frame && Cue(driver, "Open") == opens + 1 &&
                    Cue(driver, "Close") == closes + 2 && VehicleDoorPresentation.TestPosted == posts + 3,
                    "native same-frame close/open/close did not post all three independent cues; before=" + opens + "/" + closes +
                    "; after=" + Cue(driver, "Open") + "/" + Cue(driver, "Close") + "; posts=" + posts + "->" +
                    VehicleDoorPresentation.TestPosted + "; frame=" + frame + "->" + Time.frameCount + "; " + Status);
                Debug.Log("MONMP_PURCHASED_TRUCK_COALESCED_HOST: native FixedUpdate latch -> handle open -> FixedUpdate latch; one frame; cue deltas=1/2; posts=3; physical gesture not exercised.");
                phase = 3;
            }
            else if (phase == 3 && signal >= 4 && !Door(true).Get_IsOpen)
            { Move(Handle(true).transform.position); Handle(true).MouseDown(); phase = 4; }
            else if (phase == 4 && signal >= 5 && Door(true).Get_IsOpen)
            {
                Move(Handle(true).transform.position); Handle(true).MouseDown();
                finalLatchAfter = Time.unscaledTime + 0.15f; phase = 5;
            }
            else if (phase == 5 && Door(true).Get_IsOpen && Time.unscaledTime > finalLatchAfter)
            {
                Require(fixture!.OwnedClones.Contains(truck!.gameObject), "final staged latch target is not the fixture-owned purchased truck.");
                var driver = Door(true);
                int closes = Cue(driver, "Close"), posts = VehicleDoorPresentation.TestPosted;
                // An open-door click imparts force; the staged sale/player collision layout
                // need not let its hinge reach zero. Retain that normal request, then exercise
                // the actual native latch after its >0.1s timer window on only our own clone.
                CloseNativeDoorNow(driver);
                Require(Cue(driver, "Close") == closes + 1 && VehicleDoorPresentation.TestPosted == posts + 1,
                    "final staged native latch did not post exactly one close cue; " + Status);
                Debug.Log("MONMP_PURCHASED_TRUCK_FINAL_LATCH: normal handle request retained; waited >0.1s; owned driver staged at native threshold; exact close/post deltas=1/1; physical hinge closure remains manual.");
            }
            else if (phase == 5 && signal >= 3 && plugin.Session!.Peers.Count == 1)
            {
                Require(!Door(true).Get_IsOpen && !Door(false).Get_IsOpen && !hood.TestNative().Open && !hood.TestNative().Released,
                    "host panels did not finish closed.");
                Require(Cue(Door(true), "Open") >= 1 && Cue(Door(false), "Open") >= 1 && Cue(Door(true), "Close") >= 1 && Cue(Door(false), "Close") >= 1,
                    "host native door open/close cue hooks were not exercised.");
                plugin.EndPairedTestSession(); Cleanup(); return true;
            }
            return false;
        }
        private bool Guest()
        {
            if (phase == 0)
            {
                var prefab = fixture!.Display.RealObjectToBuy;
                var passengerDoor = SelectDoor(prefab, false);
                var passenger = HandleForDoor(passengerDoor);
                Debug.Log("MONMP_PURCHASED_TRUCK_HANDLE: stage=prefab-preposition; door=" + DiagnosticPath(passengerDoor.transform) +
                    "; selectedHandle=" + DiagnosticPath(passenger.transform) + "; mapped=" + (passenger.vehicle_Door == passengerDoor) +
                    "; mappedChildHandles=" + passengerDoor.GetComponentsInChildren<Vehicle_DoorHandle>(true).Count(h => h != null && h.vehicle_Door == passengerDoor));
                var position = fixture.Display.transform.TransformPoint(prefab.transform.InverseTransformPoint(passenger.transform.position));
                Move(position); phase = 1; next = Time.unscaledTime + 1f;
            }
            else if (phase == 1)
            {
                Require(Vector3.Distance(gameplay.PlayerWalking.transform.position, fixture!.Display.transform.position) <= 4f,
                    "passenger preposition is outside listing range.");
                fixture.Display.MouseDown(); phase = 2;
            }
            else if (phase == 2 && sales.TestLastReply?.Status == PurchaseStatus.Placed && truck != null && immediateCallback &&
                Door(true).Get_IsOpen && Door(false).Get_IsOpen && doors.TestSynchronized &&
                Cue(Door(true), "Open") >= 1 && Cue(Door(false), "Open") >= 1)
            {
                Require(Cue(Door(false), "Open") >= 1, "guest immediate passenger live open cue missing; " + DoorCues());
                Require(Cue(Door(true), "Open") >= 1,
                    "driver initial host-native open cue was lost before replica readiness; " + DoorCues());
                Debug.Log("MONMP_PURCHASED_TRUCK_INITIAL_CUES: " + DoorCues() + "; driverSnapshotSeed=" + driverInitializedOpenSilently +
                    "; explicit existing-replica driver close/reopen/close follows.");
                Require(Time.unscaledTime < (float)LatePoll.GetValue(doors), "doors only synchronized after periodic discovery.");
                operation = sales.TestLastReply!.Value.Operation;
                purchaseRequest = new VehicleSaleRequest(operation, listing, (int)Vehicle_Type.OlTruck, sales.TestState(listing).PriceCents).Encode();
                plugin.World!.Channel.Request(listing, purchaseRequest); Signal(1);
                phase = 3; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 3)
            {
                if (BeginSilentRepair(SharedVehicleDoors.LateAdapter, "Purchased truck door snapshot silence diagnostic")) phase = 4;
            }
            else if (phase == 4 && snapshots > snapshotBefore && plugin.World!.Channel.FullySynchronized)
            {
                Require(sounds.Values.Sum() == silentBefore && doors.TestSynchronized &&
                    VehicleDoorPresentation.TestPosted == silentPostedBefore && VehicleDoorPresentation.TestPlayed == silentPlayedBefore,
                    "door catch-up replayed a cue or lost state; soundBaseline=" + silentBefore + "; busBaseline=" +
                    silentPostedBefore + "/" + silentPlayedBefore + "; " + Status);
                phase = 19;
            }
            else if (phase == 19)
            {
                if (BeginSilentRepair(SharedPresentationEvents.Adapter, "Purchased truck historical cue snapshot silence diagnostic")) phase = 18;
            }
            else if (phase == 18 && snapshots > snapshotBefore && plugin.World!.Channel.FullySynchronized)
            {
                Require(sounds.Values.Sum() == silentBefore && doors.TestSynchronized &&
                    VehicleDoorPresentation.TestPosted == silentPostedBefore && VehicleDoorPresentation.TestPlayed == silentPlayedBefore,
                    "historical event log snapshot replayed a native cue; soundBaseline=" + silentBefore + "; busBaseline=" +
                    silentPostedBefore + "/" + silentPlayedBefore + "; " + Status);
                Debug.Log("MONMP_PURCHASED_TRUCK_SNAPSHOT_SILENT: adapters=134,133; nativeSounds=" + silentBefore +
                    "; posted=" + silentPostedBefore + "; played=" + silentPlayedBefore);
                Move(Handle(false).transform.position); phase = 5; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 5) { Handle(false).MouseDown(); phase = 6; }
            else if (phase == 6 && !Door(false).Get_IsOpen && doors.TestSynchronized && Cue(Door(false), "Close") >= 1)
            {
                Require(Cue(Door(false), "Close") >= 1, "guest passenger close cue missing.");
                coalescedOpenBefore = Cue(Door(true), "Open"); coalescedCloseBefore = Cue(Door(true), "Close");
                Signal(2); phase = 7;
            }
            else if (phase == 7 && !Door(true).Get_IsOpen && doors.TestSynchronized &&
                Cue(Door(true), "Open") >= coalescedOpenBefore + 1 && Cue(Door(true), "Close") >= coalescedCloseBefore + 2)
            {
                Require(Cue(Door(true), "Open") == coalescedOpenBefore + 1 && Cue(Door(true), "Close") == coalescedCloseBefore + 2,
                    "same-frame native cycle duplicated guest cues; " + DoorCues());
                Debug.Log("MONMP_PURCHASED_TRUCK_COALESCED_GUEST: final closed state; exact native cue deltas=1/2; queued=" +
                    VehicleDoorPresentation.TestQueued + "; expired=" + VehicleDoorPresentation.TestExpired);
                Signal(4); phase = 16;
            }
            else if (phase == 16 && Door(true).Get_IsOpen && doors.TestSynchronized && Cue(Door(true), "Open") >= coalescedOpenBefore + 2)
            { Require(Cue(Door(true), "Open") == coalescedOpenBefore + 2, "guest existing driver replica duplicated the live reopen cue; " + DoorCues()); Signal(5); phase = 17; }
            else if (phase == 17 && !Door(true).Get_IsOpen && doors.TestSynchronized && Cue(Door(true), "Close") >= coalescedCloseBefore + 3)
            { Require(Cue(Door(true), "Close") == coalescedCloseBefore + 3 && Cue(Door(true), "Open") == coalescedOpenBefore + 2,
                    "guest driver replica lost or duplicated the final live reopen/close cue; " + DoorCues());
                Debug.Log("MONMP_PURCHASED_TRUCK_LIVE_CUES: " + DoorCues() + "; guest passenger first action and host driver close/reopen/close verified.");
                Move(hood.TestReleasePosition); phase = 8; next = Time.unscaledTime + 0.8f; }
            else if (phase == 8) { hood.TestRelease(); phase = 9; }
            else if (phase == 9 && hood.TestState.Released && !hood.TestState.Open && hood.TestSynchronized)
            { Require(HoodCue("Release") >= 1, "guest native hood release cue missing."); Move(hood.TestOpenPosition); phase = 10; next = Time.unscaledTime + 0.8f; }
            else if (phase == 10) { hood.TestOpen(); phase = 11; next = Time.unscaledTime + 1f; }
            else if (phase == 11 && hood.TestState.Open && hood.TestSynchronized)
            { Require(HoodCue("Open") >= 1, "guest native hood open cue missing.");
                if (BeginSilentRepair(VehicleHoodState.Adapter, "Native hood snapshot silence diagnostic")) phase = 12; }
            else if (phase == 12 && snapshots > snapshotBefore && plugin.World!.Channel.FullySynchronized)
            { Require(sounds.Values.Sum() == silentBefore && hood.TestSynchronized, "hood catch-up replayed a cue or lost state."); Move(hood.TestClosePosition); phase = 13; next = Time.unscaledTime + 0.8f; }
            else if (phase == 13) { hood.TestClose(); phase = 14; }
            else if (phase == 14 && !hood.TestState.Open && !hood.TestState.Released && hood.TestSynchronized)
            {
                Require(HoodCue("Close") >= 1, "guest native hood close cue missing."); Signal(3); phase = 15; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 15)
            {
                silentBefore = sounds.Values.Sum(); var registry = plugin.World!.Entities;
                plugin.EndPairedTestSession();
                Require(registry.Count == 0 && doors.TestRestored && !hood.TestNative().Open && !hood.TestNative().Released,
                    "disconnect failed to restore panel baseline or remove registry bindings.");
                Require(sounds.Values.Sum() == silentBefore, "disconnect restoration replayed native panel cues.");
                Cleanup(); return true;
            }
            return false;
        }
        private bool BeginSilentRepair(ushort adapter, string reason)
        {
            if (!fixtureChannel.FullySynchronized || fixtureChannel.RepairPending) return false;
            int soundBaseline = sounds.Values.Sum(), snapshotBaseline = snapshots;
            int postedBaseline = VehicleDoorPresentation.TestPosted, playedBaseline = VehicleDoorPresentation.TestPlayed;
            // RequestResync also enforces its global cooldown. Stay in the request phase until
            // accepted; never wait for a snapshot whose request was declined.
            if (!fixtureChannel.RequestResync(SnapshotScope.Section(adapter), reason)) return false;
            silentBefore = soundBaseline; snapshotBefore = snapshotBaseline;
            silentPostedBefore = postedBaseline; silentPlayedBefore = playedBaseline;
            return true;
        }
        private static void CloseNativeDoorNow(Vehicle_Door door)
        {
            // Only this fixture-owned purchased door is staged at the native latch threshold.
            // Native FixedUpdate owns its flag, physics teardown, sound and DoorClosed event.
            JustOpened.SetValue(door, 0f);
            var euler = door.transform.localEulerAngles; euler.y = 0f; door.transform.localEulerAngles = euler;
            NativeDoorFixed.Invoke(door, null);
            Require(!door.Get_IsOpen, "native staged latch callback did not close the owned purchased door.");
        }

        private Vehicle_Door Door(bool driver) => SelectDoor(truck!.gameObject, driver);
        private Vehicle_DoorHandle Handle(bool driver) => HandleForDoor(Door(driver));
        private static Vehicle_Door SelectDoor(GameObject instance, bool driver)
        {
            var all = instance.GetComponentsInChildren<Vehicle_Door>(true);
            var matches = all.Where(d => d != null && d.isDriverDoor == driver).ToArray();
            if (matches.Length != 1)
                Require(false, "expected one " + (driver ? "driver" : "passenger") + " door on " + instance.name +
                    ", found " + matches.Length + "; doors=" + string.Join(",", all.Select(d => d == null ? "missing" : DiagnosticPath(d.transform) + ":driver=" + d.isDriverDoor)));
            return matches[0];
        }
        private static Vehicle_DoorHandle HandleForDoor(Vehicle_Door door)
        {
            // The stock truck has inside and outside native handles for each cabin door.
            // Match SharedVehicleDoors.ScanLate's first handle under this exact door.
            var handles = door.GetComponentsInChildren<Vehicle_DoorHandle>(true).Where(h => h != null && h.vehicle_Door == door).ToArray();
            if (handles.Length == 0) Require(false, "native door has no correctly mapped child handle: " + DiagnosticPath(door.transform));
            return handles[0];
        }
        private int Cue(Vehicle_Door door, string name) => sounds.TryGetValue((door.GetComponent<SOUND_Vehicle_Door>(), name), out int count) ? count : 0;
        private string DoorCues() => "driverOpen=" + Cue(Door(true), "Open") + "; driverClose=" + Cue(Door(true), "Close") +
            "; passengerOpen=" + Cue(Door(false), "Open") + "; passengerClose=" + Cue(Door(false), "Close");
        private int HoodCue(string name) => sounds.Where(p => p.Key.Item1 is SOUND_Vehicle_Hood sound && sound.GetComponent<Vehicle_Hood>().vehicleController.vehicle_type == Vehicle_Type.MainVehicle && p.Key.Item2 == name).Sum(p => p.Value);
        private void Move(Vector3 position)
        { gameplay.PlayerWalking.transform.position = position; gameplay.Get_Player_Rigidbody.position = position; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        internal void Cleanup()
        {
            sales.PurchasedVehicleAvailable -= Purchased;
            fixtureChannel.CommandRequested -= SignalReceived;
            fixtureChannel.CommandRejected -= SignalReceived;
            fixtureChannel.SnapshotApplied -= SnapshotApplied;
            hooks?.UnpatchSelf(); hooks = null; active = null;
            RestoreIsolatedFixture();
        }
        internal static void RestoreIsolatedFixture()
        {
            hooks?.UnpatchSelf(); hooks = null; active = null;
            var saved = fixture; if (saved == null) return;
            // Guest economy guards correctly suppress Cash_Manager.LoadSaveSystem while the
            // multiplayer world is attached. Teardown the diagnostic session first, including
            // ctor-failure cleanup, so native restoration is permitted and replica presentation
            // cannot overwrite restored listing fields on a subsequent frame.
            if (MonMPPlugin.Instance?.World != null) MonMPPlugin.Instance.EndPairedTestSession();
            // Destroy only instances explicitly emitted by this listing's purchase callback. Never
            // remove an original or a truck found by a broad scene/name search.
            foreach (var clone in saved.OwnedClones.Where(c => c != null))
                if (!saved.OriginalInstanceIds.Contains(clone.GetInstanceID())) Object.DestroyImmediate(clone);
            saved.Global.Vehicles = saved.Vehicles;
            saved.Vehicles.Remove(Vehicle_Type.OlTruck.ToString());
            if (saved.OriginalTruck != null) saved.Vehicles[Vehicle_Type.OlTruck.ToString()] = saved.OriginalTruck;
            foreach (var pair in saved.OriginalTruckActive) if (pair.Key != null) pair.Key.SetActive(pair.Value);
            foreach (var pair in saved.OriginalBodies) if (pair.Key != null)
            {
                pair.Key.position = pair.Value.Position; pair.Key.rotation = pair.Value.Rotation;
                pair.Key.velocity = pair.Value.Velocity; pair.Key.angularVelocity = pair.Value.AngularVelocity;
            }
            saved.Store.VehicleForSale = saved.StoreListings;
            saved.StoreListings.Clear(); saved.StoreListings.AddRange(saved.Listings);
            saved.Display.store = saved.DisplayStore!; saved.Display.price = saved.Price;
            saved.Display.gameObject.SetActive(saved.DisplayActive); saved.Store.isOpen = saved.StoreOpen;
            saved.Game.Get_isHardMode = saved.HardMode; saved.Game.Get_HardModeData = saved.HardData!;
            if (saved.HardData != null) saved.HardData.Bought_OlTruck = saved.BoughtTruck;
            GameManager.Get_PlayerStats = saved.StatsReference; Array.Copy(saved.Stats, saved.StatsReference, saved.Stats.Length);
            Object.FindObjectOfType<Cash_Manager>().LoadSaveSystem(saved.Cash, saved.WeekCash);
            saved.Game.PlayerWalking.transform.SetPositionAndRotation(saved.PlayerPosition, saved.PlayerRotation);
            saved.Game.Get_Player_Rigidbody.position = saved.PlayerPosition; saved.Game.Get_Player_Rigidbody.velocity = saved.PlayerVelocity;
            saved.Game.Get_Player_Rigidbody.angularVelocity = saved.PlayerAngularVelocity;
            var failures = new List<string>();
            if (saved.Display.gameObject.activeSelf != saved.DisplayActive) failures.Add("display active=" + saved.Display.gameObject.activeSelf + " expected=" + saved.DisplayActive);
            if (saved.Display.store != saved.DisplayStore) failures.Add("display store reference");
            if (!saved.StoreListings.SequenceEqual(saved.Listings)) failures.Add("store listing sequence");
            if (saved.Store.isOpen != saved.StoreOpen) failures.Add("store open=" + saved.Store.isOpen + " expected=" + saved.StoreOpen);
            var restoredCash = Object.FindObjectOfType<Cash_Manager>();
            if (restoredCash.GetCash != saved.Cash) failures.Add("cash=" + restoredCash.GetCash + " expected=" + saved.Cash);
            if (!restoredCash.GetWeekCash.SequenceEqual(saved.WeekCash)) failures.Add("weekly cash");
            if (!GameManager.Get_PlayerStats.SequenceEqual(saved.Stats)) failures.Add("stats array values");
            if (saved.PreownedWindows)
            {
                if (saved.OwnedClones.Any(clone => clone != null && !saved.OriginalInstanceIds.Contains(clone.GetInstanceID()))) failures.Add("owned preowned sale clone survived cleanup");
                if (saved.OriginalTruckActive.Any(pair => pair.Key == null || pair.Key.activeSelf != pair.Value)) failures.Add("original truck activation");
                bool hasTruck = saved.Vehicles.TryGetValue(Vehicle_Type.OlTruck.ToString(), out var restoredTruck);
                if (saved.OriginalTruck == null ? hasTruck : !hasTruck || restoredTruck != saved.OriginalTruck) failures.Add("original truck dictionary slot");
            }
            Require(failures.Count == 0, "fixture restoration did not match captured fields: " + string.Join("; ", failures));
            fixture = null;
            Debug.Log("MONMP_PURCHASED_TRUCK_RESTORE: original listing/store-list references, cash/week-cash, stats array, truck dictionary slot and preserved original truck activation restored.");
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Purchased truck doors test: " + message); }
        private static string ListingDiagnostics(VehicleForSale_Display display)
        {
            var prefab = display.RealObjectToBuy;
            var controllers = prefab == null ? Array.Empty<VehicleController>() : prefab.GetComponentsInChildren<VehicleController>(true);
            var vehicle = controllers.Length == 1 ? controllers[0] : null;
            var body = vehicle == null ? null : vehicle.GetComponent<Rigidbody>();
            var parts = vehicle == null ? null : vehicle.GetComponent<Vehicle_PartsManager>();
            var seats = prefab == null ? Array.Empty<Vehicle_DriverSeat>() : prefab.GetComponentsInChildren<Vehicle_DriverSeat>(true);
            var wheels = vehicle?.Wheels;
            return "path=" + DiagnosticPath(display.transform) + "; type=" + display.TypeForSale +
                "; displayActive=" + display.gameObject.activeSelf + "; store=" + (display.store == null ? "null" : display.store.GetType().Name) +
                "; storeOpen=" + (display.store?.isStoreOpen().ToString() ?? "null") + "; parent=" + (display.transform.parent != null) + "; price=" + display.price +
                "; prefab=" + (prefab == null ? "null" : prefab.name) + "; sceneValid=" + (prefab != null && prefab.scene.IsValid()) +
                "; controllers=" + controllers.Length + "; nativeType=" + vehicle?.vehicle_type +
                "; cachedBody=" + (vehicle?.vehicleRigidbody != null) + "; componentBody=" + (body != null) + "; componentKinematic=" + body?.isKinematic +
                "; cachedParts=" + (vehicle?.vehicle_PartsManager != null) + "; componentParts=" + (parts != null) + "; fuel=" + (parts?.Fuel_Tank != null) +
                "; serializedWheels=" + (wheels?.Count.ToString() ?? "null") + "; wheelRefs=" + (wheels == null ? "null" : string.Join(",", wheels.Select(w => w == null ? "null" : (w.wheelController != null).ToString()))) +
                "; seats=" + seats.Length + "; seatBound=" + (seats.Length == 1 && seats[0].vehicleController == vehicle && seats[0].seatPosition != null) +
                "; bodies=" + (prefab == null ? "null" : string.Join(",", prefab.GetComponentsInChildren<Rigidbody>(true).Select(b => b.name + ":kin=" + b.isKinematic + ":joint=" + b.GetComponent<Joint>()?.GetType().Name + ":connected=" + b.GetComponent<Joint>()?.connectedBody?.name))) +
                "; movementPrefabSupported=" + (prefab != null && SharedVehicles.SupportsPurchasedVehicle(display.TypeForSale, prefab, false)) +
                "; trailerPrefabSupported=" + (prefab != null && SharedTrailers.SupportsPurchasedVehicle(display.TypeForSale, prefab, false));
        }
        private static string DiagnosticPath(Transform? transform)
        {
            // RuntimeProbe.CanonicalPath is a scene identity helper: an asset has no scene path.
            // Diagnostic prefab paths need only the observed hierarchy and must never feed IDs.
            if (transform == null) return "missing";
            var segments = new List<string>();
            for (var current = transform; current != null && segments.Count < 64; current = current.parent)
                segments.Add((current.name ?? "unnamed") + "[" + current.GetSiblingIndex() + "]");
            segments.Reverse();
            var scene = transform.gameObject.scene;
            return (scene.IsValid() ? scene.path ?? "unnamed-scene" : "prefab") + "/" + string.Join("/", segments);
        }
    }
}
