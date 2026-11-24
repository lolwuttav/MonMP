using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    internal static class GuestNpcMutationGuard
    {
        internal static bool Allow() => MonMPPlugin.Instance?.Session?.IsRunning != true || MonMPPlugin.Instance.Session.IsHost;
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.MouseDown))]
    internal static class SharedNpcTalkPatch
    {
        private static bool Prefix(Actor __instance)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            if (plugin.Session.IsHost) return plugin.World?.NpcInteractions?.BeginClick(__instance) ?? true;
            return plugin.World?.NpcInteractions?.BeginClick(__instance) == true;
        }

        private static void Postfix(Actor __instance) =>
            MonMPPlugin.Instance?.World?.NpcInteractions?.EndClick(__instance);
    }

    [HarmonyPatch(typeof(UI_GenericWindowManager), nameof(UI_GenericWindowManager.OpenDialogs))]
    internal static class SharedNpcRemoteDialogPatch
    {
        private static bool Prefix(Actor _actor, bool FriendshipGotUp) =>
            MonMPPlugin.Instance?.World?.NpcInteractions?.RedirectRemoteDialog(_actor, FriendshipGotUp) != true;
    }

    [HarmonyPatch(typeof(UI_GenericWindowManager), nameof(UI_GenericWindowManager.OpenGilles))]
    internal static class SharedNpcRemoteGillesWindowPatch
    {
        private static bool Prefix() =>
            MonMPPlugin.Instance?.World?.NpcInteractions?.RedirectRemoteGillesWindow() != true;
    }

    // Native dialog Open calls the virtual greeting again on the viewing player.
    // While opening a host-authorized option, reuse the host's captured text so
    // Francis's/Normand's greeting effects cannot execute on the guest.
    [HarmonyPatch]
    internal static class SharedNpcGuestDialogGreetingPatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(Actor), nameof(Actor.Greetings), new[] { typeof(bool) });
            yield return AccessTools.Method(typeof(Actor_Francis), nameof(Actor.Greetings), new[] { typeof(bool) });
            yield return AccessTools.Method(typeof(Actor_Normand), nameof(Actor.Greetings), new[] { typeof(bool) });
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static bool Prefix(Actor __instance, ref string __result)
        {
            var interactions = MonMPPlugin.Instance?.World?.NpcInteractions;
            if (interactions == null || !interactions.GuestDialogGreeting(__instance, out string text)) return true;
            __result = text; return false;
        }
    }

    [HarmonyPatch]
    internal static class SharedNpcGuestDialogChoicePatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(Actor_Francis), nameof(Actor.PlayerClickedOnDialog));
            yield return AccessTools.Method(typeof(Actor_Normand), nameof(Actor.PlayerClickedOnDialog));
            yield return AccessTools.Method(typeof(Actor_Gilles), nameof(Actor.PlayerClickedOnDialog));
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        private static bool Prefix(Actor __instance, ref string __result)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true || plugin.Session.IsHost) return true;
            __result = "This dialog option is not available right now.";
            plugin.World?.NpcInteractions?.GuestDialogChoice(__instance, out __result);
            return false;
        }
    }

    [HarmonyPatch(typeof(Actor), "TalkOverHead")]
    internal static class SharedNpcTalkFeedbackCapturePatch
    {
        private static void Postfix(Actor __instance, string text) =>
            MonMPPlugin.Instance?.World?.NpcInteractions?.CaptureGreeting(__instance, text);
    }

    // Actor gifts and weed sales use a native collider callback rather than the
    // ordinary mouse dispatch. Giftable shared items route through the host; all
    // other guest-side actor trigger actions remain suppressed.
    [HarmonyPatch(typeof(Actor), "OnTriggerEnter")]
    internal static class GuestNpcTriggerGuardPatch
    {
        private static bool Prefix(Actor __instance, Collider other)
        {
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true) return true;
            return plugin.World?.NpcInteractions?.BeginActorItemTrigger(__instance, other) ?? plugin.Session.IsHost;
        }
    }

    // The voice clip of a greeting or reaction the host ran, for everyone else (#13).
    [HarmonyPatch(typeof(Actor), nameof(Actor.ActorVoice_Play))]
    internal static class NpcVoiceRelayPatch
    {
        private static void Postfix(Actor __instance, AudioClip clip) =>
            MonMPPlugin.Instance?.World?.NpcInteractions?.RelayVoice(__instance, clip);
    }

    // Native product comments call AudioSource.PlayOneShot directly, bypassing ActorVoice_Play.
    [HarmonyPatch(typeof(SOUND_ActorVoices), nameof(SOUND_ActorVoices.CommentingOnProduct))]
    internal static class NpcProductCommentRelayPatch
    {
        private static void Postfix(Actor actor, int __result) =>
            MonMPPlugin.Instance?.World?.NpcInteractions?.RelayProductComment(actor, __result);
    }

    // A reaction the host runs for a guest (or any other remote action) earns the host no achievement.
    [HarmonyPatch(typeof(Steam_AchievementManager), nameof(Steam_AchievementManager.AchievementDone))]
    internal static class RemoteActionAchievementPatch
    {
        private static bool Prefix() => !RemoteActionScope.Active;
    }

    [HarmonyPatch]
    internal static class GuestNpcMoodAndFriendshipGuardPatch
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            foreach (Type type in typeof(Actor).Assembly.GetTypes())
            {
                if (type != typeof(Actor) && !typeof(Actor).IsAssignableFrom(type)) continue;
                foreach (string name in new[] { nameof(Actor.PlayerSpitOn), nameof(Actor.PlayerSmokedOn), nameof(Actor.PlayerVomitOn) })
                {
                    MethodInfo? method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (method != null && method.ReturnType == typeof(void) && method.GetParameters().Length == 0) yield return method;
                }
            }
        }
        private static IEnumerable<MethodBase> TargetMethods() => Targets();
        // A guest's spit, smoke or vomit reaches the host, which runs the NPC's reaction (#10, #18).
        private static bool Prefix(Actor __instance, MethodBase __originalMethod)
        {
            if (GuestNpcMutationGuard.Allow()) return true;
            MonMPPlugin.Instance?.World?.NpcInteractions?.GuestReaction(__instance, __originalMethod.Name);
            return false;
        }
    }
}
