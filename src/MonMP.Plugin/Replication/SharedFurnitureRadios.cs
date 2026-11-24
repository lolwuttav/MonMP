using System;
using System.Buffers.Binary;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using UnityEngine;
using UnityEngine.Networking;

namespace MonMP.Plugin.Replication
{
    // House, vehicle and furniture radios. The host runs every native button, vehicle-ignition and
    // generator rule and publishes power/band/station/volume; guests preview a bounded queue
    // of button intents and reconcile exact host receipts. CD content remains host-selected.
    // The station broadcast (song and offset per station) follows the host so tuned radios match.
    internal sealed partial class SharedFurnitureRadios : IDisposable
    {
        private static readonly System.Reflection.FieldInfo RadioVehicle = AccessTools.Field(typeof(Furniture_Radio), "vc");
        private float nextAudioRecovery;
        private sealed class Binding
        {
            public ulong Id;
            public ulong CdId;
            public string Identity = "";
            public Furniture_Radio Radio = null!;
            public SOUND_Radio Sound = null!;
            public int HostTrackIndex = -1;
            public AudioClip? HostTrackClip;
            public bool HostTrackLookedUp;
            public HostMedia? HostTrack;
            public uint CdRevision;
            public float LastHostCdTime;
            public byte[]? AppliedTrackHash;
            public uint AppliedRevision;
            public float AppliedTime, AppliedAt;
            public int DisplayTrackIndex = int.MinValue;
            public string DisplayTrackText = "";
            public bool VolumeScreenVisible;
            public float DisplayVolume;
            public bool OffPlaybackNoted;
            public readonly RadioPresentationIntent Intent = new RadioPresentationIntent();
        }
        private sealed class HostMedia
        {
            public string Path = "";
            public byte[] Hash = Array.Empty<byte>();
            public uint Length;
            public uint DecodedBytes;
            public CdMediaFormat Format;
            public long LastWriteUtcTicks;
            public Task<byte[]>? HashTask;
            public CancellationTokenSource? Cancellation;
            public long LastProgressTimestamp;
            public bool HashReady, HashFailed, CachedUpload;
        }
        private sealed class OutboundMedia
        {
            public uint Peer, TransferId;
            public ulong Entity;
            public string MediaKey = "";
            public byte[] Hash = Array.Empty<byte>();
            public byte[] Bytes = Array.Empty<byte>();
            public CdMediaFormat Format;
            public uint DecodedBytes;
            public ushort ChunkCount, NextChunk;
            public float StartedAt, NextSendAt;
            public float LastProgressAt;
            public bool BeginSent, Prefetch;
        }
        private sealed class PendingMediaRequest
        {
            public uint Peer;
            public ulong Entity;
            public bool Prefetch;
            public ushort ResumeChunk;
        }
        private sealed class PendingMediaRead
        {
            public string Key = "";
            public HostMedia Source = null!;
            public Task<byte[]> Work = null!;
            public CancellationTokenSource Cancellation = null!;
            public List<PendingMediaRequest> Requests = new List<PendingMediaRequest>();
            public float StartedAt;
            public long LastProgressTimestamp;
        }
        private sealed class HostMediaBuffer
        {
            public byte[] Bytes = Array.Empty<byte>();
            public CdMediaFormat Format;
            public uint DecodedBytes;
            public int References;
        }
        private sealed class CachedMedia
        {
            public AudioClip Clip = null!;
            public AudioClip? CatalogueClip;
            public int Index;
            public long DecodedBytes;
            public long MemoryBytes;
            public long FileBytes;
            public string Path = "";
            public ulong LastUsed;
        }
        private sealed class MediaReservation
        {
            public long FileBytes, DecodedBytes, MemoryBytes;
        }
        private sealed class MediaRetry
        {
            public int Attempts;
            public float NextAttemptAt;
        }
        private sealed class LocalMediaLookup
        {
            public CdMediaMessage Announcement = null!;
            public Task<byte[]?> Work = null!;
            public bool Prefetch;
        }
        private static readonly AccessTools.FieldRef<Furniture_Radio, bool> IsOn = AccessTools.FieldRefAccess<Furniture_Radio, bool>("isOn");
        private static readonly AccessTools.FieldRef<Furniture_Radio, float> Station = AccessTools.FieldRefAccess<Furniture_Radio, float>("CurrentStation");
        private static readonly AccessTools.FieldRef<Furniture_Radio, int> Band = AccessTools.FieldRefAccess<Furniture_Radio, int>("FMCD");
        private static readonly AccessTools.FieldRef<Furniture_Radio, SOUND_Radio> Sound = AccessTools.FieldRefAccess<Furniture_Radio, SOUND_Radio>("SOUND_Radio");
        private static readonly AccessTools.FieldRef<SOUND_Radio, float> Volume = AccessTools.FieldRefAccess<SOUND_Radio, float>("RadioVolume");
        private static readonly AccessTools.FieldRef<SOUND_Radio, int> CdSong = AccessTools.FieldRefAccess<SOUND_Radio, int>("CurrentCDSong");
        private static readonly AccessTools.FieldRef<SOUND_Radio, float> CdTime = AccessTools.FieldRefAccess<SOUND_Radio, float>("CurrentCDTime");
        private static readonly AccessTools.FieldRef<Store_Bar, AudioSource> BarMusic = AccessTools.FieldRefAccess<Store_Bar, AudioSource>("MusicPlaying");
        // Native station order: rock, jazz, pop, electronic, rap, country, then the bar's own music.
        private static readonly string[] StationNames = { "Rock", "Jazz", "Pop", "Electronic", "Rap", "Country", "Bar" };
        private static readonly float[] StationFrequencies = { 100.9f, 104.3f, 107.5f, 102.1f, 91.9f, 98.9f, 0f };
        private static readonly AccessTools.FieldRef<SOUND_Manager, int>[] Songs = StationNames.Select(n => AccessTools.FieldRefAccess<SOUND_Manager, int>("Current" + n + "Song")).ToArray();
        private static readonly AccessTools.FieldRef<SOUND_Manager, float>[] Times = StationNames.Select(n => AccessTools.FieldRefAccess<SOUND_Manager, float>("Current" + n + "Time")).ToArray();
        private static readonly AccessTools.FieldRef<SOUND_Manager, AudioClip[]>[] Clips = StationNames.Select(n => AccessTools.FieldRefAccess<SOUND_Manager, AudioClip[]>("RADIO_" + n)).ToArray();
        private static readonly System.Reflection.MethodInfo UpdateStation = AccessTools.Method(typeof(SOUND_Manager), "UpdateRadio", new[] { typeof(float) });
        private static readonly System.Reflection.MethodInfo UpdateBar = AccessTools.Method(typeof(SOUND_Manager), "UpdateBarRadio");
        private const float Reach = 4.5f;
        // Native SOUND_Radio.Start gives speakers a 35m audible range. A listener
        // must not need button reach merely to receive the selected music.
        private const float CdAudibleReach = 35f;

        private readonly bool host;
        private readonly SharedItems items;
        private readonly Dictionary<ulong, Binding> bindings = new Dictionary<ulong, Binding>();
        private Binding[]? bindingSnapshot;
        private readonly Dictionary<Furniture_Radio, Binding> byRadio = new Dictionary<Furniture_Radio, Binding>();
        private readonly FurnitureDiscoveryCache discovery = new FurnitureDiscoveryCache();
        private readonly Dictionary<ulong, FurnitureRadioState> pending = new Dictionary<ulong, FurnitureRadioState>();
        private readonly Dictionary<ulong, CdPlaybackState> pendingCd = new Dictionary<ulong, CdPlaybackState>();
        private readonly Dictionary<ulong, float> pendingCdAt = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, Binding> byCdId = new Dictionary<ulong, Binding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<uint, OutboundMedia> outboundMedia = new Dictionary<uint, OutboundMedia>();
        private readonly Dictionary<string, HostMediaBuffer> hostMediaBuffers = new Dictionary<string, HostMediaBuffer>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PendingMediaRead> pendingMediaReads = new Dictionary<string, PendingMediaRead>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HostMedia> hostMediaByPath = new Dictionary<string, HostMedia>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, (byte[] Hash, float At)> mediaRequestTimes = new Dictionary<uint, (byte[], float)>();
        private readonly Dictionary<string, CachedMedia> transferredMedia = new Dictionary<string, CachedMedia>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> loadingMedia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> rejectedMedia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MediaReservation> mediaReservations = new Dictionary<string, MediaReservation>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MediaRetry> mediaRetries = new Dictionary<string, MediaRetry>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> mediaWarnings = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<AudioClipLoadType> loggedCdAudioModes = new HashSet<AudioClipLoadType>();
        private readonly HashSet<string> localMediaAttempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LocalMediaLookup> localMediaLookups = new Dictionary<string, LocalMediaLookup>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Task<bool>, string> cacheFileWrites = new Dictionary<Task<bool>, string>();
        private readonly CancellationTokenSource cacheCancellation = new CancellationTokenSource();
        private readonly SemaphoreSlim nativeFileIo = new SemaphoreSlim(1, 1);
        private readonly CdMediaAssembler mediaAssembler = new CdMediaAssembler();
        private NetworkSession? session;
        private WorldChannel? channel;
        private float nextScan, nextPublish, nextClock, nextClockRefresh, nextReconcile;
        private Furniture_Radio[]? stagedRadios;
        private bool periodicDiscoveryStarted;
        private short[]? publishedSongs;
        private int publishedCdSongCount = -1;
        private float nextCdIndicatorUpdate;
        private static readonly byte[] EmptyCdHash = new byte[CdPlaybackState.HashSize];
        private uint nextTransferId;
        private ulong guestRequestEntity;
        private byte[]? guestRequestedHash;
        private float guestRequestAt;
        private readonly string cacheDirectory = NativeCdDirectory.CachePath;
        private readonly Dictionary<AudioClip, HostMedia> playlistMedia = new Dictionary<AudioClip, HostMedia>();
        private readonly HashSet<uint> mediaPeersReady = new HashSet<uint>();
        private readonly Dictionary<uint, HashSet<string>> offeredToPeer = new Dictionary<uint, HashSet<string>>();
        private readonly Dictionary<string, CdMediaMessage> playlistOffers = new Dictionary<string, CdMediaMessage>(StringComparer.Ordinal);
        private readonly HashSet<string> preloadReady = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> preloadChecked = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> preloadFailed = new HashSet<string>(StringComparer.Ordinal);
        private bool guestPrefetch;
        private float nextPlaylistScan, nextPlaylistOffer, nextPreload;
        private int playlistIndex;
        private CdMediaMessage? guestAnnouncement;
        private long decodedCacheBytes;
        private long reservedDecodedBytes, reservedFileBytes;
        private ulong mediaUseOrder;
        private long hostBufferedBytes;
        private long pendingHostReadBytes, transferredFileBytes;
        private float mediaBudget = CdMediaPacing.HostBurstBytes, mediaBudgetAt;
        private uint mediaFairnessPeer;
        private const long MaximumHostBufferedBytes = 32L * 1024 * 1024;
        // Leave room for current radio selections beyond the 64-track preload catalog.
        private const int MaximumHostTrackMetadata = 128;
        private const float MediaRequestRetrySeconds = 20f;
        private bool clockApplied, disposed;
        private uint nativeButtonActor;
        private ulong nextButtonIntent;
        private const int MaximumPendingRadios = 8;
        // Set while this adapter drives native radio methods so the guest intercepts let them through.
        internal static bool Applying { get; private set; }
        internal int Count => bindings.Count;
        private bool Active => !disposed && session?.IsRunning == true && channel?.Ready == true;
        internal bool GuestSyncing => Active && !host && clockApplied;
        internal bool Guest => !host;

        public SharedFurnitureRadios(bool host, SharedItems items) { this.host = host; this.items = items; }

        public void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session; this.channel = channel;
            session.PoseReceived += OnPose; session.PeerLeft += OnLeft;
            session.CdMediaReceived += OnCdMedia;
            channel.SnapshotApplied += Snapshot; channel.Changed += Changed; channel.CommandRequested += Command; channel.PeerSynchronized += OnMediaPeerReady;
            channel.ReplyReceived += Reply;
            channel.CommandRejected += ButtonRejected;
            BindNativeCdMenu();
            // The host registers its dynamic records from Update, once the session is hosting.
        }
        private void OnMediaPeerReady(uint peer) => mediaPeersReady.Add(peer);
        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);
        private void OnLeft(uint peer)
        {
            presence.Remove(peer); mediaRequestTimes.Remove(peer); offeredToPeer.Remove(peer); mediaPeersReady.Remove(peer);
            cdPeerProgress.Remove(peer);
            cdUploadReplies.Remove(peer);
            if (incomingCdUpload?.Peer == peer) EndCdUpload(CdUploadStatus.Rejected);
            if (outboundMedia.TryGetValue(peer, out var transfer)) FinishOutbound(transfer);
            foreach (var pendingRead in pendingMediaReads.Values.ToArray())
            {
                pendingRead.Requests.RemoveAll(r => r.Peer == peer);
                if (pendingRead.Requests.Count == 0) CancelPendingRead(pendingRead);
            }
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host) return;
            foreach (var record in records.Values)
            {
                if (record.Adapter == FurnitureRadioState.Adapter && bindings.TryGetValue(record.Id, out var binding))
                {
                    try
                    {
                        binding.Intent.Reset(FurnitureRadioState.Decode(record.Payload), record.Revision);
                        if (record.Revision < binding.Intent.Revision) Apply(binding, binding.Intent.Presentation);
                    }
                    catch (InvalidDataException) { continue; }
                }
                Receive(record);
            }
        }
        private void Changed(WorldRecord record) { if (!host) Receive(record); }
        private void Receive(WorldRecord record)
        {
            if (record.Adapter == RadioStationClockState.Adapter && record.Id == RadioStationClockState.Entity)
            {
                RadioStationClockState clock;
                try { clock = RadioStationClockState.Decode(record.Payload); }
                catch (InvalidDataException) { return; }
                ApplyClock(clock);
                return;
            }
            if (record.Adapter == CdPlaybackState.Adapter)
            {
                CdPlaybackState cdState;
                try { cdState = CdPlaybackState.Decode(record.Payload); }
                catch (InvalidDataException) { return; }
                pendingCd[record.Id] = cdState;
                pendingCdAt[record.Id] = Time.time;
                if (byCdId.TryGetValue(record.Id, out var cdBinding) && cdBinding.Radio != null) ReconcileCd(cdBinding, cdState);
                return;
            }
            if (record.Adapter != FurnitureRadioState.Adapter) return;
            FurnitureRadioState state;
            try { state = FurnitureRadioState.Decode(record.Payload); }
            catch (InvalidDataException) { return; }
            if (bindings.TryGetValue(record.Id, out var current) && record.Revision < current.Intent.Revision) return;
            pending[record.Id] = state;
            if (bindings.TryGetValue(record.Id, out var b) && b.Radio != null)
            {
                ObserveRadio(b, state, record.Revision);
                if (pendingCd.TryGetValue(b.CdId, out var cdState)) ReconcileCd(b, cdState);
            }
        }

        private void ObserveRadio(Binding binding, FurnitureRadioState state, uint revision)
        {
            if (revision < binding.Intent.Revision) return;
            bool known = binding.Intent.Revision != 0;
            var previous = binding.Intent.Authority;
            bool showVolume = known && binding.Intent.Count == 0 && previous.On && state.On &&
                previous.VolumeThousandths != state.VolumeThousandths;
            if (known && previous.On != state.On)
                NotePower("host update: on=" + state.On + "; revision=" + revision + "; pending=" + binding.Intent.Count + "; radio=" + binding.Identity);
            binding.Intent.Observe(state, revision);
            Apply(binding, binding.Intent.Presentation);
            if (showVolume)
                binding.VolumeScreenVisible = RadioVolumeScreen.Refresh(binding.Radio, binding.DisplayVolume, true);
        }

        [MonMP.Core.World.WorldCommandAdapters(FurnitureRadioState.Adapter, CdPlaybackState.Adapter)]

        private void Command(uint peer, ulong id, byte[] payload)
        {
            if (!host || peer == 0) return;
            if (ReceiveCdTransferProgress(peer, payload)) return;
            if (CdMediaCacheReady.Is(payload))
            {
                CdMediaCacheReady ready;
                try { ready = CdMediaCacheReady.Decode(payload); }
                catch (InvalidDataException) { return; }
                if (outboundMedia.TryGetValue(peer, out var transfer) && transfer.Entity == id &&
                    ready.Matches(transfer.TransferId, transfer.Hash)) FinishOutbound(transfer);
                return;
            }
            if (CdMediaRequest.Is(payload) && byCdId.TryGetValue(id, out var cdBinding) && cdBinding.Radio != null)
            {
                CdMediaRequest request;
                try { request = CdMediaRequest.Decode(payload); }
                catch (InvalidDataException) { return; }
                RequestCdMedia(peer, cdBinding, request);
                return;
            }
            bool correlated = ClientInteractionRequest.Is(payload);
            ClientInteractionRequest buttonRequest = default;
            if (correlated)
            {
                try { buttonRequest = ClientInteractionRequest.Decode(payload); }
                catch (InvalidDataException) { return; }
                payload = buttonRequest.Payload;
            }
            if (!FurnitureRadioCommand.Is(payload) || !bindings.TryGetValue(id, out var b) || b.Radio == null) return;
            // Installed radio objects can be deactivated before an in-flight button intent arrives.
            FurnitureRadioCommand command;
            try { command = FurnitureRadioCommand.Decode(payload); }
            catch (InvalidDataException) { return; }
            if (!b.Radio.gameObject.activeInHierarchy || MonMPPlugin.Instance?.World?.SaveGate.Active == true ||
                !presence.TryGetValue(peer, out var seen) || Time.unscaledTime - seen.Time > 2 ||
                !SeatedAtRadio(peer, b.Radio, seen.Pose) &&
                (b.Radio.transform.position - new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z)).sqrMagnitude > RadioReachSquared(b.Radio, seen.Pose, Time.unscaledTime - seen.Time))
            {
                NoteRadio(RadioRefusal(peer, b, command), true);
                if (correlated) ButtonReceipt(peer, b, buttonRequest.Token, ClientInteractionStatus.Refused);
                return;
            }
            // The host's own native rules (vehicle ignition, generator power, band) decide the result.
            uint previousActor = nativeButtonActor;
            bool beforePower = IsOn(b.Radio);
            nativeButtonActor = peer;
            Patches.RemoteActionScope.LastError = null;
            try
            {
                using (Patches.RemoteActionScope.Enter())
                {
                    switch (command.Button)
                    {
                        case FurnitureRadioButton.Power: b.Radio.Button_Power(); break;
                        case FurnitureRadioButton.Volume: b.Radio.Button_Volume(command.Value); break;
                        case FurnitureRadioButton.Preset: b.Radio.Button_Presets((int)command.Value); break;
                        case FurnitureRadioButton.Band: b.Radio.Button_FMCD(); break;
                        case FurnitureRadioButton.SeekNext: b.Radio.Button_SeekNext(); break;
                        case FurnitureRadioButton.SeekPrevious: b.Radio.Button_SeekPrev(); break;
                    }
                }
            }
            finally { nativeButtonActor = previousActor; }
            if (command.Button == FurnitureRadioButton.Power)
                NotePower("host button result: peer=" + peer + "; before=" + beforePower + "; after=" + IsOn(b.Radio) +
                    "; token=" + (correlated ? buttonRequest.Token : 0) + "; radio=" + b.Identity);
            if (Patches.RemoteActionScope.LastError is string error)
                channel!.Reply(peer, id, NativeErrorReply.Encode(error));
            if (correlated) ButtonReceipt(peer, b, buttonRequest.Token,
                Patches.RemoteActionScope.LastError == null ? ClientInteractionStatus.Accepted : ClientInteractionStatus.Refused);
            else Publish(b);
            PublishCd(b);
        }

        private void ButtonReceipt(uint peer, Binding binding, ulong token, ClientInteractionStatus status)
        {
            Publish(binding);
            if (channel == null || !channel.Records.TryGetValue(binding.Id, out var record)) return;
            channel.Reply(peer, binding.Id, new ClientInteractionReceipt(token, status, record.Revision, Capture(binding).Encode()).Encode());
        }
        private void ButtonRejected(uint peer, ulong id, byte[] payload)
        {
            if (!host || !ClientInteractionRequest.Is(payload) || !bindings.TryGetValue(id, out var binding) || binding.Radio == null) return;
            try
            {
                var request = ClientInteractionRequest.Decode(payload);
                FurnitureRadioCommand.Decode(request.Payload);
                ButtonReceipt(peer, binding, request.Token, ClientInteractionStatus.Stale);
            }
            catch (InvalidDataException) { }
        }

        private static bool SeatedAtRadio(uint peer, Furniture_Radio radio, PlayerPose pose)
        {
            // A pose packet trails the moving chassis. Use the host's exact seat claim rather
            // than comparing two different simulation instants, but never trust posture alone.
            var state = (PlayerState)pose.State;
            if (state != PlayerState.InVehicle_Driver && state != PlayerState.InVehicle_Passenger && state != PlayerState.InFurnitureChair) return false;
            var vehicle = RadioVehicle.GetValue(radio) as NWH.VehiclePhysics2.VehicleController;
            var world = MonMPPlugin.Instance?.World;
            if (world == null) return false;
            if (vehicle == null)
            {
                // A radio with no native vehicle link (a portable radio riding in the car, or a
                // dash radio whose prefab lacks the link) is judged against the vehicle the host
                // has this player seated in. Both positions are the host's own, at one instant.
                bool chair = state == PlayerState.InFurnitureChair;
                return RadioNearSeat(radio, chair ? world.ExtraSeats.VehicleForPeer(peer) : world.Vehicles.VehicleForPeer(peer)) ||
                    !chair && (RadioNearSeat(radio, world.SchoolBusPassengers.VehicleForPeer(peer)) || RadioNearSeat(radio, world.MartinCar.VehicleForPeer(peer)));
            }
            return (state == PlayerState.InVehicle_Driver || state == PlayerState.InVehicle_Passenger) &&
                       (world.Vehicles.VehicleForPeer(peer) == vehicle || world.SchoolBusPassengers.VehicleForPeer(peer) == vehicle ||
                        world.MartinCar.VehicleForPeer(peer) == vehicle) ||
                   state == PlayerState.InFurnitureChair && world.ExtraSeats.VehicleForPeer(peer) == vehicle;
        }
        private static bool RadioNearSeat(Furniture_Radio radio, NWH.VehiclePhysics2.VehicleController? seat) =>
            seat != null && (radio.transform.position - seat.transform.position).sqrMagnitude <= Reach * Reach;

        // A seated player's pose packet trails whatever carries the radio. When the host's seat
        // records do not name that carrier (a seat kind they do not cover, or a seat change still
        // in flight), allow for the distance the carrier covers over the packet's age plus the
        // network delay. A parked radio and a walking player keep the plain reach.
        private const float TrailingSeconds = 0.4f, TrailingReach = 30f;
        private static float RadioReachSquared(Furniture_Radio radio, PlayerPose pose, float age)
        {
            float reach = Reach;
            var state = (PlayerState)pose.State;
            if (state == PlayerState.InVehicle_Driver || state == PlayerState.InVehicle_Passenger || state == PlayerState.InFurnitureChair)
                reach += System.Math.Min(TrailingReach, RadioCarrierSpeed(radio) * (System.Math.Max(0f, System.Math.Min(2f, age)) + TrailingSeconds));
            return reach * reach;
        }
        // Host copies of guest-driven vehicles are kinematic and report zero velocity (#250).
        private static float RadioCarrierSpeed(Furniture_Radio radio)
        {
            var body = radio.GetComponentInParent<Rigidbody>();
            if (body == null) return 0f;
            float speed = KinematicCarrierMotion.PointVelocity(body, radio.transform.position).magnitude;
            return float.IsNaN(speed) || float.IsInfinity(speed) ? 0f : speed;
        }

        // Bounded radio button notes, so a button that does nothing can be read from a log.
        private int radioNotes, radioWarnings;
        private int radioPowerNotes;
        private void NotePower(string text)
        {
            if (radioPowerNotes >= 40) return;
            radioPowerNotes++; MonMPPlugin.Instance?.LogRadioInfo("power: " + text);
        }
        private float lastHostButtonNote;
        private FurnitureRadioButton lastHostButton;
        private Furniture_Radio? lastHostButtonRadio;
        private void NoteRadio(string text, bool warning = false)
        {
            if (warning ? radioWarnings >= 30 : radioNotes >= 60) return;
            if (warning) { radioWarnings++; MonMPPlugin.Instance?.LogRadioWarning(text); }
            else { radioNotes++; MonMPPlugin.Instance?.LogRadioInfo(text); }
        }
        private string RadioRefusal(uint peer, Binding b, FurnitureRadioCommand command)
        {
            string why;
            try
            {
                if (!b.Radio.gameObject.activeInHierarchy) why = "the radio is inactive";
                else if (MonMPPlugin.Instance?.World?.SaveGate.Active == true) why = "a save is in progress";
                else if (!presence.TryGetValue(peer, out var seen)) why = "no position received from that player";
                else why = "state=" + (PlayerState)seen.Pose.State + "; positionAgeSeconds=" +
                    (Time.unscaledTime - seen.Time).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "; distance=" +
                    (b.Radio.transform.position - new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z)).magnitude.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                    "; reach=" + Mathf.Sqrt(RadioReachSquared(b.Radio, seen.Pose, Time.unscaledTime - seen.Time)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                    "; radioSpeed=" + RadioCarrierSpeed(b.Radio).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "; seatedAtRadio=" + SeatedAtRadio(peer, b.Radio, seen.Pose) +
                    "; radioHasVehicle=" + (RadioVehicle.GetValue(b.Radio) != null);
            }
            catch (Exception ex) { why = "details unavailable (" + ex.GetType().Name + ")"; }
            return "host refused a " + command.Button + " button from peer " + peer + ": " + why + "; radio=" + b.Identity;
        }
        // The state before the host's own native button runs (actor 0), a guest's admitted
        // button (actor = peer) or a forced ignition/generator power change.
        private void NoteHostButton(Furniture_Radio radio, FurnitureRadioButton button, float value, bool forced)
        {
            if (radioNotes >= 60) return;
            float now = Time.unscaledTime;
            if (ReferenceEquals(radio, lastHostButtonRadio) && button == lastHostButton && now >= lastHostButtonNote && now - lastHostButtonNote < 1f) return;
            lastHostButtonRadio = radio; lastHostButton = button; lastHostButtonNote = now;
            try
            {
                var culture = System.Globalization.CultureInfo.InvariantCulture;
                var vehicle = RadioVehicle.GetValue(radio) as NWH.VehiclePhysics2.VehicleController;
                byRadio.TryGetValue(radio, out var binding);
                NoteRadio("host radio button: button=" + button + "; value=" + value.ToString("F1", culture) + "; forced=" + forced +
                    "; actor=" + nativeButtonActor + "; on=" + IsOn(radio) + "; band=" + Band(radio) + "; station=" + Station(radio).ToString("F1", culture) +
                    "; volume=" + (binding?.Sound == null ? "?" : Volume(binding.Sound).ToString("F2", culture)) +
                    "; active=" + radio.gameObject.activeInHierarchy +
                    (vehicle == null ? "; vehicle=none" : "; ignition=" + vehicle.Get_IgnitionPosition + "; driverPeer=" +
                        (MonMPPlugin.Instance?.Vehicles?.DrivenBy(vehicle).ToString() ?? "?")) +
                    "; radio=" + (binding?.Identity ?? "unbound"));
            }
            catch (Exception ex) { NoteRadio("host radio button: button=" + button + "; details unavailable (" + ex.GetType().Name + ")"); }
        }

        private void Reply(ulong id, byte[] payload)
        {
            if (!host && bindings.TryGetValue(id, out var binding) && binding.Radio != null && ClientInteractionReceipt.Is(payload))
            {
                try
                {
                    var receipt = ClientInteractionReceipt.Decode(payload);
                    var state = FurnitureRadioState.Decode(receipt.State);
                    if (!binding.Intent.Acknowledge(receipt, state, CanPreviewPowerOn(binding.Radio))) return;
                    if (receipt.Status != ClientInteractionStatus.Accepted)
                        NoteRadio("the host did not accept a radio button: status=" + receipt.Status + "; radio=" + binding.Identity, true);
                    pending[id] = binding.Intent.Authority;
                    SendButton(binding);
                    Apply(binding, binding.Intent.Presentation);
                    if (pendingCd.TryGetValue(binding.CdId, out var cdState)) ReconcileCd(binding, cdState);
                }
                catch (InvalidDataException) { }
                return;
            }
            if (!host && bindings.ContainsKey(id) && NativeErrorReply.TryDecode(payload, out string error))
                Singleton<UI_ErrorMessage>.i?.Show(error);
        }

        internal void NativeButtonPressed(SOUND_Radio sound)
        {
            if (!host || !Active || Applying || SharedPresentationEvents.Replaying || sound == null ||
                sound.Get_Radio == null || !byRadio.TryGetValue(sound.Get_Radio, out var binding)) return;
            // The requester already clicked locally. Other observers hear the host's actual
            // native click, including a button attempt whose radio state did not change.
            PresentationReplays.Events?.HostAnnounce(PresentationKind.RadioButton, 0, binding.Id,
                sound.transform.position, 0, nativeButtonActor, playHere: false);
        }

        internal void PlayButtonClick(PresentationEvent e)
        {
            if (e.Kind != (byte)PresentationKind.RadioButton || e.Variant != 0 ||
                !bindings.TryGetValue(e.Target, out var binding) || binding.Radio == null || binding.Sound == null ||
                !binding.Radio.gameObject.activeInHierarchy) return;
            binding.Sound.ButtonPressed();
        }

        // ApplyBody deliberately bypasses the ignition setter's serialized UnityEvents. Reproduce
        // only its radio side effect on the authority, including the remembered-on power cycle.
        internal void IgnitionChanged(NWH.VehiclePhysics2.VehicleController vehicle, int previousIgnition, int ignition)
        {
            if (!host || !Active || (previousIgnition == 0) == (ignition == 0)) return;
            var radio = vehicle.Vehicle_Radio;
            if (radio == null || !radio.gameObject.activeInHierarchy) return;
            if (ignition == 0) radio.Button_Power(forceOff: true);
            else if (vehicle.Vehicle_Radio_WasOn) radio.Button_Power(forceOn: true);
            if (byRadio.TryGetValue(radio, out var binding)) { Publish(binding); PublishCd(binding); }
        }

        // Guest intercept for every native radio entry point. A forced call comes from a vehicle
        // ignition or generator change, which the host applies and publishes.
        internal bool AllowNative(Furniture_Radio radio, FurnitureRadioButton button, float value, bool forced)
        {
            if (host && Active && !Applying) NoteHostButton(radio, button, value, forced);
            if (Applying || host || !Active || !byRadio.TryGetValue(radio, out var b) || !channel!.Records.ContainsKey(b.Id)) return true;
            if (forced) return false;
            if (MonMPPlugin.Instance?.World?.SaveGate.Active == true || !radio.gameObject.activeInHierarchy)
            {
                SendButton(b); // Cancel previously buffered presentation at the same barrier.
                return false;
            }
            if (b.Intent.Count == 0 && bindings.Values.Count(binding => binding.Intent.Count != 0) >= MaximumPendingRadios) return false;
            if (!b.Intent.Enqueue(new FurnitureRadioCommand(button, value), CanPreviewPowerOn(radio))) return false;
            b.Sound?.ButtonPressed();
            if (button == FurnitureRadioButton.Volume && b.Intent.Presentation.On && b.Sound != null)
            {
                b.DisplayVolume = b.Intent.Presentation.VolumeThousandths / 1000f;
                b.VolumeScreenVisible = RadioVolumeScreen.Refresh(radio, b.DisplayVolume, true);
            }
            else b.VolumeScreenVisible = false;
            SendButton(b);
            Apply(b, b.Intent.Presentation);
            if (pendingCd.TryGetValue(b.CdId, out var cdState)) ReconcileCd(b, cdState);
            return false;
        }

        private void SendButton(Binding binding)
        {
            if (!Active || binding.Radio == null || !binding.Radio.gameObject.activeInHierarchy ||
                MonMPPlugin.Instance?.World?.SaveGate.Active == true)
            {
                if (binding.Intent.Count != 0)
                {
                    binding.Intent.Reset(binding.Intent.Authority, binding.Intent.Revision);
                    Apply(binding, binding.Intent.Authority);
                }
                return;
            }
            if (!binding.Intent.TryBegin(++nextButtonIntent, Time.unscaledTime, out var command)) return;
            channel!.Request(binding.Id, new ClientInteractionRequest(binding.Intent.Token, command.Encode()).Encode(), true);
        }
        private static bool CanPreviewPowerOn(Furniture_Radio radio)
        {
            if (radio.Get_HomeGenerator != null && !radio.Get_HomeGenerator.Get_isEngineRunning) return false;
            return !(RadioVehicle.GetValue(radio) is NWH.VehiclePhysics2.VehicleController vehicle) ||
                vehicle.Vehicle_Radio == null || vehicle.Get_IgnitionPosition > 0;
        }

        // Guests never pick their own next song; the host's choice arrives with the station clock.
        internal void HoldGuestStations(SOUND_Manager manager)
        {
            if (!GuestSyncing || manager == null) return;
            for (int i = 0; i < StationNames.Length; i++)
            {
                var clips = Clips[i](manager); int song = Songs[i](manager);
                if (clips == null || song < 0 || song >= clips.Length || clips[song] == null) continue;
                ref float time = ref Times[i](manager);
                if (time - 1f > clips[song].length) time = clips[song].length + 0.5f;
            }
            var cdTracks = RadioCdNative.CdPlayer(manager);
            if (cdTracks == null) return;
            // Hold only replicated, powered CD radios. Free-standing/local radios keep their
            // native clock; a synchronized radio pauses at the end boundary until the host's
            // next selected-track/time record arrives.
            foreach (var binding in bindings.Values)
            {
                var sound = binding.Sound;
                if (sound == null || binding.Radio == null || !IsOn(binding.Radio) || RadioCdNative.RadioMode(sound) != 1 ||
                    !pendingCd.TryGetValue(binding.CdId, out var state)) continue;
                // Load CD clears/rebuilds the native list asynchronously. Repair the selected
                // cached clip before native Tick indexes it, rather than waiting for Update.
                if (state.HasContent && transferredMedia.TryGetValue(Convert.ToBase64String(state.ContentHash), out var cached) && cached.Clip != null)
                    RestoreCachedCdTrack(binding, cached, cdTracks);
                int song = CdSong(sound);
                // While media is still loading, a previous local ordinal can also be invalid
                // in the rebuilt list. Keep Tick's lookup safe without selecting local music.
                if (cdTracks.Count > 0 && (song < 0 || song >= cdTracks.Count))
                { CdSong(sound) = song = 0; binding.AppliedTrackHash = null; }
                if (song < 0 || song >= cdTracks.Count || cdTracks[song] == null) continue;
                ref float time = ref CdTime(sound);
                if (time - 1f > cdTracks[song].length) time = cdTracks[song].length + 0.5f;
            }
        }

        private void ScanIfDue(float now)
        {
            bool periodic = periodicDiscoveryStarted && nextScan > 0;
            if (now < nextScan || !WorldScans.TryClaim<Furniture_Radio>(periodic)) return;
            // Radio/CD records still enroll on the first active Update. The
            // short first interval separates the later two-second fallback scans.
            nextScan = periodic ? WorldScans.NextDeadline(now, 2f) : now + (periodicDiscoveryStarted ? 2f : 0.75f);
            periodicDiscoveryStarted = true;
            Scan(periodic);
        }
        public void Update()
        {
            if (!Active)
            {
                if (!host) foreach (var binding in bindings.Values) SendButton(binding);
                return;
            }
            float now = Time.unscaledTime;
            if (stagedRadios != null && WorldScans.TryClaim()) BindStaged();
            ScanIfDue(now);
            if (!host)
            {
                foreach (var binding in bindings.Values)
                {
                    if (binding.Radio == null) continue;
                    if (binding.Intent.Expire(now))
                    {
                        NoteRadio("the host did not answer a radio button within " + RadioPresentationIntent.TimeoutSeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) +
                            " s; radio=" + binding.Identity + "; " + CdLink(0), true);
                        Apply(binding, binding.Intent.Authority);
                    }
                    SendButton(binding);
                }
            }
            if (host)
            {
                if (now >= nextPublish)
                {
                    nextPublish = now + 0.1f;
                    var snapshot = BindingsSnapshot();
                    foreach (var b in snapshot)
                    {
                        if (b.Radio != null && b.Radio.Get_IsOn &&
                            (b.Radio.Get_HomeGenerator != null && !b.Radio.Get_HomeGenerator.Get_isEngineRunning ||
                             RadioVehicle.GetValue(b.Radio) is NWH.VehiclePhysics2.VehicleController vehicle && vehicle.Get_IgnitionPosition <= 0))
                            b.Radio.Button_Power(forceOff: true);
                        Publish(b); PublishCd(b);
                    }
                }
                if (now >= nextClock)
                {
                    nextClock = now + 0.1f;
                    // Publish at once on a song change; otherwise refresh offsets once a second.
                    if (TryCaptureClock(publishedSongs, now >= nextClockRefresh || publishedCdSongCount != NativeCdSongCount, out var clock, NativeCdSongCount))
                    {
                        if (channel!.Records.ContainsKey(RadioStationClockState.Entity)) channel.Publish(RadioStationClockState.Entity, clock.Encode());
                        else channel.Register(new WorldRecord(RadioStationClockState.Entity, 1, RadioStationClockState.Adapter, clock.Encode()));
                        publishedSongs = clock.Songs; publishedCdSongCount = clock.CdSongCount; nextClockRefresh = now + 1f;
                        SetCdSongCount(clock.CdSongCount);
                    }
                }
            }
            else if (now >= nextReconcile)
            {
                nextReconcile = now + 0.5f;
                foreach (var b in bindings.Values)
                {
                    if (b.Radio != null && pending.ContainsKey(b.Id) && !Capture(b).Equals(b.Intent.Presentation)) Apply(b, b.Intent.Presentation);
                    if (b.Radio != null && pendingCd.TryGetValue(b.CdId, out var cdState)) ReconcileCd(b, cdState);
                    if (now >= nextAudioRecovery && b.Radio != null && b.Sound != null && b.Radio.Get_IsOn && Band(b.Radio) != 1 &&
                        b.Sound.Speakers != null && b.Sound.Speakers.Any(speaker => speaker != null && !speaker.isPlaying))
                        b.Sound.PlayRadio(Band(b.Radio), Station(b.Radio));
                }
                if (now >= nextAudioRecovery) nextAudioRecovery = now + 3f;
            }
            PumpMediaTransfers(now);
            UpdateCdTransferIndicator(now);
        }

        // The native registry walk (about 5 ms) and the per-radio binding pass (2-3 ms)
        // run on separate claimed frames; the binding pass follows on the next one.
        private void Scan(bool periodic = false)
        {
#if MONMP_PERFORMANCE_CAPTURE
            using var detail = Runtime.CaptureDetail.Measure("Radios.Find");
#endif
            stagedRadios = WorldScans.Find<Furniture_Radio>(periodic, true);
        }
        private void BindStaged()
        {
            var radios = stagedRadios!;
            stagedRadios = null;
#if MONMP_PERFORMANCE_CAPTURE
            using var detail = Runtime.CaptureDetail.Measure("Radios.Bind");
#endif
            discovery.BeginPass();
            try { ScanRadios(radios); }
            finally { discovery.EndPass(); }
        }
        private void ScanRadios(Furniture_Radio[] radios)
        {
            foreach (var dead in byRadio.Keys.Where(r => r == null).ToArray()) { bindings.Remove(byRadio[dead].Id); bindingSnapshot = null; byCdId.Remove(byRadio[dead].CdId); byRadio.Remove(dead); }
            PruneHostMediaCache();
            foreach (var radio in radios)
            {
                // A staged radio can be destroyed before its binding pass.
                if (radio == null || !radio.gameObject.scene.IsValid()) continue;
                var sound = Sound(radio);
                if (sound == null || sound.Speakers == null) continue; // Start has not run yet
                string identity = IdentityOf(radio);
                if (byRadio.TryGetValue(radio, out var existing))
                {
                    if (existing.Identity == identity) continue;
                    bindings.Remove(existing.Id); bindingSnapshot = null; byRadio.Remove(radio); byCdId.Remove(existing.CdId); PruneHostMediaCache();
                }
                ulong id = FurnitureRadioState.EntityFor(identity);
                ulong cdId = CdPlaybackState.EntityFor(identity);
                if (bindings.ContainsKey(id) || byCdId.ContainsKey(cdId)) continue; // ambiguous duplicate stays local
                var binding = new Binding { Id = id, CdId = cdId, Identity = identity, Radio = radio, Sound = sound };
                bindings.Add(id, binding); bindingSnapshot = null; byRadio.Add(radio, binding); byCdId.Add(cdId, binding);
                if (host)
                {
                    if (!channel!.Records.ContainsKey(id)) channel.Register(new WorldRecord(id, 1, FurnitureRadioState.Adapter, Capture(binding).Encode()));
                    if (!channel.Records.ContainsKey(cdId)) channel.Register(new WorldRecord(cdId, 1, CdPlaybackState.Adapter, CaptureCd(binding).Encode()));
                }
                else
                {
                    if (pending.TryGetValue(id, out var state) && channel!.Records.TryGetValue(id, out var record))
                    {
                        binding.Intent.Reset(state, record.Revision);
                        Apply(binding, state);
                    }
                    if (pendingCd.TryGetValue(cdId, out var cdState)) ReconcileCd(binding, cdState);
                }
            }
        }
        // A radio carried as a shared item follows that item; any other radio uses its stable
        // scene path (house, vehicle dash, installed radio part).
        private string IdentityOf(Furniture_Radio radio)
        {
            var item = radio.GetComponentInParent<InteractableItems>(true);
            return discovery.RadioIdentity(radio, item == null ? null : item.transform, item == null ? 0 : items.EntityOf(item));
        }

        private static FurnitureRadioState Capture(Binding b)
        {
            float station = Station(b.Radio);
            return new FurnitureRadioState(IsOn(b.Radio), (byte)Mathf.Clamp(Band(b.Radio), 0, 1),
                (ushort)Mathf.Clamp(Mathf.RoundToInt(station * 10f), 0, 2000), (ushort)Mathf.Clamp(Mathf.RoundToInt(Volume(b.Sound) * 1000f), 0, 1000),
                RadioSpeakerVolume.Capture(b.Sound));
        }
        private void Publish(Binding b)
        {
            if (b.Radio == null || !channel!.Records.TryGetValue(b.Id, out var record)) return;
            var state = Capture(b);
            var previous = FurnitureRadioState.Decode(record.PayloadSpan);
            if (state.Equals(previous)) return;
            if (state.On != previous.On)
                NotePower("host publish: before=" + previous.On + "; after=" + state.On + "; previousRevision=" + record.Revision + "; radio=" + b.Identity);
            channel.Publish(b.Id, state.Encode());
        }
        private void PublishCd(Binding b)
        {
            if (b.Radio == null || !channel!.Records.TryGetValue(b.CdId, out var record)) return;
            CaptureCdValues(b, out uint revision, out byte[] hash, out float time);
            var payload = record.PayloadSpan;
            int displayTrack = b.HostTrackClip != null ? b.HostTrackIndex : -1;
            if (payload.Length == CdPlaybackState.Size && payload[0] == CdPlaybackState.Version &&
                BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1)) == revision &&
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(5 + CdPlaybackState.HashSize))).Equals(time) &&
                BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(CdPlaybackState.LegacySize)) == displayTrack)
            {
                bool same = true;
                for (int i = 0; i < CdPlaybackState.HashSize; i++)
                    if (payload[5 + i] != hash[i]) { same = false; break; }
                if (same) return;
            }
            channel.Publish(b.CdId, new CdPlaybackState(revision, hash, time, displayTrack).Encode());
        }

        private CdPlaybackState CaptureCd(Binding b)
        {
            CaptureCdValues(b, out uint revision, out byte[] hash, out float time);
            return new CdPlaybackState(revision, hash, time, b.HostTrackClip != null ? b.HostTrackIndex : -1);
        }

        private void CaptureCdValues(Binding b, out uint revision, out byte[] hash, out float time)
        {
            var manager = Singleton<SOUND_Manager>.i;
            int index = CdSong(b.Sound);
            AudioClip? clip = null;
            var tracks = manager == null ? null : RadioCdNative.CdPlayer(manager);
            if (tracks != null && index >= 0 && index < tracks.Count) clip = tracks[index];
            if (!b.HostTrackLookedUp || b.HostTrackIndex != index || !ReferenceEquals(b.HostTrackClip, clip))
            {
                b.HostTrackLookedUp = true;
                b.HostTrackIndex = index;
                b.HostTrackClip = clip;
                b.HostTrack = null;
                PruneHostMediaCache();
                b.HostTrack = clip == null ? null : InspectHostMedia(clip);
                PruneHostMediaCache();
                b.CdRevision++;
            }
            CompleteHostHash(b.HostTrack);
            time = Mathf.Clamp(CdTime(b.Sound), 0f, 36000f);
            if (time + 1.5f < b.LastHostCdTime) b.CdRevision++;
            b.LastHostCdTime = time;
            hash = Band(b.Radio) == 1 && IsOn(b.Radio) && b.HostTrack?.HashReady == true ? b.HostTrack.Hash : EmptyCdHash;
            revision = b.CdRevision;
            if (CdPlaybackState.IsEmptyHash(hash)) time = 0f;
        }

        private HostMedia? InspectHostMedia(AudioClip clip, long availableBytes = long.MaxValue)
        {
            if (clip != null && uploadedCdSources.TryGetValue(clip, out var uploaded))
                return uploaded.Length <= availableBytes ? uploaded : null;
            if (clip == null || !RadioCdNative.TrackSources.TryGet(clip, out string selectedPath, out long loadedBytes, out long loadedWriteTicks) ||
                loadedBytes < 1 || loadedWriteTicks < 1)
            { WarnCdMedia("host track source metadata unavailable; reload the native CD list"); return null; }
            if (float.IsNaN(clip.length) || float.IsInfinity(clip.length) || clip.length <= 0)
            { WarnCdMedia("host track has invalid duration metadata (" + DescribeCdClip(clip) + ")"); return null; }
            if (clip.length > 600f)
            { WarnCdMedia("host track duration exceeds the supported 10 minute limit (" + DescribeCdClip(clip) + ")"); return null; }
            string nativeDirectory = NativeCdDirectory.Path;
            try
            {
                var folder = new DirectoryInfo(nativeDirectory);
                folder.Refresh();
                var file = new FileInfo(selectedPath);
                file.Refresh();
                if (!folder.Exists || !file.Exists || file.Length != loadedBytes || file.LastWriteTimeUtc.Ticks != loadedWriteTicks ||
                    !CdMediaFilePolicy.TryValidateNativeFile(nativeDirectory, file.FullName, file.Length,
                        (folder.Attributes & FileAttributes.ReparsePoint) != 0, (file.Attributes & FileAttributes.ReparsePoint) != 0, out var format))
                { WarnCdMedia("host track file changed, is unsupported or exceeds the 32 MiB file limit"); return null; }
                if (file.Length < 1 || file.Length > CdMediaMessage.MaximumFileBytes)
                { WarnCdMedia("host track exceeds the 32 MiB file limit"); return null; }
                long decodedBytes = DecodedCdBytes(clip);
                if (decodedBytes < 1 || clip.frequency <= 0)
                { WarnCdMedia("host track has invalid audio metadata (" + DescribeCdClip(clip) + ")"); return null; }
                if (!CdMediaCachePolicy.CanRetainDecoded(0, decodedBytes))
                { WarnCdMedia("host track exceeds the 256 MiB decoded track limit (" + DescribeCdClip(clip) + ")"); return null; }
                // Playlist offers that cannot fit the guest cache are skipped before
                // hashing, rather than hashed, pruned and cancelled on every scan.
                if (file.Length > availableBytes) return null;
                if (hostMediaByPath.TryGetValue(file.FullName, out var cached))
                {
                    if (cached.Length == file.Length && cached.LastWriteUtcTicks == file.LastWriteTimeUtc.Ticks && cached.Format == format)
                        return cached;
                    hostMediaByPath.Remove(file.FullName);
                    CancelHostMedia(cached);
                }
                PruneHostMediaCache();
                if (hostMediaByPath.Count >= MaximumHostTrackMetadata) return null;
                var media = new HostMedia
                {
                    Path = file.FullName, Length = checked((uint)file.Length), Format = format,
                    DecodedBytes = checked((uint)decodedBytes),
                    LastWriteUtcTicks = file.LastWriteTimeUtc.Ticks, Cancellation = new CancellationTokenSource(),
                    LastProgressTimestamp = Stopwatch.GetTimestamp()
                };
                media.HashTask = Task.Run(() => HashHostFileAsync(nativeDirectory, media.Path, media.Length,
                    media.LastWriteUtcTicks, media.Cancellation.Token, () => Interlocked.Exchange(ref media.LastProgressTimestamp, Stopwatch.GetTimestamp())));
                hostMediaByPath.Add(file.FullName, media);
                return media;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException)
            { WarnCdMedia("host track inspection failed; reload the native CD list after checking file access"); return null; }
        }

        private void CompleteHostHash(HostMedia? media)
        {
            if (media == null || media.HashReady || media.HashFailed || media.HashTask == null) return;
            long progressAt = Interlocked.Read(ref media.LastProgressTimestamp);
            double idleSeconds = (Stopwatch.GetTimestamp() - progressAt) / (double)Stopwatch.Frequency;
            if (idleSeconds < 0 || idleSeconds > CdMediaAssembler.InactivityTimeoutSeconds)
            { media.HashFailed = true; CancelHostMedia(media); return; }
            if (!media.HashTask.IsCompleted) return;
            try { media.Hash = media.HashTask.GetAwaiter().GetResult(); media.HashReady = media.Hash.Length == CdPlaybackState.HashSize; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is OperationCanceledException || ex is System.Security.SecurityException || ex is CryptographicException)
            { media.HashFailed = true; }
            finally { media.HashTask = null; media.Cancellation?.Dispose(); media.Cancellation = null; }
        }

        private void PruneHostMediaCache()
        {
            foreach (var pair in hostMediaByPath.ToArray())
            {
                bool selected = bindings.Values.Any(b => ReferenceEquals(b.HostTrack, pair.Value)) || playlistMedia.Values.Contains(pair.Value);
                bool reading = pendingMediaReads.Values.Any(r => ReferenceEquals(r.Source, pair.Value));
                if (!host && ReferenceEquals(guestCdUpload?.Source, pair.Value)) continue;
                if (selected || reading) continue;
                hostMediaByPath.Remove(pair.Key);
                CancelHostMedia(pair.Value);
            }
        }

        private static void CancelHostMedia(HostMedia media)
        {
            var cancellation = media.Cancellation;
            media.Cancellation = null;
            cancellation?.Cancel();
            var work = media.HashTask;
            media.HashTask = null;
            if (work == null) { cancellation?.Dispose(); return; }
            _ = work.ContinueWith(task =>
            {
                if (task.IsFaulted) { var ignored = task.Exception; }
                cancellation?.Dispose();
            }, TaskScheduler.Default);
        }

        private async Task<byte[]> HashHostFileAsync(string nativeDirectory, string path, uint expectedLength, long expectedWriteTicks, CancellationToken cancellation, Action reportProgress)
        {
            await nativeFileIo.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (!ValidateNativeFileMetadata(nativeDirectory, path, expectedLength, expectedWriteTicks)) throw new IOException("Selected CD file changed before hashing.");
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                using var sha = SHA256.Create();
                var buffer = new byte[65536]; long total = 0; int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) > 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    reportProgress();
                    total += read;
                    if (total > expectedLength) throw new IOException("Selected CD file grew while hashing.");
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                if (total != expectedLength || !ValidateNativeFileMetadata(nativeDirectory, path, expectedLength, expectedWriteTicks))
                    throw new IOException("Selected CD file changed while hashing.");
                return (byte[])sha.Hash!.Clone();
            }
            finally { nativeFileIo.Release(); }
        }

        private static bool ValidateNativeFileMetadata(string nativeDirectory, string path, long length, long writeTicks)
        {
            try
            {
                var folder = new DirectoryInfo(nativeDirectory); folder.Refresh();
                var file = new FileInfo(path); file.Refresh();
                return folder.Exists && file.Exists && file.Length == length && file.LastWriteTimeUtc.Ticks == writeTicks &&
                    CdMediaFilePolicy.TryValidateNativeFile(nativeDirectory, file.FullName, file.Length,
                        (folder.Attributes & FileAttributes.ReparsePoint) != 0, (file.Attributes & FileAttributes.ReparsePoint) != 0, out _);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException)
            { return false; }
        }

        private static void Apply(Binding b, FurnitureRadioState state)
        {
            var radio = b.Radio; var sound = b.Sound;
            if (radio == null || sound == null) return;
            Applying = true;
            try
            {
                float station = state.StationTenths / 10f;
                bool wasOn = IsOn(radio);
                int wasMode = Band(radio);
                bool retune = wasMode != state.Mode || Station(radio) != station;
                Band(radio) = state.Mode; Station(radio) = station;
                // A band preview must also move the sound replica out of FM; otherwise
                // a native station refresh can restart FM while verified CD media loads.
                RadioCdNative.SetRadioMode(sound, state.Mode);
                sound.SetVolume(state.VolumeThousandths / 10f);
                RadioSpeakerVolume.Apply(sound, state.SpeakerVolumeThousandths);
                b.DisplayVolume = sound.GetVolume;
                if (state.On && !wasOn)
                {
                    // The host already applied the generator rule; the guest's copy must not veto it.
                    var generator = radio.Get_HomeGenerator;
                    radio.Get_HomeGenerator = null;
                    try { radio.Button_Power(forceOn: true); }
                    finally { radio.Get_HomeGenerator = generator; }
                }
                else if (!state.On && wasOn) radio.Button_Power(forceOn: false, forceOff: true);
                else if (state.On && retune && state.Mode != 1) sound.PlayRadio(state.Mode, station);
                if (state.On) { radio.UpdateScreen(); RefreshVolumeScreen(b); }
                else b.VolumeScreenVisible = false;
                if (state.On && state.Mode == 1 && (!wasOn || wasMode != 1))
                {
                    SilenceLocalCd(sound);
                    b.AppliedTrackHash = null;
                    b.AppliedRevision = 0;
                    b.AppliedTime = 0;
                    b.AppliedAt = Time.unscaledTime;
                }
            }
            finally { Applying = false; }
        }

        private void ReconcileCd(Binding b, CdPlaybackState state)
        {
            if (host || b.Radio == null || b.Sound == null) return;
            // A stopped radio must stay silent even if a delayed native start or
            // previously scheduled source survives until this recovery pass.
            if (!IsOn(b.Radio) || !b.Radio.gameObject.activeInHierarchy) { SilenceLocalCd(b.Sound); return; }
            RefreshCdScreen(b.Radio, true);
            var manager = Singleton<SOUND_Manager>.i;
            bool inFlight = !guestPrefetch && mediaAssembler.Active && guestRequestEntity == b.CdId &&
                CdPlaybackState.HashEquals(mediaAssembler.ActiveHash, state.ContentHash);
            if (!guestPrefetch && guestRequestEntity == b.CdId && guestRequestedHash != null && !CdPlaybackState.HashEquals(guestRequestedHash, state.ContentHash))
            {
                mediaAssembler.Suspend(Time.unscaledTime);
                guestRequestEntity = 0; guestRequestedHash = null;
            }
            string key = state.HasContent ? Convert.ToBase64String(state.ContentHash) : "";
            if (state.HasContent && (rejectedMedia.Contains(key) || (rejectedMedia.Count >= CdMediaCachePolicy.MaximumTracks && !transferredMedia.ContainsKey(key))))
            { SilenceLocalCd(b.Sound); return; }
            if (!guestPrefetch && guestRequestEntity == b.CdId && guestRequestedHash != null && CdPlaybackState.HashEquals(guestRequestedHash, state.ContentHash) && !mediaAssembler.Active)
            {
                if (transferredMedia.ContainsKey(key)) guestRequestEntity = 0;
                else if (Time.unscaledTime - guestRequestAt > MediaRequestRetrySeconds)
                {
                    NoteCd("the host did not start sending the playing track within " + MediaRequestRetrySeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) +
                        " s; asking again; " + CdLink(0), true);
                    guestRequestEntity = 0; guestRequestedHash = null;
                }
                else inFlight = true;
            }
            CachedMedia? cached = null;
            bool hasMedia = state.HasContent && transferredMedia.TryGetValue(key, out cached) && cached.Clip != null;
            if (hasMedia && manager != null && cached!.Clip != null)
            {
                TouchCachedMedia(cached);
                var tracks = RadioCdNative.CdPlayer(manager);
                if (tracks != null)
                {
                    RestoreCachedCdTrack(b, cached, tracks);
                }
            }
            float receivedAt = pendingCdAt.TryGetValue(b.CdId, out var at) ? at : Time.time;
            float selectedTime = CdPlaybackContinuity.SnapshotTime(state.Time, receivedAt, Time.time);
            float expectedTime = CdPlaybackContinuity.AtSnapshot(CdTime(b.Sound), receivedAt, Time.time);
            inFlight |= loadingMedia.Contains(key);
            CdPlaybackAction action = CdPlaybackSyncPolicy.Plan(state, Band(b.Radio) == 1, IsOn(b.Radio), manager != null,
                hasMedia, hasMedia ? state.ContentHash : null, inFlight,
                b.AppliedTrackHash, b.AppliedRevision, expectedTime);
            bool audibleSource = b.Sound.gameObject.activeInHierarchy && b.Sound.Speakers != null &&
                b.Sound.Speakers.Any(speaker => speaker != null && speaker.isActiveAndEnabled);
            // Native keeps its logical clock beyond the clip while waiting for the next Tick.
            // Do not repeatedly recover that finished clip or pass an out-of-range seek to Unity.
            if (hasMedia && state.HasContent && Band(b.Radio) == 1 && IsOn(b.Radio) &&
                selectedTime >= cached!.Clip!.length)
            {
                SilenceOtherCd(b.Sound, cached.Clip!);
                return;
            }
            // Native Play schedules playback after 0.5s. Wait beyond that delay before
            // recovering, and let inactive hierarchies become ready without enabling them.
            if (action == CdPlaybackAction.Hold && hasMedia && audibleSource && Time.unscaledTime >= nextAudioRecovery &&
                Time.unscaledTime - b.AppliedAt >= 1f && CdSpeakersNeedRecovery(b.Sound, cached!.Clip!))
                action = CdPlaybackAction.ApplyHostTrack;
            switch (action)
            {
                case CdPlaybackAction.PreserveNative:
                case CdPlaybackAction.Hold:
                    return;
                case CdPlaybackAction.SilenceLocalContent:
                    SilenceLocalCd(b.Sound);
                    return;
                case CdPlaybackAction.RequestContent:
                    SilenceLocalCd(b.Sound);
                    if (!LocalCdAudible(b) || !MediaRetryReady(key)) return;
                    if (guestPrefetch && !loadingMedia.Contains(key))
                    { mediaAssembler.Suspend(Time.unscaledTime); guestRequestEntity = 0; guestRequestedHash = null; guestPrefetch = false; }
                    if (guestRequestEntity == 0 && !mediaAssembler.Active && channel != null && channel.Records.ContainsKey(b.CdId))
                    {
                        try
                        {
                            // The CD clock revises every 0.1s; admission is by the exact
                            // selected hash rather than an already stale clock revision.
                            channel.Request(b.CdId, new CdMediaRequest(state.ContentHash, false, mediaAssembler.ResumeChunkFor(state.ContentHash, Time.unscaledTime)).Encode(), useLease: true);
                            guestRequestEntity = b.CdId; guestPrefetch = false;
                            guestRequestedHash = (byte[])state.ContentHash.Clone();
                            guestRequestAt = Time.unscaledTime;
                            NoteCd("guest asked the host for the playing track: resumeChunk=" +
                                mediaAssembler.ResumeChunkFor(state.ContentHash, Time.unscaledTime) + "; " + CdLink(0));
                        }
                        catch (InvalidDataException) { }
                    }
                    return;
                case CdPlaybackAction.ApplyHostTrack:
                    if (!hasMedia || manager == null) { SilenceLocalCd(b.Sound); return; }
                    if (!audibleSource) return;
                    if (CdPlaybackState.HashEquals(b.AppliedTrackHash, state.ContentHash) && b.AppliedRevision == state.Revision)
                    {
                        if (Time.unscaledTime - b.AppliedAt >= 1f && RepairCdSpeakers(b.Sound, cached!.Clip!, selectedTime))
                            b.AppliedAt = Time.unscaledTime;
                        RadioCdNative.SetCdTime(b.Sound, selectedTime);
                        return;
                    }
                    RadioCdNative.SetRadioMode(b.Sound, 1);
                    RadioCdNative.SetCdSong(b.Sound, cached!.Index);
                    RadioCdNative.SetCdTime(b.Sound, CdPlaybackContinuity.SeekTime(selectedTime, cached.Clip!.length, false));
                    RadioCdNative.Play(manager, b.Sound);
                    b.AppliedTrackHash = (byte[])state.ContentHash.Clone();
                    b.AppliedRevision = state.Revision;
                    b.AppliedTime = selectedTime;
                    b.AppliedAt = Time.unscaledTime;
                    b.Radio.UpdateScreen();
                    return;
            }
        }

        private static void RestoreCachedCdTrack(Binding b, CachedMedia cached, List<AudioClip> tracks)
        {
            int index = NativeCdTrackIndex.Ensure(tracks, cached.Clip);
            // Multiple radios share a cache entry, but each native ordinal must be repaired.
            // Moving the exact cached clip within the native list does not change its applied
            // content identity; same-selection recovery repairs any stopped speakers separately.
            cached.Index = index;
            CdSong(b.Sound) = index;
        }

        private static bool CdSpeakersNeedRecovery(SOUND_Radio sound, AudioClip clip)
        {
            if (sound.Speakers == null) return false;
            foreach (var speaker in sound.Speakers)
                if (speaker != null && speaker.isActiveAndEnabled &&
                    (!speaker.isPlaying || !ReferenceEquals(speaker.clip, clip))) return true;
            return false;
        }

        private static bool RepairCdSpeakers(SOUND_Radio sound, AudioClip clip, float logicalTime)
        {
            if (sound.Speakers == null) return false;
            bool started = false;
            foreach (var speaker in sound.Speakers)
            {
                if (speaker == null || !speaker.isActiveAndEnabled) continue;
                bool playing = speaker.isPlaying && ReferenceEquals(speaker.clip, clip);
                float target = CdPlaybackContinuity.SeekTime(logicalTime, clip.length, playing);
                if (playing)
                {
                    // A clamped frame can leave the native cursor behind the independently
                    // playing source. Rebase the cursor without disturbing already-correct audio.
                    if (CdPlaybackContinuity.NeedsSeek(speaker.time, target)) speaker.time = target;
                    continue;
                }
                speaker.Stop();
                speaker.time = 0f;
                speaker.clip = clip;
                speaker.time = target;
                speaker.PlayDelayed(CdPlaybackContinuity.NativeStartDelay);
                started = true;
            }
            return started;
        }

        private static void SilenceOtherCd(SOUND_Radio sound, AudioClip clip)
        {
            if (sound.Speakers == null) return;
            foreach (var speaker in sound.Speakers)
                if (speaker != null && !ReferenceEquals(speaker.clip, clip))
                { speaker.Stop(); speaker.clip = null; }
        }

        // Native power-on and station refresh can start CD playback between reconciliation
        // passes. Permit only the selected, verified clip and repair its current list ordinal.
        internal bool AllowNativeCdPlayback(SOUND_Manager manager, SOUND_Radio sound)
        {
            if (!Active || manager == null || sound == null) return true;
            if (sound.Get_Radio != null && byRadio.TryGetValue(sound.Get_Radio, out var powerBinding))
            {
                if (!IsOn(powerBinding.Radio) || !powerBinding.Radio.gameObject.activeInHierarchy)
                {
                    if (!powerBinding.OffPlaybackNoted)
                    {
                        powerBinding.OffPlaybackNoted = true;
                        NotePower("blocked playback for an off/inactive radio; radio=" + powerBinding.Identity);
                    }
                    SilenceLocalCd(sound); return false;
                }
                powerBinding.OffPlaybackNoted = false;
            }
            if (host) return AllowHostCdPlayback(manager, sound);
            if (RadioCdNative.RadioMode(sound) != 1) return AllowNativeFmPlayback(manager, sound);
            if (sound.Get_Radio == null || !byRadio.TryGetValue(sound.Get_Radio, out var binding)) return true;
            var tracks = RadioCdNative.CdPlayer(manager);
            if (tracks != null && pendingCd.TryGetValue(binding.CdId, out var state) && state.HasContent &&
                transferredMedia.TryGetValue(Convert.ToBase64String(state.ContentHash), out var cached) && cached.Clip != null)
            {
                TouchCachedMedia(cached);
                RestoreCachedCdTrack(binding, cached, tracks);
                float receivedAt = pendingCdAt.TryGetValue(binding.CdId, out var at) ? at : Time.time;
                float selectedTime = CdPlaybackContinuity.SnapshotTime(state.Time, receivedAt, Time.time);
                if (selectedTime >= cached.Clip.length) { SilenceOtherCd(sound, cached.Clip); return false; }
                // Native FM refresh visits radios by their remembered station even in CD mode.
                // It must not stop and reschedule a healthy, already-applied host CD selection.
                if (CdPlaybackState.HashEquals(binding.AppliedTrackHash, state.ContentHash) && binding.AppliedRevision == state.Revision)
                {
                    if (Time.unscaledTime - binding.AppliedAt >= 1f && RepairCdSpeakers(sound, cached.Clip, selectedTime))
                        binding.AppliedAt = Time.unscaledTime;
                    CdTime(sound) = selectedTime;
                    return false;
                }
                CdTime(sound) = CdPlaybackContinuity.SeekTime(selectedTime, cached.Clip.length, false);
                return true;
            }
            SilenceLocalCd(sound);
            return false;
        }

        private bool AllowNativeFmPlayback(SOUND_Manager manager, SOUND_Radio sound)
        {
            if (!GuestSyncing) return true;
            for (int station = 0; station < StationFrequencies.Length - 1; station++)
            {
                if (sound.GetStationPlaying != StationFrequencies[station]) continue;
                var clip = HeldStationClip(manager, station);
                if (clip == null) return true;
                // The logical tail sentinel prevents a guest choosing its own next song.
                // Keep a matching audible tail, but never copy that sentinel into a seek.
                SilenceOtherCd(sound, clip);
                return false;
            }
            return true;
        }

        internal bool AllowNativeBarPlayback(Store_Bar bar)
        {
            var manager = Singleton<SOUND_Manager>.i;
            if (!GuestSyncing || bar == null || !bar.isOpen || manager == null) return true;
            var clip = HeldStationClip(manager, StationNames.Length - 1);
            if (clip == null) return true;
            var speaker = BarMusic(bar);
            if (speaker != null && !ReferenceEquals(speaker.clip, clip))
            { speaker.Stop(); speaker.clip = null; }
            return false;
        }

        private static AudioClip? HeldStationClip(SOUND_Manager manager, int station)
        {
            var clips = Clips[station](manager);
            int song = Songs[station](manager);
            if (clips == null || song < 0 || song >= clips.Length || clips[song] == null) return null;
            float time = Times[station](manager);
            return float.IsNaN(time) || float.IsInfinity(time) || time < 0f || time >= clips[song].length ? clips[song] : null;
        }

        private static readonly System.Reflection.FieldInfo CdScreenText = AccessTools.Field(typeof(Furniture_Radio), "ScreenText");
        private static readonly System.Reflection.FieldInfo CdScreenTimer = AccessTools.Field(typeof(Furniture_Radio), "TimerScreenGoBackNormal");
        private static void RefreshVolumeScreen(Binding binding)
        {
            if (binding.VolumeScreenVisible)
                binding.VolumeScreenVisible = IsOn(binding.Radio) && RadioVolumeScreen.Refresh(binding.Radio, binding.DisplayVolume, false);
        }
        internal void RefreshCdScreen(Furniture_Radio radio, bool preserveTemporary = false)
        {
            if (host || disposed || session?.IsRunning != true || radio == null || !IsOn(radio) ||
                !byRadio.TryGetValue(radio, out var binding)) return;
            RefreshVolumeScreen(binding);
            if (binding.VolumeScreenVisible || Band(radio) != 1 || !pendingCd.TryGetValue(binding.CdId, out var state)) return;
            if (preserveTemporary && CdScreenTimer.GetValue(radio) is float timer && timer > 0f) return;
            if (CdScreenText.GetValue(radio) is TMPro.TextMeshPro screen)
            {
                // A legacy record has no ordinal; retain its native label when content exists.
                if (state.DisplayTrackIndex < 0 && state.HasContent) return;
                if (binding.DisplayTrackIndex != state.DisplayTrackIndex)
                {
                    binding.DisplayTrackIndex = state.DisplayTrackIndex;
                    binding.DisplayTrackText = state.DisplayTrackIndex >= 0 ? "CD TRACK " + (state.DisplayTrackIndex + 1).ToString("00") : "NO DISC";
                }
                if (screen.text != binding.DisplayTrackText) screen.text = binding.DisplayTrackText;
            }
        }

        private static void SilenceLocalCd(SOUND_Radio sound)
        {
            if (sound == null || sound.Speakers == null) return;
            foreach (var speaker in sound.Speakers)
                if (speaker != null && (speaker.clip != null || speaker.isPlaying)) { speaker.Stop(); speaker.clip = null; }
        }

        private HostMedia? FindPlaylistMedia(byte[] hash)
        {
            foreach (var media in playlistMedia.Values)
                if (media.HashReady && !media.HashFailed && CdPlaybackState.HashEquals(media.Hash, hash)) return media;
            return null;
        }
        private static bool LocalCdAudible(Binding binding)
        {
            var player = Singleton<Gameplay>.i?.Player;
            return player != null && binding.Radio != null && binding.Radio.gameObject.activeInHierarchy &&
                (binding.Radio.transform.position - player.transform.position).sqrMagnitude <= CdAudibleReach * CdAudibleReach;
        }
        private bool WasOffered(uint peer, byte[] hash) => offeredToPeer.TryGetValue(peer, out var offered) &&
            offered.Contains(Convert.ToBase64String(hash));

        private void PumpPlaylist(float now)
        {
            var manager = Singleton<SOUND_Manager>.i;
            var tracks = manager == null ? null : RadioCdNative.CdPlayer(manager);
            if (tracks == null || tracks.Count == 0) return;
            if (now >= nextPlaylistScan)
            {
                nextPlaylistScan = now + 0.25f;
                foreach (var old in playlistMedia.Keys.Where(c => c == null || !tracks.Contains(c)).ToArray()) playlistMedia.Remove(old);
                int count = Math.Min(tracks.Count, PersistentCdMediaCache.MaximumTracks);
                playlistIndex %= count;
                var clip = tracks[playlistIndex++];
                if (clip != null && !playlistMedia.ContainsKey(clip))
                {
                    var media = InspectHostMedia(clip, PersistentCdMediaCache.MaximumBytes - playlistMedia.Values.Sum(m => (long)m.Length));
                    if (media != null) playlistMedia.Add(clip, media);
                }
                foreach (var media in playlistMedia.Values) CompleteHostHash(media);
                PruneHostMediaCache();
            }
            if (now < nextPlaylistOffer || byCdId.Count == 0) return;
            nextPlaylistOffer = now + 0.25f;
            var radio = byCdId.Values.FirstOrDefault(b => b.Radio != null && channel!.Records.ContainsKey(b.CdId));
            if (radio == null) return;
            foreach (uint peer in presence.Keys.Where(mediaPeersReady.Contains))
            {
                if (!offeredToPeer.TryGetValue(peer, out var offered)) offeredToPeer.Add(peer, offered = new HashSet<string>(StringComparer.Ordinal));
                if (offered.Count >= PersistentCdMediaCache.MaximumTracks) continue;
                var media = playlistMedia.Values.FirstOrDefault(m => m.HashReady && !m.HashFailed && !offered.Contains(Convert.ToBase64String(m.Hash)));
                if (media == null) continue;
                nextTransferId++; if (nextTransferId == 0) nextTransferId++;
                if (SendMedia(peer, CdMediaMessage.Offer(radio.CdId, nextTransferId, media.Hash, media.Length, media.DecodedBytes, media.Format)))
                    offered.Add(Convert.ToBase64String(media.Hash));
            }
        }

        // Only foreground requests belong to a particular playing radio. Background offers
        // deliberately continue while their origin radio is off.
        private void ReleaseObsoleteCdRequest(float now)
        {
            if (guestPrefetch || guestRequestEntity == 0) return;
            bool stillSelected = byCdId.TryGetValue(guestRequestEntity, out var b) && b.Radio != null && b.Sound != null &&
                b.Radio.gameObject.activeInHierarchy && IsOn(b.Radio) && Band(b.Radio) == 1 &&
                pendingCd.TryGetValue(b.CdId, out var state) && state.HasContent &&
                CdPlaybackState.HashEquals(guestRequestedHash, state.ContentHash);
            if (stillSelected && (mediaAssembler.Active || now - guestRequestAt <= MediaRequestRetrySeconds)) return;
            if (mediaAssembler.Active) mediaAssembler.Suspend(now);
            guestRequestEntity = 0;
            guestRequestedHash = null;
        }

        private void PumpPreload(float now)
        {
            if (now < nextPreload || disposed || host || channel?.FullySynchronized != true) return;
            nextPreload = now + 0.25f;
            ReleaseObsoleteCdRequest(now);
            if (guestRequestEntity != 0)
            {
                if (guestPrefetch && !mediaAssembler.Active && now - guestRequestAt > MediaRequestRetrySeconds)
                {
                    NoteCd("the host did not start sending a background playlist track within " +
                        MediaRequestRetrySeconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " s; " + CdLink(0), true);
                    guestRequestEntity = 0; guestRequestedHash = null; guestPrefetch = false;
                }
                else return;
            }
            if (mediaAssembler.Active || loadingMedia.Count != 0 || localMediaLookups.Count != 0) return;
            // A powered CD radio waiting for its current track always wins the single download slot.
            if (bindings.Values.Any(b => b.Radio != null && b.Radio.gameObject.activeInHierarchy && LocalCdAudible(b) && Band(b.Radio) == 1 && IsOn(b.Radio) &&
                pendingCd.TryGetValue(b.CdId, out var state) && state.HasContent &&
                !transferredMedia.ContainsKey(Convert.ToBase64String(state.ContentHash)) && !rejectedMedia.Contains(Convert.ToBase64String(state.ContentHash)))) return;
            foreach (var offer in playlistOffers.Values)
            {
                string key = Convert.ToBase64String(offer.ContentHash);
                if (preloadReady.Contains(key) || preloadFailed.Contains(key) || !byCdId.ContainsKey(offer.RadioEntity) ||
                    !channel.Records.ContainsKey(offer.RadioEntity)) continue;
                if (!preloadChecked.Contains(key))
                {
                    string directory = NativeCdDirectory.Path;
                    var token = cacheCancellation.Token;
                    localMediaLookups.Add(key, new LocalMediaLookup
                    {
                        Announcement = offer, Prefetch = true,
                        Work = Task.Run(async () => await PersistentCdMediaCache.ReadAsync(cacheDirectory, offer, token).ConfigureAwait(false) ??
                            await LocalCdMediaLookup.FindAsync(directory, offer, token).ConfigureAwait(false), token)
                    });
                    return;
                }
                try
                {
                    channel.Request(offer.RadioEntity, new CdMediaRequest(offer.ContentHash, prefetch: true, mediaAssembler.ResumeChunkFor(offer.ContentHash, now)).Encode(), useLease: true);
                    guestRequestEntity = offer.RadioEntity; guestRequestedHash = (byte[])offer.ContentHash.Clone();
                    guestPrefetch = true; guestRequestAt = now;
                }
                catch (InvalidDataException) { }
                return;
            }
        }

        private IEnumerator StorePreloadedMedia(byte[] bytes, CdMediaMessage announcement, string key)
        {
            Task<bool>? write = null;
            try
            {
                if (disposed) yield break;
                string path = PersistentCdMediaCache.FilePath(cacheDirectory, announcement.ContentHash, announcement.Format);
                var token = cacheCancellation.Token;
                write = Task.Run(() => PersistentCdMediaCache.WriteAsync(cacheDirectory, bytes, announcement.ContentHash, announcement.Format, token));
                cacheFileWrites.Add(write, path);
                while (!write.IsCompleted) yield return null;
                cacheFileWrites.Remove(write);
                bool ready = false;
                try { ready = write.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                write = null;
                if (!disposed)
                {
                    if (ready) { preloadReady.Add(key); if (announcement.Kind == CdMediaMessageKind.Begin) ConfirmCachedMedia(announcement); }
                    else preloadFailed.Add(key);
                }
            }
            finally { loadingMedia.Remove(key); }
        }

        private void RequestCdMedia(uint peer, Binding b, CdMediaRequest request)
        {
            if (!host || b.Radio == null || b.Sound == null || session == null) return;
            bool recentRepeat = mediaRequestTimes.TryGetValue(peer, out var last) && CdPlaybackState.HashEquals(last.Hash, request.ContentHash) && Time.unscaledTime - last.At < 2f;
            if (outboundMedia.TryGetValue(peer, out var existing))
            {
                if (!request.Prefetch && existing.Prefetch && !CdPlaybackState.HashEquals(existing.Hash, request.ContentHash)) { SendAbort(existing, 3); FinishOutbound(existing); }
                else if (CdPlaybackState.HashEquals(existing.Hash, request.ContentHash))
                {
                    // The guest asks again only after it lost this transfer (stalled past its
                    // inactivity limit, or the Begin never arrived). Chunks it can no longer
                    // accept would otherwise keep flowing until the whole file was sent, so
                    // restart the same buffered send from the guest's resume point.
                    // A switch between background and foreground priority restarts at once.
                    if (existing.Prefetch == request.Prefetch && (!existing.BeginSent || recentRepeat)) return;
                    RestartOutbound(existing, b.CdId, request.ResumeChunk, request.Prefetch);
                    mediaRequestTimes[peer] = ((byte[])request.ContentHash.Clone(), Time.unscaledTime);
                    return;
                }
                else return;
            }
            if (recentRepeat) return;
            bool poseFresh = presence.TryGetValue(peer, out var seen) && Time.unscaledTime - seen.Time <= 2f;
            bool inRange = poseFresh && (b.Radio.transform.position - new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z)).sqrMagnitude <= CdAudibleReach * CdAudibleReach;
            var current = CaptureCd(b);
            HostMedia? info = request.Prefetch ? FindPlaylistMedia(request.ContentHash) : b.HostTrack;
            bool selected = request.Prefetch ? WasOffered(peer, request.ContentHash) && info?.HashReady == true :
                current.HasContent && CdPlaybackState.HashEquals(current.ContentHash, request.ContentHash) && info?.HashReady == true;
            if (request.Prefetch) inRange = poseFresh;
            bool slotAvailable = !outboundMedia.ContainsKey(peer);
            if (!CdMediaAuthorityPolicy.MayServe(host, peer, poseFresh, inRange, selected, slotAvailable)) return;

            string mediaKey = Convert.ToBase64String(request.ContentHash);
            if (hostMediaBuffers.TryGetValue(mediaKey, out var buffer))
            {
                StartOutbound(peer, b.CdId, request.ContentHash, buffer, request.Prefetch, request.ResumeChunk);
                mediaRequestTimes[peer] = ((byte[])request.ContentHash.Clone(), Time.unscaledTime);
                return;
            }
            if (pendingMediaReads.TryGetValue(mediaKey, out var pendingRead))
            {
                pendingRead.Requests.RemoveAll(r => r.Peer == peer);
                pendingRead.Requests.Add(new PendingMediaRequest { Peer = peer, Entity = b.CdId, Prefetch = request.Prefetch, ResumeChunk = request.ResumeChunk });
                mediaRequestTimes[peer] = ((byte[])request.ContentHash.Clone(), Time.unscaledTime);
                return;
            }
            if (hostBufferedBytes + pendingHostReadBytes + info!.Length > MaximumHostBufferedBytes) return;
            string nativeDirectory = NativeCdDirectory.Path;
            var cancellation = new CancellationTokenSource();
            pendingRead = new PendingMediaRead
            {
                Key = mediaKey, Source = info!, Cancellation = cancellation, StartedAt = Time.unscaledTime,
                LastProgressTimestamp = Stopwatch.GetTimestamp(),
                Requests = new List<PendingMediaRequest> { new PendingMediaRequest { Peer = peer, Entity = b.CdId, Prefetch = request.Prefetch, ResumeChunk = request.ResumeChunk } }
            };
            var trackedRead = pendingRead;
            pendingRead.Work = Task.Run(() => ReadSelectedMediaAsync(nativeDirectory, info!, cancellation.Token,
                () => Interlocked.Exchange(ref trackedRead.LastProgressTimestamp, Stopwatch.GetTimestamp())));
            pendingMediaReads.Add(mediaKey, pendingRead);
            pendingHostReadBytes += info!.Length;
            mediaRequestTimes[peer] = ((byte[])request.ContentHash.Clone(), Time.unscaledTime);
        }

        private void RestartOutbound(OutboundMedia transfer, ulong entity, ushort resumeChunk, bool prefetch)
        {
            float now = Time.unscaledTime;
            nextTransferId++; if (nextTransferId == 0) nextTransferId++;
            transfer.TransferId = nextTransferId; transfer.Entity = entity; transfer.Prefetch = prefetch; transfer.BeginSent = false;
            transfer.NextChunk = resumeChunk <= transfer.ChunkCount ? resumeChunk : (ushort)0;
            transfer.StartedAt = now; transfer.LastProgressAt = now; transfer.NextSendAt = now;
        }

        private void StartOutbound(uint peer, ulong entity, byte[] hash, HostMediaBuffer buffer, bool prefetch = false, ushort resumeChunk = 0)
        {
            if (outboundMedia.ContainsKey(peer) || !byCdId.TryGetValue(entity, out var binding) || binding.Radio == null) return;
            bool poseFresh = presence.TryGetValue(peer, out var seen) && Time.unscaledTime - seen.Time <= 2f;
            bool inRange = poseFresh && (binding.Radio.transform.position - new Vector3(seen.Pose.X, seen.Pose.Y, seen.Pose.Z)).sqrMagnitude <= CdAudibleReach * CdAudibleReach;
            var state = CaptureCd(binding);
            if (prefetch) inRange = poseFresh;
            bool allowed = prefetch ? WasOffered(peer, hash) && FindPlaylistMedia(hash)?.HashReady == true :
                state.HasContent && CdPlaybackState.HashEquals(state.ContentHash, hash);
            if (!CdMediaAuthorityPolicy.MayServe(host, peer, poseFresh, inRange,
                allowed, true)) return;
            buffer.References++;
            nextTransferId++; if (nextTransferId == 0) nextTransferId++;
            ushort chunkCount = CdMediaMessage.ExpectedChunks((uint)buffer.Bytes.Length);
            outboundMedia.Add(peer, new OutboundMedia
            {
                Peer = peer, Entity = entity, Prefetch = prefetch, TransferId = nextTransferId, MediaKey = Convert.ToBase64String(hash),
                Hash = (byte[])hash.Clone(), Bytes = buffer.Bytes, Format = buffer.Format, DecodedBytes = buffer.DecodedBytes,
                ChunkCount = chunkCount, NextChunk = resumeChunk <= chunkCount ? resumeChunk : (ushort)0, StartedAt = Time.unscaledTime,
                LastProgressAt = Time.unscaledTime, NextSendAt = Time.unscaledTime
            });
            NoteCd("host sending a track to peer " + peer + ": bytes=" + buffer.Bytes.Length + "; chunks=" + chunkCount +
                "; resumeChunk=" + resumeChunk + "; background=" + prefetch + "; " + CdLink(peer));
        }

        private async Task<byte[]> ReadSelectedMediaAsync(string nativeDirectory, HostMedia source, CancellationToken cancellation, Action reportProgress)
        {
            await nativeFileIo.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (source.CachedUpload)
                {
                    var announcement = CdMediaMessage.Offer(CdMediaMessage.UploadEntity, 1, source.Hash, source.Length, source.DecodedBytes, source.Format);
                    var cached = await PersistentCdMediaCache.ReadAsync(cacheDirectory, announcement, cancellation).ConfigureAwait(false);
                    if (cached == null) throw new IOException("Uploaded CD cache no longer matches its declared content.");
                    reportProgress(); return cached;
                }
                if (!ValidateNativeFileMetadata(nativeDirectory, source.Path, source.Length, source.LastWriteUtcTicks)) throw new IOException("Selected CD file changed before transfer.");
                byte[] bytes = new byte[(int)source.Length];
                using (var stream = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                {
                    int offset = 0;
                    while (offset < bytes.Length)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int read = await stream.ReadAsync(bytes, offset, Math.Min(65536, bytes.Length - offset), cancellation).ConfigureAwait(false);
                        if (read == 0) throw new IOException("Selected CD file ended during transfer read.");
                        offset += read;
                        reportProgress();
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                if (!ValidateNativeFileMetadata(nativeDirectory, source.Path, bytes.LongLength, source.LastWriteUtcTicks) ||
                    !CdMediaFilePolicy.IsExpectedContent(bytes, source.Hash)) throw new IOException("Selected CD content changed before transfer.");
                return bytes;
            }
            finally { nativeFileIo.Release(); }
        }

        private void PumpPendingMediaReads(float now)
        {
            foreach (var pendingRead in pendingMediaReads.Values.ToArray())
            {
                long progressAt = Interlocked.Read(ref pendingRead.LastProgressTimestamp);
                double idleSeconds = (Stopwatch.GetTimestamp() - progressAt) / (double)Stopwatch.Frequency;
                if (now < pendingRead.StartedAt || now - pendingRead.StartedAt > CdMediaAssembler.TimeoutSeconds ||
                    idleSeconds < 0 || idleSeconds > CdMediaAssembler.InactivityTimeoutSeconds)
                { CancelPendingRead(pendingRead); continue; }
                if (!pendingRead.Work.IsCompleted) continue;
                pendingMediaReads.Remove(pendingRead.Key);
                pendingHostReadBytes -= pendingRead.Source.Length;
                byte[]? bytes = null;
                bool sourceInvalid = false;
                try { bytes = pendingRead.Work.GetAwaiter().GetResult(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException || ex is CryptographicException)
                { sourceInvalid = true; }
                catch (OperationCanceledException) { }
                pendingRead.Cancellation.Dispose();
                if (sourceInvalid) { pendingRead.Source.HashReady = false; pendingRead.Source.HashFailed = true; }
                if (bytes == null || bytes.LongLength != pendingRead.Source.Length ||
                    hostBufferedBytes + bytes.LongLength > MaximumHostBufferedBytes) continue;
                var buffer = new HostMediaBuffer { Bytes = bytes, Format = pendingRead.Source.Format, DecodedBytes = pendingRead.Source.DecodedBytes };
                hostMediaBuffers[pendingRead.Key] = buffer;
                hostBufferedBytes += bytes.LongLength;
                foreach (var target in pendingRead.Requests)
                    if (presence.ContainsKey(target.Peer)) StartOutbound(target.Peer, target.Entity, pendingRead.Source.Hash, buffer, target.Prefetch, target.ResumeChunk);
                if (buffer.References == 0)
                { hostMediaBuffers.Remove(pendingRead.Key); hostBufferedBytes -= bytes.LongLength; }
            }
        }

        private void CancelPendingRead(PendingMediaRead pendingRead)
        {
            if (!pendingMediaReads.Remove(pendingRead.Key)) return;
            pendingRead.Cancellation.Cancel();
            pendingHostReadBytes -= pendingRead.Source.Length;
            if (pendingRead.Work.IsCompleted)
            {
                if (pendingRead.Work.IsFaulted) { var ignored = pendingRead.Work.Exception; }
                pendingRead.Cancellation.Dispose();
            }
            else _ = pendingRead.Work.ContinueWith(task =>
            {
                if (task.IsFaulted) { var ignored = task.Exception; }
                pendingRead.Cancellation.Dispose();
            }, TaskScheduler.Default);
        }

        private void PumpMediaTransfers(float now)
        {
            PumpCdUploads(now);
            if (host)
            {
                PumpPlaylist(now);
                PumpPendingMediaReads(now);
                float elapsed = Math.Max(0f, now - mediaBudgetAt);
                mediaBudget = Math.Min(CdMediaPacing.HostBurstBytes, mediaBudget + elapsed * CdMediaPacing.HostBytesPerSecond);
                mediaBudgetAt = now;
                if (outboundMedia.Count == 0) return;
                var blocked = new HashSet<uint>();
                for (int sent = 0; sent < 96 && outboundMedia.Count != 0;)
                {
                    var transfer = NextOutbound(now, blocked);
                    if (transfer == null) break;
                    if (now < transfer.StartedAt || now - transfer.StartedAt > CdMediaAssembler.TimeoutSeconds || now - transfer.LastProgressAt > CdMediaAssembler.InactivityTimeoutSeconds)
                    {
                        NoteCd("host stopped sending a track to peer " + transfer.Peer + " after " +
                            (now - transfer.LastProgressAt).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " s without progress: beginSent=" +
                            transfer.BeginSent + "; chunk=" + transfer.NextChunk + "/" + transfer.ChunkCount + "; background=" + transfer.Prefetch +
                            "; " + CdLink(transfer.Peer), true);
                        SendAbort(transfer, 1); FinishOutbound(transfer); continue;
                    }
                    if (!byCdId.TryGetValue(transfer.Entity, out var selectedRadio) || selectedRadio.Radio == null)
                    { SendAbort(transfer, 2); FinishOutbound(transfer); continue; }
                    byte[] currentHash = EmptyCdHash;
                    if (!transfer.Prefetch) CaptureCdValues(selectedRadio, out _, out currentHash, out _);
                    if (transfer.Prefetch ? !WasOffered(transfer.Peer, transfer.Hash) || FindPlaylistMedia(transfer.Hash) == null :
                        CdPlaybackState.IsEmptyHash(currentHash) || !CdPlaybackState.HashEquals(currentHash, transfer.Hash))
                    { SendAbort(transfer, 2); FinishOutbound(transfer); continue; }
                    CdMediaMessage message;
                    try
                    {
                        if (!transfer.BeginSent)
                            message = CdMediaMessage.Begin(transfer.Entity, transfer.TransferId, transfer.Hash, (uint)transfer.Bytes.Length, transfer.DecodedBytes, transfer.Format, transfer.NextChunk);
                        else if (transfer.NextChunk < transfer.ChunkCount)
                        {
                            int offset = transfer.NextChunk * CdMediaMessage.ChunkBytes;
                            int length = Math.Min(CdMediaMessage.ChunkBytes, transfer.Bytes.Length - offset);
                            var data = new byte[length]; Buffer.BlockCopy(transfer.Bytes, offset, data, 0, length);
                            message = CdMediaMessage.Chunk(transfer.Entity, transfer.TransferId, transfer.NextChunk, data);
                        }
                        else
                        {
                            message = CdMediaMessage.Complete(transfer.Entity, transfer.TransferId, transfer.Hash);
                        }
                    }
                    catch (InvalidDataException) { FinishOutbound(transfer); continue; }
                    int cost = message.Data.Length == 0 ? 128 : message.Data.Length + 64;
                    if (mediaBudget < cost) break;
                    if (!SendMedia(transfer.Peer, message, transfer.Prefetch))
                    { transfer.NextSendAt = now + 0.01f; blocked.Add(transfer.Peer); mediaFairnessPeer = transfer.Peer; continue; }
                    mediaBudget -= cost; transfer.LastProgressAt = now; transfer.NextSendAt = !transfer.BeginSent ? now + 0.15f : now;
                    mediaFairnessPeer = transfer.Peer; sent++;
                    if (!transfer.BeginSent) transfer.BeginSent = true;
                    else if (transfer.NextChunk < transfer.ChunkCount) transfer.NextChunk++;
                    else
                    {
                        NoteCd("host finished sending a track to peer " + transfer.Peer + ": chunks=" + transfer.ChunkCount + "; seconds=" +
                            (now - transfer.StartedAt).ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
                        FinishOutbound(transfer);
                    }
                }
            }
            else
            {
                PumpLocalMediaLookups();
                PumpPreload(now);
                mediaAssembler.Expire(now);
            }
        }

        private OutboundMedia? NextOutbound(float now, HashSet<uint> blocked)
        {
            // Foreground playback outranks background warm-up; rotate peers within each priority.
            bool foreground = outboundMedia.Values.Any(t => !t.Prefetch && !blocked.Contains(t.Peer) && now >= t.NextSendAt);
            OutboundMedia? first = null, next = null;
            foreach (var transfer in outboundMedia.Values)
            {
                if (blocked.Contains(transfer.Peer) || now < transfer.NextSendAt || (foreground && transfer.Prefetch)) continue;
                if (first == null || transfer.Peer < first.Peer) first = transfer;
                if (transfer.Peer > mediaFairnessPeer && (next == null || transfer.Peer < next.Peer)) next = transfer;
            }
            return next ?? first;
        }

        private bool SendMedia(uint peer, CdMediaMessage message, bool background = false)
        {
            if (session?.SendCdMedia(peer, message.WriteBody, background) != true) return false;
            cdTransferRates.Sent(message.Data.Length);
            return true;
        }
        private void UpdateCdTransferIndicator(float now)
        {
            if (now < nextCdIndicatorUpdate) return;
            nextCdIndicatorUpdate = now + 0.5f;
            var counts = CdQueueCounts(now);
            PublishCdTransferProgress(now, counts.Uploads, counts.Downloads);
            cdTransferRates.Sample(now, incomingCdUpload != null || guestCdUpload != null || outboundMedia.Count != 0 || mediaAssembler.Active,
                counts.Uploads, counts.Downloads);
            cdTransferIndicator.Show(cdTransferRates.Text);
        }
        private void SendAbort(OutboundMedia transfer, byte reason)
        { try { SendMedia(transfer.Peer, CdMediaMessage.Abort(transfer.Entity, transfer.TransferId, reason)); } catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is ArgumentException) { } }
        private void FinishOutbound(OutboundMedia transfer)
        {
            outboundMedia.Remove(transfer.Peer);
            if (!hostMediaBuffers.TryGetValue(transfer.MediaKey, out var buffer)) return;
            buffer.References--;
            if (buffer.References <= 0)
            { hostMediaBuffers.Remove(transfer.MediaKey); hostBufferedBytes -= buffer.Bytes.LongLength; }
        }

        private void OnCdMedia(uint peer, Packet packet)
        {
            try
            {
                if (host)
                {
                    var upload = CdMediaMessage.ReadBody(packet.Reader); packet.Complete();
                    cdTransferRates.Received(upload.Data.Length);
                    ReceiveCdUpload(peer, upload); return;
                }
                if (!CdMediaAuthorityPolicy.MayReceive(host, peer))
                {
                    packet.Reader.BaseStream.Position = packet.Reader.BaseStream.Length;
                    packet.Complete();
                    return;
                }
                CdMediaMessage message = CdMediaMessage.ReadBody(packet.Reader);
                packet.Complete();
                cdTransferRates.Received(message.Data.Length);
                if (message.Kind == CdMediaMessageKind.UploadReply) { ReceiveCdUploadReply(message); return; }
                if (message.Kind == CdMediaMessageKind.Offer)
                {
                    // Audio and world records use separate reliable channels. Retain a bounded
                    // authenticated offer even if its newly registered radio record arrives later.
                    if (playlistOffers.Count < PersistentCdMediaCache.MaximumTracks)
                    {
                        string offeredKey = Convert.ToBase64String(message.ContentHash);
                        if (!playlistOffers.ContainsKey(offeredKey) && playlistOffers.Values.Sum(o => (long)o.FileBytes) + message.FileBytes <= PersistentCdMediaCache.MaximumBytes)
                            playlistOffers.Add(offeredKey, message);
                    }
                    return;
                }
                if (guestRequestEntity != message.RadioEntity || guestRequestedHash == null || !byCdId.ContainsKey(message.RadioEntity)) return;
                byte[] expectedHash = guestRequestedHash;
                if (message.Kind == CdMediaMessageKind.Begin && !CdPlaybackState.HashEquals(message.ContentHash, expectedHash)) return;
                if (message.Kind != CdMediaMessageKind.Begin && message.TransferId != mediaAssembler.ActiveTransferId) return;
                var state = new CdPlaybackState(0, expectedHash, 0);
                string key = Convert.ToBase64String(state.ContentHash);
                if (transferredMedia.ContainsKey(key))
                {
                    // A local verified copy can become ready while reliable chunks are in
                    // flight. Ignore those leftovers and stop only the correlated transfer.
                    if (message.Kind == CdMediaMessageKind.Begin && CdPlaybackState.HashEquals(message.ContentHash, state.ContentHash))
                        ConfirmCachedMedia(message);
                    return;
                }
                bool accepted = mediaAssembler.Accept(message, message.RadioEntity, state.ContentHash, Time.unscaledTime,
                    out byte[]? bytes, out CdMediaFormat format, out uint decodedBytes);
                if (!accepted)
                {
                    NoteCd("guest could not use a CD " + message.Kind + " message (chunk=" + message.ChunkIndex + "); the transfer will be requested again", true);
                    guestRequestEntity = 0; guestRequestedHash = null;
                    return;
                }
                if (message.Kind == CdMediaMessageKind.Abort)
                    NoteCd("the host stopped a track transfer: reason=" + message.AbortReason, true);
                if (bytes != null) NoteCd("guest received a complete track: bytes=" + bytes.Length + "; background=" + guestPrefetch);
                if (message.Kind == CdMediaMessageKind.Begin)
                {
                    NoteCd("guest receiving a track: bytes=" + message.FileBytes + "; chunks=" + message.ChunkCount +
                        "; startChunk=" + message.ChunkIndex + "; background=" + guestPrefetch);
                    guestRequestEntity = message.RadioEntity;
                    guestRequestedHash = (byte[])state.ContentHash.Clone();
                    guestRequestAt = Time.unscaledTime;
                    guestAnnouncement = message;
                    if (!guestPrefetch) StartLocalMediaLookup(message, key);
                }
                else if (message.Kind == CdMediaMessageKind.Abort)
                {
                    guestRequestEntity = 0; guestRequestedHash = null;
                }
                if (bytes != null)
                {
                    if (guestPrefetch)
                    {
                        var plugin = MonMPPlugin.Instance;
                        if (plugin != null && loadingMedia.Add(key)) plugin.StartCoroutine(StorePreloadedMedia(bytes, guestAnnouncement!, key));
                        guestRequestEntity = 0; guestRequestedHash = null; guestPrefetch = false;
                        return;
                    }
                    if (transferredMedia.TryGetValue(key, out var loaded)) ApplyPendingHash(loaded, state.ContentHash);
                    else if (loadingMedia.Add(key))
                    {
                        var plugin = MonMP.Plugin.MonMPPlugin.Instance;
                        if (plugin != null) plugin.StartCoroutine(LoadTransferredMedia(bytes, state.ContentHash, format, decodedBytes, key));
                        else loadingMedia.Remove(key);
                    }
                }
            }
            catch (InvalidDataException)
            {
                if (host)
                {
                    if (incomingCdUpload?.Peer == peer) EndCdUpload(CdUploadStatus.Rejected);
                    try { packet.Reader.BaseStream.Position = packet.Reader.BaseStream.Length; packet.Complete(); } catch { }
                    return;
                }
                mediaAssembler.Cancel(); guestRequestEntity = 0; guestRequestedHash = null;
                try { packet.Reader.BaseStream.Position = packet.Reader.BaseStream.Length; packet.Complete(); } catch { }
            }
        }

        private void StartLocalMediaLookup(CdMediaMessage announcement, string key)
        {
            if (disposed || host || loadingMedia.Contains(key) || localMediaLookups.Count != 0 ||
                localMediaAttempted.Count >= CdMediaCachePolicy.MaximumTracks || !localMediaAttempted.Add(key)) return;
            string directory = NativeCdDirectory.Path;
            var token = cacheCancellation.Token;
            localMediaLookups.Add(key, new LocalMediaLookup
            {
                Announcement = announcement,
                Work = Task.Run(async () => await PersistentCdMediaCache.ReadAsync(cacheDirectory, announcement, token).ConfigureAwait(false) ??
                    await LocalCdMediaLookup.FindAsync(directory, announcement, token).ConfigureAwait(false), token)
            });
        }

        private void PumpLocalMediaLookups()
        {
            foreach (var pair in localMediaLookups.ToArray())
            {
                var lookup = pair.Value;
                if (!lookup.Work.IsCompleted) continue;
                localMediaLookups.Remove(pair.Key);
                byte[]? bytes = null;
                try { bytes = lookup.Work.GetAwaiter().GetResult(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException ||
                    ex is NotSupportedException || ex is System.Security.SecurityException || ex is OperationCanceledException || ex is CryptographicException) { }
                if (lookup.Prefetch)
                {
                    if (bytes != null && !disposed && loadingMedia.Add(pair.Key))
                    {
                        var preloadPlugin = MonMPPlugin.Instance;
                        if (preloadPlugin != null) preloadPlugin.StartCoroutine(StorePreloadedMedia(bytes, lookup.Announcement, pair.Key));
                        else loadingMedia.Remove(pair.Key);
                    }
                    preloadChecked.Add(pair.Key);
                    continue;
                }
                if (disposed || bytes == null || transferredMedia.ContainsKey(pair.Key) || loadingMedia.Contains(pair.Key) ||
                    !pendingCd.TryGetValue(lookup.Announcement.RadioEntity, out var state) ||
                    !CdPlaybackState.HashEquals(state.ContentHash, lookup.Announcement.ContentHash)) continue;
                var plugin = MonMPPlugin.Instance;
                if (plugin == null || !loadingMedia.Add(pair.Key)) continue;
                plugin.StartCoroutine(LoadLocalMedia(bytes, lookup.Announcement, pair.Key));
            }
        }

        private IEnumerator LoadLocalMedia(byte[] bytes, CdMediaMessage announcement, string key)
        {
            // The same cache decoder checks exact samples, duration and aggregate memory
            // limits. Local decode failure leaves network assembly available as fallback.
            yield return LoadTransferredMedia(bytes, announcement.ContentHash, announcement.Format, announcement.DecodedBytes, key, true);
            if (!disposed && transferredMedia.ContainsKey(key)) ConfirmCachedMedia(announcement);
        }

        private void ConfirmCachedMedia(CdMediaMessage announcement)
        {
            if (disposed || host || channel == null || !channel.Records.ContainsKey(announcement.RadioEntity)) return;
            if (mediaAssembler.ActiveTransferId == announcement.TransferId &&
                CdPlaybackState.HashEquals(mediaAssembler.ActiveHash, announcement.ContentHash)) mediaAssembler.Cancel();
            if (guestRequestEntity == announcement.RadioEntity && CdPlaybackState.HashEquals(guestRequestedHash, announcement.ContentHash))
            { guestRequestEntity = 0; guestRequestedHash = null; }
            try { channel.Request(announcement.RadioEntity, new CdMediaCacheReady(announcement.TransferId, announcement.ContentHash).Encode(), useLease: true); }
            catch (Exception ex) when (ex is InvalidDataException || ex is InvalidOperationException || ex is IOException) { }
        }

        private void TouchCachedMedia(CachedMedia cached)
        { cached.LastUsed = ++mediaUseOrder; }

        private bool MediaRetryReady(string key)
        { return !mediaRetries.TryGetValue(key, out var retry) || Time.unscaledTime >= retry.NextAttemptAt; }

        private void DeferMedia(string key, string reason)
        {
            if (disposed) return;
            if (!mediaRetries.TryGetValue(key, out var retry))
            {
                if (mediaRetries.Count >= CdMediaCachePolicy.MaximumTracks)
                    mediaRetries.Remove(mediaRetries.OrderBy(pair => pair.Value.NextAttemptAt).First().Key);
                mediaRetries.Add(key, retry = new MediaRetry());
            }
            retry.Attempts = Math.Min(4, retry.Attempts + 1);
            retry.NextAttemptAt = Time.unscaledTime + Math.Min(30f, 2f * (1 << (retry.Attempts - 1)));
            // A later Begin can use the persistent verified bytes again after a temporary failure.
            localMediaAttempted.Remove(key);
            WarnCdMedia(reason);
        }

        // Bounded transfer milestones, so a transfer that silently never starts or
        // stalls can be read from a player's log. Warnings keep their own allowance.
        private int cdInfoNotes, cdWarningNotes;
        private void NoteCd(string text, bool warning = false)
        {
            if (warning ? cdWarningNotes >= 24 : cdInfoNotes >= 40) return;
            if (warning) { cdWarningNotes++; MonMPPlugin.Instance?.LogCdWarning(text); }
            else { cdInfoNotes++; MonMPPlugin.Instance?.LogCdInfo(text); }
        }
        private string CdLink(uint peer)
        {
            try
            {
                var metrics = session?.MetricsFor(peer) ?? default;
                return "transport=" + session?.ActiveTransport + "; rttMs=" + metrics.RoundTripTimeMs +
                    "; cdBudgetBytesPerSecond=" + metrics.MediaBytesPerSecond + "; queuedBytes=" + metrics.ReliableQueueBytes +
                    "; queuedMediaBytes=" + metrics.MediaQueueBytes;
            }
            catch { return "link status unavailable"; }
        }

        private void WarnCdMedia(string reason)
        {
            if (mediaWarnings.Count < 16 && mediaWarnings.Add(reason))
                MonMPPlugin.Instance?.LogCdWarning(reason);
        }

        private void LogCdAudioMode(AudioClip clip)
        {
            if (loggedCdAudioModes.Count < 4 && loggedCdAudioModes.Add(clip.loadType))
                MonMPPlugin.Instance?.LogCdInfo((host ? "host playlist playback mode=" : "guest cache playback mode=") + clip.loadType +
                    (host ? "; host catalogue/playback cache; 256 MiB decoded-equivalent budget" : "; compressed audio requested, 256 MiB decoded-equivalent budget retained"));
        }

        private static long DecodedCdBytes(AudioClip clip)
        {
            long samples = (long)clip.samples * clip.channels;
            return clip.samples > 0 && clip.channels > 0 && samples <= long.MaxValue / 4L ? samples * 4L : -1;
        }

        private static string DescribeCdClip(AudioClip clip)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "decoded={0} bytes, duration={1:0.###} s, frequency={2} Hz, channels={3}, samples={4}, mode={5}",
                DecodedCdBytes(clip), clip.length, clip.frequency, clip.channels, clip.samples, clip.loadType);
        }

        private HashSet<SOUND_Radio> CachedMediaSounds()
        {
            var sounds = new HashSet<SOUND_Radio>();
            var manager = Singleton<SOUND_Manager>.i;
            if (manager != null && RadioCdNative.PlayingRadios(manager) is List<SOUND_Radio> playing)
                foreach (var sound in playing) if (sound != null) sounds.Add(sound);
            foreach (var binding in bindings.Values)
                if (binding.Sound != null) sounds.Add(binding.Sound);
            foreach (var radio in UnityEngine.Object.FindObjectsOfType<Furniture_Radio>(true))
                if (radio != null && Sound(radio) is SOUND_Radio sound) sounds.Add(sound);
            return sounds;
        }

        private bool IsCachedMediaPinned(string key, CachedMedia cached, HashSet<SOUND_Radio> sounds)
        {
            // These are host playlist entries, not a disposable listener decode cache.
            if (host && uploadedCdSources.ContainsKey(cached.Clip)) return true;
            foreach (var binding in bindings.Values)
            {
                if (host && binding.Sound != null && binding.Radio != null && IsOn(binding.Radio) && Band(binding.Radio) == 1)
                {
                    // Native seek changes this ordinal before the 10 Hz record capture,
                    // and PlayDelayed is not isPlaying yet. Pin that selection too.
                    var manager = Singleton<SOUND_Manager>.i;
                    var tracks = manager == null ? null : RadioCdNative.CdPlayer(manager);
                    int index = CdSong(binding.Sound);
                    if (tracks != null && index >= 0 && index < tracks.Count && ReferenceEquals(tracks[index], cached.Clip) ||
                        restoreUploadedCd && ReferenceEquals(binding.HostTrackClip, cached.Clip)) return true;
                }
                // A selected radio can be offscreen or outside the listener's audible range.
                if (binding.Radio != null && IsOn(binding.Radio) && Band(binding.Radio) == 1 &&
                    pendingCd.TryGetValue(binding.CdId, out var selected) && selected.HasContent &&
                    string.Equals(key, Convert.ToBase64String(selected.ContentHash), StringComparison.OrdinalIgnoreCase)) return true;
                // isPlaying is false during native PlayDelayed's half-second scheduling gap.
                float sinceStart = Time.unscaledTime - binding.AppliedAt;
                if (binding.Sound != null && sinceStart >= 0f && sinceStart < 1f && binding.AppliedTrackHash != null &&
                    string.Equals(key, Convert.ToBase64String(binding.AppliedTrackHash), StringComparison.OrdinalIgnoreCase) &&
                    binding.Sound.Speakers != null && binding.Sound.Speakers.Any(s => s != null && ReferenceEquals(s.clip, cached.Clip))) return true;
            }
            foreach (var sound in sounds)
                if (sound != null && sound.Speakers != null && sound.Speakers.Any(s => s != null && s.isPlaying && ReferenceEquals(s.clip, cached.Clip))) return true;
            return false;
        }

        private bool TryReserveMedia(string key, long fileBytes, long decodedBytes, out MediaReservation? reservation)
        {
            reservation = null;
            if (disposed || mediaReservations.ContainsKey(key) || fileBytes > CdMediaMessage.MaximumFileBytes ||
                !CdMediaCachePolicy.CanReserveFile(0, fileBytes) || !CdMediaCachePolicy.CanRetainDecoded(0, decodedBytes)) return false;
            long memoryBytes = decodedBytes;
            if (host && (!CdMediaCachePolicy.CanReserveFile(transferredFileBytes + reservedFileBytes, fileBytes) ||
                transferredMedia.Count + mediaReservations.Count >= CdMediaCachePolicy.MaximumTracks)) return false;
            HashSet<SOUND_Radio>? sounds = null;
            while (!CdMediaCachePolicy.CanReserveFile(transferredFileBytes + reservedFileBytes, fileBytes) ||
                !CdMediaCachePolicy.CanRetainDecoded(decodedCacheBytes + reservedDecodedBytes + hostPlaybackReservedBytes, memoryBytes) ||
                transferredMedia.Count + mediaReservations.Count >= CdMediaCachePolicy.MaximumTracks)
            {
                if (host) { if (!EvictHostCdPlayback()) return false; continue; }
                sounds ??= CachedMediaSounds();
                var candidate = transferredMedia.OrderBy(pair => pair.Value.LastUsed)
                    .FirstOrDefault(pair => !IsCachedMediaPinned(pair.Key, pair.Value, sounds));
                if (candidate.Value == null) return false;
                RemoveCachedMedia(candidate.Key, candidate.Value, sounds);
            }
            reservation = new MediaReservation { FileBytes = fileBytes, DecodedBytes = decodedBytes, MemoryBytes = memoryBytes };
            mediaReservations.Add(key, reservation);
            reservedFileBytes += fileBytes;
            reservedDecodedBytes += memoryBytes;
            return true;
        }

        private bool CanCommitMedia(MediaReservation reservation, long decodedBytes)
        {
            return !disposed && mediaReservations.Values.Contains(reservation) && decodedBytes == reservation.DecodedBytes &&
                transferredMedia.Count + mediaReservations.Count <= CdMediaCachePolicy.MaximumTracks &&
                CdMediaCachePolicy.CanReserveFile(transferredFileBytes + reservedFileBytes - reservation.FileBytes, reservation.FileBytes) &&
                CdMediaCachePolicy.CanRetainDecoded(decodedCacheBytes + reservedDecodedBytes + hostPlaybackReservedBytes - reservation.MemoryBytes, reservation.MemoryBytes);
        }

        private void ReleaseMediaReservation(string key, MediaReservation reservation)
        {
            // Disposal clears all reservations; a suspended coroutine's finally must not release twice.
            if (!mediaReservations.TryGetValue(key, out var current) || !ReferenceEquals(current, reservation)) return;
            mediaReservations.Remove(key);
            reservedFileBytes -= reservation.FileBytes;
            reservedDecodedBytes -= reservation.MemoryBytes;
        }

        private void RemoveCachedMedia(string key, CachedMedia cached, HashSet<SOUND_Radio> sounds, bool repairBindings = true)
        {
            if (!transferredMedia.Remove(key)) return;
            if (host) { uploadedCdSources.Remove(cached.Clip); playlistMedia.Remove(cached.Clip); }
            var manager = Singleton<SOUND_Manager>.i;
            var tracks = manager == null ? null : RadioCdNative.CdPlayer(manager);
            int index = tracks == null ? -1 : tracks.FindIndex(clip => ReferenceEquals(clip, cached.Clip));
            // Detach stale sources even after vanilla Load CD removed this clip from its list.
            foreach (var sound in sounds)
            {
                if (sound == null) continue;
                if (sound.Speakers != null)
                    foreach (var speaker in sound.Speakers)
                        if (speaker != null && ReferenceEquals(speaker.clip, cached.Clip)) { speaker.Stop(); speaker.clip = null; }
                if (index < 0) continue;
                if (CdSong(sound) == index)
                {
                    // Load CD can leave this ordinal pointing at A while this radio still
                    // selects and plays B. Repair B's index below without erasing its cursor.
                    bool retainedSelection = repairBindings && bindings.Values.Any(binding => ReferenceEquals(binding.Sound, sound) &&
                        pendingCd.TryGetValue(binding.CdId, out var selected) && selected.HasContent &&
                        transferredMedia.TryGetValue(Convert.ToBase64String(selected.ContentHash), out var remaining) && remaining.Clip != null);
                    CdSong(sound) = 0;
                    if (!retainedSelection) CdTime(sound) = 0;
                }
                else if (CdSong(sound) > index) CdSong(sound)--;
            }
            if (index >= 0 && tracks != null) tracks.RemoveAt(index);
            if (tracks != null)
            {
                foreach (var remaining in transferredMedia.Values)
                    remaining.Index = tracks.FindIndex(clip => ReferenceEquals(clip, remaining.Clip));
                if (repairBindings)
                    foreach (var binding in bindings.Values)
                        if (binding.Sound != null && pendingCd.TryGetValue(binding.CdId, out var selected) && selected.HasContent &&
                            transferredMedia.TryGetValue(Convert.ToBase64String(selected.ContentHash), out var remaining) && remaining.Clip != null)
                            RestoreCachedCdTrack(binding, remaining, tracks);
            }
            decodedCacheBytes -= cached.MemoryBytes;
            transferredFileBytes -= cached.FileBytes;
            localMediaAttempted.Remove(key);
            if (cached.Clip != null)
            {
                RadioCdNative.TrackSources.Remove(cached.Clip);
                UnityEngine.Object.Destroy(cached.Clip);
            }
            if (cached.CatalogueClip != null && !ReferenceEquals(cached.CatalogueClip, cached.Clip)) UnityEngine.Object.Destroy(cached.CatalogueClip);
        }

        private IEnumerator LoadTransferredMedia(byte[] bytes, byte[] hash, CdMediaFormat format, uint expectedDecodedBytes, string key, bool localFallback = false,
            CdUploadTicket? uploadTicket = null, MediaReservation? uploadReservation = null)
        {
            string path = "";
            AudioClip? clip = null;
            UnityWebRequest? request = null;
            Task<bool>? fileWrite = null;
            MediaReservation? reservation = uploadReservation;
            bool rejectContent = false;
            string? rejectionReason = null;
            string? retryReason = null;
            try
            {
                if (disposed || host && (uploadTicket == null || !ReferenceEquals(incomingCdUpload, uploadTicket))) yield break;
                if (bytes.Length < 1 || bytes.Length > CdMediaMessage.MaximumFileBytes ||
                    !CdMediaCachePolicy.CanRetainDecoded(0, expectedDecodedBytes) || !Enum.IsDefined(typeof(CdMediaFormat), format))
                { rejectContent = true; yield break; }
                var manager = Singleton<SOUND_Manager>.i;
                var tracks = manager == null ? null : RadioCdNative.CdPlayer(manager);
                if (manager == null || tracks == null) { retryReason = "guest audio manager is not ready; CD will retry"; yield break; }
                if (!TryCachePath(hash, format, out path)) { rejectContent = true; yield break; }
                // Persist verified bytes before the memory reservation. A temporarily
                // full decoded budget then retries from disk instead of the network.
                // A host must retain the encoded source for late listeners even
                // after the contributing guest leaves. This snapshot contains
                // only live cache entries already charged to the bounded budget.
                var retainedFiles = host ? transferredMedia.Values.Select(cached => cached.Path).ToArray() : null;
                fileWrite = Task.Run(() => PersistentCdMediaCache.WriteAsync(cacheDirectory, bytes, hash, format, cacheCancellation.Token, retainedFiles));
                cacheFileWrites.Add(fileWrite, path);
                while (!fileWrite.IsCompleted) yield return null;
                cacheFileWrites.Remove(fileWrite);
                bool fileReady = false;
                try { fileReady = fileWrite.GetAwaiter().GetResult(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException || ex is OperationCanceledException || ex is CryptographicException) { }
                fileWrite = null;
                if (disposed || host && !ReferenceEquals(incomingCdUpload, uploadTicket)) yield break;
                if (!fileReady) { retryReason = "guest CD cache file could not be written; CD will retry"; yield break; }
                if (reservation == null && !TryReserveMedia(key, bytes.LongLength, expectedDecodedBytes, out reservation))
                { retryReason = "guest CD cache is temporarily full of selected or playing tracks; CD will retry"; yield break; }
                request = TryCreateAudioRequest(path, format);
                if (request == null) { retryReason = "guest CD audio request could not be created; CD will retry"; yield break; }
                if (host) request.timeout = 30;
                if (request.downloadHandler is DownloadHandlerAudioClip audioDownload)
                {
                    // Validate this one clip before admitting its catalogue entry.
                    // Hosts discard its PCM and lazily load selected songs later.
                    audioDownload.streamAudio = false;
                    audioDownload.compressed = true;
                }
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    if (request.result == UnityWebRequest.Result.DataProcessingError) rejectContent = true;
                    else retryReason = "guest CD cache file could not be read; CD will retry";
                    yield break;
                }
                bool contentLoaded = true;
                try { clip = DownloadHandlerAudioClip.GetContent(request); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException || ex is NotSupportedException)
                { contentLoaded = false; }
                if (disposed || host && !ReferenceEquals(incomingCdUpload, uploadTicket)) yield break;
                if (!contentLoaded || clip == null) { rejectContent = true; yield break; }
                long decoded = DecodedCdBytes(clip);
                if (float.IsNaN(clip.length) || float.IsInfinity(clip.length) || clip.length <= 0 || clip.frequency <= 0 || decoded < 1)
                {
                    rejectContent = true;
                    rejectionReason = "guest decoded CD has invalid audio metadata (" + DescribeCdClip(clip) + ")";
                    yield break;
                }
                if (clip.length > 600f || !CdMediaCachePolicy.CanRetainDecoded(0, decoded))
                {
                    rejectContent = true;
                    rejectionReason = "guest decoded CD exceeds the 10 minute / 256 MiB limits (" + DescribeCdClip(clip) + ")";
                    yield break;
                }
                if (decoded != expectedDecodedBytes)
                {
                    rejectContent = true;
                    rejectionReason = "guest decoded CD size differs from the host declaration (declaredDecoded=" + expectedDecodedBytes + " bytes, " + DescribeCdClip(clip) + ")";
                    yield break;
                }
                LogCdAudioMode(clip);
                tracks = RadioCdNative.CdPlayer(manager);
                if (tracks == null) { retryReason = "guest audio manager is not ready; CD will retry"; yield break; }
                if (transferredMedia.TryGetValue(key, out var existing))
                { UnityEngine.Object.Destroy(clip); clip = null; ApplyPendingHash(existing, hash); yield break; }
                if (reservation == null || !CanCommitMedia(reservation, decoded))
                { retryReason = "guest CD cache capacity changed while decoding; CD will retry"; yield break; }
                clip.name = "MonMP_CD_" + Convert.ToBase64String(hash).Substring(0, 12);
                if (host)
                {
                    var catalogue = CreateHostCdCatalogueClip(clip);
                    UnityEngine.Object.Destroy(clip);
                    clip = catalogue;
                }
                int index = tracks.Count;
                tracks.Add(clip);
                long retainedBytes = host ? 0 : decoded;
                transferredMedia.Add(key, new CachedMedia { Clip = clip, CatalogueClip = host ? clip : null, Index = index, DecodedBytes = decoded, MemoryBytes = retainedBytes, FileBytes = bytes.LongLength, Path = path });
                if (host) RegisterUploadedCd(transferredMedia[key], hash, format);
                preloadReady.Add(key);
                decodedCacheBytes += retainedBytes;
                transferredFileBytes += bytes.LongLength;
                TouchCachedMedia(transferredMedia[key]);
                mediaRetries.Remove(key);
                clip = null;
                ReleaseMediaReservation(key, reservation);
                reservation = null;
                ApplyPendingHash(transferredMedia[key], hash);
                path = "";
            }
            finally
            {
                if (request != null) request.Dispose();
                if (clip != null) UnityEngine.Object.Destroy(clip);
                if (!disposed && fileWrite != null && !string.IsNullOrEmpty(path)) cacheFileWrites[fileWrite] = path;
                if (reservation != null) ReleaseMediaReservation(key, reservation);
                if (rejectContent && !localFallback && !disposed && !transferredMedia.ContainsKey(key) && rejectedMedia.Count < CdMediaCachePolicy.MaximumTracks)
                { rejectedMedia.Add(key); WarnCdMedia(rejectionReason ?? "guest CD content failed audio validation and was rejected for this session"); }
                else if (retryReason != null && !disposed && !transferredMedia.ContainsKey(key)) DeferMedia(key, retryReason);
                loadingMedia.Remove(key);
                if (CdPlaybackState.HashEquals(guestRequestedHash, hash) &&
                    (!localFallback || !mediaAssembler.Active || !CdPlaybackState.HashEquals(mediaAssembler.ActiveHash, hash)))
                { guestRequestEntity = 0; guestRequestedHash = null; }
            }
        }

        private bool TryCachePath(byte[] hash, CdMediaFormat format, out string path)
        {
            path = "";
            try { path = PersistentCdMediaCache.FilePath(cacheDirectory, hash, format); return true; }
            catch (ArgumentException) { return false; }
        }

        private static UnityWebRequest? TryCreateAudioRequest(string path, CdMediaFormat format)
        {
            try
            {
                var audioType = RadioCdNative.FormatToAudioType(format);
                if (audioType == AudioType.UNKNOWN) return null;
                return UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, audioType);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is UriFormatException || ex is InvalidOperationException || ex is NotSupportedException)
            { return null; }
        }

        private void ApplyPendingHash(CachedMedia media, byte[] hash)
        {
            var snapshot = BindingsSnapshot();
            foreach (var binding in snapshot)
                if (pendingCd.TryGetValue(binding.CdId, out var state) && CdPlaybackState.HashEquals(state.ContentHash, hash)) ReconcileCd(binding, state);
        }

        private Binding[] BindingsSnapshot()
        {
            if (bindingSnapshot == null) bindingSnapshot = bindings.Values.ToArray();
            return bindingSnapshot;
        }

        private static bool TryCaptureClock(short[]? previousSongs, bool refresh, out RadioStationClockState clock, int cdSongCount)
        {
            clock = null!;
            var manager = Singleton<SOUND_Manager>.i;
            if (manager == null) return false;
            bool changed = previousSongs == null || previousSongs.Length != RadioStationClockState.Stations;
            for (int i = 0; i < RadioStationClockState.Stations; i++)
            {
                short song = (short)Mathf.Clamp(Songs[i](manager), -1, short.MaxValue);
                if (!changed && previousSongs![i] != song) changed = true;
                // Mathf.Clamp preserves NaN. The old eager snapshot constructor
                // rejected it on every poll, including an otherwise quiet clock.
                if (float.IsNaN(Times[i](manager))) throw new InvalidDataException("Invalid radio station clock.");
            }
            if (!changed && !refresh) return false;
            var songs = new short[RadioStationClockState.Stations]; var times = new float[RadioStationClockState.Stations];
            for (int i = 0; i < songs.Length; i++)
            {
                songs[i] = (short)Mathf.Clamp(Songs[i](manager), -1, short.MaxValue);
                times[i] = Mathf.Clamp(Times[i](manager), -1f, 36000f);
            }
            clock = new RadioStationClockState(songs, times, cdSongCount);
            return true;
        }
        private void ApplyClock(RadioStationClockState clock)
        {
            SetCdSongCount(clock.CdSongCount);
            var manager = Singleton<SOUND_Manager>.i;
            if (manager == null) return;
            bool first = !clockApplied;
            for (int i = 0; i < StationNames.Length; i++)
            {
                var clips = Clips[i](manager);
                int song = clock.Songs[i];
                if (clips == null || song < 0 || song >= clips.Length || clips[song] == null) continue;
                ref int localSong = ref Songs[i](manager);
                ref float localTime = ref Times[i](manager);
                // Both sides advance offsets by frame time; resync only a new song or a real drift.
                if (!first && localSong == song && Mathf.Abs(localTime - clock.Times[i]) < 4f) continue;
                localSong = song;
                localTime = Mathf.Clamp(clock.Times[i], 0f, Mathf.Max(0f, clips[song].length - 0.01f));
                try
                {
                    if (i < 6) UpdateStation.Invoke(manager, new object[] { StationFrequencies[i] });
                    else UpdateBar.Invoke(manager, null);
                }
                catch (System.Reflection.TargetInvocationException) { } // bar not loaded yet
            }
            clockApplied = true;
        }

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            stagedRadios = null;
            if (!host)
                foreach (var binding in bindings.Values)
                    if (binding.Radio != null && binding.Intent.Revision != 0) Apply(binding, binding.Intent.Authority);
            if (session != null) { session.PoseReceived -= OnPose; session.PeerLeft -= OnLeft; }
            if (session != null) session.CdMediaReceived -= OnCdMedia;
            if (channel != null) { channel.SnapshotApplied -= Snapshot; channel.Changed -= Changed; channel.CommandRequested -= Command; channel.PeerSynchronized -= OnMediaPeerReady; channel.ReplyReceived -= Reply; }
            if (channel != null) channel.CommandRejected -= ButtonRejected;
            cacheCancellation.Cancel();
            DisposeCdUploads();
            DisposeTransferredMedia();
            ClearCdSongCount();
            cdPeerProgress.Clear();
            foreach (var transfer in outboundMedia.Values.ToArray()) FinishOutbound(transfer);
            foreach (var read in pendingMediaReads.Values.ToArray()) CancelPendingRead(read);
            foreach (var media in hostMediaByPath.Values.ToArray()) CancelHostMedia(media);
            hostMediaByPath.Clear(); playlistMedia.Clear(); offeredToPeer.Clear(); mediaPeersReady.Clear(); playlistOffers.Clear(); preloadReady.Clear(); preloadChecked.Clear(); preloadFailed.Clear();
            var cacheWrites = cacheFileWrites.ToArray();
            cacheFileWrites.Clear();
            var localLookups = localMediaLookups.Values.Select(lookup => (Task)lookup.Work).ToArray();
            localMediaLookups.Clear(); localMediaAttempted.Clear();
            if (cacheWrites.Length == 0 && localLookups.Length == 0) cacheCancellation.Dispose();
            else
            {
                _ = Task.WhenAll(cacheWrites.Select(pair => (Task)pair.Key).Concat(localLookups)).ContinueWith(done =>
                {
                    if (done.IsFaulted) { var ignored = done.Exception; }
                    cacheCancellation.Dispose();
                }, TaskScheduler.Default);
            }
            loadingMedia.Clear();
            bindings.Clear(); bindingSnapshot = null; byRadio.Clear(); byCdId.Clear(); pending.Clear(); pendingCd.Clear(); pendingCdAt.Clear(); presence.Clear();
            discovery.Clear();
            mediaRequestTimes.Clear(); outboundMedia.Clear(); hostMediaBuffers.Clear(); hostBufferedBytes = 0;
            rejectedMedia.Clear();
            mediaAssembler.Cancel(); guestRequestEntity = 0; guestRequestedHash = null;
            session = null; channel = null;
        }

        private void DisposeTransferredMedia()
        {
            DisposeHostCdPlayback();
            var sounds = CachedMediaSounds();
            foreach (var pair in transferredMedia.OrderByDescending(pair => pair.Value.Index).ToArray())
                RemoveCachedMedia(pair.Key, pair.Value, sounds, false);
            transferredMedia.Clear(); decodedCacheBytes = 0; transferredFileBytes = 0;
            mediaReservations.Clear(); reservedDecodedBytes = 0; reservedFileBytes = 0;
            mediaRetries.Clear();
        }

        // A standalone radio (not a vehicle dash radio, which needs ignition), preferring one without a
        // generator; ordered only by structure so host and guest pick the same one.

#if MONMP_DEVELOPMENT
        internal ulong TestAnyRadio => bindings.Values
            .Where(b => b.Radio != null && b.Radio.gameObject.activeInHierarchy && channel!.Records.ContainsKey(b.Id) && b.Radio.GetComponentInParent<NWH.VehiclePhysics2.VehicleController>() == null)
            .OrderBy(b => b.Radio.Get_HomeGenerator != null).ThenBy(b => b.Id).Select(b => b.Id).FirstOrDefault();


        internal string TestInventory => string.Join(",", bindings.Values.Select(b => b.Identity + (b.Radio != null && b.Radio.Get_HomeGenerator != null ? "[gen]" : "")));


        internal bool TestClockMatchesRecord
        {
            get
            {
                if (channel == null || !channel.Records.TryGetValue(RadioStationClockState.Entity, out var record)) return false;
                var clock = RadioStationClockState.Decode(record.Payload); var manager = Singleton<SOUND_Manager>.i;
                for (int i = 0; i < StationNames.Length; i++) if (clock.Songs[i] >= 0 && Songs[i](manager) != clock.Songs[i]) return false;
                return true;
            }
        }


        internal FurnitureRadioState TestNative(ulong id) => Capture(bindings[id]);


        internal Vector3 TestPosition(ulong id) => bindings[id].Radio.transform.position;


        internal void TestPress(ulong id, FurnitureRadioButton button, float value = 0)
        {
            var radio = bindings[id].Radio;
            switch (button)
            {
                case FurnitureRadioButton.Power: radio.Button_Power(); break;
                case FurnitureRadioButton.Volume: radio.Button_Volume(value); break;
                case FurnitureRadioButton.Preset: radio.Button_Presets((int)value); break;
                case FurnitureRadioButton.Band: radio.Button_FMCD(); break;
                case FurnitureRadioButton.SeekNext: radio.Button_SeekNext(); break;
                case FurnitureRadioButton.SeekPrevious: radio.Button_SeekPrev(); break;
            }
        }


        internal int TestStationSong(int station) => Songs[station](Singleton<SOUND_Manager>.i);
#endif

    }
}
