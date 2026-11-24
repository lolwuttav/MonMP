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

namespace MonMP.Plugin.Replication
{
    // Native trunk/tailgate hinge physics simulate on the host only, exactly like the truck cabin
    // doors: Release() is a single self-toggling action (open when closed, close when open).
    internal sealed partial class SharedVehicleTrunk : IDisposable
    {
        private sealed class Binding
        {
            public ulong Id;
            public Vehicle_Trunk Trunk = null!;
            public Vehicle_Trunk_Button? Button;
            public Vehicle_Trunk_Button[] Handles = Array.Empty<Vehicle_Trunk_Button>();
            public Vehicle_Type VehicleType;
            public bool Host, ReplicaPhysicsDisabled;
            public Vector3 Position, Euler;
            public VehicleTrunkState State;
            public float TargetAngle, PresentationAngle;
            public bool SawPresentationInterpolation;
            public bool Static;
            public bool Late;
            public Func<bool>? DeferReplica;
            public NativeReplicaHingeBaseline Original = null!;
            public readonly DeferredReplicaHingeState<VehicleTrunkState> Deferred = new DeferredReplicaHingeState<VehicleTrunkState>();
            public readonly DoorPresentationIntent Intent = new DoorPresentationIntent();
            public uint LocalRevision;
            public uint LastClosedRevision;
            public float PreviewHandoffUntil;
            public bool ClosingInFlight;
            public float ClosingUntil;
        }
        // Gates on trailers bought during the session (the aluminium trailer's tailgate) exist only after
        // the catalog is made, so they were never shared: the gate opened only for whoever clicked it.
        // Such late gates share one record listing each gate's state, keyed by the trailer's shared id.
        public const ushort LateAdapter = 132;
        private static readonly ulong LateEntity = StaticId.Hash("shared-late-trunks/v1");
        internal SharedTrailers? Trailers { get; set; }
        private float nextLateScan;
        private static readonly FieldInfo IsOpenField = AccessTools.Field(typeof(Vehicle_Trunk), "isOpen");
        private static readonly FieldInfo OriginalPositionField = AccessTools.Field(typeof(Vehicle_Trunk), "originalLocalPosition");
        private static readonly FieldInfo OriginalEulerField = AccessTools.Field(typeof(Vehicle_Trunk), "originalLocalEuler");
        private static readonly FieldInfo CloseButtonField = AccessTools.Field(typeof(Vehicle_Trunk), "Collider_CloseTrunkButton");
        private static readonly FieldInfo WoodChestField = AccessTools.Field(typeof(Vehicle_Trunk), "isWoodChest");
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Vehicle_Trunk, Binding> byTrunk = new Dictionary<Vehicle_Trunk, Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Gameplay gameplay;
        private readonly bool host;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, disposed;
        private float nextCapture;
        public int Count => bindings.Count;
        public SharedVehicleTrunk(Gameplay gameplay, bool host, IEnumerable<VehicleController> vehicles, string diagnostics, NativeEntityRegistry identities,
            Func<VehicleController, bool>? deferReplica = null)
        {
            this.gameplay = gameplay; this.host = host;
            var lines = new List<string>();
            foreach (var vehicle in vehicles)
            {
                // Saved purchases use the trailer authority's sale-scoped late record too.
                if (SharedVehicleSales.Active?.OwnsSaleClone(vehicle.transform) == true) continue;
                // Vehicle_Trunk is also used for unrelated hinged lids (e.g. the Konig's glovebox), so the
                // parts manager's own authoritative reference is used instead of a blind hierarchy search.
                var parts = vehicle.GetComponent<Vehicle_PartsManager>();
                var trunk = SharedVehicles.PrimaryTrunk(vehicle);
                if (trunk == null) continue;
                bool konigSaveBacked = SharedVehicleHood.IsBuildableCar(vehicle) &&
                    parts?.Door_Trunk?.isInstalled == true && parts.Door_Trunk.partInstalled != null;
                bool restoredOpenKonig = konigSaveBacked && IsRestoredOpenKonigTrunk(vehicle, trunk, out _, out _);
                // Registered open or closed: the world catalog must not depend on whether the host has a
                // lid open when it starts hosting, or every joining guest is refused (#231).
                bool open = trunk.Get_IsOpen || restoredOpenKonig;
                if (!open && (trunk.GetComponent<Rigidbody>() != null || trunk.GetComponent<Joint>() != null)) continue;
                var handles = vehicle.GetComponentsInChildren<Vehicle_Trunk_Button>(true).Where(btn => btn.vehicle_Trunk == trunk).ToArray();
                var button = handles.FirstOrDefault();
                string identity = RuntimeProbe.CanonicalPath(trunk.transform) + "/trunk-v1";
                if (konigSaveBacked && (OriginalPositionField == null || OriginalEulerField == null))
                    throw new InvalidOperationException("Native Konig trunk prefab baseline fields are unavailable.");
                var b = new Binding { Id = StaticId.Hash(identity), Trunk = trunk, Button = button, Handles = handles, VehicleType = vehicle.vehicle_type, Host = host,
                    Position = konigSaveBacked || open ? ClosedPosition(trunk) : trunk.transform.localPosition,
                    Euler = konigSaveBacked || open ? ClosedEuler(trunk) : trunk.transform.localEulerAngles,
                    DeferReplica = deferReplica == null ? null : () => deferReplica(vehicle) };
                b.Original = new NativeReplicaHingeBaseline(trunk, CloseButtonField.GetValue(trunk) as GameObject);
                b.State = NativeState(b); b.TargetAngle = b.PresentationAngle = b.State.Angle;
                if (!host && open && b.DeferReplica?.Invoke() != true) SetReplicaPhysics(b, true);
                identities.Bind(b.Id, VehicleTrunkState.Adapter, trunk.gameObject, identity, true,
                    konigSaveBacked ? EntityPersistence.NativeSave : EntityPersistence.SessionOnly);
                bindings.Add(b.Id, b); byTrunk[trunk] = b; lines.Add(b.Id.ToString("X16") + "\t" + identity + "\tbutton=" + (button != null));
            }
            // The same hinge drives scene lids outside vehicles: the home trash/recycle bins and wood
            // chests. They follow the host exactly like a trunk.
            var buttons = UnityEngine.Object.FindObjectsOfType<Vehicle_Trunk_Button>(true).Where(btn => btn.vehicle_Trunk != null)
                .GroupBy(btn => btn.vehicle_Trunk).ToDictionary(g => g.Key, g => g.ToArray());
            foreach (var trunk in UnityEngine.Object.FindObjectsOfType<Vehicle_Trunk>(true))
            {
                if (byTrunk.ContainsKey(trunk) || !trunk.gameObject.scene.IsValid() || OwnedByVehicleOrItem(trunk.transform)) continue;
                bool open = trunk.Get_IsOpen;
                if (!open && (trunk.GetComponent<Rigidbody>() != null || trunk.GetComponent<Joint>() != null)) continue;
                string identity = SharedBuildingDoors.StablePath(trunk.transform) + "/static-trunk-v1";
                ulong id = StaticId.Hash(identity);
                if (bindings.ContainsKey(id)) continue;
                buttons.TryGetValue(trunk, out var handles);
                var button = handles?.FirstOrDefault();
                var b = new Binding { Id = id, Trunk = trunk, Button = button, Handles = handles ?? Array.Empty<Vehicle_Trunk_Button>(), VehicleType = default, Host = host, Static = true,
                    Position = open ? ClosedPosition(trunk) : trunk.transform.localPosition, Euler = open ? ClosedEuler(trunk) : trunk.transform.localEulerAngles };
                b.Original = new NativeReplicaHingeBaseline(trunk, CloseButtonField.GetValue(trunk) as GameObject);
                b.State = NativeState(b); b.TargetAngle = b.PresentationAngle = b.State.Angle;
                if (!host && open) SetReplicaPhysics(b, true);
                identities.Bind(b.Id, VehicleTrunkState.Adapter, trunk.gameObject, identity, true, EntityPersistence.SessionOnly);
                bindings.Add(b.Id, b); byTrunk[trunk] = b; lines.Add(b.Id.ToString("X16") + "\t" + identity + "\tbutton=" + (button != null) + "\tstatic");
            }

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-trunks.tsv"), new[] { "id\ttrunk-identity\tbutton" }.Concat(lines.OrderBy(s => s)));
#endif

        }
        // The native closed pose, recorded in Vehicle_Trunk.Awake before any opening.
        private static Vector3 ClosedPosition(Vehicle_Trunk trunk) =>
            OriginalPositionField?.GetValue(trunk) is Vector3 closed ? closed : trunk.transform.localPosition;
        private static Vector3 ClosedEuler(Vehicle_Trunk trunk) =>
            OriginalEulerField?.GetValue(trunk) is Vector3 closed ? closed : trunk.transform.localEulerAngles;
        private static bool OwnedByVehicleOrItem(Transform transform)
        {
            for (var current = transform; current != null; current = current.parent)
                if (current.name.Contains("(Clone)") || current.GetComponent<VehicleController>() != null ||
                    current.GetComponent<InteractableItems>() != null) return true;
            return false;
        }
        internal static bool HasSupportedKonigBodyConfiguration(VehicleController vehicle)
        {
            if (!SharedVehicleHood.IsBuildableCar(vehicle) || vehicle.vehicleRigidbody == null) return false;
            var allowedBodies = new HashSet<Rigidbody> { vehicle.vehicleRigidbody };
            var parts = vehicle.GetComponent<Vehicle_PartsManager>();
            var trunk = parts?.Get_Vehicle_Trunk;
            if (trunk != null && (trunk.Get_IsOpen || trunk.GetComponent<Rigidbody>() != null || trunk.GetComponent<Joint>() != null))
            {
                if (!IsRestoredOpenKonigTrunk(vehicle, null, out _, out var trunkBody) || trunkBody == null ||
                    trunkBody == vehicle.vehicleRigidbody) return false;
                allowedBodies.Add(trunkBody);
            }
            var hood = parts?.Get_Vehicle_Hood;
            if (hood != null && (hood.Get_IsOpen || hood.GetComponent<Rigidbody>() != null || hood.GetComponent<Joint>() != null))
            {
                if (!SharedVehicleHood.TryGetRestoredOpenKonigHoodBody(vehicle, out var hoodBody) || hoodBody == null ||
                    hoodBody == vehicle.vehicleRigidbody || allowedBodies.Contains(hoodBody)) return false;
                allowedBodies.Add(hoodBody);
            }
            // A door saved open reloads with a native body hinged to the chassis (as on the truck).
            foreach (var door in vehicle.GetComponentsInChildren<Vehicle_Door>(true))
                if (SharedVehicleDoors.IsRestoredOpenDoor(door, vehicle, out var doorBody)) allowedBodies.Add(doorBody!);
            if (!SharedVehicles.TryAddSupportedGloveboxBody(vehicle, allowedBodies)) return false;
            var bodies = SharedVehicles.VehicleBodies(vehicle);
            return bodies.Length == allowedBodies.Count && bodies.All(allowedBodies.Contains);
        }
        private static bool IsRestoredOpenKonigTrunk(VehicleController vehicle, Vehicle_Trunk? expected,
            out Vehicle_Trunk? trunk, out Rigidbody? trunkBody)
        {
            trunk = vehicle.GetComponent<Vehicle_PartsManager>()?.Get_Vehicle_Trunk;
            trunkBody = trunk?.GetComponent<Rigidbody>();
            var hinge = trunk?.GetComponent<HingeJoint>();
            var joints = trunk == null ? Array.Empty<Joint>() : trunk.GetComponents<Joint>();
            return SharedVehicleHood.IsBuildableCar(vehicle) && trunk != null && (expected == null || trunk == expected) &&
                vehicle.GetComponent<Vehicle_PartsManager>()?.Door_Trunk?.isInstalled == true &&
                vehicle.GetComponent<Vehicle_PartsManager>()?.Door_Trunk?.partInstalled != null && trunk.Get_IsOpen &&
                trunkBody != null && trunk.GetComponents<Rigidbody>().Length == 1 && joints.Length == 1 && hinge != null &&
                joints[0] == hinge && hinge.connectedBody == vehicle.vehicleRigidbody;
        }
        private static VehicleTrunkState NativeState(Binding b) => VehicleTrunkState.FromLocalEulerAngles(
            b.Trunk.Get_IsOpen, b.Euler.x, b.Trunk.transform.localEulerAngles.x);
        private static void SetReplicaPhysics(Binding b, bool open)
        {
            b.Original.SuppressPhysics();
            b.ReplicaPhysicsDisabled = true;
        }
        public IEnumerable<WorldRecord> InitialRecords()
        {
            ScanLate();
            return bindings.Values.Where(b => !b.Late).Select(b => new WorldRecord(b.Id, 1, VehicleTrunkState.Adapter, b.State.Encode()))
                .Append(new WorldRecord(LateEntity, 1, LateAdapter, EncodeLate()));
        }
        internal void RefreshPurchasedBindings()
        {
            if (disposed) return;
            ScanLate();
            if (!host && channel != null && channel.Records.TryGetValue(LateEntity, out var record)) ApplyLate(record.Payload, false, record.Revision);
        }

        private byte[] EncodeLate()
        {
            var late = bindings.Values.Where(b => b.Late && b.Trunk != null).OrderBy(b => b.Id).ToList();
            using var stream = new MemoryStream();
            using (var w = new BinaryWriter(stream))
            {
                w.Write((ushort)late.Count);
                foreach (var b in late) { w.Write(b.Id); w.Write(b.State.Encode()); }
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
                var state = VehicleTrunkState.Decode(r.ReadBytes(5));
                if (bindings.TryGetValue(id, out var b) && b.Late && b.Trunk != null)
                {
                    if (!b.Host && revision != 0 && revision < b.LocalRevision) continue;
                    if (snap) { b.Intent.Clear(); b.PreviewHandoffUntil = 0; }
                    Apply(b, state, snap || b.LocalRevision == 0 && !b.Intent.Active, revision);
                }
            }
        }
        // Trailer gates not bound at the start, found under trailers with a shared id.
        private void ScanLate()
        {
            if (Trailers == null) return;
            var controllers = Trailers.BoundControllers.Where(controller => controller != null).ToList();
            foreach (var stale in bindings.Values.Where(b => b.Late && (b.Trunk == null ||
                !controllers.Any(controller => b.Trunk.transform.IsChildOf(controller.transform)))).ToArray())
            {
                if (!host && stale.Trunk != null) stale.Original.Restore();
                bindings.Remove(stale.Id);
                if (stale.Trunk is { } staleTrunk) byTrunk.Remove(staleTrunk);
            }
            foreach (var controller in controllers)
            {
                if (controller == null) continue;
                ulong trailer = Trailers.IdOf(controller);
                if (trailer == 0) continue;
                foreach (var trunk in controller.GetComponentsInChildren<Vehicle_Trunk>(true))
                {
                    if (trunk == null || byTrunk.ContainsKey(trunk)) continue;
                    bool open = trunk.Get_IsOpen;
                    if (!open && (trunk.GetComponent<Rigidbody>() != null || trunk.GetComponent<Joint>() != null)) continue;
                    ulong id = LateTrunkId(trailer, trunk, controller);
                    if (bindings.ContainsKey(id)) continue;
                    var handles = controller.GetComponentsInChildren<Vehicle_Trunk_Button>(true).Where(btn => btn.vehicle_Trunk == trunk).ToArray();
                    var button = handles.FirstOrDefault();
                    var b = new Binding { Id = id, Trunk = trunk, Button = button, Handles = handles, VehicleType = controller.vehicle_type, Host = host, Late = true,
                        Position = open ? ClosedPosition(trunk) : trunk.transform.localPosition, Euler = open ? ClosedEuler(trunk) : trunk.transform.localEulerAngles };
                    b.Original = new NativeReplicaHingeBaseline(trunk, CloseButtonField.GetValue(trunk) as GameObject);
                    b.State = NativeState(b); b.TargetAngle = b.PresentationAngle = b.State.Angle;
                    if (!host && open) SetReplicaPhysics(b, true);
                    bindings.Add(id, b); byTrunk[trunk] = b;
                }
            }
        }
        private static ulong LateTrunkId(ulong trailer, Vehicle_Trunk trunk, VehicleController controller)
        {
            string path = trunk.name;
            for (var t = trunk.transform.parent; t != null && t != controller.transform; t = t.parent) path = t.name + "/" + path;
            return StaticId.Hash(trailer.ToString("X16") + "/" + path + "/late-trunk-v1");
        }
        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.CommandRequested += Command; channel.SnapshotApplied += Snapshot; channel.Changed += Changed;
            channel.CommandRejected += Rejected; channel.ReplyReceived += Reply;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer)
        {
            presence.Remove(peer);
            foreach (var key in trunkDecisions.Keys.Where(k => k.Peer == peer).ToArray()) trunkDecisions.Remove(key);
        }
        // Called from every trunk's FixedUpdate, so a dictionary rather than a scan.
        private Binding? Find(Vehicle_Trunk trunk) => trunk != null && byTrunk.TryGetValue(trunk, out var b) ? b : null;
        public bool Handles(Vehicle_Trunk trunk) => !disposed && session?.IsRunning == true && Find(trunk) != null;
        public bool NativePhysics(Vehicle_Trunk trunk) => host || !Handles(trunk);
        public bool Toggle(Vehicle_Trunk trunk)
        {
            if (applying) return true;
            var b = Find(trunk);
            if (b == null) return true;
            if (channel?.Ready == true)
            {
                if (!host)
                {
                    if (!channel.FullySynchronized || b.DeferReplica?.Invoke() == true || !b.Trunk.gameObject.activeInHierarchy ||
                        MonMPPlugin.Instance?.World?.SaveGate.Active == true) return false;
                    b.Intent.Toggle(b.State.Open);
                    PresentLocalIntent(b);
                    SendIntent(b);
                }
                else if (b.Late) channel.Request(LateEntity, BitConverter.GetBytes(b.Id), true);
                else channel.Request(b.Id, Array.Empty<byte>(), true);
            }
            return false;
        }
        [MonMP.Core.World.WorldCommandAdapters(VehicleTrunkState.Adapter, SharedVehicleTrunk.LateAdapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (peer != 0 && ClientInteractionRequest.Is(payload)) { ClientCommand(peer, id, payload, false); return; }
            if (host && id == LateEntity && payload.Length == 8)
            {
                id = BitConverter.ToUInt64(payload, 0);
                if (!bindings.TryGetValue(id, out var late) || !late.Late) return;
                payload = Array.Empty<byte>();
            }
            else if (id == LateEntity) return;
            if (!host || !bindings.TryGetValue(id, out var b) || payload.Length != 0 || (b.Button == null && !b.Static)) return;
            Vector3 location;
            if (peer == 0) location = gameplay.PlayerWalking.transform.position;
            else
            {
                if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2) return;
                location = new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z);
            }
            if (MonMPPlugin.Instance?.World?.SaveGate.Active == true || !InHandleRange(b, location)) return;
            bool wasOpen = b.Trunk.Get_IsOpen;
            bool wasLockedOpen = (bool)LockedOpenField.GetValue(b.Trunk);
            applying = true;
            try { b.Trunk.Release(); }
            finally { applying = false; }
            b.ClosingInFlight = wasOpen && wasLockedOpen && b.Trunk.Get_IsOpen;
            if (b.ClosingInFlight) b.ClosingUntil = Time.unscaledTime + (float)DoorPresentationIntent.TimeoutSeconds;
            Publish(b);
        }
        private void Publish(Binding b)
        {
            b.State = NativeState(b);
            if (b.Late) PublishLate();
            else if (!channel!.Records.TryGetValue(b.Id, out var record) || record.Adapter != VehicleTrunkState.Adapter ||
                record.PayloadSpan.Length != 5 || !VehicleHingePayload.Matches(record.PayloadSpan, 0, (byte)(b.State.Open ? 1 : 0), b.State.Angle))
                channel.Publish(b.Id, b.State.Encode());
        }
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
            foreach (var binding in bindings.Values) if (binding.Late && binding.Trunk != null) live++;
            if (count != live || payload.Length != 2 + count * 13) return false;
            ulong previous = 0;
            for (int i = 0, offset = 2; i < count; i++, offset += 13)
            {
                ulong id = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(offset, 8));
                if (i != 0 && id <= previous || !bindings.TryGetValue(id, out var binding) ||
                    !binding.Late || binding.Trunk == null ||
                    !VehicleHingePayload.Matches(payload, offset + 8, (byte)(binding.State.Open ? 1 : 0), binding.State.Angle)) return false;
                previous = id;
            }
            return true;
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            var staged = records.Values.Where(r => r.Adapter == VehicleTrunkState.Adapter)
                .Select(r => (Binding: bindings[r.Id], State: VehicleTrunkState.Decode(r.Payload))).ToArray();
            foreach (var value in staged)
            {
                value.Binding.Intent.Clear(); value.Binding.PreviewHandoffUntil = 0;
                Apply(value.Binding, value.State, true, records[value.Binding.Id].Revision);
            }
            if (!host && records.TryGetValue(LateEntity, out var late) && late.Adapter == LateAdapter) ApplyLate(late.Payload, true, late.Revision);
        }
        private void Changed(WorldRecord record)
        {
            if (!host && record.Adapter == VehicleTrunkState.Adapter) Apply(bindings[record.Id], VehicleTrunkState.Decode(record.Payload), false, record.Revision);
            else if (!host && record.Adapter == LateAdapter && record.Id == LateEntity) ApplyLate(record.Payload, false, record.Revision);
        }
        private static void Apply(Binding b, VehicleTrunkState state, bool snap, uint revision = 0)
        {
            if (!b.Host && revision != 0 && revision < b.LocalRevision) return;
            if (revision != 0)
            {
                b.LocalRevision = revision;
                if (!state.Open) b.LastClosedRevision = Math.Max(b.LastClosedRevision, revision);
            }
            if (!b.Host && !b.Deferred.Accept(state, b.DeferReplica?.Invoke() != true)) return;
            snap |= !b.Intent.Active && VehiclePresentationSmoothing.IsLargeAngleDiscontinuity(b.PresentationAngle, state.Angle);
            if (!snap && Mathf.Abs(Mathf.DeltaAngle(b.PresentationAngle, state.Angle)) > 0.1f)
                b.SawPresentationInterpolation = true;
            bool opened = state.Open != b.State.Open;
            b.State = state;
            if (!b.Host) b.Intent.Observe(state.Open);
            b.TargetAngle = state.Angle;
            if (snap) b.PresentationAngle = state.Angle;
            IsOpenField.SetValue(b.Trunk, state.Open);
            if (!b.Host)
            {
                // The native close collider is what the player clicks to shut an open lid.
                if (CloseButtonField?.GetValue(b.Trunk) is GameObject closeButton && closeButton != null) closeButton.SetActive(state.Open);
                if (opened && !snap) PlayHingeSound(b, state.Open);
            }
            if (!b.Host) SetReplicaPhysics(b, state.Open);
            PresentAngle(b, b.PresentationAngle);
            if (!b.Host) PresentLocalIntent(b);
        }
        private static void PlayHingeSound(Binding b, bool open)
        {
            var sound = b.Trunk.GetComponent<SOUND_Vehicle_Hood>();
            if (sound == null) return;
            bool wood = WoodChestField != null && (bool)WoodChestField.GetValue(b.Trunk);
            if (open) { if (wood) sound.OpenWood(); else sound.Open(); }
            else { if (wood) sound.CloseWood(); else sound.Close(); }
        }
        private static void PresentAngle(Binding b, float angle)
        {
            b.Trunk.transform.localPosition = b.Position;
            b.Trunk.transform.localEulerAngles = new Vector3(b.Euler.x + angle, b.Euler.y, b.Euler.z);
        }
        public void Update()
        {
            if (disposed) return;
            if (session?.IsRunning == true && channel?.Ready == true && Time.unscaledTime >= nextLateScan)
            {
                nextLateScan = Time.unscaledTime + 2f;
                ScanLate();
                // A gate bound after the late record arrived.
                if (!host && channel.Records.TryGetValue(LateEntity, out var late)) ApplyLate(late.Payload, false, late.Revision);
            }
            if (!host)
            {
                foreach (var b in bindings.Values)
                {
                    if (b.Trunk == null) continue;
                    bool ready = b.DeferReplica?.Invoke() != true;
                    if (!ready || !b.Trunk.gameObject.activeInHierarchy)
                    { b.Intent.Clear(); b.PreviewHandoffUntil = 0; continue; }
                    if (b.Deferred.TryTake(ready, out var pending)) Apply(b, pending, true);
                    if (b.Intent.Expire(Time.unscaledTime)) b.PreviewHandoffUntil = 0;
                    SendIntent(b);
                    float target = b.Intent.Active ? (b.Intent.DesiredOpen ? OpenPreviewAngle(b) : 0) : b.TargetAngle;
                    if (!b.Intent.Active && b.State.Open && Time.unscaledTime < b.PreviewHandoffUntil &&
                        Math.Abs(target) < Math.Abs(b.PresentationAngle)) target = b.PresentationAngle;
                    b.PresentationAngle = VehiclePresentationSmoothing.Angle(b.PresentationAngle, target, VehiclePresentationSmoothing.Blend);
                    PresentAngle(b, b.PresentationAngle);
                    PresentLocalIntent(b);
                }
                return;
            }
            if (channel?.Ready != true || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 0.05f;
            foreach (var b in bindings.Values)
            {
                if (b.Late) continue;
                if (b.Trunk == null) throw new InvalidOperationException("Shared vehicle trunk removed; reload before rehosting.");
                if (!b.Trunk.gameObject.activeInHierarchy) continue;
                Publish(b);
            }
            foreach (var b in bindings.Values) if (b.Late && b.Trunk != null) b.State = NativeState(b);
            PublishLate();
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.CommandRequested -= Command; channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRejected -= Rejected; channel.ReplyReceived -= Reply; }
            if (!host) foreach (var b in bindings.Values) { b.Intent.Clear(); b.PreviewHandoffUntil = 0; b.Original.Restore(); }
            trunkDecisions.Clear();
            presence.Clear(); Trailers = null;
            session = null; channel = null;
        }
        // The starting Konig's own trunk, distinct from any other tracked vehicle's tailgate.

#if MONMP_DEVELOPMENT
        internal ulong[] TestPurchasedTrunkIds(ulong entity, GameObject instance)
        {
            var vehicle = instance.GetComponent<VehicleController>();
            ulong trailer = vehicle == null ? 0 : Trailers?.IdOf(vehicle) ?? 0;
            if (vehicle == null || trailer == 0 || channel == null || !channel.Records.TryGetValue(LateEntity, out var record) ||
                record.Adapter != LateAdapter || !MatchesLateRecord(record.PayloadSpan) ||
                SharedVehicleSales.Active?.PurchasedVehicles().Any(p => p.Entity == entity && p.Instance == instance) != true)
                throw new InvalidOperationException("Purchased truck tailgate aggregate is not applied.");
            var trunk = vehicle.GetComponent<Vehicle_PartsManager>()?.Get_Vehicle_Trunk;
            if (trunk == null) throw new InvalidOperationException("Purchased truck native tailgate is missing.");
            ulong id = LateTrunkId(trailer, trunk, vehicle);
            if (!bindings.TryGetValue(id, out var binding) || !binding.Late || binding.Trunk != trunk ||
                !byTrunk.TryGetValue(trunk, out var direct) || direct != binding || binding.State.Open != trunk.Get_IsOpen ||
                (!host && Mathf.Abs(binding.TargetAngle - binding.State.Angle) > 0.01f))
                throw new InvalidOperationException("Purchased truck tailgate binding/state is missing: " + id.ToString("X16"));
            return new[] { id };
        }
        private Binding TestBinding => bindings.Values.Where(b => b.Button != null && !b.Static && !b.Late).OrderBy(b => b.Id)
            .FirstOrDefault(b => b.VehicleType == Vehicle_Type.MainVehicle)
            ?? throw new InvalidOperationException("No Konig trunk with a clickable button found among " + bindings.Count + " trunks.");


        internal VehicleTrunkState TestState => TestBinding.State;


        internal bool TestSynchronized => bindings.Values.All(b => b.Trunk.Get_IsOpen == b.State.Open &&
            Mathf.Abs(Mathf.DeltaAngle(b.Euler.x + b.State.Angle, b.Trunk.transform.localEulerAngles.x)) < 0.1f &&
            (b.Host || b.Trunk.GetComponent<Rigidbody>() == null || b.ReplicaPhysicsDisabled &&
                b.Trunk.GetComponent<Rigidbody>()?.isKinematic == true));


        internal bool TestSawPresentationInterpolation => TestBinding.SawPresentationInterpolation;


        internal void TestResetPresentationObservation() => TestBinding.SawPresentationInterpolation = false;


        internal bool TestNativeOpen => TestBinding.Trunk.Get_IsOpen;


        internal float TestNativeLocalEuler => TestBinding.Trunk.transform.localEulerAngles.x;


        internal void TestClick() => TestBinding.Button!.MouseDown();


        internal Vector3 TestPosition => TestBinding.Button!.transform.position;
#endif

    }
}
