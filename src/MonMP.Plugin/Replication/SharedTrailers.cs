using System;
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
using NWH.VehiclePhysics2.Modules.Trailer;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // The host owns trailer topology. A connected body follows the driver's authority
    // lease and publishes one bounded rigidbody pose stream through TrailerWorldState.
    internal sealed class SharedTrailers : IDisposable
    {
        private sealed class Binding
        {
            public ulong Id;
            public VehicleController Vehicle = null!;
            public TrailerModule? Trailer;
            public TrailerHitchModule? Hitch;
            public bool SharedVehicle;
            public TrailerWorldState State = null!;
            public TrailerWorldState Original = null!;
            public Vehicle.MultiplayerInstanceType OriginalMode;
            public RigidbodyConstraints OriginalConstraints;
            public bool OriginalKinematic, OriginalAutoInput, OriginalFreezeIdle, OriginalFreezeSleep;
            public CollisionDetectionMode OriginalCollision;
            public RigidbodyInterpolation OriginalInterpolation;
            public bool[] OriginalExternalWheels = Array.Empty<bool>();
            public bool ModeSet;
            public float LastMotion;
            public bool HasMotionTarget, RiderFrameKnown;
            public Vector3 PoseFrom, PoseTo, RiderFramePosition;
            public Quaternion RotationFrom, RotationTo, RiderFrameRotation;
            public TrailerLinkState MotionLink;
            public float MotionReceivedAt, NextMotionRefresh;
            public TrailerWorldState? SentMotion;
            public readonly RemoteVehiclePose Presentation = new RemoteVehiclePose();
            public Vector3 PhysicsCorrectionVelocity;
            public ulong RepairParent;
            public uint RepairGeneration;
            public float NextLinkRepair, NextLinkWarning;
        }

        private const float SendInterval = 0.05f;
        private const float InteractionRange = 5f;
        // How far the native coupler's pull reaches (NWH's 0.7 m attach threshold, plus the pull's
        // own give), the extra allowed for a guest's view of it, and for applying a host-made link.
        private const float PullRange = 1.2f, LagSlack = 0.6f, SnapshotRange = 5f;
        private readonly Gameplay gameplay;
        private readonly SharedVehicles vehicles;
        private readonly NativeEntityRegistry identities;
        private readonly bool host;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<VehicleController, Binding> byVehicle = new Dictionary<VehicleController, Binding>();
        private readonly TrailerAuthorityGraph graph = new TrailerAuthorityGraph();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applyingNative, disposed;
        private float nextSend;

#if MONMP_DEVELOPMENT
        internal int TestInteractionCommandCount { get; private set; }


        internal uint TestLastInteractionPeer { get; private set; }


        internal TrailerAuthorityAction TestLastInteractionAction { get; private set; }


        internal bool TestLastInteractionAccepted { get; private set; }


        internal bool TestInteractionPullPending(ulong childId, uint peer) =>
            pulls.Any(pull => pull.Child.Id == childId && pull.Peer == peer);


        internal int TestLocalMotionPublishCount { get; private set; }


        internal int TestRemoteMotionAcceptedCount { get; private set; }
#endif


        internal bool ApplyingNative => applyingNative;
        internal bool Ready => channel?.Ready == true;
        internal int Count => bindings.Count;

        private bool CrashAvailable(Binding binding) => !disposed && session?.IsRunning == true && Ready &&
            !binding.SharedVehicle && binding.Trailer != null && binding.State != null && binding.Vehicle != null &&
            binding.Vehicle.enabled && binding.Vehicle.gameObject.activeInHierarchy;

        internal bool TryGetCrashTarget(VehicleController vehicle, out ulong target)
        {
            target = 0;
            if (vehicle == null || !byVehicle.TryGetValue(vehicle, out var binding) || !CrashAvailable(binding) ||
                !binding.State.CanPublishMotion(host, session!.LocalPeerId) ||
                vehicle.VehicleMultiplayerInstanceType != Vehicle.MultiplayerInstanceType.Local ||
                vehicle.soundManager?.crashComponent?.Initialized != true || !CrashRootOwnerMatches(binding)) return false;
            target = binding.Id;
            return true;
        }

        internal bool TryPlayCrash(PresentationEvent cue)
        {
            if (disposed || session?.IsRunning != true || !Ready ||
                !bindings.TryGetValue(cue.Target, out var binding) || binding.SharedVehicle || binding.Trailer == null) return false;
            if (CrashAvailable(binding))
                RemoteVehicleSounds.Crash(binding.Vehicle, new Vector3(cue.X, cue.Y, cue.Z), Mathf.Clamp(cue.Value, 0f, 200f));
            return true;
        }

        internal bool AcceptCrashCue(uint peer, PresentationEvent cue)
        {
            if (!host || peer == 0 || session?.Peers.ContainsKey(peer) != true ||
                !bindings.TryGetValue(cue.Target, out var binding) || !CrashAvailable(binding)) return false;
            var link = binding.State.Link;
            if (link.Owner != peer || link.ParentId == 0 || !graph.TryGet(binding.Id, out var current) ||
                current.Owner != peer || current.ParentId != link.ParentId || current.Generation != link.Generation) return false;
            return CrashRootOwnerMatches(binding);
        }

        private bool CrashRootOwnerMatches(Binding binding)
        {
            // Detached trailers are host simulated. An attached island must still agree with
            // its driver's current lease; a queued topology update cannot keep the old owner valid.
            if (binding.State.Link.ParentId == 0) return host && binding.State.Link.Owner == ItemLease.Unowned;
            ulong root = RootId(binding.Id);
            return root != binding.Id && bindings.TryGetValue(root, out var tow) && tow.SharedVehicle &&
                tow.Vehicle != null && tow.Vehicle.enabled && tow.Vehicle.gameObject.activeInHierarchy &&
                tow.State != null && tow.State.Link.Owner == binding.State.Link.Owner &&
                vehicles.TryGetDriverState(tow.Vehicle, out var driver) && driver.Driver.Owner == binding.State.Link.Owner;
        }

        internal SharedTrailers(Gameplay gameplay, bool host, SharedVehicles vehicles,
            NativeEntityRegistry identities, string diagnostics)
        {
            this.gameplay = gameplay;
            this.host = host;
            this.vehicles = vehicles;
            this.identities = identities;

            foreach (var vehicle in vehicles.BoundControllers)
            {
                var hitch = vehicle.GetComponent<TrailerHitchModuleWrapper>()?.module;
                if (hitch != null && hitch.attachmentPoint != null)
                {
                    var sales = SharedVehicleSales.Active;
                    Vehicle_Type saleType = default;
                    ulong saleEntity = 0;
                    bool saleVehicle = sales != null && sales.TryGetVehicleEntity(vehicle.gameObject, out saleType, out saleEntity) &&
                        (saleType == Vehicle_Type.OlTruck || saleType == Vehicle_Type.SmollATV);
                    Add(vehicle, hitch, null, sharedVehicle: true,
                        idOverride: saleVehicle ? PurchasedTowId(saleEntity) : 0,
                        identityOverride: saleVehicle ? PurchasedIdentity(saleEntity, saleType, "tow") : null,
                        sceneEntity: !saleVehicle);
                }
            }

            foreach (var wrapper in UnityEngine.Object.FindObjectsOfType<TrailerModuleWrapper>(true))
            {
                var module = wrapper.module;
                var vehicle = module?.VehicleController ?? wrapper.GetComponentInParent<VehicleController>();
                if (module == null || vehicle == null ||
                    module.attachmentPoint == null || vehicle.vehicleRigidbody == null) continue;
                var sales = SharedVehicleSales.Active;
                bool saleClone = sales?.IsVehicleSaleClone(vehicle.gameObject) == true;
                Vehicle_Type saleType = default;
                ulong saleEntity = 0;
                bool hasSaleEntity = sales != null && sales.TryGetVehicleEntity(vehicle.gameObject, out saleType, out saleEntity);
                if (saleClone && !hasSaleEntity) continue;
                if (hasSaleEntity && saleType != vehicle.vehicle_type)
                    throw new InvalidOperationException("Purchased trailer sale identity type does not match its VehicleController.");
                if (!CanDiscoverTrailer(vehicle, hasSaleEntity)) continue;
                var hitch = vehicle.GetComponent<TrailerHitchModuleWrapper>()?.module;
                Add(vehicle, hitch, module, vehicles.IsBound(vehicle),
                    idOverride: hasSaleEntity ? saleEntity : 0,
                    identityOverride: hasSaleEntity ? PurchasedIdentity(saleEntity, saleType, "trailer") : null,
                    sceneEntity: !hasSaleEntity);
            }

            foreach (var binding in bindings.Values)
            {
                if (binding.Trailer?.TrailerHitch == null) continue;
                var parent = binding.Trailer.TrailerHitch.VehicleController;
                if (parent == null || !byVehicle.TryGetValue(parent, out var tow))
                    throw new InvalidOperationException("Attached trailer uses an unsupported tow vehicle.");
                if (!graph.TryGet(tow.Id, out var towLink) || !graph.TryGet(binding.Id, out var trailerLink) ||
                    !graph.TryAttach(tow.Id, binding.Id, towLink.Generation, trailerLink.Generation, ItemLease.Unowned))
                    throw new InvalidOperationException("Attached trailer topology is invalid or ambiguous.");
            }

            foreach (var binding in bindings.Values)
            {
                graph.TryGet(binding.Id, out var link);
                binding.State = Capture(binding, link);
                binding.Original = binding.State;
            }

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-trailers.tsv"),
                new[] { "id\tvehicle-path\trole\tparent\towner" }.Concat(bindings.Values.OrderBy(b => b.Id)
                    .Select(b => b.Id.ToString("X16") + "\t" + RuntimeProbe.CanonicalPath(b.Vehicle.transform) +
                        "\t" + (b.Trailer != null ? "trailer" : "tow") + "\t" + b.State.Link.ParentId.ToString("X16") +
                        "\t" + b.State.Link.Owner)));
#endif

        }

        private Binding Add(VehicleController vehicle, TrailerHitchModule? hitch, TrailerModule? trailer, bool sharedVehicle,
            ulong idOverride = 0, string? identityOverride = null, bool sceneEntity = true)
        {
            if (byVehicle.TryGetValue(vehicle, out var existing))
            {
                if ((idOverride != 0 && existing.Id != idOverride) ||
                    (identityOverride != null && identities.Resolve(existing.Id)?.SpawnKey != identityOverride))
                    throw new InvalidOperationException("Vehicle already has a conflicting trailer authority identity.");
                existing.Hitch ??= hitch;
                existing.Trailer ??= trailer;
                existing.SharedVehicle |= sharedVehicle;
                return existing;
            }

            if (vehicle.vehicleRigidbody == null) throw new InvalidOperationException("Trailer authority vehicle has no rigidbody.");
            string identity = identityOverride ?? RuntimeProbe.CanonicalPath(vehicle.transform) + "/trailer-authority-v1";
            ulong id = idOverride != 0 ? idOverride : StaticId.Hash(identity);
            if (id == 0 || bindings.ContainsKey(id)) throw new InvalidOperationException("Duplicate trailer authority identity: " + identity);
            bool canTow = hitch != null && hitch.attachmentPoint != null;
            graph.Register(id, canTow, driverAssignable: sharedVehicle);
            identities.Bind(id, TrailerWorldState.Adapter, vehicle.gameObject, identity, sceneEntity, EntityPersistence.SessionOnly);
            var body = vehicle.vehicleRigidbody;
            var binding = new Binding
            {
                Id = id,
                Vehicle = vehicle,
                Trailer = trailer,
                Hitch = hitch,
                SharedVehicle = sharedVehicle,
                OriginalMode = vehicle.VehicleMultiplayerInstanceType,
                OriginalConstraints = body.constraints,
                OriginalKinematic = body.isKinematic,
                OriginalCollision = body.collisionDetectionMode,
                OriginalInterpolation = body.interpolation,
                OriginalExternalWheels = vehicle.Wheels.Select(w => w.wheelController.UsingExternalUpdate).ToArray(),
                OriginalAutoInput = vehicle.input.autoSetInput,
                OriginalFreezeIdle = vehicle.freezeWhileIdle,
                OriginalFreezeSleep = vehicle.freezeWhileAsleep
            };
            bindings.Add(id, binding);
            byVehicle.Add(vehicle, binding);
            return binding;
        }

        private static bool IsTrailerFamily(Vehicle_Type type) => type == Vehicle_Type.DollyTrailer ||
            type == Vehicle_Type.WoodSplitter || type == Vehicle_Type.TrailerSyrupTank || type == Vehicle_Type.AluminiumTrailer;

        private static bool CanDiscoverTrailer(VehicleController vehicle, bool knownPurchase)
        {
            if (vehicle == null || !vehicle.gameObject.scene.IsValid() || !vehicle.gameObject.scene.isLoaded) return false;
            if (vehicle.gameObject.activeInHierarchy || knownPurchase) return true;
            // Native storage can hide the canonical starter splitter before hosting. Its
            // exact registered root stays in the catalog on both peers until retrieval.
            // Other inactive display/prefab objects have no canonical or purchase identity.
            var global = Singleton<GlobalClassManager>.i;
            return IsTrailerFamily(vehicle.vehicle_type) && global != null &&
                ReferenceEquals(global.GetVehicles(vehicle.vehicle_type), vehicle);
        }

        private static string PurchasedIdentity(ulong entity, Vehicle_Type type, string role) =>
            "vehicle-sale-instance-v1/" + entity.ToString("X16") + "/" + (int)type + "/" + role;

        private static ulong PurchasedTowId(ulong vehicleEntity) =>
            StaticId.Hash("vehicle-sale-tow-authority-v1/" + vehicleEntity.ToString("X16")) | WorldRecord.DynamicIdStart;

        internal static bool SupportsPurchasedVehicle(Vehicle_Type type, GameObject instance, bool requireScene = true)
        {
            if (instance == null || (requireScene && !instance.scene.IsValid())) return false;
            if (type == Vehicle_Type.OlTruck || type == Vehicle_Type.SmollATV)
            {
                if (!SharedVehicles.SupportsPurchasedVehicle(type, instance, requireScene)) return false;
                return HasSupportedPurchasedHitch(type, instance);
            }
            if (!IsTrailerFamily(type)) return false;
            var vehicleControllers = instance.GetComponentsInChildren<VehicleController>(true);
            var trailerWrappers = instance.GetComponentsInChildren<TrailerModuleWrapper>(true);
            if (vehicleControllers.Length != 1 || vehicleControllers[0] == null ||
                vehicleControllers[0].vehicle_type != type || vehicleControllers[0].vehicleRigidbody == null ||
                trailerWrappers.Length != 1 || trailerWrappers[0] == null || trailerWrappers[0].module == null ||
                trailerWrappers[0].module.attachmentPoint == null) return false;
            var moduleVehicle = trailerWrappers[0].module.VehicleController;
            if (moduleVehicle != null && moduleVehicle != vehicleControllers[0]) return false;
            var hitches = instance.GetComponents<TrailerHitchModuleWrapper>();
            return hitches.Length <= 1 && (hitches.Length == 0 ||
                (hitches[0] != null && hitches[0].module != null && hitches[0].module.attachmentPoint != null));
        }

        internal static bool HasSupportedPurchasedHitch(Vehicle_Type type, GameObject instance)
        {
            if ((type != Vehicle_Type.OlTruck && type != Vehicle_Type.SmollATV) || instance == null) return false;
            var controllers = instance.GetComponentsInChildren<VehicleController>(true);
            var wrappers = instance.GetComponents<TrailerHitchModuleWrapper>();
            if (controllers.Length != 1 || controllers[0] == null || controllers[0].vehicle_type != type ||
                wrappers.Length != 1 || wrappers[0] == null || wrappers[0].module == null ||
                wrappers[0].module.attachmentPoint == null) return false;
            var hitch = wrappers[0].module;
            return hitch.attachmentPoint.IsChildOf(instance.transform) &&
                (hitch.VehicleController == null || hitch.VehicleController == controllers[0]);
        }

        // For cars this registers the separate tow-graph node; for trailer families the sale
        // entity itself is the TrailerWorldState ID. Call after SharedVehicles for car families.
        internal bool RegisterPurchasedVehicle(ulong entity, Vehicle_Type type, GameObject instance)
        {
            if (type == Vehicle_Type.OlTruck || type == Vehicle_Type.SmollATV)
            {
                // SharedWorld registers movement first. Hydration intentionally makes a guest
                // truck's chassis and Torus kinematic, so the strict fresh native-clone predicate
                // no longer describes it. Accept only that exact previously validated binding.
                if (!vehicles.HasPurchasedVehicleBinding(entity, type, instance) || !HasSupportedPurchasedHitch(type, instance))
                    throw new InvalidOperationException("Purchased tow vehicle has an unsupported trailer-hitch layout.");
                var controller = instance.GetComponentsInChildren<VehicleController>(true).Single();
                if (!vehicles.IsBound(controller))
                    throw new InvalidOperationException("Register a purchased tow vehicle with SharedVehicles first.");
                var hitchWrapper = instance.GetComponents<TrailerHitchModuleWrapper>().SingleOrDefault();
                if (hitchWrapper?.module == null)
                    throw new InvalidOperationException("Purchased tow vehicle has an unsupported trailer-hitch layout.");
                ulong towId = PurchasedTowId(entity);
                bool alreadyBound = byVehicle.ContainsKey(controller);
                var binding = Add(controller, hitchWrapper.module, null, sharedVehicle: true, towId,
                    PurchasedIdentity(entity, type, "tow"), sceneEntity: false);
                if (!alreadyBound) InitializePurchasedBinding(binding);
                return true;
            }

            if (!IsTrailerFamily(type)) return false;
            if (entity <= WorldRecord.DynamicIdStart || !SupportsPurchasedVehicle(type, instance))
                throw new InvalidOperationException("Purchased trailer has an unsupported NWH trailer layout.");
            var vehicle = instance.GetComponentsInChildren<VehicleController>(true).Single();
            var trailer = instance.GetComponentsInChildren<TrailerModuleWrapper>(true).Single().module;
            var hitch = instance.GetComponent<TrailerHitchModuleWrapper>()?.module;
            bool trailerAlreadyBound = byVehicle.ContainsKey(vehicle);
            var trailerBinding = Add(vehicle, hitch, trailer, vehicles.IsBound(vehicle), entity,
                PurchasedIdentity(entity, type, "trailer"), sceneEntity: false);
            if (!trailerAlreadyBound) InitializePurchasedBinding(trailerBinding);
            return true;
        }

        private void InitializePurchasedBinding(Binding binding)
        {
            if (binding.State != null) return;
            if (graph.TryGet(binding.Id, out var link))
            {
                if (binding.Trailer?.TrailerHitch != null)
                {
                    var towVehicle = binding.Trailer.TrailerHitch.VehicleController;
                    if (towVehicle == null || !byVehicle.TryGetValue(towVehicle, out var tow) ||
                        !graph.TryGet(tow.Id, out var towLink) ||
                        !graph.TryAttach(tow.Id, binding.Id, towLink.Generation, link.Generation, ItemLease.Unowned))
                        throw new InvalidOperationException("Purchased trailer is already attached to an unsupported tow vehicle.");
                }
                graph.TryGet(binding.Id, out link);
                binding.State = Capture(binding, link);
                binding.Original = binding.State;
                if (binding.Trailer != null && binding.State.Link.ParentId != 0) ApplyNativeLink(binding, binding.State.Link);
                if (channel != null)
                {
                    if (host)
                    {
                        PublishRecord(binding.Id, binding.State.Encode());
                        if (binding.State.Link.ParentId != 0 && bindings.TryGetValue(binding.State.Link.ParentId, out var tow))
                        {
                            tow.State = Capture(tow, graph.TryGet(tow.Id, out var towLink) ? towLink : default);
                            PublishRecord(tow.Id, tow.State.Encode());
                        }
                    }
                    else HydratePurchasedBinding(binding);
                }
            }
        }

        // Purchased trailers and tows are bound after the channel exists, so their first host
        // write registers the dynamic record instead of publishing an unknown entity.
        private void PublishRecord(ulong id, byte[] payload)
        {
            if (channel!.Records.ContainsKey(id)) channel.Publish(id, payload);
            else channel.Register(new WorldRecord(id, 1, TrailerWorldState.Adapter, payload));
        }

        private void HydratePurchasedBinding(Binding binding)
        {
            if (channel == null || host || !channel.Ready) return;
            if (channel.Records.TryGetValue(binding.Id, out var record))
            {
                if (record.Adapter != TrailerWorldState.Adapter) throw new InvalidDataException("Purchased trailer record adapter changed.");
                var state = TrailerWorldState.Decode(record.Payload);
                binding.State = state;
                ApplyState(binding, state, snap: true);
                if (binding.Trailer != null) ApplyNativeLink(binding, state.Link);
            }
            else channel.RequestResync(SnapshotScope.ForEntity(TrailerWorldState.Adapter, binding.Id),
                "Purchased vehicle or trailer bound after its initial snapshot.");
        }

        private static TrailerWorldState Capture(Binding binding, TrailerLinkState link)
        {
            var vehicle = binding.Vehicle;
            var body = vehicle.vehicleRigidbody;
            // Native storage disables the root. Its detached physics cache can return an
            // origin pose while the Transform retains the saved garage location.
            bool active = vehicle.gameObject.activeInHierarchy;
            var position = active ? body.position : vehicle.transform.position;
            var rotation = active ? body.rotation : vehicle.transform.rotation;
            var velocity = active ? body.velocity : Vector3.zero;
            var angular = active ? body.angularVelocity : Vector3.zero;
            // Unity's trusted local quaternion can drift a few float ulps outside unit
            // component bounds. Normalize here; received wire poses remain strictly validated.
            if (!ItemPose.TryLocal(position.x, position.y, position.z,
                rotation.x, rotation.y, rotation.z, rotation.w, out var pose))
                throw new InvalidDataException("Invalid local trailer position.");
            return new TrailerWorldState(link,
                pose,
                new VehicleVector(velocity.x, velocity.y, velocity.z),
                new VehicleVector(angular.x, angular.y, angular.z));
        }

        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.OrderBy(b => b.Id)
            .Select(b => new WorldRecord(b.Id, 1, TrailerWorldState.Adapter, b.State.Encode()));

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session;
            this.channel = channel;
            session.PoseReceived += OnPose;
            session.PeerLeft += OnPeerLeft;
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed;
            channel.CommandRequested += Command;
            if (!host)
                foreach (var binding in bindings.Values) ApplyMode(binding, binding.State.Link.Owner == session.LocalPeerId);
        }

        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            var staged = new List<(Binding Binding, TrailerWorldState State)>();
            foreach (var record in records.Values.Where(r => r.Adapter == TrailerWorldState.Adapter))
            {
                if (!bindings.TryGetValue(record.Id, out var binding))
                {
                    if (record.Id > WorldRecord.DynamicIdStart) continue;
                    throw new InvalidDataException("Unknown static trailer authority record.");
                }
                staged.Add((binding, TrailerWorldState.Decode(record.Payload)));
            }
            // SnapshotApplied also fires for repairs scoped to a different adapter or one
            // trailer. SnapshotReceiver has already validated full-catalog coverage when
            // the scope is World, so apply only the trailer records present here.
            foreach (var value in staged) value.Binding.State = value.State;
            foreach (var value in staged) ApplyState(value.Binding, value.State, snap: true);
            foreach (var value in staged.Where(v => v.Binding.Trailer != null)) ApplyNativeLink(value.Binding, value.State.Link);
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != TrailerWorldState.Adapter) return;
            if (!bindings.TryGetValue(record.Id, out var binding))
            {
                if (record.Id > WorldRecord.DynamicIdStart) return;
                throw new InvalidDataException("Unknown static trailer authority record.");
            }
            var state = TrailerWorldState.Decode(record.Payload);
            binding.State = state;
            ApplyState(binding, state);
            if (binding.Trailer != null) ApplyNativeLink(binding, state.Link);
        }

        private void ApplyState(Binding binding, TrailerWorldState state, bool snap = false)
        {
            ApplyMode(binding, state.Link.Owner == session!.LocalPeerId || (host && state.Link.Owner == ItemLease.Unowned));
            if (binding.SharedVehicle || state.CanPublishMotion(host, session.LocalPeerId))
            { binding.HasMotionTarget = false; binding.RiderFrameKnown = false; return; }
            SetMotionTarget(binding, state, snap);
        }

        private void SetMotionTarget(Binding binding, TrailerWorldState state, bool snap = false)
        {
            var body = binding.Vehicle.vehicleRigidbody;
            var position = new Vector3(state.Pose.X, state.Pose.Y, state.Pose.Z);
            var rotation = new Quaternion(state.Pose.Qx, state.Pose.Qy, state.Pose.Qz, state.Pose.Qw);
            var previous = binding.MotionLink;
            snap |= !binding.HasMotionTarget || previous.ParentId != state.Link.ParentId ||
                previous.Owner != state.Link.Owner || previous.Generation != state.Link.Generation ||
                (body.position - position).sqrMagnitude > 100f;
            if (snap)
            {
                binding.PhysicsCorrectionVelocity = Vector3.zero;
                var frame = binding.Vehicle.transform;
                var fromPosition = frame.position; var fromRotation = frame.rotation;
                var walker = gameplay.PlayerWalking != null ? gameplay.PlayerWalking.transform : null!;
                Carriers?.PrepareRiders(binding.Id, walker);
                ApplyMotion(binding, state);
                Carriers?.CarryRiders(binding.Id, fromPosition, fromRotation, walker);
            }
            binding.PoseFrom = body.position; binding.RotationFrom = body.rotation;
            binding.PoseTo = position; binding.RotationTo = rotation;
            binding.MotionLink = state.Link; binding.MotionReceivedAt = Time.unscaledTime;
            if (!host) binding.Presentation.Accept(position, rotation,
                new Vector3(state.Velocity.X, state.Velocity.Y, state.Velocity.Z),
                new Vector3(state.AngularVelocity.X, state.AngularVelocity.Y, state.AngularVelocity.Z),
                binding.MotionReceivedAt, snap);
            binding.HasMotionTarget = true;
            if (snap || !binding.RiderFrameKnown)
            {
                binding.RiderFramePosition = binding.Vehicle.transform.position;
                binding.RiderFrameRotation = binding.Vehicle.transform.rotation;
                binding.RiderFrameKnown = true;
            }
        }

        internal ItemCarriers? Carriers { get; set; }
        // Trailers that items and players ride on. A shared vehicle with a trailer module (towing-service
        // carriers) is already a SharedVehicles carrier.
        internal IEnumerable<(ulong Id, Transform Frame)> CarrierFrames()
        {
            foreach (var b in bindings.Values)
                if (!b.SharedVehicle && b.Trailer != null && b.Vehicle != null) yield return (b.Id, b.Vehicle.transform);
        }

        // Natively the tow vehicle's hitch copies its lamps to the trailer each frame, from the module
        // update that a Remote tow vehicle skips: a presented trailer kept stale brake and turn lamps.
        private void CopyTowLamps()
        {
            foreach (var b in bindings.Values)
            {
                if (b.SharedVehicle || b.Trailer == null || b.Vehicle == null || b.State == null || b.State.Link.ParentId == 0) continue;
                if (!bindings.TryGetValue(b.State.Link.ParentId, out var tow) || tow.Vehicle == null ||
                    tow.Vehicle.VehicleMultiplayerInstanceType != Vehicle.MultiplayerInstanceType.Remote) continue;
                var from = tow.Vehicle.effectsManager?.lightsManager;
                var to = b.Vehicle.effectsManager?.lightsManager;
                if (from == null || to == null) continue;
                int lamps = from.GetIntState();
                if (to.GetIntState() != lamps) to.SetStateFromInt(lamps);
            }
        }

        private void ApplyMode(Binding binding, bool local)
        {
            if (binding.SharedVehicle) return;
            var vehicle = binding.Vehicle;
            var mode = local ? Vehicle.MultiplayerInstanceType.Local : Vehicle.MultiplayerInstanceType.Remote;
            if (vehicle.VehicleMultiplayerInstanceType != mode)
            {
                binding.HasMotionTarget = false; binding.RiderFrameKnown = false;
                binding.Presentation.Clear();
                binding.PhysicsCorrectionVelocity = Vector3.zero;
                vehicle.SetMultiplayerInstanceType(mode, true);
                vehicle.input.autoSetInput = false;
                vehicle.freezeWhileIdle = false;
                vehicle.freezeWhileAsleep = false;
                vehicle.vehicleRigidbody.constraints = RigidbodyConstraints.None;
                if (local) RestoreWheelUpdates(binding);
                vehicle.Wake();
            }
            RemoteVehiclePose.ConfigureInterpolation(vehicle.vehicleRigidbody, local, host, binding.OriginalInterpolation);
            binding.ModeSet = true;
        }

        private static void RestoreWheelUpdates(Binding binding)
        {
            for (int i = 0; i < Math.Min(binding.Vehicle.Wheels.Count, binding.OriginalExternalWheels.Length); i++)
            {
                var wheel = binding.Vehicle.Wheels[i].wheelController;
                if (binding.OriginalExternalWheels[i]) wheel.UseExternalUpdate(); else wheel.UseInternalUpdate();
            }
        }

        private static void ApplyMotion(Binding binding, TrailerWorldState state)
        {
            var body = binding.Vehicle.vehicleRigidbody;
            Vector3 position = new Vector3(state.Pose.X, state.Pose.Y, state.Pose.Z);
            Quaternion rotation = new Quaternion(state.Pose.Qx, state.Pose.Qy, state.Pose.Qz, state.Pose.Qw);
            body.position = position;
            body.rotation = rotation;
            binding.Vehicle.transform.SetPositionAndRotation(position, rotation);
            if (!body.isKinematic)
            {
                body.velocity = new Vector3(state.Velocity.X, state.Velocity.Y, state.Velocity.Z);
                body.angularVelocity = new Vector3(state.AngularVelocity.X, state.AngularVelocity.Y, state.AngularVelocity.Z);
            }
        }

        private void ApplyNativeLink(Binding child, TrailerLinkState link)
        {
            if (child.Trailer == null) return;
            bool sameRepair = child.RepairParent == link.ParentId && child.RepairGeneration == link.Generation;
            child.RepairParent = link.ParentId; child.RepairGeneration = link.Generation;
            var old = child.Trailer.TrailerHitch;
            if (link.ParentId != 0 && bindings.TryGetValue(link.ParentId, out var expectedTow) &&
                expectedTow.Hitch != null && NativeLinkIntact(expectedTow, child))
            { child.NextLinkRepair = 0; return; }
            // Moving snapshots arrive at 20 Hz. A missing native prerequisite
            // should retry, not allocate a new exception/stack on every sample.
            if (!host && link.ParentId != 0 && sameRepair && Time.unscaledTime < child.NextLinkRepair) return;
            // Snapshot repairs can replace a stale parent or a broken joint while
            // NWH's cached TrailerHitch reference still says "attached".
            if (old != null && old.Trailer == child.Trailer)
            {
                applyingNative = true;
                try { old.DetachTrailer(old.VehicleController); }
                finally { applyingNative = false; }
            }
            if (link.ParentId == 0)
            {
                child.NextLinkRepair = 0;
                return;
            }
            if (!bindings.TryGetValue(link.ParentId, out var tow) || tow.Hitch == null) return;
            // The host already joined them; this copy's poses may trail its own by a little.
            if (AttachNative(tow, child, SnapshotRange)) { child.NextLinkRepair = 0; return; }
            if (host) throw new InvalidOperationException("Trailer snapshot cannot be represented by the native hitch layout.");
            child.NextLinkRepair = Time.unscaledTime + 1f;
            if (Time.unscaledTime >= child.NextLinkWarning)
            {
                child.NextLinkWarning = Time.unscaledTime + 30f;
                Debug.LogWarning("MonMP trailer native link pending: trailer=" + child.Id.ToString("X16") +
                    "; tow=" + link.ParentId.ToString("X16") + "; generation=" + link.Generation +
                    "; hitchInstalled=" + HitchInstalled(tow.Hitch) + "; hitchAttached=" + tow.Hitch.attached +
                    "; trailerAttached=" + child.Trailer.attached + "; retrySeconds=1");
            }
        }

        // The native coupler pulls a trailer within reach onto the hitch, then NWH attaches the first
        // trailer inside its 0.7 m threshold. A parked or remote-driven tow vehicle skips the module
        // FixedUpdate that marks trailers in range, and host and guest see the gap a little
        // differently: close the measured gap the same way, then attach (#256).
        private sealed class Pull
        {
            public uint Peer; public Binding Tow = null!; public Binding Child = null!;
            public TrailerAuthorityCommand Command; public float Until;
        }
        private const float PullSeconds = 3f, PullSpeed = 1.2f, PullDone = 0.06f;
        private readonly List<Pull> pulls = new List<Pull>();

        private static bool CanPull(Binding tow, Binding child, float range) =>
            tow.Hitch?.attachmentPoint != null && child.Trailer?.attachmentPoint != null && HitchInstalled(tow.Hitch) &&
            !tow.Hitch.attached && tow.Hitch.Trailer == null && !child.Trailer.attached && child.Trailer.TrailerHitch == null &&
            (tow.Hitch.attachmentPoint.position - child.Trailer.attachmentPoint.position).sqrMagnitude <= range * range;

        // Host: ease each accepted coupler pull toward its hitch, then attach through the graph.
        private void UpdatePulls()
        {
            for (int i = pulls.Count - 1; i >= 0; i--)
            {
                var pull = pulls[i];
                var tow = pull.Tow; var child = pull.Child;
                if (disposed || child.Vehicle == null || tow.Vehicle == null || !CanInteract(pull.Peer, child) ||
                    !CanPull(tow, child, PullRange + LagSlack + 0.5f) ||
                    Time.unscaledTime > pull.Until)
                {
                    pulls.RemoveAt(i);
                    if (pull.Peer == 0) Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText("TRAILER_TriedToAttachWithoutSuccess"));
                    continue;
                }
                var gap = tow.Hitch!.attachmentPoint.position - child.Trailer!.attachmentPoint.position;
                if (gap.magnitude > PullDone)
                {
                    var step = Vector3.ClampMagnitude(gap, PullSpeed * Time.deltaTime);
                    var body = child.Vehicle.vehicleRigidbody;
                    body.position += step; child.Vehicle.transform.position = body.position;
                    body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero;
                    if (gap.magnitude - step.magnitude > PullDone) continue;
                }
                pulls.RemoveAt(i);
                if (!graph.TryGet(child.Id, out _) || !graph.TryGet(tow.Id, out _)) continue;
                var before = graph.Snapshot();
                var command = pull.Command;
                if (!graph.TryAttachFromValidatedInteraction(tow.Id, child.Id, command.ExpectedTowGeneration,
                    command.ExpectedTrailerGeneration, pull.Peer)) continue;
                if (!AttachNative(tow, child, PullDone * 4))
                {
                    graph.RestoreSnapshot(before);
                    if (pull.Peer == 0) Singleton<UI_ErrorMessage>.i?.Show(TEXT.GetText("TRAILER_TriedToAttachWithoutSuccess"));
                    continue;
                }
                if (pull.Peer == 0) Singleton<SOUND_Manager>.i?.PlaySound_PartTempInstalled();
                PublishGraphChanges(before);

#if MONMP_DEVELOPMENT
                TestLastInteractionAccepted = true;
#endif

            }
        }

        private bool AttachNative(Binding tow, Binding child, float range)
        {
            if (tow.Hitch?.attachmentPoint == null || child.Trailer?.attachmentPoint == null || !HitchInstalled(tow.Hitch) ||
                tow.Hitch.attached || tow.Hitch.Trailer != null || child.Trailer.attached || child.Trailer.TrailerHitch != null)
                return false;
            var gap = tow.Hitch.attachmentPoint.position - child.Trailer.attachmentPoint.position;
            if (gap.sqrMagnitude > range * range) return false;
            var body = child.Vehicle.vehicleRigidbody;
            body.position += gap;
            child.Vehicle.transform.position = body.position;
            tow.Hitch.SyncTrailers();
            applyingNative = true;
            try { NativeTrailerCoupling.AttachExpected(tow.Hitch, child.Trailer, tow.Vehicle); }
            finally { applyingNative = false; }
            if (NativeLinkIntact(tow, child)) return true;
            if (tow.Hitch.Trailer != null)
            {
                applyingNative = true;
                try { tow.Hitch.DetachTrailer(tow.Vehicle); }
                finally { applyingNative = false; }
            }
            return false;
        }

        private static readonly System.Reflection.FieldInfo? HitchInstalledField = AccessTools.Field(typeof(TrailerHitchModule), "isHitchInstalled");
        private static readonly System.Reflection.FieldInfo? HitchJointField = AccessTools.Field(typeof(TrailerHitchModule), "_configurableJoint");
        private static bool NativeLinkIntact(Binding tow, Binding child)
        {
            var joint = tow.Hitch == null ? null : HitchJointField?.GetValue(tow.Hitch) as ConfigurableJoint;
            return joint != null && joint.connectedBody == tow.Vehicle.vehicleRigidbody &&
                joint.gameObject == child.Vehicle.gameObject && tow.Hitch!.attached &&
                tow.Hitch.Trailer == child.Trailer && child.Trailer?.attached == true && child.Trailer.TrailerHitch == tow.Hitch;
        }
        private static bool HitchInstalled(TrailerHitchModule hitch) => HitchInstalledField?.GetValue(hitch) as bool? != false;

        // The free, installed hitch nearest the trailer's coupler within reach.
        private Binding? NearestHitch(Binding child, float range)
        {
            if (child.Trailer?.attachmentPoint == null) return null;
            Binding? best = null;
            float bestGap = range * range;
            foreach (var b in bindings.Values)
            {
                if (b == child || b.State == null || b.Hitch?.attachmentPoint == null || b.Hitch.attached || b.Hitch.Trailer != null ||
                    !HitchInstalled(b.Hitch)) continue;
                float gap = (b.Hitch.attachmentPoint.position - child.Trailer.attachmentPoint.position).sqrMagnitude;
                if (gap <= bestGap) { bestGap = gap; best = b; }
            }
            return best;
        }

        private static bool CanNativeAttach(Binding tow, Binding child)
        {
            if (tow.Hitch?.attachmentPoint == null || child.Trailer?.attachmentPoint == null ||
                tow.Hitch.attached || tow.Hitch.Trailer != null || child.Trailer.attached || child.Trailer.TrailerHitch != null)
                return false;
            tow.Hitch.SyncTrailers();
            var findNearest = AccessTools.Method(typeof(TrailerHitchModule), "FindNearestTrailerModule");
            if (findNearest == null) return false;
            var nearest = findNearest.Invoke(tow.Hitch, null) as TrailerModule;
            return nearest == child.Trailer;
        }

        // The stock coupler only enters multiplayer if both its tow vehicle and trailer
        // have stable catalog identities. Attach is accepted only inside NWH's native
        // hitch radius, where its existing joint setup is deterministic on every peer.
        internal bool Interact(Trailer_Coupler coupler)
        {
            if (disposed || session?.IsRunning != true) return true;
            // Never fall through to NWH's local attach/detach while a multiplayer
            // world is still synchronizing. Both the host graph and guest command
            // route must be ready before a native coupler click can mutate topology.
            if (channel?.Ready != true) return false;
            var trailer = coupler.vc?.GetComponent<TrailerModuleWrapper>()?.module;
            if (trailer == null || trailer.VehicleController == null || !byVehicle.TryGetValue(trailer.VehicleController, out var child) ||
                child.State == null) return false;
            var childLink = child.State.Link;
            Binding? tow;
            TrailerAuthorityAction action;
            if (childLink.ParentId != 0)
            {
                if (!bindings.TryGetValue(childLink.ParentId, out tow)) return false;
                action = TrailerAuthorityAction.Detach;
            }
            else
            {
                // Not isCloseEnoughtToAVehicle: only a Local tow vehicle's module update sets it (#256).
                tow = NearestHitch(child, PullRange);
                if (tow == null)
                {
                    Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText("TRAILER_TriedToAttachWithoutSuccess"));
                    return false;
                }
                action = TrailerAuthorityAction.Attach;
                Singleton<SOUND_Manager>.i.PlaySound_Player_StartLiftingHeavy();
            }
            var towLink = tow.State.Link;
            uint local = session.LocalPeerId;
            var command = new TrailerAuthorityCommand(action, tow.Id, towLink.Generation, childLink.Generation);
            if (host) Command(local, child.Id, command.Encode());
            else channel!.Request(child.Id, command.Encode(), true);
            return false;
        }

        private bool CanInteract(uint peer, Binding child)
        {
            Vector3 position;
            if (peer == 0)
            {
                // The native coupler takes a click whatever the hands hold (an equipped tool counted as held).
                if (!host || gameplay.Get_PlayerState != PlayerState.Walking) return false;
                position = gameplay.PlayerWalking.transform.position;
            }
            else
            {
                if (!presence.TryGetValue(peer, out var latest) || Time.unscaledTime - latest.Time > 2 ||
                    latest.Pose.State != (byte)PlayerState.Walking) return false;
                position = new Vector3(latest.Pose.X, latest.Pose.Y, latest.Pose.Z);
            }
            // From the coupler: a long trailer's origin can sit well behind it.
            var coupler = child.Trailer?.attachmentPoint != null ? child.Trailer.attachmentPoint.position : child.Vehicle.transform.position;
            return (position - coupler).sqrMagnitude <= InteractionRange * InteractionRange;
        }

        [MonMP.Core.World.WorldCommandAdapters(TrailerWorldState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || !bindings.TryGetValue(id, out var child)) return;
            var command = TrailerAuthorityCommand.Decode(payload);
            bool trackInteraction = command.Action != TrailerAuthorityAction.Move;
            if (trackInteraction)
            {

#if MONMP_DEVELOPMENT
                TestInteractionCommandCount++;


                TestLastInteractionPeer = peer;


                TestLastInteractionAction = command.Action;


                TestLastInteractionAccepted = false;
#endif

            }
            if (!graph.TryGet(id, out var childLink) || !graph.TryGet(command.TowId, out var towLink) ||
                !bindings.TryGetValue(command.TowId, out var tow)) return;
            if (command.Action == TrailerAuthorityAction.Move)
            {
                ApplyRemoteMotion(peer, command, child, childLink);
                return;
            }
            // A nearby walker can couple or release a trailer while another peer drives the tow root.
            // The driver keeps motion ownership; this command changes topology only after host checks.
            if (child.Trailer == null || tow.Hitch == null || !CanInteract(peer, child) ||
                towLink.Generation != command.ExpectedTowGeneration || childLink.Generation != command.ExpectedTrailerGeneration ||
                command.Motion != null) return;

            var before = graph.Snapshot();
            bool changed;
            if (command.Action == TrailerAuthorityAction.Attach)
            {
                // A guest measured the gap on its own copies: allow for the poses it had not yet seen.
                // Like the native coupler, the trailer is pulled onto the hitch over a moment, then
                // attached (#256); it used to jump there at once.
                if (pulls.Any(p => p.Child == child || p.Tow == tow) || !CanPull(tow, child, PullRange + (peer == 0 ? 0f : LagSlack))) return;
                pulls.Add(new Pull { Peer = peer, Tow = tow, Child = child, Command = command, Until = Time.unscaledTime + PullSeconds });
                return;
            }
            else if (command.Action == TrailerAuthorityAction.Detach)
            {
                if (childLink.ParentId != tow.Id || tow.Hitch.Trailer != child.Trailer ||
                    !graph.TryDetachFromValidatedInteraction(tow.Id, child.Id, command.ExpectedTowGeneration,
                        command.ExpectedTrailerGeneration, peer)) return;
                applyingNative = true;
                try { tow.Hitch.DetachTrailer(tow.Vehicle); }
                finally { applyingNative = false; }
                changed = true;
            }
            else return;

            if (changed)
            {
                PublishGraphChanges(before);
                if (trackInteraction)
#if MONMP_DEVELOPMENT
TestLastInteractionAccepted = true;
#else
{}
#endif

            }
        }

        private void ApplyRemoteMotion(uint peer, TrailerAuthorityCommand command, Binding child, TrailerLinkState link)
        {
            var motion = command.Motion;
            if (motion == null || link.ParentId == 0 || link.Owner != peer ||
                motion.Link.ParentId != link.ParentId || motion.Link.Owner != peer || motion.Link.Generation != link.Generation ||
                command.ExpectedTrailerGeneration != link.Generation || !graph.TryGet(command.TowId, out var root) ||
                command.ExpectedTowGeneration != root.Generation || command.TowId != RootId(child.Id)) return;
            var position = new Vector3(motion.Pose.X, motion.Pose.Y, motion.Pose.Z);
            float elapsed = child.LastMotion <= 0 ? SendInterval : Mathf.Clamp(Time.unscaledTime - child.LastMotion, 0.01f, 1f);
            // Admission uses the last authoritative sample, independent of presentation lag.
            var accepted = child.State.Pose;
            if ((position - new Vector3(accepted.X, accepted.Y, accepted.Z)).sqrMagnitude > Mathf.Pow(2f + 200f * elapsed, 2)) return;
            child.LastMotion = Time.unscaledTime;
            child.State = motion;
            SetMotionTarget(child, motion);
            PublishRecord(child.Id, motion.Encode());

#if MONMP_DEVELOPMENT
            TestRemoteMotionAcceptedCount++;
#endif

        }

        private ulong RootId(ulong id)
        {
            var link = bindings[id].State.Link;
            var seen = new HashSet<ulong>();
            while (link.ParentId != 0 && bindings.TryGetValue(link.ParentId, out var parent) && seen.Add(id))
            {
                id = link.ParentId;
                link = parent.State.Link;
            }
            return id;
        }

        private void SyncRootOwners()
        {
            if (!host) return;
            foreach (var binding in bindings.Values.Where(b => b.SharedVehicle))
            {
                if (!vehicles.TryGetDriverState(binding.Vehicle, out var vehicleState) ||
                    !graph.TryGet(binding.Id, out var link) || link.Owner == vehicleState.Driver.Owner || link.ParentId != 0) continue;
                var before = graph.Snapshot();
                if (graph.TrySetRootOwner(binding.Id, link.Generation, vehicleState.Driver.Owner)) PublishGraphChanges(before);
            }
        }

        private void PublishGraphChanges(KeyValuePair<ulong, TrailerLinkState>[] before)
        {
            var previous = before.ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var pair in graph.Snapshot())
            {
                var binding = bindings[pair.Key];
                if (previous.TryGetValue(pair.Key, out var old) && old.ParentId == pair.Value.ParentId &&
                    old.Owner == pair.Value.Owner && old.Generation == pair.Value.Generation) continue;
                binding.State = Capture(binding, pair.Value);
                if (host && channel != null) PublishRecord(binding.Id, binding.State.Encode());
            }
            foreach (var pair in graph.Snapshot()) ApplyMode(bindings[pair.Key], pair.Value.Owner == session?.LocalPeerId || (host && pair.Value.Owner == ItemLease.Unowned));
        }

        private void OnPeerLeft(uint peer)
        {
            presence.Remove(peer);
            if (!host) return;
            var before = graph.Snapshot();
            if (graph.RevokePeer(peer).Length != 0) PublishGraphChanges(before);
        }

        internal void RevokeOwnership(uint peer)
        {
            if (!host) return;
            var before = graph.Snapshot();
            if (graph.RevokePeer(peer).Length != 0) PublishGraphChanges(before);
        }

        // Match the tow vehicle's host physics presentation, so loose cargo sees a continuous bed.
        internal void FixedUpdate()
        {
            if (!host || disposed || !Ready) return;
            float dt = Time.fixedDeltaTime, blend = 1 - Mathf.Exp(-8f * dt);
            foreach (var binding in bindings.Values)
            {
                if (binding.SharedVehicle || binding.Vehicle == null || !binding.Vehicle.gameObject.activeInHierarchy ||
                    !binding.HasMotionTarget || binding.State.CanPublishMotion(host, session!.LocalPeerId)) continue;
                var body = binding.Vehicle.vehicleRigidbody;
                if (body == null || !body.isKinematic) continue;
                float age = Mathf.Max(0, Time.unscaledTime - binding.MotionReceivedAt);
                var velocity = new Vector3(binding.State.Velocity.X, binding.State.Velocity.Y, binding.State.Velocity.Z);
                var spin = new Vector3(binding.State.AngularVelocity.X, binding.State.AngularVelocity.Y, binding.State.AngularVelocity.Z) * Mathf.Rad2Deg;
                var predicted = binding.PoseTo + velocity * Mathf.Min(age, 0.25f);
                var predictedRotation = Quaternion.Euler(spin * Mathf.Min(age, 0.25f)) * binding.RotationTo;
                // Stop advancing when samples stop; otherwise the capped target still acquires a velocity offset.
                float advance = Mathf.Clamp(0.25f - age, 0, dt);
                if ((body.position - predicted).sqrMagnitude > 25f)
                { binding.PhysicsCorrectionVelocity = Vector3.zero; body.position = predicted; body.rotation = predictedRotation; continue; }
                var position = Vector3.SmoothDamp(body.position + velocity * advance, predicted,
                    ref binding.PhysicsCorrectionVelocity, 0.2f, float.PositiveInfinity, dt);
                var rotation = Quaternion.Slerp(Quaternion.Euler(spin * advance) * body.rotation, predictedRotation, blend);
                if ((body.position - position).sqrMagnitude < 1e-10f && Quaternion.Angle(body.rotation, rotation) < 0.001f) continue;
                KinematicCarrierMotion.Record(body, position, rotation);
                body.MovePosition(position); body.MoveRotation(rotation);
            }
        }

        private void PresentMotion()
        {
            foreach (var binding in bindings.Values)
            {
                if (binding.SharedVehicle || binding.Vehicle == null || !binding.Vehicle.gameObject.activeInHierarchy ||
                    !binding.HasMotionTarget || binding.State.CanPublishMotion(host, session!.LocalPeerId)) continue;
                var frame = binding.Vehicle.transform;
                var walker = gameplay.PlayerWalking != null ? gameplay.PlayerWalking.transform : null!;
                if (host && binding.Vehicle.vehicleRigidbody.isKinematic)
                {
                    if (binding.RiderFrameKnown) Carriers?.CarryRiders(binding.Id, binding.RiderFramePosition, binding.RiderFrameRotation, walker, walkerOnly: true);
                    binding.RiderFramePosition = frame.position; binding.RiderFrameRotation = frame.rotation; binding.RiderFrameKnown = true;
                    continue;
                }
                Vector3 position; Quaternion rotation;
                if (!host) binding.Presentation.Evaluate(Time.unscaledTime, out position, out rotation);
                else
                {
                    float fraction = Mathf.Clamp01((Time.unscaledTime - binding.MotionReceivedAt) / 0.08f);
                    position = Vector3.Lerp(binding.PoseFrom, binding.PoseTo, fraction);
                    rotation = Quaternion.Slerp(binding.RotationFrom, binding.RotationTo, fraction);
                }
                var fromPosition = frame.position; var fromRotation = frame.rotation;
                if ((fromPosition - position).sqrMagnitude < 1e-10f && Quaternion.Angle(fromRotation, rotation) < 0.001f) continue;
                Carriers?.PrepareRiders(binding.Id, walker);
                binding.Vehicle.vehicleRigidbody.position = position; binding.Vehicle.vehicleRigidbody.rotation = rotation;
                frame.SetPositionAndRotation(position, rotation);
                Carriers?.CarryRiders(binding.Id, fromPosition, fromRotation, walker);
            }
        }

        internal void Update()
        {
            if (disposed || !Ready) return;
            if (host) { SyncRootOwners(); UpdatePulls(); }
            CopyTowLamps();
            PresentMotion();
            if (Time.unscaledTime < nextSend) return;
            nextSend = Time.unscaledTime + SendInterval;
            foreach (var binding in bindings.Values)
            {
                if (binding.SharedVehicle || binding.Trailer == null) continue;
                // Guests receive links in binding.State; their graph is never mutated by
                // network updates. Reading that constructor baseline suppresses every
                // guest driver's trailer stream after attach or an ownership handoff.
                if (binding.State == null) continue;
                var link = binding.State.Link;
                if (!host && binding.NextLinkRepair > 0 && Time.unscaledTime >= binding.NextLinkRepair)
                    ApplyNativeLink(binding, link);
                if (!binding.State.CanPublishMotion(host, session!.LocalPeerId)) continue;
                var motion = Capture(binding, link);
                if (host)
                {
                    if (binding.State.SameMotion(motion) || binding.State.RestsWith(motion)) continue;
                    binding.State = motion;
                    PublishRecord(binding.Id, motion.Encode());

#if MONMP_DEVELOPMENT
                    TestLocalMotionPublishCount++;
#endif

                }
                else
                {
                    // Exact unchanged samples can use a one-second heartbeat instead of 20 reliable commands/s.
                    if (binding.SentMotion?.SameMotion(motion) == true && Time.unscaledTime < binding.NextMotionRefresh) continue;
                    binding.SentMotion = motion; binding.NextMotionRefresh = Time.unscaledTime + 1f;
                    ulong root = RootId(binding.Id);
                    channel!.Request(binding.Id, new TrailerAuthorityCommand(TrailerAuthorityAction.Move,
                        root, bindings[root].State.Link.Generation, link.Generation, motion).Encode(), true);
                }
            }
        }

        // A detached, unowned trailer whose pose the host already streams (towing-service recovery).
        internal bool IsLooseHostTrailer(VehicleController vehicle) =>
            byVehicle.TryGetValue(vehicle, out var b) && !b.SharedVehicle && b.Trailer != null && graph.TryGet(b.Id, out var link) &&
            link.Owner == ItemLease.Unowned && link.ParentId == 0 && !b.Trailer.attached && b.Trailer.TrailerHitch == null;
        // Tow roots also belong to the hitch registry, but SharedVehicles streams their
        // bodies. Only standalone trailer bindings use this adapter's motion route.
        internal bool OwnsBodyMotion(VehicleController vehicle)
        {
            return byVehicle.TryGetValue(vehicle, out var binding) && !binding.SharedVehicle && binding.Trailer != null;
        }
        internal bool Handles(VehicleController vehicle) => byVehicle.ContainsKey(vehicle);
        internal IEnumerable<VehicleController> BoundControllers => bindings.Values.Select(b => b.Vehicle);
        internal ulong IdOf(VehicleController vehicle) => byVehicle.TryGetValue(vehicle, out var b) ? b.Id : 0;


#if MONMP_DEVELOPMENT
        internal void TestRequestDetach(ulong childId, uint peer)
        {
            if (!bindings.TryGetValue(childId, out var child) || child.State.Link.ParentId == 0) throw new InvalidOperationException("No attached trailer fixture.");
            ulong towId = child.State.Link.ParentId;
            var childLink = child.State.Link;
            var towLink = bindings[towId].State.Link;
            var payload = new TrailerAuthorityCommand(TrailerAuthorityAction.Detach, towId,
                towLink.Generation, childLink.Generation).Encode();
            if (host) Command(peer, childId, payload);
            else channel!.Request(childId, payload, true);
        }


        internal (ulong TowId, ulong TrailerId) TestFixtureIds(Vehicle_Type towType = Vehicle_Type.MainVehicle)
        {
            var tow = bindings.Values.SingleOrDefault(b => b.SharedVehicle && b.Vehicle.vehicle_type == towType && b.Hitch != null);
            var child = bindings.Values.SingleOrDefault(b => b.Trailer != null &&
                b.Vehicle.name.IndexOf("WoodSplitter", StringComparison.OrdinalIgnoreCase) >= 0);
            if (tow == null || child == null) throw new InvalidOperationException("Trailer fixture requires the starting Konig and WoodSplitter trailer.");
            return (tow.Id, child.Id);
        }


        internal TrailerWorldState TestState(ulong id) => bindings[id].State;


        internal bool TestNativeLinkIntact(ulong towId, ulong childId) => NativeLinkIntact(bindings[towId], bindings[childId]);


        internal bool TestLocalWheelMode(ulong id) => !bindings[id].Vehicle.vehicleRigidbody.isKinematic &&
            bindings[id].Vehicle.Wheels.Select((wheel, index) =>
                index < bindings[id].OriginalExternalWheels.Length &&
                wheel.wheelController.UsingExternalUpdate == bindings[id].OriginalExternalWheels[index] &&
                !wheel.wheelController.visualOnlyUpdate).All(value => value);


        internal VehicleController TestVehicle(ulong id) => bindings[id].Vehicle;


        internal TrailerModule TestTrailer(ulong id) => bindings[id].Trailer ?? throw new InvalidOperationException("Entity is not a trailer.");


        internal TrailerHitchModule TestHitch(ulong id) => bindings[id].Hitch ?? throw new InvalidOperationException("Entity has no trailer hitch.");


        internal bool TestCanNativeAttach(ulong towId, ulong childId) => CanNativeAttach(bindings[towId], bindings[childId]);


        internal void TestNativeCouplerClick(ulong childId)
        {
            var child = bindings[childId];
            if (child.Trailer == null) throw new InvalidOperationException("Native coupler fixture target is not a trailer.");
            var coupler = UnityEngine.Object.FindObjectsOfType<Trailer_Coupler>()
                .SingleOrDefault(value => value.vc == child.Vehicle);
            if (coupler == null) throw new InvalidOperationException("Native coupler fixture could not find the trailer's Trailer_Coupler.");

            // Match the native hitch's normal FixedUpdate discovery before invoking
            // the actual game MouseDown entry point intercepted by Harmony.
            var tow = child.State.Link.ParentId != 0
                ? bindings[child.State.Link.ParentId]
                : bindings.Values.SingleOrDefault(value => value.SharedVehicle && value.Vehicle.vehicle_type == Vehicle_Type.MainVehicle && value.Hitch != null);
            if (tow?.Hitch == null) throw new InvalidOperationException("Native coupler fixture could not find the selected tow hitch.");
            tow.Hitch.SyncTrailers();
            var findNearest = AccessTools.Method(typeof(TrailerHitchModule), "FindNearestTrailerModule")
                ?? throw new InvalidOperationException("Native hitch candidate lookup is unavailable.");
            findNearest.Invoke(tow.Hitch, null);
            coupler.MouseDown();
        }


        internal bool TestPeerWalkingOutOfRange(uint peer, ulong childId)
        {
            if (!presence.TryGetValue(peer, out var latest) || Time.unscaledTime - latest.Time > 2 ||
                latest.Pose.State != (byte)PlayerState.Walking) return false;
            var position = new Vector3(latest.Pose.X, latest.Pose.Y, latest.Pose.Z);
            var child = bindings[childId];
            var coupler = child.Trailer?.attachmentPoint != null ? child.Trailer.attachmentPoint.position : child.Vehicle.transform.position;
            return (position - coupler).sqrMagnitude > InteractionRange * InteractionRange;
        }


        internal void TestStageAtHitch(ulong towId, ulong childId)
        {
            var tow = bindings[towId];
            var child = bindings[childId];
            if (tow.Hitch == null || child.Trailer == null || tow.Hitch.attached || tow.Hitch.Trailer != null ||
                child.Trailer.attached || child.Trailer.TrailerHitch != null)
                throw new InvalidOperationException("Cannot stage an attached trailer fixture.");
            tow.Hitch.Set_isHitchInstalled = true;
            var body = child.Vehicle.vehicleRigidbody;
            body.isKinematic = true;
            body.position += tow.Hitch.attachmentPoint.position - child.Trailer.attachmentPoint.position;
            child.Vehicle.transform.SetPositionAndRotation(body.position, body.rotation);
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            Physics.SyncTransforms();
        }


        internal string TestNativeAttachDiagnostics(ulong towId, ulong childId)
        {
            var tow = bindings[towId];
            var child = bindings[childId];
            var hitch = tow.Hitch;
            var trailer = child.Trailer;
            var field = AccessTools.Field(typeof(TrailerHitchModule), "isHitchInstalled");
            hitch?.SyncTrailers();
            var findNearest = AccessTools.Method(typeof(TrailerHitchModule), "FindNearestTrailerModule");
            var nearest = hitch != null && findNearest != null ? findNearest.Invoke(hitch, null) as TrailerModule : null;
            float gap = hitch?.attachmentPoint != null && trailer?.attachmentPoint != null
                ? Vector3.Distance(hitch.attachmentPoint.position, trailer.attachmentPoint.position) : float.PositiveInfinity;
            return "nearest=" + (nearest?.VehicleController?.name ?? "none") +
                "; installed=" + (hitch != null && field != null ? field.GetValue(hitch) : null) +
                "; hitchOn=" + (hitch?.IsOn.ToString() ?? "null") +
                "; hitchAttached=" + (hitch?.attached.ToString() ?? "null") +
                "; hitchTrailer=" + (hitch?.Trailer?.VehicleController?.name ?? "none") +
                "; trailerAttached=" + (trailer?.attached.ToString() ?? "null") +
                "; trailerHitch=" + (trailer?.TrailerHitch?.VehicleController?.name ?? "none") +
                "; pointGap=" + gap.ToString("F3") + "; threshold=" + (hitch?.attachDistanceThreshold.ToString("F3") ?? "null") +
                "; trailerBody=" + child.Vehicle.vehicleRigidbody.position.ToString("F3");
        }


        internal void TestRequestAttach(ulong towId, ulong childId, uint peer)
        {
            var tow = bindings[towId];
            var child = bindings[childId];
            var payload = new TrailerAuthorityCommand(TrailerAuthorityAction.Attach, towId,
                tow.State.Link.Generation, child.State.Link.Generation).Encode();
            if (host) Command(peer, childId, payload);
            else channel!.Request(childId, payload, true);
        }
#endif


        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnPeerLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            foreach (var binding in bindings.Values)
            {
                if (binding.Vehicle == null || binding.SharedVehicle || !binding.ModeSet) continue;
                if (binding.Vehicle.VehicleMultiplayerInstanceType != binding.OriginalMode)
                    binding.Vehicle.SetMultiplayerInstanceType(binding.OriginalMode, true);
                var body = binding.Vehicle.vehicleRigidbody;
                if (body != null)
                {
                    body.constraints = binding.OriginalConstraints;
                    body.isKinematic = binding.OriginalKinematic;
                    body.collisionDetectionMode = binding.OriginalCollision;
                    body.interpolation = binding.OriginalInterpolation;
                    if (!host) ApplyMotion(binding, binding.Original);
                }
                binding.Vehicle.input.autoSetInput = binding.OriginalAutoInput;
                binding.Vehicle.freezeWhileIdle = binding.OriginalFreezeIdle;
                binding.Vehicle.freezeWhileAsleep = binding.OriginalFreezeSleep;
                RestoreWheelUpdates(binding);
            }
            channel = null;
            session = null;
        }
    }
}
