using System.Collections.Generic;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Sound.SoundComponents;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class RemoteVehicleCrashSounds
    {
        private sealed class PendingCrash
        {
            internal readonly VehicleController Vehicle;
            internal readonly CrashComponent Component;
            internal readonly AudioSource Source;
            internal Vector3 Point;
            internal float RelativeSpeed;
            internal PendingCrash(VehicleController vehicle, CrashComponent component, Vector3 point, float speed)
            { Vehicle = vehicle; Component = component; Source = component.Source; Point = point; RelativeSpeed = speed; }
        }

        // Match CrashComponent.Update's one collisionFlag: keep the latest contact while its
        // source is busy, then play it when idle. Changing an already playing Source.clip lost
        // the new impact (SoundComponent.Play refuses it) and also changed the current sound.
        private static readonly List<PendingCrash> pending = new List<PendingCrash>();
        private sealed class LodPlayback
        {
            internal readonly VehicleController Vehicle;
            internal readonly CrashComponent Component;
            internal readonly AudioSource Source;
            internal readonly bool SourceEnabled;
            internal LodPlayback(VehicleController vehicle, CrashComponent component)
            { Vehicle = vehicle; Component = component; Source = component.Source; SourceEnabled = Source.enabled; }
        }
        private static readonly List<LodPlayback> lodPlaybacks = new List<LodPlayback>();
        private const int MaximumPendingVehicles = 64;

        internal static void Receive(VehicleController vehicle, Vector3 point, float relativeSpeed)
        {
            var crash = vehicle.soundManager?.crashComponent;
            if (!Ready(vehicle, crash)) { Forget(vehicle); return; }
            var current = Find(vehicle);
            if (!crash!.Source.isPlaying)
            {
                if (current != null) pending.Remove(current);
                Play(vehicle, crash, point, relativeSpeed);
                return;
            }
            if (current != null && ReferenceEquals(current.Component, crash) && ReferenceEquals(current.Source, crash.Source))
            { current.Point = point; current.RelativeSpeed = relativeSpeed; return; }
            if (current != null) pending.Remove(current);
            if (pending.Count == MaximumPendingVehicles) pending.RemoveAt(0);
            pending.Add(new PendingCrash(vehicle, crash, point, relativeSpeed));
        }

        internal static void Update()
        {
            for (int i = lodPlaybacks.Count - 1; i >= 0; i--)
            {
                var playback = lodPlaybacks[i];
                var vehicle = playback.Vehicle;
                var crash = vehicle != null ? vehicle.soundManager?.crashComponent : null;
                bool valid = Ready(vehicle, crash) && ReferenceEquals(playback.Component, crash) &&
                    ReferenceEquals(playback.Source, crash!.Source);
                if (valid && playback.Source.isPlaying) continue;
                Restore(playback, stop: !valid);
                lodPlaybacks.RemoveAt(i);
            }
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var current = pending[i];
                var vehicle = current.Vehicle;
                var crash = vehicle != null ? vehicle.soundManager?.crashComponent : null;
                // Native Update clears collisionFlag when inactive. Also discard a cue if
                // startup replaced its component/source rather than voicing old state there.
                if (!Ready(vehicle, crash) || !ReferenceEquals(current.Component, crash) || !ReferenceEquals(current.Source, crash!.Source))
                { pending.RemoveAt(i); continue; }
                if (crash!.Source.isPlaying) continue;
                pending.RemoveAt(i);
                Play(vehicle!, crash, current.Point, current.RelativeSpeed);
            }
        }

        private static bool Ready(VehicleController? vehicle, CrashComponent? crash) =>
            vehicle != null && vehicle.gameObject.activeInHierarchy &&
            crash != null && crash.Initialized && crash.IsOn && crash.Source != null &&
            (crash.Active && crash.Source.enabled || LodDisabled(vehicle, crash)) &&
            crash.Source.gameObject.activeInHierarchy && crash.Clip != null;

        private static bool LodDisabled(VehicleController vehicle, CrashComponent crash) =>
            !crash.IsEnabled && crash.LodIndex >= 0 && vehicle.activeLODIndex > crash.LodIndex;

        private static void Play(VehicleController vehicle, CrashComponent crash, Vector3 point, float relativeSpeed)
        {
            bool lodDisabled = LodDisabled(vehicle, crash);
            if (lodDisabled)
            {
                // Sleep selects the last native LOD, even for a nearby presented chassis.
                // Its crash component then disables this source, and SetVolume/Play all
                // return while !Active. The simulator already accepted and voiced this
                // contact: enable only its one-shot source, leaving sleep, component state,
                // LOD, mixer, attenuation and spatial curves exactly as native configured.
                if (!lodPlaybacks.Exists(p => ReferenceEquals(p.Source, crash.Source)))
                {
                    if (lodPlaybacks.Count >= MaximumPendingVehicles) return;
                    lodPlaybacks.Add(new LodPlayback(vehicle, crash));
                }
                crash.Source.enabled = true;
            }
            crash.Source.transform.position = point;
            crash.Source.clip = crash.RandomClip;
            float volume = Mathf.Clamp01(relativeSpeed * 0.025f * crash.velocityMagnitudeEffect) * crash.baseVolume;
            float pitch = Random.Range(1f - crash.pitchRandomness, 1f + crash.pitchRandomness) * crash.basePitch;
            if (lodDisabled)
            {
                crash.Source.volume = volume * vehicle.soundManager.masterVolume;
                crash.Source.pitch = Mathf.Clamp(pitch, 0f, 5f);
                crash.Source.Play();
            }
            else { crash.SetVolume(volume); crash.SetPitch(pitch); crash.Play(); }
        }

        private static void Restore(LodPlayback playback, bool stop)
        {
            if (playback.Source == null) return;
            if (stop && playback.Source.isPlaying) playback.Source.Stop();
            // An ownership/wake transition may have enabled the real component in the
            // meantime. Do not restore the old disabled source over that native state.
            if (!playback.Component.IsEnabled) playback.Source.enabled = playback.SourceEnabled;
        }

        private static PendingCrash? Find(VehicleController vehicle)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (pending[i].Vehicle == null) { pending.RemoveAt(i); continue; }
                if (ReferenceEquals(pending[i].Vehicle, vehicle)) return pending[i];
            }
            return null;
        }
        internal static void Forget(VehicleController vehicle)
        {
            var current = Find(vehicle);
            if (current != null) pending.Remove(current);
            for (int i = lodPlaybacks.Count - 1; i >= 0; i--)
                if (ReferenceEquals(lodPlaybacks[i].Vehicle, vehicle))
                { Restore(lodPlaybacks[i], stop: true); lodPlaybacks.RemoveAt(i); }
        }
        internal static void Reset()
        {
            pending.Clear();
            foreach (var playback in lodPlaybacks) Restore(playback, stop: true);
            lodPlaybacks.Clear();
        }
    }
}
