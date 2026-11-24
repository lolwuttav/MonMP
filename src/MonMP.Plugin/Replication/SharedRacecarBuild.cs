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
    // The host consumes Speedway racecar packs and owns the native three-pack build state.
    // SharedItems carries the loose packs; this static record carries installation and activation.
    internal sealed class SharedRacecarBuild : IDisposable
    {
        internal static SharedRacecarBuild? Current { get; private set; }
        private static readonly FieldInfo PacksField = AccessTools.Field(typeof(RacecarBuild), "PackInstalled");
        private static readonly FieldInfo VehicleField = AccessTools.Field(typeof(RacecarBuild), "vc");
        private static readonly FieldInfo EnableField = AccessTools.Field(typeof(RacecarBuild), "ToEnableWhenCompleted");
        private static readonly FieldInfo DisableField = AccessTools.Field(typeof(RacecarBuild), "ToDisableWhenCompleted");
        private static readonly FieldInfo PackEnableField = AccessTools.Field(typeof(RacecarBuild.RaceCar_PacksDetails), "ToEnableOnceInstalled");
        private static readonly FieldInfo PackDisableField = AccessTools.Field(typeof(RacecarBuild.RaceCar_PacksDetails), "ToDisableOnceInstalled");

        private readonly bool host;
        private readonly RacecarBuild? native;
        private readonly VehicleController? vehicle;
        private readonly Rigidbody? body;
        private readonly Dictionary<GameObject, bool> originalObjects = new Dictionary<GameObject, bool>();
        private readonly bool originalVehicleEnabled, originalKinematic, originalRootActive;
        private readonly CollisionDetectionMode originalCollision;
        private readonly RacecarBuildState original;
        private RacecarBuildState state;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applied, disposed;
        private float nextCapture;

        private bool Ready => !disposed && session?.IsRunning == true && channel?.Ready == true;

#if MONMP_DEVELOPMENT
        internal RacecarBuild? TestNative => native;
#endif


        internal RacecarBuildState State => state;


#if MONMP_DEVELOPMENT
        internal RacecarBuildState TestLocal => Capture();


        internal bool TestVehicleActive => vehicle != null && body != null && vehicle.gameObject.activeInHierarchy &&
            vehicle.enabled && vehicle.powertrain?.Initialized == true && !body.isKinematic;
#endif


        internal VehicleController? CurrentVehicle => vehicle;


        internal bool Synchronized => host || applied;


        public SharedRacecarBuild(bool host, string diagnostics)
        {
            this.host = host;
            native = Object.FindObjectsOfType<RacecarBuild>(true).Where(build => build.gameObject.scene.IsValid()).SingleOrDefault();
            string line = "none";
            if (native != null)
            {
                if (PacksField == null || VehicleField == null || EnableField == null || DisableField == null ||
                    PackEnableField == null || PackDisableField == null || native.PacksDetails == null || native.PacksDetails.Length != 3)
                    throw new InvalidOperationException("Unsupported native RacecarBuild layout.");
                vehicle = VehicleField.GetValue(native) as VehicleController;
                body = vehicle?.vehicleRigidbody;
                if (vehicle == null || body == null) throw new InvalidOperationException("Racecar build is missing its vehicle.");
                original = state = Capture();
                originalVehicleEnabled = vehicle.enabled;
                originalKinematic = body.isKinematic;
                originalCollision = body.collisionDetectionMode;
                originalRootActive = native.gameObject.activeSelf;
                if (!host)
                {
                    foreach (var go in VisualObjects(native)) originalObjects[go] = go.activeSelf;
                    FreezeToUnbuilt();
                    state = Capture();
                }
                string identity = RuntimeProbe.CanonicalPath(native.transform) + "/racecar-build-v1";
                ulong id = RacecarBuildState.Entity;
                line = id.ToString("X16") + "\t" + identity + "\tinstalled=" + state.Installed;
            }
            else
            {
                original = state = default;
            }

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-racecar-build.tsv"), new[] { "id\tidentity\tstate", line });
#endif

            Current = this;
        }

        private static IEnumerable<GameObject> VisualObjects(RacecarBuild build)
        {
            foreach (var part in build.PacksDetails)
            {
                foreach (var go in (GameObject[]?)PackEnableField.GetValue(part) ?? Array.Empty<GameObject>()) if (go != null) yield return go;
                foreach (var go in (GameObject[]?)PackDisableField.GetValue(part) ?? Array.Empty<GameObject>()) if (go != null) yield return go;
            }
            foreach (var go in (GameObject[]?)EnableField.GetValue(build) ?? Array.Empty<GameObject>()) if (go != null) yield return go;
            foreach (var go in (GameObject[]?)DisableField.GetValue(build) ?? Array.Empty<GameObject>()) if (go != null) yield return go;
        }

        private RacecarBuildState Capture()
        {
            if (native == null) return default;
            var bits = PacksField.GetValue(native) as bool[];
            if (bits == null || bits.Length != 3) throw new InvalidOperationException("Native racecar pack array changed.");
            byte mask = 0;
            for (int i = 0; i < 3; i++) if (bits[i]) mask |= (byte)(1 << i);
            return new RacecarBuildState(mask);
        }

        public IEnumerable<WorldRecord> InitialRecords()
        {
            if (native != null) yield return new WorldRecord(RacecarBuildState.Entity, 1, RacecarBuildState.Adapter, state.Encode());
        }

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
        }

        internal bool AllowNativeTrigger(RacecarBuild build, Collider other)
        {
            if (other == null || other.GetComponent<Event_RacecarPack>() == null) return true;
            if (session?.IsRunning != true) return true;
            if (native != build || native == null) return false;
            int pack = other.GetComponent<Event_RacecarPack>().IntValue1;
            bool saveBlocked = MonMPPlugin.Instance?.World?.SaveGate.Active == true;
            return RacecarBuildState.AllowNativePackMutation(true, host, saveBlocked, Capture(), pack);
        }

        internal void NativeTriggerCompleted(RacecarBuild build, Collider other)
        {
            if (!host || native != build || other == null || other.GetComponent<Event_RacecarPack>() == null || !Ready) return;
            var next = Capture();
            if (!next.Equals(state)) Publish(next);
        }

        internal bool AllowNativeRewardClaim => RacecarBuildState.AllowNativeRewardClaim(session?.IsRunning == true, host);

        private void Publish(RacecarBuildState next)
        {
            state = next;
            channel!.Publish(RacecarBuildState.Entity, next.Encode());
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (!host && native != null && records.TryGetValue(RacecarBuildState.Entity, out var record)) Apply(record, false);
        }
        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == RacecarBuildState.Adapter) Apply(record, true);
        }
        private void Apply(WorldRecord record, bool live)
        {
            if (record.Id != RacecarBuildState.Entity || record.Adapter != RacecarBuildState.Adapter)
                throw new InvalidDataException("Unknown racecar build record.");
            var next = RacecarBuildState.Decode(record.Payload);
            if (applied && next.Equals(state)) return;
            if (live && applied && native != null)
                PresentPacks(native.transform.position, state.Installed, next.Installed, !state.Complete && next.Complete, "EVENT_SuccessRacecarCompleted");
            if (!next.Complete && (vehicle!.enabled || !body!.isKinematic || !native!.gameObject.activeSelf)) FreezeToUnbuilt();
            native!.LoadFromSaveSystem(Enumerable.Range(0, 3).Select(next.Has).ToArray());
            state = next; applied = true;
        }

        // Live build feedback shared with SharedBuggyBuild (#17).
        internal static void PresentPacks(Vector3 at, int before, int after, bool completed, string completedText)
        {
            try
            {
                var sound = Singleton<SOUND_Manager>.i;
                if ((after & ~before) != 0) using (SpatialSounds.At(at)) sound.PlaySound_PartInstalled();
                if (!completed) return;
                // The host's native completion grants the build achievement; guests present earn it too.
                GuestAchievements.Award(completedText == "EVENT_SuccessBuggyCompleted" ? AchievementType.Buggy_Completed : AchievementType.Racecar_Completed);
                sound.PlaySound_BuggyBuildCompleted();
                Singleton<UI_EventMessage>.i.Show(TEXT.GetText(completedText), "Santa Goat", 15f, UI_EventMessage.EventIconType.Heart);
            }
            catch (System.Exception ex) { Debug.LogWarning("MonMP build feedback failed: " + ex.Message); }
        }

        private void FreezeToUnbuilt()
        {
            native!.CancelInvoke("BuildRacecarNextFrame"); native.CancelInvoke("ActivateRacecar");
            native.gameObject.SetActive(true);
            body!.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.isKinematic = true;
            vehicle!.enabled = false;
            vehicle.SetInputToPlayer(false);
            native.LoadFromSaveSystem(new bool[3]);
        }

        public void Update()
        {
            if (!Ready || native == null || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 0.1f;
            if (!host) { ReconcileVehicleActivation(); return; }
            var next = Capture();
            if (!next.Equals(state)) Publish(next);
        }

        private void ReconcileVehicleActivation()
        {
            if (!applied || !state.Complete || vehicle == null || !vehicle.gameObject.activeInHierarchy || vehicle.enabled) return;
            // A completed snapshot can arrive while an Entreposage garage still hides the car
            // root. Native Invoke callbacks cannot complete under that inactive parent. Replay
            // the recorded packs after SharedVehicles activates the exact existing chassis.
            native!.CancelInvoke("BuildRacecarNextFrame"); native.CancelInvoke("ActivateRacecar");
            native.gameObject.SetActive(true);
            native.LoadFromSaveSystem(Enumerable.Range(0, 3).Select(state.Has).ToArray());
        }

        public void Dispose()
        {
            if (disposed) return;
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            if (!host && native != null)
            {
                native.CancelInvoke("BuildRacecarNextFrame"); native.CancelInvoke("ActivateRacecar");
                PacksField.SetValue(native, Enumerable.Range(0, 3).Select(original.Has).ToArray());
                foreach (var pair in originalObjects) if (pair.Key != null) pair.Key.SetActive(pair.Value);
                if (body != null) { body.isKinematic = originalKinematic; body.collisionDetectionMode = originalCollision; }
                if (vehicle != null) { vehicle.enabled = originalVehicleEnabled; vehicle.SetInputToPlayer(originalVehicleEnabled); }
                native.gameObject.SetActive(originalRootActive);
            }
            disposed = true; channel = null; session = null;
            if (Current == this) Current = null;
        }

#if MONMP_DEVELOPMENT
        internal VehicleController? TestVehicle => CurrentVehicle;
        internal RacecarBuildState TestState => State;
        internal bool TestSynchronized => Synchronized;
#endif
    }
}
