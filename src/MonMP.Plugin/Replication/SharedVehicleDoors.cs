using System;
using System.Buffers.Binary;
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
using UnityEngine.Events;

namespace MonMP.Plugin.Replication
{
    // Native hinges simulate on the host, independently of root driving authority.
    // Replicas apply local door angles without creating competing hinge bodies.
    internal sealed class SharedVehicleDoors : IDisposable
    {
        private sealed class Binding
        {
            public ulong Id;
            public Vehicle_Door Door = null!;
            public Vehicle_DoorHandle? Handle;
            public Vehicle_DoorHandle[] DoorHandles = Array.Empty<Vehicle_DoorHandle>();
            public Vector3 Position, Euler;
            public VehicleDoorState State;
            public VehicleDoorState OriginalState;
            public bool OriginalDriverDoorClosed, OriginalPassengerDoorClosed;
            public Rigidbody? OriginalBody;
            public HingeJoint? OriginalHinge;
            public bool OriginalBodyKinematic, ReplicaPhysicsSuppressed;
            public RigidbodyInterpolation OriginalBodyInterpolation;
            public Rigidbody? OriginalHingeConnectedBody;
            public Vector3 OriginalLocalPosition, OriginalBodyVelocity, OriginalBodyAngularVelocity, OriginalHingeConnectedAnchor;
            public Quaternion OriginalLocalRotation;
            public bool OriginalHingeAutoConfigureConnectedAnchor;
            public float TargetAngle, PresentationAngle;
            public bool SawPresentationInterpolation;
            public bool Late;
            public uint Revision;
            public uint RequestRevision, LastClosedRevision;
            public readonly VehicleDoorOpeningPresentation Opening = new VehicleDoorOpeningPresentation();
            public readonly DoorPresentationIntent Intent = new DoorPresentationIntent();
            public readonly DeferredReplicaHingeState<VehicleDoorState> Deferred = new DeferredReplicaHingeState<VehicleDoorState>();
        }
        // Cabin doors of trucks bought during the session: they appear after the initial records, so
        // they share one record listing each door's state under the purchased truck's shared id.
        public const ushort LateAdapter = 134;
        private static readonly ulong LateEntity = StaticId.Hash("shared-late-doors/v1");
        internal Func<IEnumerable<(ulong Id, VehicleController Vehicle)>>? LateVehicles { get; set; }
        private float nextLateScan;
        private static readonly FieldInfo MartinVehicleField = AccessTools.Field(typeof(AI_MartinCar), "vc");
        private static readonly FieldInfo OpenField = AccessTools.Field(typeof(Vehicle_Door), "isOpen");
        private static readonly FieldInfo OriginalPositionField = AccessTools.Field(typeof(Vehicle_Door), "originalLocalPosition");
        private static readonly FieldInfo OriginalEulerField = AccessTools.Field(typeof(Vehicle_Door), "originalLocalEuler");
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Gameplay gameplay;
        private readonly bool host;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, disposed;
#if MONMP_DEVELOPMENT
        private bool isolatedCueFallback;
#endif
        private float nextCapture;
        private ulong nextInteraction;
        private readonly VehicleController? konig;
        private readonly Func<VehicleController, bool>? deferReplica;
        public SharedVehicleDoors(Gameplay gameplay, bool host, IEnumerable<VehicleController> vehicles, VehicleController? konig, string diagnostics, NativeEntityRegistry identities,
            Func<VehicleController, bool>? deferReplica = null)
        {
            this.gameplay = gameplay; this.host = host; this.konig = konig; this.deferReplica = deferReplica;
            var lines = new List<string>();
            foreach (var vehicle in vehicles.Where(v => v.vehicle_type == Vehicle_Type.OlTruck))
            {
                // A saved sale clone is absent from a fresh guest's scene. Its doors must use
                // the same sale-scoped aggregate as a truck purchased after hosting starts.
                if (SharedVehicleSales.Active?.OwnsSaleClone(vehicle.transform) == true) continue;
                var doors = vehicle.GetComponentsInChildren<Vehicle_Door>(true).Where(d => d.vehicleController == vehicle).ToArray();
                if (doors.Length < 2 || !doors.Any(d => d.isDriverDoor) || !doors.Any(d => !d.isDriverDoor))
                    throw new InvalidOperationException("Unsupported truck cabin.");
                foreach (var door in doors)
                {
                    var handle = door.GetComponentsInChildren<Vehicle_DoorHandle>(true).FirstOrDefault(h => h.vehicle_Door == door);
                    var body = door.GetComponent<Rigidbody>();
                    var hinge = door.GetComponent<HingeJoint>();
                    bool restoredOpenDoor = IsRestoredOpenDoor(door, vehicle, out _);
                    if (door.vehicleController != vehicle || (door.gameObject.activeInHierarchy && (!door.enabled || handle == null)) ||
                        (door.Get_IsOpen ? !restoredOpenDoor : body != null || door.GetComponent<Joint>() != null) ||
                        (body != null && (hinge == null || hinge.connectedBody != vehicle.vehicleRigidbody)) || new[] { "DoorOpened", "DoorClosed" }.Any(f =>
                        ((UnityEvent)AccessTools.Field(typeof(Vehicle_Door), f).GetValue(door)).GetPersistentEventCount() != 0))
                        throw new InvalidOperationException("Unsupported stock truck cabin door physics; only native closed doors or save-restored hinged doors are supported.");
                    string identity = RuntimeProbe.CanonicalPath(door.transform) + "/truck-door-v1";
                    Bind(door, handle, identity, identities, lines, EntityPersistence.NativeSave);
                }
            }
            // The starting Konig's driver/passenger doors are fixed chassis hinges; their handles stay
            // inactive (and thus unclickable) until the matching Door_Left/Door_Right part is installed.
            // Session-only, matching the Konig part-socket state they depend on.
            if (konig != null) BindChassisDoors(konig, "/konig-door-v1", identities, lines);
            foreach (var shell in vehicles.Where(v => v != null &&
                (v.vehicle_type == Vehicle_Type.ScrapyardKonig || v.vehicle_type == Vehicle_Type.Prinz)))
                BindChassisDoors(shell, shell.vehicle_type == Vehicle_Type.Prinz ? "/prinz-door-v1" : "/scrapyard-konig-door-v1", identities, lines);
            // The same fixed chassis hinges on the built racecar and Martin's car: each player opened only
            // their own copy (#166, #178). Martin's car is host-driven; its replica doors follow the host.
            foreach (var racecar in vehicles.Where(v => v != null && v.vehicle_type == Vehicle_Type.Racecar))
                BindChassisDoors(racecar, "/racecar-door-v1", identities, lines);
            foreach (var martin in UnityEngine.Object.FindObjectsOfType<AI_MartinCar>(true).Where(m => m != null && m.gameObject.scene.IsValid()))
            {
                var car = MartinVehicleField.GetValue(martin) as VehicleController;
                if (car == null) car = martin.GetComponentInChildren<VehicleController>(true);
                if (car != null) BindChassisDoors(car, "/martin-car-door-v1", identities, lines);
            }

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-doors.tsv"), new[] { "id\tdoor-identity" }.Concat(lines.OrderBy(s => s)));
#endif

        }
        private void BindChassisDoors(VehicleController vehicle, string suffix, NativeEntityRegistry identities, List<string> lines)
        {
            foreach (var door in vehicle.GetComponentsInChildren<Vehicle_Door>(true))
            {
                if (bindings.Values.Any(b => b.Door == door)) continue;
                var handle = vehicle.GetComponentsInChildren<Vehicle_DoorHandle>(true).FirstOrDefault(h => h.vehicle_Door == door);
                bool restoredOpen = IsRestoredOpenDoor(door, vehicle, out _);
                if (door.vehicleController != vehicle || handle == null ||
                    (!restoredOpen && (door.Get_IsOpen || door.GetComponent<Rigidbody>() != null || door.GetComponent<Joint>() != null)))
                {
                    lines.Add("0000000000000000	" + RuntimeProbe.CanonicalPath(door.transform) + suffix + "	skipped");
                    continue;
                }
                string identity = RuntimeProbe.CanonicalPath(door.transform) + suffix;
                Bind(door, handle, identity, identities, lines, EntityPersistence.SessionOnly, restoredOpen);
            }
        }
        // LoadFromSaveData reopens a door saved open by adding a native body hinged to the chassis.
        internal static bool IsRestoredOpenDoor(Vehicle_Door door, VehicleController vehicle, out Rigidbody? body)
        {
            body = door == null ? null : door.GetComponent<Rigidbody>();
            var hinge = door == null ? null : door.GetComponent<HingeJoint>();
            return door != null && door.vehicleController == vehicle && door.Get_IsOpen && body != null && hinge != null &&
                door.GetComponents<Rigidbody>().Length == 1 && door.GetComponents<Joint>().Length == 1 &&
                hinge.connectedBody == vehicle.vehicleRigidbody;
        }
        private void Bind(Vehicle_Door door, Vehicle_DoorHandle? handle, string identity, NativeEntityRegistry identities, List<string> lines,
            EntityPersistence persistence, bool restoredOpen = false, ulong lateId = 0)
        {
            // LoadFromSaveData changes truck-door local yaw and adds a native hinge/body. Keep the
            // prefab baseline captured by Awake so the saved absolute yaw becomes the replicated offset.
            bool nativeSave = persistence == EntityPersistence.NativeSave || restoredOpen;
            var b = new Binding { Id = lateId != 0 ? lateId : StaticId.Hash(identity), Late = lateId != 0, Door = door, Handle = handle,
                DoorHandles = door.vehicleController.GetComponentsInChildren<Vehicle_DoorHandle>(true).Where(h => h.vehicle_Door == door).ToArray(),
                Position = nativeSave ? (Vector3)OriginalPositionField.GetValue(door) : door.transform.localPosition,
                Euler = nativeSave ? (Vector3)OriginalEulerField.GetValue(door) : door.transform.localEulerAngles,
                OriginalLocalPosition = door.transform.localPosition,
                OriginalLocalRotation = door.transform.localRotation,
                OriginalDriverDoorClosed = door.vehicleController.isDriverDoorClosed,
                OriginalPassengerDoorClosed = door.vehicleController.isPassengerDoorClosed,
                OriginalBody = door.GetComponent<Rigidbody>(), OriginalHinge = door.GetComponent<HingeJoint>() };
            b.OriginalBodyKinematic = b.OriginalBody != null && b.OriginalBody.isKinematic;
            b.OriginalBodyInterpolation = b.OriginalBody == null ? RigidbodyInterpolation.None : b.OriginalBody.interpolation;
            b.OriginalBodyVelocity = b.OriginalBody == null ? Vector3.zero : b.OriginalBody.velocity;
            b.OriginalBodyAngularVelocity = b.OriginalBody == null ? Vector3.zero : b.OriginalBody.angularVelocity;
            b.OriginalHingeConnectedBody = b.OriginalHinge == null ? null : b.OriginalHinge.connectedBody;
            b.OriginalHingeAutoConfigureConnectedAnchor = b.OriginalHinge != null && b.OriginalHinge.autoConfigureConnectedAnchor;
            b.OriginalHingeConnectedAnchor = b.OriginalHinge == null ? Vector3.zero : b.OriginalHinge.connectedAnchor;
            b.State = NativeState(b);
            b.OriginalState = b.State;
            b.TargetAngle = b.PresentationAngle = b.State.Angle;
            if (lateId == 0) identities.Bind(b.Id, VehicleDoorState.Adapter, door.gameObject, identity, true, persistence);
            bindings.Add(b.Id, b); lines?.Add(b.Id.ToString("X16") + "\t" + identity);
        }
        private static VehicleDoorState NativeState(Binding b)
        {
            bool open = b.Door.Get_IsOpen;
            float angle = open ? Mathf.DeltaAngle(b.Euler.y, b.Door.transform.localEulerAngles.y) : 0;
            return new VehicleDoorState(open, angle);
        }
        public IEnumerable<WorldRecord> InitialRecords()
        {
            ScanLate();
            return bindings.Values.Where(b => !b.Late).Select(b => new WorldRecord(b.Id, 1, VehicleDoorState.Adapter, b.State.Encode()))
                .Append(new WorldRecord(LateEntity, 1, LateAdapter, EncodeLate()));
        }
        internal void RefreshPurchasedBindings()
        {
            if (disposed) return;
            ScanLate();
            if (!host && channel != null && channel.Records.TryGetValue(LateEntity, out var record)) ApplyLate(record.Payload, true, record.Revision);
        }
        private byte[] EncodeLate()
        {
            var late = bindings.Values.Where(b => b.Late && b.Door != null).OrderBy(b => b.Id).ToList();
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream))
            {
                w.Write((ushort)late.Count);
                foreach (var b in late) { var state = b.State.Encode(); w.Write(b.Id); w.Write((byte)state.Length); w.Write(state); }
            }
            return stream.ToArray();
        }
        private void ApplyLate(byte[] payload, bool snap, uint revision = 0)
        {
            ScanLate();
            using var r = new BinaryReader(new MemoryStream(payload));
            int count = r.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                ulong id = r.ReadUInt64();
                var state = VehicleDoorState.Decode(r.ReadBytes(r.ReadByte()));
                if (bindings.TryGetValue(id, out var b) && b.Late && b.Door != null) AcceptState(b, state, revision, snap);
            }
        }
        // The two cabin doors of each purchased truck, keyed by its shared purchase id: they toggled
        // only on the clicking player's copy.
        private IEnumerable<(ulong Id, VehicleController Vehicle)> KnownLateVehicles()
        {
            var known = LateVehicles?.Invoke() ?? Enumerable.Empty<(ulong Id, VehicleController Vehicle)>();
            // A real native purchase is registered before the clone's wheel Start completes.
            // Its sale identity is already canonical even while SharedVehicles queues motion
            // binding; immediate handle callbacks must not fall through to guest hinge physics.
            var sales = SharedVehicleSales.Active;
            if (sales != null)
                known = known.Concat(sales.PurchasedVehicles().Where(p => p.Type == Vehicle_Type.OlTruck && p.Instance != null)
                    .Select(p => (p.Entity, p.Instance.GetComponent<VehicleController>()))
                    .Where(p => p.Item2 != null));
            return known.GroupBy(p => p.Id).Select(g => g.First());
        }
        private void ScanLate(VehicleController? onlyVehicle = null)
        {
            var known = KnownLateVehicles().ToList();
            foreach (var stale in bindings.Values.Where(b => b.Late &&
                (b.Door == null || !known.Any(p => p.Vehicle == b.Door.vehicleController))).ToArray())
            {
                if (!host && stale.Door != null) RestoreLocalState(stale);
                bindings.Remove(stale.Id);
            }
            foreach (var (vehicleId, vehicle) in known)
            {
                if (vehicle == null || vehicle.vehicle_type != Vehicle_Type.OlTruck ||
                    onlyVehicle != null && vehicle != onlyVehicle) continue;
                foreach (var door in vehicle.GetComponentsInChildren<Vehicle_Door>(true))
                {
                    if (door == null || door.vehicleController != vehicle || bindings.Values.Any(b => b.Door == door)) continue;
                    var handle = door.GetComponentsInChildren<Vehicle_DoorHandle>(true).FirstOrDefault(h => h.vehicle_Door == door);
                    bool nativeOpen = IsRestoredOpenDoor(door, vehicle, out _);
                    if (handle == null || (door.Get_IsOpen ? !nativeOpen :
                        door.GetComponent<Rigidbody>() != null || door.GetComponent<Joint>() != null)) continue;
                    string path = LateDoorPath(door, vehicle);
                    ulong id = LateDoorId(vehicleId, door, vehicle);
                    if (bindings.ContainsKey(id)) continue;
                    // A player can open this door before the two-second discovery tick. Its native
                    // hinge is valid too; retain Awake's closed baseline and the live original body.
                    Bind(door, handle, path, null!, null!, EntityPersistence.SessionOnly, nativeOpen, id);
                    if (!host) SuppressReplicaPhysics(bindings[id]);
                }
            }
        }
        private static string LateDoorPath(Vehicle_Door door, VehicleController vehicle)
        {
            string path = door.name;
            for (var t = door.transform.parent; t != null && t != vehicle.transform; t = t.parent) path = t.name + "/" + path;
            return path;
        }
        private static ulong LateDoorId(ulong entity, Vehicle_Door door, VehicleController vehicle) =>
            StaticId.Hash(entity.ToString("X16") + "/" + LateDoorPath(door, vehicle) + "/late-door-v1");

        internal bool TryCueSource(SOUND_Vehicle_Door sound, out ulong id)
        {
            id = 0;
            if (disposed || session?.IsRunning != true || sound == null || !sound.gameObject.scene.IsValid()) return false;
            var door = sound.GetComponent<Vehicle_Door>();
            if (door == null || door.GetComponent<SOUND_Vehicle_Door>() != sound || !door.gameObject.activeInHierarchy ||
                !door.enabled || door.vehicleController == null) return false;
            // Handles permits discovery only within the exact known purchased instance.
            if (!Handles(door)) return false;
            var found = bindings.Values.Where(b => b.Door == door).ToArray();
            if (found.Length != 1) return false;
            if (found[0].Late && SharedVehicleSales.Active?.PurchasedVehicles().Any(p => p.Type == Vehicle_Type.OlTruck &&
                p.Instance != null && p.Instance.GetComponent<VehicleController>() == door.vehicleController &&
                LateDoorId(p.Entity, door, door.vehicleController) == found[0].Id) != true) return false;
            id = found[0].Id; return true;
        }
        internal bool TryCueTarget(ulong id, out SOUND_Vehicle_Door sound)
        {
            sound = null!;
            if (disposed || session?.IsRunning != true) return false;
            if (!bindings.ContainsKey(id))
            {
                foreach (var (entity, vehicle) in KnownLateVehicles())
                {
                    if (vehicle == null || !vehicle.GetComponentsInChildren<Vehicle_Door>(true)
                        .Any(d => d != null && d.vehicleController == vehicle && LateDoorId(entity, d, vehicle) == id)) continue;
                    ScanLate(vehicle); // Only the purchased vehicle whose native door matches this cue.
                }
            }
            if (!bindings.TryGetValue(id, out var b) || b.Door == null || !b.Door.enabled ||
                !b.Door.gameObject.activeInHierarchy || b.Door.vehicleController == null) return false;
            sound = b.Door.GetComponent<SOUND_Vehicle_Door>(); return sound != null;
        }
        internal bool IsCatalogPurchasedDoorCue(ulong id)
        {
            int matches = 0;
            var sales = SharedVehicleSales.Active;
            if (disposed || sales == null) return false;
            foreach (var (entity, prefab) in sales.TruckDoorCueCatalog())
                foreach (var door in prefab.GetComponentsInChildren<Vehicle_Door>(true))
                    if (door != null && door.vehicleController == prefab && door.GetComponent<SOUND_Vehicle_Door>() != null &&
                        door.GetComponentsInChildren<Vehicle_DoorHandle>(true).Any(h => h.vehicle_Door == door) &&
                        LateDoorId(entity, door, prefab) == id) matches++;
            return matches == 1;
        }
        private bool CueBusActive =>
#if MONMP_DEVELOPMENT
            !isolatedCueFallback &&
#endif
            ReferenceEquals(MonMPPlugin.Instance?.World?.Vehicles.Doors, this) && PresentationReplays.Events?.Live == true;
        private void PublishLate()
        {
            if (channel!.Records.TryGetValue(LateEntity, out var record) && record.Adapter == LateAdapter &&
                MatchesLateRecord(record.PayloadSpan)) return;
            channel.Publish(LateEntity, EncodeLate());
        }
        private bool MatchesLateRecord(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < 2) return false;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(payload), live = 0;
            foreach (var binding in bindings.Values) if (binding.Late && binding.Door != null) live++;
            if (count != live || payload.Length != 2 + count * 14) return false;
            ulong previous = 0;
            for (int i = 0, offset = 2; i < count; i++, offset += 14)
            {
                ulong id = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(offset, 8));
                if (i != 0 && id <= previous || payload[offset + 8] != 5 ||
                    !bindings.TryGetValue(id, out var binding) || !binding.Late || binding.Door == null ||
                    !VehicleHingePayload.Matches(payload, offset + 9, (byte)(binding.State.Open ? 1 : 0), binding.State.Angle)) return false;
                previous = id;
            }
            return true;
        }
        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            if (!host) foreach (var b in bindings.Values) SuppressReplicaPhysics(b);
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.CommandRequested += Command; channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
            channel.ReplyReceived += Reply;
            channel.CommandRejected += Rejected;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) => presence.Remove(peer);
        public bool Handles(Vehicle_Door door)
        {
            if (disposed || session?.IsRunning != true || door == null) return false;
            if (bindings.Values.Any(b => b.Door == door)) return true;
            // Discover only this exact known sale instance, without a per-click scene search.
            var vehicle = door.vehicleController;
            if (vehicle == null || !KnownLateVehicles().Any(p => p.Vehicle == vehicle)) return false;
            ScanLate(vehicle);
            if (!bindings.Values.Any(b => b.Door == door)) return false;
            // Relative toggle commands carry the expected old open state. A newly reconstructed
            // guest copy must use the latest host state before choosing that old-state byte.
            if (!host && channel?.Records.TryGetValue(LateEntity, out var late) == true)
                ApplyLate(late.Payload, true, late.Revision);
            return true;
        }
        // Host hinges always retain native physics; discovery still runs in Update and actions.
        public bool NativePhysics(Vehicle_Door door) => host || !Handles(door);
        public bool Toggle(Vehicle_Door door)
        {
            if (applying || !Handles(door)) return true;
            var b = bindings.Values.Single(v => v.Door == door);
            if (channel?.Ready == true)
            {
                if (!host)
                {
                    if (!ReplicaReady(b) || !door.isActiveAndEnabled || !door.gameObject.activeInHierarchy ||
                        !ActiveHandles(b).Any() ||
                        MonMPPlugin.Instance?.World?.SaveGate.Active == true) return false;
                    b.Intent.Toggle(b.State.Open);
                    if (b.Intent.DesiredOpen) b.Opening.Begin(b.PresentationAngle, b.Door.isDriverDoor);
                    else b.Opening.Clear();
                    SendIntent(b);
                    b.TargetAngle = PreviewAngle(b);
                    return false;
                }
                // Native FixedUpdate may latch the host door between record captures. Stamp
                // the local intent with its actual state; guests use their accepted replica.
                byte expectedOpen = (byte)((host ? door.Get_IsOpen : b.State.Open) ? 1 : 0);
                if (b.Late) channel.Request(LateEntity, BitConverter.GetBytes(b.Id).Append(expectedOpen).ToArray(), true);
                else channel.Request(b.Id, new[] { expectedOpen }, true);
            }
            return false;
        }
        private static IEnumerable<Vehicle_DoorHandle> ActiveHandles(Binding b) => b.DoorHandles.Where(h => h != null &&
            h.vehicle_Door == b.Door && h.isActiveAndEnabled && h.gameObject.activeInHierarchy);
        private void SendIntent(Binding b)
        {
            if (!b.Intent.TryBegin(++nextInteraction, b.State.Open, Time.unscaledTime)) return;
            b.RequestRevision = b.Revision;
            if (b.Intent.SentOpen && !b.Opening.Active) b.Opening.Begin(b.PresentationAngle, b.Door.isDriverDoor);
            byte expectedOpen = (byte)(b.State.Open ? 1 : 0);
            byte[] payload = b.Late ? BitConverter.GetBytes(b.Id).Append(expectedOpen).ToArray() : new[] { expectedOpen };
            channel!.Request(b.Late ? LateEntity : b.Id, new ClientInteractionRequest(b.Intent.Token, payload).Encode(), true);
        }
        [MonMP.Core.World.WorldCommandAdapters(VehicleDoorState.Adapter, SharedVehicleDoors.LateAdapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host) return;
            ulong entity = id;
            bool correlated = ClientInteractionRequest.Is(payload);
            var request = correlated ? ClientInteractionRequest.Decode(payload) : default;
            if (correlated) payload = request.Payload;
            if (host && id == LateEntity && payload.Length == 9)
            {
                // The guest's first usable handle callback can precede the host's periodic scan.
                ScanLate();
                id = BitConverter.ToUInt64(payload, 0);
                if (!bindings.TryGetValue(id, out var late) || !late.Late || late.Door == null) return;
                payload = new[] { payload[8] };
            }
            else if (id == LateEntity) return;
            if (!host || !bindings.TryGetValue(id, out var b)) return;
            if (payload.Length != 1 || payload[0] > 1) throw new InvalidDataException("Invalid door command.");
            var status = ExecuteToggle(peer, b, payload[0] == 1);
            Publish(b);
            if (correlated && peer != 0)
                channel!.Reply(peer, entity, new ClientInteractionReceipt(request.Token, status,
                    channel.Records[entity].Revision, b.State.Encode()).Encode());
        }
        private ClientInteractionStatus ExecuteToggle(uint peer, Binding b, bool observedOpen)
        {
            if (!b.Door.isActiveAndEnabled || !b.Door.gameObject.activeInHierarchy ||
                !ActiveHandles(b).Any() ||
                MonMPPlugin.Instance?.World?.SaveGate.Active == true) return ClientInteractionStatus.Refused;
            if (observedOpen != b.Door.Get_IsOpen) return ClientInteractionStatus.Stale;
            Vector3 location;
            if (peer == 0) location = gameplay.PlayerWalking.transform.position;
            else
            {
                if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2) return ClientInteractionStatus.Refused;
                location = new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z);
            }
            if (!ActiveHandles(b).Any(handle => (location - handle.transform.position).sqrMagnitude <= 16)) return ClientInteractionStatus.Refused;
            applying = true;
            try { b.Door.ToggleDoor(); }
            finally { applying = false; }
            return ClientInteractionStatus.Accepted;
        }
        private void Rejected(uint peer, ulong id, byte[] payload)
        {
            if (!host || !ClientInteractionRequest.Is(payload)) return;
            var request = ClientInteractionRequest.Decode(payload);
            ulong door = id == LateEntity && request.Payload.Length == 9 ? BitConverter.ToUInt64(request.Payload, 0) : id;
            if (!bindings.TryGetValue(door, out var b) || b.Door == null || b.Late != (id == LateEntity)) return;
            Publish(b);
            channel!.Reply(peer, id, new ClientInteractionReceipt(request.Token, ClientInteractionStatus.Stale,
                channel.Records[id].Revision, b.State.Encode()).Encode());
        }
        private void Reply(ulong id, byte[] payload)
        {
            // Replies reach every adapter: only this adapter's entities may be decoded as its receipts.
            if (host || !ClientInteractionReceipt.Is(payload) || (id != LateEntity && !bindings.ContainsKey(id))) return;
            var receipt = ClientInteractionReceipt.Decode(payload);
            Binding? b = id == LateEntity ? bindings.Values.FirstOrDefault(value => value.Late && value.Intent.Token == receipt.Token)
                : bindings.TryGetValue(id, out var found) && !found.Late ? found : null;
            if (b == null || b.Door == null || b.Intent.Token == 0 || b.Intent.Token != receipt.Token) return;
            var state = VehicleDoorState.Decode(receipt.State);
            bool accepted = receipt.Status == ClientInteractionStatus.Accepted;
            bool opening = b.Intent.SentOpen;
            bool newerAuthority = receipt.Revision < b.Revision;
            // A close can latch and another player can reopen it before this receipt arrives.
            // New moving-open captures alone still need the latch wait; an observed CLOSED
            // capture after this operation proves that wait already completed.
            bool latchAlreadyObserved = b.LastClosedRevision > b.RequestRevision && b.LastClosedRevision > receipt.Revision;
            if (receipt.Revision >= b.Revision)
            {
                b.State = state; b.Revision = receipt.Revision;
                if (!state.Open) b.LastClosedRevision = receipt.Revision;
                // A dormant chassis may still hold an earlier deferred capture. Its wake-up
                // must not apply that capture over a receipt already accepted at this revision.
                b.Deferred.Accept(state, ReplicaReady(b));
            }
            b.Intent.Acknowledge(receipt.Token, accepted, b.State.Open, Time.unscaledTime, waitForLatch: !opening && !latchAlreadyObserved);
            b.Intent.Observe(b.State.Open);
            if (!accepted || newerAuthority && !b.State.Open) b.Opening.Clear();
            SendIntent(b);
            if (accepted && opening && !newerAuthority && b.State.Open)
            {
                b.Opening.Confirm(Time.unscaledTime);
                b.Opening.Observe(true, b.State.Angle);
            }
            b.TargetAngle = PreviewAngle(b);
        }
        // Director (host): shut every open door of a vehicle, as its players would.
        internal int CloseAll(VehicleController vehicle)
        {
            if (!host || channel?.Ready != true) return 0;
            int closed = 0;
            foreach (var b in bindings.Values.Where(v => v.Door.vehicleController == vehicle && v.Door.gameObject.activeInHierarchy && v.Door.Get_IsOpen))
            {
                applying = true;
                try { b.Door.ToggleDoor(); }
                finally { applying = false; }
                Publish(b); closed++;
            }
            return closed;
        }
#if MONMP_DEVELOPMENT
        // Director host setup mirrors CloseAll: the camera's walker may be elsewhere.
        // Ordinary handle clicks keep their normal proximity and eligibility checks.
        internal int OpenAll(VehicleController vehicle)
        {
            if (!host || session?.IsHost != true || channel?.Ready != true) return 0;
            int opened = 0;
            foreach (var b in bindings.Values.Where(v => v.Door.vehicleController == vehicle &&
                v.Door.isActiveAndEnabled && v.Door.gameObject.activeInHierarchy && !v.Door.Get_IsOpen &&
                v.Handle != null && v.Handle.isActiveAndEnabled && v.Handle.gameObject.activeInHierarchy))
            {
                applying = true;
                try { b.Door.ToggleDoor(); }
                finally { applying = false; }
                Publish(b);
                if (b.Door.Get_IsOpen) opened++;
            }
            return opened;
        }
#endif
        private void Publish(Binding b)
        {
            bool open = b.Door.Get_IsOpen;
            float angle = open ? Mathf.DeltaAngle(b.Euler.y, b.Door.transform.localEulerAngles.y) : 0;
            b.State = new VehicleDoorState(open, angle);
            if (b.Late) PublishLate();
            else if (!channel!.Records.TryGetValue(b.Id, out var record) || record.Adapter != VehicleDoorState.Adapter ||
                record.PayloadSpan.Length != 5 || !VehicleHingePayload.Matches(record.PayloadSpan, 0, (byte)(open ? 1 : 0), angle))
                channel.Publish(b.Id, b.State.Encode());
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            var staged = records.Values.Where(r => r.Adapter == VehicleDoorState.Adapter)
                .Select(r => (Binding: bindings[r.Id], State: VehicleDoorState.Decode(r.Payload), r.Revision)).ToArray();
            foreach (var value in staged)
            {
                if (value.Revision < value.Binding.Revision) continue;
                value.Binding.Intent.Clear(); value.Binding.Opening.Clear();
                AcceptState(value.Binding, value.State, value.Revision, true);
            }
            if (!host && records.TryGetValue(LateEntity, out var late) && late.Adapter == LateAdapter)
            {
                foreach (var b in bindings.Values.Where(b => b.Late && b.Revision <= late.Revision))
                { b.Intent.Clear(); b.Opening.Clear(); }
                ApplyLate(late.Payload, true, late.Revision);
            }
        }
        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == VehicleDoorState.Adapter) AcceptState(bindings[record.Id], VehicleDoorState.Decode(record.Payload), record.Revision, false);
            else if (!host && record.Adapter == LateAdapter && record.Id == LateEntity) ApplyLate(record.Payload, false, record.Revision);
        }
        private void AcceptState(Binding b, VehicleDoorState state, uint revision, bool snap)
        {
            if (revision < b.Revision) return;
            b.Revision = revision;
            if (!state.Open) b.LastClosedRevision = revision;
            Apply(b, state, snap);
        }
        private void Apply(Binding b, VehicleDoorState state, bool snap)
        {
            if (!host && !b.Deferred.Accept(state, ReplicaReady(b))) return;
            // Natively ToggleDoor plays the opening and FixedUpdate the latch on the host only.
            if (!CueBusActive && !snap && state.Open != b.State.Open && b.Door.gameObject.activeInHierarchy)
            {
                var sound = b.Door.GetComponent<SOUND_Vehicle_Door>();
                if (sound != null) { if (state.Open) sound.Open(); else sound.Close(); }
            }
            b.State = state;
            b.Intent.Observe(state.Open);
            b.Opening.Observe(state.Open, state.Angle);
            SendIntent(b);
            b.TargetAngle = PreviewAngle(b);
            // Pending local motion survives older host captures; reconciliation eases back
            // to the host angle after its receipt instead of resetting an accepted click.
            if (!b.Intent.Active && !b.Opening.Active)
                snap |= VehiclePresentationSmoothing.IsLargeAngleDiscontinuity(b.PresentationAngle, state.Angle);
            if (snap && !b.Intent.Active && !b.Opening.Active) b.PresentationAngle = state.Angle;
            OpenField.SetValue(b.Door, state.Open);
            if (b.Door.gameObject.activeInHierarchy)
            {
                if (b.Door.isDriverDoor) b.Door.vehicleController.isDriverDoorClosed = !state.Open;
                else b.Door.vehicleController.isPassengerDoorClosed = !state.Open;
            }
            SuppressReplicaPhysics(b);
            PresentAngle(b, b.PresentationAngle);
        }
        private static float PreviewAngle(Binding b) => b.Intent.Active && !b.Intent.DesiredOpen ? 0f :
            b.Opening.Target(b.State.Angle, Time.unscaledTime);
        private static void AdvancePresentation(Binding b)
        {
            b.TargetAngle = PreviewAngle(b);
            float previous = b.PresentationAngle;
            b.PresentationAngle = VehiclePresentationSmoothing.Angle(b.PresentationAngle, b.TargetAngle, VehiclePresentationSmoothing.Blend);
            if (Mathf.Abs(Mathf.DeltaAngle(previous, b.PresentationAngle)) > 0.02f &&
                Mathf.Abs(Mathf.DeltaAngle(b.PresentationAngle, b.TargetAngle)) > 0.1f)
                b.SawPresentationInterpolation = true;
            PresentAngle(b, b.PresentationAngle);
        }
        private bool ReplicaReady(Binding b) => !(b.Door.vehicleController is VehicleController vehicle &&
            deferReplica?.Invoke(vehicle) == true);
        private void SuppressReplicaPhysics(Binding b)
        {
            if (host || b.OriginalBody == null || b.OriginalHinge == null) return;
            b.OriginalBody.isKinematic = true;
            b.OriginalBody.interpolation = RigidbodyInterpolation.None;
            b.OriginalHinge.autoConfigureConnectedAnchor = false;
            b.OriginalHinge.connectedBody = null;
            b.ReplicaPhysicsSuppressed = true;
        }
        private static void PresentAngle(Binding b, float angle)
        {
            b.Door.transform.localPosition = b.Position;
            b.Door.transform.localEulerAngles = new Vector3(b.Euler.x, b.Euler.y + angle, b.Euler.z);
        }
        public void Update()
        {
            if (disposed) return;
            if (session?.IsRunning == true && channel?.Ready == true && Time.unscaledTime >= nextLateScan)
            {
                nextLateScan = Time.unscaledTime + 2f;
                ScanLate();
                if (!host && channel.Records.TryGetValue(LateEntity, out var late)) ApplyLate(late.Payload, false, late.Revision);
            }
            if (!host)
            {
                foreach (var b in bindings.Values)
                {
                    if (b.Door == null) continue;
                    if (b.Intent.Expire(Time.unscaledTime)) b.Opening.Clear();
                    bool ready = ReplicaReady(b);
                    if (!ready) continue;
                    if (b.Deferred.TryTake(ready, out var pending)) Apply(b, pending, true);
                    if (b.Door.gameObject.activeInHierarchy)
                    {
                        if (b.Door.isDriverDoor) b.Door.vehicleController.isDriverDoorClosed = !b.State.Open;
                        else b.Door.vehicleController.isPassengerDoorClosed = !b.State.Open;
                    }
                    AdvancePresentation(b);
                }
                return;
            }
            if (channel?.Ready != true || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 0.05f;
            foreach (var b in bindings.Values)
            {
                if (b.Door == null) { if (b.Late) continue; throw new InvalidOperationException("Shared truck door removed; reload before rehosting."); }
                if (!b.Door.gameObject.activeInHierarchy) continue;
                Publish(b);
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.CommandRequested -= Command; channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            if (channel != null) { channel.ReplyReceived -= Reply; channel.CommandRejected -= Rejected; }
            foreach (var b in bindings.Values) { b.Intent.Clear(); b.Opening.Clear(); }
            if (!host) foreach (var b in bindings.Values) if (b.Door != null) RestoreLocalState(b);
            presence.Clear(); LateVehicles = null;
            session = null; channel = null;
        }
        private static void RestoreLocalState(Binding b)
        {
            OpenField.SetValue(b.Door, b.OriginalState.Open);
            if (b.Door.isDriverDoor) b.Door.vehicleController.isDriverDoorClosed = b.OriginalDriverDoorClosed;
            else b.Door.vehicleController.isPassengerDoorClosed = b.OriginalPassengerDoorClosed;
            b.Door.transform.localPosition = b.OriginalLocalPosition;
            b.Door.transform.localRotation = b.OriginalLocalRotation;
            if (b.OriginalHinge != null)
            {
                b.OriginalHinge.autoConfigureConnectedAnchor = false;
                b.OriginalHinge.connectedBody = b.OriginalHingeConnectedBody;
                b.OriginalHinge.connectedAnchor = b.OriginalHingeConnectedAnchor;
                b.OriginalHinge.autoConfigureConnectedAnchor = b.OriginalHingeAutoConfigureConnectedAnchor;
            }
            if (b.OriginalBody != null)
            {
                b.OriginalBody.isKinematic = b.OriginalBodyKinematic;
                b.OriginalBody.interpolation = b.OriginalBodyInterpolation;
                b.OriginalBody.velocity = b.OriginalBodyVelocity;
                b.OriginalBody.angularVelocity = b.OriginalBodyAngularVelocity;
            }
            b.ReplicaPhysicsSuppressed = false;
        }

#if MONMP_DEVELOPMENT
        internal ulong[] TestPurchasedDoorIds(ulong entity, GameObject instance)
        {
            var vehicle = instance.GetComponent<VehicleController>();
            if (vehicle == null || channel == null || !channel.Records.TryGetValue(LateEntity, out var record) ||
                record.Adapter != LateAdapter || !MatchesLateRecord(record.PayloadSpan))
                throw new InvalidOperationException("Purchased truck door aggregate is not applied.");
            var doors = vehicle.GetComponentsInChildren<Vehicle_Door>(true).Where(d => d.vehicleController == vehicle).ToArray();
            if (doors.Length != 2) throw new InvalidOperationException("Purchased truck cabin door layout changed.");
            var ids = new List<ulong>();
            foreach (var door in doors)
            {
                ulong id = LateDoorId(entity, door, vehicle);
                if (!bindings.TryGetValue(id, out var binding) || !binding.Late || binding.Door != door ||
                    binding.State.Open != door.Get_IsOpen || (!host && Mathf.Abs(binding.TargetAngle - binding.State.Angle) > 0.01f) ||
                    bindings.Values.Any(b => !b.Late && b.Door == door))
                    throw new InvalidOperationException("Purchased truck door binding/state is missing: " + id.ToString("X16"));
                ids.Add(id);
            }
            return ids.OrderBy(id => id).ToArray();
        }
        private Binding TestBinding(bool driver) => bindings.Values.Where(b => !b.Late && b.Door.isDriverDoor == driver && b.Door.vehicleController.vehicle_type == Vehicle_Type.OlTruck)
            .OrderByDescending(b => b.Door.gameObject.activeInHierarchy).ThenBy(b => b.Id).First();


        internal void TestLateOpenRegistration()
        {
            // Isolated native door: open it before ScanLate, exactly as a newly purchased truck can
            // be opened before discovery. No full vehicle clone or real purchase/save is needed.
            var source = TestBinding(true).Door;
            var priorProvider = LateVehicles;
            bool priorCueFallback = isolatedCueFallback;
            isolatedCueFallback = true;
            bool driverClosed = source.vehicleController.isDriverDoorClosed;
            bool passengerClosed = source.vehicleController.isPassengerDoorClosed;
            GameObject? clone = null;
            Binding? probe = null;
            void Require(bool condition, string message)
            { if (!condition) throw new InvalidOperationException("Late truck door #333: " + message); }
            void StopProbeSounds()
            {
                if (clone == null) return;
                foreach (var audio in clone.GetComponentsInChildren<AudioSource>(true))
                {
                    audio.Stop();
                    audio.transform.SetParent(Singleton<SOUND_Manager>.i.transform);
                }
            }
            bool Playing(AudioClip clip) => clone != null && clone.GetComponentsInChildren<AudioSource>(true)
                .Any(audio => audio.clip == clip && audio.isPlaying);
            try
            {
                clone = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent);
                clone.name = "MonMP_LateDoorProbe";
                var door = clone.GetComponent<Vehicle_Door>();
                foreach (var joint in clone.GetComponents<Joint>()) UnityEngine.Object.DestroyImmediate(joint);
                if (clone.GetComponent<Rigidbody>() is { } clonedBody) UnityEngine.Object.DestroyImmediate(clonedBody);
                OpenField.SetValue(door, false);
                var baseline = (Vector3)OriginalEulerField.GetValue(source);
                // Native door cues are distance-culled; keep the isolated clone near the observer.
                var originalPosition = source.transform.parent.InverseTransformPoint(gameplay.Player.transform.position + Vector3.up);
                OriginalEulerField.SetValue(door, baseline); OriginalPositionField.SetValue(door, originalPosition);
                door.transform.localEulerAngles = baseline; door.transform.localPosition = originalPosition;
                // Start has not run for a clone created during this diagnostic Update.
                AccessTools.Method(typeof(Vehicle_Door), "Start").Invoke(door, null);
                door.ToggleDoor();
                Require(door.Get_IsOpen && Playing(Singleton<SOUND_Manager>.i.Vehicle_Door_Open),
                    "Native unregistered open did not create its hinge and play its open cue.");
                var originalBody = door.GetComponent<Rigidbody>();
                var originalHinge = door.GetComponent<HingeJoint>();
                door.transform.localEulerAngles = baseline + Vector3.up * 12f;
                LateVehicles = () => new[] { (WorldRecord.DynamicIdStart + 333UL, source.vehicleController) };
                ScanLate();
                probe = bindings.Values.SingleOrDefault(b => b.Door == door);
                Require(probe != null && probe.Late && probe.State.Open && Mathf.Abs(probe.State.Angle - 12f) < 0.1f,
                    "Open native door was skipped or its closed baseline became the open angle.");
                Require(door.GetComponent<Rigidbody>() == originalBody && door.GetComponent<HingeJoint>() == originalHinge &&
                    (host ? !probe!.ReplicaPhysicsSuppressed && originalHinge.connectedBody == source.vehicleController.vehicleRigidbody :
                        probe!.ReplicaPhysicsSuppressed && originalBody.isKinematic && originalHinge.connectedBody == null),
                    "Discovery replaced the native hinge or retained replica physics.");
                StopProbeSounds();
                Apply(probe!, new VehicleDoorState(false, 0), true);
                Require(!Playing(Singleton<SOUND_Manager>.i.Vehicle_Door_Close), "Snapshot replayed a close cue.");
                Apply(probe!, new VehicleDoorState(true, 12), false);
                Require(Playing(Singleton<SOUND_Manager>.i.Vehicle_Door_Open), "Live reopen lost its native open cue.");
                StopProbeSounds();
                Apply(probe!, new VehicleDoorState(false, 0), false);
                Require(Playing(Singleton<SOUND_Manager>.i.Vehicle_Door_Close), "Live close lost its native latch cue.");
                RestoreLocalState(probe!);
                Require(door.Get_IsOpen && door.GetComponent<Rigidbody>() == originalBody &&
                    door.GetComponent<HingeJoint>() == originalHinge && originalBody.isKinematic == probe!.OriginalBodyKinematic &&
                    originalBody.interpolation == probe.OriginalBodyInterpolation &&
                    Vector3.Distance(originalBody.velocity, probe.OriginalBodyVelocity) < 0.01f &&
                    Vector3.Distance(originalBody.angularVelocity, probe.OriginalBodyAngularVelocity) < 0.01f &&
                    originalHinge.connectedBody == probe.OriginalHingeConnectedBody &&
                    originalHinge.autoConfigureConnectedAnchor == probe.OriginalHingeAutoConfigureConnectedAnchor &&
                    Vector3.Distance(originalHinge.connectedAnchor, probe.OriginalHingeConnectedAnchor) < 0.001f &&
                    Vector3.Distance(door.transform.localPosition, probe.OriginalLocalPosition) < 0.001f &&
                    Quaternion.Angle(door.transform.localRotation, probe.OriginalLocalRotation) < 0.1f,
                    "Open native door did not restore its exact original hinge/body and pose.");
                Debug.Log("MonMP truck doors #333: native open-before-discovery registered; host hinge/replica suppression, snapshot silence, live cues and original restore passed.");
            }
            finally
            {
                LateVehicles = priorProvider;
                isolatedCueFallback = priorCueFallback;
                if (probe != null) bindings.Remove(probe.Id);
                StopProbeSounds();
                if (clone != null) { clone.SetActive(false); UnityEngine.Object.Destroy(clone); }
                source.vehicleController.isDriverDoorClosed = driverClosed;
                source.vehicleController.isPassengerDoorClosed = passengerClosed;
            }
        }


        internal void TestClick(bool driver = true) => TestBinding(driver).Handle!.MouseDown();


        internal VehicleDoorState TestState(bool driver = true) => TestBinding(driver).State;


        internal VehicleDoorState TestNativeTruckState(bool driver = true) => NativeState(TestBinding(driver));


        internal bool TestSawPresentationInterpolation => TestBinding(true).SawPresentationInterpolation;


        internal bool TestPassengerSawPresentationInterpolation => TestBinding(false).SawPresentationInterpolation;


        internal void TestResetPresentationObservation() => TestBinding(true).SawPresentationInterpolation = false;


        internal void TestResetPresentationObservation(bool driver) => TestBinding(driver).SawPresentationInterpolation = false;


        internal Vector3 TestPosition => TestBinding(true).Handle!.transform.position;


        internal Vector3 TestPositionFor(bool driver) => TestBinding(driver).Handle!.transform.position;
#endif

        // The starting Konig's own driver/passenger doors, distinct from the stock truck cabin above.

#if MONMP_DEVELOPMENT
        private Binding TestKonigBinding(bool driver) => bindings.Values.Single(b => b.Door.isDriverDoor == driver && b.Door.vehicleController == konig);


        internal void TestKonigClick(bool driver = true) => TestKonigBinding(driver).Handle!.MouseDown();


        internal Vector3 TestKonigPosition(bool driver = true) => TestKonigBinding(driver).Handle!.transform.position;


        internal VehicleDoorState TestKonigState(bool driver = true) => TestKonigBinding(driver).State;


        internal bool TestKonigSawPresentationInterpolation => TestKonigBinding(true).SawPresentationInterpolation;


        internal void TestResetKonigPresentationObservation() => TestKonigBinding(true).SawPresentationInterpolation = false;


        internal VehicleDoorState TestKonigNative(bool driver = true)
        {
            var b = TestKonigBinding(driver);
            return new VehicleDoorState(b.Door.Get_IsOpen, b.Door.Get_IsOpen ? Mathf.DeltaAngle(b.Euler.y, b.Door.transform.localEulerAngles.y) : 0);
        }


        internal bool TestSynchronized => bindings.Values.Where(b => b.Door != null).All(b => b.Door.Get_IsOpen == b.State.Open &&
            Mathf.Abs(Mathf.DeltaAngle(b.Euler.y + b.State.Angle, b.Door.transform.localEulerAngles.y)) < 0.1f &&
            (host || b.Door.GetComponent<Rigidbody>() == null || b.OriginalBody != null &&
                b.Door.GetComponent<Rigidbody>() == b.OriginalBody && b.ReplicaPhysicsSuppressed && b.OriginalBody.isKinematic &&
                b.OriginalHinge?.connectedBody == null));


        internal bool TestRestored => host || bindings.Values.Where(b => b.Door != null).All(b => b.Door.Get_IsOpen == b.OriginalState.Open &&
            Vector3.Distance(b.Door.transform.localPosition, b.OriginalLocalPosition) < 0.001f &&
            Quaternion.Angle(b.Door.transform.localRotation, b.OriginalLocalRotation) < 0.1f &&
            b.Door.GetComponent<Rigidbody>() == b.OriginalBody && b.Door.GetComponent<HingeJoint>() == b.OriginalHinge &&
            (b.OriginalBody == null || b.OriginalBody.isKinematic == b.OriginalBodyKinematic &&
                b.OriginalBody.interpolation == b.OriginalBodyInterpolation &&
                Vector3.Distance(b.OriginalBody.velocity, b.OriginalBodyVelocity) < 0.01f &&
                Vector3.Distance(b.OriginalBody.angularVelocity, b.OriginalBodyAngularVelocity) < 0.01f) &&
            (b.OriginalHinge == null || b.OriginalHinge.connectedBody == b.OriginalHingeConnectedBody &&
                b.OriginalHinge.autoConfigureConnectedAnchor == b.OriginalHingeAutoConfigureConnectedAnchor &&
                Vector3.Distance(b.OriginalHinge.connectedAnchor, b.OriginalHingeConnectedAnchor) < 0.001f));
#endif

    }
}
