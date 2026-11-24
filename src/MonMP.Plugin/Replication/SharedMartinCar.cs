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
using NWH.WheelController3D;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The native AI_MartinCar owns the routed car. Only its host runs that AI;
    // guests receive a session-only pose, wheel-spin sample and authored run visuals.
    internal sealed partial class SharedMartinCar : IDisposable
    {
        private static readonly FieldInfo VehicleField = AccessTools.Field(typeof(AI_MartinCar), "vc")!;
        private static readonly FieldInfo RunningVisualsField = AccessTools.Field(typeof(AI_MartinCar), "ToEnableWhenRunning")!;
        private static readonly FieldInfo OriginalPositionField = AccessTools.Field(typeof(AI_MartinCar), "OriginalPosition")!;
        private static readonly FieldInfo CarStateField = AccessTools.Field(typeof(AI_MartinCar), "Car_State")!;
        private static readonly MethodInfo TickMethod = AccessTools.Method(typeof(AI_MartinCar), "Tick", new[] { typeof(int) })!;
        private static readonly FieldInfo HomeDoorCoroutineField = AccessTools.Field(typeof(AI_MartinCar_HomeDoor), "Coroutine")!;

        private sealed class Binding
        {
            internal ulong Id;
            internal string Identity = "";
            internal AI_MartinCar Controller = null!;
            internal VehicleController Vehicle = null!;
            internal GameObject[] RunningVisuals = Array.Empty<GameObject>();
            internal WheelController[] Wheels = Array.Empty<WheelController>();
            internal bool[] OriginalWheelExternal = Array.Empty<bool>();
            internal bool[] OriginalWheelVisualOnly = Array.Empty<bool>();
            internal float[] OriginalWheelAngularVelocity = Array.Empty<float>();
            internal bool[] OriginalRunningVisuals = Array.Empty<bool>();
            internal bool OriginalAiEnabled;
            internal bool OriginalVehicleEnabled;
            internal bool OriginalAutoInput, OriginalFreezeIdle, OriginalFreezeSleep;
            internal Vehicle.MultiplayerInstanceType OriginalMode;
            internal bool OriginalBodyKinematic;
            internal RigidbodyConstraints OriginalConstraints;
            internal CollisionDetectionMode OriginalCollision;
            internal Vector3 OriginalVelocity, OriginalAngularVelocity;
            internal ItemPose OriginalVehiclePose;
            internal MartinCarState Original = null!, Current = null!;
            internal ItemPose PoseFrom, PoseTo;
            internal VehicleWheelSpin WheelSpinFrom, WheelSpinTo;
            internal bool HasPresentationPose;
            internal float PoseReceivedAt;
            internal Vector3 PassengerVelocity;
            internal bool CaptureFailureLogged;

#if MONMP_DEVELOPMENT
            internal bool FixturePoseStaged;


            internal MartinCarState FixtureState = null!;


            internal ItemPose FixtureOriginPose;
#endif

            internal bool RemotePassenger;
            internal Transform? PassengerSeat;
            internal Vehicle_PassengerSeat NativePassengerSeat = null!;
            internal SeatKey PassengerKey;
            internal VehiclePassengerSeatState Passenger = new VehiclePassengerSeatState(SeatOccupancy.Unoccupied, 1);
            internal bool EnterPending, CancelEntry, ExitPending;
            internal uint RequestedEnterGeneration;
            internal float NextExitRetry;
            internal int EnteredFrame = -1;
        }

        private readonly bool host;
        private readonly string diagnosticsDirectory;
        private readonly SharedVehicles vehicles;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<AI_MartinCar, Binding> byController = new Dictionary<AI_MartinCar, Binding>();
        internal IEnumerable<(ulong Id, Transform Frame)> CarrierFrames()
        {
            foreach (var binding in bindings.Values)
                if (!disposed && binding.Vehicle != null) yield return (binding.Id, binding.Vehicle.transform);
        }
        private readonly Dictionary<WheelController, (Binding Binding, int Index)> byWheel = new Dictionary<WheelController, (Binding, int)>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, uint> latestDoorKnockNonce = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, float> lastDoorKnockRequestAt = new Dictionary<uint, float>();

#if MONMP_DEVELOPMENT
        private readonly HashSet<uint> fixtureReadyPeers = new HashSet<uint>();


        private readonly HashSet<uint> fixturePoseAcknowledgments = new HashSet<uint>();
#endif

        private AI_MartinCar_HomeDoor? homeDoor;
        private Binding? homeDoorBinding;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool disposed;
        private float nextCapture;
        private int receivedPoseUpdates;
        private int interpolationFrames;
        private int wheelInterpolationFrames;
        private int suppressedNativeCalls;

#if MONMP_DEVELOPMENT
        private bool fixtureReadyRequested;


        private float nextFixtureReadyRequestAt;
#endif

        private uint nextDoorKnockNonce;
        private float nextGuestDoorKnockAt;
        private const float DoorKnockCommandRange = 4.5f;
        private const float DoorKnockPoseFreshnessSeconds = 2f;
        private const float RemoteSleepPoseFreshnessSeconds = 2f;
        private const float NativeSleepRange = 250f;
        private const float DoorKnockPeerIntervalSeconds = 2f;
        private const float GuestDoorKnockIntervalSeconds = 1.25f;
        private const float CaptureSeconds = 0.1f;
        private const float RemotePoseInterpolationSeconds = 0.15f;

        internal int BoundCount => bindings.Count;
        internal bool GuestAttached => !host && !disposed && session?.IsRunning == true;
        internal bool TryPresentedVelocity(Vehicle vehicle, out Vector3 velocity)
        {
            velocity = default;
            if (!GuestAttached || channel?.Ready != true) return false;
            foreach (var binding in bindings.Values)
            {
                if (binding.Vehicle != vehicle) continue;
                if (!binding.HasPresentationPose || binding.Current == null || Time.unscaledTime - binding.PoseReceivedAt > 1f) return false;
                velocity = binding.PassengerVelocity;
                return true;
            }
            return false;
        }
        internal bool GuestSimulationSuppressed => GuestAttached && bindings.Count != 0 && bindings.Values.All(value => !value.Controller.enabled);

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

        // Martin's routed car is host simulated; its passenger never gains vehicle authority.
        internal bool AcceptCrashCue(uint peer, PresentationEvent cue) => false;

        internal int ReceivedPoseUpdates => receivedPoseUpdates;
        internal int InterpolationFrames => interpolationFrames;
        internal int WheelInterpolationFrames => wheelInterpolationFrames;
        internal int SuppressedNativeCalls => suppressedNativeCalls;

#if MONMP_DEVELOPMENT
        internal int TestDoorKnockRequests { get; private set; }


        internal int TestPhoneCalls { get; private set; }
#endif

        private const byte PhoneCallCommand = 2;
        private float nextGuestPhoneCallAt;
        private readonly Dictionary<uint, float> lastPhoneCallAt = new Dictionary<uint, float>();

#if MONMP_DEVELOPMENT
        internal int TestDoorKnockAccepted { get; private set; }


        internal int TestDoorKnockRejected { get; private set; }


        internal int TestDoorKnockNativeCalls { get; private set; }


        internal string TestDoorKnockLastResult { get; private set; } = "none";


        internal AI_MartinCar_HomeDoor? TestHomeDoor => homeDoor;


        internal ulong TestEntity => bindings.Values.OrderBy(binding => binding.Identity, StringComparer.Ordinal).Select(binding => binding.Id).FirstOrDefault();


        internal MartinCarState? TestState(ulong entity) => bindings.TryGetValue(entity, out var binding) ? binding.Current : null;


        internal Vector3? TestVehiclePosition(ulong entity) => bindings.TryGetValue(entity, out var binding) && binding.Vehicle != null
            ? binding.Vehicle.transform.position : (Vector3?)null;


        internal Vector3? TestOriginalVehiclePosition(ulong entity) => bindings.TryGetValue(entity, out var binding)
            ? Position(binding.Original.VehiclePose) : (Vector3?)null;


        internal bool TestHasStagedPose(ulong entity) => bindings.TryGetValue(entity, out var binding) && binding.FixturePoseStaged;


        internal Vector3? TestFixtureOriginalPosition(ulong entity) => bindings.TryGetValue(entity, out var binding) && binding.FixturePoseStaged
            ? Position(binding.FixtureOriginPose) : (Vector3?)null;


        internal int TestPoseAcknowledgmentCount => fixturePoseAcknowledgments.Count;


        internal int TestReadyPeerCount => fixtureReadyPeers.Count;


        internal int TestExpectedGuestCount => session?.IsRunning == true ? Math.Max(0, session.Peers.Count - 1) : 0;


        internal bool TestAllGuestsReady => TestExpectedGuestCount > 0 && fixtureReadyPeers.Count >= TestExpectedGuestCount;


        internal bool TestChannelReady => channel?.Ready == true;


        internal bool TestReplicaMatchesHost(ulong entity, float tolerance)
        {
            if (host || !bindings.TryGetValue(entity, out var binding) || binding.Current == null || binding.Vehicle == null)
                return false;
            if (Vector3.Distance(binding.Vehicle.transform.position, Position(binding.Current.VehiclePose)) > tolerance ||
                Quaternion.Angle(binding.Vehicle.transform.rotation, Rotation(binding.Current.VehiclePose)) > 2f ||
                binding.Vehicle.vehicleRigidbody == null || !binding.Vehicle.vehicleRigidbody.isKinematic ||
                binding.Vehicle.VehicleMultiplayerInstanceType != Vehicle.MultiplayerInstanceType.Remote ||
                binding.Controller.Get_Car_State != (AI_MartinCar.MartinCar_State)binding.Current.CarState)
                return false;
            return RunningVisualMask(binding) == binding.Current.RunningVisualMask;
        }
#endif


        internal SharedMartinCar(bool host, string diagnostics, SharedVehicles vehicles, NativeEntityRegistry registry)
        {
            this.host = host; diagnosticsDirectory = diagnostics; this.vehicles = vehicles;
            var rows = new List<string>();
            var identities = new IdentityRegistry();
            var candidates = UnityEngine.Object.FindObjectsOfType<AI_MartinCar>(true)
                .Where(value => value != null && value.gameObject.scene.IsValid())
                .OrderBy(value => SharedBuildingDoors.StablePath(value.transform), StringComparer.Ordinal).ToArray();
            foreach (var controller in candidates)
            {
                string controllerPath = SharedBuildingDoors.StablePath(controller.transform);
                string vehiclePath = "-", disposition = "bound", identity = controllerPath + "/martin-car-pose-v1";
                int wheelCount = -1, visualCount = -1;
                try
                {
                    var vehicle = VehicleField?.GetValue(controller) as VehicleController;
                    var runningVisuals = RunningVisualsField?.GetValue(controller) as GameObject[];
                    if (vehicle != null) vehiclePath = SharedBuildingDoors.StablePath(vehicle.transform);
                    wheelCount = vehicle?.Wheels.Count ?? -1; visualCount = runningVisuals?.Length ?? -1;
                    if (vehicle == null || vehicle.vehicle_type != Vehicle_Type.AI_Vehicle || vehicle.vehicleRigidbody == null ||
                        runningVisuals == null || runningVisuals.Length > MartinCarState.MaxRunningVisuals)
                        throw new InvalidOperationException("native AI controller, AI_Vehicle body, or bounded run-visual array is unavailable");
                    if (!vehicle.gameObject.scene.IsValid() || controller.transform != vehicle.transform && controller.transform.parent != vehicle.transform)
                        throw new InvalidOperationException("AI_MartinCar must be attached to its native vehicle root or direct child");
                    if (vehicles.IsBound(vehicle)) throw new InvalidOperationException("VehicleController is already claimed by a driven-vehicle adapter");
                    if (vehicle.GetComponentsInChildren<Vehicle_DriverSeat>(true).Length != 0 ||
                        vehicle.GetComponentsInChildren<Vehicle_PassengerSeat>(true).Count(seat => seat != null && seat.vehicleController == vehicle) != 1)
                        throw new InvalidOperationException("native Martin car seat layout differs from the audited passenger-only layout");
                    if (vehicle.Wheels.Count != 4 || vehicle.Wheels.Any(wheel => wheel?.wheelController == null || wheel.wheelController.wheel == null || wheel.wheelController.wheel.Visual == null))
                        throw new InvalidOperationException("native Martin car four-wheel visual mapping is unavailable");
                    if (runningVisuals.Any(value => value == null || value.transform != vehicle.transform && !value.transform.IsChildOf(vehicle.transform)))
                        throw new InvalidOperationException("a Martin run visual is null or outside the native vehicle hierarchy");

                    var wheels = vehicle.Wheels.Select(wheel => wheel.wheelController)
                        .OrderBy(wheel => SharedBuildingDoors.StablePath(wheel.transform), StringComparer.Ordinal).ToArray();
                    identity += "/vehicle/" + vehiclePath + "/ai-vehicle-pose-v1";
                    ulong id = identities.Register(identity);
                    var binding = new Binding
                    {
                        Id = id, Identity = identity, Controller = controller, Vehicle = vehicle,
                        RunningVisuals = runningVisuals, Wheels = wheels,
                        OriginalRunningVisuals = runningVisuals.Select(value => value.activeSelf).ToArray(),
                        OriginalAiEnabled = controller.enabled, OriginalVehicleEnabled = vehicle.enabled,
                        OriginalAutoInput = vehicle.input.autoSetInput, OriginalFreezeIdle = vehicle.freezeWhileIdle,
                        OriginalFreezeSleep = vehicle.freezeWhileAsleep,
                        OriginalMode = vehicle.VehicleMultiplayerInstanceType, OriginalBodyKinematic = vehicle.vehicleRigidbody.isKinematic,
                        OriginalConstraints = vehicle.vehicleRigidbody.constraints, OriginalCollision = vehicle.vehicleRigidbody.collisionDetectionMode,
                        OriginalVelocity = vehicle.vehicleRigidbody.velocity, OriginalAngularVelocity = vehicle.vehicleRigidbody.angularVelocity,
                        OriginalVehiclePose = Pose(vehicle.transform),
                        OriginalWheelExternal = wheels.Select(wheel => wheel.UsingExternalUpdate).ToArray(),
                        OriginalWheelVisualOnly = wheels.Select(wheel => wheel.visualOnlyUpdate).ToArray(),
                        OriginalWheelAngularVelocity = wheels.Select(wheel => wheel.wheel.angularVelocity).ToArray()
                    };
                    binding.NativePassengerSeat = vehicle.GetComponentsInChildren<Vehicle_PassengerSeat>(true)
                        .Single(seat => seat != null && seat.vehicleController == vehicle);
                    binding.PassengerSeat = binding.NativePassengerSeat.seatPosition;
                    if (binding.PassengerSeat == null) throw new InvalidOperationException("native Martin passenger anchor is missing");
                    binding.PassengerKey = new SeatKey(id, "martin-passenger");
                    passengerRegistry.Register(binding.PassengerKey);
                    binding.Original = binding.Current = Capture(binding);
                    binding.PoseFrom = binding.PoseTo = binding.Original.VehiclePose;
                    binding.WheelSpinFrom = binding.WheelSpinTo = binding.Original.WheelSpin;
                    registry.Bind(id, MartinCarState.Adapter, vehicle.gameObject, identity, true, EntityPersistence.SessionOnly);
                    bindings.Add(id, binding); byController.Add(controller, binding);
                    for (int i = 0; i < wheels.Length; i++) byWheel.Add(wheels[i], (binding, i));
                }
                catch (Exception ex) { disposition = "skipped: " + ex.Message; }
                rows.Add(string.Join("\t", disposition, controllerPath, vehiclePath, wheelCount, visualCount, identity));
            }
            var doors = UnityEngine.Object.FindObjectsOfType<AI_MartinCar_HomeDoor>(true)
                .Where(value => value != null && value.gameObject.scene.IsValid()).ToArray();
            var nativeTarget = UnityEngine.Object.FindObjectOfType<AI_MartinCar>();
            if (doors.Length == 1 && nativeTarget != null && byController.TryGetValue(nativeTarget, out var targetBinding))
            {
                homeDoor = doors[0];
                homeDoorBinding = targetBinding;
                rows.Add(string.Join("\t", "door-knock-bound", SharedBuildingDoors.StablePath(homeDoor.transform),
                    SharedBuildingDoors.StablePath(nativeTarget.transform), "-", "-", targetBinding.Identity));
            }
            else
            {
                rows.Add(string.Join("\t", "door-knock-unavailable", doors.Length == 0 ? "missing" : string.Join(";", doors.Select(value => SharedBuildingDoors.StablePath(value.transform))),
                    nativeTarget == null ? "missing-ai-target" : SharedBuildingDoors.StablePath(nativeTarget.transform), "-", "-", "home door requests fail closed"));
            }

#if MONMP_DEVELOPMENT
            Directory.CreateDirectory(diagnosticsDirectory);


            File.WriteAllLines(Path.Combine(diagnosticsDirectory, "shared-martin-car.tsv"),
                new[] { "status\tcontroller-path\tvehicle-path\twheel-count\trunning-visual-count\tidentity",
                    "summary\t-\t-\t-\t-\trole=" + (host ? "host" : "guest") + ",controllers=" + candidates.Length + ",bound=" + bindings.Count }.Concat(rows));
#endif

        }

        internal static void ValidateNativeLayout()
        {
            RequireField("vc", typeof(VehicleController));
            RequireField("ToEnableWhenRunning", typeof(GameObject[]));
            RequireField("OriginalPosition", typeof(Vector3));
            RequireField("Car_State", typeof(AI_MartinCar.MartinCar_State));
            RequireMethod("get_Get_Car_State", Type.EmptyTypes, typeof(AI_MartinCar.MartinCar_State));
            RequireMethod("Start", Type.EmptyTypes, typeof(void));
            RequireMethod("Update", Type.EmptyTypes, typeof(void));
            RequireMethod("Tick", new[] { typeof(int) }, typeof(void));
            RequireMethod("PlayerCall", new[] { typeof(House_Location), typeof(bool) }, typeof(int));
            RequireMethod("PlayerEnteredDetectionZone", Type.EmptyTypes, typeof(void));
            RequireMethod("PlayerExitedDetectionZone", Type.EmptyTypes, typeof(void));
            RequireHomeDoorLayout();
            RequireMethod("DoorOpened", Type.EmptyTypes, typeof(void));
            RequireMethod("DoorClosed", Type.EmptyTypes, typeof(void));
            RequireMethod("Running", Type.EmptyTypes, typeof(void));
            RequireMethod("StartRoad", new[] { typeof(bool) }, typeof(System.Collections.IEnumerator));
            RequireMethod("PutToSleep", new[] { typeof(bool) }, typeof(System.Collections.IEnumerator));
            RequireMethod("get_Get_MaxThrottle", Type.EmptyTypes, typeof(float));
            RequireEnum();
        }
        private static void RequireHomeDoorLayout()
        {
            var field = AccessTools.Field(typeof(AI_MartinCar_HomeDoor), "Coroutine");
            var method = AccessTools.Method(typeof(AI_MartinCar_HomeDoor), "MouseDown");
            if (field == null || field.IsStatic || field.FieldType != typeof(bool) || method == null || method.IsStatic || method.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported native AI_MartinCar_HomeDoor layout.");
        }

        private static void RequireField(string name, Type fieldType)
        {
            var field = AccessTools.Field(typeof(AI_MartinCar), name);
            if (field == null || field.IsStatic || field.FieldType != fieldType)
                throw new InvalidOperationException("Unsupported native AI_MartinCar field: " + name);
        }
        private static void RequireMethod(string name, Type[] arguments, Type returnType)
        {
            var method = AccessTools.Method(typeof(AI_MartinCar), name, arguments);
            if (method == null || method.IsStatic || method.ReturnType != returnType)
                throw new InvalidOperationException("Unsupported native method: AI_MartinCar." + name);
        }
        private static void RequireEnum()
        {
            var names = Enum.GetNames(typeof(AI_MartinCar.MartinCar_State));
            var values = Enum.GetValues(typeof(AI_MartinCar.MartinCar_State)).Cast<int>().OrderBy(value => value).ToArray();
            if (names.Length != 5 || !values.SequenceEqual(new[] { 0, 1, 2, 3, 4 }))
                throw new InvalidOperationException("Unsupported native Martin car state enum.");
        }
        private static MartinCarState Capture(Binding binding) => new MartinCarState((byte)binding.Controller.Get_Car_State,
            Pose(binding.Vehicle.transform), RunningVisualMask(binding), CaptureWheelSpin(binding), binding.Passenger);
        private static uint RunningVisualMask(Binding binding)
        {
            uint mask = 0;
            for (int i = 0; i < binding.RunningVisuals.Length; i++) if (binding.RunningVisuals[i].activeSelf) mask |= 1u << i;
            return mask;
        }
        private static VehicleWheelSpin CaptureWheelSpin(Binding binding)
        {
            if (binding.Wheels.Length != 4) throw new InvalidOperationException("Martin car wheel mapping is unavailable.");
            return new VehicleWheelSpin(BoundAngularVelocity(binding.Wheels[0].wheel.angularVelocity),
                BoundAngularVelocity(binding.Wheels[1].wheel.angularVelocity), BoundAngularVelocity(binding.Wheels[2].wheel.angularVelocity),
                BoundAngularVelocity(binding.Wheels[3].wheel.angularVelocity));
        }
        private static float BoundAngularVelocity(float value) => float.IsNaN(value) || float.IsInfinity(value)
            ? 0f : Mathf.Clamp(value, -VehicleWheelSpin.MaximumAngularVelocity, VehicleWheelSpin.MaximumAngularVelocity);
        private static ItemPose Pose(Transform transform)
        {
            var position = transform.position; var rotation = transform.rotation;
            return new ItemPose(position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w);
        }
        private static Vector3 Position(ItemPose pose) => new Vector3(pose.X, pose.Y, pose.Z);
        private static Quaternion Rotation(ItemPose pose) => new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
        private static ItemPose Pose(Vector3 position, Quaternion rotation) =>
            new ItemPose(position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w);
        private static ItemPose Lerp(ItemPose from, ItemPose to, float amount) =>
            Pose(Vector3.Lerp(Position(from), Position(to), amount), Quaternion.Slerp(Rotation(from), Rotation(to), amount));

        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(binding =>
            new WorldRecord(binding.Id, 1, MartinCarState.Adapter, Capture(binding).Encode()));

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected; channel = world;
            #if MONMP_DEVELOPMENT
fixtureReadyPeers.Clear(); fixturePoseAcknowledgments.Clear(); fixtureReadyRequested = false;
#endif
            presence.Clear(); latestDoorKnockNonce.Clear(); lastDoorKnockRequestAt.Clear();
            nextDoorKnockNonce = 0; nextGuestDoorKnockAt = 0;
            world.SnapshotApplied += Snapshot; world.Changed += Changed; world.CommandRequested += Command;
            world.ReplyReceived += PassengerReply;
            connected.PoseReceived += OnPose; connected.PeerLeft += OnPeerLeft;
            if (!host) foreach (var binding in bindings.Values) SetRemoteMode(binding);
        }

        private void OnPeerLeft(uint peer)
        {
#if MONMP_DEVELOPMENT
            fixtureReadyPeers.Remove(peer);
            fixturePoseAcknowledgments.Remove(peer);
#endif
            presence.Remove(peer); latestDoorKnockNonce.Remove(peer); lastDoorKnockRequestAt.Remove(peer);
            lastPhoneCallAt.Remove(peer);
            if (host) RevokeOwnership(peer);
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);

        // Guests never run the native one-second HomeDoor coroutine locally. The
        // host starts that exact coroutine after validating the request so its
        // native PlayerCall and existing Martin-car pose publisher remain canonical.
        internal bool AllowHomeDoorMouseDown(AI_MartinCar_HomeDoor candidate)
        {
            if (!GuestAttached) return true;
            if (candidate != homeDoor || homeDoorBinding == null || !DoorKnockTargetIsCurrent())
            {
                ReportDoorKnockUnavailable("Martin's home-door target is unavailable in this multiplayer world.");
                return false;
            }
            if (channel?.Ready != true || channel.FullySynchronized != true ||
                MonMPPlugin.Instance?.World?.SaveGate.Active == true)
            {
                ReportDoorKnockUnavailable("Wait for the shared world and multiplayer save to finish before knocking.");
                return false;
            }
            float now = Time.unscaledTime;
            if (now < nextGuestDoorKnockAt)
            {
                ReportDoorKnockUnavailable("Your last Martin door-knock request is still pending.");
                return false;
            }
            if (nextDoorKnockNonce == uint.MaxValue)
            {
                ReportDoorKnockUnavailable("Martin door-knock requests are unavailable until you reconnect.");
                return false;
            }
            var command = new MartinCarDoorKnockCommand(++nextDoorKnockNonce);
            // The car pose record advances continuously, so route this as an
            // adapter-authorized command rather than tying it to an old revision.
            channel.Request(homeDoorBinding.Id, command.Encode(), useLease: true);
            nextGuestDoorKnockAt = now + GuestDoorKnockIntervalSeconds;
            return false;
        }

        private bool DoorKnockTargetIsCurrent() => homeDoor != null && homeDoor.gameObject.activeInHierarchy &&
            homeDoorBinding != null && homeDoorBinding.Controller != null &&
            UnityEngine.Object.FindObjectOfType<AI_MartinCar_HomeDoor>() == homeDoor &&
            UnityEngine.Object.FindObjectOfType<AI_MartinCar>() == homeDoorBinding.Controller &&
            byController.TryGetValue(homeDoorBinding.Controller, out var selected) && ReferenceEquals(selected, homeDoorBinding);

        private static void ReportDoorKnockUnavailable(string message) => MonMPPlugin.Instance?.ReportItemUseUnavailable(message);

        internal bool SuppressNative(AI_MartinCar controller)
        {
            bool suppress = GuestAttached && controller != null && byController.ContainsKey(controller);
            if (suppress) suppressedNativeCalls++;
            return suppress;
        }

        // AI_MartinCar.Tick uses only the host's local position for its daytime
        // distance-to-sleep check. Keep its native coroutine path intact unless a
        // current guest pose is inside the same 250 m bubble around the car or origin.
        internal bool ShouldSuppressTickSleep(AI_MartinCar controller, int tick)
        {
            if (!host || controller == null || !byController.ContainsKey(controller) || tick <= 240 || tick >= 660 ||
                Singleton<TickSystem>.i.Get_TimeIsAccelerated ||
                controller.Get_Car_State == AI_MartinCar.MartinCar_State.Sleeping ||
                controller.Get_Car_State == AI_MartinCar.MartinCar_State.StoppingToSleep)
                return false;
            var player = Singleton<Gameplay>.i.Player;
            if (player == null) return false;
            Vector3 playerPosition = player.transform.position;
            Vector3 carPosition = controller.transform.position;
            Vector3 originalPosition = (Vector3)OriginalPositionField.GetValue(controller);
            if (Vector3.Distance(playerPosition, carPosition) <= NativeSleepRange ||
                Vector3.Distance(playerPosition, originalPosition) <= NativeSleepRange)
                return false;

            float now = Time.unscaledTime;
            foreach (var remote in presence.Values)
            {
                if (now - remote.Time > RemoteSleepPoseFreshnessSeconds) continue;
                Vector3 remotePosition = new Vector3(remote.Pose.X, remote.Pose.Y, remote.Pose.Z);
                if (Vector3.Distance(remotePosition, carPosition) <= NativeSleepRange ||
                    Vector3.Distance(remotePosition, originalPosition) <= NativeSleepRange)
                    return true;
            }
            return false;
        }

        // Paired assertion for #149: exercise the real native Tick method at the morning
        // sleep boundary with a staged local-far/remote-near pose. The native light branch
        // at tick 300 must still run while the scoped PutToSleep call is vetoed.

#if MONMP_DEVELOPMENT
        internal string TestRemoteTickSleepGuard()
        {
            if (!host) return "not-host";
            var binding = bindings.Values.FirstOrDefault(value => value.Controller != null && value.Vehicle != null);
            if (binding == null) return "no supported Martin car binding";
            var controller = binding.Controller;
            var gameplay = Singleton<Gameplay>.i;
            var player = gameplay?.Player;
            if (gameplay == null || player == null) return "native player transform unavailable";

            var savedPresence = presence.ToArray();
            const uint FakePeer = 0xFFFFFFFE;
            var playerTransform = player.transform;
            Vector3 savedPlayerPosition = playerTransform.position;
            Quaternion savedPlayerRotation = playerTransform.rotation;
            object savedCarState = CarStateField.GetValue(controller);
            float savedVertical = binding.Vehicle.input.Vertical;
            bool savedLowBeamInput = binding.Vehicle.input.states.lowBeamLights;
            bool savedLowBeamOn = binding.Vehicle.effectsManager.lightsManager.lowBeamLights.On;
            bool savedFastForward = Singleton<TickSystem>.i.isFastForwardTime;
            bool savedVehicleEnabled = binding.Vehicle.enabled;
            Vector3 carPosition = controller.transform.position;
            Vector3 remotePosition = carPosition;
            Vector3 hostPosition = carPosition + Vector3.up * 5000f + Vector3.right * 5000f;
            var tickSystem = Singleton<TickSystem>.i;

            try
            {
                if (gameplay.Get_IsSleeping || tickSystem.isSaving)
                    return "skip: world is sleeping or saving, so native accelerated-time sleep semantics are active";
                tickSystem.isFastForwardTime = false;
                playerTransform.SetPositionAndRotation(hostPosition, savedPlayerRotation);
                CarStateField.SetValue(controller, AI_MartinCar.MartinCar_State.Running);
                binding.Vehicle.enabled = savedVehicleEnabled;
                presence.Clear();
                presence[FakePeer] = (new PlayerPose(remotePosition.x, remotePosition.y, remotePosition.z, 0, 0,
                    (byte)PlayerState.Walking), Time.unscaledTime);
                if (!ShouldSuppressTickSleep(controller, 300))
                    return "normal daytime native sleep condition was not staged";

                binding.Vehicle.effectsManager.lightsManager.lowBeamLights.On = false;
                binding.Vehicle.input.states.lowBeamLights = false;
                TickMethod.Invoke(controller, new object[] { 300 });
                if (controller.Get_Car_State != AI_MartinCar.MartinCar_State.Running)
                    return "fresh remote did not veto the Tick-scoped PutToSleep call";
                if (!binding.Vehicle.input.states.lowBeamLights)
                    return "native tick-300 night-light update did not run while sleep was vetoed";

                presence[FakePeer] = (new PlayerPose(remotePosition.x, remotePosition.y, remotePosition.z, 0, 0,
                    (byte)PlayerState.Walking), Time.unscaledTime - RemoteSleepPoseFreshnessSeconds - 0.1f);
                if (ShouldSuppressTickSleep(controller, 300))
                    return "stale remote pose still suppresses the native daytime sleep check";
                if (ShouldSuppressTickSleep(controller, 240) || ShouldSuppressTickSleep(controller, 660))
                    return "native morning sleep time boundaries changed";
                tickSystem.isFastForwardTime = true;
                if (ShouldSuppressTickSleep(controller, 300))
                    return "accelerated-time native sleep behavior was suppressed";
                return "";
            }
            catch (Exception ex)
            {
                return "native Martin car sleep assertion failed: " + ex.GetBaseException().Message;
            }
            finally
            {
                presence.Clear();
                foreach (var entry in savedPresence) presence[entry.Key] = entry.Value;
                CarStateField.SetValue(controller, savedCarState);
                binding.Vehicle.input.Vertical = savedVertical;
                binding.Vehicle.input.states.lowBeamLights = savedLowBeamInput;
                binding.Vehicle.effectsManager.lightsManager.lowBeamLights.On = savedLowBeamOn;
                binding.Vehicle.enabled = savedVehicleEnabled;
                tickSystem.isFastForwardTime = savedFastForward;
                playerTransform.SetPositionAndRotation(savedPlayerPosition, savedPlayerRotation);
                Physics.SyncTransforms();
            }
        }
#endif

        internal bool SuppressCoroutine(object stateMachine)
        {
            if (!GuestAttached || stateMachine == null) return false;
            var owner = AccessTools.Field(stateMachine.GetType(), "<>4__this")?.GetValue(stateMachine) as AI_MartinCar;
            return owner != null && SuppressNative(owner);
        }
        internal bool TryGetReplicatedCarState(AI_MartinCar controller, out AI_MartinCar.MartinCar_State state)
        {
            state = default;
            if (!GuestAttached || controller == null || !byController.TryGetValue(controller, out var binding) || binding.Current == null) return false;
            state = (AI_MartinCar.MartinCar_State)binding.Current.CarState;
            return true;
        }
        internal bool TryGetRemoteWheelSpin(WheelController wheel, out float angularVelocity)
        {
            angularVelocity = 0;
            if (!GuestAttached || channel?.Ready != true || wheel == null || !wheel.visualOnlyUpdate ||
                !byWheel.TryGetValue(wheel, out var mapping) || !mapping.Binding.HasPresentationPose) return false;
            float amount = Mathf.Clamp01((Time.unscaledTime - mapping.Binding.PoseReceivedAt) / RemotePoseInterpolationSeconds);
            angularVelocity = Mathf.Lerp(mapping.Binding.WheelSpinFrom[mapping.Index], mapping.Binding.WheelSpinTo[mapping.Index], amount);
            return true;
        }

        private static void SetRemoteMode(Binding binding)
        {
            var vehicle = binding.Vehicle;
            vehicle.SetMultiplayerInstanceType(Vehicle.MultiplayerInstanceType.Remote, true);
            vehicle.input.autoSetInput = false;
            vehicle.freezeWhileIdle = false; vehicle.freezeWhileAsleep = false;
            vehicle.vehicleRigidbody.constraints = RigidbodyConstraints.None;
            // Awake the NWH presentation loop on copies where native PutToSleep disabled it.
            // Remote mode keeps the body kinematic and wheel updates visual-only.
            if (!vehicle.enabled) vehicle.enabled = true;
            binding.Controller.enabled = false;
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values.Where(record => record.Adapter == MartinCarState.Adapter)) Apply(record, true);
        }
        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == MartinCarState.Adapter) Apply(record, false);
        }
        [MonMP.Core.World.WorldCommandAdapters(MartinCarState.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || session?.IsHost != true || payload == null || !bindings.TryGetValue(entity, out var binding)) return;
            if (IsPassengerCommand(payload)) { CommandPassenger(peer, binding, payload); return; }
#if MONMP_DEVELOPMENT
            if (payload.Length == 3 && payload[0] == 0x4D && payload[1] == 0x43 && payload[2] == 0)
            {
                if (session?.Peers.ContainsKey(peer) == true) fixtureReadyPeers.Add(peer);
                return;
            }
            if (payload.Length == 3 && payload[0] == 0x4D && payload[1] == 0x43 && payload[2] == 1 && binding.FixturePoseStaged)
            {
                fixturePoseAcknowledgments.Add(peer);
                return;
            }

#endif
            if (payload.Length == 4 && payload[0] == 0x4D && payload[1] == 0x43 && payload[2] == PhoneCallCommand)
            {
                // A guest dialled Martin (2020): the host's car answers as native PlayerCall would.
                var location = (House_Location)payload[3];
                if (peer == 0 || session?.Peers.ContainsKey(peer) != true || binding.Controller == null ||
                    (location != House_Location.Roulotte && location != House_Location.Cottage && location != House_Location.Speedway)) return;
                float at = Time.unscaledTime;
                if (lastPhoneCallAt.TryGetValue(peer, out float lastCall) && at - lastCall < 2f) return;
                lastPhoneCallAt[peer] = at;
                if (MonMPPlugin.Instance?.World?.SaveGate.Active == true) return;

#if MONMP_DEVELOPMENT
                TestPhoneCalls++;
#endif

                // #165: the guest's own replica already showed Martin's answer; it isn't the host's call.
                using (Patches.RemoteActionScope.Enter()) binding.Controller.PlayerCall(location);
                return;
            }
            if (!MartinCarDoorKnockCommand.Is(payload)) return;

#if MONMP_DEVELOPMENT
            TestDoorKnockRequests++;
#endif

            if (peer == 0 || session?.Peers.ContainsKey(peer) != true)
            { RejectDoorKnock("unknown-peer"); return; }
            if (homeDoorBinding == null || entity != homeDoorBinding.Id || !ReferenceEquals(binding, homeDoorBinding) ||
                !DoorKnockTargetIsCurrent())
            { RejectDoorKnock("wrong-door-target"); return; }

            MartinCarDoorKnockCommand command;
            try { command = MartinCarDoorKnockCommand.Decode(payload); }
            catch (InvalidDataException) { RejectDoorKnock("malformed-command"); return; }
            if (latestDoorKnockNonce.TryGetValue(peer, out var latest) && command.Nonce <= latest)
            { RejectDoorKnock("replayed-or-stale-nonce"); return; }
            latestDoorKnockNonce[peer] = command.Nonce;

            float now = Time.unscaledTime;
            if (lastDoorKnockRequestAt.TryGetValue(peer, out var lastRequest) && now - lastRequest < DoorKnockPeerIntervalSeconds)
            { lastDoorKnockRequestAt[peer] = now; RejectDoorKnock("rate-limited"); return; }
            lastDoorKnockRequestAt[peer] = now;
            if (!presence.TryGetValue(peer, out var seen) || now - seen.Time > DoorKnockPoseFreshnessSeconds)
            { RejectDoorKnock("stale-pose"); return; }
            if (seen.Pose.State != (byte)PlayerState.Walking)
            { RejectDoorKnock("not-walking"); return; }
            Vector3 playerPosition = new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z);
            if ((playerPosition - homeDoor!.transform.position).sqrMagnitude > DoorKnockCommandRange * DoorKnockCommandRange)
            { RejectDoorKnock("out-of-range"); return; }
            if (MonMPPlugin.Instance?.World?.SaveGate.Active == true)
            { RejectDoorKnock("save-gate"); return; }
            if ((bool)HomeDoorCoroutineField.GetValue(homeDoor))
            { RejectDoorKnock("native-knock-already-running"); return; }


#if MONMP_DEVELOPMENT
            TestDoorKnockAccepted++;


            TestDoorKnockLastResult = "accepted";


            TestDoorKnockNativeCalls++;
#endif

            homeDoor.MouseDown();
        }

        private void RejectDoorKnock(string reason)
        {
#if MONMP_DEVELOPMENT
TestDoorKnockRejected++;

 TestDoorKnockLastResult = reason;
#endif
 }

#if MONMP_DEVELOPMENT
        internal bool TestStagePose(ulong entity)
        {
            if (!host || !bindings.TryGetValue(entity, out var binding) || binding.FixturePoseStaged) return false;
            var source = Capture(binding);
            binding.FixtureOriginPose = source.VehiclePose;
            var sourcePosition = Position(source.VehiclePose);
            var sourceRotation = Rotation(source.VehiclePose);
            var targetRotation = Quaternion.Euler(0, 18, 0) * sourceRotation;
            uint visuals = binding.RunningVisuals.Length == 0 ? source.RunningVisualMask : source.RunningVisualMask ^ 1u;
            binding.FixtureState = new MartinCarState(source.CarState,
                new ItemPose(sourcePosition.x + 2.5f, sourcePosition.y, sourcePosition.z, targetRotation.x, targetRotation.y, targetRotation.z, targetRotation.w),
                visuals, new VehicleWheelSpin(52, -44, 20, -16), source.Passenger);
            binding.FixturePoseStaged = true;
            binding.Current = binding.FixtureState;
            return true;
        }


        internal bool TestRestoreStagedPose(ulong entity)
        {
            if (!host || !bindings.TryGetValue(entity, out var binding) || !binding.FixturePoseStaged) return false;
            binding.FixturePoseStaged = false;
            binding.Current = Capture(binding);
            return true;
        }


        internal void TestAcknowledgePose(ulong entity)
        {
            if (!host && GuestAttached && channel?.Ready == true && bindings.ContainsKey(entity))
                channel.Request(entity, new byte[] { 0x4D, 0x43, 1 });
        }


        internal bool TestRequestReady(ulong entity)
        {
            if (host || !GuestAttached || channel?.Ready != true || !GuestSimulationSuppressed || receivedPoseUpdates == 0 ||
                !bindings.ContainsKey(entity)) return false;
            if (!fixtureReadyRequested || Time.unscaledTime >= nextFixtureReadyRequestAt)
            {
                channel.Request(entity, new byte[] { 0x4D, 0x43, 0 });
                fixtureReadyRequested = true;
                nextFixtureReadyRequestAt = Time.unscaledTime + 1f;
            }
            return true;
        }
#endif


        private void Apply(WorldRecord record, bool snap)
        {
            if (record.Adapter != MartinCarState.Adapter || !bindings.TryGetValue(record.Id, out var binding))
                throw new InvalidDataException("Unknown Martin car identity.");
            var state = MartinCarState.Decode(record.Payload);
            if ((binding.RunningVisuals.Length < MartinCarState.MaxRunningVisuals && (state.RunningVisualMask >> binding.RunningVisuals.Length) != 0))
                throw new InvalidDataException("Martin car run-visual layout differs from host snapshot.");
            // Derive motion from consecutive host targets, never the lagging rendered
            // PoseFrom (which includes interpolation error). Resync/implausible jumps start still.
            var velocity = !snap && binding.HasPresentationPose && binding.Current != null &&
                Time.unscaledTime - binding.PoseReceivedAt <= 1f
                ? (Position(state.VehiclePose) - Position(binding.Current.VehiclePose)) / CaptureSeconds : Vector3.zero;
            binding.PassengerVelocity = velocity.sqrMagnitude <= 60f * 60f ? velocity : Vector3.zero;
            binding.Current = state;
            ApplyPassenger(binding, state.Passenger);
            receivedPoseUpdates++;
            if (snap || !binding.HasPresentationPose)
            {
                binding.PoseFrom = binding.PoseTo = state.VehiclePose;
                binding.WheelSpinFrom = binding.WheelSpinTo = state.WheelSpin;
                binding.PoseReceivedAt = Time.unscaledTime;
                binding.HasPresentationPose = true;
                Present(binding, 1);
            }
            else
            {
                binding.PoseFrom = Pose(binding.Vehicle.transform);
                binding.PoseTo = state.VehiclePose;
                binding.WheelSpinFrom = CurrentWheelSpin(binding);
                binding.WheelSpinTo = state.WheelSpin;
                binding.PoseReceivedAt = Time.unscaledTime;
                binding.HasPresentationPose = true;
            }
        }

        private static VehicleWheelSpin CurrentWheelSpin(Binding binding)
        {
            float amount = Mathf.Clamp01((Time.unscaledTime - binding.PoseReceivedAt) / RemotePoseInterpolationSeconds);
            return new VehicleWheelSpin(
                Mathf.Lerp(binding.WheelSpinFrom.Wheel0, binding.WheelSpinTo.Wheel0, amount),
                Mathf.Lerp(binding.WheelSpinFrom.Wheel1, binding.WheelSpinTo.Wheel1, amount),
                Mathf.Lerp(binding.WheelSpinFrom.Wheel2, binding.WheelSpinTo.Wheel2, amount),
                Mathf.Lerp(binding.WheelSpinFrom.Wheel3, binding.WheelSpinTo.Wheel3, amount));
        }
        private static void Present(Binding binding, float amount)
        {
            var pose = Lerp(binding.PoseFrom, binding.PoseTo, amount);
            binding.Vehicle.transform.SetPositionAndRotation(Position(pose), Rotation(pose));
            var body = binding.Vehicle.vehicleRigidbody;
            if (body != null && body.isKinematic)
            { body.position = Position(pose); body.rotation = Rotation(pose); }
            for (int i = 0; i < binding.RunningVisuals.Length; i++)
            {
                bool active = (binding.Current.RunningVisualMask & (1u << i)) != 0;
                if (binding.RunningVisuals[i].activeSelf != active) binding.RunningVisuals[i].SetActive(active);
            }
        }
        // #166: where a player seated in Martin's car sits, on this peer's copy of the car. Their pose
        // trails the moving car, so their avatar rides this anchor instead (as Konig passengers do), and
        // the host measures talking to Martin from it.
        internal Transform? SeatFor(uint peer) => bindings.Values.FirstOrDefault(binding => binding.Passenger.Owner == peer)?.PassengerSeat;
        internal VehicleController? VehicleForPeer(uint peer) => bindings.Values.FirstOrDefault(binding => binding.Passenger.Owner == peer)?.Vehicle;

        // Only this adapter's authoritative passenger claim identifies a Martin rider.
        // Nearby Konig/bus passengers never stand in for Martin's native DoorClosed.
        private void UpdateRemotePassenger(Binding binding)
        {
            bool seated = binding.Passenger.Occupied && binding.Passenger.Owner != 0 &&
                presence.TryGetValue(binding.Passenger.Owner, out var remote) &&
                Time.unscaledTime - remote.Time <= RemoteSleepPoseFreshnessSeconds &&
                remote.Pose.State == (byte)PlayerState.InVehicle_Passenger;
            if (seated == binding.RemotePassenger) return;
            binding.RemotePassenger = seated;
            var state = binding.Controller.Get_Car_State;
            if (seated && (state == AI_MartinCar.MartinCar_State.WaitingForPlayer || state == AI_MartinCar.MartinCar_State.Stopping))
                binding.Controller.DoorClosed();
        }

        // #166: Martin's hotbox smoke only builds while the car's physics is awake; a guest's copy is a
        // kinematic replica, so it counts as awake whenever the host's car is out of its sleep state.
        // #165: a guest's phone reached Martin's suppressed replica, which answered nothing. Answer from the
        // replicated car as native PlayerCall does and ask the host's car to set off.
        internal bool GuestPhoneCall(AI_MartinCar controller, House_Location location)
        {
            if (!GuestAttached || controller == null || !byController.TryGetValue(controller, out var binding)) return false;
            var state = binding.Current == null ? AI_MartinCar.MartinCar_State.Sleeping : (AI_MartinCar.MartinCar_State)binding.Current.CarState;
            int tick = Singleton<TickSystem>.i.Get_Tick;
            bool asleepForTheNight = state == AI_MartinCar.MartinCar_State.Sleeping && tick > 240 && tick < 660;
            string? key = state != AI_MartinCar.MartinCar_State.Sleeping ? "PHONE_MartinCar_OnTheRoad"
                : asleepForTheNight ? "PHONE_MartinCar_Sleeping"
                : location == House_Location.Roulotte ? "PHONE_MartinCar_StartCarToHome"
                : location == House_Location.Speedway ? "PHONE_MartinCar_StartCarToTrack" : null;
            if (key != null) Singleton<UI_EventMessage>.i.Show(TEXT.GetText(key), "Martin", 8f, UI_EventMessage.EventIconType.Phone);
            if (state == AI_MartinCar.MartinCar_State.Sleeping && !asleepForTheNight && channel?.Ready == true && Time.unscaledTime >= nextGuestPhoneCallAt)
            {
                nextGuestPhoneCallAt = Time.unscaledTime + 2f;
                channel.Request(binding.Id, new byte[] { 0x4D, 0x43, PhoneCallCommand, (byte)location }, useLease: true);
            }
            return true;
        }

        internal bool RemoteAwake(NWH.Vehicle vehicle)
        {
            if (!GuestAttached || vehicle == null) return false;
            foreach (var binding in bindings.Values)
                if (binding.Vehicle == vehicle && binding.Current != null)
                    return (AI_MartinCar.MartinCar_State)binding.Current.CarState != AI_MartinCar.MartinCar_State.Sleeping;
            return false;
        }

        internal void Update()
        {
            if (disposed) return;
            UpdatePassengerOwnership();
            if (host && session?.IsRunning == true && channel != null && Time.unscaledTime >= nextCapture)
            {
                nextCapture = Time.unscaledTime + CaptureSeconds;
                foreach (var binding in bindings.Values)
                {
                    if (binding.Controller == null || binding.Vehicle == null || !binding.Controller.gameObject.scene.IsValid()) continue;
                    try
                    {
                        UpdateRemotePassenger(binding);
#if MONMP_DEVELOPMENT
                        binding.Current = binding.FixturePoseStaged ? binding.FixtureState : Capture(binding);
#endif
#if !MONMP_DEVELOPMENT
                        binding.Current = Capture(binding);
#endif
                        channel.Publish(binding.Id, binding.Current.Encode());
                    }
                    catch (Exception ex)
                    {
                        if (!binding.CaptureFailureLogged)
                        {

#if MONMP_DEVELOPMENT
                            File.AppendAllText(Path.Combine(diagnosticsDirectory, "shared-martin-car-errors.txt"), binding.Identity + "\t" + ex.Message + Environment.NewLine);
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
                    if (!binding.HasPresentationPose || binding.Vehicle == null) continue;
                    float amount = Mathf.Clamp01((Time.unscaledTime - binding.PoseReceivedAt) / RemotePoseInterpolationSeconds);
                    if (amount > 0 && amount < 1)
                    {
                        if (binding.PoseFrom.X != binding.PoseTo.X || binding.PoseFrom.Y != binding.PoseTo.Y || binding.PoseFrom.Z != binding.PoseTo.Z ||
                            binding.PoseFrom.Qx != binding.PoseTo.Qx || binding.PoseFrom.Qy != binding.PoseTo.Qy ||
                            binding.PoseFrom.Qz != binding.PoseTo.Qz || binding.PoseFrom.Qw != binding.PoseTo.Qw) interpolationFrames++;
                        if (!binding.WheelSpinFrom.Equals(binding.WheelSpinTo)) wheelInterpolationFrames++;
                    }
                    Present(binding, amount);
                }
            }
        }

        private static void Restore(Binding binding)
        {
            var vehicle = binding.Vehicle;
            if (vehicle == null) return;
            if (vehicle.VehicleMultiplayerInstanceType != binding.OriginalMode)
                vehicle.SetMultiplayerInstanceType(binding.OriginalMode, binding.OriginalBodyKinematic);
            vehicle.input.autoSetInput = binding.OriginalAutoInput;
            vehicle.freezeWhileIdle = binding.OriginalFreezeIdle;
            vehicle.freezeWhileAsleep = binding.OriginalFreezeSleep;
            vehicle.enabled = binding.OriginalVehicleEnabled;
            vehicle.transform.SetPositionAndRotation(Position(binding.OriginalVehiclePose), Rotation(binding.OriginalVehiclePose));
            vehicle.vehicleRigidbody.isKinematic = binding.OriginalBodyKinematic;
            vehicle.vehicleRigidbody.constraints = binding.OriginalConstraints;
            vehicle.vehicleRigidbody.collisionDetectionMode = binding.OriginalCollision;
            vehicle.vehicleRigidbody.velocity = binding.OriginalVelocity;
            vehicle.vehicleRigidbody.angularVelocity = binding.OriginalAngularVelocity;
            for (int i = 0; i < Math.Min(binding.Wheels.Length, binding.OriginalWheelExternal.Length); i++)
            {
                if (binding.OriginalWheelExternal[i]) binding.Wheels[i].UseExternalUpdate(); else binding.Wheels[i].UseInternalUpdate();
                binding.Wheels[i].visualOnlyUpdate = binding.OriginalWheelVisualOnly[i];
                binding.Wheels[i].wheel.angularVelocity = binding.OriginalWheelAngularVelocity[i];
            }
            for (int i = 0; i < Math.Min(binding.RunningVisuals.Length, binding.OriginalRunningVisuals.Length); i++)
                if (binding.RunningVisuals[i] != null) binding.RunningVisuals[i].SetActive(binding.OriginalRunningVisuals[i]);
            binding.Controller.enabled = binding.OriginalAiEnabled;
        }

        public void Dispose()
        {
            if (disposed) return;
            SeatAdmission?.CancelLocal(this);
            foreach (var binding in bindings.Values) ExitNativePassenger(binding, true);
            disposed = true;
            if (channel != null)
            {
                channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command;
                channel.ReplyReceived -= PassengerReply;
            }
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnPeerLeft; }
            if (!host) foreach (var binding in bindings.Values) Restore(binding);
#if MONMP_DEVELOPMENT
            else foreach (var binding in bindings.Values.Where(value => value.FixturePoseStaged).ToArray()) TestRestoreStagedPose(binding.Id);
#endif
            presence.Clear(); latestDoorKnockNonce.Clear(); lastDoorKnockRequestAt.Clear();
            session = null; channel = null;
        }
    }
}
