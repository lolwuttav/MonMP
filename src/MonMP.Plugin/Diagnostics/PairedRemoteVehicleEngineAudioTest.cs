using System;
using System.Linq;
using MonMP.Core.World;
using MonMP.Plugin.Patches;
using MonMP.Plugin.Replication;
using NWH;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    internal sealed class PairedRemoteVehicleEngineAudioTest
    {
        private const float GuestHoldSeconds = 15f;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly bool host;
        private int phase;
        private float phaseStartedAt;
        private float lastEnterAt = -1000f;
        private float runningStateAt = -1f;
        private float holdStartedAt = -1f;
        private float playbackObservedAt = -1f;
        private bool sawGuestDriver;
        private bool probed;
        private bool playbackObserved;
        private string status = "waiting for shared ATV";

        internal string Status => "remote engine audio phase=" + phase + "; " + status;

        internal PairedRemoteVehicleEngineAudioTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin;
            this.gameplay = gameplay;
            host = plugin.TestOptions.IsHost;
        }

        internal bool Tick()
        {
            if (host && plugin.Session?.IsRunning != true && !playbackObserved)
                RemoteVehicleEngineSoundPatch.SetDiagnosticTarget(null);
            if (!host && phase == 3 && plugin.Session?.IsRunning != true)
            {
                status = "host completed remote playback check";
                return true;
            }
            var shared = plugin.Vehicles;
            if (shared?.Ready != true || plugin.World?.PersonalProfileReady != true) return false;
            shared.TestType = Vehicle_Type.SmollATV;
            var vehicle = shared.TestVehicle;
            if (host) RemoteVehicleEngineSoundPatch.SetDiagnosticTarget(vehicle);
            var engineSound = vehicle.soundManager.engineRunningComponent;

            if (host) return TickHost(shared, vehicle, engineSound);
            return TickGuest(shared, vehicle);
        }

        private bool TickHost(SharedVehicles shared, VehicleController vehicle,
            NWH.VehiclePhysics2.Sound.SoundComponents.EngineRunningComponent engineSound)
        {
            if (phase == 0)
            {
                if (shared.TestState.Driver.Owner != ItemLease.Unowned)
                    throw new InvalidOperationException("Host engine-audio baseline already has a driver.");
                if (vehicle.VehicleMultiplayerInstanceType != Vehicle.MultiplayerInstanceType.Local)
                    throw new InvalidOperationException("Host engine-audio baseline is not locally simulated.");

                vehicle.input.autoSetInput = false;
                vehicle.input.Throttle = 0;
                vehicle.input.Brakes = 1;
                vehicle.Get_IgnitionPosition = 0;
                engineSound.Source?.Stop();
                phase = 1;
                phaseStartedAt = Time.unscaledTime;
                status = "settling stopped local baseline";
                return false;
            }

            if (phase == 1)
            {
                if (shared.TestState.Driver.Owner != ItemLease.Unowned)
                    throw new InvalidOperationException("A driver entered the ATV before the stopped baseline was verified.");
                if (vehicle.powertrain.engine.IsRunning || engineSound.Source?.isPlaying == true)
                {
                    if (Time.unscaledTime - phaseStartedAt > 8f)
                        throw new InvalidOperationException("Host ATV did not settle to an unowned engine-off baseline.");
                    return false;
                }
                phase = 2;
                phaseStartedAt = Time.unscaledTime;
                status = "waiting for guest engine state";
                return false;
            }

            if (phase == 2)
            {
                if (playbackObserved)
                {
                    if (vehicle.powertrain.engine.IsRunning)
                    {
                        status = "host playback confirmed; waiting for guest engine stop and NWH state restore";
                        if (Time.unscaledTime - playbackObservedAt > GuestHoldSeconds + 20f)
                            throw new InvalidOperationException("Guest engine did not stop after playback was confirmed: " +
                                RemoteVehicleEngineSoundPatch.DiagnosticSummary(vehicle));
                        return false;
                    }
                    if (RemoteVehicleEngineSoundPatch.HasRealisticSound(vehicle))
                    {
                        if (ResPlaying(vehicle, out _) || engineSound.Source?.isPlaying == true)
                        {
                            status = "engine stopped; waiting for the remote engine sound to wind down";
                            if (Time.unscaledTime - playbackObservedAt > GuestHoldSeconds + 25f)
                                throw new InvalidOperationException("Remote engine sound kept playing after the replicated engine stopped: " + ResProbe(vehicle));
                            return false;
                        }
                        status = "host voiced the remote engine through the game's own engine sound at replicated RPM and silenced it after stop; " + ResProbe(vehicle);
                        RemoteVehicleEngineSoundPatch.SetDiagnosticTarget(null);
                        return true;
                    }
                    if (!RemoteVehicleEngineSoundPatch.DiagnosticTargetActivationRestored(vehicle))
                    {
                        status = "engine stopped; waiting for remote engine audio activation restore";
                        if (Time.unscaledTime - playbackObservedAt > GuestHoldSeconds + 25f)
                            throw new InvalidOperationException("Remote engine sound state did not restore after the replicated engine stopped: " +
                                RemoteVehicleEngineSoundPatch.DiagnosticSummary(vehicle));
                        return false;
                    }

                    status = "host engine loop followed remote RPM/throttle and restored NWH state after stop; " +
                        RemoteVehicleEngineSoundPatch.DiagnosticSummary(vehicle);
                    RemoteVehicleEngineSoundPatch.SetDiagnosticTarget(null);
                    return true;
                }

                if (shared.TestState.Driver.Owner == ItemLease.Unowned)
                {
                    if (sawGuestDriver)
                        throw new InvalidOperationException("Guest released the ATV before the host observed remote engine audio.");
                    if (Time.unscaledTime - phaseStartedAt > 120f)
                        throw new InvalidOperationException("Guest did not acquire the ATV for the remote engine-audio fixture.");
                    return false;
                }
                if (shared.TestState.Driver.Owner == plugin.Session!.LocalPeerId)
                    throw new InvalidOperationException("Host unexpectedly owns the guest engine-audio fixture.");
                sawGuestDriver = true;
                if (vehicle.VehicleMultiplayerInstanceType != Vehicle.MultiplayerInstanceType.Remote || !vehicle.vehicleRigidbody.isKinematic)
                    throw new InvalidOperationException("Guest-driven host vehicle is not a kinematic Remote replica.");

                var state = shared.TestState;
                if (!state.EngineIgnition || state.Starter || state.EngineAngularVelocity <= 1f || state.Throttle < 0.3f)
                {
                    status = "waiting for running guest RPM and throttle";
                    return false;
                }

                if (runningStateAt < 0f) runningStateAt = Time.unscaledTime;
                if (RemoteVehicleEngineSoundPatch.HasRealisticSound(vehicle))
                {
                    // #121: the game voices engines through RealisticEngineSound; the remote must too, at the
                    // driver's RPM, and never through NWH's untuned full-level loop.
                    float replicatedRpm = RemoteVehicleRealisticEngineSoundPatch.ReplicatedRpm(vehicle);
                    var res = vehicle.GetComponentInChildren<RealisticEngineSound>(true);
                    bool resPlaying = ResPlaying(vehicle, out float loudest);
                    bool rpmMatches = res != null && Mathf.Abs(res.engineCurrentRPM - replicatedRpm) <= Mathf.Max(150f, replicatedRpm * 0.1f);
                    bool nwhSilent = engineSound.Source == null || !engineSound.Source.isPlaying;
                    if (!resPlaying || !rpmMatches || !nwhSilent)
                    {
                        status = "settling remote engine sound: replicated rpm=" + replicatedRpm.ToString("0") + ", RES playing=" + resPlaying +
                            ", NWH loop playing=" + !nwhSilent + ", " + ResProbe(vehicle);
                        if (Time.unscaledTime - runningStateAt > 5f)
                            throw new InvalidOperationException("Remote engine was not voiced by the game's engine sound at the driver's RPM (#121): " + status);
                        return false;
                    }
                    if (loudest > 0.5f)
                        throw new InvalidOperationException("Remote engine sound layer is at volume " + loudest + ", far above the game's own engine level (#121).");
                    Debug.Log("MonMP vehicle audio probe (host, remote engine): loudest RES layer=" + loudest.ToString("0.000") + ", " + AudioProbe(vehicle));
                    Singleton<GameAssets>.i.AUDIO_VehicleMixer.audioMixer.GetFloat("attenuation", out float resDb);
                    if (Mathf.Abs(resDb - VehicleVolumePatch.UserDb()) > 0.1f)
                        throw new InvalidOperationException("The host's vehicle mixer is at " + resDb + " dB, not the Vehicles setting " + VehicleVolumePatch.UserDb() + " dB (#121).");
                    playbackObserved = true;
                    playbackObservedAt = Time.unscaledTime;
                    status = "host remote engine voiced by the game's engine sound at replicated RPM; waiting for stop";
                    return false;
                }
                bool throttleMatches = Mathf.Abs(vehicle.powertrain.engine.ThrottlePosition - state.Throttle) <= 0.02f;
                bool sourcePlaying = engineSound.Active && engineSound.Clip != null &&
                    engineSound.Source != null && engineSound.Source.isPlaying &&
                    engineSound.Source.pitch > 0f && engineSound.Source.volume > 0f;
                if (!throttleMatches || !sourcePlaying)
                {
                    status = "settling remote audio: replicated throttle=" + state.Throttle +
                        ", engine throttle=" + vehicle.powertrain.engine.ThrottlePosition +
                        ", source playing=" + (engineSound.Source?.isPlaying == true) +
                        ", pitch=" + (engineSound.Source?.pitch ?? 0f) +
                        ", volume=" + (engineSound.Source?.volume ?? 0f);
                    if (Time.unscaledTime - runningStateAt > 5f)
                        throw new InvalidOperationException("Remote engine sound did not settle from replicated input: " + status +
                            "; patch diagnostics: " + RemoteVehicleEngineSoundPatch.DiagnosticSummary(vehicle));
                    return false;
                }
                Debug.Log("MonMP vehicle audio probe (host, remote engine): " + AudioProbe(vehicle));
                Singleton<GameAssets>.i.AUDIO_VehicleMixer.audioMixer.GetFloat("attenuation", out float hostDb);
                if (Mathf.Abs(hostDb - VehicleVolumePatch.UserDb()) > 0.1f)
                    throw new InvalidOperationException("The host's vehicle mixer is at " + hostDb + " dB, not the Vehicles setting " + VehicleVolumePatch.UserDb() + " dB (#121).");
                playbackObserved = true;
                playbackObservedAt = Time.unscaledTime;
                status = "host engine loop is playing from replicated RPM and throttle; waiting for stop and state restore";
                return false;
            }

            return false;
        }

        private bool TickGuest(SharedVehicles shared, VehicleController vehicle)
        {
            if (phase == 0)
            {
                if (vehicle.VehicleMultiplayerInstanceType != Vehicle.MultiplayerInstanceType.Remote || !vehicle.vehicleRigidbody.isKinematic)
                    throw new InvalidOperationException("Guest engine-audio baseline is not a kinematic Remote ATV.");
                MovePlayer(gameplay, shared.TestSeat + Vector3.right * 1.5f);
                phase = 1;
                phaseStartedAt = Time.unscaledTime;
                status = "settling guest pose near ATV seat";
                return false;
            }

            if (phase == 1)
            {
                if (shared.TestState.Driver.Owner != plugin.Session!.LocalPeerId)
                {
                    if (Time.unscaledTime - phaseStartedAt > 12f)
                        throw new InvalidOperationException("Guest did not acquire the ATV after settling near its seat.");
                    if (Time.unscaledTime - phaseStartedAt >= 0.7f && Time.unscaledTime - lastEnterAt >= 0.8f)
                    {
                        shared.TestEnter();
                        lastEnterAt = Time.unscaledTime;
                        status = "requesting ATV driver lease";
                    }
                    return false;
                }
                if (vehicle.VehicleMultiplayerInstanceType != Vehicle.MultiplayerInstanceType.Local || vehicle.vehicleRigidbody.isKinematic)
                    throw new InvalidOperationException("Guest driver lease did not restore native vehicle simulation.");

                vehicle.input.autoSetInput = false;
                vehicle.input.Throttle = 0;
                vehicle.input.Brakes = 0;
                vehicle.input.Handbrake = 1;
                vehicle.input.states.clutch = 1;
                vehicle.powertrain.transmission.ShiftInto(0, true);
                vehicle.Get_IgnitionPosition = 2;
                phase = 2;
                phaseStartedAt = Time.unscaledTime;
                status = "starting native engine in neutral";
                return false;
            }

            if (phase == 2)
            {
                if (vehicle.isEngineRunning)
                {
                    vehicle.Get_IgnitionPosition = 1;
                    vehicle.input.Throttle = 0.55f;
                    phase = 3;
                    phaseStartedAt = Time.unscaledTime;
                    runningStateAt = -1f;
                    holdStartedAt = -1f;
                    status = "waiting for engine and neutral input to settle";
                    return false;
                }
                if (Time.unscaledTime - phaseStartedAt > 6f)
                    throw new InvalidOperationException("Guest ATV engine did not start for the remote audio fixture.");
                return false;
            }

            if (phase == 3)
            {
                // ThrottlePosition is a powertrain output cache, not the replicated
                // control value. NWH updates it during physics substeps and can leave
                // it at zero between the scripted input change and the next substep.
                // VehicleState captures normalized input.states.throttle instead.
                if (vehicle.input.states.throttle < 0.3f)
                    vehicle.input.states.throttle = 0.55f;

                if (!vehicle.isEngineRunning || vehicle.input.states.throttle < 0.3f)
                {
                    runningStateAt = -1f;
                    holdStartedAt = -1f;
                    if (Time.unscaledTime - phaseStartedAt > 8f)
                        throw new InvalidOperationException("Guest engine did not settle with neutral throttle: " +
                            DescribeGuestEngine(vehicle, shared));
                    status = "waiting for native engine to settle: " + DescribeGuestEngine(vehicle, shared);
                    return false;
                }

                if (runningStateAt < 0f) runningStateAt = Time.unscaledTime;
                if (holdStartedAt < 0f && Time.unscaledTime - runningStateAt >= 0.25f)
                {
                    holdStartedAt = Time.unscaledTime;
                    status = "holding running engine and neutral throttle for host playback check";
                }
                if (holdStartedAt < 0f)
                {
                    if (Time.unscaledTime - phaseStartedAt > 8f)
                        throw new InvalidOperationException("Guest engine did not remain running for the remote audio fixture: " +
                            DescribeGuestEngine(vehicle, shared));
                    return false;
                }
                if (Time.unscaledTime - phaseStartedAt > GuestHoldSeconds + 10f)
                    throw new InvalidOperationException("Guest engine lost its stable hold before the host check completed: " +
                        DescribeGuestEngine(vehicle, shared));
                if (!probed && Time.unscaledTime - holdStartedAt > 2f) {
                    probed = true; Debug.Log("MonMP vehicle audio probe (guest, local engine): " + AudioProbe(vehicle));
                    Singleton<GameAssets>.i.AUDIO_VehicleMixer.audioMixer.GetFloat("attenuation", out float guestDb);
                    if (Mathf.Abs(guestDb - VehicleVolumePatch.Expected) > 0.1f || guestDb > VehicleVolumePatch.UserDb() + 0.1f)
                        throw new InvalidOperationException("The driving guest's vehicle mixer is at " + guestDb + " dB, expected " + VehicleVolumePatch.Expected + " dB (#121).");
                }
                if (Time.unscaledTime - holdStartedAt < GuestHoldSeconds) return false;

                vehicle.input.Throttle = 0;
                vehicle.Get_IgnitionPosition = 0;
                shared.TestExit();
                phase = 4;
                phaseStartedAt = Time.unscaledTime;
                status = "releasing guest driver lease";
                return false;
            }

            if (phase == 4)
            {
                if (shared.TestState.Driver.Owner != ItemLease.Unowned) return false;
                status = "guest engine presentation fixture completed";
                return true;
            }

            return false;
        }

        // Mixer identity, levels and the listener's actual output peak, to find what makes vehicles loud (#121).
        internal static string AudioProbe(VehicleController vehicle)
        {
            var manager = vehicle.soundManager;
            var game = Singleton<GameAssets>.i.AUDIO_VehicleMixer?.audioMixer;
            var nwh = manager.mixer;
            float gameDb = float.NaN, nwhDb = float.NaN;
            game?.GetFloat("attenuation", out gameDb);
            nwh?.GetFloat("attenuation", out nwhDb);
            var source = manager.engineRunningComponent.Source;
            var samples = new float[2048];
            AudioListener.GetOutputData(samples, 0);
            float peak = 0f, sum = 0f;
            foreach (var sample in samples) { peak = Mathf.Max(peak, Mathf.Abs(sample)); sum += sample * sample; }
            var engineSources = UnityEngine.Object.FindObjectsOfType<AudioSource>().Where(s => s.isPlaying && s.outputAudioMixerGroup != null &&
                (s.outputAudioMixerGroup.audioMixer == nwh || s.outputAudioMixerGroup.audioMixer == game)).ToList();
            return "game mixer=" + (game == null ? "null" : game.name + "#" + game.GetInstanceID()) + " attenuation=" + gameDb +
                ", NWH mixer=" + (nwh == null ? "null" : nwh.name + "#" + nwh.GetInstanceID()) + " attenuation=" + nwhDb +
                ", NWH master output=" + manager.masterGroup?.audioMixer?.outputAudioMixerGroup?.name +
                ", pref=" + (PlayerPrefs.HasKey("Audio_VehiclesVolume") ? PlayerPrefs.GetInt("Audio_VehiclesVolume").ToString() : "none") +
                ", listener volume=" + AudioListener.volume + ", listeners=" + UnityEngine.Object.FindObjectsOfType<AudioListener>().Count(l => l.isActiveAndEnabled) +
                ", engine source volume=" + source?.volume + " pitch=" + source?.pitch + " blend=" + source?.spatialBlend + " group=" + source?.outputAudioMixerGroup?.name +
                ", master volume=" + manager.masterVolume + ", playing vehicle sources=" + engineSources.Count + " [" +
                string.Join("; ", engineSources.Take(24).Select(s => s.transform.root.name + "/" + (s.clip == null ? "?" : s.clip.name) + "@" + s.volume.ToString("0.00") +
                    "/" + s.outputAudioMixerGroup.name + "/d=" + Vector3.Distance(s.transform.position, Camera.main == null ? Vector3.zero : Camera.main.transform.position).ToString("0"))) +
                "], output peak=" + peak + " rms=" + Mathf.Sqrt(sum / samples.Length) + ", " + ResProbe(vehicle);
        }
        private static bool ResPlaying(VehicleController vehicle, out float loudest)
        {
            loudest = 0f; bool playing = false;
            foreach (var res in vehicle.GetComponentsInChildren<RealisticEngineSound>(true))
                foreach (var source in res.GetComponentsInChildren<AudioSource>(true))
                    if (source.isPlaying && source.volume > 0f) { playing = true; loudest = Mathf.Max(loudest, source.volume); }
            return playing;
        }

        internal static string ResProbe(VehicleController vehicle)
        {
            var all = vehicle.GetComponentsInChildren<RealisticEngineSound>(true);
            var bridges = vehicle.GetComponentsInChildren<NWH.VehiclePhysics.NWH2_RES>(true);
            return "awake=" + vehicle.IsAwake + " running=" + vehicle.powertrain.engine.IsRunning + " rpm=" + vehicle.powertrain.engine.RPM.ToString("0") +
                " vertical=" + vehicle.input.Vertical.ToString("0.00") + " nwhEngineOn=" + vehicle.soundManager.engineRunningComponent.IsOn +
                ", RES=" + all.Length + " [" + string.Join("; ", all.Select(r => "enabled=" + r.enabled + " active=" + r.gameObject.activeInHierarchy +
                    " near=" + r.isCameraNear + " rpm=" + r.engineCurrentRPM.ToString("0") + " gas=" + r.gasPedalPressing + " master=" + r.masterVolume +
                    " mixer=" + r.audioMixer?.name + " sources=" + r.GetComponentsInChildren<AudioSource>(true).Length)) + "]" +
                ", RES bridges=" + string.Join(",", bridges.Select(b => b.enabled + "/" + b.gameObject.activeInHierarchy));
        }

        private static string DescribeGuestEngine(VehicleController vehicle, SharedVehicles shared) =>
            "running=" + vehicle.isEngineRunning + ", input=" + vehicle.input.Throttle +
            ", state=" + vehicle.input.states.throttle + ", engine=" + vehicle.powertrain.engine.ThrottlePosition +
            ", auto=" + vehicle.input.autoSetInput + ", ignition=" + vehicle.Get_IgnitionPosition +
            ", rpm=" + vehicle.powertrain.engine.RPM + ", owner=" + shared.TestState.Driver.Owner + ".";

        private static void MovePlayer(Gameplay gameplay, Vector3 location)
        {
            gameplay.PlayerWalking.transform.position = location;
            if (gameplay.Get_Player_Rigidbody == null) return;
            gameplay.Get_Player_Rigidbody.position = location;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
    }
}
