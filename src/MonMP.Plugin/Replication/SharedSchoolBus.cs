using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using NWH;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The native school-bus AI keeps simulating only on the host. Guests receive its
    // vehicle/Richard poses and master-door presentation, with a kinematic vehicle body.
    internal sealed class SharedSchoolBus : IDisposable
    {
        private static readonly FieldInfo RichardField = AccessTools.Field(typeof(AI_SchoolBus), "actor_Richard")!;
        private static readonly FieldInfo VehicleField = AccessTools.Field(typeof(AI_SchoolBus), "vc")!;
        private static readonly FieldInfo MasterVisualsField = AccessTools.Field(typeof(AI_SchoolBus), "MasterVisuals")!;
        private static readonly FieldInfo? BusStateField = AccessTools.Field(typeof(AI_SchoolBus), "Bus_State");
        private static readonly FieldInfo? RunningObjectsField = AccessTools.Field(typeof(AI_SchoolBus), "ToEnableWhenRunning");
        private static readonly FieldInfo? CheckpointIndexField = AccessTools.Field(typeof(AI_SchoolBus), "_index_Checkpoint");
        private static readonly FieldInfo? DayBusRunningField = AccessTools.Field(typeof(AI_SchoolBus), "Day_Bus_Running");
        private static readonly FieldInfo? BusCheckpointsField = AccessTools.Field(typeof(AI_SchoolBus), "Bus_Checkpoints");
        private static readonly MethodInfo? CheckIfTeleportationNeededMethod = AccessTools.Method(typeof(AI_SchoolBus), "CheckIfTeleportationNeeded", new[] { typeof(int) });
        // Guest bus controls the host runs: the stop button and walking into the stop zone.
        private const byte StopButton = 2, EnterZone = 3, ExitZone = 4;
        private sealed class Binding
        {
            internal ulong Id;
            internal string Identity = "";
            internal AI_SchoolBus Controller = null!;
            internal VehicleController Vehicle = null!;
            internal Actor_Richard Richard = null!;
            internal Animator? Animator;
            internal Vehicle_Visual_Pedal[] MasterVisuals = Array.Empty<Vehicle_Visual_Pedal>();
            internal bool RichardIsChild;
            internal SchoolBusState Original = null!, Current = null!;
            internal ItemPose VehicleFrom, VehicleTo, RichardFrom, RichardTo;
            internal SchoolBusVisualPose[] VisualFrom = Array.Empty<SchoolBusVisualPose>();
            internal SchoolBusVisualPose[] VisualTo = Array.Empty<SchoolBusVisualPose>();

#if MONMP_DEVELOPMENT
            internal SchoolBusVisualPose[] FixtureVisualPose = Array.Empty<SchoolBusVisualPose>();
#endif

            internal bool HasPresentationPose;
            internal bool WheelPoseResetPending = true;
            internal float PoseReceivedAt;
            internal bool OriginalAutoInput, OriginalFreezeIdle, OriginalFreezeSleep;
            internal bool OriginalVehicleEnabled, OriginalBodyKinematic, OriginalAwake;
            internal RigidbodyConstraints OriginalConstraints;
            internal CollisionDetectionMode OriginalCollision;
            internal Vector3 OriginalVelocity, OriginalAngularVelocity;
            internal Vehicle.MultiplayerInstanceType OriginalMode;
            internal bool[] OriginalExternalWheels = Array.Empty<bool>();
            internal bool[] OriginalWheelVisualModes = Array.Empty<bool>();
            internal bool CaptureFailureLogged;
            internal Vector3 EnginePosition; internal float EngineSpeed, EngineSpeedAt;

#if MONMP_DEVELOPMENT
            internal bool FixturePoseStaged;


            internal bool FixtureRendezvousPose;


            internal ItemPose FixtureVehiclePose, FixtureRichardPose;


            internal bool FixtureNativePoseHeld;


            internal SchoolBusState? FixtureHeldState;


            internal bool FixtureHeldControllerEnabled, FixtureHeldAutoInput, FixtureHeldFreezeIdle, FixtureHeldFreezeSleep;


            internal bool FixtureHeldBodyKinematic;


            internal RigidbodyConstraints FixtureHeldConstraints;


            internal CollisionDetectionMode FixtureHeldCollision;


            internal Vector3 FixtureHeldVelocity, FixtureHeldAngularVelocity;
#endif

            internal Collider[]? Zones;
            internal bool CabinChecked;
            internal MeshFilter? Cabin;
            internal int AppliedLamps = -1;
            internal readonly HashSet<uint> InZone = new HashSet<uint>();
        }

        private readonly bool host;
        private readonly string diagnosticsDirectory;
        private readonly SharedVehicles vehicles;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<AI_SchoolBus, Binding> byController = new Dictionary<AI_SchoolBus, Binding>();
        internal IEnumerable<(ulong Id, Transform Frame)> CarrierFrames()
        {
            foreach (var binding in bindings.Values)
                if (!disposed && binding.Vehicle != null) yield return (binding.Id, binding.Vehicle.transform);
        }
        private readonly Dictionary<Actor_Richard, Binding> byRichard = new Dictionary<Actor_Richard, Binding>();

#if MONMP_DEVELOPMENT
        private readonly HashSet<uint> fixturePoseAcknowledgments = new HashSet<uint>();
#endif

        private NetworkSession? session;
        private WorldChannel? channel;
        private bool disposed;
        private float nextCapture;
        private float nextControlNotice;
        private int receivedPoseUpdates;
        private int interpolationFrames;
        private int visualInterpolationFrames;
        private int suppressedNativeCalls;
        private const float CaptureSeconds = 0.1f;
        private const float RemotePoseInterpolationSeconds = 0.15f;

        internal int BoundCount => bindings.Count;
        internal bool GuestAttached => !host && !disposed && session != null;
        internal bool GuestSimulationSuppressed => GuestAttached && bindings.Count != 0;
        internal int ReceivedPoseUpdates => receivedPoseUpdates;
        internal int InterpolationFrames => interpolationFrames;
        internal int VisualInterpolationFrames => visualInterpolationFrames;
        internal int SuppressedNativeCalls => suppressedNativeCalls;

#if MONMP_DEVELOPMENT
        internal ulong TestEntity => bindings.Values.OrderBy(binding => binding.Identity, StringComparer.Ordinal).Select(binding => binding.Id).FirstOrDefault();


        internal SchoolBusState? TestState(ulong entity) => bindings.TryGetValue(entity, out var binding) ? binding.Current : null;


        internal Vector3? TestVehiclePosition(ulong entity) => bindings.TryGetValue(entity, out var binding) && binding.Vehicle != null
            ? binding.Vehicle.transform.position : (Vector3?)null;


        internal Vector3? TestRichardPosition(ulong entity) => bindings.TryGetValue(entity, out var binding) && binding.Richard != null
            ? binding.Richard.transform.position : (Vector3?)null;


        internal bool TestHasStagedPose(ulong entity) => bindings.TryGetValue(entity, out var binding) && binding.FixturePoseStaged;


        internal Vector3? TestFixtureOriginalVehiclePosition(ulong entity) => bindings.TryGetValue(entity, out var binding) && binding.FixturePoseStaged
            ? Position(binding.FixtureVehiclePose) : (Vector3?)null;


        internal int TestPoseAcknowledgmentCount => fixturePoseAcknowledgments.Count;


        internal Vector3? TestOriginalVehiclePosition(ulong entity) => bindings.TryGetValue(entity, out var binding)
            ? Position(binding.Original.VehiclePose) : (Vector3?)null;


        internal bool TestReplicaMatchesHost(ulong entity, float tolerance)
            => TestReplicaMismatchReason(entity, tolerance).Length == 0;


        internal string TestReplicaMismatchReason(ulong entity, float tolerance)
        {
            if (host || !bindings.TryGetValue(entity, out var binding) || binding.Current == null || binding.Vehicle == null || binding.Richard == null)
                return "replica binding or state unavailable";
            var state = binding.Current;
            var mismatches = new List<string>();
            float vehiclePositionError = Vector3.Distance(binding.Vehicle.transform.position, Position(state.VehiclePose));
            float vehicleRotationError = Quaternion.Angle(binding.Vehicle.transform.rotation, Rotation(state.VehiclePose));
            if (vehiclePositionError > tolerance) mismatches.Add("vehicle position error=" + vehiclePositionError.ToString("F3") + "m");
            if (vehicleRotationError > 2f) mismatches.Add("vehicle rotation error=" + vehicleRotationError.ToString("F2") + "deg");
            if (binding.Vehicle.VehicleMultiplayerInstanceType != Vehicle.MultiplayerInstanceType.Remote)
                mismatches.Add("vehicle mode=" + binding.Vehicle.VehicleMultiplayerInstanceType + " (expected Remote)");
            if (binding.Vehicle.vehicleRigidbody == null || !binding.Vehicle.vehicleRigidbody.isKinematic)
                mismatches.Add("vehicle rigidbody is not kinematic");
            if (binding.Animator != null && (binding.Animator.enabled != state.RichardAnimatorEnabled ||
                AnimatorBool(binding.Animator, "isWalking") != state.RichardWalking ||
                AnimatorBool(binding.Animator, "isDriving") != state.RichardDriving ||
                AnimatorBool(binding.Animator, "isSitting") != state.RichardSitting)) mismatches.Add("Richard animator state differs from host snapshot");
            Vector3 actualRichardPosition = binding.RichardIsChild ? binding.Richard.transform.localPosition : binding.Richard.transform.position;
            Quaternion actualRichardRotation = binding.RichardIsChild ? binding.Richard.transform.localRotation : binding.Richard.transform.rotation;
            float richardPositionError = Vector3.Distance(actualRichardPosition, Position(state.RichardPose));
            float richardRotationError = Quaternion.Angle(actualRichardRotation, Rotation(state.RichardPose));
            if (richardPositionError > tolerance)
                mismatches.Add("Richard " + (binding.RichardIsChild ? "local" : "world") + " position error=" + richardPositionError.ToString("F3") + "m");
            if (richardRotationError > 3f)
                mismatches.Add("Richard " + (binding.RichardIsChild ? "local" : "world") + " rotation error=" + richardRotationError.ToString("F2") + "deg");
            for (int i = 0; i < binding.MasterVisuals.Length; i++)
            {
                var target = state.MasterVisual(i); var actual = binding.MasterVisuals[i].transform.localEulerAngles;
                float dx = Mathf.Abs(Mathf.DeltaAngle(actual.x, target.EulerX));
                float dy = Mathf.Abs(Mathf.DeltaAngle(actual.y, target.EulerY));
                float dz = Mathf.Abs(Mathf.DeltaAngle(actual.z, target.EulerZ));
                if (dx > 2f || dy > 2f || dz > 2f)
                    mismatches.Add("master visual " + i + " error=" + dx.ToString("F1") + "/" + dy.ToString("F1") + "/" + dz.ToString("F1") + "deg");
            }
            if (mismatches.Count != 0)
                mismatches.Insert(0, "controllerEnabled=" + binding.Vehicle.enabled + ", hostEnabledField=" + state.VehicleEnabled);
            return string.Join("; ", mismatches);
        }
#endif


        internal SharedSchoolBus(bool host, string diagnostics, SharedVehicles vehicles, NativeEntityRegistry registry)
        {
            this.host = host; diagnosticsDirectory = diagnostics; this.vehicles = vehicles;
            var rows = new List<string>();
            var identities = new IdentityRegistry();
            var candidates = UnityEngine.Object.FindObjectsOfType<AI_SchoolBus>(true)
                .Where(value => value != null && value.gameObject.scene.IsValid())
                .OrderBy(value => SharedBuildingDoors.StablePath(value.transform), StringComparer.Ordinal).ToArray();
            foreach (var controller in candidates)
            {
                string controllerPath = SharedBuildingDoors.StablePath(controller.transform);
                string identity = controllerPath + "/school-bus-pose-v1";
                string disposition = "bound";
                string vehiclePath = "-", controllerRelation = "unresolved", richardPath = "-";
                int visualCount = -1;
                try
                {
                    var vehicle = VehicleField?.GetValue(controller) as VehicleController;
                    var richard = RichardField?.GetValue(controller) as Actor_Richard;
                    var visuals = MasterVisualsField?.GetValue(controller) as Vehicle_Visual_Pedal[];
                    if (vehicle != null)
                    {
                        vehiclePath = SharedBuildingDoors.StablePath(vehicle.transform);
                        controllerRelation = controller.transform == vehicle.transform ? "same-root" :
                            controller.transform.parent == vehicle.transform ? "controller-child-of-vehicle" : "unsupported";
                    }
                    if (richard != null) richardPath = SharedBuildingDoors.StablePath(richard.transform);
                    visualCount = visuals?.Length ?? -1;
                    if (vehicle == null || richard == null || visuals == null || vehicle.vehicleRigidbody == null)
                        throw new InvalidOperationException("native bus, VehicleController, Richard, or vehicle body is unavailable");
                    if (!vehicle.gameObject.scene.IsValid() || !richard.gameObject.scene.IsValid())
                        throw new InvalidOperationException("native bus references an object outside the loaded scene");
                    if (controllerRelation == "unsupported")
                        throw new InvalidOperationException("AI_SchoolBus transform must be the VehicleController root or its direct child");
                    if (vehicles.IsBound(vehicle)) throw new InvalidOperationException("VehicleController is already owned by SharedVehicles");
                    if (visuals.Length > SchoolBusState.MaxMasterVisuals || visuals.Any(value => value == null))
                        throw new InvalidOperationException("native master-visual layout is outside the supported bound");
                    bool richardIsChild = richard.transform.IsChildOf(vehicle.transform);
                    identity += "/vehicle/" + vehiclePath + "/controller-relation/" + controllerRelation +
                        "/richard/" + richardPath + (richardIsChild ? "/local" : "/world");
                    ulong id = identities.Register(identity);
                    var animator = richard.animator;
                    var binding = new Binding
                    {
                        Id = id, Identity = identity, Controller = controller, Vehicle = vehicle, Richard = richard,
                        Animator = animator, MasterVisuals = visuals, RichardIsChild = richardIsChild,
                        OriginalAutoInput = vehicle.input.autoSetInput, OriginalFreezeIdle = vehicle.freezeWhileIdle,
                        OriginalFreezeSleep = vehicle.freezeWhileAsleep, OriginalVehicleEnabled = vehicle.enabled,
                        OriginalBodyKinematic = vehicle.vehicleRigidbody.isKinematic, OriginalAwake = vehicle.IsAwake,
                        OriginalConstraints = vehicle.vehicleRigidbody.constraints,
                        OriginalCollision = vehicle.vehicleRigidbody.collisionDetectionMode,
                        OriginalVelocity = vehicle.vehicleRigidbody.velocity,
                        OriginalAngularVelocity = vehicle.vehicleRigidbody.angularVelocity,
                        OriginalMode = vehicle.VehicleMultiplayerInstanceType,
                        OriginalExternalWheels = vehicle.Wheels.Select(wheel => wheel.wheelController.UsingExternalUpdate).ToArray(),
                        OriginalWheelVisualModes = vehicle.Wheels.Select(wheel => wheel.wheelController.visualOnlyUpdate).ToArray()
                    };
                    binding.Original = binding.Current = Capture(binding);
                    binding.VehicleFrom = binding.VehicleTo = binding.Original.VehiclePose;
                    binding.RichardFrom = binding.RichardTo = binding.Original.RichardPose;
                    binding.VisualFrom = binding.VisualTo = ReadVisuals(binding.Original);
                    registry.Bind(id, SchoolBusState.Adapter, controller.gameObject, identity, true, EntityPersistence.SessionOnly);
                    bindings.Add(id, binding); byController.Add(controller, binding); byRichard.Add(richard, binding);
                }
                catch (Exception ex) { disposition = "skipped: " + ex.Message; }
                rows.Add(string.Join("\t", disposition, controllerPath, vehiclePath, controllerRelation, richardPath, visualCount, identity));
            }

#if MONMP_DEVELOPMENT
            Directory.CreateDirectory(diagnosticsDirectory);


            File.WriteAllLines(Path.Combine(diagnosticsDirectory, "shared-school-bus.tsv"),
                new[] { "status\tcontroller-path\tvehicle-path\tcontroller-relation\trichard-path\tvisual-count\tidentity",
                    "summary\t-\t-\t-\t-\t-\trole=" + (host ? "host" : "guest") + ",controllers=" + candidates.Length + ",bound=" + bindings.Count }.Concat(rows));
#endif

        }

        internal static void ValidateNativeLayout()
        {
            RequireField("actor_Richard", typeof(Actor_Richard));
            RequireField("vc", typeof(VehicleController));
            RequireField("MasterVisuals", typeof(Vehicle_Visual_Pedal[]));
            RequireField("_index_Checkpoint", typeof(int));
            RequireField("Day_Bus_Running", typeof(int[]));
            RequireField("Bus_Checkpoints", typeof(AI_SchoolBus_Checkpoint[]));
            RequireMethod(typeof(AI_SchoolBus), "get_Get_Bus_State", Type.EmptyTypes, typeof(AI_SchoolBus.SchoolBus_State));
            RequireMethod(typeof(AI_SchoolBus), "Start", Type.EmptyTypes, typeof(void));
            RequireMethod(typeof(AI_SchoolBus), "Update", Type.EmptyTypes, typeof(void));
            RequireMethod(typeof(AI_SchoolBus), "Tick", new[] { typeof(int) }, typeof(void));
            RequireMethod(typeof(AI_SchoolBus), "CheckIfTeleportationNeeded", new[] { typeof(int) }, typeof(AI_SchoolBus_Checkpoint));
            RequireMethod(typeof(AI_SchoolBus), "Button_StopBus", Type.EmptyTypes, typeof(void));
            RequireMethod(typeof(AI_SchoolBus), "PlayerEnteredDetectionZone", Type.EmptyTypes, typeof(void));
            RequireMethod(typeof(AI_SchoolBus), "PlayerExitedDetectionZone", Type.EmptyTypes, typeof(void));
            RequireMethod(typeof(AI_SchoolBus), "StartRoad", Type.EmptyTypes, typeof(void));
            RequireMethod(typeof(AI_SchoolBus), "EnableVehicle", Type.EmptyTypes, typeof(void));
            RequireMethod(typeof(AI_SchoolBus), "Running", Type.EmptyTypes, typeof(void));
            RequireMethod(typeof(AI_SchoolBus), "LightsManager", Type.EmptyTypes, typeof(void));
            RequireMethod(typeof(AI_SchoolBus), "LightsManager_Close", Type.EmptyTypes, typeof(void));
            RequireMethod(typeof(AI_SchoolBus), "get_Get_MaxThrottle", Type.EmptyTypes, typeof(float));
            RequireCoroutine(typeof(AI_SchoolBus), "PutToSleep");
            RequireCoroutine(typeof(AI_SchoolBus), "OpenMasterWhileSleeping");
            RequireMethod(typeof(Actor_Richard), "PlayerClickedOnBusRadio", Type.EmptyTypes, typeof(void));
            RequireMethod(typeof(Actor_Richard), "PlayerSpitOn", Type.EmptyTypes, typeof(void));
        }

        private static void RequireField(string name, Type fieldType)
        {
            var field = AccessTools.Field(typeof(AI_SchoolBus), name);
            if (field == null || field.IsStatic || field.FieldType != fieldType)
                throw new InvalidOperationException("Unsupported native AI_SchoolBus field: " + name);
        }
        private static void RequireMethod(Type type, string name, Type[] arguments, Type returnType)
        {
            var method = AccessTools.Method(type, name, arguments);
            if (method == null || method.IsStatic || method.ReturnType != returnType)
                throw new InvalidOperationException("Unsupported native method: " + type.FullName + "." + name);
        }
        private static void RequireCoroutine(Type type, string name)
        {
            var method = AccessTools.Method(type, name, Type.EmptyTypes);
            if (method == null || method.ReturnType != typeof(System.Collections.IEnumerator) || AccessTools.EnumeratorMoveNext(method) == null)
                throw new InvalidOperationException("Unsupported native coroutine: " + type.FullName + "." + name);
            var moveNext = AccessTools.EnumeratorMoveNext(method)!;
            var owner = AccessTools.Field(moveNext.DeclaringType, "<>4__this");
            if (moveNext.ReturnType != typeof(bool) || owner?.FieldType != type)
                throw new InvalidOperationException("Unsupported native coroutine state: " + type.FullName + "." + name);
        }

        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(binding =>
            new WorldRecord(binding.Id, 1, SchoolBusState.Adapter, Capture(binding).Encode()));

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected; channel = world;
            connected.PoseReceived += OnPose; connected.PeerLeft += OnLeft;
            world.SnapshotApplied += Snapshot; world.Changed += Changed;
            if (!host) foreach (var binding in bindings.Values) SetRemoteMode(binding);
            world.CommandRequested += Command;
        }

        internal bool SuppressNative(AI_SchoolBus controller)
        {
            bool suppress = GuestAttached && controller != null && byController.ContainsKey(controller);
            if (suppress) suppressedNativeCalls++;
            return suppress;
        }
        internal bool TryGetReplicatedBusState(AI_SchoolBus controller, out AI_SchoolBus.SchoolBus_State state)
        {
            state = default;
            if (!GuestAttached || controller == null || !byController.TryGetValue(controller, out var binding) || binding.Current == null) return false;
            state = (AI_SchoolBus.SchoolBus_State)binding.Current.BusState;
            return true;
        }
        internal bool SuppressCoroutine(object stateMachine)
        {
            if (!GuestAttached || stateMachine == null) return false;
            var owner = AccessTools.Field(stateMachine.GetType(), "<>4__this")?.GetValue(stateMachine) as AI_SchoolBus;
            return owner != null && SuppressNative(owner);
        }
        internal bool RejectExplicitControl(Actor_Richard actor, string method)
        {
            if (!GuestAttached || actor == null || !byRichard.ContainsKey(actor)) return false;
            string action = method == "Button_StopBus" ? "stop control" : method == "PlayerEnteredDetectionZone" ? "boarding" :
                method == "PlayerClickedOnBusRadio" ? "radio control" : method == "PlayerSpitOn" ? "spitting on Richard" : "bus control";
            if (Time.unscaledTime >= nextControlNotice)
            {
                nextControlNotice = Time.unscaledTime + 2.5f;
                try { Singleton<UI_ErrorMessage>.i.Show("School bus " + action + " is not synchronized yet."); }
                catch (Exception ex) { Debug.LogWarning("MonMP could not show school-bus control feedback: " + ex.Message); }
            }
            return true;
        }
        internal bool RejectExplicitControl(AI_SchoolBus controller, string method)
        {
            if (!GuestAttached || controller == null || !byController.TryGetValue(controller, out var binding)) return false;
            if (Time.unscaledTime >= nextControlNotice)
            {
                nextControlNotice = Time.unscaledTime + 2.5f;
                try { Singleton<UI_ErrorMessage>.i.Show("School bus " + (method == "Button_StopBus" ? "stop control" : "boarding") + " is not synchronized yet."); }
                catch (Exception ex) { Debug.LogWarning("MonMP could not show school-bus control feedback: " + ex.Message); }
            }
            _ = binding;
            return true;
        }

        private static SchoolBusState Capture(Binding binding)
        {
            var vehiclePose = Pose(binding.Vehicle.transform, false);
            var richardPose = Pose(binding.Richard.transform, binding.RichardIsChild);
            var visuals = binding.MasterVisuals.Select(value =>
            {
                var euler = value.transform.localEulerAngles;
                return SchoolBusVisualPose.Normalized(euler.x, euler.y, euler.z);
            }).ToArray();
            var animator = binding.Animator;
            ushort lamps = 0; byte controls = 0;
            try { var lighting = NativeVehicleLighting.Capture(binding.Vehicle); lamps = lighting.Lamps; controls = lighting.Controls; }
            catch (Exception) { }
            return new SchoolBusState((byte)binding.Controller.Get_Bus_State, binding.Vehicle.enabled,
                animator != null && animator.enabled, AnimatorBool(animator, "isWalking"),
                AnimatorBool(animator, "isDriving"), AnimatorBool(animator, "isSitting"), vehiclePose, richardPose, visuals, lamps, controls);
        }

        private static ItemPose Pose(Transform transform, bool local)
        {
            // Unity localPosition/localRotation are relative to the immediate parent, even when this is a deeper vehicle descendant.
            Vector3 position = local ? transform.localPosition : transform.position;
            Quaternion rotation = local ? transform.localRotation : transform.rotation;
            return new ItemPose(position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w);
        }
        private static Vector3 Position(ItemPose pose) => new Vector3(pose.X, pose.Y, pose.Z);
        private static Quaternion Rotation(ItemPose pose) => new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
        private static bool AnimatorBool(Animator? animator, string name)
        {
            if (animator == null) return false;
            int hash = Animator.StringToHash(name);
            return animator.parameters.Any(parameter => parameter.nameHash == hash && parameter.type == AnimatorControllerParameterType.Bool) && animator.GetBool(hash);
        }
        private static void SetAnimatorBool(Animator animator, string name, bool value)
        {
            int hash = Animator.StringToHash(name);
            if (animator.parameters.Any(parameter => parameter.nameHash == hash && parameter.type == AnimatorControllerParameterType.Bool))
                animator.SetBool(hash, value);
        }
        private static SchoolBusVisualPose[] ReadVisuals(SchoolBusState state) =>
            Enumerable.Range(0, state.MasterVisualCount).Select(state.MasterVisual).ToArray();


#if MONMP_DEVELOPMENT
        private static SchoolBusState MakeFixturePose(Binding binding, SchoolBusState live)
        {
            Vector3 originalPosition = Position(binding.FixtureVehiclePose);
            Quaternion originalRotation = Rotation(binding.FixtureVehiclePose);
            Quaternion delta = binding.FixtureRendezvousPose ? Quaternion.identity : Quaternion.Euler(0, 18, 0);
            Vector3 stagedPosition = originalPosition + Vector3.right * (binding.FixtureRendezvousPose ? 0.5f : 2.5f);
            Quaternion stagedRotation = delta * originalRotation;
            ItemPose vehiclePose = Pose(stagedPosition, stagedRotation);
            ItemPose richardPose = binding.RichardIsChild
                ? binding.FixtureRichardPose
                : Pose(stagedPosition + delta * (Position(binding.FixtureRichardPose) - originalPosition),
                    delta * Rotation(binding.FixtureRichardPose));
            var visuals = (SchoolBusVisualPose[])binding.FixtureVisualPose.Clone();
            var first = visuals[0];
            visuals[0] = SchoolBusVisualPose.Normalized((first.EulerX + 7f) % 360f,
                (first.EulerY + 11f) % 360f, (first.EulerZ + 13f) % 360f);
            return new SchoolBusState(live.BusState, live.VehicleEnabled, live.RichardAnimatorEnabled,
                live.RichardWalking, live.RichardDriving, live.RichardSitting, vehiclePose, richardPose, visuals, live.Lamps, live.LampControls);
        }
#endif


        private static ItemPose Pose(Vector3 position, Quaternion rotation) =>
            new ItemPose(position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w);

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values.Where(record => record.Adapter == SchoolBusState.Adapter)) Apply(record, true);
        }
        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == SchoolBusState.Adapter) Apply(record, false);
        }
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private float nextControlRequest;

#if MONMP_DEVELOPMENT
        internal int TestRemoteStops { get; private set; }
#endif

        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) => presence.Remove(peer);

        // A guest's own bus stop button or stop zone: the host's bus stops (or closes) for them.
        internal bool RequestControl(AI_SchoolBus controller, string method)
        {
            if (!GuestAttached || controller == null || !byController.TryGetValue(controller, out var binding)) return false;
            byte action = method == "Button_StopBus" ? StopButton : method == "PlayerEnteredDetectionZone" ? EnterZone : ExitZone;
            if (action == StopButton) { try { Singleton<SOUND_Manager>.i.PlaySound_SwitchOn(); } catch { } }
            if (action == EnterZone)
            {
                var state = Singleton<Gameplay>.i.Get_PlayerState;
                if (state != PlayerState.Walking && state != PlayerState.InCreeper && state != PlayerState.InWheelbarrow) return true;
            }
            if (channel?.Ready != true) return RejectExplicitControl(controller, method);
            if (action != ExitZone && Time.unscaledTime < nextControlRequest) return true;
            nextControlRequest = Time.unscaledTime + 0.5f;
            // Bus motion advances the record while this click is in transit. The host
            // validates the current bus state and requester pose independently.
            channel.Request(binding.Id, new byte[] { 0x53, 0x42, action }, useLease: true);
            return true;
        }

        private void RemoteControl(uint peer, Binding binding, byte action)
        {
            if (peer == 0 || BusStateField == null || !presence.TryGetValue(peer, out var seen) || Time.unscaledTime - seen.Time > 2f) return;
            var at = new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z);
            if (Vector3.Distance(at, binding.Vehicle.transform.position) > 40f) return;
            var bus = binding.Controller;
            var state = bus.Get_Bus_State;
            if (action == ExitZone) { bus.PlayerExitedDetectionZone(); return; }
            if (action == StopButton && state == AI_SchoolBus.SchoolBus_State.Sleeping) { bus.Button_StopBus(); return; }
            if (action == StopButton && state != AI_SchoolBus.SchoolBus_State.Running) return;
            bool walking = seen.Pose.State == (byte)PlayerState.Walking || seen.Pose.State == (byte)PlayerState.InCreeper || seen.Pose.State == (byte)PlayerState.InWheelbarrow;
            // The native zone check, with the remote player's state in place of the host's.
            if (action == EnterZone && !walking) return;
            if (state == AI_SchoolBus.SchoolBus_State.Sleeping || state == AI_SchoolBus.SchoolBus_State.Stopping || state == AI_SchoolBus.SchoolBus_State.WaitingForPlayer ||
                state == AI_SchoolBus.SchoolBus_State.MasterOpening || bus.PlayerSpitOnDriver) return;
            BusStateField.SetValue(bus, AI_SchoolBus.SchoolBus_State.Stopping);

#if MONMP_DEVELOPMENT
            TestRemoteStops++;
#endif

        }

        // The bus's stop zone is a PlayerDetector trigger that sees only the host's own player, so a
        // guest walking in front of the bus was pushed along. Include the actual interior as well:
        // the boarding trigger does not cover the rear aisle. Keep applying presence while occupied,
        // since the native boarding timer otherwise expires without another trigger-enter event.
        private void UpdateRemoteZones()
        {
            foreach (var binding in bindings.Values)
            {
                if (binding.Controller == null) continue;
                binding.Zones ??= FindZones(binding.Controller);
                bool hadRemote = binding.InZone.Count != 0;
                foreach (var entry in presence)
                {
                    var pose = entry.Value.Pose;
                    bool walking = pose.State == (byte)PlayerState.Walking || pose.State == (byte)PlayerState.InCreeper || pose.State == (byte)PlayerState.InWheelbarrow;
                    bool inside = walking && pose.Rest == PlayerRest.None &&
                        MonMPPlugin.Instance?.World?.Permadeath?.IsPermadead(entry.Key) != true &&
                        Time.unscaledTime - entry.Value.Time <= 2f &&
                        InStopArea(binding, new Vector3(pose.X, pose.Y, pose.Z));
                    if (inside) { binding.InZone.Add(entry.Key); RemoteControl(entry.Key, binding, EnterZone); }
                    else if (!inside) binding.InZone.Remove(entry.Key);
                }
                foreach (var gone in binding.InZone.Where(peer => !presence.ContainsKey(peer)).ToList())
                    binding.InZone.Remove(gone);
                if (hadRemote && binding.InZone.Count == 0) LeftZone(binding);
                if (binding.InZone.Count != 0 && !binding.Controller.PlayerSpitOnDriver &&
                    binding.Controller.Get_Bus_State == AI_SchoolBus.SchoolBus_State.WaitingForPlayer)
                    WaitField?.SetValue(binding.Controller, 12f);
            }
        }
        // The last player walked out of the stop zone: the native exit closes the door and drives on
        // at once instead of waiting out the 12 s boarding timer (#136). The host's own player
        // still inside keeps it waiting, as the native trigger would.
        private void LeftZone(Binding binding)
        {
            if (binding.InZone.Count > 0 || binding.Controller == null) return;
            var gameplay = Singleton<Gameplay>.i;
            var state = gameplay.Get_PlayerState;
            bool hostWalking = state == PlayerState.Walking || state == PlayerState.InCreeper || state == PlayerState.InWheelbarrow;
            if (hostWalking && gameplay.PlayerWalking != null && InStopArea(binding, gameplay.PlayerWalking.transform.position)) return;
            binding.Controller.PlayerExitedDetectionZone();

#if MONMP_DEVELOPMENT
            TestRemoteExits++;
#endif

        }

#if MONMP_DEVELOPMENT
        internal int TestRemoteExits { get; private set; }
#endif

        private static void ShowRunning(AI_SchoolBus controller, bool running)
        {
            if (!(RunningObjectsField?.GetValue(controller) is GameObject[] objects)) return;
            foreach (var shown in objects) if (shown != null && shown.activeSelf != running) shown.SetActive(running);
        }
        private static readonly FieldInfo? WaitField = AccessTools.Field(typeof(AI_SchoolBus), "totalTimeToWaitForThePlayer");
        // A fake remote walker steps into the stop zone of a running bus, then out of it after the bus
        // has stopped for them: the bus must stop, then stop waiting at once. Restored in the same frame.

#if MONMP_DEVELOPMENT
        internal string TestRemoteZoneCycle()
        {
            var binding = bindings.Values.FirstOrDefault(b => b.Controller != null);
            if (binding == null || BusStateField == null || WaitField == null) return "no bus";
            binding.Zones ??= FindZones(binding.Controller);
            if (binding.Zones.Length == 0) return "no zone";
            const uint Fake = 9999;
            var bus = binding.Controller;
            var savedState = BusStateField.GetValue(bus); var savedWait = WaitField.GetValue(bus);
            int exits = TestRemoteExits;
            try
            {
                var zone = binding.Zones[0].bounds.center - Vector3.up * 0.9f;
                BusStateField.SetValue(bus, AI_SchoolBus.SchoolBus_State.Running);
                presence[Fake] = (new PlayerPose(zone.x, zone.y, zone.z, 0, 0, (byte)PlayerState.Walking), Time.unscaledTime);
                UpdateRemoteZones();
                var stopped = bus.Get_Bus_State;
                if (stopped != AI_SchoolBus.SchoolBus_State.Stopping) return "walker in the zone left the bus " + stopped;
                BusStateField.SetValue(bus, AI_SchoolBus.SchoolBus_State.WaitingForPlayer); WaitField.SetValue(bus, 12f);
                var away = zone + binding.Controller.transform.right * 30f;
                presence[Fake] = (new PlayerPose(away.x, away.y, away.z, 0, 0, (byte)PlayerState.Walking), Time.unscaledTime);
                UpdateRemoteZones();
                float wait = (float)WaitField.GetValue(bus);
                if (TestRemoteExits == exits || wait > 0f) return "walker leaving kept the bus waiting " + wait.ToString("F1") + " s";
                return "";
            }
            finally
            {
                presence.Remove(Fake); binding.InZone.Remove(Fake);
                BusStateField.SetValue(bus, savedState); WaitField.SetValue(bus, savedWait);
            }
        }

        internal string TestRemoteCabinCycle()
        {
            var binding = bindings.Values.FirstOrDefault(b => b.Controller != null);
            if (binding == null || BusStateField == null || WaitField == null) return "no bus";
            binding.Zones ??= FindZones(binding.Controller);
            FindCabin(binding);
            if (binding.Cabin == null || binding.Cabin.sharedMesh == null) return "no audited interior mesh";
            const uint First = 9998, Second = 9997;
            var controller = binding.Controller;
            var savedState = BusStateField.GetValue(controller); var savedWait = WaitField.GetValue(controller);
            var savedZones = binding.Zones;
            var box = binding.Cabin.sharedMesh.bounds;
            var originalPosition = binding.Vehicle.transform.position; var originalRotation = binding.Vehicle.transform.rotation;
            void Walker(uint peer, Vector3 feet, byte state = (byte)PlayerState.Walking, float age = 0) =>
                presence[peer] = (new PlayerPose(feet.x, feet.y, feet.z, 0, 0, state), Time.unscaledTime - age);
            Vector3 Point(float x, float z) => binding.Cabin.transform.TransformPoint(box.center +
                Vector3.Scale(box.extents, new Vector3(x, 0, z))) - Vector3.up * 0.9f;
            try
            {
                // Exercise the cabin extension independently of the native boarding trigger.
                binding.Zones = Array.Empty<Collider>();
                for (int turn = 0; turn < 2; turn++)
                {
                    binding.Vehicle.transform.rotation = originalRotation * Quaternion.Euler(0, turn * 73, 0);
                    foreach (float x in new[] { -0.5f, 0f, 0.5f })
                        foreach (float z in new[] { -0.75f, 0f, 0.75f })
                        {
                            Walker(First, Point(x, z));
                            BusStateField.SetValue(controller, AI_SchoolBus.SchoolBus_State.Running);
                            UpdateRemoteZones();
                            if (controller.Get_Bus_State != AI_SchoolBus.SchoolBus_State.Stopping) return "cabin walker did not stop running bus";
                            BusStateField.SetValue(controller, AI_SchoolBus.SchoolBus_State.WaitingForPlayer);
                            WaitField.SetValue(controller, 0.01f); UpdateRemoteZones();
                            if ((float)WaitField.GetValue(controller) < 11f) return "standing rider lost boarding hold";
                            BusStateField.SetValue(controller, AI_SchoolBus.SchoolBus_State.MasterClosing); UpdateRemoteZones();
                            if (controller.Get_Bus_State != AI_SchoolBus.SchoolBus_State.Stopping) return "door closing was not interrupted for standing rider";
                        }
                }
                binding.Vehicle.transform.rotation = originalRotation;
                Walker(Second, Point(0, 0));
                Walker(First, Point(0, 0) + binding.Vehicle.transform.right * 20f);
                BusStateField.SetValue(controller, AI_SchoolBus.SchoolBus_State.WaitingForPlayer);
                UpdateRemoteZones();
                if ((float)WaitField.GetValue(controller) < 11f || !binding.InZone.Contains(Second)) return "second walker did not retain hold";
                Walker(Second, Point(0, 0), age: 3f); UpdateRemoteZones();
                if (binding.InZone.Count != 0 || (float)WaitField.GetValue(controller) > 0f) return "stale walker retained hold";
                Walker(First, Point(0, 0), (byte)PlayerState.InFurnitureChair);
                BusStateField.SetValue(controller, AI_SchoolBus.SchoolBus_State.Running); UpdateRemoteZones();
                if (controller.Get_Bus_State != AI_SchoolBus.SchoolBus_State.Running) return "seated rider stopped the bus";
                Walker(First, Point(0, 0)); UpdateRemoteZones();
                BusStateField.SetValue(controller, AI_SchoolBus.SchoolBus_State.WaitingForPlayer);
                presence.Remove(First); presence.Remove(Second); UpdateRemoteZones();
                if (binding.InZone.Count != 0 || (float)WaitField.GetValue(controller) > 0f) return "disconnected walker retained hold";
                Debug.Log("MonMP school bus cabin passed: 18 front/middle/rear/side samples at two headings; waiting hold, closing recovery, multiple walkers, stale/seated/disconnected release.");
                return "";
            }
            finally
            {
                presence.Remove(First); presence.Remove(Second); binding.InZone.Remove(First); binding.InZone.Remove(Second);
                binding.Zones = savedZones;
                binding.Vehicle.transform.SetPositionAndRotation(originalPosition, originalRotation);
                BusStateField.SetValue(controller, savedState); WaitField.SetValue(controller, savedWait);
            }
        }
#endif


        // Exercise the actual private native checkpoint method through Harmony. The baseline
        // and stale-pose cases verify that off-day advancement remains native; fresh remotes
        // at the bus or destination must veto only the teleport and restore its index mutation.

#if MONMP_DEVELOPMENT
        internal string TestRemoteCheckpointSkip()
        {
            if (!host) return "not-host";
            var binding = bindings.Values.FirstOrDefault(value => value.Controller != null && value.Vehicle != null);
            if (binding == null || CheckpointIndexField == null || DayBusRunningField == null || BusCheckpointsField == null ||
                CheckIfTeleportationNeededMethod == null || BusStateField == null)
                return "native checkpoint layout unavailable";
            var checkpoints = BusCheckpointsField.GetValue(binding.Controller) as AI_SchoolBus_Checkpoint[];
            var dayFlags = DayBusRunningField.GetValue(binding.Controller) as int[];
            if (checkpoints == null || checkpoints.Length < 2 || dayFlags == null || dayFlags.Length == 0)
                return "native checkpoints or day flags unavailable";
            var tickSystem = Singleton<TickSystem>.i;
            int day = tickSystem.Get_DayOfTheWeek;
            if (day < 0 || day >= dayFlags.Length) return "native day index is outside the day flags";
            var bus = binding.Controller;
            Vector3 busPosition = binding.Vehicle.transform.position;
            int startingIndex = Enumerable.Range(0, checkpoints.Length - 1)
                .Where(index => checkpoints[index] != null && Vector3.Distance(checkpoints[index].transform.position, busPosition) >= 45f)
                .OrderByDescending(index => Vector3.Distance(checkpoints[index].transform.position, busPosition))
                .DefaultIfEmpty(-1).First();
            if (startingIndex < 0) return "no checkpoint safely beyond the native 40 m advancement threshold";

            var gameplay = Singleton<Gameplay>.i;
            var player = gameplay?.Player;
            if (gameplay == null || player == null) return "native player transform unavailable";
            if (gameplay.Get_IsSleeping || tickSystem.isSaving)
                return "skip: world is sleeping or saving, so ordinary native accelerated-time semantics are active";
            Transform playerTransform = player.transform;
            Vector3 savedPlayerPosition = playerTransform.position;
            Quaternion savedPlayerRotation = playerTransform.rotation;
            int savedIndex = (int)CheckpointIndexField.GetValue(bus);
            int savedDayFlag = dayFlags[day];
            object? savedBusState = BusStateField.GetValue(bus);
            var savedPresence = presence.ToArray();
            const uint FakePeer = 0xFFFFFFFE;
            bool savedFastForward = tickSystem.isFastForwardTime;
            Vector3 farPosition = busPosition + Vector3.forward * 10000f;
            int nativeTick = tickSystem.Get_Tick;

            try
            {
                playerTransform.SetPositionAndRotation(farPosition, savedPlayerRotation);
                dayFlags[day] = 0;
                presence.Clear();

                // With no remote player, retain the native off-day fast-forward and index update.
                CheckpointIndexField.SetValue(bus, startingIndex);
                var baseline = InvokeNativeCheckpointCheck(bus, nativeTick);
                if (baseline != checkpoints[checkpoints.Length - 1] || (int)CheckpointIndexField.GetValue(bus) != checkpoints.Length - 1)
                    return "native off-day baseline did not return/advance to the final checkpoint";

                // A current remote at the bus or final destination vetoes that same native result.
                Vector3[] remoteLocations = { busPosition, checkpoints[checkpoints.Length - 1].transform.position };
                foreach (var remotePosition in remoteLocations)
                {
                    CheckpointIndexField.SetValue(bus, startingIndex);
                    presence[FakePeer] = (new PlayerPose(remotePosition.x, remotePosition.y, remotePosition.z, 0, 0,
                        (byte)PlayerState.Walking), Time.unscaledTime);
                    var vetoed = InvokeNativeCheckpointCheck(bus, nativeTick);
                    presence.Clear();
                    if (vetoed != null || (int)CheckpointIndexField.GetValue(bus) != startingIndex)
                        return "fresh remote near bus/destination did not veto and restore the native checkpoint index";
                }

                // Old presence must not block native fast-forward after its freshness window.
                CheckpointIndexField.SetValue(bus, startingIndex);
                Vector3 destination = checkpoints[checkpoints.Length - 1].transform.position;
                presence[FakePeer] = (new PlayerPose(destination.x, destination.y, destination.z, 0, 0,
                    (byte)PlayerState.Walking), Time.unscaledTime - 2.1f);
                var stale = InvokeNativeCheckpointCheck(bus, nativeTick);
                if (stale != checkpoints[checkpoints.Length - 1] || (int)CheckpointIndexField.GetValue(bus) != checkpoints.Length - 1)
                    return "stale remote presence incorrectly blocked native off-day checkpoint advancement";

                // When the off-day shortcut fails because the host is within 100 m of the
                // final destination, the native method can still reach its ordinary 210/168 m
                // branch for a different overdue checkpoint. A fresh remote 150 m from that
                // selected destination must veto it, even though 150 m exceeds off-day radii.
                Vector3 finalPosition = checkpoints[checkpoints.Length - 1].transform.position;
                Vector3 awayFromBus = finalPosition - busPosition;
                awayFromBus = awayFromBus.sqrMagnitude > 0.01f ? awayFromBus.normalized : Vector3.forward;
                int ordinaryIndex = -1;
                Vector3 ordinaryHostPosition = default;
                for (int index = 0; index < checkpoints.Length - 1 && ordinaryIndex < 0; index++)
                {
                    var candidate = checkpoints[index];
                    if (candidate == null || Vector3.Distance(busPosition, candidate.transform.position) <= 350f) continue;
                    foreach (float offset in new[] { 0f, 25f, 50f, 75f, 100f })
                    {
                        Vector3 candidateHost = finalPosition + awayFromBus * offset;
                        if (Vector3.Distance(candidateHost, finalPosition) > 100f ||
                            Vector3.Distance(candidateHost, busPosition) <= 210f ||
                            Vector3.Distance(candidateHost, candidate.transform.position) <= 168f) continue;
                        ordinaryIndex = index;
                        ordinaryHostPosition = candidateHost;
                        break;
                    }
                }
                if (ordinaryIndex < 0)
                    return "current route has no safe geometry for an off-day ordinary-branch 210/168 m assertion";

                var ordinaryDestination = checkpoints[ordinaryIndex];
                int savedTimeHere = ordinaryDestination.TimeHere;
                try
                {
                    tickSystem.isFastForwardTime = false;
                    dayFlags[day] = 0;
                    ordinaryDestination.TimeHere = 0;
                    playerTransform.SetPositionAndRotation(ordinaryHostPosition, savedPlayerRotation);
                    Vector3 ordinaryRemote = ordinaryDestination.transform.position +
                        (ordinaryDestination.transform.position - busPosition).normalized * 150f;
                    if (Vector3.Distance(ordinaryRemote, ordinaryDestination.transform.position) <= 100f ||
                        Vector3.Distance(ordinaryRemote, ordinaryDestination.transform.position) > 168f ||
                        Vector3.Distance(ordinaryRemote, busPosition) <= 210f)
                        return "current route cannot stage a remote 150 m from the ordinary destination and outside the bus radius";

                    CheckpointIndexField.SetValue(bus, ordinaryIndex);
                    presence[FakePeer] = (new PlayerPose(ordinaryRemote.x, ordinaryRemote.y, ordinaryRemote.z, 0, 0,
                        (byte)PlayerState.Walking), Time.unscaledTime);
                    var ordinaryVeto = InvokeNativeCheckpointCheck(bus, 2);
                    presence.Clear();
                    if (ordinaryVeto != null || (int)CheckpointIndexField.GetValue(bus) != ordinaryIndex)
                        return "fresh remote 150 m from the ordinary destination did not veto its native skip";

                    CheckpointIndexField.SetValue(bus, ordinaryIndex);
                    presence[FakePeer] = (new PlayerPose(ordinaryRemote.x, ordinaryRemote.y, ordinaryRemote.z, 0, 0,
                        (byte)PlayerState.Walking), Time.unscaledTime - 2.1f);
                    var ordinaryStale = InvokeNativeCheckpointCheck(bus, 2);
                    if (ordinaryStale != ordinaryDestination || (int)CheckpointIndexField.GetValue(bus) != ordinaryIndex)
                        return "stale remote presence incorrectly blocked the ordinary native checkpoint skip";

                    presence[FakePeer] = (new PlayerPose(ordinaryRemote.x, ordinaryRemote.y, ordinaryRemote.z, 0, 0,
                        (byte)PlayerState.Walking), Time.unscaledTime);
                    tickSystem.isFastForwardTime = true;
                    CheckpointIndexField.SetValue(bus, ordinaryIndex);
                    var accelerated = InvokeNativeCheckpointCheck(bus, 2);
                    if (accelerated != ordinaryDestination || (int)CheckpointIndexField.GetValue(bus) != ordinaryIndex)
                        return "accelerated-time ordinary checkpoint advancement was changed by the remote guard";

                    // This branch also prevents misclassifying an accelerated fall-through
                    // that happens to return the final checkpoint as the 100/100 shortcut.
                    var finalCheckpoint = checkpoints[checkpoints.Length - 1];
                    if (Vector3.Distance(busPosition, finalPosition) > 350f &&
                        Vector3.Distance(finalPosition, busPosition) > 210f)
                    {
                        int savedFinalTimeHere = finalCheckpoint.TimeHere;
                        try
                        {
                            finalCheckpoint.TimeHere = 0;
                            playerTransform.SetPositionAndRotation(finalPosition, savedPlayerRotation);
                            presence[FakePeer] = (new PlayerPose(finalPosition.x, finalPosition.y, finalPosition.z, 0, 0,
                                (byte)PlayerState.Walking), Time.unscaledTime);
                            tickSystem.isFastForwardTime = true;
                            CheckpointIndexField.SetValue(bus, checkpoints.Length - 1);
                            var acceleratedFinal = InvokeNativeCheckpointCheck(bus, 2);
                            if (acceleratedFinal != finalCheckpoint || (int)CheckpointIndexField.GetValue(bus) != checkpoints.Length - 1)
                                return "accelerated off-day fall-through at the final checkpoint was misclassified as the 100/100 shortcut";
                        }
                        finally { finalCheckpoint.TimeHere = savedFinalTimeHere; }
                    }
                }
                finally
                {
                    presence.Clear();
                    ordinaryDestination.TimeHere = savedTimeHere;
                    tickSystem.isFastForwardTime = savedFastForward;
                }
                return "";
            }
            catch (Exception ex)
            {
                return "native checkpoint assertion failed: " + ex.GetBaseException().Message;
            }
            finally
            {
                presence.Clear();
                foreach (var entry in savedPresence) presence[entry.Key] = entry.Value;
                CheckpointIndexField.SetValue(bus, savedIndex);
                dayFlags[day] = savedDayFlag;
                BusStateField.SetValue(bus, savedBusState);
                playerTransform.SetPositionAndRotation(savedPlayerPosition, savedPlayerRotation);
                tickSystem.isFastForwardTime = savedFastForward;
                Physics.SyncTransforms();
            }
        }
#endif


        private static AI_SchoolBus_Checkpoint? InvokeNativeCheckpointCheck(AI_SchoolBus bus, int tick)
        {
            try { return CheckIfTeleportationNeededMethod!.Invoke(bus, new object[] { tick }) as AI_SchoolBus_Checkpoint; }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        // The off-day shortcut above is independent of accelerated time by native design. For
        // ordinary route days the postfix keeps the game's accelerated-time override untouched.
        internal bool VetoRemoteCheckpointSkip(AI_SchoolBus controller, AI_SchoolBus_Checkpoint destination, int previousIndex)
        {
            if (!host || controller == null || destination == null || !byController.TryGetValue(controller, out var binding) ||
                binding.Vehicle == null || DayBusRunningField == null) return false;
            var dayFlags = DayBusRunningField.GetValue(controller) as int[];
            int day = Singleton<TickSystem>.i.Get_DayOfTheWeek;
            if (dayFlags == null || day < 0 || day >= dayFlags.Length) return false;

            var checkpoints = BusCheckpointsField?.GetValue(controller) as AI_SchoolBus_Checkpoint[];
            var localPlayer = Singleton<Gameplay>.i.Player;
            bool offDayShortcut = dayFlags[day] == 0 && checkpoints != null && checkpoints.Length != 0 &&
                ReferenceEquals(destination, checkpoints[checkpoints.Length - 1]) && localPlayer != null &&
                Vector3.Distance(localPlayer.transform.position, binding.Vehicle.transform.position) > 100f &&
                Vector3.Distance(localPlayer.transform.position, checkpoints[checkpoints.Length - 1].transform.position) > 100f;
            if (!offDayShortcut && Singleton<TickSystem>.i.Get_TimeIsAccelerated) return false;
            float busRadius = offDayShortcut ? 100f : 210f;
            float destinationRadius = offDayShortcut ? 100f : 168f;
            float now = Time.unscaledTime;
            Vector3 busPosition = binding.Vehicle.transform.position;
            Vector3 destinationPosition = destination.transform.position;
            foreach (var remote in presence.Values)
            {
                if (now - remote.Time > 2f) continue;
                Vector3 remotePosition = new Vector3(remote.Pose.X, remote.Pose.Y, remote.Pose.Z);
                if (Vector3.Distance(remotePosition, busPosition) <= busRadius ||
                    Vector3.Distance(remotePosition, destinationPosition) <= destinationRadius)
                {
                    CheckpointIndexField?.SetValue(controller, previousIndex);
                    return true;
                }
            }
            return false;
        }

#if MONMP_DEVELOPMENT
        internal bool TestRunningMatches()
        {
            var binding = bindings.Values.FirstOrDefault(b => b.Controller != null && b.Current != null);
            if (binding == null || !(RunningObjectsField?.GetValue(binding.Controller) is GameObject[] objects)) return false;
            bool running = binding.Current.BusState != (byte)AI_SchoolBus.SchoolBus_State.Sleeping;
            return objects.Where(o => o != null).All(o => o.activeSelf == running);
        }


        internal string TestRunningObjects()
        {
            var binding = bindings.Values.FirstOrDefault(b => b.Controller != null);
            if (binding == null) return "no bus";
            var objects = RunningObjectsField?.GetValue(binding.Controller) as GameObject[] ?? new GameObject[0];
            return "state " + binding.Controller.Get_Bus_State + (binding.Current == null ? "" : " replicated " + (AI_SchoolBus.SchoolBus_State)binding.Current.BusState) +
                ", Richard active " + (binding.Richard != null && binding.Richard.gameObject.activeInHierarchy) + ", running objects: " +
                string.Join(", ", objects.Where(o => o != null).Select(o => o.name + "=" + o.activeInHierarchy));
        }
#endif

        // #175: a guest's bus is a Remote NWH replica whose engine never starts, so its layered engine
        // sound (fed by NWH2_RES from the NWH engine) stayed off. Voice it from the replicated route
        // state and the replica's own movement: idle while stopped, revving with speed, gas while speeding up.
        internal bool RemoteEngine(VehicleController vehicle, out float rpm, out float speed, out bool gas)
        {
            rpm = 0f; speed = 0f; gas = false;
            if (!GuestAttached) return false;
            Binding? binding = null;
            foreach (var candidate in bindings.Values) if (candidate.Vehicle == vehicle) { binding = candidate; break; }
            if (binding?.Current == null) return false;
            float now = Time.time, dt = now - binding.EngineSpeedAt;
            var position = vehicle.transform.position;
            if (binding.EngineSpeedAt <= 0f || dt > 1f) binding.EngineSpeed = 0f;
            else if (dt > 0.0001f)
            {
                float measured = (position - binding.EnginePosition).magnitude / dt * 3.6f;
                float previous = binding.EngineSpeed;
                binding.EngineSpeed = Mathf.Lerp(previous, Mathf.Min(measured, 120f), Mathf.Clamp01(dt * 4f));
                gas = binding.EngineSpeed > previous + 0.02f || binding.EngineSpeed > 15f;
            }
            binding.EnginePosition = position; binding.EngineSpeedAt = now;
            if (binding.Current.BusState == (byte)AI_SchoolBus.SchoolBus_State.Sleeping) return true;
            speed = binding.EngineSpeed;
            // Idle near 800 rpm; a school bus in a middle gear sits around 1500-2500 on the road.
            rpm = 800f + Mathf.Repeat(speed, 30f) / 30f * 1700f + (speed > 1f ? 300f : 0f);
            return true;
        }

        // The guest's bus replica, which SharedVehicles does not bind: it still needs the remote tire,
        // bump, horn and reverse-beep sounds a bound car gets (Astra R15).
        internal bool IsReplica(VehicleController vehicle)
        {
            if (!GuestAttached) return false;
            foreach (var binding in bindings.Values) if (binding.Vehicle == vehicle) return true;
            return false;
        }

        internal bool TryGetCrashTarget(VehicleController vehicle, out ulong target)
        {
            target = 0;
            if (!host || disposed || session?.IsRunning != true || channel?.Ready != true ||
                vehicle == null || !vehicle.enabled || !vehicle.gameObject.activeInHierarchy ||
                vehicle.VehicleMultiplayerInstanceType != Vehicle.MultiplayerInstanceType.Local ||
                vehicle.soundManager?.crashComponent?.Initialized != true) return false;
            foreach (var binding in bindings.Values)
            {
                if (binding.Vehicle != vehicle || binding.Current == null) continue;
                target = binding.Id;
                return true;
            }
            return false;
        }

        internal bool TryPlayCrash(PresentationEvent cue)
        {
            if (disposed || session?.IsRunning != true || channel?.Ready != true ||
                !bindings.TryGetValue(cue.Target, out var binding)) return false;
            if (binding.Current != null && binding.Vehicle != null && binding.Vehicle.enabled &&
                binding.Vehicle.gameObject.activeInHierarchy)
                RemoteVehicleSounds.Crash(binding.Vehicle, new Vector3(cue.X, cue.Y, cue.Z), Mathf.Clamp(cue.Value, 0f, 200f));
            return true;
        }

        internal VehicleController? CrashVehicleFor(ulong target) =>
            !disposed && session?.IsRunning == true && channel?.Ready == true &&
            bindings.TryGetValue(target, out var binding) && binding.Current != null &&
            binding.Vehicle != null && binding.Vehicle.enabled && binding.Vehicle.gameObject.activeInHierarchy
                ? binding.Vehicle : null;

        // The bus's native simulation belongs to the host, including when a guest is aboard.
        internal bool AcceptCrashCue(uint peer, PresentationEvent cue) => false;

        // The replica's presented velocity: its kinematic body reads zero, so the wheels' dust never
        // started while the bus drove its route (#177, as #250 for shared vehicles).
        internal bool TryPresentedVelocity(Vehicle vehicle, out Vector3 velocity)
        {
            velocity = default;
            if (!GuestAttached) return false;
            foreach (var binding in bindings.Values)
            {
                if (binding.Vehicle != vehicle) continue;
                if (!binding.HasPresentationPose || binding.Current == null || Time.unscaledTime - binding.PoseReceivedAt > 1f) return false;
                velocity = PresentedVelocity(binding);
                return true;
            }
            return false;
        }

        private static Vector3 PresentedVelocity(Binding binding)
        {
            if (!binding.HasPresentationPose || binding.Current == null || Time.unscaledTime - binding.PoseReceivedAt > 1f)
                return Vector3.zero;
            var velocity = (Position(binding.VehicleTo) - Position(binding.VehicleFrom)) / CaptureSeconds;
            return velocity.sqrMagnitude > 60f * 60f ? Vector3.zero : velocity;
        }

        internal bool RemoteInZone(AI_SchoolBus controller) =>
            host && byController.TryGetValue(controller, out var binding) && binding.InZone.Count > 0;
        private static Collider[] FindZones(AI_SchoolBus controller)
        {
            var zones = new List<Collider>();
            foreach (var detector in Resources.FindObjectsOfTypeAll<PlayerDetector>())
            {
                if (detector == null || !detector.gameObject.scene.IsValid()) continue;
                var enter = detector.Get_PlayerIn;
                bool ours = false;
                for (int i = 0; enter != null && i < enter.GetPersistentEventCount() && !ours; i++)
                    ours = enter.GetPersistentTarget(i) == controller && enter.GetPersistentMethodName(i) == "PlayerEnteredDetectionZone";
                if (ours) zones.AddRange(detector.GetComponents<Collider>().Where(collider => collider.isTrigger));
            }
            return zones.ToArray();
        }
        private static readonly float[] BodyHeights = { 0.2f, 0.9f, 1.6f }; // feet, hip, head
        private static bool InStopArea(Binding binding, Vector3 feet)
        {
            if (InZone(binding.Zones ?? Array.Empty<Collider>(), feet)) return true;
            FindCabin(binding);
            if (binding.Cabin == null || binding.Cabin.sharedMesh == null || !binding.Cabin.gameObject.activeInHierarchy) return false;
            var bounds = binding.Cabin.sharedMesh.bounds;
            // Test the walker's centre inside the cabin, inset from its outer walls. A player's
            // head/feet can cross the skin without someone standing beside/below it counting.
            bounds.size = Vector3.Scale(bounds.size, new Vector3(0.96f, 1f, 0.98f));
            return bounds.Contains(binding.Cabin.transform.InverseTransformPoint(feet + Vector3.up * 0.9f));
        }

        private static void FindCabin(Binding binding)
        {
            if (!binding.CabinChecked)
            {
                binding.CabinChecked = true;
                // Same audited upholstery/floor mesh used by the surface highlights. Its local
                // bounds follow bus motion/rotation and exclude the exterior hood and wheels.
                // Seat glow retains the original first material but adds partition materials.
                var interiors = binding.Vehicle.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.name == "interior" &&
                    r.sharedMaterial != null &&
                    r.sharedMaterial.name.StartsWith("SchoolBus_interior", StringComparison.Ordinal)).ToArray();
                binding.Cabin = interiors.Length == 1 ? interiors[0].GetComponent<MeshFilter>() : null;
            }
        }

        private static bool InZone(Collider[] zones, Vector3 feet)
        {
            foreach (var zone in zones)
            {
                if (zone == null || !zone.enabled || !zone.gameObject.activeInHierarchy) continue;
                foreach (float height in BodyHeights)
                {
                    var point = feet + Vector3.up * height;
                    if ((zone.ClosestPoint(point) - point).sqrMagnitude < 0.09f) return true;
                }
            }
            return false;
        }
        // The bus's stop zones and whether a walker standing at each zone's center registers.

#if MONMP_DEVELOPMENT
        internal string TestZones()
        {
            var binding = bindings.Values.FirstOrDefault(b => b.Controller != null);
            if (binding == null) return "no bus";
            var zones = binding.Zones ??= FindZones(binding.Controller);
            return zones.Length + " zone(s): " + string.Join("; ", zones.Select(zone =>
            {
                var center = zone.bounds.center;
                bool active = zone.enabled && zone.gameObject.activeInHierarchy;
                return zone.name + " size " + zone.bounds.size.ToString("F1") + " active " + active +
                    (active ? " detects " + InZone(new[] { zone }, center - Vector3.up * 0.9f) : "");
            }));
        }


        internal int TestZoneCount => bindings.Values.Where(b => b.Controller != null).Select(b => (b.Zones ??= FindZones(b.Controller)).Length).DefaultIfEmpty(0).Max();
#endif


        [MonMP.Core.World.WorldCommandAdapters(SchoolBusState.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (host && payload != null && payload.Length == 3 && payload[0] == 0x53 && payload[1] == 0x42 && payload[2] >= StopButton && payload[2] <= ExitZone &&
                bindings.TryGetValue(entity, out var controlled))
            {
                RemoteControl(peer, controlled, payload[2]);
                return;
            }
#if MONMP_DEVELOPMENT
            if (!host || payload == null || payload.Length != 3 || payload[0] != 0x53 || payload[1] != 0x42 || payload[2] != 1 ||
                !bindings.TryGetValue(entity, out var binding) || !binding.FixturePoseStaged) return;
            fixturePoseAcknowledgments.Add(peer);

#endif
        }

#if MONMP_DEVELOPMENT
        internal void TestAcknowledgePose(ulong entity)
        {
            if (!host && GuestAttached && channel?.Ready == true && bindings.ContainsKey(entity))
                channel.Request(entity, new byte[] { 0x53, 0x42, 1 });
        }
#endif

        private void Apply(WorldRecord record, bool snap)
        {
            if (record.Adapter != SchoolBusState.Adapter || !bindings.TryGetValue(record.Id, out var binding))
                throw new InvalidDataException("Unknown school bus identity.");
            var state = SchoolBusState.Decode(record.Payload);
            if (state.MasterVisualCount != binding.MasterVisuals.Length)
                throw new InvalidDataException("School bus master-visual layout differs from the host.");

            binding.Current = state; receivedPoseUpdates++;
            // The native route start (EnableVehicle) shows the driver and running props, and PutToSleep
            // hides them. The guest's bus AI never runs either, so its driver was never shown (#135).
            ShowRunning(binding.Controller, state.BusState != (byte)AI_SchoolBus.SchoolBus_State.Sleeping);
            // The replica's lights module never runs: the host's lamps, blinkers included (#177).
            int lampKey = state.Lamps | state.LampControls << 16;
            if (!host && lampKey != binding.AppliedLamps && binding.Vehicle.effectsManager?.lightsManager != null)
            {
                try
                {
                    NativeVehicleLighting.Apply(binding.Vehicle, new VehicleLightingState(state.Lamps, state.LampControls, 0, 0,
                        (byte)Mathf.Clamp(binding.Vehicle.InteriorLightMode, 0, 2)));
                    binding.AppliedLamps = lampKey;
                }
                catch (Exception ex) { Debug.LogWarning("MonMP school bus lamps failed: " + ex.Message); binding.AppliedLamps = lampKey; }
            }
            // Keep the visual controller enabled in Remote/kinematic mode, including when this
            // copy joined with an asleep bus. The host route controls only its awake/FX lifecycle.
            RemoteSchoolBusPresentation.Apply(binding.Vehicle,
                state.VehicleEnabled && state.BusState != (byte)AI_SchoolBus.SchoolBus_State.Sleeping);
            if (binding.Animator != null)
            {
                binding.Animator.enabled = state.RichardAnimatorEnabled;
                SetAnimatorBool(binding.Animator, "isWalking", state.RichardWalking);
                SetAnimatorBool(binding.Animator, "isDriving", state.RichardDriving);
                SetAnimatorBool(binding.Animator, "isSitting", state.RichardSitting);
            }

            if (snap || !binding.HasPresentationPose)
            {
                binding.VehicleFrom = binding.VehicleTo = state.VehiclePose;
                binding.RichardFrom = binding.RichardTo = state.RichardPose;
                binding.VisualFrom = binding.VisualTo = ReadVisuals(state);
                binding.PoseReceivedAt = Time.unscaledTime;
                binding.HasPresentationPose = true;
                Present(binding, 1, true);
            }
            else
            {
                binding.VehicleFrom = Pose(binding.Vehicle.transform, false);
                binding.RichardFrom = Pose(binding.Richard.transform, binding.RichardIsChild);
                binding.VehicleTo = state.VehiclePose; binding.RichardTo = state.RichardPose;
                binding.VisualFrom = binding.MasterVisuals.Select(value =>
                {
                    var euler = value.transform.localEulerAngles;
                    return SchoolBusVisualPose.Normalized(euler.x, euler.y, euler.z);
                }).ToArray();
                binding.VisualTo = ReadVisuals(state);
                binding.PoseReceivedAt = Time.unscaledTime;
                binding.HasPresentationPose = true;
            }
        }

        internal void Update()
        {
            if (disposed) return;
            if (host && session?.IsRunning == true && channel?.Ready == true) UpdateRemoteZones();
            if (host && session?.IsRunning == true && channel != null && Time.unscaledTime >= nextCapture)
            {
                nextCapture = Time.unscaledTime + CaptureSeconds;
                foreach (var binding in bindings.Values)
                {
                    if (binding.Controller == null || binding.Vehicle == null || binding.Richard == null || !binding.Controller.gameObject.scene.IsValid()) continue;
                    try
                    {
                        var live = Capture(binding);
#if MONMP_DEVELOPMENT
                        binding.Current = binding.FixturePoseStaged ? MakeFixturePose(binding, live) : live;
#endif
#if !MONMP_DEVELOPMENT
                        binding.Current = live;
#endif
                        channel.Publish(binding.Id, binding.Current.Encode());
                    }
                    catch (Exception ex)
                    {
                        if (!binding.CaptureFailureLogged)
                        {

#if MONMP_DEVELOPMENT
                            File.AppendAllText(Path.Combine(diagnosticsDirectory, "shared-school-bus-errors.txt"), binding.Identity + "\t" + ex.Message + Environment.NewLine);
#endif

                            Debug.LogWarning("MonMP native capture failed: " + ex.Message);
                            binding.CaptureFailureLogged = true;
                        }
                    }
                }
            }
            if (!host && GuestAttached)
            {
                foreach (var binding in bindings.Values)
                {
                    if (!binding.HasPresentationPose || binding.Vehicle == null || binding.Richard == null) continue;
                    RemoteSchoolBusPresentation.Apply(binding.Vehicle,
                        binding.Current.VehicleEnabled && binding.Current.BusState != (byte)AI_SchoolBus.SchoolBus_State.Sleeping);
                    float amount = Mathf.Clamp01((Time.unscaledTime - binding.PoseReceivedAt) / RemotePoseInterpolationSeconds);
                    if (amount > 0 && amount < 1)
                    {
                        bool visualMoving = !SameVisuals(binding.VisualFrom, binding.VisualTo);
                        if (visualMoving) visualInterpolationFrames++;
                        if (visualMoving || !SamePose(binding.VehicleFrom, binding.VehicleTo) || !SamePose(binding.RichardFrom, binding.RichardTo)) interpolationFrames++;
                    }
                    Present(binding, amount);
                }
            }
        }

        private static void SetRemoteMode(Binding binding)
        {
            var vehicle = binding.Vehicle;
            if (vehicle.VehicleMultiplayerInstanceType != Vehicle.MultiplayerInstanceType.Remote)
                vehicle.SetMultiplayerInstanceType(Vehicle.MultiplayerInstanceType.Remote, true);
            vehicle.input.autoSetInput = false;
            vehicle.freezeWhileIdle = false; vehicle.freezeWhileAsleep = false;
            vehicle.vehicleRigidbody.constraints = RigidbodyConstraints.None;
            RemoteSchoolBusPresentation.Apply(vehicle,
                binding.Current.VehicleEnabled && binding.Current.BusState != (byte)AI_SchoolBus.SchoolBus_State.Sleeping);
        }

        private static void Present(Binding binding, float amount, bool snapped = false)
        {
            ItemPose vehiclePose = SchoolBusPoseInterpolation.Lerp(binding.VehicleFrom, binding.VehicleTo, amount);
            binding.Vehicle.transform.SetPositionAndRotation(Position(vehiclePose), Rotation(vehiclePose));
            var body = binding.Vehicle.vehicleRigidbody;
            if (body != null && body.isKinematic)
            { body.position = Position(vehiclePose); body.rotation = Rotation(vehiclePose); }
            binding.WheelPoseResetPending |= snapped;
            if (RemoteSchoolBusPresentation.PresentWheels(binding.Vehicle, PresentedVelocity(binding), Time.deltaTime, binding.WheelPoseResetPending))
                binding.WheelPoseResetPending = false;

            ItemPose richardPose = SchoolBusPoseInterpolation.Lerp(binding.RichardFrom, binding.RichardTo, amount);
            if (binding.RichardIsChild) binding.Richard.transform.localPosition = Position(richardPose);
            else binding.Richard.transform.position = Position(richardPose);
            if (binding.RichardIsChild) binding.Richard.transform.localRotation = Rotation(richardPose);
            else binding.Richard.transform.rotation = Rotation(richardPose);

            for (int i = 0; i < binding.MasterVisuals.Length; i++)
            {
                var from = binding.VisualFrom[i]; var to = binding.VisualTo[i];
                var euler = new Vector3(SchoolBusPoseInterpolation.LerpAngle(from.EulerX, to.EulerX, amount),
                    SchoolBusPoseInterpolation.LerpAngle(from.EulerY, to.EulerY, amount),
                    SchoolBusPoseInterpolation.LerpAngle(from.EulerZ, to.EulerZ, amount));
                binding.MasterVisuals[i].transform.localRotation = Quaternion.Euler(euler);
            }
        }

        private static bool SamePose(ItemPose left, ItemPose right) => left.X == right.X && left.Y == right.Y && left.Z == right.Z &&
            left.Qx == right.Qx && left.Qy == right.Qy && left.Qz == right.Qz && left.Qw == right.Qw;
        private static bool SameVisuals(SchoolBusVisualPose[] left, SchoolBusVisualPose[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
                if (!left[i].Equals(right[i])) return false;
            return true;
        }

        private static void Restore(Binding binding)
        {
            var vehicle = binding.Vehicle;
            if (vehicle == null) return;
            if (vehicle.VehicleMultiplayerInstanceType != binding.OriginalMode)
                vehicle.SetMultiplayerInstanceType(binding.OriginalMode, binding.OriginalBodyKinematic);
            vehicle.input.autoSetInput = binding.OriginalAutoInput;
            vehicle.freezeWhileIdle = binding.OriginalFreezeIdle; vehicle.freezeWhileAsleep = binding.OriginalFreezeSleep;
            int wheelCount = Math.Min(vehicle.Wheels.Count, binding.OriginalExternalWheels.Length);
            for (int i = 0; i < wheelCount; i++)
                if (binding.OriginalExternalWheels[i]) vehicle.Wheels[i].wheelController.UseExternalUpdate();
                else vehicle.Wheels[i].wheelController.UseInternalUpdate();
            for (int i = 0; i < Math.Min(vehicle.Wheels.Count, binding.OriginalWheelVisualModes.Length); i++)
                vehicle.Wheels[i].wheelController.visualOnlyUpdate = binding.OriginalWheelVisualModes[i];
            var body = vehicle.vehicleRigidbody;
            if (body != null)
            {
                body.constraints = binding.OriginalConstraints;
                body.collisionDetectionMode = binding.OriginalCollision;
                body.isKinematic = binding.OriginalBodyKinematic;
                body.velocity = binding.OriginalVelocity;
                body.angularVelocity = binding.OriginalAngularVelocity;
                body.position = Position(binding.Original.VehiclePose);
                body.rotation = Rotation(binding.Original.VehiclePose);
            }
            vehicle.transform.SetPositionAndRotation(Position(binding.Original.VehiclePose), Rotation(binding.Original.VehiclePose));
            RemoteSchoolBusPresentation.Restore(vehicle, binding.OriginalAwake, binding.OriginalVehicleEnabled);
            if (binding.Richard != null)
            {
                if (binding.RichardIsChild)
                {
                    binding.Richard.transform.localPosition = Position(binding.Original.RichardPose);
                    binding.Richard.transform.localRotation = Rotation(binding.Original.RichardPose);
                }
                else binding.Richard.transform.SetPositionAndRotation(Position(binding.Original.RichardPose), Rotation(binding.Original.RichardPose));
                if (binding.Animator != null)
                {
                    binding.Animator.enabled = binding.Original.RichardAnimatorEnabled;
                    SetAnimatorBool(binding.Animator, "isWalking", binding.Original.RichardWalking);
                    SetAnimatorBool(binding.Animator, "isDriving", binding.Original.RichardDriving);
                    SetAnimatorBool(binding.Animator, "isSitting", binding.Original.RichardSitting);
                }
            }
            for (int i = 0; i < binding.MasterVisuals.Length && i < binding.Original.MasterVisualCount; i++)
            {
                var visual = binding.Original.MasterVisual(i);
                binding.MasterVisuals[i].transform.localEulerAngles = new Vector3(visual.EulerX, visual.EulerY, visual.EulerZ);
            }
        }


#if MONMP_DEVELOPMENT
        internal bool TestStagePose(ulong id)
        {
            if (!host || !bindings.TryGetValue(id, out var binding) || binding.FixturePoseStaged) return false;
            if (binding.MasterVisuals.Length == 0) return false;
            var state = Capture(binding);
            binding.FixtureVehiclePose = state.VehiclePose; binding.FixtureRichardPose = state.RichardPose;
            binding.FixtureVisualPose = ReadVisuals(state);
            binding.FixtureRendezvousPose = false;
            binding.FixturePoseStaged = true;
            binding.Current = MakeFixturePose(binding, state);
            fixturePoseAcknowledgments.Clear();
            return true;
        }
#endif


        // The passenger acceptance fixture uses a small reversible marker to move the guest
        // toward the host's real seat anchor. Hold the AI bus still until that echo completes.

#if MONMP_DEVELOPMENT
        internal bool TestHoldNativePose(ulong id)
        {
            if (!host || !bindings.TryGetValue(id, out var binding) || binding.FixtureNativePoseHeld ||
                binding.Vehicle.vehicleRigidbody == null) return false;
            var body = binding.Vehicle.vehicleRigidbody;
            binding.FixtureHeldState = Capture(binding);
            binding.FixtureHeldControllerEnabled = binding.Controller.enabled;
            binding.FixtureHeldAutoInput = binding.Vehicle.input.autoSetInput;
            binding.FixtureHeldFreezeIdle = binding.Vehicle.freezeWhileIdle;
            binding.FixtureHeldFreezeSleep = binding.Vehicle.freezeWhileAsleep;
            binding.FixtureHeldBodyKinematic = body.isKinematic;
            binding.FixtureHeldConstraints = body.constraints;
            binding.FixtureHeldCollision = body.collisionDetectionMode;
            binding.FixtureHeldVelocity = body.velocity;
            binding.FixtureHeldAngularVelocity = body.angularVelocity;

            binding.Controller.enabled = false;
            binding.Vehicle.input.autoSetInput = false;
            binding.Vehicle.freezeWhileIdle = false;
            binding.Vehicle.freezeWhileAsleep = false;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.constraints = RigidbodyConstraints.FreezeAll;
            body.isKinematic = true;
            Vector3 originalPosition = Position(binding.FixtureHeldState.VehiclePose);
            Vector3 rendezvousOffset = Vector3.right * 2.5f;
            body.position = originalPosition + rendezvousOffset;
            body.rotation = Rotation(binding.FixtureHeldState.VehiclePose);
            binding.Vehicle.transform.SetPositionAndRotation(body.position, body.rotation);
            if (!binding.RichardIsChild) binding.Richard.transform.position += rendezvousOffset;
            binding.FixtureNativePoseHeld = true;
            binding.Current = Capture(binding);
            return true;
        }


        internal bool TestStageRendezvousPose(ulong id)
        {
            if (!host || !bindings.TryGetValue(id, out var binding) || !binding.FixtureNativePoseHeld ||
                binding.FixturePoseStaged || binding.MasterVisuals.Length == 0) return false;
            var state = Capture(binding);
            binding.FixtureVehiclePose = state.VehiclePose; binding.FixtureRichardPose = state.RichardPose;
            binding.FixtureVisualPose = ReadVisuals(state);
            binding.FixtureRendezvousPose = true;
            binding.FixturePoseStaged = true;
            binding.Current = MakeFixturePose(binding, state);
            fixturePoseAcknowledgments.Clear();
            return true;
        }


        internal bool TestReleaseNativePose(ulong id)
        {
            if (!host || !bindings.TryGetValue(id, out var binding) || !binding.FixtureNativePoseHeld || binding.FixtureHeldState == null)
                return false;
            if (binding.FixturePoseStaged) TestRestoreStagedPose(id);
            var held = binding.FixtureHeldState;
            var body = binding.Vehicle.vehicleRigidbody;
            if (body != null)
            {
                body.position = Position(held.VehiclePose);
                body.rotation = Rotation(held.VehiclePose);
                body.constraints = binding.FixtureHeldConstraints;
                body.collisionDetectionMode = binding.FixtureHeldCollision;
                body.isKinematic = binding.FixtureHeldBodyKinematic;
                body.velocity = binding.FixtureHeldVelocity;
                body.angularVelocity = binding.FixtureHeldAngularVelocity;
            }
            binding.Vehicle.transform.SetPositionAndRotation(Position(held.VehiclePose), Rotation(held.VehiclePose));
            if (binding.RichardIsChild)
            {
                binding.Richard.transform.localPosition = Position(held.RichardPose);
                binding.Richard.transform.localRotation = Rotation(held.RichardPose);
            }
            else binding.Richard.transform.SetPositionAndRotation(Position(held.RichardPose), Rotation(held.RichardPose));
            if (binding.Animator != null)
            {
                binding.Animator.enabled = held.RichardAnimatorEnabled;
                SetAnimatorBool(binding.Animator, "isWalking", held.RichardWalking);
                SetAnimatorBool(binding.Animator, "isDriving", held.RichardDriving);
                SetAnimatorBool(binding.Animator, "isSitting", held.RichardSitting);
            }
            for (int i = 0; i < binding.MasterVisuals.Length && i < held.MasterVisualCount; i++)
            {
                var visual = held.MasterVisual(i);
                binding.MasterVisuals[i].transform.localEulerAngles = new Vector3(visual.EulerX, visual.EulerY, visual.EulerZ);
            }
            binding.Vehicle.input.autoSetInput = binding.FixtureHeldAutoInput;
            binding.Vehicle.freezeWhileIdle = binding.FixtureHeldFreezeIdle;
            binding.Vehicle.freezeWhileAsleep = binding.FixtureHeldFreezeSleep;
            binding.Controller.enabled = binding.FixtureHeldControllerEnabled;
            binding.FixtureNativePoseHeld = false;
            binding.FixtureRendezvousPose = false;
            binding.Current = Capture(binding);
            binding.FixtureHeldState = null;
            return true;
        }


        internal bool TestRestoreStagedPose(ulong id)
        {
            if (!host || !bindings.TryGetValue(id, out var binding) || !binding.FixturePoseStaged) return false;
            binding.FixturePoseStaged = false;
            binding.FixtureRendezvousPose = false;
            binding.Current = Capture(binding);
            return true;
        }
#endif


        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            presence.Clear();
            if (!host) foreach (var binding in bindings.Values) Restore(binding);
            else
            {
    #if MONMP_DEVELOPMENT
            foreach (var binding in bindings.Values.Where(value => value.FixturePoseStaged).ToArray()) TestRestoreStagedPose(binding.Id);
            foreach (var binding in bindings.Values.Where(value => value.FixtureNativePoseHeld).ToArray()) TestReleaseNativePose(binding.Id);
#endif
            }
            session = null; channel = null;
        }
    }
}
