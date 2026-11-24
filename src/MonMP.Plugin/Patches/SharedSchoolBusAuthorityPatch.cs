using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class SharedSchoolBusAuthorityPatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            foreach (var method in new[]
            {
                Required(typeof(AI_SchoolBus), "Start"), Required(typeof(AI_SchoolBus), "Update"),
                Required(typeof(AI_SchoolBus), "Tick", typeof(int)), Required(typeof(AI_SchoolBus), "Button_StopBus"),
                Required(typeof(AI_SchoolBus), "PlayerEnteredDetectionZone"), Required(typeof(AI_SchoolBus), "PlayerExitedDetectionZone"),
                Required(typeof(AI_SchoolBus), "StartRoad"), Required(typeof(AI_SchoolBus), "EnableVehicle"),
                Required(typeof(AI_SchoolBus), "Running"), Required(typeof(AI_SchoolBus), "LightsManager"),
                Required(typeof(AI_SchoolBus), "LightsManager_Close"), Required(typeof(AI_SchoolBus), "get_Get_MaxThrottle"),
                Required(typeof(Actor_Richard), "PlayerClickedOnBusRadio"), Required(typeof(Actor_Richard), "PlayerSpitOn")
            }) yield return method;

            foreach (string coroutine in new[] { "PutToSleep", "OpenMasterWhileSleeping" })
            {
                var factory = Required(typeof(AI_SchoolBus), coroutine);
                if (factory.ReturnType != typeof(System.Collections.IEnumerator))
                    throw new InvalidOperationException("Unsupported school bus coroutine: " + coroutine);
                var moveNext = AccessTools.EnumeratorMoveNext(factory)
                    ?? throw new InvalidOperationException("Unsupported school bus coroutine state: " + coroutine);
                var owner = AccessTools.Field(moveNext.DeclaringType, "<>4__this");
                if (moveNext.ReturnType != typeof(bool) || owner?.FieldType != typeof(AI_SchoolBus))
                    throw new InvalidOperationException("Unsupported school bus coroutine owner: " + coroutine);
                yield return moveNext;
            }
        }

        private static MethodInfo Required(Type type, string name, params Type[] arguments) =>
            AccessTools.Method(type, name, arguments) ?? throw new InvalidOperationException("Missing native method: " + type.FullName + "." + name);

        private static IEnumerable<MethodBase> TargetMethods() => Targets();

        private static bool Prefix(object __instance, MethodBase __originalMethod)
        {
            var buses = MonMPPlugin.Instance?.World?.SchoolBus;
            if (__instance is AI_SchoolBus bus)
            {
                // The host walking out must not close the door on a guest still boarding.
                if (__originalMethod.Name == "PlayerExitedDetectionZone" && buses?.RemoteInZone(bus) == true) return false;
                if (buses?.SuppressNative(bus) != true) return true;
                if (__originalMethod.Name == "Button_StopBus" || __originalMethod.Name == "PlayerEnteredDetectionZone" ||
                    __originalMethod.Name == "PlayerExitedDetectionZone")
                    buses.RequestControl(bus, __originalMethod.Name);
                return false;
            }
            if (__instance is Actor_Richard richard)
                return buses?.RejectExplicitControl(richard, __originalMethod.Name) != true;
            if (buses?.SuppressCoroutine(__instance) == true) return false;
            return true;
        }
    }
}
