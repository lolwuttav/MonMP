using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using MonMP.Core.Protocol;
using MonMP.Core.Transport;

namespace MonMP.Core.Session
{
    public enum SessionState { Offline, Hosting, Connecting, LoadingWorld, RegisteringWorld, Connected }

    public sealed partial class NetworkSession : IDisposable
    {
        private sealed class Member
        {
            public uint PeerId;
            public double ConnectedAt, PacketTokenTime;
            public double PacketTokens = UnauthenticatedPacketBurst;
            public bool Ready;
            public bool WorldReady;
            public readonly ChatRateLimit ChatRate = new ChatRateLimit();
            public readonly ChatRateLimit PingRate = new ChatRateLimit(), CheatRate = new ChatRateLimit();
            public readonly CdMediaPacing CdPacing = new CdMediaPacing();
            public readonly int[] PacketKinds = new int[(int)MessageKind.SessionSettings + 1];
            public bool RateFailureReported;
            public string? DisconnectSummary;
        }
        private readonly SessionOptions options;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Dictionary<int, Member> connections = new Dictionary<int, Member>();
        // Timeout checks and kicks can remove connections while iterating. Keep their
        // detached view until membership changes instead of allocating one each frame.
        private KeyValuePair<int, Member>[]? connectionSnapshot;
        private KeyValuePair<int, Member>[] ConnectionSnapshot => connectionSnapshot ??= connections.ToArray();
        private readonly Dictionary<uint, PeerInfo> peers = new Dictionary<uint, PeerInfo>();
        private readonly Dictionary<uint, uint> sequences = new Dictionary<uint, uint>();
        // Cache successful world-send routes; readiness is still checked for every
        // send. Avoid walking all connections for every record for every peer.
        private readonly Dictionary<uint, int> worldSendConnections = new Dictionary<uint, int>();
        private INetworkTransport? transport;
        private Func<uint, uint, PlayerPose, bool>? poseRelayFilter;
        private int serverConnection = -1;
        // Poll dispatches packets on the Unity thread, so one frame stall can batch
        // several seconds of legal traffic. Preserve the 120/s sustained bound while
        // allowing a bounded 600-packet authenticated backlog (over 5s at the sender's
        // 80/s gameplay pace). CD uploads have their own bounded byte/packet allowance.
        // Longer stalls can still exhaust these abuse bounds; transport IO
        // continues independently of Poll. Before authentication keep the smaller bound.
        private const double HostPacketRate = 120, AuthenticatedPacketBurst = 600, UnauthenticatedPacketBurst = 120;
        private const double DirectPresenceReplyRate = 64.0;
        private double presenceReplyWindowAt;
        private int presenceRepliesInWindow;
        // Guest-side pacing under the host's per-client packet limit (120/s): a burst of world
        // commands (e.g. a cheat touching many objects) queues instead of getting the guest kicked.
        private const double GuestPacketRate = 80, GuestPacketBurst = 20;
        // World snapshot/delta traffic pauses with control headroom remaining below
        // the transport's hard per-peer queue cap.
        public const int WorldReliableQueueHighWaterPackets = ReliableLanePolicy.WorldQueueHighWaterPackets;
        public const long WorldReliableQueueHighWaterBytes = ReliableLanePolicy.WorldQueueHighWaterBytes;
        private double guestTokens = GuestPacketBurst, guestTokenTime;
        private readonly Queue<byte[]> guestReliable = new Queue<byte[]>();
        // The queue never spends the last token: a pose (20/s) always finds one, so a command backlog
        // cannot freeze this player's avatar and fail the host's fresh-pose checks. A backlog this deep
        // (about a minute of actions) means the session can no longer keep up.
        private const double PoseReserve = 1;
        private const int GuestQueueLimit = 4000;
        public int GuestQueueDepth => guestReliable.Count;
        private uint nextPeerId = 1, localSequence;
        private double joiningAt;
        private ChatRateLimit localChatRate = new ChatRateLimit();
        public SessionState State { get; private set; }
        public bool IsHost => State == SessionState.Hosting;
        public bool IsRunning => State != SessionState.Offline;
        public bool IsReady => State == SessionState.Hosting || State == SessionState.Connected;
        public Guid Epoch { get; private set; }
        public uint LocalPeerId { get; private set; }
        public Guid ProfileId => options.ProfileId;
#if MONMP_DEVELOPMENT
        // Read-only protocol metadata used to export a host actor manifest. Private
        // profile credentials and the session password deliberately stay private.
        internal string GameHashForActorManifest => options.GameHash;
        internal string ModVersionForActorManifest => options.ModVersion;
        internal string SceneForActorManifest => options.Scene;
        internal string CatalogForActorManifest => options.WorldCatalog;
        internal PlayerPose SpawnForActorManifest => options.Spawn;
#endif
        public SessionWorld? HostWorld { get; private set; }
        public int Port => transport?.LocalPort ?? 0;
        public TransportKind? ActiveTransport => transport?.Kind;
        public string TransportDescription => transport?.Description ?? "Offline";
        public string Status { get; private set; } = "Offline";
        public string LastPacketError { get; private set; } = "";
        public IReadOnlyDictionary<uint, PeerInfo> Peers => peers;
        public int PingMilliseconds => transport?.Ping(serverConnection) ?? 0;
        public long BytesSentPerSecond => transport?.BytesSentPerSecond ?? 0;
        public long BytesReceivedPerSecond => transport?.BytesReceivedPerSecond ?? 0;
        public TransportPeerMetrics MetricsFor(uint peerId)
        {
            if (transport == null) return default;
            if (!IsHost) return peerId == 0 ? transport.Metrics(serverConnection) : default;
            foreach (var pair in connections)
                if (pair.Value.PeerId == peerId && pair.Value.Ready) return transport.Metrics(pair.Key);
            return default;
        }
        public bool HasWorldReliableSendHeadroom(uint peerId)
        {
            if (!IsHost || transport == null) return false;
            // This is queried before every world record. Use the same validated
            // route as the send rather than scanning all connections per recipient.
            int connection = WorldConnectionFor(peerId);
            if (connection < 0) return false;
            var pressure = transport.Metrics(connection);
            // Steam's native pending queue is measured in bytes. It can fill
            // while the managed message queue is nearly empty; pause here before
            // dequeuing world records, retaining them for the next pump.
            return ReliableLanePolicy.WorldHasHeadroom(pressure);
        }
        public string LastPeerEvent { get; private set; } = "None";
        public event Action<PeerInfo>? PeerJoined;
        public event Action<PeerInfo>? PeerWorldReady;
        public event Action<uint>? PeerLeft;
        public event Action<uint, PlayerPose>? PoseReceived;
        // Sender-clock sample time is presentation metadata. Gameplay freshness
        // remains based on this session's local receive clock.
        public event Action<uint, PlayerPose, double>? PoseSampleReceived;
        public event Action<ChatMessage>? ChatReceived;
        public event Action<string>? StatusChanged;
        public event Action<string>? Diagnostic;
        public event Action<uint, Packet>? WorldPacketReceived;
        public event Action<uint, Packet>? CdMediaReceived;

        // WorldChannel may reduce pose fan-out by recipient. Without a world
        // channel, retain the original all-peers relay behavior.
        internal Func<uint, uint, PlayerPose, bool>? PoseRelayFilter
        {
            get => poseRelayFilter;
            set => poseRelayFilter = value;
        }

        public NetworkSession(SessionOptions options)
        {
            this.options = options.Copy();
            if (this.options.ProfileId == Guid.Empty) this.options.ProfileId = Guid.NewGuid();
            if (this.options.ProfileKey == Guid.Empty) this.options.ProfileKey = Guid.NewGuid();
            this.options.Validate();
            profileKeys[this.options.ProfileId] = SessionOptions.ProfileKeyHash(this.options.ProfileId, this.options.ProfileKey);
            profileNames[this.options.ProfileId] = this.options.DisplayName;
        }
        // Advisory compatibility metadata, retained for existing save sections; never an admission gate.
        private readonly Dictionary<Guid, byte[]> profileKeys = new Dictionary<Guid, byte[]>();
        private readonly Dictionary<Guid, string> profileNames = new Dictionary<Guid, string>();
        public byte[]? ProfileKeyHashOf(Guid profile) => profileKeys.TryGetValue(profile, out var hash) ? (byte[])hash.Clone() : null;

        // These legacy entry points deliberately remain Direct IP for protocol
        // actors, LAN tools, and existing integrations. The game UI chooses the
        // default Steam adapter explicitly before starting its session.
        public void Host(int port) => Host(new LiteNetTransport(true, options.EnableIPv6, port));

        public void Host(INetworkTransport sessionTransport)
        {
            RequireOffline();
            if (sessionTransport == null) throw new ArgumentNullException(nameof(sessionTransport));
            if (options.LoadWorldAfterHandshake) throw new InvalidOperationException("A host must load its world before listening.");
            try
            {
                Epoch = Guid.NewGuid(); LocalPeerId = 0;
                presenceReplyWindowAt = clock.Elapsed.TotalSeconds; presenceRepliesInWindow = 0;
                peers.Add(0, LocalInfo(0));
                State = SessionState.Hosting;
                OpenTransport(sessionTransport);
                SetStatus(sessionTransport.Kind == TransportKind.Direct ?
                    "Hosting MonMP on Direct UDP " + Port : "Hosting MonMP through " + sessionTransport.Description);
            }
            catch { Stop("Could not start host."); throw; }
        }

        public void Join(string address, int port)
        {
            RequireOffline();
            if (string.IsNullOrWhiteSpace(address) || port < 1 || port > 65535) throw new ArgumentException("Enter a host and port from 1 to 65535.");
            Join(new LiteNetTransport(false, options.EnableIPv6, 0, address.Trim(), port));
        }

        public void Join(INetworkTransport sessionTransport)
        {
            RequireOffline();
            if (sessionTransport == null) throw new ArgumentNullException(nameof(sessionTransport));
            options.DisplayName = PlayerNamePolicy.RequireCustom(options.DisplayName);
            try
            {
                State = SessionState.Connecting;
                joiningAt = clock.Elapsed.TotalSeconds;
                OpenTransport(sessionTransport);
                SetStatus("Connecting through " + sessionTransport.Description + "...");
            }
            catch { Stop("Could not connect to host."); throw; }
        }

        public void Kick(uint peerId)
        {
            Kick(peerId, "Removed by the host.");
        }

        public void Kick(uint peerId, string reason)
        {
            if (!IsHost || peerId == LocalPeerId) return;
            if (string.IsNullOrWhiteSpace(reason) || Encoding.UTF8.GetByteCount(reason) > 256)
                throw new ArgumentException("A bounded disconnect reason is required.", nameof(reason));
            reason = TransportDisconnectReasons.Normalize(reason);
            foreach (var pair in ConnectionSnapshot)
                if (pair.Value.Ready && pair.Value.PeerId == peerId)
                {
                    pair.Value.DisconnectSummary = reason;
                    transport!.Disconnect(pair.Key, reason);
                }
        }

        private void OpenTransport(INetworkTransport sessionTransport)
        {
            transport = sessionTransport;
            transport.Connected += OnConnected;
            transport.Disconnected += OnDisconnected;
            transport.Received += OnPacket;
            if (transport is IDirectDiscoveryTransport direct) direct.UnconnectedReceived += OnUnconnectedReceived;
            transport.Error += SetStatus;
            transport.Failed += Stop;
            transport.Start();
        }

        private void OnUnconnectedReceived(IPEndPoint endpoint, byte[] bytes)
        {
            if (!IsHost || !(transport is IDirectDiscoveryTransport direct) ||
                endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.Broadcast)) return;
            Guid request;
            try { request = LanDiscovery.DecodeQuery(bytes); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException) { return; }

            double now = clock.Elapsed.TotalSeconds;
            if (now - presenceReplyWindowAt >= 1)
            { presenceReplyWindowAt = now; presenceRepliesInWindow = 0; }
            if (presenceRepliesInWindow >= DirectPresenceReplyRate) return;

            try
            {
                var session = new LanSession(Epoch, options.DisplayName, Port, peers.Count, options.Password.Length > 0,
                    Wire.Version, options.ModVersion, options.GameHash);
                direct.SendUnconnected(LanDiscovery.EncodeResponse(request, session), endpoint);
                presenceRepliesInWindow++;
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is InvalidOperationException)
            { /* Presence is best effort and must not affect the active session. */ }
        }

        public void Tick()
        {
#if MONMP_PERFORMANCE_CAPTURE
            long pollStarted = Stopwatch.GetTimestamp();
            transport?.Poll();
            CaptureTiming.Detail("Session.Poll", Stopwatch.GetTimestamp() - pollStarted);
#else
            transport?.Poll();
#endif
            double now = clock.Elapsed.TotalSeconds;
            FlushGuestQueue();
            if (State == SessionState.Connecting && now - joiningAt > (transport?.Kind == TransportKind.Steam ? 30 : 8))
                Stop(transport?.Kind == TransportKind.Steam ?
                    "Steam connection timed out. Check Steam is online and ask the host for a new invite." :
                    "Connection timed out. Check host address and UDP port forwarding.");
            if ((State == SessionState.LoadingWorld || State == SessionState.RegisteringWorld) && now - joiningAt > 180)
                Stop("World loading timed out. Return to the menu and reconnect.");
            if (IsHost)
                foreach (var pair in ConnectionSnapshot)
                    if (!pair.Value.Ready && now - pair.Value.ConnectedAt > 5)
                    {
                        pair.Value.DisconnectSummary = "Handshake timed out before a valid Hello packet.";
                        SetLastPeerEvent("Guest " + pair.Value.DisconnectSummary);
                        transport!.Disconnect(pair.Key, "Handshake timed out.");
                        connections.Remove(pair.Key);
                        connectionSnapshot = null;
                    }
                    else if (pair.Value.Ready && !pair.Value.WorldReady && now - pair.Value.ConnectedAt > 180)
                        transport!.Disconnect(pair.Key, "World loading timed out.");
        }

        private void OnConnected(int connection)
        {
            double now = clock.Elapsed.TotalSeconds;
            connections[connection] = new Member { ConnectedAt = now, PacketTokenTime = now };
            connectionSnapshot = null;
            if (State == SessionState.Connecting)
            {
                serverConnection = connection;
                transport!.Send(connection, options.Hello(), true);
            }
        }

        private void OnDisconnected(int connection, string reason)
        {
#if MONMP_PERFORMANCE_CAPTURE
            long started = Stopwatch.GetTimestamp();
            try { OnDisconnectedCore(connection, reason); }
            finally { CaptureTiming.Detail("Session.Disconnected", Stopwatch.GetTimestamp() - started); }
        }
        private void OnDisconnectedCore(int connection, string reason)
        {
#endif
            worldSendConnections.Clear();
            if (!IsHost)
            {
                // Stop outside a callback would also be valid; LiteNetLib Stop does not poll events.
                Stop("Disconnected: " + reason);
                return;
            }
            if (connections.TryGetValue(connection, out var member) && member.Ready)
            {
                string name = peers.TryGetValue(member.PeerId, out var peer) ? peer.DisplayName : "Peer " + member.PeerId;
                SetLastPeerEvent(name + " left: " + (member.DisconnectSummary ?? reason));
                RemovePeer(member.PeerId);
                Broadcast(Wire.Encode(MessageKind.PeerLeft, Epoch, w => w.Write(member.PeerId)), true, connection);
            }
            else if (member != null)
                SetLastPeerEvent("Guest connection ended before authentication: " + (member.DisconnectSummary ?? reason));
            connections.Remove(connection);
            connectionSnapshot = null;
        }

        private void OnPacket(int connection, byte[] bytes, bool reliable)
        {
#if MONMP_PERFORMANCE_CAPTURE
            long started = Stopwatch.GetTimestamp();
            try { OnPacketCore(connection, bytes, reliable); }
            finally { CaptureTiming.Packet(bytes, Stopwatch.GetTimestamp() - started); }
        }
        private void OnPacketCore(int connection, byte[] bytes, bool reliable)
        {
#endif
            if (!connections.TryGetValue(connection, out var member)) return;
            double now = clock.Elapsed.TotalSeconds;
            // Per-client abuse bound, independent of player count. Count queued receipts
            // against a bounded token budget rather than one callback-time second.
            double receiveAge = -1, rateNow = now;
            if (IsHost && transport is IReceiveTimingTransport timing)
            {
                receiveAge = timing.CurrentReceiveAgeSeconds;
                // Only native/local receipt metadata can spread a queued batch.
                // Unknown, invalid or excessively old metadata keeps the existing
                // callback-time bound, including before connection establishment.
                if (!double.IsNaN(receiveAge) && !double.IsInfinity(receiveAge) && receiveAge >= 0 && receiveAge <= 60)
                    rateNow = Math.Max(member.ConnectedAt, now - receiveAge);
            }
            bool upload = IsHost && reliable && member.Ready && member.WorldReady && IsReady &&
                CdMediaReceived != null && !string.IsNullOrEmpty(options.WorldCatalog) && Wire.HasEnvelope(bytes, MessageKind.CdMedia, Epoch);
            if (IsHost && !(upload ? member.CdPacing.TryReceiveUpload(rateNow, bytes.Length) : TakeHostPacket(member, rateNow)))
            {
                ReportPacketRateFailure(connection, member, bytes, upload, receiveAge);
                if (!member.Ready)
                {
                    member.DisconnectSummary = "Packet rate exceeded before authentication.";
                    SetLastPeerEvent("Rejected unauthenticated guest: packet rate exceeded.");
                }
                string reason = upload ? "CD upload rate exceeded." : "Packet rate exceeded.";
                member.DisconnectSummary ??= reason;
                transport!.Disconnect(connection, reason); return;
            }
            if (IsHost && bytes.Length > 6 && bytes[6] < member.PacketKinds.Length && member.PacketKinds[bytes[6]] < int.MaxValue)
                member.PacketKinds[bytes[6]]++;
            try
            {
                using var packet = Wire.Decode(bytes);
                if ((packet.Kind != MessageKind.Pose) != reliable) throw new InvalidDataException("Incorrect message delivery mode.");
                if (IsHost && !member.Ready)
                {
                    AcceptHello(connection, member, packet);
                    return;
                }
                if (State == SessionState.Connecting)
                {
                    // A host pose travels unreliably and can overtake the reliable welcome (#112).
                    if (!reliable) return;
                    AcceptWelcome(packet);
                    member.Ready = true;
                    return;
                }
                if (packet.Epoch != Epoch) return; // Delayed packets from a previous session are inert.
                switch (packet.Kind)
                {
                    case MessageKind.SessionSettings:
                        ReceiveSessionSettings(packet);
                        break;
                    case MessageKind.CheatState:
                    case MessageKind.CheatAction:
                    case MessageKind.CheatFeedback:
                    case MessageKind.SocialPing:
                        ReceiveSocial(member, packet, now);
                        break;
                    case MessageKind.CdMedia:
                        if (!IsReady || IsHost && !member.WorldReady || string.IsNullOrEmpty(options.WorldCatalog) || CdMediaReceived == null)
                            throw new InvalidDataException("Unauthorized CD media message.");
                        CdMediaReceived.Invoke(IsHost ? member.PeerId : 0, packet);
                        packet.Complete();
                        break;
                    case MessageKind.Chat:
                        if (!IsReady || IsHost && !member.WorldReady) throw new InvalidDataException("Chat before scene registration.");
                        var chat = ChatMessage.Read(packet.Reader); packet.Complete();
                        if (IsHost && chat.PeerId != member.PeerId) throw new InvalidDataException("Chat identity spoofing.");
                        if (!peers.ContainsKey(chat.PeerId)) throw new InvalidDataException("Unknown chat sender.");
                        if (IsHost && !member.ChatRate.Take(now)) return;
                        ChatReceived?.Invoke(chat);
                        if (IsHost) Broadcast(Wire.Encode(MessageKind.Chat, Epoch, chat.Write), true, worldOnly: true);
                        break;
                    case MessageKind.Pose:
                        if (IsHost && !member.WorldReady) throw new InvalidDataException("Pose before scene registration.");
                        if (!IsReady) break;
                        uint id = packet.Reader.ReadUInt32();
                        uint sequence = packet.Reader.ReadUInt32();
                        double sampledAt = packet.Reader.ReadDouble();
                        if (double.IsNaN(sampledAt) || double.IsInfinity(sampledAt) || sampledAt < 0)
                            throw new InvalidDataException("Invalid pose sample time.");
                        var pose = PlayerPose.Read(packet.Reader).ReadCarrier(packet.Reader);
                        packet.Complete();
                        if (IsHost && id != member.PeerId) throw new InvalidDataException("Player identity spoofing.");
                        if (id == LocalPeerId || !peers.ContainsKey(id)) return;
                        if (sequences.TryGetValue(id, out uint previous) && !Wire.IsNewer(sequence, previous)) return;
                        sequences[id] = sequence;
                        latestSocialPoses[id] = (pose, now);
                        PoseReceived?.Invoke(id, pose);
                        PoseSampleReceived?.Invoke(id, pose, sampledAt);
                        if (IsHost) RelayPose(bytes, id, pose, connection);
                        break;
                    case MessageKind.SceneReady when IsHost:
                        string scene = Wire.ReadText(packet.Reader, 200), catalog = Wire.ReadText(packet.Reader, 64); packet.Complete();
                        if (member.WorldReady || scene != options.Scene || catalog != options.WorldCatalog)
                            throw new InvalidDataException("Scene registration does not match the host.");
                        member.WorldReady = true;
                        transport!.Send(connection, Wire.Encode(MessageKind.SceneAccepted, Epoch, _ => { }), true);
                        SendCheatState(connection);
                        SendSessionSettings(connection);
                        PeerWorldReady?.Invoke(peers[member.PeerId]);
                        break;
                    case MessageKind.SceneAccepted when !IsHost:
                        packet.Complete();
                        if (State != SessionState.RegisteringWorld) throw new InvalidDataException("Unexpected scene acceptance.");
                        State = SessionState.Connected; SetStatus("Scene accepted; synchronizing host world...");
                        break;
                    case MessageKind.PeerJoined when !IsHost:
                        var peer = PeerInfo.Read(packet.Reader);
                        packet.Complete();
                        if (peers.ContainsKey(peer.Id) || peers.Values.Any(p => p.ProfileId == peer.ProfileId))
                            throw new InvalidDataException("Duplicate peer identity.");
                        peers.Add(peer.Id, peer); PeerJoined?.Invoke(peer);
                        break;
                    case MessageKind.PeerLeft when !IsHost:
                        uint removed = packet.Reader.ReadUInt32(); packet.Complete();
                        if (removed == 0 || removed == LocalPeerId) throw new InvalidDataException("Invalid departure.");
                        RemovePeer(removed);
                        break;
                    case MessageKind.SnapshotBegin:
                    case MessageKind.SnapshotRecord:
                    case MessageKind.SnapshotEnd:
                    case MessageKind.WorldReply:
                    case MessageKind.WorldDelta:
                    case MessageKind.WorldCommand:
                    case MessageKind.ResyncRequest:
                    case MessageKind.WorldCheckpoint:
                    case MessageKind.SnapshotCaughtUp:
                    case MessageKind.SnapshotAcknowledged:
                        if (!IsReady || (IsHost && !member.WorldReady) || string.IsNullOrEmpty(options.WorldCatalog) || WorldPacketReceived == null ||
                            (IsHost != (packet.Kind == MessageKind.WorldCommand || packet.Kind == MessageKind.ResyncRequest || packet.Kind == MessageKind.SnapshotAcknowledged))) throw new InvalidDataException("Unauthorized world message.");
                        WorldPacketReceived.Invoke(IsHost ? member.PeerId : 0, packet);
                        packet.Complete();
                        break;
                    default: throw new InvalidDataException("Unexpected message for session state.");
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ArgumentException || ex is DecoderFallbackException)
            {
                LastPacketError = ex.GetType().Name + ": " + ex.Message;
                if (IsHost)
                {
                    if (!member.Ready)
                    {
                        member.DisconnectSummary = "Malformed or incompatible handshake packet.";
                        SetLastPeerEvent("Rejected malformed guest handshake packet.");
                    }
                    else member.DisconnectSummary ??= "Invalid packet: " + LastPacketError;
                    transport!.Disconnect(connection, "Invalid or incompatible packet.");
                }
                else Stop("Invalid or incompatible host packet.");
            }
        }

        private static bool TakeHostPacket(Member member, double now)
        {
            now = Math.Max(now, member.PacketTokenTime);
            double burst = member.Ready ? AuthenticatedPacketBurst : UnauthenticatedPacketBurst;
            member.PacketTokens = Math.Min(burst, member.PacketTokens + Math.Max(0, now - member.PacketTokenTime) * HostPacketRate);
            member.PacketTokenTime = now;
            if (member.PacketTokens < 1) return false;
            member.PacketTokens -= 1;
            return true;
        }

        private void ReportPacketRateFailure(int connection, Member member, byte[] bytes, bool upload, double receiveAge)
        {
            if (member.RateFailureReported) return;
            member.RateFailureReported = true;
            TransportPeerMetrics pressure = default;
            try { pressure = transport?.Metrics(connection) ?? default; } catch { }
            try
            {
                string counts = string.Join(",", member.PacketKinds.Select((count, kind) => (count, kind))
                    .Where(value => value.count != 0).Select(value => ((MessageKind)value.kind) + "=" + value.count));
                Diagnostic?.Invoke("MonMP packet-rate limit: peer=" + member.PeerId + "; authenticated=" + member.Ready +
                    "; worldReady=" + member.WorldReady + "; budget=" + (upload ? "CD upload" : "gameplay") +
                    "; kind=" + (bytes.Length > 6 ? ((MessageKind)bytes[6]).ToString() : "truncated") +
                    "; receiveAgeMs=" + (receiveAge * 1000).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) +
                    "; transport=" + transport?.Kind + "; rttMs=" + pressure.RoundTripTimeMs +
                    "; queuedPackets=" + pressure.ReliableQueuePackets + "; queuedBytes=" + pressure.ReliableQueueBytes +
                    "; acceptedKinds=" + counts);
            }
            catch { } // Diagnostic failure cannot prevent the peer-only disconnect.
        }

        // Guests one host admits. Every downstream limit must fit it plus the host: the save barrier
        // takes at most 64 participants.
        public const int MaxGuests = 15;

        private void AcceptHello(int connection, Member member, Packet packet)
        {
            if (packet.Kind != MessageKind.Hello || packet.Epoch != Guid.Empty) throw new InvalidDataException("Expected handshake.");
            string hash = Wire.ReadText(packet.Reader, 64);
            string mod = Wire.ReadText(packet.Reader, 32);
            string scene = Wire.ReadText(packet.Reader, 200);
            string password = Wire.ReadText(packet.Reader, 128);
            var hello = PeerInfo.Read(packet.Reader);
            string catalog = Wire.ReadText(packet.Reader, 64);
            byte deferred = packet.Reader.ReadByte();
            var key = new Guid(packet.Reader.ReadBytes(16));
            packet.Complete();
            if (deferred > 1) throw new InvalidDataException("Invalid scene loading mode.");
            string? refusal = null;
            if (hash != options.GameHash) refusal = "Game assembly does not match the host.";
            else if (mod != options.ModVersion) refusal = "MonMP version does not match the host.";
            else if (deferred == 0 && scene != options.Scene) refusal = "Load the same gameplay scene before joining.";
            else if (password != options.Password) refusal = "Incorrect session password.";
            else if (PlayerNamePolicy.ValidationError(hello.DisplayName) is string nameError) refusal = nameError;
            else if (deferred == 0 && catalog != options.WorldCatalog) refusal = "Shared object catalogs differ. Load matching worlds and mod settings.";
            else if (deferred == 1 && string.IsNullOrEmpty(options.WorldCatalog)) refusal = "Host has no registered world to load.";
            else if (peers.Count - 1 >= MaxGuests) refusal = "The session is full (" + MaxGuests + " guests).";
            else if (hello.Id != 0) refusal = "Invalid handshake identity.";
            else if (nextPeerId == uint.MaxValue) refusal = "Session identity space exhausted; restart the host.";
            if (refusal != null)
            {
                member.DisconnectSummary = refusal;
                SetLastPeerEvent("Guest join rejected: " + refusal);
                transport!.Disconnect(connection, refusal);
                return;
            }

            IEnumerable<KeyValuePair<Guid, string>> savedProfiles;
            try { savedProfiles = options.SavedProfileIdentities?.Invoke()?.ToArray() ?? Array.Empty<KeyValuePair<Guid, string>>(); }
            catch (Exception) { savedProfiles = Array.Empty<KeyValuePair<Guid, string>>(); } // corrupt optional history must not prevent joining
            Guid profile = ProfileIdentityPolicy.Resolve(hello.ProfileId, hello.DisplayName,
                savedProfiles.Concat(profileNames), peers.Values.Select(p => p.ProfileId));
            profileKeys[profile] = SessionOptions.ProfileKeyHash(profile, key);
            profileNames[profile] = hello.DisplayName;
            uint assignedId = nextPeerId++;
            var assigned = new PeerInfo(assignedId, profile, hello.DisplayName, hello.ColorRgb);
            var welcome = Wire.Encode(MessageKind.Welcome, Epoch, w => { w.Write(assignedId); w.Write(profile.ToByteArray()); peers[0].Write(w); options.World().Write(w); });
            transport!.Send(connection, welcome, true);
            foreach (var existing in peers.Values)
                if (existing.Id != 0)
                    transport.Send(connection, Wire.Encode(MessageKind.PeerJoined, Epoch, existing.Write), true);
            Broadcast(Wire.Encode(MessageKind.PeerJoined, Epoch, assigned.Write), true, connection);
            peers.Add(assignedId, assigned);
            SetLastPeerEvent(assigned.DisplayName + " joined");
            member.PeerId = assignedId; member.Ready = true;
            member.WorldReady = deferred == 0;
            PeerJoined?.Invoke(assigned);
            if (member.WorldReady) { SendCheatState(connection); SendSessionSettings(connection); PeerWorldReady?.Invoke(assigned); }
        }

        private void AcceptWelcome(Packet packet)
        {
            if (packet.Kind != MessageKind.Welcome || packet.Epoch == Guid.Empty) throw new InvalidDataException("Expected welcome.");
            uint assignedId = packet.Reader.ReadUInt32();
            var profile = new Guid(packet.Reader.ReadBytes(16));
            var host = PeerInfo.Read(packet.Reader);
            var world = SessionWorld.Read(packet.Reader);
            packet.Complete();
            if (assignedId == 0 || profile == Guid.Empty || host.Id != 0 || host.ProfileId == profile) throw new InvalidDataException("Invalid welcome identity.");
            if (options.LoadWorldAfterHandshake ? world.Catalog.Length == 0 : world.Scene != options.Scene || world.Catalog != options.WorldCatalog)
                throw new InvalidDataException("Host world configuration does not match the requested session.");
            HostWorld = world;
            options.ProfileId = profile;
            LocalPeerId = assignedId; Epoch = packet.Epoch;
            peers.Add(0, host); peers.Add(assignedId, LocalInfo(assignedId));
            SetLastPeerEvent("Connected to " + host.DisplayName);
            State = options.LoadWorldAfterHandshake ? SessionState.LoadingWorld : SessionState.Connected;
            joiningAt = clock.Elapsed.TotalSeconds;
            PeerJoined?.Invoke(host);
            SetStatus(options.LoadWorldAfterHandshake ? "Authenticated; loading host world..." : "Connected to MonMP preview");
        }

        public void CompleteWorldLoad(string scene, string catalog)
        {
            // Keep these gates in protocol order and report the exact failed one.
            // A catalog rejection otherwise looks like a missing receive subscription,
            // even after the plugin successfully constructed and attached its world.
            if (State != SessionState.LoadingWorld) throw WorldLoadFailure("state", scene, catalog);
            if (HostWorld == null) throw WorldLoadFailure("missingHostWorld", scene, catalog);
            if (scene != HostWorld.Scene) throw WorldLoadFailure("sceneMismatch", scene, catalog);
            if (catalog != HostWorld.Catalog) throw WorldLoadFailure("catalogMismatch", scene, catalog);
            if (WorldPacketReceived == null) throw WorldLoadFailure("missingReceiver", scene, catalog);
            options.Scene = scene; options.WorldCatalog = catalog;
            State = SessionState.RegisteringWorld;
            transport!.Send(serverConnection, Wire.Encode(MessageKind.SceneReady, Epoch, w => { Wire.WriteText(w, scene, 200); Wire.WriteText(w, catalog, 64); }), true);
            SetStatus("Scene registered; waiting for host snapshot...");
        }

        private InvalidOperationException WorldLoadFailure(string gate, string scene, string catalog) =>
            new InvalidOperationException("World load registration rejected: gate=" + gate +
                "; state=" + State + "; expectedState=" + SessionState.LoadingWorld +
                "; hostWorld=" + (HostWorld == null ? "missing" : "present") +
                "; receiver=" + (WorldPacketReceived == null ? "missing" : "present") +
                "; expectedScene=" + WorldLoadDiagnosticValue(HostWorld?.Scene, 200) +
                "; actualScene=" + WorldLoadDiagnosticValue(scene, 200) +
                "; expectedCatalog=" + WorldLoadDiagnosticValue(HostWorld?.Catalog, 64) +
                "; actualCatalog=" + WorldLoadDiagnosticValue(catalog, 64) + ".");
        private static string WorldLoadDiagnosticValue(string? value, int limit)
        {
            if (value == null) return "<missing>";
            var result = new StringBuilder(Math.Min(value.Length, limit) + 32);
            result.Append('"');
            for (int i = 0; i < value.Length && i < limit; i++)
            {
                char c = value[i];
                // Keep every failure on one bounded log line, even for invalid local input.
                if (c == '\r') result.Append("\\r");
                else if (c == '\n') result.Append("\\n");
                else if (c == '\t') result.Append("\\t");
                else if (c == '"' || c == '\\') { result.Append('\\'); result.Append(c); }
                else result.Append(char.IsControl(c) ? '?' : c);
            }
            result.Append('"');
            if (value.Length > limit) result.Append("[truncated; length=").Append(value.Length).Append(']');
            return result.ToString();
        }

        public void SendPose(PlayerPose pose)
        {
            if (!IsReady) return;
            double sampledAt = clock.Elapsed.TotalSeconds;
            latestSocialPoses[LocalPeerId] = (pose, sampledAt);
            var bytes = Wire.Encode(MessageKind.Pose, Epoch, w =>
            {
                w.Write(LocalPeerId); w.Write(++localSequence); w.Write(sampledAt); pose.Write(w); pose.WriteCarrier(w);
            });
            if (IsHost) RelayPose(bytes, LocalPeerId, pose, -1);
            else if (TakeGuestToken()) transport!.Send(serverConnection, bytes, false);
        }

        public bool SendChat(string text)
        {
            if (!IsReady) return false;
            var message = new ChatMessage(LocalPeerId, text);
            if (!localChatRate.Take(clock.Elapsed.TotalSeconds)) return false;
            var bytes = Wire.Encode(MessageKind.Chat, Epoch, message.Write);
            if (IsHost)
            {
                ChatReceived?.Invoke(message);
                Broadcast(bytes, true, worldOnly: true);
            }
            else
            {
                // Shares the existing 80/s guest budget with reliable world commands (#72).
                guestReliable.Enqueue(bytes);
                FlushGuestQueue();
            }
            return true;
        }

        private bool TakeGuestToken(double reserve = 0)
        {
            if (!GuestHasHeadroom(1 + reserve)) return false;
            guestTokens -= 1; return true;
        }

        private bool GuestHasHeadroom(double reserve)
        {
            double now = clock.Elapsed.TotalSeconds;
            guestTokens = Math.Min(GuestPacketBurst, guestTokens + (now - guestTokenTime) * GuestPacketRate);
            guestTokenTime = now;
            return guestTokens >= reserve;
        }

        private void FlushGuestQueue()
        {
            if (guestReliable.Count > GuestQueueLimit) { Stop("Too many queued actions for the connection; reconnect to continue."); return; }
            while (guestReliable.Count != 0 && !IsHost && transport != null && serverConnection >= 0 && TakeGuestToken(PoseReserve))
                transport.Send(serverConnection, guestReliable.Dequeue(), true);
        }

        private void RelayPose(byte[] bytes, uint source, PlayerPose pose, int except = -1)
        {
            foreach (var pair in connections)
            {
                var member = pair.Value;
                if (pair.Key == except || !member.Ready || !member.WorldReady) continue;
                if (poseRelayFilter != null && !poseRelayFilter(source, member.PeerId, pose)) continue;
                transport!.Send(pair.Key, bytes, false);
            }
        }

        public void SendWorld(uint peer, MessageKind kind, Action<BinaryWriter> body)
        {
            if (!IsReady || string.IsNullOrEmpty(options.WorldCatalog)) throw new InvalidOperationException("World channel is not ready.");
            if (IsHost)
            {
                if (kind != MessageKind.SnapshotBegin && kind != MessageKind.SnapshotRecord && kind != MessageKind.SnapshotEnd && kind != MessageKind.WorldDelta && kind != MessageKind.WorldCheckpoint && kind != MessageKind.SnapshotCaughtUp && kind != MessageKind.WorldReply)
                    throw new InvalidOperationException("Invalid host world message.");
                int connection = WorldConnectionFor(peer);
                if (connection >= 0) transport!.Send(connection, Wire.Encode(kind, Epoch, body), true);
            }
            else
            {
                if (peer != 0 || (kind != MessageKind.WorldCommand && kind != MessageKind.ResyncRequest && kind != MessageKind.SnapshotAcknowledged)) throw new InvalidOperationException("Invalid guest world message.");
                guestReliable.Enqueue(Wire.Encode(kind, Epoch, body));
                FlushGuestQueue();
            }
        }

        // Optional media yields to a congested reliable queue and has a separate bounded pace.
        // A false result leaves the caller's transfer/chunk pending for a later frame.
        public bool SendCdMedia(uint peer, Action<BinaryWriter> body, bool background = false)
        {
            if (!IsReady || string.IsNullOrEmpty(options.WorldCatalog))
                throw new InvalidOperationException("CD media requires a ready world session.");
            if (!IsHost)
            {
                if (peer != 0) throw new InvalidOperationException("CD uploads go only to the host.");
                if (serverConnection < 0 || transport == null || !connections.TryGetValue(serverConnection, out var server)) return false;
                var uploadPressure = transport.Metrics(serverConnection);
                int budget = CdMediaPacing.TransportBudget(transport.Kind, uploadPressure);
                if (budget == 0) { server.CdPacing.TryTakeUpload(clock.Elapsed.TotalSeconds, 0); return false; }
                if (guestReliable.Count != 0 || !ReliableLanePolicy.MediaHasHeadroom(uploadPressure, true) ||
                    !GuestHasHeadroom(PoseReserve + 5) || !server.CdPacing.TryTakeUpload(clock.Elapsed.TotalSeconds, budget)) return false;
                // Media has its own bounded sender/receiver budget. Keep the existing
                // gameplay reserve and leave optional uploads off the command queue.
                transport.Send(serverConnection, Wire.Encode(MessageKind.CdMedia, Epoch, body), true);
                return true;
            }
            int connection = WorldConnectionFor(peer);
            if (connection < 0 || !connections.TryGetValue(connection, out var member)) return false;
            var pressure = transport!.Metrics(connection);
            double now = clock.Elapsed.TotalSeconds;
            int mediaBudget = CdMediaPacing.TransportBudget(transport.Kind, pressure);
            if (mediaBudget == 0) { member.CdPacing.TryTake(now, background, 0); return false; }
            if (!ReliableLanePolicy.MediaHasHeadroom(pressure, background)) return false;
            if (!member.CdPacing.TryTake(now, background, mediaBudget)) return false;
            byte[] bytes = Wire.Encode(MessageKind.CdMedia, Epoch, body);
            transport.Send(connection, bytes, true);
            return true;
        }

        // WorldChannel supplies bytes produced by Wire.Encode in this same pump.
        // Immutable record fan-out can share serialization, while transport still
        // sends a separate reliable ordered message to each recipient.
        internal void SendEncodedWorldRecord(uint peer, MessageKind kind, byte[] bytes)
        {
            if (!IsHost || !IsReady || string.IsNullOrEmpty(options.WorldCatalog) ||
                (kind != MessageKind.SnapshotRecord && kind != MessageKind.WorldDelta))
                throw new InvalidOperationException("Encoded host world record is not ready.");
            int connection = WorldConnectionFor(peer);
            if (connection >= 0) transport!.Send(connection, bytes, true);
        }

        private int WorldConnectionFor(uint peer)
        {
            if (worldSendConnections.TryGetValue(peer, out int cached) &&
                connections.TryGetValue(cached, out var member) && member.Ready && member.WorldReady && member.PeerId == peer)
                return cached;
            foreach (var pair in connections)
                if (pair.Value.Ready && pair.Value.WorldReady && pair.Value.PeerId == peer)
                { worldSendConnections[peer] = pair.Key; return pair.Key; }
            worldSendConnections.Remove(peer);
            return -1;
        }

        private void Broadcast(byte[] bytes, bool reliable, int except = -1, bool worldOnly = false)
        {
            foreach (var pair in connections)
                if (pair.Key != except && pair.Value.Ready && (!worldOnly || pair.Value.WorldReady)) transport!.Send(pair.Key, bytes, reliable);
        }
        private PeerInfo LocalInfo(uint id) => new PeerInfo(id, options.ProfileId, options.DisplayName, options.ColorRgb);
        private void RemovePeer(uint id)
        {
            if (peers.TryGetValue(id, out var peer))
            {
                if (State != SessionState.Offline && !IsHost) SetLastPeerEvent(peer.DisplayName + " left");
                peers.Remove(id);
#if MONMP_PERFORMANCE_CAPTURE
                CaptureTiming.Each(PeerLeft, id);
#else
                PeerLeft?.Invoke(id);
#endif
            }
            sequences.Remove(id);
            latestSocialPoses.Remove(id);
            if (IsHost && Cheats.Granted(id)) SetCheats(new SessionCheatState(Cheats.Enabled, Cheats.GravityY, Cheats.Allowed.Where(peer => peer != id)));
        }
        private void RequireOffline()
        {
            if (IsRunning) throw new InvalidOperationException("Leave the current session first.");
        }
        private void SetStatus(string status) { Status = status; StatusChanged?.Invoke(status); }
        private void SetLastPeerEvent(string message)
        {
            const int maximumLength = 256;
            if (message.Length > maximumLength) message = message.Substring(0, maximumLength - 3) + "...";
            LastPeerEvent = message;
        }
        public void Stop(string reason = "Offline")
        {
            State = SessionState.Offline;
            // Detach before closing so even a backend that emits a final native
            // callback during cleanup cannot reenter the retiring session.
            var retiring = transport; transport = null;
            if (retiring != null)
            {
                retiring.Connected -= OnConnected;
                retiring.Disconnected -= OnDisconnected;
                retiring.Received -= OnPacket;
                retiring.Error -= SetStatus;
                retiring.Failed -= Stop;
                if (retiring is IDirectDiscoveryTransport direct) direct.UnconnectedReceived -= OnUnconnectedReceived;
                retiring.Dispose();
            }
            connections.Clear();
            connectionSnapshot = null;
            worldSendConnections.Clear();
            localChatRate = new ChatRateLimit();
            ResetSocial();
            Settings = new SessionSettingsState();
            foreach (uint id in peers.Keys.ToArray()) RemovePeer(id);
            sequences.Clear(); Epoch = Guid.Empty; LocalPeerId = 0;
            serverConnection = -1; nextPeerId = 1; localSequence = 0; guestReliable.Clear(); guestTokens = GuestPacketBurst;
            HostWorld = null;
            SetLastPeerEvent(reason);
            SetStatus(reason);
        }
        public void Dispose() => Stop();
    }
}
