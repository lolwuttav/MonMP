using System;
using System.Collections.Generic;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Underglow kits: the on/off, mode, zone, intensity and colour buttons only changed the clicking peer's
    // lights, and nobody else ever saw them. The host owns the settings of each shared vehicle's kit: guests
    // send their button presses, the host runs the native buttons (ignition gate included) and every peer
    // mirrors saved settings and the host's current native light/material values. Records are keyed by the vehicle's shared id, so bought cars join as
    // SharedVehicles binds them.
    internal sealed class SharedUnderglow : IDisposable
    {
        public const ushort Adapter = UnderglowState.Adapter;
        internal const byte OnOff = 1, Mode = 2, Color = 3, Cycle = 4, Force = 5;
        private const float CommandRange = 6f, ScanInterval = 3f;
        private static readonly AccessTools.FieldRef<Vehicle_Underglow, NWH.Vehicle> VehicleField = AccessTools.FieldRefAccess<Vehicle_Underglow, NWH.Vehicle>("vehicle");
        private static readonly AccessTools.FieldRef<Vehicle_Underglow, bool> IgnitionField = AccessTools.FieldRefAccess<Vehicle_Underglow, bool>("_ignitionOn");
        private static readonly AccessTools.FieldRef<Vehicle_Underglow, Light> UnderField = AccessTools.FieldRefAccess<Vehicle_Underglow, Light>("LightSource");
        private static readonly AccessTools.FieldRef<Vehicle_Underglow, Light> LeftField = AccessTools.FieldRefAccess<Vehicle_Underglow, Light>("FeetLight_L");
        private static readonly AccessTools.FieldRef<Vehicle_Underglow, Light> RightField = AccessTools.FieldRefAccess<Vehicle_Underglow, Light>("FeetLight_R");
        private static readonly AccessTools.FieldRef<Vehicle_Underglow, MeshRenderer> BloomField = AccessTools.FieldRefAccess<Vehicle_Underglow, MeshRenderer>("Underglow_Bloom");
        private static readonly int EmissionProperty = Shader.PropertyToID("_EmissionColor");
        private static readonly System.Reflection.MethodInfo UpdateLightsMethod = AccessTools.Method(typeof(Vehicle_Underglow), "UpdateLights", Type.EmptyTypes);
        private static readonly System.Reflection.MethodInfo IgnitionOnMethod = AccessTools.Method(typeof(Vehicle_Underglow), "Vehicle_IgnitionOn", Type.EmptyTypes);
        private static readonly System.Reflection.MethodInfo IgnitionOffMethod = AccessTools.Method(typeof(Vehicle_Underglow), "Vehicle_IgnitionOff", Type.EmptyTypes);
        private static readonly System.Reflection.FieldInfo[] SettingsButtons =
        {
            AccessTools.Field(typeof(Vehicle_Underglow), "Switch_Button_Mode"),
            AccessTools.Field(typeof(Vehicle_Underglow), "Switch_Button_Cycle"),
            AccessTools.Field(typeof(Vehicle_Underglow), "Switch_Button_Intensity"),
            AccessTools.Field(typeof(Vehicle_Underglow), "Switch_Button_Color")
        };

        // A vehicle replica's ignition changed by field write (SharedVehicles): run the kit's native
        // ignition listener, which the skipped vehicle event would have run.
        internal static void IgnitionChanged(NWH.Vehicle vehicle, bool on)
        {
            try
            {
                foreach (var kit in vehicle.GetComponentsInChildren<Vehicle_Underglow>(true))
                    if (VehicleField(kit) == vehicle) (on ? IgnitionOnMethod : IgnitionOffMethod).Invoke(kit, null);
            }
            catch (Exception ex) { Debug.LogWarning("MonMP underglow ignition update failed: " + ex.Message); }
        }

        private sealed class Binding
        {
            public ulong Id;
            public Vehicle_Underglow Kit = null!;
            public UnderglowState Original, State;
            public bool HasState;
            public Material? Material;
            public readonly int[] Settings = new int[5];
            public readonly byte[] CaptureBuffer = new byte[UnderglowState.EncodedLength];
        }
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Vehicle_Underglow, Binding> byKit = new Dictionary<Vehicle_Underglow, Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly bool host;
        private readonly SharedVehicles vehicles;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying;
        private float nextScan, nextCapture;
        public int Count => bindings.Count;

        public SharedUnderglow(bool host, SharedVehicles vehicles) { this.host = host; this.vehicles = vehicles; }

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) => presence.Remove(peer);

        internal static ulong RecordId(ulong vehicle) =>
            WorldRecord.DynamicIdStart | Math.Max(1UL, StaticId.Hash(vehicle.ToString("X16") + "/underglow-v1") & 0x7FFFFFFFFFFFFFFFUL);

        private void Scan(bool periodic = false)
        {
            foreach (var kit in WorldScans.Find<Vehicle_Underglow>(periodic, true))
            {
                if (kit == null || byKit.ContainsKey(kit) || !kit.gameObject.scene.IsValid()) continue;
                ulong vehicle = vehicles.EntityOf(VehicleField(kit));
                if (vehicle == 0) continue;
                ulong id = RecordId(vehicle);
                if (bindings.ContainsKey(id)) continue;
                var b = new Binding { Id = id, Kit = kit };
                b.Original = Capture(b);
                bindings.Add(id, b); byKit.Add(kit, b);
                if (host) channel!.Register(new WorldRecord(id, 1, Adapter, b.Original.Encode()));
                else if (channel!.Records.TryGetValue(id, out var record)) Apply(b, record.Payload);
            }
        }

        // True when a guest's button press was sent to the host instead of running natively.
        internal bool Intercept(Vehicle_Underglow kit, byte action)
        {
            if (host || applying || session?.IsRunning != true || !byKit.TryGetValue(kit, out var b)) return false;
            // Animation revises this record continuously. Range and native button
            // gates authorize commands; a rendered-frame revision does not.
            if (channel?.Ready == true && channel.Records.ContainsKey(b.Id)) channel.Request(b.Id, new[] { action }, useLease: true);
            Singleton<SOUND_Manager>.i?.PlaySound_SwitchOn();
            return true;
        }

        [MonMP.Core.World.WorldCommandAdapters(SharedUnderglow.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || !bindings.TryGetValue(id, out var b) || b.Kit == null || payload.Length != 1) return;
            if (!b.Kit.gameObject.activeInHierarchy) return;
            if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2) return;
            if ((new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z) - b.Kit.transform.position).sqrMagnitude > CommandRange * CommandRange) return;
            switch (payload[0])
            {
                case OnOff: b.Kit.Button_OnOff(); break;
                case Mode: b.Kit.Button_Mode(); break;
                case Color: b.Kit.Button_CLR(); break;
                case Cycle: b.Kit.Button_Cycle(); break;
                case Force: b.Kit.Button_Force(); break;
                default: return;
            }
            Publish(b);
        }

        private void Publish(Binding b)
        {
            if (b.Kit == null || !channel!.Records.ContainsKey(b.Id)) return;
            // Publish copies changed records. The adapter owns this scratch array;
            // unchanged native samples need no new payload allocation.
            Capture(b).WriteTo(b.CaptureBuffer);
            channel.Publish(b.Id, b.CaptureBuffer);
        }

        private static bool SourcesReady(Binding b)
        {
            if (b.Kit == null || UnderField(b.Kit) == null || LeftField(b.Kit) == null || RightField(b.Kit) == null || BloomField(b.Kit) == null) return false;
            // Unity clones the shared material on the first access only. Keep that
            // native instance for all captures/applies, including inactive installs.
            if (b.Material == null) b.Material = BloomField(b.Kit).material;
            return b.Material != null && b.Material.HasProperty(EmissionProperty);
        }

        private static UnderglowColor ColorValue(UnityEngine.Color c) => new UnderglowColor(c.r, c.g, c.b, c.a);
        private static UnityEngine.Color NativeColor(UnderglowColor c) => new UnityEngine.Color(c.Red, c.Green, c.Blue, c.Alpha);
        private static UnderglowLight LightValue(Light light) => new UnderglowLight(ColorValue(light.color), light.intensity);
        private static UnderglowState Capture(Binding b)
        {
            var s = b.Kit.Get_Settings;
            bool ready = SourcesReady(b);
            return new UnderglowState((byte)s[0], (byte)s[1], (byte)s[2], (byte)s[3], (byte)s[4], ready, IgnitionField(b.Kit),
                ready && UnderField(b.Kit).enabled, ready && LeftField(b.Kit).enabled, ready && RightField(b.Kit).enabled,
                ready && BloomField(b.Kit).gameObject.activeSelf,
                ready ? LightValue(UnderField(b.Kit)) : default, ready ? LightValue(LeftField(b.Kit)) : default,
                ready ? LightValue(RightField(b.Kit)) : default, ready ? ColorValue(b.Material!.color) : default,
                ready ? ColorValue(b.Material!.GetColor(EmissionProperty)) : default);
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values)
                if (record.Adapter == Adapter && bindings.TryGetValue(record.Id, out var b)) Apply(b, record.Payload);
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != Adapter || !bindings.TryGetValue(record.Id, out var b)) return;
            Apply(b, record.Payload);
        }

        private void Apply(Binding b, byte[] payload)
        {
            UnderglowState state;
            try { state = UnderglowState.Decode(payload); }
            catch (System.IO.InvalidDataException) { return; }
            Apply(b, state, false);
            b.State = state; b.HasState = true;
        }

        private void Apply(Binding b, UnderglowState state, bool restore)
        {
            if (b.Kit == null) return;
            if (!restore) CaptureOriginalPresentationIfReady(b);
            var current = b.Kit.Get_Settings;
            bool settingsChanged = current[0] != state.Active || current[1] != state.Mode || current[2] != state.Cycle ||
                current[3] != state.Intensity || current[4] != state.ColorIndex;
            bool ignitionChanged = IgnitionField(b.Kit) != state.Ignition;
            applying = true;
            try
            {
                if (settingsChanged)
                {
                    b.Settings[0] = state.Active; b.Settings[1] = state.Mode; b.Settings[2] = state.Cycle;
                    b.Settings[3] = state.Intensity; b.Settings[4] = state.ColorIndex;
                    b.Kit.LoadSettingFromSave(b.Settings);
                }
                IgnitionField(b.Kit) = state.Ignition;
                if ((settingsChanged || ignitionChanged || restore) && SourcesReady(b)) UpdateLightsMethod.Invoke(b.Kit, null);
                ApplyPresentation(b, state);
                if (!host && settingsChanged && !restore) RefreshSettingsPrompt(b.Kit);
            }
            catch (Exception) { }
            finally { applying = false; }
        }

        private static void ApplyLight(Light light, UnderglowLight value, bool enabled)
        { light.color = NativeColor(value.Color); light.intensity = value.Intensity; light.enabled = enabled; }

        private static void ApplyPresentation(Binding b, UnderglowState state)
        {
            if (!state.Ready || !SourcesReady(b)) return;
            CaptureOriginalPresentationIfReady(b);
            ApplyLight(UnderField(b.Kit), state.Under, state.UnderEnabled);
            ApplyLight(LeftField(b.Kit), state.Left, state.LeftEnabled);
            ApplyLight(RightField(b.Kit), state.Right, state.RightEnabled);
            b.Material!.color = NativeColor(state.Bloom);
            b.Material.SetColor(EmissionProperty, NativeColor(state.Emission));
            if (BloomField(b.Kit).gameObject.activeSelf != state.BloomActive) BloomField(b.Kit).gameObject.SetActive(state.BloomActive);
        }

        private static void CaptureOriginalPresentationIfReady(Binding b)
        {
            if (b.Original.Ready || !SourcesReady(b)) return;
            var s = b.Original; var p = Capture(b);
            b.Original = new UnderglowState(s.Active, s.Mode, s.Cycle, s.Intensity, s.ColorIndex,
                true, s.Ignition, p.UnderEnabled, p.LeftEnabled, p.RightEnabled, p.BloomActive,
                p.Under, p.Left, p.Right, p.Bloom, p.Emission);
        }

        internal bool ShouldRunNativeUpdate(Vehicle_Underglow kit)
        {
            if (host || session?.IsRunning != true || !byKit.TryGetValue(kit, out var b) ||
                !b.HasState || !b.State.Ready || !SourcesReady(b)) return true;
            CaptureOriginalPresentationIfReady(b);
            return false;
        }

        private static void RefreshSettingsPrompt(Vehicle_Underglow kit)
        {
            // Native buttons reset the clicking player's cached hover text. Guest clicks run on
            // the host, so refresh only this guest's currently hovered settings button after apply.
            var gameplay = MonMPPlugin.Instance?.Gameplay;
            var player = gameplay == null ? null : gameplay.Player;
            if (player == null) return;
            var current = player.Get_CurrentInteractable;
            if (current == null) return;
            foreach (var field in SettingsButtons)
                if (field.GetValue(kit) as Furniture_SwitchButton == current)
                { player.ResetCurrentInteractable(current); return; }
        }

        public void Update()
        {
            if (session?.IsRunning != true || channel?.Ready != true) return;
            bool periodic = nextScan > 0;
            if (Time.unscaledTime >= nextScan && WorldScans.TryClaim<Vehicle_Underglow>(periodic)) { nextScan = periodic ? WorldScans.NextDeadline(Time.unscaledTime, ScanInterval) : Time.unscaledTime + ScanInterval; Scan(periodic); }
        }

        // Run after native Update. Hosts publish current native values; guests
        // also correct ignition-listener/Start writes without restarting RGB hue.
        public void LateUpdate()
        {
            if (session?.IsRunning != true || channel?.Ready != true) return;
            if (host)
            {
                if (Time.unscaledTime < nextCapture) return;
                nextCapture = Time.unscaledTime + 0.05f;
                foreach (var b in bindings.Values) Publish(b);
            }
            else foreach (var b in bindings.Values)
                if (b.HasState && b.Kit != null) ApplyPresentation(b, b.State);
        }


#if MONMP_DEVELOPMENT
        internal bool TestBound(Vehicle_Underglow kit) => byKit.ContainsKey(kit);


        internal UnderglowState TestNative(Vehicle_Underglow kit) => Capture(byKit[kit]);


        internal UnderglowState TestState(Vehicle_Underglow kit) => UnderglowState.Decode(channel!.Records[byKit[kit].Id].Payload);


        internal UnderglowState TestCachedState(Vehicle_Underglow kit) => byKit[kit].State;


        internal uint TestRevision(Vehicle_Underglow kit) => channel!.Records[byKit[kit].Id].Revision;


        internal void TestCaptureBaseline(Vehicle_Underglow kit) { byKit[kit].Original = Capture(byKit[kit]); }


        internal void TestScan() => Scan();
#endif


        public void Dispose()
        {
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            if (!host) foreach (var b in bindings.Values) Apply(b, b.Original, true);
            session = null; channel = null; presence.Clear();
        }
    }
}
