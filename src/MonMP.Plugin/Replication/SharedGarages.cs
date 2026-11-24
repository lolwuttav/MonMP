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
    // Motorized garages: the home garage (its native build stage and animator-driven door) and the
    // speedway garage (lerped door). The host's garages are authoritative. A guest press, from a wall
    // button or a vehicle's remote opener, reaches the native ButtonPressed and is sent to the host,
    // which runs its own ButtonPressed under its electricity rules. Guests replay the door motion
    // natively, so doors keep colliding where every player sees them.
    internal sealed class SharedGarages : IDisposable
    {
        private sealed class Binding
        {
            public ulong Id;
            public GarageKind Kind;
            public MonoBehaviour Native = null!;
            public GarageState State, Original;
            public float LastPress;
            public bool Applied;
            public HomeGarageCompressorPresentation? Compressor;
        }
        private static readonly FieldInfo HomeCompressor = AccessTools.Field(typeof(HomeGarage), "airCompressor");
        private static readonly FieldInfo HomeDays = AccessTools.Field(typeof(HomeGarage), "DaysSinceBuilt");
        private static readonly FieldInfo HomeAnimator = AccessTools.Field(typeof(HomeGarage), "animator");
        private static readonly FieldInfo HomeStage1 = AccessTools.Field(typeof(HomeGarage), "Stage1");
        private static readonly FieldInfo HomeStage2 = AccessTools.Field(typeof(HomeGarage), "Stage2");
        private static readonly FieldInfo HomeFinal = AccessTools.Field(typeof(HomeGarage), "FinalGarage");
        private static readonly FieldInfo SpeedwayLocation = AccessTools.Field(typeof(Speedway_Garage), "DoorLocation");
        private static readonly FieldInfo SpeedwayTarget = AccessTools.Field(typeof(Speedway_Garage), "TargetLocation");
        private static readonly FieldInfo SpeedwayDoor = AccessTools.Field(typeof(Speedway_Garage), "GarageDoor");
        private static readonly FieldInfo SpeedwayOpen = AccessTools.Field(typeof(Speedway_Garage), "GarageDoor_OpenLocation");
        private static readonly FieldInfo SpeedwayClosed = AccessTools.Field(typeof(Speedway_Garage), "GarageDoor_ClosedLocation");
        private const string Movement = "Movement";
        private const float PressRange = 60;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<MonoBehaviour, Binding> byNative = new Dictionary<MonoBehaviour, Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly bool host;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, disposed;
        private float nextCapture;
        public int Count => bindings.Count;
        private bool Ready => !disposed && session?.IsRunning == true && channel?.Ready == true;
        // Guests leave detectors and generator cut-offs to the host's garage.
        internal bool GuestBlocks => !host && !applying && session?.IsRunning == true && !disposed;
        public SharedGarages(bool host, string diagnostics, NativeEntityRegistry identities)
        {
            this.host = host;
            var lines = new List<string>();
            void Add(MonoBehaviour native, GarageKind kind)
            {
                if (native == null || !native.gameObject.scene.IsValid()) return;
                string identity = RuntimeProbe.CanonicalPath(native.transform) + (kind == GarageKind.Home ? "/home-garage-v1" : "/speedway-garage-v1");
                ulong id = StaticId.Hash(identity);
                if (bindings.ContainsKey(id)) return;
                var b = new Binding { Id = id, Kind = kind, Native = native };
                b.State = b.Original = Capture(b);
                if (!host && native is HomeGarage home && HomeCompressor.GetValue(home) is AirCompressor compressor && compressor != null)
                    b.Compressor = new HomeGarageCompressorPresentation(compressor.gameObject);
                bindings.Add(id, b); byNative.Add(native, b);
                identities.Bind(id, GarageState.Adapter, native.gameObject, identity, true, EntityPersistence.SessionOnly);
                lines.Add(id.ToString("X16") + "\t" + identity + "\t" + kind + "\tstage=" + b.State.Stage + "\tmotion=" + b.State.Motion + "\tposition=" + b.State.Position);
            }
            foreach (var garage in Object.FindObjectsOfType<HomeGarage>(true)) Add(garage, GarageKind.Home);
            foreach (var garage in Object.FindObjectsOfType<Speedway_Garage>(true)) Add(garage, GarageKind.Speedway);

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-garages.tsv"), new[] { "id\tidentity\tkind\tstate" }.Concat(lines.OrderBy(s => s)));
#endif

        }
        public IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, GarageState.Adapter, b.State.Encode()));
        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.CommandRequested += Command; channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
            channel.ReplyReceived += Reply;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) => presence.Remove(peer);

        // ---- Native capture/apply --------------------------------------------------------------
        private static Animator? AnimatorOf(HomeGarage garage) => HomeAnimator.GetValue(garage) as Animator;
        private static GarageState Capture(Binding b)
        {
            if (b.Native is HomeGarage home)
            {
                var animator = AnimatorOf(home);
                float motion = animator != null && animator.isActiveAndEnabled ? Mathf.Clamp(animator.GetFloat(Movement), -1, 1) : 0;
                return new GarageState(GarageKind.Home, (sbyte)Mathf.Clamp((int)HomeDays.GetValue(home), -1, 5), motion, home.Get_IsDoorOpen == 1 ? 1 : 0);
            }
            var speedway = (Speedway_Garage)b.Native;
            float target = (float)SpeedwayTarget.GetValue(speedway);
            return new GarageState(GarageKind.Speedway, 0, target >= 0.5f ? 1 : 0, Mathf.Clamp01((float)SpeedwayLocation.GetValue(speedway)));
        }
        private static void ApplyStage(HomeGarage home, int stage)
        {
            if (Mathf.Clamp((int)HomeDays.GetValue(home), -1, 5) == stage) return;
            HomeDays.SetValue(home, stage);
            (HomeStage1.GetValue(home) as GameObject)?.SetActive(stage > 0 && stage < 5);
            (HomeStage2.GetValue(home) as GameObject)?.SetActive(stage > 2 && stage < 5);
            (HomeFinal.GetValue(home) as GameObject)?.SetActive(stage >= 5);
            foreach (var vehicle in Singleton<GlobalClassManager>.i.GetAllVehicles())
                if (vehicle != null && vehicle.vehicle_PartsManager != null) vehicle.vehicle_PartsManager.Toggle_RemoteGarageOpener(stage >= 5);
        }
        private void ApplyNative(Binding b, GarageState state, bool snapshot)
        {
            if (b.Native is HomeGarage home)
            {
                ApplyStage(home, state.Stage);
                ApplyCompressor(b);
                var animator = AnimatorOf(home);
                if (state.Stage < 5 || animator == null || !animator.isActiveAndEnabled) return;
                float motion = animator.GetFloat(Movement);
                // A late joiner mirrors the native save load for a door that is already open.
                if (snapshot && state.Position == 1 && home.Get_IsDoorOpen == 0 && state.Motion > 0) animator.SetTrigger("OpenAtLoad");
                if (Mathf.Abs(motion - state.Motion) < 0.001f) return;
                animator.SetFloat(Movement, state.Motion);
                var sound = home.GetComponent<SOUND_GarageDoor>();
                if (sound != null && !snapshot) { if (state.Motion != 0) sound.Open(); else sound.Close(); }
                return;
            }
            var speedway = (Speedway_Garage)b.Native;
            float location = (float)SpeedwayLocation.GetValue(speedway);
            if (Mathf.Abs(location - state.Position) > 0.3f || (snapshot && Mathf.Abs(location - state.Position) > 0.02f))
            {
                SpeedwayLocation.SetValue(speedway, state.Position);
                var door = SpeedwayDoor.GetValue(speedway) as Transform;
                var open = SpeedwayOpen.GetValue(speedway) as Transform;
                var closed = SpeedwayClosed.GetValue(speedway) as Transform;
                if (door != null && open != null && closed != null) door.position = Vector3.Lerp(closed.position, open.position, state.Position);
            }
            // The native press toggles its binary target and runs the native travel and sound.
            if (((float)SpeedwayTarget.GetValue(speedway) >= 0.5f ? 1 : 0) != state.Motion) speedway.ButtonPressed();
        }

        // ---- Guest press interception ----------------------------------------------------------
        // Returns true when the native press must not run locally.
        internal bool Intercept(MonoBehaviour garage)
        {
            if (host || applying || disposed || session?.IsRunning != true || !byNative.TryGetValue(garage, out var b)) return false;
            if (Ready && Time.unscaledTime - b.LastPress > 0.4f)
            {
                b.LastPress = Time.unscaledTime;
                channel!.Request(b.Id, new[] { GarageState.Press }, true);
            }
            return true;
        }

        // ---- Host ------------------------------------------------------------------------------
        [MonMP.Core.World.WorldCommandAdapters(GarageState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || !bindings.TryGetValue(id, out var b) || !GarageState.IsPress(payload) || b.Native == null) return;
            if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2) return;
            if ((new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z) - b.Native.transform.position).sqrMagnitude > PressRange * PressRange) return;
            if (Time.unscaledTime - b.LastPress < 0.4f) return;
            if (b.Native is HomeGarage home)
            {
                if (!home.IsBuildCompleted || home.Get_HomeGenerator == null) return;
            }
            b.LastPress = Time.unscaledTime;
            applying = true;
            Patches.RemoteActionScope.LastError = null;
            try
            {
                using (Patches.RemoteActionScope.Enter())
                {
                    if (b.Native is HomeGarage h) h.ButtonPressed();
                    else ((Speedway_Garage)b.Native).ButtonPressed();
                }
            }
            finally { applying = false; }
            if (Patches.RemoteActionScope.LastError is string error)
                channel!.Reply(peer, id, NativeErrorReply.Encode(error));
            Publish(b, Capture(b));
        }
        private void Reply(ulong id, byte[] payload)
        {
            if (!host && bindings.ContainsKey(id) && NativeErrorReply.TryDecode(payload, out string error))
                Singleton<UI_ErrorMessage>.i?.Show(error);
        }
        private void Publish(Binding b, GarageState state) { b.State = state; channel!.Publish(b.Id, state.Encode()); }

        // ---- Guest apply -----------------------------------------------------------------------
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values.Where(r => r.Adapter == GarageState.Adapter).ToArray()) Apply(record);
        }
        private void Changed(WorldRecord record) { if (!host && record.Adapter == GarageState.Adapter) Apply(record); }
        private void Apply(WorldRecord record)
        {
            if (!bindings.TryGetValue(record.Id, out var b) || b.Native == null) throw new InvalidDataException("Unknown garage record.");
            var state = GarageState.Decode(record.Payload);
            if (state.Kind != b.Kind) throw new InvalidDataException("Garage record kind mismatch.");
            b.State = state;
            bool snapshot = !b.Applied; b.Applied = true;
            applying = true;
            try { ApplyNative(b, state, snapshot); }
            finally { applying = false; }
        }

        private static void ApplyCompressor(Binding binding)
        {
            var friendship = GameManager.Get_Friendship;
            int francis = (int)Actor_Type.Francis;
            if (binding.Applied && binding.Compressor != null && friendship != null && friendship.Length > francis)
                binding.Compressor.Apply(friendship[francis], binding.State.Stage >= 5);
        }
        public void Update()
        {
            if (!Ready || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 0.2f;
            if (!host)
            {
                // Friendship and garage snapshots can arrive in either order. Poll the final
                // shared conditions too, so native startup/activation cannot leave a stale root.
                foreach (var binding in bindings.Values) ApplyCompressor(binding);
                return;
            }
            foreach (var b in bindings.Values)
            {
                if (b.Native == null) continue;
                var state = Capture(b);
                if (!state.Equals(b.State)) Publish(b, state);
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.CommandRequested -= Command; channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.ReplyReceived -= Reply; }
            if (!host)
            {
                applying = true;
                try
                {
                    foreach (var b in bindings.Values)
                    {
                        if (b.Native != null && b.Applied)
                        {
                            if (b.Native is HomeGarage home) ApplyStage(home, b.Original.Stage);
                            else ApplyNative(b, b.Original, true);
                        }
                        b.Compressor?.Dispose();
                    }
                }
                finally { applying = false; }
            }
            disposed = true; session = null; channel = null;
        }

        // ---- Test hooks ------------------------------------------------------------------------

#if MONMP_DEVELOPMENT
        internal ulong TestHome => bindings.Values.FirstOrDefault(b => b.Kind == GarageKind.Home)?.Id ?? 0;


        internal ulong TestSpeedway => bindings.Values.FirstOrDefault(b => b.Kind == GarageKind.Speedway)?.Id ?? 0;


        internal GarageState TestState(ulong id) => bindings[id].State;


        internal GarageState TestLocal(ulong id) => Capture(bindings[id]);


        internal Vector3 TestPosition(ulong id) => bindings[id].Native.transform.position;


        internal void TestPress(ulong id)
        {
            if (bindings[id].Native is HomeGarage home) home.ButtonPressed();
            else ((Speedway_Garage)bindings[id].Native).ButtonPressed();
        }


        internal void TestSetHomeStage(int stage) => ApplyStage((HomeGarage)bindings[TestHome].Native, stage);


        internal void TestSetHomeMotion(float motion) => AnimatorOf((HomeGarage)bindings[TestHome].Native)?.SetFloat(Movement, motion);


        internal bool TestHomeFinalActive => (HomeFinal.GetValue(bindings[TestHome].Native) as GameObject)?.activeSelf == true;
#endif

    }
}
