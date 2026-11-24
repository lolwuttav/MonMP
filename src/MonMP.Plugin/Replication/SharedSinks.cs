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
    // Sink and bathroom faucets: the host clicks its native knobs (electricity rules included) and
    // guests mirror both knob positions plus the running-water effect and sound.
    internal sealed class SharedSinks : IDisposable
    {
        public const ushort Adapter = 93;
        private const float CommandRange = 4.5f;
        private sealed class Binding
        {
            public ulong Id;
            public Furniture_SinkWater Sink = null!;
            public Furniture_SwitchButton? Cold, Hot;
            public byte Original;
        }
        private static readonly FieldInfo ColdField = AccessTools.Field(typeof(Furniture_SinkWater), "Knob_Cold");
        private static readonly FieldInfo HotField = AccessTools.Field(typeof(Furniture_SinkWater), "Knob_Hot");
        private static readonly MethodInfo UpdateVisualMethod = AccessTools.Method(typeof(Furniture_SinkWater), "UpdateVisual", Type.EmptyTypes);
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<Furniture_SwitchButton, (Binding Binding, byte Knob)> knobs = new Dictionary<Furniture_SwitchButton, (Binding, byte)>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly bool host;
        private NetworkSession? session;
        private WorldChannel? channel;
        private bool appliedReplica;
        private float nextCapture;
        public int Count => bindings.Count;

        public SharedSinks(bool host, string diagnostics, NativeEntityRegistry registry)
        {
            this.host = host;
            var lines = new List<string>();
            if (ColdField != null && HotField != null && UpdateVisualMethod != null)
                foreach (var sink in UnityEngine.Object.FindObjectsOfType<Furniture_SinkWater>(true))
                {
                    if (!sink.gameObject.scene.IsValid() || Spawned(sink.transform)) continue;
                    var cold = ColdField.GetValue(sink) as Furniture_SwitchButton;
                    var hot = HotField.GetValue(sink) as Furniture_SwitchButton;
                    if (cold == null && hot == null) continue;
                    if ((cold != null && knobs.ContainsKey(cold)) || (hot != null && knobs.ContainsKey(hot))) continue;
                    string path = SharedBuildingDoors.StablePath(sink.transform) + "/sink-v1";
                    ulong id = StaticId.Hash(path);
                    if (bindings.ContainsKey(id)) continue;
                    var b = new Binding { Id = id, Sink = sink, Cold = cold, Hot = hot };
                    b.Original = Capture(b);
                    bindings.Add(id, b);
                    if (cold != null) knobs[cold] = (b, 1);
                    if (hot != null) knobs[hot] = (b, 2);
                    registry.Bind(id, Adapter, sink.gameObject, path, true, EntityPersistence.SessionOnly);
                    lines.Add(id.ToString("X16") + "\t" + path + "\tcold=" + (cold != null) + "\thot=" + (hot != null));
                }
            lines.Sort(StringComparer.Ordinal);

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-sinks.tsv"), new[] { "id\tsink-path\tknobs" }.Concat(lines));
#endif

        }

        private static bool Spawned(Transform transform)
        {
            for (var current = transform; current != null; current = current.parent)
                if (current.name.Contains("(Clone)") || current.GetComponent<InteractableItems>() != null) return true;
            return false;
        }

        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b => new WorldRecord(b.Id, 1, Adapter, new[] { b.Original }));

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command;
            channel.ReplyReceived += Reply;
        }
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) => presence.Remove(peer);

        // True when a guest's knob click was sent to the host instead of running natively.
        public bool Intercept(Furniture_SwitchButton knob)
        {
            if (host || session?.IsRunning != true || !knobs.TryGetValue(knob, out var entry)) return false;
            if (channel?.Ready == true) channel.Request(entry.Binding.Id, new[] { entry.Knob });
            return true;
        }

        [MonMP.Core.World.WorldCommandAdapters(SharedSinks.Adapter)]
        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || !bindings.TryGetValue(id, out var b) || payload.Length != 1 || peer == 0) return;
            var knob = payload[0] == 1 ? b.Cold : payload[0] == 2 ? b.Hot : null;
            if (knob == null || !knob.gameObject.activeInHierarchy) return;
            if (!presence.TryGetValue(peer, out var p) || Time.unscaledTime - p.Time > 2) return;
            if ((new Vector3(p.Pose.X, p.Pose.Y, p.Pose.Z) - knob.transform.position).sqrMagnitude > CommandRange * CommandRange) return;
            Patches.RemoteActionScope.LastError = null;
            using (Patches.RemoteActionScope.Enter()) knob.MouseDown();
            if (Patches.RemoteActionScope.LastError is string error)
                channel!.Reply(peer, id, NativeErrorReply.Encode(error));
            Publish(b);
        }

        private void Reply(ulong id, byte[] payload)
        {
            if (!host && bindings.ContainsKey(id) && NativeErrorReply.TryDecode(payload, out string error))
                Singleton<UI_ErrorMessage>.i?.Show(error);
        }

        private void Publish(Binding b)
        {
            if (b.Sink == null) return;
            channel!.Publish(b.Id, new[] { Capture(b) });
        }

        private static byte Capture(Binding b) => (byte)((b.Cold != null && b.Cold.isOn ? 1 : 0) | (b.Hot != null && b.Hot.isOn ? 2 : 0));

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values)
                if (record.Adapter == Adapter && bindings.TryGetValue(record.Id, out var b) && record.Payload.Length == 1) Apply(b, record.Payload[0]);
            appliedReplica = true;
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != Adapter || !bindings.TryGetValue(record.Id, out var b) || record.Payload.Length != 1) return;
            Apply(b, record.Payload[0]);
            appliedReplica = true;
        }

        private static void Apply(Binding b, byte state)
        {
            if (b.Sink == null) return;
            if (b.Cold != null) { b.Cold.isOn = (state & 1) != 0; b.Cold.UpdateVisual(); }
            if (b.Hot != null) { b.Hot.isOn = (state & 2) != 0; b.Hot.UpdateVisual(); }
            // Only the particle/sound refresh; the electricity check runs on the host's click.
            if (b.Sink.gameObject.activeInHierarchy) UpdateVisualMethod.Invoke(b.Sink, null);
        }

        public void Update()
        {
            if (session?.IsRunning != true || channel?.Ready != true || Time.unscaledTime < nextCapture) return;
            if (host)
            {
                nextCapture = Time.unscaledTime + 0.2f;
                foreach (var b in bindings.Values) Publish(b);
            }
            else if (appliedReplica)
            {
                // A sink activated after the snapshot runs its native Start with knobs off.
                nextCapture = Time.unscaledTime + 1f;
                foreach (var b in bindings.Values)
                    if (b.Sink != null && channel.Records.TryGetValue(b.Id, out var record) && record.Payload.Length == 1 && Capture(b) != record.Payload[0])
                        Apply(b, record.Payload[0]);
            }
        }

        public void Dispose()
        {
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; channel.ReplyReceived -= Reply; }
            if (!host && appliedReplica) foreach (var b in bindings.Values) Apply(b, b.Original);
            session = null; channel = null; presence.Clear();
        }
    }
}
