using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GleyTrafficSystem;
using GleyUrbanAssets;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Patches;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Gley's pool indices and prefabs are stable for the supported scene. The
    // host owns activation and AI; guests keep the native pool for presentation
    // and receive only the host's slot state and transforms.
    internal sealed class SharedTraffic : IDisposable
    {
        private sealed class Slot
        {
            public readonly struct LocalTransform
            {
                public readonly Transform Transform;
                public readonly Vector3 Position, Scale;
                public readonly Quaternion Rotation;
                public readonly bool ActiveSelf;

                public LocalTransform(Transform transform)
                {
                    Transform = transform; Position = transform.localPosition; Rotation = transform.localRotation;
                    Scale = transform.localScale; ActiveSelf = transform.gameObject.activeSelf;
                }
            }

            public readonly ulong Id, PrefabFingerprint;
            public readonly VehicleComponent Native;
            public readonly Rigidbody Body;
#if MONMP_DEVELOPMENT
            public bool InitialKinematic, InitialGravity;
            public RigidbodyInterpolation InitialInterpolation;
            public Vector3 InitialVelocity, InitialAngularVelocity;
#endif
            public LocalTransform[] InitialTransforms = Array.Empty<LocalTransform>();
            public readonly VehicleLightsComponent? Lights;
            public readonly Material? Paint;
            public TrafficSlotState State = null!;
            public readonly CapturePublication Publication = new CapturePublication();

#if MONMP_DEVELOPMENT
            public TrafficSlotState FixtureBaseline = null!;
#endif

            public TrafficSlotState? Target;
            public Vector3 FromPosition, TargetPosition;
            public Quaternion FromRotation, TargetRotation;
            public float ReceivedAt;
            public bool HasTarget;
            // Guest wheel presentation (Gley's wheel job only runs with its simulation).
            public float WheelSpin, WheelSteer, LastYaw;
            public bool HasLastYaw;

            public Slot(int index, VehicleComponent native)
            {
                Native = native; Body = native.rb != null ? native.rb : throw new InvalidOperationException("Traffic pool slot has no rigidbody.");

#if MONMP_DEVELOPMENT
                CaptureFixtureBaseline(null);
#endif

                Id = TrafficSlotState.Entity(index); PrefabFingerprint = StaticId.Hash("gley-traffic-prefab/v1/" + native.gameObject.name);
                Lights = native.GetComponent<VehicleLightsComponent>();
                var materials = native.meshRenderer != null ? native.meshRenderer.materials : Array.Empty<Material>();
                Paint = native.matIndex >= 0 && native.matIndex < materials.Length ? materials[native.matIndex] : null;
            }


#if MONMP_DEVELOPMENT
            public void CaptureFixtureBaseline(TrafficSlotState? state)
            {
                if (state != null) FixtureBaseline = state;
                InitialKinematic = Body.isKinematic; InitialGravity = Body.useGravity;
                InitialInterpolation = Body.interpolation;
                InitialVelocity = Body.velocity; InitialAngularVelocity = Body.angularVelocity;
                InitialTransforms = Native.GetComponentsInChildren<Transform>(true).Select(transform => new LocalTransform(transform)).ToArray();
            }
#endif

        }

        // Kept separate from the mutable host fixture baseline. Guest presentation
        // never changes native density/idle/waypoint/AI bookkeeping.
        private sealed class OfflineSlot
        {
            internal readonly Slot Slot;
            internal readonly TrafficSlotState State;
            internal readonly Slot.LocalTransform[] Transforms;
            internal readonly bool Kinematic, Gravity, Sleeping;
            internal readonly RigidbodyInterpolation Interpolation;
            internal readonly Vector3 Position, Velocity, AngularVelocity;
            internal readonly Quaternion Rotation;
            internal readonly List<(object Owner, System.Reflection.FieldInfo Field, object Value)> Fields =
                new List<(object, System.Reflection.FieldInfo, object)>();
            internal readonly AIEvents.VehicleChangedState Handler;
            internal readonly int HandlerCount;
            internal readonly AudioSource? Audio;
            internal readonly bool Playing, Loop, Mute;
            internal readonly float Volume, Pitch;
            internal readonly int AudioSample;
            internal bool Restored;
            internal bool CanRestore => Slot.Native != null && Slot.Body != null && Transforms.All(t => t.Transform != null) &&
                Fields.All(f => !(f.Owner is UnityEngine.Object obj) || obj != null);

            internal OfflineSlot(Slot slot, TrafficSlotState state)
            {
                Slot = slot; State = state;
                Transforms = slot.Native.GetComponentsInChildren<Transform>(true).Select(t => new Slot.LocalTransform(t)).ToArray();
                Kinematic = slot.Body.isKinematic; Gravity = slot.Body.useGravity; Sleeping = slot.Body.IsSleeping();
                Interpolation = slot.Body.interpolation;
                Position = slot.Body.position; Rotation = slot.Body.rotation;
                Velocity = slot.Body.velocity; AngularVelocity = slot.Body.angularVelocity;
                CaptureField(slot.Native, "maxSpeed"); CaptureField(slot.Native, "isInTrigger"); CaptureField(slot.Native, "obstacleList");
                CaptureField(slot.Native, "lightsOn");
                if (slot.Native.visibilityScript != null)
                { CaptureField(slot.Native.visibilityScript, "readyToRemove"); CaptureField(slot.Native.visibilityScript, "neverBeenVisible"); }
                if (slot.Lights != null)
                    foreach (string name in new[] { "currentTime", "updateLights", "leftBlink", "rightBlink" }) CaptureField(slot.Lights, name);
                Handler = (AIEvents.VehicleChangedState)Delegate.CreateDelegate(typeof(AIEvents.VehicleChangedState), slot.Native,
                    AccessTools.Method(typeof(VehicleComponent), "AVehicleChengedState", new[] { typeof(int), typeof(Collider), typeof(SpecialDriveActionTypes) }));
                HandlerCount = AIEvents.onVehicleChangedState?.GetInvocationList().Count(d => d.Equals(Handler)) ?? 0;
                Audio = engineSoundField(slot.Native)?.audioSource;
                if (Audio != null)
                { Playing = Audio.isPlaying; Loop = Audio.loop; Mute = Audio.mute; Volume = Audio.volume; Pitch = Audio.pitch; AudioSample = Audio.clip != null ? Audio.timeSamples : 0; }
            }
            private void CaptureField(object owner, string name)
            {
                var field = AccessTools.Field(owner.GetType(), name);
                Fields.Add((owner, field, field.GetValue(owner)));
            }
            internal void Restore()
            {
                var slot = Slot;
                if (slot.Native.gameObject.activeSelf) slot.Native.DeactivateVehicle();
                // Restore the original handler count only; never activate through
                // Gley's randomizing spawn path or alter another slot's handlers.
                while (AIEvents.onVehicleChangedState?.GetInvocationList().Any(d => d.Equals(Handler)) == true)
                    AIEvents.onVehicleChangedState -= Handler;
                foreach (var t in Transforms)
                {
                    t.Transform.localPosition = t.Position; t.Transform.localRotation = t.Rotation; t.Transform.localScale = t.Scale;
                    t.Transform.gameObject.SetActive(t.ActiveSelf);
                }
                slot.Body.position = Position; slot.Body.rotation = Rotation;
                slot.Body.isKinematic = Kinematic; slot.Body.useGravity = Gravity;
                slot.Body.interpolation = Interpolation;
                slot.Body.velocity = Velocity; slot.Body.angularVelocity = AngularVelocity;
                foreach (var field in Fields) field.Field.SetValue(field.Owner, field.Value);
                ApplyColor(slot, State); ApplyLights(slot, State.Lights);
                if (Audio != null)
                {
                    Audio.loop = Loop; Audio.mute = Mute; Audio.volume = Volume; Audio.pitch = Pitch;
                    if (Playing) { Audio.Play(); if (Audio.clip != null) Audio.timeSamples = AudioSample; } else Audio.Stop();
                }
                for (int i = 0; i < HandlerCount; i++) AIEvents.onVehicleChangedState += Handler;
                if (Sleeping || !State.Active) slot.Body.Sleep(); else slot.Body.WakeUp();
                Restored = Matches();
                if (!Restored) throw new InvalidOperationException("Guest traffic offline slot restoration mismatch: " + slot.Native.GetIndex());
            }
            internal bool Matches()
            {
                var slot = Slot;
                var color = slot.Paint != null ? slot.Paint.color : Color.white;
                return color.r == State.ColorR && color.g == State.ColorG && color.b == State.ColorB && color.a == State.ColorA &&
                    slot.Native.gameObject.activeSelf == State.Active && slot.Body.position == Position && slot.Body.rotation == Rotation &&
                    slot.Body.isKinematic == Kinematic && slot.Body.useGravity == Gravity && slot.Body.velocity == Velocity && slot.Body.angularVelocity == AngularVelocity &&
                    slot.Body.interpolation == Interpolation &&
                    slot.Body.IsSleeping() == (Sleeping || !State.Active) &&
                    Transforms.All(t => t.Transform.localPosition == t.Position && t.Transform.localRotation == t.Rotation && t.Transform.localScale == t.Scale && t.Transform.gameObject.activeSelf == t.ActiveSelf) &&
                    Fields.All(f => Equals(f.Field.GetValue(f.Owner), f.Value)) &&
                    (AIEvents.onVehicleChangedState?.GetInvocationList().Count(d => d.Equals(Handler)) ?? 0) == HandlerCount &&
                    (Audio == null || (Audio.isPlaying == Playing && Audio.loop == Loop && Audio.mute == Mute && Audio.volume == Volume && Audio.pitch == Pitch));
            }
        }

        private sealed class RemoteCamera
        {
            public readonly Transform Transform;
            public float SeenAt;
            // A walking remote player as Gley sees the local one: a solid collider on its player
            // layer. It stays disabled (no physics); the host hands it to the cars' own trigger
            // handlers when the player is inside a car's front trigger, so the car brakes.
            public readonly CapsuleCollider Body;
            public bool Walking;
            public readonly HashSet<Slot> Inside = new HashSet<Slot>();
            public RemoteCamera(uint peer)
            {
                var root = new GameObject("MonMP Traffic Camera " + peer);
                Transform = root.transform;
                var body = new GameObject("MonMP Traffic Pedestrian");
                body.transform.SetParent(Transform, false);
                body.layer = PedestrianLayer;
                Body = body.AddComponent<CapsuleCollider>();
                Body.height = 1.8f; Body.radius = 0.3f; Body.center = new Vector3(0, 0.9f, 0);
                Body.enabled = false;
            }
        }

        private static int pedestrianLayer = -1;
        private static int PedestrianLayer
        {
            get
            {
                if (pedestrianLayer >= 0) return pedestrianLayer;
                int mask = Resources.Load<LayerSetup>("LayerSetupData")?.playerLayers ?? 0;
                for (int layer = 0; layer < 32 && pedestrianLayer < 0; layer++) if ((mask & (1 << layer)) != 0) pedestrianLayer = layer;
                if (pedestrianLayer < 0) pedestrianLayer = Mathf.Max(0, LayerMask.NameToLayer("Player"));
                return pedestrianLayer;
            }
        }
        // Where a guest stands to be in front of an active car (its replica's front trigger).

#if MONMP_DEVELOPMENT
        internal Vector3? TestFrontTriggerFeet
        {
            get
            {
                foreach (var slot in slots)
                {
                    if (slot.Native == null || !slot.Native.gameObject.activeInHierarchy || slot.Native.frontTrigger == null || slot.Native.frontTrigger.childCount == 0) continue;
                    var box = slot.Native.frontTrigger.GetChild(0).GetComponent<BoxCollider>();
                    if (box != null) return box.transform.TransformPoint(box.center) - Vector3.up * 0.4f;
                }
                return null;
            }
        }
#endif

        // A spot on the road ahead of an active car, and the car's front box shape, for a guest to stand in its path.

#if MONMP_DEVELOPMENT
        internal Vector3? TestRoadAhead(float distance, out string geometry)
        {
            geometry = "";
            foreach (var slot in slots)
            {
                if (slot.Native == null || !slot.Native.gameObject.activeInHierarchy) continue;
                var box = FrontBox(slot);
                if (box == null) continue;
                var car = slot.Native.transform;
                var forward = Vector3.ProjectOnPlane(car.forward, Vector3.up).normalized;
                var boxCenter = box.transform.TransformPoint(box.center);
                var ahead = boxCenter + forward * distance;
                if (!Physics.Raycast(ahead + Vector3.up * 4f, Vector3.down, out var ground, 10f, ~0, QueryTriggerInteraction.Ignore)) continue;
                var size = Vector3.Scale(box.size, box.transform.lossyScale);
                float boxAboveGround = Physics.Raycast(boxCenter, Vector3.down, out var under, 5f, ~0, QueryTriggerInteraction.Ignore) ? under.distance : -1f;
                geometry = "box size " + size.ToString("F2") + ", center " + boxAboveGround.ToString("F2") + " m above ground, " +
                    Vector3.Distance(Vector3.ProjectOnPlane(boxCenter - car.position, Vector3.up), Vector3.zero).ToString("F2") + " m ahead of the car origin";
                return ground.point;
            }
            return null;
        }
#endif

        // The nearest active car to a remote walker: its gap to them (bounds), speed and whether it sees them.

#if MONMP_DEVELOPMENT
        internal (float Gap, float Speed, bool Sees)? TestNearestCarToWalker
        {
            get
            {
                var camera = remoteCameras.Values.FirstOrDefault();
                if (camera == null) return null;
                var body = camera.Transform.position + Vector3.up * 0.9f;
                (float, float, bool)? best = null;
                foreach (var slot in slots)
                {
                    if (slot.Native == null || !slot.Native.gameObject.activeInHierarchy) continue;
                    float gap = float.MaxValue;
                    foreach (var collider in slot.Native.GetComponentsInChildren<Collider>())
                        if (!collider.isTrigger && collider.enabled) gap = Mathf.Min(gap, Vector3.Distance(collider.bounds.ClosestPoint(body), body));
                    if (best == null || gap < best.Value.Item1) best = (gap, slot.Body.velocity.magnitude, camera.Inside.Contains(slot));
                }
                return best;
            }
        }


        internal int TestPedestrianStops => remoteCameras.Values.Sum(camera => camera.Inside.Count);
#endif

        // The car a remote pedestrian stands in front of: its speed and Gley's current drive action.

#if MONMP_DEVELOPMENT
        internal string? TestPedestrianCar
        {
            get
            {
                var slot = remoteCameras.Values.SelectMany(camera => camera.Inside).FirstOrDefault(s => s.Native != null && s.Native.gameObject.activeInHierarchy);
                return slot == null ? null : slot.Body.velocity.magnitude.ToString("F2") + " " + slot.Native.GetCurrentAction();
            }
        }


        internal float TestPedestrianCarSpeed => remoteCameras.Values.SelectMany(camera => camera.Inside)
            .Where(s => s.Native != null && s.Native.gameObject.activeInHierarchy).Select(s => s.Body.velocity.magnitude).DefaultIfEmpty(-1f).Max();
#endif


        private static readonly System.Reflection.FieldInfo initialized = AccessTools.Field(typeof(TrafficManager), "initialized")
            ?? throw new InvalidOperationException("Unsupported Gley traffic manager layout: initialized field is missing.");
        private static readonly System.Reflection.FieldInfo densityManager = AccessTools.Field(typeof(TrafficManager), "densityManager")
            ?? throw new InvalidOperationException("Unsupported Gley traffic manager layout: density manager field is missing.");
        private static readonly System.Reflection.FieldInfo maxNrOfVehicles = AccessTools.Field(typeof(DensityManager), "maxNrOfVehicles")
            ?? throw new InvalidOperationException("Unsupported Gley density manager layout: max vehicle field is missing.");
        private static readonly System.Reflection.FieldInfo currentNrOfVehicles = AccessTools.Field(typeof(DensityManager), "currentnrOfVehicles")
            ?? throw new InvalidOperationException("Unsupported Gley density manager layout: current vehicle field is missing.");
        private static readonly System.Reflection.FieldInfo newVehicleRequested = AccessTools.Field(typeof(DensityManager), "newVehicleRequested")
            ?? throw new InvalidOperationException("Unsupported Gley density manager layout: pending vehicle field is missing.");
        private static readonly System.Reflection.FieldInfo trafficVehicles = AccessTools.Field(typeof(TrafficManager), "trafficVehicles")
            ?? throw new InvalidOperationException("Unsupported Gley traffic manager layout: traffic vehicles field is missing.");
        private static readonly System.Reflection.FieldInfo waypointManager = AccessTools.Field(typeof(TrafficManager), "waypointManager")
            ?? throw new InvalidOperationException("Unsupported Gley traffic manager layout: waypoint manager field is missing.");
        private static readonly System.Reflection.FieldInfo gridManager = AccessTools.Field(typeof(TrafficManager), "gridManager")
            ?? throw new InvalidOperationException("Unsupported Gley traffic manager layout: grid manager field is missing.");
        private static readonly System.Reflection.FieldInfo cameraPositions = AccessTools.Field(typeof(TrafficManager), "activeCameraPositions")
            ?? throw new InvalidOperationException("Unsupported Gley traffic camera position field.");
        internal static Type CameraCacheArrayType => cameraPositions.FieldType;
        private static readonly System.Reflection.FieldInfo gridCameraPositions = AccessTools.Field(typeof(GridManager), "activeCameraPositions")
            ?? throw new InvalidOperationException("Unsupported Gley grid camera position field.");
        private static readonly System.Reflection.FieldInfo activeSquaresLevel = AccessTools.Field(typeof(TrafficManager), "activeSquaresLevel")
            ?? throw new InvalidOperationException("Unsupported Gley active grid level field.");
        private static Type CameraPositionType => CameraCacheArrayType.GetGenericArguments()[0];
        internal static void VerifyCameraCacheLayout()
        {
            Type array = CameraCacheArrayType;
            if (!array.IsGenericType || array.GetGenericTypeDefinition().FullName != "Unity.Collections.NativeArray`1" ||
                array.GetGenericArguments().Length != 1 || CameraPositionType.FullName != "Unity.Mathematics.float3" ||
                gridCameraPositions.FieldType != array || activeSquaresLevel.FieldType != typeof(int) ||
                cameraPositions.IsStatic || gridCameraPositions.IsStatic || activeSquaresLevel.IsStatic)
                throw new InvalidOperationException("Unsupported Gley native camera cache types.");
            var handler = AccessTools.Method(typeof(VehicleComponent), "AVehicleChengedState", new[] { typeof(int), typeof(Collider), typeof(SpecialDriveActionTypes) });
            var changedState = AccessTools.Field(typeof(AIEvents), "onVehicleChangedState");
            if (handler == null || handler.IsStatic || handler.ReturnType != typeof(void) ||
                changedState?.IsStatic != true || changedState.FieldType != typeof(AIEvents.VehicleChangedState))
                throw new InvalidOperationException("Unsupported native traffic subscription callback.");
            var item = array.GetProperty("Item");
            var length = array.GetProperty("Length");
            var isCreated = array.GetProperty("IsCreated");
            var dispose = array.GetMethod("Dispose", Type.EmptyTypes);
            var update = AccessTools.Method(typeof(GridManager), "UpdateGrid", new[] { typeof(int), array });
            if (item?.PropertyType != CameraPositionType || item.GetIndexParameters().Length != 1 ||
                item.GetIndexParameters()[0].ParameterType != typeof(int) || item.SetMethod == null || item.SetMethod.IsStatic ||
                item.GetMethod == null || item.GetMethod.IsStatic || length?.PropertyType != typeof(int) ||
                length.GetMethod == null || length.GetMethod.IsStatic ||
                isCreated?.PropertyType != typeof(bool) || isCreated.GetMethod == null || isCreated.GetMethod.IsStatic ||
                dispose == null || dispose.IsStatic || dispose.ReturnType != typeof(void) ||
                CameraPositionType.GetConstructor(new[] { typeof(float), typeof(float), typeof(float) }) == null ||
                update == null || update.IsStatic || update.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported Gley native camera cache accessors.");
            foreach (string axis in new[] { "x", "y", "z" })
                if (CameraPositionType.GetField(axis)?.FieldType != typeof(float))
                    throw new InvalidOperationException("Unsupported native float3 axis: " + axis);
        }
        private static readonly System.Reflection.FieldInfo gridSceneData = AccessTools.Field(typeof(GridManager), "currentSceneData")
            ?? throw new InvalidOperationException("Unsupported Gley grid manager layout: scene data field is missing.");
        private static readonly System.Reflection.FieldInfo idleVehicles = AccessTools.Field(typeof(TrafficVehicles), "idleVehicles")
            ?? throw new InvalidOperationException("Unsupported Gley traffic pool layout: idle vehicles field is missing.");
        private static readonly System.Reflection.FieldInfo colliderWidth = AccessTools.Field(typeof(VehicleComponent), "colliderWidth")
            ?? throw new InvalidOperationException("Unsupported Gley vehicle layout: collider width field is missing.");
        private static readonly System.Reflection.MethodInfo closestWaypoint = AccessTools.Method(typeof(DensityManager), "GetClosestWayoint", new[] { typeof(Vector3), typeof(VehicleTypes) })
            ?? throw new InvalidOperationException("Unsupported Gley density manager layout: closest waypoint method is missing.");
        private static readonly System.Reflection.MethodInfo waypointPosition = AccessTools.Method(typeof(WaypointManagerBase), "GetWaypointPosition", new[] { typeof(int) })
            ?? throw new InvalidOperationException("Unsupported Gley waypoint manager layout: waypoint position method is missing.");
        private static readonly System.Reflection.MethodInfo waypointDisabled = AccessTools.Method(typeof(WaypointManagerBase), "IsDisabled", new[] { typeof(int) })
            ?? throw new InvalidOperationException("Unsupported Gley waypoint manager layout: disabled check is missing.");
        private static readonly System.Reflection.MethodInfo waypointFree = AccessTools.Method(typeof(DensityManager), "IsWaypointFree", new[] { typeof(int), typeof(float), typeof(float), typeof(float), typeof(float) })
            ?? throw new InvalidOperationException("Unsupported Gley density manager layout: waypoint validation method is missing.");
        private const float PublishInterval = 0.1f;
        private const float InterpolationInterval = 0.1f;
        private const float CameraFreshness = 3f;
        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly CurrentSceneData? cameraSceneData;
        private readonly TrafficManager cameraManager;
        private int displacedCameraArrayReleases;
        private readonly List<OfflineSlot> offlineSlots = new List<OfflineSlot>();
        private readonly UnityEngine.SceneManagement.Scene originalScene;
        private int neverInitializedReplicaSlots;
        private bool offlineRestored;
        private int originalNativeCount;
        private VehicleComponent[] originalIdleSlots = Array.Empty<VehicleComponent>();
        private object?[] originalActionLists = Array.Empty<object?>();
        private readonly List<Slot> slots = new List<Slot>();
        private readonly Dictionary<uint, RemoteCamera> remoteCameras = new Dictionary<uint, RemoteCamera>();
        private readonly HashSet<uint> synchronizedPeers = new HashSet<uint>();
        private Transform[] configuredCameras = Array.Empty<Transform>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private float nextPublish, nextCameraScan;
        private bool receivedBaseline, disposed;
        private int hostPoseUpdates, hostActivationUpdates, receivedPoseUpdates;

#if MONMP_DEVELOPMENT
        private bool guestInterpolated;
#endif

        private string diagnostics;


#if MONMP_DEVELOPMENT
        internal int TestSlotCount => slots.Count;


        internal int TestActiveSlots => slots.Count(slot => (host ? slot.State.Active : slot.Target?.Active == true));


        internal int TestNativeActiveSlots => slots.Count(slot => slot.Native.gameObject.activeSelf);


        internal int TestNativeActiveSlotIndex => slots.FindIndex(slot => slot.Native.gameObject.activeSelf);


        internal int TestTrafficCurrentCount
        {
            get
            {
                var manager = UnityEngine.Object.FindObjectOfType<TrafficManager>();
                var density = manager != null ? densityManager.GetValue(manager) : null;
                if (density == null || currentNrOfVehicles.GetValue(density) is not int count)
                    throw new InvalidOperationException("Native Gley active vehicle count is unavailable for the isolated fixture.");
                return count;
            }
        }


        internal bool TestTrafficAddPending
        {
            get
            {
                var manager = UnityEngine.Object.FindObjectOfType<TrafficManager>();
                var density = manager != null ? densityManager.GetValue(manager) : null;
                if (density == null || newVehicleRequested.GetValue(density) is not bool pending)
                    throw new InvalidOperationException("Native Gley add-vehicle state is unavailable for the isolated fixture.");
                return pending;
            }
        }


        internal int TestSkippedGuestUpdates => TrafficAuthorityPatches.SkippedUpdates + TrafficAuthorityPatches.SkippedFixedUpdates;


        internal bool TestSuppressedGuestTriggerExit() => !host && slots.Count > 0 &&
            TrafficAuthorityPatches.TestGuestTriggerExitSuppression(slots[0].Native);


        internal int TestHostPoseUpdates => hostPoseUpdates;


        internal int TestHostActivationUpdates => hostActivationUpdates;


        internal int TestReceivedPoseUpdates => receivedPoseUpdates;
#endif

        private static readonly System.Reflection.FieldInfo nativeDrivingAI = AccessTools.Field(typeof(TrafficManager), "drivingAI");
        private static readonly System.Reflection.FieldInfo nativeDriveActions = AccessTools.Field(typeof(DrivingAI), "driveActions");
        private object? NativeActions(int index)
        {
            var ai = nativeDrivingAI.GetValue(cameraManager);
            var actions = (DriveActions[])nativeDriveActions.GetValue(ai);
            return actions[index].activeActions;
        }

#if MONMP_DEVELOPMENT
        internal int TestNeverInitializedReplicaSlots => neverInitializedReplicaSlots;
#endif

        private bool OfflineBookkeepingMatches() =>
            (int)currentNrOfVehicles.GetValue(densityManager.GetValue(cameraManager)) == originalNativeCount &&
            ((IEnumerable<VehicleComponent>)idleVehicles.GetValue(trafficVehicles.GetValue(cameraManager))).SequenceEqual(originalIdleSlots) &&
            slots.Select(slot => NativeActions(slot.Native.GetIndex())).Zip(originalActionLists, (current, original) => ReferenceEquals(current, original)).All(match => match);

#if MONMP_DEVELOPMENT
        internal bool TestOfflineRestored => offlineRestored && offlineSlots.All(s => s.Restored);


        internal bool TestNoUninitializedResumedSlot => offlineSlots.All(s =>
            (!s.Slot.Native.gameObject.activeSelf && s.Slot.Body.IsSleeping()) || NativeActions(s.Slot.Native.GetIndex()) != null);


        internal string TestStageNeverInitializedReplica()
        {
            if (host || !TrafficAuthorityPatches.IsGuestSuppressed) throw new InvalidOperationException("Guest replica staging requires native suppression.");
            var source = slots.FirstOrDefault(s => s.Target?.Active == true);
            var baseline = offlineSlots.FirstOrDefault(s => !s.State.Active && originalActionLists[s.Slot.Native.GetIndex()] == null && !s.Slot.Native.gameObject.activeSelf && NativeActions(s.Slot.Native.GetIndex()) == null);
            if (source == null || baseline == null) throw new InvalidOperationException("Traffic fixture needs a real never-initialized inactive native slot.");
            var t = source.Target!; var slot = baseline.Slot;
            var state = new TrafficSlotState(slot.Native.GetIndex(), slot.PrefabFingerprint, true, t.Pose,
                t.VelocityX, t.VelocityY, t.VelocityZ, t.Lights, t.ColorR, t.ColorG, t.ColorB, t.ColorA);
            Apply(new WorldRecord(slot.Id, 1, TrafficSlotState.Adapter, state.Encode()));
            if (!slot.Native.gameObject.activeSelf || !slot.Body.isKinematic || NativeActions(slot.Native.GetIndex()) != null)
                throw new InvalidOperationException("Controlled replica activation did not preserve the uninitialized native AI case.");
            return "locally prepared adapter record; native-index=" + slot.Native.GetIndex() + "; original-active=false; activeActions=null; replica-active=true";
        }


        internal bool TestGuestInterpolated => guestInterpolated;


        internal bool TestGuestSimulationSuppressed => !host && TrafficAuthorityPatches.IsGuestSuppressed;


        internal int TestHostCameraCount => configuredCameras.Length;


        internal bool TestBaselineApplied => host
            ? channel != null && slots.All(slot => channel.Records.TryGetValue(slot.Id, out var record) && record.Adapter == TrafficSlotState.Adapter)
            : receivedBaseline;


        internal bool TestAllPeersSynchronized => session?.IsHost == true && synchronizedPeers.Count == session.Peers.Count - 1;


        internal bool TestGuestReplicaMatches => !host && receivedBaseline && slots.All(slot =>
            slot.HasTarget && slot.Native.gameObject.activeSelf == slot.Target!.Active &&
            slot.Target.PrefabFingerprint == slot.PrefabFingerprint &&
            (!slot.Target.Active || slot.Body.isKinematic));


        internal bool TestHostHasRecentTraffic => host && slots.Any(slot => slot.State.Active);
#endif


        public SharedTraffic(Gameplay gameplay, bool host, string diagnostics)
        {
            this.gameplay = gameplay; this.host = host; this.diagnostics = diagnostics;
            var manager = UnityEngine.Object.FindObjectOfType<TrafficManager>();
            if (manager == null || initialized.GetValue(manager) is not bool ready || !ready)
                throw new InvalidOperationException("The native Gley traffic pool has not initialized.");
            VerifyCameraCacheLayout();
            cameraManager = manager;
            originalScene = gameplay.gameObject.scene;
            var nativeGrid = gridManager.GetValue(manager);
            cameraSceneData = nativeGrid != null ? gridSceneData.GetValue(nativeGrid) as CurrentSceneData : null;
            var native = Manager.GetVehicleList();
            if (native == null || native.Count == 0 || native.Count > TrafficSlotState.MaxSlots)
                throw new InvalidOperationException("The native Gley traffic pool has an unsupported slot count.");
            for (int i = 0; i < native.Count; i++)
            {
                if (native[i] == null || native[i].GetIndex() != i)
                    throw new InvalidOperationException("The native Gley traffic pool has an unstable slot order.");
                var slot = new Slot(i, native[i]);
                slot.State = Capture(slot);
#if MONMP_DEVELOPMENT
                slot.FixtureBaseline = slot.State;
#endif
                if (!host) offlineSlots.Add(new OfflineSlot(slot, slot.State));
                slots.Add(slot);
            }
            if (!host)
            {
                originalNativeCount = (int)currentNrOfVehicles.GetValue(densityManager.GetValue(manager));
                originalIdleSlots = ((IEnumerable<VehicleComponent>)idleVehicles.GetValue(trafficVehicles.GetValue(manager))).ToArray();
                originalActionLists = slots.Select(slot => NativeActions(slot.Native.GetIndex())).ToArray();
                // Remove the independently randomized guest spawns before the
                // first snapshot; authoritative slots are activated on receipt.
                foreach (var slot in slots)
                {
                    if (slot.Native.gameObject.activeSelf) slot.Native.DeactivateVehicle();
                    slot.Body.velocity = Vector3.zero; slot.Body.angularVelocity = Vector3.zero;
                }
            }

#if MONMP_DEVELOPMENT
            WriteDiagnostic("MONMP_TRAFFIC_REGISTERED", "slots=" + slots.Count + "; active=" + slots.Count(s => s.State.Active) + "; role=" + (host ? "host" : "guest"));
#endif

        }

        public IEnumerable<WorldRecord> InitialRecords()
        {
            foreach (var slot in slots)
                yield return new WorldRecord(slot.Id, 1, TrafficSlotState.Adapter, slot.State.Encode());
        }

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
            channel.PeerSynchronized += PeerSynchronized; session.PoseReceived += Pose;
            session.PeerLeft += PeerLeft;
            if (host) ConfigureCameras(force: true);
        }

        private readonly HashSet<ulong> flungLogged = new HashSet<ulong>();
        private TrafficSlotState Capture(Slot slot)
        {
            var position = slot.Body.position; var rotation = slot.Body.rotation;
            TrafficLightFlags lights = TrafficLightFlags.None;
            if (slot.Lights?.frontLights != null && slot.Lights.frontLights.activeSelf) lights |= TrafficLightFlags.Front;
            if (slot.Lights?.rearLights != null && slot.Lights.rearLights.activeSelf) lights |= TrafficLightFlags.Rear;
            if (slot.Lights?.reverseLights != null && slot.Lights.reverseLights.activeSelf) lights |= TrafficLightFlags.Reverse;
            if (slot.Lights?.stopLights != null && slot.Lights.stopLights.activeSelf) lights |= TrafficLightFlags.Brake;
            if (slot.Lights?.blinkerLeft != null && slot.Lights.blinkerLeft.activeSelf) lights |= TrafficLightFlags.LeftIndicator;
            if (slot.Lights?.blinkerRight != null && slot.Lights.blinkerRight.activeSelf) lights |= TrafficLightFlags.RightIndicator;
            var color = slot.Paint != null ? slot.Paint.color : Color.white;
            var velocity = slot.Native.gameObject.activeSelf ? slot.Body.velocity : Vector3.zero;
            // A car physics flung out of the world (or to a non-finite pose) goes back to its last
            // shared pose at rest; one bad slot must not stop every later slot's update.
            if (!ItemPose.TryLocal(position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w, out var pose) ||
                !ItemPose.FiniteValue(velocity.x, 1000) || !ItemPose.FiniteValue(velocity.y, 1000) || !ItemPose.FiniteValue(velocity.z, 1000))
            {
                if (flungLogged.Add(slot.Id)) Debug.LogWarning("MonMP traffic slot " + slot.Id + " left the world at " + position + "; restored its last shared pose.");
                pose = slot.State != null && slot.State.Pose.IsValid ? slot.State.Pose : new ItemPose(0, -1000, 0);
                slot.Body.velocity = Vector3.zero; slot.Body.angularVelocity = Vector3.zero;
                slot.Body.transform.SetPositionAndRotation(new Vector3(pose.X, pose.Y, pose.Z), new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw));
                velocity = Vector3.zero;
            }
            return TrafficCapture.Reuse(slot.State, slot.Native.GetIndex(), slot.PrefabFingerprint, slot.Native.gameObject.activeSelf,
                pose, velocity.x, velocity.y, velocity.z, lights, color.r, color.g, color.b, color.a);
        }

        public void Update()
        {
            if (disposed || session?.IsRunning != true) return;
            if (host)
            {
                UpdateHostCameras();
                UpdatePedestrians();
                if (Time.unscaledTime < nextPublish) return;
                nextPublish = Time.unscaledTime + PublishInterval;
                foreach (var slot in slots)
                {
                    var state = Capture(slot);
                    channel!.Records.TryGetValue(slot.Id, out var currentRecord);
                    if (!slot.Publication.CanSkip(state, currentRecord))
                    {
                        var encoded = state.Encode();
                        if (state.Active != slot.State.Active) hostActivationUpdates++;
                        else if (state.Active && Vector3.Distance(Position(state.Pose), Position(slot.State.Pose)) >= 0.02f)
                            hostPoseUpdates++;
                        channel.Publish(slot.Id, encoded);
                        slot.Publication.Published(state, channel.Records.TryGetValue(slot.Id, out var published) ? published : null, encoded);
                    }
                    slot.State = state;
                }
                return;
            }
            PresentReplicas();
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host || disposed) return;
            var replicas = records.Values.Where(r => r.Adapter == TrafficSlotState.Adapter).ToArray();
            if (replicas.Length == 0) return;
            if (!receivedBaseline && replicas.Length != slots.Count)
                throw new InvalidDataException("Traffic snapshot does not contain every native pool slot.");
            foreach (var record in replicas) Apply(record);
            if (!receivedBaseline)
            {
                receivedBaseline = true;

#if MONMP_DEVELOPMENT
                WriteDiagnostic("MONMP_TRAFFIC_SNAPSHOT_APPLIED", "slots=" + slots.Count + "; active=" + TestActiveSlots + "; simulation-skips=" + TestSkippedGuestUpdates);
#endif

            }
        }

        private void Changed(WorldRecord record)
        {
            if (!host && !disposed && record.Adapter == TrafficSlotState.Adapter) Apply(record);
        }

        private void Apply(WorldRecord record)
        {
            var state = TrafficSlotState.Decode(record.Payload);
            if (record.Adapter != TrafficSlotState.Adapter || state.Slot >= slots.Count ||
                record.Id != slots[state.Slot].Id || state.PrefabFingerprint != slots[state.Slot].PrefabFingerprint)
                throw new InvalidDataException("Host traffic pool layout differs from this guest.");
            var slot = slots[state.Slot];
            if (slot.HasTarget && slot.Target?.Active == true && state.Active &&
                Vector3.Distance(Position(slot.Target.Pose), Position(state.Pose)) >= 0.02f)
                receivedPoseUpdates++;
            bool wasActive = slot.Native.gameObject.activeSelf;
            if (!state.Active)
            {
                if (wasActive) slot.Native.DeactivateVehicle();
                slot.Body.velocity = Vector3.zero; slot.Body.angularVelocity = Vector3.zero;
                slot.Target = state; slot.HasTarget = true;
                return;
            }
            var destination = Position(state.Pose); var rotation = Rotation(state.Pose);
            if (!wasActive)
            {
                if (NativeActions(slot.Native.GetIndex()) == null)
                {
                    neverInitializedReplicaSlots++;
                    if (neverInitializedReplicaSlots <= 4)
                        Debug.Log("MonMP traffic guest uninitialized replica: slot=" + state.Slot + "; native-index=" + slot.Native.GetIndex() + "; activeActions=null; originally-active=" + offlineSlots[state.Slot].State.Active);
                }
                slot.Native.ActivateVehicle(destination, rotation, 1f);
                slot.Native.transform.SetPositionAndRotation(destination, rotation);
            }
            slot.Body.isKinematic = true; slot.Body.useGravity = false;
            slot.Body.interpolation = RigidbodyInterpolation.None;
            slot.Body.velocity = Vector3.zero; slot.Body.angularVelocity = Vector3.zero;
            if (!wasActive) TrafficReplicaPose.Apply(slot.Body, slot.Native.transform, destination, rotation);
            slot.FromPosition = wasActive ? slot.Native.transform.position : destination;
            slot.FromRotation = wasActive ? slot.Native.transform.rotation : rotation;
            slot.TargetPosition = destination; slot.TargetRotation = rotation;
            slot.ReceivedAt = Time.unscaledTime; slot.Target = state; slot.HasTarget = true;
            ApplyColor(slot, state); ApplyLights(slot, state.Lights);
        }

        private void PresentReplicas()
        {
            foreach (var slot in slots)
            {
                if (!slot.HasTarget || slot.Target?.Active != true || !slot.Native.gameObject.activeSelf) continue;
                float elapsed = Mathf.Max(0, Time.unscaledTime - slot.ReceivedAt);
                float blend = Mathf.Clamp01(elapsed / InterpolationInterval);
                Vector3 position = Vector3.Lerp(slot.FromPosition, slot.TargetPosition, blend);
                Quaternion rotation = Quaternion.Slerp(slot.FromRotation, slot.TargetRotation, blend);
#if MONMP_DEVELOPMENT
                if (blend > 0.02f && blend < 0.98f) guestInterpolated = true;
#endif
                float extrapolate = Mathf.Clamp(elapsed - InterpolationInterval, 0, 0.15f);
                position += new Vector3(slot.Target!.VelocityX, slot.Target.VelocityY, slot.Target.VelocityZ) * extrapolate;
                TrafficReplicaPose.Apply(slot.Body, slot.Native.transform, position, rotation);
                PresentWheels(slot, position, rotation);
                PresentEngineSound(slot);
            }
        }

        private static readonly AccessTools.FieldRef<VehicleComponent, EngineSoundComponent> engineSoundField =
            AccessTools.FieldRefAccess<VehicleComponent, EngineSoundComponent>("engineSound");
        private static readonly AccessTools.FieldRef<VehicleComponent, int> maxSpeedField =
            AccessTools.FieldRefAccess<VehicleComponent, int>("maxSpeed");

        // Gley sets each car's engine volume and pitch from its speed every frame
        // (TrafficVehicles.UpdateVehicleScripts). The guest's traffic simulation is suppressed, so its
        // replicas kept the full level that ActivateVehicle starts them at, far louder than native
        // traffic (#180). Feed the same native update the replicated speed (native master volume 1).
        // #180: every active replica's engine sits inside Gley's own speed-based volume/pitch range.

#if MONMP_DEVELOPMENT
        internal bool TestEngineLevels(out string detail)
        {
            bool ok = true; int checkedSlots = 0;
            var parts = new List<string>();
            foreach (var slot in slots)
            {
                if (slot.Target?.Active != true || !slot.Native.gameObject.activeSelf) continue;
                var engine = engineSoundField(slot.Native);
                if (engine == null || engine.audioSource == null) continue;
                checkedSlots++;
                float volume = engine.audioSource.volume, pitch = engine.audioSource.pitch;
                bool inRange = volume <= Mathf.Max(engine.minVolume, engine.maxVolume) + 0.001f && volume >= Mathf.Min(engine.minVolume, engine.maxVolume) - 0.001f &&
                    pitch <= Mathf.Max(engine.minPitch, engine.maxPitch) + 0.001f && pitch >= Mathf.Min(engine.minPitch, engine.maxPitch) - 0.001f;
                ok &= inRange;
                if (parts.Count < 6) parts.Add(slot.Native.name + " vol=" + volume.ToString("0.000") + " [" + engine.minVolume + ".." + engine.maxVolume + "] pitch=" + pitch.ToString("0.00") +
                    " [" + engine.minPitch + ".." + engine.maxPitch + "] group=" + engine.audioSource.outputAudioMixerGroup?.name + " blend=" + engine.audioSource.spatialBlend);
            }
            detail = checkedSlots + " active replica engines: " + string.Join("; ", parts);
            return ok && checkedSlots > 0;
        }
#endif


        private static void PresentEngineSound(Slot slot)
        {
            var engine = engineSoundField(slot.Native);
            if (engine == null || engine.audioSource == null) return;
            var target = slot.Target!;
            float kmh = new Vector3(target.VelocityX, target.VelocityY, target.VelocityZ).magnitude * 3.6f;
            engine.UpdateEngineSound(kmh, Mathf.Max(1, maxSpeedField(slot.Native)), 1f);
        }

        private static readonly System.Reflection.FieldInfo? roadLayersField = AccessTools.Field(typeof(TrafficManager), "roadLayers");
        private int? roadMask;

        // Mirrors Gley's UpdateWheelJob: each wheel graphic sits on the road below its origin (or at
        // full suspension), spins with forward speed and front wheels steer with the yaw rate.
        private void PresentWheels(Slot slot, Vector3 position, Quaternion rotation)
        {
            var wheels = slot.Native.allWheels;
            if (wheels == null || wheels.Length == 0) return;
            if (roadMask == null)
            {
                var manager = UnityEngine.Object.FindObjectOfType<TrafficManager>();
                roadMask = manager != null && roadLayersField?.GetValue(manager) is LayerMask mask && mask.value != 0 ? mask.value : 0;
            }
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            var target = slot.Target!;
            var forward = rotation * Vector3.forward;
            float speed = Vector3.Dot(new Vector3(target.VelocityX, target.VelocityY, target.VelocityZ), forward);
            float yaw = rotation.eulerAngles.y;
            if (slot.HasLastYaw && Mathf.Abs(speed) > 0.5f)
            {
                float yawRate = Mathf.DeltaAngle(slot.LastYaw, yaw) * Mathf.Deg2Rad / dt;
                float wheelbase = slot.Native.wheelDistance > 0 ? slot.Native.wheelDistance : 2.5f;
                float steer = Mathf.Atan(yawRate * wheelbase / speed) * Mathf.Rad2Deg;
                float max = slot.Native.GetMaxSteer() > 0 ? slot.Native.GetMaxSteer() : 30f;
                slot.WheelSteer = Mathf.Lerp(slot.WheelSteer, Mathf.Clamp(steer, -max, max), Mathf.Clamp01(dt * 8f));
            }
            slot.LastYaw = yaw; slot.HasLastYaw = true;
            var down = rotation * Vector3.down;
            var toWorld = Matrix4x4.TRS(position, rotation, Vector3.one) * Matrix4x4.TRS(slot.Native.transform.position, slot.Native.transform.rotation, Vector3.one).inverse;
            foreach (var wheel in wheels)
            {
                if (wheel?.wheelTransform == null || wheel.wheelTransform.childCount == 0) continue;
                var graphic = wheel.wheelTransform.GetChild(0);
                // Probe the road at the pose the body is moving to; place the graphic in the
                // hierarchy's current frame, which the body move then carries along.
                var origin = toWorld.MultiplyPoint3x4(wheel.wheelTransform.position);
                float length = wheel.raycastLength > 0 ? wheel.raycastLength : wheel.wheelRadius + wheel.maxSuspension + 0.5f;
                float drop = roadMask != 0 && Physics.Raycast(origin, down, out var hit, length, roadMask!.Value, QueryTriggerInteraction.Ignore)
                    ? hit.distance - wheel.wheelRadius : wheel.maxSuspension;
                graphic.position = wheel.wheelTransform.position + slot.Native.transform.rotation * Vector3.down * drop;
                graphic.localRotation = Quaternion.Euler(slot.WheelSpin, wheel.wheelPosition == Wheel.WheelPosition.Front ? slot.WheelSteer : 0f, 0f);
            }
            float circumference = wheels[0].wheelCircumference > 0 ? wheels[0].wheelCircumference : 2 * Mathf.PI * Mathf.Max(wheels[0].wheelRadius, 0.1f);
            slot.WheelSpin = Mathf.Repeat(slot.WheelSpin + speed / circumference * 360f * dt, 360f);
        }

        private static Vector3 Position(ItemPose pose) => new Vector3(pose.X, pose.Y, pose.Z);
        private static Quaternion Rotation(ItemPose pose) => new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw);

        private static void ApplyColor(Slot slot, TrafficSlotState state)
        {
            if (slot.Paint != null) slot.Paint.color = new Color(state.ColorR, state.ColorG, state.ColorB, state.ColorA);
        }

        private static void ApplyLights(Slot slot, TrafficLightFlags flags)
        {
            if (slot.Lights == null) return;
            if (slot.Lights.frontLights != null) slot.Lights.frontLights.SetActive((flags & TrafficLightFlags.Front) != 0);
            if (slot.Lights.rearLights != null) slot.Lights.rearLights.SetActive((flags & TrafficLightFlags.Rear) != 0);
            if (slot.Lights.reverseLights != null) slot.Lights.reverseLights.SetActive((flags & TrafficLightFlags.Reverse) != 0);
            if (slot.Lights.stopLights != null) slot.Lights.stopLights.SetActive((flags & TrafficLightFlags.Brake) != 0);
            if (slot.Lights.blinkerLeft != null) slot.Lights.blinkerLeft.SetActive((flags & TrafficLightFlags.LeftIndicator) != 0);
            if (slot.Lights.blinkerRight != null) slot.Lights.blinkerRight.SetActive((flags & TrafficLightFlags.RightIndicator) != 0);
        }

        private void Pose(uint peer, PlayerPose pose)
        {
            if (!host || disposed) return;
            if (!remoteCameras.TryGetValue(peer, out var camera)) remoteCameras.Add(peer, camera = new RemoteCamera(peer));
            camera.Transform.SetPositionAndRotation(new Vector3(pose.X, pose.Y, pose.Z), Quaternion.Euler(0, pose.Yaw, 0));
            camera.SeenAt = Time.unscaledTime;
            camera.Walking = pose.State == (byte)PlayerState.Walking || pose.State == (byte)PlayerState.InCreeper || pose.State == (byte)PlayerState.InWheelbarrow;
            // #381: PoseReceived is dispatched by Session.Tick/transport.Poll on Unity's
            // Update thread. Do not leave a moved, off-grid transform registered until
            // the half-second scan. Only membership crossings need native SetCameras.
            if (configuredCameras.Contains(camera.Transform) != IsInsideTrafficGrid(camera.Transform.position))
                ConfigureCameras(force: false);
        }

        // Cars stop for walking remote players the way they stop for the local one.
        private void UpdatePedestrians()
        {
            foreach (var camera in remoteCameras.Values)
            {
                bool present = camera.Walking && Time.unscaledTime - camera.SeenAt <= CameraFreshness;
                Vector3 feet = camera.Transform.position;
                foreach (var slot in slots)
                {
                    bool active = slot.Native != null && slot.Native.gameObject.activeInHierarchy;
                    bool inside = present && active && InFrontTrigger(slot, feet, camera.Inside.Contains(slot));
                    if (inside == camera.Inside.Contains(slot)) continue;
                    if (inside) { camera.Inside.Add(slot); Trigger(slot, camera.Body, true); }
                    else { camera.Inside.Remove(slot); if (active) Trigger(slot, camera.Body, false); }
                }
            }
        }

        // The test is the box's footprint (with the walker's width) against a standing body. The box
        // reaches only ~4.5 m past the bumper: a car at road speed cannot stop in that, and unlike the
        // host's own player a guest has no body on the host to block it, so the car kept going and its
        // copy pushed the guest along. The footprint is stretched ahead by the car's braking distance.
        private static bool InFrontTrigger(Slot slot, Vector3 feet, bool seen)
        {
            var box = FrontBox(slot);
            if (box == null) return false;
            var scale = box.transform.lossyScale;
            var local = Vector3.Scale(box.transform.InverseTransformPoint(feet) - box.center, scale);
            var half = Vector3.Scale(box.size, scale) * 0.5f;
            half = new Vector3(Mathf.Abs(half.x), Mathf.Abs(half.y), Mathf.Abs(half.z));
            const float Width = 0.3f, Braking = 4f;
            float speed = slot.Body != null ? Vector3.Dot(slot.Body.velocity, box.transform.forward) : 0f;
            float ahead = speed > 0 ? Mathf.Min(speed * speed / (2 * Braking), 25f) : 0f;
            // Once seen, a car that stopped short keeps the walker until they step out of its path.
            if (seen) ahead = Mathf.Max(ahead, 8f);
            return Mathf.Abs(local.x) <= half.x + Width && local.z <= half.z + Width + ahead && local.z >= -half.z - Width &&
                local.y <= half.y + 0.5f && local.y >= -half.y - 2.5f;
        }
        private static BoxCollider? FrontBox(Slot slot)
        {
            var front = slot.Native.frontTrigger;
            if (front == null || front.childCount == 0) return null;
            var box = front.GetChild(0).GetComponent<BoxCollider>();
            return box == null || !box.enabled || !box.gameObject.activeInHierarchy ? null : box;
        }

        private static void Trigger(Slot slot, Collider body, bool enter)
        {
            try { if (enter) slot.Native.OnTriggerEnter(body); else slot.Native.OnTriggerExit(body); }
            catch (Exception ex) { Debug.LogWarning("MonMP traffic pedestrian trigger failed: " + ex.Message); }
        }

        private void ReleasePedestrian(RemoteCamera camera)
        {
            foreach (var slot in camera.Inside)
                if (slot.Native != null && slot.Native.gameObject.activeInHierarchy) Trigger(slot, camera.Body, false);
            camera.Inside.Clear();
        }

        private void PeerLeft(uint peer)
        {
            synchronizedPeers.Remove(peer);
            if (!remoteCameras.TryGetValue(peer, out var camera)) return;
            ReleasePedestrian(camera);
            remoteCameras.Remove(peer); UnityEngine.Object.Destroy(camera.Transform.gameObject);
            ConfigureCameras(force: true);
        }

        private void PeerSynchronized(uint peer) => synchronizedPeers.Add(peer);

        private void UpdateHostCameras()
        {
            if (Time.unscaledTime < nextCameraScan) return;
            nextCameraScan = Time.unscaledTime + 0.5f;
            ConfigureCameras(force: false);
        }

        private void ConfigureCameras(bool force)
        {
            if (!host || gameplay.Player == null) return;
            if (cameraSceneData?.grid == null || cameraSceneData.grid.Length == 0) return;
            var cameras = new List<Transform> { gameplay.Player.transform };
            foreach (var entry in remoteCameras.Values)
            {
                if (Time.unscaledTime - entry.SeenAt > CameraFreshness) continue;
                // Gley's CurrentSceneData.GetCell indexes a finite grid without
                // bounds checks. Remote players can leave that grid, so only
                // register their poses while GetCell accepts them.
                if (IsInsideTrafficGrid(entry.Transform.position)) cameras.Add(entry.Transform);
            }
            if (!force && configuredCameras.SequenceEqual(cameras)) return;
            configuredCameras = cameras.ToArray();
            object displaced = cameraPositions.GetValue(cameraManager);
            Manager.SetCameras(configuredCameras);
            SynchronizeNativeCameraCache(configuredCameras);
            ReleaseDisplacedCameraCache(displaced);
        }

        private void SynchronizeNativeCameraCache(Transform[] cameras)
        {
            // Native UpdateCamera replaces the array on count changes but leaves it
            // unseeded, and density reads GridManager's old cache before UpdateGrid.
            // Its Update/FixedUpdate jobs all Complete before returning to this main
            // thread callback. Seed the selected transforms and use the synchronous
            // native grid update; do not allocate/schedule jobs or touch density state.
            object array = cameraPositions.GetValue(cameraManager);
            var arrayType = CameraCacheArrayType;
            int length = (int)arrayType.GetProperty("Length")!.GetValue(array);
            if (length != cameras.Length) throw new InvalidOperationException("Native traffic camera count did not match membership.");
            var item = arrayType.GetProperty("Item")!;
            var positionConstructor = CameraPositionType.GetConstructor(new[] { typeof(float), typeof(float), typeof(float) })!;
            for (int i = 0; i < cameras.Length; i++)
            {
                Vector3 position = cameras[i].position;
                object value = positionConstructor.Invoke(new object[] { position.x, position.y, position.z });
                item.SetValue(array, value, new object[] { i });
            }
            object grid = gridManager.GetValue(cameraManager);
            AccessTools.Method(typeof(GridManager), "UpdateGrid", new[] { typeof(int), arrayType })
                .Invoke(grid, new[] { activeSquaresLevel.GetValue(cameraManager), array });
        }
        private void ReleaseDisplacedCameraCache(object displaced)
        {
            // GridManager now holds the new array. Density never retains the old
            // array, and DriveJob is recreated each FixedUpdate and completed there.
            // Dispose only our displaced allocation, never the native current array.
            object current = cameraPositions.GetValue(cameraManager);
            if (displaced.Equals(current)) return;
            if (!(bool)CameraCacheArrayType.GetProperty("IsCreated")!.GetValue(displaced))
                throw new InvalidOperationException("Displaced native camera array was already released.");
            CameraCacheArrayType.GetMethod("Dispose", Type.EmptyTypes)!.Invoke(displaced, null);
            if ((bool)CameraCacheArrayType.GetProperty("IsCreated")!.GetValue(displaced))
                throw new InvalidOperationException("Displaced native camera array remained created after disposal.");
            displacedCameraArrayReleases++;
        }


#if MONMP_DEVELOPMENT
        private bool TestNativeCameraCacheMatchesMembership()
        {
            object array = cameraPositions.GetValue(cameraManager);
            object grid = gridManager.GetValue(cameraManager);
            if (!(bool)CameraCacheArrayType.GetProperty("IsCreated")!.GetValue(array) ||
                !array.Equals(gridCameraPositions.GetValue(grid)) ||
                (int)CameraCacheArrayType.GetProperty("Length")!.GetValue(array) != configuredCameras.Length) return false;
            var item = CameraCacheArrayType.GetProperty("Item")!;
            for (int i = 0; i < configuredCameras.Length; i++)
            {
                object value = item.GetValue(array, new object[] { i });
                var actual = new Vector3((float)CameraPositionType.GetField("x")!.GetValue(value),
                    (float)CameraPositionType.GetField("y")!.GetValue(value), (float)CameraPositionType.GetField("z")!.GetValue(value));
                if (actual != configuredCameras[i].position) return false;
            }
            return true;
        }
#endif


        private bool IsInsideTrafficGrid(Vector3 position)
        {
            if (cameraSceneData?.grid == null || cameraSceneData.grid.Length == 0) return false;
            try { return cameraSceneData.GetCell(position) != null; }
            catch { return false; }
        }


#if MONMP_DEVELOPMENT
        internal bool TestOffGridRemoteCameraFiltering()
        {
            if (!host || gameplay.Player == null) return false;
            var remoteEntry = remoteCameras.FirstOrDefault(entry => configuredCameras.Contains(entry.Value.Transform));
            var remote = remoteEntry.Value;
            if (remote == null || !IsInsideTrafficGrid(remote.Transform.position)) return false;

            Vector3 original = remote.Transform.position;
            Quaternion originalRotation = remote.Transform.rotation;
            float originalSeen = remote.SeenAt;
            bool originalWalking = remote.Walking;
            var originalCameras = configuredCameras.ToArray();
            // Stay inside PlayerPose's finite +/-100000 wire bounds while leaving this grid.
            var outside = new[] { new Vector3(100000, 0, 100000), new Vector3(-100000, 0, -100000),
                new Vector3(100000, 0, -100000), new Vector3(-100000, 0, 100000) }
                .FirstOrDefault(position => !IsInsideTrafficGrid(position));
            if (IsInsideTrafficGrid(outside)) return false;
            var outsidePose = new PlayerPose(outside.x, outside.y, outside.z, originalRotation.eulerAngles.y, 0);
            var originalPose = new PlayerPose(original.x, original.y, original.z, originalRotation.eulerAngles.y, 0,
                originalWalking ? (byte)PlayerState.Walking : (byte)PlayerState.InVehicle_Driver);
            outsidePose.Validate(); originalPose.Validate();
            int originalReleases = displacedCameraArrayReleases;
            bool excluded = false, stableOutside = false, restored = false, stableInside = false;
            try
            {
                // Exercise production admission immediately, without a forced reconciliation
                // or advancing the half-second scan (which concealed the original gap).
                Pose(remoteEntry.Key, outsidePose);
                excluded = !configuredCameras.Contains(remote.Transform) &&
                    configuredCameras.SequenceEqual(originalCameras.Where(camera => camera != remote.Transform)) && TestNativeCameraCacheMatchesMembership() && displacedCameraArrayReleases == originalReleases + 1;
                var excludedCameras = configuredCameras;
                Pose(remoteEntry.Key, outsidePose);
                stableOutside = ReferenceEquals(configuredCameras, excludedCameras) && displacedCameraArrayReleases == originalReleases + 1;
                Pose(remoteEntry.Key, originalPose);
                restored = configuredCameras.SequenceEqual(originalCameras) && TestNativeCameraCacheMatchesMembership() && displacedCameraArrayReleases == originalReleases + 2;
                var restoredCameras = configuredCameras;
                Pose(remoteEntry.Key, originalPose);
                stableInside = ReferenceEquals(configuredCameras, restoredCameras) && displacedCameraArrayReleases == originalReleases + 2;
            }
            finally
            {
                Pose(remoteEntry.Key, originalPose);
                remote.Transform.SetPositionAndRotation(original, originalRotation);
                remote.SeenAt = originalSeen;
                remote.Walking = originalWalking;
            }
            return excluded && stableOutside && restored && stableInside && displacedCameraArrayReleases == originalReleases + 2 &&
                TestNativeCameraCacheMatchesMembership() && configuredCameras.SequenceEqual(originalCameras) &&
                remote.Transform.position == original && remote.Transform.rotation == originalRotation &&
                remote.SeenAt == originalSeen && remote.Walking == originalWalking;
        }


        internal TrafficSlotState TestState(int slot)
        {
            if (slot < 0 || slot >= slots.Count) throw new ArgumentOutOfRangeException(nameof(slot));
            return slots[slot].State;
        }


        internal int TestTrafficDensity
        {
            get
            {
                var manager = UnityEngine.Object.FindObjectOfType<TrafficManager>();
                var density = manager != null ? densityManager.GetValue(manager) : null;
                if (density == null || maxNrOfVehicles.GetValue(density) is not int count)
                    throw new InvalidOperationException("Native traffic density is unavailable for the isolated fixture.");
                return count;
            }
        }


        internal void TestSetTrafficDensity(int count)
        {
            if (count < 0 || count > slots.Count) throw new ArgumentOutOfRangeException(nameof(count));
            Manager.SetTrafficDensity(count);
        }


        internal string TestAddNativeTrafficVehicle()
        {
            if (!host) throw new InvalidOperationException("Only the host can stage native Gley traffic.");
            if (TestNativeActiveSlots != 0 || TestTrafficCurrentCount != 0)
                throw new InvalidOperationException("Native Gley staging requires an empty, consistently tracked traffic pool.");

            var manager = UnityEngine.Object.FindObjectOfType<TrafficManager>()
                ?? throw new InvalidOperationException("Native Gley traffic manager disappeared before waypoint staging.");
            var density = densityManager.GetValue(manager)
                ?? throw new InvalidOperationException("Native Gley density manager disappeared before waypoint staging.");
            var waypoints = waypointManager.GetValue(manager)
                ?? throw new InvalidOperationException("Native Gley waypoint manager disappeared before waypoint staging.");
            var pool = trafficVehicles.GetValue(manager)
                ?? throw new InvalidOperationException("Native Gley vehicle pool disappeared before waypoint staging.");
            var grid = gridManager.GetValue(manager)
                ?? throw new InvalidOperationException("Native Gley grid manager disappeared before waypoint staging.");
            if (newVehicleRequested.GetValue(density) is not bool pending || pending)
                throw new InvalidOperationException("Native Gley already has an add-vehicle request in progress.");
            if (idleVehicles.GetValue(pool) is not ICollection idle || idle.Count != slots.Count)
                throw new InvalidOperationException("Native Gley idle pool does not match the empty traffic slot catalog.");

            var scene = gridSceneData.GetValue(grid) as CurrentSceneData
                ?? throw new InvalidOperationException("Native Gley scene waypoint grid is unavailable.");
            if (scene.grid == null || scene.allWaypoints == null || scene.grid.Length == 0 || scene.allWaypoints.Length == 0)
                throw new InvalidOperationException("Native Gley scene waypoint grid is empty.");

            var types = slots.Select(slot => slot.Native.vehicleType).Distinct().OrderBy(type => (int)type).ToArray();
            foreach (var slot in slots) slot.CaptureFixtureBaseline(Capture(slot));
            int cells = 0, spawnPoints = 0, poolCompatible = 0, exactNearest = 0, enabled = 0, collisionFree = 0;
            int badIndex = 0, wrongCell = 0, typeMismatch = 0, nearestMismatch = 0, disabled = 0, blocked = 0;
            int selectedWaypoint = -1;
            Vector3 selectedPosition = Vector3.zero;

            foreach (var row in scene.grid)
            {
                if (row?.row == null) continue;
                foreach (var cell in row.row)
                {
                    if (cell == null) continue;
                    cells++;
                    foreach (SpawnWaypoint spawn in cell.spawnWaypoints ?? new List<SpawnWaypoint>())
                    {
                        spawnPoints++;
                        int index = spawn.waypointIndex;
                        if (index < 0 || index >= scene.allWaypoints.Length || scene.allWaypoints[index] == null) { badIndex++; continue; }
                        Vector3 position = (Vector3)waypointPosition.Invoke(waypoints, new object[] { index })!;
                        GridCell owner;
                        try { owner = scene.GetCell(position); }
                        catch { wrongCell++; continue; }
                        if (owner == null || owner.row != cell.row || owner.column != cell.column ||
                            owner.spawnWaypoints == null || !owner.spawnWaypoints.Any(candidate => candidate.waypointIndex == index))
                        { wrongCell++; continue; }
                        if (spawn.allowedVehicles == null || types.Any(type => !spawn.allowedVehicles.Contains((int)type)))
                        { typeMismatch++; continue; }
                        poolCompatible++;

                        bool closestForEveryPoolType = true;
                        foreach (var type in types)
                        {
                            object? result = closestWaypoint.Invoke(density, new object[] { position, type });
                            if (result is not int closest || closest != index)
                            { closestForEveryPoolType = false; break; }
                        }
                        if (!closestForEveryPoolType) { nearestMismatch++; continue; }
                        exactNearest++;
                        if (waypointDisabled.Invoke(waypoints, new object[] { index }) is not bool isDisabled || isDisabled)
                        { disabled++; continue; }
                        enabled++;

                        bool clearForWholePool = true;
                        foreach (var slot in slots)
                        {
                            if (colliderWidth.GetValue(slot.Native) is not float width || slot.Native.frontTrigger == null)
                                throw new InvalidOperationException("Native Gley vehicle slot " + slot.Native.GetIndex() + " lacks initialized collider dimensions/front trigger.");
                            float frontOffset = slot.Native.frontTrigger.localPosition.z;
                            object? freeResult = waypointFree.Invoke(density, new object[]
                            {
                                index, slot.Native.length / 2f, slot.Native.coliderHeight, width / 2f, frontOffset
                            });
                            if (freeResult is not bool isFree || !isFree)
                            { clearForWholePool = false; break; }
                        }
                        if (!clearForWholePool) { blocked++; continue; }
                        collisionFree++;
                        selectedWaypoint = index;
                        selectedPosition = position;
                        break;
                    }
                    if (selectedWaypoint >= 0) break;
                }
                if (selectedWaypoint >= 0) break;
            }

            string diagnostics = "slots=" + slots.Count + "; types=" + string.Join(",", types.Select(type => type + "(" + (int)type + ")")) +
                "; cells=" + cells + "; spawn-points=" + spawnPoints + "; pool-compatible=" + poolCompatible +
                "; exact-nearest=" + exactNearest + "; enabled=" + enabled + "; collision-free=" + collisionFree +
                "; bad-index=" + badIndex + "; wrong-cell=" + wrongCell + "; type-mismatch=" + typeMismatch +
                "; nearest-mismatch=" + nearestMismatch + "; disabled=" + disabled + "; blocked=" + blocked;
            if (selectedWaypoint < 0)
            {
                WriteDiagnostic("MONMP_TRAFFIC_STAGE_REJECTED", diagnostics);
                throw new InvalidOperationException("No safe native Gley spawn waypoint passed pool-type, native-nearest, enabled and collision preflight. " + diagnostics);
            }

            // Gley's WaitForAddVehicle asks for a random idle vehicle before
            // using its requested type. The selected point is deliberately the
            // nearest allowed point for every pool type and has clearance for
            // every slot, so that native choice remains safe for this fixture.
            Manager.AddVehicle(selectedPosition, types[0]);
            string staged = diagnostics + "; selected-waypoint=" + selectedWaypoint + "; selected-type=" + types[0] +
                "; position=" + selectedPosition;
            WriteDiagnostic("MONMP_TRAFFIC_STAGE_REQUESTED", staged);
            return staged;
        }


        internal void TestCancelPendingNativeTrafficAdd()
        {
            if (!host) return;
            var manager = UnityEngine.Object.FindObjectOfType<TrafficManager>();
            var density = manager != null ? densityManager.GetValue(manager) : null;
            if (density == null || newVehicleRequested.GetValue(density) is not bool pending || !pending) return;

            // DensityManager has one coroutine (WaitForAddVehicle), which is
            // the fixture request guarded by newVehicleRequested.
            (density as MonoBehaviour)?.StopAllCoroutines();
            newVehicleRequested.SetValue(density, false);
        }


        internal void TestRemoveTrafficSlot(int index)
        {
            if (index < 0 || index >= slots.Count) throw new ArgumentOutOfRangeException(nameof(index));
            var manager = UnityEngine.Object.FindObjectOfType<TrafficManager>();
            var slot = slots[index];
            if (manager == null || initialized.GetValue(manager) is not bool ready || !ready || !slot.Native.gameObject.activeSelf)
                return;

            Manager.RemoveVehicle(slot.Native.gameObject);
            var baseline = slot.FixtureBaseline;
            Vector3 position = Position(baseline.Pose);
            Quaternion rotation = Rotation(baseline.Pose);
            foreach (var transform in slot.InitialTransforms)
            {
                if (transform.Transform == null) continue;
                transform.Transform.localPosition = transform.Position;
                transform.Transform.localRotation = transform.Rotation;
                transform.Transform.localScale = transform.Scale;
                transform.Transform.gameObject.SetActive(transform.ActiveSelf);
            }
            slot.Native.transform.SetPositionAndRotation(position, rotation);
            slot.Body.position = position; slot.Body.rotation = rotation;
            slot.Body.isKinematic = slot.InitialKinematic; slot.Body.useGravity = slot.InitialGravity;
            slot.Body.interpolation = slot.InitialInterpolation;
            slot.Body.velocity = slot.InitialVelocity; slot.Body.angularVelocity = slot.InitialAngularVelocity;
            ApplyColor(slot, baseline); ApplyLights(slot, baseline.Lights);
            slot.State = baseline;
            channel?.Publish(slot.Id, baseline.Encode());
        }


        private void WriteDiagnostic(string result, string detail)
        {

#if MONMP_DEVELOPMENT
            File.WriteAllText(System.IO.Path.Combine(diagnostics, "traffic-authority.txt"), result + "\n" + detail + "\n");
#endif

        }
#endif


        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (session != null)
                {
                    session.PoseReceived -= Pose; session.PeerLeft -= PeerLeft;
                    foreach (var camera in remoteCameras.Values)
                    {
                        ReleasePedestrian(camera);
                        if (camera.Transform != null) UnityEngine.Object.Destroy(camera.Transform.gameObject);
                    }
                }
                if (channel != null)
                {
                    channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.PeerSynchronized -= PeerSynchronized;
                }
                var manager = UnityEngine.Object.FindObjectOfType<TrafficManager>();
                if (host && gameplay.Player != null && manager != null && manager == cameraManager && initialized.GetValue(manager) is bool ready && ready)
                {
                    var originalCamera = new[] { gameplay.Player.transform };
                    object displaced = cameraPositions.GetValue(cameraManager);
                    Manager.SetCamera(originalCamera[0]);
                    SynchronizeNativeCameraCache(originalCamera);
                    ReleaseDisplacedCameraCache(displaced);
                }
                Exception? restoreFailure = null;
                if (!host && originalScene.IsValid() && originalScene.isLoaded && gameplay != null && gameplay.gameObject.scene == originalScene && cameraManager != null)
                {
                    foreach (var baseline in offlineSlots)
                    {
                        if (!baseline.CanRestore || baseline.Slot.Native.gameObject.scene != originalScene) continue;
                        try { baseline.Restore(); }
                        catch (Exception ex) { restoreFailure ??= ex; Debug.LogError("MonMP traffic offline restore failed for slot " + baseline.Slot.Native.GetIndex() + ": " + ex); }
                    }
                    offlineRestored = offlineSlots.All(s => s.Restored) && OfflineBookkeepingMatches();
                    if (!offlineRestored && restoreFailure == null && offlineSlots.All(s => s.Restored))
                        restoreFailure = new InvalidOperationException("Guest traffic density/idle/action bookkeeping changed during presentation.");
                }
                if (restoreFailure != null) throw new InvalidOperationException("Guest native traffic offline restoration failed.", restoreFailure);
            }
            finally { remoteCameras.Clear(); synchronizedPeers.Clear(); session = null; channel = null; }
        }
    }
}
