using System.Collections.Generic;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Modules.NOS;
using NWH.VehiclePhysics2.Sound;
using NWH.VehiclePhysics2.Sound.SoundComponents;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // NWH's SoundManager.Update returns before its sound components on any non-Local vehicle, so a
    // vehicle driven by someone else had only the engine (repaired separately) and no tire, skid,
    // bump, horn or reverse-beep sounds. These components read only inputs a presented vehicle now
    // has (replicated slip, horn, gear, the #250 speed), so they run as native on the remote copy.
    // The crash sound is not among them: it needs the driver's collision, relayed as an event.
    internal static class RemoteVehicleSounds
    {
        private const float MissingNosRetryInterval = 1f;
        private sealed class SoundBinding
        {
            internal VehicleController Vehicle = null!;
            internal NOSModuleWrapper? Nos;
            internal float NextNosLookup;
            internal bool WasBraking;
        }
        private static readonly Dictionary<int, SoundBinding> soundBindings = new Dictionary<int, SoundBinding>();
        private static readonly List<int> retiredVehicles = new List<int>();
        private static float nextPrune;

        internal static void Update(SoundManager manager, VehicleController vehicle)
        {
            if (vehicle == null) return;
            RefreshSurfaces(vehicle);
            Run(manager.wheelSkidComponent);
            Run(manager.wheelTireNoiseComponent);
            Run(manager.suspensionBumpComponent);
            Run(manager.hornComponent);
            Run(manager.reverseBeepComponent);
            // The crank: it plays while the replicated engine.StarterActive is set (rt-12 report).
            Run(manager.engineStartComponent);
            // The fan reads only the replicated RPM; the boiling hiss reads the driver's replicated
            // coolant flag (Astra R13/R14).
            Run(manager.engineFanComponent);
            Run(manager.engineCoolingComponent);
            // Whine, turbo and brake hiss read powertrain values only the driver's simulation computes.
            // Derive them here from the replicated throttle, RPM and brake, the way native derives them,
            // without running the powertrain or the brakes (Astra R13).
            var engine = vehicle.powertrain.engine;
            float throttle = engine.IsRunning ? Mathf.Clamp01(vehicle.input.states.throttle) : 0f;
            engine.ThrottlePosition = throttle;
            engine.generatedPower = engine.IsRunning ? Mathf.Clamp01(engine.RPMPercent) * throttle * engine.maxPower : 0f;
            Run(manager.transmissionWhineComponent);
            var turbo = engine.forcedInduction;
            if (turbo != null && turbo.useForcedInduction)
            {
                if (engine.IsRunning) turbo.Update(engine);
                else turbo.boost = 0f;
                Run(manager.turboWhistleComponent);
                Run(manager.turboFlutterComponent);
            }
            Run(manager.brakeHissComponent);
            // SoundManager skips every component on Remote cars. SharedNitrous supplies Boost from
            // the driver's usage record; the native sound reads IsBeingUsed without consuming charge.
            var binding = BindingFor(vehicle);
            Run(Nitrous(binding)?.module?.soundComponent);
            bool braking = vehicle.input.states.brakes > 0.05f;
            if (binding.WasBraking && !braking && manager.brakeHissComponent is { } hiss && hiss.Active)
                hiss.PlayBrakeHiss();
            binding.WasBraking = braking;
        }

        private static SoundBinding BindingFor(VehicleController vehicle)
        {
            int id = vehicle.GetInstanceID();
            if (!soundBindings.TryGetValue(id, out var binding) || !ReferenceEquals(binding.Vehicle, vehicle))
            {
                // A destroyed Unity object can leave its managed wrapper alive until collection;
                // instance ID reuse must never borrow that vehicle's component or brake state.
                binding = new SoundBinding { Vehicle = vehicle };
                soundBindings[id] = binding;
            }
            return binding;
        }

        private static NOSModuleWrapper? Nitrous(SoundBinding binding)
        {
            // Unity's null check catches component destruction. Retry that immediately, while a
            // true absence is cached briefly so cars without NOS do not search every sound frame.
            if (binding.Nos == null && (!ReferenceEquals(binding.Nos, null) || Time.unscaledTime >= binding.NextNosLookup))
            {
                binding.Nos = binding.Vehicle.GetComponent<NOSModuleWrapper>();
                binding.NextNosLookup = Time.unscaledTime + MissingNosRetryInterval;
            }
            // Read module each time: native SetModule can replace it on the same live wrapper.
            return binding.Nos == null ? null : binding.Nos;
        }

        private static void RefreshSurfaces(VehicleController vehicle)
        {
#if MONMP_PERFORMANCE_CAPTURE
            using var capture = Runtime.CaptureDetail.Measure("NativeVehicleAudio.RefreshSurfaces");
#endif
            if (vehicle.VehicleMultiplayerInstanceType != NWH.Vehicle.MultiplayerInstanceType.Remote || vehicle.powertrain?.Initialized != true) return;
            var detection = vehicle.groundDetection;
            var preset = detection?.groundDetectionPreset;
            if (detection?.Initialized != true || !detection.IsEnabled || preset == null || preset.fallbackSurfacePreset == null) return;
            // Native powertrain substeps refresh these caches, but Remote vehicles skip them. Use
            // the visual wheel's current ground hit without advancing the transmission or engine.
            foreach (var wheel in vehicle.powertrain.wheels)
            {
                if (wheel?.wheelController == null) continue;
                detection.GetCurrentSurfaceMap(wheel.wheelController, ref wheel.surfaceMapIndex, ref wheel.surfacePreset);
            }
        }

        private static void Run(SoundComponent? component)
        {
            if (component == null || !component.Active) return;
            component.Update();
        }

        // The driver's crash, on this machine's copy of the vehicle: the native crash clip at the
        // contact point, as loud as the driver's relative speed made it.
        internal static void Crash(VehicleController vehicle, Vector3 point, float relativeSpeed)
        {
            RemoteVehicleCrashSounds.Receive(vehicle, point, relativeSpeed);
        }

        internal static void TickCrashPending()
        {
            RemoteVehicleCrashSounds.Update();
            if (Time.unscaledTime < nextPrune) return;
            nextPrune = Time.unscaledTime + 1f;
            foreach (var pair in soundBindings)
                if (pair.Value.Vehicle == null) retiredVehicles.Add(pair.Key);
            foreach (int id in retiredVehicles) soundBindings.Remove(id);
            retiredVehicles.Clear();
        }
        internal static void Reset()
        {
            RemoteVehicleCrashSounds.Reset();
            soundBindings.Clear(); retiredVehicles.Clear(); nextPrune = 0;
        }
    }
}
