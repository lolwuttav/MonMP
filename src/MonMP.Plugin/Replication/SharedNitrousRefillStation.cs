using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using NWH.VehiclePhysics2.VehicleGUI;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    internal sealed class SharedNitrousRefillStation : IDisposable
    {
        private static readonly FieldInfo GaugeField = AccessTools.Field(typeof(NitrousRefillStation), "NitrousPressureGauge");
        private static readonly FieldInfo WorkingField = AccessTools.Field(typeof(NitrousRefillStation), "isWorking");
        private static readonly FieldInfo SoundField = AccessTools.Field(typeof(NitrousRefillStation), "SOUND_NitrousRefillStation");
        private static readonly FieldInfo LoopField = AccessTools.Field(typeof(SOUND_NitrousRefillStation), "NitrousFill");
        private static readonly FieldInfo OneShotField = AccessTools.Field(typeof(SOUND_NitrousRefillStation), "OneShotAudio");
        private sealed class AudioOriginal
        {
            internal AudioSource Source = null!;
            internal bool Playing;
            internal float Time, Volume, Pitch;
            internal AudioOriginal(AudioSource source)
            { Source = source; Playing = source.isPlaying; Time = source.time; Volume = source.volume; Pitch = source.pitch; }
            internal void Restore()
            {
                if (Source == null) return;
                Source.Stop(); Source.volume = Volume; Source.pitch = Pitch;
                if (Source.clip != null) Source.time = Mathf.Clamp(Time, 0, Source.clip.length);
                if (Playing) Source.Play();
            }
        }
        private sealed class Binding
        {
            internal ulong Id;
            internal NitrousRefillStation Station = null!;
            internal NitrousRefillStationState State;
            internal AnalogGauge? OriginalGauge;
            internal float OriginalPressure;
            internal AudioOriginal? OriginalLoop, OriginalOneShot;
            internal bool Applied;
            internal readonly Queue<(PresentationEvent Event, float Expires)> Pending = new Queue<(PresentationEvent, float)>();
        }
        private readonly bool host;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private readonly Dictionary<NitrousRefillStation, Binding> byStation = new Dictionary<NitrousRefillStation, Binding>();
        private NetworkSession? session;
        private WorldChannel? channel;
        private SOUND_Manager? soundManager;
        private float nextCapture;
        private bool disposed;
        private readonly int[] cueCounts = new int[2];
        private readonly bool[] cueSounds = new bool[2];
        internal int Count => bindings.Count;
        internal bool IsFilling(NitrousRefillStation station) => station != null && byStation.TryGetValue(station, out var b) &&
            (host ? (bool)WorkingField.GetValue(station) : b.State.Filling);

#if MONMP_DEVELOPMENT
        internal NitrousRefillStation? TestStation => bindings.Values.Select(b => b.Station).FirstOrDefault();


        internal NitrousRefillStationState TestState(NitrousRefillStation station)
        {
            if (station == null) return default;
            var gauge = GaugeField.GetValue(station) as AnalogGauge;
            var loop = Source(Sound(station), LoopField);
            return new NitrousRefillStationState(loop != null && loop.isPlaying, Mathf.Clamp01(gauge == null ? 0 : gauge.Value));
        }


        internal NitrousRefillStationState TestCanonicalState(NitrousRefillStation station) => byStation[station].State;


        internal int TestCueCount(byte variant) => variant < 2 ? cueCounts[variant] : 0;


        internal bool TestCueSoundObserved(byte variant) => variant < 2 && cueSounds[variant];
#endif


        internal SharedNitrousRefillStation(bool host, string diagnostics, NativeEntityRegistry identities)
        {
            this.host = host;
            foreach (var station in Object.FindObjectsOfType<NitrousRefillStation>(true))
            {
                if (station == null || !station.gameObject.scene.IsValid()) continue;
                string identity = SharedBuildingDoors.StablePath(station.transform) + "/nitrous-refill-presentation-v1";
                ulong id = StaticId.Hash(identity);
                var b = new Binding { Id = id, Station = station, State = Capture(station) };
                identities.Bind(id, NitrousRefillStationState.Adapter, station.gameObject, identity, true, EntityPersistence.SessionOnly);
                bindings.Add(id, b); byStation.Add(station, b);
            }

#if MONMP_DEVELOPMENT
            File.WriteAllLines(Path.Combine(diagnostics, "shared-nitrous-refill-stations.tsv"),
                new[] { "id\tidentity" }.Concat(bindings.Values.Select(b => b.Id.ToString("X16") + "\t" + RuntimeProbe.CanonicalPath(b.Station.transform))));
#endif

        }

        internal IEnumerable<WorldRecord> InitialRecords() => bindings.Values.Select(b =>
            new WorldRecord(b.Id, 1, NitrousRefillStationState.Adapter, Capture(b.Station).Encode()));

        internal void Attach(NetworkSession connected, WorldChannel world)
        {
            session = connected; channel = world;
            world.SnapshotApplied += Snapshot; world.Changed += Changed;
        }
        private static SOUND_NitrousRefillStation? Sound(NitrousRefillStation station) => SoundField.GetValue(station) as SOUND_NitrousRefillStation;
        private static AudioSource? Source(SOUND_NitrousRefillStation? sound, FieldInfo field) => sound == null ? null : field.GetValue(sound) as AudioSource;
        private static NitrousRefillStationState Capture(NitrousRefillStation station)
        {
            if (station == null) return default;
            var gauge = GaugeField.GetValue(station) as AnalogGauge;
            return new NitrousRefillStationState((bool)WorkingField.GetValue(station), Mathf.Clamp01(gauge == null ? 0 : gauge.Value));
        }

        // Native sound methods already played locally; only their one-shots are relayed.
        internal void NativeCue(SOUND_NitrousRefillStation sound, byte variant)
        {
            if (!host || disposed || session?.IsRunning != true || SharedPresentationEvents.Replaying || variant > 1 || sound == null) return;
            var station = sound.GetComponent<NitrousRefillStation>();
            if (station != null && byStation.TryGetValue(station, out var b))
            {
                ObserveCue(sound, variant);
                PresentationReplays.Post(PresentationKind.NitrousRefillStation, variant, b.Id, station.transform.position);
            }
        }

        internal void PlayCue(PresentationEvent e)
        {
            if (host || disposed || e.Variant > 1 || !bindings.TryGetValue(e.Target, out var b) || b.Station == null) return;
            if (!TryPlayCue(b, e) && b.Pending.Count < 8) b.Pending.Enqueue((e, Time.unscaledTime + 2f));
        }
        private bool TryPlayCue(Binding b, PresentationEvent e)
        {
            var sound = Sound(b.Station);
            var source = Source(sound, OneShotField);
            // Avoid the native singleton getter, which creates an uninitialized manager when absent.
            if (soundManager == null) soundManager = Object.FindObjectOfType<SOUND_Manager>();
            var manager = soundManager;
            if (sound == null || source == null || manager == null) return false;
            var clip = e.Variant == 0 ? manager.Part_Installed : manager.Part_Uninstalled;
            if (clip == null) return false;
            if (b.OriginalOneShot == null) b.OriginalOneShot = new AudioOriginal(source);
            sound.PlayOneShot(source, clip);
            ObserveCue(sound, e.Variant);
            return true;
        }
        private void ObserveCue(SOUND_NitrousRefillStation sound, byte variant)
        {

#if MONMP_DEVELOPMENT
            if (MonMPPlugin.Instance?.TestOptions.Enabled != true) return;
#endif

            cueCounts[variant]++;
            var source = Source(sound, OneShotField);
            cueSounds[variant] |= source != null && source.isPlaying;
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            // A repair/rejoin snapshot advances the event log to history; queued old one-shots must
            // not restart attachment/removal when their sources finally become available.
            foreach (var b in bindings.Values) b.Pending.Clear();
            foreach (var record in records.Values) if (record.Adapter == NitrousRefillStationState.Adapter) Changed(record);
        }
        private void Changed(WorldRecord record)
        {
            if (host || record.Adapter != NitrousRefillStationState.Adapter || !bindings.TryGetValue(record.Id, out var b)) return;
            b.State = NitrousRefillStationState.Decode(record.Payload); b.Applied = true;
            Apply(b);
        }
        private static void Apply(Binding b)
        {
            if (b.Station == null) return;
            var gauge = GaugeField.GetValue(b.Station) as AnalogGauge;
            if (gauge != null)
            {
                if (b.OriginalGauge == null) { b.OriginalGauge = gauge; b.OriginalPressure = gauge.Value; }
                gauge.Value = b.State.PressureValue;
            }
            var loop = Source(Sound(b.Station), LoopField);
            if (loop == null) return;
            if (b.OriginalLoop == null) b.OriginalLoop = new AudioOriginal(loop);
            // StartFilling/StopFilling also emit one-shots; state application must only touch the loop.
            if (loop.isPlaying != b.State.Filling) { if (b.State.Filling) loop.Play(); else loop.Pause(); }
        }

        internal void Update()
        {
            if (disposed || session?.IsRunning != true || channel?.Ready != true) return;
            if (host)
            {
                if (Time.unscaledTime < nextCapture) return;
                nextCapture = Time.unscaledTime + 0.1f;
                foreach (var b in bindings.Values) if (b.Station != null)
                {
                    b.State = Capture(b.Station);
                    channel.Publish(b.Id, b.State.Encode());
                }
                return;
            }
            foreach (var b in bindings.Values)
            {
                if (b.Station == null) continue;
                if (b.Applied) Apply(b); // Sound component/source Start may follow the snapshot by a frame.
                while (b.Pending.Count > 0)
                {
                    var cue = b.Pending.Peek();
                    if (Time.unscaledTime >= cue.Expires) { b.Pending.Dequeue(); continue; }
                    if (!TryPlayCue(b, cue.Event)) break;
                    b.Pending.Dequeue();
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; }
            try
            {
                if (!host) foreach (var b in bindings.Values)
                {
                    if (b.OriginalGauge != null) b.OriginalGauge.Value = b.OriginalPressure;
                    b.OriginalLoop?.Restore(); b.OriginalOneShot?.Restore();
                }
            }
            finally { session = null; channel = null; soundManager = null; bindings.Clear(); byStation.Clear(); }
        }
    }
}
