using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch]
    internal static class SharedActorRouteAuthorityPatch
    {
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(AI_Actor_FollowRoute), "Update")!;
            yield return AccessTools.Method(typeof(AI_Actor_FollowRoute), "StartWalking", new[] { typeof(int), typeof(bool) })!;
            yield return AccessTools.Method(typeof(AI_Actor_FollowRoute), "StopWalking")!;
            yield return AccessTools.Method(typeof(Actor), "OnEnable", System.Type.EmptyTypes)!;
        }

        internal static IEnumerable<MethodInfo> Targets() => TargetMethods().Cast<MethodInfo>()
            .Concat(new[] { AccessTools.Method(typeof(AI_Actor_FollowRoute), "Start")! });

        private static bool Prefix(object __instance, MethodBase __originalMethod)
        {
            var routes = MonMPPlugin.Instance?.World?.ActorRoutes;
            if (__instance is AI_Actor_FollowRoute controller) return routes?.FreezesRouteAi(controller) != true;
            if (__instance is Actor actor && __originalMethod.Name == "OnEnable") return routes?.SuppressActorEnable(actor) != true;
            return true;
        }
    }

    // Keep the state/finalizer on activation only, not on each route AI Update.
    [HarmonyPatch(typeof(Actor), "OnEnable")]
    internal static class SharedStoreActorEnablePatch
    {
        internal readonly struct EnableState
        {
            private readonly int index;
            private readonly bool[]? talked, bought, gifted;
            private readonly bool wasTalked, wasBought, wasGifted;
            internal EnableState(Actor actor)
            {
                index = (int)actor.actor_Type;
                talked = GameManager.Get_Friendship_TalkedToday; bought = GameManager.Get_Friendship_BoughtWeedToday;
                gifted = GameManager.Get_Friendship_SyrupGiftedToday;
                wasTalked = Read(talked, index); wasBought = Read(bought, index); wasGifted = Read(gifted, index);
            }
            private static bool Read(bool[]? values, int at) => values != null && (uint)at < values.Length && values[at];
            private static void Write(bool[]? values, int at, bool value) { if (values != null && (uint)at < values.Length) values[at] = value; }
            internal void Restore()
            { Write(talked, index, wasTalked); Write(bought, index, wasBought); Write(gifted, index, wasGifted); }
        }

        private static void Prefix(Actor __instance, out EnableState __state)
        {
            __state = default;
            var world = MonMPPlugin.Instance?.World;
            // Preserve authoritative daily flags while allowing native
            // OnEnable to restore the actor's seated/standing animation.
            if (world?.Clock.Stores?.Applying == true) __state = new EnableState(__instance);
        }
        private static void Finalizer(EnableState __state) => __state.Restore();
    }
    [HarmonyPatch(typeof(AI_Actor_FollowRoute), "Start")]
    internal static class SharedActorRouteStartPatch
    {
        private static void Postfix(AI_Actor_FollowRoute __instance) =>
            MonMPPlugin.Instance?.World?.ActorRoutes?.NativeRouteStarted(__instance);
    }

}
