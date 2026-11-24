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
    internal sealed partial class SharedVehicleWindows : IDisposable
    {
        private sealed class Binding
        {
            public ulong Id;
            public Vehicle_Button_WindowSwitch Button = null!;
            public Transform Window = null!;
            public Transform? Lever;
            public SOUND_Vehicle_ElectricWindow? Sound;
            public AudioSource? Motor;
            public VehicleWindowState State, Original;
            public bool HasOriginal;
            public bool Purchased;
            public readonly DeferredVehicleWindowState<VehicleWindowState> Deferred = new DeferredVehicleWindowState<VehicleWindowState>();
            public bool SawPresentationInterpolation;
            public uint Revision;
            public readonly DoorPresentationIntent Intent = new DoorPresentationIntent();
            public WindowPreview? Preview;
            public NWH.Vehicle? NativeVehicle;
            public Vehicle_PartsManager? Parts;
            public Transform? DownLocation;
            public Vector3 UpPosition;
            public Quaternion UpRotation;
            public bool Manual;
            public ulong MotorIntent;
            public bool MotorIntentDown;
        }
        private static readonly FieldInfo Target = AccessTools.Field(typeof(Vehicle_Button_WindowSwitch), "CurrentTarget");
        private static readonly FieldInfo WindowField = AccessTools.Field(typeof(Vehicle_Button_WindowSwitch), "Window");
        private static readonly FieldInfo LeverField = AccessTools.Field(typeof(Vehicle_Button_WindowSwitch), "LeverMeshToRotate");
        private static readonly FieldInfo UpRotationField = AccessTools.Field(typeof(Vehicle_Button_WindowSwitch), "UpRotation");
        private static readonly FieldInfo MotorField = AccessTools.Field(typeof(SOUND_Vehicle_ElectricWindow), "ElectricWindow");
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Vehicle_Button_WindowSwitch, Binding> byButton = new Dictionary<Vehicle_Button_WindowSwitch, Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Gameplay gameplay;
        private readonly bool host;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, disposed;
        private float nextCapture;
        internal IEnumerable<ulong> RequiredCatalogEntityIds => bindings.Values.Where(b => !b.Purchased && b.Id > WorldRecord.DynamicIdStart).Select(b => b.Id);
        public SharedVehicleWindows(Gameplay gameplay, bool host, IEnumerable<VehicleController> vehicles, NativeEntityRegistry identities)
        {
            this.gameplay = gameplay; this.host = host;
            foreach (var vehicle in vehicles.Distinct())
            {
                ulong saleId = 0;
                SharedVehicleSales.Active?.TryGetVehicleEntity(vehicle.gameObject, out _, out saleId);
                RegisterVehicle(vehicle, identities, saleId);
            }
        }
        internal IEnumerable<WorldRecord> RegisterVehicle(VehicleController vehicle, NativeEntityRegistry identities, ulong saleId = 0)
        {
            var records = new List<WorldRecord>();
            if (disposed || vehicle == null) return records;
            foreach (var button in vehicle.GetComponentsInChildren<Vehicle_Button_WindowSwitch>(true).Distinct())
            {
                if (byButton.ContainsKey(button)) continue;
                var b = new Binding { Button = button, Purchased = saleId != 0, Window = WindowField.GetValue(button) as Transform ??
                    throw new InvalidOperationException("Missing native vehicle window."), Lever = (Transform?)LeverField.GetValue(button) };
                b.Sound = button.GetComponent<SOUND_Vehicle_ElectricWindow>();
                // An inactive vehicle may not have run either native Start yet. Do not
                // capture CurrentTarget, UpRotation, or the sound field until they have.
                b.State = new VehicleWindowState(false, true, Local(b.Window), Local(b.Lever));
                string identity;
                if (saleId != 0)
                {
                    string path = "$root";
                    if (button.transform != vehicle.transform && !PurchasedVehiclePartIdentityPolicy.TryRelativePath(vehicle.transform, button.transform, out path))
                        throw new InvalidOperationException("Purchased vehicle window has no relative path.");
                    int component = Array.IndexOf(button.GetComponents<Vehicle_Button_WindowSwitch>(), button);
                    identity = "purchased-vehicle-window-v1/" + saleId.ToString("X16") + "/" + (int)vehicle.vehicle_type + "/" + path + "/component/" + component;
                }
                else identity = RuntimeProbe.CanonicalPath(button.transform) + "/window-v1";
                ulong hash = StaticId.Hash(identity);
                b.Id = saleId == 0 ? hash : WorldRecord.DynamicIdStart | Math.Max(1UL, hash & 0x7FFFFFFFFFFFFFFFUL);
                identities.Bind(b.Id, VehicleWindowState.Adapter, button.gameObject, identity, true);
                bindings.Add(b.Id, b);
                byButton.Add(button, b);
                TryPrepare(b);
                if (!host && channel?.Records.TryGetValue(b.Id, out var retained) == true && retained.Adapter == VehicleWindowState.Adapter)
                    Receive(b, VehicleWindowState.Decode(retained.Payload), false, true, retained.Revision);
                records.Add(new WorldRecord(b.Id, 1, VehicleWindowState.Adapter, b.State.Encode()));
            }
            return records;
        }
        public IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, VehicleWindowState.Adapter, b.State.Encode()));
        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += Pose; session.PeerLeft += Left;
            channel.CommandRequested += Command; channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
            channel.CommandRejected += Rejected; channel.ReplyReceived += Reply;
        }
        private void Pose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void Left(uint peer)
        {
            presence.Remove(peer);
            foreach (var key in windowDecisions.Keys.Where(k => k.Peer == peer).ToArray()) windowDecisions.Remove(key);
        }
        internal bool IsReplica(Vehicle_Button_WindowSwitch button) => !host && !disposed && byButton.ContainsKey(button);
        public bool Click(Vehicle_Button_WindowSwitch button)
        {
            if (applying || disposed) return true;
            if (!byButton.TryGetValue(button, out var b)) return false; // Unregistered vehicle controls cannot mutate a private copy.
            if (!TryPrepare(b) || !CanUseNative(b)) return false;
            if (session?.IsRunning != true) return true;
            if (channel?.FullySynchronized == true && MonMPPlugin.Instance?.World?.SaveGate.Active != true)
            {
                if (host) channel.Request(b.Id, new[] { (byte)(b.State.TargetDown ? 1 : 0) }, true);
                else
                {
                    b.Intent.Toggle(b.State.TargetDown);
                    b.Sound!.windowSwitch();
                    BeginPreview(b, b.Intent.DesiredOpen);
                    SendIntent(b);
                }
            }
            return false;
        }
        [MonMP.Core.World.WorldCommandAdapters(VehicleWindowState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (peer != 0) { ClientCommand(peer, id, payload, false); return; }
            if (!host || !bindings.TryGetValue(id, out var b)) return;
            if (payload.Length != 1 || payload[0] > 1) throw new InvalidDataException("Invalid window command.");
            if (MonMPPlugin.Instance?.World?.SaveGate.Active == true || !TryPrepare(b) || !CanUseNative(b) ||
                (payload[0] == 1) != Capture(b).TargetDown) return;
            Vector3 location;
            if (peer == 0) location = gameplay.PlayerWalking.transform.position;
            else
            {
                if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2) return;
                location = new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z);
            }
            if ((location - b.Button.transform.position).sqrMagnitude > 16) return;
            bool beforeTarget = Capture(b).TargetDown;
            applying = true;
            try { b.Button.MouseDown(); }
            finally { applying = false; }
            // Missing-door and unpowered electric controls click without changing their target.
            // Successful actions retain the motor-start cue; snapshots remain silent.
            if (Capture(b).TargetDown == beforeTarget)
                PresentationReplays.Post(PresentationKind.VehicleWindowClick, 0, b.Id, b.Button.transform.position);
            Publish(b);
        }
        internal void PlayClick(PresentationEvent cue)
        {
            if (!host && !disposed && cue.Kind == (byte)PresentationKind.VehicleWindowClick && cue.Variant == 0 &&
                bindings.TryGetValue(cue.Target, out var binding) && CanUseNative(binding) && binding.Sound != null)
                binding.Sound.windowSwitch();
        }
        private static ItemPose Local(Transform? t)
        {
            if (t == null) return new ItemPose(0, 0, 0);
            var p = t.localPosition; var q = t.localRotation;
            return new ItemPose(p.x, p.y, p.z, q.x, q.y, q.z, q.w);
        }
        private static VehicleWindowState Capture(Binding b)
        {
            float target = (float)Target.GetValue(b.Button);
            if (target != 0 && target != 1) throw new InvalidOperationException("Unsupported native window target.");
            return new VehicleWindowState(target == 1, b.Button.isClose, Local(b.Window), Local(b.Lever), b.Motor!.isPlaying);
        }
        private static bool ButtonStarted(Binding b)
        {
            if (b.Button == null || UpRotationField == null) return false;
            var rotation = (Quaternion)UpRotationField.GetValue(b.Button);
            float norm = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
            // Unity's Transform.localRotation is normalized. The uninitialized native
            // Quaternion is all-zero until Vehicle_Button_WindowSwitch.Start captures it.
            return DeferredVehicleWindowState<VehicleWindowState>.HasCapturedButtonPose(norm);
        }
        private static bool SoundStarted(Binding b)
        {
            if (b.Button == null) return false;
            if (b.Sound == null) b.Sound = b.Button.GetComponent<SOUND_Vehicle_ElectricWindow>();
            if (b.Sound == null || MotorField == null) return false;
            b.Motor = MotorField.GetValue(b.Sound) as AudioSource;
            return b.Motor != null;
        }
        private static bool NativeStarted(Binding b) => b.HasOriginal
            ? b.Button != null && b.Sound != null && b.Motor != null : ButtonStarted(b) && SoundStarted(b);
        private static bool CanUseNative(Binding b) => b.Button != null && DeferredVehicleWindowState<VehicleWindowState>.CanUseNative(
            b.HasOriginal || ButtonStarted(b), b.HasOriginal ? b.Sound != null && b.Motor != null : SoundStarted(b),
            b.Button.enabled, b.Button.gameObject.activeInHierarchy);
        private static bool TryPrepare(Binding b)
        {
            if (!NativeStarted(b)) return false;
            if (!b.HasOriginal)
            {
                b.NativeVehicle = VehicleField.GetValue(b.Button) as NWH.Vehicle;
                b.Parts = PartsField.GetValue(b.Button) as Vehicle_PartsManager;
                b.DownLocation = DownField.GetValue(b.Button) as Transform;
                b.UpPosition = (Vector3)UpPositionField.GetValue(b.Button);
                b.UpRotation = (Quaternion)UpRotationField.GetValue(b.Button);
                b.Manual = (bool)ManualField.GetValue(b.Button);
                b.Original = Capture(b);
                b.HasOriginal = true;
                if (!b.Deferred.HasPending) b.State = b.Original;
            }
            return true;
        }
        private static void Set(Transform? t, ItemPose p)
        {
            if (t == null) return;
            t.localPosition = new Vector3(p.X, p.Y, p.Z); t.localRotation = new Quaternion(p.Qx, p.Qy, p.Qz, p.Qw);
        }
        private static void Apply(Binding b, VehicleWindowState state, bool effects = false, bool snap = true)
        {
            if (!NativeStarted(b)) return;
            snap |= VehiclePresentationSmoothing.IsLargeTransformDiscontinuity(b.Window, state.Window) ||
                VehiclePresentationSmoothing.IsLargeTransformDiscontinuity(b.Lever, state.Lever);
            bool starting = state.MotorPlaying && !b.Motor!.isPlaying;
            if (b.Preview == null)
            {
                if (starting)
                {
                    if (effects && !(b.MotorIntent != 0 && b.MotorIntentDown == state.TargetDown)) b.Sound!.windowSwitch();
                    b.Motor!.Play();
                }
                else if (!state.MotorPlaying && b.Motor!.isPlaying) b.Motor!.Pause();
            }
            if (state.MotorPlaying && b.MotorIntentDown == state.TargetDown) b.MotorIntent = 0;
            b.State = state;
            Target.SetValue(b.Button, state.TargetDown ? 1f : 0f); b.Button.isClose = state.Closed;
            if (snap && b.Preview == null) { Set(b.Window, state.Window); Set(b.Lever, state.Lever); }
            else if (!Matches(b.Window, state.Window) || !Matches(b.Lever, state.Lever)) b.SawPresentationInterpolation = true;
        }
        private static bool Matches(Transform? t, ItemPose p)
        {
            if (t == null) return true;
            return Vector3.Distance(t.localPosition, new Vector3(p.X, p.Y, p.Z)) < VehiclePresentationSmoothing.PositionSettleDistance &&
                Quaternion.Angle(t.localRotation, new Quaternion(p.Qx, p.Qy, p.Qz, p.Qw)) < VehiclePresentationSmoothing.RotationSettleDegrees;
        }
        private static void Present(Transform? t, ItemPose p, float blend)
        {
            if (t == null) return;
            var position = t.localPosition; var rotation = t.localRotation;
            var nextPosition = VehiclePresentationSmoothing.Position(position, new Vector3(p.X, p.Y, p.Z), blend);
            var nextRotation = VehiclePresentationSmoothing.Rotation(rotation, new Quaternion(p.Qx, p.Qy, p.Qz, p.Qw), blend);
            if (position != nextPosition) t.localPosition = nextPosition;
            if (rotation != nextRotation) t.localRotation = nextRotation;
        }
        private static bool SamePose(ItemPose a, ItemPose b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z &&
            a.Qx == b.Qx && a.Qy == b.Qy && a.Qz == b.Qz && a.Qw == b.Qw;
        private void Publish(Binding b)
        {
            var state = Capture(b);
            if (state.TargetDown == b.State.TargetDown && state.Closed == b.State.Closed && state.MotorPlaying == b.State.MotorPlaying &&
                SamePose(state.Window, b.State.Window) && SamePose(state.Lever, b.State.Lever)) return;
            var payload = state.Encode();
            b.State = state; channel!.Publish(b.Id, payload);
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            var staged = records.Values.Where(r => r.Adapter == VehicleWindowState.Adapter && bindings.ContainsKey(r.Id))
                .Select(r => (Binding: bindings[r.Id], State: VehicleWindowState.Decode(r.Payload), r.Revision)).ToArray();
            foreach (var entry in staged)
            {
                if (entry.Revision < entry.Binding.Revision) continue;
                entry.Binding.Intent.Clear(); entry.Binding.MotorIntent = 0; CancelPreview(entry.Binding);
                Receive(entry.Binding, entry.State, false, true, entry.Revision);
            }
        }
        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == VehicleWindowState.Adapter && bindings.TryGetValue(record.Id, out var binding))
                Receive(binding, VehicleWindowState.Decode(record.Payload), true, false, record.Revision);
        }
        private void Receive(Binding binding, VehicleWindowState state, bool effects, bool snap, uint revision = 0)
        {
            if (revision != 0 && revision < binding.Revision) return;
            if (revision != 0) binding.Revision = revision;
            bool prepared = TryPrepare(binding);
            binding.State = state;
            if (binding.Intent.Token == 0 && binding.MotorIntent != 0 && state.TargetDown != binding.MotorIntentDown) binding.MotorIntent = 0;
            if (binding.Preview?.Confirmed == true && state.TargetDown != binding.Preview.Down) CancelPreview(binding);
            if (!prepared || !CanUseNative(binding))
            {
                binding.Deferred.Stage(state, effects, snap);
                return;
            }
            binding.Deferred.Clear();
            Apply(binding, state, effects, snap);
        }
        public void Update()
        {
            if (disposed) return;
            if (!host)
            {
                float blend = VehiclePresentationSmoothing.Blend;
                foreach (var b in bindings.Values)
                {
                    if (b.Button == null || b.Window == null || !TryPrepare(b))
                    { b.Intent.Clear(); b.MotorIntent = 0; CancelPreview(b); continue; }
                    if (b.Deferred.HasPending && b.Deferred.TryTake(true, b.Motor != null, b.Button.enabled, b.Button.gameObject.activeInHierarchy,
                        out var pending, out var effects, out var snap))
                        Apply(b, pending, effects, snap);
                    bool available = CanUseNative(b) && channel?.FullySynchronized == true;
                    bool saving = MonMPPlugin.Instance?.World?.SaveGate.Active == true;
                    if (!available || saving)
                    { b.Intent.Clear(); b.MotorIntent = 0; CancelPreview(b, resumeAuthority: available && saving); continue; }
                    if (b.Intent.Expire(Time.unscaledTime)) { b.MotorIntent = 0; CancelPreview(b, resumeAuthority: true); }
                    if (UpdatePreview(b)) continue;
                    Present(b.Window, b.State.Window, blend); Present(b.Lever, b.State.Lever, blend);
                }
                return;
            }
            if (channel?.Ready != true || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 0.05f;
            foreach (var b in bindings.Values)
            {
                if (b.Button == null || b.Window == null) throw new InvalidOperationException("Shared vehicle window removed.");
                if (!TryPrepare(b)) continue;
                Publish(b);
            }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (session != null) { session.PoseReceived -= Pose; session.PeerLeft -= Left; }
            if (channel != null) { channel.CommandRequested -= Command; channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            if (channel != null) { channel.CommandRejected -= Rejected; channel.ReplyReceived -= Reply; }
            foreach (var b in bindings.Values) { b.Intent.Clear(); b.MotorIntent = 0; CancelPreview(b); }
            if (!host) foreach (var b in bindings.Values)
                if (b.HasOriginal && b.Button != null && b.Window != null) { b.Deferred.Clear(); Apply(b, b.Original); }
            windowDecisions.Clear(); presence.Clear(); channel = null; session = null;
        }

#if MONMP_DEVELOPMENT
        private static Vehicle_Type TestVehicleType(Binding b) => b.Button != null ?
            b.Button.GetComponentInParent<VehicleController>()?.vehicle_type ?? Vehicle_Type.Useless : Vehicle_Type.Useless;


        private static int TestVehiclePriority(Binding b) => TestVehicleType(b) switch
        {
            Vehicle_Type.SmollATV => 0,
            Vehicle_Type.Buggy => 1,
            Vehicle_Type.OlTruck => 2,
            Vehicle_Type.MainVehicle => 3,
            _ => 4
        };
#endif

        // Prefer a window on a tracked non-car vehicle when the paired fixture is run. The previous
        // hash-only choice did not tell us whether an ATV/Buggy control was covered at all.

#if MONMP_DEVELOPMENT
        private ulong testSelectedEntity;
        private Binding TestBinding => testSelectedEntity != 0 && bindings.TryGetValue(testSelectedEntity, out var selected) ? selected :
            bindings.Values.OrderBy(b => CanUseNative(b) ? 0 : 1).ThenBy(TestVehiclePriority).ThenBy(b => b.Id).First();

        internal ulong TestSelectCanonicalWindow(VehicleController vehicle, ulong expectedEntity = 0)
        {
            var selected = bindings.Values.Where(b => !b.Purchased && b.Button != null &&
                b.Button.GetComponentInParent<VehicleController>() == vehicle && TryPrepare(b) && CanUseNative(b) &&
                (expectedEntity == 0 || b.Id == expectedEntity)).OrderBy(b => b.Id).FirstOrDefault();
            if (selected == null) throw new InvalidOperationException("No ready persistent canonical window matches the exact fixture vehicle/entity.");
            testSelectedEntity = selected.Id;
            return testSelectedEntity;
        }

        internal void TestClearSelection() => testSelectedEntity = 0;


        internal int TestCount => bindings.Count;
        internal bool TestDisposed => disposed;


        internal Vehicle_Type TestBindingVehicleType => TestVehicleType(TestBinding);


        internal string TestVehicleTypeCoverage => string.Join(",", bindings.Values.Where(b => b.Button != null)
            .GroupBy(TestVehicleType).OrderBy(g => g.Key).Select(g => g.Key + ":" + g.Count()));


        internal ulong TestEntity => TestBinding.Id;


        internal Vector3 TestPosition => TestBinding.Button.transform.position;


        internal VehicleWindowState TestState => TestBinding.State;


        internal bool TestSawPresentationInterpolation => TestBinding.SawPresentationInterpolation;


        internal bool TestPresentationPending => !Matches(TestBinding.Window, TestBinding.State.Window) || !Matches(TestBinding.Lever, TestBinding.State.Lever);


        internal void TestResetPresentationObservation() => TestBinding.SawPresentationInterpolation = false;


        internal void TestClick() { if (CanUseNative(TestBinding)) TestBinding.Button.MouseDown(); }


        internal void TestPower() { if (CanUseNative(TestBinding)) TestBinding.Button.GetComponentInParent<NWH.Vehicle>().Get_IgnitionPosition = 1; }


        internal void TestDrift() => TestBinding.Window.localPosition += Vector3.up;


        private static bool TestMatchesNative(Binding b, VehicleWindowState expected)
        {
            var current = Capture(b);
            return current.TargetDown == expected.TargetDown && current.Closed == expected.Closed && current.MotorPlaying == expected.MotorPlaying &&
                Matches(b.Window, expected.Window) && Matches(b.Lever, expected.Lever);
        }
        internal bool TestSynchronized => TryPrepare(TestBinding) && TestMatchesNative(TestBinding, TestBinding.State);


        internal bool TestRestored => bindings.Values.All(b => !b.HasOriginal || b.Button == null || b.Window == null ||
            TestMatchesNative(b, b.Original));

        internal string TestRestorationDiagnostic => string.Join(" | ", bindings.Values.OrderBy(b => b.Id).Select(TestRestorationDetail));
        private static string TestRestorationDetail(Binding b)
        {
            string identity = b.Id.ToString("X16") + ":purchased=" + b.Purchased;
            if (!b.HasOriginal) return identity + ":uncaptured";
            if (b.Button == null || b.Window == null) return identity + ":destroyed";
            var current = Capture(b);
            float Distance(ItemPose a, ItemPose original) => Vector3.Distance(new Vector3(a.X, a.Y, a.Z), new Vector3(original.X, original.Y, original.Z));
            float Rotation(ItemPose a, ItemPose original) => Quaternion.Angle(new Quaternion(a.Qx, a.Qy, a.Qz, a.Qw), new Quaternion(original.Qx, original.Qy, original.Qz, original.Qw));
            return identity + ":matches=" + TestMatchesNative(b, b.Original) + ":exact=" + current.Encode().SequenceEqual(b.Original.Encode()) +
                ":target=" + current.TargetDown + "/" + b.Original.TargetDown + ":closed=" + current.Closed + "/" + b.Original.Closed +
                ":motor=" + current.MotorPlaying + "/" + b.Original.MotorPlaying + ":windowPositionDelta=" + Distance(current.Window, b.Original.Window).ToString("R") +
                ":windowRotationDelta=" + Rotation(current.Window, b.Original.Window).ToString("R") + ":leverPositionDelta=" + Distance(current.Lever, b.Original.Lever).ToString("R") +
                ":leverRotationDelta=" + Rotation(current.Lever, b.Original.Lever).ToString("R");
        }
        internal VehicleWindowState TestOriginalState => TestBinding.Original;


        internal bool TestMotorPlaying => TestBinding.Motor != null && TestBinding.Motor.isPlaying;


        internal VehicleWindowState TestNativeState => TryPrepare(TestBinding) ? Capture(TestBinding) : TestBinding.State;
#endif

    }
}
