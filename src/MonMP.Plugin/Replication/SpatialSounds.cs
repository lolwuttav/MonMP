using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Mon Bazou plays most interaction sounds (tire pump, part installed, switches, cash, ...) through
    // 2D sources on SOUND_General, because natively only the local player can cause them. Replaying
    // another player's action runs the same native code, so those sounds would land at full volume in
    // every player's ears wherever they are. While a scope is open, 2D plays are rerouted to a pooled 3D
    // emitter at the acting player (or the object), muted, or left alone, innermost scope first.
    internal static class SpatialSounds
    {
        private enum Kind : byte { Local, At, Mute }
        private readonly struct Frame
        {
            public readonly Kind Kind; public readonly Vector3 Position;
            // Who the sounds belong to (peer + 1; 0: unknown). Two players screwing bolts at once
            // replay the same native loop source; each needs its own emitter.
            public readonly uint Owner;
            public readonly bool ExpireLoop;
            public Frame(Kind kind, Vector3 position, uint owner = 0, bool expireLoop = false)
            { Kind = kind; Position = position; Owner = owner; ExpireLoop = expireLoop; }
        }
        private sealed class Scope : IDisposable
        {
            private readonly int depth; private bool disposed;
            public Scope(int depth) { this.depth = depth; }
            public void Dispose()
            {
                if (disposed) return; disposed = true;
                if (frames.Count > depth) frames.RemoveRange(depth, frames.Count - depth);
            }
        }

        private const float MaxDistance = 30f;
        private const int PoolLimit = 24;
        // Match SOUND_Manager.InitializeSoundComponent when the original caller has no source.
        // A lower-priority relay could otherwise disappear behind nearby native sound voices.
        private const int NativePriority = 20;
        private static readonly List<Frame> frames = new List<Frame>();
        private static readonly List<AudioSource> pool = new List<AudioSource>();
        private static readonly Dictionary<(AudioSource Native, uint Owner), AudioSource> loops = new Dictionary<(AudioSource, uint), AudioSource>();
        private static readonly PresentationLoopLifetime<(AudioSource, uint)> loopLifetimes = new PresentationLoopLifetime<(AudioSource, uint)>();
        private static readonly Dictionary<(AudioSource Native, uint Owner), (int Starts, int Stops)> loopTransitions = new Dictionary<(AudioSource, uint), (int, int)>();

#if MONMP_DEVELOPMENT
        internal static (bool Active, bool Playing, int Starts, int Stops) TestLoop(AudioSource source, uint actor)
        {
            var key = (source, actor + 1);
            loopTransitions.TryGetValue(key, out var counts);
            bool active = loops.TryGetValue(key, out var emitter) && emitter != null;
            return (active, active && emitter!.isPlaying, counts.Starts, counts.Stops);
        }
#endif

        private static AudioSource? overflow;
        private static GameObject? host;

        // Set by the plugin: a connected player's avatar position, and the remote avatar closest to a point.
        public static Func<uint, Vector3?>? PeerPosition;
        public static Func<Vector3, Vector3?>? NearestRemote;
        internal static int Rerouted { get; private set; }
        internal static int Muted { get; private set; }
        internal static int Exhausted { get; private set; }

        public static IDisposable At(Vector3 position) => Push(new Frame(Kind.At, position));
        // A replayed action of a known player: loops it starts are that player's alone.
        public static IDisposable At(Vector3 position, uint actor, bool expireLoop = false) => Push(new Frame(Kind.At, position, actor + 1, expireLoop));
        public static IDisposable Mute() => Push(new Frame(Kind.Mute, default));
        // Sounds that belong to this player (a shared to-do completion, a reply to their own request).
        public static IDisposable Local() => Push(new Frame(Kind.Local, default));
        public static IDisposable AtPeer(uint peer)
        {
            var position = PeerPosition?.Invoke(peer);
            return position.HasValue ? At(position.Value) : Remote();
        }
        // The acting player is unknown (a replicated change): the nearest other player is the best guess,
        // and with two players it is exactly right.
        public static IDisposable Remote()
        {
            var position = NearestRemote?.Invoke(Listener());
            return position.HasValue ? At(position.Value) : Mute();
        }

        private static IDisposable Push(Frame frame) { frames.Add(frame); return new Scope(frames.Count - 1); }

        private static bool Active(out Frame frame)
        {
            frame = default;
            if (frames.Count == 0) return false;
            frame = frames[frames.Count - 1];
            return frame.Kind != Kind.Local;
        }

        private static bool Flat(AudioSource? source) => source != null && source.spatialBlend < 0.5f;

        private static Vector3 Listener()
        {
            var camera = Camera.main;
            if (camera != null) return camera.transform.position;
            try { var player = Singleton<Gameplay>.i?.Player; if (player != null) return player.transform.position; } catch { }
            return Vector3.zero;
        }

        // True when the native play should be skipped because it was rerouted or muted.
        internal static bool Reroute(AudioSource? source, AudioClip? clip, float volume, float pitch, out AudioSource? emitter)
        {
            emitter = null;
            if (clip == null || !Active(out var frame)) return false;
            if (source != null && !Flat(source)) return false;
            bool silent = frame.Kind == Kind.Mute || Vector3.Distance(Listener(), frame.Position) > MaxDistance + 5f;
            if (silent && source != null) { Muted++; return true; }
            emitter = Emitter(source);
            if (emitter == null)
            {
                // Every emitter is busy with a loop: stay silent rather than fall back to the native
                // full-volume 2D play. Manager callers get a silent handle they may keep.
                Exhausted++; Muted++;
                if (source == null) { emitter = Overflow(); emitter.clip = clip; emitter.volume = 0f; }
                return true;
            }
            // SOUND_Manager.PlayOneShot2D callers may keep and replay the source they get back.
            if (silent) { emitter.clip = clip; emitter.volume = 0f; Muted++; return true; }
            emitter.transform.position = frame.Position;
            emitter.clip = clip; emitter.loop = false; emitter.volume = volume; emitter.pitch = pitch; emitter.time = 0f;
            emitter.Play();
            Rerouted++;
            return true;
        }

        internal static bool RerouteLoop(AudioSource? source, bool restart)
        {
            if (source == null || source.clip == null || !Flat(source) || !Active(out var frame)) return false;
            if (frame.Kind == Kind.Mute) { Muted++; return true; }
            var key = (source, frame.Owner);
            if (!loops.TryGetValue(key, out var emitter) || emitter == null)
            {
                emitter = Emitter(source);
                if (emitter == null) { Exhausted++; Muted++; return true; }
                loops[key] = emitter;
            }
            emitter.transform.position = frame.Position;
            if (frame.ExpireLoop) loopLifetimes.Renew(key, Time.unscaledTime);
            if (!restart && emitter.isPlaying && emitter.clip == source.clip) return true;
            emitter.clip = source.clip; emitter.loop = source.loop; emitter.volume = source.volume; emitter.pitch = source.pitch;
            emitter.Play();

#if MONMP_DEVELOPMENT
            if (MonMPPlugin.Instance?.TestOptions.Enabled == true && emitter.isPlaying)
            {
                loopTransitions.TryGetValue(key, out var counts);
                loopTransitions[key] = (counts.Starts + 1, counts.Stops);
            }
#endif

            Rerouted++;
            return true;
        }

        // A pause/stop inside a scope ends the rerouted copy and leaves the local player's own source alone.
        internal static bool StopLoop(AudioSource? source)
        {
            if (source == null || !Active(out var frame)) return false;
            var key = (source, frame.Owner);
            if (loops.TryGetValue(key, out var emitter))
            {
                loopLifetimes.Remove(key);
                loops.Remove(key); if (emitter != null) emitter.Stop();

#if MONMP_DEVELOPMENT
                if (MonMPPlugin.Instance?.TestOptions.Enabled == true)
                {
                    loopTransitions.TryGetValue(key, out var counts);
                    loopTransitions[key] = (counts.Starts, counts.Stops + 1);
                }
#endif

                return true;
            }
            return Flat(source);
        }

        // A player left: end the loops their replayed actions started (a stop they never sent).
        internal static void StopOwner(uint peer)
        {
            uint owner = peer + 1;
            foreach (var key in new List<(AudioSource, uint)>(loops.Keys))
                if (key.Item2 == owner) { var emitter = loops[key]; loops.Remove(key); loopLifetimes.Remove(key); if (emitter != null) emitter.Stop(); }
        }

        internal static void Tick()
        {
            foreach (var key in loopLifetimes.Expire(Time.unscaledTime))
                if (loops.TryGetValue(key, out var emitter)) { loops.Remove(key); if (emitter != null) emitter.Stop(); }
        }

        private static AudioSource Overflow()
        {
            if (overflow == null)
            {
                if (host == null) { host = new GameObject("MonMP Spatial Sounds"); UnityEngine.Object.DontDestroyOnLoad(host); }
                var child = new GameObject("Overflow");
                child.transform.SetParent(host.transform, false);
                overflow = child.AddComponent<AudioSource>();
                overflow.playOnAwake = false; overflow.spatialBlend = 1f; overflow.maxDistance = MaxDistance;
            }
            overflow.Stop();
            return overflow;
        }

        private static AudioSource? Emitter(AudioSource? native)
        {
            if (host == null)
            {
                host = new GameObject("MonMP Spatial Sounds");
                UnityEngine.Object.DontDestroyOnLoad(host);
            }
            AudioSource? free = null;
            foreach (var candidate in pool)
                if (candidate != null && !candidate.isPlaying && !loops.ContainsValue(candidate)) { free = candidate; break; }
            if (free == null && pool.Count >= PoolLimit)
            {
                // Pool full: take over the one-shot closest to finishing; loops are never stolen.
                float best = -1f;
                foreach (var candidate in pool)
                {
                    if (candidate == null || loops.ContainsValue(candidate) || candidate.clip == null || candidate.clip.length <= 0f) continue;
                    float progress = candidate.time / candidate.clip.length;
                    if (progress > best) { best = progress; free = candidate; }
                }
                if (free == null) return null;
                free.Stop();
            }
            if (free == null)
            {
                var child = new GameObject("Emitter " + pool.Count);
                child.transform.SetParent(host.transform, false);
                free = child.AddComponent<AudioSource>();
                free.playOnAwake = false; free.spatialBlend = 1f; free.dopplerLevel = 0f;
                free.maxDistance = MaxDistance;
                try
                {
                    var assets = Singleton<GameAssets>.i;
                    if (assets != null && assets.AudioSourceCustomCurve != null)
                    {
                        free.SetCustomCurve(AudioSourceCurveType.CustomRolloff, assets.AudioSourceCustomCurve);
                        free.rolloffMode = AudioRolloffMode.Custom;
                    }
                    else free.rolloffMode = AudioRolloffMode.Linear;
                }
                catch { free.rolloffMode = AudioRolloffMode.Linear; }
                pool.Add(free);
            }
            free.priority = native?.priority ?? NativePriority;
            try { free.outputAudioMixerGroup = native?.outputAudioMixerGroup ?? Singleton<GameAssets>.i?.AUDIO_EffectMixer; } catch { }
            return free;
        }

        public static void Reset()
        {
            frames.Clear();
            foreach (var emitter in loops.Values) if (emitter != null) emitter.Stop();
            loops.Clear();
            loopLifetimes.Clear();
            loopTransitions.Clear();
        }
    }

    [HarmonyPatch(typeof(SOUND_Base), nameof(SOUND_Base.PlayOneShot), typeof(AudioSource), typeof(AudioClip), typeof(float), typeof(float))]
    internal static class SpatialOneShotPatch
    {
        private static bool Prefix(AudioSource audioSource, AudioClip audioClip, float volume, float pitch) =>
            !SpatialSounds.Reroute(audioSource, audioClip, volume, pitch, out _);
    }

    [HarmonyPatch(typeof(SOUND_Base), nameof(SOUND_Base.PlayOneShot), typeof(AudioSource))]
    internal static class SpatialClipOneShotPatch
    {
        private static bool Prefix(AudioSource audioSource) =>
            audioSource == null || !SpatialSounds.Reroute(audioSource, audioSource.clip, audioSource.volume, audioSource.pitch, out _);
    }

    [HarmonyPatch(typeof(SOUND_Base), nameof(SOUND_Base.PlayAudioSource), typeof(AudioSource), typeof(bool))]
    internal static class SpatialLoopPatch
    {
        private static bool Prefix(AudioSource audioSource, bool ReplayEvenIfAlreadyPlayer) =>
            !SpatialSounds.RerouteLoop(audioSource, ReplayEvenIfAlreadyPlayer);
    }

    [HarmonyPatch(typeof(SOUND_Base), nameof(SOUND_Base.PauseAudioSource))]
    internal static class SpatialPausePatch
    {
        private static bool Prefix(AudioSource audioSource) => !SpatialSounds.StopLoop(audioSource);
    }

    [HarmonyPatch(typeof(SOUND_Base), nameof(SOUND_Base.StopAudioSource))]
    internal static class SpatialStopPatch
    {
        private static bool Prefix(AudioSource audioSource) => !SpatialSounds.StopLoop(audioSource);
    }

    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.PlayOneShot2D))]
    internal static class SpatialManager2DPatch
    {
        private static bool Prefix(AudioClip _clip, bool _randomPitch, float _volume, float _pitch, ref AudioSource? __result)
        {
            float pitch = _randomPitch ? Mathf.Lerp(_pitch * 0.85f, _pitch * 1.15f, UnityEngine.Random.value) : 1f;
            if (!SpatialSounds.Reroute(null, _clip, _volume * 0.2f, pitch, out var emitter)) return true;
            __result = emitter;
            return false;
        }
    }
}
