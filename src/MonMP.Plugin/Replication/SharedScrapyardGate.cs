using System;
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
    // Adapter 34: host-owned scrapyard gate access and native gate presentation.
    // ToDo completion remains in SharedProgress; this adapter carries the two hinges,
    // collision objects, handle pin and one-shot dust presentation.
    internal sealed class SharedScrapyardGate : IDisposable
    {
        private static readonly FieldInfo LockedColliderField = AccessTools.Field(typeof(Scrapyard), "Collider_Locked");
        private static readonly FieldInfo NormalColliderField = AccessTools.Field(typeof(Scrapyard), "Collider_Normal");
        private static readonly FieldInfo DustField = AccessTools.Field(typeof(Scrapyard), "DustOnBuy");
        private static readonly FieldInfo PinField = AccessTools.Field(typeof(Scrapyard), "DoorHandlePin");
        private static readonly FieldInfo LeftField = AccessTools.Field(typeof(Scrapyard), "FenceDoorLeft");
        private static readonly FieldInfo RightField = AccessTools.Field(typeof(Scrapyard), "FenceDoorRight");
        private static readonly FieldInfo PoliceTicketsField = AccessTools.Field(typeof(Store_PoliceInterceptor), "ActiveTickets");
        private static readonly FieldInfo LeftOriginalField = AccessTools.Field(typeof(Scrapyard), "Left_OriginalRotation");
        private static readonly FieldInfo RightOriginalField = AccessTools.Field(typeof(Scrapyard), "Right_OriginalRotation");
        private static readonly FieldInfo LeftEndField = AccessTools.Field(typeof(Scrapyard), "Left_EndRotation");
        private static readonly FieldInfo RightEndField = AccessTools.Field(typeof(Scrapyard), "Right_EndRotation");
        private const float CommandRange = 4.5f;

        private sealed class Binding
        {
            public ulong Id;
            public string Path = "";
            public Scrapyard Gate = null!;
            public GameObject LockedCollider = null!;
            public GameObject NormalCollider = null!;
            public GameObject Dust = null!;
            public GameObject Pin = null!;
            public Transform Left = null!;
            public Transform Right = null!;
            public Quaternion LeftClosed, RightClosed, LeftOpen, RightOpen;
            public ScrapyardGateState Original;
        }

        private readonly bool host;
        private readonly Binding binding;
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool applying, appliedReplica;
        private float nextCapture;

#if MONMP_DEVELOPMENT
        internal int TestCriminalRefusals { get; private set; }


        internal int TestFriendshipRefusals { get; private set; }


        internal int TestMissingPose { get; private set; }


        internal int TestStalePose { get; private set; }


        internal int TestNonWalkingPose { get; private set; }


        internal int TestOutOfRange { get; private set; }


        internal int TestSaveBlocked { get; private set; }


        internal int TestHostCriminalDenials { get; private set; }


        internal int TestHostFriendshipDenials { get; private set; }


        internal string TestLastRejectReason { get; private set; } = "none";
#endif


        internal float LastPoseAge { get; private set; }


        internal float LastDistance { get; private set; }


#if MONMP_DEVELOPMENT
        internal byte TestLastPoseState { get; private set; }
#endif

        internal bool Ready => channel?.Ready == true;

#if MONMP_DEVELOPMENT
        internal bool TestChannelAttached => channel != null && session?.IsRunning == true;


        internal bool TestRecordAvailable => channel != null && channel.Records.ContainsKey(binding.Id);
#endif

        private bool Active => session?.IsRunning == true && channel != null;
        private bool IsGuest => Active && !host;

        internal SharedScrapyardGate(bool host, string diagnostics, NativeEntityRegistry registry)
        {
            this.host = host;
            var gates = UnityEngine.Object.FindObjectsOfType<Scrapyard>(true).Where(g => g != null && g.gameObject.scene.IsValid()).ToArray();
            if (gates.Length != 1) throw new InvalidOperationException("Expected one native scrapyard gate; found " + gates.Length + ".");
            var gate = gates[0];
            var locked = RequireObject(LockedColliderField, gate, "Collider_Locked");
            var normal = RequireObject(NormalColliderField, gate, "Collider_Normal");
            var dust = RequireObject(DustField, gate, "DustOnBuy");
            var pin = RequireObject(PinField, gate, "DoorHandlePin");
            var left = RequireTransform(LeftField, gate, "FenceDoorLeft");
            var right = RequireTransform(RightField, gate, "FenceDoorRight");
            if (new[] { LeftOriginalField, RightOriginalField, LeftEndField, RightEndField }.Any(f => f == null || f.FieldType != typeof(Quaternion)))
                throw new InvalidOperationException("Unsupported native scrapyard gate rotation fields.");
            string path = SharedBuildingDoors.StablePath(gate.transform) + "/scrapyard-gate-v1";
            var identities = new IdentityRegistry();
            ulong id = identities.Register(path);
            binding = new Binding { Id = id, Path = path, Gate = gate, LockedCollider = locked, NormalCollider = normal,
                Dust = dust, Pin = pin, Left = left, Right = right,
                LeftClosed = (Quaternion)LeftOriginalField.GetValue(gate), RightClosed = (Quaternion)RightOriginalField.GetValue(gate),
                LeftOpen = (Quaternion)LeftEndField.GetValue(gate), RightOpen = (Quaternion)RightEndField.GetValue(gate),
                Original = Capture(left, right, locked, normal, pin, dust) };
            registry.Bind(id, ScrapyardGateState.Adapter, gate.gameObject, path, true, EntityPersistence.SessionOnly);

#if MONMP_DEVELOPMENT
            Directory.CreateDirectory(diagnostics);


            File.WriteAllText(Path.Combine(diagnostics, "shared-scrapyard-gate.tsv"),
                "id\tgate-path\tleft\tright\n" + id.ToString("X16") + "\t" + path + "\t" + left.name + "\t" + right.name + "\n");
#endif

        }

        private static GameObject RequireObject(FieldInfo field, Scrapyard gate, string name)
        {
            if (field == null || field.FieldType != typeof(GameObject)) throw new InvalidOperationException("Unsupported Scrapyard." + name + " field.");
            return field.GetValue(gate) as GameObject ?? throw new InvalidOperationException("Scrapyard." + name + " is missing.");
        }
        private static Transform RequireTransform(FieldInfo field, Scrapyard gate, string name)
        {
            if (field == null || field.FieldType != typeof(Transform)) throw new InvalidOperationException("Unsupported Scrapyard." + name + " field.");
            return field.GetValue(gate) as Transform ?? throw new InvalidOperationException("Scrapyard." + name + " is missing.");
        }
        private static ScrapyardGateState Capture(Transform left, Transform right, GameObject locked, GameObject normal, GameObject pin, GameObject dust)
        {
            var l = left.localRotation; var r = right.localRotation;
            return new ScrapyardGateState(l.x, l.y, l.z, l.w, r.x, r.y, r.z, r.w,
                locked.activeSelf, normal.activeSelf, pin.activeSelf, dust.activeSelf);
        }
        private ScrapyardGateState Capture() => Capture(binding.Left, binding.Right, binding.LockedCollider, binding.NormalCollider, binding.Pin, binding.Dust);
        internal IEnumerable<WorldRecord> InitialRecords() => new[] { new WorldRecord(binding.Id, 1, ScrapyardGateState.Adapter, Capture().Encode()) };

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command; channel.ReplyReceived += Reply;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) => presence.Remove(peer);

        // A guest can request the native first-access action; police and friendship state stay
        // host-side, and all other guest calls that mutate the gate are suppressed.
        public bool AllowMutation(Scrapyard gate, string method)
        {
            if (!IsGuest || gate != binding.Gate || applying) return true;
            if (method == nameof(Scrapyard.OpenDoor)) RequestOpen();
            return false;
        }
        private void RequestOpen()
        {
            if (!Ready || MonMPPlugin.Instance?.World?.SaveGate.Active == true) return;
            channel!.Request(binding.Id, ScrapyardGateMessage.OpenCommand(), useLease: true);
        }

        private Vector3 Position => (binding.Left.position + binding.Right.position) * 0.5f;
        [MonMP.Core.World.WorldCommandAdapters(ScrapyardGateState.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || session?.IsHost != true || id != binding.Id || !ScrapyardGateMessage.IsOpenCommand(payload)) return;

#if MONMP_DEVELOPMENT
            TestRequests++;
#endif

            if (peer == 0 || !presence.TryGetValue(peer, out var value))
            {

#if MONMP_DEVELOPMENT
                TestMissingPose++;

 TestLastRejectReason = "missing-pose";
#endif
 return;
            }

LastPoseAge = Time.unscaledTime - value.Time;


#if MONMP_DEVELOPMENT
            TestLastPoseState = value.Pose.State;
#endif

            if (LastPoseAge > 2f)
            {

#if MONMP_DEVELOPMENT
                TestStalePose++;

 TestLastRejectReason = "stale-pose";
#endif
 return;
            }
            if (value.Pose.State != (byte)PlayerState.Walking)
            {

#if MONMP_DEVELOPMENT
                TestNonWalkingPose++;

 TestLastRejectReason = "not-walking";
#endif
 return;
            }
            var player = new Vector3(value.Pose.X, value.Pose.Y, value.Pose.Z);

LastDistance = Vector3.Distance(Position, player);

            if (LastDistance > CommandRange)
            {

#if MONMP_DEVELOPMENT
                TestOutOfRange++;

 TestLastRejectReason = "out-of-range";
#endif
 return;
            }
            if (MonMPPlugin.Instance?.World?.SaveGate.Active == true)
            {

#if MONMP_DEVELOPMENT
                TestSaveBlocked++;

 TestLastRejectReason = "save-gate";
#endif
 return;
            }

            // Mirror Scrapyard.OpenDoor's native guards so feedback is shown to the requesting guest.
            // Once admitted, invoke the native method itself so the host owns progression and animation.
            var gameplay = UnityEngine.Object.FindObjectOfType<Gameplay>();
            if (gameplay == null || gameplay.Store_PoliceInterceptor == null) return;
            if (gameplay.Store_PoliceInterceptor.Get_HasCriminalRecord)
            {

#if MONMP_DEVELOPMENT
                TestHostCriminalDenials++;

 TestLastRejectReason = "criminal-record";
#endif

                channel!.Reply(peer, binding.Id, ScrapyardGateMessage.EncodeReply(ScrapyardGateReply.CriminalRecord));
                return;
            }
            if (!Singleton<ToDoList_Manager>.i.isDone(ToDoList_Line.Intermediate_ScrapyardGateIsOpen) && GameManager.Get_Friendship[7] < 25)
            {

#if MONMP_DEVELOPMENT
                TestHostFriendshipDenials++;

 TestLastRejectReason = "friendship";
#endif

                channel!.Reply(peer, binding.Id, ScrapyardGateMessage.EncodeReply(ScrapyardGateReply.Friendship));
                return;
            }

#if MONMP_DEVELOPMENT
            TestLastRejectReason = "accepted";
#endif

            binding.Gate.OpenDoor();
            channel!.Publish(binding.Id, Capture().Encode());
        }
        private void Reply(ulong id, byte[] payload)
        {
            if (host || id != binding.Id) return;
            switch (ScrapyardGateMessage.DecodeReply(payload))
            {
                case ScrapyardGateReply.CriminalRecord:
                    Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText("ERROR_Scrapyard_CriminalRecoard_JacquesDoesntTrustYou"));
#if MONMP_DEVELOPMENT
TestCriminalRefusals++;
#endif
 break;
                case ScrapyardGateReply.Friendship:
                    Singleton<UI_ErrorMessage>.i.Show(TEXT.GetText("ERROR_Scrapyard_NotFriendEnoughWithJacques"));
#if MONMP_DEVELOPMENT
TestFriendshipRefusals++;
#endif
 break;
                default: throw new InvalidDataException("Unknown scrapyard gate denial.");
            }

#if MONMP_DEVELOPMENT
            TestRefusals++;
#endif

        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            // SnapshotApplied also fires for section/entity repairs. Those snapshots may only
            // contain an unrelated adapter; only the initial world snapshot is required to
            // include this statically registered record.
            if (!records.TryGetValue(binding.Id, out var record))
            {
                if (appliedReplica) return;
                throw new InvalidDataException("Initial scrapyard gate record is missing.");
            }
            if (record.Adapter != ScrapyardGateState.Adapter)
                throw new InvalidDataException("Initial scrapyard gate record has the wrong adapter.");
            appliedReplica = true; Apply(ScrapyardGateState.Decode(record.Payload));
        }
        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != ScrapyardGateState.Adapter) return;
            if (record.Id != binding.Id) throw new InvalidDataException("Unknown scrapyard gate record.");
            appliedReplica = true; Apply(ScrapyardGateState.Decode(record.Payload));
        }
        private void Apply(ScrapyardGateState state)
        {
            applying = true;
            try
            {
                // Scene teardown (quitting while connected) may already have destroyed these objects.
                if (binding.LockedCollider != null) binding.LockedCollider.SetActive(state.LockedCollider);
                if (binding.NormalCollider != null) binding.NormalCollider.SetActive(state.NormalCollider);
                if (binding.Pin != null) binding.Pin.SetActive(state.Pin);
                if (binding.Dust != null) binding.Dust.SetActive(state.Dust);
                if (binding.Left != null) binding.Left.localRotation = new Quaternion(state.LeftX, state.LeftY, state.LeftZ, state.LeftW);
                if (binding.Right != null) binding.Right.localRotation = new Quaternion(state.RightX, state.RightY, state.RightZ, state.RightW);
            }
            finally { applying = false; }
        }

        public void Update()
        {
            if (!host || !Ready || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 0.08f;
            channel!.Publish(binding.Id, Capture().Encode());
        }
        public void Dispose()
        {
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; channel.ReplyReceived -= Reply; }
            if (!host && appliedReplica) Apply(binding.Original);
            session = null; channel = null; presence.Clear();
        }


#if MONMP_DEVELOPMENT
        internal int TestRequests { get; private set; }


        internal int TestRefusals { get; private set; }


        internal ScrapyardGateState TestNative => Capture();


        internal ScrapyardGateState TestRecord => ScrapyardGateState.Decode(channel!.Records[binding.Id].Payload);


        internal Scrapyard TestGate => binding.Gate;


        internal Vector3 TestPosition => Position;


        internal ulong TestEntity => binding.Id;


        internal bool TestSynchronized => Ready && Quaternion.Angle(binding.Left.localRotation,
            new Quaternion(TestRecord.LeftX, TestRecord.LeftY, TestRecord.LeftZ, TestRecord.LeftW)) < 0.5f &&
            Quaternion.Angle(binding.Right.localRotation, new Quaternion(TestRecord.RightX, TestRecord.RightY, TestRecord.RightZ, TestRecord.RightW)) < 0.5f &&
            TestNative.LockedCollider == TestRecord.LockedCollider && TestNative.NormalCollider == TestRecord.NormalCollider &&
            TestNative.Pin == TestRecord.Pin && TestNative.Dust == TestRecord.Dust;


        internal bool TestRestored => Quaternion.Angle(binding.Left.localRotation, new Quaternion(binding.Original.LeftX, binding.Original.LeftY, binding.Original.LeftZ, binding.Original.LeftW)) < 0.5f &&
            Quaternion.Angle(binding.Right.localRotation, new Quaternion(binding.Original.RightX, binding.Original.RightY, binding.Original.RightZ, binding.Original.RightW)) < 0.5f &&
            TestNative.LockedCollider == binding.Original.LockedCollider && TestNative.NormalCollider == binding.Original.NormalCollider &&
            TestNative.Pin == binding.Original.Pin && TestNative.Dust == binding.Original.Dust;


        internal void TestRequest() => RequestOpen();


        internal void TestSetFriendship(int value) => GameManager.Get_Friendship[7] = value;


        internal void TestSetProgress(bool complete)
        {
            var line = Singleton<ToDoList_Manager>.i.Get_Lines[ToDoList_Line.Intermediate_ScrapyardGateIsOpen];
            line.value = complete; line.UpdateStrike();
        }


        internal void TestSetCriminalRecord(bool present)
        {
            var gameplay = UnityEngine.Object.FindObjectOfType<Gameplay>() ?? throw new InvalidOperationException("Gameplay is missing.");
            var police = gameplay.Store_PoliceInterceptor ?? throw new InvalidOperationException("Police store is missing.");
            var tickets = PoliceTicketsField.GetValue(police) as List<PoliceTicketData> ?? throw new InvalidOperationException("Police ticket list is missing.");
            tickets.RemoveAll(ticket => ticket.ConsequenceApplied);
            if (present) tickets.Add(new PoliceTicketData { ConsequenceApplied = true });
        }


        internal bool TestOpenMoving => Quaternion.Angle(binding.Left.localRotation, binding.LeftClosed) > 0.5f &&
            Quaternion.Angle(binding.Left.localRotation, binding.LeftOpen) > 0.5f;


        internal bool TestAtOpenEndpoint => Quaternion.Angle(binding.Left.localRotation, binding.LeftOpen) < 0.5f &&
            Quaternion.Angle(binding.Right.localRotation, binding.RightOpen) < 0.5f;


        internal bool TestHasCriminalRecord => UnityEngine.Object.FindObjectOfType<Gameplay>()?.Store_PoliceInterceptor?.Get_HasCriminalRecord == true;


        internal int TestFriendship => GameManager.Get_Friendship[7];


        internal bool TestProgress => Singleton<ToDoList_Manager>.i.isDone(ToDoList_Line.Intermediate_ScrapyardGateIsOpen);


        internal float TestLastPoseAge => LastPoseAge;
        internal float TestLastDistance => LastDistance;
#endif
    }
}
