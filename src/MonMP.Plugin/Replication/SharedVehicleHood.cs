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
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Native hood hinge physics simulate on the host only, exactly like the truck cabin doors: the
    // interior lever releases the latch, the exterior catch pops it under spring force, and either the
    // exterior close catch or the FixedUpdate settle closes it. Angle travels continuously while open.
    internal sealed partial class SharedVehicleHood : IDisposable
    {
        private sealed class Binding
        {
            public ulong Id;
            public Vehicle_Hood Hood = null!;
            public Vehicle_InteriorHoodRelease? Interior;
            public Vehicle_ExteriorHoodRelease? Exterior;
            public Vehicle_ExteriorHoodRelease? Closer;
            public MonoBehaviour[] ReleaseHandles = Array.Empty<MonoBehaviour>(), OpenHandles = Array.Empty<MonoBehaviour>(), CloseHandles = Array.Empty<MonoBehaviour>();
            public bool Host, ReplicaPhysicsDisabled;
            public Vector3 Position, Euler;
            public VehicleHoodState State;
            public VehicleHoodState Authority;
            public Func<bool>? CueBusLive;
            public uint Revision, LastClosedRevision;
            public readonly HoodLocalPresentation Preview = new HoodLocalPresentation();
            public float TargetAngle, PresentationAngle;
            public bool SawPresentationInterpolation;
            public Func<bool>? DeferReplica;
            public NativeReplicaHingeBaseline Original = null!;
            public readonly DeferredReplicaHingeState<VehicleHoodState> Deferred = new DeferredReplicaHingeState<VehicleHoodState>();
        }
        private static readonly FieldInfo IsOpenField = AccessTools.Field(typeof(Vehicle_Hood), "isOpen");
        private static readonly FieldInfo IsReleasedField = AccessTools.Field(typeof(Vehicle_Hood), "isReleased");
        private static readonly FieldInfo OriginalPositionField = AccessTools.Field(typeof(Vehicle_Hood), "originalLocalPosition");
        private static readonly FieldInfo OriginalEulerField = AccessTools.Field(typeof(Vehicle_Hood), "originalLocalEuler");
        private static readonly FieldInfo CloseButtonField = AccessTools.Field(typeof(Vehicle_Hood), "Collider_CloseHoodButton");
        private static readonly FieldInfo IsCloserField = AccessTools.Field(typeof(Vehicle_ExteriorHoodRelease), "isToCloseTheHood");
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Gameplay gameplay;
        private readonly bool host;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, disposed;
        private float nextCapture;
        public int Count => bindings.Count;
        public SharedVehicleHood(Gameplay gameplay, bool host, IEnumerable<VehicleController> vehicles, string diagnostics, NativeEntityRegistry identities,
            Func<VehicleController, bool>? deferReplica = null)
        {
            this.gameplay = gameplay; this.host = host;
            var lines = new List<string>();
            foreach (var vehicle in vehicles)
            {
                // Use the parts manager's own authoritative reference rather than a blind hierarchy search,
                // matching the trunk (which must avoid picking up the Konig's unrelated glovebox lid).
                var parts = vehicle.GetComponent<Vehicle_PartsManager>();
                var hood = parts?.Get_Vehicle_Hood;
                if (hood == null || hood.vehicleController != vehicle) continue;
                bool konigSaveBacked = IsBuildableCar(vehicle) &&
                    parts?.Door_Hood?.isInstalled == true && parts.Door_Hood.partInstalled != null;
                bool restoredOpenKonig = konigSaveBacked && IsRestoredOpenKonigHood(vehicle, hood, out _, out _);
                if (hood.Get_IsOpen || hood.GetComponent<Rigidbody>() != null || hood.GetComponent<Joint>() != null)
                {
                    if (!restoredOpenKonig) continue;
                }
                var exteriors = vehicle.GetComponentsInChildren<Vehicle_ExteriorHoodRelease>(true).Where(e => e.vehicle_Hood == hood).ToArray();
                var exterior = exteriors.FirstOrDefault(e => !(bool)IsCloserField.GetValue(e));
                var closer = exteriors.FirstOrDefault(e => (bool)IsCloserField.GetValue(e));
                var interiors = vehicle.GetComponentsInChildren<Vehicle_InteriorHoodRelease>(true).Where(i => exteriors.Contains(i.vehicle_ExteriorHoodRelease)).ToArray();
                var interior = interiors.FirstOrDefault();
                string identity = RuntimeProbe.CanonicalPath(hood.transform) + "/hood-v1";
                if (konigSaveBacked && (OriginalPositionField == null || OriginalEulerField == null))
                    throw new InvalidOperationException("Native Konig hood prefab baseline fields are unavailable.");
                var b = new Binding { Id = StaticId.Hash(identity), Hood = hood, Interior = interior, Exterior = exterior, Closer = closer, Host = host,
                    ReleaseHandles = interiors.Cast<MonoBehaviour>().ToArray(),
                    OpenHandles = exteriors.Where(e => !(bool)IsCloserField.GetValue(e)).Cast<MonoBehaviour>().ToArray(),
                    CloseHandles = exteriors.Where(e => (bool)IsCloserField.GetValue(e)).Cast<MonoBehaviour>().ToArray(),
                    Position = konigSaveBacked ? (Vector3)OriginalPositionField.GetValue(hood) : hood.transform.localPosition,
                    Euler = konigSaveBacked ? (Vector3)OriginalEulerField.GetValue(hood) : hood.transform.localEulerAngles,
                    DeferReplica = deferReplica == null ? null : () => deferReplica(vehicle) };
                b.Original = new NativeReplicaHingeBaseline(hood, CloseButtonField.GetValue(hood) as GameObject);
                b.State = b.Authority = NativeState(b); b.TargetAngle = b.PresentationAngle = b.State.Angle;
                b.CueBusLive = () => PresentationReplays.Events?.Live == true;
                (CloseButtonField.GetValue(hood) as GameObject)?.SetActive(b.State.Open);
                if (!host && restoredOpenKonig && b.DeferReplica?.Invoke() != true) SetReplicaPhysics(b, true);
                identities.Bind(b.Id, VehicleHoodState.Adapter, hood.gameObject, identity, true,
                    konigSaveBacked ? EntityPersistence.NativeSave : EntityPersistence.SessionOnly);
                bindings.Add(b.Id, b); lines.Add(b.Id.ToString("X16") + "\t" + identity + "\tinterior=" + (interior != null) + "\texterior=" + (exterior != null) + "\tcloser=" + (closer != null));
            }

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-hoods.tsv"), new[] { "id\thood-identity\tbuttons" }.Concat(lines.OrderBy(s => s)));
#endif

        }
        internal static bool TryGetRestoredOpenKonigHoodBody(VehicleController vehicle, out Rigidbody? hoodBody)
        {
            hoodBody = null;
            var parts = vehicle.GetComponent<Vehicle_PartsManager>();
            var hood = parts?.Get_Vehicle_Hood;
            if (hood == null || !IsRestoredOpenKonigHood(vehicle, hood, out _, out hoodBody)) return false;
            return true;
        }
        internal static bool IsBuildableCar(VehicleController vehicle) => vehicle != null &&
            (vehicle.vehicle_type == Vehicle_Type.MainVehicle || vehicle.vehicle_type == Vehicle_Type.ScrapyardKonig ||
             vehicle.vehicle_type == Vehicle_Type.Prinz);
        private static bool IsRestoredOpenKonigHood(VehicleController vehicle, Vehicle_Hood expected,
            out Vehicle_Hood? hood, out Rigidbody? hoodBody)
        {
            hood = vehicle.GetComponent<Vehicle_PartsManager>()?.Get_Vehicle_Hood;
            hoodBody = hood?.GetComponent<Rigidbody>();
            var hinge = hood?.GetComponent<HingeJoint>();
            var joints = hood == null ? Array.Empty<Joint>() : hood.GetComponents<Joint>();
            var parts = vehicle.GetComponent<Vehicle_PartsManager>();
            return IsBuildableCar(vehicle) && hood != null && hood == expected && hood.vehicleController == vehicle &&
                parts?.Door_Hood?.isInstalled == true && parts.Door_Hood.partInstalled != null && hood.Get_IsOpen &&
                hoodBody != null && hood.GetComponents<Rigidbody>().Length == 1 && joints.Length == 1 && hinge != null &&
                joints[0] == hinge && hinge.connectedBody == vehicle.vehicleRigidbody;
        }
        private static VehicleHoodState NativeState(Binding b) => VehicleHoodState.FromLocalEulerAngles(
            (bool)IsReleasedField.GetValue(b.Hood), b.Hood.Get_IsOpen, b.Euler.x, b.Hood.transform.localEulerAngles.x);
        private static void SetReplicaPhysics(Binding b, bool open)
        {
            b.Original.SuppressPhysics();
            b.ReplicaPhysicsDisabled = true;
        }
        public IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, VehicleHoodState.Adapter, b.State.Encode()));
        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.CommandRequested += Command; channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
            channel.CommandRejected += Rejected; channel.ReplyReceived += Reply;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer)
        {
            presence.Remove(peer);
            foreach (var key in hoodDecisions.Keys.Where(k => k.Peer == peer).ToArray()) hoodDecisions.Remove(key);
        }
        private Binding? Find(Vehicle_Hood hood) => bindings.Values.FirstOrDefault(b => b.Hood == hood);
        public bool Handles(Vehicle_Hood hood) => !disposed && session?.IsRunning == true && Find(hood) != null;
        public bool NativePhysics(Vehicle_Hood hood) => host || !Handles(hood);
        // action: 0=release latch, 1=open, 2=close
        public bool Trigger(Vehicle_Hood hood, byte action)
        {
            if (applying || disposed || session?.IsRunning != true) return true;
            var b = Find(hood);
            if (b == null) return true;
            if (channel?.FullySynchronized == true)
            {
                if (host) channel.Request(b.Id, new[] { action }, true);
                else if (action <= 2 && b.DeferReplica?.Invoke() != true && InstalledAndActive(b) &&
                    ActiveHandles(b, action).Any() && MonMPPlugin.Instance?.World?.SaveGate.Active != true)
                {
                    var visual = b.Preview.Presented(b.Authority);
                    bool preplayed = b.CueBusLive?.Invoke() == true &&
                        b.Hood.GetComponent<SOUND_Vehicle_Hood>() != null &&
                        (action == 0 ? !visual.Released && !visual.Open : action == 1 && !visual.Open);
                    if (!b.Preview.Enqueue(action, b.Authority, preplayed)) return false;
                    if (preplayed) PlayActionSound(b, action);
                    SendPreview(b);
                    RefreshPreview(b);
                }
            }
            return false;
        }
        [MonMP.Core.World.WorldCommandAdapters(VehicleHoodState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (host && ClientInteractionRequest.Is(payload)) { ClientCommand(peer, id, payload, false); return; }
            if (!host || !bindings.TryGetValue(id, out var b) || payload.Length != 1 || payload[0] > 2) return;
            byte action = payload[0];
            if (!InstalledAndActive(b) || MonMPPlugin.Instance?.World?.SaveGate.Active == true) return;
            Vector3 location;
            if (peer == 0) location = gameplay.PlayerWalking.transform.position;
            else
            {
                if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2) return;
                location = new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z);
            }
            if (!ActiveHandles(b, action).Any(handle => (location - handle.transform.position).sqrMagnitude <= 16)) return;
            bool released = (bool)IsReleasedField.GetValue(b.Hood);
            bool opened = b.Hood.Get_IsOpen;
            bool valid = action == 0 ? !released : action == 1 ? released : b.Hood.Get_IsOpen;
            if (!valid) return;
            applying = true;
            try
            {
                if (action == 0) b.Hood.Release();
                else if (action == 1) b.Hood.Open();
                else b.Hood.Close();
            }
            finally { applying = false; }
            Publish(b);
            if (action == 0 ? !released && b.State.Released : action == 1 && !opened && b.State.Open)
                Announce(b, action, 0);
        }
        private void Publish(Binding b)
        {
            b.State = NativeState(b);
            byte flags = (byte)((b.State.Released ? 1 : 0) | (b.State.Open ? 2 : 0));
            if (channel!.Records.TryGetValue(b.Id, out var record) && record.Adapter == VehicleHoodState.Adapter &&
                record.PayloadSpan.Length == 5 && VehicleHingePayload.Matches(record.PayloadSpan, 0, flags, b.State.Angle)) return;
            channel.Publish(b.Id, b.State.Encode());
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            var staged = records.Values.Where(r => r.Adapter == VehicleHoodState.Adapter)
                .Select(r => (Binding: bindings[r.Id], State: VehicleHoodState.Decode(r.Payload), r.Revision)).ToArray();
            foreach (var value in staged)
            {
                if (value.Revision < value.Binding.Revision) continue;
                value.Binding.Preview.Clear();
                AcceptState(value.Binding, value.State, value.Revision, true);
            }
        }
        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == VehicleHoodState.Adapter) AcceptState(bindings[record.Id], VehicleHoodState.Decode(record.Payload), record.Revision, false);
        }
        private static void Apply(Binding b, VehicleHoodState state, bool snap)
        {
            if (!b.Host && !b.Deferred.Accept(state, b.DeferReplica?.Invoke() != true)) return;
            // The host's native Release/Open/FixedUpdate play the latch, lift and slam; a live change
            // plays them here too (never the join snapshot).
            if (!snap && b.Hood != null && b.Hood.gameObject.activeInHierarchy)
            {
                var sound = b.Hood.GetComponent<SOUND_Vehicle_Hood>();
                if (sound != null)
                {
                    if (state.Open && !b.State.Open && b.CueBusLive?.Invoke() != true) sound.Open();
                    else if (!state.Open && b.State.Open) sound.Close();
                    else if (state.Released && !b.State.Released && !state.Open && b.CueBusLive?.Invoke() != true) sound.Release();
                }
            }
            snap |= VehiclePresentationSmoothing.IsLargeAngleDiscontinuity(b.PresentationAngle, state.Angle);
            if (!snap && Mathf.Abs(Mathf.DeltaAngle(b.PresentationAngle, state.Angle)) > 0.1f)
                b.SawPresentationInterpolation = true;
            b.State = state;
            b.Preview.Observe(state, b.Revision, Time.unscaledTime);
            RefreshPreview(b);
            if (snap && !b.Preview.Active) b.PresentationAngle = state.Angle;
            if (!b.Host) SetReplicaPhysics(b, state.Open);
            PresentAngle(b, b.PresentationAngle);
        }
        private static void PresentAngle(Binding b, float angle)
        {
            b.Hood.transform.localPosition = b.Position;
            var visual = b.Preview.Presented(b.Authority);
            float x = visual.Released && !visual.Open ? -2f : b.Euler.x + angle;
            b.Hood.transform.localEulerAngles = new Vector3(x, b.Euler.y, b.Euler.z);
        }
        private static bool CloseButtonMatchesState(Binding b)
        {
            var closeButton = CloseButtonField.GetValue(b.Hood) as GameObject;
            return closeButton == null || closeButton.activeSelf == b.State.Open;
        }
        public void Update()
        {
            if (disposed) return;
            if (!host)
            {
                foreach (var b in bindings.Values)
                {
                    if (b.Hood == null) continue;
                    bool refresh = b.Preview.Expire(Time.unscaledTime) || b.Preview.Active;
                    bool ready = b.DeferReplica?.Invoke() != true;
                    if (!ready) continue;
                    if (b.Deferred.TryTake(ready, out var pending)) Apply(b, pending, true);
                    SendPreview(b);
                    if (refresh || b.Preview.Active) RefreshPreview(b);
                    b.PresentationAngle = VehiclePresentationSmoothing.Angle(b.PresentationAngle, b.TargetAngle, VehiclePresentationSmoothing.Blend);
                    PresentAngle(b, b.PresentationAngle);
                }
                return;
            }
            if (channel?.Ready != true || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 0.05f;
            foreach (var b in bindings.Values)
            {
                if (b.Hood == null) throw new InvalidOperationException("Shared vehicle hood removed; reload before rehosting.");
                if (!b.Hood.gameObject.activeInHierarchy) continue;
                Publish(b);
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.CommandRequested -= Command; channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            if (channel != null) { channel.CommandRejected -= Rejected; channel.ReplyReceived -= Reply; }
            foreach (var b in bindings.Values) b.Preview.Clear();
            hoodDecisions.Clear(); presence.Clear();
            if (!host) foreach (var b in bindings.Values) b.Original.Restore();
            session = null; channel = null;
        }
        // The starting Konig's own hood, distinct from any other tracked vehicle's.

#if MONMP_DEVELOPMENT
        private Binding TestBinding => bindings.Values.Where(b => b.Interior != null && b.Exterior != null && b.Closer != null).OrderBy(b => b.Id)
            .FirstOrDefault(b => b.Hood.vehicleController.vehicle_type == Vehicle_Type.MainVehicle)
            ?? throw new InvalidOperationException("No fully-equipped Konig hood (interior+exterior+closer buttons) found among " + bindings.Count + " hoods.");


        internal VehicleHoodState TestState => TestBinding.State;


        internal bool TestSynchronized => bindings.Values.All(b => b.Hood.Get_IsOpen == b.State.Open &&
            (bool)IsReleasedField.GetValue(b.Hood) == b.State.Released &&
            CloseButtonMatchesState(b) &&
            Mathf.Abs(Mathf.DeltaAngle(b.State.Released && !b.State.Open ? -2f : b.Euler.x + b.State.Angle,
                b.Hood.transform.localEulerAngles.x)) < 0.1f &&
            (b.Host || b.Hood.GetComponent<Rigidbody>() == null || b.ReplicaPhysicsDisabled &&
                b.Hood.GetComponent<Rigidbody>()?.isKinematic == true));


        internal bool TestSawPresentationInterpolation => TestBinding.SawPresentationInterpolation;


        internal void TestResetPresentationObservation() => TestBinding.SawPresentationInterpolation = false;


        internal VehicleHoodState TestNative(bool applyingLocal = false)
        {
            return NativeState(TestBinding);
        }


        internal float TestNativeLocalEuler => TestBinding.Hood.transform.localEulerAngles.x;


        internal float TestBaselineEuler => TestBinding.Euler.x;


        internal void TestRelease() => TestBinding.Interior!.MouseDown();


        internal void TestOpen() => TestBinding.Exterior!.MouseDown();


        internal void TestClose() => TestBinding.Closer!.MouseDown();


        internal Vector3 TestReleasePosition => TestBinding.Interior!.transform.position;


        internal Vector3 TestOpenPosition => TestBinding.Exterior!.transform.position;


        internal Vector3 TestClosePosition => TestBinding.Closer!.transform.position;
#endif

    }
}
