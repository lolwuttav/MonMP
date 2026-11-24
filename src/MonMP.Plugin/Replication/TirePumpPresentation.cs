using System;
using System.Collections.Generic;
using System.Linq;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Host-confirmed cues only. Each actor owns one finite spatial emitter; refreshes keep a
    // loop alive without restarting it, while missing release/departure packets safely expire.
    internal static class TirePumpPresentation
    {
        internal const byte Stop = 0, Stroke = 1, Inflate = 2, Deflate = 3, Pop = 4;
        private sealed class Cue
        {
            internal AudioSource Source = null!;
            internal byte Kind;
            internal float Until;
        }
        private static readonly Dictionary<uint, Cue> cues = new Dictionary<uint, Cue>();
        private static readonly Dictionary<uint, (int Starts, int Stops)> counts = new Dictionary<uint, (int, int)>();

#if MONMP_DEVELOPMENT
        internal static (bool Playing, int Starts, int Stops) TestActor(uint actor)
        {
            counts.TryGetValue(actor, out var count);
            return (cues.TryGetValue(actor, out var cue) && cue.Source != null && cue.Source.isPlaying, count.Starts, count.Stops);
        }


        internal static byte TestKind(uint actor) => cues.TryGetValue(actor, out var cue) ? cue.Kind : Stop;
#endif

        internal static void Play(PresentationEvent e)
        {
            if (e.Variant > Pop) return;
            if (e.Variant == Stop) { Remove(e.Actor); return; }
            var plugin = MonMPPlugin.Instance;
            var at = new Vector3(e.X, e.Y, e.Z);
            var viewer = plugin?.Gameplay?.PlayerWalking;
            if (viewer == null || Vector3.Distance(viewer.transform.position, at) > 30f) { Remove(e.Actor); return; }
            var sound = Singleton<SOUND_Manager>.i;
            if (sound == null) return;
            bool loop = e.Variant == Inflate || e.Variant == Deflate;
            if (cues.TryGetValue(e.Actor, out var existing) && existing.Kind == e.Variant && loop && existing.Source != null)
            {
                existing.Until = Time.unscaledTime + .8f;
                existing.Source.transform.position = at;
                return;
            }
            Remove(e.Actor);
            if (cues.Count >= 16) return;
            var holder = new GameObject("MonMP tire pump cue " + e.Actor);
            holder.transform.position = at;
            var clip = e.Variant == Stroke ? sound.Tool_TirePump : e.Variant == Pop ? sound.Tool_TirePump_Explosion : sound.Tool_TirePumpAirOut;
            if (clip == null) { UnityEngine.Object.Destroy(holder); return; }
            float volume = e.Variant == Inflate ? .06f : e.Variant == Deflate ? .1f : .2f;
            float pitch = e.Variant == Inflate ? .7f : e.Variant == Deflate ? 1.1f : 1f;
            var source = holder.AddComponent<SOUND_Base>().InitializeSoundComponent(clip, volume, pitch, loop, 1f, false, null, 30f);
            source.Play();
            cues[e.Actor] = new Cue { Source = source, Kind = e.Variant,
                Until = Time.unscaledTime + (loop ? .8f : clip.length / pitch + .1f) };
            counts.TryGetValue(e.Actor, out var count);
            counts[e.Actor] = (count.Starts + 1, count.Stops);
        }
        internal static void Tick()
        {
            foreach (var actor in cues.Keys.ToArray())
                if (cues[actor].Source == null || Time.unscaledTime >= cues[actor].Until) Remove(actor);
        }
        internal static void Remove(uint actor)
        {
            if (!cues.TryGetValue(actor, out var cue)) return;
            cues.Remove(actor);
            try
            {
                if (cue.Source != null)
                {
                    var holder = cue.Source.gameObject;
                    try { cue.Source.Stop(); }
                    finally { if (holder != null) UnityEngine.Object.Destroy(holder); }
                }
            }
            finally
            {
                counts.TryGetValue(actor, out var count); counts[actor] = (count.Starts, count.Stops + 1);
            }
        }
        internal static void Reset()
        {
            var failures = new List<Exception>();
            try
            {
                foreach (var actor in cues.Keys.ToArray())
                    try { Remove(actor); } catch (Exception ex) { failures.Add(ex); }
            }
            finally { cues.Clear(); counts.Clear(); }
            if (failures.Count != 0) throw new AggregateException("Tire pump cue cleanup failed.", failures);
        }
    }
}
