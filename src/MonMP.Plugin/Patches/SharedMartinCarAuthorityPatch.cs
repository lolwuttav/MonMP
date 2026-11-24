using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class SharedMartinCarAuthorityPatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            foreach (var method in new[]
            {
                Required("Start"), Required("Update"), Required("Tick", typeof(int)),
                Required("PlayerCall", typeof(House_Location), typeof(bool)),
                Required("PlayerEnteredDetectionZone"), Required("PlayerExitedDetectionZone"),
                Required("DoorOpened"), Required("DoorClosed"), Required("Running"),
                Required("StartRoad", typeof(bool)), Required("PutToSleep", typeof(bool)),
                Required("get_Get_MaxThrottle")
            }) yield return method;
            yield return AccessTools.Method(typeof(AI_MartinCar_HomeDoor), "MouseDown")
                ?? throw new InvalidOperationException("Missing native method: AI_MartinCar_HomeDoor.MouseDown");
        }

        private static MethodInfo Required(string name, params Type[] arguments) =>
            AccessTools.Method(typeof(AI_MartinCar), name, arguments)
            ?? throw new InvalidOperationException("Missing native method: AI_MartinCar." + name);

        private static IEnumerable<MethodBase> TargetMethods() => Targets();

        private static bool Prefix(object __instance)
        {
            if (__instance is AI_MartinCar_HomeDoor homeDoor)
            {
                var plugin = MonMPPlugin.Instance;
                if (plugin?.Session?.IsRunning == true && !plugin.Session.IsHost)
                {
                    if (plugin.World?.MartinCar == null)
                    {
                        plugin.ReportItemUseUnavailable("Wait for the shared Martin car world before knocking.");
                        return false;
                    }
                    return plugin.World.MartinCar.AllowHomeDoorMouseDown(homeDoor);
                }
                return true;
            }
            if (__instance is AI_MartinCar car)
                return MonMPPlugin.Instance?.World?.MartinCar?.SuppressNative(car) != true;
            return MonMPPlugin.Instance?.World?.MartinCar?.SuppressCoroutine(__instance) != true;
        }
    }

    // The authority prefix skips a guest's native PlayerCall (0: no answer). Answer from the replica and
    // forward the call to the host (#165). A door knock never reaches here on a guest.
    [HarmonyPatch]
    internal static class SharedMartinCarPhonePatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(AI_MartinCar), "PlayerCall", new[] { typeof(House_Location), typeof(bool) })
                ?? throw new InvalidOperationException("Missing native method: AI_MartinCar.PlayerCall");
        }

        private static IEnumerable<MethodBase> TargetMethods() => Targets();

        private static void Postfix(AI_MartinCar __instance, House_Location house_Location, bool knockOnDoor, ref int __result)
        {
            if (knockOnDoor) return;
            if (MonMPPlugin.Instance?.World?.MartinCar?.GuestPhoneCall(__instance, house_Location) == true) __result = 1;
        }
    }

    [HarmonyPatch]
    internal static class SharedMartinCarCoroutinePatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            foreach (string coroutine in new[] { "StartRoad", "PutToSleep" })
            {
                var factory = AccessTools.Method(typeof(AI_MartinCar), coroutine, new[] { typeof(bool) })
                    ?? throw new InvalidOperationException("Missing native Martin car coroutine: " + coroutine);
                var moveNext = AccessTools.EnumeratorMoveNext(factory)
                    ?? throw new InvalidOperationException("Unsupported Martin car coroutine state: " + coroutine);
                var owner = AccessTools.Field(moveNext.DeclaringType, "<>4__this");
                if (factory.ReturnType != typeof(System.Collections.IEnumerator) || moveNext.ReturnType != typeof(bool) || owner?.FieldType != typeof(AI_MartinCar))
                    throw new InvalidOperationException("Unsupported Martin car coroutine owner: " + coroutine);
                yield return moveNext;
            }
        }

        private static IEnumerable<MethodBase> TargetMethods() => Targets();

        // Keep an already-running native iterator alive but paused. Returning true
        // without advancing preserves the coroutine so a disconnect resumes the
        // exact sleep/road sequence instead of leaving Martin permanently inert.
        private static bool Prefix(object __instance, ref bool __result)
        {
            if (MonMPPlugin.Instance?.World?.MartinCar?.SuppressCoroutine(__instance) != true) return true;
            __result = true;
            return false;
        }
    }
}
