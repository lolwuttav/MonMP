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
using NWH.VehiclePhysics2.Modules.Trailer;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // The host owns the towing charge, queued vehicle and recovery coroutine. The multiplayer
    // request follows the stock menu's type filter and requires one unambiguous replicated or
    // deterministic local-copy movement route for the selected native vehicle.
    internal sealed class SharedTowingService : IDisposable
    {
        private const float Price = 99.99f;
        private const float InteractionRange = 5f;
        private const float PoseMaxAge = 2f;
        private const float CaptureInterval = 0.2f;
        private const float BudgetWindow = 2f;
        private const int RequestsPerWindow = 6;
        private const int MaxReplayResults = 2048;

        private static readonly FieldInfo TruckStoreField = AccessTools.Field(typeof(Store_TowingService_Truck), "Store_TowingService");
        private static readonly MethodInfo TowMethod = AccessTools.Method(typeof(Store_TowingService), nameof(Store_TowingService.TowThisVehicle), new[] { typeof(Vehicle_Type) });
        private static readonly MethodInfo NewDayMethod = AccessTools.Method(typeof(Store_TowingService), "NewDay", Type.EmptyTypes);
        private static readonly MethodInfo LoadMethod = AccessTools.Method(typeof(Store_TowingService), nameof(Store_TowingService.LoadFromSave), new[] { typeof(Vehicle_Type) });

        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly Store_TowingService service;
        private readonly Store_TowingService_Truck[] trucks;
        private readonly HashSet<Store_TowingService_Truck> exactTrucks = new HashSet<Store_TowingService_Truck>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, (float Started, int Count)> budgets = new Dictionary<uint, (float, int)>();
        private readonly Dictionary<Guid, float> pending = new Dictionary<Guid, float>();
        private readonly Dictionary<Guid, Vehicle_Type> pendingTypes = new Dictionary<Guid, Vehicle_Type>();
        private readonly Dictionary<(uint Peer, Guid Operation), TowingServiceStatus> replayResults =
            new Dictionary<(uint, Guid), TowingServiceStatus>();
        private readonly Queue<(uint Peer, Guid Operation)> replayOrder = new Queue<(uint, Guid)>();
        private readonly TowingServiceState original;
        private TowingServiceState state;
        private NetworkSession? session;
        private WorldChannel? channel;
        private TowingServiceState? lastPublished;
        private float nextCapture;
        private float recoveryDeadline;
        private float nextRecoveryAttempt;
        private bool recoveryPending;
        private bool recoveryInFlight;
        private bool applying, snapshotReceived, disposed;

        internal static SharedTowingService? Active { get; private set; }
        internal bool NewActionsBlocked { get; set; }
        internal bool TransactionsSettled => pending.Count == 0 && !recoveryInFlight;

#if MONMP_DEVELOPMENT
        internal int TestAccepted { get; private set; }


        internal int TestRejected { get; private set; }


        internal TowingServiceState TestState => state;
        internal TowingServiceStatus? TestLastValidationStatus { get; private set; }
        internal TowingServiceStatus? TestLastDecisionStatus { get; private set; }
        internal TowingServiceStatus? TestLastReplyStatus { get; private set; }
        internal string TestLastValidation { get; private set; } = "none";
#endif


        internal SharedTowingService(Gameplay gameplay, bool host)
        {
            this.gameplay = gameplay ?? throw new ArgumentNullException(nameof(gameplay));
            this.host = host;
            if (TruckStoreField?.FieldType != typeof(Store_TowingService) || TowMethod == null || NewDayMethod == null || LoadMethod == null)
                throw new InvalidOperationException("Unsupported native towing-service API layout.");

            var services = Object.FindObjectsOfType<Store_TowingService>(true)
                .Where(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.scene.isLoaded).ToArray();
            if (services.Length != 1)
                throw new InvalidOperationException("Expected exactly one native towing service in the active world; found " + services.Length + ".");
            service = services[0];
            trucks = Object.FindObjectsOfType<Store_TowingService_Truck>(true)
                .Where(value => value != null && value.gameObject.scene == service.gameObject.scene &&
                    ReferenceEquals(TruckStoreField.GetValue(value), service)).ToArray();
            if (trucks.Length != 1)
                throw new InvalidOperationException("Expected exactly one towing-service truck bound to the native service; found " + trucks.Length + ".");
            foreach (var truck in trucks) exactTrucks.Add(truck);

            state = original = Capture();
            Active = this;
        }

        private TowingServiceState Capture() => new TowingServiceState(
            (int)service.Get_CurrentVehicleOnWait, service.isStoreOpen(), recoveryInFlight);

        internal IEnumerable<WorldRecord> InitialRecords() => new[]
        {
            new WorldRecord(TowingServiceState.Entity, 1, TowingServiceState.Adapter, state.Encode())
        };

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            session.PoseReceived += PoseReceived;
            session.PeerLeft += PeerLeft;
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed;
            channel.CommandRequested += Command;
            channel.CommandRejected += Rejected;
            channel.ReplyReceived += Reply;
        }

        private bool Running => !disposed && session?.IsRunning == true;
        private bool Ready => Running && channel?.FullySynchronized == true;
        private bool IsGuest => Running && !host;

        internal bool AllowTruckOpen(Store_TowingService_Truck candidate)
        {
            if (!Running || applying) return true;
            if (candidate == null || !exactTrucks.Contains(candidate) || !ReferenceEquals(TruckStoreField.GetValue(candidate), service))
            {
                Report("This towing-service truck is not registered in the shared world.");
                return false;
            }
            if (host) return true;
            if (!Ready || NewActionsBlocked || MonMP.Plugin.MonMPPlugin.Instance?.World?.SaveGate.Active == true ||
                !state.StoreOpen || state.HasVehicleOnWait)
            {
                Report("The host towing service is closed, busy, or still synchronizing.");
                return false;
            }
            return true;
        }

        internal bool AllowOpen(Store_TowingService candidate)
        {
            if (!Running || applying) return true;
            if (!ReferenceEquals(candidate, service)) return false;
            if (host) return true;
            if (!Ready || NewActionsBlocked || MonMP.Plugin.MonMPPlugin.Instance?.World?.SaveGate.Active == true ||
                !state.StoreOpen || state.HasVehicleOnWait)
            {
                Report("The host towing service is closed, busy, or still synchronizing.");
                return false;
            }
            return true;
        }

        // Returns true when the stock purchase may execute. Offline play is untouched;
        // online host purchases retain the native charge/UI while guests send a request.
        internal bool InterceptTow(Store_TowingService candidate, Vehicle_Type vehicleType)
        {
            if (!Running || applying) return true;
            if (!ReferenceEquals(candidate, service))
            {
                Report("This towing-service instance is not registered in the shared world.");
                return false;
            }
            if (!Ready || NewActionsBlocked || MonMP.Plugin.MonMPPlugin.Instance?.World?.SaveGate.Active == true)
            {
                Report("Wait for the shared world and save to finish before requesting a tow.");
                return false;
            }

            if (host)
            {
                var status = ValidateRequest(0, vehicleType);
                if (status != TowingServiceStatus.Accepted)
                {

#if MONMP_DEVELOPMENT
                    TestRejected++;
#endif

                    ReportStatus(status);
                    return false;
                }
                return true;
            }

            if (pending.Count >= 16)
            {
                Report("Wait for the pending towing-service request to finish.");
                return false;
            }
            TowingServiceRequest request;
            try { request = new TowingServiceRequest(Guid.NewGuid(), (int)vehicleType); }
            catch (InvalidDataException)
            {
                ReportStatus(TowingServiceStatus.InvalidVehicle);
                return false;
            }
            pending.Add(request.Operation, Time.unscaledTime);
            pendingTypes[request.Operation] = vehicleType;
            while (pendingTypes.Count > 32) pendingTypes.Remove(pendingTypes.Keys.First());
            try { channel!.Request(TowingServiceState.Entity, request.Encode()); }
            catch
            {
                pending.Remove(request.Operation);
                throw;
            }
            return false;
        }

        internal void NativeTowCompleted(Store_TowingService candidate)
        {
            if (!Running || !host || applying || !ReferenceEquals(candidate, service)) return;
            CaptureAndPublish();
        }

        internal bool AllowNativeNewDay(Store_TowingService candidate)
        {
            if (!Running || applying || !ReferenceEquals(candidate, service)) return true;
            if (!host) return false;
            if (candidate.Get_CurrentVehicleOnWait == Vehicle_Type.Useless)
            {
                recoveryPending = false;
                return true;
            }
            if (recoveryInFlight) return false;
            if (!Ready || NewActionsBlocked || MonMP.Plugin.MonMPPlugin.Instance?.World?.SaveGate.Active == true
#if MONMP_DEVELOPMENT
                || TestDeferNextNewDay
#endif
            )
            {

#if MONMP_DEVELOPMENT
                if (TestDeferNextNewDay) { TestDeferNextNewDay = false; TestDeferredNewDays++; }
#endif

                if (!recoveryPending) Debug.Log("MonMP deferred the queued tow of " + candidate.Get_CurrentVehicleOnWait + " until shared actions resume.");
                recoveryPending = true;
                return false;
            }
            var type = candidate.Get_CurrentVehicleOnWait;
            if (!TryGetReplicatedAvailableTarget(type, out _, out _))
            {
                if (!recoveryPending) Debug.Log("MonMP deferred the queued tow of " + type + ": the vehicle is in use, attached or not synchronized.");
                recoveryPending = true;
                Report("The queued tow is waiting because its vehicle is not safely synchronized or is currently in use.");
                return false;
            }
            recoveryPending = false;
            return true;
        }

        internal bool AllowNativeLoad(Store_TowingService candidate) => !Running || host || applying;

        internal bool AllowVehicleEntry(Gameplay candidate, Vehicle vehicle)
        {
            if (!Running || applying || candidate == null || vehicle == null || !ReferenceEquals(candidate, gameplay) ||
                candidate.Get_CurrentVehicleController == vehicle) return true;
            int queuedType = host ? (int)service.Get_CurrentVehicleOnWait : state.CurrentVehicleType;
            if (queuedType == TowingServiceState.UselessVehicleType || (int)vehicle.vehicle_type != queuedType) return true;
            Report("This vehicle is already queued for towing.");
            return false;
        }

        internal bool AllowTrailerAttach(VehicleController tow)
        {
            if (!Running || tow == null) return true;
            int queuedType = host ? (int)service.Get_CurrentVehicleOnWait : state.CurrentVehicleType;
            return queuedType == TowingServiceState.UselessVehicleType || (int)tow.vehicle_type != queuedType;
        }

        private TowingServiceStatus ValidateRequest(uint peer, Vehicle_Type vehicleType)
        {
            bool peerKnown = peer == 0 || session?.Peers.ContainsKey(peer) == true;
            bool poseKnown = TryPosition(peer, out Vector3 position, out bool freshWalking);
            bool inRange = poseKnown && Vector3.Distance(position, trucks[0].transform.position) <= InteractionRange;
            bool enumValid = Enum.IsDefined(typeof(Vehicle_Type), vehicleType) && vehicleType != Vehicle_Type.Useless;
            bool listed = enumValid && IsNativeListingUnlocked(vehicleType);
            bool targetTypeCanUseMovementRoute = enumValid && TowingTargetPolicy.IsNativeMenuType((int)vehicleType);
            bool targetAvailable = targetTypeCanUseMovementRoute && TryGetReplicatedAvailableTarget(vehicleType, out _, out _);
            bool seized = enumValid && gameplay.Store_PoliceInterceptor != null &&
                gameplay.Store_PoliceInterceptor.Get_IsThisCarSeizeByThePolice(vehicleType);
            var cash = Singleton<Cash_Manager>.i;
            bool canAfford = cash != null && cash.isCanAfford(Price);
#if MONMP_DEVELOPMENT
            return TestValidateHostRequest(peer, vehicleType, poseKnown, position,
#else
            return TowingServicePolicy.ValidateHostRequest(
#endif
                Ready && peerKnown,
                NewActionsBlocked || MonMP.Plugin.MonMPPlugin.Instance?.World?.SaveGate.Active == true,
                peerKnown && TakeBudget(peer),
                ReferenceEquals(service, Object.FindObjectsOfType<Store_TowingService>(true).SingleOrDefault()),
                service.isStoreOpen(),
                service.Get_CurrentVehicleOnWait != Vehicle_Type.Useless,
                enumValid,
                listed,
                targetTypeCanUseMovementRoute,
                targetAvailable,
                seized,
                freshWalking,
                inRange,
                canAfford);
        }

#if MONMP_DEVELOPMENT
        // Snapshot the exact arguments passed to the policy; never re-evaluate a gate or
        // consume another budget entry just to explain a development fixture rejection.
        private TowingServiceStatus TestValidateHostRequest(uint peer, Vehicle_Type type, bool poseKnown, Vector3 position,
            bool synchronized, bool saveBlocked, bool requestBudget, bool exactService, bool storeOpen,
            bool alreadyWaiting, bool validVehicleType, bool listingUnlocked, bool targetMovementReplicated,
            bool targetAvailable, bool policeSeized, bool freshWalkingPose, bool inRange, bool canAfford)
        {
            var result = TowingServicePolicy.ValidateHostRequest(synchronized, saveBlocked, requestBudget,
                exactService, storeOpen, alreadyWaiting, validVehicleType, listingUnlocked, targetMovementReplicated,
                targetAvailable, policeSeized, freshWalkingPose, inRange, canAfford);
            TestLastValidationStatus = result;
            bool sampled = presence.TryGetValue(peer, out var sample);
            float age = peer == 0 ? 0f : sampled ? Time.unscaledTime - sample.Time : float.PositiveInfinity;
            int playerState = peer == 0 ? (int)gameplay.Get_PlayerState : sampled ? sample.Pose.State : -1;
            TestLastValidation = "peer=" + peer + "; type=" + type + "; poseKnown=" + poseKnown +
                "; pose=" + position.ToString("F3") + "; poseAge=" + age.ToString("F3") + "; poseState=" + playerState +
                "; truck=" + trucks[0].transform.position.ToString("F3") + "; distance=" +
                Vector3.Distance(position, trucks[0].transform.position).ToString("F3") +
                "; synchronized=" + synchronized + "; saveBlocked=" + saveBlocked + "; requestBudget=" + requestBudget +
                "; exactService=" + exactService + "; storeOpen=" + storeOpen + "; alreadyWaiting=" + alreadyWaiting +
                "; validVehicleType=" + validVehicleType + "; listingUnlocked=" + listingUnlocked +
                "; targetMovementReplicated=" + targetMovementReplicated + "; targetAvailable=" + targetAvailable +
                "; policeSeized=" + policeSeized + "; freshWalking=" + freshWalkingPose +
                "; inRange=" + inRange + "; canAfford=" + canAfford;
            return result;
        }
#endif

        private bool IsNativeListingUnlocked(Vehicle_Type type)
        {
            if (!TowingTargetPolicy.IsNativeMenuType((int)type)) return false;
            var todo = Singleton<ToDoList_Manager>.i;
            if (todo == null) return false;
            var world = gameplay;
            var hard = world.Get_HardModeData;
            if (world.Get_isHardMode &&
                ((type == Vehicle_Type.OlTruck && hard?.Bought_OlTruck != true) ||
                 (type == Vehicle_Type.SmollATV && hard?.Bought_SmollATV != true) ||
                 (type == Vehicle_Type.WoodSplitter && hard?.Bought_WoodSplitter != true))) return false;
            return type switch
            {
                Vehicle_Type.SmallCruiser => todo.isDone(ToDoList_Line.Boat_SmallCruiserBought),
                Vehicle_Type.FishingBoat => todo.isDone(ToDoList_Line.Boat_Fishing_Bought),
                Vehicle_Type.Buggy => todo.isBuggyDone(),
                Vehicle_Type.Racecar => todo.isRacecarDone(),
                Vehicle_Type.LeMissile => todo.isDone(ToDoList_Line.Intermediate_ScrapyardGateIsOpen),
                Vehicle_Type.ScrapyardKonig => todo.isDone(ToDoList_Line.Intermediate_UnlockScrapKonig),
                Vehicle_Type.AluminiumTrailer => todo.isDone(ToDoList_Line.Intermediate_SmallAluminiumTrailerBought),
                Vehicle_Type.TrailerSyrupTank => todo.isDone(ToDoList_Line.Intermediate_TrailerSyrupTankBought),
                Vehicle_Type.DollyTrailer => todo.isDone(ToDoList_Line.Intermediate_DollyTrailerBought),
                Vehicle_Type.Prinz => todo.isDone(ToDoList_Line.Advanced_Prinz_Unlocked),
                _ => true
            };
        }

        // The stock menu omits PitBike and AI_Vehicle. For menu-listed types, the queue is accepted
        // only when a canonical native target exists and one movement route is available: a shared
        // vehicle, a loose shared trailer, or the guest's matching local native copy.
        private static Vehicle? NativeTarget(Vehicle_Type type)
        {
            var global = Singleton<GlobalClassManager>.i;
            var target = global == null ? null : global.GetVehicles(type);
            if (target == null || global == null) return null;
            var registered = global.GetAllVehicles();
            int typeCount = registered.Count(value => value != null && value.vehicle_type == type);
            int exactCount = registered.Count(value => ReferenceEquals(value, target));
            return TowingTargetPolicy.IsCanonicalNativeTarget((int)type, (int)target.vehicle_type,
                target.gameObject.scene.IsValid() && target.gameObject.scene.isLoaded,
                target.vehicleRigidbody != null, typeCount, exactCount) ? target : null;
        }

        private static bool IsMirroredLocally(Vehicle target)
        {
            var world = MonMP.Plugin.MonMPPlugin.Instance?.World;
            var controller = target as VehicleController ?? target.GetComponent<VehicleController>();
            return world == null || controller == null || (!world.Vehicles.Handles(target) && !world.Trailers.OwnsBodyMotion(controller));
        }

        private bool TryGetReplicatedAvailableTarget(Vehicle_Type type, out Vehicle target, out VehicleController controller)
        {
            target = null!;
            controller = null!;
            if (!TowingTargetPolicy.IsNativeMenuType((int)type)) return false;
            target = NativeTarget(type)!;
            if (target == null) return false;
            controller = target as VehicleController ?? target.GetComponent<VehicleController>();
            if (controller == null) return false;
            var world = MonMP.Plugin.MonMPPlugin.Instance?.World;
            var shared = world?.Vehicles;
            bool sharedVehicle = shared?.Handles(target) == true;
            bool sharedTrailer = world?.Trailers.OwnsBodyMotion(controller) == true;
            bool localNativeMirror = IsMirroredLocally(target);
            if (!TowingTargetPolicy.HasSingleMovementRoute(sharedVehicle, sharedTrailer, localNativeMirror)) return false;
            if (sharedVehicle)
            {
                if (!target.gameObject.activeInHierarchy || shared == null || !shared.TryGetDriverState(controller, out var vehicleState) ||
                    vehicleState.Driver.Owner != ItemLease.Unowned || shared.HasPassengerOccupant(controller) ||
                    !shared.CanControl(target)) return false;
            }
            else if (sharedTrailer)
            {
                if (!target.gameObject.activeInHierarchy || world == null || !world.Trailers.IsLooseHostTrailer(controller)) return false;
            }
            else if (gameplay.Get_CurrentVehicleController == controller) return false;
            var resolvedController = controller;
            var hitch = resolvedController.GetComponent<TrailerHitchModuleWrapper>()?.module;
            var trailer = resolvedController.GetComponent<TrailerModuleWrapper>()?.module;
            if (hitch != null && (hitch.attached || hitch.Trailer != null)) return false;
            if (trailer != null && (trailer.attached || trailer.TrailerHitch != null)) return false;
            if (Object.FindObjectsOfType<Vehicle_Trailer_Dolly>(true)
                .Any(dolly => dolly != null && dolly.isACarAttached && dolly.GetCarAttached == resolvedController)) return false;
            return true;
        }

        private bool TryPosition(uint peer, out Vector3 position, out bool freshWalking)
        {
            if (peer == 0)
            {
                position = gameplay.PlayerWalking.transform.position;
                freshWalking = gameplay.Get_PlayerState == PlayerState.Walking;
                return true;
            }
            if (!presence.TryGetValue(peer, out var sample))
            {
                position = default;
                freshWalking = false;
                return false;
            }
            float age = Time.unscaledTime - sample.Time;
            position = new Vector3(sample.Pose.X, sample.Pose.Y, sample.Pose.Z);
            freshWalking = age >= 0 && age <= PoseMaxAge && sample.Pose.State == (byte)PlayerState.Walking;
            return age >= 0 && age <= PoseMaxAge;
        }

        private bool TakeBudget(uint peer)
        {
            float now = Time.unscaledTime;
            if (!budgets.TryGetValue(peer, out var budget) || now < budget.Started || now - budget.Started >= BudgetWindow)
                budget = (now, 0);
            if (budget.Count >= RequestsPerWindow) { budgets[peer] = budget; return false; }
            budgets[peer] = (budget.Started, budget.Count + 1);
            return true;
        }

        private void PoseReceived(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);

        private void PeerLeft(uint peer)
        {
            presence.Remove(peer);
            budgets.Remove(peer);
        }

        [MonMP.Core.World.WorldCommandAdapters(TowingServiceState.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || session?.IsHost != true || entity != TowingServiceState.Entity || peer == 0 ||
                session.Peers.ContainsKey(peer) != true || !TowingServiceRequest.Is(payload)) return;
            TowingServiceRequest request;
            try { request = TowingServiceRequest.Decode(payload); }
            catch (InvalidDataException) { return; }
            var key = (peer, request.Operation);
            if (replayResults.TryGetValue(key, out var previous)) { SendReply(peer, request.Operation, previous); return; }

            var type = (Vehicle_Type)request.VehicleType;
            var status = ValidateRequest(peer, type);
            if (status == TowingServiceStatus.Accepted)
            {
                try
                {
                    applying = true;
                    // The requester gets this confirmation through its reply, not on the host's screen.
                    using (Patches.RemoteActionScope.Enter()) service.TowThisVehicle(type);
                    CaptureAndPublish();
                    if (service.Get_CurrentVehicleOnWait != type) status = TowingServiceStatus.NativeRejected;
                    else
#if MONMP_DEVELOPMENT
TestAccepted++;
#else
{}
#endif

                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    status = TowingServiceStatus.NativeRejected;
                    try { CaptureAndPublish(); } catch (Exception captureError) { Debug.LogException(captureError); }
                }
                finally { applying = false; }
            }
            if (status != TowingServiceStatus.Accepted)
#if MONMP_DEVELOPMENT
TestRejected++;
#else
{}
#endif

            Remember(key, status);
            SendReply(peer, request.Operation, status);
        }

        private void Remember((uint Peer, Guid Operation) key, TowingServiceStatus status)
        {
            replayResults[key] = status;
            replayOrder.Enqueue(key);
            while (replayOrder.Count > MaxReplayResults) replayResults.Remove(replayOrder.Dequeue());
        }

        private void SendReply(uint peer, Guid operation, TowingServiceStatus status)
        {
#if MONMP_DEVELOPMENT
            TestLastDecisionStatus = status;
#endif
            if (channel?.Ready == true && session?.Peers.ContainsKey(peer) == true)
                channel.Reply(peer, TowingServiceState.Entity, new TowingServiceReply(operation, status).Encode());
        }

        private void Rejected(uint peer, ulong entity, byte[] payload)
        {
            if (!host || entity != TowingServiceState.Entity || !TowingServiceRequest.Is(payload)) return;
            try { SendReply(peer, TowingServiceRequest.Decode(payload).Operation, TowingServiceStatus.NotReady); }
            catch (InvalidDataException) { }
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (host || entity != TowingServiceState.Entity || !TowingServiceReply.Is(payload)) return;
            TowingServiceReply reply;
            try { reply = TowingServiceReply.Decode(payload); }
            catch (InvalidDataException) { return; }
#if MONMP_DEVELOPMENT
            TestLastReplyStatus = reply.Status;
#endif
            if (!pending.Remove(reply.Operation)) { pendingTypes.Remove(reply.Operation); return; }
            if (reply.Status != TowingServiceStatus.Accepted) { pendingTypes.Remove(reply.Operation); ReportStatus(reply.Status); return; }
            // Native TowThisVehicle ran on the host, so only the host saw its confirmation; the tow itself
            // happens at the next new day. Show the requester the same message (#123).
            bool boat = pendingTypes.TryGetValue(reply.Operation, out var type) && pendingTypes.Remove(reply.Operation) &&
                (type == Vehicle_Type.SmallCruiser || type == Vehicle_Type.FishingBoat);
            Singleton<UI_EventMessage>.i?.Show(TEXT.GetText(boat ? "SUCCESS_BoatTowingServiceBought" : "SUCCESS_TowingServiceBought"),
                TEXT.GetText("TowingService_Name"), 8f);
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            if (!records.TryGetValue(TowingServiceState.Entity, out var record))
            {
                if (snapshotReceived) return;
                throw new InvalidDataException("Initial towing-service state record is missing.");
            }
            if (record.Adapter != TowingServiceState.Adapter)
                throw new InvalidDataException("Initial towing-service state has the wrong adapter.");
            snapshotReceived = true;
            Apply(TowingServiceState.Decode(record.Payload));
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Id != TowingServiceState.Entity || record.Adapter != TowingServiceState.Adapter) return;
            Apply(TowingServiceState.Decode(record.Payload));
        }

        private void Apply(TowingServiceState next)
        {
            if (snapshotReceived && state.CurrentVehicleType != TowingServiceState.UselessVehicleType &&
                next.CurrentVehicleType == TowingServiceState.UselessVehicleType)
                MirrorCompletedTow((Vehicle_Type)state.CurrentVehicleType);
            state = next;
            recoveryInFlight = next.RecoveryInFlight;
            applying = true;
            try { service.LoadFromSave((Vehicle_Type)next.CurrentVehicleType); }
            finally { applying = false; }
        }

        private static readonly FieldInfo VehicleSpawnField = AccessTools.Field(typeof(Store_TowingService), "VehicleSpawnLocation");
        private static readonly FieldInfo BoatSpawnField = AccessTools.Field(typeof(Store_TowingService), "BoatSpawnLocation");

        // The host's queue cleared after its recovery ran. An unsynchronized vehicle only moved on
        // the host, so place this guest's copy at the same native spawn point.
        private void MirrorCompletedTow(Vehicle_Type type)
        {
            try
            {
                var target = NativeTarget(type);
                if (target == null || !IsMirroredLocally(target)) return;
                var controller = target as VehicleController ?? target.GetComponent<VehicleController>();
                if (controller != null && gameplay.Get_CurrentVehicleController == controller) return;
                bool boat = type == Vehicle_Type.SmallCruiser || type == Vehicle_Type.FishingBoat;
                var spawn = (boat ? BoatSpawnField : VehicleSpawnField)?.GetValue(service) as Transform;
                if (spawn == null) return;
                target.gameObject.SetActive(true);
                if (!boat && controller != null)
                {
                    var hitch = target.GetComponent<TrailerHitchModuleWrapper>()?.module;
                    if (hitch != null && hitch.attached) hitch.DetachTrailer(controller);
                    var trailer = target.GetComponent<TrailerModuleWrapper>()?.module;
                    if (trailer != null && trailer.attached && trailer.isCloseEnoughtToAVehicle != null)
                        trailer.isCloseEnoughtToAVehicle.DetachTrailer(trailer.isCloseEnoughtToAVehicle.VehicleController);
                }
                target.Sleep();
                target.Get_IgnitionPosition = 0;
                target.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
                target.vehicleRigidbody.isKinematic = false;
                target.vehicleRigidbody.collisionDetectionMode = CollisionDetectionMode.Continuous;
                target.DestroyAllSkidmarks();

#if MONMP_DEVELOPMENT
                TestMirrored++;
#endif

            }
            catch (Exception ex) { Debug.LogWarning("MonMP could not mirror the host's tow of " + type + ": " + ex.Message); }
        }

#if MONMP_DEVELOPMENT
        internal int TestMirrored { get; private set; }


        internal Vector3 TestTruckPosition => trucks[0].transform.position;


        internal Vector3 TestVehicleSpawn => ((Transform)VehicleSpawnField.GetValue(service)).position;


        internal void TestRequest(Vehicle_Type type) => service.TowThisVehicle(type);


        internal void TestHostOpen()
        {
            service.isOpen = true;
            var cash = Singleton<Cash_Manager>.i;
            if (cash != null && !cash.isCanAfford(Price * 2)) cash.Event_Received(500f);
        }


        internal void TestRunRecovery() => NewDayMethod.Invoke(service, null);
#endif

        // Simulates the store opening while a shared sleep or save blocks new actions.

#if MONMP_DEVELOPMENT
        internal bool TestDeferNextNewDay;


        internal int TestDeferredNewDays;
#endif


        private void CaptureAndPublish()
        {
            var next = Capture();
            state = next;
            if (!Ready || channel == null || lastPublished.HasValue && lastPublished.Value.Equals(next)) return;
            channel.Publish(TowingServiceState.Entity, next.Encode());
            lastPublished = next;
        }

        internal void Update()
        {
            if (!Running) return;
            if (!host)
            {
                if (pending.Count > 0) foreach (var operation in pending.Where(pair => Time.unscaledTime - pair.Value > 8f).Select(pair => pair.Key).ToArray())
                {
                    pending.Remove(operation);
                    Report("The towing-service request timed out; check the shared service state before retrying.");
                }
                if (snapshotReceived && state.CurrentVehicleType != (byte)service.Get_CurrentVehicleOnWait)
                    Apply(state);
                return;
            }
            if (recoveryInFlight && ((int)service.Get_CurrentVehicleOnWait == TowingServiceState.UselessVehicleType ||
                Time.unscaledTime >= recoveryDeadline))
            {
                recoveryInFlight = false;
                CaptureAndPublish();
            }
            if ((int)service.Get_CurrentVehicleOnWait == TowingServiceState.UselessVehicleType)
                recoveryPending = false;
            if (!Ready || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + CaptureInterval;
            try { CaptureAndPublish(); }
            catch (InvalidDataException ex) { Debug.LogWarning("MonMP could not publish towing-service state: " + ex.Message); }
            if (recoveryPending && !recoveryInFlight &&
                (int)service.Get_CurrentVehicleOnWait != TowingServiceState.UselessVehicleType &&
                Time.unscaledTime >= nextRecoveryAttempt && !NewActionsBlocked &&
                MonMP.Plugin.MonMPPlugin.Instance?.World?.SaveGate.Active != true)
            {
                nextRecoveryAttempt = Time.unscaledTime + 1f;
                var queuedType = service.Get_CurrentVehicleOnWait;
                bool seized = gameplay.Store_PoliceInterceptor != null &&
                    gameplay.Store_PoliceInterceptor.Get_IsThisCarSeizeByThePolice(queuedType);
                if (seized)
                {
                    recoveryPending = false;
                }
                else if (TryGetReplicatedAvailableTarget(queuedType, out _, out _))
                {
                    try
                    {
                        NewDayMethod!.Invoke(service, null);
                    }
                    catch (Exception ex)
                    {
                        recoveryPending = true;
                        Debug.LogException(ex);
                    }
                }
            }
        }

        private void ReportStatus(TowingServiceStatus status)
        {
            string message = status switch
            {
                TowingServiceStatus.Closed => "The towing service is closed.",
                TowingServiceStatus.Busy => "Another vehicle is already waiting for towing.",
                TowingServiceStatus.Locked => "That vehicle is still locked by host progression.",
                TowingServiceStatus.UnsupportedVehicle => "That vehicle type is not yet synchronized for towing.",
                TowingServiceStatus.VehicleUnavailable => "That vehicle is not currently available for safe recovery.",
                TowingServiceStatus.PoliceSeized => "The police have seized that vehicle.",
                TowingServiceStatus.TooFar => "Move closer to the towing-service truck.",
                TowingServiceStatus.Funds => "The host does not have enough money for towing.",
                TowingServiceStatus.SaveBlocked => "Wait for the shared save to finish before requesting a tow.",
                TowingServiceStatus.NotWalking => "Request towing while walking near the truck.",
                _ => "The towing-service request was rejected (" + status + ")."
            };
            Report(message);
        }

        private static void Report(string message) => MonMP.Plugin.MonMPPlugin.Instance?.ReportItemUseUnavailable(message);

        internal void NativeNewDayCompleted(Store_TowingService candidate)
        {
            if (!Running || !host || applying || !ReferenceEquals(candidate, service)) return;
            recoveryPending = false;
            bool seized = service.Get_CurrentVehicleOnWait != Vehicle_Type.Useless &&
                gameplay.Store_PoliceInterceptor != null &&
                gameplay.Store_PoliceInterceptor.Get_IsThisCarSeizeByThePolice(service.Get_CurrentVehicleOnWait);
            if ((int)service.Get_CurrentVehicleOnWait != TowingServiceState.UselessVehicleType && !seized)
            {
                recoveryInFlight = true;
                // The native rescue coroutine normally takes about three seconds. The
                // deadline only releases a stalled save barrier; the queue remains
                // durable and will be retried by the game's normal NewDay path.
                recoveryDeadline = Time.unscaledTime + 30f;
            }
            CaptureAndPublish();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= PoseReceived; session.PeerLeft -= PeerLeft; }
            if (channel != null)
            {
                channel.SnapshotApplied -= Snapshot;
                channel.Changed -= Changed;
                channel.CommandRequested -= Command;
                channel.CommandRejected -= Rejected;
                channel.ReplyReceived -= Reply;
            }
            if (!host && snapshotReceived)
            {
                applying = true;
                try { service.LoadFromSave((Vehicle_Type)original.CurrentVehicleType); }
                catch (Exception ex) { Debug.LogWarning("MonMP could not restore the local towing-service queue: " + ex.Message); }
                finally { applying = false; }
            }
            if (ReferenceEquals(Active, this)) Active = null;
            presence.Clear(); budgets.Clear(); pending.Clear(); pendingTypes.Clear(); replayResults.Clear(); replayOrder.Clear();
            session = null; channel = null;
        }

        internal static IEnumerable<MethodBase> RequiredTargets()
        {
            yield return AccessTools.Method(typeof(Store_TowingService_Truck), nameof(Store_TowingService_Truck.MouseDown))!;
            yield return AccessTools.Method(typeof(Store_TowingService), nameof(Store_TowingService.OpenTowingService))!;
            yield return TowMethod!;
            yield return NewDayMethod!;
            yield return LoadMethod!;
            yield return AccessTools.Method(typeof(TrailerHitchModule), "AttachTrailer", new[] { typeof(VehicleController) })!;
        }
    }
}
