using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using NWH.VehiclePhysics2;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // World fuel: every scene fuel tank (vehicles, home generator, wood splitter, speedway storage,
    // boats), filler cap and fuel door. The host's native tanks are authoritative and published on
    // change. A guest pours natively (gas can or pump gun) and reports each liter moved; the host
    // re-applies the transfer against its own source: the canister's level, the storage tank or
    // the shared wallet at the pump's current price.
    internal sealed class SharedFuel : IDisposable
    {
        private sealed class Binding
        {
            public ulong Id;
            public string Identity = "";
            public FuelKind Kind;
            public Part_FuelTank? Tank;
            public Part_FuelTankCap? Cap;
            public Part_FuelDoor? Door;
            public SOUND_Vehicle_FuelDoor? RequiredDoorSound;
            public VehicleController? Vehicle;
            public bool Purchased, OriginalCaptured = true, DeferredNativeApply;
            public FuelState DeferredState, DeferredPrevious;
            public bool DeferredSnap;
            public FuelState State, Original;
            public float BusyUntil, NextSend;
            public bool DeferredTankApply;
            public Vector3 CapTargetPosition;
            public Quaternion CapTargetRotation;
            public bool CapPresentationPending, SawCapPresentationInterpolation;
            public Component Native => Tank != null ? (Component)Tank : Cap != null ? Cap : Door!;
        }
        private static readonly FieldInfo CapScrewed = AccessTools.Field(typeof(Part_FuelTankCap), "Screwed");
        private static readonly FieldInfo CapRunning = AccessTools.Field(typeof(Part_FuelTankCap), "CoroutineRunning");
        private static readonly FieldInfo CapHole = AccessTools.Field(typeof(Part_FuelTankCap), "FuelTankHole");
        private static readonly FieldInfo CapOpenLocation = AccessTools.Field(typeof(Part_FuelTankCap), "CapOpenLocation");
        private static readonly FieldInfo CapUnscrewLocation = AccessTools.Field(typeof(Part_FuelTankCap), "CapUnscrewLocation");
        private static readonly MethodInfo CapVisual = AccessTools.Method(typeof(Part_FuelTankCap), "UpdateVisual");
        private static readonly FieldInfo DoorOpen = AccessTools.Field(typeof(Part_FuelDoor), "isOpen");
        private static readonly MethodInfo DoorVisual = AccessTools.Method(typeof(Part_FuelDoor), "UpdateVisual");
        private static readonly MethodInfo DoorMouseDown = AccessTools.Method(typeof(Part_FuelDoor), "MouseDown");
        private static readonly FieldInfo DoorSound = AccessTools.Field(typeof(Part_FuelDoor), "SOUND_Vehicle_FuelDoor");
        private static readonly FieldInfo CanisterTank = AccessTools.Field(typeof(Tool_FuelCanister), "FuelTank");
        private static readonly FieldInfo GunStorage = AccessTools.Field(typeof(Kaligaz_FuelGun), "fuelStorageTank");
        private static readonly FieldInfo GunDock = AccessTools.Field(typeof(Kaligaz_FuelGun), "IsDockFuelGun");
        private static readonly FieldInfo GunOrdinaire = AccessTools.Field(typeof(Kaligaz_FuelGun), "isOrdinaire");
        // Set only while a native pour coroutine step runs (see FuelPourPatch).
        internal static Component? InPour;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Component, Binding> byNative = new Dictionary<Component, Binding>();
        private readonly Dictionary<Part_FuelTank, Binding> byTank = new Dictionary<Part_FuelTank, Binding>();
        private readonly Dictionary<ulong, (Vehicle_Type Type, GameObject Instance)> purchasedVehicles = new Dictionary<ulong, (Vehicle_Type, GameObject)>();
        private readonly Dictionary<ulong, WorldRecord> deferredPurchasedRecords = new Dictionary<ulong, WorldRecord>();
        private readonly Dictionary<ulong, (Kaligaz_FuelGun Gun, Vector3 Origin)> guns = new Dictionary<ulong, (Kaligaz_FuelGun, Vector3)>();
        private readonly Dictionary<Kaligaz_FuelGun, ulong> gunIds = new Dictionary<Kaligaz_FuelGun, ulong>();
        private readonly Dictionary<Part_FuelTank, float> tankBusy = new Dictionary<Part_FuelTank, float>();
        private readonly Dictionary<(FuelTool, ulong, FuelTarget, ulong), float> pending = new Dictionary<(FuelTool, ulong, FuelTarget, ulong), float>();
        private readonly Dictionary<uint, (float Tokens, float Time)> budgets = new Dictionary<uint, (float, float)>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Gameplay gameplay;
        private readonly SharedItems items;
        private readonly SharedVehicles vehicles;
        private readonly NativeEntityRegistry identities;
        private readonly bool host;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, disposed;
        private float nextCapture, nextFlush, nextPurchasedDiscovery;
        public int Count => bindings.Count;
        private bool Ready => !disposed && session?.IsRunning == true && channel?.Ready == true;
        public SharedFuel(Gameplay gameplay, bool host, SharedItems items, SharedVehicles vehicles, IEnumerable<VehicleController> driven, string diagnostics, NativeEntityRegistry identities)
        {
            this.gameplay = gameplay; this.host = host; this.items = items; this.vehicles = vehicles;
            this.identities = identities;
            var drivenTanks = driven.Where(v => v.vehicle_PartsManager?.Fuel_Tank != null).ToDictionary(v => v.vehicle_PartsManager.Fuel_Tank, v => v);

#if MONMP_DEVELOPMENT
            var lines = new List<string>();
#endif

            void Add(Component native, FuelKind kind, string suffix, float value)
            {
                var door = native as Part_FuelDoor;
                string identity = (native is Part_FuelTankCap stableCap ? CapStablePath(stableCap) : RuntimeProbe.CanonicalPath(native.transform)) + suffix;
                bool sceneValid = native.gameObject.scene.IsValid();
                bool hasItemAncestor = native.GetComponentsInParent<InteractableItems>(true).Length > 0;
                bool saleClone = SharedVehicleSales.Active?.OwnsSaleClone(native.transform) == true;
                // Canisters (and anything else carried) belong to the item system.
                if (!sceneValid || hasItemAncestor || saleClone)
                {
                    if (door != null)
                    {
                        string reason = !sceneValid ? "not-in-loaded-scene" : hasItemAncestor ? "interactable-item-ancestor" : "vehicle-sale-clone";

#if MONMP_DEVELOPMENT
                        lines.Add("0000000000000000\t" + identity + "\tDoorSkipped\t" +
                            FuelDoorDiagnostic(door, sceneValid, hasItemAncestor, saleClone, "supported", reason));
#endif

                    }
                    return;
                }
                ulong id = StaticId.Hash(identity);
                if (bindings.ContainsKey(id))
                {

#if MONMP_DEVELOPMENT
                    lines.Add(id.ToString("X16") + "\t" + identity + "\tduplicate-skipped" +
                        (door != null ? "\t" + FuelDoorDiagnostic(door, sceneValid, hasItemAncestor, saleClone, "supported", "duplicate-static-id") : ""));
#endif

                    return;
                }
                var b = new Binding { Id = id, Identity = identity, Kind = kind, Tank = native as Part_FuelTank,
                    Cap = native as Part_FuelTankCap, Door = door,
                    RequiredDoorSound = door != null ? door.GetComponent<SOUND_Vehicle_FuelDoor>() : null };
                b.State = b.Original = new FuelState(kind, value);
                if (b.Cap != null)
                {
                    b.CapTargetPosition = b.Cap.transform.localPosition;
                    b.CapTargetRotation = b.Cap.transform.localRotation;
                }
                if (b.Tank != null) { byTank.Add(b.Tank, b); if (drivenTanks.TryGetValue(b.Tank, out var v)) b.Vehicle = v; }
                identities.Bind(id, FuelState.Adapter, native.gameObject, identity, true, EntityPersistence.SessionOnly);
                bindings.Add(id, b);
                byNative.Add(native, b);

#if MONMP_DEVELOPMENT
                lines.Add(id.ToString("X16") + "\t" + identity + "\t" + kind + "\t" + value +
                    (b.Vehicle != null ? "\tvehicle=" + b.Vehicle.vehicle_type : "") +
                    (door != null ? "\t" + FuelDoorDiagnostic(door, sceneValid, hasItemAncestor, saleClone, "supported", "eligible") : ""));
#endif

            }
            foreach (var tank in Object.FindObjectsOfType<Part_FuelTank>(true)) Add(tank, FuelKind.Tank, "/fuel-tank-v1", Level(tank));
            foreach (var cap in Object.FindObjectsOfType<Part_FuelTankCap>(true))
            {
                if (!cap.gameObject.scene.IsValid() || cap.GetComponentsInParent<InteractableItems>(true).Length > 0) continue;
                string identity = CapStablePath(cap) + "/fuel-cap-v2";
                if (!HasSupportedFuelCapShape(cap, out var reason))
                {

#if MONMP_DEVELOPMENT
                    lines.Add("0000000000000000\t" + identity + "\tCapSkipped\t" + reason);
#endif

                    continue;
                }
                Add(cap, FuelKind.Cap, "/fuel-cap-v2", (float)CapScrewed.GetValue(cap));
            }
            foreach (var door in Object.FindObjectsOfType<Part_FuelDoor>(true))
            {
                string identity = RuntimeProbe.CanonicalPath(door.transform) + "/fuel-door-v1";
                bool sceneValid = door.gameObject.scene.IsValid();
                bool hasItemAncestor = door.GetComponentsInParent<InteractableItems>(true).Length > 0;
                bool saleClone = SharedVehicleSales.Active?.OwnsSaleClone(door.transform) == true;
                bool supported = HasSupportedFuelDoorShape(door, out var shapeReason);
                if (!sceneValid || hasItemAncestor || saleClone || !supported)
                {
                    string reason = !sceneValid ? "not-in-loaded-scene" : hasItemAncestor ? "interactable-item-ancestor" :
                        saleClone ? "vehicle-sale-clone" : shapeReason;

#if MONMP_DEVELOPMENT
                    lines.Add("0000000000000000\t" + identity + "\tDoorSkipped\t" +
                        FuelDoorDiagnostic(door, sceneValid, hasItemAncestor, saleClone, shapeReason, reason));
#endif

                    continue;
                }
                Add(door, FuelKind.Door, "/fuel-door-v1", (bool)DoorOpen.GetValue(door) ? 1 : 0);
            }
            foreach (var gun in Object.FindObjectsOfType<Kaligaz_FuelGun>(true))
            {
                if (!gun.gameObject.scene.IsValid()) continue;
                string identity = RuntimeProbe.CanonicalPath(gun.transform) + "/fuel-gun-v1";
                ulong id = StaticId.Hash(identity);
                if (guns.ContainsKey(id)) continue;
                guns.Add(id, (gun, gun.transform.position)); gunIds.Add(gun, id);

#if MONMP_DEVELOPMENT
                lines.Add(id.ToString("X16") + "\t" + identity + "\tPump\tstorage=" + (GunStorage.GetValue(gun) as Part_FuelTank != null) + "\tdock=" + GunDock.GetValue(gun));
#endif

            }

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-fuel.tsv"), new[] { "id\tidentity\tkind\tvalue\tdetails" }.Concat(lines.OrderBy(s => s)));
#endif

        }
        // Start caches GetComponent<SOUND_Vehicle_FuelDoor>(); the native Prinz object contains two
        // of those sound components, but only the first selected component is used. Match that
        // selection rather than rejecting a valid authored duplicate. An inactive ancestor can
        // leave the cache null, so visual replay still waits for Start to populate it.
        private static bool HasSupportedFuelDoorShape(Part_FuelDoor door, out string reason)
        {
            if (DoorOpen == null || DoorOpen.DeclaringType != typeof(Part_FuelDoor) || DoorOpen.FieldType != typeof(bool) ||
                !DoorOpen.IsPrivate || DoorOpen.IsStatic || DoorVisual == null || DoorVisual.DeclaringType != typeof(Part_FuelDoor) ||
                DoorVisual.ReturnType != typeof(void) || DoorVisual.GetParameters().Length != 0 || !DoorVisual.IsPrivate || DoorVisual.IsStatic ||
                DoorMouseDown == null || DoorMouseDown.DeclaringType != typeof(Part_FuelDoor) || DoorMouseDown.ReturnType != typeof(void) ||
                DoorMouseDown.GetParameters().Length != 0 || !DoorMouseDown.IsPublic || DoorMouseDown.IsStatic ||
                DoorSound == null || DoorSound.DeclaringType != typeof(Part_FuelDoor) || DoorSound.FieldType != typeof(SOUND_Vehicle_FuelDoor) ||
                !DoorSound.IsPrivate || DoorSound.IsStatic)
            {
                reason = "native-api-shape";
                return false;
            }
            var doors = door.GetComponents<Part_FuelDoor>();
            var requiredSound = door.GetComponent<SOUND_Vehicle_FuelDoor>();
            var cachedSound = DoorSound.GetValue(door) as SOUND_Vehicle_FuelDoor;
            if (doors.Length != 1 || doors[0] != door || requiredSound == null ||
                cachedSound != null && cachedSound != requiredSound)
            {
                reason = "expected-one-door-and-native-selected-sound-on-object";
                return false;
            }
            bool open = (bool)DoorOpen.GetValue(door);
            Vector3 expected = open ? new Vector3(0, -90, 0) : Vector3.zero;
            Vector3 euler = door.transform.localEulerAngles;
            if (Mathf.Abs(Mathf.DeltaAngle(euler.x, expected.x)) > 0.1f ||
                Mathf.Abs(Mathf.DeltaAngle(euler.y, expected.y)) > 0.1f ||
                Mathf.Abs(Mathf.DeltaAngle(euler.z, expected.z)) > 0.1f)
            {
                reason = "native-open-state-pose-mismatch";
                return false;
            }
            reason = "supported";
            return true;
        }

        // Emit the exact live door/component eligibility state so a skipped canonical dormant door
        // can be distinguished from an uninitialized native Start cache, a missing required sound,
        // an item ancestor, or an owned sale clone. This is diagnostic only; the shape validator
        // above remains authoritative and retains its native sound selection and pose checks.

#if MONMP_DEVELOPMENT
        private static string FuelDoorDiagnostic(Part_FuelDoor door, bool sceneValid, bool hasItemAncestor,
            bool saleClone, string shapeReason, string eligibilityReason)
        {
            var doors = door.GetComponents<Part_FuelDoor>();
            var sounds = door.GetComponents<SOUND_Vehicle_FuelDoor>();
            var requiredSound = door.GetComponent<SOUND_Vehicle_FuelDoor>();
            var cachedSound = DoorSound == null ? null : DoorSound.GetValue(door) as SOUND_Vehicle_FuelDoor;
            bool oneDoor = doors.Length == 1 && doors[0] == door;
            string cache = cachedSound == null ? "unset" : requiredSound != null && cachedSound == requiredSound ? "matches-required-component" : "different-component";
            bool nativeReady = requiredSound != null && cachedSound == requiredSound;
            string open = DoorOpen != null && DoorOpen.FieldType == typeof(bool) ? DoorOpen.GetValue(door)?.ToString() ?? "null" : "unavailable";
            string requiredSoundObject = requiredSound != null ? RuntimeProbe.CanonicalPath(requiredSound.transform) : "missing";
            string cachedSoundObject = cachedSound == null ? "unset" : RuntimeProbe.CanonicalPath(cachedSound.transform);
            var vehicle = door.GetComponentInParent<VehicleController>(true);
            string vehicleIdentity = vehicle == null ? "-" : vehicle.vehicle_type + ":" + RuntimeProbe.CanonicalPath(vehicle.transform);
            return "vehicle=" + vehicleIdentity + ";sceneValid=" + sceneValid + ";hasItemAncestor=" + hasItemAncestor +
                ";saleClone=" + saleClone + ";activeSelf=" + door.gameObject.activeSelf +
                ";activeInHierarchy=" + door.gameObject.activeInHierarchy + ";componentEnabled=" + door.enabled +
                ";isActiveAndEnabled=" + door.isActiveAndEnabled + ";doorComponents=" + doors.Length +
                ";sameObjectDoor=" + oneDoor + ";requiredSoundComponents=" + sounds.Length +
                ";requiredSoundObject=" + requiredSoundObject + ";requiredSoundPlacement=same-object" +
                ";cachedSound=" + cache + ";cachedSoundObject=" + cachedSoundObject + ";nativeReady=" + nativeReady +
                ";open=" + open + ";localEuler=" + door.transform.localEulerAngles +
                ";shape=" + shapeReason + ";eligibility=" + eligibilityReason;
        }
#endif

        // The native cap can use an optional unscrew offset, but it always needs a hole object and
        // an open-location transform so UpdateVisual can safely move the cap when fully unscrewed.
        private static bool HasSupportedFuelCapShape(Part_FuelTankCap cap, out string reason)
        {
            if (CapScrewed == null || CapScrewed.DeclaringType != typeof(Part_FuelTankCap) || CapScrewed.FieldType != typeof(float) ||
                !CapScrewed.IsPrivate || CapScrewed.IsStatic || CapRunning == null || CapRunning.DeclaringType != typeof(Part_FuelTankCap) ||
                CapRunning.FieldType != typeof(bool) || !CapRunning.IsPrivate || CapRunning.IsStatic ||
                CapHole == null || CapHole.DeclaringType != typeof(Part_FuelTankCap) || CapHole.FieldType != typeof(GameObject) ||
                !CapHole.IsPrivate || CapHole.IsStatic || CapOpenLocation == null || CapOpenLocation.DeclaringType != typeof(Part_FuelTankCap) ||
                CapOpenLocation.FieldType != typeof(Transform) || !CapOpenLocation.IsPrivate || CapOpenLocation.IsStatic ||
                CapUnscrewLocation == null || CapUnscrewLocation.DeclaringType != typeof(Part_FuelTankCap) ||
                CapUnscrewLocation.FieldType != typeof(Transform) || !CapUnscrewLocation.IsPrivate || CapUnscrewLocation.IsStatic ||
                CapVisual == null || CapVisual.DeclaringType != typeof(Part_FuelTankCap) || CapVisual.ReturnType != typeof(void) ||
                CapVisual.GetParameters().Length != 0 || !CapVisual.IsPrivate || CapVisual.IsStatic)
            {
                reason = "native-api-shape";
                return false;
            }
            if (cap.GetComponents<Part_FuelTankCap>().Length != 1 || CapHole.GetValue(cap) == null || CapOpenLocation.GetValue(cap) == null)
            {
                reason = "missing-or-duplicate-cap-visuals";
                return false;
            }
            float screwed = (float)CapScrewed.GetValue(cap);
            if (float.IsNaN(screwed) || float.IsInfinity(screwed) || screwed < 0 || screwed > 1)
            {
                reason = "invalid-native-screw-state";
                return false;
            }
            var hole = (GameObject)CapHole.GetValue(cap);
            var openLocation = (Transform)CapOpenLocation.GetValue(cap);
            // A cap on a vehicle stored since load (the racecar in its Speedway garage) has not run Start,
            // which hides the prefab's open hole; screwed down and in place, it is a native closed cap.
            if (Unstarted(cap) && screwed == 1 && cap.transform.parent != openLocation) { reason = "supported"; return true; }
            if (screwed == 0 ? cap.transform.parent != openLocation || !hole.activeSelf : cap.transform.parent == openLocation || hole.activeSelf)
            {
                reason = "native-cap-visual-state-mismatch";
                return false;
            }
            reason = "supported";
            return true;
        }
        private static readonly FieldInfo CapOriginalParent = AccessTools.Field(typeof(Part_FuelTankCap), "OriginalParent");
        private static bool Unstarted(Part_FuelTankCap cap) => CapOriginalParent.GetValue(cap) == null;
        // Native UpdateVisual moves a fully unscrewed cap to CapOpenLocation and returns it as the
        // last child, so its live path differs from a fresh copy's and the world catalog would refuse
        // every guest. Its home and name are the same on every machine, open or closed.
        internal static Transform CapHome(Part_FuelTankCap cap) => CapOriginalParent.GetValue(cap) as Transform ?? cap.transform.parent;
        private static string CapStablePath(Part_FuelTankCap cap) =>
            RuntimeProbe.CanonicalPath(CapHome(cap)) + "/" + StaticId.Segment(cap.gameObject.name, 0);
        private static float Level(Part_FuelTank tank) => Mathf.Clamp(tank.Get_FuelLevel, 0, FuelState.MaxLevel);
        internal IEnumerable<ulong> RequiredCatalogEntityIds => bindings.Values
            .Where(b => !b.Purchased && b.Id > WorldRecord.DynamicIdStart).Select(b => b.Id)
            .Concat(FuelState.Station > WorldRecord.DynamicIdStart ? new[] { FuelState.Station } : Array.Empty<ulong>());
        internal bool IsPurchasedRecord(ulong id) => bindings.TryGetValue(id, out var binding) && binding.Purchased;
        public IEnumerable<WorldRecord> InitialRecords()
        {
            DiscoverPurchasedVehicles();
            return bindings.Values.Where(b => host || !b.Purchased)
                .Select(b => new WorldRecord(b.Id, 1, FuelState.Adapter, b.State.Encode()))
                .Append(new WorldRecord(FuelState.Station, 1, FuelState.Adapter, new FuelState(FuelKind.Station, 0).Encode()));
        }
        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            DiscoverPurchasedVehicles();
            foreach (var binding in bindings.Values.Where(b => b.Purchased).ToArray()) HydratePurchasedRecord(binding);
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.CommandRequested += Command; channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) { presence.Remove(peer); budgets.Remove(peer); }

        // Purchased fuel caps and doors are optional dynamic records. Tanks stay on their
        // existing vehicle-fuel route, so this registration deliberately adds no tank binding.
        internal bool RegisterPurchasedVehicle(ulong entity, Vehicle_Type type, GameObject instance)
        {
            if (!PurchasedVehicleFuelIdentityPolicy.Supports(type)) return false;
            if (entity <= WorldRecord.DynamicIdStart) throw new InvalidOperationException("Purchased vehicle fuel entity is not dynamic.");
            if (instance == null || !instance.scene.IsValid()) throw new InvalidOperationException("Purchased vehicle fuel instance is outside the active scene.");
            if (purchasedVehicles.TryGetValue(entity, out var existing))
            {
                if (existing.Type != type || existing.Instance != instance)
                    throw new InvalidOperationException("Conflicting purchased vehicle fuel binding for entity " + entity.ToString("X16") + ".");
            }
            else
            {
                var alias = purchasedVehicles.FirstOrDefault(pair => pair.Value.Instance == instance);
                if (alias.Value.Instance != null && alias.Key != entity)
                    throw new InvalidOperationException("Purchased vehicle fuel instance already has a different sale identity.");
                purchasedVehicles.Add(entity, (type, instance));
            }
            RegisterPurchasedFuelControls(entity, type, instance);
            return true;
        }

        private void DiscoverPurchasedVehicles()
        {
            foreach (var pair in purchasedVehicles.ToArray())
            {
                if (pair.Value.Instance == null || !pair.Value.Instance.scene.IsValid()) continue;
                RegisterPurchasedFuelControls(pair.Key, pair.Value.Type, pair.Value.Instance);
            }
        }

        private void RegisterPurchasedFuelControls(ulong entity, Vehicle_Type type, GameObject instance)
        {
            foreach (var cap in instance.GetComponentsInChildren<Part_FuelTankCap>(true))
            {
                if (cap == null || !cap.gameObject.scene.IsValid() || cap.GetComponentsInParent<InteractableItems>(true).Length > 0 ||
                    !HasSupportedFuelCapShape(cap, out _)) continue;
                if (!TryPurchasedRelativePath(instance.transform, cap, out var path)) continue;
                RegisterPurchasedControl(entity, type, cap, FuelKind.Cap, path, (float)CapScrewed.GetValue(cap));
            }
            foreach (var door in instance.GetComponentsInChildren<Part_FuelDoor>(true))
            {
                if (door == null || !door.gameObject.scene.IsValid() || door.GetComponentsInParent<InteractableItems>(true).Length > 0 ||
                    !HasSupportedFuelDoorShape(door, out _)) continue;
                if (!TryPurchasedRelativePath(instance.transform, door, out var path)) continue;
                RegisterPurchasedControl(entity, type, door, FuelKind.Door, path, (bool)DoorOpen.GetValue(door) ? 1 : 0);
            }
        }

        private static bool TryPurchasedRelativePath(Transform vehicleRoot, Component native, out string path)
        {
            Transform target = native.transform;
            if (native is Part_FuelTankCap cap)
            {
                // Use the authored tank path rather than the cap's current parent. Native
                // UpdateVisual reparents a fully unscrewed cap to CapOpenLocation.
                var authoredParent = CapOriginalParent.GetValue(cap) as Transform;
                var stableParent = authoredParent ?? cap.transform.parent;
                if (stableParent == null || stableParent.GetComponentInParent<Part_FuelTank>() == null ||
                    !TryRelativePath(vehicleRoot, stableParent, out var parentPath)) { path = ""; return false; }
                // The saved OriginalParent survives native open/close reparenting; the control's
                // name distinguishes it from other controls beneath that authored parent.
                path = parentPath + "/" + StaticId.Segment(cap.gameObject.name, 0);
                return true;
            }
            return TryRelativePath(vehicleRoot, target, out path);
        }

        private static bool TryRelativePath(Transform root, Transform target, out string path)
        {
            if (root == target) { path = "root"; return true; }
            var segments = new List<string>();
            var current = target;
            while (current != root)
            {
                if (current == null) { path = ""; return false; }
                segments.Add(StaticId.Segment(current.name, current.GetSiblingIndex()));
                current = current.parent;
            }
            segments.Reverse();
            path = string.Join("/", segments);
            return true;
        }

        private void RegisterPurchasedControl(ulong saleEntity, Vehicle_Type type, Component native, FuelKind kind, string relativePath, float value)
        {
            string identity = PurchasedVehicleFuelIdentityPolicy.Identity(saleEntity, type, relativePath, kind);
            ulong id = PurchasedVehicleFuelIdentityPolicy.EntityId(saleEntity, type, relativePath, kind);
            if (byNative.TryGetValue(native, out var nativeBinding))
            {
                if (nativeBinding.Id != id || !nativeBinding.Purchased)
                    throw new InvalidOperationException("Purchased fuel control already has another identity: " + native.name);
                return;
            }
            if (bindings.TryGetValue(id, out var existing))
            {
                if (existing.Native != native || !existing.Purchased)
                    throw new InvalidOperationException("Purchased fuel identity collision: " + identity);
                return;
            }
            var door = native as Part_FuelDoor;
            var binding = new Binding
            {
                Id = id, Identity = identity, Kind = kind, Cap = native as Part_FuelTankCap, Door = door,
                RequiredDoorSound = door != null ? door.GetComponent<SOUND_Vehicle_FuelDoor>() : null,
                Purchased = true
            };
            binding.State = binding.Original = new FuelState(kind, value);
            binding.OriginalCaptured = host || PurchasedNativeReady(binding);
            if (binding.Cap != null)
            {
                binding.CapTargetPosition = binding.Cap.transform.localPosition;
                binding.CapTargetRotation = binding.Cap.transform.localRotation;
            }
            identities.Bind(id, FuelState.Adapter, native.gameObject, identity, true, EntityPersistence.SessionOnly);
            bindings.Add(id, binding);
            byNative.Add(native, binding);
            if (channel == null) return;
            if (host)
            {
                if (channel.Records.TryGetValue(id, out var retained)) Apply(retained, true);
                else channel.Register(new WorldRecord(id, 1, FuelState.Adapter, binding.State.Encode()));
            }
            else HydratePurchasedRecord(binding);
        }

        private void HydratePurchasedRecord(Binding binding)
        {
            if (channel == null || host) return;
            if (!channel.Records.TryGetValue(binding.Id, out var retained) &&
                !deferredPurchasedRecords.TryGetValue(binding.Id, out retained)) return;
            if (retained.Adapter != FuelState.Adapter) throw new InvalidDataException("Purchased fuel record adapter changed.");
            Apply(retained, true);
            deferredPurchasedRecords.Remove(binding.Id);
        }

        private static bool DoorNativeReady(Binding binding) => binding.Door != null && binding.RequiredDoorSound != null &&
            (DoorSound.GetValue(binding.Door) as SOUND_Vehicle_FuelDoor) == binding.RequiredDoorSound;

        private static bool PurchasedNativeReady(Binding binding) => binding.Cap != null
            ? !Unstarted(binding.Cap)
            : binding.Door != null && DoorNativeReady(binding);

        // ---- Guest: native pour reporting -------------------------------------------------------
        // Called from the Part_FuelTank level setter while a native pour step runs.
        internal void LevelChanged(Part_FuelTank tank, float before, float after)
        {
            if (applying || !Ready || InPour == null) return;
            if (host)
            {
                if (after > before && byTank.TryGetValue(tank, out var filled) && filled.Vehicle != null)
                    vehicles.RecordRefuel(filled.Vehicle, after - before);
                return;
            }
            float now = Time.unscaledTime;
            tankBusy[tank] = now + 1.2f;
            FuelTool tool; ulong toolId;
            if (InPour is Tool_FuelCanister canister)
            {
                if (tank == CanisterTank.GetValue(canister) as Part_FuelTank) return; // the source; the host drains its own copy
                tool = FuelTool.Canister; toolId = items.EntityOf(canister);
            }
            else if (InPour is Kaligaz_FuelGun gun)
            {
                if (tank == GunStorage.GetValue(gun) as Part_FuelTank) return;
                tool = FuelTool.Pump; gunIds.TryGetValue(gun, out toolId);
            }
            else return;
            float delta = after - before;
            if (toolId == 0 || delta <= 0) return;
            FuelTarget target; ulong targetId;
            if (byTank.TryGetValue(tank, out var b)) { target = FuelTarget.Tank; targetId = b.Id; b.BusyUntil = now + 1.2f; }
            else
            {
                var can = tank.GetComponentInParent<Tool_FuelCanister>();
                targetId = can != null ? items.EntityOf(can) : 0; target = FuelTarget.Item;
                if (targetId == 0) return;
            }
            var key = (tool, toolId, target, targetId);
            pending[key] = (pending.TryGetValue(key, out float sum) ? sum : 0) + delta;
        }
        // True while this machine is mid-pour into or out of that tank: incoming echoes lag behind.
        internal bool Busy(Part_FuelTank? tank) => tank != null && tankBusy.TryGetValue(tank, out float until) && Time.unscaledTime < until;
        internal bool Busy(Tool_FuelCanister canister) => Busy(CanisterTank.GetValue(canister) as Part_FuelTank);
        private void Flush()
        {
            foreach (var pair in pending)
            {
                float amount = pair.Value;
                while (amount >= 0.001f)
                {
                    float chunk = Mathf.Min(amount, FuelTransfer.MaxAmount); amount -= chunk;
                    var k = pair.Key;
                    channel!.Request(FuelState.Station, new FuelTransfer(k.Item1, k.Item2, k.Item3, k.Item4, chunk).Encode(), true);
                }
            }
            pending.Clear();
        }

        // ---- Host: authoritative application ---------------------------------------------------
        private bool PeerPosition(uint peer, out Vector3 position)
        {
            position = default;
            if (peer == 0) return false;
            if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2 || p.Pose.State != (byte)PlayerState.Walking) return false;
            position = new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z); return true;
        }
        [MonMP.Core.World.WorldCommandAdapters(FuelState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0) return;
            if (id == FuelState.Station) { Transfer(peer, FuelTransfer.Decode(payload)); return; }
            if (!bindings.TryGetValue(id, out var b) || b.Kind == FuelKind.Tank) return;
            var state = FuelState.Decode(payload);
            if (state.Kind != b.Kind || !PeerPosition(peer, out var at) || (at - b.Native.transform.position).sqrMagnitude > 25) return;
            if (b.Cap != null)
            {
                // The host watches the guest's hand turn the cap, not a snap to each 0.25 s sample.
                if ((bool)CapRunning.GetValue(b.Cap)) return;
                ApplyCap(b, b.State, state, false);
            }
            else
            {
                applying = true;
                try { ApplyNative(b, state); }
                finally { applying = false; }
            }
            Publish(b, state);
        }
        private void Transfer(uint peer, FuelTransfer t)
        {
            if (!PeerPosition(peer, out var at)) return;
            // Faster than any native pour (pump 2 L/s, can 1.3 L/s), with slack for batching.
            float now = Time.unscaledTime;
            var budget = budgets.TryGetValue(peer, out var existing) ? existing : (Tokens: 10f, Time: now);
            budget.Tokens = Mathf.Min(10, budget.Tokens + (now - budget.Time) * 3); budget.Time = now;
            float amount = Mathf.Min(t.Amount, budget.Tokens);
            // Target room first, exactly like the native pour caps AmountToMove before paying.
            Part_FuelTank? targetTank = null; Tool_FuelCanister? targetCan = null; Vector3 targetAt;
            if (t.Target == FuelTarget.Tank)
            {
                if (!bindings.TryGetValue(t.TargetId, out var b) || b.Tank == null) return;
                targetTank = b.Tank; targetAt = b.Tank.transform.position;
                amount = Mathf.Min(amount, targetTank.Get_FuelCapacity - targetTank.Get_FuelLevel);
            }
            else
            {
                targetCan = items.ItemOf(t.TargetId) as Tool_FuelCanister;
                if (targetCan == null || !items.FreeOrOwnedBy(t.TargetId, peer)) return;
                targetAt = targetCan.transform.position;
                amount = Mathf.Min(amount, targetCan.Value1_Max - targetCan.Value1);
            }
            if ((targetAt - at).sqrMagnitude > 36) return;
            Tool_FuelCanister? sourceCan = null; Part_FuelTank? storage = null; float price = 0;
            if (t.Tool == FuelTool.Canister)
            {
                sourceCan = items.ItemOf(t.ToolId) as Tool_FuelCanister;
                if (sourceCan == null || sourceCan == targetCan || !items.OwnedBy(t.ToolId, peer)) return;
                amount = Mathf.Min(amount, sourceCan.Value1);
            }
            else
            {
                if (!guns.TryGetValue(t.ToolId, out var g) || g.Gun == null || (bool)GunOrdinaire.GetValue(g.Gun)) return;
                bool dock = (bool)GunDock.GetValue(g.Gun);
                if ((at - g.Origin).sqrMagnitude > (dock ? 15 * 15 : 7 * 7)) return;
                storage = GunStorage.GetValue(g.Gun) as Part_FuelTank;
                if (storage != null) { if (storage == targetTank) return; amount = Mathf.Min(amount, storage.Get_FuelLevel); }
                else
                {
                    price = dock ? Store_KaliGas.FuelPrice_Super_Dock : Store_KaliGas.FuelPrice_Super;
                    var cash = Singleton<Cash_Manager>.i;
                    if (price <= 0 || cash.GetCash <= 0) return;
                    amount = Mathf.Min(amount, cash.GetCash / 100f / price);
                }
            }
            if (amount < 0.0005f) return;
            budget.Tokens -= amount; budgets[peer] = budget;
            applying = true;
            try
            {
                if (sourceCan != null) sourceCan.Value1 = Mathf.Clamp(sourceCan.Value1 - amount, 0, sourceCan.Value1_Max);
                else if (storage != null) storage.Get_FuelLevel -= amount;
                else Singleton<Cash_Manager>.i.Event_Spent(amount * price, false);
                if (targetTank != null)
                {
                    float before = targetTank.Get_FuelLevel;
                    targetTank.Get_FuelLevel += amount;
                    if (byTank.TryGetValue(targetTank, out var filled) && filled.Vehicle != null)
                        vehicles.RecordRefuel(filled.Vehicle, targetTank.Get_FuelLevel - before);
                }
                else targetCan!.Value1 = Mathf.Clamp(targetCan.Value1 + amount, 0, targetCan.Value1_Max);
                // The guest proxy replaces Kaligaz_FuelGun's native transfer body,
                // including its litres-pumped statistic (storage guns count too).
                if (t.Tool == FuelTool.Pump) GameManager.Get_PlayerStats[6] += amount;
            }
            finally { applying = false; }
            // Tank records publish on the next capture; canisters with the item loop; wallet with economy.
            nextCapture = 0;
        }
        private void Publish(Binding b, FuelState state) { b.State = state; channel!.Publish(b.Id, state.Encode()); }
        private FuelState Capture(Binding b) => b.Kind switch
        {
            FuelKind.Tank => new FuelState(FuelKind.Tank, Level(b.Tank!)),
            FuelKind.Cap => new FuelState(FuelKind.Cap, Mathf.Clamp01((float)CapScrewed.GetValue(b.Cap))),
            _ => new FuelState(FuelKind.Door, (bool)DoorOpen.GetValue(b.Door) ? 1 : 0)
        };

        // ---- Both: applying records to native objects ------------------------------------------
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values.Where(r => r.Adapter == FuelState.Adapter && r.Id != FuelState.Station).ToArray())
            {
                if (bindings.ContainsKey(record.Id)) Apply(record, true);
                else if (record.Id > WorldRecord.DynamicIdStart) deferredPurchasedRecords[record.Id] = record;
                else throw new InvalidDataException("Unknown fuel record.");
            }
        }
        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != FuelState.Adapter || record.Id == FuelState.Station) return;
            if (bindings.ContainsKey(record.Id)) Apply(record, false);
            else if (record.Id > WorldRecord.DynamicIdStart) deferredPurchasedRecords[record.Id] = record;
            else throw new InvalidDataException("Unknown fuel record.");
        }
        private void Apply(WorldRecord record, bool snap)
        {
            if (!bindings.TryGetValue(record.Id, out var b)) throw new InvalidDataException("Unknown fuel record.");
            var state = FuelState.Decode(record.Payload);
            if (state.Kind != b.Kind) throw new InvalidDataException("Fuel record kind mismatch.");
            var previous = b.State;
            b.State = state;
            if (b.Purchased && !b.OriginalCaptured)
            {
                DeferNativeApply(b, previous, state, snap);
                return;
            }
            if (Time.unscaledTime < b.BusyUntil || Busy(b.Tank))
            {
                if (b.Kind == FuelKind.Tank) b.DeferredTankApply = true;
                return;
            }
            // A vehicle this guest drives burns its own fuel; VehicleState carries it to the host.
            if (b.Vehicle != null && vehicles.DrivenBy(b.Vehicle) == session?.LocalPeerId) return;
            if (b.Cap != null && (bool)CapRunning.GetValue(b.Cap)) return;
            if (b.Cap != null)
            {
                ApplyCap(b, previous, state, snap);
                return;
            }
            applying = true;
            try { ApplyNative(b, state); b.DeferredTankApply = false; }
            finally { applying = false; }
        }
        private void ResolveDeferredTanks()
        {
            foreach (var b in bindings.Values)
            {
                if (!b.DeferredTankApply || b.Tank == null || Time.unscaledTime < b.BusyUntil || Busy(b.Tank)) continue;
                if (b.Vehicle != null && vehicles.DrivenBy(b.Vehicle) == session?.LocalPeerId) continue;
                applying = true;
                try { ApplyNative(b, b.State); b.DeferredTankApply = false; }
                finally { applying = false; }
            }
        }
        private void DeferNativeApply(Binding binding, FuelState previous, FuelState state, bool snap)
        {
            binding.DeferredNativeApply = true;
            binding.DeferredState = state;
            binding.DeferredPrevious = previous;
            binding.DeferredSnap = snap;
        }
        private void ResolveDeferredNativeApplies()
        {
            foreach (var b in bindings.Values)
            {
                if (!b.DeferredNativeApply || b.Native == null) continue;
                if (!b.OriginalCaptured)
                {
                    if (!PurchasedNativeReady(b)) continue;
                    b.Original = Capture(b);
                    b.OriginalCaptured = true;
                }
                if (b.Door != null && !DoorNativeReady(b) || b.Cap != null && b.Purchased && Unstarted(b.Cap)) continue;
                var state = b.DeferredState;
                if (b.Cap != null)
                {
                    b.DeferredNativeApply = false;
                    ApplyCap(b, b.DeferredPrevious, state, b.DeferredSnap);
                    continue;
                }
                applying = true;
                try
                {
                    if (b.Door != null) ResolveDeferredDoor(b);
                    else { b.DeferredNativeApply = false; ApplyNative(b, state); }
                }
                finally { applying = false; }
            }
        }
        private void ApplyCap(Binding b, FuelState previous, FuelState state, bool snap)
        {
            if (b.Purchased && !b.OriginalCaptured)
            {
                DeferNativeApply(b, previous, state, snap);
                return;
            }
            var cap = b.Cap!;
            var beforeWorldPosition = cap.transform.position;
            var beforeWorldRotation = cap.transform.rotation;
            applying = true;
            try { ApplyNative(b, state); }
            finally { applying = false; }

            var transform = cap.transform;
            var targetPosition = transform.localPosition;
            var targetRotation = transform.localRotation;
            b.CapTargetPosition = targetPosition;
            if (snap)
            {
                b.CapTargetRotation = targetRotation;
                transform.localPosition = targetPosition;
                transform.localRotation = targetRotation;
                b.CapPresentationPending = false;
                return;
            }
            if (state.Value <= 0 || previous.Value <= 0)
            {
                // The native visual reparents a fully unscrewed cap. Its open endpoint is authored
                // at identity under CapOpenLocation; when screwing it back in, UpdateVisual restores
                // the saved native rotation before the remaining screw travel begins.
                b.CapTargetRotation = targetRotation;
            }
            else
            {
                // ScrewUnscrew rotates about the cap's local up axis at 300 degrees per second while
                // Screwed changes at 0.5 units per second: 600 degrees for a full screw travel.
                // FuelState carries only Screwed, so derive the matching host rotation from its delta.
                float degrees = (state.Value - previous.Value) * 600f;
                b.CapTargetRotation = b.CapTargetRotation * Quaternion.Euler(0, degrees, 0);
            }

            // Convert the previous world pose into the native target parent so an endpoint reparent
            // remains visually continuous, then let the guest follow the native screw path smoothly.
            var parent = transform.parent;
            transform.localPosition = parent != null ? parent.InverseTransformPoint(beforeWorldPosition) : beforeWorldPosition;
            transform.localRotation = parent != null ? Quaternion.Inverse(parent.rotation) * beforeWorldRotation : beforeWorldRotation;
            b.CapPresentationPending =
                Vector3.Distance(transform.localPosition, b.CapTargetPosition) > VehiclePresentationSmoothing.PositionSettleDistance ||
                Quaternion.Angle(transform.localRotation, b.CapTargetRotation) > VehiclePresentationSmoothing.RotationSettleDegrees;
        }

        private static void PresentCap(Binding b, float blend)
        {
            if (!b.CapPresentationPending || b.Cap == null) return;
            if ((bool)CapRunning.GetValue(b.Cap))
            {
                // A local native unscrew coroutine owns the transform while this player is using it.
                b.CapPresentationPending = false;
                b.CapTargetPosition = b.Cap.transform.localPosition;
                b.CapTargetRotation = b.Cap.transform.localRotation;
                return;
            }
            var transform = b.Cap.transform;
            Vector3 nextPosition = VehiclePresentationSmoothing.Position(transform.localPosition, b.CapTargetPosition, blend);
            Quaternion nextRotation = VehiclePresentationSmoothing.Rotation(transform.localRotation, b.CapTargetRotation, blend);
            bool intermediate = Vector3.Distance(nextPosition, b.CapTargetPosition) > VehiclePresentationSmoothing.PositionSettleDistance ||
                Quaternion.Angle(nextRotation, b.CapTargetRotation) > VehiclePresentationSmoothing.RotationSettleDegrees;
            if (intermediate && (Vector3.Distance(nextPosition, transform.localPosition) > 0.0001f ||
                Quaternion.Angle(nextRotation, transform.localRotation) > 0.01f)) b.SawCapPresentationInterpolation = true;
            transform.localPosition = nextPosition;
            transform.localRotation = nextRotation;
            b.CapPresentationPending = intermediate;
        }
        private static void ApplyNative(Binding b, FuelState state)
        {
            switch (b.Kind)
            {
                case FuelKind.Tank:
                    if (Mathf.Abs(b.Tank!.Get_FuelLevel - state.Value) > 0.0005f) b.Tank.Get_FuelLevel = state.Value;
                    break;
                case FuelKind.Cap:
                    float current = (float)CapScrewed.GetValue(b.Cap);
                    if (FuelState.CapTravelMatches(current, state.Value, 0.0005f)) break;
                    CapScrewed.SetValue(b.Cap, state.Value);
                    // UpdateVisual needs Start's captured positions; an unstarted cap takes the value alone.
                    if (Unstarted(b.Cap!)) break;
                    // UpdateVisual re-parents the cap when it reaches fully open; do that once.
                    if (state.Value > 0 || !((GameObject)CapHole.GetValue(b.Cap)).activeInHierarchy) CapVisual.Invoke(b.Cap, null);
                    break;
                case FuelKind.Door:
                    ApplyDoorState(b, state);
                    break;
            }
        }
        private static bool ApplyDoorState(Binding b, FuelState state)
        {
            if (b.Purchased && !b.OriginalCaptured || !DoorNativeReady(b))
            {
                DeferDoorState(b, state);
                return false;
            }
            bool open = state.Value == 1;
            if ((bool)DoorOpen.GetValue(b.Door) != open)
            {
                DoorOpen.SetValue(b.Door, open);
                DoorVisual.Invoke(b.Door, null);
            }
            b.DeferredNativeApply = false;
            return true;
        }
        private static void DeferDoorState(Binding b, FuelState state)
        {
            b.DeferredNativeApply = true;
            b.DeferredState = state;
            b.DeferredSnap = true;
        }
        private static bool ResolveDeferredDoor(Binding binding)
        {
            if (!binding.DeferredNativeApply || binding.Door == null || !binding.OriginalCaptured || !DoorNativeReady(binding)) return false;
            var state = binding.DeferredState;
            binding.DeferredNativeApply = false;
            return ApplyDoorState(binding, state);
        }

        public void Update()
        {
            if (!Ready) return;
            float now = Time.unscaledTime;
            if (now >= nextPurchasedDiscovery)
            {
                nextPurchasedDiscovery = now + 1f;
                DiscoverPurchasedVehicles();
            }
            ResolveDeferredNativeApplies();
            if (host)
            {
                float hostBlend = VehiclePresentationSmoothing.Blend;
                foreach (var b in bindings.Values) PresentCap(b, hostBlend);
                if (now < nextCapture) return;
                nextCapture = now + 0.2f;
                foreach (var b in bindings.Values)
                {
                    if (b.Native == null || b.DeferredNativeApply) continue;
                    var state = Capture(b);
                    if (!state.Equals(b.State) || (b.Kind == FuelKind.Tank && Mathf.Abs(state.Value - b.State.Value) > 0.0005f)) Publish(b, state);
                }
                return;
            }
            float blend = VehiclePresentationSmoothing.Blend;
            ResolveDeferredTanks();
            foreach (var b in bindings.Values) PresentCap(b, blend);
            if (now >= nextFlush) { nextFlush = now + 0.2f; Flush(); }
            // Caps and doors the guest turned natively: ask the host to adopt the new position.
            foreach (var b in bindings.Values)
            {
                if (b.Kind == FuelKind.Tank || b.Native == null || b.DeferredNativeApply || now < b.NextSend) continue;
                var local = Capture(b);
                if (local.Equals(b.State)) continue;
                if (b.Cap != null)
                {
                    b.CapPresentationPending = false;
                    b.CapTargetPosition = b.Cap.transform.localPosition;
                    b.CapTargetRotation = b.Cap.transform.localRotation;
                }
                if (b.Cap != null && (bool)CapRunning.GetValue(b.Cap) && now < b.BusyUntil - 0.9f) continue;
                b.NextSend = now + 0.25f; b.BusyUntil = now + 1.2f;
                channel!.Request(b.Id, local.Encode(), true);
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.CommandRequested -= Command; channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            if (!host)
            {
                applying = true;
                try
                {
                    foreach (var b in bindings.Values)
                        if (b.Native != null && b.Vehicle == null && b.OriginalCaptured &&
                            (b.Door == null || DoorNativeReady(b))) ApplyNative(b, b.Original);
                }
                finally { applying = false; }
            }
            InPour = null; session = null; channel = null;
        }

        // ---- Test hooks ------------------------------------------------------------------------

#if MONMP_DEVELOPMENT
        internal ulong TestTankOf(VehicleController vehicle) => byTank.TryGetValue(vehicle.vehicle_PartsManager.Fuel_Tank, out var b) ? b.Id : 0;


        internal FuelState TestState(ulong id) => bindings[id].State;


        internal Part_FuelTank TestTank(ulong id) => bindings[id].Tank!;


        internal Part_FuelTankCap? TestCapNear(Vector3 position) => bindings.Values.Where(b => b.Cap != null)
            .OrderBy(b => (b.Cap!.transform.position - position).sqrMagnitude).Select(b => b.Cap).FirstOrDefault();


        internal Part_FuelDoor[] TestVehicleFuelDoorCandidates(VehicleController vehicle) => vehicle.GetComponentsInChildren<Part_FuelDoor>(true)
            .Where(door => door != null && door.GetComponentInParent<VehicleController>(true) == vehicle).ToArray();


        internal Part_FuelDoor[] TestVehicleFuelDoors(VehicleController vehicle) => bindings.Values.Where(b => b.Door != null &&
                b.Door.GetComponentInParent<VehicleController>(true) == vehicle)
            .Select(b => b.Door!).ToArray();


        internal string TestVehicleFuelDoorDiagnostics(VehicleController vehicle)
        {
            var candidates = TestVehicleFuelDoorCandidates(vehicle);
            if (candidates.Length == 0)
                return "vehicle=" + vehicle.vehicle_type + ":" + RuntimeProbe.CanonicalPath(vehicle.transform) + ";doorCandidates=0;sharedDoors=0";
            return string.Join(" | ", candidates.Select(door =>
            {
                bool sceneValid = door.gameObject.scene.IsValid();
                bool hasItemAncestor = door.GetComponentsInParent<InteractableItems>(true).Length > 0;
                bool saleClone = SharedVehicleSales.Active?.OwnsSaleClone(door.transform) == true;
                HasSupportedFuelDoorShape(door, out var shapeReason);
                ulong id = TestIdOf(door);
                return RuntimeProbe.CanonicalPath(door.transform) + "/fuel-door-v1;sharedId=" +
                    (id == 0 ? "unbound" : id.ToString("X16")) + ";" +
                    FuelDoorDiagnostic(door, sceneValid, hasItemAncestor, saleClone, shapeReason,
                        id == 0 ? "not-in-static-binding" : "bound");
            }));
        }


        internal Part_FuelTankCap[] TestVehicleFuelCaps(VehicleController vehicle) => bindings.Values.Where(b => b.Cap != null && b.Cap.GetComponentInParent<VehicleController>() == vehicle)
            .Select(b => b.Cap!).ToArray();


        internal bool TestDoorOpen(Part_FuelDoor door) => (bool)DoorOpen.GetValue(door);


        internal bool TestDoorNativeReady(Part_FuelDoor door)
        {
            var requiredSound = door.GetComponent<SOUND_Vehicle_FuelDoor>();
            var cachedSound = DoorSound.GetValue(door) as SOUND_Vehicle_FuelDoor;
            return requiredSound != null && cachedSound == requiredSound;
        }


        internal bool TestDoorPoseMatchesNativeState(Part_FuelDoor door)
        {
            bool open = TestDoorOpen(door);
            Vector3 expected = open ? new Vector3(0, -90, 0) : Vector3.zero;
            Vector3 euler = door.transform.localEulerAngles;
            return Mathf.Abs(Mathf.DeltaAngle(euler.x, expected.x)) <= 0.1f &&
                Mathf.Abs(Mathf.DeltaAngle(euler.y, expected.y)) <= 0.1f &&
                Mathf.Abs(Mathf.DeltaAngle(euler.z, expected.z)) <= 0.1f;
        }


        internal string TestDoorDiagnostic(Part_FuelDoor door)
        {
            bool sceneValid = door.gameObject.scene.IsValid();
            bool hasItemAncestor = door.GetComponentsInParent<InteractableItems>(true).Length > 0;
            bool saleClone = SharedVehicleSales.Active?.OwnsSaleClone(door.transform) == true;
            HasSupportedFuelDoorShape(door, out var shapeReason);
            ulong id = TestIdOf(door);
            return RuntimeProbe.CanonicalPath(door.transform) + "/fuel-door-v1;sharedId=" +
                (id == 0 ? "unbound" : id.ToString("X16")) + ";" +
                FuelDoorDiagnostic(door, sceneValid, hasItemAncestor, saleClone, shapeReason,
                    id == 0 ? "not-in-static-binding" : "bound");
        }


        internal bool TestOriginalDoorOpen(Part_FuelDoor door) => bindings.Values.Single(b => b.Door == door).Original.Value == 1;


        internal ulong TestIdOf(Component native) => bindings.Values.FirstOrDefault(b => b.Native == native)?.Id ?? 0;


        internal (Kaligaz_FuelGun Gun, ulong Id)? TestPaidGun => guns.OrderBy(p => p.Key).Where(p => p.Value.Gun != null && GunStorage.GetValue(p.Value.Gun) == null &&
            !(bool)GunOrdinaire.GetValue(p.Value.Gun) && !(bool)GunDock.GetValue(p.Value.Gun)).Select(p => ((Kaligaz_FuelGun, ulong)?)(p.Value.Gun, p.Key)).FirstOrDefault();


        internal void TestSetCap(Part_FuelTankCap cap, float value) { CapScrewed.SetValue(cap, value); if (value > 0 || !((GameObject)CapHole.GetValue(cap)).activeInHierarchy) CapVisual.Invoke(cap, null); }


        internal Part_FuelTank TestCanisterTank(Tool_FuelCanister can) => (Part_FuelTank)CanisterTank.GetValue(can);


        internal Vector3 TestGunOrigin(ulong id) => guns[id].Origin;


        internal float TestCap(Part_FuelTankCap cap) => (float)CapScrewed.GetValue(cap);


        internal float TestOriginalCap(Part_FuelTankCap cap) => bindings.Values.Single(b => b.Cap == cap).Original.Value;


        internal bool TestSawCapPresentationInterpolation(Part_FuelTankCap cap) => bindings.Values.Single(b => b.Cap == cap).SawCapPresentationInterpolation;


        internal void TestResetCapPresentationObservation(Part_FuelTankCap cap) => bindings.Values.Single(b => b.Cap == cap).SawCapPresentationInterpolation = false;


        internal void TestRequestDoorState(Part_FuelDoor door, bool open)
        {
            var binding = bindings.Values.Single(b => b.Door == door);
            channel!.Request(binding.Id, new FuelState(FuelKind.Door, open ? 1 : 0).Encode(), true);
        }
#endif

        // Replays exactly what one native pour step does, with the coroutine marker set.

#if MONMP_DEVELOPMENT
        internal void TestPour(Component tool, Part_FuelTank target, float amount, Part_FuelTank? source = null)
        {
            InPour = tool;
            try
            {
                target.Get_FuelLevel += amount;
                if (source != null) source.Get_FuelLevel -= amount;
            }
            finally { InPour = null; }
        }
#endif

    }
}
