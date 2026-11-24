using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Shared fueling on the parked stock ATV: the host drops a 20 L gas can holding 10 L next to it and
    // drains the ATV to 2 L. The guest grabs the can and pours 3 L natively (host drains its can copy
    // and fills the tank). The starting Konig's cap/door and canonical Prinz door exercise nearby
    // commands, distant rejection and guest rollback before the can goes to a paid Kaligaz pump.
    internal sealed class PairedFuelTest
    {
        private const float Start = 2, Can = 10, Poured = 3, Pumped = 2;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private ulong tank, can, gun, light;
        private ushort canPrefab;
        private int phase, cash;
        private uint lightRevision;
        private float next, deadline, price;
        private float capRemoteChangeAt, capRemoteTarget;
        private bool capRemoteChangeStarted;
        private float capOriginal, capRollbackTarget;
        private bool requestedCan;
        private Part_FuelTankCap? cap;
        private Part_FuelDoor? door;
        private ulong doorId;
        private bool doorInitialOpen, doorTargetOpen, doorOriginalOpen;
        private VehicleController? prinzVehicle;
        private Part_FuelDoor? prinzDoor;
        private ulong prinzDoorId;
        private bool prinzDoorOriginalOpen, prinzDoorTargetOpen;
        private bool prinzInitialDoorReplayVerified;
        private static readonly FieldInfo PrinzGarageField = AccessTools.Field(typeof(EntreposageGarage), "isPrinzGarage")
            ?? throw new MissingFieldException(typeof(EntreposageGarage).FullName, "isPrinzGarage");
        private static readonly FieldInfo HomeGarageField = AccessTools.Field(typeof(EntreposageGarage), "isHomeGarage")
            ?? throw new MissingFieldException(typeof(EntreposageGarage).FullName, "isHomeGarage");
        private static readonly FieldInfo GarageDoorsField = AccessTools.Field(typeof(EntreposageGarage), "GarageDoors")
            ?? throw new MissingFieldException(typeof(EntreposageGarage).FullName, "GarageDoors");
        private static readonly FieldInfo GarageGeneratorField = AccessTools.Field(typeof(EntreposageGarage), "homeGenerator")
            ?? throw new MissingFieldException(typeof(EntreposageGarage).FullName, "homeGenerator");
        private static readonly MethodInfo GarageAccessible = AccessTools.PropertyGetter(typeof(EntreposageGarage), "isAccessibleByPlayer")
            ?? throw new MissingMethodException(typeof(EntreposageGarage).FullName, "get_isAccessibleByPlayer");
        private int prinzPreparationStage, prinzUnlockFrame;
        private float prinzPreparationDeadline;
        private bool prinzGarageRequested;
        private string prinzPreparation = "awaiting-host-native-unlock";
        private EntreposageGarage? prinzGarage;
        private EntreposageGarage_Door? prinzBay;
        private int prinzBayIndex;
        internal string Status
        {
            get
            {
                var world = plugin.World;
                var items = plugin.Items;
                string tankValue = world == null || tank == 0 ? "?" : world.Fuel.TestState(tank).Value.ToString("0.00");
                string canValue = items == null || can == 0 ? "?" : items.TestState(can).Values.Value1.ToString("0.00");
                string doorValue = world == null || door == null ? "?" : world.Fuel.TestDoorOpen(door).ToString();
                string prinzValue = world == null ? "?" : prinzVehicle == null ? "canonical-vehicle-unavailable" :
                    world.Fuel.TestVehicleFuelDoorDiagnostics(prinzVehicle);
                return "fuel phase=" + phase + "; tank=" + tankValue + "; can=" + canValue +
                    "; fuelDoor=" + doorValue + "; prinzFuelDoor=" + prinzValue +
                    "; prinzPreparation=" + prinzPreparationStage + "/" + prinzPreparation +
                    "; reconnectSettled=" + (world?.ReconnectPositions?.Settled == true) +
                    "; fuel=" + (world?.Fuel.Count.ToString() ?? "?");
            }
        }
        internal PairedFuelTest(MonMPPlugin plugin, Gameplay gameplay) { this.plugin = plugin; this.gameplay = gameplay; }
        internal bool Tick()
        {
            if (deadline == 0) deadline = Time.unscaledTime + 120;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Fuel test stalled: " + Status);
            var world = plugin.World;
            if (world == null || plugin.Session == null) throw new InvalidOperationException("Fuel test session ended before completion: " + Status);
            var items = world.Items; var fuel = world.Fuel;
            if (!items.Ready) return false;
            if (tank == 0)
            {
                world.Vehicles.TestType = Vehicle_Type.SmollATV;
                tank = fuel.TestTankOf(world.Vehicles.TestVehicle);
                Require(tank != 0, "The ATV's fuel tank is not a shared fuel record.");
                world.Vehicles.TestType = Vehicle_Type.MainVehicle;
                var fillerVehicle = world.Vehicles.TestVehicle;
                world.Vehicles.TestType = Vehicle_Type.SmollATV;
                var doors = fuel.TestVehicleFuelDoors(fillerVehicle);
                Require(doors.Length == 1, "The starting Konig must expose exactly one supported, source-shaped fuel door; found " + doors.Length + ". " +
                    (doors.Length == 0 ? "vehicle=" + RuntimeProbe.CanonicalPath(fillerVehicle.transform) : fuel.TestDoorDiagnostic(doors[0])));
                door = doors[0]; doorId = fuel.TestIdOf(door);
                Require(doorId != 0, "The starting Konig's fuel door is not a shared fuel record: " + fuel.TestDoorDiagnostic(door));
                doorInitialOpen = fuel.TestDoorOpen(door); doorTargetOpen = !doorInitialOpen; doorOriginalOpen = fuel.TestOriginalDoorOpen(door);
                prinzVehicle = SharedVehicleParts.CanonicalPrinzVehicle().SingleOrDefault();
                Require(prinzVehicle != null, "The canonical Prinz vehicle is unavailable to the fuel acceptance hook.");
                var prinzDoors = fuel.TestVehicleFuelDoorCandidates(prinzVehicle!);
                Require(prinzDoors.Length == 1, "The canonical Prinz must expose exactly one native fuel-door candidate; found " + prinzDoors.Length + ". " +
                    fuel.TestVehicleFuelDoorDiagnostics(prinzVehicle!));
                prinzDoor = prinzDoors[0]; prinzDoorId = fuel.TestIdOf(prinzDoor);
                Require(prinzDoorId != 0, "The canonical Prinz fuel door is not a shared static fuel record: " + fuel.TestDoorDiagnostic(prinzDoor));
                prinzDoorOriginalOpen = fuel.TestOriginalDoorOpen(prinzDoor);
                prinzDoorTargetOpen = !fuel.TestDoorOpen(prinzDoor);
                var caps = fuel.TestVehicleFuelCaps(fillerVehicle);
                Require(caps.Length == 1, "The starting Konig must expose exactly one supported fuel cap; found " + caps.Length + ".");
                cap = caps[0];
                Require(fuel.TestIdOf(cap) != 0, "The starting Konig's fuel cap is not a shared fuel record.");
                capOriginal = fuel.TestOriginalCap(cap!);
                capRollbackTarget = Mathf.Abs(capOriginal - 0.5f) < 0.01f ? 0.25f : 0.5f;
                canPrefab = items.TestPrefab("Tool/FuelCanister_20L");
                var paid = fuel.TestPaidGun ?? throw new InvalidOperationException("No paid Kaligaz super pump found.");
                gun = paid.Id; price = Store_KaliGas.FuelPrice_Super;
            }
            // Fresh native saves keep the Prinz locked/stored. Prepare it on the host through
            // native progression and its actual garage route before waiting for its fuel door.
            if (plugin.TestOptions.IsHost && !PrepareNativePrinz(fuel)) return false;
            if (prinzDoor != null)
            {
                if (!prinzDoor.gameObject.activeInHierarchy || !fuel.TestDoorNativeReady(prinzDoor))
                {
                    if (prinzPreparationDeadline == 0) prinzPreparationDeadline = Time.unscaledTime + 20f;
                    Require(Time.unscaledTime <= prinzPreparationDeadline, "Canonical Prinz did not become native-ready within 20 seconds: " + Status);
                    return false;
                }
                Require(fuel.TestDoorPoseMatchesNativeState(prinzDoor), "Prinz door native open bool and local rotation disagree: " + fuel.TestDoorDiagnostic(prinzDoor));
                if (!prinzInitialDoorReplayVerified)
                {
                    Require(fuel.TestDoorOpen(prinzDoor) == (fuel.TestState(prinzDoorId).Value == 1),
                        "The canonical Prinz did not replay the host door record after native readiness: " + fuel.TestDoorDiagnostic(prinzDoor));
                    prinzInitialDoorReplayVerified = true;
                }
                // A later native MouseDown predicts the bool before the host acknowledges it.
                // Phase 32 separately requires that accepted record and native pose to converge.
            }
            if (Time.unscaledTime < next) return false;
            return plugin.TestOptions.IsHost ? Host(items, fuel) : Guest(items, fuel);
        }
        private bool PrepareNativePrinz(SharedFuel fuel)
        {
            if (prinzPreparationStage == 3) return true;
            if (plugin.Session!.Peers.Count < 2) { prinzPreparation = "waiting-for-paired-peer"; return false; }
            if (prinzPreparationDeadline == 0) prinzPreparationDeadline = Time.unscaledTime + 20f;
            Require(Time.unscaledTime <= prinzPreparationDeadline, "Native Prinz prerequisite exceeded 20 seconds: " + Status);
            if (prinzPreparationStage == 0)
            {
                var todo = Singleton<ToDoList_Manager>.i ?? throw new InvalidOperationException("Native Prinz prerequisite has no ToDoList_Manager.");
                if (!todo.isDone(ToDoList_Line.Advanced_Prinz_Unlocked))
                {
                    var target = AccessTools.Method(typeof(Steam_AchievementManager), nameof(Steam_AchievementManager.AchievementDone), new[] { typeof(AchievementType) });
                    var prefix = typeof(PairedFuelTest).GetMethod(nameof(SuppressFixtureAchievement), BindingFlags.Static | BindingFlags.NonPublic);
                    if (target == null || prefix == null) throw new InvalidOperationException("Cannot guard achievements for the isolated native Prinz unlock.");
                    var guard = new Harmony("MonMP.LocalTest.FuelPrinzAchievementGuard");
                    try
                    {
                        guard.Patch(target, prefix: new HarmonyMethod(prefix));
                        PatchHealth.VerifyManualPatchInstalled(guard, target);
                        todo.Advanced_PrinzUnlocked();
                    }
                    finally { guard.UnpatchSelf(); }
                }
                Require(todo.isDone(ToDoList_Line.Advanced_Prinz_Unlocked), "Native Advanced_PrinzUnlocked did not complete its progression line.");
                prinzUnlockFrame = Time.frameCount;
                prinzPreparationStage = 1; prinzPreparation = "native-unlock-complete-awaiting-next-frame";
                return false;
            }
            if (Time.frameCount <= prinzUnlockFrame) return false;
            if (prinzDoor!.gameObject.activeInHierarchy && fuel.TestDoorNativeReady(prinzDoor))
            {
                prinzPreparationStage = 3; prinzPreparation = "native-door-active-and-ready";
                Debug.Log("MonMP fuel prepared canonical Prinz through native unlock/garage; " + fuel.TestDoorDiagnostic(prinzDoor));
                return true;
            }
            if (prinzGarage == null)
            {
                var garages = Resources.FindObjectsOfTypeAll<EntreposageGarage>()
                    .Where(garage => garage != null && garage.gameObject.scene.IsValid() && (bool)PrinzGarageField.GetValue(garage)!).ToArray();
                Require(garages.Length <= 1, "Native Prinz prerequisite found ambiguous owning garages: " + garages.Length);
                prinzPreparation = "awaiting-native-Prinz-garage/count=" + garages.Length;
                if (garages.Length == 0) return false;
                prinzGarage = garages[0];
            }
            bool accessible = (bool)GarageAccessible.Invoke(prinzGarage, null)!;
            bool needsPower = (bool)HomeGarageField.GetValue(prinzGarage)!;
            var generator = GarageGeneratorField.GetValue(prinzGarage) as HomeGenerator;
            prinzPreparation = "garage=" + RuntimeProbe.CanonicalPath(prinzGarage.transform) + "/accessible=" + accessible +
                "/home-power-required=" + needsPower + "/generator=" + (generator == null ? "none" : generator.Get_isEngineRunning.ToString());
            Require(accessible, "Native Prinz garage access still refused after native unlock: " + prinzPreparation);
            Require(!needsPower || generator == null || generator.Get_isEngineRunning,
                "Native Prinz garage requires a running native home generator: " + prinzPreparation);
            if (prinzBay == null)
            {
                var doors = GarageDoorsField.GetValue(prinzGarage) as EntreposageGarage_Door[];
                Require(doors != null && doors.Length > 0, "Native Prinz garage has no serialized bays: " + prinzPreparation);
                var bays = doors!.Select((bay, index) => (Bay: bay, Index: index))
                    .Where(pair => pair.Bay != null && pair.Bay.GetVehiclesInBay.Length == 1 && pair.Bay.GetVehiclesInBay[0] == prinzVehicle).ToArray();
                Require(bays.Length <= 1, "Canonical Prinz appears in multiple native garage bays.");
                prinzPreparation += "/canonical-bays=" + bays.Length;
                if (bays.Length == 0) return false;
                prinzBay = bays[0].Bay; prinzBayIndex = bays[0].Index;
            }
            prinzPreparationStage = 2;
            prinzPreparation += "/bay=" + prinzBayIndex + "/open=" + prinzBay.Get_IsDoorOpen + "/moving=" + prinzBay.Get_IsDoorMoving;
            if (prinzBay.Get_IsDoorMoving) return false;
            if (!prinzGarageRequested)
            {
                MoveNear(prinzBay.transform.position);
                // ToggleDoor retains native access/electricity checks; Door_Open retrieves the
                // exact parked car through LoadThisCar. An already-open bay uses that same
                // native load entry point, as the existing isolated shell fixture does.
                if (!prinzBay.Get_IsDoorOpen) prinzGarage.ToggleDoor(prinzBayIndex);
                else prinzGarage.LoadThisCar(prinzBayIndex, prinzVehicle!);
                prinzGarageRequested = true;
                Require(prinzBay.Get_IsDoorMoving || prinzBay.Get_IsDoorOpen,
                    "Native Prinz garage refused its retrieval: " + prinzPreparation);
                prinzPreparation += "/native-retrieval-requested";
            }
            return false;
        }
        private static bool SuppressFixtureAchievement(AchievementType __0) => false;
        private Vector3 TankPosition(SharedFuel fuel) => fuel.TestTank(tank).transform.position;
        private static bool Near(float a, float b) => Mathf.Abs(a - b) < 0.02f;
        private bool Host(SharedItems items, SharedFuel fuel)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                fuel.TestTank(tank).Get_FuelLevel = Start;
                var spawned = (Tool_FuelCanister)items.TestSpawn(TankPosition(fuel) + new Vector3(1.2f, 0.6f, 0), canPrefab);
                spawned.Value1 = Can;
                if (spawned.rb != null) spawned.rb.isKinematic = true;
                cash = Singleton<Cash_Manager>.i.GetCash;
                Require(cash > 1000, "The host save needs at least $10 for the pump step.");
                phase = 1; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 1)
            {
                can = items.TestNewestPrefab(canPrefab);
                Require(can != 0 && items.TestState(can).Exists && Near(items.TestState(can).Values.Value1, Can), "The host's gas can was not shared with its fuel.");
                phase = 2;
            }
            else if (phase == 2 && Near(fuel.TestTank(tank).Get_FuelLevel, Start + Poured))
            {
                var hostCan = (Tool_FuelCanister)items.TestItem(can)!;
                Require(Near(hostCan.Value1, Can - Poured), "The host did not drain its copy of the guest's gas can.");
                phase = 3;
            }
            else if (phase == 3 && Near(((Tool_FuelCanister)items.TestItem(can)!).Value1, Can - Poured + Pumped))
            {
                int expected = cash - Mathf.RoundToInt(Pumped * price * 100);
                Require(Mathf.Abs(Singleton<Cash_Manager>.i.GetCash - expected) <= 2, "Pump fuel was not charged once at the live price: " + Singleton<Cash_Manager>.i.GetCash + " vs " + expected);
                phase = 4;
            }
            else if (phase == 4 && !capRemoteChangeStarted)
            {
                ulong capId = fuel.TestIdOf(cap!);
                if (!Near(fuel.TestState(capId).Value, capRollbackTarget)) return false;
                if (capRemoteChangeAt == 0) { capRemoteChangeAt = Time.unscaledTime + 1.5f; return false; }
                if (Time.unscaledTime < capRemoteChangeAt) return false;
                capRemoteTarget = capOriginal;
                fuel.TestSetCap(cap!, capRemoteTarget);
                capRemoteChangeStarted = true;
            }
            else if (phase == 4 && plugin.Session!.Peers.Count == 1)
            {
                Require(fuel.TestState(doorId).Value == (doorTargetOpen ? 1 : 0) && fuel.TestDoorOpen(door!) == doorTargetOpen,
                    "The host did not retain the accepted nearby fuel-door command after the distant request.");
                Require(fuel.TestState(prinzDoorId).Value == (prinzDoorTargetOpen ? 1 : 0) &&
                    fuel.TestDoorOpen(prinzDoor!) == prinzDoorTargetOpen && fuel.TestDoorNativeReady(prinzDoor!) &&
                    fuel.TestDoorPoseMatchesNativeState(prinzDoor!),
                    "The host did not retain the canonical Prinz door click and native state: " + fuel.TestDoorDiagnostic(prinzDoor!));
                Require(Near(fuel.TestState(fuel.TestIdOf(cap!)).Value, capRemoteTarget) && Near(fuel.TestCap(cap!), capRemoteTarget),
                    "The host did not retain its later remote fuel-cap update after the guest disconnect.");
                return true;
            }
            return false;
        }
        private bool Guest(SharedItems items, SharedFuel fuel)
        {
            if (phase == 0)
            {
                if (plugin.World?.ReconnectPositions?.Settled != true) return false;
                can = items.TestNewestPrefab(canPrefab);
                if (can == 0 || !items.TestState(can).Exists || items.TestItem(can) == null || !Near(items.TestState(can).Values.Value1, Can) ||
                    !Near(fuel.TestState(tank).Value, Start) || !Near(fuel.TestTank(tank).Get_FuelLevel, Start)) return false;
                MoveNear(TankPosition(fuel));
                phase = 1; next = Time.unscaledTime + 1;
            }
            else if (phase == 1)
            {
                if (!requestedCan)
                {
                    // A player can equip a loose gas can directly with Use, without first taking it
                    // into the hand. The lease must arrive before native EquipTool mutates input state.
                    items.TestUse(can);
                    requestedCan = true;
                    Require(gameplay.Player.Get_ActiveTool == null && gameplay.Player.Get_HoldingItem == null,
                        "Direct Use equipped the gas can before its shared lease was granted.");
                    Require(!items.TestActiveToolInputAllowed(can), "Pending gas-can pickup accepted tool mouse input before a lease grant.");
                    next = Time.unscaledTime + 0.4f;
                    return false;
                }
                if (items.TestState(can).Lease.Owner != plugin.Session!.LocalPeerId) return false;
                Require(gameplay.Player.Get_ActiveTool == items.TestItem(can) && gameplay.Player.Get_HoldingItem == null,
                    "Granted gas can was not equipped through the vanilla player state.");
                Require(items.TestActiveToolInputAllowed(can), "Approved gas can could not receive tool mouse input.");
                Require(!items.TestActiveToolInputWhileReleasing(can), "A gas can pending release accepted tool mouse input.");
                Require(!items.TestActiveToolInputWhileTransfersBlocked(can), "The item transfer gate accepted tool mouse input.");
                light = plugin.Lights!.TestEntity;
                lightRevision = plugin.Lights.TestRecord(light).Revision;
                MoveNear(plugin.Lights.TestPosition(light));
                phase = 9; next = Time.unscaledTime + 1;
            }
            else if (phase == 9)
            {
                Require(gameplay.Player.Get_ActiveTool == items.TestItem(can), "Gas can equip state broke before an unrelated interaction.");
                plugin.Lights!.TestClick(light);
                phase = 10; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 10 && plugin.Lights!.TestRecord(light).Revision > lightRevision)
            {
                Require(gameplay.Player.Get_ActiveTool == items.TestItem(can), "Unrelated light interaction unequipped the gas can.");
                MoveNear(TankPosition(fuel));
                phase = 2; next = Time.unscaledTime + 1;
            }
            else if (phase == 2)
            {
                var local = (Tool_FuelCanister)items.TestItem(can)!;
                fuel.TestPour(local, fuel.TestTank(tank), Poured, fuel.TestCanisterTank(local));
                phase = 3;
            }
            else if (phase == 3 && Near(fuel.TestState(tank).Value, Start + Poured) && Near(items.TestState(can).Values.Value1, Can - Poured))
            {
                Require(Near(fuel.TestTank(tank).Get_FuelLevel, Start + Poured), "The guest's own pour was lost.");
                MoveNear(cap!.transform.position);
                phase = 26; next = Time.unscaledTime + 1;
            }
            else if (phase == 26)
            {
                fuel.TestSetCap(cap!, 0.5f);
                phase = 4;
            }
            else if (phase == 4 && Near(fuel.TestState(fuel.TestIdOf(cap!)).Value, 0.5f))
            {
                fuel.TestSetCap(cap!, 1); phase = 5; next = Time.unscaledTime + 1.3f;
            }
            else if (phase == 5 && Near(fuel.TestState(fuel.TestIdOf(cap!)).Value, 1)) phase = 6;
            else if (phase == 6)
            {
                MoveNear(fuel.TestGunOrigin(gun));
                phase = 7; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 7)
            {
                var paid = fuel.TestPaidGun!.Value;
                var local = (Tool_FuelCanister)items.TestItem(can)!;
                fuel.TestPour(paid.Gun, fuel.TestCanisterTank(local), Pumped);
                phase = 8;
            }
            else if (phase == 8 && Near(items.TestState(can).Values.Value1, Can - Poured + Pumped))
            {
                MoveNear(door!.transform.position);
                phase = 18; next = Time.unscaledTime + 1;
            }
            else if (phase == 18)
            {
                // Exercise the native component's exact MouseDown -> state/visual path; SharedFuel
                // observes the resulting bool and asks the host to adopt it.
                door!.MouseDown();
                Require(fuel.TestDoorPoseMatchesNativeState(door) && fuel.TestDoorNativeReady(door),
                    "The native Konig fuel-door click did not update its pose/cache: " + fuel.TestDoorDiagnostic(door));
                phase = 19; next = Time.unscaledTime + 0.3f;
            }
            else if (phase == 19 && fuel.TestState(doorId).Value == (doorTargetOpen ? 1 : 0))
            {
                Require(fuel.TestDoorOpen(door!) == doorTargetOpen, "The accepted host fuel-door state was not visible on the guest.");
                MoveNear(door!.transform.position + new Vector3(10, 0, 0));
                phase = 20; next = Time.unscaledTime + 1;
            }
            else if (phase == 20)
            {
                // A syntactically valid opposite state from beyond 5 m must not override the host.
                fuel.TestRequestDoorState(door!, !doorTargetOpen);
                phase = 21; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 21)
            {
                Require(fuel.TestState(doorId).Value == (doorTargetOpen ? 1 : 0) && fuel.TestDoorOpen(door!) == doorTargetOpen,
                    "The host accepted a fuel-door command from beyond its 5 m range.");
                // The range probe left us 10 m from the Konig. Now click the canonical Prinz door
                // while nearby; this specifically covers its retained inactive-hierarchy topology.
                MoveNear(prinzDoor!.transform.position);
                phase = 31; next = Time.unscaledTime + 1;
            }
            else if (phase == 31)
            {
                Require(fuel.TestDoorNativeReady(prinzDoor!) && fuel.TestDoorPoseMatchesNativeState(prinzDoor!),
                    "The canonical Prinz door is not native-ready for MouseDown: " + fuel.TestDoorDiagnostic(prinzDoor!));
                prinzDoorTargetOpen = !fuel.TestDoorOpen(prinzDoor!);
                prinzDoor!.MouseDown();
                Require(fuel.TestDoorOpen(prinzDoor) == prinzDoorTargetOpen && fuel.TestDoorPoseMatchesNativeState(prinzDoor),
                    "Prinz Part_FuelDoor.MouseDown did not update the native bool/rotation: " + fuel.TestDoorDiagnostic(prinzDoor));
                phase = 32; next = Time.unscaledTime + 0.3f;
            }
            else if (phase == 32 && fuel.TestState(prinzDoorId).Value == (prinzDoorTargetOpen ? 1 : 0))
            {
                Require(fuel.TestDoorOpen(prinzDoor!) == prinzDoorTargetOpen && fuel.TestDoorNativeReady(prinzDoor!) &&
                    fuel.TestDoorPoseMatchesNativeState(prinzDoor!),
                    "The accepted Prinz state did not converge through the native sound/cache path: " + fuel.TestDoorDiagnostic(prinzDoor!));
                // Let the host receive the latest player pose before the existing can lease checks.
                MoveNear(items.TestItem(can)!.transform.position);
                phase = 22; next = Time.unscaledTime + 1;
            }
            else if (phase == 22)
            {
                items.TestUse(can);
                Require(gameplay.Player.Get_ActiveTool == null && gameplay.Player.Get_HoldingItem == items.TestItem(can),
                    "Use did not return the equipped gas can to the player's hand.");
                items.TestDrop(false);
                phase = 11; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 11 && items.TestState(can).Lease.Owner == ItemLease.Unowned)
            {
                Require(gameplay.Player.Get_ActiveTool == null && gameplay.Player.Get_HoldingItem == null,
                    "Dropping the gas can did not clear player interaction state.");
                items.TestGrab(can);
                phase = 12; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 12 && items.TestState(can).Lease.Owner == plugin.Session!.LocalPeerId)
            {
                Require(gameplay.Player.Get_HoldingItem == items.TestItem(can) && gameplay.Player.Get_ActiveTool == null,
                    "The patched hand-grab left the gas can in a broken player state.");
                lightRevision = plugin.Lights!.TestRecord(light).Revision;
                MoveNear(plugin.Lights.TestPosition(light));
                phase = 14; next = Time.unscaledTime + 1;
            }
            else if (phase == 14)
            {
                Require(gameplay.Player.Get_HoldingItem == items.TestItem(can), "Hand-grabbed gas can was lost before the light interaction.");
                plugin.Lights!.TestClick(light);
                phase = 15; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 15 && plugin.Lights!.TestRecord(light).Revision > lightRevision)
            {
                Require(gameplay.Player.Get_HoldingItem == items.TestItem(can), "Light interaction corrupted the hand-grabbed can state.");
                MoveNear(items.TestItem(can)!.transform.position);
                phase = 27; next = Time.unscaledTime + 1;
            }
            else if (phase == 27)
            {
                // This transition drops the guest lease after CancelLocal. Keep the peer pose close
                // to the can so the host's 4.5 m drop guard sees the intended current location.
                items.TestUse(can);
                Require(gameplay.Player.Get_ActiveTool == items.TestItem(can), "Guest could not re-equip the re-picked gas can.");
                Require(!items.TestRejectEquippedToolDuringRelease(can), "A releasing can dispatched tool input.");
                Require(gameplay.Player.Get_ActiveTool == null && gameplay.Player.Get_HoldingItem == null,
                    "Rejected tool input did not clear the stale native equip and hand state.");
                phase = 16; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 16 && items.TestState(can).Lease.Owner == ItemLease.Unowned)
            {
                items.TestUse(can);
                Require(gameplay.Player.Get_ActiveTool == null, "Clean pickup after rejected tool input equipped before the lease grant.");
                phase = 17; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 17 && items.TestState(can).Lease.Owner == plugin.Session!.LocalPeerId)
            {
                Require(gameplay.Player.Get_ActiveTool == items.TestItem(can), "Clean pickup after rejected tool input did not equip the can.");
                items.TestUse(can);
                items.TestDrop(false);
                phase = 13; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 13 && items.TestState(can).Lease.Owner == ItemLease.Unowned)
            {
                Require(gameplay.Player.Get_ActiveTool == null && gameplay.Player.Get_HoldingItem == null,
                    "Guest interaction state did not recover after the second drop.");
                MoveNear(cap!.transform.position);
                phase = 24; next = Time.unscaledTime + 1;
            }
            else if (phase == 24)
            {
                fuel.TestSetCap(cap!, capRollbackTarget);
                phase = 25; next = Time.unscaledTime + 0.3f;
            }
            else if (phase == 25 && Near(fuel.TestState(fuel.TestIdOf(cap!)).Value, capRollbackTarget))
            {
                Require(Near(fuel.TestCap(cap!), capRollbackTarget), "The accepted host fuel-cap state was not visible on the guest.");
                fuel.TestResetCapPresentationObservation(cap!);
                phase = 30; next = Time.unscaledTime + 0.5f;
            }
            else if (phase == 30 && Near(fuel.TestState(fuel.TestIdOf(cap!)).Value, capOriginal) &&
                fuel.TestSawCapPresentationInterpolation(cap!))
            {
                Require(Near(fuel.TestCap(cap!), capOriginal), "The remote fuel-cap target was not applied on the guest.");
                plugin.EndPairedTestSession();
                Require(Near(fuel.TestCap(cap!), capOriginal), "Guest disconnect did not restore its original native fuel-cap state.");
                Require(fuel.TestDoorOpen(door!) == doorOriginalOpen, "Guest disconnect did not restore its original native fuel-door state.");
                Require(fuel.TestDoorOpen(prinzDoor!) == prinzDoorOriginalOpen && fuel.TestDoorNativeReady(prinzDoor!) &&
                    fuel.TestDoorPoseMatchesNativeState(prinzDoor!),
                    "Guest disconnect did not restore the canonical Prinz native fuel-door baseline: " + fuel.TestDoorDiagnostic(prinzDoor!));
                return true;
            }
            return false;
        }
        private void MoveNear(Vector3 target)
        {
            var location = target + new Vector3(0, 0.2f, 1.5f);
            gameplay.PlayerWalking.transform.position = location;
            gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private static void Require(bool ok, string reason) { if (!ok) throw new InvalidOperationException("Fuel test: " + reason); }
    }
}
