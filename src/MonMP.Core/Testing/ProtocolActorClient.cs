using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MonMP.Core.Persistence;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;

namespace MonMP.Core.Testing
{
    // A protocol-only player for exercising a real MonMP host. It uses the same
    // authenticated session, snapshot, profile, live-profile, and save-round code
    // as a game client, but never creates Unity objects or touches native saves.
    public sealed class ProtocolActorClient : IDisposable
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly string expectedScene;
        private readonly string expectedCatalog;
        private readonly ProfileRestoreChannel profileRestore;
        private readonly LiveProfileChannel liveProfiles;
        private readonly SaveRoundChannel saveRounds;
        private PlayerProfileState profileState;
        private PlayerPose currentPose;
        private bool worldLoadRequested;
        private bool joined;
        private bool saveBlocked;
        private bool disposed;
        private double profileRestoreDeadline = -1;

        public NetworkSession Session { get; }
        public WorldChannel World { get; }
        public Guid ProfileId => Session.ProfileId;
        public uint PeerId => Session.LocalPeerId;
        public string Status => Session.Status;
        public bool ProfileReady => profileRestore.Phase == ProfileRestorePhase.Applied || profileRestore.Phase == ProfileRestorePhase.Absent;
        public bool Ready => !disposed && Session.State == SessionState.Connected && World.FullySynchronized && ProfileReady;
        public bool Gated => saveBlocked;
        public bool CanAct => Ready && !Gated;
        public PlayerPose CurrentPose => currentPose;
        public PlayerProfileState ProfileState => profileState;
        public SavePhase SavePhase => saveRounds.Phase;
        public int PingMilliseconds => Session.PingMilliseconds;

        public ProtocolActorClient(ProtocolActorManifest manifest, string displayName, string password = "")
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            manifest.ValidateForClient();
            expectedScene = manifest.Scene;
            expectedCatalog = manifest.Catalog;
            currentPose = manifest.Spawn.ToPose();
            profileState = DefaultProfileState();

            Guid profileId = Guid.NewGuid();
            var options = new SessionOptions
            {
                GameHash = manifest.GameHash,
                ModVersion = manifest.ModVersion,
                Scene = manifest.Scene,
                DisplayName = displayName,
                ProfileId = profileId,
                ProfileKey = Guid.NewGuid(),
                ColorRgb = ColorFor(profileId),
                Password = password,
                WorldCatalog = "",
                LoadWorldAfterHandshake = true,
                Spawn = currentPose
            };
            Session = new NetworkSession(options);

            var catalog = manifest.RequiredRecords
                .Select(record => new WorldRecord(record.Id, 1, record.Adapter, Array.Empty<byte>())).ToArray();
            World = new WorldChannel(Session, catalog, manifest.DynamicAdapters, manifest.LeaseAdapters,
                clock: () => clock.Elapsed.TotalSeconds, requiredDynamicEntities: manifest.RequiredDynamicEntities);
            profileRestore = new ProfileRestoreChannel(Session, World, _ => null);
            liveProfiles = new LiveProfileChannel(Session, World, () => clock.Elapsed.TotalSeconds);
            saveRounds = new SaveRoundChannel(Session, World, () => clock.Elapsed.TotalSeconds,
                () => Array.Empty<uint>(), _ => { }, blocked => saveBlocked = blocked, () => { },
                () => Ready, () => profileState);
            Session.CdMediaReceived += DiscardCdMedia;
        }

        public void Join(string host, int port)
        {
            ThrowIfDisposed();
            if (joined) throw new InvalidOperationException("A protocol actor instance can join only once; create a new actor for reconnects.");
            if (Session.State != SessionState.Offline) throw new InvalidOperationException("This actor is already connected or connecting.");
            Session.Join(host, port);
            joined = true;
        }

        // Call regularly from the owning process. This pumps the real transport and
        // standard world/profile/save channels; each actor should have one pump owner.
        public void Tick()
        {
            if (disposed) return;
            Session.Tick();
            if (!worldLoadRequested && Session.State == SessionState.LoadingWorld)
            {
                var hostWorld = Session.HostWorld ?? throw new InvalidDataException("Host omitted its world configuration.");
                if (hostWorld.Scene != expectedScene || hostWorld.Catalog != expectedCatalog)
                {
                    Session.Stop("Host world does not match the actor manifest.");
                    return;
                }
                Session.CompleteWorldLoad(hostWorld.Scene, hostWorld.Catalog);
                worldLoadRequested = true;
            }

            World.Pump();
            if (World.FullySynchronized && profileRestoreDeadline < 0)
                profileRestoreDeadline = clock.Elapsed.TotalSeconds + 30;
            if (profileRestore.Phase == ProfileRestorePhase.Ready)
                profileRestore.TryApply(message => profileState = message.State);
            if (!ProfileReady && profileRestoreDeadline >= 0 && clock.Elapsed.TotalSeconds > profileRestoreDeadline)
            {
                Session.Stop("Saved player profile restoration timed out.");
                return;
            }
            saveRounds.Update();
        }

        public bool SendPose(PlayerPose pose)
        {
            if (!CanAct) return false;
            pose.Validate();
            Session.SendPose(pose);
            currentPose = pose;
            return true;
        }

        public bool SendChat(string text)
        {
            if (!CanAct) return false;
            return Session.SendChat(text);
        }

        // Sends an adapter-defined payload through the ordinary WorldChannel path.
        // True means local submission only. Callers requiring success must observe a
        // matching adapter reply or the requested authoritative replicated state.
        // World.CommandRejected is a host-side notification, not a guest outcome signal.
        public bool Request(ulong entity, byte[] payload, bool useLease = false)
        {
            if (!CanAct || !World.Ready || payload == null || payload.Length > 128 || !World.Records.ContainsKey(entity)) return false;
            World.Request(entity, payload, useLease);
            return true;
        }

        public bool TryGetRecord(ulong entity, out WorldRecord record) => World.Records.TryGetValue(entity, out record!);

        public void SetProfileState(PlayerProfileState state)
        {
            ThrowIfDisposed();
            if (saveBlocked) throw new InvalidOperationException("Profile state is frozen for a save round.");
            profileState = state ?? throw new ArgumentNullException(nameof(state));
        }

        // Optional safe-profile refresh. LiveProfileChannel enforces its one-second
        // send bound; coordinated saves still use SaveRoundChannel below.
        public bool SubmitLiveProfile()
        {
            if (!CanAct) return false;
            return liveProfiles.Submit(profileState);
        }

        private static PlayerProfileState DefaultProfileState() => new PlayerProfileState(
            Enumerable.Repeat(1f, 7), new int[7], new float[PlayerProfileState.StatCount], 0);

        private static uint ColorFor(Guid id)
        {
            byte[] bytes = id.ToByteArray();
            uint color = (uint)(bytes[0] << 16 | bytes[1] << 8 | bytes[2]);
            return color < 0x202020 ? color ^ 0xA0A0A0u : color;
        }

        private static void DiscardCdMedia(uint peer, Packet packet)
        {
            // Session requires a media subscriber and a fully consumed packet. Decode
            // bounded protocol fields, but never cache or play audio in a headless actor.
            CdMediaMessage.ReadBody(packet.Reader);
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(ProtocolActorClient));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Session.CdMediaReceived -= DiscardCdMedia;
            saveRounds.Dispose();
            liveProfiles.Dispose();
            profileRestore.Dispose();
            World.Dispose();
            Session.Dispose();
        }
    }
}
