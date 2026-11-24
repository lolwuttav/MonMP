using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Adapter 1: furniture light switches and their actual illuminated state only.
    // No UnityEvent or arbitrary single-player interaction is replayed on behalf of a peer.
    internal sealed class SharedLights : IDisposable
    {
        private sealed class Binding
        {
            public ulong Id;
            public Furniture_Light Light = null!;
            public Furniture_SwitchButton Switch = null!;
            public byte Original;
            public byte Authority;
            public uint Revision;
        }
        private sealed class SwitchPresentation
        {
            public readonly DoorPresentationIntent Intent = new DoorPresentationIntent();
            public readonly HashSet<ulong> Replies = new HashSet<ulong>();
            public readonly List<(uint Revision, bool On)> Sounds = new List<(uint, bool)>();
            public uint SilentRevision;
            public bool SoundResolved;
        }
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Furniture_Light, Binding> byLight = new Dictionary<Furniture_Light, Binding>();
        private readonly Dictionary<Furniture_SwitchButton, List<Binding>> switches = new Dictionary<Furniture_SwitchButton, List<Binding>>();
        private readonly Dictionary<Furniture_SwitchButton, SwitchPresentation> presentations = new Dictionary<Furniture_SwitchButton, SwitchPresentation>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly BunkerLightVisibility bunkerVisibility;
        private NetworkSession? session;
        private WorldChannel? channel;
        private float nextCapture;
        private bool appliedReplica;
        private ulong nextInteraction;
        private static readonly AccessTools.FieldRef<Furniture_Light, bool> NeedElectricity = AccessTools.FieldRefAccess<Furniture_Light, bool>("NeedElectricity");
        private static readonly AccessTools.FieldRef<Furniture_Light, bool> NeedHydroElectricity = AccessTools.FieldRefAccess<Furniture_Light, bool>("NeedHydroElectricity");
        public bool IsGuest => session?.IsRunning == true && !session.IsHost;
        public bool Ready => channel?.Ready == true;
        public int Count => bindings.Count;
        public string CatalogHash { get; }

#if MONMP_DEVELOPMENT
        internal ulong TestEntity => bindings.Values.Where(Live).Min(b => b.Id);
#endif

        // The light the host changed most (its test light); host and guest can see different live
        // sets when the host's save has built structures the guest's world is still waking.

#if MONMP_DEVELOPMENT
        internal ulong TestHostChangedEntity => bindings.Values.OrderByDescending(b => channel!.Records[b.Id].Revision).ThenBy(b => b.Id).First().Id;


        internal bool TestLive(ulong id) => Live(bindings[id]);


        internal Vector3 TestPosition(ulong id) => bindings[id].Switch.transform.position;


        internal WorldRecord TestRecord(ulong id) => channel!.Records[id];


        internal void TestClick(ulong id) => bindings[id].Switch.MouseDown();


        internal string TestMismatches => string.Join(", ", bindings.Values.Where(b => Capture(b.Light, b.Switch) != channel!.Records[b.Id].Payload[0])
            .Select(b => RuntimeProbe.CanonicalPath(b.Light.transform) + " local=" + Capture(b.Light, b.Switch) + " host=" + channel!.Records[b.Id].Payload[0] +
                " active=" + b.Light.gameObject.activeInHierarchy + " source=" + (b.Light.lightSource != null)));


        internal bool TestSynchronized => Ready && bindings.Values.All(b => Capture(b.Light, b.Switch) == channel!.Records[b.Id].Payload[0]);


        internal void TestDamageReplica(ulong id)
        {
            if (!IsGuest) throw new InvalidOperationException("Only a test guest replica may be changed.");
            Apply(bindings[id], (byte)(channel!.Records[id].Payload[0] ^ 3));
        }


        internal bool TestRestored => bindings.Values.All(b => b.Light != null && Capture(b.Light, b.Switch) == b.Original);
#endif

        public event Action? SnapshotReady;

        public SharedLights(string diagnosticsDirectory, NativeEntityRegistry nativeRegistry)
        {
            var field = AccessTools.Field(typeof(Furniture_Light), "SwitchButton");
            var identities = new IdentityRegistry();
            var lines = new List<string>();
            foreach (var light in UnityEngine.Object.FindObjectsOfType<Furniture_Light>(true))
            {
                // Include inactive lights: structures such as the Sugar Shack activate theirs once
                // built, and a menu-joining guest registers from an unbuilt fresh world.
                // Furniture_Light creates lightSource when it first wakes, so a never-activated light
                // has none yet; Capture/Apply already treat a missing source as unlit.
                if (!light.gameObject.scene.IsValid()) continue;
                var button = field.GetValue(light) as Furniture_SwitchButton;
                if (button == null || !button.gameObject.scene.IsValid()) continue;
                // Exclude switches with extra serialized actions. Runtime light callbacks are
                // identified by their Furniture_Light relationship and are never invoked here.
                if (button.Get_SwitchOn.GetPersistentEventCount() != 0 || button.Get_SwitchOff.GetPersistentEventCount() != 0 || button.Get_RightMouseButton.GetPersistentEventCount() != 0) continue;
                int ordinal = Array.IndexOf(light.GetComponents<Furniture_Light>(), light);
                string path = RuntimeProbe.CanonicalPath(light.transform) + "/" + StaticId.Segment(typeof(Furniture_Light).FullName!, ordinal) + "/shared-light-v1";
                ulong id = identities.Register(path);
                var binding = new Binding { Id = id, Light = light, Switch = button, Original = Capture(light, button), Authority = Capture(light, button), Revision = 1 };
                bindings.Add(id, binding);
                byLight.Add(light, binding);
                nativeRegistry.Bind(id, 1, light.gameObject, path, true, EntityPersistence.SessionOnly);
                if (!switches.TryGetValue(button, out var group)) { group = new List<Binding>(); switches.Add(button, group); }
                group.Add(binding);
                lines.Add(id.ToString("X16") + "\t" + path + "\t" + RuntimeProbe.CanonicalPath(button.transform));
            }
            foreach (var pair in switches)
            {
                pair.Value.Sort((a, b) => a.Id.CompareTo(b.Id));
                presentations.Add(pair.Key, new SwitchPresentation());
            }
            if (bindings.Count == 0) throw new InvalidOperationException("No initialized furniture lights available. Wait for world loading to finish.");
            bunkerVisibility = new BunkerLightVisibility(bindings.Values.Select(binding => binding.Light));
            CatalogHash = WorldRecord.Digest(InitialRecords(), true);

#if MONMP_DEVELOPMENT
            Directory.CreateDirectory(diagnosticsDirectory);
#endif

            lines.Sort(StringComparer.Ordinal);

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnosticsDirectory, "shared-lights.tsv"), new[] { "# Catalog SHA-256: " + CatalogHash, "id\tlight-path\tswitch-path" }.Concat(lines));
#endif

        }

        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, 1, new[] { b.Original }));
        public void Attach(NetworkSession session, WorldChannel world)
        {
            if (channel != null) throw new InvalidOperationException("Shared lights already attached.");
            this.session = session; channel = world;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed;
            channel.CommandRequested += Toggle;
            channel.CommandRejected += Rejected;
            channel.ReplyReceived += Reply;
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            // Validate every payload and binding before changing any Unity state. Scoped repairs
            // touch only their own switches; the full snapshot also retires pending presentation.
            var lights = records.Values.Where(r => r.Adapter == 1).ToArray();
            foreach (var record in lights) Validate(record);
            if (!IsGuest) return;
            appliedReplica = true;
            foreach (var record in lights)
            {
                var b = bindings[record.Id];
                Reset(presentations[b.Switch]);
                Accept(b, record.Payload[0], record.Revision);
            }
            foreach (var button in lights.Select(r => bindings[r.Id].Switch).Distinct()) Present(switches[button]);
            SnapshotReady?.Invoke();
        }
        private void Changed(WorldRecord record)
        {
            if (!IsGuest || record.Adapter != 1) return;
            Validate(record);
            var b = bindings[record.Id];
            if (record.Revision < b.Revision) return;
            var group = switches[b.Switch];
            bool was = (group[0].Authority & 1) != 0;
            Accept(b, record.Payload[0], record.Revision);
            if (b == group[0] && was != ((b.Authority & 1) != 0)) AuthoritySound(b, record.Revision);
            presentations[b.Switch].Intent.Observe((group[0].Authority & 1) != 0);
            Present(group);
        }
        private static void Accept(Binding b, byte state, uint revision)
        {
            if (revision < b.Revision) return;
            b.Authority = state; b.Revision = revision;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) => presence.Remove(peer);
        public bool Handles(Furniture_SwitchButton button) => session?.IsRunning == true && switches.ContainsKey(button);
        public bool Handles(Furniture_Light light) => IsGuest && byLight.ContainsKey(light);
        public void Click(Furniture_SwitchButton button)
        {
            if (!Ready || !switches.TryGetValue(button, out var group) || button == null || !button.isActiveAndEnabled ||
                !button.gameObject.activeInHierarchy || MonMPPlugin.Instance?.World?.SaveGate.Active == true) return;
            bool on;
            if (IsGuest)
            {
                var presentation = presentations[button];
                presentation.Intent.Toggle((group[0].Authority & 1) != 0);
                on = presentation.Intent.DesiredOpen;
                Send(group);
                appliedReplica = true;
                Present(group);
            }
            else
            {
                on = !button.isOn;
                // The host's command already provides its one native switch cue.
                channel!.Request(group[0].Id);
            }
            // The native click's own feedback, which the shared transaction skips: the switch sound,
            // the no-power error, and the hovered ON/OFF tag.
            if (IsGuest) using (SpatialSounds.Local()) SwitchSound(on);
            if (on)
                foreach (var item in group)
                {
                    var generator = item.Light.Get_HomeGenerator;
                    if (generator == null) continue;
                    string? error = NeedElectricity(item.Light) && !generator.Get_isEngineRunning ? "ERROR_NoElectricity"
                        : NeedHydroElectricity(item.Light) && !generator.Get_IsHydroRunning ? "ERROR_NoGridElectricity" : null;
                    if (error == null) continue;
                    try { Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText(error)); } catch { }
                    break;
                }
            Refresh(button);
        }
        private void Send(List<Binding> group)
        {
            var b = group[0]; var presentation = presentations[b.Switch];
            if (!presentation.Intent.TryBegin(++nextInteraction, (b.Authority & 1) != 0, Time.unscaledTime)) return;
            presentation.Replies.Clear(); presentation.SoundResolved = false;
            channel!.Request(b.Id, new ClientInteractionRequest(presentation.Intent.Token, new[] { (byte)((b.Authority & 1) != 0 ? 1 : 0) }).Encode(), expectedRevision: b.Revision);
        }
        private static bool CanEmit(Furniture_Light light)
        {
            if (light == null || light.lightSource == null) return false;
            var generator = light.Get_HomeGenerator;
            return (!NeedElectricity(light) || generator != null && generator.Get_isEngineRunning) &&
                (!NeedHydroElectricity(light) || generator != null && generator.Get_IsHydroRunning);
        }
        private void Present(List<Binding> group)
        {
            var presentation = presentations[group[0].Switch];
            bool on = presentation.Intent.PresentationOpen((group[0].Authority & 1) != 0);
            foreach (var b in group)
            {
                if (b.Light == null || b.Switch == null) continue;
                bool lit = presentation.Intent.Active ? on && CanEmit(b.Light) : (b.Authority & 2) != 0;
                byte state = (byte)((on ? 1 : 0) | (lit ? 2 : 0));
                if (Capture(b.Light, b.Switch) != state) Apply(b, state);
            }
            Refresh(group[0].Switch);
        }
        private static void SwitchSound(bool on)
        {
            try
            {
                var sound = Singleton<SOUND_Manager>.i;
                if (sound == null) return;
                if (on) sound.PlaySound_SwitchOn(); else sound.PlaySound_SwitchOff();
            }
            catch { }
        }
        // Another player's click: its sound (scoped to them by the channel) and a hovered tag refresh.
        private void Observed(Furniture_SwitchButton button, bool on)
        {
            SwitchSound(on);
            Refresh(button);
        }
        private static void Refresh(Furniture_SwitchButton button)
        {
            try
            {
                var player = Singleton<Gameplay>.i?.Player;
                if (player != null && player.Get_CurrentInteractable == button && button.isActiveAndEnabled) button.MouseHover();
            }
            catch { }
        }
        private void AuthoritySound(Binding b, uint revision)
        {
            var presentation = presentations[b.Switch];
            bool on = (b.Authority & 1) != 0;
            if (revision == presentation.SilentRevision) { presentation.SilentRevision = 0; return; }
            // The delta has no actor identity. Hold its cue until this exact operation's
            // receipt can distinguish our accepted echo from a competing player's click.
            if (presentation.Intent.Token != 0 && !presentation.SoundResolved)
            {
                if (presentation.Sounds.Count == 32)
                {
                    using (SpatialSounds.At(b.Switch.transform.position)) SwitchSound(presentation.Sounds[0].On);
                    presentation.Sounds.RemoveAt(0);
                }
                presentation.Sounds.Add((revision, on));
            }
            else SwitchSound(on);
        }
        private static void ResolveSounds(Furniture_SwitchButton button, SwitchPresentation presentation, uint silentRevision)
        {
            presentation.SilentRevision = silentRevision;
            presentation.SoundResolved = true;
            foreach (var sound in presentation.Sounds)
                if (sound.Revision == silentRevision) presentation.SilentRevision = 0;
                else if (button != null) using (SpatialSounds.At(button.transform.position)) SwitchSound(sound.On);
            presentation.Sounds.Clear();
        }
        private static void Reset(SwitchPresentation presentation)
        {
            presentation.Intent.Clear(); presentation.Replies.Clear(); presentation.Sounds.Clear();
            presentation.SilentRevision = 0; presentation.SoundResolved = false;
        }
        [MonMP.Core.World.WorldCommandAdapters(1)]
        private void Toggle(uint peer, ulong entity, byte[] payload)
        {
            if (session?.IsHost != true || !bindings.TryGetValue(entity, out var binding) || binding.Switch == null) return;
            bool correlated = ClientInteractionRequest.Is(payload);
            var request = correlated ? ClientInteractionRequest.Decode(payload) : default;
            if (correlated)
            {
                if (request.Payload.Length != 1 || request.Payload[0] > 1) throw new InvalidDataException("Invalid light intent.");
            }
            else if (payload.Length != 0) throw new InvalidDataException("Invalid light command.");
            var status = ExecuteToggle(peer, binding, correlated ? request.Payload[0] : (byte?)null);
            if (correlated && peer != 0) Receipt(peer, switches[binding.Switch], request.Token, status);
        }
        private ClientInteractionStatus ExecuteToggle(uint peer, Binding binding, byte? observed)
        {
            // Unbuilt structures keep their switch inactive; natively it cannot be clicked.
            if (!binding.Switch.isActiveAndEnabled || !binding.Switch.gameObject.activeInHierarchy ||
                MonMPPlugin.Instance?.World?.SaveGate.Active == true) return ClientInteractionStatus.Refused;
            if (peer != 0)
            {
                if (!presence.TryGetValue(peer, out var position) || Time.unscaledTime - position.Time > 2) return ClientInteractionStatus.Refused;
                var location = new Vector3(position.Pose.X, position.Pose.Y, position.Pose.Z);
                if ((binding.Switch.transform.position - location).sqrMagnitude > 4.5f * 4.5f) return ClientInteractionStatus.Refused;
            }
            if (observed.HasValue && (observed.Value != 0) != binding.Switch.isOn) return ClientInteractionStatus.Stale;
            bool value = !binding.Switch.isOn;
            binding.Switch.isOn = value; binding.Switch.UpdateVisual();
            Observed(binding.Switch, value);
            foreach (var item in switches[binding.Switch])
            {
                // A light that never woke has no lightSource yet and native TurnOn/TurnOff would throw.
                if (item.Light.lightSource == null) { Publish(item); continue; }
                if (value) item.Light.Get_IsOn = true; // Host's existing electricity rules decide whether it emits light.
                else item.Light.TurnOff();
                Publish(item);
            }
            return ClientInteractionStatus.Accepted;
        }
        private void Receipt(uint peer, List<Binding> group, ulong token, ClientInteractionStatus status)
        {
            // Each linked lamp has its own revision and native power requirements. Reply per
            // lamp so large linked groups remain bounded without inventing a shared revision.
            foreach (var b in group) Publish(b);
            foreach (var b in group)
            {
                var record = channel!.Records[b.Id];
                channel.Reply(peer, b.Id, new ClientInteractionReceipt(token, status, record.Revision, record.Payload).Encode());
            }
        }
        private void Rejected(uint peer, ulong entity, byte[] payload)
        {
            if (session?.IsHost != true || !ClientInteractionRequest.Is(payload) || !bindings.TryGetValue(entity, out var b) || b.Switch == null) return;
            var request = ClientInteractionRequest.Decode(payload);
            if (request.Payload.Length != 1 || request.Payload[0] > 1) throw new InvalidDataException("Invalid light intent.");
            Receipt(peer, switches[b.Switch], request.Token, ClientInteractionStatus.Stale);
        }
        private void Reply(ulong entity, byte[] payload)
        {
            if (!IsGuest || !ClientInteractionReceipt.Is(payload) || !bindings.TryGetValue(entity, out var b) || b.Switch == null) return;
            var receipt = ClientInteractionReceipt.Decode(payload);
            if (receipt.State.Length != 1 || receipt.State[0] > 3) throw new InvalidDataException("Invalid light receipt.");
            var group = switches[b.Switch]; var presentation = presentations[b.Switch];
            if (presentation.Intent.Token == 0 || presentation.Intent.Token != receipt.Token || !presentation.Replies.Add(entity)) return;
            Accept(b, receipt.State[0], receipt.Revision);
            if (b == group[0]) ResolveSounds(b.Switch, presentation, receipt.Status == ClientInteractionStatus.Accepted ? receipt.Revision : 0);
            if (presentation.Replies.Count == group.Count)
            {
                presentation.Intent.Acknowledge(receipt.Token, receipt.Status == ClientInteractionStatus.Accepted, (group[0].Authority & 1) != 0, Time.unscaledTime);
                presentation.Intent.Observe((group[0].Authority & 1) != 0);
                Send(group);
            }
            Present(group);
        }
        public void Update()
        {
            if (session?.IsRunning == true) bunkerVisibility.Update();
            if (IsGuest)
                foreach (var group in switches.Values)
                {
                    var presentation = presentations[group[0].Switch];
                    if (!presentation.Intent.Active) continue;
                    bool invalid = !Ready || group[0].Switch == null || !group[0].Switch.isActiveAndEnabled ||
                        !group[0].Switch.gameObject.activeInHierarchy || MonMPPlugin.Instance?.World?.SaveGate.Active == true;
                    if (!invalid && !presentation.Intent.Expire(Time.unscaledTime)) continue;
                    ResolveSounds(group[0].Switch, presentation, 0);
                    Reset(presentation);
                    Present(group);
                }
            if (session?.IsHost == true && Ready && Time.unscaledTime >= nextCapture)
            {
                nextCapture = Time.unscaledTime + 0.1f;
                foreach (var binding in bindings.Values) Publish(binding);
            }
            else if (IsGuest && appliedReplica && Ready && Time.unscaledTime >= nextCapture)
            {
                // A light the host's progression activates after the snapshot wakes with its native
                // default; bring it back to the host's record.
                nextCapture = Time.unscaledTime + 0.5f;
                foreach (var group in switches.Values) Present(group);
            }
        }
        private static bool Live(Binding b) => b.Light != null && b.Light.lightSource != null && b.Light.gameObject.activeInHierarchy &&
            b.Switch != null && b.Switch.gameObject.activeInHierarchy;
        private void Publish(Binding b)
        {
            if (b.Light == null || b.Switch == null) throw new InvalidOperationException("A registered furniture light was removed; rehost after reloading the scene.");
            byte state = Capture(b.Light, b.Switch);
            if (channel!.Records.TryGetValue(b.Id, out var previous) && previous.PayloadSpan.Length == 1 && previous.PayloadSpan[0] == state) return;
            channel.Publish(b.Id, new[] { state });
        }
        private static byte Capture(Furniture_Light light, Furniture_SwitchButton button) => (byte)((button.isOn ? 1 : 0) | (light.lightSource != null && light.lightSource.enabled ? 2 : 0));
        private void Validate(WorldRecord record)
        {
            byte[] value = record.Payload;
            if (record.Adapter != 1 || value.Length != 1 || value[0] > 3 || !bindings.TryGetValue(record.Id, out var b) || b.Light == null || b.Switch == null)
                throw new InvalidDataException("Invalid shared-light state or missing scene binding.");
        }
        private void Apply(WorldRecord record) => Apply(bindings[record.Id], record.Payload[0]);
        private static void Apply(Binding b, byte state)
        {
            b.Switch.isOn = (state & 1) != 0; b.Switch.UpdateVisual();
            bool lit = (state & 2) != 0;
            if (b.Light.lightSource != null) b.Light.lightSource.enabled = lit;
            if (b.Light.mesh == null) return;
            var materials = b.Light.mesh.materials;
            int index = b.Light.MaterialIndex;
            if (index < 0 || index >= materials.Length) return;
            if (lit) { materials[index].EnableKeyword("_EMISSION"); materials[index].SetColor("_EmissionColor", b.Light.LightColor); }
            else materials[index].DisableKeyword("_EMISSION");
        }
        public void Dispose()
        {
            bunkerVisibility.Dispose();
            bool restore = appliedReplica;
            // Plugin calls Dispose before stopping the network so the role is still known.
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null)
            {
                channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Toggle;
                channel.CommandRejected -= Rejected; channel.ReplyReceived -= Reply;
            }
            foreach (var presentation in presentations.Values) Reset(presentation);
            channel = null;
            if (restore) foreach (var b in bindings.Values) if (b.Light != null && b.Switch != null) Apply(b, b.Original);
            session = null; presence.Clear();
        }
    }
}
