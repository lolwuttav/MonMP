using System;
using System.Collections.Generic;
using System.Diagnostics;
using MonMP.Core.Transport;

namespace MonMP.Plugin.Networking
{
    internal sealed class SteamTransport : INetworkTransport, IReceiveTimingTransport
    {
        internal const int NativeSendBufferBytes = 128 * 1024;
        // Steam has no bandwidth estimation: with different minimum and maximum
        // rates a connection keeps its setup-time guess (about 4380 bytes per
        // round trip, near 22 KB/s before a ping is known) for its whole life.
        // Valve documents setting both to the same value, so keep the fixed
        // 1 MiB/s used from 0.9.36 to 0.9.76. Optional media still backs off
        // per link from loss and queue delay without changing gameplay admission.
        internal const int SendRateBytesPerSecond = 1024 * 1024;
        internal const int MinimumSendRateBytesPerSecond = SendRateBytesPerSecond;
        private const int NativePendingHighWaterBytes = 64 * 1024;
        private const int ReliableQueueLimitMessages = 4096;
        // CD audio may occupy only a small slice of Steam's native buffer, so a
        // world record queued behind it waits milliseconds rather than seconds.
        private const int MediaPendingHighWaterBytes = 16 * 1024;
        private const int MediaOutstandingHighWaterBytes = NativeSendBufferBytes - MediaPendingHighWaterBytes;
        // ConfigureConnectionLanes priorities: lower numbers drain first. World
        // reliable (0) and unreliable poses (1) share bandwidth; CD media (2)
        // uses only what they leave idle.
        internal static readonly int[] LanePriorities = { 0, 0, 1 };
        private sealed class Link
        {
            internal readonly int Id;
            internal readonly uint Handle;
            internal readonly ulong Identity;
            internal bool Connected, Closing;
            internal string Route = "";
            internal bool SendPressureReported;
            internal readonly Queue<byte[]> Reliable = new Queue<byte[]>(), Media = new Queue<byte[]>();
            internal readonly SteamMediaCongestion MediaCongestion = new SteamMediaCongestion();
            internal double NextMediaNotice;
            internal bool StatusReported;
            internal long ReliableBytes, MediaBytes;
            internal double ConnectedAt = -1, LastReceiveAt = -1, LastSendAt = -1, MetricsAt = -1;
            internal SteamLinkMetrics LastMetrics;
            internal int QueuedMessages => Reliable.Count + Media.Count;
            internal Link(int id, uint handle, ulong identity) { Id = id; Handle = handle; Identity = identity; }
        }
        private readonly ISteamBackend backend;
        private readonly bool host, forceRelay;
        private readonly string name;
        private readonly int members;
        private readonly ulong requestedLobby;
        private readonly Queue<Action> pending = new Queue<Action>();
        private readonly Dictionary<uint, Link> links = new Dictionary<uint, Link>();
        private readonly Dictionary<int, Link> ids = new Dictionary<int, Link>();
        // Links MonMP closed itself, kept briefly so Steam's own end reason for
        // them is still logged when its state change arrives afterwards.
        private readonly Dictionary<uint, int> retired = new Dictionary<uint, int>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
#if MONMP_DEVELOPMENT
        private readonly Func<double>? testClock;
#endif
        private double Now
        {
            get
            {
#if MONMP_DEVELOPMENT
                if (testClock != null) return testClock();
#endif
                return clock.Elapsed.TotalSeconds;
            }
        }
        private double startedAt, nextLobbyCheck, nextMetrics, rateAt;
        private double lastPollAt = -1, pollGap;
        private long sent, received, rateSent, rateReceived;
        private int nextId;
        private uint listener;
        private ulong expectedHost;
        private string nonce = "";
        private bool started, disposed, queueOverflow;

        internal SteamTransport(ISteamBackend backend, bool host, ulong lobby, string name, int members, bool forceRelay
#if MONMP_DEVELOPMENT
            , Func<double>? testClock = null
#endif
            )
        {
            this.backend = backend; this.host = host; requestedLobby = lobby; this.name = name;
            this.members = Math.Max(2, Math.Min(64, members)); this.forceRelay = forceRelay;
#if MONMP_DEVELOPMENT
            this.testClock = testClock;
#endif
            backend.LobbyCreated += OnLobbyCreated; backend.LobbyEntered += OnLobbyEntered;
            backend.LobbyChanged += OnLobbyChanged; backend.ConnectionChanged += OnConnection;
        }
        public TransportKind Kind => TransportKind.Steam;
        public string Description => forceRelay ? "Steam (relay required)" : "Steam";
        public int LocalPort => 0;
        public long BytesSentPerSecond { get; private set; }
        public long BytesReceivedPerSecond { get; private set; }
        public long ReliableQueueOverflowDisconnects { get; private set; }
        private double receiveAge = -1;
        private long receiveAgeSampledAt;
        public double CurrentReceiveAgeSeconds
        {
            get
            {
                if (receiveAge < 0 || receiveAgeSampledAt == 0) return receiveAge;
                double elapsed = (Stopwatch.GetTimestamp() - receiveAgeSampledAt) / (double)Stopwatch.Frequency;
                return elapsed >= 0 ? receiveAge + elapsed : -1;
            }
        }
        internal ulong LobbyId { get; private set; }
        internal bool IsHosting => host;
        internal bool Ready { get; private set; }
        internal bool IsDisposed => disposed;
        internal string Failure { get; private set; } = "";
        internal string Status { get; private set; } = "Steam session has not started.";
        public event Action<int>? Connected;
        public event Action<int, string>? Disconnected;
        public event Action<int, byte[], bool>? Received;
        public event Action<string>? Error;
        public event Action<string>? Failed;
        internal event Action<string>? Diagnostic;

        public void Start()
        {
            if (disposed || started) throw new InvalidOperationException("Steam transport cannot be restarted; create a new session.");
            if (!host && requestedLobby == 0) throw new ArgumentException("A valid Steam lobby is required.");
            started = true; startedAt = Now;
            Status = host ? "Creating a Steam friends lobby..." : "Joining the Steam lobby...";
            if (host) backend.CreateLobby(members); else backend.JoinLobby(requestedLobby);
        }

        private void Enqueue(Action work)
        {
            if (disposed) return;
            if (pending.Count >= 2048) { queueOverflow = true; return; }
            pending.Enqueue(work);
        }
        private void OnLobbyCreated(ulong lobby, string error) => Enqueue(() => Created(lobby, error));
        private void OnLobbyEntered(ulong lobby, string error) => Enqueue(() => Entered(lobby, error));
        private void OnLobbyChanged(ulong lobby) => Enqueue(() => { if (lobby == LobbyId) CheckLobby(); });
        private void OnConnection(SteamConnectionEvent change)
        {
            if (!links.ContainsKey(change.Handle) && !retired.ContainsKey(change.Handle) &&
                (!host || listener == 0 || change.Listener != listener)) return;
            Enqueue(() => Change(change));
        }

        private void Created(ulong lobby, string error)
        {
            if (!host || disposed) return;
            if (error.Length != 0 || lobby == 0) { Fail(error.Length == 0 ? "Steam did not create a lobby." : error); return; }
            LobbyId = lobby; expectedHost = backend.LocalIdentity; nonce = Guid.NewGuid().ToString("N");
            listener = backend.Listen(forceRelay);
            if (listener == 0) { Fail("Steam could not open a P2P listen socket. Check Steam authentication and relay diagnostics."); return; }
            foreach (var pair in SteamSessionMetadata.Create(expectedHost, name, nonce))
                if (!backend.SetLobbyData(lobby, pair.Key, pair.Value)) { Fail("Steam could not publish MonMP lobby metadata."); return; }
            backend.SetLobbyJoinable(lobby, true); backend.SetJoinPresence(lobby);
            Ready = true; Status = "Hosting through Steam. Invite friends from Steam.";
            Diagnostic?.Invoke("Steam lobby ready; app=1520370; sockets=012; routing=" + (forceRelay ? "Valve relay required" : "Steam automatic") + "; lobby=" + lobby);
        }

        private void Entered(ulong lobby, string error)
        {
            if (host || disposed) return;
            Diagnostic?.Invoke("Steam join: lobby entry " + lobby + "; result=" + (error.Length == 0 ? "OK" : error));
            if (error.Length != 0 || lobby != requestedLobby)
            { if (lobby != 0) backend.LeaveLobby(lobby); Fail(error.Length == 0 ? "Steam entered an unexpected lobby." : error); return; }
            LobbyId = lobby;
            if (!ValidateLobby(out expectedHost, out nonce, out var problem)) { Fail(problem); return; }
            if (expectedHost == backend.LocalIdentity) { Fail("You cannot join your own MonMP host on the same Steam account."); return; }
            var handle = backend.Connect(expectedHost, forceRelay);
            Diagnostic?.Invoke("Steam join: compatible lobby; connecting to original host; handle=" + handle + "; relayRequired=" + forceRelay);
            if (handle == 0) { Fail("Steam could not begin the P2P connection."); return; }
            var link = new Link(nextId++, handle, expectedHost); links.Add(handle, link); ids.Add(link.Id, link);
            if (!backend.Configure(handle)) { Fail("Steam could not configure independent reliable message lanes."); return; }
            Status = "Steam is establishing the connection...";
        }

        private bool ValidateLobby(out ulong owner, out string session, out string error) =>
            SteamSessionMetadata.Validate(key => backend.LobbyData(LobbyId, key), backend.LobbyOwner(LobbyId), out owner, out session, out error);

        private void CheckLobby()
        {
            if (disposed || LobbyId == 0) return;
            if (!backend.LobbyMember(LobbyId, backend.LocalIdentity)) { Fail("The Steam lobby closed or you left the lobby."); return; }
            if (host)
            {
                if (backend.LobbyOwner(LobbyId) != expectedHost) { Fail("Steam lobby ownership changed; the host session has ended."); return; }
                foreach (var link in new List<Link>(links.Values))
                    if (!backend.LobbyMember(LobbyId, link.Identity)) Remove(link, "The player left the Steam lobby.", true);
            }
            else if (!ValidateLobby(out var owner, out var session, out var error) || owner != expectedHost || session != nonce || !backend.LobbyMember(LobbyId, expectedHost))
                Fail(error.Length == 0 ? "The original MonMP host has left; the session has ended." : error);
        }

        private void Change(SteamConnectionEvent change)
        {
            if (disposed) return;
            if (change.State == 1 && host)
            {
                if (change.Listener != listener) return; // Another mod/game owns that listen socket.
                Diagnostic?.Invoke("Steam incoming connection: handle=" + change.Handle + "; lobbyMember=" + backend.LobbyMember(LobbyId, change.Identity));
                if (!Ready || change.Identity == 0 || change.Identity == expectedHost || !backend.LobbyMember(LobbyId, change.Identity) ||
                    links.Count >= members - 1 || HasIdentity(change.Identity))
                { backend.Close(change.Handle, "Join the current MonMP lobby before connecting, or wait for the existing connection to close.", false); return; }
                var incoming = new Link(nextId++, change.Handle, change.Identity); links.Add(change.Handle, incoming); ids.Add(incoming.Id, incoming);
                if (!backend.Configure(change.Handle) || !backend.Accept(change.Handle)) Remove(incoming, "Steam rejected the incoming connection.", true);
                return;
            }
            if (!links.TryGetValue(change.Handle, out var link))
            {
                if ((change.State == 4 || change.State == 5) && retired.TryGetValue(change.Handle, out int retiredPeer))
                {
                    retired.Remove(change.Handle);
                    Diagnostic?.Invoke("Steam closed link ended: peer=" + retiredPeer + "; state=" + change.State +
                        "; endReason=" + change.EndReason + "; detail=" + change.Detail);
                }
                return;
            }
            Diagnostic?.Invoke("Steam connection state: handle=" + change.Handle + "; state=" + change.State +
                "; endReason=" + change.EndReason + "; detail=" + change.Detail);
            if (change.Identity != 0 && change.Identity != link.Identity) { Remove(link, "Steam peer identity changed unexpectedly.", true); return; }
            if (change.State == 3 && !link.Connected)
            {
                link.ConnectedAt = Now;
                link.Connected = true; Ready = true; Status = host ? "Hosting through Steam." : "Connected through Steam.";
                Connected?.Invoke(link.Id);
            }
            else if (change.State == 4 || change.State == 5)
            {
                ReportLinkContext(link, "state=" + change.State + "; endReason=" + change.EndReason);
                Remove(link, "Steam connection ended (" + change.EndReason + "): " + change.Detail, true);
            }
        }
        private bool HasIdentity(ulong identity)
        { foreach (var link in links.Values) if (link.Identity == identity) return true; return false; }

        public void Poll()
        {
            if (disposed || !started) return;
            double now = Now;
            pollGap = lastPollAt < 0 ? 0 : Math.Max(0, now - lastPollAt);
            lastPollAt = now;
            try
            {
                if (queueOverflow) { Fail("Steam callback backlog exceeded its bounded limit."); return; }
                for (int n = 0; n < 2048 && pending.Count != 0 && !disposed; n++) pending.Dequeue()();
                if (disposed) return;
                if (!Ready && now - startedAt >= 30) { Fail("Steam join timed out. Check that the friend is still hosting and Steam is online."); return; }
                if (LobbyId != 0 && now >= nextLobbyCheck) { nextLobbyCheck = now + 1; CheckLobby(); }
                if (disposed) return;
                foreach (var packet in backend.Receive(256))
                {
                    if (disposed) return;
                    if (!links.TryGetValue(packet.Connection, out var link) || !link.Connected || link.Closing) continue;
                    if ((!packet.Reliable && packet.Lane != 1) ||
                        !TransportMessageRules.ValidIncoming(packet.Bytes, packet.Reliable, packet.Reliable ? packet.Lane : 0))
                    { Remove(link, "Invalid Steam message size, reliability channel, or boundary.", true); continue; }
                    received += packet.Bytes.Length;
                    link.LastReceiveAt = Now;
                    receiveAge = packet.ReceiveAgeSeconds; receiveAgeSampledAt = packet.ReceiveAgeSampledAt;
                    try { Received?.Invoke(link.Id, packet.Bytes, packet.Reliable); }
                    finally { receiveAge = -1; receiveAgeSampledAt = 0; }
                }
                if (disposed) return;
                DrainReliable();
                if (disposed) return;
                if (now >= nextMetrics)
                {
                    nextMetrics = now + 5;
                    foreach (var link in new List<Link>(links.Values))
                    {
                        var route = backend.Metrics(link.Handle).Route;
                        if (route != link.Route && route.Length != 0)
                        { link.Route = route; Diagnostic?.Invoke("Steam peer " + link.Id + ": " + route); }
                    }
                }
                if (now - rateAt >= 1)
                {
                    BytesSentPerSecond = (long)((sent - rateSent) / Math.Max(1, now - rateAt));
                    BytesReceivedPerSecond = (long)((received - rateReceived) / Math.Max(1, now - rateAt));
                    rateAt = now; rateSent = sent; rateReceived = received;
                }
            }
            catch (Exception ex) { if (!disposed) Fail("Steam transport failed: " + ex.Message); }
        }

        public void Send(int connection, byte[] bytes, bool reliable)
        {
            TransportMessageRules.ValidateOutgoing(bytes);
            if (disposed || !ids.TryGetValue(connection, out var link) || !link.Connected || link.Closing) return;
            if (reliable)
            {
                // Steam reports bytes, not an outstanding message count. Count
                // only the messages we actually retain; the wire's 1024-byte cap
                // bounds this queue to 4 MiB of payload. Native submissions have
                // their own byte budget in DrainReliable and Steam's send buffer.
                if (link.QueuedMessages >= ReliableQueueLimitMessages)
                {
                    link.Closing = true; ReliableQueueOverflowDisconnects++;
                    Enqueue(() =>
                    {
                        if (!ids.TryGetValue(connection, out var current)) return;
                        // Diagnostics must not turn one congested peer into a
                        // host failure if its native handle has already closed.
                        try
                        {
                            var metrics = backend.Metrics(current.Handle);
                            Diagnostic?.Invoke("Steam reliable backlog overflow: peer=" + current.Id +
                                "; queuedMessages=" + current.QueuedMessages + "; queuedBytes=" + (current.ReliableBytes + current.MediaBytes) +
                                "; nativePendingBytes=" + metrics.PendingBytes + "; nativeUnackedBytes=" + metrics.SentUnackedBytes);
                        }
                        catch { }
                        ReportLinkContext(current, "reliableQueueOverflow");
                        Remove(current, "Steam reliable send queue exceeded 4096 queued messages.", true);
                    });
                    return;
                }
                if (TransportMessageRules.ChannelFor(bytes, true) == TransportMessageRules.CdMediaReliableChannel)
                { link.Media.Enqueue((byte[])bytes.Clone()); link.MediaBytes += bytes.Length; return; }
                link.Reliable.Enqueue((byte[])bytes.Clone()); link.ReliableBytes += bytes.Length; return;
            }
            int result = backend.Send(link.Handle, bytes, false, 1);
            if (result == 1) { sent += bytes.Length; link.LastSendAt = Now; return; }
            // Unreliable NoDelay may discard poses under congestion.
        }
        private void DrainReliable()
        {
            if (links.Count == 0) return;
            foreach (var link in new List<Link>(links.Values))
            {
                if (link.Closing) continue;
                var metrics = backend.Metrics(link.Handle);
                link.LastMetrics = metrics; link.MetricsAt = lastPollAt;
                link.MediaCongestion.Observe(metrics, Now);
                long pendingBytes = metrics.PendingBytes;
                long outstandingBytes = pendingBytes + metrics.SentUnackedBytes;
                // World messages always go first. Media follows only once the
                // world queue is empty, within its smaller native allowance.
                if (Drain(link, link.Reliable, ref link.ReliableBytes, NativePendingHighWaterBytes, NativeSendBufferBytes,
                    ref pendingBytes, ref outstandingBytes) && link.Reliable.Count == 0 && link.MediaCongestion.BytesPerSecond != 0)
                    Drain(link, link.Media, ref link.MediaBytes, MediaPendingHighWaterBytes, MediaOutstandingHighWaterBytes,
                        ref pendingBytes, ref outstandingBytes);
                // Logged whether or not media is queued: a paused budget refuses CD
                // messages before they ever reach this transport's queue.
                if (link.Connected && link.MediaCongestion.BytesPerSecond == 0 && Now >= link.NextMediaNotice)
                {
                    link.NextMediaNotice = Now + 30;
                    try { Diagnostic?.Invoke("Steam CD traffic paused: " + LinkStatus(link, metrics)); } catch { }
                }
                // One baseline line per link once its native status has settled.
                if (link.Connected && !link.StatusReported && link.ConnectedAt >= 0 && Now - link.ConnectedAt >= 10)
                {
                    link.StatusReported = true;
                    try { Diagnostic?.Invoke("Steam link status: " + LinkStatus(link, metrics)); } catch { }
                }
                if (disposed) return;
            }
        }
        private bool Drain(Link link, Queue<byte[]> queue, ref long queuedBytes, long pendingLimit, long outstandingLimit,
            ref long pendingBytes, ref long outstandingBytes)
        {
            for (int n = 0; n < 128 && !link.Closing && queue.Count != 0; n++)
            {
                var bytes = queue.Peek();
                // Leave complete messages queued until Steam has room. Data
                // awaiting an ACK is healthy in-flight traffic, not thousands
                // of guessed messages or a reason to disconnect the peer.
                if (pendingBytes + bytes.Length > pendingLimit ||
                    outstandingBytes + bytes.Length > outstandingLimit) return true;
                int result = backend.Send(link.Handle, bytes, true, TransportMessageRules.ChannelFor(bytes, true));
                if (result == 25)
                {
                    if (!link.SendPressureReported) { link.SendPressureReported = true; Error?.Invoke("Steam send buffer is busy; reliable messages remain queued for retry."); }
                    return false; // LimitExceeded: retain this complete message and retry next Poll.
                }
                if (result != 1)
                {
                    ReportLinkContext(link, "reliableSendResult=" + result);
                    Remove(link, "Steam reliable send failed (" + result + ").", true); return false;
                }
                queue.Dequeue(); queuedBytes -= bytes.Length; sent += bytes.Length;
                link.LastSendAt = Now;
                pendingBytes += bytes.Length; outstandingBytes += bytes.Length;
            }
            return !link.Closing;
        }
        public int Ping(int connection) => Metrics(connection).RoundTripTimeMs;
        private static string LinkStatus(Link link, in SteamLinkMetrics metrics)
        {
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            return "peer=" + link.Id + "; rttMs=" + metrics.Ping +
                "; lossPercent=" + metrics.Loss.ToString("F1", culture) +
                "; remoteLossPercent=" + metrics.RemoteLoss.ToString("F1", culture) +
                "; nativePendingBytes=" + metrics.PendingBytes + "; nativeUnackedBytes=" + metrics.SentUnackedBytes +
                "; nativeSendRate=" + metrics.SendRateBytesPerSecond +
                "; nativeQueueMs=" + metrics.QueueTimeMs.ToString("F0", culture) +
                "; nativeQueueUsec=" + metrics.NativeQueueMicroseconds +
                "; cdBudgetBytesPerSecond=" + link.MediaCongestion.BytesPerSecond +
                "; worldQueued=" + link.Reliable.Count + "; mediaQueued=" + link.Media.Count +
                "; route=" + (metrics.Route ?? "unknown");
        }
        private void ReportLinkContext(Link link, string cause)
        {
            try
            {
                double now = Now;
                string Seconds(double at) => at < 0 ? "unknown" : Math.Max(0, now - at).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                Diagnostic?.Invoke("Steam connection end context: " + cause + "; connectedSeconds=" + Seconds(link.ConnectedAt) +
                    "; pollGapSeconds=" + pollGap.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                    "; sinceReceiveSeconds=" + Seconds(link.LastReceiveAt) + "; sinceSendSeconds=" + Seconds(link.LastSendAt) +
                    "; lastMetricsAgeSeconds=" + Seconds(link.MetricsAt) + "; " + LinkStatus(link, link.LastMetrics));
            }
            catch { } // Preserve peer cleanup even if native/logging state retired.
        }
        public TransportPeerMetrics Metrics(int connection)
        {
            if (disposed || !ids.TryGetValue(connection, out var link)) return default;
            var value = backend.Metrics(link.Handle);
            link.MediaCongestion.Observe(value, Now);
            var metrics = new TransportPeerMetrics(value.Ping, 0, value.Loss,
                link.QueuedMessages, value.Route, "steam:" + link.Identity,
                link.ReliableBytes + link.MediaBytes + value.PendingBytes, link.Media.Count, link.MediaBytes);
            return new TransportPeerMetrics(metrics, link.MediaCongestion.BytesPerSecond);
        }
        public void Disconnect(int connection, string reason)
        {
            if (disposed || !ids.TryGetValue(connection, out var closing) || closing.Closing) return;
            reason = TransportDisconnectReasons.Normalize(reason);
            closing.Closing = true;
            Enqueue(() => { if (ids.TryGetValue(connection, out var link)) Remove(link, reason, true); });
        }
        private void Remove(Link link, string reason, bool close)
        {
            if (!links.Remove(link.Handle)) return;
            ids.Remove(link.Id);
            if (close)
            {
                if (retired.Count >= 16) retired.Clear();
                retired[link.Handle] = link.Id;
                backend.Close(link.Handle, reason, false);
            }
            if (!host) { Ready = false; Status = reason; }
            Disconnected?.Invoke(link.Id, reason);
        }
        private void Fail(string reason)
        {
            if (disposed) return;
            Status = reason; Failure = reason;
            Diagnostic?.Invoke("Steam session failure: " + reason);
            try { Diagnostic?.Invoke("Steam service diagnostics: " + NativeDiagnostics()); } catch { }
            // Close resources before notifying NetworkSession, which may dispose us
            // recursively inside Error. Nothing from this adapter runs afterward.
            Dispose(); Failed?.Invoke(reason);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; Ready = false;
            backend.LobbyCreated -= OnLobbyCreated; backend.LobbyEntered -= OnLobbyEntered;
            backend.LobbyChanged -= OnLobbyChanged; backend.ConnectionChanged -= OnConnection;
            if (host && LobbyId != 0) { try { backend.SetLobbyJoinable(LobbyId, false); backend.SetLobbyData(LobbyId, "monmp_state", "closed"); } catch { } }
            foreach (var link in new List<Link>(links.Values)) { try { backend.Close(link.Handle, "MonMP session ended.", false); } catch { } }
            links.Clear(); ids.Clear(); pending.Clear(); retired.Clear();
            if (listener != 0) { try { backend.CloseListener(listener); } catch { } listener = 0; }
            if (LobbyId != 0) { try { backend.LeaveLobby(LobbyId); } catch { } }
            if (host) { try { backend.ClearJoinPresence(); } catch { } }
            backend.Dispose();
        }
        internal string NativeDiagnostics() => backend is SteamNativeBackend native ? native.ServiceDiagnostics() : "Test Steam backend";
    }
}
