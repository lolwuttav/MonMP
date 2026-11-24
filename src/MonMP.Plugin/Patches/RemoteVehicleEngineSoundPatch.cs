using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NWH;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Powertrain;
using NWH.VehiclePhysics2.Sound;
using NWH.VehiclePhysics2.Sound.SoundComponents;
using UnityEngine;
using UnityEngine.Audio;

namespace MonMP.Plugin.Patches
{
    [HarmonyPatch(typeof(SoundManager), nameof(SoundManager.Update))]
    internal static class RemoteVehicleEngineSoundPatch
    {
        private sealed class RemoteAudioActivationState
        {
            internal readonly SoundManager Manager;
            internal readonly EngineRunningComponent EngineSound;
            internal readonly bool IsOn;
            internal readonly bool IsEnabled;
            internal readonly int LodIndex;
            internal readonly bool SourceEnabled;

            internal RemoteAudioActivationState(SoundManager manager, EngineRunningComponent engineSound)
            {
                Manager = manager;
                EngineSound = engineSound;
                IsOn = engineSound.IsOn;
                IsEnabled = engineSound.IsEnabled;
                LodIndex = engineSound.LodIndex;
                SourceEnabled = engineSound.Source?.enabled ?? false;
            }
        }

        // The host crash entered Mono's ConditionalWeakTable.TryGetValue cast path here.
        // Keep only temporarily activated remote engines in a small reference-identity list.
        private static readonly List<RemoteAudioActivationState> remoteAudioActivations = new();

#if MONMP_DEVELOPMENT
        private static VehicleController? diagnosticTarget;
#endif


#if MONMP_DEVELOPMENT
        private static bool prepareEvaluated;
#endif

        private static bool shapeSupported;

#if MONMP_DEVELOPMENT
        private static int prepareCalls;
#endif


#if MONMP_DEVELOPMENT
        private static int postfixCalls;
#endif


#if MONMP_DEVELOPMENT
        private static int targetCalls;
#endif


#if MONMP_DEVELOPMENT
        private static int remoteTargetCalls;
#endif


#if MONMP_DEVELOPMENT
        private static int boundTargetCalls;
#endif


#if MONMP_DEVELOPMENT
        private static int activationFrames;
#endif


#if MONMP_DEVELOPMENT
        private static int activationStarts;
#endif


#if MONMP_DEVELOPMENT
        private static int activationRestores;
#endif


#if MONMP_DEVELOPMENT
        private static int engineUpdateCalls;
#endif


#if MONMP_DEVELOPMENT
        private static string lastTargetGuard = "no target postfix call observed";
#endif


#if MONMP_DEVELOPMENT
        private static bool lastRestoreMatched;
#endif


#if MONMP_DEVELOPMENT
        internal static int DoubledLoopsStopped { get; private set; }
#endif


        [HarmonyPrepare]
        private static bool Prepare()
        {

#if MONMP_DEVELOPMENT
            prepareCalls++;
#endif


#if MONMP_DEVELOPMENT
            prepareEvaluated = true;
#endif

            shapeSupported = RemoteVehicleEngineAudioNativeShape.IsSupported(
                typeof(SoundManager), typeof(EngineRunningComponent), typeof(VehicleComponent),
                typeof(SoundComponent), typeof(VehicleController), typeof(Vehicle), typeof(Vehicle.MultiplayerInstanceType),
                typeof(EngineComponent), typeof(AudioSource), typeof(AudioClip), typeof(AudioMixerGroup), typeof(AudioMixer));
            return shapeSupported;
        }


#if MONMP_DEVELOPMENT
        internal static void SetDiagnosticTarget(VehicleController? vehicle)
        {
            if (diagnosticTarget == vehicle) return;
            diagnosticTarget = vehicle;
            postfixCalls = targetCalls = remoteTargetCalls = boundTargetCalls = activationFrames = activationStarts =
                activationRestores = engineUpdateCalls = 0;
            lastTargetGuard = "no target postfix call observed";
            lastRestoreMatched = false;
        }
#endif


#if MONMP_DEVELOPMENT
        internal static string DiagnosticSummary(VehicleController vehicle)
        {
            bool patchInstalled = false;
            string patchCheck = "unavailable";
            try
            {
                MethodInfo? update = AccessTools.Method(typeof(SoundManager), nameof(SoundManager.Update));
                patchInstalled = update != null && Harmony.GetPatchInfo(update)?.Postfixes.Any(p =>
                    p.owner == MonMPPlugin.PluginId && p.PatchMethod.DeclaringType == typeof(RemoteVehicleEngineSoundPatch)) == true;
                patchCheck = patchInstalled ? "installed" : "missing";
            }
            catch (Exception exception)
            {
                patchCheck = "check failed: " + exception.GetType().Name;
            }

            var soundManager = vehicle == null ? null : vehicle.soundManager;
            var engineSound = soundManager == null ? null : soundManager.engineRunningComponent;
            var source = engineSound == null ? null : engineSound.Source;
            RemoteAudioActivationState? activation = FindRemoteActivation(soundManager);
            bool hasActivation = activation != null;
            bool bound = MonMPPlugin.Instance?.Vehicles?.IsBound(vehicle!) == true;
            return "patch=" + patchCheck + ", prepare=" + prepareEvaluated + "/" + prepareCalls + "/shape=" + shapeSupported +
                ", SoundManager.Update calls=" + postfixCalls + ", ATV calls=" + targetCalls +
                ", remote=" + remoteTargetCalls + ", bound=" + boundTargetCalls + ", engine Update calls=" + engineUpdateCalls +
                ", remote activation frames/starts/restores=" + activationFrames + "/" + activationStarts + "/" + activationRestores +
                ", active snapshot=" + hasActivation + (hasActivation ? "/on=" + activation!.IsOn + "/enabled=" + activation.IsEnabled + "/lod=" + activation.LodIndex : string.Empty) +
                ", last restore matched=" + lastRestoreMatched +
                ", last guard=" + lastTargetGuard + ", live mode=" + vehicle!.VehicleMultiplayerInstanceType +
                ", bound now=" + bound + ", engine active=" + (engineSound?.Active ?? false) +
                "/initialized=" + (engineSound?.Initialized ?? false) + "/on=" + (engineSound?.IsOn ?? false) +
                "/enabled=" + (engineSound?.IsEnabled ?? false) + ", clip=" + (engineSound?.Clip != null) +
                ", source=" + (source != null) + "/clip=" + (source?.clip != null) + "/enabled=" + (source?.enabled ?? false) +
                "/playing=" + (source?.isPlaying ?? false) + "/pitch=" + (source?.pitch ?? 0f) +
                "/volume=" + (source?.volume ?? 0f) + ", mixer=" + (engineSound?.audioMixerGroup != null) +
                "/audioMixer=" + (engineSound?.audioMixerGroup?.audioMixer != null) +
                ", engine running=" + vehicle.powertrain.engine.IsRunning + ", RPM=" + vehicle.powertrain.engine.RPM +
                ", throttle input=" + vehicle.input.states.throttle + ", engine throttle=" + vehicle.powertrain.engine.ThrottlePosition + ".";
        }
#endif


#if MONMP_DEVELOPMENT
        internal static bool DiagnosticTargetActivationRestored(VehicleController vehicle)
        {
            if (!object.ReferenceEquals(diagnosticTarget, vehicle) || vehicle == null ||
                vehicle.powertrain.engine.IsRunning || activationRestores == 0 || !lastRestoreMatched)
                return false;
            return FindRemoteActivation(vehicle.soundManager) == null;
        }
#endif


        [HarmonyPostfix]
        private static void Postfix(SoundManager __instance)
        {
            SoundManager? soundManager = __instance;
            if (soundManager == null) return;
            PruneDestroyedActivations();

#if MONMP_DEVELOPMENT
            if (diagnosticTarget != null) postfixCalls++;
#endif

            VehicleController? vehicle = soundManager.VehicleController;
#if MONMP_DEVELOPMENT
            bool isTarget = diagnosticTarget != null && object.ReferenceEquals(soundManager, diagnosticTarget.soundManager);
#else
            bool isTarget = false;
#endif

#if MONMP_DEVELOPMENT
            if (isTarget) targetCalls++;
#endif

            if (vehicle == null)
            {
                RestoreRemoteActivation(soundManager, soundManager.engineRunningComponent, isTarget,
                    engineStopped: false, silenceRemote: true);

#if MONMP_DEVELOPMENT
                if (isTarget) lastTargetGuard = "no VehicleController";
#endif

                return;
            }
            if (vehicle.VehicleMultiplayerInstanceType != Vehicle.MultiplayerInstanceType.Remote)
            {
                RestoreRemoteActivation(soundManager, soundManager.engineRunningComponent, isTarget,
                    vehicle.powertrain.engine.IsRunning, silenceRemote: false);
                // #173: NWH2_RES switches NWH's own engine loop off once, 0.15 s after it starts. A guest's
                // car turned from Remote to Local when it takes the wheel re-enables NWH's sound components,
                // so the loop came back on under the layered engine sound: both played, far louder than
                // on the host. Keep the loop off for every car the layered sound voices, as native intends.
                if (MonMPPlugin.Instance?.Session?.IsRunning == true && HasRealisticSound(vehicle) &&
                    soundManager.engineRunningComponent is { } localLoop && localLoop.IsOn)
                {
                    localLoop.IsOn = false;
                    if (localLoop.Source != null && localLoop.Source.isPlaying) localLoop.Source.Stop();

#if MONMP_DEVELOPMENT
                    DoubledLoopsStopped++;
#endif

                }

#if MONMP_DEVELOPMENT
                if (isTarget) lastTargetGuard = "mode=" + vehicle.VehicleMultiplayerInstanceType;
#endif

                return;
            }

#if MONMP_DEVELOPMENT
            if (isTarget) remoteTargetCalls++;
#endif

            var plugin = MonMPPlugin.Instance;
            if (plugin?.Vehicles?.IsBound(vehicle) != true)
            {
                RestoreRemoteActivation(soundManager, soundManager.engineRunningComponent, isTarget,
                    vehicle.powertrain.engine.IsRunning, silenceRemote: true);
                if (plugin?.World?.SchoolBus?.IsReplica(vehicle) == true) Replication.RemoteVehicleSounds.Update(soundManager, vehicle);

#if MONMP_DEVELOPMENT
                if (isTarget) lastTargetGuard = plugin?.Vehicles == null ? "shared vehicles unavailable" : "vehicle is not bound";
#endif

                return;
            }

#if MONMP_DEVELOPMENT
            if (isTarget) boundTargetCalls++;
#endif

            Replication.RemoteVehicleSounds.Update(soundManager, vehicle);

            var engineSound = soundManager.engineRunningComponent;
            // A vehicle with the game's own layered engine sound is voiced by it, fed the replicated
            // engine (RemoteVehicleRealisticEngineSoundPatch). Keep NWH's loop off, as NWH2_RES does:
            // playing it too was the full-level engine that peaked at 100% volume (#121).
            if (HasRealisticSound(vehicle))
            {
                RestoreRemoteActivation(soundManager, engineSound, isTarget, engineStopped: false, silenceRemote: true);
                if (engineSound != null)
                {
                    engineSound.IsOn = false;
                    if (engineSound.Source != null && engineSound.Source.isPlaying) engineSound.Source.Stop();
                }

#if MONMP_DEVELOPMENT
                if (isTarget) lastTargetGuard = "voiced by RealisticEngineSound; NWH loop kept off";
#endif

                return;
            }
            if (engineSound == null)
            {
                RestoreRemoteActivation(soundManager, null, isTarget, engineStopped: false, silenceRemote: true);

#if MONMP_DEVELOPMENT
                if (isTarget) lastTargetGuard = "engine component missing";
#endif

                return;
            }

            var engine = vehicle.powertrain.engine;
            if (!engine.IsRunning)
            {
                // A stopped remote engine must not inherit rev input or trigger asset
                // recovery. Restore the NWH state/LOD snapshot before stopping its loop.
                bool restored = RestoreRemoteActivation(soundManager, engineSound, isTarget,
                    engineStopped: true, silenceRemote: true);
                var stoppedSource = engineSound.Source;
                if (!restored && engineSound.Active) engineSound.Update();
                if (stoppedSource != null && stoppedSource.isPlaying) stoppedSource.Stop();

#if MONMP_DEVELOPMENT
                if (isTarget) lastTargetGuard = "engine stopped; cache and clip recovery skipped";
#endif

                return;
            }

            if (!engineSound.Initialized)
            {
                RestoreRemoteActivation(soundManager, engineSound, isTarget, engineStopped: false, silenceRemote: true);

#if MONMP_DEVELOPMENT
                if (isTarget) lastTargetGuard = "engine component not initialized";
#endif

                return;
            }

            // Only running Remote vehicles receive the replicated input cache and
            // missing default clip recovery. The native SetDefaults path is the
            // source-grounded loader for EngineRunning; Update itself never loads it.
            if (engineSound.Source == null)
            {
                RestoreRemoteActivation(soundManager, engineSound, isTarget, engineStopped: false, silenceRemote: true);

#if MONMP_DEVELOPMENT
                if (isTarget) lastTargetGuard = "engine AudioSource missing";
#endif

                return;
            }
            if (engineSound.audioMixerGroup == null || engineSound.audioMixerGroup.audioMixer == null)
            {
                RestoreRemoteActivation(soundManager, engineSound, isTarget, engineStopped: false, silenceRemote: true);
                engineSound.Source.Stop();

#if MONMP_DEVELOPMENT
                if (isTarget) lastTargetGuard = "engine audio mixer missing";
#endif

                return;
            }
            if (engineSound.Clip == null)
                engineSound.SetDefaults(vehicle);
            if (engineSound.Clip == null)
            {
                RestoreRemoteActivation(soundManager, engineSound, isTarget, engineStopped: false, silenceRemote: true);

#if MONMP_DEVELOPMENT
                if (isTarget) lastTargetGuard = "EngineRunning clip missing after native SetDefaults fallback";
#endif

                return;
            }
            if (engineSound.Source.clip != engineSound.Clip)
                engineSound.Source.clip = engineSound.Clip;

            ActivateRemoteEngineSound(soundManager, engineSound, isTarget);
            if (!engineSound.Active || !engineSound.Source.enabled)
            {

#if MONMP_DEVELOPMENT
                if (isTarget) lastTargetGuard = "remote engine activation did not produce an active enabled source";
#endif

                return;
            }

            engine.ThrottlePosition = Mathf.Clamp01(vehicle.input.states.throttle);
            engineSound.Update();

#if MONMP_DEVELOPMENT
            if (isTarget)
            {
                engineUpdateCalls++;
                lastTargetGuard = "engine Update called; running=" + engine.IsRunning +
                    ", throttle=" + engine.ThrottlePosition + ", playing=" + engineSound.Source.isPlaying;
            }
#endif

        }

        private static readonly Dictionary<VehicleController, bool> realisticSound = new Dictionary<VehicleController, bool>();
        internal static bool HasRealisticSound(VehicleController vehicle)
        {
            if (!realisticSound.TryGetValue(vehicle, out bool has))
            {
                if (realisticSound.Count > 64) realisticSound.Clear();
                realisticSound[vehicle] = has = vehicle.GetComponentInChildren<NWH.VehiclePhysics.NWH2_RES>(true) != null &&
                    vehicle.GetComponentInChildren<RealisticEngineSound>(true) != null;
            }
            return has;
        }

        private static void ActivateRemoteEngineSound(SoundManager soundManager, EngineRunningComponent engineSound, bool isTarget)
        {
            if (FindRemoteActivation(soundManager) == null)
            {
                remoteAudioActivations.Add(new RemoteAudioActivationState(soundManager, engineSound));

#if MONMP_DEVELOPMENT
                if (isTarget) activationStarts++;
#endif

            }

            // A remote vehicle can be permanently placed beyond NWH's local LOD
            // cutoff. Override only this engine-loop component while replicated
            // engine state says it is running; leave SoundManager and vehicle LODs
            // unchanged so unrelated audio/effects remain under native control.
            engineSound.IsOn = true;
            engineSound.LodIndex = -1;
            if (!engineSound.IsEnabled || engineSound.Source?.enabled != true)
                engineSound.Enable();


#if MONMP_DEVELOPMENT
            if (isTarget) activationFrames++;
#endif

        }

        private static bool RestoreRemoteActivation(SoundManager soundManager, EngineRunningComponent? engineSound,
            bool isTarget, bool engineStopped, bool silenceRemote)
        {
            RemoteAudioActivationState? state = FindRemoteActivation(soundManager);
            if (state == null) return false;
            remoteAudioActivations.Remove(state);
            engineSound ??= state.EngineSound;
            if (engineSound != null)
            {
                engineSound.IsOn = state.IsOn;
                engineSound.LodIndex = state.LodIndex;
                if (state.IsEnabled) engineSound.Enable();
                else engineSound.Disable();

                var source = engineSound.Source;
                if (source != null)
                {
                    if (engineStopped && engineSound.Active)
                        engineSound.Update();
                    else if (silenceRemote && source.isPlaying)
                        source.Stop();

                    if (silenceRemote && !engineStopped)
                    {
                        source.pitch = 0f;
                        source.volume = 0f;
                    }
                    source.enabled = state.SourceEnabled;
                    if ((engineStopped || silenceRemote) && source.isPlaying) source.Stop();
                }


#if MONMP_DEVELOPMENT
                if (isTarget)
                {
                    activationRestores++;
                    lastRestoreMatched = engineSound.IsOn == state.IsOn && engineSound.IsEnabled == state.IsEnabled &&
                        engineSound.LodIndex == state.LodIndex && engineSound.Source?.enabled == state.SourceEnabled &&
                        engineSound.Source?.isPlaying != true;
                }
#endif

            }
            return true;
        }

        private static RemoteAudioActivationState? FindRemoteActivation(SoundManager? soundManager)
        {
            if (soundManager == null) return null;
            for (int i = 0; i < remoteAudioActivations.Count; i++)
                if (object.ReferenceEquals(remoteAudioActivations[i].Manager, soundManager))
                    return remoteAudioActivations[i];
            return null;
        }

        private static void PruneDestroyedActivations()
        {
            for (int i = remoteAudioActivations.Count - 1; i >= 0; i--)
                if (remoteAudioActivations[i].Manager == null)
                    remoteAudioActivations.RemoveAt(i);
        }
    }
}
