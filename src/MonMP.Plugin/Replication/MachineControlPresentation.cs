using System;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The click/pull is local feedback, not an engine-start prediction. Host fuel, native
    // starting-coroutine and switch rules still decide whether the engine actually starts.
    internal static class MachineControlPresentation
    {
        private static readonly FieldInfo PullCord = AccessTools.Field(typeof(SOUND_WoodSplitter), "PullCord");
        private static PullScope? currentPull;
        private static Furniture_SwitchButton? currentSwitch;

        private sealed class PullScope : IDisposable
        {
            internal readonly SOUND_WoodSplitter? Sound;
            internal readonly ulong Target;
            internal readonly uint Actor;
            private readonly PullScope? previous;
            internal PullScope(SOUND_WoodSplitter? sound, ulong target, uint actor)
            { Sound = sound; Target = target; Actor = actor; previous = currentPull; currentPull = this; }
            public void Dispose() => currentPull = previous;
        }

        internal static IDisposable HostPull(SOUND_WoodSplitter? sound, ulong target, uint actor) => new PullScope(sound, target, actor);

        private sealed class SwitchScope : IDisposable
        {
            private readonly Furniture_SwitchButton? previous = currentSwitch;
            internal SwitchScope(Furniture_SwitchButton button) => currentSwitch = button;
            public void Dispose() => currentSwitch = previous;
        }
        internal static IDisposable NativeSwitch(Furniture_SwitchButton button) => new SwitchScope(button);
        internal static bool SwitchAlreadyPlayed(Furniture_SwitchButton? button) => button != null && ReferenceEquals(currentSwitch, button);

        // The native coroutine advances its first step inside StartCoroutine. Only the exact
        // sound component in that host request may inherit the requester; another machine's
        // simultaneous pull remains a distinct host action and is heard by every client.
        internal static bool NativePull(SOUND_WoodSplitter sound)
        {
            var scope = currentPull;
            if (scope == null || !ReferenceEquals(scope.Sound, sound) || PresentationReplays.Events?.IsHost != true) return false;
            PresentationReplays.Events.HostAnnounce(PresentationKind.MachinePullStart, 0, scope.Target,
                sound.transform.position, 0, scope.Actor, playHere: false);
            return true;
        }

        internal static bool LocalPull(SOUND_WoodSplitter? sound)
        {
            if (sound == null || !sound.gameObject.activeInHierarchy || PullCord.GetValue(sound) is not AudioSource source || source.clip == null)
                return false; // Native Start has not initialized this source yet; retain the host cue.
            using (SpatialSounds.Local()) sound.PullStart();
            return true;
        }

        internal static bool LocalSwitch(bool on)
        {
            if (Singleton<SOUND_Manager>.i == null) return false;
            using (SpatialSounds.Local()) SwitchSound(on);
            return true;
        }

        internal static void AnnounceSwitch(ulong target, bool on, Vector3 at, uint actor = 0) =>
            PresentationReplays.Events?.HostAnnounce(PresentationKind.MachineControl, on ? (byte)1 : (byte)0,
                target, at, 0, actor, playHere: false);

        internal static void PlaySwitch(PresentationEvent e)
        {
            if (e.Kind != (byte)PresentationKind.MachineControl || e.Variant > 1) return;
            var world = MonMPPlugin.Instance?.World;
            if (world == null || !(world.Generator.TrySound(e.Target, out var sound) || world.WoodSplitters.TrySound(e.Target, out sound)) || sound == null) return;
            using (SpatialSounds.At(sound.transform.position)) SwitchSound(e.Variant == 1);
        }

        // A nonzero target is an exact machine binding; never fall back to a nearby machine
        // if a bought splitter has not appeared yet or the original was removed.
        internal static bool PlayPull(PresentationEvent e)
        {
            if (e.Target == 0) return false;
            var world = MonMPPlugin.Instance?.World;
            if (world != null && (world.Generator.TrySound(e.Target, out var sound) || world.WoodSplitters.TrySound(e.Target, out sound)) && sound != null)
                sound.PullStart();
            return true;
        }

        private static void SwitchSound(bool on)
        {
            var sound = Singleton<SOUND_Manager>.i;
            if (sound == null) return;
            if (on) sound.PlaySound_SwitchOn(); else sound.PlaySound_SwitchOff();
        }
    }
}
