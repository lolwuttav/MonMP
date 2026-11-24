using MonMP.Core.World;
using NWH.VehiclePhysics2.Sound.SoundComponents;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal static class VehicleCrashPresentation
    {
        internal static void Native(CrashComponent crash, Collision collision, bool wasPlaying, bool ranOriginal)
        {
            var world = MonMPPlugin.Instance?.World;
            var events = world?.Events;
            var source = crash.Source;
            var vehicle = crash.VehicleController;
            if (!ranOriginal || wasPlaying || SharedPresentationEvents.Replaying || world?.IsDisposed != false ||
                events?.Live != true || vehicle == null || collision == null || !crash.Active ||
                source == null || !source.enabled || !source.gameObject.activeInHierarchy || !source.isPlaying) return;
            ulong target;
            if (!world.Vehicles.TryGetCrashTarget(vehicle, out target) &&
                !world.SchoolBus.TryGetCrashTarget(vehicle, out target) &&
                !world.MartinCar.TryGetCrashTarget(vehicle, out target) &&
                !world.Trailers.TryGetCrashTarget(vehicle, out target)) return;
            events.Post(PresentationKind.VehicleCrash, 0, target, source.transform.position,
                Mathf.Clamp(collision.relativeVelocity.magnitude, 0f, 200f));
        }

        internal static bool AcceptCue(uint peer, PresentationEvent cue)
        {
            var world = MonMPPlugin.Instance?.World;
            if (world?.IsDisposed != false || peer == 0 || !(cue.Value >= 0f && cue.Value <= 200f)) return false;
            if (VehicleCrashGameplay.IsReport(cue)) return VehicleCrashGameplay.ValidReport(cue) &&
                world.Vehicles.AcceptCrashCue(peer, cue) && VehicleCrashGameplay.AcceptRecipient(cue);
            return cue.Variant == 0 && (world.Vehicles.AcceptCrashCue(peer, cue) || world.Trailers.AcceptCrashCue(peer, cue));
        }

        internal static void Play(PresentationEvent cue)
        {
            var world = MonMPPlugin.Instance?.World;
            if (world?.IsDisposed != false) return;
            if (VehicleCrashGameplay.IsDelivery(cue)) { VehicleCrashGameplay.Play(cue); return; }
            if (cue.Variant != 0) return;
            // Originating actors are already skipped by the presentation bus. A delayed cue from
            // the previous owner remains audible even if this machine now simulates the vehicle.
            if (world.Vehicles.TryPlayCrash(cue) || world.SchoolBus.TryPlayCrash(cue) ||
                world.MartinCar.TryPlayCrash(cue)) return;
            world.Trailers.TryPlayCrash(cue);
        }
    }
}
