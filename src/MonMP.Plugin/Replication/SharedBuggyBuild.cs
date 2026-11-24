using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using NWH.VehiclePhysics2;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // The host's six native pack-trigger results are authoritative. SharedItems carries the loose
    // packs and removes a consumed pack; this record carries the built frame and vehicle activation.
    internal sealed class SharedBuggyBuild : IDisposable
    {
        internal static SharedBuggyBuild? Current { get; private set; }
        private static readonly FieldInfo PacksField = AccessTools.Field(typeof(BuggyBuild), "PackInstalled");
        private static readonly FieldInfo VehicleField = AccessTools.Field(typeof(BuggyBuild), "vc");
        private static readonly FieldInfo SeatField = AccessTools.Field(typeof(BuggyBuild), "Seat");
        private static readonly FieldInfo EnableField = AccessTools.Field(typeof(BuggyBuild), "ToEnableWhenCompleted");
        private static readonly FieldInfo DisableField = AccessTools.Field(typeof(BuggyBuild), "ToDisableWhenCompleted");
        private static readonly FieldInfo PackEnableField = AccessTools.Field(typeof(BuggyBuild.BuggyBuild_PacksDetails), "ToEnableOnceInstalled");
        private static readonly FieldInfo PackDisableField = AccessTools.Field(typeof(BuggyBuild.BuggyBuild_PacksDetails), "ToDisableOnceInstalled");

        private readonly BuggyBuild? native;
        private readonly VehicleController? vehicle;
        private readonly Rigidbody? body;
        private readonly bool host;
        private readonly ulong id;
        private readonly Dictionary<GameObject, bool> originalObjects = new Dictionary<GameObject, bool>();
        private readonly Dictionary<Collider, bool> originalSeats = new Dictionary<Collider, bool>();
        private readonly bool originalVehicleEnabled, originalKinematic, originalRootActive;
        private readonly CollisionDetectionMode originalCollision;
        private NetworkSession? session;
        private WorldChannel? channel;
        private BuggyBuildState original, state;
        private bool applied, disposed;
        private float nextCapture;

        internal int Count => native != null ? 1 : 0;

#if MONMP_DEVELOPMENT
        internal BuggyBuild? TestNative => native;
#endif


        internal VehicleController? CurrentVehicle => vehicle;


        internal BuggyBuildState State => state;


#if MONMP_DEVELOPMENT
        internal BuggyBuildState TestLocal => Capture();


        internal BuggyBuildState TestOriginal => original;
#endif


        internal bool Synchronized => host || applied;


        internal bool VehicleActive => vehicle != null && body != null && vehicle.enabled && !body.isKinematic;


#if MONMP_DEVELOPMENT
        internal bool TestRestored => !host && native != null && Capture().Equals(original) && native.gameObject.activeSelf == originalRootActive &&
            (vehicle == null || vehicle.enabled == originalVehicleEnabled) && (body == null ||
            (body.isKinematic == originalKinematic && body.collisionDetectionMode == originalCollision)) &&
            originalObjects.All(p => p.Key == null || p.Key.activeSelf == p.Value) &&
            originalSeats.All(p => p.Key == null || p.Key.enabled == p.Value);
#endif

        private bool Ready => !disposed && native != null && session?.IsRunning == true && channel?.Ready == true;

        public SharedBuggyBuild(bool host, string diagnostics, NativeEntityRegistry identities)
        {
            this.host = host;
            native = Object.FindObjectsOfType<BuggyBuild>(true).FirstOrDefault(b => b.gameObject.scene.IsValid());
            string line = "none";
            if (native != null)
            {
                if (PacksField == null || VehicleField == null || SeatField == null || EnableField == null || DisableField == null ||
                    PackEnableField == null || PackDisableField == null || native.PacksDetails == null || native.PacksDetails.Length != 6)
                    throw new InvalidOperationException("Unsupported native BuggyBuild layout.");
                vehicle = VehicleField.GetValue(native) as VehicleController;
                body = vehicle?.vehicleRigidbody;
                if (vehicle == null || body == null || SeatField.GetValue(native) is not GameObject seat)
                    throw new InvalidOperationException("Buggy build is missing its vehicle or seat.");
                original = state = Capture();
                originalVehicleEnabled = vehicle.enabled;
                originalKinematic = body.isKinematic;
                originalCollision = body.collisionDetectionMode;
                originalRootActive = native.gameObject.activeSelf;
                if (!host)
                {
                    foreach (var collider in seat.GetComponents<Collider>()) originalSeats[collider] = collider.enabled;
                    foreach (var go in VisualObjects(native)) originalObjects[go] = go.activeSelf;
                }
                string identity = RuntimeProbe.CanonicalPath(native.transform) + "/buggy-build-v1";
                id = StaticId.Hash(identity);
                identities.Bind(id, BuggyBuildState.Adapter, native.gameObject, identity, true, EntityPersistence.SessionOnly);
                line = id.ToString("X16") + "\t" + identity + "\tinstalled=" + state.Installed;
            }

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-buggy-build.tsv"), new[] { "id\tidentity\tstate", line });
#endif

            Current = this;
        }

        private static IEnumerable<GameObject> VisualObjects(BuggyBuild build)
        {
            foreach (var part in build.PacksDetails)
            {
                foreach (var go in (GameObject[])PackEnableField.GetValue(part)) if (go != null) yield return go;
                foreach (var go in (GameObject[])PackDisableField.GetValue(part)) if (go != null) yield return go;
            }
            foreach (var go in (GameObject[])EnableField.GetValue(build)) if (go != null) yield return go;
            foreach (var go in (GameObject[])DisableField.GetValue(build)) if (go != null) yield return go;
        }

        private BuggyBuildState Capture()
        {
            if (native == null) return default;
            var bits = PacksField.GetValue(native) as bool[];
            if (bits == null || bits.Length != 6) throw new InvalidOperationException("Native buggy pack array changed.");
            byte mask = 0;
            for (int i = 0; i < 6; i++) if (bits[i]) mask |= (byte)(1 << i);
            return new BuggyBuildState(mask);
        }

        public IEnumerable<WorldRecord> InitialRecords()
        {
            if (native != null) yield return new WorldRecord(id, 1, BuggyBuildState.Adapter, state.Encode());
        }

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
        }

        // True lets the native trigger run. A guest's local collider never installs or consumes its
        // replica; its host-side shared twin reaches the host's native trigger instead.
        internal bool AllowNativeTrigger(BuggyBuild build, Collider other)
        {
            if (native != build || disposed || session?.IsRunning != true || other == null) return true;
            var pack = other.GetComponent<Event_BuggyPack>();
            if (pack == null) return true;
            int kind = pack.IntValue1;
            if (kind < 0 || kind >= 6) return false;
            if (!host) return false;
            if (MonMPPlugin.Instance?.World?.SaveGate.Active == true) return false;
            return !Capture().Has(kind);
        }

        internal void NativeTriggerCompleted(BuggyBuild build, Collider other)
        {
            if (!host || native != build || other == null || other.GetComponent<Event_BuggyPack>() == null || !Ready) return;
            var next = Capture();
            if (!next.Equals(state)) Publish(next);
        }

        private void Publish(BuggyBuildState next)
        {
            state = next;
            channel!.Publish(id, next.Encode());
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (!host && native != null && records.TryGetValue(id, out var record)) Apply(record, false);
        }
        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == BuggyBuildState.Adapter) Apply(record, true);
        }
        private void Apply(WorldRecord record, bool live)
        {
            if (record.Id != id || record.Adapter != BuggyBuildState.Adapter) throw new InvalidDataException("Unknown buggy build record.");
            var next = BuggyBuildState.Decode(record.Payload);
            // A pack another player installed while this guest played: the native install click and,
            // for the last one, the completion jingle and message LoadFromSaveSystem skips (#17).
            if (live && applied && native != null)
                SharedRacecarBuild.PresentPacks(native.transform.position, state.Installed, next.Installed, !state.Complete && next.Complete, "EVENT_SuccessBuggyCompleted");
            state = next;
            if (!applied || !Capture().Equals(next))
            {
                applied = true;
                // A built guest save can join an unbuilt host. LoadFromSaveSystem updates the pack
                // meshes, but native BuggyBuild has no reverse of its vehicle-enable callback.
                if (!next.Complete && vehicle != null && body != null && (vehicle.enabled || !body.isKinematic))
                    ResetToUnbuilt();
                native!.LoadFromSaveSystem(Enumerable.Range(0, 6).Select(next.Has).ToArray());
            }
        }

        private void ResetToUnbuilt()
        {
            native!.CancelInvoke("BuildBuggyNextFrame"); native.CancelInvoke("ActivateBuggy");
            body!.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.isKinematic = true;
            vehicle!.enabled = false;
            vehicle.SetInputToPlayer(false);
            if (SeatField.GetValue(native) is GameObject seat)
                foreach (var collider in seat.GetComponents<Collider>()) collider.enabled = false;
            foreach (var go in (GameObject[])EnableField.GetValue(native)) if (go != null) go.SetActive(false);
            foreach (var go in (GameObject[])DisableField.GetValue(native)) if (go != null) go.SetActive(true);
            native.gameObject.SetActive(true);
        }

        public void Update()
        {
            if (!Ready || !host || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 0.1f;
            var next = Capture();
            if (!next.Equals(state)) Publish(next);
        }

        public void Dispose()
        {
            if (disposed) return;
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            if (!host && applied && native != null)
            {
                native.CancelInvoke("BuildBuggyNextFrame"); native.CancelInvoke("ActivateBuggy");
                PacksField.SetValue(native, Enumerable.Range(0, 6).Select(original.Has).ToArray());
                foreach (var pair in originalObjects) if (pair.Key != null) pair.Key.SetActive(pair.Value);
                foreach (var pair in originalSeats) if (pair.Key != null) pair.Key.enabled = pair.Value;
                if (body != null) { body.isKinematic = originalKinematic; body.collisionDetectionMode = originalCollision; }
                if (vehicle != null) vehicle.enabled = originalVehicleEnabled;
                native.gameObject.SetActive(originalRootActive);
            }
            disposed = true; channel = null; session = null;
            if (Current == this) Current = null;
        }

#if MONMP_DEVELOPMENT
        internal VehicleController? TestVehicle => CurrentVehicle;
        internal BuggyBuildState TestState => State;
        internal bool TestSynchronized => Synchronized;
        internal bool TestVehicleActive => VehicleActive;
#endif
    }
}
