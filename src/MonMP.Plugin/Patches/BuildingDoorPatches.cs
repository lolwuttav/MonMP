using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class BuildingDoorHandlePatch
    {
        internal static IEnumerable<MethodInfo> Targets()
        {
            foreach (var type in new[] { typeof(Furniture_DoorHandle), typeof(Furniture_DoorHandle_Dealer), typeof(Furniture_DoorHandle_RogerStore), typeof(Furniture_DoorHandle_VIPRoom) })
                yield return AccessTools.DeclaredMethod(type, nameof(Furniture_DoorHandle.MouseDown));
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static bool Prefix(Furniture_DoorHandle __instance, MethodBase __originalMethod) =>
            MonMPPlugin.Instance?.World?.Doors.AllowHandle(__instance, __originalMethod.DeclaringType!) ?? true;
    }

    [HarmonyPatch]
    internal static class BuildingDoorMutationPatch
    {
        internal static IEnumerable<MethodInfo> Targets()
        {
            yield return AccessTools.Method(typeof(Furniture_Door), nameof(Furniture_Door.ToggleDoor));
            yield return AccessTools.Method(typeof(Furniture_Door), nameof(Furniture_Door.CloseAndLockDoor), new[] { typeof(bool) });
            yield return AccessTools.Method(typeof(Furniture_Door), nameof(Furniture_Door.UnlockDoor));
            yield return AccessTools.Method(typeof(Furniture_Door), nameof(Furniture_Door.ForceOpen));
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static bool Prefix(Furniture_Door __instance, MethodBase __originalMethod) =>
            // Pending guest bar pass-out must not relock doors restored on disconnect.
            (!Replication.GuestDeathTransition.BlockPersonalCompletion || __originalMethod.Name != nameof(Furniture_Door.CloseAndLockDoor)) &&
            (MonMPPlugin.Instance?.World?.Doors.AllowMutation(__instance, __originalMethod.Name == nameof(Furniture_Door.ToggleDoor)) ?? true);
    }

    [HarmonyPatch(typeof(StoreManagers), nameof(StoreManagers.Tick))]
    internal static class StoreDoorPresencePatch
    {
        private static readonly MethodInfo Distance = AccessTools.Method(typeof(Vector3), nameof(Vector3.Distance), new[] { typeof(Vector3), typeof(Vector3) });
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var calls = code.Where(i => i.Calls(Distance)).ToArray();
            if (calls.Length != 1) throw new InvalidOperationException("Unsupported StoreManagers.Tick door-distance layout.");
            calls[0].opcode = OpCodes.Call; calls[0].operand = AccessTools.Method(typeof(StoreDoorPresencePatch), nameof(StoreDoorDistance));
            return code;
        }
        internal static float StoreDoorDistance(Vector3 player, Vector3 door) =>
            MonMPPlugin.Instance?.World?.Doors.NearestPlayerDistance(player, door) ?? Vector3.Distance(player, door);
    }

    // Distance-gated NPC groups (the dealers and their smoke) stay live on the host while any
    // player is near, so a guest's talk or sale finds them active.
    [HarmonyPatch(typeof(DisableWhenPlayerTooFar), "Tick")]
    internal static class DistanceGatedPresencePatch
    {
        private static readonly MethodInfo Distance = AccessTools.Method(typeof(Vector3), nameof(Vector3.Distance), new[] { typeof(Vector3), typeof(Vector3) });
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var calls = code.Where(i => i.Calls(Distance)).ToArray();
            if (calls.Length != 1) throw new InvalidOperationException("Unsupported DisableWhenPlayerTooFar.Tick distance layout.");
            calls[0].opcode = OpCodes.Call; calls[0].operand = AccessTools.Method(typeof(DistanceGatedPresencePatch), nameof(GateDistance));
            return code;
        }
        // Native order is Distance(gate, player).
        internal static float GateDistance(Vector3 gate, Vector3 player) => StoreDoorPresencePatch.StoreDoorDistance(player, gate);
    }
}
