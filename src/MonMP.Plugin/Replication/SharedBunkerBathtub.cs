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
    // The host owns the native bathtub target. The vanilla save remains the durable store.
    internal sealed class SharedBunkerBathtub : IDisposable
    {
        private static readonly FieldInfo BathtubField = AccessTools.Field(typeof(Bunker_Bathtub_Button), "bunker_Bathtub");
        private static readonly FieldInfo TargetField = AccessTools.Field(typeof(Bunker_Bathtub), "TargetLocation");
        private static readonly FieldInfo LocationField = AccessTools.Field(typeof(Bunker_Bathtub), "BathLocation");
        private static readonly FieldInfo MovingField = AccessTools.Field(typeof(Bunker_Bathtub), "Coroutine_BathMoving");
        // Bunker_Bathtub.Start adds the hydraulic sound BunkerButton uses; a bunker the host's
        // progression just activated on a guest has not run Start yet.
        private static readonly FieldInfo SoundField = AccessTools.Field(typeof(Bunker_Bathtub), "SOUND_HydraulicMotor");
        private const float CommandRange = 4.5f;

        private readonly bool host;
        private readonly Bunker_Bathtub_Button button;
        private readonly Bunker_Bathtub bathtub;
        private readonly ulong id;
        private readonly BunkerBathtubState original;
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private BunkerBathtubState state;
        private bool applying, appliedReplica, snapshotReceived, disposed;
        private float nextCapture;

        internal bool Ready => channel?.Ready == true;

#if MONMP_DEVELOPMENT
        internal bool TestChannelAttached => channel != null && session?.IsRunning == true;


        internal bool TestRecordAvailable => channel != null && channel.Records.ContainsKey(id);


        internal bool TestBuilt => IsBuiltAndActive;


        internal bool TestSettled => Settled;


        internal int TestRequests { get; private set; }


        internal int TestAccepted { get; private set; }


        internal int TestRejected { get; private set; }


        internal string TestLastRejectReason { get; private set; } = "none";


        internal BunkerBathtubState TestState => state;


        internal BunkerBathtubState TestNative => Capture();


        internal Bunker_Bathtub_Button TestButton => button;


        internal Vector3 TestPosition => button.transform.position;


        internal ulong TestEntity => id;


        internal bool TestRestored => TargetOpen == original.TargetOpen && Settled;
#endif


        internal SharedBunkerBathtub(bool host, string diagnostics, NativeEntityRegistry registry)
        {
            this.host = host;
            if (BathtubField == null || BathtubField.FieldType != typeof(Bunker_Bathtub) ||
                TargetField == null || TargetField.FieldType != typeof(float) ||
                LocationField == null || LocationField.FieldType != typeof(float) ||
                MovingField == null || MovingField.FieldType != typeof(bool) ||
                SoundField == null || SoundField.FieldType != typeof(SOUND_HydraulicMotor))
                throw new InvalidOperationException("Unsupported native bunker bathtub layout.");

            var buttons = UnityEngine.Object.FindObjectsOfType<Bunker_Bathtub_Button>(true)
                .Where(value => value != null && value.gameObject.scene.IsValid()).ToArray();
            if (buttons.Length != 1) throw new InvalidOperationException("Expected one native bunker bathtub button; found " + buttons.Length + ".");
            button = buttons[0];
            bathtub = BathtubField.GetValue(button) as Bunker_Bathtub ??
                throw new InvalidOperationException("The native bunker bathtub button has no bathtub target.");
            string path = SharedBuildingDoors.StablePath(bathtub.transform) + "/bunker-bathtub-v1";
            id = new IdentityRegistry().Register(path);
            state = Capture(); original = state;
            registry.Bind(id, BunkerBathtubState.Adapter, bathtub.gameObject, path, true, EntityPersistence.NativeSave);

#if MONMP_DEVELOPMENT
            Directory.CreateDirectory(diagnostics);


            File.WriteAllText(Path.Combine(diagnostics, "shared-bunker-bathtub.tsv"),
                "id\tbathtub-path\tbutton-path\ttarget-open\n" + id.ToString("X16") + "\t" + path + "\t" +
                SharedBuildingDoors.StablePath(button.transform) + "\t" + TargetOpen + "\n");
#endif

        }

        private bool TargetOpen => ReadUnit(TargetField, "TargetLocation", bathtub) > 0.5f;
        private bool IsBuiltAndActive => bathtub != null && bathtub.gameObject.activeInHierarchy && SoundField.GetValue(bathtub) != null &&
            Singleton<ToDoList_Manager>.i != null && Singleton<ToDoList_Manager>.i.isDone(ToDoList_Line.Dealer_BunkerBuilt);
        private bool Settled => !((bool)MovingField.GetValue(bathtub)) &&
            Math.Abs(ReadUnit(LocationField, "BathLocation", bathtub) - ReadUnit(TargetField, "TargetLocation", bathtub)) < 0.0001f;

        private float ReadUnit(FieldInfo field, string name, Bunker_Bathtub target)
        {
            float value = (float)field.GetValue(target);
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > 1f)
                throw new InvalidDataException("Native bunker bathtub " + name + " is outside [0,1].");
            return value;
        }

        private BunkerBathtubState Capture() => new BunkerBathtubState(TargetOpen);
        internal IEnumerable<WorldRecord> InitialRecords() => new[] { new WorldRecord(id, 1, BunkerBathtubState.Adapter, Capture().Encode()) };

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command;
        }

        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) => presence.Remove(peer);

        internal bool AllowMutation(Bunker_Bathtub_Button candidate)
        {
            if (!IsGuest || candidate != button || applying) return true;
            RequestToggle();
            return false;
        }

        internal bool AllowDetector(Bunker_Bathtub candidate) => !IsGuest || candidate != bathtub || applying;

        private bool IsGuest => session?.IsRunning == true && !host && channel != null;
        private void RequestToggle()
        {
            if (!Ready || !IsBuiltAndActive || MonMPPlugin.Instance?.World?.SaveGate.Active == true) return;
            channel!.Request(id, BunkerBathtubMessage.ToggleCommand());
        }

        [MonMP.Core.World.WorldCommandAdapters(BunkerBathtubState.Adapter)]

        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || session?.IsHost != true || entity != id || !BunkerBathtubMessage.IsToggleCommand(payload)) return;

#if MONMP_DEVELOPMENT
            TestRequests++;
#endif

            if (peer == 0 || !presence.TryGetValue(peer, out var sample))
            { Reject("missing-pose"); return; }
            if (Time.unscaledTime - sample.Time > 2f)
            { Reject("stale-pose"); return; }
            if (sample.Pose.State != (byte)PlayerState.Walking)
            { Reject("not-walking"); return; }
            if (Vector3.Distance(new Vector3(sample.Pose.X, sample.Pose.Y, sample.Pose.Z), button.transform.position) > CommandRange)
            { Reject("out-of-range"); return; }
            if (MonMPPlugin.Instance?.World?.SaveGate.Active == true)
            { Reject("save-gate"); return; }
            if (!IsBuiltAndActive)
            { Reject("bunker-not-built"); return; }


#if MONMP_DEVELOPMENT
            TestLastRejectReason = "accepted";

 TestAccepted++;
#endif

            applying = true;
            try { button.MouseDown(); }
            finally { applying = false; }
            Publish(Capture());
        }

        private void Reject(string reason) {
#if MONMP_DEVELOPMENT
TestRejected++;

 TestLastRejectReason = reason;
#endif
 }
        private void Publish(BunkerBathtubState next)
        {
            if (next.Equals(state)) return;
            state = next; channel!.Publish(id, next.Encode());
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            if (!records.TryGetValue(id, out var record))
            {
                if (snapshotReceived) return;
                throw new InvalidDataException("Initial bunker bathtub record is missing.");
            }
            if (record.Adapter != BunkerBathtubState.Adapter)
                throw new InvalidDataException("Initial bunker bathtub record has the wrong adapter.");
            snapshotReceived = true;
            Apply(BunkerBathtubState.Decode(record.Payload));
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != BunkerBathtubState.Adapter) return;
            if (record.Id != id) throw new InvalidDataException("Unknown bunker bathtub record.");
            Apply(BunkerBathtubState.Decode(record.Payload));
        }

        private void Apply(BunkerBathtubState next)
        {
            state = next; appliedReplica = true;
            if (!IsBuiltAndActive || TargetOpen == next.TargetOpen) return;
            applying = true;
            try { button.MouseDown(); }
            finally { applying = false; }
            if (TargetOpen != next.TargetOpen)
                throw new InvalidOperationException("Native bunker bathtub button did not apply the host target.");
        }

        public void Update()
        {
            if (!host)
            {
                if (snapshotReceived && appliedReplica && IsBuiltAndActive && TargetOpen != state.TargetOpen) Apply(state);
                return;
            }
            if (!Ready || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 0.1f;
            Publish(Capture());
        }

        public void Dispose()
        {
            if (disposed) return;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            if (!host && appliedReplica && IsBuiltAndActive && TargetOpen != original.TargetOpen)
            {
                applying = true;
                try { button.MouseDown(); }
                finally { applying = false; }
            }
            disposed = true; presence.Clear(); session = null; channel = null;
        }


#if MONMP_DEVELOPMENT
        internal void TestRequest() => RequestToggle();


        internal void TestSetTarget(bool targetOpen)
        {
            if (TargetOpen == targetOpen) return;
            applying = true;
            try { button.MouseDown(); }
            finally { applying = false; }
        }


        internal bool TestTargetOpen => TargetOpen;
#endif

    }
}
