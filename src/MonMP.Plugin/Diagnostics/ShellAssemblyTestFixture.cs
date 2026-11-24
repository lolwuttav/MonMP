using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal static class ShellAssemblyTestFixture
    {
        private sealed class VehicleSnapshot
        {
            internal VehicleController Vehicle = null!;
            internal bool ActiveSelf, Awake;
            internal Transform? Parent;
            internal Vector3 LocalPosition, LocalScale;
            internal Quaternion LocalRotation;
            internal Rigidbody? Body;
            internal Vector3 BodyPosition, BodyVelocity, BodyAngularVelocity;
            internal Quaternion BodyRotation;
            internal bool IsKinematic;
            internal RigidbodyConstraints Constraints;
            internal CollisionDetectionMode CollisionMode;
            internal EntreposageGarage? Garage;
        }

        private static readonly FieldInfo AwakeField = AccessTools.Field(typeof(VehicleController), "_isAwake")
            ?? throw new MissingFieldException(typeof(VehicleController).FullName, "_isAwake");
        private static readonly FieldInfo GarageDoorsField = AccessTools.Field(typeof(EntreposageGarage), "GarageDoors")
            ?? throw new MissingFieldException(typeof(EntreposageGarage).FullName, "GarageDoors");
        private static readonly FieldInfo PrinzGarageField = AccessTools.Field(typeof(EntreposageGarage), "isPrinzGarage")
            ?? throw new MissingFieldException(typeof(EntreposageGarage).FullName, "isPrinzGarage");
        private static readonly FieldInfo ScrapGarageField = AccessTools.Field(typeof(EntreposageGarage), "isScrapyardGarage")
            ?? throw new MissingFieldException(typeof(EntreposageGarage).FullName, "isScrapyardGarage");
        private static readonly List<VehicleSnapshot> vehicles = new List<VehicleSnapshot>();
        private static Harmony? achievementGuard;
        private static bool prepared;

        internal static void Prepare()
        {
            if (prepared) return;
            var target = AccessTools.Method(typeof(Steam_AchievementManager), nameof(Steam_AchievementManager.AchievementDone), new[] { typeof(AchievementType) });
            var prefix = typeof(ShellAssemblyTestFixture).GetMethod(nameof(SuppressAchievement), BindingFlags.Static | BindingFlags.NonPublic);
            if (target == null || prefix == null) throw new InvalidOperationException("Cannot guard achievements for the isolated shell test.");
            achievementGuard = new Harmony("MonMP.LocalTest.ShellAssemblyAchievementGuard");
            achievementGuard.Patch(target, prefix: new HarmonyMethod(prefix));
            PatchHealth.VerifyManualPatchInstalled(achievementGuard, target);
            var todo = Singleton<ToDoList_Manager>.i;
            if (!todo.isDone(ToDoList_Line.Intermediate_UnlockScrapKonig)) todo.UnlockScrapKonig();
            if (!todo.isDone(ToDoList_Line.Advanced_Prinz_Unlocked)) todo.Advanced_PrinzUnlocked();
            PrepareCanonicalShells();
            prepared = true;
        }

        // Both isolated shell suites need the canonical Prinz and Scrapyard managers fully
        // awakened before SharedWorld snapshots their native wheel controllers. Activate through
        // the game's own garage entry point, then restore prior activity, sleep, pose and body state.
        private static void PrepareCanonicalShells()
        {
            var global = UnityEngine.Object.FindObjectOfType<GlobalClassManager>();
            if (global == null) throw new InvalidOperationException("Shell fixture cannot find GlobalClassManager.");
            foreach (var pair in new[]
            {
                (Type: Vehicle_Type.Prinz, GarageField: PrinzGarageField),
                (Type: Vehicle_Type.ScrapyardKonig, GarageField: ScrapGarageField)
            })
            {
                var native = global.GetVehicles(pair.Type) as VehicleController;
                if (native == null || !native.gameObject.scene.IsValid())
                    throw new InvalidOperationException("Shell fixture cannot find the canonical " + pair.Type + " controller.");
                var manager = native.GetComponent<Vehicle_PartsManager>();
                var body = native.GetComponent<Rigidbody>() ?? native.vehicleRigidbody;
                if (manager == null || body == null)
                    throw new InvalidOperationException("Shell fixture canonical " + pair.Type + " has no native parts manager or rigidbody.");

                var snapshot = new VehicleSnapshot
                {
                    Vehicle = native,
                    ActiveSelf = native.gameObject.activeSelf,
                    Awake = (bool)AwakeField.GetValue(native)!,
                    Parent = native.transform.parent,
                    LocalPosition = native.transform.localPosition,
                    LocalRotation = native.transform.localRotation,
                    LocalScale = native.transform.localScale,
                    Body = body,
                    BodyPosition = body.position,
                    BodyRotation = body.rotation,
                    BodyVelocity = body.velocity,
                    BodyAngularVelocity = body.angularVelocity,
                    IsKinematic = body.isKinematic,
                    Constraints = body.constraints,
                    CollisionMode = body.collisionDetectionMode
                };
                vehicles.Add(snapshot);

                var garages = Resources.FindObjectsOfTypeAll<EntreposageGarage>()
                    .Where(garage => garage != null && garage.gameObject.scene.IsValid() &&
                        (bool)pair.GarageField.GetValue(garage)!)
                    .ToArray();
                if (garages.Length != 1)
                    throw new InvalidOperationException("Shell fixture expected one native " + pair.Type + " garage, found " + garages.Length + ".");
                if (!(GarageDoorsField.GetValue(garages[0]) is Array doors) || doors.Length == 0 || doors.GetValue(0) == null)
                    throw new InvalidOperationException("Shell fixture native " + pair.Type + " garage has no loadable bay.");

                snapshot.Garage = garages[0];
                garages[0].LoadThisCar(0, native);
                if (!native.gameObject.activeInHierarchy || !(bool)AwakeField.GetValue(native)!)
                    throw new InvalidOperationException("Native garage activation did not wake canonical " + pair.Type + ".");
                Debug.Log("MonMP shell assembly prepared " + pair.Type + "; priorActiveSelf=" + snapshot.ActiveSelf +
                    "; priorAwake=" + snapshot.Awake + "; active=" + native.gameObject.activeInHierarchy +
                    "; wheels=" + (native.Wheels == null ? 0 : native.Wheels.Count) +
                    "; parts=" + (manager.carPartSockets == null ? 0 : manager.carPartSockets.Count));
            }
        }

        private static bool SuppressAchievement(AchievementType __0) => false;

        internal static void Cleanup()
        {
            Exception? failure = null;
            for (int i = vehicles.Count - 1; i >= 0; i--)
            {
                var snapshot = vehicles[i];
                try
                {
                    var vehicle = snapshot.Vehicle;
                    if (vehicle == null) continue;
                    if (snapshot.Awake) vehicle.Wake(); else vehicle.Sleep();
                    vehicle.transform.SetParent(snapshot.Parent, true);
                    vehicle.transform.localPosition = snapshot.LocalPosition;
                    vehicle.transform.localRotation = snapshot.LocalRotation;
                    vehicle.transform.localScale = snapshot.LocalScale;
                    if (snapshot.Body != null)
                    {
                        snapshot.Body.isKinematic = snapshot.IsKinematic;
                        snapshot.Body.constraints = snapshot.Constraints;
                        snapshot.Body.collisionDetectionMode = snapshot.CollisionMode;
                        snapshot.Body.position = snapshot.BodyPosition;
                        snapshot.Body.rotation = snapshot.BodyRotation;
                        snapshot.Body.velocity = snapshot.BodyVelocity;
                        snapshot.Body.angularVelocity = snapshot.BodyAngularVelocity;
                    }
                    vehicle.gameObject.SetActive(snapshot.ActiveSelf);
                    snapshot.Garage?.UpdateList();
                    Debug.Log("MonMP shell assembly restored " + vehicle.vehicle_type + "; activeSelf=" +
                        snapshot.ActiveSelf + "; awake=" + (bool)AwakeField.GetValue(vehicle)!);
                }
                catch (Exception ex) { failure ??= ex; }
            }
            vehicles.Clear();
            achievementGuard?.UnpatchSelf(); achievementGuard = null; prepared = false;
            if (failure != null) throw new InvalidOperationException("Shell canonical vehicle fixture restoration failed.", failure);
        }
    }
}
