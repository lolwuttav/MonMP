using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using NWH;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Modules.Coolant;
using NWH.VehiclePhysics2.Modules.Trailer;
using NWH.VehiclePhysics2.Powertrain;
using NWH.VehiclePhysics2.VehicleGUI;
using NWH.WheelController3D;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedVehicles : IDisposable
    {
        internal sealed class NativeSeatButtonBinding
        {
            internal Konig_Seat Seat = null!;
            internal Konig_Seat_Button? Button;
            internal int Children;
            internal float NextDiscovery;
        }
        private sealed class SeatAdjustmentBinding
        {
            public Binding Vehicle = null!;
            public ulong Id;
            public Konig_Seat Seat = null!;
            public bool Driver;
            public Part_Seat_Forward? ForwardButton;
            public Part_Seat_Height? HeightButton;
            public VehicleSeatAdjustmentState State, OriginalState;
            public Vector3 OriginalLocalPosition;
            public float OriginalSeatParameterForward, OriginalSeatParameterHeight;
            public bool RecordEnabled, OriginalCaptured;
            public VehicleSeatAdjustmentState? Pending;
            public float ControlsCheckedAt = float.NegativeInfinity;
            public bool LocalClickPending, RestoreParametersAfterLocalClick;
            public VehicleSeatAdjustmentState LocalClickBefore;
            public VehicleSeatAdjustmentAxis LocalClickAxis;
            public int LocalClickDirection;
            public float LocalClickParameterForward, LocalClickParameterHeight;
            public readonly List<Part_Seat> PartScratch = new List<Part_Seat>();
            public readonly List<Part_Seat_Forward> ForwardScratch = new List<Part_Seat_Forward>();
            public readonly List<Part_Seat_Height> HeightScratch = new List<Part_Seat_Height>();
        }

        private sealed class Binding
        {
            public ulong Id;
            public Component[]? Controls;
            public CoolantModule? Coolant;
            public bool CoolantResolved;
            public Vehicle_Button_ParkingBrake? ParkingBrake;
            public float ParkingBrakeCheckedAt = float.NegativeInfinity;
            public bool DisconnectSecuring;
            public double RefuelAcknowledged;
            public DashGUIController[]? GearDisplays;
            public float GearDisplaysCheckedAt = float.NegativeInfinity;
            public Vehicle_Button_LightDomeOptions[]? DomeModeControls;
            public VehicleController Vehicle = null!;
            public Vehicle_DriverSeat? Seat;
            // The scrapyard Konig's native driver interaction may use Konig_Seat rather than
            // Vehicle_DriverSeat. Keep it separate from the starting Konig seat binding because
            // the latter also owns passenger and seat-adjustment persistence.
            public Konig_Seat? ScrapyardKonigDriverSeat;
            public Konig_Seat? KonigDriverSeat, KonigPassengerSeat;
            public SeatAdjustmentBinding? DriverSeatAdjustment, PassengerSeatAdjustment;
            public bool ShellMotionShapeReported;
            public bool TryGetDriverSeat(out Component? component, out Transform? position)
            {
                component = null; position = null;
                if (KonigDriverSeat != null)
                {
                    component = KonigDriverSeat; position = KonigDriverSeat.seatPosition;
                    return KonigDriverSeat.isActiveAndEnabled && position != null && position.gameObject.activeInHierarchy;
                }
                if (Vehicle.vehicle_type == Vehicle_Type.ScrapyardKonig || Vehicle.vehicle_type == Vehicle_Type.Prinz)
                {
                    var standard = Vehicle.GetComponentsInChildren<Vehicle_DriverSeat>(true);
                    var konig = Vehicle.GetComponentsInChildren<Konig_Seat>(true)
                        .Where(value => value != null && value.Get_isDriverSeat).ToArray();
                    bool standardBelongs = standard.Length == 1 && standard[0] != null && standard[0].vehicleController == Vehicle;
                    bool konigBelongs = konig.Length == 1 && konig[0] != null && konig[0].vehicleController == Vehicle;
                    var candidate = standardBelongs ? (Component)standard[0] : konigBelongs ? konig[0] : null;
                    Transform? candidatePosition = candidate is Vehicle_DriverSeat standardSeat ? standardSeat.seatPosition :
                        candidate is Konig_Seat konigSeat ? konigSeat.seatPosition : null;
                    if (!ScrapyardKonigSupportPolicy.SupportsDriverSeat(new ScrapyardKonigDriverSeatShape(
                        standard.Length + konig.Length, standardBelongs || konigBelongs,
                        candidatePosition != null && candidatePosition.gameObject.activeInHierarchy &&
                            candidate is Behaviour behaviour && behaviour.isActiveAndEnabled))) return false;
                    component = candidate;
                    position = candidatePosition;
                    return true;
                }
                if (ScrapyardKonigDriverSeat != null)
                {
                    component = ScrapyardKonigDriverSeat; position = ScrapyardKonigDriverSeat.seatPosition;
                    return position != null && position.gameObject.activeInHierarchy;
                }
                if (Seat != null)
                {
                    component = Seat; position = Seat.seatPosition;
                    return position != null && position.gameObject.activeInHierarchy;
                }
                return false;
            }
            public Transform DriverPosition => TryGetDriverSeat(out _, out var position) && position != null
                ? position : throw new InvalidOperationException("This vehicle has no supported active driver seat.");
            public Component DriverComponent => TryGetDriverSeat(out var component, out _) && component != null
                ? component : throw new InvalidOperationException("This vehicle has no supported active driver seat.");
            public bool DriverCanUse()
            {
                if (!TryGetDriverSeat(out var component, out _) || component == null) return false;
                if (component is Konig_Seat konigSeat)
                    return konigSeat.CanSeat() && Vehicle.vehicle_PartsManager.CheckSeatSolidity(true);
                return component is Vehicle_DriverSeat standardSeat && standardSeat.CanUse();
            }
            public VehicleState State = null!, Original = null!;
            public VehicleState? LastPublishedState;
            public WorldRecord? LastPublishedRecord;
            public bool AutoInput, FreezeIdle, FreezeSleep, ExitPending, BodyApplyPending, LiftFreezeApplied;
            public bool? RequestedExitDriverSide;
            public NativeSeatButtonBinding? DriverExitButton, PassengerExitButton;
            // Buggy: a vehicle the players build (the buggy or the racecar), bound dormant until built.
            public bool Buggy, Racecar, Dormant, DeferredAtStart, OriginalCaptured;
            public bool OriginalRootActive, OriginalShellEnabled;
            public Vector3 OriginalShellPosition;
            public Quaternion OriginalShellRotation;
            public bool[] ExternalWheels = null!;
            public WheelController[] WheelVisuals = Array.Empty<WheelController>();
            public WheelController[] IndexedWheelVisuals = Array.Empty<WheelController>();
            public readonly float[] LastRemoteWheelSpinVisual = new float[4];
            public byte RemoteWheelSpinVisualAppliedMask, RemoteWheelSpinVisualHighMask;
            public TrailerHitchModule? Hitch;
            public Rigidbody? Accessory;
            public bool HitchEnabled;
            public Vehicle.MultiplayerInstanceType OriginalMode;
            public RigidbodyConstraints OriginalConstraints;
            public bool OriginalKinematic, OriginalVehicleEnabled;
            public CollisionDetectionMode OriginalCollision;
            public RigidbodyInterpolation OriginalInterpolation;
            public VehicleState? PendingState;
            public UnityEngine.Events.UnityAction? NativeInitialized;
            public ulong PassengerId;
            public SeatKey PassengerKey;
            public VehiclePassengerSeatState PassengerState;
            public bool PassengerExitPending;
            public float NextPassengerExitRetry;
            public float LastUpdate;
            public int EnteredFrame = -1;
            public bool HasPresentationPose;
            public Vector3 PoseFrom, PoseTo, PoseVelocity, PoseAngularVelocity;
            public Vector3 RiderFramePosition; public Quaternion RiderFrameRotation = Quaternion.identity; public bool RiderFrameKnown;
            public Quaternion RotationFrom, RotationTo;
            public float PoseReceivedAt;
            public readonly RemoteVehiclePose Presentation = new RemoteVehiclePose();
            public Vector3 PhysicsCorrectionVelocity;
        }
        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly NativeEntityRegistry identities;
        internal Func<ulong, bool>? ForeignPassengerSeatRecord;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Vehicle, Binding> byVehicle = new Dictionary<Vehicle, Binding>();
        private readonly Dictionary<WheelController, (Binding Binding, int Index)> byWheel =
            new Dictionary<WheelController, (Binding, int)>();
        private readonly Dictionary<ulong, (Vehicle_Type Type, GameObject Instance)> pendingPurchasedVehicles =
            new Dictionary<ulong, (Vehicle_Type, GameObject)>();
        private readonly ItemLeaseTable drivers = new ItemLeaseTable();
        private readonly SeatRegistry passengers = new SeatRegistry();
        private readonly Dictionary<ulong, Binding> passengerBindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<ulong, SeatAdjustmentBinding> seatAdjustmentBindings = new Dictionary<ulong, SeatAdjustmentBinding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();

#if MONMP_DEVELOPMENT
        private readonly HashSet<ulong> testDriverEntryTraceIds = new HashSet<ulong>();


        private readonly Queue<string> testDriverEntryTrace = new Queue<string>();


#if MONMP_DEVELOPMENT
private bool testDriverEntryTraceEnabled;
#endif


        private int testDriverEntryTraceSequence;


        private int testGuestMouseDownCount, testEnterExitPrefixCount, testHostWorldRejectedCount, testHostVehicleCommandCount;
#endif

        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, disposed;
        private bool applyingMechanics;
        private bool exitDeferred;
        private float nextSend;
        private float nextNativeShellPromotion;
        private const float RemotePoseInterpolationSeconds = 0.08f;
        public int Count => bindings.Count;
        public SharedVehicleDoors Doors { get; }
        public SharedVehicleWindows Windows { get; }
        public SharedVehicleHood Hood { get; }
        public SharedVehicleTrunk Trunk { get; }
        public bool Ready => channel?.Ready == true;
        public bool TransfersBlocked { get; set; }
        private static VehicleState DormantState(Binding b, ItemLease driver)
        {
            var p = b.Vehicle.transform.position; var q = b.Vehicle.transform.rotation;
            return VehicleStatePublication.Reuse(b.State, driver, new ItemPose(p.x, p.y, p.z, q.x, q.y, q.z, q.w),
                active: b.Vehicle.gameObject.activeSelf, motionReady: false, refuelCredit: b.State?.RefuelCredit ?? 0);
        }
        private static bool[] CaptureExternalWheels(VehicleController v)
        {
            try { return v.Wheels.Select(w => w.wheelController.UsingExternalUpdate).ToArray(); }
            catch { return Array.Empty<bool>(); }
        }
        private static WheelController[] CaptureWheelVisualOrder(VehicleController v) => v.Wheels
            .Select(w => w.wheelController)
            .OrderBy(w => RuntimeProbe.CanonicalPath(w.transform), StringComparer.Ordinal)
            .ToArray();
        private static float BoundWheelAngularVelocity(float value) => float.IsNaN(value) || float.IsInfinity(value)
            ? 0f : Mathf.Clamp(value, -VehicleWheelSpin.MaximumAngularVelocity, VehicleWheelSpin.MaximumAngularVelocity);
        private static VehicleWheelSpin CaptureWheelSpin(Binding b)
        {
            if (b.WheelVisuals.Length != 4) throw new InvalidOperationException("Shared vehicle wheel visual mapping is unavailable.");
            return new VehicleWheelSpin(BoundWheelAngularVelocity(b.WheelVisuals[0].wheel.angularVelocity),
                BoundWheelAngularVelocity(b.WheelVisuals[1].wheel.angularVelocity),
                BoundWheelAngularVelocity(b.WheelVisuals[2].wheel.angularVelocity),
                BoundWheelAngularVelocity(b.WheelVisuals[3].wheel.angularVelocity));
        }
        private static float Slip(float slip) => float.IsNaN(slip) ? 0f : Mathf.Clamp01(Mathf.Abs(slip));
        private static VehicleEffects CaptureEffects(Binding b)
        {
            var v = b.Vehicle;
            var flags = (v.input.states.horn ? VehicleEffectFlags.Horn : 0) | (v.powertrain.engine.revLimiterActive ? VehicleEffectFlags.RevLimiter : 0) |
                (v.isCoolantBoiling ? VehicleEffectFlags.CoolantBoiling : 0);
            var coolant = Coolant(b);
            float heat = coolant?.EngineHeatPercent ?? 0f;
            float radiatorHeat = coolant == null ? 0f : NativeVehicleCoolant.RadiatorHeat(coolant);
            if (b.WheelVisuals.Length != 4) return new VehicleEffects(flags, 0, 0, 0, 0, 0, 0, 0, 0, heat, radiatorHeat);
            var w = b.WheelVisuals;
            return new VehicleEffects(flags,
                Slip(w[0].sideFriction.slip), Slip(w[1].sideFriction.slip), Slip(w[2].sideFriction.slip), Slip(w[3].sideFriction.slip),
                Slip(w[0].forwardFriction.slip), Slip(w[1].forwardFriction.slip), Slip(w[2].forwardFriction.slip), Slip(w[3].forwardFriction.slip), heat, radiatorHeat);
        }
        private static readonly AccessTools.FieldRef<TransmissionComponent, int> CurrentGearIndex =
            AccessTools.FieldRefAccess<TransmissionComponent, int>("_currentGearIndex");
        private static CoolantModule? Coolant(Binding b)
        {
            if (!b.CoolantResolved) { b.Coolant = b.Vehicle.GetComponent<CoolantModuleWrapper>()?.module; b.CoolantResolved = true; }
            return b.Coolant;
        }
        // The driver's engine heat on this copy's coolant module, which runs only for a driver: the
        // dash gauge reads it here, and it is where this machine's engine continues from on taking the wheel.
        private static void PresentHeat(Binding b) => ApplyHeat(b, b.State.Effects);
        private static void ApplyHeat(Binding b, VehicleEffects effects)
        {
            var coolant = Coolant(b);
            if (coolant == null) return;
            NativeVehicleCoolant.Apply(coolant, b.Vehicle, effects);
        }
        // Only the indexed four-wheel replicas with authoritative contact/spin presentation
        // may bypass NWH's independent local tire solver.
        internal bool SuppressesWheelForces(WheelController wheel)
        {
            if (disposed || !Ready || session?.IsRunning != true || wheel == null || !byWheel.TryGetValue(wheel, out var entry)) return false;
            var binding = entry.Binding;
            return !binding.Dormant && binding.State != null && binding.WheelVisuals.Length == 4 && !IsLocal(binding.State) &&
                RemoteWheelForces.NativeReplica(wheel, binding.Vehicle);
        }

        // A presented wheel's driver-side slip, for the smoke, skid sounds and skidmarks that read it.
        internal bool TryGetRemoteWheelSlip(WheelController wheel, out float lateral, out float longitudinal, out VehicleController? vehicle)
        {
            lateral = longitudinal = 0f; vehicle = null;
            if (!Ready || session?.IsRunning != true || wheel == null || !wheel.visualOnlyUpdate) return false;
            if (!byWheel.TryGetValue(wheel, out var entry)) return false;
            var b = entry.Binding;
            if (b.Dormant || b.State == null || IsLocal(b.State) || b.WheelVisuals.Length != 4) return false;
            vehicle = b.Vehicle;
            if (!RemoteEffectsLive(b)) return true;
            lateral = b.State.Effects.Lateral(entry.Index); longitudinal = b.State.Effects.Longitudinal(entry.Index);
            return true;
        }
        // Held-input effects (horn, limiter, slip) only while a driver is sending fresh samples: a driver
        // who dropped with the horn held must not leave it sounding on everyone else's copy.
        private static bool RemoteEffectsLive(Binding b) => b.State.Driver.Owner != ItemLease.Unowned &&
            b.HasPresentationPose && Time.unscaledTime - b.PoseReceivedAt <= 1f;
        private static void PresentEffects(Binding b)
        {
            var v = b.Vehicle;
            bool live = RemoteEffectsLive(b);
            v.input.states.horn = live && b.State.Effects.Horn;
            v.powertrain.engine.revLimiterActive = live && b.State.Effects.RevLimiter;
            // Boiling outlasts the driver: a parked hot car keeps steaming as it did for its last driver.
            v.isCoolantBoiling = b.State.Effects.CoolantBoiling;
            PresentHeat(b);
        }
        private static VehicleWheelAngles CaptureWheelAngles(Binding b)
        {
            if (b.WheelVisuals.Length != 4) return default;
            return new VehicleWheelAngles(b.WheelVisuals[0].wheel.rotationAngle, b.WheelVisuals[1].wheel.rotationAngle,
                b.WheelVisuals[2].wheel.rotationAngle, b.WheelVisuals[3].wheel.rotationAngle);
        }
        // Spin alone let each peer's wheels come to rest at their own roll angle, so the bolt holes on a
        // wheel sat elsewhere on each screen: the other player's bolt went into the "wrong" hole, and the
        // host judged a guest's wrench aim against holes the guest could not see (#184). Once the
        // remote wheels have (nearly) stopped, turn ours to the sender's angle. A sleeping vehicle runs
        // no visual update, so the visual is turned here directly as well.
        private static void AlignWheelAngles(Binding b)
        {
            if (b.WheelVisuals.Length != 4 || b.State == null || b.State.WheelSpin.MaxAbsAngularVelocity > 1f) return;
            for (int i = 0; i < 4; i++)
            {
                var controller = b.WheelVisuals[i];
                var wheel = controller != null ? controller.wheel : null;
                var visual = wheel?.Visual;
                if (wheel == null || visual == null) continue;
                float delta = Mathf.DeltaAngle(wheel.rotationAngle, b.State.WheelAngles[i]);
                if (Mathf.Abs(delta) < 0.75f) continue;
                wheel.rotationAngle = Mathf.Repeat(wheel.rotationAngle + delta, 360f);
                visual.transform.rotation = Quaternion.AngleAxis(delta, controller!.transform.right) * visual.transform.rotation;
            }
        }
        public uint[] Owners => bindings.Values.Select(b => host ? drivers[b.Id].Owner : b.State.Driver.Owner)
            .Concat(bindings.Values.Where(b => b.PassengerId != 0).Select(b => b.PassengerState.Owner))
            .Where(p => p != ItemLease.Unowned).Distinct().ToArray();
        public bool LocalFlushComplete => session != null && !Owners.Contains(session.LocalPeerId) &&
            !exitDeferred && gameplay.Get_CurrentVehicleController == null && gameplay.Get_PlayerState == PlayerState.Walking;
        internal SharedSeatAdmission? SeatAdmission { get; set; }
        public void FlushLocalOwnership()
        {
            SeatAdmission?.CancelLocal(this);
            if (!Ready || session == null) return;
            foreach (var b in bindings.Values) b.RequestedExitDriverSide = null;
            // These native modes also prevent a safe walking-profile capture. Use
            // their normal clicks so shared/cottage release and native settling run.
            if (gameplay.Get_PlayerState == PlayerState.InCreeper && gameplay.Get_CurrentCreeper != null)
                gameplay.Get_CurrentCreeper.MouseDown();
            if (gameplay.Get_PlayerState == PlayerState.InWheelbarrow && gameplay.Get_CurrentWheelBarrow != null)
                gameplay.Get_CurrentWheelBarrow.HandleClick();
            // Let computer/bed animations return the walker to its final native position.
            var needs = UnityEngine.Object.FindObjectOfType<BasicNeeds_Manager>();
            if (gameplay.Get_PlayerState == PlayerState.InFurnitureChair && !gameplay.Get_IsSleeping && needs?.Get_isSaving != true)
            {
                var seat = gameplay.PlayerWalking.transform.parent;
                if (seat != null) gameplay.EnterExitChair(seat);
            }
            foreach (var b in bindings.Values.Where(b => b.State.Driver.Owner == session.LocalPeerId).ToArray()) RequestExit(b);
            foreach (var b in bindings.Values.Where(b => b.PassengerId != 0 && b.PassengerState.Owner == session.LocalPeerId).ToArray()) RequestPassengerExit(b);
        }
        public void RevokeOwnership(uint peer)
        {
            if (!host || session?.IsHost != true) throw new InvalidOperationException("Only the host can revoke vehicle seat ownership.");
            foreach (var b in bindings.Values.Where(b => drivers[b.Id].Owner == peer).ToArray()) Release(b, peer);
            if (passengers.TryReleasePeer(peer, out var vacated))
            {
                var b = passengerBindings.Values.Single(value => value.PassengerKey == vacated.Seat);
                ApplyPassenger(b, vacated); PublishPassenger(b);
            }
        }
        // Explicit session shutdown only. A save uses FlushLocalOwnership directly and
        // does not latch a brake merely because it temporarily drains seat ownership.
        internal void PrepareLocalDisconnect()
        {
            if (disposed || session == null) return;
            foreach (var b in bindings.Values) b.RequestedExitDriverSide = null;
            foreach (var b in bindings.Values.Where(b => b.State.Driver.Owner != ItemLease.Unowned &&
                (host || b.State.Driver.Owner == session.LocalPeerId)).ToArray())
            {
                b.DisconnectSecuring = true;
                NativeVehicleDeparture.SetParkingBrake(b.Vehicle, ParkingBrakeOf(b));
                if (host) b.State = NativeVehicleDeparture.Parked(IsLocal(b.State) ? Capture(b) : b.State);
            }
        }
        internal void CancelLocalDisconnect()
        {
            foreach (var b in bindings.Values) b.DisconnectSecuring = false;
        }
        internal bool Applying => applying;
        internal event Action<uint, Vector3>? DriverExited;
        internal bool ApplyingMechanics => applyingMechanics;
        private static bool ProgressionLocked(Vehicle_Type type)
        {
            var todo = Singleton<ToDoList_Manager>.i;
            if (todo == null) return false;
            return type == Vehicle_Type.ScrapyardKonig ? !todo.isDone(ToDoList_Line.Intermediate_UnlockScrapKonig) :
                type == Vehicle_Type.Prinz ? !todo.isDone(ToDoList_Line.Advanced_Prinz_Unlocked) :
                type == Vehicle_Type.LeMissile && !todo.isDone(ToDoList_Line.Intermediate_ScrapyardGateIsOpen);
        }
        public SharedVehicles(Gameplay gameplay, bool host, string diagnostics, NativeEntityRegistry identities)
        {
            this.gameplay = gameplay; this.host = host; this.identities = identities;
            if (gameplay.Get_CurrentVehicleController != null) throw new InvalidOperationException("Leave your vehicle before hosting or joining.");
            carLifts = new CarLiftVehicleIndex(UnityEngine.Object.FindObjectsOfType<Furniture_CarLift>(true));
            var lines = new List<string>();
            // Starters the host keeps in a storage garage are inactive on both sides but still bound,
            // so taking one out of storage later drives the shared copy.
            // A menu-joining guest's fresh world takes the host's set; a guest joining from its own
            // loaded world has its own garages (the catalog check then compares both).
            var hostWorld = host ? null : MonMPPlugin.Instance?.GuestWorld?.Configuration;
            HardModeWorldLayout.SessionStored = hostWorld != null ? hostWorld.StoredVehicles : HardModeWorldLayout.CaptureStoredVehicles();
            var candidates = UnityEngine.Object.FindObjectsOfType<VehicleController>().Concat(HardModeWorldLayout.SessionStoredStarters()).ToList();
            var globalVehicles = UnityEngine.Object.FindObjectOfType<GlobalClassManager>();
            var nativeScrapyardVehicle = globalVehicles?.GetVehicles(Vehicle_Type.ScrapyardKonig) as VehicleController;
            var nativePrinzVehicle = SharedVehicleParts.CanonicalPrinzVehicle().SingleOrDefault();
            if (nativeScrapyardVehicle != null && nativeScrapyardVehicle.gameObject.scene.IsValid())
            {
                if (!candidates.Contains(nativeScrapyardVehicle)) candidates.Add(nativeScrapyardVehicle);
            }
            else lines.Add("scrapyard-konig\t-\tskipped-canonical-native-vehicle-unavailable");
            if (nativePrinzVehicle != null)
            {
                if (!candidates.Contains(nativePrinzVehicle)) candidates.Add(nativePrinzVehicle);
            }
            else lines.Add("prinz\t-\tskipped-canonical-native-vehicle-unavailable");
            var buggyVehicle = SharedBuggyBuild.Current?.CurrentVehicle;
            if (buggyVehicle != null && !candidates.Contains(buggyVehicle)) candidates.Add(buggyVehicle);
            // The speedway racecar was never bound: each peer drove its own copy, and seated players were
            // posed from their walking pose (on the hood, behind the seat).
            var racecarVehicle = SharedRacecarBuild.Current?.CurrentVehicle;
            if (racecarVehicle != null && !candidates.Contains(racecarVehicle)) candidates.Add(racecarVehicle);
            // Le Missile is bound whether or not it is active yet (scrapyard progression, storage garage),
            // so both peers register the same catalog and pulling it out later drives the shared copy.
            foreach (var missileCandidate in UnityEngine.Object.FindObjectsOfType<VehicleController>(true))
                if (missileCandidate.vehicle_type == Vehicle_Type.LeMissile && missileCandidate.gameObject.scene.IsValid() &&
                    !missileCandidate.gameObject.name.EndsWith("(Clone)") && !candidates.Contains(missileCandidate))
                    candidates.Add(missileCandidate);
            foreach (var v in candidates)
            {
                var sales = SharedVehicleSales.Active;
                bool saleClone = sales?.IsVehicleSaleClone(v.gameObject) == true;
                Vehicle_Type saleType = default;
                ulong saleEntity = 0;
                bool hasSaleEntity = sales != null && sales.TryGetVehicleEntity(v.gameObject, out saleType, out saleEntity);
                if (saleClone && !hasSaleEntity) continue;
                bool truck = v.vehicle_type == Vehicle_Type.OlTruck;
                bool racecar = v.vehicle_type == Vehicle_Type.Racecar && v == racecarVehicle;
                bool buggy = v.vehicle_type == Vehicle_Type.Buggy || racecar;
                bool scrapyardKonig = v.vehicle_type == Vehicle_Type.ScrapyardKonig;
                bool prinz = v.vehicle_type == Vehicle_Type.Prinz;
                bool nativeKonigShell = scrapyardKonig || prinz;
                bool missile = v.vehicle_type == Vehicle_Type.LeMissile;
                if ((!truck && !buggy && !scrapyardKonig && !prinz && !missile && v.vehicle_type != Vehicle_Type.SmollATV) || !v.gameObject.scene.IsValid()) continue;
                if (scrapyardKonig && nativeScrapyardVehicle != v) continue;
                if (prinz && nativePrinzVehicle != v) continue;
                if (hasSaleEntity && saleType != v.vehicle_type)
                    throw new InvalidOperationException("Purchased vehicle sale identity type does not match its VehicleController.");
                // Guests defer every buggy until the host's build snapshot arrives. This lets a host
                // with an unbuilt buggy join a guest whose local save has already completed it.
                bool deferBuggy = buggy && (!host || v.vehicleRigidbody == null || v.vehicleRigidbody.isKinematic || !v.enabled ||
                    !v.gameObject.activeInHierarchy || !NativeMotionInitialized(v));
                bool stored = HardModeWorldLayout.IsSessionStored(v) || (missile && !v.gameObject.activeInHierarchy);
                // Wheel references are serialized before VehicleController.Start. They do not
                // prove its powertrain can be captured or switched to remote simulation. Reserve
                // the same Missile identity on both peers while native startup is pending.
                bool deferMissile = missile && (!host || !v.gameObject.activeInHierarchy || !v.enabled ||
                    v.vehicleRigidbody == null || v.vehicleRigidbody.isKinematic || !NativeMotionInitialized(v));
                bool deferMotion = deferBuggy || deferMissile;
                var seats = v.GetComponentsInChildren<Vehicle_DriverSeat>(buggy || nativeKonigShell || missile || stored);
                var shellKonigSeats = nativeKonigShell ? v.GetComponentsInChildren<Konig_Seat>(true) : Array.Empty<Konig_Seat>();
                var scrapyardDrivers = shellKonigSeats.Where(seat => seat != null && seat.Get_isDriverSeat).ToArray();
                bool standardSeatSupported = seats.Length == 1 && seats[0] != null && seats[0].seatPosition != null &&
                    seats[0].vehicleController == v && (!nativeKonigShell || (scrapyardDrivers.Length == 0 &&
                    seats[0].isActiveAndEnabled && seats[0].seatPosition.gameObject.activeInHierarchy));
                bool scrapyardSeatSupported = nativeKonigShell && seats.Length == 0 && scrapyardDrivers.Length == 1 &&
                    scrapyardDrivers[0].seatPosition != null && scrapyardDrivers[0].vehicleController == v;
                bool supportedDriverShape = nativeKonigShell ? (standardSeatSupported ^ scrapyardSeatSupported) : standardSeatSupported;
                Vehicle_DriverSeat? standardDriverSeat = standardSeatSupported ? seats[0] : null;
                Konig_Seat? scrapyardDriverSeat = scrapyardSeatSupported ? scrapyardDrivers[0] : null;
                Transform? driverPosition = scrapyardDriverSeat != null ? scrapyardDriverSeat.seatPosition : standardDriverSeat?.seatPosition;
                var hitch = v.GetComponent<TrailerHitchModuleWrapper>()?.module;
                // Native shell hinges are independently shape-validated below. Their supported body
                // count is normalized only after that exact saved hood/trunk/door configuration passes.
                var bodies = nativeKonigShell ? VehicleBodies(v, true) : VehicleBodies(v, buggy || missile || stored);
                var wheels = v.Wheels;
                var accessory = truck ? bodies.FirstOrDefault(r => r != v.vehicleRigidbody && r.name == "Torus" && r.GetComponent<ConfigurableJoint>()?.connectedBody == v.vehicleRigidbody) : null;
                string truckBodyReason = "";
                bool truckBodiesSupported = !truck || HasSupportedTruckBodyConfiguration(v, v.vehicleRigidbody,
                    accessory, bodies, stored, out truckBodyReason);
                // A save reload leaves a vehicle still locked behind progression kinematic (the native load
                // releases only unlocked ones), while a menu-joining guest's fresh world has it dynamic.
                bool progressionKinematic = v.vehicleRigidbody != null && v.vehicleRigidbody.isKinematic && ProgressionLocked(v.vehicle_type);
                bool commonUnsupportedShape = !nativeKonigShell &&
                    ((!deferMotion && ((!truck && !buggy && !v.NoDriverDoor) || v.vehicleRigidbody == null ||
                        (v.vehicleRigidbody.isKinematic && !(missile && progressionKinematic)) || v.vehicle_PartsManager?.Fuel_Tank == null || v.Wheels!.Count != 4 ||
                        (truck ? !truckBodiesSupported : buggy ? !HasSupportedBuiltVehicleBodyConfiguration(v, bodies) : bodies.Length != 1) ||
                        (truck && (accessory == null || accessory.isKinematic)))) ||
                     !supportedDriverShape || (deferMissile && (!v.NoDriverDoor || bodies.Length != 1 ||
                        bodies[0] != (v.vehicleRigidbody != null ? v.vehicleRigidbody : v.GetComponent<Rigidbody>()) ||
                        v.GetComponentsInChildren<VehicleController>(true).Length != 1)));
                bool nativeShellDormant = false;
                if (nativeKonigShell)
                {
                    bool hingesSupported = SharedVehicleTrunk.HasSupportedKonigBodyConfiguration(v);
                    var shape = new NativeKonigShellMotionShape(
                        isCanonicalVehicle: scrapyardKonig ? nativeScrapyardVehicle == v : nativePrinzVehicle == v,
                        isInLoadedScene: v.gameObject.scene.IsValid(),
                        rootActive: v.gameObject.activeInHierarchy,
                        controllerEnabled: v.enabled,
                        rootBodyPresent: v.vehicleRigidbody != null,
                        // A save reload can leave the still-locked native shell kinematic. It is
                        // catalogued dormant and promoted only after the native unlock releases it.
                        rootBodyDynamic: v.vehicleRigidbody != null && (!v.vehicleRigidbody.isKinematic || FrozenOnCarLift(v)),
                        hasFuelTank: v.vehicle_PartsManager?.Fuel_Tank != null,
                        hasSupportedSavedHinges: hingesSupported,
                        progressionLocked: progressionKinematic,
                        controllerCount: v.GetComponentsInChildren<VehicleController>(true).Length,
                        rigidbodyCount: hingesSupported ? 1 : bodies.Length,
                        wheelCount: wheels == null ? 0 : wheels.Count,
                        wheelVisualCount: wheels == null ? 0 : wheels.Count(wheel => wheel != null && wheel.wheelController != null));
                    nativeShellDormant = NativeKonigShellSupportPolicy.CanDeferActivation(shape);
                    if (!nativeShellDormant && !NativeKonigShellSupportPolicy.SupportsMotion(shape))
                    {
                        lines.Add((prinz ? "prinz" : "scrapyard-konig") + "\t" + RuntimeProbe.CanonicalPath(v.transform) +
                            "\tskipped-unsupported-motion-shape;active=" + v.gameObject.activeInHierarchy +
                            ";controllerEnabled=" + shape.ControllerEnabled + ";rootDynamic=" + shape.RootBodyDynamic +
                            ";fuel=" + shape.HasFuelTank + ";savedHinges=" + hingesSupported + ";controllers=" + shape.ControllerCount +
                            ";rigidbodies=" + shape.RigidbodyCount + ";wheels=" + shape.WheelCount +
                            ";wheelVisuals=" + shape.WheelVisualCount);
                        continue;
                    }
                }
                else if (commonUnsupportedShape)
                {
                    if (missile)
                    {
                        lines.Add("le-missile	" + RuntimeProbe.CanonicalPath(v.transform) + "	skipped-unsupported-shape;active=" +
                            v.gameObject.activeInHierarchy + ";seats=" + seats.Length + ";bodies=" + bodies.Length);
                        continue;
                    }
                    if (truck)
                        Debug.LogWarning("MonMP OlTruck registration rejected: path=" + RuntimeProbe.CanonicalPath(v.transform) +
                            "; seats=" + seats.Length + "; driverShape=" + supportedDriverShape +
                            "; root=" + (v.vehicleRigidbody == null ? "missing" : v.vehicleRigidbody.isKinematic ? "kinematic" : "dynamic") +
                            "; accessory=" + (accessory == null ? "missing" : accessory.isKinematic ? "kinematic" : "dynamic") +
                            "; fuel=" + (v.vehicle_PartsManager?.Fuel_Tank != null) + "; wheels=" + (wheels?.Count ?? 0) +
                            "; bodyShape=" + (truckBodiesSupported ? "supported" : truckBodyReason));
                    throw new InvalidOperationException("Unsupported " + v.vehicle_type + " vehicle body/seat configuration.");
                }
                // A fresh guest's unlocked/dynamic default is not evidence that the host's
                // saved shell is ready. Wait for its explicit lifecycle flags before promotion.
                if (nativeKonigShell && !host) nativeShellDormant = true;
                string identity = hasSaleEntity ? PurchasedIdentity(saleEntity, saleType, "vehicle") : scrapyardKonig
                    ? ScrapyardKonigSupportPolicy.VehicleIdentity(RuntimeProbe.CanonicalPath(v.transform)) : prinz
                    ? PrinzSupportPolicy.VehicleIdentity(RuntimeProbe.CanonicalPath(v.transform))
                    : RuntimeProbe.CanonicalPath(v.transform) + (truck ? "/truck-driver-v1/" : racecar ? "/racecar-driver-v1/" : buggy ? "/buggy-driver-v1/" : missile ? "/missile-driver-v1/" : "/atv-driver-v1/") + RuntimeProbe.CanonicalPath(driverPosition!);
                if (accessory != null) identity += "/accessory/" + RuntimeProbe.CanonicalPath(accessory.transform);
                ulong vehicleId = hasSaleEntity ? saleEntity : scrapyardKonig
                    ? ScrapyardKonigSupportPolicy.DynamicEntityId(identity) : prinz
                    ? PrinzSupportPolicy.DynamicEntityId(identity)
                    : StaticId.Hash(identity);
                var originalBody = v.vehicleRigidbody != null ? v.vehicleRigidbody : v.GetComponent<Rigidbody>();
                var b = new Binding { Id = vehicleId, Vehicle = v, Seat = standardDriverSeat,
                    ScrapyardKonigDriverSeat = scrapyardDriverSeat, KonigDriverSeat = scrapyardDriverSeat, AutoInput = v.input.autoSetInput,
                    FreezeIdle = v.freezeWhileIdle, FreezeSleep = v.freezeWhileAsleep,
                    ExternalWheels = deferMotion || nativeShellDormant ? Array.Empty<bool>() : CaptureExternalWheels(v),
                    WheelVisuals = deferMotion || nativeShellDormant ? Array.Empty<WheelController>() : CaptureWheelVisualOrder(v),
                    Hitch = hitch, HitchEnabled = hitch?.IsOn == true, Accessory = accessory, Buggy = buggy, Racecar = racecar,
                    DeferredAtStart = (buggy || missile) && !host, OriginalMode = v.VehicleMultiplayerInstanceType,
                    OriginalConstraints = originalBody != null ? originalBody.constraints : RigidbodyConstraints.None,
                    OriginalKinematic = originalBody == null ||
                        (originalBody.isKinematic && !(nativeKonigShell && FrozenOnCarLift(v))),
                    OriginalCollision = originalBody != null ? originalBody.collisionDetectionMode : CollisionDetectionMode.Discrete,
                    OriginalInterpolation = originalBody != null ? originalBody.interpolation : v.interpolation,
                    OriginalRootActive = v.gameObject.activeSelf, OriginalShellEnabled = v.enabled,
                    OriginalShellPosition = v.transform.position, OriginalShellRotation = v.transform.rotation,
                    OriginalVehicleEnabled = v.enabled, Dormant = deferMotion || nativeShellDormant,
                    OriginalCaptured = !deferMotion && !nativeShellDormant };
                if (missile && !deferMissile && b.WheelVisuals.Length != 4)
                {
                    lines.Add("le-missile	" + identity + "	skipped-wheel-visuals=" + b.WheelVisuals.Length);
                    continue;
                }
                if (!deferMotion && !nativeShellDormant && b.WheelVisuals.Length != 4)
                    throw new InvalidOperationException("Unsupported " + v.vehicle_type + " four-wheel visual mapping.");
                drivers.Add(b.Id);
                if (deferMotion || nativeShellDormant) b.State = b.Original = DormantState(b, drivers[b.Id]);
                else { b.State = b.Original = Capture(b); b.OriginalCaptured = true; }
                bindings.Add(b.Id, b);
                IndexBinding(b);
                identities.Bind(b.Id, VehicleState.Adapter, v.gameObject, identity, !hasSaleEntity,
                    hasSaleEntity ? EntityPersistence.SessionOnly : EntityPersistence.NativeSave);
                string bindingState = deferMotion || nativeShellDormant ? "dormant" : nativeKonigShell
                    ? (supportedDriverShape ? "bound-seat-ready" : "bound-seat-unavailable") + ";activeInHierarchy=" + v.gameObject.activeInHierarchy
                    : "active";
                lines.Add(b.Id.ToString("X16") + "\t" + identity + "\t" + bindingState);
            }
            var konig = candidates
                .FirstOrDefault(v => v.vehicle_type == Vehicle_Type.MainVehicle && v.gameObject.scene.IsValid());
            var seatAdjustmentControlRows = new List<string>
            {
                "vehiclePath\tseatRole\tseatPath\tcontrolType\tcontrolPath\tactiveSelf\tactiveInHierarchy\tenabled\treferencedPartPath\treferencedPartSeatPath\tresult"
            };
            if (konig != null)
            {
                var nativeSeats = konig.GetComponentsInChildren<Konig_Seat>(true);
                var driverSeat = nativeSeats.SingleOrDefault(seat => seat.Get_isDriverSeat);
                var passengerSeat = nativeSeats.SingleOrDefault(seat => !seat.Get_isDriverSeat);
                Part_Seat[] driverParts = driverSeat == null ? Array.Empty<Part_Seat>() : driverSeat.GetComponentsInChildren<Part_Seat>(true)
                    .Where(part => part != null && part.isPlaced && (part.transform == driverSeat.transform || part.transform.IsChildOf(driverSeat.transform))).ToArray();
                Part_Seat? driverPart = driverParts.Length == 1 ? driverParts[0] : null;
                var forwardButtons = driverSeat == null ? Array.Empty<Part_Seat_Forward>() : DiscoverSeatAdjustmentControls<Part_Seat_Forward>(
                    konig, driverSeat, driverPart, seatAdjustmentControlRows, true);
                var heightButtons = driverSeat == null ? Array.Empty<Part_Seat_Height>() : DiscoverSeatAdjustmentControls<Part_Seat_Height>(
                    konig, driverSeat, driverPart, seatAdjustmentControlRows, true);
                Part_Seat[] passengerParts = passengerSeat == null ? Array.Empty<Part_Seat>() : passengerSeat.GetComponentsInChildren<Part_Seat>(true)
                    .Where(part => part != null && part.isPlaced && (part.transform == passengerSeat.transform || part.transform.IsChildOf(passengerSeat.transform))).ToArray();
                Part_Seat? passengerPart = passengerParts.Length == 1 ? passengerParts[0] : null;
                var passengerForwardButtons = passengerSeat == null ? Array.Empty<Part_Seat_Forward>() : DiscoverSeatAdjustmentControls<Part_Seat_Forward>(
                    konig, passengerSeat, passengerPart, seatAdjustmentControlRows, true);
                var passengerHeightButtons = passengerSeat == null ? Array.Empty<Part_Seat_Height>() : DiscoverSeatAdjustmentControls<Part_Seat_Height>(
                    konig, passengerSeat, passengerPart, seatAdjustmentControlRows, true);
                if (driverSeat != null && driverParts.Length != 1)

#if MONMP_DEVELOPMENT
                    seatAdjustmentControlRows.Add(ControlDiagnosticRow(konig, driverSeat, "driver", "Part_Seat", "-", false, false, false, "-", "-",
                        "unavailable: expected exactly one installed Part_Seat in the driver-seat hierarchy, found " + driverParts.Length));
#else
{}
#endif

                if (passengerSeat != null && passengerParts.Length != 1)

#if MONMP_DEVELOPMENT
                    seatAdjustmentControlRows.Add(ControlDiagnosticRow(konig, passengerSeat, "passenger", "Part_Seat", "-", false, false, false, "-", "-",
                        "unavailable: expected exactly one installed Part_Seat in the passenger-seat hierarchy, found " + passengerParts.Length));
#else
{}
#endif

                var body = konig.vehicleRigidbody;
                var hitch = konig.GetComponent<TrailerHitchModuleWrapper>()?.module;
                if (nativeSeats.Length != 2 || driverSeat == null || passengerSeat == null ||
                    driverSeat.vehicleController != konig || passengerSeat.vehicleController != konig ||
                    driverSeat.seatPosition == null || passengerSeat.seatPosition == null ||
                    body == null || (body.isKinematic && !FrozenOnCarLift(konig)) || konig.vehicle_PartsManager?.Fuel_Tank == null ||
                    konig.vehicle_PartsManager.Get_CustomParameterToSave == null ||
                    konig.vehicle_PartsManager.Get_CustomParameterToSave.Length < 2 ||
                    konig.Wheels.Count != 4 || !SharedVehicleTrunk.HasSupportedKonigBodyConfiguration(konig))
                    throw new InvalidOperationException("Unsupported starting Konig seat or body configuration: " +
                        KonigUnsupportedReasons(konig, nativeSeats, driverSeat, passengerSeat, body) + ".");
                string identity = RuntimeProbe.CanonicalPath(konig.transform) + "/konig-driver-v1/" + RuntimeProbe.CanonicalPath(driverSeat.seatPosition);
                var b = new Binding { Id = StaticId.Hash(identity), Vehicle = konig,
                    KonigDriverSeat = driverSeat, KonigPassengerSeat = passengerSeat,
                    AutoInput = konig.input.autoSetInput, FreezeIdle = konig.freezeWhileIdle, FreezeSleep = konig.freezeWhileAsleep,
                    ExternalWheels = CaptureExternalWheels(konig), WheelVisuals = CaptureWheelVisualOrder(konig),
                    Hitch = hitch, HitchEnabled = hitch?.IsOn == true,
                    OriginalMode = konig.VehicleMultiplayerInstanceType, OriginalConstraints = body.constraints,
                    OriginalKinematic = body.isKinematic && !FrozenOnCarLift(konig), OriginalCollision = body.collisionDetectionMode,
                    OriginalInterpolation = body.interpolation,
                    OriginalVehicleEnabled = konig.enabled, OriginalCaptured = true };
                if (b.ExternalWheels.Length != 4 || b.WheelVisuals.Length != 4)
                    throw new InvalidOperationException("Starting Konig wheel visual/update mapping is unavailable.");
                string driverAdjustmentIdentity = RuntimeProbe.CanonicalPath(driverSeat.transform) + "/konig-seat-adjustment-v1";
                b.DriverSeatAdjustment = CreateSeatAdjustmentBinding(b, driverSeat, StaticId.Hash(driverAdjustmentIdentity),
                    true, forwardButtons, heightButtons);
                string passengerAdjustmentIdentity = RuntimeProbe.CanonicalPath(passengerSeat.transform) + "/konig-seat-adjustment-v1";
                b.PassengerSeatAdjustment = CreateSeatAdjustmentBinding(b, passengerSeat, StaticId.Hash(passengerAdjustmentIdentity),
                    true, passengerForwardButtons, passengerHeightButtons);
                b.PassengerKey = new SeatKey(b.Id, "konig-passenger");
                b.PassengerId = StaticId.Hash(RuntimeProbe.CanonicalPath(passengerSeat.transform) + "/konig-passenger-v1");
                var occupancy = passengers.Register(b.PassengerKey);
                b.PassengerState = new VehiclePassengerSeatState(occupancy.Peer, occupancy.Generation);
                drivers.Add(b.Id);
                b.State = b.Original = Capture(b);
                bindings.Add(b.Id, b);
                IndexBinding(b);
                passengerBindings.Add(b.PassengerId, b);
                identities.Bind(b.Id, VehicleState.Adapter, konig.gameObject, identity, true);
                identities.Bind(b.PassengerId, VehiclePassengerSeatState.Adapter, passengerSeat.gameObject,
                    RuntimeProbe.CanonicalPath(passengerSeat.transform) + "/konig-passenger-v1", true, EntityPersistence.SessionOnly);
                BindSeatAdjustmentIdentity(b.DriverSeatAdjustment!, driverSeat.gameObject, driverAdjustmentIdentity, true);
                BindSeatAdjustmentIdentity(b.PassengerSeatAdjustment!, passengerSeat.gameObject, passengerAdjustmentIdentity, true);
                lines.Add(b.Id.ToString("X16") + "\t" + identity + "\tactive");
                lines.Add(b.PassengerId.ToString("X16") + "\t" + b.PassengerKey + "\tpassenger");
                lines.Add(b.DriverSeatAdjustment!.Id.ToString("X16") + "\t" + driverAdjustmentIdentity + "\tseat-adjustment-driver" +
                    (b.DriverSeatAdjustment.ForwardButton != null && b.DriverSeatAdjustment.HeightButton != null ? "\tcontrols-ready" : "\tcontrols-unavailable"));
                lines.Add(b.PassengerSeatAdjustment!.Id.ToString("X16") + "\t" + passengerAdjustmentIdentity + "\tseat-adjustment-passenger" +
                    (b.PassengerSeatAdjustment.ForwardButton != null && b.PassengerSeatAdjustment.HeightButton != null ? "\tcontrols-ready" : "\tcontrols-unavailable"));
            }
            else
                seatAdjustmentControlRows.Add("-\t-\t-\t-\t-\tfalse\tfalse\tfalse\t-\t-\tstarting Konig VehicleController not found");
            foreach (var shell in bindings.Values.Where(binding => IsNativeKonigShell(binding.Vehicle.vehicle_type)))
                BindNativeKonigShellSeats(shell, seatAdjustmentControlRows);
            var accessVehicles = bindings.Values.Select(b => b.Vehicle);
            Doors = new SharedVehicleDoors(gameplay, host, bindings.Values.Select(b => b.Vehicle), konig, diagnostics, identities,
                v => Find(v)?.Dormant == true);
            Doors.LateVehicles = () => bindings.Values.Where(b => b.Id > WorldRecord.DynamicIdStart && !b.Dormant && b.Vehicle != null &&
                b.Vehicle.vehicle_type == Vehicle_Type.OlTruck).Select(b => (b.Id, b.Vehicle));
            Windows = new SharedVehicleWindows(gameplay, host, bindings.Values.Select(b => b.Vehicle), identities);
            Hood = new SharedVehicleHood(gameplay, host, accessVehicles, diagnostics, identities,
                v => Find(v)?.Dormant == true);
            Trunk = new SharedVehicleTrunk(gameplay, host, accessVehicles, diagnostics, identities,
                v => Find(v)?.Dormant == true);

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-konig-seat-adjustment-controls.tsv"), seatAdjustmentControlRows);


            File.WriteAllLines(Path.Combine(diagnostics, "shared-vehicles.tsv"), new[] { "id\tvehicle-seat-identity\tstate" }.Concat(lines.OrderBy(s => s)));
#endif

        }

        private static string PurchasedIdentity(ulong entity, Vehicle_Type type, string role) =>
            "vehicle-sale-instance-v1/" + entity.ToString("X16") + "/" + (int)type + "/" + role;

        private readonly CarLiftVehicleIndex carLifts;

        // A save with a raised lift reloads with the vehicle frozen kinematic on it (SaveSystem
        // calls FreezeUnfreezeVehicleOnLift). The lift, not the vehicle, owns that state.
        internal bool FrozenOnCarLift(Vehicle vehicle) => carLifts.Contains(vehicle);
        internal void ObserveCarLift(Furniture_CarLift lift) { if (!disposed) carLifts.Observe(lift); }

        private string KonigUnsupportedReasons(VehicleController konig, Konig_Seat[] seats, Konig_Seat? driver, Konig_Seat? passenger, Rigidbody? body)
        {
            var reasons = new List<string>();
            if (seats.Length != 2) reasons.Add("seats=" + seats.Length);
            if (driver == null) reasons.Add("no driver seat");
            if (passenger == null) reasons.Add("no passenger seat");
            if (driver != null && (driver.vehicleController != konig || driver.seatPosition == null)) reasons.Add("driver seat unbound");
            if (passenger != null && (passenger.vehicleController != konig || passenger.seatPosition == null)) reasons.Add("passenger seat unbound");
            if (body == null) reasons.Add("no rigidbody");
            else if (body.isKinematic && !FrozenOnCarLift(konig)) reasons.Add("rigidbody kinematic (still loading?)");
            if (konig.vehicle_PartsManager?.Fuel_Tank == null) reasons.Add("no fuel tank");
            var parameters = konig.vehicle_PartsManager?.Get_CustomParameterToSave;
            if (parameters == null || parameters.Length < 2) reasons.Add("seat parameters=" + (parameters?.Length ?? -1));
            if (konig.Wheels.Count != 4) reasons.Add("wheels=" + konig.Wheels.Count);
            if (!SharedVehicleTrunk.HasSupportedKonigBodyConfiguration(konig))
                reasons.Add("extra bodies=" + string.Join(",", konig.GetComponentsInChildren<Rigidbody>(true)
                    .Where(r => r != body).Select(r => r.name + (r.isKinematic ? "(kinematic)" : "") +
                        (r.GetComponent<Joint>() != null ? "(joint)" : "") + (r.GetComponent<InteractableItems>() != null ? "(item)" : ""))));
            return string.Join("; ", reasons);
        }

        private static bool IsPurchasedVehicleType(Vehicle_Type type) =>
            type == Vehicle_Type.OlTruck || type == Vehicle_Type.SmollATV;

        internal static bool SupportsPurchasedVehicle(Vehicle_Type type, GameObject instance, bool requireScene = true)
        {
            if (!IsPurchasedVehicleType(type) || instance == null || (requireScene && !instance.scene.IsValid())) return false;
            var controllers = instance.GetComponentsInChildren<VehicleController>(true);
            if (controllers.Length != 1) return false;
            var vehicle = controllers[0];
            if (vehicle == null || vehicle.vehicle_type != type) return false;
            // The store references an uninstantiated native prefab. NWH.Vehicle.Awake assigns
            // these two caches from components, so validating an asset must read the same
            // components without running Awake (which would register/mutate a native vehicle).
            // Live scene instances retain the strict initialized-cache contract.
            var body = requireScene ? vehicle.vehicleRigidbody : vehicle.GetComponent<Rigidbody>();
            var parts = requireScene ? vehicle.vehicle_PartsManager : vehicle.GetComponent<Vehicle_PartsManager>();
            if (body == null || body.isKinematic || parts?.Fuel_Tank == null ||
                vehicle.Wheels == null || vehicle.Wheels.Count != 4 ||
                vehicle.Wheels.Any(w => w == null || w.wheelController == null)) return false;
            if (type == Vehicle_Type.SmollATV && !vehicle.NoDriverDoor) return false;

            var seats = vehicle.GetComponentsInChildren<Vehicle_DriverSeat>(true);
            if (seats.Length != 1 || seats[0] == null || seats[0].seatPosition == null || seats[0].vehicleController != vehicle) return false;

            var bodies = VehicleBodies(vehicle);
            if (type == Vehicle_Type.SmollATV) return bodies.Length == 1;

            var accessory = bodies.FirstOrDefault(candidate => candidate != body && candidate.name == "Torus" &&
                candidate.GetComponent<ConfigurableJoint>()?.connectedBody == body);
            if (accessory == null || accessory.isKinematic) return false;
            return HasSupportedTruckBodyConfiguration(vehicle, body, accessory, bodies, true, out _);
        }

        // Called for sale clones discovered before channel creation and for clones reconstructed
        // after a sale update. The supplied sale entity is the VehicleState ID for cars/ATVs.
        internal bool HasPurchasedVehicleBinding(ulong entity, Vehicle_Type type, GameObject instance) =>
            entity > WorldRecord.DynamicIdStart && IsPurchasedVehicleType(type) && instance != null &&
            bindings.TryGetValue(entity, out var binding) && !binding.Dormant && binding.Vehicle != null &&
            binding.Vehicle.gameObject == instance && binding.Vehicle.vehicle_type == type;

        internal bool RegisterPurchasedVehicle(ulong entity, Vehicle_Type type, GameObject instance)
        {
            if (!IsPurchasedVehicleType(type)) return false;
            if (entity <= WorldRecord.DynamicIdStart) throw new InvalidOperationException("Purchased vehicle entity is not dynamic.");
            if (instance == null || !instance.scene.IsValid()) throw new InvalidOperationException("Purchased vehicle instance is outside the active scene.");

            if (bindings.TryGetValue(entity, out var existing))
            {
                if (existing.Vehicle.gameObject != instance || existing.Vehicle.vehicle_type != type)
                    throw new InvalidOperationException("Conflicting purchased vehicle binding for entity " + entity.ToString("X16") + ".");
                return true;
            }
            var alreadyBound = bindings.Values.FirstOrDefault(binding => binding.Vehicle.gameObject == instance);
            if (alreadyBound != null)
            {
                if (alreadyBound.Id != entity) throw new InvalidOperationException("Purchased vehicle already has a different shared movement identity.");
                return true;
            }

            if (!TryInspectPurchasedVehicle(type, instance, allowUninitializedWheels: true, out var vehicle, out var truck,
                out var seat, out var accessory, out bool wheelsReady))
            {
                pendingPurchasedVehicles.Remove(entity);
                return false;
            }
            if (!wheelsReady)
            {
                pendingPurchasedVehicles[entity] = (type, instance);
                return true;
            }
            pendingPurchasedVehicles.Remove(entity);
            AddPurchasedVehicle(entity, type, instance, vehicle!, truck, seat!, accessory);
            return true;
        }

        private static bool TryInspectPurchasedVehicle(Vehicle_Type type, GameObject instance, bool allowUninitializedWheels,
            out VehicleController? vehicle, out bool truck, out Vehicle_DriverSeat? seat,
            out Rigidbody? accessory, out bool wheelsReady)
        {
            vehicle = null; truck = type == Vehicle_Type.OlTruck; seat = null; accessory = null; wheelsReady = false;
            if (!IsPurchasedVehicleType(type) || instance == null || !instance.scene.IsValid()) return false;
            var controllers = instance.GetComponentsInChildren<VehicleController>(true);
            if (controllers.Length != 1 || controllers[0] == null) return false;
            vehicle = controllers[0];
            if (vehicle.vehicle_type != type || vehicle.vehicleRigidbody == null || vehicle.vehicleRigidbody.isKinematic ||
                vehicle.vehicle_PartsManager?.Fuel_Tank == null) return false;
            if (type == Vehicle_Type.SmollATV && !vehicle.NoDriverDoor) return false;

            var seats = vehicle.GetComponentsInChildren<Vehicle_DriverSeat>(true);
            if (seats.Length != 1 || seats[0] == null || seats[0].seatPosition == null || seats[0].vehicleController != vehicle) return false;
            seat = seats[0];

            var bodies = VehicleBodies(vehicle);
            if (!truck)
            {
                if (bodies.Length != 1) return false;
            }
            else
            {
                foreach (var body in bodies)
                {
                    if (body != vehicle.vehicleRigidbody && body.name == "Torus" &&
                        body.GetComponent<ConfigurableJoint>()?.connectedBody == vehicle.vehicleRigidbody)
                    {
                        accessory = body;
                        break;
                    }
                }
                if (accessory == null || accessory.isKinematic) return false;
                if (!HasSupportedTruckBodyConfiguration(vehicle, vehicle.vehicleRigidbody, accessory, bodies, true, out _)) return false;
            }

            if (vehicle.Wheels == null || vehicle.Wheels.Count == 0)
                return allowUninitializedWheels;
            if (vehicle.Wheels.Count != 4 || vehicle.Wheels.Any(w => w == null || w.wheelController == null)) return false;
            wheelsReady = true;
            return true;
        }

        private void AddPurchasedVehicle(ulong entity, Vehicle_Type type, GameObject instance, VehicleController vehicle,
            bool truck, Vehicle_DriverSeat seat, Rigidbody? accessory)
        {
            if (Find(vehicle) != null) throw new InvalidOperationException("Purchased vehicle is already bound under another movement identity.");
            var identity = PurchasedIdentity(entity, type, "vehicle");
            var hitch = vehicle.GetComponent<TrailerHitchModuleWrapper>()?.module;
            var original = new Binding
            {
                Id = entity,
                Vehicle = vehicle,
                Seat = seat,
                AutoInput = vehicle.input.autoSetInput,
                FreezeIdle = vehicle.freezeWhileIdle,
                FreezeSleep = vehicle.freezeWhileAsleep,
                ExternalWheels = CaptureExternalWheels(vehicle),
                WheelVisuals = CaptureWheelVisualOrder(vehicle),
                Hitch = hitch,
                HitchEnabled = hitch?.IsOn == true,
                Accessory = accessory,
                OriginalMode = vehicle.VehicleMultiplayerInstanceType,
                OriginalConstraints = vehicle.vehicleRigidbody.constraints,
                OriginalKinematic = vehicle.vehicleRigidbody.isKinematic,
                OriginalCollision = vehicle.vehicleRigidbody.collisionDetectionMode,
                OriginalInterpolation = vehicle.vehicleRigidbody.interpolation,
                OriginalVehicleEnabled = vehicle.enabled
            };
            if (original.ExternalWheels.Length != 4 || original.WheelVisuals.Length != 4)
                throw new InvalidOperationException("Purchased vehicle has no supported four-wheel visual/update mapping.");

            drivers.Add(entity);
            original.State = original.Original = Capture(original);
            original.OriginalCaptured = true;
            bindings.Add(entity, original);
            IndexBinding(original);
            identities.Bind(entity, VehicleState.Adapter, instance, identity, false, EntityPersistence.SessionOnly);

            if (channel == null) return;
            if (host)
            {
                // A purchase during a live session binds after the channel's initial records.
                if (channel.Records.ContainsKey(entity)) channel.Publish(entity, original.State.Encode());
                else channel.Register(new WorldRecord(entity, 1, VehicleState.Adapter, original.State.Encode()));
            }
            else HydratePurchasedVehicle(original);
        }

        private void HydratePurchasedVehicle(Binding binding)
        {
            if (channel == null || host || !channel.Ready) return;
            if (channel.Records.TryGetValue(binding.Id, out var record))
            {
                if (record.Adapter != VehicleState.Adapter) throw new InvalidDataException("Purchased vehicle record adapter changed.");
                Apply(binding, VehicleState.Decode(record.Payload));
            }
            else channel.RequestResync(SnapshotScope.ForEntity(VehicleState.Adapter, binding.Id),
                "Purchased vehicle bound after its initial snapshot.");
        }

        internal IEnumerable<ulong> RequiredPassengerCatalogEntityIds => passengerBindings.Keys.Where(id => id > WorldRecord.DynamicIdStart);
        public IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, VehicleState.Adapter, b.State.Encode()))
            .Concat(passengerBindings.Values.Select(b => new WorldRecord(b.PassengerId, 1, VehiclePassengerSeatState.Adapter, b.PassengerState.Encode())))
            .Concat(seatAdjustmentBindings.Values.Where(adjustment => adjustment.RecordEnabled)
                .Select(adjustment => new WorldRecord(adjustment.Id, 1, VehicleSeatAdjustmentState.Adapter, adjustment.State.Encode())))
            .Concat(Doors.InitialRecords()).Concat(Windows.InitialRecords()).Concat(Hood.InitialRecords()).Concat(Trunk.InitialRecords());
        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            Doors.Attach(session, channel); Windows.Attach(session, channel); Hood.Attach(session, channel); Trunk.Attach(session, channel);
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command;
            channel.ReplyReceived += PassengerReply;

#if MONMP_DEVELOPMENT
            channel.CommandRejected += CommandRejected;
#endif

            foreach (var b in bindings.Values) if (b.Hitch != null) b.Hitch.IsOn = false;
            if (!host) foreach (var b in bindings.Values.Where(b => !b.Dormant)) SetMode(b, false);
        }
        private string racecarWaitLogged = "";
        private static bool NativeMotionInitialized(VehicleController vehicle) =>
            vehicle.powertrain?.Initialized == true && vehicle.input?.Initialized == true && vehicle.brakes?.Initialized == true;

        private static bool HasSharedLifecycle(Vehicle_Type type) =>
            IsNativeKonigShell(type) || type == Vehicle_Type.LeMissile || type == Vehicle_Type.Racecar || type == Vehicle_Type.Buggy;

        private static bool OptionalVehicleReady(Binding binding, bool host)
        {
            var vehicle = binding.Vehicle;
            return vehicle != null && vehicle.gameObject.activeInHierarchy && vehicle.enabled && NativeMotionInitialized(vehicle) &&
                vehicle.vehicleRigidbody != null && (!vehicle.vehicleRigidbody.isKinematic || !host && binding.PendingState?.MotionReady == true) &&
                vehicle.Wheels != null && vehicle.Wheels.Count == 4 && vehicle.Wheels.All(wheel => wheel != null && wheel.wheelController != null);
        }

        private static bool BuggySeatReady(Binding b)
        {
            if (b.Seat == null || !b.Seat.isActiveAndEnabled || b.Seat.seatPosition == null || !b.Seat.seatPosition.gameObject.activeInHierarchy) return false;
            return b.Seat.GetComponentsInChildren<Collider>(true).Any(c => c != null && c.enabled && c.gameObject.activeInHierarchy);
        }
        private bool BuggyReady(Binding b)
        {
            bool guestMotionReady = !host && b.PendingState?.MotionReady == true;
            if (b.Racecar)
            {
                var race = SharedRacecarBuild.Current;
                if (race?.Synchronized != true || !race.State.Complete || !OptionalVehicleReady(b, host)) return false;
                // The host's root activation and completed motion state are separate from the
                // build packs. An inactive stored car must wait for the garage's host activation.
                if (!host && b.PendingState?.MotionReady != true) return false;
            }
            else
            {
                var build = SharedBuggyBuild.Current;
                if (build?.Synchronized != true || !build.State.Complete ||
                    (host ? !build.VehicleActive : !guestMotionReady)) return false;
            }
            string why = b.Vehicle == null ? "vehicle" : !b.Vehicle.enabled ? "disabled" : b.Vehicle.vehicleRigidbody == null || b.Vehicle.vehicleRigidbody.isKinematic && !guestMotionReady ? "kinematic" :
                b.Vehicle.Wheels.Count != 4 || b.Vehicle.Wheels.Any(w => w == null || w.wheelController == null) ? "wheels=" + b.Vehicle.Wheels.Count :
                b.Seat == null ? "seat" : !b.Seat.isActiveAndEnabled ? "seat-inactive" : b.Seat.seatPosition == null || !b.Seat.seatPosition.gameObject.activeInHierarchy ? "seat-position" :
                !BuggySeatReady(b) ? "seat-collider" : "";
            string kind = b.Racecar ? "racecar" : "buggy";
            if (kind + why != racecarWaitLogged) { racecarWaitLogged = kind + why; if (why.Length > 0) Debug.Log("MonMP " + kind + " built, binding waits for: " + why); }
            return
                b.Vehicle != null && b.Vehicle.enabled && NativeMotionInitialized(b.Vehicle) && b.Vehicle.vehicleRigidbody != null &&
                (!b.Vehicle.vehicleRigidbody.isKinematic || guestMotionReady) &&
                b.Vehicle.Wheels.Count == 4 && b.Vehicle.Wheels.All(w => w != null && w.wheelController != null) && BuggySeatReady(b);
        }
        private void PromoteReadyBuggies()
        {
            foreach (var b in bindings.Values)
            {
                if (!b.Buggy || !b.Dormant) continue;
                if (!BuggyReady(b) || (!host && b.PendingState == null)) continue;
                var v = b.Vehicle;
                var bodies = VehicleBodies(v);
                if ((!b.Racecar && !v.NoDriverDoor) || v.vehicle_PartsManager?.Fuel_Tank == null || v.Wheels.Count != 4 ||
                    !HasSupportedBuiltVehicleBodyConfiguration(v, bodies) ||
                    b.Seat!.vehicleController != v || b.Seat.seatPosition == null)
                    throw new InvalidOperationException("Unsupported completed " + (b.Racecar ? "racecar" : "Buggy") + " configuration.");
                // BuggyBuild.Awake disables VehicleController before Start. Its Start performs
                // the native powertrain/wheel setup, so a session can begin before Wheels is
                // populated. Capture this baseline only after activation and native setup finish.
                b.ExternalWheels = CaptureExternalWheels(v);
                b.WheelVisuals = CaptureWheelVisualOrder(v);
                IndexWheels(b);
                if (b.ExternalWheels.Length != v.Wheels.Count || b.WheelVisuals.Length != 4)
                    throw new InvalidOperationException("Completed Buggy wheel visual/update mapping is unavailable.");
                b.Dormant = false;
                var native = Capture(b);
                b.Original = native; b.OriginalCaptured = true;
                if (host)
                {
                    b.State = native.WithDriver(drivers[b.Id]);
                    Publish(b);
                }
                else
                {
                    b.State = native;
                    Apply(b, b.PendingState!);
                    b.PendingState = null;
                }
            }
        }
        private void PromoteReadyMissiles()
        {
            foreach (var binding in bindings.Values)
            {
                if (!binding.Dormant || binding.Vehicle.vehicle_type != Vehicle_Type.LeMissile ||
                    !host && binding.PendingState?.MotionReady != true || !OptionalVehicleReady(binding, host)) continue;
                var vehicle = binding.Vehicle;
                var bodies = VehicleBodies(vehicle, true);
                if (!vehicle.NoDriverDoor || vehicle.vehicle_PartsManager?.Fuel_Tank == null || bodies.Length != 1 ||
                    bodies[0] != vehicle.vehicleRigidbody || vehicle.GetComponentsInChildren<VehicleController>(true).Length != 1 ||
                    binding.Seat == null || binding.Seat.vehicleController != vehicle || !BuggySeatReady(binding)) continue;
                binding.ExternalWheels = CaptureExternalWheels(vehicle);
                binding.WheelVisuals = CaptureWheelVisualOrder(vehicle);
                if (binding.ExternalWheels.Length != 4 || binding.WheelVisuals.Length != 4) continue;
                IndexWheels(binding);
                binding.Dormant = false;
                var native = Capture(binding);
                binding.Original = native; binding.OriginalCaptured = true;
                if (host)
                {
                    // Host progression permanently released this native chassis. Session cleanup
                    // must retain its newly unlocked body instead of freezing the old locked state.
                    binding.OriginalKinematic = vehicle.vehicleRigidbody.isKinematic;
                    binding.OriginalCollision = vehicle.vehicleRigidbody.collisionDetectionMode;
                    binding.OriginalInterpolation = vehicle.vehicleRigidbody.interpolation;
                    binding.State = native.WithDriver(drivers[binding.Id]);
                    Publish(binding);
                }
                else
                {
                    binding.State = native;
                    var pending = binding.PendingState;
                    binding.PendingState = null;
                    if (pending != null) Apply(binding, pending);
                }
            }
        }
        private NativeKonigShellMotionShape NativeKonigShellShape(Binding b)
        {
            var v = b.Vehicle;
            bool canonical = v.vehicle_type == Vehicle_Type.Prinz
                ? SharedVehicleParts.CanonicalPrinzVehicle().Any(candidate => candidate == v)
                : (UnityEngine.Object.FindObjectOfType<GlobalClassManager>()?.GetVehicles(Vehicle_Type.ScrapyardKonig) as VehicleController) == v;
            bool supportedHinges = SharedVehicleTrunk.HasSupportedKonigBodyConfiguration(v);
            var bodies = VehicleBodies(v, true);
            var wheels = v.Wheels;
            bool progressionKinematic = v.vehicleRigidbody != null && v.vehicleRigidbody.isKinematic && ProgressionLocked(v.vehicle_type);
            return new NativeKonigShellMotionShape(canonical, v.gameObject.scene.IsValid(), v.gameObject.activeInHierarchy,
                v.enabled, v.vehicleRigidbody != null,
                v.vehicleRigidbody != null && (!v.vehicleRigidbody.isKinematic || FrozenOnCarLift(v) ||
                    !host && b.PendingState?.MotionReady == true),
                v.vehicle_PartsManager?.Fuel_Tank != null, supportedHinges, progressionKinematic,
                v.GetComponentsInChildren<VehicleController>(true).Length,
                supportedHinges ? 1 : bodies.Length,
                wheels == null ? 0 : wheels.Count,
                wheels == null ? 0 : wheels.Count(wheel => wheel != null && wheel.wheelController != null));
        }
        private void PromoteReadyNativeKonigShells()
        {
            if (Time.unscaledTime < nextNativeShellPromotion) return;
            nextNativeShellPromotion = Time.unscaledTime + 0.1f;
            foreach (var b in bindings.Values)
            {
                if (!b.Dormant || !IsNativeKonigShell(b.Vehicle.vehicle_type)) continue;
                if (!host && b.PendingState?.MotionReady != true) continue;
                // Dormant shells already passed the catalog gate. Avoid scene and hierarchy
                // discovery until native startup makes promotion possible; validate the full
                // shape below on the first eligible poll before changing authority.
                var candidate = b.Vehicle;
                if (candidate == null || !candidate.gameObject.activeInHierarchy || !candidate.enabled ||
                    candidate.vehicleRigidbody == null || candidate.Wheels == null || candidate.Wheels.Count != 4) continue;
                var shape = NativeKonigShellShape(b);
                if (NativeKonigShellSupportPolicy.CanDeferActivation(shape)) continue;
                if (!NativeKonigShellSupportPolicy.SupportsMotion(shape))
                {
                    if (!b.ShellMotionShapeReported)
                    {
                        b.ShellMotionShapeReported = true;
                        Debug.Log("MonMP " + b.Vehicle.vehicle_type + " remains dormant: unsupported native shell shape; " +
                            "active=" + shape.RootActive + ";controllerEnabled=" + shape.ControllerEnabled +
                            ";body=" + shape.RootBodyPresent + ";dynamic=" + shape.RootBodyDynamic +
                            ";fuel=" + shape.HasFuelTank + ";savedHinges=" + shape.HasSupportedSavedHinges +
                            ";controllers=" + shape.ControllerCount + ";bodies=" + shape.RigidbodyCount +
                            ";wheels=" + shape.WheelCount + ";wheelVisuals=" + shape.WheelVisualCount);
                    }
                    continue;
                }
                var v = b.Vehicle;
                var body = v.vehicleRigidbody;
                b.ExternalWheels = CaptureExternalWheels(v);
                b.WheelVisuals = CaptureWheelVisualOrder(v);
                IndexWheels(b);
                if (b.ExternalWheels.Length != 4 || b.WheelVisuals.Length != 4) continue;

                // The native Start/save restore sequence can finish after the world catalog is
                // created. Capture restoration state only now, before shared authority changes it.
                b.AutoInput = v.input.autoSetInput;
                b.FreezeIdle = v.freezeWhileIdle;
                b.FreezeSleep = v.freezeWhileAsleep;
                b.OriginalMode = v.VehicleMultiplayerInstanceType;
                b.OriginalConstraints = body.constraints;
                if (host) b.OriginalKinematic = body.isKinematic && !FrozenOnCarLift(v);
                b.OriginalCollision = body.collisionDetectionMode;
                b.OriginalInterpolation = body.interpolation;
                b.OriginalVehicleEnabled = v.enabled;
                b.Hitch = v.GetComponent<TrailerHitchModuleWrapper>()?.module;
                b.HitchEnabled = b.Hitch?.IsOn == true;
                b.Dormant = false;
                CaptureOriginalShellSeatConfiguration(b);
                var native = Capture(b);
                b.Original = native;
                b.OriginalCaptured = true;
                if (host)
                {
                    b.State = native.WithDriver(drivers[b.Id]);
                    Publish(b);
                }
                else
                {
                    b.State = native;
                    var pending = b.PendingState;
                    b.PendingState = null;
                    if (pending != null) Apply(b, pending);
                }
                CaptureOrUpdateNativeShellSeatAdjustment(b);
                Debug.Log("MonMP " + v.vehicle_type + " native shell activated and shared; id=" + b.Id.ToString("X16") +
                    ";active=" + v.gameObject.activeInHierarchy + ";wheels=" + b.WheelVisuals.Length);
            }
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);

#if MONMP_DEVELOPMENT
        private void CommandRejected(uint peer, ulong id, byte[] payload)
        {
            if (!testDriverEntryTraceEnabled || !host || !testDriverEntryTraceIds.Contains(id)) return;
            try
            {
                var command = VehicleCommand.Decode(payload);
                if (command.Action != VehicleAction.Enter) return;
                testHostWorldRejectedCount++;
                var currentRevision = channel != null && channel.Records.TryGetValue(id, out var record) ? record.Revision : 0;
                RecordDriverEntryTrace("host-world-channel", "enter rejected before SharedVehicles.Command; peer=" + peer +
                    ", id=" + id.ToString("X16") + ", recordRevision=" + currentRevision +
                    ", requestedOwner=" + command.State.Driver.Owner + ", requestedGeneration=" + command.State.Driver.Generation);
            }
            catch (InvalidDataException)
            {
                // Other SharedWorld command adapters share this callback.
            }
        }


        private void RecordDriverEntryTrace(string role, string detail)
        {
            if (!testDriverEntryTraceEnabled) return;
            string line = (++testDriverEntryTraceSequence).ToString() + "/" + role + ": " + detail;
            if (testDriverEntryTrace.Count == 6) testDriverEntryTrace.Dequeue();
            testDriverEntryTrace.Enqueue(line);
            Debug.Log("MONMP_VEHICLE_ENTRY_TRACE: " + line);
        }


        private static string SeatIdentity(Transform? seat) => seat == null ? "null" : RuntimeProbe.CanonicalPath(seat);


        private string DescribeDriverEntryGate(Binding b, uint peer, Transform? suppliedSeat, uint requestedOwner, uint requestedGeneration)
        {
            bool hasPose = presence.TryGetValue(peer, out var observed);
            Vector3 location = peer == 0
                ? gameplay.PlayerWalking.transform.position
                : hasPose ? new Vector3(observed.Pose.X, observed.Pose.Y, observed.Pose.Z) : Vector3.zero;
            bool hasDriverSeat = b.TryGetDriverSeat(out var driverComponent, out var driverPosition);
            float seatDistance = hasDriverSeat && driverComponent != null && (peer == 0 || hasPose)
                ? Vector3.Distance(location, driverComponent.transform.position) : -1f;
            bool holding = peer == 0
                ? gameplay.Player.IsHoldingAnObject
                : MonMPPlugin.Instance?.Items?.PeerHolding(peer) == true;
            string seatCanUse;
            string driverCanUse;
            try
            {
                seatCanUse = driverComponent is Vehicle_DriverSeat standard ? standard.CanUse().ToString() :
                    driverComponent is Konig_Seat konig ? konig.CanSeat().ToString() : "missing";
            }
            catch (Exception exception) { seatCanUse = "throws-" + exception.GetType().Name; }
            try { driverCanUse = b.DriverCanUse().ToString(); }
            catch (Exception exception) { driverCanUse = "throws-" + exception.GetType().Name; }
            var lease = drivers[b.Id];
            var expectedPosition = hasDriverSeat ? driverPosition : null;
            string playerState = peer == 0 ? gameplay.Get_PlayerState.ToString() : hasPose ? observed.Pose.State.ToString() : "missing";
            string pose = peer == 0 ? "local" : hasPose
                ? "age=" + (Time.unscaledTime - observed.Time).ToString("F2") + ",pos=" + location.ToString("F2")
                : "missing";
            return "vehicle=" + b.Vehicle.vehicle_type + ",peer=" + peer + ",seat=" + SeatIdentity(suppliedSeat) +
                ",expected=" + SeatIdentity(expectedPosition) + ",seatMatch=" + (expectedPosition != null && suppliedSeat == expectedPosition) +
                ",Ready=" + Ready + ",blocked=" + TransfersBlocked + ",state=" + playerState + ",holding=" + holding +
                ",pose=" + pose + ",distance=" + seatDistance.ToString("F2") + "m,seatCanUse=" + seatCanUse +
                ",DriverCanUse=" + driverCanUse + ",NoDriverDoor=" + b.Vehicle.NoDriverDoor +
                ",driverDoorClosed=" + b.Vehicle.isDriverDoorClosed + ",passengerDoorClosed=" + b.Vehicle.isPassengerDoorClosed +
                ",requested=" + requestedOwner + "/" + requestedGeneration + ",current=" + lease.Owner + "/" + lease.Generation;
        }
#endif

        private void IndexBinding(Binding binding)
        {
            // Match the earlier FirstOrDefault lookup if an unsupported catalog repeats a native object.
            if (!byVehicle.ContainsKey(binding.Vehicle)) byVehicle.Add(binding.Vehicle, binding);
            IndexWheels(binding);
            if (!host && HasSharedLifecycle(binding.Vehicle.vehicle_type) && binding.NativeInitialized == null)
            {
                // Native Start restores Local physics before firing this event. Keep a guest's
                // pending chassis frozen until its host state and completed native shape agree.
                binding.NativeInitialized = () =>
                {
                    if (!disposed && binding.Dormant) FreezeDormantPhysics(binding);
                };
                binding.Vehicle.OnVehicleInitialized.AddListener(binding.NativeInitialized);
            }
        }
        private void IndexWheels(Binding binding)
        {
            // Dormant build/shell vehicles acquire their native wheels on promotion.
            // Replace the mapping at that lifecycle boundary; queries still read live state.
            foreach (var wheel in binding.IndexedWheelVisuals)
                if (wheel != null && byWheel.TryGetValue(wheel, out var previous) && previous.Binding == binding)
                    byWheel.Remove(wheel);
            for (int i = 0; i < binding.WheelVisuals.Length; i++)
                if (binding.WheelVisuals[i] != null && !byWheel.ContainsKey(binding.WheelVisuals[i]))
                    byWheel.Add(binding.WheelVisuals[i], (binding, i));
            binding.IndexedWheelVisuals = binding.WheelVisuals;
        }
        private Binding? Find(Vehicle? vehicle) => vehicle != null && byVehicle.TryGetValue(vehicle, out var binding) ? binding : null;
        // The driver's reported velocity for a vehicle presented from another peer's samples. Its body is
        // kinematic and teleported, so the native speed (from the body's velocity) read zero and the
        // wheels' dust clouds and other speed-driven effects never started (#250).
        internal bool TryPresentedVelocity(Vehicle vehicle, out Vector3 velocity)
        {
            velocity = default;
            if (disposed || !Ready) return false;
            var b = Find(vehicle);
            if (b == null || b.Dormant || !b.HasPresentationPose || b.State == null || IsLocal(b.State) || Time.unscaledTime - b.PoseReceivedAt > 1f) return false;
            velocity = b.PoseVelocity; return true;
        }
        public bool Handles(Vehicle? vehicle) => !disposed && session?.IsRunning == true && vehicle != null && Find(vehicle) != null;
        public bool CanControl(Vehicle vehicle)
        {
            if (disposed || session?.IsRunning != true || applying) return true;
            var b = Find(vehicle);
            return b == null || Ready && !b.Dormant && IsLocal(b.State);
        }
        private bool IsLocal(VehicleState state) => session != null && (state.Driver.Owner == session.LocalPeerId || (host && state.Driver.Owner == ItemLease.Unowned));
        internal uint DrivenBy(VehicleController vehicle) => Find(vehicle)?.State.Driver.Owner ?? ItemLease.Unowned;

#if MONMP_DEVELOPMENT
        internal bool TestDormant(VehicleController vehicle) => Find(vehicle)?.Dormant ?? true;


        internal VehiclePassengerSeatState TestPassengerStateFor(VehicleController vehicle) =>
            passengerBindings.Values.Single(binding => binding.Vehicle == vehicle).PassengerState;


        internal void TestReleaseSeatForAbort(VehicleController vehicle, uint peer)
        {
            if (!host || vehicle == null || peer == ItemLease.Unowned) return;
            var binding = Find(vehicle);
            if (binding == null) return;
            if (binding.State.Driver.Owner == peer) Release(binding, peer);
            if (binding.PassengerId != 0 && binding.PassengerState.Owner == peer &&
                passengers.TryRelease(binding.PassengerKey, peer, binding.PassengerState.Generation, out var released))
            {
                ApplyPassenger(binding, released);
                PublishPassenger(binding);
            }
        }
#endif

        // SharedFuel needs every mapped vehicle to identify buggy fuel tanks even before construction.
        internal IEnumerable<VehicleController> Driven => bindings.Values.Select(b => b.Vehicle);
        internal IEnumerable<VehicleController> BoundControllers => bindings.Values.Select(b => b.Vehicle);
        internal bool IsBound(VehicleController vehicle) => Find(vehicle) != null;
        // Target-scoped towing checks include synchronized passenger occupancy. The native local
        // player fallback also covers a seat while its catalog record is temporarily unavailable.
        internal bool HasPassengerOccupant(VehicleController vehicle) => vehicle != null &&
            (passengerBindings.Values.Any(b => b.Vehicle == vehicle && b.PassengerId != 0 && b.PassengerState.Occupied) ||
             (gameplay.Get_CurrentVehicleController == vehicle && gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger) ||
             MonMPPlugin.Instance?.World?.ExtraSeats.HasPassengerOccupant(vehicle) == true);
        internal ulong EntityOf(Vehicle? vehicle) => Find(vehicle)?.Id ?? 0;
        internal VehicleController? VehicleFor(ulong id) => bindings.TryGetValue(id, out var b) ? b.Vehicle : null;
        internal bool IsDriverOwner(uint peer) => bindings.Values.Any(b => b.State.Driver.Owner == peer);
        internal bool IsPassengerOwner(uint peer) => passengers.TryGetSeat(peer, out _);
        internal bool TryGetDriverState(VehicleController vehicle, out VehicleState state)
        {
            var binding = Find(vehicle);
            if (binding == null) { state = null!; return false; }
            state = binding.State;
            return true;
        }
        // Police reports use the latest accepted owner sample rather than the
        // remote kinematic body's native speed, which belongs to presentation.
        internal bool TryAuthorizePoliceRadar(uint peer, ulong entity, uint generation, out VehicleController? vehicle,
            out Vector3 driverPosition, out Vector3 forward, out float speedKmh, out float sampleAge)
        {
            vehicle = null; driverPosition = forward = default; speedKmh = sampleAge = 0f;
            if (disposed || !host || !Ready || session?.IsRunning != true || peer == 0 ||
                !session.Peers.ContainsKey(peer) || !bindings.TryGetValue(entity, out var b) || b.Dormant ||
                b.Vehicle == null || !b.Vehicle.gameObject.activeInHierarchy || b.State == null || !b.State.Active ||
                b.State.Driver.Owner != peer || !drivers.Owns(entity, peer, generation) ||
                !b.TryGetDriverSeat(out _, out var seat) || seat == null) return false;
            sampleAge = Time.unscaledTime - b.LastUpdate;
            if (sampleAge < 0 || sampleAge > 1f) return false;
            var pose = b.State.Pose;
            var rotation = new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
            forward = rotation * Vector3.forward;
            var velocity = Vector(b.State.Velocity);
            // NWH.Vehicle.Speed is abs(LocalForwardVelocity), not velocity magnitude.
            speedKmh = Mathf.Abs(Vector3.Dot(velocity, forward)) * 3.6f;
            // Retain authored chassis scale: InverseTransformPoint returns local
            // units, which cannot be rotated directly into a world-space pose.
            var seatOffset = Quaternion.Inverse(b.Vehicle.transform.rotation) * (seat.position - b.Vehicle.transform.position);
            driverPosition = Position(pose) + rotation * seatOffset;
            vehicle = b.Vehicle; return true;
        }
        internal bool TryGetRemoteWheelSpin(WheelController wheel, out float angularVelocity)
        {
            angularVelocity = 0f;
            if (!Ready || session?.IsRunning != true || wheel == null || !wheel.visualOnlyUpdate) return false;
            if (!byWheel.TryGetValue(wheel, out var entry)) return false;
            var b = entry.Binding;
            if (b.Dormant || IsLocal(b.State) || b.WheelVisuals.Length != 4 ||
                b.Vehicle.vehicleRigidbody == null || !b.Vehicle.vehicleRigidbody.isKinematic) return false;
            int i = entry.Index;
            angularVelocity = b.State.WheelSpin[i];
            // This lookup is called only from the visual postfix after its
            // finite-dt and mesh guards; the returned sample is applied next.
            b.LastRemoteWheelSpinVisual[i] = angularVelocity;
            b.RemoteWheelSpinVisualAppliedMask |= (byte)(1 << i);
            if (Mathf.Abs(angularVelocity) >= 5f) b.RemoteWheelSpinVisualHighMask |= (byte)(1 << i);
            return true;
        }
        // The steering wheel a remote driver holds and its current visual angle about its own
        // forward axis (native Steering turns it by Angle * steeringWheelTurnRatio).
        internal bool TryGetSteeringWheel(uint peer, out Transform wheel, out float angle, out Vector3 vehicleRight)
        {
            wheel = null!; angle = 0; vehicleRight = Vector3.right;
            var vehicle = bindings.Values.FirstOrDefault(b => b.State.Driver.Owner == peer)?.Vehicle;
            var steering = vehicle != null ? vehicle.steering : null;
            if (steering?.steeringWheel == null || !steering.steeringWheel.gameObject.activeInHierarchy) return false;
            wheel = steering.steeringWheel;
            angle = steering.Angle * steering.steeringWheelTurnRatio;
            vehicleRight = vehicle!.transform.right;
            return true;
        }
        public Transform? SeatFor(uint peer)
        {
            var driver = bindings.Values.FirstOrDefault(b => b.State.Driver.Owner == peer);
            if (driver != null && driver.TryGetDriverSeat(out _, out var position)) return position;
            return passengerBindings.Values.FirstOrDefault(b => b.PassengerState.Owner == peer)?.KonigPassengerSeat?.seatPosition;
        }
        internal VehicleController? VehicleForPeer(uint peer)
        {
            var driver = bindings.Values.FirstOrDefault(b => b.State.Driver.Owner == peer);
            if (driver != null && driver.TryGetDriverSeat(out _, out _)) return driver.Vehicle;
            return passengerBindings.Values.FirstOrDefault(b => b.PassengerState.Owner == peer)?.KonigPassengerSeat?.vehicleController as VehicleController;
        }
        private static readonly System.Reflection.FieldInfo NativeKonigSeat = AccessTools.Field(typeof(Konig_Seat_Button), "konig_Seat");
        private static Konig_Seat_Button? NativeExitButton(Binding binding, Konig_Seat konig, bool driver)
        {
            var cached = driver ? binding.DriverExitButton : binding.PassengerExitButton;
            if (cached != null && cached.Seat == konig && cached.Children == konig.transform.childCount &&
                Time.unscaledTime < cached.NextDiscovery && (cached.Button == null ||
                cached.Button.isActiveAndEnabled && cached.Button.transform.IsChildOf(konig.transform) &&
                ReferenceEquals(NativeKonigSeat.GetValue(cached.Button), konig))) return cached.Button;
            // Seat/part topology can arrive after a shell activates. Cache failed
            // and ambiguous discoveries too; revisit stable topology every two
            // seconds rather than allocate a hierarchy scan each frame.
            Konig_Seat_Button? selected = null;
            int matches = 0;
            foreach (var button in konig.GetComponentsInChildren<Konig_Seat_Button>(true))
                if (button != null && button.isActiveAndEnabled && ReferenceEquals(NativeKonigSeat.GetValue(button), konig))
                { selected = button; matches++; }
            cached = new NativeSeatButtonBinding { Seat = konig, Button = matches == 1 ? selected : null,
                Children = konig.transform.childCount, NextDiscovery = Time.unscaledTime + 2f };
            if (driver) binding.DriverExitButton = cached; else binding.PassengerExitButton = cached;
            return cached.Button;
        }
        private bool TryGetLocalExitSeat(out VehicleController? vehicle, out Transform? position, out Interactable? seat, out bool driver)
        {
            vehicle = null; position = null; seat = null; driver = false;
            if (disposed || !Ready || session?.IsRunning != true) return false;
            var current = gameplay.Get_CurrentVehicleController;
            var binding = Find(current);
            if (binding == null || binding.Dormant) return false;
            driver = gameplay.Get_PlayerState == PlayerState.InVehicle_Driver;
            Konig_Seat? konig;
            if (driver)
            {
                if (binding.State.Driver.Owner != session.LocalPeerId) return false;
                // Entry already vetted the driver's native component shape.
                // Use that bound component for its current occupant; shell
                // admission's hierarchy scan belongs to entry, not each hover.
                Component? component = (Component?)binding.KonigDriverSeat ?? binding.ScrapyardKonigDriverSeat ??
                    (Component?)binding.Seat;
                seat = component as Vehicle_DriverSeat;
                konig = component as Konig_Seat;
                if (seat is Vehicle_DriverSeat standard && standard.vehicleController == binding.Vehicle) position = standard.seatPosition;
                else if (konig != null && konig.vehicleController == binding.Vehicle && konig.isActiveAndEnabled) position = konig.seatPosition;
                else return false;
            }
            else
            {
                if (gameplay.Get_PlayerState != PlayerState.InVehicle_Passenger ||
                    binding.PassengerId == 0 || binding.PassengerState.Owner != session.LocalPeerId) return false;
                konig = binding.KonigPassengerSeat;
                position = konig?.seatPosition;
            }
            if (konig != null)
            {
                seat = NativeExitButton(binding, konig, driver);
                if (seat == null) return false;
            }
            if (seat == null || !seat.isActiveAndEnabled || position == null || !position.gameObject.activeInHierarchy) return false;
            vehicle = binding.Vehicle;
            return true;
        }
        internal Interactable? PickLocalExitSeat(Ray ray)
        {
            if (gameplay.Player.Get_HoldingItem != null) return null;
            return TryGetLocalExitSeat(out var vehicle, out var position, out var seat, out _)
                ? VehicleSeatExitAim.Pick(seat!, position!, vehicle!, ray) : null;
        }
        internal bool ClickLocalExitSeat(Interactable clicked)
        {
            if (!TryGetLocalExitSeat(out var vehicle, out var position, out var seat, out var driver) || clicked != seat) return false;
            // CanUse/CanSeat and CheckSeatSolidity are entry gates. A player who
            // already owns this seat needs only the native open-door exit gate.
            EnterExit(vehicle!, position!, driver);
            return true;
        }
        // Controller exit through the local VR bridge: the same occupied-seat click as above.
        internal bool HasLocalExitSeat => TryGetLocalExitSeat(out _, out _, out _, out _);
        internal bool ClickLocalExitSeat() =>
            TryGetLocalExitSeat(out _, out _, out var seat, out _) && seat != null && ClickLocalExitSeat(seat);
        internal bool TryGetReconnectExit(uint peer, out Vector3 position)
        {
            var driver = bindings.Values.FirstOrDefault(b => b.State.Driver.Owner == peer);
            if (driver != null) return NativeVehicleDeparture.TryExit(driver.Vehicle, true, out position);
            var passenger = passengerBindings.Values.FirstOrDefault(b => b.PassengerState.Owner == peer);
            return NativeVehicleDeparture.TryExit(passenger?.Vehicle, false, out position);
        }
        public bool EnterExit(Vehicle vehicle, Transform seat, bool driver)
        {
            var b = Find(vehicle);

#if MONMP_DEVELOPMENT
            bool trace = testDriverEntryTraceEnabled && b != null && testDriverEntryTraceIds.Contains(b.Id);


            if (trace)
            {
                testEnterExitPrefixCount++;
                RecordDriverEntryTrace("enter-exit-prefix", DescribeDriverEntryGate(b!, session?.LocalPeerId ?? 0,
                    seat, 0, b!.State.Driver.Generation));
            }
#endif

            if (applying) return true;
            // Canonical purchased clones can expose their native seat before wheel Start
            // has created the shared movement binding. Do not grant local entry then.
            if (!disposed && session?.IsRunning == true && vehicle != null && pendingPurchasedVehicles.Values.Any(p =>
                p.Instance != null && vehicle.transform.IsChildOf(p.Instance.transform))) return false;
            if (!Handles(vehicle)) return true;
            b = Find(vehicle)!;
            if (b.Dormant || !Ready) return false;
            if (!VehicleSeatDoors.AllowsTransition(gameplay, vehicle, driver)) return false;
            if (gameplay.Get_CurrentVehicleController == vehicle)
            {
                b.RequestedExitDriverSide = NativeVehicleSeatExit.DriverSide(b.Vehicle,
                    gameplay.Get_PlayerState == PlayerState.InVehicle_Driver);
                bool passenger = gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger && b.PassengerId != 0;
                // Standing is local presentation. The exact host-owned claim is
                // released separately and remains unavailable to another rider.
                ExitLocal();
                if (passenger) RequestPassengerExit(b);
                else RequestExit(b);
                return false;
            }
            if (TransfersBlocked) return false;
            if (gameplay.Get_PlayerState != PlayerState.Walking || gameplay.Player.IsHoldingAnObject) return false;
            if (!driver)
            {
                if (b.PassengerId != 0 && seat == b.KonigPassengerSeat?.seatPosition)
                {
                    if (SeatAdmission?.IsPendingLocal(this, b.PassengerId) == true) return false; // a repeat click keeps waiting
                    if (SeatAdmission?.TryBeginLocal(this, b.PassengerId, b.PassengerState.Generation, session!.LocalPeerId) == false) return false;
                    channel!.Request(b.PassengerId, new VehiclePassengerSeatCommand(VehiclePassengerSeatAction.Enter, b.PassengerState.Generation).Encode(), true);
                }
                return false;
            }
            if (!b.TryGetDriverSeat(out _, out var driverPosition) || driverPosition == null || seat != driverPosition) return false;
            if (SeatAdmission?.IsPendingLocal(this, b.Id) == true) return false; // a repeat click keeps waiting
            if (SeatAdmission?.TryBeginLocal(this, b.Id, b.State.Driver.Generation, session!.LocalPeerId) == false) return false;

#if MONMP_DEVELOPMENT
            if (trace) RecordDriverEntryTrace("enter-request", "local gates passed; worldReady=" + (channel?.Ready == true) +
                ", recordPresent=" + (channel?.Records.ContainsKey(b.Id) == true));
#endif

            channel!.Request(b.Id, new VehicleCommand(VehicleAction.Enter, b.State).Encode(), true);
            return false;
        }
        public void NativeExited()
        {
            if (applying || disposed || !Ready) return;
            foreach (var b in bindings.Values) b.RequestedExitDriverSide = null;
            foreach (var b in bindings.Values) if (b.State.Driver.Owner == session!.LocalPeerId) RequestExit(b);
            foreach (var b in passengerBindings.Values) if (b.PassengerState.Owner == session!.LocalPeerId) RequestPassengerExit(b);
        }
        private void RequestExit(Binding b)
        {
            if (b.State.Driver.Owner != session!.LocalPeerId) return;
            b.ExitPending = true;
            channel!.Request(b.Id, new VehicleCommand(VehicleAction.Exit, Capture(b)).Encode(), true);
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            var staged = new List<(Binding Binding, VehicleState State)>();
            foreach (var record in records.Values.Where(r => r.Adapter == VehicleState.Adapter))
            {
                if (!bindings.TryGetValue(record.Id, out var binding))
                {
                    if (record.Id > WorldRecord.DynamicIdStart) continue;
                    throw new InvalidDataException("Unknown static vehicle authority record.");
                }
                staged.Add((binding, VehicleState.Decode(record.Payload)));
            }
            foreach (var value in staged)
            {
                Apply(value.Binding, value.State);
            }
            foreach (var record in records.Values.Where(r => r.Adapter == VehiclePassengerSeatState.Adapter))
            {
                if (passengerBindings.TryGetValue(record.Id, out var passenger))
                    ApplyPassenger(passenger, VehiclePassengerSeatState.Decode(record.Payload));
                else if (record.Id <= WorldRecord.DynamicIdStart && ForeignPassengerSeatRecord?.Invoke(record.Id) != true)
                    throw new InvalidDataException("Unknown static vehicle passenger-seat record.");
            }
            foreach (var record in records.Values.Where(r => r.Adapter == VehicleSeatAdjustmentState.Adapter))
            {
                if (seatAdjustmentBindings.TryGetValue(record.Id, out var seatAdjustment))
                    ApplySeatAdjustment(seatAdjustment, VehicleSeatAdjustmentState.Decode(record.Payload));
                else if (record.Id <= WorldRecord.DynamicIdStart)
                    throw new InvalidDataException("Unknown static vehicle seat-adjustment record.");
            }
        }
        private void Changed(WorldRecord r)
        {
            if (!host && r.Adapter == VehicleState.Adapter)
            {
                if (!bindings.TryGetValue(r.Id, out var b))
                {
                    if (r.Id > WorldRecord.DynamicIdStart) return;
                    throw new InvalidDataException("Unknown static vehicle authority record.");
                }
                var state = VehicleState.Decode(r.Payload);
                Apply(b, state);
            }
            if (!host && r.Adapter == VehiclePassengerSeatState.Adapter)
            {
                if (passengerBindings.TryGetValue(r.Id, out var passenger))
                    ApplyPassenger(passenger, VehiclePassengerSeatState.Decode(r.Payload));
                else if (r.Id <= WorldRecord.DynamicIdStart && ForeignPassengerSeatRecord?.Invoke(r.Id) != true)
                    throw new InvalidDataException("Unknown static vehicle passenger-seat record.");
            }
            if (!host && r.Adapter == VehicleSeatAdjustmentState.Adapter)
            {
                if (seatAdjustmentBindings.TryGetValue(r.Id, out var seatAdjustment))
                    ApplySeatAdjustment(seatAdjustment, VehicleSeatAdjustmentState.Decode(r.Payload));
                else if (r.Id <= WorldRecord.DynamicIdStart)
                    throw new InvalidDataException("Unknown static vehicle seat-adjustment record.");
            }
        }
        private void RefuseSeat(uint peer, ulong entity, uint generation, FurnitureSeatRefusal reason) =>
            SeatRefusals.Send(channel, peer, entity, generation, reason);

        private FurnitureSeatRefusal DriverRefusal(Binding b, uint peer)
        {
            if (TransfersBlocked) return FurnitureSeatRefusal.TransfersBlocked;
            var lease = drivers[b.Id];
            if (lease.Owner != ItemLease.Unowned && lease.Owner != peer) return FurnitureSeatRefusal.Occupied;
            if (SeatAdmission?.IsSeated(peer) == true || passengers.TryGetSeat(peer, out _) ||
                MonMPPlugin.Instance?.World?.SchoolBusPassengers.IsOwner(peer) == true ||
                MonMPPlugin.Instance?.World?.BoatMotion.IsOwner(peer) == true ||
                bindings.Values.Any(value => drivers[value.Id].Owner == peer)) return FurnitureSeatRefusal.AlreadySeated;
            Vector3 location;
            if (peer == 0)
            {
                if (gameplay.Player.IsHoldingAnObject) return FurnitureSeatRefusal.Holding;
                if (gameplay.Get_PlayerState != PlayerState.Walking) return FurnitureSeatRefusal.NotWalking;
                location = gameplay.PlayerWalking.transform.position;
            }
            else
            {
                if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2 ||
                    p.Pose.State != (byte)PlayerState.Walking) return FurnitureSeatRefusal.NotWalking;
                if (MonMPPlugin.Instance?.Items?.PeerHolding(peer) == true) return FurnitureSeatRefusal.Holding;
                location = new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z);
            }
            if (!b.TryGetDriverSeat(out var driver, out _) || driver == null) return FurnitureSeatRefusal.MissingSeat;
            if ((location - driver.transform.position).sqrMagnitude > 16f) return FurnitureSeatRefusal.TooFar;
            return b.DriverCanUse() ? FurnitureSeatRefusal.Stale : FurnitureSeatRefusal.NotUsable;
        }

        [MonMP.Core.World.WorldCommandAdapters(VehicleState.Adapter, VehiclePassengerSeatState.Adapter, VehicleSeatAdjustmentState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (host && seatAdjustmentBindings.TryGetValue(id, out var seatAdjustment))
            {
                CommandSeatAdjustment(seatAdjustment, peer, payload);
                return;
            }
            if (host && passengerBindings.TryGetValue(id, out var passenger))
            {
                CommandPassenger(passenger, peer, payload);
                return;
            }
            // Dormant buggies are present in the static catalog so construction can promote
            // them without registering a new world entity. Reject forged or stale driver
            // commands until the synchronized native build has made the vehicle driveable.
            if (!host || !bindings.TryGetValue(id, out var b) || b.Dormant) return;
            if (VehicleRefuelAcknowledgement.Is(payload)) { AcknowledgeRefuel(b, peer, VehicleRefuelAcknowledgement.Decode(payload)); return; }
            if (VehicleControlCommand.HasMarker(payload)) { CommandControl(b, peer, VehicleControlCommand.Decode(payload)); return; }
            var command = VehicleCommand.Decode(payload);

#if MONMP_DEVELOPMENT
            bool traceEnter = testDriverEntryTraceEnabled && testDriverEntryTraceIds.Contains(id) && command.Action == VehicleAction.Enter;


            if (traceEnter)
            {
                testHostVehicleCommandCount++;
                RecordDriverEntryTrace("host-command", "SharedVehicles received Enter; accessoryShapeMatches=" +
                    (command.State.Accessory.HasValue == (b.Accessory != null)) + "; " +
                    DescribeDriverEntryGate(b, peer, b.TryGetDriverSeat(out _, out var traceSeat) ? traceSeat : null,
                        command.State.Driver.Owner, command.State.Driver.Generation));
            }
#endif

            if (command.State.Accessory.HasValue != (b.Accessory != null)) throw new InvalidDataException("Vehicle accessory layout mismatch.");
            if (!command.State.Active || !command.State.MotionReady) return;
            if (command.Action == VehicleAction.Enter)
            {
                try
                {

#if MONMP_DEVELOPMENT
                    bool trace = traceEnter;


                    void Reject(string gate)
                    {
                        if (trace) RecordDriverEntryTrace("host-entry-reject", gate + "; " +
                            DescribeDriverEntryGate(b, peer, b.TryGetDriverSeat(out _, out var traceSeat) ? traceSeat : null,
                                command.State.Driver.Owner, command.State.Driver.Generation));
                    }
#endif

                    if (!b.TryGetDriverSeat(out var driverComponent, out var driverPosition) ||
                        driverComponent == null || driverPosition == null)
                    {
#if MONMP_DEVELOPMENT
    Reject("no supported active driver seat is installed");
#endif
     return; }
                    if (TransfersBlocked) {
#if MONMP_DEVELOPMENT
    Reject("TransfersBlocked");
#endif
     return; }
                    if (SeatAdmission?.IsSeated(peer) == true) {
#if MONMP_DEVELOPMENT
    Reject("owns shared seat");
#endif
     return; }
                    if (passengers.TryGetSeat(peer, out var passengerSeat)) {
#if MONMP_DEVELOPMENT
    Reject("owns passenger seat " + passengerSeat);
#endif
     return; }
                    if (MonMPPlugin.Instance?.World?.SchoolBusPassengers.IsOwner(peer) == true) {
#if MONMP_DEVELOPMENT
    Reject("owns school bus passenger seat");
#endif
     return; }
                    if (MonMPPlugin.Instance?.World?.BoatMotion.IsOwner(peer) == true) {
#if MONMP_DEVELOPMENT
    Reject("owns shared boat driver seat");
#endif
     return; }
                    // A fresh walking pose can briefly outlive a seat grant. Reserve at most
                    // one driver lease per peer across the entire bound fleet.
                    var existingDriver = bindings.Values.FirstOrDefault(value => drivers[value.Id].Owner == peer);
                    if (existingDriver != null) {
#if MONMP_DEVELOPMENT
    Reject("owns driver lease for " + existingDriver.Vehicle.vehicle_type);
#endif
     return; }
                    Vector3 location;
                    if (peer == 0)
                    {
                        if (gameplay.Get_PlayerState != PlayerState.Walking || gameplay.Player.IsHoldingAnObject)
                        {

#if MONMP_DEVELOPMENT
                            Reject("host player not free/walking");
#endif

                            return;
                        }
                        location = gameplay.PlayerWalking.transform.position;
                    }
                    else
                    {
                        if (!presence.TryGetValue(peer, out var p))
                        {

#if MONMP_DEVELOPMENT
                            Reject("no presence pose");
#endif

                            return;
                        }
                        if (Time.unscaledTime - p.Time > 2)
                        {

#if MONMP_DEVELOPMENT
                            Reject("presence pose stale");
#endif

                            return;
                        }
                        if (p.Pose.State != (byte)PlayerState.Walking)
                        {

#if MONMP_DEVELOPMENT
                            Reject("presence state is not Walking");
#endif

                            return;
                        }
                        if (MonMPPlugin.Instance!.Items!.PeerHolding(peer))
                        {

#if MONMP_DEVELOPMENT
                            Reject("owns shared held item");
#endif

                            return;
                        }
                        location = new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z);
                    }
                    float distanceSquared = (location - driverComponent.transform.position).sqrMagnitude;
                    if (distanceSquared > 16f)
                    {

#if MONMP_DEVELOPMENT
                        Reject("out of range");
#endif

                        return;
                    }
                    if (!b.DriverCanUse()) {
#if MONMP_DEVELOPMENT
    Reject("DriverCanUse=false");
#endif
     return; }
                    if (!drivers.TryAcquire(id, peer, command.State.Driver.Generation))
                    {

#if MONMP_DEVELOPMENT
                        Reject("driver lease TryAcquire=false");
#endif

                        return;
                    }
                    var state = Capture(b).WithDriver(drivers[id]);

#if MONMP_DEVELOPMENT
                    if (trace) RecordDriverEntryTrace("host-entry-grant", "driver lease acquired; " +
                        DescribeDriverEntryGate(b, peer, b.DriverPosition, command.State.Driver.Owner, command.State.Driver.Generation));
#endif

                    b.LastUpdate = Time.unscaledTime; Apply(b, state); Publish(b);
                    // The native entry drops under-bolted parts; a guest skips that on its copy (#184).
                    if (peer != 0)
                    {
                        try { b.Vehicle.GetComponent<Vehicle_PartsManager>()?.CheckPartSolidity(); }
                        catch (Exception ex) { Debug.LogWarning("MonMP part solidity check failed: " + ex.Message); }
                    }
                    return;
                }
                finally
                {
                    // Anything short of a grant answers the requester now, not at its claim timeout.
                    if (drivers[id].Owner != peer) RefuseSeat(peer, id, command.State.Driver.Generation, DriverRefusal(b, peer));
                }
            }
            if (!drivers.Owns(id, peer, command.State.Driver.Generation) || command.State.Driver.Owner != peer) return;
            if (peer != 0)
            {
                float limit = 10 + 200 * Mathf.Min(5, Time.unscaledTime - b.LastUpdate);
                if ((Position(command.State.Pose) - Position(b.State.Pose)).sqrMagnitude > limit * limit) return;
                // The host lift owns this physics invariant; a driver sample cannot clear or
                // invent it, including frames already in flight when the lift reaches its stop.
                var state = ReconcileDriverFuel(b, command.State).WithLiftFrozen(FrozenOnCarLift(b.Vehicle));
                Apply(b, b.DisconnectSecuring ? NativeVehicleDeparture.Parked(state) : state);
            }
            b.LastUpdate = Time.unscaledTime;
            if (command.Action == VehicleAction.Exit)
            {
                Release(b, peer);
            }
            else Publish(b);
        }
        private static short SeatCentimeters(float meters)
        {
            if (!VehicleSeatAdjustmentRange.TryCentimeters(meters, out var centimeters))
                throw new InvalidDataException("Konig seat adjustment is outside the replicated range.");
            return centimeters;
        }
        private static bool IsNativeKonigShell(Vehicle_Type type) =>
            type == Vehicle_Type.ScrapyardKonig || type == Vehicle_Type.Prinz;
        private static string NativeShellOptionalIdentity(VehicleController vehicle, string role, Transform seat) =>
            "native-vehicle-v1/" + vehicle.vehicle_type + "/" + role + "-v1/" + RuntimeProbe.CanonicalPath(seat);
        private static ulong NativeShellDynamicId(VehicleController vehicle, string identity) =>
            vehicle.vehicle_type == Vehicle_Type.Prinz ? PrinzSupportPolicy.DynamicEntityId(identity) :
                ScrapyardKonigSupportPolicy.DynamicEntityId(identity);
        private void BindNativeKonigShellSeats(Binding b, List<string>? diagnostics = null)
        {
            var vehicle = b.Vehicle;
            if (!IsNativeKonigShell(vehicle.vehicle_type) || vehicle == null) return;
            var seats = vehicle.GetComponentsInChildren<Konig_Seat>(true);
            var drivers = seats.Where(seat => seat != null && seat.Get_isDriverSeat).ToArray();
            var passengersFound = seats.Where(seat => seat != null && !seat.Get_isDriverSeat).ToArray();
            Konig_Seat? driver = drivers.Length == 1 && drivers[0].vehicleController == vehicle && drivers[0].seatPosition != null
                ? drivers[0] : null;
            Konig_Seat? passenger = passengersFound.Length == 1 && passengersFound[0].vehicleController == vehicle && passengersFound[0].seatPosition != null
                ? passengersFound[0] : null;
            if (driver != null)
            {
                b.KonigDriverSeat = driver;
                if (vehicle.vehicle_type == Vehicle_Type.ScrapyardKonig) b.ScrapyardKonigDriverSeat = driver;
            }
            var custom = vehicle.vehicle_PartsManager?.Get_CustomParameterToSave;
            var seatShape = new NativeKonigShellSeatShape(drivers.Length, passengersFound.Length,
                driver != null, passenger != null, driver?.seatPosition != null, passenger?.seatPosition != null,
                custom?.Length ?? 0);
            if (NativeKonigShellSupportPolicy.SupportsPassengerRecords(new NativeKonigShellSeatShape(
                drivers.Length, passengersFound.Length, driver != null, passenger != null,
                driver?.seatPosition != null, passenger?.seatPosition != null,
                custom?.Length ?? 0)))
            {
                b.KonigPassengerSeat = passenger;
                if (b.PassengerId == 0)
                {
                    string identity = NativeShellOptionalIdentity(vehicle, "passenger-seat", passenger!.transform);
                    b.PassengerKey = new SeatKey(b.Id, vehicle.vehicle_type + "-passenger-v1");
                    b.PassengerId = NativeShellDynamicId(vehicle, identity);
                    var occupancy = passengers.Register(b.PassengerKey);
                    b.PassengerState = new VehiclePassengerSeatState(occupancy.Peer, occupancy.Generation);
                    passengerBindings.Add(b.PassengerId, b);
                    identities.Bind(b.PassengerId, VehiclePassengerSeatState.Adapter, passenger.gameObject,
                        identity, true, EntityPersistence.SessionOnly);
                    diagnostics?.Add(b.Id.ToString("X16") + "\t" + identity + "\tpassenger");
                }
            }

            if (driver != null)
            {
                if (b.DriverSeatAdjustment == null)
                {
                    string identity = NativeShellOptionalIdentity(vehicle, "seat-adjustment", driver.transform);
                    b.DriverSeatAdjustment = CreateSeatAdjustmentBinding(b, driver,
                        NativeShellDynamicId(vehicle, identity), false, null, null);
                    BindSeatAdjustmentIdentity(b.DriverSeatAdjustment!, driver.gameObject, identity, true);
                }
                if (NativeKonigShellSupportPolicy.SupportsSeatAdjustmentRecord(seatShape) && !b.DriverSeatAdjustment!.RecordEnabled)
                {
                    b.DriverSeatAdjustment!.State = CaptureSeatAdjustment(driver, 1);
                    b.DriverSeatAdjustment.RecordEnabled = true;
                    if (b.OriginalCaptured) CaptureOriginalShellSeatConfiguration(b);
                    diagnostics?.Add(b.Id.ToString("X16") + "\t" +
                        NativeShellOptionalIdentity(vehicle, "seat-adjustment", driver.transform) + "\tseat-adjustment");
                }
            }
            if (passenger != null && NativeKonigShellSupportPolicy.SupportsPassengerSeatAdjustmentRecord(seatShape) &&
                b.PassengerSeatAdjustment == null)
            {
                string identity = NativeShellOptionalIdentity(vehicle, "passenger-seat-adjustment", passenger.transform);
                b.PassengerSeatAdjustment = CreateSeatAdjustmentBinding(b, passenger,
                    NativeShellDynamicId(vehicle, identity), true, null, null);
                BindSeatAdjustmentIdentity(b.PassengerSeatAdjustment!, passenger.gameObject, identity, true);
                if (b.OriginalCaptured) CaptureOriginalShellSeatConfiguration(b);
                diagnostics?.Add(b.Id.ToString("X16") + "\t" + identity + "\tseat-adjustment-passenger");
            }
            if (diagnostics != null) RefreshSeatAdjustmentControls(b, true, diagnostics);
        }
        private static void CaptureOriginalShellSeatConfiguration(Binding b)
        {
            if (!b.OriginalCaptured) return;
            if (b.DriverSeatAdjustment != null) CaptureOriginalSeatConfiguration(b.DriverSeatAdjustment);
            if (b.PassengerSeatAdjustment != null) CaptureOriginalSeatConfiguration(b.PassengerSeatAdjustment);
        }
        private void RefreshNativeShellSeatAdjustmentControls(Binding b, bool force = false)
        {
            if (b.Dormant) return;
            RefreshSeatAdjustmentControls(b, force);
        }
        private void CaptureOrUpdateNativeShellSeatAdjustment(Binding b)
        {
            CaptureOriginalShellSeatConfiguration(b);
            foreach (var adjustment in SeatAdjustments(b).Where(value => value.RecordEnabled))
            {
                if (adjustment.Seat == null) continue;
                if (host)
                {
                    RestorePassengerSeatPosition(adjustment);
                    adjustment.State = CaptureSeatAdjustment(adjustment.Seat, adjustment.State.Generation);
                    if (channel?.Ready == true && channel.Records.ContainsKey(adjustment.Id))
                        channel.Publish(adjustment.Id, adjustment.State.Encode());
                }
                else if (adjustment.Pending.HasValue)
                {
                    ApplySeatAdjustment(adjustment, adjustment.Pending.Value);
                    adjustment.Pending = null;
                }
            }
        }
        private static T[] DiscoverSeatAdjustmentControls<T>(VehicleController vehicle, Konig_Seat seat, Part_Seat? ownedPart,
            List<string> diagnostics, bool repairNullReference) where T : Behaviour
        {

#if MONMP_DEVELOPMENT
            string role = seat.Get_isDriverSeat ? "driver" : "passenger";
#endif


#if MONMP_DEVELOPMENT
            string type = typeof(T).Name;
#endif

            var all = seat.GetComponentsInChildren<T>(true)
                .Where(button => button != null && button.transform != seat.transform && button.transform.IsChildOf(seat.transform)).ToArray();
            var ready = new List<T>();
            foreach (var button in all)
            {
                var referencedPart = SeatPart(button);

#if MONMP_DEVELOPMENT
                bool activeSelf = button.gameObject.activeSelf;
#endif

                bool activeInHierarchy = button.gameObject.activeInHierarchy;

#if MONMP_DEVELOPMENT
                string result;
#endif

                if (!activeInHierarchy || !button.enabled)

#if MONMP_DEVELOPMENT
                    result = "inactive control";
#else
{}
#endif

                else if (ownedPart == null)

#if MONMP_DEVELOPMENT
                    result = "unavailable: seat hierarchy must contain exactly one Part_Seat";
#else
{}
#endif

                else if (referencedPart == null && repairNullReference)
                {
                    AssignSeatPart(button, ownedPart);
                    referencedPart = ownedPart;
                    ready.Add(button);

#if MONMP_DEVELOPMENT
                    result = "ready: repaired null Part_Seat reference from seat hierarchy";
#endif

                }
                else if (referencedPart == null)

#if MONMP_DEVELOPMENT
                    result = "unavailable: serialized Part_Seat reference is null";
#else
{}
#endif

                else if (referencedPart != ownedPart)

#if MONMP_DEVELOPMENT
                    result = "unavailable: serialized Part_Seat reference is not the seat's native child";
#else
{}
#endif

                else
                {
                    ready.Add(button);

#if MONMP_DEVELOPMENT
                    result = "ready";
#endif

                }


#if MONMP_DEVELOPMENT
                var referenceSeat = referencedPart != null ? referencedPart.GetComponentInParent<Konig_Seat>() : null;
#endif


#if MONMP_DEVELOPMENT
                diagnostics.Add(ControlDiagnosticRow(vehicle, seat, role, type, RuntimeProbe.CanonicalPath(button.transform),
                    activeSelf, activeInHierarchy, button.enabled,
                    referencedPart != null ? RuntimeProbe.CanonicalPath(referencedPart.transform) : "-",
                    referenceSeat != null ? RuntimeProbe.CanonicalPath(referenceSeat.transform) : "-", result));
#endif

            }

#if MONMP_DEVELOPMENT
            string summary = ready.Count == 1
                ? "ready: one active control bound to the seat's native Part_Seat"
                : "unavailable: expected one active control bound to the seat's native Part_Seat; found " + ready.Count +
                    " eligible of " + all.Length + " control component(s)";
#endif


#if MONMP_DEVELOPMENT
            diagnostics.Add(ControlDiagnosticRow(vehicle, seat, role, type, "-", false, false, false,
                ownedPart != null ? RuntimeProbe.CanonicalPath(ownedPart.transform) : "-", "-", summary));
#endif

            return ready.ToArray();
        }

#if MONMP_DEVELOPMENT
        private static string ControlDiagnosticRow(VehicleController vehicle, Konig_Seat seat, string role, string type,
            string controlPath, bool activeSelf, bool activeInHierarchy, bool enabled, string partPath, string partSeatPath, string result) =>
            RuntimeProbe.CanonicalPath(vehicle.transform) + "\t" + role + "\t" + RuntimeProbe.CanonicalPath(seat.transform) + "\t" + type + "\t" +
            controlPath + "\t" + activeSelf + "\t" + activeInHierarchy + "\t" + enabled + "\t" + partPath + "\t" + partSeatPath + "\t" + result;
#endif

        private static VehicleSeatAdjustmentState CaptureSeatAdjustment(Binding b, uint generation)
        {
            return CaptureSeatAdjustment(b.KonigDriverSeat!, generation);
        }
        private static VehicleSeatAdjustmentState CaptureSeatAdjustment(Konig_Seat seat, uint generation)
        {
            var local = seat.transform.localPosition;
            return new VehicleSeatAdjustmentState(SeatCentimeters(local.z), SeatCentimeters(local.y), generation);
        }
        private SeatAdjustmentBinding CreateSeatAdjustmentBinding(Binding owner, Konig_Seat seat, ulong id,
            bool recordEnabled, Part_Seat_Forward[]? forwardButtons, Part_Seat_Height[]? heightButtons)
        {
            var adjustment = new SeatAdjustmentBinding
            {
                Vehicle = owner, Id = id, Seat = seat, Driver = seat.Get_isDriverSeat,
                ForwardButton = forwardButtons?.Length == 1 ? forwardButtons[0] : null,
                HeightButton = heightButtons?.Length == 1 ? heightButtons[0] : null,
                State = CaptureSeatAdjustment(seat, 1), RecordEnabled = recordEnabled
            };
            if (owner.OriginalCaptured) CaptureOriginalSeatConfiguration(adjustment);
            return adjustment;
        }
        private void BindSeatAdjustmentIdentity(SeatAdjustmentBinding adjustment, GameObject gameObject, string identity, bool staticEntity)
        {
            if (seatAdjustmentBindings.ContainsKey(adjustment.Id))
                throw new InvalidOperationException("Duplicate Konig seat-adjustment identity: " + adjustment.Id.ToString("X16"));
            identities.Bind(adjustment.Id, VehicleSeatAdjustmentState.Adapter, gameObject, identity, staticEntity,
                EntityPersistence.SessionOnly);
            seatAdjustmentBindings.Add(adjustment.Id, adjustment);
            RestorePassengerSeatPosition(adjustment);
        }
        private static void CaptureOriginalSeatConfiguration(SeatAdjustmentBinding adjustment)
        {
            if (adjustment.OriginalCaptured || adjustment.Seat == null) return;
            var custom = adjustment.Vehicle.Vehicle.vehicle_PartsManager?.Get_CustomParameterToSave;
            if (adjustment.Driver && (custom == null || custom.Length < 2)) return;
            adjustment.OriginalState = CaptureSeatAdjustment(adjustment.Seat, adjustment.State.Generation);
            adjustment.OriginalLocalPosition = adjustment.Seat.transform.localPosition;
            if (adjustment.Driver)
            {
                adjustment.OriginalSeatParameterForward = custom![0];
                adjustment.OriginalSeatParameterHeight = custom[1];
            }
            adjustment.OriginalCaptured = true;
        }
        private static IEnumerable<SeatAdjustmentBinding> SeatAdjustments(Binding binding)
        {
            if (binding.DriverSeatAdjustment != null) yield return binding.DriverSeatAdjustment;
            if (binding.PassengerSeatAdjustment != null) yield return binding.PassengerSeatAdjustment;
        }
        private void RefreshSeatAdjustmentControls(Binding binding, bool force = false, List<string>? diagnostics = null)
        {
            if (binding.Dormant && diagnostics == null) return;
            RefreshSeatAdjustmentControls(binding, binding.DriverSeatAdjustment, force, diagnostics);
            RefreshSeatAdjustmentControls(binding, binding.PassengerSeatAdjustment, force, diagnostics);
        }
        private static void RefreshSeatAdjustmentControls(Binding binding, SeatAdjustmentBinding? adjustment,
            bool force, List<string>? diagnostics)
        {
            if (adjustment == null || adjustment.Seat == null ||
                (!force && Time.unscaledTime - adjustment.ControlsCheckedAt < 0.5f)) return;
            if (diagnostics == null)
            {
                // Ordinary polling needs eligibility only. Building diagnostic rows here
                // walked every ancestor path and allocated arrays/strings only to discard them.
                var partsScratch = adjustment.PartScratch;
                adjustment.Seat.GetComponentsInChildren(true, partsScratch);
                Part_Seat? part = null;
                int count = 0;
                var seatTransform = adjustment.Seat.transform;
                foreach (var found in partsScratch)
                    if (found != null && found.isPlaced && (found.transform == seatTransform || found.transform.IsChildOf(seatTransform)))
                    { part = found; count++; }
                if (count != 1) part = null;
                adjustment.ForwardButton = FindUniqueSeatAdjustmentControl(adjustment.Seat, part, adjustment.ForwardScratch);
                adjustment.HeightButton = FindUniqueSeatAdjustmentControl(adjustment.Seat, part, adjustment.HeightScratch);
                adjustment.ControlsCheckedAt = Time.unscaledTime;
                return;
            }
            var parts = adjustment.Seat.GetComponentsInChildren<Part_Seat>(true)
                .Where(part => part != null && part.isPlaced && (part.transform == adjustment.Seat.transform ||
                    part.transform.IsChildOf(adjustment.Seat.transform))).ToArray();
            var ownedPart = parts.Length == 1 ? parts[0] : null;
            var rows = diagnostics;
            var forward = DiscoverSeatAdjustmentControls<Part_Seat_Forward>(binding.Vehicle, adjustment.Seat,
                ownedPart, rows, true);
            var height = DiscoverSeatAdjustmentControls<Part_Seat_Height>(binding.Vehicle, adjustment.Seat,
                ownedPart, rows, true);
            adjustment.ForwardButton = forward.Length == 1 ? forward[0] : null;
            adjustment.HeightButton = height.Length == 1 ? height[0] : null;
            adjustment.ControlsCheckedAt = Time.unscaledTime;
            if (diagnostics != null && parts.Length != 1)

#if MONMP_DEVELOPMENT
                diagnostics.Add(ControlDiagnosticRow(binding.Vehicle, adjustment.Seat,
                    adjustment.Driver ? "driver" : "passenger", "Part_Seat", "-", false, false, false, "-", "-",
                    "unavailable: expected exactly one installed Part_Seat in the seat hierarchy, found " + parts.Length));
#else
{}
#endif

        }
        private static T? FindUniqueSeatAdjustmentControl<T>(Konig_Seat seat, Part_Seat? ownedPart,
            List<T> scratch) where T : Behaviour
        {
            seat.GetComponentsInChildren(true, scratch);
            T? result = null;
            int count = 0;
            var seatTransform = seat.transform;
            foreach (var button in scratch)
            {
                if (button == null || button.transform == seatTransform || !button.transform.IsChildOf(seatTransform) ||
                    !button.gameObject.activeInHierarchy || !button.enabled || ownedPart == null) continue;
                var referencedPart = SeatPart(button);
                if (referencedPart == null) { AssignSeatPart(button, ownedPart); referencedPart = ownedPart; }
                if (referencedPart != ownedPart) continue;
                result = button; count++;
            }
            return count == 1 ? result : null;
        }
        private static Part_Seat? SeatPart(Component button) => button is Part_Seat_Forward forward ? forward.konig_Seat :
            button is Part_Seat_Height height ? height.konig_Seat : null;
        private static void AssignSeatPart(Component button, Part_Seat part)
        {
            if (button is Part_Seat_Forward forward) forward.konig_Seat = part;
            else if (button is Part_Seat_Height height) height.konig_Seat = part;
        }
        private Binding? FindKonigSeatControlBinding(Component button)
        {
            var seat = SeatPart(button)?.GetComponentInParent<Konig_Seat>() ?? button.GetComponentInParent<Konig_Seat>();
            if (seat == null) return null;
            var controller = seat.vehicleController as VehicleController ?? seat.GetComponentInParent<VehicleController>();
            if (controller == null) return null;
            var b = Find(controller);
            if (b != null) RefreshNativeShellSeatAdjustmentControls(b, force: true);
            return b?.KonigDriverSeat != null && (b.KonigDriverSeat == seat || b.KonigPassengerSeat == seat) ? b : null;
        }
        private SeatAdjustmentBinding? FindSeatAdjustmentBinding(Component button)
        {
            var seat = SeatPart(button)?.GetComponentInParent<Konig_Seat>() ?? button.GetComponentInParent<Konig_Seat>();
            var controller = seat != null ? seat.vehicleController as VehicleController ?? seat.GetComponentInParent<VehicleController>() : null;
            var binding = controller != null ? Find(controller) : null;
            if (binding == null || seat == null) return null;
            RefreshNativeShellSeatAdjustmentControls(binding, force: true);
            if (binding.DriverSeatAdjustment?.Seat == seat) return binding.DriverSeatAdjustment;
            if (binding.PassengerSeatAdjustment?.Seat == seat) return binding.PassengerSeatAdjustment;
            return null;
        }
        private static Component? SeatAdjustmentControl(SeatAdjustmentBinding adjustment, VehicleSeatAdjustmentAxis axis)
            => axis == VehicleSeatAdjustmentAxis.Forward ? adjustment.ForwardButton : adjustment.HeightButton;
        private static bool SeatAdjustmentControlReady(SeatAdjustmentBinding adjustment, Component button,
            VehicleSeatAdjustmentAxis axis)
        {
            if (adjustment.Seat == null || adjustment.Vehicle.Dormant || !adjustment.RecordEnabled ||
                adjustment.Seat.vehicleController != adjustment.Vehicle.Vehicle ||
                adjustment.Seat.Get_isDriverSeat != adjustment.Driver || !adjustment.Seat.isActiveAndEnabled ||
                adjustment.Seat.seatPosition == null || !adjustment.Seat.seatPosition.gameObject.activeInHierarchy ||
                SeatAdjustmentControl(adjustment, axis) != button || !(button is Behaviour behaviour) ||
                !behaviour.isActiveAndEnabled || !behaviour.gameObject.activeInHierarchy) return false;
            var part = SeatPart(button);
            if (part == null || !part.isPlaced || part.GetComponentInParent<Konig_Seat>() != adjustment.Seat) return false;
            var parts = adjustment.Vehicle.Vehicle.vehicle_PartsManager;
            // Native adjustment buttons do not test bolt solidity. CheckSeatSolidity is an
            // entry action that can detach a loose seat, not a read-only control predicate.
            return parts != null && parts.Get_CustomParameterToSave != null && parts.Get_CustomParameterToSave.Length >= 2;
        }
        private static bool TrySeatAdjustmentLimits(Konig_Seat seat,
            out short minimumForward, out short maximumForward, out short minimumHeight, out short maximumHeight)
        {
            minimumForward = maximumForward = minimumHeight = maximumHeight = 0;
            static bool Limits(Konig_Seat target, string minimumName, string maximumName, out short minimum, out short maximum)
            {
                minimum = maximum = 0;
                var minimumField = AccessTools.Field(typeof(Konig_Seat), minimumName);
                var maximumField = AccessTools.Field(typeof(Konig_Seat), maximumName);
                return minimumField?.GetValue(target) is float min && maximumField?.GetValue(target) is float max &&
                    VehicleSeatAdjustmentRange.TryLimits(min, max, out minimum, out maximum);
            }
            return Limits(seat, "MinSeatForward", "MaxSeatForward", out minimumForward, out maximumForward) &&
                Limits(seat, "MinSeatHeight", "MaxSeatHeight", out minimumHeight, out maximumHeight);
        }
        private static bool TrySeatAdjustmentStep(SeatAdjustmentBinding adjustment, VehicleSeatAdjustmentAxis axis,
            int direction, uint generation, out VehicleSeatAdjustmentState next)
        {
            next = default;
            if (adjustment.Seat == null || !TrySeatAdjustmentLimits(adjustment.Seat,
                out var minForward, out var maxForward, out var minHeight, out var maxHeight)) return false;
            var native = CaptureSeatAdjustment(adjustment.Seat, adjustment.State.Generation);
            var command = new VehicleSeatAdjustmentCommand(axis, direction, generation);
            return VehicleSeatAdjustmentPolicy.TryStep(adjustment.State, command,
                native.ForwardCentimeters, native.HeightCentimeters,
                minForward, maxForward, minHeight, maxHeight, out next);
        }
        internal bool SeatAdjustmentButton(Component button, VehicleSeatAdjustmentAxis axis, int direction)
        {
            var konigBinding = FindKonigSeatControlBinding(button);
            if (konigBinding == null || session?.IsRunning != true || applying || disposed) return true;
            if (!Ready || TransfersBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true) return false;
            var adjustment = FindSeatAdjustmentBinding(button);
            if (adjustment == null || !SeatAdjustmentControlReady(adjustment, button, axis) ||
                !TrySeatAdjustmentStep(adjustment, axis, direction, adjustment.State.Generation, out _))
            {
                LogSeatAdjustmentRefusal(konigBinding, adjustment, button, axis);
                return false;
            }
            if (host)
            {
                adjustment.LocalClickPending = true;
                adjustment.LocalClickBefore = adjustment.State;
                adjustment.LocalClickAxis = axis;
                adjustment.LocalClickDirection = direction;
                if (!adjustment.Driver)
                {
                    var custom = adjustment.Vehicle.Vehicle.vehicle_PartsManager?.Get_CustomParameterToSave;
                    if (custom != null && custom.Length >= 2)
                    {
                        adjustment.RestoreParametersAfterLocalClick = true;
                        adjustment.LocalClickParameterForward = custom[0];
                        adjustment.LocalClickParameterHeight = custom[1];
                    }
                }
                return true;
            }
            var vehicle = adjustment.Vehicle;
            bool attached = gameplay.Get_CurrentVehicleController == vehicle.Vehicle;
            if (!VehicleSeatAdjustmentPolicy.AllowsActor(gameplay.Get_PlayerState == PlayerState.Walking,
                attached && gameplay.Get_PlayerState == PlayerState.InVehicle_Driver,
                attached && gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger,
                !vehicle.ExitPending && vehicle.State.Driver.Owner == session!.LocalPeerId,
                !vehicle.PassengerExitPending && vehicle.PassengerState.Occupied && vehicle.PassengerState.Owner == session!.LocalPeerId) ||
                !channel!.Records.ContainsKey(adjustment.Id)) return false;
            channel.Request(adjustment.Id,
                new VehicleSeatAdjustmentCommand(axis, direction, adjustment.State.Generation).Encode(), true);
            return false;
        }
        private float seatRefusalLogAt;
        private void LogSeatAdjustmentRefusal(Binding binding, SeatAdjustmentBinding? adjustment, Component button, VehicleSeatAdjustmentAxis axis)
        {
            if (Time.unscaledTime < seatRefusalLogAt) return;
            seatRefusalLogAt = Time.unscaledTime + 5f;
            Debug.LogWarning("MonMP refused seat adjustment: " + SeatAdjustmentDiagnostic(binding, adjustment, button, axis));
        }
        private static string SeatAdjustmentDiagnostic(Binding binding, SeatAdjustmentBinding? adjustment, Component? button, VehicleSeatAdjustmentAxis axis)
        {
            string text = binding.Vehicle.vehicle_type + " dormant=" + binding.Dormant;
            if (adjustment == null) return text + " binding=missing";
            text += " " + (adjustment.Driver ? "driver" : "passenger") + " record=" + adjustment.RecordEnabled +
                " seatActive=" + (adjustment.Seat != null && adjustment.Seat.isActiveAndEnabled) +
                " accepted=" + adjustment.State.ForwardCentimeters + "/" + adjustment.State.HeightCentimeters +
                " control=" + (SeatAdjustmentControl(adjustment, axis) == null ? "none" : button != null && SeatAdjustmentControl(adjustment, axis) == button ? "same" : "other");
            var part = button != null ? SeatPart(button) : null;
            text += " part=" + (part == null ? "none" : "placed:" + part.isPlaced + ",underSeat:" + (part.GetComponentInParent<Konig_Seat>() == adjustment.Seat));
            text += " custom=" + (binding.Vehicle.vehicle_PartsManager?.Get_CustomParameterToSave?.Length ?? -1);
            if (adjustment.Seat == null) return text;
            var local = adjustment.Seat.transform.localPosition;
            text += " native=" + local.z.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "/" + local.y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
            return text + (TrySeatAdjustmentLimits(adjustment.Seat, out var minForward, out var maxForward, out var minHeight, out var maxHeight)
                ? " limits=" + minForward + ".." + maxForward + "/" + minHeight + ".." + maxHeight : " limits=unavailable");
        }
        internal void NativeSeatAdjustmentButton(Component button)
        {
            if (!host || !Ready || applying || disposed) return;
            var adjustment = FindSeatAdjustmentBinding(button);
            if (adjustment == null || !adjustment.LocalClickPending) return;
            try
            {
                if (adjustment.State.Equals(adjustment.LocalClickBefore) &&
                    TryExpectedSeatAdjustmentStep(adjustment, adjustment.LocalClickBefore, adjustment.LocalClickAxis,
                        adjustment.LocalClickDirection, out var next))
                {
                    var current = CaptureSeatAdjustment(adjustment.Seat, adjustment.State.Generation);
                    if (current.ForwardCentimeters == next.ForwardCentimeters && current.HeightCentimeters == next.HeightCentimeters)
                    {
                        adjustment.State = next;
                        channel!.Publish(adjustment.Id, next.Encode());
                    }
                    else ApplySeatAdjustment(adjustment, adjustment.State);
                }
                else ApplySeatAdjustment(adjustment, adjustment.State);
            }
            finally
            {
                adjustment.LocalClickPending = false;
                if (adjustment.RestoreParametersAfterLocalClick)
                {
                    var custom = adjustment.Vehicle.Vehicle.vehicle_PartsManager?.Get_CustomParameterToSave;
                    if (custom != null && custom.Length >= 2)
                    {
                        custom[0] = adjustment.LocalClickParameterForward;
                        custom[1] = adjustment.LocalClickParameterHeight;
                    }
                    adjustment.RestoreParametersAfterLocalClick = false;
                }
            }
        }
        // Parking brake, light switches and the transfer lever of a vehicle nobody drives belong to the
        // host's simulation, so a guest used to be locked out of them unless it took the wheel (#138).
        private static readonly Type[] RemoteControlTypes = { typeof(Vehicle_Button_ParkingBrake), typeof(Vehicle_Button_LightSwitch),
            typeof(Vehicle_Button_Highbeam), typeof(Vehicle_Button_HazardLights), typeof(Vehicle_Button_ExtraLights), typeof(Vehicle_HighLowShifter),
            // #169: the key cranks while held, so its MouseDown and MouseUp both go to the host.
            typeof(Vehicle_IgnitionSwitch) };
        private readonly Dictionary<uint, (float Started, int Count)> lastControl = new Dictionary<uint, (float, int)>();
        private static Component[] Controls(Binding b) => b.Controls ??= b.Vehicle.GetComponentsInChildren<Component>(true)
            .Where(c => c != null && RemoteControlTypes.Contains(c.GetType()))
            .OrderBy(c => RuntimeProbe.CanonicalPath(c.transform), StringComparer.Ordinal).ThenBy(c => c.GetType().Name, StringComparer.Ordinal).ToArray();
        private static Vehicle_Button_LightDomeOptions[] DomeModeControls(Binding b) => b.DomeModeControls ??=
            b.Vehicle.GetComponentsInChildren<Vehicle_Button_LightDomeOptions>(true)
                .Where(c => c != null)
                .OrderBy(c => RuntimeProbe.CanonicalPath(c.transform), StringComparer.Ordinal).ToArray();

#if MONMP_DEVELOPMENT
        internal int TestControlRequests { get; private set; }


        internal int TestControlsApplied { get; private set; }


        internal int TestDomeModeRequests { get; private set; }


        internal int TestDomeModesApplied { get; private set; }
#endif

        // Returns true when the click was sent to the host instead of running natively.
        internal bool RequestControl(Component control, string method)
        {
            if (MonMPPlugin.Instance?.World?.PassengerControls?.Request(control, method) == true) return true;
            if (host || applying || !Ready || session?.IsRunning != true || control == null) return false;
            var b = bindings.Values.FirstOrDefault(value => control.transform.IsChildOf(value.Vehicle.transform));
            if (b == null || b.Dormant || IsLocal(b.State)) return false;
            int index = Array.IndexOf(Controls(b), control);
            if (index < 0 || !Enum.TryParse(method, out VehicleControlMethod parsed)) return false;
            if (b.State.Driver.Owner != ItemLease.Unowned && b.State.Driver.Owner != 0)
            {
                if (parsed != VehicleControlMethod.MouseUp) MonMPPlugin.Instance?.ReportItemUseUnavailable("Someone else is driving this vehicle.");
                return true;
            }
            if (TransfersBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true) return true;
            channel!.Request(b.Id, new VehicleControlCommand((ushort)index, parsed).Encode(), true);

#if MONMP_DEVELOPMENT
            TestControlRequests++;
#endif

            QueuePromptRefresh(control);
            return true;
        }
        // #164: a native click refreshes the hover prompt itself (ResetCurrentInteractable); a guest's
        // click only changes state once the host's reply arrives, so refresh the prompt after it.
        private readonly List<(Interactable Control, float At)> promptRefreshes = new List<(Interactable, float)>();
        private void QueuePromptRefresh(Component control)
        {
            if (control is not Interactable interactable) return;
            promptRefreshes.Add((interactable, Time.unscaledTime + 0.25f));
            promptRefreshes.Add((interactable, Time.unscaledTime + 0.8f));
        }
        // #164: switch labels ([ON]/[OFF], dome mode) only refresh on re-hover. A peer's toggle, or the host's
        // reply to this player's click, can land at any time, so refresh while the hovered vehicle's lights change.
        private Interactable? hoveredControl; private int hoveredLights = -1;
        private void WatchHoveredLights()
        {
            var player = gameplay.Player; var current = player?.Get_CurrentInteractable;
            if (current == null || !current.GetType().Name.StartsWith("Vehicle_Button_", StringComparison.Ordinal)) { hoveredControl = null; return; }
            var vehicle = current.GetComponentInParent<VehicleController>(); var m = vehicle?.effectsManager?.lightsManager;
            if (m == null) { hoveredControl = null; return; }
            int key = (m.lowBeamLights?.On == true ? 1 : 0) | (m.highBeamLights?.On == true ? 2 : 0) | (m.extraLights?.On == true ? 4 : 0) |
                (m.interiorLights?.On == true ? 8 : 0) | (vehicle!.InteriorLightMode << 4);
            if (current == hoveredControl && key != hoveredLights) player!.ResetCurrentInteractable(current);
            hoveredControl = current; hoveredLights = key;
        }
        private void RefreshPrompts()
        {
            if (promptRefreshes.Count == 0) return;
            float now = Time.unscaledTime;
            for (int i = promptRefreshes.Count - 1; i >= 0; i--)
            {
                var (control, at) = promptRefreshes[i];
                if (now < at) continue;
                promptRefreshes.RemoveAt(i);
                var player = gameplay.Player;
                if (control != null && player != null && player.Get_CurrentInteractable == control) player.ResetCurrentInteractable(control);
            }
        }
        // Native dome-mode clicks are gated to the local vehicle driver; route a walk-up guest's
        // click to the host while keeping their indices outside the existing control table (#145).
        internal bool RequestDomeMode(Vehicle_Button_LightDomeOptions control)
        {
            if (MonMPPlugin.Instance?.World?.PassengerControls?.Request(control, "ChangeLightMode") == true) return true;
            if (host || applying || !Ready || session?.IsRunning != true || control == null) return false;
            var b = bindings.Values.FirstOrDefault(value => control.transform.IsChildOf(value.Vehicle.transform));
            if (b == null || b.Dormant || IsLocal(b.State)) return false;
            int index = Array.IndexOf(DomeModeControls(b), control);
            if (index < 0 || index > ushort.MaxValue - VehicleControlCommand.DomeModeControlBase) return false;
            if (b.State.Driver.Owner != ItemLease.Unowned && b.State.Driver.Owner != 0)
            { MonMPPlugin.Instance?.ReportItemUseUnavailable("Someone else is driving this vehicle."); return true; }
            if (TransfersBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true) return true;
            ushort controlId = (ushort)(VehicleControlCommand.DomeModeControlBase + index);
            channel!.Request(b.Id, new VehicleControlCommand(controlId, VehicleControlMethod.MouseDown).Encode(), true);

#if MONMP_DEVELOPMENT
            TestControlRequests++;
#endif

            QueuePromptRefresh(control);

#if MONMP_DEVELOPMENT
            TestDomeModeRequests++;
#endif

            return true;
        }
        private void CommandControl(Binding b, uint peer, VehicleControlCommand command)
        {
            string? reject = null;
            bool domeMode = command.Control >= VehicleControlCommand.DomeModeControlBase;
            Behaviour? control = null;
            Vehicle_Button_LightDomeOptions? dome = null;
            if (domeMode && command.Method == VehicleControlMethod.MouseDown)
            {
                int index = command.Control - VehicleControlCommand.DomeModeControlBase;
                var domes = DomeModeControls(b);
                if ((uint)index < (uint)domes.Length) control = dome = domes[index];
            }
            else if (!domeMode)
            {
                var controls = Controls(b);
                if (command.Control < controls.Length) control = controls[command.Control] as Behaviour;
            }
            if (peer == 0 || TransfersBlocked || (drivers[b.Id].Owner != ItemLease.Unowned && drivers[b.Id].Owner != 0)) reject = "driven or blocked";
            else if (domeMode && command.Method != VehicleControlMethod.MouseDown) reject = "invalid dome control method";
            else if (control == null || !control.isActiveAndEnabled) reject = "control " + command.Control + " unavailable";
            else if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2) reject = "no fresh pose";
            else if ((new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z) - control.transform.position).sqrMagnitude > 25) reject = "too far";
            else if (lastControl.TryGetValue(peer, out var window) && Time.unscaledTime - window.Started < 1f && window.Count >= 8) reject = "too fast";
            Debug.Log("MonMP vehicle control from peer " + peer + ": " + (control == null ? "?" : control.GetType().Name) + "." + command.Method +
                " on " + b.Vehicle.vehicle_type + (reject == null ? " applied" : " rejected: " + reject));
            if (reject != null || control == null) return;
            lastControl[peer] = lastControl.TryGetValue(peer, out var current) && Time.unscaledTime - current.Started < 1f
                ? (current.Started, current.Count + 1) : (Time.unscaledTime, 1);
            applying = true;
            try
            {
                if (domeMode) dome!.ChangeLightMode();
                else
                {
                    var native = AccessTools.Method(control.GetType(), command.Method.ToString(), Type.EmptyTypes);
                    if (native == null) return;
                    native.Invoke(control, null);
                }
            }
            finally { applying = false; }

#if MONMP_DEVELOPMENT
            TestControlsApplied++;
#endif

            if (domeMode)
#if MONMP_DEVELOPMENT
TestDomeModesApplied++;
#else
{}
#endif

        }

#if MONMP_DEVELOPMENT
        internal void TestClickControl<T>(VehicleControlMethod method) where T : Component
        {
            var control = CurrentVehicle.GetComponentsInChildren<T>(true).First();
            AccessTools.Method(control.GetType(), method.ToString(), Type.EmptyTypes).Invoke(control, null);
        }
#endif

        private void CommandSeatAdjustment(SeatAdjustmentBinding adjustment, uint peer, byte[] payload)
        {
            var command = VehicleSeatAdjustmentCommand.Decode(payload);
            if (peer == 0 || TransfersBlocked || MonMPPlugin.Instance?.World?.SaveGate.Active == true ||
                adjustment.Vehicle.Dormant || !adjustment.RecordEnabled) return;
            var control = SeatAdjustmentControl(adjustment, command.Axis);
            if (control == null || !SeatAdjustmentControlReady(adjustment, control, command.Axis)) return;
            if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2 ||
                !VehicleSeatAdjustmentPolicy.AllowsActor(p.Pose.State == (byte)PlayerState.Walking,
                    p.Pose.State == (byte)PlayerState.InVehicle_Driver,
                    p.Pose.State == (byte)PlayerState.InVehicle_Passenger,
                    adjustment.Vehicle.State.Driver.Owner == peer,
                    adjustment.Vehicle.PassengerState.Occupied && adjustment.Vehicle.PassengerState.Owner == peer) ||
                MonMPPlugin.Instance?.Items?.PeerHolding(peer) == true) return;
            var location = new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z);
            if ((location - control.transform.position).sqrMagnitude > 16 ||
                !TrySeatAdjustmentStep(adjustment, command.Axis, command.Direction, command.ExpectedGeneration, out var next)) return;
            applying = true;
            try { ApplySeatAdjustment(adjustment, next); }
            finally { applying = false; }
            channel!.Publish(adjustment.Id, next.Encode());
        }
        private static bool TryExpectedSeatAdjustmentStep(SeatAdjustmentBinding adjustment,
            VehicleSeatAdjustmentState before, VehicleSeatAdjustmentAxis axis, int direction,
            out VehicleSeatAdjustmentState next)
        {
            next = default;
            if (adjustment.Seat == null || !TrySeatAdjustmentLimits(adjustment.Seat,
                out var minForward, out var maxForward, out var minHeight, out var maxHeight)) return false;
            var command = new VehicleSeatAdjustmentCommand(axis, direction, before.Generation);
            return VehicleSeatAdjustmentPolicy.TryStep(before, command,
                before.ForwardCentimeters, before.HeightCentimeters,
                minForward, maxForward, minHeight, maxHeight, out next);
        }
        private void ApplySeatAdjustment(SeatAdjustmentBinding adjustment, VehicleSeatAdjustmentState state)
        {
            var binding = adjustment.Vehicle;
            if (IsNativeKonigShell(binding.Vehicle.vehicle_type) && binding.Dormant)
            {
                adjustment.Pending = state;
                adjustment.State = state;
                return;
            }
            if (adjustment.Seat == null) throw new InvalidDataException("Konig seat adjustment has no native seat: " + binding.Vehicle.vehicle_type + ".");
            if (!TrySeatAdjustmentLimits(adjustment.Seat,
                out var minForward, out var maxForward, out var minHeight, out var maxHeight) ||
                state.ForwardCentimeters < minForward || state.ForwardCentimeters > maxForward ||
                state.HeightCentimeters < minHeight || state.HeightCentimeters > maxHeight)
                throw new InvalidDataException("Konig seat adjustment is outside the native seat range: vehicle=" + binding.Vehicle.vehicle_type +
                    "; seat=" + (adjustment.Driver ? "driver" : "passenger") + "; state=" + state.ForwardCentimeters + "/" + state.HeightCentimeters +
                    "; forward=" + minForward + ".." + maxForward + "; height=" + minHeight + ".." + maxHeight + ".");
            if (!adjustment.RecordEnabled) return;
            float forward = state.ForwardCentimeters / 100f, height = state.HeightCentimeters / 100f;
            var parts = binding.Vehicle.vehicle_PartsManager;
            var custom = parts?.Get_CustomParameterToSave;
            if (adjustment.Driver && (custom == null || custom.Length < 2))
            {
                if (IsNativeKonigShell(binding.Vehicle.vehicle_type))
                {
                    adjustment.Pending = state;
                    adjustment.State = state;
                    return;
                }
                throw new InvalidDataException("Konig seat adjustment parameters are unavailable.");
            }
            adjustment.State = state;
            var local = adjustment.Seat.transform.localPosition;
            if (Mathf.Abs(local.z - forward) > 0.001f || Mathf.Abs(local.y - height) > 0.001f)
            {
                adjustment.Seat.LoadFromSaveSystem(forward, height);
                if (gameplay.Get_CurrentVehicleController == binding.Vehicle &&
                    ((adjustment.Driver && gameplay.Get_PlayerState == PlayerState.InVehicle_Driver) ||
                     (!adjustment.Driver && gameplay.Get_PlayerState == PlayerState.InVehicle_Passenger)))
                    gameplay.AdjustSeat();
            }
            if (adjustment.Driver)
            {
                custom![0] = forward;
                custom[1] = height;
            }
        }
        private void Release(Binding b, uint owner, bool secure = false)
        {
            if (!drivers.Release(b.Id, owner, b.State.Driver.Generation)) return;
            var state = (IsLocal(b.State) ? Capture(b) : b.State).WithDriver(drivers[b.Id]);
            if (NativeVehicleDeparture.TryExit(b.Vehicle, true, state.Pose, out var exit)) DriverExited?.Invoke(owner, exit);
            secure |= b.DisconnectSecuring;
            if (secure) state = NativeVehicleDeparture.Parked(state);
            Apply(b, state);
            if (secure) NativeVehicleDeparture.SetParkingBrake(b.Vehicle, ParkingBrakeOf(b));
            b.DisconnectSecuring = false;
            Publish(b);
        }
        private void OnLeft(uint peer)
        {
            presence.Remove(peer);
            if (!host) return;
            foreach (var b in bindings.Values.Where(b => drivers[b.Id].Owner == peer).ToArray()) Release(b, peer, secure: true);
            RevokeOwnership(peer);
        }
        private void SetMode(Binding b, bool local)
        {
            var v = b.Vehicle;
            var mode = local ? Vehicle.MultiplayerInstanceType.Local : Vehicle.MultiplayerInstanceType.Remote;
            if (b.Accessory != null && b.Accessory.isKinematic == local)
            {
                b.Accessory.isKinematic = !local;
                if (local) { b.Accessory.velocity = v.vehicleRigidbody.velocity; b.Accessory.angularVelocity = Vector3.zero; }
            }
            if (v.VehicleMultiplayerInstanceType != mode)
            {
                b.Presentation.Clear();
                b.PhysicsCorrectionVelocity = Vector3.zero;
                v.SetMultiplayerInstanceType(mode, true);
                if (!local)
                {
                    var ignition = v.GetComponentInChildren<Vehicle_IgnitionSwitch>();
                    if (ignition != null) { ignition.MouseUp(); ignition.StopAllCoroutines(); }
                }
                v.input.autoSetInput = local && b.AutoInput;
                v.freezeWhileIdle = local && b.FreezeIdle; v.freezeWhileAsleep = local && b.FreezeSleep;
                v.vehicleRigidbody.constraints = RigidbodyConstraints.None;
                if (local)
                    for (int i = 0; i < Math.Min(v.Wheels.Count, b.ExternalWheels.Length); i++)
                    {
                        var wheel = v.Wheels[i]?.wheelController;
                        if (wheel == null) continue;
                        if (b.ExternalWheels[i]) wheel.UseExternalUpdate(); else wheel.UseInternalUpdate();
                    }
                v.Wake();
            }
            RemoteVehiclePose.ConfigureInterpolation(v.vehicleRigidbody, local, host, b.OriginalInterpolation);
            ApplyLiftFreeze(b, local);
            if (!local || b.State.Driver.Owner != session?.LocalPeerId) v.SetInputToPlayer(false);
        }
        private void ApplyLiftFreeze(Binding b, bool local)
        {
            bool frozen = host ? FrozenOnCarLift(b.Vehicle) : b.State.LiftFrozen;
            if (frozen) b.PhysicsCorrectionVelocity = Vector3.zero;
            NativeVehicleLiftFreeze.Apply(b.Vehicle.vehicleRigidbody, local, frozen, ref b.LiftFreezeApplied);
        }
        private void Apply(Binding b, VehicleState state)
        {
            if (!host && HasSharedLifecycle(b.Vehicle.vehicle_type)) ApplySharedLifecycle(b, state);
            if (b.Dormant) { b.PendingState = state; return; }
            if (state.Accessory.HasValue != (b.Accessory != null)) throw new InvalidDataException("Vehicle accessory layout mismatch.");
            SeatAdmission?.Observe(this, b.Id, state.Driver.Generation);
            bool wasLocal = IsLocal(b.State), local = IsLocal(state);
            bool ownerChanged = b.State.Driver.Owner != state.Driver.Owner || b.State.Driver.Generation != state.Driver.Generation;
            double previousRefuelCredit = b.State.RefuelCredit;
            if (gameplay.Get_CurrentVehicleController == b.Vehicle && gameplay.Get_PlayerState == PlayerState.InVehicle_Driver &&
                state.Driver.Owner != session!.LocalPeerId) ExitLocal();
            b.State = state;
            if (ownerChanged) b.ExitPending = false;
            SetMode(b, local);
            bool applyBody = !local || !wasLocal || ownerChanged || b.BodyApplyPending;
            if (applyBody)
            {
                // Ownership is recorded before native application. Retain the retry intent
                // until the entire body succeeds, including delayed drivetrain startup.
                b.BodyApplyPending = true;
                // A retry on the unchanged local simulator restores what a failed apply left
                // undone, never its motion: the record is this peer's own older echo.
                ApplyBody(b, state, !local && b.HasPresentationPose && !ownerChanged, local && wasLocal && !ownerChanged);
                b.BodyApplyPending = false;
            }
            ApplyRefuelCredit(b, previousRefuelCredit, local, ownerChanged, applyBody);
            if (local && state.Driver.Owner == ItemLease.Unowned) b.Vehicle.input.states.Reset();
            if (state.Driver.Owner == session!.LocalPeerId && gameplay.Get_CurrentVehicleController != b.Vehicle && !b.ExitPending)
            {
                if (TransfersBlocked || SeatAdmission?.AllowsLocalEntry(this, b.Id, state.Driver.Generation,
                    b.TryGetDriverSeat(out _, out var waitingSeat) && StillWaitingAt(waitingSeat)) == false ||
                    gameplay.Get_PlayerState != PlayerState.Walking || gameplay.Player.IsHoldingAnObject) { RequestExit(b); return; }
                if (!b.TryGetDriverSeat(out _, out var driverPosition) || driverPosition == null)
                { RequestExit(b); return; }
                applying = true;
                try { gameplay.EnterExitVehicle(b.Vehicle, driverPosition, true); b.EnteredFrame = Time.frameCount; }
                finally { applying = false; }
            }
        }
        private static void ApplySharedLifecycle(Binding binding, VehicleState state)
        {
            var vehicle = binding.Vehicle;
            bool activating = state.Active && !vehicle.gameObject.activeSelf;
            // Apply the host's location before making a stored/incomplete root visible. Its
            // native original location must never contribute a frame of guest collision.
            if (binding.Dormant || activating)
            {
                binding.Presentation.Clear();
                binding.PhysicsCorrectionVelocity = Vector3.zero;
                var position = Position(state.Pose);
                var rotation = new Quaternion(state.Pose.Qx, state.Pose.Qy, state.Pose.Qz, state.Pose.Qw);
                vehicle.transform.SetPositionAndRotation(position, rotation);
                var body = vehicle.vehicleRigidbody != null ? vehicle.vehicleRigidbody : vehicle.GetComponent<Rigidbody>();
                if (body != null)
                {
                    if (binding.Dormant)
                    {
                        body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                        body.isKinematic = true;
                    }
                    body.position = position; body.rotation = rotation;
                }
            }
            if (vehicle.gameObject.activeSelf != state.Active) vehicle.gameObject.SetActive(state.Active);
            // BuggyBuild/RacecarBuild own their controls and controller activation. The
            // shared car record enables its parent root; it must not bypass those build steps.
            if (state.MotionReady && !binding.Buggy) vehicle.enabled = true;
            if (binding.Dormant) FreezeDormantPhysics(binding);
        }
        private static void FreezeDormantPhysics(Binding binding)
        {
            var body = binding.Vehicle.vehicleRigidbody;
            if (body == null) return;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.isKinematic = true;
        }
        private void FreezePendingGuestMotion()
        {
            if (host) return;
            foreach (var binding in bindings.Values)
                if (binding.Dormant && binding.Vehicle != null && HasSharedLifecycle(binding.Vehicle.vehicle_type) &&
                    binding.Vehicle.gameObject.activeInHierarchy) FreezeDormantPhysics(binding);
        }
        private void ApplyBody(Binding b, VehicleState s, bool smoothPose = false, bool keepLocalMotion = false)
        {
            var v = b.Vehicle; var body = v.vehicleRigidbody;
            if (keepLocalMotion) { ApplyBodyState(b, s, true); return; }
            var targetPosition = Position(s.Pose);
            var targetRotation = new Quaternion(s.Pose.Qx, s.Pose.Qy, s.Pose.Qz, s.Pose.Qw);
            bool snap = !smoothPose || (body.position - targetPosition).sqrMagnitude > 100f;
            if (!snap)
            {
                b.PoseFrom = body.position; b.RotationFrom = body.rotation;
            }
            else
            {
                b.PhysicsCorrectionVelocity = Vector3.zero;
                body.position = targetPosition; body.rotation = targetRotation;
                v.transform.SetPositionAndRotation(body.position, body.rotation);
                b.PoseFrom = targetPosition; b.RotationFrom = targetRotation;
            }
            b.PoseTo = targetPosition; b.RotationTo = targetRotation;
            b.PoseVelocity = Vector(s.Velocity); b.PoseAngularVelocity = Vector(s.AngularVelocity);
            b.PoseReceivedAt = Time.unscaledTime; b.HasPresentationPose = true;
            if (!host) b.Presentation.Accept(targetPosition, targetRotation,
                s.LiftFrozen ? Vector3.zero : b.PoseVelocity, s.LiftFrozen ? Vector3.zero : b.PoseAngularVelocity,
                b.PoseReceivedAt, snap);
            if (b.Accessory != null && s.Accessory.HasValue)
            {
                var a = s.Accessory.Value;
                b.Accessory.transform.localPosition = Position(a);
                b.Accessory.transform.localRotation = new Quaternion(a.Qx, a.Qy, a.Qz, a.Qw);
                b.Accessory.position = b.Accessory.transform.position; b.Accessory.rotation = b.Accessory.transform.rotation;
                if (!b.Accessory.isKinematic) { b.Accessory.velocity = Vector(s.Velocity); b.Accessory.angularVelocity = Vector3.zero; }
            }
            if (!body.isKinematic) { body.velocity = Vector(s.Velocity); body.angularVelocity = Vector(s.AngularVelocity); }
            ApplyBodyState(b, s, false);
        }
        private void ApplyBodyState(Binding b, VehicleState s, bool keepLocalMotion)
        {
            var v = b.Vehicle;
            int previousIgnition = v.IgnitionPosition;
            v.IgnitionPosition = s.Ignition; v.powertrain.engine.ignition = s.EngineIgnition;
            // The field write skips the native ignition events. Replaying those events replays their
            // serialized listeners too (a guest replica then sent the host ignition-switch clicks).
            // Notify the dedicated underglow and radio adapters without replaying those listeners.
            if ((previousIgnition == 0) != (s.Ignition == 0)) SharedUnderglow.IgnitionChanged(v, s.Ignition > 0);
            MonMPPlugin.Instance?.World?.FurnitureRadios.IgnitionChanged(v, previousIgnition, s.Ignition);
            // Update the native key visual without replaying crank/ignition events.
            applyingMechanics = true;
            try { v.GetComponentInChildren<Vehicle_IgnitionSwitch>()?.LoadFromSave(s.Ignition); }
            finally { applyingMechanics = false; }
            if (!keepLocalMotion) { v.powertrain.engine.StarterActive = s.Starter; v.powertrain.engine.angularVelocity = s.EngineAngularVelocity; }
            ApplyTransmissionState(v.powertrain.transmission, s);
            RefreshGearPresentation(b);
            // Wire values are already normalized by the owner's input handler. Applying
            // property setters again would invert controls a second time on some assets.
            if (!keepLocalMotion)
            {
                v.input.states.steering = s.Steering; v.input.states.throttle = s.Throttle; v.input.states.brakes = s.Brake;
                v.brakes.handbrakeValue = s.Handbrake;
                // Already normalized by the sender; the setter would apply inversion again.
                v.input.states.handbrake = s.HandbrakeInput;
            }
            var parking = ParkingBrakeOf(b);
            if (parking != null && parking.LoadFromSave != s.ParkingBrake) parking.LoadFromSave = s.ParkingBrake;
            // Native dashboard updates this lever only with player input enabled.
            parking?.VisualValue(s.HandbrakeInput);
            var transfer = v.GetComponentInChildren<Vehicle_HighLowShifter>();
            // Konig custom parameters [0] and [1] are seat forward/height, not a transfer case.
            if (b.KonigDriverSeat == null && transfer != null && v.vehicle_PartsManager.Get_CustomParameterToSave?.Length > 0 && v.vehicle_PartsManager.Get_CustomParameterToSave[0] != s.TransferCase)
                transfer.LoadFromSaveSystem(s.TransferCase);
            if (MonMPPlugin.Instance?.World?.Fuel.Busy(v.vehicle_PartsManager.Fuel_Tank) != true) v.vehicle_PartsManager.Fuel_Tank.Get_FuelLevel = s.Fuel;
            // The driver's own lamp switches and thermal state are newer than its echoed record.
            if (keepLocalMotion) return;
            NativeVehicleLighting.Apply(v, s.Lighting);
            // Restore both native thermal reservoirs before a new simulator can update them.
            ApplyHeat(b, s.Effects);
        }
        private static void RefreshGearPresentation(Binding b)
        {
            var v = b.Vehicle;
            // DashGUIController.Update only updates gears while input.IsOn. Remote replicas
            // deliberately disable player input, so their gearbox changes but their knob and
            // dashboard stay at the previous gear until somebody enters the vehicle.
            // Resolve inactive dashboards too, including controls installed after construction.
            if (b.GearDisplays == null || Time.unscaledTime - b.GearDisplaysCheckedAt >= 2f)
            {
                b.GearDisplays = v.GetComponentsInChildren<DashGUIController>(true);
                b.GearDisplaysCheckedAt = Time.unscaledTime;
            }
            var transmission = v.powertrain.transmission;
            string gear = transmission.TransmissionType == TransmissionComponent.Type.AutomaticSequential && transmission.Gear > 0
                ? "D" : transmission.GearName;
            // With the ignition off the native dashboard blanks its gear readouts every frame;
            // writing them here would flash the gear for one frame. The shifter still moves.
            bool powered = v.Get_IgnitionPosition > 0;
            foreach (var display in b.GearDisplays)
            {
                if (display == null || display.vehicleController != v || !display.useDigitalGearGauge) continue;
                if (powered && display.digitalGearGauge != null) display.digitalGearGauge.stringValue = gear;
                if (display.useVisualGearKnob)
                {
                    display.visualGearKnob_Manual?.GearChanged(gear);
                    display.visualGearKnob_Auto?.GearChanged(gear);
                }
                if (!powered) continue;
                foreach (var gauge in display.AutometerGearGauges)
                    if (gauge != null) gauge.SetString(gear);
            }
        }
        private static void ApplyTransmissionState(TransmissionComponent transmission, VehicleState state)
        {
            if (transmission.TransmissionType != TransmissionComponent.Type.AutomaticSequential)
            {
                transmission.ShiftInto(state.Gear, true);
                transmission.automaticParkModeEngage = state.AutomaticPark;
                return;
            }
            // ShiftInto is a driver-input transition, even with instant=true:
            // P + requested 1 means R; requested R while already in R means P.
            // A received final gear must not be interpreted as another shift click.
            // ApplyBody excludes the unchanged local simulator, so its native shift
            // events and input behavior still run on the peer that owns the driver.
            if (transmission.gearingProfile == null || state.Gear < -transmission.ReverseGearCount || state.Gear > transmission.ForwardGearCount)
                // Parts apply after vehicles and native Update_Drivetrain rebuilds the profile
                // later. This is local readiness, not proof of a malformed wire state.
                throw new InvalidOperationException("Native transmission profile is not ready for the replicated gear.");
            float ratio = transmission.GetGearRatio(state.Gear);
            if (float.IsNaN(ratio) || float.IsInfinity(ratio))
                throw new InvalidOperationException("Native replicated transmission ratio is not ready.");
            CurrentGearIndex(transmission) = state.Gear;
            transmission.ratio = ratio;
            transmission.automaticParkModeEngage = state.AutomaticPark;
        }
        private VehicleState Capture(Binding b)
        {
            var v = b.Vehicle; var p = v.transform.position; var q = v.transform.rotation;
            if (host) NativeVehicleLighting.RefreshInterior(v);
            return VehicleStatePublication.Reuse(b.State, host || b.State == null ? drivers[b.Id] : b.State.Driver, new ItemPose(p.x, p.y, p.z, q.x, q.y, q.z, q.w),
                Vector(v.vehicleRigidbody.velocity), Vector(v.vehicleRigidbody.angularVelocity), v.vehicle_PartsManager.Fuel_Tank.Get_FuelLevel,
                v.powertrain.engine.angularVelocity, v.input.Steering, v.input.Throttle, v.input.Brakes, v.brakes.handbrakeValue,
                v.powertrain.transmission.Gear, v.IgnitionPosition, v.powertrain.engine.ignition, v.powertrain.engine.StarterActive,
                ParkingBrakeOf(b)?.LoadFromSave ?? 0,
                b.KonigDriverSeat == null && v.vehicle_PartsManager.Get_CustomParameterToSave?.Length > 0
                    ? Mathf.RoundToInt(v.vehicle_PartsManager.Get_CustomParameterToSave[0]) : 0,
                b.Accessory != null ? LocalPose(b.Accessory.transform) : (ItemPose?)null, NativeVehicleLighting.Capture(v), CaptureWheelSpin(b), CaptureWheelAngles(b), CaptureEffects(b), v.input.Handbrake,
                v.powertrain.transmission.automaticParkModeEngage, v.gameObject.activeSelf, true, b.State?.RefuelCredit ?? 0,
                host ? FrozenOnCarLift(v) : b.State?.LiftFrozen == true,
                // A parked body never sleeps natively; do not stream its float noise.
                restDeadband: host && drivers[b.Id].Owner == ItemLease.Unowned);
        }
        // Vehicle_PartsManager.Awake resolves its lever with an active-only lookup, which misses the built
        // racecar's lever (its controls pack is installed after Awake), so its state never replicated (#178).
        private static Vehicle_Button_ParkingBrake? ParkingBrakeOf(Binding b)
        {
            if (b.ParkingBrake != null) return b.ParkingBrake;
            if (Time.unscaledTime - b.ParkingBrakeCheckedAt < 2f) return null;
            b.ParkingBrakeCheckedAt = Time.unscaledTime;
            var v = b.Vehicle;
            Vehicle_Button_ParkingBrake? found = v.vehicle_PartsManager.ParkingBrake;
            if (found == null && v.brakes != null) found = v.brakes.Button_ParkingBrake;
            if (found == null) found = v.GetComponentsInChildren<Vehicle_Button_ParkingBrake>(true).FirstOrDefault(p => p.vehicleController == null || p.vehicleController == v);
            return b.ParkingBrake = found == null ? null : found;
        }
        private static ItemPose LocalPose(Transform t)
        {
            var p = t.localPosition; var q = t.localRotation; return new ItemPose(p.x, p.y, p.z, q.x, q.y, q.z, q.w);
        }
        private void Publish(Binding b)
        {
            var state = b.State;
            channel!.Records.TryGetValue(b.Id, out var current);
            if (VehicleStatePublication.CanSkip(state, b.LastPublishedState, b.LastPublishedRecord, current)) return;
            var payload = state.Encode();
            channel.Publish(b.Id, payload);
            b.LastPublishedState = state;
            // Changed handlers may synchronously publish another value. Only retain
            // the shortcut when the final channel record still holds our payload.
            b.LastPublishedRecord = channel.Records.TryGetValue(b.Id, out var published) && published.SameValue(payload) ? published : null;
        }
        private bool PublishInactiveLifecycle(Binding binding)
        {
            if (!host || !HasSharedLifecycle(binding.Vehicle.vehicle_type)) return false;
            if (binding.Dormant)
            {
                binding.State = DormantState(binding, drivers[binding.Id]);
                Publish(binding);
                return true;
            }
            if (!binding.Vehicle.gameObject.activeInHierarchy)
            {
                binding.State = Capture(binding);
                Publish(binding);
                return true;
            }
            return false;
        }
        internal ItemCarriers? Carriers { get; set; }
        internal IEnumerable<(ulong Id, Transform Frame)> CarrierFrames()
        {
            foreach (var b in bindings.Values)
                if (!b.Dormant && b.Vehicle != null) yield return (b.Id, b.Vehicle.transform);
        }
        // A guest-driven vehicle on the host: moved through every physics step, not teleported, so hinged
        // doors and cargo see the body's real velocity. Each step advances at the driver's reported
        // velocity and eases out the remaining error. Re-lerping each 20 Hz sample from Update gave the
        // bed a sawtooth velocity (and skipped steps entirely), which tumbled loose cargo (#119).
        internal void FixedUpdate()
        {
            if (disposed || !Ready) return;
            // A guest's previously built car does not run native Start again when build replay
            // releases its body. Keep pending authority frozen before the physics integration.
            FreezePendingGuestMotion();
            if (!host) return;
            float dt = Time.fixedDeltaTime, k = 1 - Mathf.Exp(-8f * dt);
            foreach (var b in bindings.Values)
            {
                if (b.Dormant || b.Vehicle == null || !b.Vehicle.gameObject.activeInHierarchy || !b.HasPresentationPose || IsLocal(b.State)) continue;
                var body = b.Vehicle.vehicleRigidbody;
                if (body == null || !body.isKinematic || b.State.LiftFrozen) continue;
                float age = Mathf.Max(0, Time.unscaledTime - b.PoseReceivedAt);
                var predicted = b.PoseTo + b.PoseVelocity * Mathf.Min(age, 0.25f);
                var spin = b.PoseAngularVelocity * Mathf.Rad2Deg;
                var predictedRotation = Quaternion.Euler(spin * Mathf.Min(age, 0.25f)) * b.RotationTo;
                if ((body.position - predicted).sqrMagnitude > 25f) { SnapRemoteCarrier(b, predicted, predictedRotation); continue; }
                float advance = Mathf.Clamp(0.25f - age, 0, dt);
                // Correct packet timing error with continuous correction velocity. A
                // first-order position lerp changes bed speed at every packet receipt.
                var position = Vector3.SmoothDamp(body.position + b.PoseVelocity * advance, predicted,
                    ref b.PhysicsCorrectionVelocity, 0.2f, float.PositiveInfinity, dt);
                var rotation = Quaternion.Slerp(Quaternion.Euler(spin * advance) * body.rotation, predictedRotation, k);
                if ((body.position - position).sqrMagnitude < 1e-10f && Quaternion.Angle(body.rotation, rotation) < 0.001f) continue;
                KinematicCarrierMotion.Record(body, position, rotation);
                body.MovePosition(position); body.MoveRotation(rotation);
            }
        }
        private void SnapRemoteCarrier(Binding b, Vector3 position, Quaternion rotation)
        {
            b.PhysicsCorrectionVelocity = Vector3.zero;
            var body = b.Vehicle.vehicleRigidbody;
            var frame = b.Vehicle.transform;
            var fromPosition = body.position; var fromRotation = body.rotation;
            var walker = gameplay.PlayerWalking != null ? gameplay.PlayerWalking.transform : null!;
            // Measure support before teleporting, at the physics pose rather than an
            // interpolated render pose. Parented/kinematic cargo pins are excluded by
            // PrepareRiders and follow their parent exactly once.
            frame.SetPositionAndRotation(fromPosition, fromRotation);
            Carriers?.PrepareRiders(b.Id, walker);
            body.position = position; body.rotation = rotation;
            frame.SetPositionAndRotation(position, rotation);
            Carriers?.CarryRiders(b.Id, fromPosition, fromRotation, walker);
            // The later walker-only Update must not apply this snap a second time.
            b.RiderFramePosition = position; b.RiderFrameRotation = rotation; b.RiderFrameKnown = true;
        }
        public void Update()
        {
            if (disposed) return;
            RefreshPrompts();
            WatchHoveredLights();
            foreach (var pending in pendingPurchasedVehicles.ToArray())
            {
                if (pending.Value.Instance == null)
                {
                    pendingPurchasedVehicles.Remove(pending.Key);
                    continue;
                }
                RegisterPurchasedVehicle(pending.Key, pending.Value.Type, pending.Value.Instance);
            }
            foreach (var shell in bindings.Values)
            {
                if (IsNativeKonigShell(shell.Vehicle.vehicle_type)) RefreshNativeShellSeatAdjustmentControls(shell);
            }
            if (!Ready) return;
            UpdatePassengerReleases();
            FreezePendingGuestMotion();
            PromoteReadyBuggies();
            PromoteReadyMissiles();
            PromoteReadyNativeKonigShells();
            // The host too: it used to snap a guest-driven vehicle to each 20 Hz sample, which a host
            // riding along saw as stutter (#110) and which rattled cargo in the bed (#119).
            foreach (var b in bindings.Values)
            {
                if (b.Dormant || b.Vehicle == null || !b.Vehicle.gameObject.activeInHierarchy ||
                    !b.HasPresentationPose || IsLocal(b.State)) continue;
                var walker = gameplay.PlayerWalking != null ? gameplay.PlayerWalking.transform : null!;
                if (host && b.Vehicle.vehicleRigidbody.isKinematic)
                {
                    // FixedUpdate moves the body through the physics step; this only carries the walker.
                    var frame = b.Vehicle.transform;
                    if (b.RiderFrameKnown) Carriers?.CarryRiders(b.Id, b.RiderFramePosition, b.RiderFrameRotation, walker, walkerOnly: true);
                    b.RiderFramePosition = frame.position; b.RiderFrameRotation = frame.rotation; b.RiderFrameKnown = true;
                    continue;
                }
                b.RiderFrameKnown = false;
                Vector3 position; Quaternion rotation;
                if (!host) b.Presentation.Evaluate(Time.unscaledTime, out position, out rotation);
                else
                {
                    float fraction = Mathf.Clamp01((Time.unscaledTime - b.PoseReceivedAt) / RemotePoseInterpolationSeconds);
                    position = Vector3.Lerp(b.PoseFrom, b.PoseTo, fraction);
                    rotation = Quaternion.Slerp(b.RotationFrom, b.RotationTo, fraction);
                }
                var fromPosition = b.Vehicle.transform.position; var fromRotation = b.Vehicle.transform.rotation;
                if ((fromPosition - position).sqrMagnitude >= 1e-8f || Quaternion.Angle(fromRotation, rotation) >= 0.01f)
                    Carriers?.PrepareRiders(b.Id, walker);
                b.Vehicle.vehicleRigidbody.position = position;
                b.Vehicle.vehicleRigidbody.rotation = rotation;
                b.Vehicle.transform.SetPositionAndRotation(position, rotation);
                Carriers?.CarryRiders(b.Id, fromPosition, fromRotation, walker);
            }
            foreach (var b in bindings.Values)
                if (!b.Dormant && b.Vehicle != null && b.Vehicle.gameObject.activeInHierarchy && b.State != null && !IsLocal(b.State))
                { AlignWheelAngles(b); PresentEffects(b); }
            Doors.Update(); Windows.Update(); Hood.Update(); Trunk.Update();
            if (Time.unscaledTime < nextSend) return;
            nextSend = Time.unscaledTime + 0.05f;
            foreach (var b in bindings.Values)
            {
                if (PublishInactiveLifecycle(b)) continue;
                if (b.Dormant) continue;
                if (b.Vehicle == null) throw new InvalidOperationException("Shared vehicle removed; reload before rehosting.");
                if (host)
                {
                    bool frozen = FrozenOnCarLift(b.Vehicle);
                    if (b.State.LiftFrozen != frozen)
                    {
                        b.State = b.State.WithLiftFrozen(frozen);
                        ApplyLiftFreeze(b, IsLocal(b.State));
                        if (!IsLocal(b.State)) Publish(b);
                    }
                }
                // Native optional vehicles can be present in the loaded scene but inactive until
                // their progression gate is reached. Keep their stable binding and resume once the
                // host's progression activates the same chassis on each peer.
                if (!b.Vehicle.gameObject.activeInHierarchy) continue;
                if (host && b.State.Driver.Owner != ItemLease.Unowned && b.State.Driver.Owner != 0 && Time.unscaledTime - b.LastUpdate > 5)
                    Release(b, b.State.Driver.Owner, secure: true);
                if (IsLocal(b.State))
                {
                    b.State = Capture(b);
                    if (host) Publish(b);
                    else channel!.Request(b.Id, new VehicleCommand(b.ExitPending ? VehicleAction.Exit : VehicleAction.Move, b.State).Encode(), true);
                }
            }
        }
        internal bool TryGetCrashTarget(VehicleController vehicle, out ulong target)
        {
            target = 0;
            var b = Find(vehicle);
            if (disposed || !Ready || session?.IsRunning != true || b == null || b.Dormant ||
                b.State == null || !IsLocal(b.State) || !vehicle.enabled || !vehicle.gameObject.activeInHierarchy ||
                vehicle.VehicleMultiplayerInstanceType != Vehicle.MultiplayerInstanceType.Local) return false;
            target = b.Id;
            return true;
        }
        internal bool AcceptCrashCue(uint peer, PresentationEvent cue)
        {
            return !disposed && host && Ready && session?.IsRunning == true && peer != 0 &&
                bindings.TryGetValue(cue.Target, out var b) && !b.Dormant && b.State != null &&
                b.State.Driver.Owner == peer && b.Vehicle != null && b.Vehicle.gameObject.activeInHierarchy;
        }
        internal bool TryPlayCrash(PresentationEvent e)
        {
            if (disposed || !Ready || !bindings.TryGetValue(e.Target, out var b) || b.Dormant || b.Vehicle == null) return false;
            RemoteVehicleSounds.Crash(b.Vehicle, new Vector3(e.X, e.Y, e.Z), Mathf.Clamp(e.Value, 0f, 200f));
            return true;
        }
        private void ExitLocal()
        {
            var vehicle = gameplay.Get_CurrentVehicleController;
            if (vehicle == null) return;
            if (Find(vehicle)?.EnteredFrame == Time.frameCount)
            {
                // Mover.EnterVehicle uses deferred Destroy(rigidbody). Native Setup during
                // an exit in this same frame would reuse that soon-to-be-destroyed body.
                if (!exitDeferred) { exitDeferred = true; MonMPPlugin.Instance!.StartCoroutine(ExitNextFrame(vehicle)); }
                return;
            }
            var binding = Find(vehicle);
            applying = true;
            try
            {
                if (binding?.RequestedExitDriverSide is bool driverSide) NativeVehicleSeatExit.Exit(gameplay, vehicle, driverSide);
                else AccessTools.Method(typeof(Gameplay), "Force_ExitVehicle").Invoke(gameplay, null);
            }
            finally
            {
                if (binding != null) binding.RequestedExitDriverSide = null;
                applying = false;
            }
        }

#if MONMP_DEVELOPMENT
        internal string TestExitTrace => "exitDeferred=" + exitDeferred + "; exitRan=" + testDeferredExitRuns + "; entered=" + bindings[TestId].EnteredFrame +
            "; frame=" + Time.frameCount + "; owner=" + bindings[TestId].State.Driver.Owner + "; exitPending=" + bindings[TestId].ExitPending;


        private int testDeferredExitRuns;
#endif

        private IEnumerator ExitNextFrame(Vehicle expected)
        {
            yield return null;

#if MONMP_DEVELOPMENT
            testDeferredExitRuns++;
#endif

            exitDeferred = false;
            if (gameplay != null && gameplay.Get_CurrentVehicleController == expected) ExitLocal();
        }
        private void RestoreDeferredBuggyBaseline(Binding b)
        {
            var v = b.Vehicle; var body = v.vehicleRigidbody;
            try
            {
                if (v.VehicleMultiplayerInstanceType != b.OriginalMode) v.SetMultiplayerInstanceType(b.OriginalMode, true);
                if (b.OriginalCaptured) ApplyBody(b, b.Original);
                else
                {
                    var pose = b.Original.Pose;
                    var position = Position(pose); var rotation = new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw);
                    v.transform.SetPositionAndRotation(position, rotation);
                    if (body != null) { body.position = position; body.rotation = rotation; }
                }
            }
            finally { RestoreNativeConfiguration(b); }
        }
        private void RestoreNativeConfiguration(Binding b)
        {
            var v = b.Vehicle;
            if (v.VehicleMultiplayerInstanceType != b.OriginalMode) v.SetMultiplayerInstanceType(b.OriginalMode, true);
            var body = v.vehicleRigidbody;
            v.SetInputToPlayer(false); v.input.states.Reset();
            v.input.autoSetInput = b.AutoInput; v.freezeWhileIdle = b.FreezeIdle; v.freezeWhileAsleep = b.FreezeSleep;
            int wheelCount = Math.Min(v.Wheels.Count, b.ExternalWheels.Length);
            for (int i = 0; i < wheelCount; i++)
            {
                // A wheel can disappear during scene teardown while the chassis is
                // still live. Restore remaining wheels and independent body flags.
                var wheel = v.Wheels[i]?.wheelController;
                if (wheel == null) continue;
                if (b.ExternalWheels[i]) wheel.UseExternalUpdate(); else wheel.UseInternalUpdate();
            }
            if (body != null)
            {
                body.constraints = b.OriginalConstraints;
                body.collisionDetectionMode = b.OriginalCollision;
                body.interpolation = b.OriginalInterpolation;
                body.isKinematic = b.OriginalKinematic || FrozenOnCarLift(v);
            }
            v.enabled = b.OriginalVehicleEnabled;
        }
        public void Dispose()
        {
            if (disposed) return;
            SeatAdmission?.CancelLocal(this);
            foreach (var b in bindings.Values) b.RequestedExitDriverSide = null;
            bool exitLocal = gameplay != null && Handles(gameplay.Get_CurrentVehicleController);
            disposed = true;
            var failures = new List<Exception>();
            void Restore(Action action)
            {
                try { action(); }
                catch (Exception ex) { failures.Add(ex); }
            }
            try
            {
                if (exitLocal) Restore(ExitLocal);
                Restore(Doors.Dispose); Restore(Windows.Dispose); Restore(Hood.Dispose); Restore(Trunk.Dispose);
                if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
                if (channel != null)
                {
                    channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command;
                    channel.ReplyReceived -= PassengerReply;

#if MONMP_DEVELOPMENT
                    channel.CommandRejected -= CommandRejected;
#endif

                }
                foreach (var b in bindings.Values)
                {
                    if (b.Vehicle == null) continue;
                    if (b.NativeInitialized != null)
                        Restore(() => b.Vehicle.OnVehicleInitialized.RemoveListener(b.NativeInitialized));
                    if (b.Dormant && (b.Buggy || HasSharedLifecycle(b.Vehicle.vehicle_type)))
                    {
                        if (!host && HasSharedLifecycle(b.Vehicle.vehicle_type)) Restore(() => RestoreShellBaseline(b));
                        continue;
                    }
                    if (b.DeferredAtStart)
                    {
                        Restore(() => RestoreDeferredBuggyBaseline(b));
                        if (!host && HasSharedLifecycle(b.Vehicle.vehicle_type)) Restore(() => RestoreShellBaseline(b));
                        continue;
                    }
                    Restore(() => SetMode(b, true));
                    if (!host)
                    {
                        b.State = b.Original;
                        Restore(() => ApplyBody(b, b.Original));
                        Restore(() => RestoreSeatAdjustmentBaselines(b));
                    }
                    else Restore(() => ApplyBody(b, b.State));
                    // A failed native body/seat callback must not prevent independent
                    // configuration restoration or cleanup of the remaining vehicles.
                    Restore(() => RestoreNativeConfiguration(b));
                    Restore(() => { if (b.Hitch != null) b.Hitch.IsOn = b.HitchEnabled; });
                    if (!host && IsNativeKonigShell(b.Vehicle.vehicle_type)) Restore(() => RestoreShellBaseline(b));
                }
            }
            finally { carLifts.Clear(); NativeVehicleLighting.ResetExtraVisuals(); byVehicle.Clear(); byWheel.Clear(); channel = null; session = null; }
            if (failures.Count != 0) throw new AggregateException("Shared vehicle restoration failed.", failures);
        }
        private void RestoreSeatAdjustmentBaselines(Binding binding)
        {
            foreach (var adjustment in SeatAdjustments(binding))
            {
                if (!adjustment.OriginalCaptured || adjustment.Seat == null) continue;
                ApplySeatAdjustment(adjustment, adjustment.OriginalState);
                adjustment.Seat.LoadFromSaveSystem(adjustment.OriginalLocalPosition.z, adjustment.OriginalLocalPosition.y);
                adjustment.Pending = null;
                if (!adjustment.Driver) continue;
                var custom = binding.Vehicle.vehicle_PartsManager?.Get_CustomParameterToSave;
                if (custom == null || custom.Length < 2)
                    throw new InvalidOperationException("Konig driver seat save parameters disappeared during baseline restoration.");
                custom[0] = adjustment.OriginalSeatParameterForward;
                custom[1] = adjustment.OriginalSeatParameterHeight;
            }
        }
        private void RestoreShellBaseline(Binding b)
        {
            b.Vehicle.gameObject.SetActive(b.OriginalRootActive);
            b.Vehicle.enabled = b.OriginalShellEnabled;
            b.Vehicle.transform.SetPositionAndRotation(b.OriginalShellPosition, b.OriginalShellRotation);
            var body = b.Vehicle.vehicleRigidbody;
            if (body != null)
            {
                body.position = b.OriginalShellPosition; body.rotation = b.OriginalShellRotation;
                body.isKinematic = b.OriginalKinematic || FrozenOnCarLift(b.Vehicle);
                body.collisionDetectionMode = b.OriginalCollision;
                body.interpolation = b.OriginalInterpolation;
            }
        }
        private static Vector3 Position(ItemPose p) => new Vector3(p.X, p.Y, p.Z);
        private static Vector3 Vector(VehicleVector p) => new Vector3(p.X, p.Y, p.Z);
        private static VehicleVector Vector(Vector3 p) => new VehicleVector(p.x, p.y, p.z);

#if MONMP_DEVELOPMENT
        internal Vehicle_Type TestType { get; set; } = Vehicle_Type.SmollATV;


        internal bool TestScrapyardKonigSupported => bindings.Values.Any(b => b.Vehicle.vehicle_type == Vehicle_Type.ScrapyardKonig);

        internal bool TestPrinzBound => bindings.Values.Any(b => b.Vehicle.vehicle_type == Vehicle_Type.Prinz);


        internal bool TestBuggyReserved => bindings.Values.Any(b => b.Buggy && !b.Racecar);


        internal bool TestRacecarLive => bindings.Values.Any(b => b.Racecar && !b.Dormant);


        internal bool TestRacecarBound => bindings.Values.Any(b => b.Racecar);


        internal bool TestLeMissileBound => bindings.Values.Any(b => b.Vehicle.vehicle_type == Vehicle_Type.LeMissile);


        internal bool TestBuggyDormant => bindings.Values.Any(b => b.Buggy && !b.Racecar && b.Dormant);


        internal ulong TestId => bindings.Values.Single(b => b.Vehicle.vehicle_type == TestType).Id;


        internal VehicleController CurrentVehicle => bindings[TestId].Vehicle;
        internal VehicleController TestVehicle => CurrentVehicle;


        internal VehicleState State => bindings[TestId].State;
        internal VehicleState TestState => State;


        internal string TestDriverEntryDiagnostic => !testDriverEntryTraceEnabled
            ? "trace-disabled"
            : (host
                ? "hostEnterCommands=" + testHostVehicleCommandCount + ",worldRevisionRejects=" + testHostWorldRejectedCount
                : "guestMouseDowns=" + testGuestMouseDownCount + ",enterExitReached=" + testEnterExitPrefixCount) +
                "; " + string.Join(" || ", testDriverEntryTrace.ToArray());


        internal void TestEnableDriverEntryDiagnostics()
        {
            if (testDriverEntryTraceEnabled) return;
            testDriverEntryTraceEnabled = true;
            foreach (Vehicle_Type type in new[] { Vehicle_Type.SmollATV, Vehicle_Type.OlTruck, Vehicle_Type.ScrapyardKonig, Vehicle_Type.Prinz, Vehicle_Type.MainVehicle })
            {
                var binding = bindings.Values.FirstOrDefault(value => value.Vehicle.vehicle_type == type);
                if (binding != null) testDriverEntryTraceIds.Add(binding.Id);
            }

#if MONMP_DEVELOPMENT
            RecordDriverEntryTrace("fixture", "driver entry trace enabled; role=" + (host ? "host" : "guest") +
                ", trackedIds=" + string.Join(",", testDriverEntryTraceIds.Select(id => id.ToString("X16")).ToArray()));
#endif

        }


        internal void TestResetWheelSpinVisualDiagnostics()
        {
            var b = bindings[TestId];
            Array.Clear(b.LastRemoteWheelSpinVisual, 0, b.LastRemoteWheelSpinVisual.Length);
            b.RemoteWheelSpinVisualAppliedMask = 0;
            b.RemoteWheelSpinVisualHighMask = 0;
        }


        internal bool TestRemoteWheelSpinVisualApplied
        {
            get
            {
                var b = bindings[TestId];
                return b.WheelVisuals.Length == 4 && b.RemoteWheelSpinVisualAppliedMask == 0x0f &&
                    b.RemoteWheelSpinVisualHighMask != 0;
            }
        }


        internal string TestWheelSpinVisualDiagnostic(VehicleWheelSpin packetState)
        {
            var b = bindings[TestId];
            static string Steps(IEnumerable<float> values) => string.Join(",", values.Select(v => Mathf.RoundToInt(v / VehicleWheelSpin.AngularVelocityStep).ToString()).ToArray());
            return "wireSteps=" + Steps(new[] { packetState.Wheel0, packetState.Wheel1, packetState.Wheel2, packetState.Wheel3 }) +
                "; lastAppliedSteps=" + Steps(b.LastRemoteWheelSpinVisual) +
                "; postfixMask=0x" + b.RemoteWheelSpinVisualAppliedMask.ToString("X1") +
                "; highSpinMask=0x" + b.RemoteWheelSpinVisualHighMask.ToString("X1");
        }


        internal Vector3 TestSeat => bindings[TestId].DriverComponent.transform.position;


        internal bool TestEntryPending => SeatAdmission?.IsPendingLocal(this, TestId) == true;


        internal void TestEnter()
        {
            var b = bindings[TestId];
            if (!b.TryGetDriverSeat(out var seatComponent, out var driverPosition) ||
                seatComponent == null || driverPosition == null)
                throw new InvalidOperationException("The selected shared vehicle has no supported native driver seat.");

#if MONMP_DEVELOPMENT
            bool trace = testDriverEntryTraceEnabled && testDriverEntryTraceIds.Contains(b.Id);


            if (trace)
            {
                testGuestMouseDownCount++;
                string canUse;
                try { canUse = seatComponent is Vehicle_DriverSeat standard ? standard.CanUse().ToString() :
                    seatComponent is Konig_Seat konig ? konig.CanSeat().ToString() : "missing"; }
                catch (Exception exception) { canUse = "throws-" + exception.GetType().Name; }
                RecordDriverEntryTrace("guest-before-mousedown", "seatCanUse=" + canUse + ", localHolding=" + gameplay.Player.IsHoldingAnObject +
                    ", " + DescribeDriverEntryGate(b, session?.LocalPeerId ?? 0, driverPosition, b.State.Driver.Owner, b.State.Driver.Generation));
            }
#endif

            if (seatComponent is Vehicle_DriverSeat standardSeat) standardSeat.MouseDown();
            else if (seatComponent is Konig_Seat konigSeat) konigSeat.SitDown();
            else throw new InvalidOperationException("The selected shared vehicle has no supported native driver seat.");

#if MONMP_DEVELOPMENT
            if (trace) RecordDriverEntryTrace("guest-after-mousedown", "currentVehicle=" +
                (gameplay.Get_CurrentVehicleController == b.Vehicle) + ", playerState=" + gameplay.Get_PlayerState +
                ", owner=" + b.State.Driver.Owner + ", worldChannelReady=" + (channel?.Ready == true) +
                ", entityPresent=" + (channel?.Records.ContainsKey(b.Id) == true));
#endif

        }


        internal void TestExit() => gameplay.EnterExitVehicle(CurrentVehicle, bindings[TestId].DriverPosition, true);


        internal void TestStale(uint peer, VehicleState state) => Command(peer, TestId, new VehicleCommand(VehicleAction.Move, state).Encode());


        private Binding TestKonigBinding => bindings.Values.Single(b => b.Vehicle.vehicle_type == Vehicle_Type.MainVehicle);


        internal VehicleController TestKonigVehicle => TestKonigBinding.Vehicle;


        internal Vehicle_Button_LightDomeOptions? TestKonigDomeModeControl => DomeModeControls(TestKonigBinding).FirstOrDefault();


        internal VehicleState TestKonigState => TestKonigBinding.State;


        internal VehiclePassengerSeatState TestKonigPassengerState => TestKonigBinding.PassengerState;


        private SeatAdjustmentBinding TestKonigDriverAdjustment => TestKonigBinding.DriverSeatAdjustment!;


        private SeatAdjustmentBinding TestKonigPassengerAdjustment => TestKonigBinding.PassengerSeatAdjustment!;


        internal VehicleSeatAdjustmentState TestKonigSeatAdjustment => TestKonigDriverAdjustment.State;


        internal VehicleSeatAdjustmentState TestKonigOriginalSeatAdjustment => TestKonigDriverAdjustment.OriginalState;


        internal VehicleSeatAdjustmentState TestKonigNativeSeatAdjustment => CaptureSeatAdjustment(TestKonigDriverAdjustment.Seat, TestKonigDriverAdjustment.State.Generation);


        internal VehicleSeatAdjustmentState TestKonigPassengerSeatAdjustment => TestKonigPassengerAdjustment.State;


        internal VehicleSeatAdjustmentState TestKonigNativePassengerSeatAdjustment => CaptureSeatAdjustment(TestKonigPassengerAdjustment.Seat, TestKonigPassengerAdjustment.State.Generation);


        internal VehicleSeatAdjustmentState TestKonigOriginalPassengerSeatAdjustment => TestKonigPassengerAdjustment.OriginalState;


        internal Vector3 TestKonigSeatForwardPosition => TestKonigDriverAdjustment.ForwardButton!.transform.position;


        internal Part_Seat_Height TestKonigSeatHeightButton => TestKonigDriverAdjustment.HeightButton!;


        internal Part_Seat_Forward TestKonigPassengerSeatForwardButton => TestKonigPassengerAdjustment.ForwardButton!;


        internal Part_Seat_Height TestKonigPassengerSeatHeightButton => TestKonigPassengerAdjustment.HeightButton!;


        internal bool TestKonigSeatAdjustmentControlsReady => TestKonigDriverAdjustment.ForwardButton != null && TestKonigDriverAdjustment.HeightButton != null;


        internal bool TestKonigPassengerSeatAdjustmentForwardReady => TestKonigPassengerAdjustment.ForwardButton != null;


        internal bool TestKonigPassengerSeatAdjustmentHeightReady => TestKonigPassengerAdjustment.HeightButton != null;


        internal int TestKonigSeatAdjustmentForwardDirection
        {
            get
            {
                var seat = TestKonigDriverAdjustment.Seat;
                var minimum = (float)AccessTools.Field(typeof(Konig_Seat), "MinSeatForward")!.GetValue(seat)!;
                var maximum = (float)AccessTools.Field(typeof(Konig_Seat), "MaxSeatForward")!.GetValue(seat)!;
                var current = seat.transform.localPosition.z;
                if (current + 0.009f <= maximum) return 1;
                if (current - 0.009f >= minimum) return -1;
                throw new InvalidOperationException("Konig forward slider has no legal one-step direction.");
            }
        }


        internal int TestKonigPassengerSeatAdjustmentForwardDirection => SeatAdjustmentDirection(
            TestKonigPassengerAdjustment.Seat, "MinSeatForward", "MaxSeatForward", forward: true);
#endif

        private static int SeatAdjustmentDirection(Konig_Seat seat, string minimumField, string maximumField, bool forward)
        {
            float current = forward ? seat.transform.localPosition.z : seat.transform.localPosition.y;
            float minimum = (float)AccessTools.Field(typeof(Konig_Seat), minimumField)!.GetValue(seat)!;
            float maximum = (float)AccessTools.Field(typeof(Konig_Seat), maximumField)!.GetValue(seat)!;
            if (current + 0.009f <= maximum) return 1;
            if (current - 0.009f >= minimum) return -1;
            throw new InvalidOperationException("Konig seat slider has no legal one-step direction.");
        }

#if MONMP_DEVELOPMENT
        private (Binding Binding, SeatAdjustmentBinding? Adjustment) TestShellAdjustment(VehicleController vehicle, bool driver)
        {
            var binding = Find(vehicle) ?? throw new InvalidOperationException("Shell seat vehicle is unbound: " + vehicle.vehicle_type);
            RefreshSeatAdjustmentControls(binding, true);
            return (binding, driver ? binding.DriverSeatAdjustment : binding.PassengerSeatAdjustment);
        }
        internal string TestShellSeatAdjustmentDiagnostic(VehicleController vehicle, bool driver, VehicleSeatAdjustmentAxis axis)
        {
            var (binding, adjustment) = TestShellAdjustment(vehicle, driver);
            return SeatAdjustmentDiagnostic(binding, adjustment, adjustment == null ? null : SeatAdjustmentControl(adjustment, axis), axis);
        }
        internal uint TestShellSeatAdjustmentGeneration(VehicleController vehicle, bool driver) =>
            TestShellAdjustment(vehicle, driver).Adjustment?.State.Generation ?? 0;
        // One legal native click toward the middle of the seat's travel; false when no control exists.
        internal bool TestClickShellSeatAdjustment(VehicleController vehicle, bool driver, VehicleSeatAdjustmentAxis axis)
        {
            var adjustment = TestShellAdjustment(vehicle, driver).Adjustment;
            if (adjustment == null || adjustment.Seat == null) return false;
            var control = SeatAdjustmentControl(adjustment, axis);
            if (control == null) return false;
            if (!TrySeatAdjustmentLimits(adjustment.Seat, out var minForward, out var maxForward, out var minHeight, out var maxHeight)) return false;
            bool up = axis == VehicleSeatAdjustmentAxis.Forward
                ? adjustment.State.ForwardCentimeters < (minForward + maxForward) / 2
                : adjustment.State.HeightCentimeters < (minHeight + maxHeight) / 2;
            if (control is Part_Seat_Forward forward) { if (up) forward.MouseDown(); else forward.MouseRightDown(); }
            else if (control is Part_Seat_Height height) { if (up) height.MouseDown(); else height.MouseRightDown(); }
            else return false;
            return true;
        }


        internal void TestClickKonigSeatAdjustmentForward(int direction)
        {
            if (direction == 1) TestKonigDriverAdjustment.ForwardButton!.MouseDown();
            else if (direction == -1) TestKonigDriverAdjustment.ForwardButton!.MouseRightDown();
            else throw new ArgumentOutOfRangeException(nameof(direction));
        }


        internal void TestClickKonigPassengerSeatAdjustment(VehicleSeatAdjustmentAxis axis, int direction)
        {
            var button = SeatAdjustmentControl(TestKonigPassengerAdjustment, axis);
            if (axis == VehicleSeatAdjustmentAxis.Forward && button is Part_Seat_Forward forward)
            { if (direction == 1) forward.MouseDown(); else if (direction == -1) forward.MouseRightDown(); else throw new ArgumentOutOfRangeException(nameof(direction)); }
            else if (axis == VehicleSeatAdjustmentAxis.Height && button is Part_Seat_Height height)
            { if (direction == 1) height.MouseDown(); else if (direction == -1) height.MouseRightDown(); else throw new ArgumentOutOfRangeException(nameof(direction)); }
            else throw new InvalidOperationException("The native passenger-seat adjustment control is unavailable.");
        }


        internal float TestKonigSeatCustomParameter(int index) => TestKonigVehicle.vehicle_PartsManager.Get_CustomParameterToSave[index];


        internal float TestKonigOriginalSeatCustomParameter(int index) => index == 0
            ? TestKonigDriverAdjustment.OriginalSeatParameterForward : index == 1
                ? TestKonigDriverAdjustment.OriginalSeatParameterHeight : throw new ArgumentOutOfRangeException(nameof(index));


        internal Vector3 TestKonigOriginalSeatLocalPosition => TestKonigDriverAdjustment.OriginalLocalPosition;


        internal Vector3 TestKonigOriginalPassengerSeatLocalPosition => TestKonigPassengerAdjustment.OriginalLocalPosition;


        internal Vector3 TestKonigDriverPosition => TestKonigBinding.KonigDriverSeat!.transform.position;


        internal Vector3 TestKonigPassengerPosition => TestKonigBinding.KonigPassengerSeat!.transform.position;


        internal Vector3 TestKonigPassengerSeatTargetPosition => TestKonigBinding.KonigPassengerSeat!.seatPosition.position;


        internal Vector3 TestKonigWalkerPosition => gameplay.PlayerWalking.transform.position;


        internal void TestKonigEnterDriver() => TestKonigBinding.KonigDriverSeat!.SitDown();


        internal void TestKonigExitDriver() => gameplay.EnterExitVehicle(TestKonigVehicle, TestKonigBinding.DriverPosition, true);


        internal void TestKonigEnterPassenger() => TestKonigBinding.KonigPassengerSeat!.SitDown();


        internal void TestKonigExitPassenger() => gameplay.EnterExitVehicle(TestKonigVehicle, TestKonigBinding.KonigPassengerSeat!.seatPosition, false);


        internal void TestKonigStalePassengerExit(uint peer, uint generation) =>
            Command(peer, TestKonigBinding.PassengerId, new VehiclePassengerSeatCommand(VehiclePassengerSeatAction.Exit, generation).Encode());


        internal string TestKonigPeerPose(uint peer) => presence.TryGetValue(peer, out var value)
            ? "state=" + value.Pose.State + ", age=" + (Time.unscaledTime - value.Time).ToString("F2") + "s"
            : "no pose";


        internal bool TestRestored => bindings.Values.All(b =>
            (b.DeferredAtStart || (b.Dormant && (b.Buggy || HasSharedLifecycle(b.Vehicle.vehicle_type)))) ? DeferredBaselineRestored(b) :
                b.Vehicle.VehicleMultiplayerInstanceType == Vehicle.MultiplayerInstanceType.Local &&
                NativeVehicleLighting.Capture(b.Vehicle).Lamps == b.Original.Lighting.Lamps && NativeVehicleLighting.Capture(b.Vehicle).Controls == b.Original.Lighting.Controls &&
                b.Vehicle.InteriorLightMode == b.Original.Lighting.InteriorMode &&
                !b.Vehicle.vehicleRigidbody.isKinematic && (b.Accessory == null || !b.Accessory.isKinematic) && b.Vehicle.input.autoSetInput == b.AutoInput &&
                (host || Vector3.Distance(b.Vehicle.transform.position, Position(b.Original.Pose)) < 0.1f)) && Doors.TestRestored;

        internal string TestRestoreDiagnostic => "doors=" + Doors.TestRestored + "; " + string.Join("; ", bindings.Values.Select(b =>
            b.Vehicle.vehicle_type + ": dormant=" + b.Dormant + ", deferred=" + b.DeferredAtStart +
            ", mode=" + b.Vehicle.VehicleMultiplayerInstanceType + "/" + b.OriginalMode +
            ", kinematic=" + b.Vehicle.vehicleRigidbody.isKinematic + "/" + b.OriginalKinematic +
            ", positionDelta=" + Vector3.Distance(b.Vehicle.transform.position, Position(b.Original.Pose)).ToString("F3") +
            ", nativeBaseline=" + DeferredBaselineRestored(b)));

        private bool DeferredBaselineRestored(Binding b)
        {
            var v = b.Vehicle; var body = v.vehicleRigidbody;
            var pose = b.Original.Pose;
            return v.VehicleMultiplayerInstanceType == b.OriginalMode && v.enabled == b.OriginalVehicleEnabled && v.input.autoSetInput == b.AutoInput &&
                v.freezeWhileIdle == b.FreezeIdle && v.freezeWhileAsleep == b.FreezeSleep &&
                body.isKinematic == (b.OriginalKinematic || (IsNativeKonigShell(v.vehicle_type) && FrozenOnCarLift(v))) && body.collisionDetectionMode == b.OriginalCollision && body.constraints == b.OriginalConstraints &&
                Vector3.Distance(v.transform.position, Position(pose)) < 0.1f &&
                Enumerable.Range(0, Math.Min(v.Wheels.Count, b.ExternalWheels.Length)).All(i => v.Wheels[i].wheelController.UsingExternalUpdate == b.ExternalWheels[i]);
        }

        internal bool TestAccessoryMode => bindings.Values.All(b => b.Accessory == null || b.Accessory.isKinematic == b.Vehicle.vehicleRigidbody.isKinematic);
#endif

    }
}
