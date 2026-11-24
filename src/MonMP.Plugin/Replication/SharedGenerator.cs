using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // The home generator's combustion engine and kill switch belong to the host. Guests request
    // native start/stop controls and run only the local presentation methods for accepted state.
    // Fuel itself is synchronized by SharedFuel.
    internal sealed class SharedGenerator : IDisposable
    {
        private static readonly FieldInfo KillSwitchField = AccessTools.Field(typeof(HomeGenerator), "KillSwitchButton");
        private static readonly FieldInfo KillSwitchStateField = AccessTools.Field(typeof(HomeGenerator), "isKillSwitchOff");
        private static readonly FieldInfo EngineRunningField = AccessTools.Field(typeof(HomeGenerator), "isEngineRunning");
        private static readonly FieldInfo AutoLightsField = RequireNativeField(typeof(HomeGenerator), "AutoLightsConnected", typeof(Furniture_AutoLight[]));
        // Keep these exact native field references validated: GetkWh is a billing getter and must
        // never be called by guests merely to redraw this gauge.
        private static readonly FieldInfo HomeMeterField = RequireNativeField(typeof(HomeGenerator), "home_HydroCompteur", typeof(Home_HydroCompteur));
        private static readonly FieldInfo MeterKwhField = RequireNativeField(typeof(Home_HydroCompteur), "kWh", typeof(float));
        private static readonly FieldInfo HydroStoreField = AccessTools.Field(typeof(HomeGenerator), "store_Hydro");
        private static readonly FieldInfo HydroBuildDayField = AccessTools.Field(typeof(Store_HydroSaintClin), "DaySinceBuildStarted");
        private static readonly FieldInfo HydroCutField = AccessTools.Field(typeof(Store_HydroSaintClin), "HydroCut");
        private static readonly MethodInfo EngineStartVisual = AccessTools.Method(typeof(HomeGenerator), "EngineStartingUp");
        private static readonly MethodInfo EngineStopVisual = AccessTools.Method(typeof(HomeGenerator), "EngineShutingDown");
        private const float CommandRange = 4f, CommandInterval = 0.35f;

        private readonly HomeGenerator? generator;
        private readonly SOUND_WoodSplitter? sound;
        private Store_HydroSaintClin? hydro;
        private readonly Furniture_SwitchButton? killSwitch;
        private readonly bool host;
        private readonly ulong id;
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, float> lastPeerCommand = new Dictionary<uint, float>();
        private sealed class AutoLightBinding
        {
            internal ulong Id;
            internal Furniture_AutoLight Light = null!;
            internal Material? Material;
            internal byte Original, State;
            internal bool OriginalSourceCaptured, OriginalMaterialCaptured, Applied;
            internal Color OriginalEmission;
        }
        private readonly Dictionary<ulong, AutoLightBinding> autoLights = new Dictionary<ulong, AutoLightBinding>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private GeneratorState original, state;
        private bool applying, disposed;
        private float lastGuestCommand, nextCapture;
        private float nextGuestPull;
        private ulong nextInteraction;
        private int hydroPresentationUpdates;
        private int powerPresentationTransitions;

        internal int Count => generator != null ? 1 : 0;
        internal bool GuestBlocks => !host && !applying && !disposed && session?.IsRunning == true;

#if MONMP_DEVELOPMENT
        internal GeneratorState TestState => state;


        internal GeneratorState TestNative => Capture();


        internal GeneratorState TestOriginal => original;


        internal bool TestRestored => !host && generator != null && Capture().Equals(original);


        internal bool TestHydroQuery => generator != null && generator.Get_IsHydroRunning;


        internal bool TestPowerQuery => generator != null && generator.Get_isEngineRunning;


        internal bool TestRawHydroQuery => hydro != null && hydro.Get_isHydroRunning;


        internal float TestMeterKwh => ReadMeterKwh();


        internal int TestHydroPresentationUpdates => hydroPresentationUpdates;


        internal int TestPowerPresentationTransitions => powerPresentationTransitions;


        internal HomeGenerator? TestGenerator => generator;


        internal Furniture_SwitchButton? TestKillSwitch => killSwitch;


        internal ulong TestEntity => id;
#endif

        private bool Ready => !disposed && session?.IsRunning == true && channel?.Ready == true && generator != null;

        private static FieldInfo RequireNativeField(Type owner, string name, Type expectedType)
        {
            var field = AccessTools.Field(owner, name);
            if (field == null || field.FieldType != expectedType)
                throw new InvalidOperationException("Expected native field " + owner.FullName + "." + name + " of type " + expectedType.FullName + ".");
            return field;
        }

        public SharedGenerator(bool host, string diagnostics, NativeEntityRegistry identities)
        {
            this.host = host;
            generator = Object.FindObjectsOfType<HomeGenerator>(true).FirstOrDefault(g => g.gameObject.scene.IsValid());
            string line = "none";
            if (generator != null)
            {
                sound = generator.GetComponent<SOUND_WoodSplitter>();
                killSwitch = KillSwitchField.GetValue(generator) as Furniture_SwitchButton;
                if (killSwitch == null) throw new InvalidOperationException("Home generator kill switch is missing.");
                hydro = HydroStoreField.GetValue(generator) as Store_HydroSaintClin ??
                    Object.FindObjectsOfType<Store_HydroSaintClin>(true).FirstOrDefault(s => s.gameObject.scene.IsValid());
                string identity = RuntimeProbe.CanonicalPath(generator.transform) + "/home-generator-v1";
                id = StaticId.Hash(identity);
                original = state = Capture();
                _ = state.Encode();
                identities.Bind(id, GeneratorState.Adapter, generator.gameObject, identity, true, EntityPersistence.SessionOnly);
                foreach (var light in (AutoLightsField.GetValue(generator) as Furniture_AutoLight[] ?? Array.Empty<Furniture_AutoLight>()).Where(l => l != null).Distinct())
                {
                    int ordinal = Array.IndexOf(light.GetComponents<Furniture_AutoLight>(), light);
                    string lampIdentity = RuntimeProbe.CanonicalPath(light.transform) + "/" + StaticId.Segment(typeof(Furniture_AutoLight).FullName!, ordinal) + "/generator-auto-light-v1";
                    var lamp = new AutoLightBinding { Id = StaticId.Hash(lampIdentity), Light = light };
                    lamp.State = CaptureAutoLight(lamp);
                    CaptureOriginalAutoLight(lamp);
                    autoLights.Add(lamp.Id, lamp);
                    identities.Bind(lamp.Id, GeneratorState.Adapter, light.gameObject, lampIdentity, true, EntityPersistence.SessionOnly);
                }
                line = id.ToString("X16") + "\t" + identity + "\trunning=" + state.Running + "\tkillSwitchEnabled=" + state.KillSwitchEnabled + "\thydroRunning=" + state.HydroRunning + "\tmeterKwh=" + state.MeterKwh;
            }

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-generator.tsv"), new[] { "id\tidentity\tstate", line });
#endif

        }

        public IEnumerable<WorldRecord> InitialRecords()
        {
            if (generator != null) yield return new WorldRecord(id, 1, GeneratorState.Adapter, state.Encode());
            foreach (var lamp in autoLights.Values) yield return new WorldRecord(lamp.Id, 1, GeneratorState.Adapter, new[] { lamp.State });
        }

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.CommandRequested += Command; channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
        }

        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) { presence.Remove(peer); lastPeerCommand.Remove(peer); }
        private GeneratorState Capture()
        {
            if (hydro == null && generator != null)
                hydro = HydroStoreField.GetValue(generator) as Store_HydroSaintClin ??
                    Object.FindObjectsOfType<Store_HydroSaintClin>(true).FirstOrDefault(s => s.gameObject.scene.IsValid());
            return new GeneratorState(generator!.Get_IsCombustionEngineRunning, generator.Get_KillSwitchState,
                generator.Get_IsHydroRunning, ReadMeterKwh());
        }

        private float ReadMeterKwh()
        {
            if (generator == null) return 0f;
            var meter = HomeMeterField.GetValue(generator) as Home_HydroCompteur;
            if (meter == null) throw new InvalidOperationException("Home generator kWh meter is missing.");
            return (float)MeterKwhField.GetValue(meter)!;
        }

        private void ApplyMeterKwh(float value)
        {
            if (generator == null) return;
            var meter = HomeMeterField.GetValue(generator) as Home_HydroCompteur;
            if (meter == null) throw new InvalidOperationException("Home generator kWh meter is missing.");
            // Home_HydroCompteur.FixedUpdate consumes this scalar; assigning it has no billing side effect.
            if ((float)MeterKwhField.GetValue(meter)! != value) MeterKwhField.SetValue(meter, value);
        }

        // Guests keep their own HydroData, but every native generator power query reads the host's
        // derived availability while a session is active. The native hydro record remains local.
        internal bool TryGetHydroRunning(HomeGenerator native, out bool running)
        {
            if (!host && !disposed && session?.IsRunning == true && native == generator)
            {
                running = state.HydroRunning;
                return true;
            }
            running = false;
            return false;
        }


#if MONMP_DEVELOPMENT
        internal void TestSetHydroAvailability(bool running)
        {
            if (!host || hydro == null) throw new InvalidOperationException("Hydro test needs the host's hydro store.");
            HydroBuildDayField.SetValue(hydro, 5);
            HydroCutField.SetValue(hydro, !running);
            nextCapture = 0;
        }


        internal void TestApplyState(GeneratorState next)
        {
            if (host || generator == null || disposed) throw new InvalidOperationException("Generator state probe is guest-only.");
            var previous = state;
            state = next;
            ApplyNative(next, previous);
        }
#endif


        // True means the guest interaction was handled and must not run against its private copy.
        internal bool InterceptStart(HomeGenerator native)
        {
            if (host || native != generator || disposed || session?.IsRunning != true) return false;
            Send(new GeneratorCommand(GeneratorCommandKind.Start));
            return true;
        }

        internal bool InterceptKillSwitch(Furniture_SwitchButton native)
        {
            if (host || native != killSwitch || disposed || session?.IsRunning != true) return false;
            Send(new GeneratorCommand(GeneratorCommandKind.SetKillSwitch, !state.KillSwitchEnabled));
            return true;
        }

        internal bool TrySound(ulong entity, out SOUND_WoodSplitter? native)
        {
            native = entity == id && generator != null && generator.gameObject.activeInHierarchy ? sound : null;
            return native != null;
        }

        private void Send(GeneratorCommand command)
        {
            if (!Ready || channel?.FullySynchronized != true || generator?.gameObject.activeInHierarchy != true ||
                MonMPPlugin.Instance?.World?.SaveGate.Active == true || Time.unscaledTime - lastGuestCommand < CommandInterval) return;
            lastGuestCommand = Time.unscaledTime;
            bool presented = false;
            if (command.Kind == GeneratorCommandKind.Start)
            {
                // Native pulls occupy half a second. Do not preview another pull while the
                // previous local attempt is still in that native busy interval.
                if (!state.Running && Time.unscaledTime >= nextGuestPull)
                { presented = MachineControlPresentation.LocalPull(sound); nextGuestPull = Time.unscaledTime + 0.5f; }
            }
            else presented = MachineControlPresentation.LocalSwitch(command.Enabled);
            byte[] payload = command.Encode();
            if (presented) payload = new ClientInteractionRequest(++nextInteraction, payload).Encode();
            channel.Request(id, payload, true);
        }

        private bool PeerIsNear(uint peer)
        {
            if (peer == 0 || !presence.TryGetValue(peer, out var seen) || Time.unscaledTime - seen.Time > 2 || seen.Pose.State != (byte)PlayerState.Walking)
                return false;
            var at = new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z);
            return (at - generator!.transform.position).sqrMagnitude <= CommandRange * CommandRange;
        }

        [MonMP.Core.World.WorldCommandAdapters(GeneratorState.Adapter)]

        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || entity != id || generator == null || !generator.gameObject.activeInHierarchy || !PeerIsNear(peer) ||
                Time.unscaledTime - (lastPeerCommand.TryGetValue(peer, out float last) ? last : -CommandInterval) < CommandInterval ||
                MonMPPlugin.Instance?.World?.SaveGate.Active == true) return;
            GeneratorCommand command;
            bool presented = ClientInteractionRequest.Is(payload);
            try
            {
                if (presented) payload = ClientInteractionRequest.Decode(payload).Payload;
                command = GeneratorCommand.Decode(payload);
            }
            catch (InvalidDataException) { return; }
            lastPeerCommand[peer] = Time.unscaledTime;
            bool previousSwitch = generator.Get_KillSwitchState;
            // A host click may be waiting for the periodic state capture when this request
            // arrives. Preserve its separate sound before attributing this guest operation.
            if (previousSwitch != state.KillSwitchEnabled)
                MachineControlPresentation.AnnounceSwitch(id, previousSwitch, generator.transform.position);
            applying = true;
            try
            {
                using var spatial = SpatialSounds.AtPeer(peer);
                if (command.Kind == GeneratorCommandKind.Start)
                {
                    // Keep the native fuel, kill-switch and start-cord checks on the host.
                    using var pull = MachineControlPresentation.HostPull(sound, id, presented ? peer : 0);
                    generator.StartEngine();
                }
                else if (generator.Get_KillSwitchState != command.Enabled)
                {
                    if (command.Enabled)
                    {
                        generator.KillSwitchOn();
                        Singleton<SOUND_Manager>.i.PlaySound_SwitchOn();
                    }
                    else
                    {
                        generator.KillSwitchOff();
                        Singleton<SOUND_Manager>.i.PlaySound_SwitchOff();
                    }
                    killSwitch!.isOn = command.Enabled;
                    killSwitch.UpdateVisual();
                }
            }
            finally { applying = false; }
            if (generator.Get_KillSwitchState != previousSwitch)
                MachineControlPresentation.AnnounceSwitch(id, generator.Get_KillSwitchState, generator.transform.position, presented ? peer : 0);
            Publish(Capture());
        }

        private void Publish(GeneratorState next)
        {
            if (next.Equals(state)) return;
            state = next;
            channel!.Publish(id, next.Encode());
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host || generator == null) return;
            foreach (var lamp in autoLights.Values)
                if (records.TryGetValue(lamp.Id, out var lampRecord)) ValidateAutoLight(lampRecord);
            // Source callbacks can change lamps too. Preserve the guest's pre-apply state first,
            // then apply exact host lamp samples after those callbacks have completed.
            foreach (var lamp in autoLights.Values) CaptureOriginalAutoLight(lamp);
            if (records.TryGetValue(id, out var record)) Apply(record, false);
            foreach (var lamp in autoLights.Values)
                if (records.TryGetValue(lamp.Id, out var lampRecord)) ApplyAutoLightRecord(lampRecord);
        }

        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == GeneratorState.Adapter) Apply(record, true);
        }

        private void Apply(WorldRecord record, bool live)
        {
            if (autoLights.ContainsKey(record.Id)) { ApplyAutoLightRecord(record); return; }
            if (record.Id != id || record.Adapter != GeneratorState.Adapter) throw new InvalidDataException("Unknown generator record.");
            var next = GeneratorState.Decode(record.Payload);
            var previous = state;
            state = next;
            // Switch one-shots travel as actor-bound events. State can be coalesced or arrive
            // before its request result without repeating the requester's immediate click.
            foreach (var lamp in autoLights.Values) CaptureOriginalAutoLight(lamp);
            ApplyNative(next, previous);
            foreach (var lamp in autoLights.Values)
                if (lamp.Applied) ApplyAutoLight(lamp, lamp.State);
        }

        private static Material? AutoLightMaterial(AutoLightBinding lamp)
        {
            if (lamp.Material == null && lamp.Light != null && lamp.Light.mesh != null)
            {
                var materials = lamp.Light.mesh.materials;
                int index = lamp.Light.MaterialIndex;
                if (index >= 0 && index < materials.Length) lamp.Material = materials[index];
            }
            return lamp.Material;
        }

        private static byte CaptureAutoLight(AutoLightBinding lamp)
        {
            var material = AutoLightMaterial(lamp);
            return (byte)((lamp.Light != null && lamp.Light.lightSource != null && lamp.Light.lightSource.enabled ? 1 : 0) |
                (material != null && material.IsKeywordEnabled("_EMISSION") ? 2 : 0));
        }

        private static void CaptureOriginalAutoLight(AutoLightBinding lamp)
        {
            if (lamp.Light == null) return;
            if (!lamp.OriginalSourceCaptured && lamp.Light.lightSource != null)
            {
                if (lamp.Light.lightSource.enabled) lamp.Original |= 1;
                lamp.OriginalSourceCaptured = true;
            }
            var material = AutoLightMaterial(lamp);
            if (!lamp.OriginalMaterialCaptured && material != null)
            {
                if (material.IsKeywordEnabled("_EMISSION")) lamp.Original |= 2;
                lamp.OriginalEmission = material.GetColor("_EmissionColor");
                lamp.OriginalMaterialCaptured = true;
            }
        }

        private static void ValidateAutoLight(WorldRecord record)
        {
            if (record.Adapter != GeneratorState.Adapter || record.Payload == null || record.Payload.Length != 1 || record.Payload[0] > 3)
                throw new InvalidDataException("Invalid generator automatic-light sample.");
        }

        private void ApplyAutoLightRecord(WorldRecord record)
        {
            ValidateAutoLight(record);
            if (!autoLights.TryGetValue(record.Id, out var lamp)) throw new InvalidDataException("Unknown generator automatic light.");
            CaptureOriginalAutoLight(lamp);
            lamp.State = record.Payload[0]; lamp.Applied = true;
            ApplyAutoLight(lamp, lamp.State);
        }

        private static void ApplyAutoLight(AutoLightBinding lamp, byte value)
        {
            if (lamp.Light == null) return;
            if (lamp.Light.lightSource != null) lamp.Light.lightSource.enabled = (value & 1) != 0;
            var material = AutoLightMaterial(lamp);
            if (material == null) return;
            if ((value & 2) != 0)
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", lamp.Light.LightColor);
            }
            else material.DisableKeyword("_EMISSION");
        }

        private void UpdateAutoLights()
        {
            foreach (var lamp in autoLights.Values)
            {
                if (host)
                {
                    byte next = CaptureAutoLight(lamp);
                    if (next == lamp.State) continue;
                    lamp.State = next;
                    channel!.Publish(lamp.Id, new[] { next });
                }
                else if (lamp.Applied)
                {
                    // An inactive lamp may build its source in native Start after the snapshot.
                    CaptureOriginalAutoLight(lamp);
                    ApplyAutoLight(lamp, lamp.State);
                }
            }
        }

        private void RestoreAutoLights()
        {
            foreach (var lamp in autoLights.Values)
            {
                if (!lamp.Applied || lamp.Light == null) continue;
                if (lamp.OriginalSourceCaptured && lamp.Light.lightSource != null)
                    lamp.Light.lightSource.enabled = (lamp.Original & 1) != 0;
                var material = AutoLightMaterial(lamp);
                if (!lamp.OriginalMaterialCaptured || material == null) continue;
                if ((lamp.Original & 2) != 0) material.EnableKeyword("_EMISSION");
                else material.DisableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", lamp.OriginalEmission);
            }
        }

        private void ApplyNative(GeneratorState next, GeneratorState previous)
        {
            if (generator == null || killSwitch == null) return;
            bool wasPowered = previous.Running || previous.HydroRunning;
            bool isPowered = next.Running || next.HydroRunning;
            bool engineChanged = previous.Running != next.Running;
            bool powerChanged = wasPowered != isPowered;
            applying = true;
            try
            {
                ApplyMeterKwh(next.MeterKwh);
                if (generator.Get_KillSwitchState != next.KillSwitchEnabled)
                    KillSwitchStateField.SetValue(generator, next.KillSwitchEnabled);
                if (killSwitch.isOn != next.KillSwitchEnabled)
                {
                    killSwitch.isOn = next.KillSwitchEnabled;
                    killSwitch.UpdateVisual();
                }
                if (generator.Get_IsCombustionEngineRunning != next.Running)
                {
                    if (engineChanged && powerChanged)
                    {
                        // These native methods update the engine sound/flag and signal power once.
                        (next.Running ? EngineStartVisual : EngineStopVisual).Invoke(generator, null);
                    }
                    else
                    {
                        // Keep engine audio in sync when hydro already supplies (or combustion
                        // still supplies) power; EngineStartingUp/ShutingDown also toggle devices.
                        EngineRunningField.SetValue(generator, next.Running);
                        var sound = generator.GetComponent<SOUND_WoodSplitter>();
                        if (sound != null)
                        {
                            if (next.Running) sound.StartEngine();
                            else sound.StopEngine();
                        }
                    }
                }
                if (powerChanged && !engineChanged)
                {
                    if (isPowered) generator.Signal_PowerIsOn();
                    else generator.Signal_PowerIsOff();
                }
                if (powerChanged) powerPresentationTransitions++;
                if (previous.HydroRunning != next.HydroRunning) hydroPresentationUpdates++;
            }
            finally { applying = false; }
        }

        public void Update()
        {
            if (!Ready || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 0.2f;
            if (host)
            {
                var next = Capture();
                if (next.KillSwitchEnabled != state.KillSwitchEnabled && generator != null)
                    MachineControlPresentation.AnnounceSwitch(id, next.KillSwitchEnabled, generator.transform.position);
                Publish(next);
            }
            UpdateAutoLights();
        }

        public void Dispose()
        {
            if (disposed) return;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.CommandRequested -= Command; channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            if (!host && generator != null)
            {
                var previous = state;
                state = original;
                ApplyNative(original, previous);
                RestoreAutoLights();
            }
            disposed = true; session = null; channel = null; presence.Clear(); lastPeerCommand.Clear();
        }
    }
}
