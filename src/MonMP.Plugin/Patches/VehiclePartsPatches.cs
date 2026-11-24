using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Vehicle_Plate), nameof(Vehicle_Plate.MouseRightDown))]
    internal static class SharedVehiclePlateClickPatch
    {
        private static bool Prefix(Vehicle_Plate __instance) =>
            MonMPPlugin.Instance?.World?.VehiclePlates.MouseRightDown(__instance) != false;

        private static void Postfix(Vehicle_Plate __instance) =>
            MonMPPlugin.Instance?.World?.VehiclePlates.NativeRightDown(__instance);
    }

    // Native input callers wait while a structural operation is pending. Accepted local
    // interactions retain native feedback and personal achievements/todos; these hooks
    // capture the resulting socket state and exact removal output for host acceptance.
    [HarmonyPatch(typeof(Vehicle_PartSocket), nameof(Vehicle_PartSocket.InstallPart))]
    internal static class SharedVehiclePartInstallPatch
    {
        private static void Postfix(Vehicle_PartSocket __instance) => MonMPPlugin.Instance?.World?.VehicleParts.NotifyChanged(__instance);
    }
    [HarmonyPatch(typeof(Vehicle_PartSocket), nameof(Vehicle_PartSocket.RemovePart))]
    internal static class SharedVehiclePartRemovePatch
    {
        private static void Prefix(Vehicle_PartSocket __instance, out (InteractableParts? Part, IDisposable? Scope) __state) =>
            __state = (__instance.partInstalled, MonMPPlugin.Instance?.World?.VehicleParts.BeginNativePartRemoval(__instance));
        private static void Postfix(Vehicle_PartSocket __instance, (InteractableParts? Part, IDisposable? Scope) __state)
        { try { MonMPPlugin.Instance?.World?.VehicleParts.NotifyRemoved(__instance, __state.Part); } finally { __state.Scope?.Dispose(); } }
        private static Exception? Finalizer(Exception? __exception, (InteractableParts? Part, IDisposable? Scope) __state)
        { __state.Scope?.Dispose(); return __exception; }
    }
    // Entering a seat drops parts that are not bolted on enough (a wheel on one bolt). On a guest that
    // drop ran on its own copy only: the wheel and its bolt vanished for it and the host kept a ghost
    // bolt (#184). The host runs the check when it grants a guest the driver seat.
    [HarmonyPatch(typeof(Vehicle_PartsManager), nameof(Vehicle_PartsManager.CheckPartSolidity))]
    internal static class SharedVehiclePartSolidityPatch
    {
        private static bool Prefix() => MonMPPlugin.Instance?.World?.VehicleParts.AllowNativeSolidityCheck() != false;
    }

    [HarmonyPatch(typeof(Vehicle_PartsManager), nameof(Vehicle_PartsManager.VehiclePart_FullyTighten))]
    internal static class SharedVehiclePartTightenPatch
    {
        private static void Postfix(Vehicle_PartSocket part) => MonMPPlugin.Instance?.World?.VehicleParts.NotifyChanged(part);
    }

    // The Konig rack's four tuning controls mutate the same fields stored in native
    // GameObjectData. Only these stock methods are listened to; replicas never create types
    // or invoke arbitrary remote methods.
    [HarmonyPatch]
    internal static class SharedSteeringRackValuesPatch
    {
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var name in new[] { "KnobToeAngle_FL", "KnobToeAngle_FR", "KnobAdjustMaxSteerAngle", "KnobAdjustSteerSpeed" })
                yield return AccessTools.Method(typeof(Part_SteeringRack), name, new[] { typeof(int) })
                    ?? throw new MissingMethodException(typeof(Part_SteeringRack).FullName, name);
        }
        private static void Postfix(Part_SteeringRack __instance) =>
            MonMPPlugin.Instance?.World?.VehicleParts.NotifyNativeValuesChanged(__instance);
    }

    // Native save loading assigns GameObjectData values before Vehicle_PartSocket.InstallPart.
    // The Konig rack's Value1/Value2 setters can no-op until its serialized knob references are
    // available, while SaveSystem reapplies only the separate ToeAngle fields after installation.
    // Reconcile the tuning values after loading from the same selected-save vehicle record the
    // native loader uses (GetThisVehicleFromLastSave instead reads the newest save timestamp).
    internal static class NativeKonigSteeringRackLoadFix
    {
        internal static void RestoreFromSelectedSave()
        {
            try
            {
                var getVehiclesFromSave = AccessTools.Method(typeof(SaveSystem), "GetVehiclesFromSave", Type.EmptyTypes)
                    ?? throw new MissingMethodException(typeof(SaveSystem).FullName, "GetVehiclesFromSave");
                var vehiclesFromSelectedSave = getVehiclesFromSave.Invoke(null, null) as VehicleControllerData[];
                VehicleControllerData savedVehicle = null!;
                if (vehiclesFromSelectedSave != null)
                {
                    foreach (var candidate in vehiclesFromSelectedSave)
                    {
                        if (candidate != null && candidate.id == Vehicle_Type.MainVehicle.ToString())
                        {
                            savedVehicle = candidate;
                            break;
                        }
                    }
                }
                var savedRack = savedVehicle?.SteeringRack;
                if (savedRack == null || string.IsNullOrEmpty(savedRack.id) ||
                    !WholeInRange(savedRack.value1, 20, 70) || !WholeInRange(savedRack.value2, 5, 35)) return;

                bool foundMatchingRack = false;
                foreach (var vehicle in UnityEngine.Object.FindObjectsOfType<NWH.VehiclePhysics2.VehicleController>())
                {
                    if (vehicle.vehicle_type != Vehicle_Type.MainVehicle) continue;
                    var manager = vehicle.GetComponent<Vehicle_PartsManager>();
                    var socket = manager?.SteeringRack;
                    if (socket == null || !socket.isInstalled || socket.partInstalled is not Part_SteeringRack rack ||
                        !string.Equals(rack.PrefabName, savedRack.id, StringComparison.Ordinal)) continue;

                    foundMatchingRack = true;
                    float actualValue1 = rack.Value1;
                    float actualValue2 = rack.Value2;
                    int actualIntValue1 = rack.IntValue1;
                    int actualIntValue2 = rack.IntValue2;
                    bool fullyTightened = socket.CheckIfAllBoltAre100Percent();

                    RestoreRackValue(rack, savedRack.value1, maximumAngle: true);
                    RestoreRackValue(rack, savedRack.value2, maximumAngle: false);
                    if (actualValue1 != savedRack.value1 || actualValue2 != savedRack.value2)
                    {
                        // The native setters refresh the knob labels, but unlike the native knob
                        // handlers they do not refresh the vehicle's steering controller.
                        manager?.Update_Wheels();
                        string before = actualValue1 + "/" + actualValue2 + "/" + actualIntValue1 + "/" + actualIntValue2;
                        string after = rack.Value1 + "/" + rack.Value2 + "/" + rack.IntValue1 + "/" + rack.IntValue2;
                        UnityEngine.Debug.Log("MonMP restored selected-save Konig rack tuning: prefab=" + rack.PrefabName +
                            "; savedPrefab=" + savedRack.id + "; saved=" + savedRack.value1 + "/" + savedRack.value2 + "/" + savedRack.intValue1 + "/" + savedRack.intValue2 +
                            "; before=" + before + "; after=" + after +
                            "; tightened=" + fullyTightened + ".");
                    }
                    return;
                }
                if (!foundMatchingRack)
                    UnityEngine.Debug.LogWarning("MonMP could not find the selected-save Konig steering rack after native load: prefab=" + savedRack.id + ".");
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError("MonMP could not restore saved Konig steering-rack tuning: " + ex);
            }
        }

        private static bool WholeInRange(float value, float min, float max) =>
            !float.IsNaN(value) && !float.IsInfinity(value) && value >= min && value <= max && value == MathF.Round(value);

        private static void RestoreRackValue(Part_SteeringRack rack, float target, bool maximumAngle)
        {
            if (maximumAngle) rack.Value1 = target;
            else rack.Value2 = target;
            if ((maximumAngle ? rack.Value1 : rack.Value2) == target) return;

            string methodName = maximumAngle ? "AdjustMaxSteerAngle" : "AdjustSteerSpeed";
            var adjust = AccessTools.Method(typeof(Part_SteeringRack), methodName, new[] { typeof(int), typeof(bool) })
                ?? throw new MissingMethodException(typeof(Part_SteeringRack).FullName, methodName);
            int limit = maximumAngle ? 70 : 35;
            for (int i = 0; i < limit; i++)
            {
                float before = maximumAngle ? rack.Value1 : rack.Value2;
                if (before == target) return;
                int direction = Math.Sign(target - before);
                adjust.Invoke(rack, new object[] { direction, false });
                float after = maximumAngle ? rack.Value1 : rack.Value2;
                if (after == before) break;
            }
            if ((maximumAngle ? rack.Value1 : rack.Value2) != target)
                throw new InvalidOperationException("Native steering-rack control could not apply its saved tuning value.");
        }
    }
}
