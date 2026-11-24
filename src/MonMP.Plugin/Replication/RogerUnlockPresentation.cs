using System;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Only the native host greeting's locked-to-open transition announces this completion. The
    // replicated Roger flag still restores/loads silently; genuine championship wins retain their
    // existing event relay. The native greeting scope lets that generic relay distinguish the calls.
    internal static class RogerUnlockPresentation
    {
        [ThreadStatic] private static int nativeDepth;
        internal static bool NativeGreetingActive => nativeDepth > 0;
        internal sealed class GreetingScope
        {
            internal readonly bool WasOpen, Active;
            internal readonly int PreviousDepth;
            internal bool Closed;
            internal GreetingScope(bool active)
            {
                WasOpen = GameManager.Get_RogerStoreIsOpen; Active = active; PreviousDepth = nativeDepth;
                if (active) nativeDepth++;
            }
        }

#if MONMP_DEVELOPMENT
        internal static int TestPosted { get; private set; }


        internal static int TestPlayed { get; private set; }


        internal static bool TestSoundObserved { get; private set; }
#endif


        internal static GreetingScope Begin()
        {
            var plugin = MonMPPlugin.Instance;
            return new GreetingScope(!SharedPresentationEvents.Replaying && plugin?.Session?.IsRunning == true && plugin.Session.IsHost);
        }
        internal static void Completed(Actor_Roger actor, GreetingScope? scope, string result)
        {
            try
            {
                var plugin = MonMPPlugin.Instance;
                if (scope == null || !scope.Active || scope.WasOpen || !GameManager.Get_RogerStoreIsOpen ||
                    actor == null || !actor.gameObject.scene.IsValid() || result != TEXT.Actors_Roger_StoreUnlocked ||
                    plugin?.Session?.IsRunning != true || !plugin.Session.IsHost || PresentationReplays.Events?.Live != true) return;
                ulong id = plugin.World?.NpcInteractions.ActorId(actor) ?? 0;
                if (id == 0) return; // An unregistered actor never receives an invented cue identity.
                PresentationReplays.Post(PresentationKind.RogerUnlock, 0, id, actor.transform.position);

#if MONMP_DEVELOPMENT
                TestPosted++;
#endif

            }
            finally { Close(scope); }
        }
        internal static void Close(GreetingScope? scope)
        {
            if (scope == null || scope.Closed) return;
            scope.Closed = true;
            if (scope.Active) nativeDepth = scope.PreviousDepth;
        }

        internal static void Play(PresentationEvent e)
        {
            if (e.Kind != (byte)PresentationKind.RogerUnlock || e.Variant != 0 || e.Value != 0 || e.Actor != 0) return;
            var plugin = MonMPPlugin.Instance;
            if (plugin?.Session?.IsRunning != true || plugin.Session.IsHost || plugin.World == null) return;
            var actor = plugin.World.NpcInteractions.ActorById(e.Target) as Actor_Roger;
            if (actor == null || !actor.gameObject.scene.IsValid()) return;
            var gameplay = plugin.Gameplay;
            if (gameplay == null || (gameplay.PlayerWalking.transform.position - actor.transform.position).sqrMagnitude > 30f * 30f) return;
            var sound = UnityEngine.Object.FindObjectOfType<SOUND_Manager>();
            var message = UnityEngine.Object.FindObjectOfType<UI_EventMessage>();
            // Spatial audio belongs to Roger; the native informational message belongs to the nearby
            // local viewer. Neither call mutates friendship, purchases or the shared unlock flag.
            // The generic race UI hook does not test the event-bus replay flag. Retain the Roger
            // context through this exact native Show too, so it cannot publish a second race message.
            var replay = new GreetingScope(true);
            try
            {
                int routed = SpatialSounds.Rerouted;
                using (SpatialSounds.At(actor.transform.position)) sound?.PlaySound_BuggyBuildCompleted();

#if MONMP_DEVELOPMENT
                TestSoundObserved |= SpatialSounds.Rerouted > routed;
#endif

                message?.Show(TEXT.Actors_Roger_StoreUnlocked_Info, actor.actor_Type.ToString(), 16f, UI_EventMessage.EventIconType.Talk);

#if MONMP_DEVELOPMENT
                TestPlayed++;
#endif

            }
            finally { Close(replay); }
        }
        internal static void Reset() { nativeDepth = 0;
#if MONMP_DEVELOPMENT
TestPosted = TestPlayed = 0;

 TestSoundObserved = false;
#endif
 }
    }
}
