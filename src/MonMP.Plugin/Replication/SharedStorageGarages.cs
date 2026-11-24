using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using NWH;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Storage garage (EntreposageGarage) bay doors. The host runs every native door button, its
    // electricity/ownership/safety checks and the native load (door opens) or unload (door closes on
    // a single vehicle) of the parked vehicle, counting remote players standing in the bay. Guests
    // send button intents, mirror the door animation and hide or show their copy of the vehicle.
    internal sealed class SharedStorageGarages : IDisposable
    {
        private sealed class Binding
        {
            public ulong Id;
            public EntreposageGarage_Door Door = null!;
            public Vehicle? Stored; // the vehicle this side keeps unloaded behind the door
            public ulong MotionEpoch;
            public uint MotionPeer;
        }
        private sealed class MotionActor
        {
            public Binding Binding = null!;
            public uint Peer;
            public ulong Epoch;
            public NetworkSession Session = null!;
            public Guid SessionEpoch;
            public WorldChannel Channel = null!;
        }
        private sealed class ActorExit : IDisposable
        {
            private Action? exit;
            public ActorExit(Action exit) { this.exit = exit; }
            public void Dispose() { var action = exit; exit = null; action?.Invoke(); }
        }
        private static readonly AccessTools.FieldRef<EntreposageGarage_Door, EntreposageGarage> Garage = AccessTools.FieldRefAccess<EntreposageGarage_Door, EntreposageGarage>("EntreposageGarage");
        private static readonly AccessTools.FieldRef<EntreposageGarage_Door, int> DoorId = AccessTools.FieldRefAccess<EntreposageGarage_Door, int>("DoorID");
        private static readonly AccessTools.FieldRef<EntreposageGarage_Door, Animator> DoorAnimator = AccessTools.FieldRefAccess<EntreposageGarage_Door, Animator>("Animator_Door");
        private static readonly AccessTools.FieldRef<EntreposageGarage_Door, BoxCollider> Bay = AccessTools.FieldRefAccess<EntreposageGarage_Door, BoxCollider>("BayDetector_Collider");
        private static readonly AccessTools.FieldRef<EntreposageGarage_Door, Furniture_SwitchButton> DoorSwitch = AccessTools.FieldRefAccess<EntreposageGarage_Door, Furniture_SwitchButton>("DoorButton");
        private const float DoorSpeed = 2.4f;

        private readonly bool host;
        private readonly SharedVehicles vehicles;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<EntreposageGarage_Door, Binding> byDoor = new Dictionary<EntreposageGarage_Door, Binding>();
        private readonly Dictionary<ulong, StorageGarageDoorState> pending = new Dictionary<ulong, StorageGarageDoorState>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private float nextScan, nextPublish, nextReconcile;
        private bool disposed;
        private EntreposageGarage_Door? requestingDoor;
        private uint requestingPeer;
        private MotionActor? steppingActor;
        // Set while this adapter drives a native load/unload so the guest intercepts let it through.
        internal static bool ApplyingVehicle { get; private set; }
        internal int Count => bindings.Count;
        private bool Active => !disposed && session?.IsRunning == true && channel?.Ready == true;

        public SharedStorageGarages(bool host, SharedVehicles vehicles) { this.host = host; this.vehicles = vehicles; }

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command;
            channel.ReplyReceived += Reply;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer)
        {
            presence.Remove(peer);
            // A resumed/canceled old native iterator stays quiet, but cannot send to a reused peer ID.
            foreach (var binding in bindings.Values)
                if (binding.MotionPeer == peer) binding.MotionEpoch++;
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values) Receive(record);
        }
        private void Changed(WorldRecord record) { if (!host) Receive(record); }
        private void Receive(WorldRecord record)
        {
            if (record.Adapter != StorageGarageDoorState.Adapter) return;
            StorageGarageDoorState state;
            try { state = StorageGarageDoorState.Decode(record.Payload); }
            catch (InvalidDataException) { return; }
            pending[record.Id] = state;
            if (bindings.TryGetValue(record.Id, out var b) && b.Door != null) Apply(b, state);
        }

        [MonMP.Core.World.WorldCommandAdapters(StorageGarageDoorState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0 || !StorageGarageDoorCommand.Is(payload) || !bindings.TryGetValue(id, out var b) || b.Door == null) return;

#if MONMP_DEVELOPMENT
            TestCommands++;
#endif

            if (!presence.TryGetValue(peer, out var seen) || Time.unscaledTime - seen.Time > 2) return;
            if ((b.Door.transform.position - new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z)).sqrMagnitude > 12f * 12f) return;
            var garage = Garage(b.Door);
            if (garage == null) return;
            // The scrapyard button's native MouseDown gates the König unlock before raising its
            // switch event. Recheck that same shared ToDo line on the host for stale/direct commands;
            // ordinary storage buttons keep the native garage's existing checks only.
            if (DoorSwitch(b.Door) is Furniture_SwitchButton_GarageButtonScrap &&
                !Singleton<ToDoList_Manager>.i.isDone(ToDoList_Line.Intermediate_UnlockScrapKonig)) return;
            Patches.RemoteActionScope.LastError = null;
            // Native ownership, electricity and moving refusals belong to the requester.
            var previousDoor = requestingDoor;
            uint previousPeer = requestingPeer;
            requestingDoor = b.Door; requestingPeer = peer;
            try
            {
                using (Patches.RemoteActionScope.Enter()) garage.ToggleDoor(DoorId(b.Door));
            }
            finally { requestingDoor = previousDoor; requestingPeer = previousPeer; }
            if (Patches.RemoteActionScope.LastError is string error)
                channel!.Reply(peer, id, NativeErrorReply.Encode(error));
            Publish(b);
        }

        private void Reply(ulong id, byte[] payload)
        {
            if (!host && bindings.ContainsKey(id) && NativeErrorReply.TryDecode(payload, out string error))
                Singleton<UI_ErrorMessage>.i?.Show(error);
        }

        internal IEnumerator CaptureDoorMotion(EntreposageGarage_Door door, IEnumerator inner)
        {
            // The native safety check can start an automatic reopen from a closing step.
            // Keep that motion in the same requester generation, even during teardown.
            MotionActor? actor = steppingActor?.Binding.Door == door ? steppingActor : null;
            if (actor == null)
            {
                if (!host || !Active || !byDoor.TryGetValue(door, out var binding)) return inner;
                binding.MotionEpoch++;
                binding.MotionPeer = requestingDoor == door ? requestingPeer : 0;
                if (requestingDoor != door || requestingPeer == 0) return inner;
                actor = new MotionActor { Binding = binding, Peer = requestingPeer, Epoch = binding.MotionEpoch,
                    Session = session!, SessionEpoch = session!.Epoch, Channel = channel! };
            }
            var captured = actor;
            return new StorageGarageMotionFeedback(inner, () => EnterMotionActor(captured), error => ReplyMotion(captured, error));
        }

        private IDisposable EnterMotionActor(MotionActor actor)
        {
            var previous = steppingActor; steppingActor = actor;
            return new ActorExit(() => steppingActor = previous);
        }

        private void ReplyMotion(MotionActor actor, string error)
        {
            if (!host || !Active || !ReferenceEquals(session, actor.Session) || !ReferenceEquals(channel, actor.Channel) ||
                session!.Epoch != actor.SessionEpoch || session.Peers.ContainsKey(actor.Peer) != true || actor.Binding.MotionEpoch != actor.Epoch ||
                !bindings.TryGetValue(actor.Binding.Id, out var binding) || !ReferenceEquals(binding, actor.Binding)) return;
            channel!.Reply(actor.Peer, binding.Id, NativeErrorReply.Encode(error));
        }

        // Guest intercept for the door button: the host toggles the door.
        internal bool AllowToggle(EntreposageGarage garage, int door)
        {
            if (host || !Active) return true;
            var b = bindings.Values.FirstOrDefault(value => value.Door != null && Garage(value.Door) == garage && DoorId(value.Door) == door);
            if (b == null || !channel!.Records.ContainsKey(b.Id)) return true;
            channel.Request(b.Id, StorageGarageDoorCommand.Encode(), true);
            return false;
        }
        // Guest: the host alone loads and unloads parked vehicles (its record drives this side).
        internal bool AllowLoadUnload(EntreposageGarage garage) =>
            host || ApplyingVehicle || !Active || !bindings.Values.Any(b => b.Door != null && Garage(b.Door) == garage);
        // Guest: the host already ran the close checks (Leo, vehicle count) for the mirrored door.
        internal bool SkipCloseCheck(EntreposageGarage_Door door) => !host && Active && byDoor.ContainsKey(door);
        // Only while the door moves (its closing coroutine): the native save gates also read
        // isPlayerInBay and must keep asking about the local player only.
        // Host: a remote player standing in the bay stops the door closing on them, like the host
        // player. Guest: its mirrored door never reverses on its own.
        internal bool? PlayerInBay(EntreposageGarage_Door door)
        {
            if (!Active || !byDoor.ContainsKey(door) || !Moving(door)) return null;
            if (!host) return false;
            var box = Bay(door);
            if (box == null) return null;
            float now = Time.unscaledTime;
            foreach (var seen in presence.Values)
                if (now - seen.Time <= 2 && InBox(new Vector3(seen.Pose.X, seen.Pose.Y + 0.9f, seen.Pose.Z), box)) return true;
            return null;
        }

        public void Update()
        {
            if (!Active) return;
            float now = Time.unscaledTime;
            // GetAllVehicles includes inactive stored vehicles from the native registry.
            // Lazily share its snapshot across registration and publication in this pass.
            Vehicle[]? fleet = null;
            bool periodic = nextScan > 0;
            if (now >= nextScan && WorldScans.TryClaim<EntreposageGarage_Door>(periodic)) { nextScan = periodic ? WorldScans.NextDeadline(now, 2f) : now + 2f; Scan(ref fleet, periodic); }
            if (host)
            {
                if (now >= nextPublish)
                {
                    nextPublish = now + 0.2f;
                    foreach (var b in bindings.Values)
                    {
                        if (b.Door == null || !channel!.Records.TryGetValue(b.Id, out var record)) continue;
                        var state = Capture(b, Fleet(ref fleet));
                        if (!state.Equals(StorageGarageDoorState.Decode(record.Payload))) channel.Publish(b.Id, state.Encode());
                    }
                }
            }
            else if (now >= nextReconcile)
            {
                nextReconcile = now + 0.5f;
                foreach (var b in bindings.Values)
                    if (b.Door != null && pending.TryGetValue(b.Id, out var state)) Apply(b, state);
            }
        }

        private void Scan(ref Vehicle[]? fleet, bool periodic = false)
        {
            foreach (var dead in byDoor.Keys.Where(d => d == null).ToArray()) { bindings.Remove(byDoor[dead].Id); byDoor.Remove(dead); }
            foreach (var door in WorldScans.Find<EntreposageGarage_Door>(periodic, true))
            {
                if (byDoor.ContainsKey(door) || !door.gameObject.scene.IsValid()) continue;
                if (Garage(door) == null || Bay(door) == null || DoorAnimator(door) == null) continue; // Start has not run yet
                ulong id = StorageGarageDoorState.EntityFor(SharedBuildingDoors.StablePath(door.transform));
                if (bindings.ContainsKey(id)) continue; // ambiguous duplicate stays local
                var binding = new Binding { Id = id, Door = door };
                bindings.Add(id, binding); byDoor.Add(door, binding);
                if (host)
                {
                    var state = Capture(binding, Fleet(ref fleet));
                    if (!channel!.Records.ContainsKey(id)) channel.Register(new WorldRecord(id, 1, StorageGarageDoorState.Adapter, state.Encode()));
                }
                else if (pending.TryGetValue(id, out var state)) Apply(binding, state);
            }
        }

        private static bool Moving(EntreposageGarage_Door door) => door.Get_IsDoorMoving;
        private static bool LocalOpen(EntreposageGarage_Door door) =>
            Moving(door) ? DoorAnimator(door).GetFloat("Movement") > 0 : door.Get_IsDoorOpen;

        private StorageGarageDoorState Capture(Binding b)
        {
            var parked = b.Door.GetVehiclesInBay;
            bool stored = parked.Length == 1 && parked[0] != null && !parked[0].gameObject.activeInHierarchy;
            return new StorageGarageDoorState(LocalOpen(b.Door), stored, stored ? vehicles.EntityOf(parked[0]) : 0);
        }

        private static Vehicle[] Fleet(ref Vehicle[]? fleet) =>
            fleet ??= Singleton<GlobalClassManager>.i.GetAllVehicles();

        private StorageGarageDoorState Capture(Binding b, Vehicle[] fleet)
        {
            var box = Bay(b.Door);
            Vehicle? parked = null;
            int count = 0;
            foreach (var vehicle in fleet)
            {
                if (vehicle == null || !InBox(vehicle.transform.position, box)) continue;
                parked = vehicle;
                // Only exact single occupancy can be stored; no per-bay result array is needed.
                if (++count > 1) break;
            }
            bool stored = count == 1 && parked != null && !parked.gameObject.activeInHierarchy;
            return new StorageGarageDoorState(LocalOpen(b.Door), stored, stored ? vehicles.EntityOf(parked!) : 0);
        }

        private void Publish(Binding b)
        {
            if (b.Door == null || !channel!.Records.TryGetValue(b.Id, out var record)) return;
            var state = Capture(b);
            if (!state.Equals(StorageGarageDoorState.Decode(record.Payload))) channel.Publish(b.Id, state.Encode());
        }

        private void Apply(Binding b, StorageGarageDoorState state)
        {
            var door = b.Door; var garage = Garage(door);
            if (door == null || garage == null) return;
            int doorId = DoorId(door);
            // The parked vehicle first: a door opening shows it before the door rises, and a closed
            // door hides it once the host's native unload ran.
            if (state.Stored)
            {
                Vehicle? target = state.StoredVehicle != 0 ? vehicles.VehicleFor(state.StoredVehicle) : null;
                if (target == null && state.StoredVehicle == 0)
                {
                    var parked = door.GetVehiclesInBay;
                    if (parked.Length == 1) target = parked[0];
                }
                if (target != null)
                {
                    b.Stored = target;
                    if (target.gameObject.activeSelf) Run(() => garage.UnloadThisCar(doorId, target));
                }
            }
            else if (b.Stored != null)
            {
                var stored = b.Stored; b.Stored = null;
                if (!stored.gameObject.activeSelf) Run(() => garage.LoadThisCar(doorId, stored));
            }
            if (state.Open == LocalOpen(door)) return;
            if (Moving(door)) DoorAnimator(door).SetFloat("Movement", state.Open ? DoorSpeed : -DoorSpeed);
            else if (state.Open) door.Door_Open();
            else door.Door_Close();
        }
        private static void Run(Action action)
        {
            ApplyingVehicle = true;
            try { action(); }
            finally { ApplyingVehicle = false; }
        }

        // Host at session start: a vehicle parked behind any storage garage door.
        internal static bool InAnyBay(Vehicle vehicle) =>
            UnityEngine.Object.FindObjectsOfType<EntreposageGarage_Door>(true).Any(door => Bay(door) is { } box && InBox(vehicle.transform.position, box));

        // EntreposageGarage_Door.PointInOABB.
        private static bool InBox(Vector3 point, BoxCollider box)
        {
            point = box.transform.InverseTransformPoint(point) - box.center;
            var half = box.size * 0.5f;
            return Mathf.Abs(point.x) < half.x && Mathf.Abs(point.y) < half.y && Mathf.Abs(point.z) < half.z;
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; channel.ReplyReceived -= Reply; }
            bindings.Clear(); byDoor.Clear(); pending.Clear(); presence.Clear();
            session = null; channel = null;
        }

        // Test hooks.

#if MONMP_DEVELOPMENT
        internal IEnumerable<ulong> TestDoors => bindings.Keys;


        internal EntreposageGarage_Door TestDoor(ulong id) => bindings[id].Door;


        internal bool TestOpen(ulong id) => LocalOpen(bindings[id].Door);


        internal bool TestMoving(ulong id) => Moving(bindings[id].Door);


        internal StorageGarageDoorState TestCapture(ulong id) => Capture(bindings[id]);


        internal StorageGarageDoorState? TestRecord(ulong id) =>
            channel != null && channel.Records.TryGetValue(id, out var record) ? StorageGarageDoorState.Decode(record.Payload) : (StorageGarageDoorState?)null;


        internal int TestCommands { get; private set; }


        internal bool TestBayReady(ulong id) => bindings[id].Door.gameObject.activeInHierarchy && !Moving(bindings[id].Door) &&
            bindings[id].Door.Get_IsDoorClose && bindings[id].Door.GetVehiclesInBay.Length == 0;


        internal Vector3 TestBayFloor(ulong id)
        {
            var box = Bay(bindings[id].Door);
            return box.transform.TransformPoint(box.center - new Vector3(0, box.size.y * 0.5f - 0.8f, 0));
        }


        internal bool TestInBay(ulong id, Vector3 point) => InBox(point, Bay(bindings[id].Door));


        internal Furniture_SwitchButton TestButton(ulong id) =>
            AccessTools.FieldRefAccess<EntreposageGarage_Door, Furniture_SwitchButton>("DoorButton")(bindings[id].Door);
#endif

    }
}
