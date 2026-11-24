using System;
using HarmonyLib;
using Leo;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(Leo_Manager), nameof(Leo_Manager.Update_NameToAddAfter))]
    internal static class SharedLeoNamePatch
    {
        private static void Postfix(Leo_Manager __instance, ref string __result)
        {
            var shared = MonMPPlugin.Instance?.World?.Leo;
            if (shared != null) __result = shared.GuestNameSuffix(__instance, __result);
        }
    }

    [HarmonyPatch(typeof(EscapeMenu_Friend_Slot), nameof(EscapeMenu_Friend_Slot.UpdateSlot))]
    internal static class SharedLeoFriendStatusPatch
    {
        private static void Postfix(Actor_Type ___actor_Type, UnityEngine.GameObject ___TIP_PettedToday, UnityEngine.GameObject ___TIP_NotPettedToday)
        {
            var shared = MonMPPlugin.Instance?.World?.Leo;
            if (___actor_Type != Actor_Type.Leo || shared == null || !shared.TryGuestUiState(out var state)) return;
            bool petted = GameManager.Get_Friendship_TalkedToday[(int)Actor_Type.Leo];
            if (___TIP_PettedToday != null) ___TIP_PettedToday.SetActive(state.IsTamed && petted);
            if (___TIP_NotPettedToday != null) ___TIP_NotPettedToday.SetActive(state.IsTamed && !petted);
        }
    }

    [HarmonyPatch(typeof(StateMachine), "Update")]
    internal static class SharedLeoAiUpdatePatch
    {
        private static bool Prefix(StateMachine __instance) =>
            MonMPPlugin.Instance?.World?.Leo.MirrorsNativeAi(__instance) != true;
    }

    [HarmonyPatch(typeof(StateMachine), "LateUpdate")]
    internal static class SharedLeoAiPhysicsPatch
    {
        private static bool Prefix(StateMachine __instance) =>
            MonMPPlugin.Instance?.World?.Leo.MirrorsNativeAi(__instance) != true;
    }

    [HarmonyPatch(typeof(Leo_Manager), "Tick")]
    internal static class SharedLeoTickPatch
    {
        private static bool Prefix(Leo_Manager __instance) =>
            MonMPPlugin.Instance?.World?.Leo.MirrorsNativeTick(__instance) != true;
    }

    // Guests apply petting's personal relief locally and request the native shared
    // friendship/state effect from the host; orders also use validated host commands.
    [HarmonyPatch(typeof(Leo_Manager), nameof(Leo_Manager.MouseDown))]
    internal static class SharedLeoPetPatch
    {
        private static bool Prefix(Leo_Manager __instance)
        {
            if (!GuestLeo(__instance)) return true;
            // The guest's own half of petting: the heart over Leo and its calming effect on this
            // player's needs. The validated heart request applies friendship and Leo's state on the host.
            var shared = MonMPPlugin.Instance?.World?.Leo;
            if ((__instance.Get_IsTamed || shared?.SharedTamed == true) && shared?.SharedPetting != true && __instance.petHearth != null &&
                !__instance.petHearth.gameObject.activeSelf)
            {
                Replication.SharedLeoHearts.Pop(__instance.petHearth.gameObject);
                UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>()?.Pet_Leo();
                MonMPPlugin.Instance?.World?.LeoHearts?.Petted();
            }
            return false;
        }
        internal static bool GuestLeo(Leo_Manager native)
        {
            var leo = MonMPPlugin.Instance?.World?.Leo;
            // An unregistered or duplicate Leo is an unsafe native identity too.
            return native != null && leo?.GuestBlocks == true;
        }
    }

    [HarmonyPatch(typeof(Leo_Manager), nameof(Leo_Manager.Use))]
    internal static class SharedLeoOrderPatch
    {
        private static bool Prefix(Leo_Manager __instance, ref bool __result, out IDisposable? __state)
        {
            __state = null;
            if (!SharedLeoPetPatch.GuestLeo(__instance))
            {
                __state = MonMPPlugin.Instance?.World?.Leo?.BeginHostOrder(__instance);
                return true;
            }
            // A guest's follow / stay-home order goes to the host's Leo (#251).
            MonMPPlugin.Instance?.World?.Leo?.RequestOrder();
            __result = false; return false;
        }
        private static void Postfix(IDisposable? __state) => __state?.Dispose();
        private static Exception? Finalizer(Exception? __exception, IDisposable? __state)
        { __state?.Dispose(); return __exception; }
    }

    // The hover prompt for the order a guest's Use sends, read from the shared Leo (#251).
    [HarmonyPatch(typeof(Leo_Manager), nameof(Leo_Manager.MouseHover))]
    internal static class SharedLeoHoverPatch
    {
        private static void Postfix(Leo_Manager __instance)
        {
            if (SharedLeoPetPatch.GuestLeo(__instance)) MonMPPlugin.Instance?.World?.Leo?.GuestHover(__instance);
        }
    }

    // Roaming near a guest who ordered Leo to follow: the native spot to wander to is behind the host's
    // player, which pulled him back to the host every few seconds.
    [HarmonyPatch(typeof(RoamingPlayer), "GoToRandomDestination")]
    internal static class SharedLeoRoamLeaderPatch
    {
        private static readonly AccessTools.FieldRef<RoamingPlayer, UnityEngine.Vector3> Destination =
            AccessTools.FieldRefAccess<RoamingPlayer, UnityEngine.Vector3>("RandomDestination");
        private static void Postfix(RoamingPlayer __instance)
        {
            var shared = MonMPPlugin.Instance?.World?.Leo;
            if (shared != null && shared.TryLeaderRoamDestination(out var destination)) Destination(__instance) = destination;
        }
    }

    // #251: taming Leo with treats as a guest. The untamed states check the host's hands; while a guest
    // is the lurer (SharedLeo), these checks read that guest's hands on the host instead.
    [HarmonyPatch(typeof(HidingSpotState), nameof(HidingSpotState.Tick))]
    internal static class SharedLeoHidingLurePatch
    {
        private static readonly AccessTools.FieldRef<HidingSpotState, ScaredSM> Sm = AccessTools.FieldRefAccess<HidingSpotState, ScaredSM>("_sm");
        private static bool Prefix(HidingSpotState __instance)
        {
            var leo = MonMPPlugin.Instance?.World?.Leo;
            if (leo?.GuestLuring != true) return true;
            var sm = Sm(__instance);
            if (!GameManager.Get_Friendship_SyrupGiftedToday[24] && UnityEngine.Vector3.Distance(sm.transform.position, sm.player.position) < 40f && leo.LurerHoldsTreats)
            {
                sm.leo_Manager.SOUND_Leo.Play_LeoSniffing();
                sm.ChangeState(sm.seakingToPlayerState);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(SeakingToPlayerState), nameof(SeakingToPlayerState.Tick))]
    internal static class SharedLeoSeekLurePatch
    {
        internal static readonly AccessTools.FieldRef<SeakingToPlayerState, ScaredSM> Sm = AccessTools.FieldRefAccess<SeakingToPlayerState, ScaredSM>("_sm");
        internal static readonly AccessTools.FieldRef<SeakingToPlayerState, bool> Tamed = AccessTools.FieldRefAccess<SeakingToPlayerState, bool>("isTamed");
        internal static readonly AccessTools.FieldRef<SeakingToPlayerState, bool> GoToPlayer = AccessTools.FieldRefAccess<SeakingToPlayerState, bool>("GoToPlayer");
        internal static readonly AccessTools.FieldRef<SeakingToPlayerState, float> ReturnTimer = AccessTools.FieldRefAccess<SeakingToPlayerState, float>("Timer_TimeBeforeReturnHiding");
        internal static readonly AccessTools.FieldRef<SeakingToPlayerState, float> GoToBedTimer = AccessTools.FieldRefAccess<SeakingToPlayerState, float>("Timer_Tamed_TimeGoToCarBed");
        internal static readonly AccessTools.FieldRef<SeakingToPlayerState, float> BringBedTimer = AccessTools.FieldRefAccess<SeakingToPlayerState, float>("Timer_Tamed_TimeBringBackBed");
        internal static readonly AccessTools.FieldRef<SeakingToPlayerState, UnityEngine.Transform> HidingSpot = AccessTools.FieldRefAccess<SeakingToPlayerState, UnityEngine.Transform>("HidingSpot");
        private static bool Prefix(SeakingToPlayerState __instance)
        {
            var leo = MonMPPlugin.Instance?.World?.Leo;
            if (leo?.GuestLuring != true || Tamed(__instance)) return true;
            var sm = Sm(__instance);
            bool flag = leo.LurerHoldsTreats;
            if (flag && UnityEngine.Vector3.Distance(sm.player.position, sm.transform.position) > 50f) flag = false;
            if (flag && UnityEngine.Vector3.Distance(sm.transform.position, HidingSpot(__instance).position) > 45f) flag = false;
            if (!flag && GoToPlayer(__instance))
            {
                ReturnTimer(__instance) = 7f;
                sm.animator.SetFloat("Forward", 0f); sm.animator.SetFloat("Turn", 0f);
                sm.agent.enabled = false;
            }
            if (flag && !GoToPlayer(__instance)) { ReturnTimer(__instance) = 7f; sm.agent.enabled = true; }
            GoToPlayer(__instance) = flag;
            return false;
        }
    }

    // Within 3 m of a luring guest: Leo eats the guest's treats (or, at the eighth day of friendship,
    // is tamed and fetches his bed to that guest) exactly as the native branch does for the host.
    [HarmonyPatch(typeof(SeakingToPlayerState), nameof(SeakingToPlayerState.UpdateLogic))]
    internal static class SharedLeoEatLurePatch
    {
        private static bool Prefix(SeakingToPlayerState __instance)
        {
            var leo = MonMPPlugin.Instance?.World?.Leo;
            if (leo?.GuestLuring != true || SharedLeoSeekLurePatch.Tamed(__instance)) return true;
            var sm = SharedLeoSeekLurePatch.Sm(__instance);
            if (UnityEngine.Vector3.Distance(sm.player.position, sm.transform.position) >= 3f) return true;
            var manager = sm.leo_Manager;
            if (leo.LurerHoldsTreats)
            {
                manager.SOUND_Leo.Stop_LeoSniffing();
                if (!GameManager.Get_Friendship_SyrupGiftedToday[24])
                {
                    GameManager.Get_Friendship[24]++;
                    manager.uI_FrontLeftPopup.Friendship(Actor_Type.Leo);
                    manager.petHearth.gameObject.SetActive(true);
                }
                GameManager.Get_Friendship_SyrupGiftedToday[24] = true;
                if (GameManager.Get_Friendship[24] >= 8)
                {
                    Singleton<UI_EventMessage>.i.Show(TEXT.GetText("Leo_SuccessfullyTamed"), "", 10f, UI_EventMessage.EventIconType.Heart);
                    Singleton<SOUND_Manager>.i.PlaySound_BuggyBuildCompleted();
                    SharedLeoSeekLurePatch.Tamed(__instance) = true;
                    SharedLeoSeekLurePatch.GoToBedTimer(__instance) = 15f;
                    SharedLeoSeekLurePatch.BringBedTimer(__instance) = 15f;
                    sm.agent.enabled = true;
                    sm.agent.SetDestination(SharedLeoSeekLurePatch.HidingSpot(__instance).position);
                    sm.agent.speed = 8f;
                    return false;
                }
                leo.ConsumeLurerTreat();
                manager.SOUND_Leo.Play_Leo_BiteChomp();
                manager.TreatInMouth.SetActive(true);
            }
            sm.ChangeState(sm.goHidingSpotState);
            return false;
        }
    }

    [HarmonyPatch(typeof(Leo_Manager), nameof(Leo_Manager.TreatGiven))]
    internal static class SharedLeoTreatPatch
    {
        private static bool Prefix(Leo_Manager __instance, out bool __state)
        {
            __state = Gifted();
            return !SharedLeoPetPatch.GuestLeo(__instance);
        }

        // A treat the host's tamed Leo just ate, for every other player (#22).
        private static void Postfix(Leo_Manager __instance, bool __state)
        {
            if (!__state && Gifted() && !SharedLeoPetPatch.GuestLeo(__instance) && __instance.Get_IsTamed)
                MonMPPlugin.Instance?.World?.Leo?.HostTreatEaten();
        }

        private static bool Gifted() => GameManager.Get_Friendship_SyrupGiftedToday is bool[] gifted && gifted.Length > 24 && gifted[24];
    }

    [HarmonyPatch(typeof(Leo_Manager), nameof(Leo_Manager.CarBed))]
    internal static class SharedLeoCarBedPatch
    {
        private static bool Prefix(Leo_Manager __instance, CarBed carBed, out IDisposable? __state)
        {
            __state = null;
            // The host's own car bed: Leo leaves it following the host, and the native range check
            // measures from the host rather than a client who last led him.
            if (!SharedLeoPetPatch.GuestLeo(__instance)) { __state = MonMPPlugin.Instance?.World?.Leo?.BeginHostOrder(__instance); return true; }
            MonMPPlugin.Instance?.World?.Leo.RequestCarBed(carBed);
            return false;
        }
        private static void Postfix(IDisposable? __state) => __state?.Dispose();
        private static Exception? Finalizer(Exception? __exception, IDisposable? __state)
        { __state?.Dispose(); return __exception; }
    }

    [HarmonyPatch(typeof(Leo_Manager), nameof(Leo_Manager.PlayerIsSleeping))]
    internal static class SharedLeoBedPatch
    {
        private static bool Prefix(Leo_Manager __instance, out IDisposable? __state)
        {
            __state = null;
            if (SharedLeoPetPatch.GuestLeo(__instance)) return false;
            // Leo sleeps beside the host; led by a client, the native checks (within 100 m, out of bed
            // once the player is 3 m away) measured from that client and he jumped straight out.
            __state = MonMPPlugin.Instance?.World?.Leo?.BeginHostOrder(__instance);
            return true;
        }
        private static void Postfix(IDisposable? __state) => __state?.Dispose();
        private static Exception? Finalizer(Exception? __exception, IDisposable? __state)
        { __state?.Dispose(); return __exception; }
    }

    [HarmonyPatch(typeof(Leo_Manager), nameof(Leo_Manager.LeoTamed))]
    internal static class SharedLeoTamePatch
    {
        private static bool Prefix(Leo_Manager __instance) => !SharedLeoPetPatch.GuestLeo(__instance);
    }
}
