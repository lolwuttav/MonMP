using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;

namespace MonMP.Plugin.Patches
{
    // #149: scope the guard to the synchronous PutToSleep factory call made by Tick. Other
    // native sleep coroutines continue to run through the existing authority patches.
    internal static class SharedMartinCarTickSleepScope
    {
        internal struct State
        {
            internal AI_MartinCar? PreviousCar;
            internal bool PreviousSuppress;
        }

        [ThreadStatic] private static AI_MartinCar? activeCar;
        [ThreadStatic] private static bool suppressTickSleep;

        internal static State Enter(AI_MartinCar controller, int tick)
        {
            var state = new State { PreviousCar = activeCar, PreviousSuppress = suppressTickSleep };
            bool suppress = MonMPPlugin.Instance?.World?.MartinCar?.ShouldSuppressTickSleep(controller, tick) == true;
            activeCar = controller;
            suppressTickSleep = suppress;
            return state;
        }

        internal static void Exit(State state)
        {
            activeCar = state.PreviousCar;
            suppressTickSleep = state.PreviousSuppress;
        }

        internal static bool Suppress(AI_MartinCar controller) => activeCar == controller && suppressTickSleep;
    }

    [HarmonyPatch]
    internal static class SharedMartinCarTickSleepScopePatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            var method = AccessTools.Method(typeof(AI_MartinCar), "Tick", new[] { typeof(int) })
                ?? throw new InvalidOperationException("Missing native AI_MartinCar.Tick(int).");
            if (method.IsStatic || method.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported native AI_MartinCar.Tick(int) signature.");
            yield return method;
        }

        private static IEnumerable<MethodBase> TargetMethods() => Targets();

        private static void Prefix(AI_MartinCar __instance, int tick, out SharedMartinCarTickSleepScope.State __state)
            => __state = SharedMartinCarTickSleepScope.Enter(__instance, tick);

        private static Exception? Finalizer(Exception? __exception, SharedMartinCarTickSleepScope.State __state)
        {
            SharedMartinCarTickSleepScope.Exit(__state);
            return __exception;
        }
    }

    [HarmonyPatch]
    internal static class SharedMartinCarTickSleepFactoryPatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            var method = AccessTools.Method(typeof(AI_MartinCar), "PutToSleep", new[] { typeof(bool) })
                ?? throw new InvalidOperationException("Missing native AI_MartinCar.PutToSleep(bool).");
            if (method.IsStatic || method.ReturnType != typeof(IEnumerator))
                throw new InvalidOperationException("Unsupported native AI_MartinCar.PutToSleep(bool) signature.");
            yield return method;
        }

        private static IEnumerable<MethodBase> TargetMethods() => Targets();

        private static bool Prefix(AI_MartinCar __instance, ref IEnumerator __result)
        {
            if (!SharedMartinCarTickSleepScope.Suppress(__instance)) return true;
            __result = Empty();
            return false;
        }

        private static IEnumerator Empty() { yield break; }
    }
}
