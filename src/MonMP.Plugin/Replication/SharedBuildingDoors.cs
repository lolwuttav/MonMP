using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Adapter 15: static scene Furniture_Door open/lock state. The host runs native toggles and
    // store/home locking; guests preview their own intent with native animation and reconcile
    // the correlated host decision without repeating successful animation sounds.
    internal sealed partial class SharedBuildingDoors : IDisposable
    {
        internal static readonly FieldInfo OpenField = AccessTools.Field(typeof(Furniture_Door), "isOpen");
        internal static readonly FieldInfo LockedField = AccessTools.Field(typeof(Furniture_Door), "isLocked");
        private static readonly FieldInfo HorizontalField = AccessTools.Field(typeof(Furniture_Door), "isHorizontal");
        private static readonly FieldInfo CottageField = AccessTools.Field(typeof(Furniture_Door), "isCottage");
        private static readonly FieldInfo InitializedField = AccessTools.Field(typeof(Furniture_Door), "isInitialized");
        private static readonly FieldInfo OriginalYField = AccessTools.Field(typeof(Furniture_Door), "OriginalYRotation");
        private static readonly FieldInfo OriginalZField = AccessTools.Field(typeof(Furniture_Door), "OriginalZRotation");
        private static readonly MethodInfo StartMethod = AccessTools.Method(typeof(Furniture_Door), "Start");
        private static readonly MethodInfo OpenAnimation = AccessTools.Method(typeof(Furniture_Door), "OpenAnimation");
        private static readonly MethodInfo OpenHorizontalAnimation = AccessTools.Method(typeof(Furniture_Door), "OpenHorizontalAnimation");
        private const float CommandRange = 4.5f, AnimationSeconds = 1.2f;

        private sealed class Binding
        {
            public ulong Id;
            public Furniture_Door Door = null!;
            public Furniture_DoorHandle[] Handles = null!;
            public BuildingDoorState Original;
            public Quaternion OriginalRotation;
            public float AnimatingUntil;
            public BuildingDoorState Authority;
            public uint Revision;
            public byte PendingHandle;
            public readonly DoorPresentationIntent Intent = new DoorPresentationIntent();
        }
        private readonly bool host;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Furniture_Door, Binding> byDoor = new Dictionary<Furniture_Door, Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, appliedReplica;
        private float nextCapture;
        private ulong nextInteraction;
        public int Count => bindings.Count;
        public bool Ready => channel?.Ready == true;
        private bool Active => session?.IsRunning == true && channel != null;
        private bool IsGuest => Active && !host;

        public SharedBuildingDoors(bool host, string diagnostics, NativeEntityRegistry registry)
        {
            this.host = host;
            var handles = UnityEngine.Object.FindObjectsOfType<Furniture_DoorHandle>(true)
                .Where(h => h.furniture_Door != null && h.gameObject.scene.IsValid())
                .GroupBy(h => h.furniture_Door).ToDictionary(g => g.Key, g => g.OrderBy(h => StablePath(h.transform), StringComparer.Ordinal).ToArray());
            var homeDoors = new HashSet<Furniture_Door>(UnityEngine.Object.FindObjectsOfType<Home>(true).SelectMany(h => h.HouseDoors ?? Array.Empty<Furniture_Door>()).Where(d => d != null));
            var identities = new IdentityRegistry();
            var lines = new List<string>();
            foreach (var door in UnityEngine.Object.FindObjectsOfType<Furniture_Door>(true))
            {
                if (!door.gameObject.scene.IsValid() || Spawned(door.transform)) continue;
                if (!handles.TryGetValue(door, out var doorHandles)) doorHandles = Array.Empty<Furniture_DoorHandle>();
                if (doorHandles.Length > byte.MaxValue) throw new InvalidOperationException("Too many handles on " + door.name);
                if (!(bool)InitializedField.GetValue(door)) StartMethod.Invoke(door, null);
                string path = StablePath(door.transform) + "/building-door-v1";
                ulong id = identities.Register(path);
                var b = new Binding { Id = id, Door = door, Handles = doorHandles, Original = Capture(door), Authority = Capture(door), OriginalRotation = door.transform.localRotation };
                bindings.Add(id, b); byDoor.Add(door, b);
                registry.Bind(id, BuildingDoorState.Adapter, door.gameObject, path, true, homeDoors.Contains(door) ? EntityPersistence.NativeSave : EntityPersistence.SessionOnly);
                lines.Add(id.ToString("X16") + "\t" + path + "\t" + doorHandles.Length + "\t" + (homeDoors.Contains(door) ? "home" : "session"));
            }
            lines.Sort(StringComparer.Ordinal);

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-building-doors.tsv"), new[] { "id\tdoor-path\thandles\tpersistence" }.Concat(lines));
#endif

        }

        // Sibling indices shift when a save deletes default objects, so identify each segment
        // by its ordinal among same-named siblings instead.
        internal static string StablePath(Transform transform)
        {
            var segments = new Stack<string>();
            for (var current = transform; current != null; current = current.parent)
            {
                IEnumerable<Transform> siblings = current.parent != null
                    ? Enumerable.Range(0, current.parent.childCount).Select(i => current.parent.GetChild(i))
                    : current.gameObject.scene.GetRootGameObjects().Select(g => g.transform);
                int ordinal = 0;
                foreach (var sibling in siblings)
                {
                    if (sibling == current) break;
                    if (sibling.name == current.name) ordinal++;
                }
                segments.Push(StaticId.Segment(current.name.Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r"), ordinal));
            }
            var scene = transform.gameObject.scene;
            return StaticId.Segment(scene.path, scene.buildIndex) + "/" + string.Join("/", segments);
        }
        private static bool Spawned(Transform transform)
        {
            for (var current = transform; current != null; current = current.parent)
                if (current.name.Contains("(Clone)") || current.GetComponent<InteractableItems>() != null) return true;
            return false;
        }

        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, BuildingDoorState.Adapter, Capture(b.Door).Encode()));
        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command; channel.ReplyReceived += Reply;
            channel.CommandRejected += VipRejected;
            channel.CommandRejected += Rejected;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) => presence.Remove(peer);

        private static BuildingDoorState Capture(Furniture_Door door) => new BuildingDoorState((bool)OpenField.GetValue(door), (bool)LockedField.GetValue(door));
        private static bool CottageLocked(Furniture_Door door) => (bool)CottageField.GetValue(door) && !Singleton<ToDoList_Manager>.i.isDone(ToDoList_Line.Intermediate_CottageBought);
        private static bool LockedFor(Furniture_Door door) => (bool)LockedField.GetValue(door) || CottageLocked(door);

        // Returns whether the native handle body may run.
        public bool AllowHandle(Furniture_DoorHandle handle, Type declaring)
        {
            if (Active && host && NewPurchasesBlocked && declaring == typeof(Furniture_DoorHandle_VIPRoom) &&
                !Singleton<ToDoList_Manager>.i.isDone(ToDoList_Line.DecorationStoreVIPRoom))
            {
                MonMPPlugin.Instance?.ReportItemUseUnavailable("Wait for the multiplayer save to finish before unlocking the VIP room.");
                return false;
            }
            if (!IsGuest) return true;
            if (handle.furniture_Door == null || !byDoor.TryGetValue(handle.furniture_Door, out var b))
            {
                if (declaring != typeof(Furniture_DoorHandle_VIPRoom)) return true;
                MonMPPlugin.Instance?.ReportItemUseUnavailable("The VIP room handle is not available in the shared world.");
                return false;
            }
            if (declaring == typeof(Furniture_DoorHandle_Dealer)) return true; // Native ToDo check, then the base handle routes.
            if (declaring == typeof(Furniture_DoorHandle_VIPRoom))
            {
                if (Singleton<ToDoList_Manager>.i.isDone(ToDoList_Line.DecorationStoreVIPRoom))
                {
                    // VIP: unlocking happens once per world, so a guest who joined later earns it at the unlocked door.
                    GuestAchievements.AwardToDo(ToDoList_Line.DecorationStoreVIPRoom);
                    return true;
                }
                RouteVipUnlock(b, (Furniture_DoorHandle_VIPRoom)handle);
                return false;
            }
            // The Roger store flag is host progression; the host validates it for this handle.
            Route(b, handle);
            return false;
        }
        private void Route(Binding b, Furniture_DoorHandle handle)
        {
            if (LockedFor(b.Door)) { b.Door.ToggleDoor(); return; } // Native locked message and sound only.
            if (!Ready || MonMPPlugin.Instance?.World?.SaveGate.Active == true) return;
            int index = Array.IndexOf(b.Handles, handle);
            if (index < 0 || !handle.isActiveAndEnabled || !handle.gameObject.activeInHierarchy || !b.Door.isActiveAndEnabled) return;
            b.PendingHandle = (byte)index;
            b.Intent.Toggle(b.Authority.Open);
            SendIntent(b);
            appliedReplica = true;
            Apply(b, new BuildingDoorState(b.Intent.PresentationOpen(b.Authority.Open), b.Authority.Locked), true);
        }
        private void SendIntent(Binding b)
        {
            if (b.Revision == 0 || !b.Intent.TryBegin(++nextInteraction, b.Authority.Open, Time.unscaledTime)) return;
            var payload = new BuildingDoorCommand(b.Authority.Open, b.PendingHandle).Encode();
            channel!.Request(b.Id, new ClientInteractionRequest(b.Intent.Token, payload).Encode(), expectedRevision: b.Revision);
        }
        // Guests keep native toggles only for the locked-feedback path; host state is authoritative.
        public bool AllowMutation(Furniture_Door door, bool toggle)
        {
            if (!IsGuest || applying || !byDoor.ContainsKey(door)) return true;
            return toggle && LockedFor(door);
        }

        [MonMP.Core.World.WorldCommandAdapters(BuildingDoorState.Adapter)]

        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || session?.IsHost != true || !bindings.TryGetValue(id, out var b)) return;
            if (VipRoomUnlockRequest.Is(payload)) { VipCommand(peer, id, payload, false); return; }
            bool correlated = ClientInteractionRequest.Is(payload);
            var request = correlated ? ClientInteractionRequest.Decode(payload) : default;
            if (correlated) payload = request.Payload;
            var command = BuildingDoorCommand.Decode(payload);
            ClientInteractionStatus status = ExecuteToggle(peer, b, command);
            channel!.Publish(b.Id, Capture(b.Door).Encode());
            if (correlated && peer != 0) Receipt(peer, b, request.Token, status);
            else if (!correlated && peer != 0 && status == ClientInteractionStatus.Locked) channel.Reply(peer, id, BuildingDoorReply.Encode());
        }
        private ClientInteractionStatus ExecuteToggle(uint peer, Binding b, BuildingDoorCommand command)
        {
            if (command.Handle >= b.Handles.Length || b.Door == null) return ClientInteractionStatus.Refused;
            var handle = b.Handles[command.Handle];
            if (handle == null || !handle.isActiveAndEnabled || !handle.gameObject.activeInHierarchy ||
                !b.Door.isActiveAndEnabled || !b.Door.gameObject.activeInHierarchy) return ClientInteractionStatus.Refused;
            if (peer != 0)
            {
                if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2) return ClientInteractionStatus.Refused;
                if ((handle.transform.position - new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z)).sqrMagnitude > CommandRange * CommandRange) return ClientInteractionStatus.Refused;
            }
            if (MonMPPlugin.Instance?.World?.SaveGate.Active == true) return ClientInteractionStatus.Refused;
            if (command.ObservedOpen != (bool)OpenField.GetValue(b.Door)) return ClientInteractionStatus.Stale;
            if (LockedFor(b.Door) || (handle is Furniture_DoorHandle_Dealer && !Singleton<ToDoList_Manager>.i.isDone(ToDoList_Line.Dealer_Unlock)) ||
                (handle is Furniture_DoorHandle_RogerStore && !GameManager.Get_RogerStoreIsOpen) ||
                (handle is Furniture_DoorHandle_VIPRoom && !Singleton<ToDoList_Manager>.i.isDone(ToDoList_Line.DecorationStoreVIPRoom)))
            {
#if MONMP_DEVELOPMENT
                TestRefusals++;
#endif
                return ClientInteractionStatus.Locked;
            }
            b.Door.ToggleDoor();
            return ClientInteractionStatus.Accepted;
        }
        private void Receipt(uint peer, Binding b, ulong token, ClientInteractionStatus status)
        {
            var state = Capture(b.Door);
            channel!.Publish(b.Id, state.Encode());
            channel.Reply(peer, b.Id, new ClientInteractionReceipt(token, status, channel.Records[b.Id].Revision, state.Encode()).Encode());
        }
        private void Rejected(uint peer, ulong id, byte[] payload)
        {
            if (!host || !ClientInteractionRequest.Is(payload) || !bindings.TryGetValue(id, out var b) || b.Door == null) return;
            var request = ClientInteractionRequest.Decode(payload);
            BuildingDoorCommand.Decode(request.Payload);
            Receipt(peer, b, request.Token, ClientInteractionStatus.Stale);
        }
        private void Reply(ulong id, byte[] payload)
        {
            if (VipRoomUnlockReply.Is(payload)) { VipReply(id, payload); return; }
            if (host || !bindings.TryGetValue(id, out var b) || b.Door == null) return;
            if (ClientInteractionReceipt.Is(payload))
            {
                var receipt = ClientInteractionReceipt.Decode(payload);
                var state = BuildingDoorState.Decode(receipt.State);
                if (b.Intent.Token != receipt.Token || b.Intent.Token == 0) return;
                if (receipt.Revision >= b.Revision) { b.Authority = state; b.Revision = receipt.Revision; }
                b.Intent.Acknowledge(receipt.Token, receipt.Status == ClientInteractionStatus.Accepted, b.Authority.Open, Time.unscaledTime);
                b.Intent.Observe(b.Authority.Open);
                SendIntent(b);
                Apply(b, new BuildingDoorState(b.Intent.PresentationOpen(b.Authority.Open), b.Authority.Locked), receipt.Status == ClientInteractionStatus.Accepted);
                if (receipt.Status != ClientInteractionStatus.Locked) return;
            }
            else BuildingDoorReply.Validate(payload);

#if MONMP_DEVELOPMENT
            TestRefusals++;
#endif

            Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText("ERROR_DoorLocked"));
            b.Door.GetComponent<SOUND_Furniture_Door>()?.Locked();
        }
        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            var staged = records.Values.Where(r => r.Adapter == BuildingDoorState.Adapter)
                .Select(r => (Binding: bindings.TryGetValue(r.Id, out var b) && b.Door != null ? b : throw new InvalidDataException("Missing building door binding."), State: BuildingDoorState.Decode(r.Payload), r.Revision)).ToArray();
            if (host) return;
            appliedReplica = true;
            foreach (var value in staged) AcceptState(value.Binding, value.State, value.Revision, false);
        }
        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != BuildingDoorState.Adapter) return;
            if (!bindings.TryGetValue(record.Id, out var b) || b.Door == null) throw new InvalidDataException("Missing building door binding.");
            appliedReplica = true;
            AcceptState(b, BuildingDoorState.Decode(record.Payload), record.Revision, true);
        }
        private void AcceptState(Binding b, BuildingDoorState state, uint revision, bool animate)
        {
            if (revision < b.Revision) return;
            b.Authority = state; b.Revision = revision;
            if (!animate || state.Locked) b.Intent.Clear();
            b.Intent.Observe(state.Open);
            SendIntent(b);
            Apply(b, new BuildingDoorState(b.Intent.PresentationOpen(state.Open), state.Locked), animate);
        }
        private void Apply(Binding b, BuildingDoorState state, bool animate)
        {
            var door = b.Door;
            applying = true;
            try
            {
                LockedField.SetValue(door, state.Locked);
                bool current = (bool)OpenField.GetValue(door);
                if (animate && current != state.Open && door.isActiveAndEnabled)
                {
                    // Native canceled animations write their old endpoint once more. Stop them
                    // before a reversal so an earlier click cannot snap the new local preview.
                    door.StopAllCoroutines();
                    OpenField.SetValue(door, state.Open);
                    var animation = (bool)HorizontalField.GetValue(door) ? OpenHorizontalAnimation : OpenAnimation;
                    door.StartCoroutine((IEnumerator)animation.Invoke(door, null));
                    b.AnimatingUntil = Time.unscaledTime + AnimationSeconds;
                }
                else if (!animate || current != state.Open || (Time.unscaledTime >= b.AnimatingUntil && !AtEndpoint(b, state.Open)))
                {
                    door.StopAllCoroutines();
                    OpenField.SetValue(door, state.Open);
                    door.transform.localRotation = Endpoint(b, state.Open);
                    b.AnimatingUntil = 0;
                }
            }
            finally { applying = false; }
        }
        private static Quaternion Endpoint(Binding b, bool open)
        {
            var door = b.Door; var e = door.transform.localEulerAngles;
            if ((bool)HorizontalField.GetValue(door))
            {
                float z = (float)OriginalZField.GetValue(door);
                return Quaternion.Euler(e.x, e.y, open ? -(door.MaxAngle + z) : z);
            }
            float y = (float)OriginalYField.GetValue(door);
            return Quaternion.Euler(e.x, open ? -(door.MaxAngle + y) : y, e.z);
        }
        private static bool AtEndpoint(Binding b, bool open) => Quaternion.Angle(b.Door.transform.localRotation, Endpoint(b, open)) < 0.5f;

        // Stores lock their doors once the player walks away; in a session every player counts.
        public float NearestPlayerDistance(Vector3 local, Vector3 target)
        {
            float best = Vector3.Distance(local, target);
            if (!host || !Active) return best;
            foreach (var p in presence.Values)
                if (Time.unscaledTime - p.Time <= 10) best = Mathf.Min(best, Vector3.Distance(new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z), target));
            return best;
        }
        public void Update()
        {
            ExpireVipUnlocks();
            if (!host)
            {
                foreach (var b in bindings.Values)
                    if (b.Door != null && b.Intent.Expire(Time.unscaledTime)) Apply(b, b.Authority, false);
                return;
            }
            if (!host || !Ready || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 0.1f;
            foreach (var b in bindings.Values)
            {
                if (b.Door == null) throw new InvalidOperationException("A registered building door was removed; reload before rehosting.");
                channel!.Publish(b.Id, Capture(b.Door).Encode());
            }
        }
        public void Dispose()
        {
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; channel.ReplyReceived -= Reply; }
            if (channel != null) channel.CommandRejected -= VipRejected;
            if (channel != null) channel.CommandRejected -= Rejected;
            foreach (var b in bindings.Values) b.Intent.Clear();
            if (!host && appliedReplica)
                foreach (var b in bindings.Values)
                {
                    if (b.Door == null) continue;
                    b.Door.StopAllCoroutines();
                    OpenField.SetValue(b.Door, b.Original.Open); LockedField.SetValue(b.Door, b.Original.Locked);
                    b.Door.transform.localRotation = b.OriginalRotation;
                }
            session = null; channel = null; presence.Clear();
            ResetVipUnlocks();
        }


#if MONMP_DEVELOPMENT
        internal int TestRefusals { get; private set; }


        internal void TestRequest(ulong id, bool observedOpen) => channel!.Request(id, new BuildingDoorCommand(observedOpen, 0).Encode());


        internal ulong TestEntity(Func<Furniture_Door, bool> filter) => bindings.Values.Where(b => b.Door != null && b.Handles.Length > 0 && filter(b.Door)).OrderBy(b => b.Id).First().Id;


        internal Furniture_Door TestDoor(ulong id) => bindings[id].Door;


        internal Vector3 TestHandlePosition(ulong id) => bindings[id].Handles[0].transform.position;


        internal void TestClick(ulong id) => bindings[id].Handles[0].MouseDown();


        internal BuildingDoorState TestNative(ulong id) => Capture(bindings[id].Door);


        internal BuildingDoorState TestRecord(ulong id) => BuildingDoorState.Decode(channel!.Records[id].Payload);


        internal bool TestAtEndpoint(ulong id) => AtEndpoint(bindings[id], Capture(bindings[id].Door).Open);


        internal bool TestSynchronized => Ready && bindings.Values.All(b => Capture(b.Door).Equals(BuildingDoorState.Decode(channel!.Records[b.Id].Payload)) &&
            (Time.unscaledTime < b.AnimatingUntil || AtEndpoint(b, Capture(b.Door).Open)));


        internal void TestDrift(ulong id)
        {
            if (host) throw new InvalidOperationException("Only a guest replica may drift.");
            var b = bindings[id]; bool open = !(bool)OpenField.GetValue(b.Door);
            OpenField.SetValue(b.Door, open); b.Door.transform.localRotation = Endpoint(b, open); b.AnimatingUntil = 0;
        }


        internal bool TestRestored => bindings.Values.All(b => b.Door == null || (Capture(b.Door).Equals(b.Original) && Quaternion.Angle(b.Door.transform.localRotation, b.OriginalRotation) < 0.5f));


        internal int TestHomeCount => bindings.Values.Count(b => b.Door != null && UnityEngine.Object.FindObjectsOfType<Home>(true).Any(h => h.HouseDoors?.Contains(b.Door) == true));
#endif

    }
}
