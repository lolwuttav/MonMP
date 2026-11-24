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

namespace MonMP.Plugin.Diagnostics
{
    // Six guest-carried Buggy packs enter the build trigger. The host consumes each shared twin,
    // completes native ToDo and enables the vehicle; the guest then drives it through a real lease.
    internal sealed class PairedBuggyBuildTest
    {
        private static readonly string[] Paths =
        {
            "Event/Buggy/BuggyPack_Tubes", "Event/Buggy/BuggyPack_Steering",
            "Event/Buggy/BuggyPack_Wheels", "Event/Buggy/BuggyPack_Drivetrain",
            "Event/Buggy/BuggyPack_SeatPlastics", "Event/Buggy/BuggyPack_Engine"
        };
        private static readonly ToDoList_Line[] Lines =
        {
            ToDoList_Line.Buggy_Tubes, ToDoList_Line.Buggy_Steering, ToDoList_Line.Buggy_Wheels,
            ToDoList_Line.Buggy_Drivetrain, ToDoList_Line.Buggy_SeatPlastics, ToDoList_Line.Buggy_Engine
        };
        private static readonly FieldInfo PacksField = AccessTools.Field(typeof(BuggyBuild), "PackInstalled");
        private static readonly FieldInfo VehicleField = AccessTools.Field(typeof(BuggyBuild), "vc");
        private static readonly FieldInfo SeatField = AccessTools.Field(typeof(BuggyBuild), "Seat");
        private static readonly FieldInfo EnableField = AccessTools.Field(typeof(BuggyBuild), "ToEnableWhenCompleted");
        private static readonly FieldInfo DisableField = AccessTools.Field(typeof(BuggyBuild), "ToDisableWhenCompleted");
        private static readonly HashSet<int> suppressedSavedPackIds = new HashSet<int>();
        private static int suppressedSavedPackCount;
        private static string suppressedSavedPackSummary = "none";
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly ushort[] prefabs = new ushort[6];
        private readonly ulong[] packs = new ulong[6];
        private int phase, pack;
        private bool baselineChecked;
        private float next, deadline;
        private Vector3 driveStart;

        internal string Status
        {
            get
            {
                var shared = SharedBuggyBuild.Current;
                var todo = UnityEngine.Object.FindObjectOfType<ToDoList_Manager>();
                byte todoMask = 0;
                if (todo != null) for (int i = 0; i < 6; i++) if (todo.isDone(Lines[i])) todoMask |= (byte)(1 << i);
                return "buggy phase=" + phase + "; pack=" + pack + "; mask=" +
                    (shared?.TestState.Installed.ToString("X2") ?? "none") + "; local=" +
                    (shared?.TestLocal.Installed.ToString("X2") ?? "none") + "; vehicle=" +
                    (shared?.TestVehicleActive.ToString() ?? "none") + "; vc=" +
                    (shared?.TestVehicle?.enabled.ToString() ?? "none") + "; kinematic=" +
                    (shared?.TestVehicle?.vehicleRigidbody?.isKinematic.ToString() ?? "none") + "; dormant=" +
                    (plugin.World?.Vehicles.TestBuggyDormant.ToString() ?? "none") + "; todo=" + todoMask.ToString("X2") +
                    "; live={" + LiveFixtureState(shared?.TestNative) + "}" +
                    "; suppressedSavedPacks=" + suppressedSavedPackSummary +
                    "; synchronized=" + (shared?.TestSynchronized.ToString() ?? "none") +
                    "; peers=" + (plugin.Session?.Peers.Count.ToString() ?? "none") +
                    "; driver=" + (plugin.World?.Vehicles.TestState.Driver.Owner.ToString() ?? "none") +
                    "; drive=" + (shared?.TestVehicle == null ? "none" :
                        Vector3.Distance(shared.TestVehicle.transform.position, driveStart).ToString("0.00"));
            }
        }
        internal PairedBuggyBuildTest(MonMPPlugin plugin, Gameplay gameplay)
        { this.plugin = plugin; this.gameplay = gameplay; }

        // A dedicated isolated save can contain a previously completed Buggy. Normalize its
        // in-memory native state before BeginPairedTestSession constructs SharedWorld and takes
        // the immutable catalog snapshot. This is test fixture setup only; production sync never
        // rewrites native progression to make a test pass.
        internal static void PrepareIsolatedFixture()
        {
            if (!LocalTestIsolation.Installed)
                throw new InvalidOperationException("Buggy fixture preparation requires isolated test saves.");

            var native = UnityEngine.Object.FindObjectsOfType<BuggyBuild>(true)
                .FirstOrDefault(b => b.gameObject.scene.IsValid());
            var todo = UnityEngine.Object.FindObjectOfType<ToDoList_Manager>();
            if (native == null || todo == null || PacksField == null || VehicleField == null || SeatField == null ||
                EnableField == null || DisableField == null)
                throw new InvalidOperationException("Buggy fixture cannot find the native build, ToDo manager, or expected fields; " + LiveFixtureState(native));
            var packs = PacksField.GetValue(native) as bool[];
            var vehicle = VehicleField.GetValue(native) as VehicleController;
            var body = vehicle?.vehicleRigidbody;
            var seat = SeatField.GetValue(native) as GameObject;
            if (packs == null || packs.Length != 6 || vehicle == null || body == null || seat == null)
                throw new InvalidOperationException("Buggy fixture native layout is incomplete; " + LiveFixtureState(native));

            native.gameObject.SetActive(true);
            SuppressSavedPacks(native);
            native.CancelInvoke("BuildBuggyNextFrame");
            native.CancelInvoke("ActivateBuggy");
            Array.Clear(packs, 0, packs.Length);

            // Reverse the native completion callback in the same order used by the adapter's
            // guest rollback, then ask the game's own loader to refresh all six pack visuals.
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.isKinematic = true;
            vehicle.SetInputToPlayer(false);
            vehicle.enabled = false;
            foreach (var collider in seat.GetComponents<Collider>()) collider.enabled = false;
            foreach (var go in (EnableField.GetValue(native) as GameObject[] ?? Array.Empty<GameObject>())) if (go != null) go.SetActive(false);
            foreach (var go in (DisableField.GetValue(native) as GameObject[] ?? Array.Empty<GameObject>())) if (go != null) go.SetActive(true);
            foreach (var lineId in Lines)
            {
                var line = todo.Get_Lines[lineId];
                line.value = false;
                line.UpdateStrike();
            }
            native.LoadFromSaveSystem(new bool[6]);

            int enabledSeatColliders = seat.GetComponents<Collider>().Count(c => c.enabled);
            if (native.gameObject.activeSelf != true || vehicle.enabled || !body.isKinematic ||
                packs.Any(installed => installed) || enabledSeatColliders != 0 || Lines.Any(todo.isDone))
                throw new InvalidOperationException("Buggy fixture did not reach the unbuilt native baseline; " + LiveFixtureState(native));
        }

        private static void SuppressSavedPacks(BuggyBuild native)
        {
            if (!native.GetComponents<Collider>().Any(c => c != null && c.enabled && c.isTrigger))
                throw new InvalidOperationException("Buggy fixture has no enabled build trigger; " + LiveFixtureState(native));

            // The isolated saved world can contain existing packs. They may be inactive at
            // this instant and enter the trigger after the next physics update. The test
            // spawns its own six packs later, so remove all scene-owned saved packs now.
            var savedPacks = UnityEngine.Object.FindObjectsOfType<Event_BuggyPack>(true)
                .Where(pack => pack != null && pack.gameObject.scene.IsValid())
                .ToArray();
            suppressedSavedPackIds.Clear();
            foreach (var pack in savedPacks) suppressedSavedPackIds.Add(pack.GetInstanceID());
            foreach (var pack in savedPacks) pack.gameObject.SetActive(false);
            suppressedSavedPackCount = savedPacks.Length;
            suppressedSavedPackSummary = savedPacks.Length == 0 ? "none" : string.Join("|", savedPacks.Select(pack =>
                pack.IntValue1 + "@" + pack.transform.position.ToString("F1")));
        }

        internal static bool SuppressSavedTrigger(Collider other)
        {
            var pack = other.GetComponent<Event_BuggyPack>();
            return pack != null && suppressedSavedPackIds.Contains(pack.GetInstanceID());
        }

        private static string LiveFixtureState(BuggyBuild? native)
        {
            if (native == null)
                native = UnityEngine.Object.FindObjectsOfType<BuggyBuild>(true).FirstOrDefault(b => b.gameObject.scene.IsValid());
            if (native == null) return "build=missing";
            var packs = PacksField?.GetValue(native) as bool[];
            byte mask = 0;
            if (packs != null) for (int i = 0; i < Math.Min(6, packs.Length); i++) if (packs[i]) mask |= (byte)(1 << i);
            var vehicle = VehicleField?.GetValue(native) as VehicleController;
            var body = vehicle?.vehicleRigidbody;
            var seat = SeatField?.GetValue(native) as GameObject;
            var seatColliders = seat == null ? Array.Empty<Collider>() : seat.GetComponents<Collider>();
            var todo = UnityEngine.Object.FindObjectOfType<ToDoList_Manager>();
            byte todoMask = 0;
            if (todo != null) for (int i = 0; i < Lines.Length; i++) if (todo.isDone(Lines[i])) todoMask |= (byte)(1 << i);
            return "liveMask=" + mask.ToString("X2") + "; liveVc=" + (vehicle?.enabled.ToString() ?? "none") +
                "; liveKinematic=" + (body?.isKinematic.ToString() ?? "none") +
                "; liveCollision=" + (body?.collisionDetectionMode.ToString() ?? "none") +
                "; liveRootActive=" + native.gameObject.activeSelf +
                "; liveSeatColliders=" + seatColliders.Count(c => c.enabled) + "/" + seatColliders.Length +
                "; liveTodo=" + todoMask.ToString("X2") +
                "; suppressedSavedPackCount=" + suppressedSavedPackCount +
                "; pendingBuild=" + native.IsInvoking("BuildBuggyNextFrame") +
                "; pendingActivate=" + native.IsInvoking("ActivateBuggy");
        }

        internal bool Tick()
        {
            var shared = SharedBuggyBuild.Current ?? throw new InvalidOperationException("Buggy build adapter is absent; " + LiveFixtureState(null));
            var items = plugin.World!.Items;
            plugin.World.Vehicles.TestType = Vehicle_Type.Buggy;
            if (deadline == 0) deadline = Time.unscaledTime + 170;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Buggy build test stalled: " + Status);
            if (!items.Ready || !plugin.World.Channel.FullySynchronized || Time.unscaledTime < next) return false;
            Require(shared.TestNative != null, "native BuggyBuild is missing.");
            for (int i = 0; i < 6; i++) prefabs[i] = items.TestPrefab(Paths[i]);
            if (!baselineChecked)
            {
                var fixtureVehicle = shared.TestVehicle;
                Require(shared.TestOriginal.Installed == 0 && shared.TestLocal.Installed == 0 &&
                    fixtureVehicle != null && !fixtureVehicle.enabled && fixtureVehicle.vehicleRigidbody.isKinematic,
                    "fixture must start with an unbuilt, physically dormant buggy; " + Status);
                var vehicles = plugin.World.Vehicles;
                vehicles.TestType = Vehicle_Type.Buggy;
                bool hasRecord = plugin.World.Channel.Records.TryGetValue(vehicles.TestId, out var record);
                Require(vehicles.TestBuggyReserved && vehicles.TestBuggyDormant && hasRecord && record!.Adapter == VehicleState.Adapter,
                    "unbuilt buggy was not reserved in the initial static vehicle catalog: id=" + vehicles.TestId.ToString("X16") +
                    "; binding=" + vehicles.TestBuggyReserved + "; dormant=" + vehicles.TestBuggyDormant +
                    "; record=" + hasRecord + "; adapter=" + (hasRecord ? record!.Adapter.ToString() : "none") +
                    "; records=" + plugin.World.Channel.Records.Count + "; " + Status);
                baselineChecked = true;
            }
            return plugin.TestOptions.IsHost ? Host(shared, items) : Guest(shared, items);
        }

        private bool Host(SharedBuggyBuild shared, SharedItems items)
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                var origin = shared.TestNative!.transform.position;
                var tank = shared.TestVehicle?.vehicle_PartsManager?.Fuel_Tank;
                if (tank != null) tank.Get_FuelLevel = Mathf.Max(tank.Get_FuelLevel, 10f);
                for (int i = 0; i < 6; i++)
                {
                    var item = items.TestSpawn(origin + new Vector3(6 + i * 1.3f, 1.5f, 0), prefabs[i]);
                    Require(item is Event_BuggyPack && item.IntValue1 == i, "pack resource index does not match native build order.");
                    item.transform.localScale = Vector3.one * 0.86f;
                    item.isSpawnFromTheSaveLoadSystem = true;
                    item.CreateBasics();
                    if (item.rb != null) item.rb.isKinematic = true;
                }
                phase = 1; next = Time.unscaledTime + 1.5f;
            }
            else if (phase == 1)
            {
                for (int i = 0; i < 6; i++) packs[i] = items.TestTaggedItem(prefabs[i], 0.86f);
                if (packs.Any(id => id == 0)) return false;
                phase = 2;
            }
            else if (phase == 2 && pack < 6 && shared.TestState.Has(pack) && !items.TestState(packs[pack]).Exists)
            {
                Require(UnityEngine.Object.FindObjectOfType<ToDoList_Manager>().isDone(Lines[pack]),
                    "host ToDo did not complete for pack " + pack + ".");
                Require(shared.TestState.Installed == (1 << (pack + 1)) - 1,
                    "a pack installed twice or out of order on the host.");
                pack++;
                if (pack == 6) phase = 3;
            }
            else if (phase == 3 && shared.TestVehicleActive)
            {
                Require(shared.TestState.Complete, "native buggy became active before all six packs.");
                driveStart = shared.TestVehicle!.transform.position;
                phase = 4;
            }
            else if (phase == 4)
            {
                var vehicles = plugin.World!.Vehicles;
                vehicles.TestType = Vehicle_Type.Buggy;
                uint driver = vehicles.TestState.Driver.Owner;
                if (driver == ItemLease.Unowned || driver == 0) return false;
                Require(!vehicles.TestBuggyDormant && vehicles.TestVehicle.VehicleMultiplayerInstanceType == Vehicle.MultiplayerInstanceType.Remote &&
                    vehicles.TestVehicle.vehicleRigidbody.isKinematic,
                    "host did not yield the completed buggy to the guest driver.");
                phase = 5;
            }
            else if (phase == 5 && Vector3.Distance(shared.TestVehicle!.transform.position, driveStart) > 0.3f) phase = 6;
            else if (phase == 6 && plugin.World!.Vehicles.TestState.Driver.Owner == ItemLease.Unowned && plugin.Session!.Peers.Count == 1)
            {
                Require(Vector3.Distance(shared.TestVehicle!.transform.position, driveStart) > 0.3f,
                    "host did not observe the guest's native buggy drive.");
                return true;
            }
            return false;
        }

        private bool Guest(SharedBuggyBuild shared, SharedItems items)
        {
            if (phase == 0)
            {
                if (pack == 6)
                {
                    var todo = UnityEngine.Object.FindObjectOfType<ToDoList_Manager>();
                    if (shared.TestVehicle?.enabled != true || plugin.World!.Vehicles.TestBuggyDormant || !shared.TestLocal.Complete ||
                        todo == null || !Lines.All(todo.isDone)) return false;
                    Require(shared.TestVehicle.vehicleRigidbody.isKinematic,
                        "completed guest Buggy did not enter remote physics mode.");
                    var vehicles = plugin.World.Vehicles;
                    vehicles.TestType = Vehicle_Type.Buggy;
                    driveStart = vehicles.TestVehicle.transform.position;
                    MoveNear(vehicles.TestSeat + Vector3.right * 1.5f);
                    phase = 5; next = Time.unscaledTime + 0.8f;
                    return false;
                }
                packs[pack] = items.TestTaggedItem(prefabs[pack], 0.86f);
                if (packs[pack] == 0 || items.TestItem(packs[pack]) == null) return false;
                Require(!shared.TestLocal.Has(pack), "guest installed a pack before placing it.");
                MoveNear(items.TestItem(packs[pack])!.transform.position + Vector3.forward);
                phase = 1; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 1)
            {
                items.TestGrab(packs[pack]); phase = 2;
            }
            else if (phase == 2 && items.TestState(packs[pack]).Lease.Owner == plugin.Session!.LocalPeerId)
            {
                Require(gameplay.Player.Get_HoldingItem == items.TestItem(packs[pack]), "guest did not hold pack " + pack + ".");
                var trigger = shared.TestNative!.GetComponents<Collider>().FirstOrDefault(c => c.isTrigger) ??
                    shared.TestNative.GetComponent<Collider>();
                Require(trigger != null, "buggy build trigger collider is absent.");
                MoveNear(trigger!.bounds.center + Vector3.forward);
                phase = 3; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 3)
            {
                var trigger = shared.TestNative!.GetComponents<Collider>().FirstOrDefault(c => c.isTrigger) ??
                    shared.TestNative.GetComponent<Collider>();
                Require(trigger != null, "buggy build trigger collider disappeared.");
                items.TestItem(packs[pack])!.transform.position = trigger!.bounds.center;
                items.TestDrop(false);
                Require(!shared.TestLocal.Has(pack), "guest collider installed the pack locally.");
                phase = 4;
            }
            else if (phase == 4 && shared.TestState.Has(pack) && !items.TestState(packs[pack]).Exists)
            {
                Require(shared.TestLocal.Has(pack), "guest build visual did not follow pack " + pack + ".");
                pack++; phase = 0; next = Time.unscaledTime + 0.4f;
            }
            else if (phase == 5)
            {
                plugin.World!.Vehicles.TestEnter(); phase = 6;
            }
            else if (phase == 6 && plugin.World!.Vehicles.TestState.Driver.Owner == plugin.Session!.LocalPeerId)
            {
                var v = plugin.World.Vehicles.TestVehicle;
                Require(gameplay.Get_CurrentVehicleController == v && gameplay.Get_PlayerState == PlayerState.InVehicle_Driver &&
                    v.VehicleMultiplayerInstanceType == Vehicle.MultiplayerInstanceType.Local && !v.vehicleRigidbody.isKinematic,
                    "guest did not acquire native buggy driving authority.");
                v.input.autoSetInput = false; v.input.Throttle = 0; v.input.Brakes = 0; v.input.Handbrake = 0;
                v.input.states.clutch = 1;
                if (v.vehicle_PartsManager.ParkingBrake != null) v.vehicle_PartsManager.ParkingBrake.LoadFromSave = 0;
                v.brakes.handbrakeValue = 0; v.powertrain.transmission.ShiftInto(0, true);
                v.Get_IgnitionPosition = 2;
                phase = 7; next = Time.unscaledTime + 2;
            }
            else if (phase == 7)
            {
                var v = plugin.World!.Vehicles.TestVehicle;
                Require(v.isEngineRunning, "native buggy engine did not start after the build.");
                v.Get_IgnitionPosition = 1; v.powertrain.transmission.ShiftInto(1, true); v.input.Throttle = 0.6f;
                phase = 8; next = Time.unscaledTime + 3;
            }
            else if (phase == 8)
            {
                var vehicles = plugin.World!.Vehicles; var v = vehicles.TestVehicle;
                Require(Vector3.Distance(v.transform.position, driveStart) > 0.3f,
                    "guest could not drive the newly built buggy.");
                v.input.Throttle = 0; v.input.Brakes = 1;
                if (v.vehicle_PartsManager.ParkingBrake != null) v.vehicle_PartsManager.ParkingBrake.LoadFromSave = 1;
                v.brakes.handbrakeValue = 1;
                v.input.autoSetInput = true;
                vehicles.TestExit(); phase = 9; next = Time.unscaledTime + 0.8f;
            }
            else if (phase == 9 && plugin.World!.Vehicles.TestState.Driver.Owner == ItemLease.Unowned)
            {
                Require(gameplay.Get_PlayerState == PlayerState.Walking && shared.TestVehicle!.vehicleRigidbody.isKinematic,
                    "buggy driver exit did not restore guest walking and remote physics.");
                var vehicles = plugin.World.Vehicles;
                plugin.EndPairedTestSession();
                Require(shared.TestRestored, "guest native buggy build was not restored on disconnect.");
                Require(vehicles.TestRestored, "guest vehicle mode and position were not restored on disconnect.");
                return true;
            }
            return false;
        }

        private void MoveNear(Vector3 position)
        {
            gameplay.PlayerWalking.transform.position = position;
            gameplay.Get_Player_Rigidbody.position = position;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Buggy build test: " + message + "; " + LiveFixtureState(null)); }
    }
}
