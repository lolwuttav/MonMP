using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Exercise the real relay's departure paths with an identity absent from live membership.
    // Queued entries are staged and restored synchronously; none is sent to another peer.
    internal static class PresentationDepartureProbe
    {
        internal static void Verify(MonMPPlugin plugin, Gameplay gameplay)
        {
            var events = plugin.World!.Events!;
            const uint departed = uint.MaxValue;
            Require(!plugin.Session!.Peers.ContainsKey(departed), "Fixture identity unexpectedly belongs to a live peer.");
            var pending = (Queue<PresentationEvent>)AccessTools.Field(typeof(SharedPresentationEvents), "pending").GetValue(events);
            var prior = pending.ToArray();
            var at = gameplay.Player.transform.position;
            var start = new PresentationEvent(1, (byte)PresentationKind.BoltWork,
                (byte)PresentationReplays.BoltCue.ScrewStart, departed, 0, at.x, at.y, at.z, 0);
            var stop = start.WithSequence(2, departed);
            var worldCue = new PresentationEvent(3, (byte)PresentationKind.MachineDone, 0, departed, 0, at.x, at.y, at.z, 0);
            var other = start.WithSequence(4, 0);
            int played = events.TestPlayed;
            var source = AccessTools.Field(typeof(SOUND_General), "AdjustableSpanner_Screw")
                .GetValue(Singleton<SOUND_Manager>.i.GetComponent<SOUND_General>()) as AudioSource;
            Require(source != null, "Native spanner loop source is absent.");
            var before = SpatialSounds.TestLoop(source!, departed);
            try
            {
                AccessTools.Method(typeof(SharedPresentationEvents), "Play").Invoke(events, new object[] { start });
                var after = SpatialSounds.TestLoop(source!, departed);
                Require(events.TestPlayed == played && after.Starts == before.Starts && !after.Playing,
                    "A delivered start recreated a native owner loop after the actor left.");
                pending.Clear(); pending.Enqueue(start); pending.Enqueue(worldCue); pending.Enqueue(other); pending.Enqueue(stop);
                AccessTools.Method(typeof(SharedPresentationEvents), "OnLeft").Invoke(events, new object[] { departed });
                var remaining = pending.ToArray();
                Require(remaining.Length == 2 && remaining[0].Kind == worldCue.Kind && remaining[0].Actor == departed &&
                    remaining[1].Actor == 0 && remaining[1].Sequence == other.Sequence,
                    "Departure retained owned cues or removed/reordered world and other-player cues.");
                Debug.Log("MonMP presentation #334: departed actor cannot recreate native loop; queued owned cues purged while world/host cue order survives.");
            }
            finally
            {
                pending.Clear(); foreach (var cue in prior) pending.Enqueue(cue);
                SpatialSounds.StopOwner(departed);
            }
        }
        private static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Presentation departure #334: " + message); }
    }
}
