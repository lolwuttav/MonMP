using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed class SharedRoadSigns : IDisposable
    {
        private sealed class Binding
        {
            internal ulong Id;
            internal BendOnCollision Native = null!;
            internal Collider Collider = null!;
            internal RoadSignState Original;
            internal Quaternion OriginalRotation;
        }
        private static readonly FieldInfo AlreadyBent = AccessTools.Field(typeof(BendOnCollision), "alreadyBend");
        private static readonly FieldInfo OriginalEuler = AccessTools.Field(typeof(BendOnCollision), "OriginalLocalEuler");
        private static readonly FieldInfo Solidity = AccessTools.Field(typeof(BendOnCollision), "Solidity");
        private static readonly FieldInfo XAxisOnly = AccessTools.Field(typeof(BendOnCollision), "XAxisOnly");
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<BendOnCollision, Binding> byNative = new Dictionary<BendOnCollision, Binding>();
        private readonly bool host;
        private readonly Gameplay gameplay;
        private readonly SharedVehicles vehicles;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool disposed;

        internal SharedRoadSigns(bool host, Gameplay gameplay, SharedVehicles vehicles, NativeEntityRegistry registry)
        {
            this.host = host; this.gameplay = gameplay; this.vehicles = vehicles;
            foreach (var native in UnityEngine.Object.FindObjectsOfType<BendOnCollision>(true))
            {
                if (!native.gameObject.scene.IsValid() || native.GetComponent<Collider>() is not Collider collider) continue;
                string path = SharedBuildingDoors.StablePath(native.transform) + "/road-sign-v1";
                var binding = new Binding { Id = StaticId.Hash(path), Native = native, Collider = collider,
                    OriginalRotation = native.transform.localRotation };
                binding.Original = Capture(binding); bindings.Add(binding.Id, binding); byNative.Add(native, binding);
                registry.Bind(binding.Id, RoadSignState.Adapter, native.gameObject, path, true, EntityPersistence.SessionOnly);
            }
        }
        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(binding =>
            new WorldRecord(binding.Id, 1, RoadSignState.Adapter, binding.Original.Encode()));
        internal ulong EntityOf(BendOnCollision sign) => sign != null && byNative.TryGetValue(sign, out var binding) ? binding.Id : 0;
        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected; channel = world;
            world.SnapshotApplied += Snapshot; world.Changed += Changed; world.CommandRequested += Command;
        }
        private bool Live => !disposed && session?.IsRunning == true && channel?.FullySynchronized == true;
        private bool Blocked => !Live || MonMPPlugin.Instance?.World?.SaveGate.Active == true ||
            SharedPortableConsoleCharger.Active?.ConsoleSaveInProgress == true;

        // Native Gameplay calls this only for its local chassis. Keep the real
        // native host mutation; guest contacts wait for the host's shared state.
        internal bool BeforeCollision(BendOnCollision sign, Collision collision)
        {
            if (disposed || session?.IsRunning != true || !byNative.TryGetValue(sign, out var binding)) return true;
            if (Blocked || collision == null || binding.Collider == null || !binding.Collider.enabled ||
                !sign.gameObject.activeInHierarchy || (bool)AlreadyBent.GetValue(sign) ||
                gameplay.Get_PlayerState != PlayerState.InVehicle_Driver ||
                gameplay.Get_CurrentVehicleController is not VehicleController vehicle ||
                !vehicles.TryGetDriverState(vehicle, out var state) || state.Driver.Owner != session.LocalPeerId) return false;
            float speed = collision.relativeVelocity.magnitude;
            if (float.IsNaN(speed) || float.IsInfinity(speed) || speed <= 1f || collision.collider != binding.Collider) return false;
            if (host) return true;
            if (collision.contactCount == 0) return false;
            var point = collision.GetContact(0).point;
            var request = new RoadSignImpactCommand(vehicles.EntityOf(vehicle), state.Driver.Generation,
                Mathf.Min(speed, 12f), point.x, point.y, point.z);
            channel!.Request(binding.Id, request.Encode()); return false;
        }
        internal void AfterCollision(BendOnCollision sign, bool ranOriginal)
        {
            if (ranOriginal && host && Live && byNative.TryGetValue(sign, out var binding)) Publish(binding);
        }

        [WorldCommandAdapters(RoadSignState.Adapter)]
        private void Command(uint peer, ulong id, byte[] data)
        {
            if (!host || Blocked || peer == 0 || !bindings.TryGetValue(id, out var binding) ||
                binding.Native == null || binding.Collider == null || !binding.Collider.enabled ||
                !binding.Native.gameObject.activeInHierarchy || (bool)AlreadyBent.GetValue(binding.Native)) return;
            RoadSignImpactCommand request;
            try { request = RoadSignImpactCommand.Decode(data); }
            catch (InvalidDataException) { return; }
            if (!vehicles.TryAuthorizePoliceRadar(peer, request.Vehicle, request.Generation,
                    out var vehicle, out _, out _, out _, out float age) || vehicle == null ||
                !vehicles.TryGetDriverState(vehicle, out var sample)) return;
            var point = new Vector3(request.X, request.Y, request.Z);
            var position = new Vector3(sample.Pose.X, sample.Pose.Y, sample.Pose.Z);
            var velocity = new Vector3(sample.Velocity.X, sample.Velocity.Y, sample.Velocity.Z);
            if (!RoadSignImpactPolicy.Accepts(request.Speed, velocity.magnitude, age,
                (binding.Collider.ClosestPoint(point) - point).sqrMagnitude, (position - point).sqrMagnitude)) return;
            // This is the native BendOnCollision.VehicleCollision calculation.
            // The host's local player may be walking or driving another vehicle;
            // use this driver's accepted chassis orientation instead of Gameplay's.
            var rotation = new Quaternion(sample.Pose.Qx, sample.Pose.Qy, sample.Pose.Qz, sample.Pose.Qw);
            var euler = binding.Native.transform.InverseTransformDirection(rotation * Vector3.right) *
                request.Speed * (float)Solidity.GetValue(binding.Native) * 5f;
            var original = (Vector3)OriginalEuler.GetValue(binding.Native);
            euler.y = original.y;
            AlreadyBent.SetValue(binding.Native, true);
            binding.Native.transform.localEulerAngles = euler;
            binding.Collider.enabled = false;
            if ((bool)XAxisOnly.GetValue(binding.Native))
                binding.Native.transform.localEulerAngles = new Vector3(binding.Native.transform.localEulerAngles.x, original.y, original.z);
            Publish(binding);
        }
        private static RoadSignState Capture(Binding binding)
        {
            var euler = binding.Native.transform.localEulerAngles;
            // Unity can return tiny negative Euler components from a valid
            // quaternion (observed native yaw: -2.04905664E-05). Canonicalize
            // native readings; the wire codec remains strict about its range.
            return new RoadSignState((bool)AlreadyBent.GetValue(binding.Native), binding.Collider.enabled,
                NativeAngle(euler.x), NativeAngle(euler.y), NativeAngle(euler.z));
        }
        private static float NativeAngle(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("Invalid native road sign rotation.");
            return Mathf.Repeat(value, 360f);
        }
        private void Publish(Binding binding) => channel?.Publish(binding.Id, Capture(binding).Encode());
        private static void Apply(Binding binding, RoadSignState state)
        {
            if (binding.Native == null) return;
            AlreadyBent.SetValue(binding.Native, state.Bent);
            binding.Native.transform.localEulerAngles = new Vector3(state.X, state.Y, state.Z);
            if (binding.Collider != null) binding.Collider.enabled = state.ColliderEnabled;
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        { if (!host) foreach (var record in records.Values) Changed(record); }
        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == RoadSignState.Adapter && bindings.TryGetValue(record.Id, out var binding))
                Apply(binding, RoadSignState.Decode(record.Payload));
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (channel != null)
            { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            // Native signs have no save state. A session must not leak its bend
            // or disabled collision into the next world on either peer.
            foreach (var binding in bindings.Values)
            {
                Apply(binding, binding.Original);
                if (binding.Native != null) binding.Native.transform.localRotation = binding.OriginalRotation;
            }
            session = null; channel = null; bindings.Clear(); byNative.Clear();
        }
    }
}
