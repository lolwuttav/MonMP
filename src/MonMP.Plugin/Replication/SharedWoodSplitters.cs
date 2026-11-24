using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NWH.VehiclePhysics2;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // #167: the wood splitter trailer's engine, kill switch and log intake belong to the host. Guests
    // send their pull-cord and switch presses; the host runs the native methods (fuel and kill-switch
    // checks included) and every peer mirrors engine, switch and split sound. Splitters are keyed by
    // their shared trailer id, so bought splitters join as SharedTrailers binds them.
    internal sealed class SharedWoodSplitters : IDisposable
    {
        public const ushort Adapter = 100;
        private static readonly ulong Id = StaticId.Hash("wood-splitters-v1");
        private static readonly FieldInfo RunningField = AccessTools.Field(typeof(WoodSplitter), "isEngineRunning");
        private static readonly FieldInfo KillSwitchOffField = AccessTools.Field(typeof(WoodSplitter), "isKillSwitchOff");
        private static readonly AccessTools.FieldRef<WoodSplitter, Transform> RamField = AccessTools.FieldRefAccess<WoodSplitter, Transform>("Ram");
        private static readonly AccessTools.FieldRef<WoodSplitter, Transform> HandleField = AccessTools.FieldRefAccess<WoodSplitter, Transform>("Handle");
        private static readonly AccessTools.FieldRef<WoodSplitter, Transform> RamMaxField = AccessTools.FieldRefAccess<WoodSplitter, Transform>("RamMaxPosition");
        private static readonly AccessTools.FieldRef<WoodSplitter, Vector3> RamOriginField = AccessTools.FieldRefAccess<WoodSplitter, Vector3>("RamOriginalPosition");
        internal const byte Start = 1, SwitchOn = 2, SwitchOff = 3;
        private const float CommandRange = 5f, CommandInterval = 0.35f;
        private static readonly byte[] Empty = { 0, 0 };
        private static readonly AccessTools.FieldRef<Furniture_SwitchButton, bool> ClickOnly = AccessTools.FieldRefAccess<Furniture_SwitchButton, bool>("isOnClickOnly");

        private sealed class Entry
        {
            public WoodSplitter Splitter = null!;
            public SOUND_WoodSplitter? Sound;
            public Furniture_SwitchButton? Switch;
            public bool Running, KillSwitchOff;
            public byte Cuts;
            public Coroutine? Ram;
            public Transform? ReplayRamTarget, ReplayHandleTarget;
            public Vector3 ReplayRamPosition;
            public Quaternion ReplayHandleRotation;
        }

        private readonly bool host;
        private readonly SharedTrailers trailers;
        private readonly Dictionary<ulong, Entry> entries = new Dictionary<ulong, Entry>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, float> lastPeerCommand = new Dictionary<uint, float>();
        private readonly Dictionary<ulong, bool> observedSwitches = new Dictionary<ulong, bool>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private byte[] published = Empty;
        private float nextCapture, nextScan, lastGuestCommand;
        private bool applying, disposed;
        private ulong nextInteraction;

        internal bool GuestBlocks => !host && !applying && !disposed && session?.IsRunning == true;
        internal int Count => entries.Count;

        internal SharedWoodSplitters(bool host, SharedTrailers trailers)
        {
            this.host = host; this.trailers = trailers;
        }

        internal IEnumerable<WorldRecord> InitialRecords() { yield return new WorldRecord(Id, 1, Adapter, Empty); }

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command;
        }

        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer) { presence.Remove(peer); lastPeerCommand.Remove(peer); }

        // Splitters are found through their trailer; trailers bought mid-session bind later, so rescan.
        private void Scan(bool periodic = false)
        {
            nextScan = periodic ? WorldScans.NextDeadline(Time.unscaledTime, 2f) : Time.unscaledTime + 2f;
            foreach (var splitter in WorldScans.Find<WoodSplitter>(periodic))
            {
                if (splitter == null || !splitter.gameObject.scene.IsValid()) continue;
                var vehicle = splitter.GetComponentInParent<VehicleController>();
                ulong id = vehicle == null ? 0 : trailers.IdOf(vehicle);
                if (id == 0 || entries.ContainsKey(id)) continue;
                // The pull cord is a click-only switch beside the stateful kill switch; only the latter
                // carries an on/off visual (#167 follow-up: two switches left the kill switch unsynced).
                var switches = vehicle!.GetComponentsInChildren<Furniture_SwitchButton>(true).Where(s => !ClickOnly(s) && Targets(s, splitter)).ToArray();
                entries[id] = new Entry
                {
                    Splitter = splitter, Sound = splitter.GetComponent<SOUND_WoodSplitter>(),
                    Switch = switches.Length == 1 ? switches[0] : null,
                    Running = (bool)RunningField.GetValue(splitter), KillSwitchOff = (bool)KillSwitchOffField.GetValue(splitter),
                };
                observedSwitches[id] = (bool)KillSwitchOffField.GetValue(splitter);
            }
            foreach (var gone in entries.Where(e => e.Value.Splitter == null).Select(e => e.Key).ToList())
            { entries.Remove(gone); observedSwitches.Remove(gone); }
        }

        private static bool Targets(Furniture_SwitchButton button, WoodSplitter splitter)
        {
            var e = button.Get_SwitchOn;
            for (int i = 0; i < e.GetPersistentEventCount(); i++)
                if (e.GetPersistentTarget(i) == splitter) return true;
            return button.transform.IsChildOf(splitter.transform);
        }

        private bool TryFind(WoodSplitter splitter, out ulong id)
        {
            if (!entries.Values.Any(e => e.Splitter == splitter)) Scan();
            id = entries.FirstOrDefault(e => e.Value.Splitter == splitter).Key;
            return id != 0;
        }

        internal bool TrySound(ulong entity, out SOUND_WoodSplitter? sound)
        {
            sound = entries.TryGetValue(entity, out var entry) && entry.Splitter != null && entry.Splitter.gameObject.activeInHierarchy ? entry.Sound : null;
            return sound != null;
        }

        internal IDisposable? ObserveNativeSwitch(Furniture_SwitchButton button)
        {
            if (!GuestBlocks) return null;
            if (Time.unscaledTime >= nextScan) Scan();
            return entries.Values.Any(e => e.Switch == button) ? MachineControlPresentation.NativeSwitch(button) : null;
        }

        // True when a guest's pull cord or kill switch went to the host instead of its private copy.
        internal bool Intercept(WoodSplitter splitter, byte kind)
        {
            if (!GuestBlocks) return false;
            if (kind < Start || kind > SwitchOff || splitter == null || !splitter.gameObject.activeInHierarchy ||
                MonMPPlugin.Instance?.World?.SaveGate.Active == true) return true;
            if (channel?.FullySynchronized == true && Time.unscaledTime - lastGuestCommand >= CommandInterval && TryFind(splitter, out ulong id))
            {
                lastGuestCommand = Time.unscaledTime;
                var entry = entries[id];
                bool presented = kind == Start
                    ? !(bool)RunningField.GetValue(splitter) && MachineControlPresentation.LocalPull(entry.Sound)
                    : MachineControlPresentation.SwitchAlreadyPlayed(entry.Switch);
                var payload = new byte[9];
                payload[0] = kind;
                BitConverter.GetBytes(id).CopyTo(payload, 1);
                if (presented) payload = new ClientInteractionRequest(++nextInteraction, payload).Encode();
                channel.Request(Id, payload, true);
            }
            return true;
        }

        [MonMP.Core.World.WorldCommandAdapters(SharedWoodSplitters.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || entity != Id || peer == 0 || payload == null || MonMPPlugin.Instance?.World?.SaveGate.Active == true) return;
            bool presented = ClientInteractionRequest.Is(payload);
            if (presented)
            {
                try { payload = ClientInteractionRequest.Decode(payload).Payload; }
                catch (InvalidDataException) { return; }
            }
            if (payload.Length != 9 || payload[0] < Start || payload[0] > SwitchOff) return;
            ulong id = BitConverter.ToUInt64(payload, 1);
            if (!entries.ContainsKey(id)) Scan();
            if (!entries.TryGetValue(id, out var entry) || entry.Splitter == null || !entry.Splitter.gameObject.activeInHierarchy) return;
            if (!presence.TryGetValue(peer, out var seen) || Time.unscaledTime - seen.Time > 2 ||
                (new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z) - entry.Splitter.transform.position).sqrMagnitude > CommandRange * CommandRange) return;
            if (Time.unscaledTime - (lastPeerCommand.TryGetValue(peer, out float last) ? last : -CommandInterval) < CommandInterval) return;
            lastPeerCommand[peer] = Time.unscaledTime;
            bool previousSwitch = (bool)KillSwitchOffField.GetValue(entry.Splitter);
            if (observedSwitches.TryGetValue(id, out bool observed) && previousSwitch != observed)
                MachineControlPresentation.AnnounceSwitch(id, previousSwitch, entry.Splitter.transform.position);
            observedSwitches[id] = previousSwitch;
            applying = true;
            try
            {
                using var spatial = SpatialSounds.AtPeer(peer);
                switch (payload[0])
                {
                    case Start:
                        using (MachineControlPresentation.HostPull(entry.Sound, id, presented ? peer : 0)) entry.Splitter.StartEngine();
                        break;
                    case SwitchOn: entry.Splitter.KillSwitchOn(); SetSwitch(entry, true); Singleton<SOUND_Manager>.i.PlaySound_SwitchOn(); break;
                    case SwitchOff: entry.Splitter.KillSwitchOff(); SetSwitch(entry, false); Singleton<SOUND_Manager>.i.PlaySound_SwitchOff(); break;
                }
            }
            finally { applying = false; }
            if (payload[0] != Start)
            {
                bool on = (bool)KillSwitchOffField.GetValue(entry.Splitter);
                observedSwitches[id] = on;
                MachineControlPresentation.AnnounceSwitch(id, on, entry.Splitter.transform.position, presented ? peer : 0);
            }
            // StartEngine's coroutine sets the flag after its 0.3 s pull; the next captures publish it.
            nextCapture = 0;
        }

        private static void SetSwitch(Entry entry, bool killSwitchOff)
        {
            if (entry.Switch == null || entry.Switch.isOn == killSwitchOff) return;
            entry.Switch.isOn = killSwitchOff;
            entry.Switch.UpdateVisual();
        }

        // Host: the native trigger accepted a log and started a cut; guests replay its split sound.
        internal void HostCut(WoodSplitter splitter)
        {
            if (!host || session?.IsRunning != true || !TryFind(splitter, out ulong id)) return;
            entries[id].Cuts++;
            nextCapture = 0;
        }

        private byte[] Encode()
        {
            var live = entries.Where(e => e.Value.Splitter != null).OrderBy(e => e.Key).ToList();
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((ushort)live.Count);
            foreach (var e in live)
            {
                writer.Write(e.Key);
                writer.Write((byte)(((bool)RunningField.GetValue(e.Value.Splitter) ? 1 : 0) | ((bool)KillSwitchOffField.GetValue(e.Value.Splitter) ? 2 : 0)));
                writer.Write(e.Value.Cuts);
            }
            return stream.ToArray();
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (!host && records.TryGetValue(Id, out var record)) Apply(record.Payload, false);
        }

        private void Changed(WorldRecord record)
        {
            if (!host && record.Id == Id && record.Adapter == Adapter) Apply(record.Payload, true);
        }

        private void Apply(byte[] payload, bool live)
        {
            if (payload == null || payload.Length < 2) return;
            int count = BitConverter.ToUInt16(payload, 0);
            if (payload.Length != 2 + count * 10) return;
            Scan();
            applying = true;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    int at = 2 + i * 10;
                    ulong id = BitConverter.ToUInt64(payload, at);
                    byte flags = payload[at + 8], cuts = payload[at + 9];
                    if (!entries.TryGetValue(id, out var entry) || entry.Splitter == null) continue;
                    bool running = (flags & 1) != 0, killSwitchOff = (flags & 2) != 0;
                    // Actor-bound interaction events carry switch clicks independently from
                    // coalesced state; the native requester's own click is never echoed.
                    KillSwitchOffField.SetValue(entry.Splitter, killSwitchOff);
                    SetSwitch(entry, killSwitchOff);
                    if ((bool)RunningField.GetValue(entry.Splitter) != running)
                    {
                        RunningField.SetValue(entry.Splitter, running);
                        if (running) entry.Sound?.StartEngine(); else entry.Sound?.StopEngine();
                    }
                    if (cuts != entry.Cuts)
                    {
                        entry.Cuts = cuts;
                        if (live)
                        {
                            entry.Sound?.Split();
                            if (entry.Ram == null && entry.Splitter.isActiveAndEnabled) { entry.Ram = entry.Splitter.StartCoroutine(ReplayRam(entry));
#if MONMP_DEVELOPMENT
TestRamReplays++;
#endif
 }
                        }
                    }
                }
            }
            finally { applying = false; }
        }

        // The guest never runs the native cut (ActivateRam), so its ram and lever stood still while the
        // host's log split (#167 follow-up). The same motion, without spawning halves (the host's are shared).

#if MONMP_DEVELOPMENT
        internal int TestRamReplays { get; private set; }


        internal bool? TestSwitchVisual(WoodSplitter splitter) => entries.Values.FirstOrDefault(e => e.Splitter == splitter)?.Switch?.isOn;
#endif

        private static System.Collections.IEnumerator ReplayRam(Entry entry)
        {
            var splitter = entry.Splitter;
            try
            {
                var ram = RamField(splitter); var handle = HandleField(splitter); var max = RamMaxField(splitter);
                if (ram == null || handle == null || max == null) yield break;
                entry.ReplayRamTarget = ram; entry.ReplayHandleTarget = handle;
                entry.ReplayRamPosition = ram.localPosition;
                entry.ReplayHandleRotation = handle.localRotation;
                var origin = RamOriginField(splitter);
                yield return new WaitForSeconds(0.6f);
                handle.localEulerAngles = new Vector3(-20f, 0f, 0f);
                for (float t = 0.8f; t > 0f; t -= Time.fixedDeltaTime)
                {
                    ram.localPosition = Vector3.Lerp(max.localPosition, origin, t / 0.8f);
                    yield return new WaitForFixedUpdate();
                }
                handle.localEulerAngles = Vector3.zero;
                yield return new WaitForSeconds(1f);
                handle.localEulerAngles = new Vector3(20f, 0f, 0f);
                for (float t = 1.1f; t > 0f; t -= Time.fixedDeltaTime)
                {
                    ram.localPosition = Vector3.Lerp(origin, max.localPosition, t / 1.1f);
                    yield return new WaitForFixedUpdate();
                }
                ram.localPosition = origin;
                handle.localEulerAngles = Vector3.zero;
            }
            finally { entry.Ram = null; }
        }

        private static void StopRamReplay(Entry entry)
        {
            if (entry.Ram == null) return;
            // Stop only the replay owned by this adapter, then restore its starting pose.
            // The native component survives disconnect; its host cutting coroutine is independent.
            var ram = entry.ReplayRamTarget; var handle = entry.ReplayHandleTarget;
            var position = entry.ReplayRamPosition; var rotation = entry.ReplayHandleRotation;
            if (entry.Splitter != null) entry.Splitter.StopCoroutine(entry.Ram);
            entry.Ram = null;
            if (ram != null) ram.localPosition = position;
            if (handle != null) handle.localRotation = rotation;
            entry.ReplayRamTarget = null; entry.ReplayHandleTarget = null;
        }

        internal void Update()
        {
            if (!host || disposed || session?.IsRunning != true || channel?.Ready != true || Time.unscaledTime < nextCapture) return;
            nextCapture = Time.unscaledTime + 0.25f;
            if (Time.unscaledTime >= nextScan && WorldScans.TryClaim<WoodSplitter>(true)) Scan(true);
            foreach (var pair in entries)
                if (pair.Value.Splitter != null)
                {
                    bool on = (bool)KillSwitchOffField.GetValue(pair.Value.Splitter);
                    if (observedSwitches.TryGetValue(pair.Key, out bool previous) && on != previous)
                        MachineControlPresentation.AnnounceSwitch(pair.Key, on, pair.Value.Splitter.transform.position);
                    observedSwitches[pair.Key] = on;
                }
            var next = Encode();
            if (next.SequenceEqual(published)) return;
            published = next;
            channel.Publish(Id, next);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; }
            if (!host)
                foreach (var entry in entries.Values)
                    if (entry.Splitter != null)
                    {
                        StopRamReplay(entry);
                        RunningField.SetValue(entry.Splitter, entry.Running);
                        KillSwitchOffField.SetValue(entry.Splitter, entry.KillSwitchOff);
                        if (!entry.Running) entry.Sound?.StopEngine();
                    }
            session = null; channel = null; presence.Clear(); entries.Clear(); observedSwitches.Clear();
        }
    }
}
